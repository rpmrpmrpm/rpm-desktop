using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RPM.Core.Library;

using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Nas;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step one: the central music folder.
///
/// <para>The rule for which folders may be adopted is <see cref="LibraryFolder"/>. The music-folder card in
/// settings uses the same rule, so both places answer the same way.</para>
///
/// <para><b>The row is the validation.</b> The rim, the glyph and the second line of the row change
/// together, so there is one object to read. A separate coloured line under the picker would be a
/// second element that describes the first. The row itself would look the same for an accepted folder
/// and a refused one.</para>
/// </summary>
public sealed partial class WizardFolderStep : UserControl
{
    /// <summary>The folder was accepted and its library is open. Carries the path.</summary>
    public event EventHandler<string>? Chosen;

    /// <summary>The picker's own "nothing chosen yet" state, which comes before <see
    /// cref="LibraryFolderKind"/>. It also has OpenFailed, the one state the classifier cannot report,
    /// because it happens only when opening fails.</summary>
    private enum FolderState { None, Empty, ExistingLibrary, HasMusic, NotEmpty, OpenFailed }

    /// <summary>For a folder that already holds music: what the person chose rpm may do with it.</summary>
    private FileHandling? _adopt;

    private static readonly Windows.UI.Color Amber = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66);
    private static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E);
    private static readonly Windows.UI.Color Cyan  = Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);

    private string? _path;
    private FolderState _state = FolderState.None;

    public WizardFolderStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(3);

        Dress();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        // The central folder is a destination, so the panel offers "New Folder". An empty one can be
        // made here, rather than in Finder and back.
        var picker = App.Services.GetRequiredService<FolderPickerService>();
        var path = await picker.PickFolderAsync(canCreateDirectories: true);
        if (path is null)
            return;

        var kind = LibraryFolder.Classify(path);
        FileHandling? adopt = null;
        if (kind == LibraryFolderKind.HasMusic)
        {
            adopt = await ExistingMusicDialog.AskAsync(XamlRoot, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
            if (adopt is null)
                return;
        }

        _path = path;
        _adopt = adopt;
        _state = kind switch
        {
            LibraryFolderKind.Empty => FolderState.Empty,
            LibraryFolderKind.ExistingLibrary => FolderState.ExistingLibrary,
            LibraryFolderKind.HasMusic => FolderState.HasMusic,
            _ => FolderState.NotEmpty,
        };
        Dress();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_path is not { } path || !Adoptable)
            return;

        // The folder is only the library once its mirror is actually open. Opening reaches the disk and
        // can fail. A path written to settings before that fails is the path the next launch tries and
        // fails at again. So open first, and write second.
        try
        {
            var library = App.Services.GetRequiredService<LibraryService>();
            if (_state == FolderState.HasMusic && _adopt is { } files)
                library.Adopt(path, files);
            else
                library.Open(path);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("library", $"the library could not be opened: {ex.Message}");
            _state = FolderState.OpenFailed;
            Dress();
            return;
        }

        App.Services.GetRequiredService<SettingsService>().LibraryPath = path;
        Chosen?.Invoke(this, path);
    }

    private bool Adoptable =>
        _state is FolderState.Empty or FolderState.ExistingLibrary || (_state == FolderState.HasMusic && _adopt is not null);

    // ── a NAS as the place for the library ──

    private NasDiscovery? _nas;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _nas = App.Services.GetRequiredService<NasDiscovery>();
        _nas.Changed += OnNasChanged;
        _nas.Start();
        _nas.Ask();
        ShowNasRows();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_nas is not null)
            _nas.Changed -= OnNasChanged;
    }

    // Raised on a background thread. The callback must not throw: on WinUI an exception there ends the app.
    private void OnNasChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        try { ShowNasRows(); }
        catch (Exception ex) { RPM.Core.Diagnostics.Scope.Said("nas", $"the NAS rows could not be shown: {ex.Message}"); }
    });

    private void ShowNasRows()
    {
        if (_nas is null)
            return;
        NasRows.Children.Clear();
        foreach (var nas in _nas.Machines)
            NasRows.Children.Add(NasRow(nas));
    }

    private Border NasRow(DiscoveredNas nas)
    {
        var hint = new TextBlock
        {
            Text = nas.SameVersion ? Loc.Format("Nas_LocationHint", nas.Name) : Loc.Format("Nas_NeedsUpdate", nas.Name),
            FontSize = 12,
            Foreground = new SolidColorBrush(nas.SameVersion ? Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF) : Amber),
            TextWrapping = TextWrapping.Wrap,
        };
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = Loc.Format("Nas_Location", nas.Name),
            FontSize = 14,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(hint);
        Grid.SetColumn(text, 1);

        var glyph = new FontIcon
        {
            Style = (Style)Application.Current.Resources["FaIcon"],
            Glyph = "\uf233",
            FontSize = 18,
            Foreground = new SolidColorBrush(Cyan),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(glyph);
        grid.Children.Add(text);

        if (nas.SameVersion)
        {
            var choose = new RpmButton
            {
                Content = Loc.Get("Nas_Choose"),
                Padding = new Thickness(18, 0, 18, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            choose.Click += async (_, _) => await ChooseNasAsync(nas, choose, hint);
            _ = SayExistingMusicAsync(nas, hint);
            Grid.SetColumn(choose, 2);
            grid.Children.Add(choose);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
            Child = grid,
        };
    }

    /// <summary>Changes the row's line when the NAS folder already holds music. The beacon does not say so,
    /// only the NAS's hello, so the line is updated when the hello arrives.</summary>
    private async Task SayExistingMusicAsync(DiscoveredNas nas, TextBlock hint)
    {
        try
        {
            if (await NasLibraryService.HelloAsync(nas, CancellationToken.None) is { Nas.ExistingMusic: true })
                hint.Text = Loc.Format("Nas_ExistingMusicHint", nas.Name);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"the NAS hello could not be read: {ex.Message}");
        }
    }

    /// <summary>The NAS login, then the library on it. The library becomes the collection only once it
    /// is open, as with a folder.</summary>
    private async Task ChooseNasAsync(DiscoveredNas nas, Button button, TextBlock hint)
    {
        button.IsEnabled = false;
        try
        {
            var service = App.Services.GetRequiredService<NasLibraryService>();
            if (await NasLibraryService.HelloAsync(nas, CancellationToken.None) is not { } hello)
            {
                hint.Text = Loc.Format("Nas_Unreachable", nas.Name);
                hint.Foreground = new SolidColorBrush(Amber);
                return;
            }

            // Music already on the NAS: the person chooses what rpm may do with it before signing in, so
            // the sign-in can make the NAS folder the library as chosen.
            IReadOnlyList<NasLibraryChoiceRow> choices;
            if (hello.Nas!.ExistingMusic)
            {
                if (await ExistingMusicDialog.AskAsync(XamlRoot, nas.Name) is not { } files)
                    return;
                choices = [new NasLibraryChoiceRow(Loc.Format("Nas_UseExistingMusic", nas.Name), new NasTarget.ExistingMusic(files))];
            }
            else
            {
                choices = NasLoginDialog.ChoicesFor(hello.Nas, service);
            }

            NasPrepared? prepared = null;
            var login = await NasLoginDialog.ShowAsync(XamlRoot, nas, choices,
                async (credentials, chosen) =>
                {
                    var (problem, ready) = await service.PrepareAsync(nas, hello, credentials, chosen);
                    prepared = ready;
                    return problem;
                });
            if (login is null || prepared is null)
                return;

            if (service.Open(prepared) is NasOpenResult.Opened opened)
            {
                Chosen?.Invoke(this, opened.Path);
                return;
            }

            hint.Text = Loc.Format("Nas_OpenFailed", nas.Name);
            hint.Foreground = new SolidColorBrush(Amber);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"choosing the NAS failed: {ex.Message}");
            hint.Text = Loc.Format("Nas_OpenFailed", nas.Name);
            hint.Foreground = new SolidColorBrush(Amber);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>The row, the glyph and the button, all from the one state. It is one method, not a
    /// property per element, so the state cannot change without every element updating.
    /// </summary>
    private void Dress()
    {
        PathText.Text = _path ?? Loc.Get("Wizard_NoFolderChosen");

        var (rim, glyphInk, line) = _state switch
        {
            FolderState.Empty => (Cyan, Cyan, ""),
            FolderState.ExistingLibrary => (Green, Green, Loc.Get("Setup_ExistingLibrary")),
            FolderState.HasMusic => (Green, Green, Loc.Get(
                _adopt == FileHandling.Kept ? "Setup_HasMusicKept" : "Setup_HasMusicOrganised")),
            FolderState.NotEmpty => (Amber, Amber, Loc.Get("Setup_NotEmpty")),
            FolderState.OpenFailed => (Amber, Amber, Loc.Get("Wizard_FolderFailed")),
            _ => (Windows.UI.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF), Cyan, ""),
        };

        PickerRow.BorderBrush = new SolidColorBrush(rim);
        PickerGlyph.Foreground = new SolidColorBrush(glyphInk);

        // The glyph shows which kind of folder this is, without being read. A plain folder means
        // nothing is chosen. A ticked one is a folder that will be adopted. A warned one is a refusal.
        PickerGlyph.Glyph = _state switch
        {
            FolderState.Empty or FolderState.ExistingLibrary or FolderState.HasMusic => "\uf00c",
            FolderState.NotEmpty or FolderState.OpenFailed => "\uf071",
            _ => "\uf07c",
        };

        StateText.Text = line;
        StateText.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StateText.Foreground = new SolidColorBrush(
            _state is FolderState.ExistingLibrary or FolderState.HasMusic ? Green : Amber);

        NextButton.IsEnabled = Adoptable;
    }
}

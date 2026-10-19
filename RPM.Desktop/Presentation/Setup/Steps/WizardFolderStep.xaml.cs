using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RPM.Core.Library;

using RPM.Desktop.Services.Library;
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
    private enum FolderState { None, Empty, ExistingLibrary, NotEmpty, OpenFailed }

    private static readonly Windows.UI.Color Amber = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66);
    private static readonly Windows.UI.Color Green = Windows.UI.Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E);
    private static readonly Windows.UI.Color Cyan  = Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);

    private string? _path;
    private FolderState _state = FolderState.None;

    public WizardFolderStep()
    {
        InitializeComponent();
        Kicker.Text = Loc.Format("Wizard_StepOf", 1, 3);

        // The big one, and the only one that turns. This is the screen where the record is the picture.
        // The later steps keep it as a small mark beside the heading.
        WizardRecord.Dress(Disc, size: 330, turn: true);

        DressTagline();
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

        _path = path;
        _state = LibraryFolder.Classify(path) switch
        {
            LibraryFolderKind.Empty => FolderState.Empty,
            LibraryFolderKind.ExistingLibrary => FolderState.ExistingLibrary,
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
            App.Services.GetRequiredService<LibraryService>().Open(path);
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

    private bool Adoptable => _state is FolderState.Empty or FolderState.ExistingLibrary;

    /// <summary>
    /// The tagline under the mark, split the way the artwork header splits it.
    /// </summary>
    /// <remarks>The leading "re-" is shown a shade darker, and only where the translation really
    /// starts with it. The hyphen is an English style choice. Other languages join the prefix to the
    /// word, or say it another way, so they get the line in one tone rather than an arbitrary few
    /// characters darkened. The rule is the same as <c>ArtDisplay.SyncTagline</c>, because it is the
    /// same line under the same mark. Two rules would eventually disagree.
    /// </remarks>
    private void DressTagline()
    {
        var line = Loc.Get("Art_ParkedTagline");
        var split = line.StartsWith("re-", StringComparison.Ordinal) ? 3
                  : line.StartsWith("re", StringComparison.Ordinal) ? 2
                  : 0;

        TaglineHead.Text = line[..split];
        TaglineTail.Text = line[split..];
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
            FolderState.Empty or FolderState.ExistingLibrary => "\uf00c",
            FolderState.NotEmpty or FolderState.OpenFailed => "\uf071",
            _ => "\uf07c",
        };

        StateText.Text = line;
        StateText.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StateText.Foreground = new SolidColorBrush(
            _state is FolderState.ExistingLibrary ? Green : Amber);

        NextButton.IsEnabled = Adoptable;
    }
}

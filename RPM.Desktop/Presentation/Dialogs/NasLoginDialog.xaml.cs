using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;
using RPM.Desktop.Strings;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Asks once for the NAS login, mounts the share, and chooses or makes the library on it. It returns a
/// library that is mounted and claimed; opening it and saving it are the caller's, on the interface thread.
/// </summary>
public sealed partial class NasLoginDialog : ContentDialog
{
    private readonly NasLibraryService _service;
    private readonly DiscoveredNas _nas;
    private readonly PeerHello _hello;

    /// <summary>The library choices in the order the list shows them. Null is "a new library".</summary>
    private readonly List<NasLibraryEntry?> _choices = [];

    /// <summary>The prepared library when sign-in worked; null when the window was closed.</summary>
    public NasPrepared? Prepared { get; private set; }

    public NasLoginDialog(NasLibraryService service, DiscoveredNas nas, PeerHello hello)
    {
        InitializeComponent();
        _service = service;
        _nas = nas;
        _hello = hello;
        Shell.Title = Loc.Format("Nas_LoginTitle", nas.Name);
        Shell.AttachResize(this, minWidth: 420, minHeight: 420, maxWidth: 620,
            defaultHeight: hello.Nas!.Libraries.Count > 0 ? 500 : 440, defaultWidth: 500);
        FillLibraries(hello.Nas);
    }

    private void FillLibraries(NasHello nas)
    {
        if (nas.Libraries.Count == 0)
            return;
        var mine = _service.DefaultChoice(nas);
        if (mine is not null)
            _choices.Add(mine);
        _choices.Add(null);
        _choices.AddRange(nas.Libraries.Where(l => !ReferenceEquals(l, mine)).OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase));
        var newName = _service.NewFolderName(nas);
        foreach (var choice in _choices)
            LibraryBox.Items.Add(choice is null ? Loc.Format("Nas_NewLibrary", newName) : choice.Name);
        LibraryBox.SelectedIndex = 0;
        LibraryRow.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private async void Password_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await SignInAsync();
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await SignInAsync();

    private async Task SignInAsync()
    {
        var user = UserBox.Text.Trim();
        if (user.Length == 0 || !SignInButton.IsEnabled)
            return;
        var chosen = _choices.Count > 0 && LibraryBox.SelectedIndex >= 0 ? _choices[LibraryBox.SelectedIndex] : null;

        SignInButton.IsEnabled = false;
        Say(Loc.Format("Nas_Connecting", _nas.Name));
        try
        {
            var (problem, ready) = await _service.PrepareAsync(_nas, _hello, new NasCredentials(user, PasswordBox.Password), chosen);
            if (ready is not null)
            {
                Prepared = ready;
                Hide();
                return;
            }

            Say(problem switch
            {
                NasOpenResult.WrongLogin => Loc.Get("Nas_LoginWrong"),
                NasOpenResult.Unreachable => Loc.Format("Nas_Unreachable", _nas.Name),
                _ => Loc.Format("Nas_OpenFailed", _nas.Name),
            });
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Scope.Said("nas", $"signing in to the NAS failed: {ex.Message}");
            Say(Loc.Format("Nas_OpenFailed", _nas.Name));
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private void Say(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the window and returns the prepared library, or null when it was closed.</summary>
    public static async Task<NasPrepared?> ShowAsync(
        XamlRoot? xamlRoot, NasLibraryService service, DiscoveredNas nas, PeerHello hello)
    {
        var dialog = new NasLoginDialog(service, nas, hello) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog.Prepared;
    }
}

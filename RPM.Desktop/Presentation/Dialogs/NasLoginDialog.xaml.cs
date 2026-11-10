using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;
using RPM.Desktop.Strings;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One choice in the library list: what it says, and the library, or null for a new one.</summary>
public sealed record NasLibraryChoiceRow(string Label, NasLibraryEntry? Entry);

/// <summary>
/// Asks once for the NAS login and runs the caller's step with it: mount and open a library, mount to
/// check the login before a move, or claim a library in place. The step's problem, if any, is shown here,
/// so a wrong password is corrected without closing the window.
/// </summary>
public sealed partial class NasLoginDialog : ContentDialog
{
    private readonly DiscoveredNas _nas;
    private readonly IReadOnlyList<NasLibraryChoiceRow> _choices;
    private readonly Func<NasCredentials, NasLibraryEntry?, Task<NasOpenResult?>> _attempt;

    /// <summary>The login the step accepted; null when the window was closed.</summary>
    public NasCredentials? Login { get; private set; }

    /// <param name="choices">The libraries to choose from, or none to show no list.</param>
    /// <param name="attempt">The step to run with the login and the chosen library. It returns null when
    /// it worked, or the problem to show.</param>
    public NasLoginDialog(
        DiscoveredNas nas, IReadOnlyList<NasLibraryChoiceRow> choices,
        Func<NasCredentials, NasLibraryEntry?, Task<NasOpenResult?>> attempt)
    {
        InitializeComponent();
        _nas = nas;
        _choices = choices;
        _attempt = attempt;
        Shell.Title = Loc.Format("Nas_LoginTitle", nas.Name);
        Shell.AttachResize(this, minWidth: 420, minHeight: 420, maxWidth: 620,
            defaultHeight: choices.Count > 0 ? 500 : 440, defaultWidth: 500);
        if (choices.Count > 0)
        {
            foreach (var choice in choices)
                LibraryBox.Items.Add(choice.Label);
            LibraryBox.SelectedIndex = 0;
            LibraryRow.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The library list for a NAS: this account's library first, then a new one, then the others.</summary>
    public static IReadOnlyList<NasLibraryChoiceRow> ChoicesFor(NasHello hello, NasLibraryService service)
    {
        if (hello.Libraries.Count == 0)
            return [];
        var mine = service.DefaultChoice(hello);
        var rows = new List<NasLibraryChoiceRow>();
        if (mine is not null)
            rows.Add(new NasLibraryChoiceRow(mine.Name, mine));
        rows.Add(new NasLibraryChoiceRow(Loc.Format("Nas_NewLibrary", service.NewFolderName(hello)), null));
        rows.AddRange(hello.Libraries
            .Where(l => !ReferenceEquals(l, mine))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new NasLibraryChoiceRow(l.Name, l)));
        return rows;
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
        var chosen = _choices.Count > 0 && LibraryBox.SelectedIndex >= 0 ? _choices[LibraryBox.SelectedIndex].Entry : null;
        var login = new NasCredentials(user, PasswordBox.Password);

        SignInButton.IsEnabled = false;
        Say(Loc.Format("Nas_Connecting", _nas.Name));
        try
        {
            var problem = await _attempt(login, chosen);
            if (problem is null)
            {
                Login = login;
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

    /// <summary>Shows the window. Returns the login the step accepted, or null when it was closed.</summary>
    public static async Task<NasCredentials?> ShowAsync(
        XamlRoot? xamlRoot, DiscoveredNas nas, IReadOnlyList<NasLibraryChoiceRow> choices,
        Func<NasCredentials, NasLibraryEntry?, Task<NasOpenResult?>> attempt)
    {
        var dialog = new NasLoginDialog(nas, choices, attempt) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog.Login;
    }
}

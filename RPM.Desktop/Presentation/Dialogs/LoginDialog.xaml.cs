using RPM.Core.Cloud;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>The sign-in dialog. It offers Apple and Google, or email and password. A password is sent
/// straight to the rpm API's <c>auth/login</c>. Social sign-in opens the browser and polls for the
/// result, because a desktop app cannot host the redirect URI that a provider posts back to.
/// </summary>
public sealed partial class LoginDialog : ContentDialog
{
    private readonly AuthService _auth;
    private bool _success;
    private CancellationTokenSource? _pollCts;

    public LoginDialog(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;

        // Fixed to the width it opens at, rather than growing to the maximum. It is short enough that
        // the body ends where the content ends, so no empty space appears between the last line and the
        // footer. The user can still drag it to resize, using the grip.
        Shell.AttachResize(this, minWidth: 420, minHeight: 520, maxWidth: 620,
            defaultHeight: 560, defaultWidth: 520);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        Hide();
    }

    private async void GoogleButton_Click(object sender, RoutedEventArgs e) => await TrySocialLoginAsync("Google");
    private async void AppleButton_Click(object sender, RoutedEventArgs e) => await TrySocialLoginAsync("Apple");

    private async void SignInButton_Click(object sender, RoutedEventArgs e) => await TryPasswordLoginAsync();

    /// <summary>Set when Sign up was pressed: this window closes and the sign-up window opens in its place
    /// (see <see cref="ShowAsync"/>), because two dialogs cannot be up at once.</summary>
    private bool _wantsSignUp;

    private void SignUpButton_Click(object sender, RoutedEventArgs e)
    {
        _pollCts?.Cancel();
        _wantsSignUp = true;
        Hide();
    }

    /// <summary>Opens the website's reset page with the address typed so far. The reset is finished
    /// there. The person then comes back to this dialog and signs in with the new password.</summary>
    private void ForgotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(RpmSite.ForgotPasswordFor(EmailBox.Text)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Faults.Note("auth", "forgot password link", ex);
            ShowError(Loc.Get("Login_ForgotNoBrowser"));
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await TryPasswordLoginAsync();
        }
    }

    private async Task TrySocialLoginAsync(string provider)
    {
        SetAllButtonsEnabled(false);
        ErrorText.Visibility = Visibility.Collapsed;
        SocialStatusText.Text = Loc.Get("Login_OpeningBrowser");
        SocialStatusText.Visibility = Visibility.Visible;

        _pollCts = new CancellationTokenSource();
        try
        {
            SocialStatusText.Text = Loc.Get("Login_WaitingBrowser");
            Show(await _auth.SignInSocialAsync(provider, _pollCts.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Show(new SignInOutcome.Failed(Faults.Note("auth", "sign-in dialog", ex)));
        }
        finally
        {
            SocialStatusText.Visibility = Visibility.Collapsed;
            SetAllButtonsEnabled(true);
            _pollCts?.Dispose();
            _pollCts = null;
        }
    }

    private async Task TryPasswordLoginAsync()
    {
        var email = EmailBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Password ?? string.Empty;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            ShowError(Loc.Get("Login_NeedFields"));
            return;
        }

        SetAllButtonsEnabled(false);
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            Show(await _auth.LoginAsync(email, password));
        }
        catch (Exception ex)
        {
            Show(new SignInOutcome.Failed(Faults.Note("auth", "sign-in dialog", ex)));
        }
        finally
        {
            SetAllButtonsEnabled(true);
        }
    }

    private void SetAllButtonsEnabled(bool enabled)
    {
        GoogleButton.IsEnabled = enabled;
        AppleButton.IsEnabled = enabled;
        SignInButton.IsEnabled = enabled;
        SignUpButton.IsEnabled = enabled;
        EmailBox.IsEnabled = enabled;
        PasswordBox.IsEnabled = enabled;
    }

    /// <summary>Says how a sign-in ended: closes on success, shows the outcome's sentence otherwise.
    /// Internal so the gallery can stand an outcome in.</summary>
    internal void Show(SignInOutcome outcome)
    {
        if (outcome is SignInOutcome.SignedIn)
        {
            _success = true;
            Hide();
            return;
        }

        if (outcome.StringKey is { } key)
            ShowError(Loc.Get(key));
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Shows the login dialog. Returns true if sign-in succeeded.
    ///
    /// <paramref name="reason"/> replaces the standing subtitle when sign-in is needed for a specific
    /// action, such as looking up a release, searching for covers, or recognising a track. The user
    /// then learns why before pressing anything, instead of finding a button that does nothing. It
    /// replaces the subtitle rather than adding to it. The standing subtitle is about the cloud
    /// library, which is what a sign-in from the menu is for. It is the wrong reason for a user who
    /// pressed "Look up release".
    /// </summary>
    public static async Task<bool> ShowAsync(AuthService auth, XamlRoot? xamlRoot, [Shown] string? reason = null)
    {
        if (xamlRoot is null) return false;
        var dialog = new LoginDialog(auth) { XamlRoot = xamlRoot };
        if (!string.IsNullOrWhiteSpace(reason))
            dialog.SubtitleText.Text = reason;
        await DialogHost.ShowAsync(dialog);
        if (dialog._wantsSignUp)
            return await SignUpDialog.ShowAsync(auth, xamlRoot);
        return dialog._success;
    }
}

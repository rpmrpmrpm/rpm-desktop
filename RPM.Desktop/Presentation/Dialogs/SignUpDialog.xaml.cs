using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Making an rpm account with an email and password. The form goes to the accounts service, which mails
/// a six-digit code; the code is typed here; and the new account is signed in with the password just
/// chosen, so the window ends signed in, as the sign-in window does.
///
/// <para>The window also opens on the code alone (<see cref="ShowForCodeAsync"/>), for an account that was
/// made and never confirmed: the sign-in window sends it here, because the mail carries only a code.</para>
/// </summary>
public sealed partial class SignUpDialog : ContentDialog
{
    private readonly AuthService _auth;
    private bool _success;

    /// <summary>True while a request is out. The window stays open meanwhile: closed in the middle of the
    /// last step, it would answer "not signed in" while the sign-in finishes behind it.</summary>
    private bool _busy;

    /// <summary>The address and password the code was mailed for, held for the sign-in that follows.</summary>
    private string _email = "", _password = "";

    private bool OnCode => CodeFace.Visibility == Visibility.Visible;

    public SignUpDialog(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Shell.AttachResize(this, minWidth: 420, minHeight: 440, maxWidth: 620,
            defaultHeight: 480, defaultWidth: 500);
        Closing += (_, args) => args.Cancel = _busy;
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await GoAsync();

    private async void Form_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await GoAsync();
    }

    private async void Code_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await GoAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private async Task GoAsync()
    {
        if (_busy) return;
        Say(null);
        Busy(true);
        try
        {
            if (OnCode)
                await ConfirmAsync();
            else
                await RegisterAsync();
        }
        finally
        {
            Busy(false);
        }

        if (_success)
            Hide();
    }

    private async Task RegisterAsync()
    {
        var name = NameBox.Text?.Trim() ?? "";
        var email = EmailBox.Text?.Trim() ?? "";
        var password = PasswordBox.Password ?? "";
        if (name.Length == 0 || email.Length == 0 || password.Length == 0)
        {
            Say(Loc.Get("SignUp_NeedFields"));
            return;
        }

        var answer = await _auth.SignUpAsync(name, email, password);
        if (answer.StringKey is { } refused)
        {
            Say(Loc.Get(refused));
            return;
        }

        ShowCode(email, password);
    }

    /// <summary>Turns the window to the code face for <paramref name="email"/>.</summary>
    private void ShowCode(string email, string password)
    {
        _email = email;
        _password = password;
        CodeSentText.Text = Loc.Format("SignUp_CodeSent", email);
        FormFace.Visibility = Visibility.Collapsed;
        CodeFace.Visibility = Visibility.Visible;
        GoText.Text = Loc.Get("SignUp_Confirm");
        CodeBox.Focus(FocusState.Programmatic);
    }

    private async Task ConfirmAsync()
    {
        var code = CodeBox.Text?.Trim() ?? "";
        if (code.Length == 0)
        {
            Say(Loc.Get("SignUp_NeedCode"));
            return;
        }

        var answer = await _auth.ConfirmSignUpAsync(_email, code);
        if (answer.StringKey is { } refused)
        {
            Say(Loc.Get(refused));
            return;
        }

        // The account is live: sign it in with the password it was made with. A second Confirm after a
        // failed sign-in is safe, because the service answers a confirmed account as done.
        var signedIn = await _auth.LoginAsync(_email, _password);
        if (signedIn is SignInOutcome.SignedIn)
        {
            _success = true;
            return;
        }

        Say(signedIn.StringKey is { } key ? Loc.Get(key) : Loc.Get("SignUp_Failed"));
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Say(null);
        Busy(true);
        try
        {
            var answer = await _auth.ResendSignUpCodeAsync(_email);
            Say(Loc.Get(answer.StringKey ?? "SignUp_CodeResent"));
        }
        finally
        {
            Busy(false);
        }
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        GoButton.IsEnabled = CancelButton.IsEnabled = ResendButton.IsEnabled = !busy;
        NameBox.IsEnabled = EmailBox.IsEnabled = PasswordBox.IsEnabled = CodeBox.IsEnabled = !busy;
    }

    private void Say(string? line)
    {
        ErrorText.Text = line ?? "";
        ErrorText.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows the window. Returns true when it ends with the new account signed in.</summary>
    public static Task<bool> ShowAsync(AuthService auth, XamlRoot? xamlRoot) =>
        ShowAsync(new SignUpDialog(auth), xamlRoot);

    /// <summary>
    /// Shows the window on the code face, for an account that exists and was never confirmed. No new code
    /// is sent on opening: a new code would replace the one already in the person's inbox. "Send a new
    /// code" is there for a mail that was lost, and an expired code is answered with a new one by the
    /// service. Returns true when it ends with the account signed in.
    /// </summary>
    public static Task<bool> ShowForCodeAsync(AuthService auth, XamlRoot? xamlRoot, string email, string password)
    {
        var dialog = new SignUpDialog(auth);
        dialog.ShowCode(email, password);
        return ShowAsync(dialog, xamlRoot);
    }

    private static async Task<bool> ShowAsync(SignUpDialog dialog, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null) return false;
        dialog.XamlRoot = xamlRoot;
        await DialogHost.ShowAsync(dialog);
        return dialog._success;
    }
}

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
/// </summary>
public sealed partial class SignUpDialog : ContentDialog
{
    private readonly AuthService _auth;
    private bool _success;

    /// <summary>The address and password the code was mailed for, held for the sign-in that follows.</summary>
    private string _email = "", _password = "";

    private bool OnCode => CodeFace.Visibility == Visibility.Visible;

    public SignUpDialog(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Shell.AttachResize(this, minWidth: 420, minHeight: 440, maxWidth: 620,
            defaultHeight: 480, defaultWidth: 500);
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
        if (!answer.Done)
        {
            Say(Loc.Get(answer.StringKey));
            return;
        }

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
        if (!answer.Done)
        {
            Say(Loc.Get(answer.StringKey));
            return;
        }

        // The account is live: sign it in with the password it was made with.
        var signedIn = await _auth.LoginAsync(_email, _password);
        if (signedIn is SignInOutcome.SignedIn)
        {
            _success = true;
            Hide();
            return;
        }

        Say(signedIn.StringKey is { } key ? Loc.Get(key) : Loc.Get("SignUp_Failed"));
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        ResendButton.IsEnabled = false;
        var answer = await _auth.ResendSignUpCodeAsync(_email);
        ResendButton.IsEnabled = true;
        Say(answer.Done ? Loc.Get("SignUp_CodeResent") : Loc.Get(answer.StringKey));
    }

    private void Busy(bool busy)
    {
        GoButton.IsEnabled = !busy;
        NameBox.IsEnabled = EmailBox.IsEnabled = PasswordBox.IsEnabled = CodeBox.IsEnabled = !busy;
    }

    private void Say(string? line)
    {
        ErrorText.Text = line ?? "";
        ErrorText.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows the window. Returns true when it ends with the new account signed in.</summary>
    public static async Task<bool> ShowAsync(AuthService auth, XamlRoot? xamlRoot)
    {
        if (xamlRoot is null) return false;
        var dialog = new SignUpDialog(auth) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._success;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The check in front of every feature that needs the rpm API. The user is asked to sign in before the
/// window opens, not after.
///
/// <para><b>Asking is the gate.</b> A signed-out user who picks "Look up release" sees the login dialog
/// with the reason on it, and the lookup runs as soon as sign-in succeeds. A disabled menu entry would
/// hide the feature, and a window that searches and finds nothing would hide the cause. Neither is
/// used.</para>
///
/// <para><b>Where the decision lives.</b> This class only shows the dialog. Whether access is allowed at
/// all is decided by <see cref="AccountGate"/>, so services with no window get the same answer.</para>
/// </summary>
public static class SignInGate
{
    /// <summary>
    /// Whether an errand that needs the rpm API may run, asking the user to sign in when that is the only
    /// thing missing.
    /// </summary>
    /// <param name="root">The tree to ask in. Null means there is no window to ask in, and the answer is
    /// no.</param>
    /// <param name="reason">Why this action needs an account, in the user's language. Every caller must
    /// pass it, with no default. A login dialog that does not say why it appears looks like the app lost
    /// the session.</param>
    public static async Task<bool> AllowAsync(XamlRoot? root, string reason)
    {
        if (root is null)
            return false;

        var gate = App.Services.GetRequiredService<AccountGate>();
        switch (gate.Access)
        {
            case AccountAccess.Open:
                return true;

            case AccountAccess.NotConfigured:
                // Not the user's doing and not theirs to fix, so there is nothing to ask. Show what is
                // missing and stop.
                await PromptDialog.ShowAsync(
                    root, Loc.Get("Login_NotAvailableTitle"), Loc.Get("Login_NotAvailable"),
                    closeText: Loc.Get("Common_OK"), defaultButton: ContentDialogButton.Close);
                return false;

            default:
                return await LoginDialog.ShowAsync(
                    App.Services.GetRequiredService<AuthService>(), root, reason);
        }
    }
}

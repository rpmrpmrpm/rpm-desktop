using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Cloud;

namespace RPM.Desktop.Services.Account;

/// <summary>What deleting the account came to.</summary>
public enum AccountDeletionOutcome
{
    /// <summary>The account is deleted, this computer is signed out, and the library has forgotten
    /// the account.</summary>
    Deleted,

    /// <summary>The server refused the typed address. It is not the account's verified email, or the
    /// account has none. Nothing was deleted.</summary>
    EmailRefused,

    /// <summary>The request did not get through, or the server failed. Nothing was deleted, and the
    /// person is still signed in.</summary>
    Failed,
}

/// <summary>
/// Deletes the signed-in account. This is the only way to delete it.
///
/// <para><b>The order matters.</b></para>
/// <list type="number">
/// <item>The server goes first. Until it answers 204, nothing on this computer changes. So a refusal or
/// a dropped connection leaves the person signed in, with everything as it was.</item>
/// <item>Then sign-out (<see cref="AuthService.Logout"/>). Once signed out, <c>CloudSyncService</c>
/// starts no new upload run, so the run it waits for in the next step is the last one.</item>
/// <item>Then the library is reset (<see cref="CloudSyncService.ForgetDeletedAccountAsync"/>). Every
/// track goes back to never uploaded, and the radio log's sent totals are cleared.</item>
/// </list>
///
/// <para>The server erases the cloud data itself, on its own schedule, within two hours. Nothing here
/// waits for that.</para>
/// </summary>
public sealed class AccountDeletion(
    AuthService auth, CloudSyncService sync, RadioService radio, ILogger<AccountDeletion> log)
{
    /// <summary>Call from the UI thread. Sign-out raises <see cref="AuthService.Changed"/> on the
    /// caller's thread, as the Sign out button does.</summary>
    public async Task<AccountDeletionOutcome> DeleteAsync(string typedEmail, CancellationToken ct)
    {
        var answer = await auth.DeleteAccountOnServerAsync(typedEmail, ct);
        if (answer != AccountDeletionOutcome.Deleted)
        {
            log.LogWarning("[Account] Deletion not done: {Outcome}", answer);
            return answer;
        }

        log.LogInformation("[Account] Deleted on the server; signing out and resetting the library");
        auth.Logout();

        // From here on the account is gone, whatever happens below. A reset that fails leaves rows
        // that still say Synced. That is wrong, but no data is lost, so it is logged and not reported
        // as a failed deletion. The deletion itself did not fail.
        try
        {
            await sync.ForgetDeletedAccountAsync();
            radio.Listens.ForgetAccount();
            radio.Favorites.ForgetAccount();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "[Account] The library could not be reset after the account was deleted");
        }

        return AccountDeletionOutcome.Deleted;
    }
}

using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The beta welcome: a note for an account that the server has marked as a closed beta tester.
///
/// <para><b>Read from state each time the hints are drawn, not from an event.</b> The note is due when
/// the standing says tester and this machine has not shown it to this account
/// (<see cref="BetaWelcomeDialog.DueFor"/>). The server's answer can arrive before the library is up
/// (during setup, or behind the splash) or long after it. A draw is asked for when the answer arrives,
/// when the library is shown and when the splash goes, and each draw asks again. So whichever of these
/// comes last opens the note.</para>
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>
    /// Asks the server for the account's standing again. Called each time the library is shown: at
    /// launch, and when the setup wizard hands over.
    ///
    /// <para>Signing in asks too, but an account can be marked as a tester after that: while the wizard
    /// is still open, or while the app is closed. Without this, such an account would wait for its next
    /// sign-in or a visit to the Cloud settings. The answer raises <see cref="SupporterService.Changed"/>
    /// only when it is new, and that asks for a draw.</para>
    /// </summary>
    private static void AskStanding()
    {
        if (App.Services.GetService<SupporterService>() is { } supporter)
            RPM.Desktop.Services.Shell.Background.Run(() => supporter.GetAsync());
    }

    /// <summary>True from the moment the note is asked to open until it has closed. A draw in between
    /// must not open a second note, and the hints must not be drawn under it.</summary>
    private bool _betaWelcomeOpen;

    /// <summary>
    /// Opens the beta welcome if it is due, and says whether it is open. Called by
    /// <see cref="DrawCoachmarks"/>, only over the library and with the splash gone.
    /// </summary>
    private bool OfferBetaWelcome()
    {
        if (_betaWelcomeOpen)
            return true;

        var settings = App.Services.GetRequiredService<SettingsService>();
        var standing = App.Services.GetRequiredService<SupporterService>().Standing;
        if (XamlRoot is not { } root
            || BetaWelcomeDialog.DueFor(Auth?.UserId, standing, settings.BetaWelcomed) is not { } account)
            return false;

        _betaWelcomeOpen = true;
        _ = ShowBetaWelcomeAsync(root, account, settings);
        return true;
    }

    /// <summary>
    /// Shows the note, then records it under the account it was opened for. The id is read before the
    /// note opens, so signing out or changing account while it is up cannot record a different account.
    ///
    /// <para>It is recorded even when the note could not be shown. Otherwise the draw that follows would
    /// try again, fail again, and keep the hints away for the rest of the session. The log line says that
    /// it failed.</para>
    /// </summary>
    private async Task ShowBetaWelcomeAsync(XamlRoot root, string account, SettingsService settings)
    {
        try
        {
            await BetaWelcomeDialog.ShowAsync(root);
        }
        catch (Exception e)
        {
            RPM.Core.Diagnostics.Said.Line("setup", $"the beta welcome could not be shown: {e.Message}");
        }
        finally
        {
            settings.MarkBetaWelcomed(account);
            _betaWelcomeOpen = false;
            RefreshCoachmarks();
        }
    }
}

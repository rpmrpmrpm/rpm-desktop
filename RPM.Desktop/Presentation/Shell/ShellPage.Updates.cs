using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

using RPM.Desktop.Services.Updates;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The shell's part in updating. It reads the update feed once per launch, and asks the user when an
/// update has been fetched.
/// </summary>
/// <remarks>
/// Kept here, not in the service, because a window belongs to the shell. <see cref="AppUpdates"/>
/// knows what update there is. This class decides whether anyone can be asked, and where the question
/// is shown.
/// </remarks>
public sealed partial class ShellPage
{
    /// <summary>How long after the shell is up before the feed is read.
    ///
    /// <para>Not at once. The first seconds are used by the library read, the artwork and the window
    /// layout. A download started then would compete with the app the user is waiting for. An update is
    /// never urgent, because it installs when the user quits.</para></summary>
    private static readonly TimeSpan UpdateWait = TimeSpan.FromSeconds(12);

    /// <summary>Whether the check has been arranged. The Loaded event fires again each time the shell
    /// is re-parented. Without this flag a second subscription would make a second check and a second
    /// window.</summary>
    private bool _updateHooked;

    private void HookUpdates()
    {
        if (_updateHooked)
            return;
        _updateHooked = true;

        var updates = App.Services.GetRequiredService<AppUpdates>();
        if (!updates.Managed)
            return;

        Services.Shell.Ticker.Once(DispatcherQueue, UpdateWait, () => _ = FetchAndOfferAsync(updates));
    }

    /// <summary>Reads the feed, and asks the question if something came back. No cancellation token: the
    /// work is a download on a worker, and the thing that ends it is the process ending.</summary>
    private async Task FetchAndOfferAsync(AppUpdates updates)
    {
        if (await updates.FetchAsync())
            await OfferUpdateAsync(updates);
    }

    /// <summary>
    /// Asks the question, once per launch.
    /// </summary>
    /// <remarks>
    /// Restart now closes the window. Closing the window runs the app's own shutdown: it saves the
    /// window's place, the session, the evening's plays, the player and the library. The updater already
    /// waits for this process to end (see <see cref="AppUpdates.Apply"/>), so the app comes back as the
    /// new version by quitting, not by being killed where it stands.
    ///
    /// <para>Later records the version and leaves the app alone. The update is installed when the user
    /// quits, as the window text says.</para>
    /// </remarks>
    private async Task OfferUpdateAsync(AppUpdates updates)
    {
        if (!updates.MayAsk || updates.Staged is not { } version)
            return;

        // The gate is set before the window opens, not after it closes. If the window is dismissed and
        // opened again, the gate must already be set, or the question could appear twice.
        updates.AskedNow();

        var answer = await Dialogs.PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Update_Title"),
            Loc.Format("Update_Body", $"{version.Major}.{version.Minor}.{version.Build}"),
            primaryText: Loc.Get("Update_Restart"),
            closeText: Loc.Get("Update_Later"),
            defaultButton: ContentDialogButton.Primary);

        if (answer == ContentDialogResult.Primary)
        {
            // Close the window rather than calling Exit. Closing it runs the app's own shutdown, and the
            // updater is already waiting for this process to end.
            if (updates.Apply(restart: true))
                App.AppWindow?.Close();
            return;
        }

        updates.Defer();
    }
}

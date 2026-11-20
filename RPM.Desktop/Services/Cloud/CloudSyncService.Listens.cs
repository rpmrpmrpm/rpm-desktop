using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The listening log, kept in step between the listener's computers, phones, televisions and the web app.
///
/// <para>Pull first, then push, as the plays do. The listens of the other devices go into this library's log
/// (<see cref="LibraryDatabase.AcceptListens"/>), so the lists that read the log here (On repeat, Rediscover,
/// Late night, First listens) count what was played anywhere. They do not move the play counters: those arrive
/// as each device's totals (<see cref="SyncPlaysAsync"/>).</para>
///
/// <para>This library's own listens go to the store once each, under the same device id as its play totals. The
/// store cuts the lists for the phones from them when the account has no recent desktop cut.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>Listens per request, the store's own limit.</summary>
    private const int ListensPerSend = 500;

    /// <summary>Requests each way per pass. A library sending years of history, or taking it in for the first
    /// time, does it over a few passes.</summary>
    private const int ListenRequestsPerPass = 20;

    /// <summary>Runs after <see cref="SyncPlaysAsync"/>, which claims the seeded play rows under
    /// <see cref="LibraryDatabase.PlaysDevice"/> first.</summary>
    private async Task SyncListensAsync(LibraryDatabase db, CancellationToken ct)
    {
        var mine = db.PlaysDevice;

        // ── in ──
        try
        {
            // See ListensPull for why the cursor moves after every page and each pass asks from a margin before it.
            var taken = await ListensPull.RunAsync(
                db, mine, (since, page, token) => _cloud.ListListensAsync(since, page, token), ListenRequestsPerPass, ct);
            if (taken > 0)
                _log.LogInformation("[Cloud] {Count} listens arrived from other devices", taken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The push still runs: this library's own listens do not wait on reading the others'.
            _log.LogDebug(ex, "[Cloud] Could not read the listening log");
        }

        // ── out ──
        for (int request = 0; request < ListenRequestsPerPass && !ct.IsCancellationRequested; request++)
        {
            var due = db.ListensToSend(mine, ListensPerSend);
            if (due.Count == 0)
                return;

            try
            {
                await _cloud.PutListensAsync(mine, due, ct);
                db.NoteListensSent(due);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Kept unsent, and sent again on the next pass. The ids make a second send harmless.
                _log.LogDebug(ex, "[Cloud] Could not send the listening log");
                return;
            }

            if (due.Count < ListensPerSend)
                return;
        }
    }
}

using System.Globalization;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// What the listener has played, kept in step between their computers and their phone.
///
/// <para>Pull first, then push. This is the same order as the marks, for the same reason: what has been
/// taken in no longer needs to be sent from this machine.</para>
///
/// <para>The rows this machine sends are its own, and they are totals. A send that fails, or lands but
/// whose answer is lost, is sent again on the next pass with the same value. That changes nothing. See
/// <c>RPM.Lambda.Api.Services.TrackPlayStore</c> for why this must hold.</para>
///
/// <para>The counters themselves are in the mirror. See <see cref="LibraryDatabase.ApplyForeignPlays"/>,
/// which holds the rules for whose row is whose. This is the part that carries them.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>How many rows one run may send. This is the store's own limit per request, and it is more
    /// than a sitting's listening.</summary>
    private const int PlaysPerRun = 200;

    private async Task SyncPlaysAsync(LibraryDatabase db, CancellationToken ct)
    {
        // Use the library's own name, not the install's name. Two libraries opened on one machine hold
        // different music under one account. With one shared name, each would read the other's rows as its
        // own. See LibraryDatabase.PlaysDeviceKey.
        var mine = db.PlaysDevice;

        // The rows written by the v67 migration belong to this library, but the migration could not say so
        // at the time. So they are claimed once, before anything reads them as this machine's.
        db.ClaimSeededPlays(mine);

        // ── in ──
        var cursor = long.TryParse(
            db.GetMeta(LibraryDatabase.PlaysCursorKey), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var seen) ? seen : 0;

        IReadOnlyList<CloudTrackPlays> theirs;
        try
        {
            theirs = await _cloud.ListTrackPlaysAsync(cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the play counts");
            return;
        }

        // ApplyForeignPlays drops this machine's own rows, and rows for tracks that this mirror does not
        // hold. Both kinds arrive in the normal course, because the listing contains everything the
        // listener has, on every device.
        var taken = db.ApplyForeignPlays(
            mine,
            [.. theirs.Select(r => new LibraryDatabase.TrackPlayRow(
                r.RpmId, r.DeviceId, (int)r.Plays, PlayClock.Ticks(r.LastPlayedAt), 0))]);

        // The cursor moves on what ARRIVED, not on what was applied. A row skipped because this mirror has
        // no such track would otherwise be requested on every pass.
        if (theirs.Count > 0)
            db.SetMeta(
                LibraryDatabase.PlaysCursorKey,
                theirs.Max(r => r.UpdatedAt).ToString(CultureInfo.InvariantCulture));

        if (taken > 0)
            _log.LogInformation("[Cloud] {Count} play counts arrived from another device", taken);

        // ── out ──
        if (ct.IsCancellationRequested)
            return;

        var due = db.PlaysToSend(mine, PlaysPerRun);
        if (due.Count == 0)
            return;

        try
        {
            await _cloud.PutTrackPlaysAsync(
                mine,
                [.. due.Select(r => (r.RpmId, r.Plays, PlayClock.Seconds(r.LastPlayedUtcTicks)))],
                ct);

            // The rows as they were SENT are used, not the rows as they stand now. A listen counted since
            // the read is still unsent, and it goes out on the next pass. See
            // LibraryDatabase.NotePlaysSent.
            db.NotePlaysSent(mine, due);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not send the play counts");
        }
    }
}

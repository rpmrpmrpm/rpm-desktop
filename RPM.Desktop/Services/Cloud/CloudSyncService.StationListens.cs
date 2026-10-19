using Microsoft.Extensions.DependencyInjection;
using RPM.Core.Radio;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// What the listener has heard on the radio, kept in step between their computers and their phone.
///
/// <para>This is the same leg as <see cref="SyncPlaysAsync"/>, but over the station log instead of the
/// mirror. Pull first, then push. What is pushed is this log's own totals.</para>
///
/// <para>The log is named by itself, not by the library. It lives beside the presets and outlives whichever
/// library is open. See <see cref="RadioListens.Device"/>.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>How many rows one run may send. This is the store's own per-request limit, which is large
    /// enough for a whole log.</summary>
    private const int StationListensPerRun = 200;

    private async Task SyncStationListensAsync(CancellationToken ct)
    {
        // This is asked for here, not passed to the constructor. The radio service is created at launch,
        // long before the first pass.
        if (App.Services.GetService<RadioService>()?.Listens is not { } log)
            return;

        // Null when the log could not be read this session, or when its name could not be stored. Syncing
        // without a name that lasts would bring this log's own rows back as another device's rows in the
        // next session.
        if (log.Device is not { } mine)
            return;

        // ── in ──
        IReadOnlyList<RPM.Core.Cloud.CloudStationListens> theirs;
        try
        {
            theirs = await _cloud.ListStationListensAsync(log.Cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the radio listening");
            return;
        }

        var taken = log.Apply(
            [.. theirs
                .Where(r => r.Station?.ToStation() is not null)
                .Select(r => new ForeignRadioListen(
                    r.DeviceId,
                    new RadioListenRow(r.Station!.ToStation()!, r.HeardSeconds, r.Tunes, r.LastHeardAt)))]);

        // Counts are taken from the rows that ARRIVED, not from the rows that were applied. This is the
        // same as the plays leg. Most of what comes back is this log's own rows.
        if (theirs.Count > 0)
            log.NoteCursor(theirs.Max(r => r.UpdatedAt));

        if (taken > 0)
            _log.LogInformation("[Cloud] {Count} radio listening rows arrived from another device", taken);

        // ── out ──
        if (ct.IsCancellationRequested)
            return;

        var due = log.Unsent(StationListensPerRun);
        if (due.Count == 0)
            return;

        try
        {
            await _cloud.PutStationListensAsync(mine, due, ct);

            // The rows are taken as they were SENT. A listen noted since the read is still unsent, and it
            // goes out on the next pass. See RadioListens.NoteSent.
            log.NoteSent(due);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not send the radio listening");
        }
    }
}

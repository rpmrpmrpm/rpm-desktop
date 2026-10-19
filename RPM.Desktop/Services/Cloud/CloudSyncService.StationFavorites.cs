using Microsoft.Extensions.DependencyInjection;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The saved radio stations, kept in step between the listener's computers, phones and TVs.
///
/// <para>Pull first, then push, as the station log does. Rows the store has changed since the cursor are
/// taken in where they are newer than what this computer holds. Then this computer's changes that the store
/// has not accepted yet are sent. The rule belongs to <see cref="RPM.Core.Radio.FavoriteLedger"/>. The
/// Android apps and the iPhone run the same pass.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>How many changes one run may send. This is the store's own limit per request.</summary>
    private const int StationFavoritesPerRun = 500;

    private async Task SyncStationFavoritesAsync(CancellationToken ct)
    {
        // This is asked for here, not passed to the constructor. The radio service is created at launch,
        // long before the first pass.
        if (App.Services.GetService<RadioService>()?.Favorites is not { } favorites)
            return;
        if (_auth.UserId is not { Length: > 0 } userId)
            return;

        favorites.ForAccount(userId);

        // ── in ──
        IReadOnlyList<RPM.Core.Radio.FavoriteRow> rows;
        try
        {
            rows = await _cloud.ListStationFavoritesAsync(favorites.Cursor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the saved stations");
            return;
        }

        favorites.Take(rows);

        // ── out ──
        if (ct.IsCancellationRequested)
            return;

        var due = favorites.ToSend(StationFavoritesPerRun);
        if (due.Count == 0)
            return;

        try
        {
            await _cloud.PutStationFavoritesAsync([.. due.Values], ct);

            // The changes are taken as they were SENT. A star pressed since the read is still unsent, and
            // it goes out on the next pass. See FavoriteLedger.Sent.
            favorites.NoteSent(due);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not send the saved stations");
        }
    }
}

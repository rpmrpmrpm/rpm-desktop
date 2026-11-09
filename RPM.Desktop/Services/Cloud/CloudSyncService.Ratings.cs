using System.Net;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The listener's star ratings, kept in step with the cloud on a pass of their own.
///
/// <para>In first, then out, as the plays are. In takes the rating the catalogue lists for each track,
/// except where this machine has a rating it has not sent yet (that one is newer). Out sends the
/// unsent ratings of tracks the cloud holds. A server that does not have the ratings route answers
/// 404; the ratings then stay unsent and go on a later pass, so nothing is lost while the server is
/// older than the app.</para>
///
/// <para>A rating is not part of the catalogue row's comparison (<see cref="CloudSyncPlan"/>). If it
/// were, a rating that cannot be sent yet would make the row differ on every pass, and the row would
/// be pushed again for ever.</para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>How many ratings one run may send. This is the store's own limit per request.</summary>
    private const int RatingsPerRun = 200;

    private async Task SyncRatingsAsync(LibraryDatabase db, IReadOnlyList<CloudTrack> cloud, CancellationToken ct)
    {
        // ── in ──
        // A catalogue row from a server that does not send ratings says nothing, and is left alone.
        var taken = db.AcceptTrackRatings(cloud
            .Where(t => t.Tags?.Rating is not null)
            .Select(t => (t.RpmId, t.Tags!.Rating!.Value)));
        if (taken > 0)
            _log.LogInformation("[Cloud] {Count} song ratings arrived from another device", taken);

        // ── out ──
        while (!ct.IsCancellationRequested)
        {
            var due = db.RatingsToSend(RatingsPerRun);
            if (due.Count == 0)
                return;

            try
            {
                await _cloud.PutTrackRatingsAsync([.. due.Select(r => (r.RpmId, r.Rating))], ct);
                db.NoteRatingsSent([.. due.Select(r => (r.Id, r.Rating))]);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _log.LogDebug("[Cloud] The server does not take song ratings yet; they stay unsent");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[Cloud] Could not send the song ratings");
                return;
            }
        }
    }
}

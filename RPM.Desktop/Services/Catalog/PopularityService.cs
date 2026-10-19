using RPM.Core.Net;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Finds the song on a record that is played most, for a given album.
///
/// <para><b>One request, sent to the RPM API.</b> The server makes four calls to two catalogues:
/// MusicBrainz for the release and its recordings, and ListenBrainz twice, first to map those to the
/// recordings it counts and then for the play figures. All of these run on the server. The client does not
/// call these catalogues itself. <see cref="RPM.Fingerprint.External.CatalogueReach"/> refuses direct
/// calls. The server caches its answer for thirty days, so each record costs the chain once for all
/// users.</para>
///
/// <para><b>Both sources are CC0 and free to use.</b> Do not replace them with a streaming service to get
/// around MusicBrainz's rate limit. The services with the best data for this, such as Deezer, license their
/// APIs for non-commercial use only. A missing star is a smaller problem than breaking a licence in a
/// product people pay for.</para>
///
/// <para><b>Asked, never volunteered.</b> Nothing here runs on a schedule. A caller asks when an album page
/// opens and the record's row has no answer yet. The answer is saved to that row, so each record is looked
/// up once. The star is then read from the stored row on later visits.</para>
///
/// <para>Does not throw for network reasons. A missing star and no internet look the same to the page, and
/// neither is an error that should interrupt playback.</para>
/// </summary>
public sealed class PopularityService(RPM.Core.Metadata.Providers.IRpmMetadataApi api)
{
    /// <summary>
    /// The record's most played song, or the kind of empty result that came back instead.
    /// </summary>
    /// <param name="releaseMbid">The MusicBrainz release this album is already linked to, if any. It is
    /// used as given and not searched for again. An album linked through the picker has the id. For an
    /// album without a link, the server falls back to the pressings, then to a search: a Discogs link, a
    /// vinyl rip, or an album entered by hand.</param>
    /// <param name="trackCount">How many tracks the album has. It tells one edition of a record from
    /// another when several answer to the same name.</param>
    public async Task<PopularTrackAnswer> MostPlayedTrackAsync(
        string? artist, string? album, int trackCount, string? releaseMbid, CancellationToken ct)
    {
        var counted = await CountedAsync(artist, album, trackCount, releaseMbid, ct);

        return counted is not { } answer ? PopularTrackAnswer.NotAsked
             : answer.Tracks.Count == 0 ? PopularTrackAnswer.Nothing
             : PopularTrackAnswer.Of(TrackPopularity.MostPlayed(
                   answer.Tracks, answer.Plays, TrackPopularity.AlbumPageLead));
    }

    /// <summary>
    /// Play counts for every song on a record, by disc and track position.
    ///
    /// <para>Uses the same request as the star on the album page, but keeps the whole answer instead of
    /// only the top song. A chart of a whole record needs the count for every track. The counts are fetched
    /// anyway, so keeping them costs nothing extra.</para>
    ///
    /// <para>Null where nobody was reached (a service is down, a rate limit is still in force, or there is
    /// no network). An empty answer means the catalogues were asked and have no such record. The caller
    /// saves an empty answer but must not save null. See <see cref="PopularTrackAnswer"/> for why.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<TrackPlace, RecordingPlays>?> AlbumPlaysAsync(
        string? artist, string? album, int trackCount, string? releaseMbid, CancellationToken ct)
    {
        var counted = await CountedAsync(artist, album, trackCount, releaseMbid, ct);

        return counted is not { } answer
            ? null
            : TrackPopularity.PlaysByPlace(answer.Tracks, answer.Plays);
    }

    /// <summary>
    /// The one request that both questions above are asked through.
    /// </summary>
    /// <returns>Null where nobody was reached at any step. The API returns a 503 for this, not an empty
    /// answer, so the two cases stay separate here. A value with no tracks means the catalogues answered
    /// and have nothing.</returns>
    private async Task<CatalogPlays?> CountedAsync(
        string? artist, string? album, int trackCount, string? releaseMbid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return CatalogPlays.Empty;   // A record with no name cannot be found in any catalogue.

        try
        {
            // Null means no answer. It is never saved; see the summary.
            return (await api.GetPlaysAsync(artist, album, trackCount, releaseMbid, ct)).Answers(out var plays, out _)
                ? plays
                : null;
        }
        // A page change is not a failed lookup. It is passed through unchanged.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // Nobody was reached, so there is no answer.
        }
    }
}

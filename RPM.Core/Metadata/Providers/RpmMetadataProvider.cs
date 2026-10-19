using RPM.Core.Models;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// The app's metadata provider. This is the single source that is always available: the RPM API. Which
/// upstream catalogue answered is the server's business, and it is never shown here. The app sees one
/// provider, "RPM". Search and lookup are separate calls, because no catalogue returns track lengths from
/// a search. So lookups are spent on a shortlist.
/// </summary>
public sealed class RpmMetadataProvider(IRpmMetadataApi api) : IMetadataProvider
{
    public const string ProviderId = "rpm";

    private readonly IRpmMetadataApi _api = api;

    public string Id => ProviderId;

    public string DisplayName => "RPM";

    /// <summary>True whenever the API can be reached for this user. There is no separate account to connect
    /// for this provider.</summary>
    public bool IsAvailable => _api.IsAvailable;

    /// <summary>No. A bare catalogue number or barcode is a Discogs search. Here, the catalogue-number
    /// filter only narrows an album search.</summary>
    public bool SupportsCatalogNumberSearch => false;

    public async Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(
        ReleaseQuery query, CancellationToken ct = default)
    {
        // An album, an artist or a catalogue number is each enough on its own. An artist alone is a real
        // search, such as "everything The Corrs put out on CD". Refusing an artist-only search would make
        // the search look unchanged after the album box is cleared.
        if (string.IsNullOrWhiteSpace(query.Album)
            && string.IsNullOrWhiteSpace(query.Artist)
            && string.IsNullOrWhiteSpace(query.CatalogNumber))
            return Asked.Answer<IReadOnlyList<ReleaseSearchResult>>([]);

        if (!(await _api.SearchAsync(
                query.Album, query.Artist, query.Limit, query.Media,
                query.CatalogNumber, query.Country, query.Year, ct)).Answers(out var found, out var why))
            return Asked.Failed<IReadOnlyList<ReleaseSearchResult>>(why);

        // Asking for one medium and getting none is not a real answer. A CD listing of the same master
        // still names a record's tracks correctly. So the search widens to all media. It widens only when
        // the narrow search answered with nothing. A narrow search that failed is not widened, because the
        // wider search would then be read as "there are none on that medium".
        if (query.Media != ReleaseMedia.Any && found.Count == 0
            && !(await _api.SearchAsync(
                    query.Album, query.Artist, query.Limit, ReleaseMedia.Any,
                    query.CatalogNumber, query.Country, query.Year, ct)).Answers(out found, out why))
            return Asked.Failed<IReadOnlyList<ReleaseSearchResult>>(why);

        // The API answers per medium, because a release is often a box as well as a record. A search result
        // is different: the media fold back into one row per release. This also makes the track count match
        // the flat listing from GetReleaseAsync.
        return Asked.Answer<IReadOnlyList<ReleaseSearchResult>>(found.GroupBy(c => c.ReleaseId).Select(g =>
        {
            var release = g.First();
            var formats = g.Select(c => c.Format)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .ToList();

            return new ReleaseSearchResult(
                Ref: new ReleaseRef(ProviderId, release.ReleaseId),
                Title: release.Title,
                Artist: release.Artist,
                Year: release.Year,
                Date: release.Date,
                Country: release.Country,
                Format: formats.Count > 0 ? string.Join(" + ", formats) : null,
                TrackCount: g.Sum(c => c.TrackCount),
                Label: release.Label,
                CatalogNumber: release.CatalogNumber,
                Note: release.Note,
                Relevance: Math.Clamp(release.Score / 100.0, 0, 1),
                CoverUrl: null);
        }).ToList());
    }

    public async Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        => (await _api.GetReleaseAsync(releaseId, ct)).Then<ReleaseDetail?>(details => details is null ? null : Detail(details));

    /// <summary>One release's listing as the app's own shape.</summary>
    private static ReleaseDetail Detail(CatalogReleaseDetail details)
    {
        // Build from the media so each track keeps its disc and its number on that disc. The flat
        // continuous Position is kept for the merged view and for matching.
        uint discCount = (uint)details.Media.Count;
        var tracks = new List<ReleaseTrackInfo>();
        if (details.Media.Count > 0)
        {
            uint overall = 0;
            foreach (var medium in details.Media)   // the API returns these in medium order
            {
                uint discNo = discCount > 1 ? medium.Position : 0;
                uint discPos = 0;
                foreach (var trk in medium.Tracks)
                    tracks.Add(Track(
                        trk, details.Artist, ++overall, discNo, ++discPos,
                        discCount > 1 ? medium.Title : null));
            }
        }
        else
        {
            tracks = details.Tracks
                .Select((t, i) => Track(
                    t, details.Artist, t.Position == 0 ? (uint)(i + 1) : t.Position))
                .ToList();
        }

        // The catalogue sends one genre string. For a dance record its most-voted answer is "deep house",
        // which is a style, not a genre. Reading it as a pair gives both halves, at no cost, because it is
        // the same string. If the server sends a style of its own, that is used as it is, without a second
        // guess.
        var kind = details.Subgenre is { Length: > 0 }
            ? new GenreChoice(details.Genre ?? "", details.Subgenre)
            : Genres.Read(details.Genre);

        var formats = details.Media
            .Select(m => m.Format)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct()
            .ToList();

        return new ReleaseDetail(
            new ReleaseRef(ProviderId, details.Id),
            details.Title,
            details.Artist,
            details.Year,
            Country: details.Country,
            Format: formats.Count > 0 ? string.Join(" + ", formats) : null,
            Tracks: tracks,
            DiscCount: discCount,
            ArtistSort: details.ArtistSort,
            ArtistId: details.ArtistId,
            ReleaseGroupId: details.ReleaseGroupId,
            OriginalYear: details.OriginalYear,
            Barcode: details.Barcode,
            Label: details.Label,
            CatalogNumber: details.CatalogNumber,
            Genre: kind.IsKnown ? kind.Genre : details.Genre,
            Subgenre: kind.Subgenre);
    }

    /// <summary>One wire track as the app's own shape, for a listing that did not come through a search.
    /// This is a CD that names itself from its disc ID, which is how most rips are named. The mapping is
    /// the same, so a disc that identifies itself carries everything a hand-picked pressing does.</summary>
    public static ReleaseTrackInfo Adopt(CatalogTrack track, string? releaseArtist, uint position)
        => Track(track, releaseArtist, position);

    /// <summary>
    /// One wire track as the app's own shape. Everything the API sends is kept. The listing is fetched once
    /// and adopted once. So a field that is dropped here is never received by the rip, and nothing looks it
    /// up again.
    /// </summary>
    /// <param name="releaseArtist">Only a fallback. A track that names its own artist keeps that artist.
    /// That is what a compilation is.</param>
    private static ReleaseTrackInfo Track(
        CatalogTrack track, string? releaseArtist, uint position,
        uint disc = 0, uint discPosition = 0, string? discSubtitle = null) => new(
        position,
        track.Title,
        track.Artist ?? releaseArtist,
        track.LengthSeconds,
        Disc: disc,
        DiscPosition: discPosition,
        DiscSubtitle: discSubtitle,
        // The wire sends the iTunes advisory numbers, which the enum uses with the same values. See
        // AdvisoryTag.
        Advisory: Enum.IsDefined((ContentAdvisory)track.Advisory)
            ? (ContentAdvisory)track.Advisory
            : ContentAdvisory.Unknown,
        ArtistSort: track.ArtistSort,
        RecordingId: track.RecordingId,
        ArtistId: track.ArtistId,
        Isrc: track.Isrc,
        Work: track.Work,
        MovementName: track.MovementName,
        MovementNumber: track.MovementNumber,
        Composers: track.Composers,
        ComposerSort: track.ComposerSort,
        Lyricist: track.Lyricist,
        Arranger: track.Arranger,
        Conductor: track.Conductor,
        Ensemble: track.Ensemble,
        Soloists: track.Soloists);
}

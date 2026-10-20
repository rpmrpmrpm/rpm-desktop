using RPM.Core.Models;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// A release, and the catalogue that names it. An id means nothing without the catalogue it came from. A
/// MusicBrainz MBID and a Discogs release number are both "the id of Rumours", and they share no namespace.
/// So the two always travel together.
/// </summary>
/// <param name="Provider">The provider's <see cref="IMetadataProvider.Id"/>, such as "musicbrainz" or
/// "discogs".</param>
public sealed record ReleaseRef(string Provider, string Id);

/// <summary>What the user is looking for.</summary>
/// <param name="Album">The album name, as typed. Providers escape it. Callers do not.</param>
/// <param name="Artist">Narrows the search when the artist is already known. That is usually the case,
/// because the recording came from somewhere.</param>
/// <param name="Media">Ask only for pressings on one medium: a record when the audio came off a turntable,
/// a CD when it came off a drive. This must filter, not just rank. A famous album is mostly CDs in any
/// catalogue (thirteen to one for "Rumours"). The pressing being looked for would never reach the first
/// page otherwise. A provider that finds nothing on that medium should widen the search, not answer with
/// nothing.</param>
/// <param name="CatalogNumber">The pressing's catalogue number, printed on the sleeve and stamped in the
/// run-out. It is the surest way to name the exact record in hand. Discogs searches it as <c>catno</c>.
/// MusicBrainz uses the <c>catno</c> Lucene field. It may be the whole query, with no album.</param>
/// <param name="Barcode">The EAN or UPC barcode on the packaging. It is unique worldwide, so it names one
/// pressing exactly. Discogs matches it as <c>barcode</c>. It may be the whole query, with no
/// album.</param>
/// <param name="Country">Two-letter country of the pressing, such as "GB" or "US". A 1973 UK cut is not the
/// 1978 US repress.</param>
/// <param name="Year">Year of the pressing, when the user knows it. 0 or null means not given.</param>
public sealed record ReleaseQuery(
    string Album,
    string? Artist = null,
    ReleaseMedia Media = ReleaseMedia.Any,
    int Limit = 25,
    string? CatalogNumber = null,
    string? Country = null,
    uint? Year = null,
    string? Barcode = null);

/// <summary>
/// One release as a search returns it. The shape is the same whichever provider answered, so results from
/// every provider can sit in one list and be compared.
///
/// <para>Every field here tells pressings of one album apart. That is what a search is asked. "Rumours" is
/// a 1977 UK cut, a 1984 CD and a 2013 remaster, and they are different records.</para>
/// </summary>
/// <param name="Year">0 when the release has no date.</param>
/// <param name="Country">Where this pressing came out: "GB", "US", or "XE" (Europe).</param>
/// <param name="Format">The format, such as "12&quot; Vinyl" or "CD". Null when the provider does not
/// say.</param>
/// <param name="CatalogNumber">Printed on the sleeve and stamped in the run-out. It is the surest way to
/// know that a pressing is the one in your hands.</param>
/// <param name="Note">The provider's own qualifier, such as "deluxe edition" or "remastered".</param>
/// <param name="Relevance">The provider's own score for the query, scaled to 0 to 1.</param>
/// <param name="CoverUrl">A front cover, when the provider gives one with the result. Null does not mean
/// there is none. See <see cref="IReleaseArtProvider"/>.</param>
public sealed record ReleaseSearchResult(
    ReleaseRef Ref,
    string Title,
    string? Artist,
    uint Year,
    string? Date,
    string? Country,
    string? Format,
    int TrackCount,
    string? Label = null,
    string? CatalogNumber = null,
    string? Note = null,
    double Relevance = 0,
    string? CoverUrl = null,
    uint MediumPosition = 1)
{
    /// <summary>Whether this release is a record. It picks the pressings whose track lengths can match a
    /// vinyl recording.</summary>
    public bool IsVinyl => ReleaseMedia.Vinyl.Matches(Format);
}

/// <summary>
/// One track of a release's listing.
///
/// <para>Everything after the disc fields is what the catalogue can say about the track, beyond its name.
/// All of it is optional. A provider that does not know a field leaves it null. For example, Discogs has no
/// advisory flag and no work relations. The rip then writes nothing for that field, rather than something
/// wrong.</para>
/// </summary>
/// <param name="Position">The overall 1-based number across the whole release. It runs across discs. The
/// merged "one album" view numbers tracks this way.</param>
/// <param name="LengthSeconds">Null when the catalogue has no time for the track. This is common, and it is
/// not a reason to distrust the rest of the listing.</param>
/// <param name="Disc">The disc the track is on, 1-based. 0 for a single-disc release.</param>
/// <param name="DiscPosition">The track's 1-based number within its disc. This is what is written to the
/// TRACKNUMBER tag, so the file reads back as "disc N track M". 0 falls back to
/// <paramref name="Position"/>.</param>
/// <param name="DiscSubtitle">The disc's own title in the set, such as "B-Sides", when the release names
/// it.</param>
/// <param name="Artist">This track's own artist. On a compilation it differs from the release artist on
/// every row. Putting the release artist on all of them is how a various-artists rip ends up filed under
/// one name.</param>
/// <param name="Title">Always the whole name of what is on the track, including "Work: Movement". A listing
/// is read by things that know nothing about works, such as a filename template, a tag editor or a car
/// stereo. Forty tracks called "Allegro" is what a bare movement name gives them.</param>
/// <param name="MovementCount">How many movements the work has, where the listing groups them and so knows.
/// 0 elsewhere. 0 means unknown, not one.</param>
public sealed record ReleaseTrackInfo(
    uint Position,
    string Title,
    string? Artist,
    double? LengthSeconds,
    uint Disc = 0,
    uint DiscPosition = 0,
    string? DiscSubtitle = null,
    ContentAdvisory Advisory = ContentAdvisory.Unknown,
    string? ArtistSort = null,
    string? RecordingId = null,
    string? ArtistId = null,
    string? Isrc = null,
    string? Work = null,
    string? MovementName = null,
    uint MovementNumber = 0,
    uint MovementCount = 0,
    string? Composers = null,
    string? ComposerSort = null,
    string? Lyricist = null,
    string? Arranger = null,
    string? Conductor = null,
    string? Ensemble = null,
    string? Soloists = null)
{
    /// <summary>
    /// What a movement of a work is called in full: "Sonata In C Major, KV. 330: Allegretto".
    ///
    /// <para>The two parts are kept in their own tags. That is what lets a player group a sonata and show
    /// its movements under one heading. The title is still the full title, though. A listing that gives
    /// three rows called "Allegro" is unusable anywhere the grouping is not understood. This method builds
    /// the title here, so both catalogues and the rip build it the same way.</para>
    ///
    /// <para>If the movement name already starts with the work name, it is used as it is. Some listings
    /// write the full name into every row. "Sonata: Sonata: Allegro" helps nobody.</para>
    /// </summary>
    public static string TitleOf(string? work, string movement)
    {
        movement = movement.Trim();
        if (string.IsNullOrWhiteSpace(work))
            return movement;

        work = work.Trim();
        return movement.Length == 0
            ? work
            : movement.StartsWith(work, StringComparison.OrdinalIgnoreCase)
                ? movement
                : $"{work}: {movement}";
    }
}

/// <summary>A release's full listing: what a search result becomes once it is worth the lookup.</summary>
/// <param name="DiscCount">How many discs the release spans. 1, or 0 when the provider does not
/// say.</param>
/// <param name="OriginalYear">When the record first came out, as opposed to when this pressing did.</param>
/// <param name="Photos">How many pictures of this pressing the catalogue holds, besides its front cover.
/// These are the collector's photos of the disc, the labels and the run-out. They come with the lookup that
/// is already paid for, and that is the only reason they are here. Asking for them separately would cost a
/// throttled request for each release. A catalogue with no such pictures, or one that does not say, reports
/// 0. 0 does not mean the front cover is missing too. See
/// <see cref="IReleaseArtProvider.OtherImagesAsync"/> for what the pictures are.</param>
/// <param name="ReleaseType">What kind of release it is, from the catalogue's own types, or null where it does
/// not say. The import and the album editor fill their release type from it.</param>
/// <param name="Genres">The catalogue's own genre words, most votes first, where it gives a ranked list
/// (MusicBrainz, through a server new enough to send it). <see cref="Genre"/> and <see cref="Subgenre"/> are
/// what the app's lists make of it (<see cref="Metadata.Genres.PickVoted"/>). The words are kept because a
/// record whose shelf is already set by its file can still take its style from them
/// (<see cref="Metadata.Genres.StyleUnder"/>). Null where the catalogue gives no such list.</param>
public sealed record ReleaseDetail(
    ReleaseRef Ref,
    string Title,
    string? Artist,
    uint Year,
    string? Country,
    string? Format,
    IReadOnlyList<ReleaseTrackInfo> Tracks,
    uint DiscCount = 0,
    string? ArtistSort = null,
    string? ArtistId = null,
    string? ReleaseGroupId = null,
    uint OriginalYear = 0,
    string? Barcode = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Genre = null,
    string? Subgenre = null,
    int Photos = 0,
    RPM.Core.Library.ReleaseType? ReleaseType = null,
    IReadOnlyList<string>? Genres = null);

/// <summary>
/// Somewhere that knows which records exist, such as MusicBrainz or Discogs. They answer the same two
/// questions, and the app should not care which one answered.
///
/// <para>Search is cheap, and lookup is not. No provider returns track lengths from a search, and both are
/// rate-limited. So they are two separate calls on purpose. Lookups are spent on a shortlist.</para>
/// </summary>
public interface IMetadataProvider
{
    /// <summary>A stable key, used in <see cref="ReleaseRef.Provider"/>: "musicbrainz" or
    /// "discogs".</summary>
    string Id { get; }

    /// <summary>What to call it on screen, so a result can say where it came from.</summary>
    string DisplayName { get; }

    /// <summary>False when this provider cannot be used right now. For example, Discogs is unavailable
    /// until the user has connected their account. An unavailable provider is skipped. That is not an
    /// error.</summary>
    bool IsAvailable { get; }

    /// <summary>True when a bare catalogue number or barcode typed into the search finds a release here.
    /// Discogs matches both in its general search. MusicBrainz, as used here, does not. This decides
    /// whether the lookup offers those as search terms. It counts only while the provider is
    /// available.</summary>
    bool SupportsCatalogNumberSearch { get; }

    /// <summary>The releases that match <paramref name="query"/>. An empty list means there are none.
    /// <see cref="Asked{T}.Unanswered"/> means the catalogue did not answer, or could not be asked. A
    /// window must not say "no releases" for the second case.</summary>
    Task<Asked<IReadOnlyList<ReleaseSearchResult>>> SearchAsync(ReleaseQuery query, CancellationToken ct = default);

    /// <summary>The full listing, with whatever track times the catalogue has. Null when the catalogue
    /// knows no such release. Unanswered when it did not answer. A window shows "no tracklist" only for the
    /// first case.</summary>
    Task<Asked<ReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default);
}

/// <summary>
/// Somewhere that knows what a specific release looks like.
///
/// <para>This is keyed by release, not by name. That is the difference that matters. A name gets one image
/// for "Fleetwood Mac, Rumours". A release gets the sleeve of the pressing being considered. Choosing
/// between pressings is exactly what a search result is for.</para>
///
/// <para>This is separate from the name-keyed artwork lookup (TheAudioDB, then fanart.tv). That lookup
/// answers a different question: what should be on screen while this album plays. It could join this
/// registry as one more provider later, if that is worth doing.</para>
/// </summary>
public interface IReleaseArtProvider
{
    /// <summary>Ask for the picture as it was uploaded, not for one of the archive's rendered sizes. The
    /// album folder's cover image is fetched at this size. It is written once and kept, and nothing
    /// downstream fetches it again. An archive's largest render is a limit that somebody else chose. The
    /// copy written into the track files is scaled down from this one, separately.</summary>
    const int OriginalSize = int.MaxValue;

    string Id { get; }

    /// <summary>Whether this provider can speak for that release. The Cover Art Archive answers for
    /// MusicBrainz ids and nothing else. Discogs answers for its own.</summary>
    bool Owns(ReleaseRef release);

    /// <summary>A front cover no larger than <paramref name="maxSize"/> px, or the upload itself at
    /// <see cref="OriginalSize"/>. Null when there is none. Providers should cache answers, including
    /// misses. "This release has no art" is worth remembering. A provider that could not ask returns
    /// <see cref="Asked{T}.Unanswered"/>, which is never cached as a miss.</summary>
    Task<Asked<string?>> FrontCoverUrlAsync(ReleaseRef release, int maxSize = 250, CancellationToken ct = default);

    /// <summary>
    /// Every picture of this release that is not its front cover. These are the collector's photos of the
    /// disc, the labels, the run-out and the inner sleeve. A caller uses them to look for a disc among
    /// them.
    ///
    /// <para>These are candidates, not answers. A catalogue does not say which image is the disc. They are
    /// simply the pictures somebody uploaded, in upload order. The caller picks the one that holds a disc
    /// by looking at them.</para>
    ///
    /// <para>The default is empty. An archive that only knows about front covers says so by returning
    /// nothing. That is an answer. Unanswered means the archive was asked and did not answer.</para>
    /// </summary>
    Task<Asked<IReadOnlyList<ReleaseImage>>> OtherImagesAsync(ReleaseRef release, CancellationToken ct = default)
        => Task.FromResult(Asked.Answer<IReadOnlyList<ReleaseImage>>([]));
}

/// <summary>One of a release's pictures, at both the sizes an archive offers it.</summary>
/// <param name="ProbeUrl">The small copy, used to decide whether a picture is worth having. A thumbnail is
/// enough to see whether something is round.</param>
/// <param name="FullUrl">The upload. It is fetched only for the picture that wins.</param>
public sealed record ReleaseImage(string ProbeUrl, string FullUrl);

namespace RPM.Fingerprint.Catalog;

// The neutral metadata wire contract between the RPM API and the desktop app. No upstream
// provider identity (MusicBrainz, and any future source) crosses this boundary: the server maps
// whatever answered into these records, and the client only ever sees "RPM" metadata.

/// <summary>One medium of a release as a search turned it up: enough to tell pressings apart
/// without a second call. <see cref="Year"/> is 0 when the release carries no date.</summary>
/// <param name="ReleaseGroupId">The album this pressing is an edition of. The server's cover search uses it
/// to ask fanart.tv by group. Null for answers that were stored without it.</param>
public sealed record CatalogRelease(
    string ReleaseId,
    string Title,
    string? Artist,
    uint Year,
    string? Date,
    string? Country,
    string? Format,
    uint MediumPosition,
    int TrackCount,
    string? Label,
    string? CatalogNumber,
    string? Note,
    double Score,
    string? ReleaseGroupId = null);

/// <summary>
/// One track slot. <see cref="Position"/> is 1-based within its medium (or overall for a flat listing).
///
/// <para>Everything after the length is optional and defaults to nothing. A client built against an
/// older shape is unaffected. A cache entry written without these fields deserialises with them empty.
/// Nothing here is worth a second request: it all rides the one release lookup.</para>
/// </summary>
/// <param name="Artist">The track's own artist, which on a compilation is not the release artist.</param>
/// <param name="Advisory">Whether the lyrics are explicit, in iTunes' own numbers: 1 explicit,
/// 2 clean, 0 nobody has said. No catalogue of releases carries this. The server adds it from a
/// separate source; see the advisory enrichment in the metadata controller.</param>
/// <param name="Work">The composition this is a performance of, for the classical shelf. Null for
/// everything that is just a song.</param>
public sealed record CatalogTrack(
    uint Position,
    string Title,
    string? Artist,
    double? LengthSeconds,
    byte Advisory = 0,
    string? ArtistSort = null,
    string? RecordingId = null,
    string? ArtistId = null,
    string? Isrc = null,
    string? Work = null,
    string? MovementName = null,
    uint MovementNumber = 0,
    string? Composers = null,
    string? ComposerSort = null,
    string? Lyricist = null,
    string? Arranger = null,
    string? Conductor = null,
    string? Ensemble = null,
    string? Soloists = null);

/// <summary>One medium (disc) of a release with its own ordered tracks.</summary>
public sealed record CatalogMedium(
    uint Position,
    IReadOnlyList<CatalogTrack> Tracks,
    string? Format,
    string? Title);

/// <summary>A release's full ordered listing across every medium it has. <see cref="Tracks"/> is the
/// flat overall order; <see cref="Media"/> keeps the discs apart.</summary>
/// <param name="OriginalYear">When the record first came out, as against when this pressing did.
/// Zero when the catalogue does not say.</param>
public sealed record CatalogReleaseDetail(
    string Id,
    string Title,
    string? Artist,
    uint Year,
    IReadOnlyList<CatalogTrack> Tracks,
    IReadOnlyList<CatalogMedium> Media,
    string? ArtistSort = null,
    string? ArtistId = null,
    string? ReleaseGroupId = null,
    uint OriginalYear = 0,
    string? Barcode = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Genre = null,
    string? Subgenre = null,
    string? Country = null);

/// <summary>What a CD's table of contents resolved to: the matching medium's track list, and
/// everything the catalogue knows about the pressing it belongs to. A rip is the one moment those
/// facts can be had, so the disc lookup carries as much as a release lookup does.</summary>
public sealed record CatalogDisc(
    string ReleaseId,
    string Title,
    string? Artist,
    uint Year,
    uint DiscNumber,
    uint DiscCount,
    IReadOnlyList<CatalogTrack> Tracks,
    string? ArtistSort = null,
    string? ArtistId = null,
    string? ReleaseGroupId = null,
    uint OriginalYear = 0,
    string? Barcode = null,
    string? Label = null,
    string? CatalogNumber = null,
    string? Genre = null,
    string? Subgenre = null,
    string? Country = null,
    string? Format = null,
    string? DiscSubtitle = null);

/// <summary>
/// Where a release's front cover can be fetched from, once the server has looked.
///
/// <para><see cref="Url"/> is null when the archives hold no picture of this pressing. Plenty of them
/// do not. That is an <i>answer</i>, and the point of asking. A cover's address at the Cover Art
/// Archive is a function of the release id, so the client could always build one. A built address that
/// has never been checked looks the same as a real cover until the picture fails to arrive. So it is
/// checked here once, cached, and served to everyone.</para>
///
/// <para>A call that could not reach the archive is NOT this record with a null url. It is the request
/// failing. "There is no cover" and "nobody could ask" must not look the same, or the no-cover answer
/// gets cached for a release that has one.</para>
/// </summary>
public sealed record CatalogCover(string? Url);

/// <summary>One music DVD a search turned up.</summary>
public sealed record CatalogDvd(
    string ReleaseId,
    string Title,
    string? Artist,
    uint Year,
    string? Format,
    uint MediumPosition,
    int TrackCount);

// ── the artist, as the encyclopedia panes need them ──

/// <summary>
/// One outside page an act keeps.
/// </summary>
/// <param name="Relation">What the answering catalogue called the link, passed on untouched. Which
/// SITE it is on is a judgement the app makes for itself from the address (see ReferenceLinks). A name
/// invented here would be a second opinion to reconcile. But the label cannot be derived, and the app
/// needs one word of it. An artist's own homepage has whatever host its owner bought, so there is
/// nothing in the address to recognise it by, and the label is the only thing that says what it is.</param>
public sealed record CatalogLink(string Url, string? Relation = null);

/// <summary>
/// One person in a band, as the catalogues have them.
/// </summary>
/// <param name="Born">Their date of birth as far as it is known: a whole date, a month, or a year.</param>
/// <param name="Died">The day they died, where they have. Null for the living and for the many whose
/// entries simply do not say, which is why <paramref name="HasDied"/> is asked separately. A death
/// that is known without its date is still a death.</param>
public sealed record CatalogArtistMember(
    string Name,
    string? Id = null,
    string? Born = null,
    string? Died = null,
    bool HasDied = false);

/// <summary>
/// What the catalogues know about one act, fused from every source that answered and stripped of
/// which one did. Everything past the name is optional: this is augmentation, and an artist nobody
/// has written up is an ordinary case rather than a failure.
/// </summary>
/// <param name="Id">The answering catalogue's own id for them, opaque to the app and useful only for
/// handing back to the same API. Null when the act was recognised by name alone.</param>
/// <param name="Kind">"Group", "Person", "Orchestra", "Choir"... This decides whether a lifespan reads
/// as a formation or a birth, and whether an ending can be mourned.</param>
/// <param name="Began">Formed, or born. A partial date: "1967", "1967-07", "1967-07-12".</param>
/// <param name="Ended">Disbanded, or died. Null while still going.</param>
/// <param name="HasEnded">True when a catalogue says the life-span is closed. Distinct from
/// <paramref name="Ended"/> being set: a band can be known to have split without the date being known.</param>
/// <param name="WikidataId">The entity the encyclopedias hang off, when a source handed one over. This
/// saves the app a lookup it would otherwise have to make to find it.</param>
public sealed record CatalogArtist(
    string Name,
    string? Id = null,
    string? Kind = null,
    string? Disambiguation = null,
    string? Country = null,
    string? Area = null,
    string? Began = null,
    string? Ended = null,
    bool HasEnded = false,
    string? WikidataId = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<CatalogLink>? Links = null,
    IReadOnlyList<CatalogArtistMember>? Members = null)
{
    public static readonly CatalogArtist Nobody = new("");

    /// <summary>Nothing was found worth showing. The name alone is not an answer. The app already has the
    /// name, since it is what was asked with.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Id)
        && string.IsNullOrWhiteSpace(WikidataId)
        && (Links is null || Links.Count == 0)
        && (Genres is null || Genres.Count == 0)
        && string.IsNullOrWhiteSpace(Began);
}

/// <summary>
/// What the catalogues know about a record. It is the release GROUP, not one pressing. For example,
/// "Rumours" is one album, and its 1977 UK cut, its 1984 CD and its 2013 remaster are editions of it.
/// The pressing in the user's library is already described by its own tags.
/// </summary>
/// <param name="FirstReleased">When the album first came out in any edition. This is the date people mean
/// by a record's year, not the date printed on the copy that the user owns.</param>
/// <param name="PrimaryType">"Album", "Single", "EP", "Broadcast", "Other".</param>
/// <param name="SecondaryTypes">"Live", "Compilation", "Soundtrack", "Remix"... often empty.</param>
public sealed record CatalogAlbum(
    string Title,
    string? Id = null,
    string? Artist = null,
    string? ArtistId = null,
    string? Disambiguation = null,
    string? FirstReleased = null,
    string? PrimaryType = null,
    IReadOnlyList<string>? SecondaryTypes = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<CatalogLink>? Links = null);

/// <summary>
/// What the world plays of one record, as far as anybody counts it.
/// </summary>
/// <param name="Tracks">The record's tracks with the recording each is a copy of, in pressed order,
/// already mapped onto the recordings the figures are filed under.</param>
/// <param name="Plays">The figures, by recording. A track nothing is reported for is absent rather than
/// nought: nobody asked about it and nobody answered, and nought is a claim.</param>
public sealed record CatalogPlays(
    IReadOnlyList<ReleaseTrack> Tracks,
    IReadOnlyList<RecordingPlays> Plays)
{
    public static readonly CatalogPlays Empty = new([], []);

    public bool IsEmpty => Tracks.Count == 0;
}

/// <summary>
/// Everything the outside world says about one record: the act, the album, and the ids that let a
/// caller ask somebody else about either. One request, instead of six.
/// </summary>
public sealed record CatalogPapers(
    CatalogArtist? Artist = null,
    CatalogAlbum? Album = null)
{
    public static readonly CatalogPapers Empty = new();

    /// <summary>
    /// True when the API said this is only part of an answer (its <c>X-RPM-Partial</c> header): a source
    /// could not be asked, or had not answered when the API's wait ran out. Not on the wire. The client
    /// that read the header sets it, and whatever would otherwise keep the answer reads it and does not.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Partial { get; init; }

    public bool IsEmpty => (Artist is null || Artist.IsEmpty) && Album is null;
}

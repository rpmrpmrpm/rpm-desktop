using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// The desktop app's one window onto the RPM catalogue API. The upstream source that answers is mapped to
/// the neutral <c>Catalog*</c> wire types on the server, and this app never names it. To the app there is
/// one metadata source, "RPM".
/// </summary>
public interface IRpmMetadataApi
{
    /// <summary>
    /// Whether the API can be asked anything at all right now. It needs an address to call and a session to
    /// call it with.
    ///
    /// <para>This is stated, not assumed, so a window can ask the user to sign in before it asks for
    /// anything. Every call below also says, through <see cref="Asked{T}"/>, whether it was answered. An
    /// empty answer means the catalogues have nothing. A failure is never an empty answer.</para>
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Release search. <paramref name="media"/> asks the server to apply that medium's format
    /// filter; <see cref="ReleaseMedia.Any"/> asks for no filter at all.</summary>
    /// <returns>An empty list when nothing matches; <see cref="Asked{T}.Unanswered"/> when the API did
    /// not answer, which a window must not report as "no releases".</returns>
    Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(
        string text, string? artist, int limit, ReleaseMedia media,
        string? catalogNumber, string? country, uint? year, CancellationToken ct = default);

    /// <summary>A release's full listing: null when no catalogue knows the id, unanswered when the API
    /// did not answer.</summary>
    Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default);

    /// <summary>
    /// The papers on one record: who made it, what the album is, and the outside pages for both. They come
    /// in ONE request. The result is cached for everyone, by whoever asked first.
    ///
    /// <para>One request replaces up to six rate-limited calls. Those calls would take about seven seconds
    /// to fill the pane for a record nobody had looked up. Every listener would pay that cost
    /// separately.</para>
    /// </summary>
    /// <param name="album">Null or blank for a loose track. The artist is then looked up alone.</param>
    /// <param name="tracks">How many songs this copy of the record has, where the tags say so. It settles a
    /// question no catalogue can answer: an album and the single named after it are separate entries that
    /// score alike. It is not part of any cache key. It is a property of the copy, not a second question
    /// about the record.</param>
    /// <returns>The papers. These are <see cref="CatalogPapers.Empty"/> when the catalogues know nothing
    /// about the record. Or <see cref="Asked{T}.Unanswered"/> when the API could not be asked or did not
    /// answer. The two are kept apart, because the first is written down and the second must not
    /// be.</returns>
    Task<Asked<CatalogPapers>> GetPapersAsync(
        string artist, string? album, int tracks, CancellationToken ct = default);

    /// <summary>
    /// Everyone the catalogue has under a name, best first. This is the shortlist a reader picks from when
    /// the card is about the wrong act.
    ///
    /// <para>It is asked only when a reader asks for it. A card whose act was found through the record the
    /// user owns was not guessed at. Asking "who else has this name" on every lookup would spend a request
    /// on a question nobody asked.</para>
    /// </summary>
    /// <returns>An empty list when nobody else has the name; <see cref="Asked{T}.Unanswered"/> when the API
    /// did not answer. That is not the same thing, and it must not be shown as one.</returns>
    Task<Asked<IReadOnlyList<CatalogArtist>>> GetArtistsNamedAsync(
        string name, int limit, CancellationToken ct = default);

    /// <summary>One act by MusicBrainz id. This is the answer to a reader's choice from that shortlist. It
    /// is a lookup, not a search, so there is no name to check. The reader already chose it, which is why
    /// they were offered the list.</summary>
    /// <returns>Null when no catalogue knows the id (the server's 404); <see cref="Asked{T}.Unanswered"/>
    /// when the API did not answer.</returns>
    Task<Asked<CatalogArtist?>> GetArtistAsync(string mbid, CancellationToken ct = default);

    /// <summary>
    /// What the world plays of one record, by track.
    /// </summary>
    /// <param name="releaseMbid">The MusicBrainz release this album is already linked to, when it is known.
    /// It is passed on as given and is not looked up again.</param>
    /// <returns><b>Empty and unanswered are different answers and must be kept apart.</b> Empty means the
    /// question was asked and there is nothing. That is worth writing down, and it stops the record being
    /// asked about on every visit. Unanswered means the question could not be put. It must not be written
    /// down anywhere. A rate limit that was not waited out would otherwise be saved as "this record has no
    /// favourite", and that star would never appear again.</returns>
    Task<Asked<CatalogPlays>> GetPlaysAsync(
        string artist, string album, int tracks, string? releaseMbid, CancellationToken ct = default);

    /// <summary>A CD by its disc ID, with a fuzzy TOC fallback: null when no catalogue knows the disc,
    /// unanswered when the API did not answer. The rip window says "unknown disc" for the first and
    /// "the lookup failed" for the second.</summary>
    Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default);

    /// <summary>
    /// Where a release's front cover can be fetched from, at no more than <paramref name="maxSize"/>
    /// px: null when the archives hold nothing for it, unanswered when the API did not answer.
    /// </summary>
    /// <remarks>The address is never a guess. The server composes it and checks it. A release with no art
    /// gives null, not a URL that returns 404 when something finally tries to draw it.</remarks>
    Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default);

    /// <summary>
    /// The cover of the record a song is on, found by the artist and title alone. This is what an internet
    /// radio station announces, and it is all a radio player has to go on.
    /// </summary>
    /// <param name="size">The edge of the square wanted, in pixels. The server clamps it.</param>
    /// <returns><see cref="CatalogSongCover.None"/> (every field null) when nobody holds the song, which is
    /// also the answer for a jingle or an advert. <see cref="Asked{T}.Unanswered"/> when the API could not
    /// be asked or did not answer. The server says "busy" with a 503 for a song it could not look up in
    /// time, and that is unanswered too.</returns>
    Task<Asked<CatalogSongCover>> GetSongCoverAsync(
        string artist, string title, int size, CancellationToken ct = default);
}

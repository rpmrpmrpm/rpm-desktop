namespace RPM.Fingerprint.Catalog;

// The neutral cover-search wire contract between the RPM API and its clients. Unlike the release
// contracts, the upstream identity does cross this boundary. A picture found by searching the open web
// is a different kind of claim from one that a record catalogue holds against a pressing. The person
// choosing has to be told which one they are looking at, so the source is named on the badge.

/// <summary>Which of a record's two pictures is being looked for. They are different searches, not one
/// search filtered afterwards: only three of the sources file disc art at all, and asking the other
/// five for it returns sleeves that would sit in the grid pretending to be discs.</summary>
public enum CoverKind
{
    /// <summary>The sleeve. What every source holds, and the default.</summary>
    Cover,

    /// <summary>The picture printed on the disc itself, usually a transparent PNG of the round label.
    /// Only the Cover Art Archive (which types its images), TheAudioDB and fanart.tv file these.</summary>
    Disc,
}

/// <summary>
/// One picture a cover search turned up, and where it came from.
///
/// <para>Name-keyed, which is what separates this from the release-keyed cover lookup. That lookup
/// answers "what does <i>this pressing</i> look like" and needs a release id to ask. This one answers
/// "show me covers for these words". The record in hand is often not in any catalogue under the name
/// its owner types, so the user looks at pictures and points at one.</para>
///
/// <para>There are always two addresses. <see cref="ThumbUrl"/> is what the grid draws. Sixty of them
/// are drawn at once, so it must be small. <see cref="FullUrl"/> is fetched exactly once, for the one
/// the user picks. Some sources give both. Where a source offers only one size, both fields carry it,
/// and the grid draws a large picture small.</para>
/// </summary>
/// <param name="Source">Which source said so. It is shown on the result's badge: "Apple Music",
/// "Discogs", "Google Images". It is also the key that the source filter switches on. So the display
/// name and the identity are one value. There is no second, hidden id for a list of eight sources.</param>
/// <param name="Width">Pixels across, or 0 where the source does not say. 0 is not "small". It sorts as
/// unknown, which is different from a 100 px thumbnail that claims to be a cover.</param>
/// <param name="Note">The one line under the title that separates two results from the same source:
/// "1977, GB, Vinyl" for a pressing, or the site's host for a web image. Null where there is nothing to
/// add.</param>
/// <param name="PageUrl">Where the picture lives in public: the release page, or the web page it was
/// found on. Null where the source has no such page. It is used only by "Open in browser", which is the
/// one way to check a doubtful web result before taking it.</param>
/// <param name="Kind">Whether this picture is a sleeve or a disc. A source only says so when it files the
/// two apart. Everything else is a sleeve, because that is all it holds.</param>
/// <param name="DiscUrl">The disc that belongs to THIS sleeve: the same pressing, or the same release
/// group, according to the same source that provided the cover.
///
/// <para>The disc is carried on the cover rather than being a result of its own, because that is how the
/// fact arrives. The Cover Art Archive lists a release's images together and types them. TheAudioDB puts
/// both on one album row. fanart.tv files both under one release group. Splitting them into two unrelated
/// grid tiles would throw away the one thing that makes them worth having, which is that they go together.</para>
///
/// <para>Null for the five sources that hold no disc art, and for a release whose disc has not been
/// photographed.</para></param>
public sealed record CoverCandidate(
    string Source,
    string ThumbUrl,
    string FullUrl,
    int Width = 0,
    int Height = 0,
    string? Title = null,
    string? Artist = null,
    string? Note = null,
    string? PageUrl = null,
    CoverKind Kind = CoverKind.Cover,
    string? DiscThumbUrl = null,
    string? DiscUrl = null)
{
    /// <summary>Whether taking this sleeve would also get the disc that goes with it. Drives the small
    /// disc mark on the tile, and the second download when it is chosen.</summary>
    public bool HasDisc => !string.IsNullOrWhiteSpace(DiscUrl);

    /// <summary>The disc as a candidate in its own right, for the disc search's grid. Null when there
    /// is no disc. Carries the sleeve's own words, because they describe the same release.</summary>
    public CoverCandidate? AsDisc => HasDisc
        ? new CoverCandidate(Source, DiscThumbUrl ?? DiscUrl!, DiscUrl!,
              Title: Title, Artist: Artist, Note: Note, PageUrl: PageUrl, Kind: CoverKind.Disc)
        : null;

    /// <summary>"1400×1400", or null when the source did not say how big the picture is. Shown under
    /// the thumbnail, because size is most of what separates a usable cover from a decoration.
    ///
    /// <para>Spelled the way the album editor spells the size of the cover already on the record, since
    /// the two numbers are read one after the other by somebody deciding whether to replace it.</para>
    ///
    /// <para>Null is not "small" and not "unknown forever": only three of the eight sources state their
    /// dimensions, and the window measures the rest itself. See the measure pass in the search
    /// window.</para></summary>
    public string? Dimensions => Width > 0 && Height > 0 ? $"{Width}×{Height}" : null;

    /// <summary>
    /// Square within a hair's breadth, or the source not saying.
    ///
    /// <para>A sleeve is square. A web image search for one returns many things that are not: banners,
    /// artist photographs, "top 50 albums of 1977" collages. Unknown counts as square on purpose. The
    /// sources that do not give dimensions are the record catalogues, whose pictures are covers by
    /// construction. Dropping them for not answering would throw away the good results in order to
    /// filter out the bad ones.</para>
    /// </summary>
    public bool IsSquarish => Width <= 0 || Height <= 0
                           || Math.Abs((double)Width / Height - 1.0) <= 0.08;

    /// <summary>The picture's area, for ranking. 0 where the size is unknown, which is why rank never
    /// uses this alone. See the source weighting in the search service.</summary>
    public long Pixels => (long)Math.Max(Width, 0) * Math.Max(Height, 0);
}

/// <summary>
/// What a cover search returned, and who was asked.
///
/// <para><paramref name="Sources"/> is every source the server <i>can</i> ask, whether or not it
/// answered this time, so the window can offer a filter that matches what is really configured. A
/// source missing from this list has no key on the server and is not a tick box that quietly returns
/// nothing.</para>
/// </summary>
/// <param name="Cached">Whether this answer came out of the store rather than off the wire. Not shown
/// to the user; it is what makes the "Search again" button meaningfully different from re-running the
/// same search.</param>
/// <param name="ReleaseGroupIds">The MusicBrainz release groups these words turned out to mean, in
/// the order the search ranked them.
///
/// <para>Not pictures, and not for the grid. They are here for the one source that cannot be searched
/// on the server: fanart.tv keys its hand-made HD sleeves by release group and is asked on the user's
/// own key, so the client has to make that call itself. Otherwise it would have to repeat the whole
/// MusicBrainz search, on a service that allows one request a second, to learn what the server already
/// knows.</para></param>
public sealed record CoverSearchResponse(
    IReadOnlyList<CoverCandidate> Results,
    IReadOnlyList<string> Sources,
    bool Cached = false,
    IReadOnlyList<string>? ReleaseGroupIds = null);

/// <summary>What one fan-out found: the pictures, and the release groups the words resolved to along
/// the way. The second is a by-product of reaching the Cover Art Archive, which can only be found
/// through MusicBrainz. It is kept, because the client needs exactly it.</summary>
public sealed record CoverSearchFindings(
    IReadOnlyList<CoverCandidate> Results,
    IReadOnlyList<string> ReleaseGroupIds);

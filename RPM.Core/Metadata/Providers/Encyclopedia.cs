using System.Text.Json.Serialization;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// An outside site that has a page about an artist or a record. Each link is named, not left as a bare URL,
/// because a row of links is only useful when each one says where it goes. The order is a judgement: the
/// artist's own site before a social account, and an encyclopedia before a shop. A list of strings cannot
/// carry that order.
/// </summary>
public enum ReferenceSite
{
    /// <summary>The artist's or label's own site, as MusicBrainz's "official homepage" relation names
    /// it.</summary>
    OfficialSite,
    Wikipedia,
    Discogs,
    AllMusic,
    Bandcamp,
    Spotify,
    AppleMusic,
    YouTube,
    SoundCloud,
    LastFm,
    Genius,
    Instagram,
    Facebook,
    X,

    /// <summary>Where many acts still keep their blog, and where people who left Twitter went. MusicBrainz
    /// files both as "social network", so they are named here separately.</summary>
    Tumblr,
    Bluesky,

    Wikidata,

    /// <summary>A site we have no name for. It is kept and shown last, labelled by its host.</summary>
    Other,
}

/// <summary>One outside page, and which site it is on.</summary>
public sealed record ReferenceLink(ReferenceSite Site, string Url)
{
    /// <summary>The host without "www.". This is the label for an unnamed link.</summary>
    [JsonIgnore]
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri)
        ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
        : Url;
}

/// <summary>
/// What the free catalogues know about an artist. Everything is optional. This is extra information, and an
/// artist nobody has written up is an ordinary case, not a failure.
/// </summary>
/// <param name="Mbid">The MusicBrainz artist id, when one was matched. Everything else hangs off it:
/// Wikidata, the artist's photos, and the release groups.</param>
/// <param name="Kind">Such as "Group", "Person", "Orchestra" or "Choir", as MusicBrainz types them.</param>
/// <param name="Area">Where they are from, spelled out ("United Kingdom"). <paramref name="Country"/> is
/// the same fact in two letters. Either may be missing on its own.</param>
/// <param name="Began">Formed or born. A MusicBrainz date: "1967", "1967-07" or "1967-07-12".</param>
/// <param name="Ended">Disbanded or died. Null while still going.</param>
/// <param name="HasEnded">True when MusicBrainz says the life span is closed. This is separate from
/// <paramref name="Ended"/> being set. A band can be known to have split without the date being
/// known.</param>
/// <summary>
/// One person in a band, as the catalogue has them.
/// </summary>
/// <param name="Born">Their date of birth, as far as it is known. It may be a whole date, a month or a
/// year. The catalogue keeps this on the PERSON, not on their membership. So a member list needs a second
/// question about the people in it.</param>
/// <param name="Died">The day they died, where that is known. Null for the living, and for the many entries
/// that do not say.</param>
/// <param name="Role">What they played, in plain words: "guitar", "guitar, lead vocals" or "drums". Null
/// where the catalogue names none. See <see cref="BandRoles.Spoken"/>, which turns the MusicBrainz
/// attribute list into this.
///
/// <para>This has a default, and that is not a correctness hole. A membership with no instrument is an
/// ordinary thing the catalogue holds, and null says exactly that. It also means a profile cached before
/// this field existed loads as a lineup with no roles, rather than failing to load. That is what such a
/// cache honestly holds until it is fetched again.</para></param>
public sealed record BandMember(
    string Name,
    string? Mbid = null,
    string? Born = null,
    string? Died = null,
    bool HasDied = false,
    string? Role = null);

public sealed record ArtistProfile(
    string? Mbid,
    string Name,
    string? Disambiguation = null,
    string? Kind = null,
    string? Country = null,
    string? Area = null,
    string? Began = null,
    string? Ended = null,
    bool HasEnded = false,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<ReferenceLink>? Links = null,
    IReadOnlyList<BandMember>? Members = null,
    IReadOnlyList<string>? DiscogsArtistIds = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> Tags => Genres ?? [];

    [JsonIgnore]
    public IReadOnlyList<ReferenceLink> Pages => Links ?? [];

    /// <summary>
    /// Every Discogs artist number the catalogue holds for this act, in the order it gave them.
    ///
    /// <para>Kept apart from <see cref="Pages"/>, because the two answer different questions, and their
    /// answers have different lengths. A page of links needs ONE Discogs link. A reader who presses
    /// "Discogs" wants to go to the artist, not to the four entries somebody filed them under. So
    /// <see cref="ReferenceLinks.Merge"/> keeps the first per site and drops the rest. A ranking of an
    /// artist's best-known records needs all of them. The order of MusicBrainz's relations does not say
    /// which number holds the records.</para>
    ///
    /// <para>Adele is the case that made this necessary. MusicBrainz gives her two numbers. 1638187 is
    /// "Adele Adkins", the writing and producing credit. 819015 is "Adele (3)", the records. The merge kept
    /// only the first. The only record credited to the writing page as its own is a pop compilation that
    /// two people own. So her most popular record came out as "Divas Del Pop", although her first album
    /// alone is in fourteen thousand collections.</para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> DiscogsIds => DiscogsArtistIds ?? [];

    /// <summary>Who is in the band, oldest membership first. Empty for a person, and for a band nobody has
    /// filled in.</summary>
    [JsonIgnore]
    public IReadOnlyList<BandMember> Lineup => Members ?? [];
}

/// <summary>
/// What the free catalogues know about a record. This is the release group, not one pressing. "Rumours" is
/// one album, and its 1977 UK cut, its 1984 CD and its 2013 remaster are editions of it. The pressing in
/// the user's library is already described by its own tags.
/// </summary>
/// <param name="FirstReleased">When the album first came out, in any edition. This is the date people mean
/// when they give a record's year. It is not the date printed on the copy the user owns.</param>
/// <param name="PrimaryType">"Album", "Single", "EP", "Broadcast" or "Other".</param>
/// <param name="SecondaryTypes">"Live", "Compilation", "Soundtrack", "Remix" and so on. Often
/// empty.</param>
public sealed record AlbumProfile(
    string? Mbid,
    string Title,
    string? Artist = null,
    string? Disambiguation = null,
    string? FirstReleased = null,
    string? PrimaryType = null,
    IReadOnlyList<string>? SecondaryTypes = null,
    IReadOnlyList<string>? Genres = null,
    IReadOnlyList<ReferenceLink>? Links = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> Kinds => SecondaryTypes ?? [];

    [JsonIgnore]
    public IReadOnlyList<string> Tags => Genres ?? [];

    [JsonIgnore]
    public IReadOnlyList<ReferenceLink> Pages => Links ?? [];
}

/// <summary>
/// A Wikipedia lead section: the opening of the article, in plain text, with the page it was read from.
/// </summary>
/// <param name="Language">The wiki it came from, such as "en" or "fr". It is shown, because an article read
/// in the user's own language and one read in English are different things. The second is a
/// fallback.</param>
/// <param name="Extract">Plain text, with paragraphs separated by blank lines. There is no markup. It is
/// shown as prose, and Wikipedia's HTML would need cleaning before it was safe to show.</param>
public sealed record EncyclopediaArticle(
    string Language,
    string Title,
    string Extract,
    string Url,
    string? ThumbnailUrl = null)
{
    /// <summary>The first paragraph, which the pane shows before the reader asks for more.</summary>
    [JsonIgnore]
    public string Lead
    {
        get
        {
            var end = Extract.IndexOf("\n\n", StringComparison.Ordinal);
            return end < 0 ? Extract : Extract[..end];
        }
    }

    /// <summary>True when there is more than the lead paragraph. Only then is the "read more" control worth
    /// showing.</summary>
    [JsonIgnore]
    public bool HasMore => Extract.Contains("\n\n", StringComparison.Ordinal);
}

/// <summary>
/// Everything the outside world says about the record now playing: the artist, the album, and the
/// encyclopedia article for each. Every member is optional on its own. A lookup that partly worked still
/// has something to show, and showing it is the point.
/// </summary>
/// <param name="ArtistShortlist">Everyone the catalogue offered when the artist was searched by name, best
/// first. This includes whoever was chosen. Empty when the name was never the question. See
/// <see cref="Shortlist"/>.</param>
public sealed record TrackDossier(
    ArtistProfile? Artist = null,
    AlbumProfile? Album = null,
    EncyclopediaArticle? ArtistArticle = null,
    EncyclopediaArticle? AlbumArticle = null,
    IReadOnlyList<ArtistProfile>? ArtistShortlist = null)
{
    public static readonly TrackDossier Empty = new();

    [JsonIgnore]
    public bool IsEmpty => Artist is null && Album is null && ArtistArticle is null && AlbumArticle is null;

    /// <summary>
    /// The artists a name search offered, best first, with the one that was taken among them.
    ///
    /// <para>This is kept because the search is a guess, and the guess is sometimes wrong. A MusicBrainz
    /// query is a Lucene relevance score, not a filter. A name that several acts share is settled by
    /// whichever act the index ranks first. "Ground Zero" is an American metalcore band, a Japanese
    /// experimental group and several others. The card came out about the wrong one. Nothing in the
    /// catalogue can settle that. A reader can settle it in a second, because the line under each name
    /// shows what tells them apart.</para>
    ///
    /// <para>Empty when the artist was found through the RECORD, not by name. That is the good path and the
    /// usual one. A release group names its artist with their id attached, so there was one answer and
    /// never a list. A card with nothing here has nothing to choose between. That is the honest reason it
    /// offers no choice.</para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ArtistProfile> Shortlist => ArtistShortlist ?? [];

    /// <summary>The album's and the artist's outside pages as one row, with one entry per site. The album's
    /// links come first, because they are the more specific answer. Its Discogs page is about this record.
    /// Its artist's page is about everything the artist made. MusicBrainz often gives the same URL from
    /// both ends, so one of the two has to win.</summary>
    [JsonIgnore]
    public IReadOnlyList<ReferenceLink> Links => ReferenceLinks.Merge(
        Album?.Pages ?? [], Artist?.Pages ?? []);
}

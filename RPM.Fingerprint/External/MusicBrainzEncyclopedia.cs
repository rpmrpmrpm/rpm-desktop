using System.Text.Json;
using RPM.Fingerprint.Catalog;

namespace RPM.Fingerprint.External;

/// <summary>One GET that returns a body, or null when the service has no such item.</summary>
public delegate Task<string?> FetchJson(string url, CancellationToken ct);

/// <summary>
/// The artist and the album as MusicBrainz has them. These are the two entities that the track-info
/// pane and the artist page show, as opposed to the release lookups that the ripper and the splitter
/// make.
///
/// <para>This runs in the RPM API, not in the desktop app. The app asks the API one question and is
/// never told which catalogue answered. This gives one shared cache, one rate limit and one point of
/// contact, and the source can be changed without a new desktop build.</para>
///
/// <para>The album is looked up first, even though the pane shows the artist first, because a
/// release-group search returns the artist WITH their id. So the record the user owns identifies the
/// artist, not a name that several bands share. The artist is searched by name only when there is no
/// album.</para>
///
/// <para>Every step may return empty, and that is a valid answer: the catalogue has no such item. A step
/// that could not ask is not empty. It throws <see cref="MusicBrainzUnreachableException"/>, and the
/// caller decides what the missing part means. Do not turn that exception into null: a timeout would
/// then be cached as "no record found".</para>
/// </summary>
public sealed class MusicBrainzEncyclopedia(FetchJson fetch)
{
    /// <summary>The live service, behind the same shared rate limit as every other MusicBrainz call.
    /// A user agent is required: the service refuses requests without a real one.</summary>
    public static MusicBrainzEncyclopedia Live(string userAgent)
        => new((url, ct) => MusicBrainzClient.FetchAsync(url, userAgent, ct));

    private const string Base = "https://musicbrainz.org/ws/2";

    /// <summary>The most requests one uncached lookup makes: the album by name, the artist by name or
    /// alias, the album again by that artist's id, a detail lookup for each of the two, and (for a
    /// band) one more for the dates of all members. At one request per second this is also the
    /// minimum time it takes, which is why the result is cached for all users.</summary>
    public const int MaxRequests = 6;

    /// <summary>
    /// The artist alone, by name. This is the slow path, used for artists that the faster catalogues
    /// do not know.
    /// </summary>
    public async Task<CatalogArtist?> ArtistByNameAsync(string? artist, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(artist)
            ? null
            : await LookupArtistAsync(await SearchArtistMbidAsync(artist, ct), ct);

    /// <summary>
    /// Every artist the catalogue has under a name, best first. This is the shortlist the user picks
    /// from when the card shows the wrong artist.
    ///
    /// <para>One search and no lookups. The shortlist shows a name and the disambiguation line, and
    /// the search returns both. Looking up each one would cost five more rate-limited calls for
    /// fields that are not shown until a row is chosen.</para>
    ///
    /// <para>An exact name match is moved to the front. The Lucene score decides between several real
    /// artists, but it must not rank them above the one whose name is exactly what was asked for.</para>
    ///
    /// <para><b>The search asks for <see cref="Searched"/> results, not <paramref name="limit"/></b>,
    /// because only results that came back can be moved to the front. An exact match can be far down
    /// the list (see <see cref="Searched"/>). The shortlist is still <paramref name="limit"/> long: it
    /// is cut after the exact match is moved, not before.</para>
    /// </summary>
    public async Task<IReadOnlyList<CatalogArtist>> ArtistsNamedAsync(
        string? artist, int limit = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return [];

        if (await SearchArtistsAsync(artist, ct) is not { Count: > 0 } hits)
            return [];

        return [.. hits.Where(h => NameMatch.Same(h.Name, artist))
                       .Concat(hits.Where(h => !NameMatch.Same(h.Name, artist)))
                       .Take(Math.Max(limit, 1))];
    }

    /// <summary>
    /// How many results the name search reads before choosing.
    ///
    /// <para>The value comes from a measurement. MusicBrainz scores
    /// <c>artist:"ROSÉ" OR alias:"ROSÉ"</c> with Karen Rose (an author) at 100 and ROSÉ herself at 76,
    /// in twentieth place, because the query ignores accents and every "Rose" in the index matches.
    /// The exact match must be in the results before it can be preferred; with only five results, the
    /// card shows a different artist with a similar name.</para>
    ///
    /// <para>It is one request either way. The cost is a longer body, not another rate-limited
    /// second.</para>
    /// </summary>
    private const int Searched = 25;

    /// <summary>Every artist the catalogue has under a name, with an id, in the catalogue's order.
    /// Both the shortlist and the id search read this, so they always search the same results.</summary>
    private async Task<IReadOnlyList<CatalogArtist>> SearchArtistsAsync(string artist, CancellationToken ct)
    {
        var name = MusicBrainzClient.Escape(artist);
        var query = $"artist:\"{name}\" OR alias:\"{name}\"";
        var url = $"{Base}/artist?query={Uri.EscapeDataString(query)}&limit={Searched}&fmt=json";

        if (await Read(url, ct) is not { } body)
            return [];

        return [.. (Safely(() => ParseArtistSearch(body)) ?? []).Where(h => h.Id is { Length: > 0 })];
    }

    /// <summary>
    /// The artist by their id: the same answer as <see cref="ArtistByNameAsync"/> with half the
    /// requests and no uncertainty.
    ///
    /// <para>A search is a Lucene score over the whole index, not a filter, so it returns the nearest
    /// match even when that match is wrong, and the name must then be checked. An id has neither
    /// problem. Call this only when the name the id came from has already been checked, which is what
    /// <c>Keyring</c> does.</para>
    /// </summary>
    public async Task<CatalogArtist?> ArtistByMbidAsync(string? mbid, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(mbid) ? null : await LookupArtistAsync(mbid, ct);

    /// <summary>
    /// The album alone.
    ///
    /// <para><paramref name="artistId"/> is a function, not a value, and it is called only on the path
    /// that needs it. A search by name finds the album for nearly every artist. The id is needed only
    /// when it does not: a renamed artist keeps their records under the new name, so a release group
    /// by "Ye" cannot be found by searching for "Kanye West". An id has no spelling. Because the id is
    /// requested late, this can run at the same time as the artist lookup that produces it.</para>
    /// </summary>
    public async Task<CatalogAlbum?> AlbumAsync(
        string? artist, string? album, int tracks, Func<Task<string?>> artistId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return null;

        var wantsAlbum = AlbumSized(tracks);

        var (found, _) = await LookupAlbumAsync(artist, album, wantsAlbum, ct);
        if (found is not null)
            return found;

        return await artistId() is { Length: > 0 } id
            ? await LookupAlbumByArtistAsync(id, album, wantsAlbum, ct)
            : null;
    }

    /// <summary>
    /// The album, and the id of the artist that the catalogue credits for it. The id is the more
    /// useful of the two: it identifies the artist through a record, not through a name.
    ///
    /// <para>The result is CHECKED before either is used. A MusicBrainz query is a Lucene score over
    /// the whole index, not a filter: if it does not have the record, it returns the nearest one it
    /// has, with a high score, credited to a different artist. Using that artist id makes the pane
    /// show "Kanye West Tribute Band": the tribute act is a real entry, it matches the words, and
    /// later code cannot tell the difference.</para>
    /// </summary>
    private async Task<(CatalogAlbum? Album, string? ArtistMbid)> LookupAlbumAsync(
        string artist, string? album, bool wantsAlbum, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(album))
            return (null, null);

        var query = $"releasegroup:\"{MusicBrainzClient.Escape(album)}\" AND artist:\"{MusicBrainzClient.Escape(artist)}\"";
        var url = $"{Base}/release-group?query={Uri.EscapeDataString(query)}&limit=5&fmt=json";

        if (await Read(url, ct) is not { } body)
            return (null, null);

        var hits = (Safely(() => ParseReleaseGroupSearch(body)) ?? [])
            .Where(h => NameMatch.Same(h.Artist, artist))
            .ToList();

        var best = Preferred(hits, album, wantsAlbum);
        if (best is null)
            return (null, null);   // another artist's record; the caller asks again by artist id

        var artistMbid = Safely(() => ParseCreditedArtistMbid(body, best.Id));
        return (await DetailAsync(best, ct), artistMbid);
    }

    /// <summary>
    /// The album again, this time searched by the artist's own id.
    ///
    /// <para>This finds albums that the name search misses: a renamed artist keeps their records under
    /// the new name, so a release group by "Ye" cannot be found by searching for "Kanye West". An id
    /// has no spelling.</para>
    /// </summary>
    private async Task<CatalogAlbum?> LookupAlbumByArtistAsync(
        string artistMbid, string album, bool wantsAlbum, CancellationToken ct)
    {
        var query = $"arid:{MusicBrainzClient.Escape(artistMbid)} AND releasegroup:\"{MusicBrainzClient.Escape(album)}\"";
        var url = $"{Base}/release-group?query={Uri.EscapeDataString(query)}&limit=5&fmt=json";

        if (await Read(url, ct) is not { } body)
            return null;

        var hits = (Safely(() => ParseReleaseGroupSearch(body)) ?? [])
            .Where(h => NameMatch.Same(h.Title, album))
            .ToList();

        return Preferred(hits, album, wantsAlbum) is { } best ? await DetailAsync(best, ct) : null;
    }

    /// <summary>
    /// Picks which of the catalogue's results is the record being played.
    ///
    /// <para>A title is often shared by a single and by the album named after it. They are separate
    /// release groups with the same score for the same query. Without this choice, the first result
    /// wins, and a fourteen-track album can show as "Single · 2020" with the single's date and
    /// genres.</para>
    /// </summary>
    private static CatalogAlbum? Preferred(IReadOnlyList<CatalogAlbum> hits, string album, bool wantsAlbum)
    {
        if (hits.Count == 0)
            return null;

        // An exact title wins over a near match, whatever their types: if a search for "Discovery"
        // returns "Discovery (Deluxe)" first, that result is earlier, not better.
        var named = hits.Where(h => NameMatch.Same(h.Title, album)).ToList();
        var pool = named.Count > 0 ? named : hits;

        if (!wantsAlbum)
            return pool[0];

        return pool.FirstOrDefault(h => string.Equals(h.PrimaryType, "Album", StringComparison.OrdinalIgnoreCase))
               ?? pool[0];
    }

    /// <summary>
    /// Whether to treat a title as an album instead of the single named after it.
    ///
    /// <para>The limit is three tracks, the industry's own rule: a release of three tracks or fewer is
    /// a single, and EPs and albums start at four.</para>
    ///
    /// <para>A count of ZERO means the tags have no track count, and then the album still wins. Most of
    /// a music library is albums, so an untagged track is more likely to be from an album than a
    /// single, and the album entry has more information if it is wrong. Only a count that is known to
    /// be small chooses the single.</para>
    /// </summary>
    public static bool AlbumSized(int tracks) => tracks == 0 || tracks > SingleTracks;

    private const int SingleTracks = 3;

    /// <summary>
    /// The album by its release-group id.
    ///
    /// <para>The album part of the pane depends on this call because of <c>url-rels</c>: the links a
    /// lookup returns give the album's Wikidata entity, and the entity is used to fetch the Wikipedia
    /// article in the user's language. A search returns only a title and a date. Only this lookup
    /// leads to the article text.</para>
    ///
    /// <para>So a lookup by id matters more here than the second it saves: a search that finds the
    /// wrong release group does not only show the wrong label, it shows a different record's
    /// article.</para>
    /// </summary>
    public async Task<CatalogAlbum?> AlbumByMbidAsync(string? releaseGroupMbid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(releaseGroupMbid))
            return null;

        var body = await Read(
            $"{Base}/release-group/{releaseGroupMbid}?inc=url-rels+genres+artist-credits&fmt=json", ct);

        return body is null ? null : Safely(() => ParseReleaseGroup(body));
    }

    /// <summary>The search returns the album's name and date. Only a lookup returns its genres and
    /// the links to pages about it, which is most of what the pane shows.</summary>
    private async Task<CatalogAlbum?> DetailAsync(CatalogAlbum best, CancellationToken ct)
        => best.Id is { Length: > 0 } ? await AlbumByMbidAsync(best.Id, ct) ?? best : best;

    /// <summary>
    /// The artist's id, from a search by every name they have had.
    ///
    /// <para>Name OR alias, and the alias is required. Artists get renamed (Kanye West is stored as
    /// "Ye"), and a name-only search for the old name matches no artist, but it does match every
    /// tribute act and mashup project that still has the old name in THEIR name.</para>
    ///
    /// <para>An exact name match still wins when there is one: the score decides which Nirvana is
    /// meant, but it must never rank above the artist whose name is exactly what was asked for.</para>
    /// </summary>
    private async Task<string?> SearchArtistMbidAsync(string artist, CancellationToken ct)
    {
        var hits = await SearchArtistsAsync(artist, ct);
        var best = hits.FirstOrDefault(h => NameMatch.Same(h.Name, artist)) ?? hits.FirstOrDefault();
        return best?.Id;
    }

    private async Task<CatalogArtist?> LookupArtistAsync(string? mbid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mbid))
            return null;   // no artist with that name or alias; the file's own tags are kept

        // artist-rels is added to the genres and the links on the same request. It returns the band's
        // members.
        var url = $"{Base}/artist/{mbid}?inc=url-rels+genres+artist-rels&fmt=json";
        if (await Read(url, ct) is not { } body)
            return null;

        var artist = Safely(() => ParseArtist(body));
        if (artist is null)
            return null;

        var members = Safely(() => ParseBandMembers(body)) ?? [];
        return members.Count == 0 ? artist : artist with { Members = await DatedAsync(members, ct) };
    }

    /// <summary>
    /// The band's members, with each member's own dates.
    ///
    /// <para>One request for all of them. The relation that names a member has only their name and id
    /// (a person's birth date is stored on the person), and one lookup each would cost one
    /// rate-limited second per musician. A search for all their ids together takes one request, and
    /// search results include life-spans.</para>
    ///
    /// <para>A member that the search does not return keeps their name and has no dates. As in the
    /// rest of this file, missing data is left out, never invented.</para>
    /// </summary>
    private async Task<IReadOnlyList<CatalogArtistMember>> DatedAsync(
        IReadOnlyList<CatalogArtistMember> members, CancellationToken ct)
    {
        var ids = members.Where(m => m.Id is { Length: > 0 }).Select(m => m.Id!).ToList();
        if (ids.Count == 0)
            return members;

        var query = $"arid:({string.Join(" OR ", ids.Take(MaxMembersAsked))})";
        var url = $"{Base}/artist?query={Uri.EscapeDataString(query)}&limit={MaxMembersAsked}&fmt=json";

        if (await Read(url, ct) is not { } body)
            return members;

        var dated = (Safely(() => ParseArtistSearch(body)) ?? [])
            .Where(a => a.Id is { Length: > 0 })
            .ToDictionary(a => a.Id!, StringComparer.OrdinalIgnoreCase);

        return [.. members.Select(m =>
            m.Id is { Length: > 0 } id && dated.TryGetValue(id, out var person)
                ? m with { Born = person.Began, Died = person.Ended, HasDied = person.HasEnded }
                : m)];
    }

    /// <summary>The most members asked about. A longer list is something like an orchestra, and is
    /// too long to show on the card.</summary>
    private const int MaxMembersAsked = 25;

    // ── reading the catalogue's two shapes ──
    //
    // The search endpoint and the lookup endpoint return different shapes for the same entity. A
    // search has a score and no relations. A lookup has relations and genres and no score. The
    // difference shows only when a record with an unusual shape comes through.

    public static IReadOnlyList<CatalogArtist> ParseArtistSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var artists)
            || artists.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<(double Score, CatalogArtist Artist)>();
        foreach (var artist in artists.EnumerateArray())
        {
            var score = artist.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
            found.Add((score, ReadArtist(artist)));
        }

        return [.. found.OrderByDescending(f => f.Score).Select(f => f.Artist)];
    }

    public static CatalogArtist? ParseArtist(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("name", out _)
            ? ReadArtist(doc.RootElement)
            : null;
    }

    /// <summary>
    /// The members of this band according to the catalogue, in the catalogue's order, with names and
    /// ids only.
    ///
    /// <para>Read from the artist-to-artist relations of a lookup. A "member of band" relation with
    /// direction BACKWARD is a person in this band. The same relation with direction forward means
    /// this band is a member of something else (for example a supergroup), and is ignored.</para>
    /// </summary>
    public static IReadOnlyList<CatalogArtistMember> ParseBandMembers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("relations", out var relations)
            || relations.ValueKind != JsonValueKind.Array)
            return [];

        var members = new List<CatalogArtistMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in relations.EnumerateArray())
        {
            if (Text(relation, "type") is not "member of band"
                || Text(relation, "direction") is not "backward"
                || !relation.TryGetProperty("artist", out var artist))
                continue;

            if (Text(artist, "name") is not { Length: > 0 } name)
                continue;

            // One entry per person. A member who left and rejoined has two relations, but is one
            // musician, and a list that shows them twice looks like an error.
            var id = Text(artist, "id");
            if (!seen.Add(id ?? name))
                continue;

            members.Add(new CatalogArtistMember(name, id));
        }

        return members;
    }

    private static CatalogArtist ReadArtist(JsonElement e)
    {
        var (began, ended, hasEnded) = ReadLifeSpan(e);
        return new CatalogArtist(
            Name: Text(e, "name") ?? "",
            Id: Text(e, "id"),
            Kind: Text(e, "type"),
            Disambiguation: Text(e, "disambiguation"),
            Country: Text(e, "country"),
            // "area" is where they are from. The search endpoint often returns only one of the two,
            // and a begin-area ("Liverpool") is more specific when both are present.
            Area: Nested(e, "begin-area", "name") ?? Nested(e, "area", "name"),
            Began: began,
            Ended: ended,
            HasEnded: hasEnded,
            Genres: ReadGenres(e),
            Links: ReadRelationLinks(e));
    }

    public static IReadOnlyList<CatalogAlbum> ParseReleaseGroupSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<(double Score, CatalogAlbum Album)>();
        foreach (var group in groups.EnumerateArray())
        {
            var score = group.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
            found.Add((score, ReadReleaseGroup(group)));
        }

        return [.. found.OrderByDescending(f => f.Score).Select(f => f.Album)];
    }

    public static CatalogAlbum? ParseReleaseGroup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("title", out _)
            ? ReadReleaseGroup(doc.RootElement)
            : null;
    }

    private static CatalogAlbum ReadReleaseGroup(JsonElement e)
    {
        var secondary = new List<string>();
        if (e.TryGetProperty("secondary-types", out var types) && types.ValueKind == JsonValueKind.Array)
            foreach (var type in types.EnumerateArray())
                if (type.GetString() is { Length: > 0 } name)
                    secondary.Add(name);

        return new CatalogAlbum(
            Title: Text(e, "title") ?? "",
            Id: Text(e, "id"),
            Artist: JoinArtistCredit(e),
            ArtistId: CreditedArtistId(e),
            Disambiguation: Text(e, "disambiguation"),
            FirstReleased: Text(e, "first-release-date"),
            PrimaryType: Text(e, "primary-type"),
            SecondaryTypes: secondary,
            Genres: ReadGenres(e),
            Links: ReadRelationLinks(e));
    }

    /// <summary>
    /// The artist id credited on a release group. Cheaper than a second search: a release-group search
    /// for the album already returns its artist with the id, and the artist lookup should use that id.
    /// It is matched through the record the user owns, not through a name that several bands
    /// share.
    /// </summary>
    /// <param name="releaseGroupMbid">Which group in a search result to read the credit from. The
    /// CALLER decides, because only the caller knows which result it accepted: a search returns the
    /// nearest match even when it is not the record asked for, and the top-scoring group is often a
    /// tribute act's.</param>
    public static string? ParseCreditedArtistMbid(string json, string? releaseGroupMbid = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("release-groups", out var groups)
            && groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() > 0)
        {
            var named = releaseGroupMbid is { Length: > 0 }
                ? groups.EnumerateArray().FirstOrDefault(g => Text(g, "id") == releaseGroupMbid)
                : default;

            root = named.ValueKind == JsonValueKind.Object
                ? named
                : groups.EnumerateArray()
                    .OrderByDescending(g => g.TryGetProperty("score", out var s) && s.TryGetDouble(out var v) ? v : 0)
                    .First();
        }

        return CreditedArtistId(root);
    }

    private static string? CreditedArtistId(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var credit in credits.EnumerateArray())
            if (credit.TryGetProperty("artist", out var artist) && Text(artist, "id") is { } id)
                return id;

        return null;
    }

    /// <summary>
    /// The genres a lookup returns, most votes first. The number is limited so that an artist with
    /// many tags does not fill the pane with genres that only one or two people voted for.
    ///
    /// <para>Genres, not tags. MusicBrainz has both, and tags are unmoderated free text: the tag list
    /// for a famous band contains its members' names, "seen live" and worse. Genres are the curated
    /// subset, and are safe to show without checking them first.</para>
    /// </summary>
    private static IReadOnlyList<string> ReadGenres(JsonElement e, int max = 6)
    {
        if (!e.TryGetProperty("genres", out var genres) || genres.ValueKind != JsonValueKind.Array)
            return [];

        var scored = new List<(int Count, string Name)>();
        foreach (var genre in genres.EnumerateArray())
        {
            if (Text(genre, "name") is not { Length: > 0 } name)
                continue;
            var count = genre.TryGetProperty("count", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            scored.Add((count, name));
        }

        return [.. scored.OrderByDescending(g => g.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.Name).Take(max)];
    }

    /// <summary>
    /// The external pages that a lookup's relations link to, in the catalogue's order. Links marked
    /// ended (for example a homepage that no longer exists) are removed: a dead link shown as a source
    /// is worse than no source.
    ///
    /// <para>Not filtered further, and kept in relation order, because two callers need different
    /// things from the same list: a row of links needs one link per site, and the card that ranks an
    /// artist's records needs every Discogs number. Filtering here would decide for both.</para>
    /// </summary>
    private static IReadOnlyList<CatalogLink> ReadRelationLinks(JsonElement e)
    {
        if (!e.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return [];

        var links = new List<CatalogLink>();
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True)
                continue;

            if (Nested(relation, "url", "resource") is not { Length: > 0 } url)
                continue;

            links.Add(new CatalogLink(url, Text(relation, "type")));
        }

        return links;
    }

    private static (string? Began, string? Ended, bool HasEnded) ReadLifeSpan(JsonElement e)
    {
        if (!e.TryGetProperty("life-span", out var span) || span.ValueKind != JsonValueKind.Object)
            return (null, null, false);

        var hasEnded = span.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True;
        return (Text(span, "begin"), Text(span, "end"), hasEnded);
    }

    private static string? JoinArtistCredit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return null;

        var names = new List<string>();
        foreach (var credit in credits.EnumerateArray())
            if (Text(credit, "name") is { Length: > 0 } name)
                names.Add(name);
            else if (credit.TryGetProperty("artist", out var artist) && Text(artist, "name") is { Length: > 0 } n)
                names.Add(n);

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static string? Text(JsonElement e, string property) =>
        e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? s : null;

    private static string? Nested(JsonElement e, string property, string child) =>
        e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object
            ? Text(value, child) : null;

    // ── asking ──

    /// <summary>One GET. Null means the catalogue has no such item. Any failure to ask throws, and is
    /// not caught here: see the class remarks.</summary>
    private Task<string?> Read(string url, CancellationToken ct) => fetch(url, ct);

    private static T? Safely<T>(Func<T?> parse)
    {
        try
        {
            return parse();
        }
        catch
        {
            return default;   // an unknown shape; the other results are still used
        }
    }
}

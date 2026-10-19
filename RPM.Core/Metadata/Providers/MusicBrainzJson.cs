using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Reads MusicBrainz's artist and release-group answers. These are the two entities the track-info pane is
/// about. The release lookups used by the ripper and the splitter are separate.
///
/// <para>This only parses. It has no network code, so the shapes can be tested against the payloads the
/// service really returns. That matters more here than usual. The same field is spelled differently by the
/// search endpoint and the lookup endpoint. A search scores its hits and includes no nested data. A lookup
/// includes relations and genres, and no score. The difference is invisible until a record with an unusual
/// shape comes through.</para>
/// </summary>
public static class MusicBrainzJson
{
    /// <summary>
    /// The artists a name search matched, best first. The search endpoint's own score decides. It is a
    /// Lucene relevance score over the whole index. It is a far better judge of "did they mean this
    /// Nirvana" than anything that can be worked out from the fields.
    /// </summary>
    public static IReadOnlyList<ArtistProfile> ParseArtistSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var artists)
            || artists.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<(double Score, ArtistProfile Artist)>();
        foreach (var artist in artists.EnumerateArray())
        {
            var score = artist.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
            found.Add((score, ReadArtist(artist)));
        }

        return [.. found.OrderByDescending(f => f.Score).Select(f => f.Artist)];
    }

    /// <summary>
    /// Who the catalogue says is in this band, in the order it lists them. Names and ids only.
    ///
    /// <para>Read from the artist-to-artist relations of a lookup. A "member of band" relation that points
    /// BACKWARD names somebody who is in this band. The same relation pointing forward means this band is a
    /// member of something else, such as a supergroup's constituent acts. That is a different fact, so it
    /// is not included. Other relations, such as a tribute act, a session player or a founder, are people
    /// connected to the band. They are not members of it.</para>
    ///
    /// <para>Names, ids and what they PLAYED. Dates are not here. They belong to the person, and getting
    /// them is a second question. The instrument is here, because it is a fact about somebody's time in
    /// this band, not about them as a person.</para>
    ///
    /// <para>The attribute list is not read as a role. It holds how somebody was a member, as well as what
    /// they played. It is in the catalogue's own vocabulary and in no useful order. See
    /// <see cref="BandRoles.Spoken"/>, which handles that.</para>
    /// </summary>
    public static IReadOnlyList<BandMember> ParseBandMembers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("relations", out var relations)
            || relations.ValueKind != JsonValueKind.Array)
            return [];

        var members = new List<BandMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relation in relations.EnumerateArray())
        {
            if (Text(relation, "type") is not "member of band"
                || Text(relation, "direction") is not "backward"
                || !relation.TryGetProperty("artist", out var artist))
                continue;

            if (Text(artist, "name") is not { Length: > 0 } name)
                continue;

            // One entry per person. A member who left and rejoined has two relations and is one musician. A
            // list that names them twice would look like a mistake.
            var id = Text(artist, "id");
            if (!seen.Add(id ?? name))
                continue;

            members.Add(new BandMember(name, id, Role: BandRoles.Spoken(Words(relation, "attributes"))));
        }

        return members;
    }

    /// <summary>The strings in one array property. This gives nothing where the property is missing or is
    /// not an array. Anything that is not a string is passed over, not shown. A number where a word is
    /// expected means the data is not what this app should guess at.</summary>
    private static IReadOnlyList<string> Words(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var words = new List<string>();
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } word)
                words.Add(word);

        return words;
    }

    /// <summary>One artist as the lookup endpoint gives them: the same fields the search carries, plus the
    /// relations and genres that only a lookup asks for.</summary>
    public static ArtistProfile? ParseArtist(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("name", out _)
            ? ReadArtist(doc.RootElement)
            : null;
    }

    private static ArtistProfile ReadArtist(JsonElement e)
    {
        var (began, ended, hasEnded) = ReadLifeSpan(e);
        return new ArtistProfile(
            Mbid: Text(e, "id"),
            Name: Text(e, "name") ?? "",
            Disambiguation: Text(e, "disambiguation"),
            Kind: Text(e, "type"),
            Country: Text(e, "country"),
            // "area" is where they are from. The search endpoint often carries only one of the two fields.
            // A begin-area ("Liverpool") is the more useful answer when both are there.
            Area: Nested(e, "begin-area", "name") ?? Nested(e, "area", "name"),
            Began: began,
            Ended: ended,
            HasEnded: hasEnded,
            Genres: ReadGenres(e),
            Links: ReadRelationLinks(e),
            // Read from the relations BEFORE they are thinned to one link per site. See
            // ArtistProfile.DiscogsIds for why the card that ranks an artist's records cannot use the list
            // the page's links are drawn from.
            DiscogsArtistIds: ReadDiscogsArtistIds(e));
    }

    /// <summary>Every Discogs artist number among an entity's relations, in the order they were given, with
    /// no repeats. This reads the raw relations, not the merged links. That is the whole point (see
    /// <see cref="ArtistProfile.DiscogsIds"/>).</summary>
    private static IReadOnlyList<string> ReadDiscogsArtistIds(JsonElement e)
    {
        if (!e.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return [];

        var ids = new List<string>();
        foreach (var relation in relations.EnumerateArray())
        {
            if (Nested(relation, "url", "resource") is not { Length: > 0 } url)
                continue;
            if (ReferenceLinks.DiscogsArtistId(url) is not { Length: > 0 } id)
                continue;
            if (!ids.Contains(id, StringComparer.Ordinal))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The release groups a search matched, best first. Each one is an album, not one pressing of it.
    /// </summary>
    public static IReadOnlyList<AlbumProfile> ParseReleaseGroupSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("release-groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
            return [];

        var found = new List<(double Score, AlbumProfile Album)>();
        foreach (var group in groups.EnumerateArray())
        {
            var score = group.TryGetProperty("score", out var s) && s.TryGetDouble(out var sv) ? sv : 0;
            found.Add((score, ReadReleaseGroup(group)));
        }

        return [.. found.OrderByDescending(f => f.Score).Select(f => f.Album)];
    }

    /// <summary>One release group as the lookup endpoint gives it, with its relations and genres.</summary>
    public static AlbumProfile? ParseReleaseGroup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("title", out _)
            ? ReadReleaseGroup(doc.RootElement)
            : null;
    }

    private static AlbumProfile ReadReleaseGroup(JsonElement e)
    {
        var secondary = new List<string>();
        if (e.TryGetProperty("secondary-types", out var types) && types.ValueKind == JsonValueKind.Array)
            foreach (var type in types.EnumerateArray())
                if (type.GetString() is { Length: > 0 } name)
                    secondary.Add(name);

        return new AlbumProfile(
            Mbid: Text(e, "id"),
            Title: Text(e, "title") ?? "",
            Artist: JoinArtistCredit(e),
            Disambiguation: Text(e, "disambiguation"),
            FirstReleased: Text(e, "first-release-date"),
            PrimaryType: Text(e, "primary-type"),
            SecondaryTypes: secondary,
            Genres: ReadGenres(e),
            Links: ReadRelationLinks(e));
    }

    /// <summary>
    /// The artist id credited on a release group. This is cheaper than a second search. A release-group
    /// search for the album already names its artist with the id attached. That id is the one the artist
    /// lookup should use. It is matched through the record the user owns, not through a name that several
    /// bands share.
    /// </summary>
    /// <param name="releaseGroupMbid">Which group in a search answer to read the credit from. The CALLER
    /// decides, because only the caller knows which hit it accepted. A search returns the nearest match
    /// whether or not it is the record asked for, and the top-scoring group is often a tribute act's. Null
    /// takes the best-scoring group. That is correct for a lookup answer, which holds a single
    /// group.</param>
    public static string? ParseCreditedArtistMbid(string json, string? releaseGroupMbid = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Either shape: a lookup (the group itself) or a search (the named group, or its best-scoring).
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

        if (!root.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var credit in credits.EnumerateArray())
            if (credit.TryGetProperty("artist", out var artist) && Text(artist, "id") is { } id)
                return id;

        return null;
    }

    /// <summary>
    /// The genres a lookup carries, most-voted first. The list is capped, so a heavily tagged artist does
    /// not fill a pane with the long tail of one person's opinion.
    ///
    /// <para>Genres, not tags. MusicBrainz has both. Tags are free text that nobody checks. The tag list
    /// for a famous band can contain its members' names, "seen live", and worse. Genres are the curated
    /// subset. They are the only ones worth showing without reading them first.</para>
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

    /// <summary>The url-rels of a lookup, classified and ordered. Relations with no URL are dropped. So are
    /// relations that MusicBrainz has marked ended, such as a homepage that has gone. A dead link offered
    /// as a source is worse than no source.</summary>
    private static IReadOnlyList<ReferenceLink> ReadRelationLinks(JsonElement e)
    {
        if (!e.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return [];

        var links = new List<ReferenceLink>();
        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.TryGetProperty("ended", out var ended)
                && ended.ValueKind == JsonValueKind.True)
                continue;

            if (Nested(relation, "url", "resource") is not { Length: > 0 } url)
                continue;

            links.Add(new ReferenceLink(ReferenceLinks.Classify(url, Text(relation, "type")), url));
        }

        return ReferenceLinks.Merge(links);
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
}

using System.Text.Json;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// The conductor a classical record credits, as the encyclopedias have them: who they are, what they
/// did besides conducting, when they lived, what Wikipedia says, a photograph, and where else they are
/// written about.
/// </summary>
/// <param name="Entity">The Wikidata entity the rest was read from.</param>
/// <param name="Name">The name the encyclopedia files them under: "Colin Davis" for the credit "Sir Colin
/// Davis".</param>
/// <param name="Roles">What they were, in the reader's language where Wikidata has it: conductor, composer,
/// pianist. Conductor first.</param>
/// <param name="Born">The year of birth, or null.</param>
/// <param name="Died">The year of death, or null for somebody living or a date nobody wrote down.</param>
/// <param name="PhotoUrl">The article's own lead picture, or the entity's image on Commons.</param>
public sealed record ConductorProfile(
    string Entity,
    string Name,
    IReadOnlyList<string> Roles,
    string? Born,
    string? Died,
    EncyclopediaArticle? Article,
    string? PhotoUrl,
    IReadOnlyList<ReferenceLink> Links);

public sealed partial class EncyclopediaLookup
{
    /// <summary>Wikidata's "conductor", the occupation (P106) every candidate has to carry.</summary>
    private const string ConductorOccupation = "Q158852";

    /// <summary>What a credit puts in front of a conductor's name and an encyclopedia leaves off.</summary>
    private static readonly string[] Honorifics = ["Sir ", "Dame ", "Lord ", "Maestro ", "Dr. ", "Dr "];

    /// <summary>
    /// The conductor this credit names, or null where the encyclopedias have nobody by that name who
    /// conducts.
    ///
    /// <para><b>Found by name, and only where the name and the occupation both agree.</b> The rest of
    /// this class never searches an article by name, because a confident paragraph about the wrong band
    /// is worse than none. A conductor is the exception it can afford: the search is narrowed to people
    /// Wikidata files as conductors, and the answer has to be the credit's own name, one of the names the
    /// entity goes by in some language ("Sir Colin Davis" is an alias of Colin Davis). Two conductors of
    /// one name are possible; where both answer, the better-known wins, by how many Wikipedias write
    /// about them.</para>
    ///
    /// <para>A credit of two conductors ("Karajan; Böhm") is read for the first.</para>
    /// </summary>
    public async Task<Asked<ConductorProfile?>> ConductorAsync(
        string? credit, IReadOnlyList<string>? languages = null, CancellationToken ct = default)
    {
        if (ConductorName(credit) is not { } name)
            return Asked.Answer<ConductorProfile?>(null);

        var wikis = languages is { Count: > 0 } ? languages : ["en"];
        var search = $"{WikidataApi}?action=query&list=search&format=json&srlimit=5&srprop="
                   + $"&srsearch={Uri.EscapeDataString($"{name} haswbstatement:P106={ConductorOccupation}")}";
        if (!(await ReadAsync(search, SearchHits, ct)).Answers(out var hits, out var why))
            return Asked.Failed<ConductorProfile?>(why);
        if (hits.Count == 0)
            return Asked.Answer<ConductorProfile?>(null);

        var languageList = string.Join('|', wikis.Append("en").Append("mul").Distinct());
        var url = $"{WikidataApi}?action=wbgetentities&format=json&props=labels|aliases|claims|sitelinks"
                + $"&languages={Uri.EscapeDataString(languageList)}&ids={Uri.EscapeDataString(string.Join('|', hits))}";
        if (!(await ReadAsync(url, body => PickConductor(body, [name, credit!.Trim()]), ct)).Answers(out var picked, out why))
            return Asked.Failed<ConductorProfile?>(why);
        if (picked is not { } person)
            return Asked.Answer<ConductorProfile?>(null);

        // The roles' names and the article, at once. Neither depends on the other.
        var rolesTask = person.Roles.Count == 0
            ? Task.FromResult(Asked.Answer<IReadOnlyList<string>>([]))
            : ReadAsync(
                $"{WikidataApi}?action=wbgetentities&format=json&props=labels&languages={Uri.EscapeDataString(languageList)}"
                + $"&ids={Uri.EscapeDataString(string.Join('|', person.Roles))}",
                body => RoleNames(body, person.Roles, wikis), ct);
        var articleTask = ArticleAsync(person.Entity, null, wikis, ct);
        await Task.WhenAll(rolesTask, articleTask);

        if (!articleTask.Result.Answers(out var article, out why))
            return Asked.Failed<ConductorProfile?>(why);

        // A role's name that did not arrive costs the card a line, not the card.
        var roles = rolesTask.Result.Match(found => found, _ => []);

        var photo = article?.ThumbnailUrl
                    ?? (person.Image is { } file
                        ? $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file)}?width=480"
                        : null);

        return Asked.Answer<ConductorProfile?>(new ConductorProfile(
            person.Entity,
            person.Label ?? name,
            roles,
            person.Born,
            person.Died,
            article,
            photo,
            person.Links));
    }

    /// <summary>The name to search for: the credit's first conductor, without the knighthood.</summary>
    public static string? ConductorName(string? credit)
    {
        if (string.IsNullOrWhiteSpace(credit))
            return null;

        var first = credit.Split([';', '/', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .FirstOrDefault();
        if (first is not { Length: > 0 })
            return null;

        foreach (var title in Honorifics)
            if (first.StartsWith(title, StringComparison.OrdinalIgnoreCase))
                first = first[title.Length..].Trim();

        return first.Length == 0 ? null : first;
    }

    /// <summary>The entities a search answer lists, in its order.</summary>
    private static IReadOnlyList<string> SearchHits(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("query", out var query)
            || !query.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.Array)
            return [];
        return [.. search.EnumerateArray()
                         .Select(h => h.TryGetProperty("title", out var t) ? t.GetString() : null)
                         .Where(q => q is { Length: > 1 } && q[0] == 'Q')
                         .Select(q => q!)];
    }

    /// <summary>One person the search found, as far as the card needs them.</summary>
    internal sealed record ConductorEntity(
        string Entity, string? Label, IReadOnlyList<string> Roles, string? Born, string? Died, string? Image,
        IReadOnlyList<ReferenceLink> Links, int Articles);

    /// <summary>
    /// The person among the search's entities whose name is one of <paramref name="names"/>, in any
    /// language, as a label or an alias; where several are, the one with articles in the most Wikipedias.
    /// Null where nobody answers to the name.
    /// </summary>
    internal static ConductorEntity? PickConductor(string body, IReadOnlyList<string> names)
    {
        var keys = names.Where(n => n.Length > 0).Select(MetadataCleaner.ComparisonKey).ToHashSet();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return null;

        ConductorEntity? best = null;
        foreach (var entry in entities.EnumerateObject())
        {
            var e = entry.Value;
            if (!e.TryGetProperty("claims", out var claims))
                continue;

            var spellings = Spellings(e).ToList();
            if (!spellings.Any(s => keys.Contains(MetadataCleaner.ComparisonKey(s))))
                continue;

            var roles = ItemsOf(claims, "P106").ToList();
            if (!roles.Contains(ConductorOccupation))
                continue;

            // Conductor first: it is why the card is here. The rest in the order Wikidata lists them.
            roles = [ConductorOccupation, .. roles.Where(r => r != ConductorOccupation).Take(3)];

            var articles = e.TryGetProperty("sitelinks", out var sites) && sites.ValueKind == JsonValueKind.Object
                ? sites.EnumerateObject().Count(s => s.Name.EndsWith("wiki", StringComparison.Ordinal)
                                                     && s.Name is not ("commonswiki" or "specieswiki" or "metawiki"))
                : 0;

            var found = new ConductorEntity(
                entry.Name,
                Label(e),
                roles,
                YearOf(claims, "P569"),
                YearOf(claims, "P570"),
                StringsOf(claims, "P18").FirstOrDefault(),
                ConductorLinks(entry.Name, claims),
                articles);

            if (best is null || found.Articles > best.Articles)
                best = found;
        }
        return best;
    }

    /// <summary>The roles' names in the first of the reader's languages that has one, then English. A role
    /// with no name in either is left out rather than shown as its number.</summary>
    private static IReadOnlyList<string> RoleNames(string body, IReadOnlyList<string> ids, IReadOnlyList<string> languages)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("entities", out var entities))
            return [];

        var names = new List<string>();
        foreach (var id in ids)
        {
            if (!entities.TryGetProperty(id, out var e) || !e.TryGetProperty("labels", out var labels))
                continue;
            foreach (var language in languages.Append("en").Append("mul"))
                if (labels.TryGetProperty(language, out var l) && l.TryGetProperty("value", out var v)
                    && v.GetString() is { Length: > 0 } name)
                {
                    names.Add(name);
                    break;
                }
        }
        return names;
    }

    /// <summary>Where else a conductor is written about: their own site, and the music catalogues that file
    /// them. Wikidata holds each one's id, so the link is built, never searched for.</summary>
    private static IReadOnlyList<ReferenceLink> ConductorLinks(string entity, JsonElement claims)
    {
        var links = new List<ReferenceLink>();
        foreach (var site in StringsOf(claims, "P856").Take(1))
            links.Add(new ReferenceLink(ReferenceLinks.Classify(site, "official homepage"), site));
        foreach (var id in StringsOf(claims, "P1953").Take(1))
            links.Add(new ReferenceLink(ReferenceSite.Discogs, $"https://www.discogs.com/artist/{id}"));
        foreach (var id in StringsOf(claims, "P1728").Take(1))
            links.Add(new ReferenceLink(ReferenceLinks.Classify($"https://www.allmusic.com/artist/{id}"), $"https://www.allmusic.com/artist/{id}"));
        foreach (var id in StringsOf(claims, "P434").Take(1))
            links.Add(new ReferenceLink(ReferenceLinks.Classify($"https://musicbrainz.org/artist/{id}"), $"https://musicbrainz.org/artist/{id}"));
        links.Add(new ReferenceLink(ReferenceLinks.Classify($"https://www.wikidata.org/wiki/{entity}"), $"https://www.wikidata.org/wiki/{entity}"));
        return links;
    }

    private static string? Label(JsonElement e)
    {
        if (!e.TryGetProperty("labels", out var labels))
            return null;
        foreach (var language in new[] { "en", "mul" })
            if (labels.TryGetProperty(language, out var l) && l.TryGetProperty("value", out var v)
                && v.GetString() is { Length: > 0 } name)
                return name;
        foreach (var any in labels.EnumerateObject())
            if (any.Value.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } name)
                return name;
        return null;
    }

    private static IEnumerable<string> Spellings(JsonElement e)
    {
        if (e.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
            foreach (var l in labels.EnumerateObject())
                if (l.Value.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } s)
                    yield return s;
        if (e.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Object)
            foreach (var language in aliases.EnumerateObject())
                if (language.Value.ValueKind == JsonValueKind.Array)
                    foreach (var a in language.Value.EnumerateArray())
                        if (a.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } s)
                            yield return s;
    }

    private static IEnumerable<string> ItemsOf(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var statements) || statements.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var s in statements.EnumerateArray())
            if (s.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } q)
                yield return q;
    }

    private static IEnumerable<string> StringsOf(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var statements) || statements.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var s in statements.EnumerateArray())
            if (s.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
                yield return text;
    }

    /// <summary>The year of a date claim. A birth year is what a card about a person prints.</summary>
    private static string? YearOf(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var statements) || statements.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in statements.EnumerateArray())
            if (s.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("time", out var t) && t.GetString() is { Length: > 5 } stamp)
            {
                var year = stamp.TrimStart('+');
                var dash = year.IndexOf('-', 1);
                return dash > 0 ? year[..dash] : null;
            }
        return null;
    }
}

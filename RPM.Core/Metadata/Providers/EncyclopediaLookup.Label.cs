using System.Text.Json;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// The record label a release names, as the encyclopedias have it: when it was active, what it is known
/// for, its logo, what Wikipedia says, and where else it is written about.
/// </summary>
/// <param name="Entity">The Wikidata entity the rest was read from.</param>
/// <param name="Name">The name the encyclopedia files it under: "Philips Classics Records" for the tag
/// "Philips Classics".</param>
/// <param name="Founded">The year it was founded, or null.</param>
/// <param name="Ended">The year it closed, or null for a label still going or a date nobody wrote
/// down.</param>
/// <param name="Genres">What it is known for, in the reader's language where Wikidata has it.</param>
/// <param name="LogoUrl">The entity's logo on Commons, or the article's own lead picture.</param>
public sealed record LabelProfile(
    string Entity,
    string Name,
    string? Founded,
    string? Ended,
    IReadOnlyList<string> Genres,
    EncyclopediaArticle? Article,
    string? LogoUrl,
    IReadOnlyList<ReferenceLink> Links);

public sealed partial class EncyclopediaLookup
{
    /// <summary>Wikidata's "record label", what every candidate has to be an instance of (P31).</summary>
    private const string RecordLabel = "Q18127";

    /// <summary>The words a label's name trails that the tag on a record often leaves off, or adds: "Warp"
    /// and "Warp Records" are one label. Compared as words of the comparison key, so only whole words at the
    /// end go.</summary>
    private static readonly string[] LabelSuffixes =
        ["records", "recordings", "record", "ltd", "limited", "inc", "gmbh", "llc", "bv", "sa", "co"];

    /// <summary>
    /// The record label this tag names, or null where the encyclopedias have no label by that name.
    ///
    /// <para><b>Found by name, as a conductor is.</b> The search is narrowed to Wikidata's record labels,
    /// and the answer has to carry the tag's own name as a label or an alias in some language, give or take a
    /// trailing "Records". Where two labels answer, the better-known wins, by how many Wikipedias write about
    /// it.</para>
    ///
    /// <para>A tag of two labels ("Philips; Decca") is read for the first.</para>
    /// </summary>
    public async Task<Asked<LabelProfile?>> LabelAsync(
        string? tag, IReadOnlyList<string>? languages = null, CancellationToken ct = default)
    {
        if (LabelName(tag) is not { } name)
            return Asked.Answer<LabelProfile?>(null);

        var wikis = languages is { Count: > 0 } ? languages : ["en"];
        var search = $"{WikidataApi}?action=query&list=search&format=json&srlimit=5&srprop="
                   + $"&srsearch={Uri.EscapeDataString($"{name} haswbstatement:P31={RecordLabel}")}";
        if (!(await ReadAsync(search, SearchHits, ct)).Answers(out var hits, out var why))
            return Asked.Failed<LabelProfile?>(why);
        if (hits.Count == 0)
            return Asked.Answer<LabelProfile?>(null);

        var languageList = string.Join('|', wikis.Append("en").Append("mul").Distinct());
        var url = $"{WikidataApi}?action=wbgetentities&format=json&props=labels|aliases|claims|sitelinks"
                + $"&languages={Uri.EscapeDataString(languageList)}&ids={Uri.EscapeDataString(string.Join('|', hits))}";
        if (!(await ReadAsync(url, body => PickLabel(body, name), ct)).Answers(out var picked, out why))
            return Asked.Failed<LabelProfile?>(why);
        if (picked is not { } label)
            return Asked.Answer<LabelProfile?>(null);

        // The genres' names and the article, at once, as the conductor's roles are.
        var genresTask = label.Genres.Count == 0
            ? Task.FromResult(Asked.Answer<IReadOnlyList<string>>([]))
            : ReadAsync(
                $"{WikidataApi}?action=wbgetentities&format=json&props=labels&languages={Uri.EscapeDataString(languageList)}"
                + $"&ids={Uri.EscapeDataString(string.Join('|', label.Genres))}",
                body => RoleNames(body, label.Genres, wikis), ct);
        var articleTask = ArticleAsync(label.Entity, null, wikis, ct);
        await Task.WhenAll(genresTask, articleTask);

        if (!articleTask.Result.Answers(out var article, out why))
            return Asked.Failed<LabelProfile?>(why);

        // A genre's name that did not arrive costs the card a line, not the card.
        var genres = genresTask.Result.Match(found => found, _ => []);

        // The logo Wikidata files, drawn by Commons at a width the card can cut down from. The article's own
        // picture is the fallback: a label's infobox leads with its logo. The entity's plain image (P18) is
        // not used, because for a label that is as often one of its record sleeves.
        var logo = label.Logo is { } file
            ? $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file)}?width=480"
            : article?.ThumbnailUrl;

        return Asked.Answer<LabelProfile?>(new LabelProfile(
            label.Entity,
            label.Label ?? name,
            label.Founded,
            label.Ended,
            genres,
            article,
            logo,
            label.Links));
    }

    /// <summary>The name to search for: the tag's first label, trimmed.</summary>
    public static string? LabelName(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        // Split on the semicolon alone. A slash and an ampersand are inside real names: "Sony/ATV", "A&M".
        var first = tag.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .FirstOrDefault();
        return first is { Length: > 0 } ? first : null;
    }

    /// <summary>A label's name for comparing: the comparison key with the trailing company words taken
    /// off. Never empty for a name that had letters: "Records" alone is kept as it is.</summary>
    internal static string LabelKey(string name)
    {
        var words = MetadataCleaner.ComparisonKey(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && LabelSuffixes.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>One label the search found, as far as the card needs it.</summary>
    internal sealed record LabelEntity(
        string Entity, string? Label, string? Founded, string? Ended, IReadOnlyList<string> Genres, string? Logo,
        IReadOnlyList<ReferenceLink> Links, int Articles);

    /// <summary>
    /// The record label among the search's entities whose name is <paramref name="name"/>, in any language,
    /// as a label or an alias, give or take a trailing "Records"; where several are, the one with articles in
    /// the most Wikipedias. Null where no label answers to the name.
    /// </summary>
    internal static LabelEntity? PickLabel(string body, string name)
    {
        var key = LabelKey(name);
        if (key.Length == 0)
            return null;

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return null;

        LabelEntity? best = null;
        foreach (var entry in entities.EnumerateObject())
        {
            var e = entry.Value;
            if (!e.TryGetProperty("claims", out var claims))
                continue;

            if (!ItemsOf(claims, "P31").Contains(RecordLabel))
                continue;

            if (!Spellings(e).Any(s => LabelKey(s) == key))
                continue;

            var articles = e.TryGetProperty("sitelinks", out var sites) && sites.ValueKind == JsonValueKind.Object
                ? sites.EnumerateObject().Count(s => s.Name.EndsWith("wiki", StringComparison.Ordinal)
                                                     && s.Name is not ("commonswiki" or "specieswiki" or "metawiki"))
                : 0;

            var found = new LabelEntity(
                entry.Name,
                Label(e),
                YearOf(claims, "P571"),
                YearOf(claims, "P576"),
                [.. ItemsOf(claims, "P136").Take(3)],
                StringsOf(claims, "P154").FirstOrDefault(),
                LabelLinks(entry.Name, claims),
                articles);

            if (best is null || found.Articles > best.Articles)
                best = found;
        }
        return best;
    }

    /// <summary>Where else a label is written about: its own site, and the music catalogues that file it.
    /// Wikidata holds each one's id, so the link is built, never searched for.</summary>
    private static IReadOnlyList<ReferenceLink> LabelLinks(string entity, JsonElement claims)
    {
        var links = new List<ReferenceLink>();
        foreach (var site in StringsOf(claims, "P856").Take(1))
            links.Add(new ReferenceLink(ReferenceLinks.Classify(site, "official homepage"), site));
        foreach (var id in StringsOf(claims, "P1955").Take(1))
            links.Add(new ReferenceLink(ReferenceSite.Discogs, $"https://www.discogs.com/label/{id}"));
        foreach (var id in StringsOf(claims, "P966").Take(1))
            links.Add(new ReferenceLink(ReferenceLinks.Classify($"https://musicbrainz.org/label/{id}"), $"https://musicbrainz.org/label/{id}"));
        links.Add(new ReferenceLink(ReferenceLinks.Classify($"https://www.wikidata.org/wiki/{entity}"), $"https://www.wikidata.org/wiki/{entity}"));
        return links;
    }
}

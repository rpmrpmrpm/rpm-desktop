using System.Text.Json;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Reads Wikidata and Wikipedia. This is the route from "MusicBrainz says this artist is Q170572" to a
/// paragraph about them.
///
/// <para>Wikidata is in the middle on purpose. It is not an extra step that could be removed. MusicBrainz
/// does not store Wikipedia links directly. It stores the Wikidata entity, and Wikidata stores which
/// article each language's Wikipedia has for that entity. Going through it also gives the right article. An
/// article looked up by name gets a search engine's guess about which Nirvana is meant. An entity id was
/// matched by a person.</para>
/// </summary>
public static class WikipediaJson
{
    /// <summary>
    /// Which Wikipedia has an article for the entity, in the caller's order of preference, and what the
    /// article is called there. Returns the first preference that the entity has a page on.
    ///
    /// <para>Wikidata keys its sitelinks as "enwiki", "frwiki" or "zhwiki". That is the language code with
    /// "wiki" added and no separator. So "zh-Hans" must be reduced to "zh" before it can match. Sister
    /// projects ("enwikiquote", "commonswiki") are in the same dictionary but are not Wikipedia. Only an
    /// exact "{language}wiki" counts.</para>
    /// </summary>
    /// <param name="languages">Best first, for example ["fr", "en"] for a French reader. Codes may include
    /// a region ("zh-Hans"). The region is dropped.</param>
    public static (string Language, string Title)? ParseSitelink(string json, IReadOnlyList<string> languages)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entities", out var entities)
            || entities.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var entity in entities.EnumerateObject())
        {
            if (!entity.Value.TryGetProperty("sitelinks", out var sitelinks)
                || sitelinks.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var language in languages)
            {
                var code = BaseLanguage(language);
                if (code.Length == 0)
                    continue;

                if (sitelinks.TryGetProperty(code + "wiki", out var link)
                    && link.TryGetProperty("title", out var title)
                    && title.GetString() is { Length: > 0 } name)
                    return (code, name);
            }
        }

        return null;
    }

    /// <summary>Reduces a language code to its base part. "zh-Hans" becomes "zh", and "en" stays "en". The
    /// result is lower case, because Wikidata's keys are lower case.</summary>
    public static string BaseLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "";
        var code = language.Trim();
        var dash = code.IndexOfAny(['-', '_']);
        return (dash > 0 ? code[..dash] : code).ToLowerInvariant();
    }

    /// <summary>
    /// The lead section of an article, as the MediaWiki action API returns it with
    /// <c>prop=extracts&amp;exintro&amp;explaintext</c>.
    ///
    /// <para>Null for a page that does not exist. The API answers those with a negative page id and a
    /// "missing" marker, not with an error. Also null for a disambiguation page. That is a list of other
    /// articles, and it reads as nonsense under an artist's name.</para>
    /// </summary>
    public static EncyclopediaArticle? ParseExtract(string json, string language)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("query", out var query)
            || !query.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var page in pages.EnumerateObject())
        {
            var value = page.Value;
            if (value.TryGetProperty("missing", out _))
                continue;

            if (value.TryGetProperty("extract", out var extract)
                && extract.GetString() is { Length: > 0 } text
                && !string.IsNullOrWhiteSpace(text))
            {
                if (IsDisambiguation(value))
                    continue;

                var title = value.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var url = value.TryGetProperty("fullurl", out var u) && u.GetString() is { Length: > 0 } fullUrl
                    ? fullUrl
                    : $"https://{language}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}";

                var thumbnail = value.TryGetProperty("thumbnail", out var thumb)
                    && thumb.TryGetProperty("source", out var source)
                        ? source.GetString() : null;

                return new EncyclopediaArticle(language, title, Tidy(text), url, thumbnail);
            }
        }

        return null;
    }

    /// <summary>A disambiguation page has the "disambiguation" page property. It is checked by that
    /// property, not by reading the text, because the text would need a different rule in every
    /// language.</summary>
    private static bool IsDisambiguation(JsonElement page) =>
        page.TryGetProperty("pageprops", out var props)
        && props.ValueKind == JsonValueKind.Object
        && props.TryGetProperty("disambiguation", out _);

    /// <summary>
    /// The extract as prose. The API separates paragraphs with a single newline and keeps its own section
    /// headings. The pane shows paragraphs, so they are separated with a blank line. Stray whitespace at
    /// the start and end of each one is removed.
    /// </summary>
    private static string Tidy(string extract)
    {
        var paragraphs = extract
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return string.Join("\n\n", paragraphs);
    }
}

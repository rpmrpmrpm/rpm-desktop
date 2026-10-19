using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPM.Core.Radio;

/// <summary>
/// One short line saying what a station IS. It is taken from what the station publishes itself, and
/// from the encyclopedia.
///
/// <para><b>The station's own page comes first.</b> A broadcaster's front page has a <c>meta
/// description</c> written by the broadcaster, to describe the broadcaster. It is about a hundred and
/// fifty characters long, because that is what a search result shows. It is the station's own words and
/// already the right length. The page is one this app fetches anyway while it looks for the logo.
/// Nothing else is a better answer to "what is this station".</para>
///
/// <para><b>Wikipedia comes second, and only where the article is plainly about a radio station.</b>
/// The article is found by NAME. Other parts of this app refuse to look up an encyclopedia by name. See
/// <c>EncyclopediaLookup.ArticleAsync</c>, which turns down a Wikipedia search by name, because a
/// confident paragraph about the wrong band is worse than none. The same objection applies here, and it
/// is answered differently. A station has no MusicBrainz entity to go through, so the name is all there
/// is. <see cref="FromWikipedia"/> refuses anything that does not read as a radio station. The cost is
/// that a station whose article is written in a language with no word like "radio" gets no line. The
/// benefit is that the card never describes a bus route.</para>
/// </summary>
public static partial class StationBlurb
{
    /// <summary>
    /// How long a line may be before it is cut.
    /// </summary>
    /// <remarks>
    /// <b>This number and the width of the box on the card are set against each other.</b> Changing one
    /// without the other breaks the promise the pair make, which is that a description is shown whole.
    /// The box is 300 points across and wraps to two lines, which is about eighty characters at that
    /// size. So eighty is what fits. If the box is widened, this can rise. If it is narrowed, this must
    /// fall, or the text starts being trimmed with an ellipsis. Avoiding that trimming is why the wrap
    /// exists.
    ///
    /// <para>The box is 300 points wide, not wider, so that the two lines come out balanced and the
    /// second line does not hold a single word on its own.</para>
    ///
    /// <para>A meta description runs to about 155 characters, because that is what a search result
    /// shows. Most of them are one sentence, well inside this limit.</para>
    /// </remarks>
    public const int Longest = 80;

    /// <summary>
    /// What the station's own front page says it is: <c>og:description</c>, then <c>meta
    /// name="description"</c>.
    /// </summary>
    /// <remarks>
    /// The Open Graph description comes first. It is written for a person who reads a shared link. The
    /// plain description is written for a search engine, and it is more often stuffed with the
    /// station's frequencies. Both are the broadcaster's own words.
    ///
    /// <para>Null where the page declares neither. That is common enough to be ordinary. Many front
    /// pages are an app shell with nothing in the head but a title.</para>
    /// </remarks>
    public static string? FromPage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        // The head of a page holds these tags. A front page can run to a megabyte of script, so only
        // the start of the page is read. StationIcons reads the same stretch, for the same reason.
        var head = html.Length > HeadRead ? html[..HeadRead] : html;

        string? social = null;
        string? plain = null;

        foreach (Match tag in MetaTag().Matches(head))
        {
            var attributes = Attributes(tag.Value);
            var what = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name");
            if (what is null || !attributes.TryGetValue("content", out var content))
                continue;

            if (social is null && what.Equals("og:description", StringComparison.OrdinalIgnoreCase))
                social = content;
            else if (plain is null && what.Equals("description", StringComparison.OrdinalIgnoreCase))
                plain = content;
        }

        return Shorten(social) ?? Shorten(plain);
    }

    /// <summary>
    /// What Wikipedia's summary says, where the summary is about a radio station.
    /// </summary>
    /// <param name="json">A REST <c>page/summary</c> body.</param>
    /// <remarks>
    /// Wikidata's one-line description is preferred over the article's opening. For example, "Dutch
    /// public radio station" comes before two sentences about when it was founded. Wikidata's line is
    /// written to be a single line, which is what this slot needs. The article's opening is prose, and
    /// it is cut.
    ///
    /// <para>There are two refusals, and both matter. A summary whose <c>type</c> is not
    /// <c>standard</c> is a disambiguation page, or a redirect to one. That is what a station named
    /// after an ordinary word lands on. A summary that nowhere reads as a radio station is the wrong
    /// article. Looked up by name, "Star" and "Kiss" find things that are not stations at all.</para>
    /// </remarks>
    public static string? FromWikipedia(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            // Anything but a plain article: a disambiguation page, or a "no-extract" stub.
            if (root.TryGetProperty("type", out var type)
                && type.GetString() is { Length: > 0 } kind
                && !kind.Equals("standard", StringComparison.OrdinalIgnoreCase))
                return null;

            var described = Text(root, "description");
            var extract = Text(root, "extract");

            if (!IsARadioStation(described) && !IsARadioStation(extract))
                return null;

            // The article's opening is PROSE, and a slot with one line wants one sentence. The second
            // sentence is nearly always the year the station was founded, so only the first sentence is
            // kept.
            return Shorten(described) ?? Shorten(FirstSentence(extract));
        }
        catch (JsonException)
        {
            return null;   // not JSON, or not the shape this reads. The card simply has less to show
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// Whether a piece of writing reads as being about a radio station.
    /// </summary>
    /// <remarks>
    /// The word is a loanword in nearly every language. "radio station", "station de radio", "emisora
    /// de radio", "Radiosender", "radiozender" and "stazione radio" all contain it. This is
    /// deliberately not a list of translations. A list has to be maintained, and every entry it lacks
    /// is a station this refuses, rather than a station it describes wrongly. A refusal is the safer
    /// way to be wrong.
    ///
    /// <para>A language that writes the word differently gets no line. That cost is stated here. It is
    /// small next to a card confidently describing something that is not a radio station at all.</para>
    /// </remarks>
    public static bool IsARadioStation(string? text)
        => text is { Length: > 0 } && text.Contains("radio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One line, fit to print: entities decoded, whitespace squashed, and cut to <see cref="Longest"/>.
    /// </summary>
    /// <remarks>
    /// Cut at a sentence end where there is one inside the limit. A description that is two sentences
    /// long is nearly always one sentence saying what the station is, and a second selling it. If there
    /// is no sentence end, cut at a word and add an ellipsis. Never cut mid-word. A line that ends "the
    /// Netherla…" looks like the app broke the text, not like a shortened line.
    ///
    /// <para>Returns null for nothing, for whitespace, and for anything too short to be a description.
    /// A <c>meta description</c> of "Home" or "NPO" means the page did not write one.</para>
    /// </remarks>
    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var line = Whitespace().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        if (line.Length < Shortest)
            return null;

        if (line.Length <= Longest)
            return line;

        // A sentence end inside the limit, and not one so early that the line is a fragment.
        var sentence = LastSentenceEnd(line, Longest);
        if (sentence >= Shortest)
            return line[..sentence].TrimEnd();

        var word = line.LastIndexOf(' ', Longest - 1);
        return (word >= Shortest ? line[..word].TrimEnd() : line[..(Longest - 1)].TrimEnd()) + "…";
    }

    /// <summary>The first sentence of a piece of prose. If the text is one sentence or none, the whole
    /// text is returned. It uses the same rule about what counts as a stop as
    /// <see cref="LastSentenceEnd"/>, so "npo.nl" and "Radio 2.5" do not end a sentence.</summary>
    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        for (var i = 0; i < text.Length; i++)
            if (text[i] is '.' or '!' or '?' && (i + 1 >= text.Length || text[i + 1] == ' '))
                return text[..(i + 1)];

        return text;
    }

    /// <summary>Where the last sentence inside <paramref name="within"/> ends, counting the stop, or -1
    /// if there is none. A stop must be followed by a space or be the end of the text. So "Radio 2.5"
    /// and "npo.nl" are not sentence ends.</summary>
    private static int LastSentenceEnd(string line, int within)
    {
        var last = -1;
        var limit = Math.Min(within, line.Length);

        for (var i = 0; i < limit; i++)
            if (line[i] is '.' or '!' or '?' && (i + 1 >= line.Length || line[i + 1] == ' '))
                last = i + 1;

        return last;
    }

    /// <summary>The shortest text that counts as a description. Shorter text, such as "Home", "NPO" or
    /// "Live", is the head of a page that declares a description without writing one.</summary>
    private const int Shortest = 16;

    /// <summary>How much of a page is read when looking for its head. This is the same figure that
    /// <c>StationIcons.HeadRead</c> uses, for the same reason. It is written out again here, not
    /// shared, because the two read the same page for different tags, and either may change on its
    /// own.</summary>
    private const int HeadRead = 200_000;

    /// <summary>One tag's attributes, with the values unquoted and the names folded for case. This is
    /// the same reader that <c>StationIcons</c> has. It is kept beside its own use instead of shared.
    /// It is only six lines, and a shared type that exists only to be shared adds little.</summary>
    private static Dictionary<string, string> Attributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match pair in Attribute().Matches(tag))
        {
            var name = pair.Groups["name"].Value;
            var value = pair.Groups["quoted"].Success ? pair.Groups["quoted"].Value
                      : pair.Groups["single"].Success ? pair.Groups["single"].Value
                      : pair.Groups["bare"].Value;

            attributes.TryAdd(name, value);
        }

        return attributes;
    }

    /// <summary>
    /// The picture the article leads with, where the article is about a radio station.
    ///
    /// <para><b>It is read from the SAME body that <see cref="FromWikipedia"/> reads.</b> So a
    /// station's line and its mark cost one request between them. The checks are not repeated here,
    /// because the caller has already asked whether the article is about a radio station. An article
    /// that is not gets neither answer.</para>
    ///
    /// <para><b>What comes back is a lead image, not a logo.</b> That difference is the main caveat. In
    /// a sample of eighteen stations, nine articles had a picture, and only two of those were the
    /// station's mark. The rest were the logo printed on a solid box, which is what the France Inter
    /// and BBC World Service images are. Others were a photograph of the building (Deutschlandfunk) or
    /// of a studio (Radio 538). So this is offered as ONE more candidate, and never as the answer.
    /// Whatever uses it must put it through the same test as every other candidate, and most of these
    /// will fail that test. That is the intended result, not a shortfall.</para>
    /// </summary>
    public static string? LogoFromWikipedia(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("type", out var type)
                && type.GetString() is { Length: > 0 } kind
                && !kind.Equals("standard", StringComparison.OrdinalIgnoreCase))
                return null;

            if (!IsARadioStation(Text(root, "description")) && !IsARadioStation(Text(root, "extract")))
                return null;

            // The original is tried before the thumbnail. It is the same file either way, and the
            // address carries the size. RadioLogo.Sized asks for a size that Wikimedia will render.
            return Picture(root, "originalimage") ?? Picture(root, "thumbnail");
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Text(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static string? Picture(JsonElement root, string name)
            => root.TryGetProperty(name, out var picture)
               && picture.ValueKind == JsonValueKind.Object
               && Text(picture, "source") is { Length: > 0 } source
                ? source
                : null;
    }

    /// <summary>The address of Wikipedia's one-paragraph summary for a title, in a language.</summary>
    public static string SummaryUrl(string language, string title)
        => $"https://{language}.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title)}";

    [GeneratedRegex("""<meta\s[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    [GeneratedRegex("""(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<quoted>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'>]+))""")]
    private static partial Regex Attribute();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

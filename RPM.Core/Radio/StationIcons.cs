using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPM.Core.Radio;

/// <summary>What kind of picture a page offered, which decides how far it is trusted. Declaration order
/// is preference order: <see cref="StationIcons.Best"/> sorts by it.</summary>
public enum StationIconKind
{
    /// <summary>An icon listed in the site's web app manifest. The manifest declares icons for a home
    /// screen at every size the site has. So the icon is square, it is the mark, and it is the largest
    /// copy the broadcaster publishes. Sizes of 512 and 1024 are ordinary here, where an apple-touch
    /// icon stops at 180. It is also the copy that still has its alpha (transparency). Nothing requires
    /// a manifest icon to be opaque, and the platform that reads it does the compositing.</summary>
    Manifest,

    /// <summary>An <c>apple-touch-icon</c>. It is declared for a phone's home screen, so it is square,
    /// it is the mark rather than a photograph, and it is big enough to be worth having.
    ///
    /// <para>It ranks below <see cref="Manifest"/> for two reasons. Both were found by reading the
    /// sites, not assumed. iOS places one of these on an opaque tile, so sites flatten them. Of the top
    /// stations' sites that were read, the apple-touch icon is the copy WITHOUT alpha where the
    /// manifest's copy has it. Also, it is capped at 180 by convention, which is short of the size the
    /// now-playing square draws.</para></summary>
    AppleTouch,

    /// <summary>A logo that the page names as one, in so many words. This is schema.org's <c>"logo"</c>
    /// in a JSON-LD block, or an <c>og:logo</c>. It is the broadcaster's own mark, not an icon cut from
    /// it. So it nearly always has alpha, and it is often a drawing.
    ///
    /// <para>It ranks below the two icon kinds because it is often a WORDMARK, which is wider than it
    /// is tall. Those two kinds are square by their own definitions. A wide mark still draws, because
    /// it is fitted whole rather than filled. It ranks above the rest because it is the only source
    /// here that names the logo.</para></summary>
    Logo,

    /// <summary>An <c>msapplication-TileImage</c>. It is a transparent PNG at 144 or larger. It ranks
    /// this low because the transparency is there for the tile it was drawn for. That tile has a colour
    /// declared beside it, so the mark is often white and nothing else. On a dark background that reads
    /// well. So it is not used while a coloured copy is still on the list.</summary>
    Tile,

    /// <summary>A plain <c>rel="icon"</c>. Usually the same mark, often smaller.</summary>
    Icon,

    /// <summary>An <c>og:image</c>: the picture the site wants shown when it is shared. It is found on
    /// most pages, and it is the least trustworthy source. For example, Radio 538's is a 1200×630
    /// photograph of its presenters. So whatever uses it must check the shape of the picture that
    /// arrives. See <see cref="LooksLikeAMark"/>.</summary>
    Social,
}

/// <param name="Url">Absolute, resolved against the page it was declared on.</param>
/// <param name="Size">The largest edge the page claimed, or 0 where it claimed none. Only ever a claim
/// Nothing here has seen the picture.</param>
public readonly record struct StationIcon(string Url, StationIconKind Kind, int Size = 0);

/// <summary>
/// The pictures that a station's own homepage declares, for the stations the directory has no logo for.
///
/// <para><b>Why this is needed at all.</b> Of 400 Dutch stations the directory lists, 232 give a logo
/// URL and 164 give none. But 389 give a HOMEPAGE. The logos are not missing from the internet. They
/// are missing from one field. The homepage is the page every station does publish, so it is the place
/// to find them. For example, NPO 3FM declares a 180×180 apple-touch-icon on its front page, and
/// nothing in the directory mentions it.</para>
///
/// <para>This is a reader of one small part of a page, not an HTML parser. What is wanted is the
/// handful of <c>link</c> and <c>meta</c> tags in a document's head. Each of them is a single tag with
/// attributes. Anything this misreads shows up as one candidate that does not load, and the row already
/// survives that.</para>
/// </summary>
public static partial class StationIcons
{
    /// <summary>
    /// The icons that <paramref name="html"/> declares, best first. See <see cref="Best"/> for the
    /// order, and <see cref="StationIconKind"/> for why it is that order.
    ///
    /// <para>Everything here is read off the page itself. The site's web app manifest is a SECOND file,
    /// and the largest mark most broadcasters publish is in that file. So a caller that can make
    /// another request should follow this with <see cref="ManifestAt"/> and
    /// <see cref="FromManifest"/>.</para>
    /// </summary>
    /// <param name="pageUrl">Where the page was fetched from. Relative hrefs are resolved against it.
    /// Nothing is returned without it, because a relative href with no page to resolve it against is
    /// not an address.</param>
    public static IReadOnlyList<StationIcon> From(string? html, string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
            return [];

        // The head is where these live, and a station's front page can be a megabyte of script. Reading
        // the first stretch of it is the whole job.
        var head = html.Length > HeadRead ? html[..HeadRead] : html;

        var found = new List<StationIcon>(4);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match tag in LinkTag().Matches(head))
        {
            var attributes = Attributes(tag.Value);
            if (!attributes.TryGetValue("href", out var href))
                continue;

            var rel = attributes.GetValueOrDefault("rel", "").ToLowerInvariant();
            var kind = rel.Contains("apple-touch-icon", StringComparison.Ordinal) ? StationIconKind.AppleTouch
                     : rel.Split(' ').Contains("icon") ? StationIconKind.Icon
                     : (StationIconKind?)null;

            if (kind is { } which && Resolve(href, page) is { } url && seen.Add(url))
                found.Add(new StationIcon(url, which, Largest(attributes.GetValueOrDefault("sizes"))));
        }

        foreach (Match tag in MetaTag().Matches(head))
        {
            var attributes = Attributes(tag.Value);
            var what = (attributes.GetValueOrDefault("property")
                        ?? attributes.GetValueOrDefault("name")
                        ?? "").ToLowerInvariant();

            var kind = what switch
            {
                "og:image" => StationIconKind.Social,
                "og:logo" => StationIconKind.Logo,
                "msapplication-tileimage" => StationIconKind.Tile,
                _ => (StationIconKind?)null,
            };

            if (kind is { } which
                && attributes.TryGetValue("content", out var content)
                && Resolve(content, page) is { } url
                && seen.Add(url))
                found.Add(new StationIcon(url, which));
        }

        // The logo the page names as such. It is kept apart from the tags above, because it is not a
        // tag. It is a field inside a JSON document that the page carries for search engines. It is the
        // only place on a site where a picture is labelled "our logo" rather than "our icon". Of 65 top
        // stations' sites, 26 publish one. Of the 30 that load, 22 carry alpha and 5 are drawings. That
        // is the best ratio of any source here.
        foreach (var url in LinkedData(head, page))
            if (seen.Add(url))
                found.Add(new StationIcon(url, StationIconKind.Logo));

        return Best(found);
    }

    /// <summary>
    /// Icons in the order they are worth trying: by kind first, then by the size the page claimed.
    ///
    /// <para>This is shared, so that a caller which adds the manifest's icons beside the page's icons
    /// can re-order the whole list, rather than appending one list behind the other. <see cref="From"/>
    /// returns its own answer through this, and sorting an ordered list again changes nothing.</para>
    ///
    /// <para>Kind comes before size, and this is the important half. A 512-pixel share card is a
    /// photograph of the presenters, and a 180-pixel apple-touch icon is the station's mark. Sorting on
    /// the number alone would put the photograph first. Within one kind the number decides, because a
    /// kind says what the picture IS.</para>
    ///
    /// <para>The sort is stable. Two icons that the page says nothing about keep the order in which the
    /// page declared them.</para>
    /// </summary>
    public static IReadOnlyList<StationIcon> Best(IEnumerable<StationIcon> icons)
        => icons
            .OrderBy(i => (int)i.Kind)
            .ThenByDescending(i => i.Size)
            .ToList();

    /// <summary>
    /// The address of the page's web app manifest, or null if the page declares none.
    ///
    /// <para>This is split from <see cref="FromManifest"/>, because the manifest needs a second FETCH.
    /// Nothing in this file fetches anything. The caller asks for the address, gets the file, and
    /// passes the text back.</para>
    /// </summary>
    public static string? ManifestAt(string? html, string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page))
            return null;

        var head = html.Length > HeadRead ? html[..HeadRead] : html;

        foreach (Match tag in LinkTag().Matches(head))
        {
            var attributes = Attributes(tag.Value);
            if (attributes.GetValueOrDefault("rel", "").Split(' ')
                    .Contains("manifest", StringComparer.OrdinalIgnoreCase)
                && attributes.TryGetValue("href", out var href)
                && Resolve(href, page) is { } url)
                return url;
        }

        return null;
    }

    /// <summary>
    /// The icons that a web app manifest lists, resolved against the address the manifest was fetched
    /// from.
    ///
    /// <para><b>Why this is worth a second request.</b> The big transparent copy of a station's mark is
    /// often in the manifest. Of 65 of the directory's most-played stations, 23 publish a manifest. Of
    /// those 23, 11 list a bigger logo than anything the page declares. 9 of the 11 go from below the
    /// now-playing square's 512 pixels to above it. Read from the page alone, 8 of the 65 reach that
    /// square at full size. Read with the manifest, 13 do. NPO Radio 1 is the plain case. Its
    /// apple-touch icon is 180 pixels with no alpha. Its manifest lists the same mark at 256 pixels,
    /// with the alpha intact.</para>
    ///
    /// <para>The second request is cheap. The page has already been fetched by the time this is asked
    /// for. The manifest is one more small file from a host that is already connected. It is cached per
    /// host, beside the page, rather than per station.</para>
    /// </summary>
    /// <param name="manifestUrl">The address from <see cref="ManifestAt"/>. Icon <c>src</c> values
    /// resolve against the MANIFEST, not the page. The spec says so. A site that keeps its manifest in
    /// a subdirectory writes relative paths that mean nothing from the page's address.</param>
    public static IReadOnlyList<StationIcon> FromManifest(string? json, string? manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(json)
            || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var at))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("icons", out var icons)
                || icons.ValueKind != JsonValueKind.Array)
                return [];

            var found = new List<StationIcon>(4);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != JsonValueKind.Object
                    || Text(icon, "src") is not { Length: > 0 } src
                    || Resolve(src, at) is not { } url
                    || !seen.Add(url))
                    continue;

                var purpose = Text(icon, "purpose")?.ToLowerInvariant().Split(' ') ?? [];

                // A monochrome icon is a silhouette that the platform tints. It carries the shape of
                // the mark, but none of its colour. It is dropped rather than ranked. Used as a logo,
                // it is a flat blob, which looks less like a picture than the antenna does.
                if (purpose.Contains("monochrome") && !purpose.Contains("any"))
                    continue;

                var size = Largest(Text(icon, "sizes"));

                // A maskable icon is the mark inside a safe zone. The platform crops it to whatever
                // shape it likes, so about a fifth of each edge is deliberate empty space. Used whole,
                // it is a small mark in a large square. So its worth is its declared size less that
                // padding, and that is what it is ordered by. It is still kept. A maskable 512 beats a
                // plain 192, and either way it is a real picture.
                if (purpose.Contains("maskable") && !purpose.Contains("any") && size != int.MaxValue)
                    size = size * MaskSafeZone / 100;

                found.Add(new StationIcon(url, StationIconKind.Manifest, size));
            }

            return Best(found);
        }
        catch (JsonException)
        {
            return [];   // a manifest that is not JSON is a manifest with no icons in it
        }
    }

    /// <summary>How much of a maskable icon's edge is mark rather than the safe zone around it. The
    /// least the spec guarantees is a circle of 80% diameter. The value 80 is that, read as a
    /// percentage. It is used only to ORDER these against icons that are all mark.</summary>
    private const int MaskSafeZone = 80;

    /// <summary>One string field of a JSON object, or null where it is absent or is not a string.
    /// <c>sizes</c> and <c>purpose</c> are both optional and both turn up written as something
    /// else.</summary>
    private static string? Text(JsonElement node, string name)
        => node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Every <c>"logo"</c> that the page's JSON-LD blocks name, as an absolute address.
    ///
    /// <para>These are parsed, not matched with a pattern like the tags above. A JSON-LD block IS a
    /// document. A broadcaster's block is often an <c>@graph</c> of a dozen entities, with the
    /// organization somewhere inside it. A regular expression for <c>"logo"</c> cannot tell the nesting
    /// from the escaping. A block that does not parse is skipped. The page usually has several
    /// blocks.</para>
    ///
    /// <para>Only <c>logo</c> is taken, never <c>image</c>. An organization's <c>image</c> is as often
    /// a photograph of a building or a studio as it is a mark. Photographs come from another source
    /// here, which has to be shape-checked (see <see cref="StationIconKind.Social"/>).</para>
    /// </summary>
    private static List<string> LinkedData(string head, Uri page)
    {
        var found = new List<string>();

        foreach (Match block in LinkedDataBlock().Matches(head))
        {
            try
            {
                using var document = JsonDocument.Parse(block.Groups["json"].Value);
                Dig(document.RootElement, 0);
            }
            catch (JsonException)
            {
                // One block of several, and the page's own tags are read whether or not it parses.
            }
        }

        return found;

        void Dig(JsonElement node, int depth)
        {
            if (depth > LinkedDataDepth)
                return;

            switch (node.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        Dig(item, depth + 1);
                    break;

                case JsonValueKind.Object:
                    foreach (var field in node.EnumerateObject())
                        if (field.NameEquals("logo"))
                            Take(field.Value);
                        else
                            Dig(field.Value, depth + 1);
                    break;
            }
        }

        // A logo is written in three ways, and all three occur in the wild. It can be a URL on its own,
        // an ImageObject with the URL inside it, or a list of either.
        void Take(JsonElement logo)
        {
            switch (logo.ValueKind)
            {
                case JsonValueKind.String when Resolve(logo.GetString() ?? "", page) is { } url:
                    found.Add(url);
                    break;

                case JsonValueKind.Object when Text(logo, "url") is { Length: > 0 } inside
                                               && Resolve(inside, page) is { } url:
                    found.Add(url);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in logo.EnumerateArray())
                        Take(item);
                    break;
            }
        }
    }

    /// <summary>How far into a JSON-LD block the walk goes. An <c>@graph</c> of entities, each carrying
    /// a publisher that carries a logo, is three or four levels deep. Past this depth, a logo is not
    /// one for the station whose page this is.</summary>
    private const int LinkedDataDepth = 8;

    /// <summary>
    /// The address where many sites keep their home-screen icon, for a page that declares none.
    ///
    /// <para>This is a guess, and a cheap one. NPO 3FM serves the icon there, while Radio 538 and 100%
    /// NL answer 404. A 404 costs one request. The guess goes last, after everything the page actually
    /// said.</para>
    /// </summary>
    public static string? WellKnown(string? homepage)
        => Uri.TryCreate(homepage, UriKind.Absolute, out var page)
           && (page.Scheme == Uri.UriSchemeHttp || page.Scheme == Uri.UriSchemeHttps)
            ? new Uri(page, "/apple-touch-icon.png").ToString()
            : null;

    /// <summary>
    /// Whether a picture's proportions are those of a mark rather than a photograph. This is the test
    /// that an <see cref="StationIconKind.Social"/> image has to pass before it is used as a logo.
    ///
    /// <para>It is applied to the picture that ARRIVED, never to the size the page claimed. The claim
    /// is the part that cannot be trusted. Radio 538's social image is 1200×630. That is the shape of a
    /// share card, not of a station's badge.</para>
    /// </summary>
    public static bool LooksLikeAMark(int width, int height)
        => width > 0 && height > 0
           && (double)Math.Max(width, height) / Math.Min(width, height) <= SquareEnough;

    /// <summary>How far from square a mark may be. A little over four to three is enough for a wordmark
    /// that is wider than it is tall, and nowhere near enough for a 1200×630 share card.</summary>
    private const double SquareEnough = 1.4;

    /// <summary>How much of a page is read when looking for its head. Front pages run to a megabyte of
    /// script. The tags this needs are in the first part of every page.</summary>
    private const int HeadRead = 200_000;

    /// <summary>The largest edge in a <c>sizes</c> attribute. For example, "32x32 180x180" names two
    /// pictures, and the larger is the one worth asking for. Zero where the attribute says nothing.
    /// Everything where it says "any", which is what a scalable icon declares.</summary>
    private static int Largest(string? sizes)
    {
        if (string.IsNullOrWhiteSpace(sizes))
            return 0;

        if (sizes.Contains("any", StringComparison.OrdinalIgnoreCase))
            return int.MaxValue;

        var biggest = 0;
        foreach (Match pair in SizePair().Matches(sizes))
            if (int.TryParse(pair.Groups[1].Value, out var edge) && edge > biggest)
                biggest = edge;

        return biggest;
    }

    /// <summary>An href made absolute, or null where it is not something to fetch. A <c>data:</c> icon
    /// needs no fetching, and this code does not pass one on. Any address that resolves to a scheme
    /// other than http or https is not a picture this app should request.</summary>
    private static string? Resolve(string href, Uri page)
    {
        var trimmed = Decode(href.Trim());
        if (trimmed.Length == 0 || !Uri.TryCreate(page, trimmed, out var absolute))
            return null;

        return absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps
            ? absolute.ToString()
            : null;
    }

    /// <summary>The HTML entity that a URL in an attribute can use, and that matters here. An href with
    /// a query string is written <c>?a=1&amp;amp;b=2</c> in HTML, but it must be fetched with a plain
    /// ampersand.</summary>
    private static string Decode(string value)
        => value.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    /// <summary>One tag's attributes, with the values unquoted and the names folded for case. If an
    /// attribute appears twice, the first value is kept, as a browser does.</summary>
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

    [GeneratedRegex("""<link\s[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    [GeneratedRegex("""<meta\s[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTag();

    /// <summary>A JSON-LD block and what is inside it. The type is matched loosely because a page
    /// writes it <c>application/ld+json</c> with and without a charset after it.</summary>
    [GeneratedRegex("""<script[^>]*?type\s*=\s*["']?application/ld\+json[^>]*>(?<json>.*?)</script>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkedDataBlock();

    [GeneratedRegex("""(?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<quoted>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s"'>]+))""")]
    private static partial Regex Attribute();

    [GeneratedRegex("""(\d+)\s*[xX]\s*\d+""")]
    private static partial Regex SizePair();
}

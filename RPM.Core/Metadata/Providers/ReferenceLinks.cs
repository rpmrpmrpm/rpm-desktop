namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Turns the list of URLs a catalogue returns into a row of named links for the screen.
///
/// <para>Two decisions are made here. Both are judgements, not parsing. The first is what kind of site a
/// URL is. MusicBrainz labels its relations ("official homepage", "social network", "streaming"), but these
/// labels are too broad to name a site. A dozen services are all "streaming". So the host decides. The
/// relation type is used for one thing only: a host cannot say that a site belongs to the artist. The
/// second decision is the order. The artist's own site comes first, then the encyclopedias, then the
/// catalogues, then where you can listen, then the social accounts. A row is read left to right, so the
/// first link should be the one most people want.</para>
/// </summary>
public static class ReferenceLinks
{
    /// <summary>The MusicBrainz relation type that means "this is the artist's own site". It is the only
    /// relation label kept, because it says something the URL cannot.</summary>
    public const string OfficialHomepageRelation = "official homepage";

    /// <summary>Left to right. See the class note: own site, encyclopedias, catalogues, listening, social,
    /// and the machine-readable one (Wikidata) last of the named sites.</summary>
    private static readonly ReferenceSite[] Order =
    [
        ReferenceSite.OfficialSite,
        ReferenceSite.Wikipedia,
        ReferenceSite.Discogs,
        ReferenceSite.AllMusic,
        ReferenceSite.Bandcamp,
        ReferenceSite.Spotify,
        ReferenceSite.AppleMusic,
        ReferenceSite.YouTube,
        ReferenceSite.SoundCloud,
        ReferenceSite.LastFm,
        ReferenceSite.Genius,
        ReferenceSite.Instagram,
        ReferenceSite.Facebook,
        ReferenceSite.X,
        ReferenceSite.Tumblr,
        ReferenceSite.Bluesky,
        ReferenceSite.Wikidata,
        ReferenceSite.Other,
    ];

    /// <summary>Which site a URL is on. <paramref name="relationType"/> is the catalogue's label for the
    /// link. It is used only to recognise the artist's own homepage. A personal site can have any host its
    /// owner chose, so the URL alone cannot show that it belongs to the artist.</summary>
    public static ReferenceSite Classify(string url, string? relationType = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return ReferenceSite.Other;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];

        // Recognised hosts win over the relation label. MusicBrainz often files Bandcamp and YouTube pages
        // as "official homepage". If a Bandcamp page were called the artist's own site, a shop would appear
        // where the reader expects the band's main site.
        var site = FromHost(host);
        if (site is not ReferenceSite.Other)
            return site;

        return string.Equals(relationType, OfficialHomepageRelation, StringComparison.OrdinalIgnoreCase)
            ? ReferenceSite.OfficialSite
            : ReferenceSite.Other;
    }

    private static ReferenceSite FromHost(string host)
    {
        // Match the end of the host, not the whole host. Every Wikipedia is on a language subdomain, and
        // Bandcamp gives each artist a subdomain of their own.
        if (host.EndsWith("wikipedia.org", StringComparison.Ordinal)) return ReferenceSite.Wikipedia;
        if (host.EndsWith("wikidata.org", StringComparison.Ordinal)) return ReferenceSite.Wikidata;
        if (host.EndsWith("bandcamp.com", StringComparison.Ordinal)) return ReferenceSite.Bandcamp;

        // Tumblr and Bluesky also give each account a subdomain of its own, like Bandcamp.
        if (host.EndsWith("tumblr.com", StringComparison.Ordinal)) return ReferenceSite.Tumblr;
        if (host.EndsWith("bsky.app", StringComparison.Ordinal)
            || host.EndsWith("bsky.social", StringComparison.Ordinal)) return ReferenceSite.Bluesky;

        return host switch
        {
            "discogs.com" => ReferenceSite.Discogs,
            "allmusic.com" => ReferenceSite.AllMusic,
            "open.spotify.com" or "spotify.com" or "play.spotify.com" => ReferenceSite.Spotify,
            "music.apple.com" or "itunes.apple.com" => ReferenceSite.AppleMusic,
            "youtube.com" or "music.youtube.com" or "youtu.be" => ReferenceSite.YouTube,
            "soundcloud.com" => ReferenceSite.SoundCloud,
            "last.fm" => ReferenceSite.LastFm,
            "genius.com" => ReferenceSite.Genius,
            "instagram.com" => ReferenceSite.Instagram,
            "facebook.com" or "m.facebook.com" => ReferenceSite.Facebook,
            "twitter.com" or "x.com" => ReferenceSite.X,
            _ => ReferenceSite.Other,
        };
    }

    /// <summary>
    /// One link per site, in display order. The earlier lists win. For example, the album's own Discogs
    /// page is a better answer than its artist's page, so the album's links are passed first.
    ///
    /// <para>"Other" is the exception to one link per site. Sites with no name are not one site. They are
    /// everything left over. So all of them are kept, and duplicates are removed by URL only.</para>
    /// </summary>
    public static IReadOnlyList<ReferenceLink> Merge(params IReadOnlyList<ReferenceLink>[] lists)
    {
        var bySite = new Dictionary<ReferenceSite, ReferenceLink>();
        var others = new List<ReferenceLink>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in lists)
        {
            foreach (var link in list)
            {
                if (string.IsNullOrWhiteSpace(link.Url) || !seenUrls.Add(link.Url))
                    continue;

                if (link.Site == ReferenceSite.Other)
                    others.Add(link);
                else
                    bySite.TryAdd(link.Site, link);
            }
        }

        var ordered = new List<ReferenceLink>(bySite.Count + others.Count);
        foreach (var site in Order)
        {
            if (site == ReferenceSite.Other)
                ordered.AddRange(others);
            else if (bySite.TryGetValue(site, out var link))
                ordered.Add(link);
        }
        return ordered;
    }

    /// <summary>The Wikidata entity id ("Q170572") in a Wikidata URL, or null when it is not one. Wikidata
    /// links a MusicBrainz artist to their Wikipedia article. MusicBrainz no longer stores Wikipedia links
    /// directly. It points to Wikidata instead.</summary>
    public static string? WikidataEntityId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("wikidata.org", StringComparison.OrdinalIgnoreCase))
            return null;

        var last = uri.AbsolutePath.TrimEnd('/').Split('/')[^1];
        return last.Length > 1 && last[0] is 'Q' or 'q' && last[1..].All(char.IsAsciiDigit)
            ? "Q" + last[1..]
            : null;
    }

    /// <summary>
    /// The artist number in a Discogs artist URL ("3461453" out of
    /// <c>https://www.discogs.com/artist/3461453-Ookay</c>), or null when the URL is not one.
    ///
    /// <para>The slug after the number, here "Ookay", is decoration. Discogs accepts the bare number, and
    /// its API only ever wants that. The number is taken from the link the catalogues already gave. It is
    /// not looked up by name. A name search on Discogs returns the nearest match it has, and for an artist
    /// name the nearest match is often another artist.</para>
    /// </summary>
    public static string? DiscogsArtistId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("discogs.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindIndex(parts, p => p.Equals("artist", StringComparison.OrdinalIgnoreCase));
        if (at < 0 || at + 1 >= parts.Length)
            return null;

        var last = parts[at + 1];
        var cut = last.IndexOf('-');
        var number = cut > 0 ? last[..cut] : last;
        return number.Length > 0 && number.All(char.IsAsciiDigit) ? number : null;
    }

    /// <summary>The article title in a Wikipedia URL ("Fleetwood Mac"), and its language. This is the form
    /// that MusicBrainz's older, direct Wikipedia links are still stored in. Null when the URL is not a
    /// Wikipedia article.</summary>
    public static (string Language, string Title)? WikipediaArticle(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !uri.Host.EndsWith("wikipedia.org", StringComparison.OrdinalIgnoreCase))
            return null;

        var host = uri.Host.Split('.');
        var language = host.Length >= 3 ? host[0] : "en";

        const string wiki = "/wiki/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(wiki, StringComparison.Ordinal) || path.Length <= wiki.Length)
            return null;

        var title = Uri.UnescapeDataString(path[wiki.Length..]).Replace('_', ' ');
        return string.IsNullOrWhiteSpace(title) ? null : (language, title);
    }
}

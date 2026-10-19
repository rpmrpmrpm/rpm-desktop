using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// The row of outside pages under an artist: what each URL is, and which ones survive being merged
/// with the album's own. The classification matters because a link nobody can name is a link nobody
/// clicks, and MusicBrainz's own labels are too coarse to name one by.
/// </summary>
public class ReferenceLinksTests
{
    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Fleetwood_Mac", ReferenceSite.Wikipedia)]
    [InlineData("https://fr.wikipedia.org/wiki/Fleetwood_Mac", ReferenceSite.Wikipedia)]
    [InlineData("https://www.wikidata.org/wiki/Q170572", ReferenceSite.Wikidata)]
    [InlineData("https://www.discogs.com/artist/4664", ReferenceSite.Discogs)]
    [InlineData("https://www.allmusic.com/artist/mn0000255280", ReferenceSite.AllMusic)]
    [InlineData("https://open.spotify.com/artist/08GQAI4eElDnROBrJRGE0X", ReferenceSite.Spotify)]
    [InlineData("https://music.apple.com/us/artist/fleetwood-mac/158038", ReferenceSite.AppleMusic)]
    [InlineData("https://www.youtube.com/@fleetwoodmac", ReferenceSite.YouTube)]
    [InlineData("https://soundcloud.com/fleetwoodmac", ReferenceSite.SoundCloud)]
    [InlineData("https://www.last.fm/music/Fleetwood+Mac", ReferenceSite.LastFm)]
    [InlineData("https://twitter.com/fleetwoodmac", ReferenceSite.X)]
    [InlineData("https://x.com/fleetwoodmac", ReferenceSite.X)]
    public void A_url_is_named_by_its_host(string url, ReferenceSite expected) =>
        Assert.Equal(expected, ReferenceLinks.Classify(url));

    /// <summary>Bandcamp gives every artist a subdomain of their own, so matching the exact host would
    /// name none of them.</summary>
    [Fact]
    public void Every_bandcamp_subdomain_is_bandcamp() =>
        Assert.Equal(ReferenceSite.Bandcamp, ReferenceLinks.Classify("https://fleetwoodmac.bandcamp.com/"));

    /// <summary>A personal site has whatever host its owner bought, so nothing in the URL identifies
    /// it. The catalogue's relation label is the only thing that can, and it is used for this case
    /// and nothing else.</summary>
    [Fact]
    public void An_unrecognised_host_is_the_artists_own_site_when_the_catalogue_says_so()
    {
        Assert.Equal(ReferenceSite.OfficialSite,
            ReferenceLinks.Classify("https://fleetwoodmac.com/", ReferenceLinks.OfficialHomepageRelation));
        Assert.Equal(ReferenceSite.Other, ReferenceLinks.Classify("https://fleetwoodmac.com/"));
    }

    /// <summary>A common case: MusicBrainz files a fair number of Bandcamp and YouTube pages under
    /// "official homepage". Trusting the label there would put a shop where the reader expects the
    /// band's own site, so a host we DO recognise wins over the label.</summary>
    [Fact]
    public void A_recognised_host_wins_over_the_official_homepage_label() =>
        Assert.Equal(ReferenceSite.Bandcamp,
            ReferenceLinks.Classify("https://fleetwoodmac.bandcamp.com/", ReferenceLinks.OfficialHomepageRelation));

    [Fact]
    public void A_url_that_is_not_a_url_at_all_is_simply_other() =>
        Assert.Equal(ReferenceSite.Other, ReferenceLinks.Classify("not a url"));

    // ── merging the album's links with its artist's ──

    private static ReferenceLink Link(ReferenceSite site, string url) => new(site, url);

    /// <summary>Own site, encyclopedias, catalogues, listening, social: a row is read from left to
    /// right, and the first entry should be the one most people want.</summary>
    [Fact]
    public void The_row_is_ordered_by_what_a_reader_wants_first()
    {
        var merged = ReferenceLinks.Merge([
            Link(ReferenceSite.Instagram, "https://instagram.com/a"),
            Link(ReferenceSite.Wikidata, "https://www.wikidata.org/wiki/Q1"),
            Link(ReferenceSite.Discogs, "https://www.discogs.com/artist/1"),
            Link(ReferenceSite.OfficialSite, "https://a.example"),
            Link(ReferenceSite.Wikipedia, "https://en.wikipedia.org/wiki/A"),
        ]);

        Assert.Equal(
            [ReferenceSite.OfficialSite, ReferenceSite.Wikipedia, ReferenceSite.Discogs,
             ReferenceSite.Instagram, ReferenceSite.Wikidata],
            merged.Select(l => l.Site));
    }

    /// <summary>The album's Discogs page is a better answer than its artist's, so the album's list is
    /// passed first.</summary>
    [Fact]
    public void The_first_list_wins_a_site_the_second_also_has()
    {
        var merged = ReferenceLinks.Merge(
            [Link(ReferenceSite.Discogs, "https://www.discogs.com/release/1")],
            [Link(ReferenceSite.Discogs, "https://www.discogs.com/artist/1")]);

        Assert.Equal("https://www.discogs.com/release/1", Assert.Single(merged).Url);
    }

    /// <summary>MusicBrainz returns the same URL for both the album and the artist often enough to
    /// matter, and two links to one page are one link too many.</summary>
    [Fact]
    public void The_same_url_twice_is_kept_once()
    {
        var merged = ReferenceLinks.Merge(
            [Link(ReferenceSite.Wikipedia, "https://en.wikipedia.org/wiki/A")],
            [Link(ReferenceSite.Wikipedia, "https://en.wikipedia.org/wiki/A")]);

        Assert.Single(merged);
    }

    /// <summary>"Other" is not one site: it is everything left over, so one-per-site would discard
    /// links that have nothing to do with each other.</summary>
    [Fact]
    public void Everything_unnamed_is_kept_rather_than_collapsed()
    {
        var merged = ReferenceLinks.Merge([
            Link(ReferenceSite.Other, "https://one.example"),
            Link(ReferenceSite.Other, "https://two.example"),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_link_with_no_url_is_dropped() =>
        Assert.Empty(ReferenceLinks.Merge([Link(ReferenceSite.Wikipedia, "  ")]));

    // ── the two URLs that are read rather than shown ──

    [Theory]
    [InlineData("https://www.wikidata.org/wiki/Q170572", "Q170572")]
    [InlineData("https://wikidata.org/wiki/Q1/", "Q1")]
    [InlineData("https://en.wikipedia.org/wiki/Q170572", null)]
    [InlineData("https://www.wikidata.org/wiki/Property:P434", null)]
    public void A_wikidata_entity_is_read_out_of_its_url(string url, string? expected) =>
        Assert.Equal(expected, ReferenceLinks.WikidataEntityId(url));

    /// <summary>The older, direct form that MusicBrainz still has on some artists: the article's
    /// language is in the subdomain and its title is the path, underscores and all.</summary>
    [Fact]
    public void A_wikipedia_url_gives_up_its_language_and_title()
    {
        Assert.Equal(("fr", "Fleetwood Mac"),
            ReferenceLinks.WikipediaArticle("https://fr.wikipedia.org/wiki/Fleetwood_Mac"));

        Assert.Equal(("de", "Sgt. Pepper's Lonely Hearts Club Band"),
            ReferenceLinks.WikipediaArticle(
                "https://de.wikipedia.org/wiki/Sgt.%20Pepper's%20Lonely%20Hearts%20Club%20Band"));
    }

    [Theory]
    [InlineData("https://en.wikipedia.org/")]
    [InlineData("https://www.discogs.com/artist/4664")]
    public void Anything_that_is_not_an_article_gives_up_nothing(string url) =>
        Assert.Null(ReferenceLinks.WikipediaArticle(url));
}

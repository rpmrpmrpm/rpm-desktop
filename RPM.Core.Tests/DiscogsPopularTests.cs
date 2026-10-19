using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// Ordering an artist's records by how many people keep a copy, out of Discogs' artist-releases page.
///
/// <para>Three rules are tested, and each one decides whether the card is useful or misleading: only
/// the artist's OWN work counts, there is one entry per album and not one per pressing, and the order
/// is the collection count, computed here because Discogs cannot sort by it.</para>
/// </summary>
public class DiscogsPopularTests
{
    private static string Page(params string[] releases)
        => "{\"pagination\":{\"pages\":1},\"releases\":[" + string.Join(",", releases) + "]}";

    private static string Release(
        long id, string title, string role = "Main", string type = "master",
        int year = 1976, int have = 0, int want = 0)
        => "{" +
           $"\"id\":{id},\"title\":\"{title}\",\"role\":\"{role}\",\"type\":\"{type}\",\"year\":{year}," +
           $"\"stats\":{{\"community\":{{\"in_collection\":{have},\"in_wantlist\":{want}}}}}" +
           "}";

    [Fact]
    public void The_most_owned_record_comes_first()
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(1, "Waterloo", have: 4_000),
            Release(2, "Arrival", have: 21_000),
            Release(3, "Super Trouper", have: 12_000)));

        Assert.Equal(["Arrival", "Super Trouper", "Waterloo"], popular.Select(r => r.Title));
    }

    /// <summary>Two records nobody has are still two records, and the one more people want is the more
    /// notable of them. Without the tie-break the order would be the page's own order, which for this
    /// endpoint is by year.</summary>
    [Fact]
    public void Wantlists_break_a_tie_on_collections()
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(1, "Ordinary", have: 50, want: 3),
            Release(2, "Sought after", have: 50, want: 900)));

        Assert.Equal("Sought after", popular[0].Title);
    }

    /// <summary>The endpoint returns every credit an artist has. A compilation with one track by them
    /// is not one of their records, and a card that ranked those would be about other people's
    /// albums.</summary>
    [Theory]
    [InlineData("Appearance")]
    [InlineData("TrackAppearance")]
    [InlineData("Remix")]
    [InlineData("Producer")]
    public void Only_their_own_work_is_ranked(string role)
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(1, "Theirs", have: 10),
            Release(2, "Somebody else's", role: role, have: 90_000)));

        Assert.Equal(["Theirs"], popular.Select(r => r.Title));
    }

    /// <summary>A famous album has forty pressings and Discogs lists all of them under the master that
    /// groups them. Without a filter, a card about a famous artist shows one album forty times.</summary>
    [Fact]
    public void One_entry_per_album_not_per_pressing()
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(10, "Arrival", type: "master", have: 21_000),
            Release(11, "Arrival", type: "release", have: 900),
            Release(12, "Arrival", type: "release", have: 400)));

        Assert.Single(popular);
        Assert.Equal(21_000, popular[0].Have);
    }

    /// <summary>But an artist nobody has grouped yet has no masters at all, and dropping the loose
    /// releases there would empty the card.</summary>
    [Fact]
    public void Releases_stand_where_there_are_no_masters()
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(1, "Demo", type: "release", have: 12),
            Release(2, "Single", type: "release", have: 40)));

        Assert.Equal(["Single", "Demo"], popular.Select(r => r.Title));
    }

    [Fact]
    public void A_master_links_to_the_master_and_a_release_to_the_release()
    {
        var master = DiscogsPopular.Parse(Page(Release(77, "Grouped", type: "master")))[0];
        var loose = DiscogsPopular.Parse(Page(Release(88, "Loose", type: "release")))[0];

        Assert.Equal("https://www.discogs.com/master/77", master.Url);
        Assert.Equal("https://www.discogs.com/release/88", loose.Url);
    }

    /// <summary>Discogs does not put stats on every row. A missing count is zero, which orders it last
    /// and does not discard the page.</summary>
    [Fact]
    public void A_row_with_no_counts_is_still_a_record()
    {
        var popular = DiscogsPopular.Parse(
            "{\"releases\":[{\"id\":5,\"title\":\"Quiet\",\"role\":\"Main\",\"type\":\"master\",\"year\":1980}]}");

        Assert.Equal("Quiet", popular[0].Title);
        Assert.Equal(0, popular[0].Have);
    }

    [Fact]
    public void The_card_asks_for_a_handful_and_gets_a_handful()
    {
        var many = Enumerable.Range(1, 40)
            .Select(i => Release(i, $"Record {i}", have: i * 10))
            .ToArray();

        Assert.Equal(5, DiscogsPopular.Parse(Page(many)).Count);
        Assert.Equal(3, DiscogsPopular.Parse(Page(many), limit: 3).Count);
    }

    /// <summary>The card is optional: anything unreadable gives no card, never an exception thrown at
    /// the page.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"releases\":\"nonsense\"}")]
    public void Nonsense_is_an_empty_card(string json) => Assert.Empty(DiscogsPopular.Parse(json));

    // ── reading more than one page, and more than one artist page ──

    /// <summary>
    /// An artist can have several Discogs numbers. The first one the catalogue lists may be a name
    /// variant with a few bootlegs on it, while the page with the real catalogue is second (for
    /// example Kanye West). Ranked across everything gathered, the artist's own records win on their
    /// own counts.
    /// </summary>
    [Fact]
    public void A_second_artist_page_is_ranked_beside_the_first()
    {
        var variant = DiscogsPopular.Read(Page(Release(1, "Bootleg", have: 9)));
        var main = DiscogsPopular.Read(Page(Release(2, "The College Dropout", have: 90_000)));

        var popular = DiscogsPopular.Rank([.. variant, .. main]);

        Assert.Equal(["The College Dropout", "Bootleg"], popular.Select(r => r.Title));
    }

    /// <summary>A page read on its own is a ranking of that page. The card wants the artist, so the
    /// pages are gathered first and ordered once.</summary>
    [Fact]
    public void Pages_are_ranked_together_rather_than_one_at_a_time()
    {
        var first = DiscogsPopular.Read(Page(Release(1, "Early", have: 100)));
        var second = DiscogsPopular.Read(Page(Release(2, "Famous", have: 50_000)));

        Assert.Equal("Famous", DiscogsPopular.Rank([.. first, .. second])[0].Title);
    }

    /// <summary>The pages overlap when an API ignores a page number, and two of an artist's pages
    /// can list the same record. One record, one row.</summary>
    [Fact]
    public void The_same_record_gathered_twice_is_one_row()
    {
        var page = DiscogsPopular.Read(Page(
            Release(10, "Arrival", have: 21_000),
            Release(20, "Waterloo", have: 4_000)));

        var popular = DiscogsPopular.Rank([.. page, .. page]);

        Assert.Equal(["Arrival", "Waterloo"], popular.Select(r => r.Title));
    }

    /// <summary>A master and a pressing can have the same number (they are numbered separately), so
    /// "is this id one of the masters" would keep a pressing whose number matches by chance. The row
    /// says which it is.</summary>
    [Fact]
    public void A_pressing_that_shares_a_masters_number_is_still_a_pressing()
    {
        var popular = DiscogsPopular.Parse(Page(
            Release(42, "The album", type: "master", have: 5_000),
            Release(42, "A pressing of something else", type: "release", have: 80_000)));

        Assert.Equal(["The album"], popular.Select(r => r.Title));
    }

    /// <summary>How many pages to read, from the endpoint's own page count. One page when it does not
    /// say, as for every hand-written page in these tests.</summary>
    [Fact]
    public void The_page_count_comes_off_the_pagination()
    {
        Assert.Equal(7, DiscogsPopular.PageCount(
            "{\"pagination\":{\"pages\":7},\"releases\":[]}"));
        Assert.Equal(1, DiscogsPopular.PageCount("{\"releases\":[]}"));
        Assert.Equal(1, DiscogsPopular.PageCount("not json at all"));
    }
}

/// <summary>
/// Finding the Discogs artist number in the link the catalogues give. It is the only reliable way to
/// ask Discogs about an artist: a search by name returns the closest match the database has, and the
/// closest match to an artist's name is often a different artist.
/// </summary>
public class DiscogsArtistIdTests
{
    [Theory]
    [InlineData("https://www.discogs.com/artist/3461453-Ookay", "3461453")]
    [InlineData("https://www.discogs.com/artist/3461453", "3461453")]
    [InlineData("https://discogs.com/artist/4664", "4664")]
    [InlineData("https://www.discogs.com/artist/13733/", "13733")]
    [InlineData("https://www.discogs.com/de/artist/13733-ABBA", "13733")]
    public void The_number_is_taken_and_the_slug_left(string url, string expected)
        => Assert.Equal(expected, ReferenceLinks.DiscogsArtistId(url));

    [Theory]
    [InlineData("https://www.discogs.com/release/12345")]          // a record, not an artist
    [InlineData("https://www.discogs.com/master/999")]
    [InlineData("https://www.discogs.com/")]
    [InlineData("https://en.wikipedia.org/wiki/ABBA")]             // somebody else's site
    [InlineData("https://www.discogs.com/artist/not-a-number")]
    [InlineData("nonsense")]
    public void Anything_else_is_not_an_artist(string url)
        => Assert.Null(ReferenceLinks.DiscogsArtistId(url));
}

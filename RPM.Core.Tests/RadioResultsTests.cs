using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class RadioResultsTests
{
    /// <summary>A station as the directory would file it. <paramref name="stream"/> is given where
    /// the address is what the test is about. Left alone, it is an address that nothing else shares,
    /// so a test about names is not also a test about addresses.</summary>
    private static RadioStation Station(
        string name, string codec = "MP3", int bitrate = 128, string? favicon = null,
        string country = "NL", bool reachable = true, string? uuid = null, string? homepage = null,
        string? stream = null)
        => new()
        {
            Uuid = uuid ?? $"{name}-{codec}-{bitrate}",
            Name = name,
            StreamUrl = stream ?? $"http://example.test/{name}/{codec}/{bitrate}/{uuid}",
            Codec = codec,
            BitrateKbps = bitrate,
            Favicon = favicon,
            Homepage = homepage,
            CountryCode = country,
            Reachable = reachable,
        };

    private static string[] Names(IReadOnlyList<RadioStation> stations)
        => stations.Select(s => s.Name).ToArray();

    /// <summary>Radio Browser files a stream, not a station. A Netherlands search returns NPO Radio 1
    /// twice, once at MP3 192k and once at AAC 32k, and they are one station.</summary>
    [Fact]
    public void One_station_listed_twice_becomes_one_row()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 1", "MP3", 192),
            Station("NPO Radio 1", "AAC", 32),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);
    }

    /// <summary>The point worth stating: it is often the WORST stream that carries the picture, so
    /// the logo is taken from whichever row has one, and not by keeping that row.</summary>
    [Fact]
    public void The_best_stream_wears_the_logo_off_its_worse_twin()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 1", "MP3", 192),
            Station("NPO Radio 1", "AAC", 32, favicon: "http://npo.test/logo.png"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);
        Assert.Equal("http://npo.test/logo.png", one.Favicon);
    }

    // ── every logo travels, because which of them works cannot be known from the URL ──

    /// <summary>
    /// The real entries that the directory holds for NPO Radio 1, homepages included. The best
    /// stream's logo is a Wikimedia file that refuses a request with no browser behind it (403, and
    /// 400 for one claiming to be a browser). The other 192k entry's logo is an SVG. The one that
    /// works is a plain PNG on the station's own site, filed under the 64k stream. Nothing in those
    /// URLs says which is which, so all of them travel; the ranking only decides what is tried
    /// first.
    /// </summary>
    [Fact]
    public void The_logo_most_likely_to_work_is_tried_first()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 1", "MP3", 192, homepage: "https://www.nporadio1.nl/",
                favicon: "https://upload.wikimedia.org/wikipedia/commons/thumb/d/da/NPO_Radio_1_logo_2014.svg/640px-NPO_Radio_1_logo_2014.svg.png"),
            Station("NPO Radio 1", "MP3", 192, homepage: "https://www.nporadio1.nl/",
                favicon: "https://www.nporadio1.nl/svg/npo_radio1_logo.svg"),
            Station("NPO Radio 1", "AAC", 64, homepage: "https://www.nporadio1.nl/",
                favicon: "https://www.nporadio1.nl/apple-touch-icon.png"),
            Station("NPO Radio 1", "AAC", 32, homepage: "http://radio1.nl/",
                favicon: "http://radio1.nl/apple-touch-icon.png"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);

        // The broadcaster's own PNG, which is the one that actually serves.
        Assert.Equal("https://www.nporadio1.nl/apple-touch-icon.png", one.Favicon);

        // …and the rest still travel, worst last, for the row to fall back on.
        //
        // The Wikimedia render is not last. It is a PNG on a host that serves the app (RadioLogo.Sized
        // asks for a width Wikimedia renders), so it sorts above a plain-http address on a site that
        // is not the broadcaster's.
        Assert.Equal(
            ["https://www.nporadio1.nl/svg/npo_radio1_logo.svg",
             "https://upload.wikimedia.org/wikipedia/commons/thumb/d/da/NPO_Radio_1_logo_2014.svg/640px-NPO_Radio_1_logo_2014.svg.png",
             "http://radio1.nl/apple-touch-icon.png"],
            one.OtherLogos);
    }

    /// <summary>Nothing is ever excluded by the ranking: a station whose only logo is on a host that
    /// turns the app away is still tried. The alternative would be letting a list of hosts decide
    /// that a broadcaster has no picture.</summary>
    [Fact]
    public void A_badly_ranked_logo_is_still_kept()
    {
        var picked = RadioResults.OnePerStation([
            Station("Some Station", "MP3", 192,
                favicon: "https://upload.wikimedia.org/wikipedia/commons/logo.png"),
            Station("Some Station", "AAC", 64),
        ]);

        Assert.Equal(
            "https://upload.wikimedia.org/wikipedia/commons/logo.png",
            Assert.Single(picked).Favicon);
    }

    [Fact]
    public void The_broadcasters_own_copy_beats_a_third_partys()
    {
        Assert.True(
            RadioLogo.Rank("https://www.station.test/logo.png", "https://station.test/")
            > RadioLogo.Rank("https://cdn.elsewhere.test/logo.png", "https://station.test/"));
    }

    /// <summary>The directory spells one broadcaster's address both ways in a single answer.</summary>
    [Fact]
    public void A_www_prefix_is_not_a_different_site()
    {
        Assert.Equal(
            RadioLogo.Rank("https://station.test/logo.png", "https://www.station.test/"),
            RadioLogo.Rank("https://www.station.test/logo.png", "https://station.test/"));
    }

    /// <summary>
    /// Wikimedia is NOT a host that refuses the app.
    ///
    /// <para>A Wikimedia logo is not ranked below an address with no file extension. With the app's own
    /// agent, 15 of the 29 Wikimedia logos in the directory's 1,500 most-clicked stations load exactly
    /// as filed, and the rest load once the width is snapped to one that Wikimedia renders. A 400
    /// answer is about the SIZE, not the request. See RadioLogo.Wikimedia.</para>
    /// </summary>
    [Fact]
    public void A_wikimedia_logo_is_ranked_on_what_it_is_rather_than_on_its_host()
    {
        Assert.True(
            RadioLogo.Rank("https://upload.wikimedia.org/logo.png", homepage: null)
            > RadioLogo.Rank("https://anywhere.test/logo", homepage: null));
    }

    /// <summary>A drawing renders, but through a second image source and a rasterise step, so a
    /// photograph of the same mark on the same site is the better first try.</summary>
    [Fact]
    public void A_picture_is_tried_before_a_drawing()
    {
        Assert.True(
            RadioLogo.Rank("https://station.test/logo.png", "https://station.test/")
            > RadioLogo.Rank("https://station.test/logo.svg", "https://station.test/"));
    }

    [Fact]
    public void A_url_that_is_not_a_url_ranks_below_everything()
    {
        Assert.Equal(int.MinValue, RadioLogo.Rank("not a url", homepage: null));
    }

    /// <summary>Candidates that the ranker cannot tell apart keep the order the directory gave them,
    /// and not an arbitrary one.</summary>
    [Fact]
    public void Equal_candidates_keep_the_directorys_order()
    {
        var picked = RadioResults.OnePerStation([
            Station("Some Station", "MP3", 192, favicon: "https://a.test/logo.png"),
            Station("Some Station", "AAC", 64, favicon: "https://b.test/logo.png"),
            Station("Some Station", "AAC", 32, favicon: "https://c.test/logo.png"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal("https://a.test/logo.png", one.Favicon);
        Assert.Equal(["https://b.test/logo.png", "https://c.test/logo.png"], one.OtherLogos);
    }

    [Fact]
    public void The_same_logo_on_two_entries_is_listed_once()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 2", "MP3", 192, favicon: "http://www.nporadio2.nl/apple-touch-icon.png"),
            Station("NPO Radio 2", "AAC", 64, favicon: "http://www.nporadio2.nl/apple-touch-icon.png"),
            Station("NPO Radio 2", "AAC", 32, favicon: "http://WWW.NPORADIO2.NL/apple-touch-icon.png"),
        ]);

        Assert.Empty(Assert.Single(picked).OtherLogos);
    }

    /// <summary>A station with one logo has nothing to fall back on, and does not pay for a list.</summary>
    [Fact]
    public void One_logo_leaves_no_alternates()
    {
        var picked = RadioResults.OnePerStation([
            Station("Radio 10", "MP3", 128, favicon: "http://r10.test/logo.png"),
            Station("Radio 10", "AAC", 64),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal("http://r10.test/logo.png", one.Favicon);
        Assert.Empty(one.OtherLogos);
    }

    /// <summary>A site icon is readable (see IcoImage) and is kept, but it is a browser-tab picture:
    /// the station's mark at 32 pixels. So it is the last thing tried.</summary>
    [Fact]
    public void A_site_icon_is_kept_and_tried_last()
    {
        var picked = RadioResults.OnePerStation([
            Station("Sky Radio", "AAC", 64, favicon: "https://sky.test/favicon.ico"),
            Station("Sky Radio", "MP3", 192, favicon: "https://sky.test/logo.png"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal("https://sky.test/logo.png", one.Favicon);
        Assert.Equal(["https://sky.test/favicon.ico"], one.OtherLogos);
    }

    /// <summary>A drawing is worth trying. Only a bitmap DECODER cannot read one, and the row hands
    /// those to an SvgImageSource instead.</summary>
    [Fact]
    public void A_drawing_is_worth_trying()
    {
        Assert.True(RadioLogo.IsDrawable("https://a.test/logo.svg"));
        Assert.True(RadioLogo.IsVector("https://a.test/logo.svg"));
        Assert.False(RadioLogo.IsVector("https://a.test/logo.png"));
        Assert.False(RadioLogo.IsVector(null));
    }

    /// <summary>
    /// A site icon is a station's mark at 32 pixels, so a real picture on a twin entry goes first and
    /// the .ico waits behind it. If the .ico counted as "this entry has a logo", the PNG on the twin
    /// would be discarded and the row would draw an antenna although the station's picture was one
    /// field away.
    /// </summary>
    [Fact]
    public void A_site_icon_does_not_displace_a_real_picture()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 1", "MP3", 192, favicon: "https://nporadio1.test/favicon.ico"),
            Station("NPO Radio 1", "AAC", 32, favicon: "https://nporadio1.test/logo.png"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);
        Assert.Equal("https://nporadio1.test/logo.png", one.Favicon);
    }

    /// <summary>Nothing drawable anywhere: the entry keeps what it was tagged with, and is not
    /// rewritten to null. The row draws the antenna either way, and the field is still the directory's
    /// own answer about this station.</summary>
    [Fact]
    public void An_undrawable_logo_survives_when_no_twin_has_a_better_one()
    {
        var picked = RadioResults.OnePerStation([
            Station("Sky Radio", "MP3", 192, favicon: "https://sky.test/favicon.ico"),
            Station("Sky Radio", "AAC", 64),
        ]);

        Assert.Equal("https://sky.test/favicon.ico", Assert.Single(picked).Favicon);
    }

    [Theory]
    [InlineData("https://a.test/logo.png", true)]
    [InlineData("http://a.test/logo.jpg", true)]
    [InlineData("https://a.test/favicon.ico", true)]
    [InlineData("ftp://a.test/logo.png", false)]
    [InlineData("/relative/logo.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void What_counts_as_a_logo_worth_trying(string? favicon, bool drawable)
    {
        Assert.Equal(drawable, RadioLogo.IsDrawable(favicon));
    }

    /// <summary>What a URL ends in is read off its PATH: a query string does not make a picture a site
    /// icon, and the word "favicon.ico" in one does not either.</summary>
    [Fact]
    public void The_path_is_what_is_ranked_not_the_whole_url()
    {
        Assert.True(
            RadioLogo.Rank("https://a.test/logo.png?icon=favicon.ico", homepage: null)
            > RadioLogo.Rank("https://a.test/favicon.ico?v=2", homepage: null));
    }

    /// <summary>Last of the four, under an unknown extension: a browser-tab icon is a picture that
    /// exists. It is drawn soft, and is better than the antenna only because there is nothing
    /// else.</summary>
    [Fact]
    public void A_site_icon_ranks_under_everything_else_worth_trying()
    {
        const string? nowhere = null;

        Assert.True(RadioLogo.Rank("https://a.test/logo.png", nowhere)
                  > RadioLogo.Rank("https://a.test/logo.svg", nowhere));
        Assert.True(RadioLogo.Rank("https://a.test/logo.svg", nowhere)
                  > RadioLogo.Rank("https://a.test/logo", nowhere));
        Assert.True(RadioLogo.Rank("https://a.test/logo", nowhere)
                  > RadioLogo.Rank("https://a.test/favicon.ico", nowhere));
    }

    [Fact]
    public void A_logo_the_best_stream_already_has_is_kept()
    {
        var picked = RadioResults.OnePerStation([
            Station("Radio 10", "MP3", 128, favicon: "http://r10.test/good.png"),
            Station("Radio 10", "AAC", 64, favicon: "http://r10.test/other.png"),
        ]);

        Assert.Equal("http://r10.test/good.png", Assert.Single(picked).Favicon);
    }

    /// <summary>The directory's checker has looked. A 320k stream it could not open is worse to offer
    /// than a 96k one it could.</summary>
    [Fact]
    public void A_stream_the_directory_could_not_reach_loses_to_one_it_could()
    {
        var picked = RadioResults.OnePerStation([
            Station("Sky Radio", "MP3", 320, reachable: false),
            Station("Sky Radio", "AAC", 96, reachable: true),
        ]);

        Assert.Equal(96, Assert.Single(picked).BitrateKbps);
    }

    /// <summary>Unstated loses to any stated bitrate. It does not rank as zero and equal-worst,
    /// because that would give a silent preference for whichever unstated stream came back first.</summary>
    [Fact]
    public void A_stream_that_declares_no_bitrate_loses_to_one_that_does()
    {
        var picked = RadioResults.OnePerStation([
            Station("Qmusic", "MP3", 0),
            Station("Qmusic", "MP3", 96),
        ]);

        Assert.Equal(96, Assert.Single(picked).BitrateKbps);
    }

    /// <summary>Case, spacing and punctuation are not the difference between two stations.</summary>
    [Fact]
    public void The_same_name_spelt_differently_is_the_same_station()
    {
        var picked = RadioResults.OnePerStation([
            Station("Q music Nederland", "AAC", 95),
            Station("Q-Music  NEDERLAND", "MP3", 192),
        ]);

        Assert.Single(picked);
    }

    /// <summary>A name is only unique inside a country. Collapsing these would delete a broadcaster in
    /// order to tidy up a duplicate that was not there.</summary>
    [Fact]
    public void The_same_name_in_two_countries_is_two_stations()
    {
        var picked = RadioResults.OnePerStation([
            Station("Radio 1", "MP3", 128, country: "NL"),
            Station("Radio 1", "MP3", 128, country: "GB"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    /// <summary>The directory sorts by how often a station is played. Sorting again here would discard
    /// that for an order nobody asked for, so a collapsed station keeps the place it first held.</summary>
    [Fact]
    public void The_directorys_own_order_is_kept()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 1", "MP3", 192),
            Station("Radio 538", "MP3", 128),
            Station("NPO Radio 2", "MP3", 192),
            Station("NPO Radio 1", "AAC", 32),
            Station("Arrow Classic Rock", "MP3", 192),
            Station("NPO Radio 2", "AAC", 64),
        ]);

        Assert.Equal(
            ["NPO Radio 1", "Radio 538", "NPO Radio 2", "Arrow Classic Rock"],
            Names(picked));
    }

    /// <summary>Nothing is dropped for having no picture: a great many of the stations that a country
    /// listens to have none.</summary>
    [Fact]
    public void A_station_with_no_logo_still_appears()
    {
        var picked = RadioResults.OnePerStation([
            Station("Radio 538", "MP3", 128),
            Station("3FM", "MP3", 192),
        ]);

        Assert.Equal(["Radio 538", "3FM"], Names(picked));
        Assert.All(picked, s => Assert.Null(s.Favicon));
    }

    [Fact]
    public void A_list_with_nothing_to_collapse_comes_back_as_it_was()
    {
        RadioStation[] one = [Station("NPO Radio 1")];

        Assert.Same(one, RadioResults.OnePerStation(one));
        Assert.Empty(RadioResults.OnePerStation([]));
    }

    /// <summary>A station with no name at all cannot be filed under one, and dropping it would delete
    /// a row that the directory did return. Two of them at two addresses are two rows.</summary>
    [Fact]
    public void A_nameless_station_stands_for_itself()
    {
        var picked = RadioResults.OnePerStation([
            Station("", "MP3", 128, uuid: "a", stream: "http://one.test/a"),
            Station("", "MP3", 128, uuid: "b", stream: "http://two.test/b"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    // ── the second key: one address contributed under several names ──

    /// <summary>
    /// Anybody may file a stream, so one address arrives under whatever each contributor called it.
    /// The Dutch directory has "NPO 2" and "NPO Radio 2" at the same icecast mount, and they are
    /// one station however differently the two rows spell it.
    /// </summary>
    [Fact]
    public void One_address_under_two_names_becomes_one_row()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 2", "MP3", 192, stream: "http://icecast.omroep.nl/radio2-bb-mp3"),
            Station("NPO 2", "MP3", 192, stream: "http://icecast.omroep.nl/radio2-bb-mp3"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal("NPO Radio 2", one.Name);   // the row the directory listed first, being the busier
    }

    /// <summary>
    /// Neither key reaches all four of these rows on its own, and together they do: the MP3 pair share
    /// an address, the AAC pair share an address, and each name bridges one pair to the other.
    /// </summary>
    [Fact]
    public void Names_and_addresses_together_reach_what_neither_reaches_alone()
    {
        var picked = RadioResults.OnePerStation([
            Station("NPO Radio 2", "MP3", 192, stream: "http://icecast.omroep.nl/radio2-bb-mp3"),
            Station("NPO 2", "MP3", 192, stream: "http://icecast.omroep.nl/radio2-bb-mp3"),
            Station("NPO Radio 2", "AAC", 64, stream: "https://icecast.omroep.nl/radio2-bb-aac"),
            Station("NPO 2", "AAC", 64, stream: "http://icecast.omroep.nl/radio2-bb-aac"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);
        Assert.Equal("NPO Radio 2", one.Name);
    }

    /// <summary>http and https of one address are one stream, and the directory's entries for a station
    /// disagree about which as often as they disagree about anything else.</summary>
    [Fact]
    public void The_scheme_is_not_part_of_the_address()
    {
        var picked = RadioResults.OnePerStation([
            Station("3FM", "MP3", 192, stream: "http://icecast.omroep.nl/3fm-bb-mp3"),
            Station("NPO 3FM", "MP3", 192, stream: "https://icecast.omroep.nl/3fm-bb-mp3"),
            Station("NPO 3FM Serious Radio", "MP3", 192, stream: "http://icecast.omroep.nl/3fm-bb-mp3/"),
        ]);

        Assert.Single(picked);
    }

    /// <summary>A leading "www." and a trailing slash are spelling, not address.</summary>
    [Fact]
    public void Www_and_a_trailing_slash_are_not_a_difference()
    {
        var picked = RadioResults.OnePerStation([
            Station("Deep", stream: "http://www.stream.deep.radio/hd/"),
            Station("Deep Radio", stream: "https://stream.deep.radio/hd"),
        ]);

        Assert.Single(picked);
    }

    /// <summary>A stated port IS the address: one host serves a station per port.</summary>
    [Fact]
    public void A_stated_port_tells_two_stations_apart()
    {
        var picked = RadioResults.OnePerStation([
            Station("First", stream: "http://shared.test:8000/stream"),
            Station("Second", stream: "http://shared.test:8010/stream"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    /// <summary>
    /// And so is the query string. One CDN serves hundreds of stations (playerservices.streamtheworld
    /// .com is the usual one), and dropping what tells them apart would merge broadcasters that share
    /// nothing but a host.
    /// </summary>
    [Fact]
    public void The_query_string_tells_two_stations_apart()
    {
        var picked = RadioResults.OnePerStation([
            Station("One", stream: "https://cdn.test/api/livestream-redirect?station=ONE"),
            Station("Two", stream: "https://cdn.test/api/livestream-redirect?station=TWO"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    /// <summary>
    /// The address carries a country for the same reason the name does. CNN and CNN UK are filed under
    /// one address in two countries, and they are two stations everywhere else in the app.
    /// </summary>
    [Fact]
    public void One_address_in_two_countries_stays_two_stations()
    {
        var picked = RadioResults.OnePerStation([
            Station("CNN", country: "US", stream: "https://tunein.cdnstream1.test/2868_96.mp3"),
            Station("CNN UK", country: "GB", stream: "https://tunein.cdnstream1.test/2868_96.mp3"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    /// <summary>A row the directory has no usable address for is still filed under its name.</summary>
    [Fact]
    public void An_unreadable_address_leaves_the_name_to_do_the_work()
    {
        var picked = RadioResults.OnePerStation([
            Station("Pirate FM", "MP3", 192, stream: "not a url"),
            Station("Pirate FM", "AAC", 32, stream: "also not a url"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(192, one.BitrateKbps);
    }

    /// <summary>Rows that agree on neither key are left alone, in the order they came.</summary>
    [Fact]
    public void Stations_that_share_neither_key_all_survive()
    {
        var picked = RadioResults.OnePerStation([
            Station("Alpha", stream: "http://a.test/one"),
            Station("Beta", stream: "http://b.test/two"),
            Station("Gamma", stream: "http://c.test/three"),
        ]);

        Assert.Equal(["Alpha", "Beta", "Gamma"], Names(picked));
    }

    // ── which picture, when the entries disagree ──

    /// <summary>One asset on a resizing host, at whatever size each contributor pasted.</summary>
    private static string Brand(string asset, int size)
        => $"https://i.iheart.com/v3/re/assets.brands/{asset}?ops=gravity(%22center%22),contain({size},{size})";

    /// <summary>
    /// Where a station's entries name two different pictures, the one that more of them name wins.
    ///
    /// <para>Based on real data: three of the directory's entries for 102.7 KIIS FM, two naming
    /// the station's wordmark at two different sizes and one naming an unrelated letter. Nothing in
    /// either address says which is the station's mark; how many people chose it does.</para>
    /// </summary>
    [Fact]
    public void The_picture_more_entries_name_is_the_one_tried_first()
    {
        var picked = RadioResults.OnePerStation([
            Station("KIIS FM", "AAC", 128, favicon: Brand("theletter", 167)),
            Station("KIIS FM", "MP3", 64, favicon: Brand("thewordmark", 32)),
            Station("KIIS FM", "MP3", 96, favicon: Brand("thewordmark", 360)),
        ]);

        var one = Assert.Single(picked);
        Assert.Contains("thewordmark", one.Favicon);

        // …and the letter is not discarded: the walk falls back to it if the wordmark will not load.
        Assert.Contains(one.OtherLogos, url => url.Contains("theletter"));
    }

    /// <summary>The same asset at two sizes is ONE picture and TWO votes. If votes were counted by
    /// address, a station whose contributors all agreed would look like a station that could not
    /// decide, and the odd one out would win on a tie-break it should lose.</summary>
    [Fact]
    public void One_asset_filed_at_two_sizes_is_one_candidate_to_try()
    {
        var picked = RadioResults.OnePerStation([
            Station("KIIS FM", "MP3", 64, favicon: Brand("thewordmark", 32)),
            Station("KIIS FM", "MP3", 96, favicon: Brand("thewordmark", 360)),
        ]);

        var one = Assert.Single(picked);
        Assert.Contains("thewordmark", one.Favicon);
    }

    /// <summary>Agreement is not the only test, and it is not the strongest. A picture from the
    /// broadcaster's own site beats one that two strangers happened to file. This is the ranker's
    /// strongest rule.</summary>
    [Fact]
    public void The_stations_own_copy_still_beats_a_picture_two_entries_agreed_on()
    {
        var picked = RadioResults.OnePerStation([
            Station("KIIS FM", "AAC", 128, homepage: "https://kiisfm.test/",
                    favicon: "https://kiisfm.test/logo.png"),
            Station("KIIS FM", "MP3", 64, homepage: "https://kiisfm.test/",
                    favicon: Brand("amirror", 32)),
            Station("KIIS FM", "MP3", 96, homepage: "https://kiisfm.test/",
                    favicon: Brand("amirror", 360)),
        ]);

        Assert.Equal("https://kiisfm.test/logo.png", Assert.Single(picked).Favicon);
    }

    // ── one channel, several naming patterns ──

    /// <summary>
    /// Qmusic in the Dutch directory: five entries, four spellings, three addresses, one station. The
    /// name key folds the spellings and the address key folds the rest.
    /// </summary>
    [Fact]
    public void The_spellings_of_one_channel_become_one_row()
    {
        var picked = RadioResults.OnePerStation([
            Station("Qmusic", "MP3", 96, stream: "https://icecast-qmusicnl.test/Qmusic_nl_live_96.mp3"),
            Station("Q-Music", "AAC+", 96, stream: "https://icecast-qmusicnl.test/Qmusic_nl_live_high"),
            Station("Q music Nederland", "AAC+", 96, stream: "https://stream.qmusic.nl/qmusic/aachigh"),
            Station("QMUSIC NEDERLAND", "MP3", 128, stream: "https://stream.qmusic.nl/qmusic/mp3"),
            Station("QMUSIC NEDERLAND AAC HIGH", "AAC+", 95, stream: "https://stream.qmusic.nl/qmusic/aachigh"),
        ]);

        var one = Assert.Single(picked);
        Assert.Equal(128, one.BitrateKbps);
    }

    /// <summary>
    /// And the line it must not cross: a broadcaster's other channels differ from the parent by one
    /// word and are not the parent. Folding on a prefix would delete every one of these.
    /// </summary>
    [Fact]
    public void A_broadcasters_other_channels_are_not_the_broadcaster()
    {
        var picked = RadioResults.OnePerStation([
            Station("Qmusic", stream: "https://stream.qmusic.test/qmusic/mp3"),
            Station("Qmusic Non-Stop", stream: "https://stream.qmusic.test/nonstop/mp3"),
            Station("Qmusic Het Foute Uur", stream: "https://stream.qmusic.test/fouteuur/mp3"),
            Station("Radio 10", stream: "https://stream.radio10.test/live"),
            Station("Radio 10 non-stop", stream: "https://stream.radio10.test/nonstop"),
        ]);

        Assert.Equal(5, picked.Count);
    }

    /// <summary>A country word at the head of a name is the brand, not a qualifier.</summary>
    [Fact]
    public void Gb_news_is_not_a_news_station_called_uk()
    {
        var picked = RadioResults.OnePerStation([
            Station("GB News", country: "GB", stream: "https://listen.test/gbnews.mp3"),
            Station("News Radio UK", country: "GB", stream: "https://listen.test/newsradio.mp3"),
        ]);

        Assert.Equal(2, picked.Count);
    }

    /// <summary>Two Russian stations whose names differ only outside ASCII are two stations.</summary>
    [Fact]
    public void Two_stations_named_in_another_script_stay_two()
    {
        var picked = RadioResults.OnePerStation([
            Station("Вести FM", country: "RU", stream: "https://a.test/vesti"),
            Station("Ретро FM", country: "RU", stream: "https://a.test/retro"),
        ]);

        Assert.Equal(2, picked.Count);
    }
}

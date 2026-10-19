using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class RadioAdsTests
{
    // ── which stations are marked ──

    /// <summary>The four stations that the directory tags "commercial", out of the three thousand
    /// most-played stations it lists.</summary>
    [Theory]
    [InlineData("commercial", "hot adult contemporary", "london")]
    [InlineData("commercial", "dance", "electronic")]
    [InlineData("bloemfontein", "commercial", "local news")]
    public void A_station_that_says_it_is_commercial_is_marked(params string[] tags)
    {
        Assert.True(RadioAds.CarriesAds(tags));
    }

    /// <summary>Public is not ad-free, which is the whole reason it is marked too: NPO carries STER,
    /// NPR carries sponsorship, 1LIVE carries spots.</summary>
    [Theory]
    [InlineData("public radio")]
    [InlineData("public service")]
    [InlineData("new york public radio")]
    [InlineData("Public Radio")]
    [InlineData("öffentlich-rechtlich")]
    [InlineData("government")]
    public void A_public_broadcaster_is_marked(string tag)
    {
        Assert.True(RadioAds.CarriesAds([tag, "news"]));
    }

    /// <summary>A station that states the specific thing has given the answer.</summary>
    [Theory]
    [InlineData("non-commercial")]
    [InlineData("noncommercial")]
    [InlineData("no ads")]
    [InlineData("ad-free")]
    [InlineData("werbefrei")]
    public void A_station_that_says_it_carries_none_is_not_marked(string tag)
    {
        Assert.False(RadioAds.CarriesAds([tag, "jazz"]));
    }

    /// <summary>"non-commercial" contains "commercial". If read the wrong way round, it says the
    /// opposite of what it means, so the denial is read first and wins.</summary>
    [Fact]
    public void Saying_both_means_the_one_that_is_specific()
    {
        Assert.False(RadioAds.CarriesAds(["public radio", "non-commercial"]));
        Assert.False(RadioAds.CarriesAds(["non-commercial"]));
    }

    /// <summary>Silence is silence. Nearly every station in the directory says nothing about this, and
    /// saying nothing is not "no".</summary>
    [Fact]
    public void A_station_that_says_nothing_is_not_marked()
    {
        Assert.False(RadioAds.CarriesAds(["90s", "club", "dance"]));
        Assert.False(RadioAds.CarriesAds([]));
        Assert.False(RadioAds.CarriesAds(null));
    }

    // ── and what is happening right now ──

    [Theory]
    [InlineData("Advertisement")]
    [InlineData("advertisements")]
    [InlineData("ADVERTISING")]
    [InlineData("Ad Break")]
    [InlineData("Commercial")]
    [InlineData("Reclame")]
    [InlineData("Werbung")]
    [InlineData("Publicité")]
    [InlineData("Publicidad")]
    [InlineData("- Werbung -")]
    [InlineData("[reclame]")]
    [InlineData("  Advertisement  ")]
    public void A_station_announcing_a_break_is_in_one(string announced)
    {
        Assert.True(RadioAds.IsAdBreak(announced));
    }

    /// <summary>The whole line has to be the marker. A listener must not be told there is an ad break
    /// just because the record playing has an unlucky title. They can hear the truth for
    /// themselves.</summary>
    [Theory]
    [InlineData("The Commercials - Advertisement For Myself")]
    [InlineData("Werbung für die Liebe")]
    [InlineData("Adele - Hello")]
    [InlineData("Adverts of Our Lives - The Band")]
    public void A_record_that_merely_mentions_one_is_not_a_break(string announced)
    {
        Assert.False(RadioAds.IsAdBreak(announced));
    }

    [Fact]
    public void Nothing_announced_is_not_a_break()
    {
        Assert.False(RadioAds.IsAdBreak(null));
        Assert.False(RadioAds.IsAdBreak(""));
        Assert.False(RadioAds.IsAdBreak("   "));
        Assert.False(RadioAds.IsAdBreak("- - -"));
    }

    // ── the broadcaster the station belongs to, for the stations whose tags say nothing ──

    /// <summary>The stations on a Dutch dial. None of them carries a tag that the rule above can read
    /// (of 400 Dutch stations exactly two do), so the broadcaster is what decides for them.</summary>
    [Theory]
    [InlineData("NPO Radio 1", "https://www.nporadio1.nl/")]
    [InlineData("NPO Radio 2", "http://www.nporadio2.nl/")]
    [InlineData("3FM", "https://www.npo3fm.nl/")]
    [InlineData("Radio 538", "https://www.538.nl/")]
    [InlineData("Sky Radio", "https://www.skyradio.nl/")]
    [InlineData("Radio 10", "https://www.radio10.nl/")]
    [InlineData("Joe", "https://joe.nl/")]
    [InlineData("QMUSIC NEDERLAND", "https://qmusic.nl/")]
    [InlineData("100% NL", "https://www.100p.nl/")]
    [InlineData("Arrow Classic Rock", "https://www.arrow.nl/")]
    [InlineData("Radio Gelderland", "https://www.omroepgelderland.nl/")]
    public void A_known_broadcaster_is_marked(string name, string homepage)
    {
        Assert.True(RadioAds.CarriesAds(["pop"], name, homepage));
    }

    /// <summary>The site is the better key: "Joe" and "538" are words, but joe.nl and 538.nl are
    /// addresses that only one organisation can publish from.</summary>
    [Fact]
    public void The_site_answers_even_where_the_name_would_not()
    {
        Assert.True(RadioAds.CarriesAds(null, "Some Rebrand", "https://www.538.nl/some/page"));
        Assert.True(RadioAds.CarriesAds(null, "Some Rebrand", "https://nporadio1.nl/"));
    }

    /// <summary>Matched from the start and on whole words. Otherwise the list would find a broadcaster
    /// in every station whose name merely contains one of these.</summary>
    [Theory]
    [InlineData("Nippon Radio")]
    [InlineData("Bbcrooklyn Sound")]
    [InlineData("Radio NPO Tribute")]
    [InlineData("Intense Radio")]
    public void A_name_that_merely_contains_one_is_not_that_broadcaster(string name)
    {
        Assert.False(RadioAds.CarriesAds(["dance"], name, "https://example.test/"));
    }

    /// <summary>A station's own statement about itself wins over the list it happens to be in.</summary>
    [Fact]
    public void A_station_saying_it_carries_none_is_taken_at_its_word()
    {
        Assert.False(RadioAds.CarriesAds(["non-commercial"], "NPO Radio 1", "https://www.nporadio1.nl/"));
    }

    /// <summary>A station nobody has heard of is not marked. A short list costs a missing mark, never
    /// a false one.</summary>
    [Fact]
    public void An_unknown_station_is_still_not_marked()
    {
        Assert.False(RadioAds.CarriesAds(["dance"], "Intense Radio", "https://www.intenseradio.com/"));
    }

    // ── the American marker, once its fields are off (see IcyFields) ──

    /// <summary>What iHeart writes when a break starts. The ids are already removed by the time this
    /// reads it.</summary>
    [Theory]
    [InlineData("Spot Block Start")]
    [InlineData("Spot Block")]
    [InlineData("spot block start")]
    public void A_spot_block_starting_is_a_break(string announced)
    {
        Assert.True(RadioAds.IsAdBreak(announced));
        Assert.False(RadioAds.IsBreakOver(announced));
    }

    /// <summary>…and what it writes when the break ends. This is the opposite answer: nothing is
    /// playing, and saying "Ad break" would say the reverse of what the station just said.
    /// </summary>
    [Theory]
    [InlineData("Spot Block End")]
    [InlineData("spot block end")]
    public void A_spot_block_ending_is_neither_a_break_nor_a_record(string announced)
    {
        Assert.True(RadioAds.IsBreakOver(announced));
        Assert.False(RadioAds.IsAdBreak(announced));
    }

    /// <summary>A record is still a record. The whole line has to be the marker.</summary>
    [Theory]
    [InlineData("Spot - Wet Leg")]
    [InlineData("The Blockheads - Spot The Difference")]
    [InlineData(null)]
    [InlineData("")]
    public void Anything_else_is_neither(string? announced)
    {
        Assert.False(RadioAds.IsAdBreak(announced));
        Assert.False(RadioAds.IsBreakOver(announced));
    }
}

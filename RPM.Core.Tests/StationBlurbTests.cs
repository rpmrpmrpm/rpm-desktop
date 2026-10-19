using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The line under a station's name on the home screen's radio card: where it comes from, and what it
/// refuses.
///
/// <para>The refusals are the half worth testing. This is the one lookup in the app that finds a
/// Wikipedia article by NAME. Everything else refuses to do that, because a confident paragraph
/// about the wrong subject is worse than none. What makes it safe here is that it will not take an
/// article that does not read as a radio station. That rule is one a later edit could quietly
/// remove.</para>
/// </summary>
public class StationBlurbTests
{
    // ── what the station says about itself ──

    [Fact]
    public void The_open_graph_description_is_preferred_over_the_plain_one()
    {
        const string html = """
            <html><head>
            <meta name="description" content="NPO Radio 2 op 88.0 FM, 88.1, 88.3, 89.0, 89.5 FM en DAB+">
            <meta property="og:description" content="NPO Radio 2 is het station voor muziekliefhebbers.">
            </head></html>
            """;

        Assert.Equal("NPO Radio 2 is het station voor muziekliefhebbers.", StationBlurb.FromPage(html));
    }

    [Fact]
    public void The_plain_description_is_taken_where_there_is_no_other()
    {
        const string html =
            """<html><head><meta name="description" content="The station for music lovers."></head></html>""";

        Assert.Equal("The station for music lovers.", StationBlurb.FromPage(html));
    }

    [Fact]
    public void Entities_are_decoded()
    {
        const string html =
            """<html><head><meta name="description" content="Rock &amp; roll, all day &#39;round."></head></html>""";

        Assert.Equal("Rock & roll, all day 'round.", StationBlurb.FromPage(html));
    }

    /// <summary>A front page that is an app shell with nothing in its head. This is common and
    /// ordinary.</summary>
    [Fact]
    public void A_page_that_declares_nothing_says_nothing()
        => Assert.Null(StationBlurb.FromPage("<html><head><title>Radio</title></head></html>"));

    [Fact]
    public void Nothing_at_all_says_nothing()
    {
        Assert.Null(StationBlurb.FromPage(null));
        Assert.Null(StationBlurb.FromPage("   "));
    }

    /// <summary>A head that declares a description without writing one.</summary>
    [Fact]
    public void A_description_too_short_to_be_one_is_not_taken()
        => Assert.Null(StationBlurb.FromPage("""<html><head><meta name="description" content="Home"></head></html>"""));

    // ── what the encyclopedia says ──

    /// <summary>Wikidata's own one-liner is what this slot wants: somebody wrote it to BE a line.</summary>
    [Fact]
    public void The_short_description_is_preferred_over_the_article_opening()
    {
        const string json = """
            {"type":"standard","title":"NPO Radio 2",
             "description":"Dutch public radio station",
             "extract":"NPO Radio 2 is a Dutch national radio station operated by the NPO. It began in 1947."}
            """;

        Assert.Equal("Dutch public radio station", StationBlurb.FromWikipedia(json));
    }

    [Fact]
    public void The_article_opening_is_taken_where_there_is_no_short_description()
    {
        const string json = """
            {"type":"standard","title":"NPO Radio 2",
             "extract":"NPO Radio 2 is a Dutch national radio station. It began in 1947."}
            """;

        Assert.Equal("NPO Radio 2 is a Dutch national radio station.", StationBlurb.FromWikipedia(json));
    }

    /// <summary>The refusal the whole by-name lookup rests on: an article that is not about a radio
    /// station is the wrong article. "Kiss" and "Star" find things that are not stations at all.</summary>
    [Fact]
    public void An_article_that_is_not_about_a_station_is_refused()
    {
        const string json = """
            {"type":"standard","title":"Kiss",
             "description":"American rock band",
             "extract":"Kiss is an American rock band formed in New York City in 1973."}
            """;

        Assert.Null(StationBlurb.FromWikipedia(json));
    }

    /// <summary>What a station named after an ordinary word lands on.</summary>
    [Fact]
    public void A_disambiguation_page_is_refused()
    {
        const string json = """
            {"type":"disambiguation","title":"Radio 2",
             "description":"Wikipedia disambiguation page",
             "extract":"Radio 2 may refer to several radio stations."}
            """;

        Assert.Null(StationBlurb.FromWikipedia(json));
    }

    [Fact]
    public void A_body_that_is_not_json_says_nothing()
    {
        Assert.Null(StationBlurb.FromWikipedia("<html>404</html>"));
        Assert.Null(StationBlurb.FromWikipedia(null));
    }

    /// <summary>The word is a loanword in nearly every language, which is why one word is used and not
    /// a list of translations to maintain.</summary>
    [Theory]
    [InlineData("Dutch public radio station")]
    [InlineData("station de radio française")]
    [InlineData("emisora de radio española")]
    [InlineData("öffentlich-rechtlicher Radiosender")]
    [InlineData("Nederlandse radiozender")]
    public void A_station_in_any_of_these_languages_reads_as_one(string described)
        => Assert.True(StationBlurb.IsARadioStation(described));

    [Theory]
    [InlineData("American rock band")]
    [InlineData("bus route in London")]
    [InlineData(null)]
    [InlineData("")]
    public void Anything_else_does_not(string? described)
        => Assert.False(StationBlurb.IsARadioStation(described));

    // ── one line, and no longer ──

    [Fact]
    public void Whitespace_is_squashed_to_one_line()
        => Assert.Equal(
            "The station for music lovers.",
            StationBlurb.Shorten("  The station\r\n   for   music lovers.  "));

    /// <summary>A description that is two sentences is nearly always one saying what the station is and
    /// a second selling it. The first is the answer.</summary>
    [Fact]
    public void A_long_line_is_cut_at_a_sentence_where_there_is_one()
    {
        var text = "NPO Radio 2 is the Dutch station for music lovers. "
                 + "Listen live to the best songs from every decade, all day and every day.";

        var cut = StationBlurb.Shorten(text);

        Assert.Equal("NPO Radio 2 is the Dutch station for music lovers.", cut);
        Assert.DoesNotContain("…", cut);
    }

    /// <summary>Never mid-word: a line ending "the Netherla…" looks as if the app broke the text and
    /// did not shorten it.</summary>
    [Fact]
    public void A_long_line_with_no_sentence_in_it_is_cut_at_a_word()
    {
        var text = string.Join(' ', Enumerable.Repeat("music", 60));

        var cut = StationBlurb.Shorten(text);

        Assert.NotNull(cut);
        Assert.True(cut!.Length <= StationBlurb.Longest);
        Assert.EndsWith("music…", cut);
    }

    [Fact]
    public void A_line_already_short_enough_is_left_alone()
    {
        const string text = "The station for music lovers.";
        Assert.Equal(text, StationBlurb.Shorten(text));
    }

    /// <summary>A stop has to end a sentence, not sit inside a number or a domain.</summary>
    [Fact]
    public void A_stop_inside_a_domain_is_not_a_sentence_end()
    {
        var text = "Listen to everything at npo.nl and on every platform there is, "
                 + "wherever you happen to be, at any hour of the day or night, for free.";

        var cut = StationBlurb.Shorten(text);

        Assert.NotNull(cut);
        Assert.DoesNotContain("npo.nl", cut![..Math.Min(cut.Length, 12)]);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void The_summary_address_names_the_language_and_escapes_the_title()
        => Assert.Equal(
            "https://nl.wikipedia.org/api/rest_v1/page/summary/NPO%20Radio%202",
            StationBlurb.SummaryUrl("nl", "NPO Radio 2"));
}

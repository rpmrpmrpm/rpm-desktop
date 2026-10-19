using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The line a banner card puts a record forward with.
///
/// <para>Tested here because it must never say anything the article does not say. A template that
/// praised every record would look the same from the outside. Only a test that gives it a plain
/// article and expects nothing back can tell them apart.
/// </para>
/// </summary>
public class AlbumHypeTests
{
    // ── it says only what the article says ──

    /// <summary>The fault this exists to stop. An article that describes a record plainly gives no
    /// reason to press it, and the honest answer is no line at all, not "critically acclaimed" over
    /// a record nobody ever acclaimed.</summary>
    [Fact]
    public void An_article_with_nothing_remarkable_in_it_gets_no_line()
        => Assert.Null(AlbumHype.For(
            "Some Record is an album by a band from Leeds. It was recorded over three weeks "
            + "and released on a small label.", 1994));

    [Fact]
    public void No_article_is_no_line()
    {
        Assert.Null(AlbumHype.For(null, 1973));
        Assert.Null(AlbumHype.For("", 1973));
        Assert.Null(AlbumHype.For("   ", 1973));
    }

    // ── what it finds ──

    [Theory]
    [InlineData("The album was released to widespread critical acclaim.", "Critically acclaimed album")]
    [InlineData("It is one of the best-selling albums of all time.", "A best-selling album")]
    [InlineData("The record topped the UK Albums Chart for five weeks.", "A number one album")]
    [InlineData("It won a Grammy Award for Best Rock Album.", "Grammy winning album")]
    [InlineData("The album is considered a landmark of the genre.", "A landmark record")]
    [InlineData("It is the debut studio album by the English musician.", "The debut album")]
    [InlineData("A concept album about a journey across America.", "A concept album")]
    public void It_reads_what_the_article_carries(string extract, string expected)
        => Assert.Equal($"{expected} from 1973", AlbumHype.For(extract, 1973));

    /// <summary>The strongest thing first. An article usually has several of these (a debut that was
    /// acclaimed and went to number one), and the line has room for one, so it should be the one most
    /// likely to make somebody press the card.</summary>
    [Fact]
    public void The_strongest_claim_wins()
    {
        var extract = "The debut studio album, released to critical acclaim, "
                    + "it won a Grammy Award and topped the chart.";

        Assert.Equal("Grammy winning album from 1973", AlbumHype.For(extract, 1973));
    }

    /// <summary>A chart placing that is not the top is not a reason to press anything, and must not be
    /// read as one just because the word "number" is in the text.</summary>
    [Fact]
    public void A_middling_chart_placing_is_not_a_claim()
        => Assert.Null(AlbumHype.For("The album reached number 12 on the chart.", 1994));

    // ── the year ──

    /// <summary>A record that does not say when it was made gets the claim alone. Zero is not a date
    /// and is never printed.</summary>
    [Fact]
    public void A_record_with_no_year_gets_the_claim_alone()
        => Assert.Equal(
            "Critically acclaimed album", AlbumHype.For("Released to critical acclaim.", 0));

    /// <summary>The year places a record in time, and is part of what makes the line useful.</summary>
    [Fact]
    public void The_year_is_part_of_the_offer()
        => Assert.Equal(
            "The debut album from 1973", AlbumHype.For("The debut album by the musician.", 1973));

    // ── it stays a glance ──

    /// <summary>It reads as a phrase ("Critically acclaimed album from 1973"), not as a label with a
    /// date added after a comma. Checked over every line the reading can produce, so an entry added
    /// without a noun at the end fails here instead of producing "Grammy winner from 1973".</summary>
    [Fact]
    public void No_line_it_can_produce_carries_a_comma()
    {
        foreach (var extract in Everything)
        {
            var line = AlbumHype.For($"The album, a {extract} record.", 1973);
            if (line is not null)
            {
                Assert.DoesNotContain(",", line, StringComparison.Ordinal);
                Assert.Contains(" from 1973", line, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>One search phrase from each entry the reading can match, so the two sweeps below cover the
    /// whole table rather than whatever was easy to type.</summary>
    private static readonly string[] Everything =
    [
        "critical acclaim", "best-selling", "topped the", "grammy award",
        "mercury prize", "brit award", "landmark", "debut album", "concept album",
        "live album", "soundtrack",
    ];

    /// <summary>Over the length limit the line is dropped rather than trimmed: half a claim is worse than
    /// none, and this sits on one line of a card under a title set at 24pt.</summary>
    [Fact]
    public void Every_line_it_can_produce_fits()
    {
        foreach (var extract in Everything)
        {
            var line = AlbumHype.For($"The album, a {extract} record.", 1973);
            if (line is not null)
                Assert.True(line.Length <= AlbumHype.MostCharacters, $"{line} is {line.Length}");
        }
    }

    // ── a claim has to be about THIS record ──

    /// <summary>
    /// A claim about a song is not a claim about the record.
    ///
    /// <para>The article for Inna's Hot says <c>"Hot" and "Amazing" were both number ones in
    /// Romania</c>. Those are two SINGLES, and the ALBUM must not be called a number one. An
    /// encyclopedia lead describes the record and then its songs, so a sentence that names a song in
    /// quotes is not a sentence about the record.</para>
    /// </summary>
    [Fact]
    public void A_single_that_charted_is_not_the_album_charting()
    {
        var extract = "Hot is the debut studio album by Romanian singer Inna, released on 4 August "
                    + "2009 by Magic Records. \"Hot\" and \"Amazing\" were both number ones in Romania.";

        Assert.Equal("The debut album from 2009", AlbumHype.For(extract, 2009));
    }

    /// <summary>Naming a prize is not winning it, and an encyclopedia names the nomination far more
    /// often than the win. Adele's 19 was shortlisted for the Mercury Prize and must not be called a
    /// Mercury Prize winner.</summary>
    [Fact]
    public void A_nomination_is_not_a_win()
    {
        Assert.Null(AlbumHype.For("The album was shortlisted for the Mercury Prize.", 2008));
        Assert.Null(AlbumHype.For("It was nominated for a Grammy Award for Best Rock Album.", 2008));

        // A win is still read.
        Assert.Equal(
            "Grammy winning album from 2008",
            AlbumHype.For("It won the Grammy Award for Best Rock Album.", 2008));
    }

    /// <summary>A record that MENTIONS a soundtrack is not a soundtrack. Madonna's Like a Prayer is
    /// the example: its article says she had worked on "the soundtrack for the 1987 film Who's That
    /// Girl".</summary>
    [Fact]
    public void A_soundtrack_mentioned_in_passing_is_not_this_record()
    {
        var extract = "She recorded it with the producers she had worked with on the soundtrack for "
                    + "the 1987 film. The record was a critical success.";

        Assert.Equal("Critically acclaimed album from 1989", AlbumHype.For(extract, 1989));
    }

    /// <summary>
    /// An award named in one sentence and a win in another are two facts about two things, so the
    /// claim is read one sentence at a time rather than over the whole extract. Over the whole text
    /// this reads as a Mercury Prize win, which it is not.
    /// </summary>
    [Fact]
    public void A_claim_is_only_worth_the_sentence_it_stands_in()
    {
        var extract = "The album was shortlisted for the Mercury Prize. "
                    + "The band won a local songwriting award that year.";

        Assert.Null(AlbumHype.For(extract, 2008));
    }

    /// <summary>
    /// A known limit of the rule, tested so that it is documented.
    ///
    /// <para>A sentence that names the award and carries a winning word fires, whoever the sentence is
    /// actually ABOUT. "Its producer won a Grammy" reads as the record winning one. Telling those apart
    /// means finding the subject of a sentence, which needs a parser, and a wrong parser fails quietly
    /// by praising the record, which is the one failure that matters here. If this is fixed later,
    /// this test shows what to change.</para>
    /// </summary>
    [Fact]
    public void A_win_by_somebody_else_in_the_same_sentence_is_read_as_the_records()
        => Assert.Equal(
            "Grammy winning album from 2008",
            AlbumHype.For("Its producer won a Grammy the following year for other work.", 2008));

    // ── the accolades that are not prizes ──

    /// <summary>
    /// Accolades that are not prizes or chart places, as in the Beggars Banquet article.
    ///
    /// <para>That article never says acclaimed, number one, best-selling or won. The one sentence
    /// with anything in it reads: "The album has appeared on many lists of the greatest albums of all
    /// time, including by Rolling Stone, and it was inducted into the Grammy Hall of Fame in 1999."
    /// These are two strong and true accolades, and the table must read both.</para>
    /// </summary>
    [Fact]
    public void An_album_on_the_greatest_lists_says_so()
    {
        var extract = "The album has appeared on many lists of the greatest albums of all time, "
                    + "including by Rolling Stone, and it was inducted into the Grammy Hall of Fame "
                    + "in 1999.";

        Assert.Equal("An all-time great album from 1968", AlbumHype.For(extract, 1968));
    }

    /// <summary>A hall of fame induction is NOT a Grammy won, and reading it as one would be false. It
    /// is its own line, below the wins.</summary>
    [Fact]
    public void A_hall_of_fame_induction_is_its_own_claim_and_not_a_win()
        => Assert.Equal(
            "Grammy Hall of Fame album from 1968",
            AlbumHype.For("In 1999 it was inducted into the Grammy Hall of Fame.", 1968));

    /// <summary>Certified, never the word alone: "platinum" also appears in band names and in hair
    /// colours.</summary>
    [Fact]
    public void Platinum_counts_only_where_the_article_says_certified()
    {
        Assert.Equal(
            "A platinum-selling album from 1984",
            AlbumHype.For("The record was certified platinum in eleven countries.", 1984));

        Assert.Null(AlbumHype.For("The band wore platinum wigs on the sleeve.", 1984));
    }

    /// <summary>The Beggars Banquet case, read from the record's real article rather than asserted
    /// about it.</summary>
    [Fact]
    public void Tubular_bells_reads_as_its_article_describes_it()
    {
        var extract = "Tubular Bells is the debut studio album by the English musician Mike Oldfield, "
                    + "released on 25 May 1973. It was a critical and commercial success.";

        Assert.Equal("Critically acclaimed album from 1973", AlbumHype.For(extract, 1973));
    }
}

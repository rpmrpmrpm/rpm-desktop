using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// Reading a lyric sheet as one number.
///
/// <para>The two gates carry most of these tests, because without them the reading is not merely
/// weaker, it is confidently wrong in the direction a list would draw from. See
/// <see cref="LyricValence.LeastEnglishShare"/>, where the measurement is.</para>
///
/// <para>Nothing here decides whether a song is in a list. An unknown reading means the song is
/// ranked by the measurements that exist for all music, which is the normal ranking.</para>
/// </summary>
public class LyricValenceTests
{
    /// <summary>A sheet from lines of words, built through the real parser rather than around it.
    /// Timings are irrelevant to the reading and are spaced a second apart so the sheet is well
    /// formed.</summary>
    private static LyricSheet Sheet(params string[] lines) =>
        LyricSheet.Parse(string.Join('\n', lines.Select((text, i) => $"[{i / 60:00}:{i % 60:00}.00]{text}")));

    /// <summary>Enough plain English scaffolding to pass the language gate, so a test about the
    /// sentiment is not quietly a test about the language.</summary>
    private static LyricSheet Saying(string phrase, int times = 12)
    {
        var lines = new List<string>();
        for (var i = 0; i < times; i++)
        {
            lines.Add($"and it is {phrase} for you and for me");
            lines.Add("that is what they all know about the way we do this");
        }

        return Sheet([.. lines]);
    }

    // ── the reading ──

    [Fact]
    public void A_happy_song_reads_positive_and_a_sad_one_negative()
    {
        var happy = LyricValence.Read(Saying("love and joy and happy"));
        var sad = LyricValence.Read(Saying("pain and grief and lonely"));

        Assert.True(happy.Known);
        Assert.True(sad.Known);
        Assert.True(happy.Valence > 0, $"happy read {happy.Valence}");
        Assert.True(sad.Valence < 0, $"sad read {sad.Valence}");
    }

    [Fact]
    public void Negation_turns_a_word_round()
    {
        var plain = LyricValence.Read(Saying("good and good and good"));
        var negated = LyricValence.Read(Saying("not good and not good and not good"));

        Assert.True(plain.Valence > 0);
        Assert.True(negated.Valence < plain.Valence, $"{negated.Valence} should be under {plain.Valence}");
    }

    /// <summary>A flip and not a mirror: "not bad" is not as good as "good". The lexicon's own
    /// number, kept because undoing it would be inventing one.</summary>
    [Fact]
    public void Negation_takes_some_of_the_force_off_as_well()
    {
        var bad = LyricValence.Read(Saying("bad and bad and bad"));
        var notBad = LyricValence.Read(Saying("not bad and not bad and not bad"));

        Assert.True(notBad.Valence > 0);
        Assert.True(notBad.Valence < Math.Abs(bad.Valence));
    }

    [Fact]
    public void A_booster_makes_a_word_count_for_more()
    {
        var plain = LyricValence.Read(Saying("good and good and good"));
        var boosted = LyricValence.Read(Saying("very good and very good and very good"));

        Assert.True(boosted.Valence > plain.Valence);
    }

    // ── the gates, which are the whole design ──

    /// <summary>
    /// The failure this gate exists for, in the shape it actually took.
    ///
    /// <para>Measured over 1,857 real songs, six of the eight most negative in the collection were
    /// Dutch or French. Foreign words land on English lexicon entries by accident, and a song
    /// matching few of them has an extreme mean, so they collect at exactly the end a Sad list would
    /// draw from. The text below is Dutch and contains "die", "je" and "mee", all of which the
    /// English lexicon has opinions about.</para>
    /// </summary>
    [Fact]
    public void Words_that_are_not_english_are_refused_rather_than_scored()
    {
        var dutch = Sheet([.. Enumerable.Repeat(
            "ik wil je nog een keer zien want die dagen gaan zo snel mee", 12)]);

        Assert.False(LyricValence.Read(dutch).Known);
    }

    /// <summary>A mean over a handful of words is noise, and noise lands at the extremes.</summary>
    [Fact]
    public void Too_few_words_to_average_is_refused()
    {
        var thin = Sheet(
            "and it is love for you and for me",
            "that is what they all know about the way we do this");

        var read = LyricValence.Read(thin);

        Assert.False(read.Known);
        Assert.Equal(0, read.Words);
    }

    [Fact]
    public void The_reading_says_how_many_words_it_was_taken_over()
    {
        var read = LyricValence.Read(Saying("love and joy and happy"));

        Assert.True(read.Words >= LyricValence.LeastWords);
    }

    [Fact]
    public void No_sheet_is_an_unknown_reading()
    {
        Assert.False(LyricValence.Read(null).Known);
    }

    [Fact]
    public void An_empty_sheet_is_an_unknown_reading()
    {
        Assert.False(LyricValence.Read(LyricSheet.Empty).Known);
    }

    /// <summary>Unknown is not nought. Nought is the middle of the scale, and a song nobody has read
    /// must not be placed in the middle of a distribution it was never measured against.</summary>
    [Fact]
    public void An_unknown_reading_is_not_a_neutral_score()
    {
        Assert.False(LyricReading.Unknown.Known);
        Assert.NotEqual(0, LyricReading.Unknown.Valence);
    }

    // ── the table ──

    [Fact]
    public void The_lexicon_reads_back_what_the_generator_wrote()
    {
        Assert.Equal(LyricLexicon.Words, 7209);
        Assert.NotNull(LyricLexicon.Rating("love"));
        Assert.True(LyricLexicon.Rating("love") > 0);
        Assert.True(LyricLexicon.Rating("grief") < 0);
        Assert.Null(LyricLexicon.Rating("the"));
    }

    /// <summary>The tokenizer drops apostrophes, so a lexicon word carrying one could never be
    /// matched. The generator keeps none, and this is what holds it to that after somebody replaces
    /// the lexicon.</summary>
    [Fact]
    public void No_word_in_the_table_carries_an_apostrophe()
    {
        var fields = typeof(LyricLexicon)
            .GetField("Table", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as string;

        Assert.NotNull(fields);
        Assert.DoesNotContain('\'', fields);
    }
}

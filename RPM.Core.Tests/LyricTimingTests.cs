using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

public class LyricTimingTests
{
    // ── the words themselves ──

    [Fact]
    public void The_offsets_index_the_string_that_was_handed_in()
    {
        const string line = "Hello darkness my old friend";
        var spans = LyricTiming.Sweep(line, 0, 4);

        Assert.Equal(5, spans.Count);
        Assert.Equal("Hello", line.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("friend", line.Substring(spans[4].Start, spans[4].Length));
    }

    [Fact]
    public void Punctuation_stays_on_the_word_it_hangs_off()
    {
        // "night," is one thing to read and so one thing to light.
        const string line = "Goodnight, my love.";
        var spans = LyricTiming.Sweep(line, 0, 3);

        Assert.Equal(3, spans.Count);
        Assert.Equal("Goodnight,", line.Substring(spans[0].Start, spans[0].Length));
        Assert.Equal("love.", line.Substring(spans[2].Start, spans[2].Length));
    }

    [Fact]
    public void A_line_with_nothing_to_sing_has_no_words()
    {
        Assert.Empty(LyricTiming.Sweep("   ", 0, 4));
        Assert.Empty(LyricTiming.Sweep("", 0, 4));
        Assert.Empty(LyricTiming.Sweep(null, 0, 4));
    }

    // ── the guess: a line spread across its own words ──

    [Fact]
    public void The_sweep_starts_with_the_line_and_runs_in_order()
    {
        var spans = LyricTiming.Sweep("one two three", 10, 13);

        Assert.Equal(10, spans[0].From, 3);
        for (var i = 1; i < spans.Count; i++)
        {
            Assert.True(spans[i].From >= spans[i - 1].From, "words are sung in the order they are read");
            Assert.True(spans[i].To >= spans[i].From, "no word ends before it starts");
        }
    }

    [Fact]
    public void A_longer_word_is_given_longer()
    {
        // Character count stands in for syllables: "tomorrow" takes longer to sing than "and", and the
        // length of the word is most of why.
        var spans = LyricTiming.Sweep("and tomorrow", 0, 6);

        var shorter = spans[0].To - spans[0].From;
        var longer = spans[1].To - spans[1].From;

        Assert.True(longer > shorter, $"'tomorrow' got {longer:F2}s and 'and' got {shorter:F2}s");
    }

    [Fact]
    public void The_last_word_lands_exactly_on_the_end_however_many_there_are()
    {
        // Shares measured from the start of the line rather than accumulated, so rounding cannot drift
        // the final word off the end of its own line. A gap short enough that the line fills it, so this
        // asks about the arithmetic and not about the cap.
        var spans = LyricTiming.Sweep("a bb ccc dddd e ff ggg h ii jjj", 0, 1.5);

        Assert.Equal(10, spans.Count);
        Assert.Equal(1.5, spans[^1].To, 6);
    }

    [Fact]
    public void A_line_followed_by_a_long_silence_finishes_rather_than_crawling()
    {
        // The failure this is here to stop: an LRC gives no durations, so the only end available is the
        // next line, and between a verse and a chorus that can be half a minute of guitar. Spread across
        // it naively, a short line moves one word every eight seconds and reads as a stall.
        var spans = LyricTiming.Sweep("just a few words", from: 0, until: 120);

        Assert.True(spans[^1].To < 10, $"the sweep ran to {spans[^1].To:F1}s of a two-minute gap");
    }

    [Fact]
    public void The_last_line_of_a_sheet_has_an_end_even_though_nothing_follows_it()
    {
        var spans = LyricTiming.Sweep("the very last line", from: 200, until: double.PositiveInfinity);

        Assert.NotEmpty(spans);
        Assert.True(double.IsFinite(spans[^1].To), "an infinite end would leave the last line never sung");
        Assert.True(spans[^1].To > 200);
    }

    [Fact]
    public void A_dense_line_is_swept_fast_rather_than_being_slowed_to_the_cap()
    {
        // The cap is a ceiling, not a target: a rapped line with sixty characters in two seconds gets
        // two seconds, not ten.
        var spans = LyricTiming.Sweep("rapid fire words all crammed into a very short line", 0, 2);

        Assert.Equal(2, spans[^1].To, 6);
    }

    // ── the file, where it said ──

    [Fact]
    public void Word_stamps_are_used_verbatim_when_the_sheet_carried_them()
    {
        var stamped = new LyricWord[]
        {
            new(10.0, "Hello"),
            new(10.9, "darkness"),
            new(12.4, "friend"),
        };

        var spans = LyricTiming.Sweep("Hello darkness friend", 10, 20, stamped);

        Assert.Equal(10.0, spans[0].From, 3);
        Assert.Equal(10.9, spans[1].From, 3);
        Assert.Equal(12.4, spans[2].From, 3);
        Assert.Equal(10.9, spans[0].To, 3);   // each word runs up to the next one's moment
        Assert.Equal(12.4, spans[1].To, 3);
    }

    [Fact]
    public void A_stamp_list_that_does_not_match_the_text_is_ignored_rather_than_lined_up_by_force()
    {
        // The masking, a differently-parsed line, a sheet whose stamps and words disagree: whatever the
        // cause, a list that does not line up cannot be matched word for word, and pairing them off
        // anyway would light the wrong words with total confidence. Better an honest guess.
        var stamped = new LyricWord[] { new(10.0, "Hello"), new(10.9, "darkness") };

        var spans = LyricTiming.Sweep("Hello darkness my old friend", 10, 14, stamped);

        Assert.Equal(5, spans.Count);
        Assert.Equal(10, spans[0].From, 3);
        Assert.NotEqual(10.9, spans[1].From, 3);   // spread, not taken from the mismatched list
    }

    // ── putting a short measurement back on the line ──

    /// <summary>The spans a forced aligner would hand back for <paramref name="text"/> if it could spell
    /// every word but the ones in <paramref name="cannot"/>, evenly spread, since what is under test is
    /// the matching and not the timing.</summary>
    private static WordSpan[] Measured(string text, params string[] cannot)
    {
        var all = LyricTiming.Sweep(text, 1.0, 9.0);
        var kept = new List<WordSpan>();
        foreach (var span in all)
            if (Array.IndexOf(cannot, text.Substring(span.Start, span.Length)) < 0)
                kept.Add(span with { From = 1.0 + kept.Count, To = 1.0 + kept.Count + 0.5 });
        return [.. kept];
    }

    [Fact]
    public void A_line_the_measurement_covered_whole_is_handed_straight_back()
    {
        const string line = "Hello darkness my old friend";
        var measured = Measured(line);

        var whole = LyricTiming.Whole(measured, line, 1.0, 9.0, 9.0);

        Assert.Same(measured, whole);   // the ordinary line costs nothing at all
    }

    [Fact]
    public void A_word_the_model_has_no_letters_for_keeps_its_place_in_the_line()
    {
        // The bug this exists for: the model's alphabet has no digits, so "12" is not in the answer, and
        // a reader going down the two lists in step puts "or" on 12's timing and runs out a word early.
        const string line = "At half past 12 or so";

        var whole = LyricTiming.Whole(Measured(line, "12"), line, 1.0, 9.0, 9.0);

        Assert.Equal(6, whole.Count);
        var words = new List<string>();
        foreach (var span in whole)
            words.Add(line.Substring(span.Start, span.Length));
        Assert.Equal(new[] { "At", "half", "past", "12", "or", "so" }, words);
    }

    [Fact]
    public void An_unmeasured_word_takes_the_room_between_the_words_either_side_of_it()
    {
        const string line = "At half past 12 or so";

        var whole = LyricTiming.Whole(Measured(line, "12"), line, 1.0, 9.0, 9.0);

        Assert.Equal(whole[2].To, whole[3].From, 3);   // opens where "past" left off
        Assert.Equal(whole[4].From, whole[3].To, 3);   // and closes where "or" begins
    }

    [Fact]
    public void Several_unmeasured_words_in_a_row_share_the_gap_rather_than_each_taking_all_of_it()
    {
        const string line = "Her measurements were 36 25 34 exactly";

        var whole = LyricTiming.Whole(Measured(line, "36", "25", "34"), line, 1.0, 9.0, 9.0);

        Assert.Equal(7, whole.Count);
        for (var i = 3; i <= 5; i++)
        {
            Assert.True(whole[i].To > whole[i].From, "every one of them gets a share");
            Assert.Equal(whole[i].To, whole[i + 1].From, 3);
        }
        Assert.Equal(whole[2].To, whole[3].From, 3);
    }

    [Fact]
    public void The_words_never_run_backwards_however_many_were_dropped()
    {
        // The whole reason an unmeasured word is not simply left with its guessed timing. A guess is
        // spread across the line's own stamps and a measurement is off the record; drop one between two
        // of the other and it can open before its predecessor has closed, which a sweep shows as the
        // highlight jumping back up the line.
        const string line = "I got 4 stories with 21 rooms and 3 doors";

        var whole = LyricTiming.Whole(Measured(line, "4", "21", "3"), line, 1.0, 9.0, 9.0);

        for (var i = 1; i < whole.Count; i++)
        {
            Assert.True(whole[i].From >= whole[i - 1].From, $"word {i} opens before word {i - 1}");
            Assert.True(whole[i].To >= whole[i].From, $"word {i} closes before it opens");
        }
    }

    [Fact]
    public void An_unmeasured_word_at_either_end_is_bounded_by_the_line_and_by_the_wall()
    {
        const string line = "12 lovers later 99";

        var whole = LyricTiming.Whole(Measured(line, "12", "99"), line, 1.0, 9.0, 8.0);

        Assert.Equal(1.0, whole[0].From, 3);    // nothing before it but the line's own moment
        Assert.Equal(8.0, whole[^1].To, 3);     // nothing after it but the end of what was searched
    }

    [Fact]
    public void A_measurement_of_some_other_line_is_refused_rather_than_lined_up_by_force()
    {
        // Nothing matches on Start, so there is no honest way to place these against this text. Handing
        // the caller the measurement it gave is at least an answer it can recognise as not fitting.
        var elsewhere = Measured("Something else entirely spoken here");

        var whole = LyricTiming.Whole(elsewhere, "At half past 12 or so", 1.0, 9.0, 9.0);

        Assert.Same(elsewhere, whole);
    }

    // ── the guessed sweep's held last word ──

    [Fact]
    public void The_sweep_is_never_slower_than_its_cap_and_the_last_word_holds_on()
    {
        // Seven letters before the last word, swept at the cap; the last word then holds on, at most
        // LastHeld past its singing-speed end.
        var spans = LyricTiming.Sweep("seven!! on", 0, 60);

        Assert.Equal(7 / 7.0, spans[0].To, 9);
        Assert.Equal((9 / 7.0) + LyricTiming.LastHeld, spans[^1].To, 9);
    }

    [Fact]
    public void A_held_last_word_ends_just_short_of_the_next_line()
    {
        // "sends it on" is swept by 11/7 s; the next line is at 2.5 s, so "on" holds until a quarter second
        // before it.
        var spans = LyricTiming.Sweep("sends it on", 0, 2.5);

        Assert.Equal(2.5 - LyricTiming.BeforeNext, spans[^1].To, 9);
    }

    [Fact]
    public void The_sweep_shares_a_line_by_length_and_ends_on_the_next_line()
    {
        var spans = LyricTiming.Sweep("aa bbbb", 10, 10.6);

        Assert.Equal(2, spans.Count);
        Assert.Equal(10.0, spans[0].From, 9);
        Assert.Equal(10.2, spans[0].To, 9);
        Assert.Equal(10.6, spans[1].To, 9);
    }
}

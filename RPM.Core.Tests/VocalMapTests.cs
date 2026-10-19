using RPM.Core.Analysis;
using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// Where somebody is singing on one record.
///
/// <para>The cases that decide a mix, in the order they matter. A sparse dance sheet has to read
/// as mostly breakdown or the DJ has nowhere to go. A record nobody has written down must not
/// read as silence, or every untranscribed record becomes the favourite place to lay a vocal.
/// And a record a writer marked as instrumental has to be told apart from one nobody has marked at
/// all.</para>
/// </summary>
public class VocalMapTests
{
    private static LyricSheet Sheet(params string[] lines) => LyricSheet.Parse(string.Join('\n', lines));

    // ── the sparse sheet, the case that matters ──

    [Fact]
    public void A_line_claims_only_as_much_record_as_its_words_could_cover()
    {
        // Two stamps a minute apart with four words between them. The record is not sung over for a
        // minute: it is sung over for a couple of seconds and then it is a breakdown.
        var map = VocalMap.FromSheet(Sheet(
            "[00:30.00] I wanna feel",
            "[01:30.00] I wanna feel it now"));

        Assert.Equal(VocalMap.Told.Sheet, map.Source);

        // "Iwannafeel" is ten letters at five a second, so two seconds, plus the margin either side.
        var (from, to) = map.Sung[0];
        Assert.Equal(30 - VocalMap.Margin, from, 3);
        Assert.Equal(32 + VocalMap.Margin, to, 3);

        // Which leaves the minute between them almost entirely free.
        Assert.Equal(0, map.SungSecondsIn(35, 50), 3);
        Assert.True(map.SingsIn(29, 4));
    }

    [Fact]
    public void The_next_stamp_bounds_a_line_whose_words_would_run_past_it()
    {
        // A long line two seconds before the next one. Its own words would claim eight seconds; it
        // cannot have them, because the sheet says the next line has started.
        var map = VocalMap.FromSheet(Sheet(
            "[00:10.00] this is a considerably longer line than the gap allows for",
            "[00:12.00] and the next one"));

        Assert.Single(map.Sung);                                    // merged: they are a breath apart
        Assert.True(map.Sung[0].To < 20, $"ran to {map.Sung[0].To}");
    }

    [Fact]
    public void A_gap_of_a_breath_is_not_a_place_to_mix()
    {
        // Two lines a bar apart. A DJ cannot bring a record in between them, so the map must not
        // offer the hole.
        var map = VocalMap.FromSheet(Sheet(
            "[00:10.00] first line here",
            "[00:12.00] second line here"));

        Assert.Single(map.Sung);
        Assert.Equal(0, map.SungSecondsIn(0, 9), 1);                // and it has not swallowed the record
    }

    // ── the distinction the whole thing turns on ──

    [Fact]
    public void No_sheet_is_not_the_same_as_no_singing()
    {
        var nothing = VocalMap.FromSheet(null);

        Assert.False(nothing.IsKnown);
        Assert.False(nothing.IsInstrumental);                       // NOT instrumental: nobody has said
        Assert.Equal(VocalMap.Told.Nothing, nothing.Source);
    }

    [Fact]
    public void Unsynced_words_say_nothing_about_when()
    {
        // Plain lyrics with no stamps. There are words, so the record is not instrumental, but nothing
        // here says WHERE, which is the only question being asked.
        var map = VocalMap.FromSheet(LyricSheet.Parse("I wanna feel it now\nAnd again"));

        Assert.False(map.IsKnown);
    }

    [Fact]
    public void A_sheet_of_nothing_but_notes_is_an_instrumental()
    {
        // What a writer does for a record with no words on it. This is a real answer, and the easiest
        // kind of record to mix under.
        var map = VocalMap.FromSheet(Sheet(
            "[00:00.00] ♪",
            "[01:30.00] ♪",
            "[03:00.00] ♪"));

        Assert.True(map.IsKnown);
        Assert.True(map.IsInstrumental);
        Assert.Empty(map.Sung);
        Assert.Equal(0, map.SungSecondsIn(0, 300), 3);
    }

    [Fact]
    public void A_marked_break_between_verses_is_left_open()
    {
        var map = VocalMap.FromSheet(Sheet(
            "[00:10.00] the first verse",
            "[00:20.00] ♪",
            "[01:20.00] the second verse"));

        // Nothing claimed across the minute the writer marked.
        Assert.Equal(0, map.SungSecondsIn(25, 50), 3);
        Assert.Equal(2, map.Sung.Count);
    }

    // ── the bands, for a record nobody wrote down ──

    [Fact]
    public void The_bands_answer_for_a_record_with_no_sheet()
    {
        var map = VocalMap.FromBands(Bands(duration: 240, singsFrom: 60, singsFor: 60));

        Assert.Equal(VocalMap.Told.Bands, map.Source);
        Assert.True(map.SingsIn(70, 10));
        Assert.Equal(0, map.SungSecondsIn(150, 60), 1);
    }

    [Fact]
    public void For_agrees_the_two_readings_and_falls_back_to_whichever_it_has()
    {
        var analysis = Bands(duration: 240, singsFrom: 60, singsFor: 60);

        // Both readings: the changeover is placed by the stretch they agree on. The sheet puts the
        // singing at the end and the bands put it in the middle, so they agree on nothing at all. That
        // is an honest instrumental, because both of them were asked.
        var agreed = VocalMap.For(analysis, Sheet("[03:00.00] sung right at the end"));
        Assert.Equal(VocalMap.Told.Agreed, agreed.Source);
        Assert.Equal(0, agreed.SungSecondsIn(60, 60), 1);

        // A sheet with nothing measured beside it stands on its own.
        var sheeted = VocalMap.For(null, Sheet("[03:00.00] sung right at the end"));
        Assert.Equal(VocalMap.Told.Sheet, sheeted.Source);

        // And a record nobody wrote down is read off its own bands.
        var bare = VocalMap.For(analysis, null);
        Assert.Equal(VocalMap.Told.Bands, bare.Source);
        Assert.True(bare.SingsIn(70, 10));

        var blind = VocalMap.For(null, null);
        Assert.False(blind.IsKnown);
    }

    /// <summary>
    /// The agreed reading is never wider than either of the two it came from. That is the whole
    /// trade: a vocal only one of them heard is one the changeover may now be laid over.
    /// </summary>
    [Fact]
    public void The_agreed_reading_is_never_wider_than_either_of_its_two()
    {
        var analysis = Bands(duration: 240, singsFrom: 60, singsFor: 60);
        var sheet = Sheet("[01:05.00] singing over the middle of this record for a while yet");

        var agreed = VocalMap.For(analysis, sheet);
        var told = VocalMap.FromSheet(sheet);
        var measured = VocalMap.FromBands(analysis);

        Assert.Equal(VocalMap.Told.Agreed, agreed.Source);
        Assert.True(agreed.SungSecondsIn(0, 240) <= told.SungSecondsIn(0, 240));
        Assert.True(agreed.SungSecondsIn(0, 240) <= measured.SungSecondsIn(0, 240));
    }

    [Fact]
    public void An_unread_record_says_nothing_rather_than_silence()
    {
        var unread = new TrackAnalysis
        {
            FilePath = "/library/unread.flac",
            DurationSeconds = 240,
            VocalCurve = [],
        };

        Assert.False(VocalMap.FromBands(unread).IsKnown);
        Assert.False(VocalMap.FromBands(null).IsKnown);
    }

    // ── reading it ──

    [Fact]
    public void Sung_seconds_are_counted_across_several_stretches()
    {
        var map = VocalMap.FromSheet(Sheet(
            "[00:10.00] aaaaaaaaaa",             // ten letters: two seconds
            "[01:00.00] aaaaaaaaaa",
            "[02:00.00] aaaaaaaaaa"));

        var each = 2 + 2 * VocalMap.Margin;
        Assert.Equal(each * 3, map.SungSecondsIn(0, 180), 2);
        Assert.Equal(each, map.SungSecondsIn(0, 55), 2);   // clear of the second, which opens at 59.4
        Assert.Equal(0, map.SungSecondsIn(20, 35), 2);
    }

    [Fact]
    public void A_stretch_asked_about_backwards_or_empty_is_nought()
    {
        var map = VocalMap.FromSheet(Sheet("[00:10.00] singing here"));

        Assert.Equal(0, map.SungSecondsIn(10, 0), 3);
        Assert.Equal(0, map.SungSecondsIn(10, -5), 3);
        Assert.Equal(0, VocalMap.Unknown.SungSecondsIn(0, 600), 3);
    }

    [Fact]
    public void Word_stamps_are_used_where_the_sheet_carries_them()
    {
        // A word-by-word sheet states both ends rather than bounding them, and the last word still
        // needs the same bound the line would have got.
        var map = VocalMap.FromSheet(LyricSheet.Parse("[00:10.00] <00:10.00> hold <00:14.00> on"));

        Assert.Equal(VocalMap.Told.Sheet, map.Source);
        Assert.True(map.SingsIn(13.5, 1), "the last word is where the sheet put it");
        Assert.True(map.Sung[0].To < 16, $"and it does not run on: ended {map.Sung[0].To}");
    }



    [Fact]
    public void A_record_that_only_sings_for_a_quarter_of_itself_is_still_read_right()
    {
        // The case that matters: a house record with one vocal section and three minutes of instrumental
        // either side. Judged against the median, the whole record would read as sung, because the
        // median IS the instrumental.
        var map = VocalMap.FromBands(Bands(duration: 240, singsFrom: 90, singsFor: 60));

        Assert.True(map.SingsIn(100, 10));
        Assert.Equal(0, map.SungSecondsIn(0, 80), 1);
        Assert.Equal(0, map.SungSecondsIn(160, 70), 1);
    }

    [Fact]
    public void A_flat_band_is_called_sung_throughout_rather_than_instrumental()
    {
        // Nothing to tell a wall-to-wall vocal from an instrumental: both are flat. Of the two guesses
        // only one is safe, and it is not the one that makes this the best record to mix under.
        var flat = new TrackAnalysis
        {
            FilePath = "/library/flat.flac",
            DurationSeconds = 120,
            VocalCurve = [.. Enumerable.Repeat(0.4f, (int)(120 / EnergyProfile.StepSeconds))],
        };

        var map = VocalMap.FromBands(flat);

        Assert.True(map.IsKnown);
        Assert.False(map.IsInstrumental);
        Assert.True(map.SingsIn(0, 120));
        Assert.True(map.SingsIn(60, 10));
    }

    // ── where the two readings agree ──

    /// <summary>
    /// The overlap is only what both readings claim, and it is how a drifting sheet is caught.
    ///
    /// <para>A sheet's timings run a second or two out on most records, and the measured band calls a
    /// lead line a voice. Where the two land on the same stretch there is very little room left for
    /// either to be wrong about it. Where only one of them does, one of them is wrong.</para>
    /// </summary>
    [Fact]
    public void Both_keeps_only_the_stretch_the_two_readings_share()
    {
        // The same line, written down two seconds apart by two readings. This is the ordinary case.
        var early = VocalMap.FromSheet(Sheet("[00:10.00] I wanna feel it now and again right here"));
        var late = VocalMap.FromSheet(Sheet("[00:15.00] I wanna feel it now and again right here"));

        var both = VocalMap.Both(early, late);

        // The middle, which both of them claim.
        Assert.True(both.SingsIn(15.5, 1));

        // And neither end, which only one of them does.
        Assert.False(both.SingsIn(10, 3), "the early reading's head is its own claim");
        Assert.False(both.SingsIn(19, 2), "the late reading's tail is its own claim");

        // Never wider than either of the two it came from.
        Assert.True(both.SungSecondsIn(0, 60) < early.SungSecondsIn(0, 60));
        Assert.True(both.SungSecondsIn(0, 60) < late.SungSecondsIn(0, 60));
    }

    [Fact]
    public void A_stretch_only_one_reading_knows_about_is_not_in_the_overlap()
    {
        var sheet = VocalMap.FromSheet(Sheet(
            "[00:10.00] I wanna feel it now and again right here",
            "[00:40.00] and here is a second verse nobody else heard"));
        var band = VocalMap.FromSheet(Sheet("[00:10.00] I wanna feel it now and again right here"));

        var both = VocalMap.Both(sheet, band);

        Assert.True(both.SingsIn(11, 3));
        Assert.False(both.SingsIn(41, 5));
    }

    [Fact]
    public void Both_walks_the_two_lists_rather_than_stopping_at_the_first_overlap()
    {
        // One reading singing straight through against another that finds three separate stretches
        // inside it. Every one of them is an overlap, and a walk that stepped the wrong list would come
        // back with only the first.
        var throughout = VocalMap.FromSheet(Sheet(
            [.. Enumerable.Range(0, 24).Select(i => $"[00:{i * 5:00}.00] singing on and on")]));
        var patchy = VocalMap.FromSheet(Sheet(
            "[00:10.00] here",
            "[00:40.00] and here",
            "[01:10.00] and here too"));

        var both = VocalMap.Both(throughout, patchy);

        Assert.Equal(3, both.Sung.Count);
        Assert.True(both.SingsIn(10, 1));
        Assert.True(both.SingsIn(70, 1));
    }

    /// <summary>
    /// Two readings that share nothing are an honest instrumental: both were asked and neither found
    /// a voice here. A reading that does not exist is not, and must not come back as one. An empty
    /// map reads as "nobody sings on this record", which is the DJ's favourite thing to lay a vocal
    /// over.
    /// </summary>
    [Fact]
    public void An_overlap_needs_two_readings_to_be_an_answer_at_all()
    {
        var one = VocalMap.FromSheet(Sheet("[00:10.00] I wanna feel"));

        Assert.False(VocalMap.Both(one, VocalMap.Unknown).IsKnown);
        Assert.False(VocalMap.Both(VocalMap.Unknown, VocalMap.Unknown).IsKnown);

        var apart = VocalMap.Both(one, VocalMap.FromSheet(Sheet("[01:00.00] I wanna feel")));
        Assert.True(apart.IsKnown);
        Assert.True(apart.IsInstrumental);
    }

    /// <summary>A record whose middle band is busy for one stretch and quiet either side of it.</summary>
    private static TrackAnalysis Bands(double duration, double singsFrom, double singsFor)
    {
        var steps = (int)(duration / EnergyProfile.StepSeconds);
        var voice = new float[steps];
        for (var i = 0; i < steps; i++)
        {
            var at = i * EnergyProfile.StepSeconds;
            voice[i] = at >= singsFrom && at < singsFrom + singsFor ? 0.5f : 0.12f;
        }

        return new TrackAnalysis
        {
            FilePath = "/library/bands.flac",
            DurationSeconds = duration,
            VocalCurve = voice,
        };
    }
}

using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The record read one window at a time, so that a beat's number is not decided by an error
/// measured somewhere else in the record.
///
/// <para>The readings here are built the way a model produces them: times rounded to its twenty
/// millisecond frame, sometimes missing a beat, sometimes over a record that does not hold one
/// tempo.</para>
/// </summary>
public class TempoWindowsTests
{
    private const double Frame = 1.0 / 50;

    /// <summary>A steady record as the model reads one: beats at one tempo, snapped to its frame.</summary>
    private static double[] Steady(double bpm, int beats, double startAt = 0)
    {
        double period = 60.0 / bpm;
        var times = new double[beats];
        for (int i = 0; i < beats; i++)
            times[i] = Math.Round((startAt + i * period) / Frame) * Frame;
        return times;
    }

    /// <summary>A record whose tempo walks evenly from one end to the other, which is what a band or a
    /// slipping turntable does.</summary>
    private static double[] Drifting(double fromBpm, double toBpm, int beats)
    {
        var times = new double[beats];
        double at = 0;
        for (int i = 0; i < beats; i++)
        {
            times[i] = Math.Round(at / Frame) * Frame;
            double bpm = fromBpm + (toBpm - fromBpm) * i / (beats - 1.0);
            at += 60.0 / bpm;
        }
        return times;
    }

    [Fact]
    public void A_steady_record_is_numbered_one_beat_at_a_time()
    {
        var reading = TempoWindows.Read(Steady(128, 400));

        Assert.True(reading.IsUsable);
        Assert.Equal(128, 60.0 / reading.Period, 1);

        // Every beat numbered exactly once, in order, with nothing skipped: the whole grid counts bars
        // by index, so a single doubled or dropped number rotates the bar phase for the rest of the
        // record.
        for (int i = 0; i < 400; i++)
            Assert.Equal(i, reading.Index[i]);
    }

    [Fact]
    public void The_numbering_holds_over_a_long_record_where_a_single_period_would_slip()
    {
        // The failure this guards against. A period taken over the whole track and divided into every
        // beat's distance from the first multiplies its own error by the beat count, so five hundred
        // beats in, it is out by half a beat and the numbering steps. Here each number is decided from
        // one gap against a period measured beside it, so nothing accumulates however long the record is.
        var beats = Steady(126, 900);
        var reading = TempoWindows.Read(beats);

        Assert.Equal(899, reading.Index[^1]);
        Assert.Equal(126, 60.0 / reading.Period, 2);
    }

    [Fact]
    public void A_record_that_holds_its_tempo_reads_the_same_in_every_window()
    {
        var reading = TempoWindows.Read(Steady(128, 600));

        // Under a tenth of a percent apart end to end, which is inside what the frame alone accounts
        // for. This is the reading that lets a straight grid be kept.
        Assert.True(reading.Spread < 0.001, $"windows spread by {reading.Spread:P3} on a sequenced record");
        Assert.True(reading.Anchors.Length > 8, $"only {reading.Anchors.Length} windows over 600 beats");
    }

    [Fact]
    public void A_record_that_drifts_says_so_and_says_where()
    {
        // 124 at the top, 128 at the end: three percent, which no single number describes.
        var reading = TempoWindows.Read(Drifting(124, 128, 600));

        Assert.True(reading.Spread > 0.02, $"a three percent drift read as {reading.Spread:P2}");

        // And the windows are in the order the record played: each one faster than the last.
        for (int i = 1; i < reading.Anchors.Length; i++)
            Assert.True(reading.Anchors[i].Period < reading.Anchors[i - 1].Period,
                $"window {i} at {60 / reading.Anchors[i].Period:F2} did not follow " +
                $"{60 / reading.Anchors[i - 1].Period:F2}");

        // Which is the point: the tempo at the outro, where the mixing happens, is the tempo at the outro
        // and not the average of the record.
        var last = reading.Anchors[^1];
        Assert.Equal(128, 60.0 / last.Period, 0);
    }

    [Fact]
    public void Beats_the_model_missed_leave_a_hole_in_the_numbering_rather_than_shifting_it()
    {
        var whole = Steady(128, 400);
        var gappy = new List<double>();
        for (int i = 0; i < whole.Length; i++)
            if (i % 17 != 5)                       // one beat in seventeen never reported
                gappy.Add(whole[i]);

        var reading = TempoWindows.Read(gappy);

        // The last beat still carries the number it would have had, which is what stops a missing beat
        // from rotating the bar phase for everything after it.
        Assert.Equal(399, reading.Index[^1]);
        Assert.Equal(128, 60.0 / reading.Period, 1);
    }

    [Fact]
    public void A_breakdown_is_numbered_across_and_does_not_get_a_vote_on_the_tempo()
    {
        // Sixteen bars in the middle where the model found nothing. The line across them is only an
        // interpolation: it is right at both ends and says nothing about the middle. So it is still used
        // to place beats, but it is not allowed to say what the record's tempo is.
        var whole = Steady(128, 480);
        var holed = new List<double>();
        for (int i = 0; i < whole.Length; i++)
            if (i is < 200 or >= 264)
                holed.Add(whole[i]);

        var reading = TempoWindows.Read(holed);

        Assert.Equal(479, reading.Index[^1]);
        Assert.Equal(128, 60.0 / reading.Period, 1);
        Assert.Contains(reading.Anchors, a => a.Density < 0.9);
        Assert.Contains(reading.Anchors, a => a.Density > 0.99);
    }

    [Fact]
    public void A_beat_number_names_one_time_and_a_later_one_than_the_number_before_it()
    {
        // What the bend is allowed to cost and no more. Everything downstream counts bars by index, so
        // the map from number to time has to stay one to one and go forwards, straight line or not.
        var reading = TempoWindows.Read(Drifting(124, 128, 600));

        double last = double.MinValue;
        for (double index = -40; index < 640; index += 0.5)
        {
            double at = reading.TimeAt(index);
            Assert.True(at > last, $"beat {index} landed at {at:F4}, not after {last:F4}");
            last = at;
        }
    }

    [Fact]
    public void A_reading_too_short_to_window_is_still_read()
    {
        var reading = TempoWindows.Read(Steady(120, 10));

        Assert.True(reading.IsUsable);
        Assert.Equal(120, 60.0 / reading.Period, 0);
        Assert.Single(reading.Anchors);
    }

    [Fact]
    public void Nothing_to_read_is_not_a_tempo()
    {
        Assert.False(TempoWindows.Read([]).IsUsable);
        Assert.False(TempoWindows.Read([1.0]).IsUsable);
    }

    // ── beats the reader marked twice ──

    /// <summary>A reading where the picker marked some beats a second time, three frames later. This is
    /// the closest pair BeatModelDecoder can emit, and it does so on a fifth of the dance corpus.</summary>
    private static double[] Doubled(double bpm, int beats, int everyNth)
    {
        var times = new List<double>();
        foreach (var (t, i) in Steady(bpm, beats).Select((t, i) => (t, i)))
        {
            times.Add(t);
            if (i % everyNth == 0)
                times.Add(t + 3 * Frame);
        }
        return [.. times];
    }

    [Fact]
    public void A_beat_the_reader_marked_twice_is_not_counted_twice()
    {
        // The fault this guards against. The numbering rounds the step between a pair 60 ms apart to
        // nought, cannot hold a step of nought, and raises it to one. The false beat then becomes a
        // real one and every beat after it is numbered one out. The tempo comes back self-consistent and
        // wrong, which nothing downstream can recover from.
        var reading = TempoWindows.Read(Doubled(128, 400, everyNth: 2));

        Assert.True(reading.IsUsable);
        Assert.Equal(128, 60 / reading.Period, 1);
    }

    [Fact]
    public void Reading_every_beat_twice_is_still_that_record()
    {
        var reading = TempoWindows.Read(Doubled(128, 400, everyNth: 1));

        Assert.True(reading.IsUsable);
        Assert.Equal(128, 60 / reading.Period, 1);
    }

    [Fact]
    public void A_record_that_wanders_is_not_smoothed_into_one_that_does_not()
    {
        // The other half, which is easy to get wrong: a rubato record's shortest gap is around half its
        // typical one. That is nowhere near a doubled pair but well inside a rule written loosely.
        // Merging those would collapse real beats and turn a record this app must refuse to beat-match
        // into one it would try to match.
        var wandering = new List<double> { 0 };
        double at = 0;
        for (int i = 1; i < 200; i++)
        {
            // A beat of 0.667 s pulled about by a fifth of itself, three-beat cycle.
            at += 0.667 + 0.12 * ((i % 3) - 1);
            wandering.Add(Math.Round(at / Frame) * Frame);
        }

        var reading = TempoWindows.Read(wandering);

        Assert.True(reading.IsUsable);
        Assert.Equal(199, reading.Index[^1]);   // every beat kept, none collapsed
    }

    [Fact]
    public void A_missing_beat_is_still_a_missing_beat()
    {
        // Merging must not touch the opposite fault. A gap of two beats numbers as two, and a reader that
        // started collapsing things could start numbering it as one.
        var beats = Steady(128, 400).Where((_, i) => i is not (100 or 101 or 250)).ToArray();

        var reading = TempoWindows.Read(beats);

        Assert.True(reading.IsUsable);
        Assert.Equal(128, 60 / reading.Period, 1);
        Assert.Equal(399, reading.Index[^1]);   // the numbering counts the beats that were not there
    }
}

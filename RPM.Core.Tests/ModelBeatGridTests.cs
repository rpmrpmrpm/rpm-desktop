using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The fitting that turns a model's reading into a beat grid a mix can use.
///
/// <para>The readings here are built the way a model produces them: times quantised to its 20 ms
/// frame, sometimes missing a beat, sometimes drifting. The result must be an evenly spaced grid with
/// the bar lines in the right place, because all later code counts beats by index.</para>
/// </summary>
public class ModelBeatGridTests
{
    private const double Frame = 1.0 / 50;

    /// <summary>A reading as the model gives one: beats at a steady tempo, snapped to its frame
    /// grid, with a bar line every <paramref name="beatsPerBar"/> beats from
    /// <paramref name="firstBar"/>.</summary>
    private static BeatReading Reading(
        double bpm, int beats, double startAt = 0, int beatsPerBar = 4, int firstBar = 0, double wobble = 0)
    {
        double period = 60.0 / bpm;
        var beatTimes = new List<double>();
        var downbeats = new List<double>();
        for (int i = 0; i < beats; i++)
        {
            // Quantised to the model's own frame. This is the jitter the fitting has to remove.
            double drift = wobble * ((i % 3) - 1);
            double at = Math.Round((startAt + i * period + drift) / Frame) * Frame;
            beatTimes.Add(at);
            if (i >= firstBar && (i - firstBar) % beatsPerBar == 0)
                downbeats.Add(at);
        }
        return new BeatReading { BeatTimes = beatTimes, DownbeatTimes = downbeats };
    }

    [Fact]
    public void Reads_tempo_back_off_a_quantised_grid()
    {
        var result = ModelBeatGrid.Fit(Reading(128, 200), durationSeconds: 100);

        Assert.True(result.IsUsable);
        // A single gap between two frame-snapped beats is only accurate to ±4%; fitted across two
        // hundred of them the tempo comes back to within a tenth of a BPM.
        Assert.Equal(128, result.Bpm, 1);
        Assert.Equal(4, result.BeatsPerBar);
        Assert.True(result.BpmConfidence > 0.7, $"confidence {result.BpmConfidence}");
    }

    [Fact]
    public void Grid_is_even_to_the_microsecond()
    {
        var result = ModelBeatGrid.Fit(Reading(128, 200), durationSeconds: 100);

        var gaps = result.BeatTimes.Zip(result.BeatTimes.Skip(1), (a, b) => b - a).ToArray();
        // The main purpose: the model's beats are accurate to 20 ms at best, the fitted grid is
        // exact. A beat-match allows 15 ms of drift over a whole changeover.
        Assert.All(gaps, gap => Assert.Equal(gaps[0], gap, 9));
    }

    [Fact]
    public void Grid_covers_the_whole_record()
    {
        // The model reports beats where it hears them. A record that fades in from silence has no
        // beats to hear at the start, and that is exactly where a changeover needs a bar line.
        var result = ModelBeatGrid.Fit(Reading(120, 100, startAt: 30), durationSeconds: 120);

        Assert.True(result.BeatTimes[0] < 0.6, $"grid starts at {result.BeatTimes[0]}");
        Assert.True(result.BeatTimes[^1] > 119.4, $"grid ends at {result.BeatTimes[^1]}");
        Assert.All(result.BeatTimes, at => Assert.InRange(at, 0, 120));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    public void Counts_the_bar_the_way_the_record_does(int beatsPerBar)
    {
        // The DSP assumes four. A waltz mixed as if it were in four has every fade a third too
        // long and every bar line on the wrong beat.
        var result = ModelBeatGrid.Fit(Reading(120, 240, beatsPerBar: beatsPerBar), durationSeconds: 120);

        Assert.Equal(beatsPerBar, result.BeatsPerBar);
        Assert.Equal(1, result.DownbeatConfidence, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Finds_which_beat_of_the_bar_is_the_one(int firstBar)
    {
        var result = ModelBeatGrid.Fit(Reading(120, 200, firstBar: firstBar), durationSeconds: 100);

        // The grid starts at the record's first beat here, so the grid should keep the phase the
        // model reported.
        Assert.Equal(firstBar % 4, result.FirstDownbeat);
        Assert.Equal(1, result.DownbeatConfidence, 3);
    }

    [Fact]
    public void A_missed_beat_does_not_rotate_the_bar_phase()
    {
        // The failure this prevents. A model that misses one beat halfway through leaves a hole; if
        // the fit renumbered everything after it, every later bar line would be one beat out and
        // every changeover would miss the one.
        var clean = Reading(120, 200);
        var beats = clean.BeatTimes.ToList();
        var missing = beats.Where((_, i) => i != 100).ToList();
        var gappy = new BeatReading { BeatTimes = missing, DownbeatTimes = clean.DownbeatTimes };

        var whole = ModelBeatGrid.Fit(clean, 100);
        var holed = ModelBeatGrid.Fit(gappy, 100);

        Assert.Equal(whole.Bpm, holed.Bpm, 1);
        Assert.Equal(whole.FirstDownbeat, holed.FirstDownbeat);
        Assert.Equal(whole.BeatsPerBar, holed.BeatsPerBar);
        Assert.Equal(whole.BeatTimes[0], holed.BeatTimes[0], 3);
    }

    [Fact]
    public void A_missed_bar_line_does_not_make_the_bar_longer()
    {
        var clean = Reading(120, 200);
        var fewer = clean.DownbeatTimes.Where((_, i) => i != 10).ToList();

        var result = ModelBeatGrid.Fit(
            new BeatReading { BeatTimes = clean.BeatTimes, DownbeatTimes = fewer }, 100);

        // One gap of eight beats among forty-nine of four: the most common gap is still four. An
        // average would give 4.08, which does not round to anything useful.
        Assert.Equal(4, result.BeatsPerBar);
        Assert.True(result.DownbeatConfidence > 0.9);
    }

    [Fact]
    public void A_record_that_does_not_hold_a_tempo_says_so()
    {
        // Beats spread around the tempo instead of sitting on it, like a rubato ballad. It can still
        // be played and faded but must not be beat-matched.
        var rubato = Reading(90, 120, wobble: 0.12);
        var result = ModelBeatGrid.Fit(rubato, durationSeconds: 90);

        Assert.True(result.BpmConfidence < TrackAnalysis.MinBeatMatchConfidence,
            $"confidence {result.BpmConfidence} would let this be beat-matched");
    }

    [Fact]
    public void Not_enough_beats_is_not_a_grid()
    {
        var result = ModelBeatGrid.Fit(Reading(120, 4), durationSeconds: 10);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public void No_bar_lines_leaves_a_grid_and_an_honest_zero()
    {
        var beats = Reading(120, 100);
        var result = ModelBeatGrid.Fit(
            new BeatReading { BeatTimes = beats.BeatTimes, DownbeatTimes = [] }, 60);

        // Still a usable grid (a record can be beat-matched without knowing where its bar starts),
        // but the confidence must not claim that the bar is known.
        Assert.True(result.IsUsable);
        Assert.Equal(4, result.BeatsPerBar);
        Assert.Equal(0, result.DownbeatConfidence);
    }

    [Fact]
    public void A_grid_fits_straight_into_an_analysis()
    {
        // What the analyser does with the result, checked end to end: the bar arithmetic the mix
        // uses must agree with the grid it was given.
        var result = ModelBeatGrid.Fit(Reading(120, 240, beatsPerBar: 3), durationSeconds: 120);
        var analysis = new TrackAnalysis
        {
            FilePath = "x.flac",
            DurationSeconds = 120,
            Bpm = result.Bpm,
            BpmConfidence = result.BpmConfidence,
            BeatTimes = result.BeatTimes,
            FirstDownbeat = result.FirstDownbeat,
            BeatsPerBar = result.BeatsPerBar,
        };

        Assert.True(analysis.CanBeatMatch);
        Assert.Equal(1.5, analysis.BarSeconds, 2);   // three beats at 120 BPM

        double bar = analysis.NextDownbeatAfter(10) ?? -1;
        double next = analysis.NextDownbeatAfter(bar) ?? -1;
        Assert.Equal(analysis.BarSeconds, next - bar, 2);
    }

    // ── read against the DSP's grid, not instead of it ──

    /// <summary>A regularised DSP grid, which is always a straight line (see
    /// <see cref="BeatTracker.Regularize"/>).</summary>
    private static double[] DspGrid(double bpm, double durationSeconds, double phase = 0)
    {
        double period = 60.0 / bpm;
        var beats = new List<double>();
        for (double at = phase; at < durationSeconds; at += period)
            beats.Add(at);
        return [.. beats];
    }

    private static double Spacing(double[] grid) => (grid[^1] - grid[0]) / (grid.Length - 1);

    /// <summary>How far apart two grids of the same period are, in seconds, ignoring which beat each
    /// one starts on. Comparing only the first beats is a trap: a line whose phase crosses zero starts
    /// a whole beat later while having hardly moved.</summary>
    private static double PhaseGap(double a, double b, double period)
    {
        double beats = (a - b) / period;
        return Math.Abs(beats - Math.Round(beats)) * period;
    }

    [Theory]
    [InlineData(160, 80, 80)]      // the DSP counted every offbeat; the model reads half that
    [InlineData(80, 160, 160)]     // and the opposite case
    public void The_model_settles_which_octave_the_record_is_counted_in(
        double dspBpm, double modelBpm, double expected)
    {
        // Both readings are inside the window, so folding alone cannot separate them. This is the
        // case the DSP's prior gets wrong, and the only one the model can fix.
        var result = ModelBeatGrid.Against(
            Reading(modelBpm, (int)(modelBpm * 2)), DspGrid(dspBpm, 120), 120);

        Assert.True(result.IsUsable);
        Assert.Equal(expected, result.Bpm, 1);
        Assert.Equal(60.0 / expected, Spacing(result.BeatTimes), 3);
    }

    [Fact]
    public void A_dsp_counting_twice_as_fast_does_not_pull_a_slow_reading_up()
    {
        // The tempo is the model's, and the record is stored at the octave the model read. The DSP
        // at 128 does not get a vote on counting: it only places the beats it agrees with, and each
        // of those lands on one of the model's beats.
        var result = ModelBeatGrid.Against(Reading(64, 128), DspGrid(128, 120), 120);

        Assert.Equal(64, result.Bpm, 1);
    }

    [Fact]
    public void Thinning_the_grid_keeps_the_beats_the_model_actually_heard()
    {
        // The DSP is at 160, the model at 80, so half the DSP's beats are kept. Which half matters:
        // keep the wrong alternate beats and every bar line lands half a bar out. The model's beats
        // start on the DSP's *second* beat, so that is the set to keep.
        double dspPeriod = 60.0 / 160;
        var reading = Reading(80, 120, startAt: dspPeriod);

        var result = ModelBeatGrid.Against(reading, DspGrid(160, 120), 120);

        Assert.Equal(80, result.Bpm, 1);
        foreach (double heard in reading.BeatTimes)
        {
            double beats = (heard - result.BeatTimes[0]) / (60.0 / 80);
            double off = Math.Abs(beats - Math.Round(beats)) * (60.0 / 80);
            Assert.True(off < 0.03, $"the model heard a beat at {heard:F3}s, {off * 1000:F0} ms off the grid");
        }
    }

    [Fact]
    public void A_record_read_faster_than_the_window_is_halved_into_it()
    {
        // The top of the window is the library's convention and still applies. A record read at 208
        // is stored as 104, the tempo the rest of the library is compared against.
        var result = ModelBeatGrid.Against(Reading(208, 400), [], 120);

        Assert.True(result.IsUsable);
        Assert.InRange(result.Bpm, TempoEstimator.MinBpm, TempoEstimator.MaxBpm);
        Assert.Equal(104, result.Bpm, 1);
    }

    [Fact]
    public void A_slow_record_is_stored_slow()
    {
        // There is no bottom to the window. A record read at 64 is a ballad that runs at 64; doubling
        // it would say the record is twice as fast as it is. On 1697 annotated tracks, doubling
        // changed 49 records, corrected none of them and broke 37.
        var result = ModelBeatGrid.Against(Reading(64, 128), [], 120);

        Assert.True(result.IsUsable);
        Assert.Equal(64, result.Bpm, 1);
        Assert.Equal(60.0 / 64, Spacing(result.BeatTimes), 4);
    }

    // ── the model sets the tempo; the DSP only shifts the line ──

    [Theory]
    [InlineData(92.913, 139.360)]   // E-Type, "Campione 2000": a 3:2 case
    [InlineData(92.741, 141.625)]   // Cygnus X, "Superstring": another 3:2 case
    [InlineData(70.0, 105.0)]       // the same 3:2 well inside the window, both ways
    [InlineData(105.0, 70.0)]
    public void A_dsp_reading_that_is_not_an_octave_out_does_not_override_the_model(
        double dspBpm, double modelBpm)
    {
        // Onset autocorrelation can lock onto the dotted quarter and read two thirds of the true
        // tempo. No power of two reaches from there to the model's answer, so the fit must not try
        // to match the two by doubling or halving. If it did, it would get a tempo neither reading
        // named, fold it back to the DSP's, and store the record two thirds slow while marking it as
        // read by the model.
        var result = ModelBeatGrid.Against(
            Reading(modelBpm, (int)(modelBpm * 2)), DspGrid(dspBpm, 120), 120);

        Assert.True(result.IsUsable);
        Assert.Equal(TempoEstimator.FoldDown(modelBpm), result.Bpm, 1);
        Assert.Equal(60.0 / TempoEstimator.FoldDown(modelBpm), Spacing(result.BeatTimes), 4);
    }

    [Fact]
    public void The_dsp_never_moves_the_period_even_when_the_two_all_but_agree()
    {
        // A DSP half a percent off the model is the tempting case: it looks close enough to keep,
        // but half a percent is 78 ms of drift over eight bars. The period is always the model's, so
        // there is no threshold here for a record to fall either side of.
        var result = ModelBeatGrid.Against(Reading(128, 240), DspGrid(128.64, 120), 120);

        Assert.Equal(60.0 / 128, Spacing(result.BeatTimes), 4);
    }

    [Fact]
    public void The_dsp_slides_the_model_line_onto_the_onsets()
    {
        // What the DSP is still used for. The model reports on a 20 ms frame and can have every beat
        // of a record consistently early; the DSP places its beats on the onsets themselves, so the
        // line moves onto them.
        var dsp = DspGrid(128, 120, phase: 0.017);

        var result = ModelBeatGrid.Against(Reading(128, 240), dsp, 120);

        Assert.Equal(128, result.Bpm, 1);
        Assert.Equal(dsp[0], result.BeatTimes[0], 3);
    }

    [Fact]
    public void A_dsp_at_a_tempo_of_its_own_gets_no_more_than_the_snap_budget()
    {
        // At two thirds of the model's tempo, every other DSP beat still lands on a real onset and
        // the ones between are half a beat away. So the DSP still votes (the near beats are real
        // placements), but the half-beat misses must be filtered out, and what is left may only move
        // the line by milliseconds. The tempo never changes.
        var reading = Reading(139.36, 400);
        var model = ModelBeatGrid.Fit(reading, 120);

        var result = ModelBeatGrid.Against(reading, DspGrid(92.913, 120), 120);

        Assert.Equal(model.Bpm, result.Bpm, 6);

        double moved = PhaseGap(result.BeatTimes[0], model.BeatTimes[0], 60.0 / result.Bpm);
        Assert.True(moved <= 0.04, $"the grid moved {moved * 1000:F1} ms on a DSP line that crosses it");
    }

    [Fact]
    public void The_line_is_slid_by_milliseconds_and_never_by_a_beat()
    {
        // The limit that makes the shift safe. A DSP grid a third of a beat away from the model's is
        // not the same beat read less accurately, and following it would undo the tempo.
        double period = 60.0 / 128;
        var reading = Reading(128, 240);
        var model = ModelBeatGrid.Fit(reading, 120);

        var result = ModelBeatGrid.Against(reading, DspGrid(128, 120, phase: period / 3), 120);

        double moved = PhaseGap(result.BeatTimes[0], model.BeatTimes[0], period);
        Assert.True(moved < 0.041, $"the grid moved {moved * 1000:F0} ms");
    }

    [Fact]
    public void A_tempo_the_model_is_unsure_of_is_not_reported_as_certain()
    {
        // The stored confidence must be the model's, because the tempo is the model's. A DSP
        // confidence of 1.00 on a record the DSP read two thirds slow would hide the error.
        var rubato = Reading(90, 240, wobble: 0.12);

        var result = ModelBeatGrid.Against(rubato, DspGrid(90, 160), 160);

        Assert.Equal(ModelBeatGrid.Fit(rubato, 160).BpmConfidence, result.BpmConfidence, 6);
        Assert.True(result.BpmConfidence < TrackAnalysis.MinBeatMatchConfidence,
            $"confidence {result.BpmConfidence} would let this be beat-matched");
    }

    [Fact]
    public void A_steady_record_still_comes_back_beat_matchable()
    {
        // The other side: using the model's confidence must not silently lower every record. The
        // model's beats are frame-snapped, so the spread is never zero.
        var result = ModelBeatGrid.Against(Reading(128, 240), DspGrid(128, 120), 120);

        Assert.True(result.BpmConfidence > TrackAnalysis.MinBeatMatchConfidence,
            $"confidence {result.BpmConfidence}");
    }

    [Fact]
    public void The_grid_holds_the_tempo_across_a_whole_record()
    {
        // Why the model's frame does not matter once a line is fitted through hundreds of its beats:
        // a changeover is limited by drift, and TransitionPlanner allows 15 ms of it over a whole
        // changeover.
        var result = ModelBeatGrid.Against(Reading(128, 512), DspGrid(128, 240), 240);

        double truePeriod = 60.0 / 128;
        double drift = Math.Abs((result.BeatTimes[^1] - result.BeatTimes[0])
                                - truePeriod * (result.BeatTimes.Length - 1));
        Assert.True(drift < 0.015, $"{drift * 1000:F1} ms of drift across the record");
    }

    [Fact]
    public void Bar_lines_that_do_not_agree_with_each_other_are_not_named()
    {
        // Downbeats spread across two phases of the bar: the model did not find a bar here. Saying
        // so leaves the harmony reading in place; naming a bar would set every fade in the mix.
        double period = 60.0 / 128;
        var beats = new List<double>();
        for (int i = 0; i < 240; i++)
            beats.Add(Math.Round(i * period / Frame) * Frame);
        var scattered = new BeatReading
        {
            BeatTimes = beats,
            // Gaps of 4 and 5 alternating: the most common is 4, and in a four-beat bar these land
            // on three different beats, so no phase is supported by the reading.
            DownbeatTimes =
            [
                beats[0], beats[4], beats[8], beats[13],
                beats[17], beats[21], beats[26], beats[30],
            ],
        };

        var result = ModelBeatGrid.Against(scattered, DspGrid(128, 120), 120);

        Assert.True(result.IsUsable);
        Assert.Equal(0, result.DownbeatConfidence);
        Assert.Equal(4, result.BeatsPerBar);
    }
}

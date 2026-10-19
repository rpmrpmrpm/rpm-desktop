using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// A grid over a record that does not hold one tempo, and the local reading a changeover is planned
/// against.
///
/// <para>A straight line is right in the middle of a drifting record and wrong at both ends, and
/// both ends are where the mixing happens. These tests check that the grid follows the record where
/// the drift is larger than a changeover's whole drift allowance, that it stays one straight line
/// where it is not, and that later code can tell the two apart only by reading the beats.</para>
/// </summary>
public class DriftingGridTests
{
    private const double Frame = 1.0 / 50;

    /// <summary>A record whose tempo changes evenly from one end to the other, as the model would
    /// report it: on its own frame grid, with a bar line every fourth beat.</summary>
    private static (BeatReading Reading, double[] True) Drifting(double fromBpm, double toBpm, int beats)
    {
        var truth = new double[beats];
        var seen = new double[beats];
        var bars = new List<double>();

        double at = 0;
        for (int i = 0; i < beats; i++)
        {
            truth[i] = at;
            seen[i] = Math.Round(at / Frame) * Frame;
            if (i % 4 == 0)
                bars.Add(seen[i]);

            double bpm = fromBpm + (toBpm - fromBpm) * i / (beats - 1.0);
            at += 60.0 / bpm;
        }

        return (new BeatReading { BeatTimes = seen, DownbeatTimes = bars }, truth);
    }

    private static BeatReading Steady(double bpm, int beats)
    {
        double period = 60.0 / bpm;
        var times = new List<double>();
        var bars = new List<double>();
        for (int i = 0; i < beats; i++)
        {
            double at = Math.Round(i * period / Frame) * Frame;
            times.Add(at);
            if (i % 4 == 0)
                bars.Add(at);
        }
        return new BeatReading { BeatTimes = times, DownbeatTimes = bars };
    }

    /// <summary>The largest distance between a grid beat and the record's real beat.</summary>
    private static double Worst(double[] grid, double[] truth)
    {
        double worst = 0;
        foreach (double beat in truth)
        {
            double near = double.MaxValue;
            foreach (double line in grid)
                near = Math.Min(near, Math.Abs(line - beat));
            worst = Math.Max(worst, near);
        }
        return worst;
    }

    [Fact]
    public void A_record_that_drifts_gets_a_grid_that_drifts_with_it()
    {
        // 124 at the start and 128 at the end: three percent over six hundred beats, which is about
        // five minutes. No straight line can be within a beat of both ends of that.
        var (reading, truth) = Drifting(124, 128, 600);

        var result = ModelBeatGrid.Fit(reading, truth[^1] + 1);

        Assert.True(result.IsUsable);

        // The grid is on the record's beats all the way through, at every point a changeover could
        // use, measured at worst and not on average.
        double worst = Worst(result.BeatTimes, truth);
        Assert.True(worst < 0.02, $"the grid is {worst * 1000:F0} ms off the record at its worst");

        // A straight line is not, by a margin much larger than rounding: fitted through the same beats,
        // it is off the record by most of a beat.
        double period = (truth[^1] - truth[0]) / (truth.Length - 1);
        var straight = new double[truth.Length];
        for (int i = 0; i < truth.Length; i++)
            straight[i] = truth[0] + i * period;

        double straightWorst = Worst(straight, truth);
        Assert.True(straightWorst > 20 * worst,
            $"a straight line was only {straightWorst * 1000:F0} ms off, against the bent grid's " +
            $"{worst * 1000:F0} ms — this record does not drift enough to be testing anything");
    }

    [Fact]
    public void A_record_sequenced_to_a_click_keeps_its_straight_line()
    {
        // The other half, and the more important one, because it covers nearly every record. Two
        // machines round an inaudible bend differently, and the pool sees agreement only when it sees
        // the same text, so a grid that can be straight must come back straight.
        var result = ModelBeatGrid.Fit(Steady(128, 600), 300);

        Assert.True(result.IsUsable);

        double first = result.BeatTimes[1] - result.BeatTimes[0];
        for (int i = 1; i < result.BeatTimes.Length; i++)
            Assert.Equal(first, result.BeatTimes[i] - result.BeatTimes[i - 1], 9);

        // And the tempo in the table is exactly that line's tempo, because the planner computes its
        // pull from the tempo and places the drop on a beat from the grid.
        Assert.Equal(result.Bpm, 60.0 / first, 6);
    }

    /// <summary>Whether a grid is one line or several: the difference between the largest and the
    /// smallest beat spacing, as a fraction of a beat.</summary>
    private static double Bend(double[] grid)
    {
        double least = double.MaxValue, most = 0;
        for (int i = 1; i < grid.Length; i++)
        {
            double gap = grid[i] - grid[i - 1];
            least = Math.Min(least, gap);
            most = Math.Max(most, gap);
        }
        return (most - least) / least;
    }

    [Fact]
    public void A_drift_a_changeover_would_feel_is_kept()
    {
        // A quarter of a percent over four minutes. Small enough to be inaudible as a tempo and large
        // enough to matter as a position. A straight line gets this record wrong silently, because it
        // is a position error and not a tempo error. Over sixteen bars it is nothing; extended from the
        // first beat to the outro it is a tenth of a second, and the outro is where the mixing happens.
        var (reading, truth) = Drifting(126, 126.32, 500);
        var result = ModelBeatGrid.Fit(reading, truth[^1] + 1);

        Assert.True(Bend(result.BeatTimes) > 1e-6, "a drift worth a tenth of a second was straightened away");

        double worst = Worst(result.BeatTimes, truth);
        Assert.True(worst < TransitionPlanner.MaxDriftSeconds,
            $"the grid is {worst * 1000:F0} ms off, past the {TransitionPlanner.MaxDriftSeconds * 1000:F0} ms " +
            "a whole changeover is allowed");
    }

    [Fact]
    public void A_drift_nobody_could_hear_is_straightened_away()
    {
        // The same record with a twentieth of the drift: two hundredths of a percent, which a straight
        // line covers well inside a changeover's allowance. Keeping the bend here gives nothing and
        // costs the pool a fact: two machines that round the same inaudible variation differently
        // write different text, and the store reads different text as two people disagreeing.
        var (reading, truth) = Drifting(126, 126.025, 500);
        var result = ModelBeatGrid.Fit(reading, truth[^1] + 1);

        double first = result.BeatTimes[1] - result.BeatTimes[0];
        for (int i = 1; i < result.BeatTimes.Length; i++)
            Assert.Equal(first, result.BeatTimes[i] - result.BeatTimes[i - 1], 9);
    }

    [Fact]
    public void The_bar_line_still_lands_on_the_one_when_the_grid_bends()
    {
        // The property a straight line guarantees, which the bend must keep. Every fade in a mix is
        // measured in bars.
        var (reading, _) = Drifting(124, 128, 600);
        var result = ModelBeatGrid.Fit(reading, 300);

        Assert.Equal(4, result.BeatsPerBar);
        Assert.True(result.DownbeatConfidence > 0.9,
            $"the bar was read at {result.DownbeatConfidence:P0} across a bend");

        // And the grid's own bar lines are the model's, all the way to the end of the record, not only
        // for the first few bars before the numbering drifts.
        var grid = result.BeatTimes;
        foreach (double bar in reading.DownbeatTimes)
        {
            int at = 0;
            double near = double.MaxValue;
            for (int k = 0; k < grid.Length; k++)
                if (Math.Abs(grid[k] - bar) < near)
                {
                    near = Math.Abs(grid[k] - bar);
                    at = k;
                }

            Assert.Equal(result.FirstDownbeat % 4, at % 4);
        }
    }

    [Fact]
    public void The_tempo_at_the_mix_point_is_the_tempo_there_and_not_the_average()
    {
        var (reading, truth) = Drifting(124, 128, 600);
        var result = ModelBeatGrid.Fit(reading, truth[^1] + 1);

        var analysis = new TrackAnalysis
        {
            FilePath = "/library/drifts.wav",
            DurationSeconds = truth[^1] + 1,
            Bpm = result.Bpm,
            BpmConfidence = result.BpmConfidence,
            BeatTimes = result.BeatTimes,
            FirstDownbeat = result.FirstDownbeat,
            BeatsPerBar = result.BeatsPerBar,
        };

        // Read where the record comes in and where it goes out: the two points a changeover uses,
        // and the two that are furthest from the whole-track number.
        Assert.Equal(124, analysis.BpmAt(truth[8]), 0);
        Assert.Equal(128, analysis.BpmAt(truth[^8]), 0);

        // And the one number in the table is between them, which is why it is the wrong one to plan a
        // changeover with.
        Assert.InRange(analysis.Bpm, 125, 127);
    }

    [Fact]
    public void On_a_straight_grid_the_local_tempo_is_the_tempo()
    {
        // Nearly every record. Nothing about the local reading may move a plan on one of these.
        var result = ModelBeatGrid.Fit(Steady(128, 600), 300);
        var analysis = new TrackAnalysis
        {
            FilePath = "/library/click.wav",
            DurationSeconds = 300,
            Bpm = result.Bpm,
            BeatTimes = result.BeatTimes,
            FirstDownbeat = result.FirstDownbeat,
            BeatsPerBar = result.BeatsPerBar,
        };

        foreach (double at in (double[])[0, 1, 30, 120, 200, 280, 299])
            Assert.Equal(analysis.Bpm, analysis.BpmAt(at), 6);
    }

    [Fact]
    public void A_record_the_bent_grid_follows_exactly_is_read_as_steady()
    {
        // Steadiness is the difference between where the beats were heard and where the grid puts
        // them. Measured by dividing by one period, it would report a drifting record as the least
        // steady record in the library however well the grid follows it. That is wrong, and
        // TrackAnalysis.IsSteadyEnoughToHold shortens every fade on a record that fails it.
        var (reading, truth) = Drifting(124, 128, 600);
        var result = ModelBeatGrid.Fit(reading, truth[^1] + 1);

        double steadiness = TrackShape.TempoStability(truth, result.BeatTimes);

        Assert.True(steadiness > 0.4,
            $"a record its own grid follows to the millisecond read as {steadiness:F3} steady");

        var analysis = new TrackAnalysis
        {
            FilePath = "/library/drifts.wav",
            DurationSeconds = truth[^1] + 1,
            Bpm = result.Bpm,
            BeatTimes = result.BeatTimes,
            TempoStability = steadiness,
        };
        Assert.True(analysis.IsSteadyEnoughToHold);
    }

    [Fact]
    public void A_record_with_no_grid_answers_its_own_tempo_everywhere()
    {
        var analysis = new TrackAnalysis { FilePath = "/library/speech.wav", DurationSeconds = 60 };

        Assert.Equal(0, analysis.BpmAt(30));
        Assert.Equal(0, analysis.BpmAt(0));
    }
}

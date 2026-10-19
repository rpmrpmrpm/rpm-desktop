using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// A changeover into and out of a record that does not keep one tempo, in both ways a set uses: the
/// mix at the end of a record, and the next key pressed part way through one.
///
/// <para>These tests are about the tempo at which the two records are matched. A record read once
/// as a whole reports the average of its drift, and the record is not at the average tempo at
/// either point a changeover uses. The mix would then be planned to a number that is right only in
/// the middle of the track, where nobody mixes.</para>
/// </summary>
public class DriftingMixTests
{
    /// <summary>A record whose tempo changes evenly from one end to the other, with the grid that
    /// follows it, as ModelBeatGrid would build it.</summary>
    private static TrackAnalysis Drifting(string path, double fromBpm, double toBpm, int beats)
    {
        const double frame = 1.0 / 50;
        var seen = new List<double>();
        var bars = new List<double>();

        double at = 0;
        for (int i = 0; i < beats; i++)
        {
            double stamped = Math.Round(at / frame) * frame;
            seen.Add(stamped);
            if (i % 4 == 0)
                bars.Add(stamped);

            at += 60.0 / (fromBpm + (toBpm - fromBpm) * i / (beats - 1.0));
        }

        var fitted = ModelBeatGrid.Fit(
            new BeatReading { BeatTimes = seen, DownbeatTimes = bars }, at + 1);

        return new TrackAnalysis
        {
            FilePath = path,
            DurationSeconds = at + 1,
            Bpm = fitted.Bpm,
            BpmConfidence = fitted.BpmConfidence,
            BeatTimes = fitted.BeatTimes,
            FirstDownbeat = fitted.FirstDownbeat,
            BeatsPerBar = fitted.BeatsPerBar,
            DownbeatConfidence = fitted.DownbeatConfidence,
            IntroEndSeconds = 8,
            OutroStartSeconds = at - 30,
            AudioStartSeconds = 0,
            AudioEndSeconds = at,
        };
    }

    private static MixTrack Track(TrackAnalysis analysis) => new()
    {
        Source = analysis.FilePath,
        Path = analysis.FilePath,
        Title = analysis.FilePath,
        TaggedDuration = analysis.DurationSeconds,
        Analysis = analysis,
    };

    [Fact]
    public void The_mix_at_the_end_of_a_record_meets_it_at_the_tempo_it_is_playing_there()
    {
        // 124 at the start, 128 at the outro. The mix happens at the outro, so the two records must
        // meet around 128, not the 126 in the table.
        var from = Drifting("/library/out.wav", 124, 128, 600);
        var to = Drifting("/library/in.wav", 128, 128, 600);

        var plan = TransitionPlanner.Plan(Track(from), from.Bpm, Track(to), null);

        Assert.True(plan.Style is TransitionStyle.BeatMatched,
            $"planned a {plan.Style} on two records that hold their grids");

        double outgoingHere = from.BpmAt(plan.StartAtSeconds);
        Assert.True(Math.Abs(outgoingHere - 128) < 0.5,
            $"the record is doing {outgoingHere:F2} where the mix starts, not 128");

        // The plan meets that tempo, not the table's average.
        Assert.True(Math.Abs(plan.MixBpm - outgoingHere) < Math.Abs(plan.MixBpm - from.Bpm) + 1e-9,
            $"met at {plan.MixBpm:F2}, closer to the table's {from.Bpm:F2} than to the " +
            $"{outgoingHere:F2} under the needle");

        // And the trace says so, because a plan with a tempo that is not in the table looks like a
        // bug to anybody reading the lab.
        Assert.Contains(plan.Trace, line => line.Contains("not the", StringComparison.Ordinal)
                                         && line.Contains("in the table", StringComparison.Ordinal));
    }

    [Fact]
    public void The_next_key_meets_the_record_at_the_bar_under_the_needle()
    {
        // The main reason for the local reading: a press can happen anywhere, so there is no point in
        // the record where the tempo could be measured in advance.
        var from = Drifting("/library/out.wav", 124, 128, 600);
        var to = Drifting("/library/in.wav", 126, 126, 600);

        double early = from.BeatTimes[40];
        double late = from.BeatTimes[520];

        // The playing tempo each press is given, which is what Deck.PlayingBpm reports at the play
        // position.
        var pressedEarly = TransitionPlanner.SkipTo(Track(from), from.BpmAt(early), Track(to), early);
        var pressedLate = TransitionPlanner.SkipTo(Track(from), from.BpmAt(late), Track(to), late);

        double atEarly = from.BpmAt(pressedEarly.StartAtSeconds);
        double atLate = from.BpmAt(pressedLate.StartAtSeconds);

        Assert.True(atLate > atEarly + 2,
            $"the record read {atEarly:F2} early and {atLate:F2} late — it is not drifting enough to test");

        // Two presses on the same pair of records, planned at two different tempos, because the record
        // is at two different tempos. A plan built from the table's one number could not tell them
        // apart.
        Assert.True(Math.Abs(pressedLate.MixBpm - pressedEarly.MixBpm) > 0.5,
            $"both presses met at the same {pressedEarly.MixBpm:F2} on a record that drifts 4 BPM");
    }

    [Fact]
    public void A_record_sequenced_to_a_click_plans_exactly_as_it_did()
    {
        // The check on all of the above. Nearly every record is like this one: its grid comes back
        // straight, and the local reading must not change a single number in its plan.
        var from = Drifting("/library/out.wav", 128, 128, 600);
        var to = Drifting("/library/in.wav", 126, 126, 600);

        var plan = TransitionPlanner.Plan(Track(from), from.Bpm, Track(to), null);

        Assert.Equal(from.Bpm, from.BpmAt(plan.StartAtSeconds), 6);
        Assert.Equal(to.Bpm, to.BpmAt(to.IntroEndSeconds), 6);
        Assert.DoesNotContain(plan.Trace, line => line.Contains("in the table", StringComparison.Ordinal));
    }
}

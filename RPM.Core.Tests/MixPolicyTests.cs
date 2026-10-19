using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The planner's numbers are defaults that can be changed, not fixed limits. The default policy gives
/// exactly the planner's standard behaviour, and a changed value really reaches the calculation.
/// </summary>
public class MixPolicyTests
{
    [Fact]
    public void The_default_policy_is_the_planner_as_shipped()
    {
        Assert.True(MixPolicy.Default.IsDefault);
        Assert.Equal(TransitionPlanner.MaxTempoPull, MixPolicy.Default.MaxTempoPull);
        Assert.Equal(TransitionPlanner.OutroFadeBars, MixPolicy.Default.OutroFadeBars);
        Assert.Equal(TransitionPlanner.MaxDriftSeconds, MixPolicy.Default.MaxDriftSeconds);

        // An unchanged policy does not count as an override.
        Assert.True(new MixOverrides { Policy = new MixPolicy() }.IsEmpty);
    }

    [Fact]
    public void A_raised_tempo_pull_turns_a_blend_back_into_a_mix()
    {
        // 120 against 150: the geometric meeting point needs a pull of about 11.8% from each side.
        // At the default 10% the clamps leave the grids drifting apart and the planner blends. With
        // 13% allowed, both records reach the meeting point exactly and the planner beat-matches.
        var outgoing = Track(Analysis(bpm: 120, duration: 300, outro: 280));
        var incoming = Track(Analysis(bpm: 150, duration: 300));

        var stock = TransitionPlanner.Plan(outgoing, 120, incoming);
        Assert.Equal(TransitionStyle.Blend, stock.Style);

        var freed = TransitionPlanner.Plan(
            outgoing, 120, incoming,
            new MixOverrides { Policy = new MixPolicy { MaxTempoPull = 0.13 } });

        Assert.Equal(TransitionStyle.BeatMatched, freed.Style);
        Assert.Equal(Math.Sqrt(120.0 * 150.0), freed.MixBpm, 1);
        Assert.Contains(freed.Trace, line => line.StartsWith("policy:", StringComparison.Ordinal));

        // The changed policy stays on the plan, so the journal records what was asked.
        Assert.NotNull(freed.Overrides?.Policy);
    }

    [Fact]
    public void A_longer_fade_is_a_knob_rather_than_a_limit()
    {
        var outgoing = Track(Analysis(bpm: 128, duration: 300, outro: 200));
        var incoming = Track(Analysis(bpm: 128, duration: 300));

        var stock = TransitionPlanner.Plan(outgoing, 128, incoming);
        Assert.Equal(TransitionPlanner.OutroFadeBars, stock.FadeBars);

        var longer = TransitionPlanner.Plan(
            outgoing, 128, incoming,
            new MixOverrides { Policy = new MixPolicy { OutroFadeBars = 32 } });

        Assert.Equal(32, longer.FadeBars);
    }

    [Fact]
    public void The_extended_record_ceiling_can_be_lifted()
    {
        // A seven-minute record with its outro at six and a half minutes: the default policy leaves at
        // three minutes, a lifted ceiling lets it play to its own outro.
        var track = Track(Analysis(bpm: 128, duration: 420, outro: 390));

        Assert.Equal(TransitionPlanner.ExtendedMixOutSeconds, TransitionPlanner.MixOutPoint(track), 1);

        var lifted = new MixPolicy { ExtendedRecordSeconds = 600 };
        Assert.Equal(390, TransitionPlanner.MixOutPoint(track, lifted), 1);

        // Asked again both ways: the memo is kept per policy, so it does not return the first answer
        // to the second question.
        Assert.Equal(TransitionPlanner.ExtendedMixOutSeconds, TransitionPlanner.MixOutPoint(track), 1);
        Assert.Equal(390, TransitionPlanner.MixOutPoint(track, lifted), 1);
    }

    [Fact]
    public void A_turned_knob_is_named_in_the_asked_line()
    {
        var overrides = new MixOverrides { Policy = new MixPolicy { MaxTempoPull = 0.15 } };
        Assert.False(overrides.IsEmpty);
        Assert.Contains("pull 15", overrides.ToString(), StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static TrackAnalysis Analysis(double bpm, double duration, double? outro = null)
    {
        var beats = new List<double>();
        if (bpm > 0)
            for (double at = 0; at < duration; at += 60.0 / bpm)
                beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "x.flac",
            DurationSeconds = duration,
            Bpm = bpm,
            BpmConfidence = bpm > 0 ? 0.9 : 0,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,
            EnergyCurve = [0.3f, 0.3f],
            Intensity = 0.5,
            IntroEndSeconds = 8,
            OutroStartSeconds = outro ?? Math.Max(1, duration - 20),
        };
    }

    private static MixTrack Track(TrackAnalysis analysis) => new()
    {
        Source = analysis.FilePath,
        Path = analysis.FilePath,
        Title = "t",
        TaggedDuration = analysis.DurationSeconds,
        Analysis = analysis,
    };
}

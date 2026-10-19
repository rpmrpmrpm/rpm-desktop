using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// How far apart the two tracks' beats are in the MIDDLE of a changeover.
///
/// <para>Other beat-matching tests check only the ends. The pulls are chosen so the two tempos meet,
/// the changeover waits for a bar line so the drop lands on the sample, and
/// TransitionPlanner.SpanLock bends the pull so the far end lands on the sample too. A changeover can
/// pass all of those checks and still be half a beat out in the middle, because a real grid is not a
/// straight line: a track whose tempo wanders plays its beats early for a while and late for a
/// while, and one constant pull cannot follow it.</para>
///
/// <para>These tests check the middle. The measure is beat n of the incoming track against beat n
/// of the outgoing track, both in output seconds. The limit is MixPolicy.MaxDriftSeconds, the same
/// fifteen milliseconds the planner allows for two tempos it cannot match exactly.</para>
/// </summary>
public class GridDriftTests
{
    private const int Bpm = 120;

    private static readonly MixPolicy Phrase = new() { PhraseBlend = true };

    /// <summary>
    /// A record whose grid runs at <paramref name="bpm"/> until <paramref name="bendAt"/> and at
    /// <paramref name="bendBpm"/> after it. This tempo step is what a track looks like when a band
    /// played it, when it was cut from two takes, or when the beat tracker changed its estimate.
    /// </summary>
    private static MixTrack Track(
        string name, double duration, double outro, double bpm = Bpm,
        double bendAt = double.MaxValue, double bendBpm = 0, double jitter = 0,
        double wander = 0, double wanderSeconds = 60, int phraseBars = 8, double intro = 0)
    {
        var beats = new List<double>();
        for (double at = 0, n = 0; at < duration; n++)
        {
            beats.Add(at + (jitter > 0 && n % 2 == 0 ? jitter : 0));

            // A track that does not hold one tempo. Two shapes: a step (a cut between takes, or the
            // tracker changing its estimate) and a wander (a track played by people rather than
            // programmed; ModelBeatGrid keeps this shape when it is large enough to matter).
            double here = at >= bendAt && bendBpm > 0 ? bendBpm : bpm;
            if (wander > 0)
                here *= 1 + wander * Math.Sin(2 * Math.PI * at / wanderSeconds);
            at += 60.0 / here;
        }

        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = 4,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = [0.3f, 0.3f],
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                Phrases = new PhraseGrid(phraseBars, 0, 1),
            },
        };
    }

    /// <summary>
    /// The largest distance between the two grids anywhere inside the changeover, in output seconds.
    /// Compared beat for beat: the incoming track is cued on a beat and dropped on a beat, so its nth
    /// beat should play at the same time as the nth beat of the outgoing track.
    /// </summary>
    private static double WorstDrift(TransitionPlan plan, MixTrack outgoing)
    {
        var outs = outgoing.Analysis!.BeatTimes;
        var ins = plan.Incoming.Analysis!.BeatTimes;
        double outRatio = plan.OutgoingTempoRatio > 0 ? plan.OutgoingTempoRatio : 1;

        // The pull as it will be played: one piece on an ordinary changeover, several where the
        // pull follows the grid. Read from the plan rather than assumed, so the test measures the
        // schedule that is actually played.
        var steps = plan.TempoSteps.Count > 0
            ? plan.TempoSteps
            : [new TempoStep(0, plan.CueInSeconds, plan.TempoRatio > 0 ? plan.TempoRatio : 1)];

        int i = outs.TakeWhile(at => at < plan.StartAtSeconds - 1e-9).Count();
        int j = ins.TakeWhile(at => at < plan.CueInSeconds - 1e-9).Count();
        double until = plan.StartAtSeconds + plan.FadeSeconds * outRatio;

        double worst = 0;
        int piece = 0;
        for (int k = 0; i + k < outs.Count && j + k < ins.Count && outs[i + k] <= until + 1e-9; k++)
        {
            double heard = (outs[i + k] - plan.StartAtSeconds) / outRatio;
            while (piece + 1 < steps.Count && heard >= steps[piece + 1].AtSeconds - 1e-9)
                piece++;

            var step = steps[piece];
            worst = Math.Max(worst, Math.Abs(step.AtSeconds + (ins[j + k] - step.CueSeconds) / step.Ratio - heard));
        }

        return worst;
    }

    /// <summary>
    /// A sixteen-bar phrase blend onto a track whose grid changes tempo part way through. Both
    /// tracks play together for half a minute, and with one constant pull they end up almost a fifth
    /// of a beat apart in the middle.
    /// </summary>
    [Fact]
    public void A_changeover_is_not_made_longer_than_the_two_grids_can_hold()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, bendAt: 20, bendBpm: 118);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.True(
            WorstDrift(plan, outgoing) <= TransitionPlanner.MaxDriftSeconds,
            $"the grids walk {WorstDrift(plan, outgoing) * 1000:F0} ms apart inside a " +
            $"{plan.FadeBars}-bar {plan.Style}");
    }

    /// <summary>
    /// The first fix keeps the full length. A track whose grid wanders is played at the speed each
    /// part of it needs (the pull follows the grid instead of the tempo), so all sixteen bars are
    /// still played and the beats still line up.
    ///
    /// <para>This is what a sync button does, but planned in advance instead of corrected while
    /// playing: the changeover knows both grids, so it can choose the right speed up front.</para>
    /// </summary>
    [Fact]
    public void A_wandering_grid_is_followed_rather_than_the_changeover_cut_short()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, wander: 0.004, wanderSeconds: 50);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.Equal(16, plan.FadeBars);
        Assert.True(plan.TempoSteps.Count > 1, "one pull was kept on a record that does not hold one tempo");
        Assert.True(
            WorstDrift(plan, outgoing) <= TransitionPlanner.MaxDriftSeconds,
            $"{plan.TempoSteps.Count} pieces of pull and the grids still walk " +
            $"{WorstDrift(plan, outgoing) * 1000:F0} ms apart");
    }

    /// <summary>The pieces change speed only on a bar line of the mix. A speed change in the middle
    /// of a bar is audible.</summary>
    [Fact]
    public void The_pull_changes_on_bar_lines()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, wander: 0.004, wanderSeconds: 50);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.True(plan.TempoSteps.Count > 1);
        foreach (var step in plan.TempoSteps)
            Assert.Equal(0, step.AtSeconds % plan.BarSeconds, 3);
    }

    /// <summary>Two steady tracks stay on one pull. Each piece boundary reopens the decoder at the
    /// new speed, so it is used only when the grids need it.</summary>
    [Fact]
    public void A_steady_pair_is_left_on_one_pull()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.Empty(plan.TempoSteps);
    }

    /// <summary>When following the grid is not enough, the changeover is made shorter. Here the bend
    /// is a step rather than a wander, and no piece boundary falls on it.</summary>
    [Fact]
    public void The_count_is_halved_until_it_holds()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, bendAt: 20, bendBpm: 118);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.InRange(plan.FadeBars, 1, 8);
    }

    /// <summary>A track whose grid is not steady for even one bar is not beat-matched at all. A
    /// one-bar beat-match on such a grid would still be out of time.</summary>
    [Fact]
    public void A_grid_that_holds_for_no_bar_at_all_is_not_beat_matched()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280, jitter: 0.040);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.True(
            plan.Style is TransitionStyle.Blend or TransitionStyle.Cut,
            $"a record that wanders 40 ms a beat was beat-matched as {plan.Style}");
    }

    /// <summary>Two steady tracks still get the full changeover. The drift check does not shorten a
    /// changeover whose grids line up.</summary>
    [Fact]
    public void Two_steady_records_keep_the_full_length()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Phrase });

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.Equal(16, plan.FadeBars);
        Assert.True(WorstDrift(plan, outgoing) <= TransitionPlanner.MaxDriftSeconds);
    }
}

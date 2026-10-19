using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>
/// One rule the mixing code does not follow, and what following it would have done to the changeover
/// that is about to happen.
/// </summary>
/// <param name="Signal">The measurement the rule would read, named as it is on
/// <see cref="TrackAnalysis"/>.</param>
/// <param name="Rule">The rule itself, stated plainly enough to be disagreed with. This is the part
/// that is a guess.</param>
/// <param name="Now">What the planner does about this record as things stand.</param>
/// <param name="Proposed">What it would do instead.</param>
/// <param name="Delta">How big the difference is, in the unit the rule is about.</param>
public sealed record MixProposal(
    string Signal,
    string Rule,
    string Now,
    string Proposed,
    string Delta);

/// <summary>
/// What the measurements nobody reads would say about a changeover, if anyone read them.
///
/// <para>Nothing here runs during a mix. Each rule is a hypothesis written down so that it can be
/// tested against a real pair of records by a person who can hear the result. For example, a proposal
/// that would shorten this fade from sixteen bars to four is a claim. A DJ who listens to the
/// sixteen-bar version can say in one word whether the claim is right. That judgement is worth more
/// than any amount of reasoning about it. A rule that lives only in someone's head cannot be tested
/// this way.</para>
///
/// <para>Only the rules that would change this changeover are returned. A rule that agrees with the
/// planner on this pair proves nothing, and it would hide the rules that do not agree. The number of
/// rules checked is reported separately. See <see cref="Checked"/>.</para>
/// </summary>
public static class MixProposals
{
    /// <summary>How many rules are examined for each changeover, whether or not they apply.</summary>
    public const int Checked = 8;

    /// <summary>Below this value a record is measured as wandering. See
    /// <see cref="TrackAnalysis.IsSteadyEnoughToHold"/>. The mix does not call that reader at
    /// present.</summary>
    private const double LooseBelow = 0.4;

    /// <summary>The length a loose record is proposed to be held to: four bars. That is long enough to
    /// be a changeover. It is short enough that a record whose beats sit a fifth of a beat away from
    /// the ruler has not drifted far by the end of it.</summary>
    private const int LooseBars = 4;

    public static IReadOnlyList<MixProposal> For(
        MixTrack? outgoing, double outgoingBpm, MixTrack? incoming, TransitionPlan? plan)
    {
        if (outgoing is null || incoming is null || plan is null)
            return [];

        var found = new List<MixProposal>(Checked);

        Steadiness(outgoing, "outgoing", plan, found);
        Steadiness(incoming, "incoming", plan, found);
        BarPhase(outgoing, plan, found);
        BarLength(outgoing, plan, found);
        Extrapolated(outgoing, plan, found);
        Phrasing(outgoing, plan, found);
        IncomingDrop(incoming, plan, found);
        Levels(outgoing, incoming, plan, found);

        return found;
    }

    /// <summary>
    /// A record measured as loose cannot hold a long changeover, however well its nominal tempo
    /// matches.
    ///
    /// <para>The planner's drift budget is computed from the tempo the clamps could not reach. That
    /// budget assumes both records hold that tempo exactly for the whole sixteen bars. A record whose
    /// beats sit a fifth of a beat away from the ruler does not hold its tempo. So the budget does not
    /// describe what that record does.</para>
    /// </summary>
    private static void Steadiness(MixTrack track, string which, TransitionPlan plan, List<MixProposal> found)
    {
        double stability = track.TempoStability;
        if (stability == TrackShape.Unmeasured || stability > LooseBelow)
            return;
        if (plan.FadeBars <= LooseBars || plan.Style is TransitionStyle.Cut)
            return;

        found.Add(new MixProposal(
            nameof(TrackAnalysis.TempoStability),
            $"A record measured under {LooseBelow:F2} wanders too much to be held on a bent tempo, so cap the changeover at {LooseBars} bars.",
            $"{plan.FadeBars} bars of {plan.BarSeconds:F2}s ({plan.FadeSeconds:F1}s)",
            $"{LooseBars} bars ({LooseBars * plan.BarSeconds:F1}s)",
            $"{which} steadiness {stability:F2}, {plan.FadeBars - LooseBars} bars shorter"));
    }

    /// <summary>
    /// A bar line that nobody could find is still a bar line the mix is aligned to. The mix is aligned
    /// to it just as hard as to an obvious one.
    /// </summary>
    private static void BarPhase(MixTrack track, TransitionPlan plan, List<MixProposal> found)
    {
        if (track.Analysis is null || plan.Style is TransitionStyle.Cut)
            return;
        double confidence = track.DownbeatConfidence;
        if (confidence >= TrackAnalysis.MinDownbeatConfidence)
            return;

        found.Add(new MixProposal(
            nameof(TrackAnalysis.DownbeatConfidence),
            $"Under {TrackAnalysis.MinDownbeatConfidence:F2} the four bar phases were effectively tied, so the changeover should not be held for a downbeat that is one guess in four.",
            $"held for the next downbeat on a {confidence:F2} reading",
            "start on the next beat instead, or shorten the changeover so a wrong phase costs less",
            $"downbeat confidence {confidence:F2}"));
    }

    /// <summary>
    /// The one place where a measured bar length is not used. The grid and the bar snapping use the
    /// record's own beats per bar. The fade always counts four beats to a bar.
    /// </summary>
    private static void BarLength(MixTrack track, TransitionPlan plan, List<MixProposal> found)
    {
        if (track.Analysis is null || plan.BarSeconds <= 0 || track.BeatsPerBar == 4)
            return;

        double counted = plan.BarSeconds / 4 * track.BeatsPerBar;
        found.Add(new MixProposal(
            nameof(TrackAnalysis.BeatsPerBar),
            "TransitionPlanner should measure a bar as the record's own BeatsPerBar rather than as four, which is what every other reader of the grid already does.",
            $"bar of {plan.BarSeconds:F2}s (4 beats), {plan.FadeBars} bars = {plan.FadeSeconds:F1}s",
            $"bar of {counted:F2}s ({track.BeatsPerBar} beats), {plan.FadeBars} bars = {plan.FadeBars * counted:F1}s",
            $"{Math.Abs(plan.FadeBars * counted - plan.FadeSeconds):F1}s out"));
    }

    /// <summary>
    /// The grid is carried to both ends of a record on purpose. The outro is where the tracker stops
    /// defending an onset. So the bar line a changeover is timed to is often one that nobody heard.
    /// </summary>
    private static void Extrapolated(MixTrack track, TransitionPlan plan, List<MixProposal> found)
    {
        if (track.Analysis is not { BeatLastSeconds: > 0 } analysis)
            return;
        if (!analysis.IsExtrapolatedAt(plan.StartAtSeconds))
            return;

        double past = plan.StartAtSeconds - analysis.BeatLastSeconds;
        found.Add(new MixProposal(
            nameof(TrackAnalysis.BeatLastSeconds),
            "A changeover timed to an extrapolated bar line should say so, and either pull back to the last measured bar or stop claiming to be beat-matched.",
            $"{plan.Style} timed to a bar line at {MixInputs.Sec(plan.StartAtSeconds)}",
            $"the last bar anybody heard was at {MixInputs.Sec(analysis.BeatLastSeconds)}",
            $"{past:F1}s past the measured grid"));
    }

    /// <summary>
    /// A changeover lands on one of the fours counted from the bar where the drums come in. This checks
    /// whether it should land on the phrase the record was measured to repeat over instead. A phrase is
    /// eight, sixteen or thirty-two bars long, and it starts where the reading says it does.
    /// </summary>
    private static void Phrasing(MixTrack track, TransitionPlan plan, List<MixProposal> found)
    {
        if (track.Analysis is not { Phrases.IsKnown: true } analysis || plan.Style is TransitionStyle.Cut)
            return;
        if (PhraseNear(analysis, plan.StartAtSeconds) is not { } phrase)
            return;

        double off = Math.Abs(phrase - plan.StartAtSeconds);
        if (off < 0.05 || off > analysis.BarSeconds * analysis.Phrases.Bars)
            return;

        found.Add(new MixProposal(
            nameof(TrackAnalysis.Phrases),
            "The mix-out should snap to the phrase this record was measured to repeat over, rather than to the fours counted from where its drums came in, which is what MixInFinder already does for the next key.",
            $"starts at {MixInputs.Sec(plan.StartAtSeconds)}, on a four but mid-phrase",
            $"start at {MixInputs.Sec(phrase)}, on the {analysis.Phrases.Bars}-bar phrase",
            $"{off:F1}s, {off / Math.Max(0.001, analysis.BarSeconds):F1} bars"));
    }

    /// <summary>
    /// The room waits for the drop of the incoming record to land. The cue point is chosen without
    /// using that drop, so the drop can land away from the bar where the changeover ends.
    /// </summary>
    private static void IncomingDrop(MixTrack track, TransitionPlan plan, List<MixProposal> found)
    {
        if (track.Analysis is null || track.Drops.Count == 0 || plan.FadeSeconds <= 0)
            return;

        // Unless this changeover already does it. A double drop is cued from the drop, not from the
        // grid (see TransitionPlanner.Dropped). Proposing this rule for a double drop would contradict
        // the changeover itself.
        if (plan.Style is TransitionStyle.DoubleDrop)
            return;

        // Where the fade ends on the record coming in, counted in its own seconds from its cue point.
        double landsAt = plan.CueInSeconds + plan.FadeSeconds;
        double drop = track.Drops.OrderBy(d => Math.Abs(d - landsAt)).First();
        double off = drop - landsAt;
        if (Math.Abs(off) < 0.25 || Math.Abs(off) > 30)
            return;

        found.Add(new MixProposal(
            nameof(TrackAnalysis.DropSeconds),
            "The incoming record should be cued so that its own drop lands on the bar the changeover finishes on, which is the whole reason the drop is measured.",
            $"cued at {MixInputs.Sec(plan.CueInSeconds)}, fade ends at {MixInputs.Sec(landsAt)} of that record",
            $"cue at {MixInputs.Sec(Math.Max(0, plan.CueInSeconds + off))} so the drop at {MixInputs.Sec(drop)} lands on it",
            $"{off:+0.0;-0.0}s, {Math.Abs(off) / Math.Max(0.001, plan.BarSeconds):F1} bars"));
    }

    /// <summary>
    /// How loud each record is at the exact point where the changeover meets it. Both are measured, and
    /// the planner reads neither. The difference decides whether a record arrives under the one it
    /// replaces or on top of it.
    /// </summary>
    private static void Levels(MixTrack outgoing, MixTrack incoming, TransitionPlan plan, List<MixProposal> found)
    {
        if (outgoing.OutroLevel <= 0 || incoming.IntroLevel <= 0)
            return;

        // Changeovers where nothing arrives over anything. There is no crossfade to weight, and no
        // moment when one record's level is heard against the other's.
        if (plan.Style is TransitionStyle.Cut or TransitionStyle.Slam or TransitionStyle.Spinback)
            return;

        double gap = incoming.IntroLevel - outgoing.OutroLevel;
        if (gap <= 0.15)
            return;

        found.Add(new MixProposal(
            $"{nameof(TrackAnalysis.IntroLevel)} / {nameof(TrackAnalysis.OutroLevel)}",
            "A record whose intro is louder than the outro it is arriving over should come in trimmed, or the crossfade should be weighted, so that it arrives underneath rather than over the top.",
            $"equal-power crossfade, both records at their own level",
            $"trim the incoming record by about {20 * Math.Log10(Math.Max(0.05, outgoing.OutroLevel / incoming.IntroLevel)):F1} dB over the fade",
            $"intro {incoming.IntroLevel:F2} against outro {outgoing.OutroLevel:F2}, {gap:F2} louder"));
    }

    /// <summary>The phrase boundary nearest to <paramref name="seconds"/>, or null when the beat grid
    /// does not reach one.</summary>
    private static double? PhraseNear(TrackAnalysis analysis, double seconds)
    {
        var phrases = analysis.Phrases;
        if (!phrases.IsKnown || analysis.BeatTimes.Count == 0 || analysis.BeatsPerBar < 1)
            return null;

        double best = double.NaN;
        double bestOff = double.MaxValue;

        // Every phrase boundary is a bar line whose bar number is a whole number of phrases past the
        // first one. The search goes bar by bar rather than solving for the answer. The grid is a list,
        // and the last phrase of a record is often past the end of it.
        for (int bar = phrases.FirstBar; ; bar += phrases.Bars)
        {
            int beat = analysis.FirstDownbeat + bar * analysis.BeatsPerBar;
            if (beat < 0)
                continue;
            if (beat >= analysis.BeatTimes.Count)
                break;

            double at = analysis.BeatTimes[beat];
            double off = Math.Abs(at - seconds);
            if (off < bestOff)
            {
                bestOff = off;
                best = at;
            }
            else if (at > seconds)
            {
                break;                  // moved past the closest boundary, so stop
            }
        }

        return double.IsNaN(best) ? null : best;
    }
}

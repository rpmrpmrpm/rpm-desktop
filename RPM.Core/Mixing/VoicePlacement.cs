using RPM.Core.Analysis;

namespace RPM.Core.Mixing;

/// <summary>
/// Where to leave one record and where to join the next, chosen together, so that the changeover does not
/// lay two singers over each other.
///
/// <para><b>Together, where the changeover allows it.</b> Moving only one end is not enough. The outgoing
/// record can walk back to a stretch with no voice in it, but the incoming record starts at its own body,
/// singing or not. That solves only half the problem, and often the wrong half. Many changeovers are fixed
/// by bringing the new record in a phrase later, past its first verse, instead of giving up more of the
/// outgoing record.</para>
///
/// <para><b>But not every changeover may move both ends.</b> On the ordinary beat-matched changeover the
/// incoming cue is fixed. It is set one fade's length before the intro of the new record ends, so that its
/// drop lands as the fade finishes. The intro is played under the old record for exactly this purpose.
/// Moving the cue does not trade one vocal for another. It loses the arrival. So the caller states which
/// kind this is, see <see cref="CueIn"/>. It is an argument, not a default, because a wrong value fails
/// silently. The plan looks correct, but the new record starts in the wrong place.</para>
///
/// <para><b>The standing answer is tried first, and every other candidate must beat it.</b> The search
/// opens at the points the planner chose and walks outwards. A candidate is taken only when it is strictly
/// better (see <see cref="Better"/>). So a record with nothing to gain stays where it is. Nothing moves for
/// a difference that is within the noise of hand-written lyric timings.</para>
///
/// <para>The search is bounded on both sides, because every step walked is music that nobody hears. Out of
/// the record going, it is limited by <see cref="MixOutFinder.PhrasesBack"/>. Into the record coming, it is
/// limited by <see cref="MostOfARecord"/>. Past that point, the new record is not skipping an intro. It is
/// starting somewhere else.</para>
/// </summary>
public static class VoicePlacement
{
    /// <summary>How far into the record coming in the search may cue. The same bound
    /// <see cref="MixInFinder"/> holds itself to, and for the same reason: two fifths in is not an intro
    /// being skipped, it is a different part of the song.</summary>
    public const double MostOfARecord = 0.4;

    /// <summary>Whether the record coming in may be cued somewhere other than where the planner put
    /// it.</summary>
    public enum CueIn
    {
        /// <summary>It may not. The cue of an ordinary changeover makes the new record's drop land as the
        /// fade finishes. Moving it would lose that, and nothing gained is worth that loss.</summary>
        Fixed,

        /// <summary>It may. A phrase blend deliberately does not touch either record's intro, so where the
        /// new one comes in is a choice about its body and nothing else depends on it.</summary>
        Free,
    }

    /// <summary>
    /// One record's part in the changeover: what it is, where it sings, where the planner put it, and how
    /// far one step moves it.
    /// </summary>
    /// <param name="Record">The analysis, for its length and its intro.</param>
    /// <param name="Voices">Where somebody sings on it.</param>
    /// <param name="At">Where the planner put it. The search opens here.</param>
    /// <param name="Step">One step, in seconds: the record's phrase on a phrase blend, its four on the
    /// ordinary changeover. A whole number of these keeps every candidate on the same lines
    /// <paramref name="At"/> was already on, so nothing here has to snap and nothing here can
    /// unsnap.</param>
    public readonly record struct Side(
        TrackAnalysis Record, VocalMap Voices, double At, double Step);

    /// <summary>
    /// One candidate changeover: where each record is, what the two of them are singing over it, and how
    /// far each end had to move to get there.
    /// </summary>
    /// <param name="StartAt">Where the outgoing record hands over.</param>
    /// <param name="CueIn">…and where the incoming one is cued.</param>
    /// <param name="Voices">What the two of them sing over the changeover.</param>
    /// <param name="StepsBack">Steps of the outgoing record given up to get here, nought for the point the
    /// planner had already chosen.</param>
    /// <param name="StepsIn">…and steps of the incoming record skipped past its own.</param>
    public readonly record struct Placed(
        double StartAt, double CueIn, MixVoices Voices, int StepsBack, int StepsIn)
    {
        /// <summary>Whether this is the point the planner chose. Nothing moved.</summary>
        public bool Unmoved => StepsBack == 0 && StepsIn == 0;
    }

    /// <summary>
    /// The best pair of points within reach, or the pair it was given when nothing beats them.
    /// </summary>
    /// <param name="leaving">The record being left. The search walks BACK from where it is and never
    /// forward, because forward is where the outro is.</param>
    /// <param name="arriving">The record coming in. The search walks IN from where it is, and only when
    /// <paramref name="cue"/> allows it.</param>
    /// <param name="fadeSeconds">How long the two run together. The whole of it is scored.</param>
    /// <param name="cue">Whether the incoming record may be moved at all.</param>
    /// <param name="stepsBack">How many steps of the outgoing record may be given up.</param>
    public static Placed Choose(
        Side leaving, Side arriving, double fadeSeconds, CueIn cue,
        int stepsBack = MixOutFinder.PhrasesBack)
    {
        ArgumentNullException.ThrowIfNull(leaving.Record);
        ArgumentNullException.ThrowIfNull(arriving.Record);
        ArgumentNullException.ThrowIfNull(leaving.Voices);
        ArgumentNullException.ThrowIfNull(arriving.Voices);

        var standing = new Placed(
            leaving.At, arriving.At,
            MixVoices.Over(leaving.Voices, leaving.At, arriving.Voices, arriving.At, fadeSeconds),
            0, 0);

        if (fadeSeconds <= 0 || stepsBack < 1 || leaving.Step <= 0)
            return standing;

        // If neither record has voice data, which is normal for a library that has not been read, every
        // candidate reads the same. The search is skipped, and the standing answer is returned.
        if (!leaving.Voices.IsKnown && !arriving.Voices.IsKnown)
            return standing;

        // Never move back into the outgoing record's own intro. The intro is written to be mixed over, not
        // out of, so the rule for the incoming end also applies here.
        double floorAt = Math.Max(0, leaving.Record.IntroEndSeconds);

        // …and never so far into the incoming record that its own body is gone, nor so far that the
        // changeover would run off the end of it.
        double ceilingAt = cue == CueIn.Free && arriving.Step > 0
            ? Math.Min(
                arriving.Record.DurationSeconds * MostOfARecord,
                Math.Max(arriving.At, arriving.Record.DurationSeconds - fadeSeconds))
            : arriving.At;

        var best = standing;

        // Outgoing outermost and incoming innermost, both walking away from the standing answer, so the
        // first candidate examined IS the standing one and everything after it has to be strictly better to
        // displace it. That ordering is what makes the tie-break "change nothing".
        for (var back = 0; back < stepsBack; back++)
        {
            double startAt = leaving.At - back * leaving.Step;
            if (startAt < floorAt - 1e-6)
                break;

            for (var forward = 0; ; forward++)
            {
                double cueIn = arriving.At + forward * (arriving.Step > 0 ? arriving.Step : 1);
                if (cueIn > ceilingAt + 1e-6)
                    break;

                var voices = MixVoices.Over(
                    leaving.Voices, startAt, arriving.Voices, cueIn, fadeSeconds);

                if (Better(voices, back + forward, best.Voices, best.StepsBack + best.StepsIn) < 0)
                    best = new Placed(startAt, cueIn, voices, back, forward);

                if (cue == CueIn.Fixed)
                    break;
            }
        }

        return best;
    }

    /// <summary>
    /// Which of two candidates is the better changeover: negative when the first is.
    ///
    /// <para><b>Four things in order, and the order is the whole rule.</b></para>
    ///
    /// <para>1. <b>Two voices at once.</b> The fault. Beat-matching cannot rescue it, because two records
    /// can share drums and cannot share voices, and no amount of anything below makes it acceptable.</para>
    ///
    /// <para>2. <b>The incoming record singing alone.</b> This ranks above everything except the clash. It
    /// is kept separate from the outgoing record's singing, because they are different events. The outgoing
    /// voice is one the listener has been hearing, and it is on its way out. An incoming voice arriving
    /// under it spoils the handover. It buries the hook of the track being introduced under a record that
    /// is leaving. When someone has to be singing, it should be the voice already playing.</para>
    ///
    /// <para>3. <b>Record given up.</b> Every step walked is music that nobody hears: a phrase of the old
    /// record skipped, or a phrase of the new one never played. This ranks above the last clause, because
    /// it is a real cost to the listeners. The last clause is only a preference.</para>
    ///
    /// <para>4. <b>The outgoing record singing alone</b>, which is what a mix normally is. It decides
    /// between candidates that cost the same. It never causes movement on its own. Without this rule, the
    /// search could give up a phrase of the record to shorten a vocal that was on its way out
    /// anyway.</para>
    ///
    /// <para>Readings are compared with a tolerance of <see cref="MixVoices.Same"/>, because they are
    /// seconds worked out from timestamps typed by hand. A changeover must not move a phrase for a
    /// difference of a hundredth of a second.</para>
    /// </summary>
    private static int Better(MixVoices a, int movedA, MixVoices b, int movedB)
    {
        if (Math.Abs(a.Both - b.Both) > MixVoices.Same)
            return a.Both.CompareTo(b.Both);

        if (Math.Abs(a.Arriving - b.Arriving) > MixVoices.Same)
            return a.Arriving.CompareTo(b.Arriving);

        if (movedA != movedB)
            return movedA.CompareTo(movedB);

        return Math.Abs(a.Leaving - b.Leaving) > MixVoices.Same ? a.Leaving.CompareTo(b.Leaving) : 0;
    }
}

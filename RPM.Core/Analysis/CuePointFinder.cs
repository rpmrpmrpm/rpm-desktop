namespace RPM.Core.Analysis;

/// <summary>
/// Finds the two points a DJ needs in a track: where the intro gives way to the body (the next
/// track can come in from here) and where the outro begins (start mixing out here). Both are read
/// from the energy curve. The intro is the run at the start that is quieter than the body, and the
/// outro is the run at the end. Both are then moved onto the beat grid, because a mix that starts
/// off the bar is worse than no mix.
/// </summary>
public static class CuePointFinder
{
    /// <summary>A section counts as "the body" once it reaches this share of the track's median
    /// level. The value is well under 1, because an intro is usually stripped down, not silent.</summary>
    private const double BodyThreshold = 0.55;

    /// <summary>The largest share of the track that a cue may take. Without it, a track that never
    /// settles would report a two-minute intro and half of it would be skipped.</summary>
    private const double MaxCueShare = 0.35;

    public readonly record struct Cues(double IntroEndSeconds, double OutroStartSeconds);

    /// <summary>
    /// <paramref name="beatTimes"/> and <paramref name="firstDownbeat"/> come from the beat grid;
    /// pass an empty grid and the cues come back unsnapped, which is still usable.
    /// </summary>
    /// <param name="bassLevels">
    /// The same curve taken below 200 Hz (see <see cref="EnergyProfile.ComputeLowBand"/>). The cues
    /// are read from this curve when the track has any bass, because it answers the question that
    /// matters (has the <em>beat</em> started) and not "is it loud yet". A spoken intro over
    /// clapping is as loud as the song but has almost no kick. Judged on the full band it looks like
    /// the body of the track, and the next track would be mixed into the talking. When this is
    /// empty the full-band curve is used, which is correct for a track with no kick.
    /// </param>
    public static Cues Find(
        ReadOnlySpan<float> levels,
        double durationSeconds,
        IReadOnlyList<double> beatTimes,
        int firstDownbeat,
        int beatsPerBar = 4,
        ReadOnlySpan<float> bassLevels = default)
    {
        if (durationSeconds <= 0)
            return new Cues(0, 0);
        if (levels.Length == 0)
            return new Cues(0, durationSeconds);

        // Read the cues from the bass curve when the track has enough bass. "Enough" is measured
        // against the track's own full-band level, so a quietly mastered track still counts. A track
        // with really no bass (solo piano, a cappella) uses the full-band curve, so noise does not
        // produce meaningless cues.
        var cueLevels = levels;
        if (bassLevels.Length == levels.Length &&
            EnergyProfile.Median(bassLevels) > EnergyProfile.Median(levels) * 0.08)
            cueLevels = bassLevels;

        double body = EnergyProfile.Median(cueLevels) * BodyThreshold;
        double limit = durationSeconds * MaxCueShare;

        // Intro: the first reading that reaches the body level and stays there for about two
        // seconds. One loud stab in an ambient intro is not the drop.
        double introEnd = 0;
        int hold = (int)Math.Ceiling(2.0 / EnergyProfile.StepSeconds);
        for (int i = 0; i < cueLevels.Length; i++)
        {
            if (cueLevels[i] < body)
                continue;
            bool sustained = true;
            for (int j = i; j < Math.Min(cueLevels.Length, i + hold); j++)
                if (cueLevels[j] < body) { sustained = false; break; }
            if (!sustained)
                continue;
            introEnd = i * EnergyProfile.StepSeconds;
            break;
        }
        introEnd = Math.Clamp(introEnd, 0, limit);

        // Outro: walking back from the end, the first reading that is still at body level.
        // Everything after it is the tail that the next track can play over.
        double outroStart = durationSeconds;
        for (int i = cueLevels.Length - 1; i >= 0; i--)
        {
            if (cueLevels[i] < body)
                continue;
            outroStart = (i + 1) * EnergyProfile.StepSeconds;
            break;
        }
        outroStart = Math.Clamp(outroStart, durationSeconds - limit, durationSeconds);

        // A track that is quiet all through can give an outro before the intro ends. In that case
        // report the whole track instead.
        if (outroStart <= introEnd)
            return new Cues(0, durationSeconds);

        return new Cues(
            SnapToBar(introEnd, beatTimes, firstDownbeat, beatsPerBar, forward: true),
            SnapToBar(outroStart, beatTimes, firstDownbeat, beatsPerBar, forward: false));
    }

    /// <summary>
    /// Moves a time onto the nearest bar line. It moves forward for a cue you play <i>from</i>
    /// (never start in the middle of a bar) and backward for a cue you mix <i>out</i> from (never
    /// go past the start of the fade). Returns the time unchanged when there is no grid to snap to.
    /// </summary>
    public static double SnapToBar(
        double seconds,
        IReadOnlyList<double> beatTimes,
        int firstDownbeat,
        int beatsPerBar,
        bool forward)
    {
        ArgumentNullException.ThrowIfNull(beatTimes);
        if (beatTimes.Count == 0 || beatsPerBar < 1)
            return seconds;

        double best = seconds;
        double bestGap = double.MaxValue;
        for (int i = firstDownbeat; i < beatTimes.Count; i += beatsPerBar)
        {
            double at = beatTimes[i];
            if (forward && at < seconds - 1e-6) continue;
            if (!forward && at > seconds + 1e-6) continue;
            double gap = Math.Abs(at - seconds);
            if (gap < bestGap)
            {
                bestGap = gap;
                best = at;
            }
        }
        return best;
    }
}

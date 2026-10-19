namespace TempoBench;

/// <summary>
/// How far a grid moves away from the record it describes.
///
/// <para>The accuracy tables cannot show this, and a mix depends on it. Accuracy1 allows four
/// percent, which at 128 BPM is five beats a minute; a beat-match allows fifteen milliseconds over a
/// whole changeover, which is a fiftieth of that. So a reading can pass every published measure and
/// still fail in the second half of the fade, and no published number shows it.</para>
///
/// <para>What is measured is the offset between each annotated beat and the nearest beat of this
/// grid, as it changes through the record. A line through those offsets separates the two possible
/// errors, which need opposite fixes:</para>
///
/// <list type="bullet">
/// <item><b>Drift</b>: the slope. The tempo is wrong by that fraction, so the grid moves steadily
/// away, and the error at the end of a changeover is everything that accumulated over it.</item>
/// <item><b>Jitter</b>: what remains after the line. The beats are in the right place on average
/// but each one is scattered, which a fade hides and a loop does not.</item>
/// </list>
/// </summary>
internal static class Drift
{
    /// <summary>
    /// How close one of this grid's beats must be to an annotated beat to count as the same beat. A
    /// quarter of a beat: beyond that, the nearest beat is as likely to be the next one, and the
    /// offset is meaningless.
    ///
    /// <para><b>This biases the result in one direction.</b> A beat that is left out was far off, so
    /// a grid that is badly wrong is measured on its better half and reported as better than it is.
    /// <see cref="Result.Coverage"/> shows this, and a comparison between two grids is fair only when
    /// their coverage matches. <see cref="OverChangeover"/> does not need this limit.</para>
    /// </summary>
    private const double SameBeatWithin = 0.25;

    /// <summary>The changeover length used for the results: thirty-two beats, which is the eight
    /// bars of four that <c>TransitionPlanner</c> plans.</summary>
    private const int ChangeoverBeats = 32;

    /// <param name="Percent">How fast the grid moves away from the record, as a percentage of the
    /// tempo. This is the tempo error, measured from the beats.</param>
    /// <param name="ChangeoverMs">The drift over eight bars: the number to compare with
    /// <c>TransitionPlanner.MaxDriftSeconds</c>.</param>
    /// <param name="JitterMs">The median scatter that remains after the drift is removed.</param>
    /// <param name="Coverage">The fraction of annotated beats that had a beat of this grid close
    /// enough. Low coverage means the two grids do not describe the same record, and the other
    /// numbers are meaningless.</param>
    internal readonly record struct Result(
        double Percent, double ChangeoverMs, double JitterMs, double Coverage)
    {
        public bool IsUsable => Coverage >= 0.5;
    }

    public static Result Of(IReadOnlyList<double>? ours, IReadOnlyList<double>? annotated)
    {
        if (ours is not { Count: >= 4 } || annotated is not { Count: >= 8 })
            return default;

        double beat = MedianGap(annotated);
        if (beat <= 0)
            return default;

        // For each annotated beat, the nearest beat of this grid and its offset. Beats with no beat
        // close enough are left out and lower the coverage; no value is guessed for them.
        var times = new List<double>(annotated.Count);
        var offsets = new List<double>(annotated.Count);
        int at = 0;
        foreach (double reference in annotated)
        {
            while (at + 1 < ours.Count && Math.Abs(ours[at + 1] - reference) <= Math.Abs(ours[at] - reference))
                at++;

            double offset = ours[at] - reference;
            if (Math.Abs(offset) <= SameBeatWithin * beat)
            {
                times.Add(reference);
                offsets.Add(offset);
            }
        }

        double coverage = (double)offsets.Count / annotated.Count;
        if (offsets.Count < 8)
            return new Result(0, 0, 0, coverage);

        // The drift, as a line through the offsets against time.
        double meanTime = times.Average(), meanOffset = offsets.Average();
        double cov = 0, var = 0;
        for (int i = 0; i < offsets.Count; i++)
        {
            cov += (times[i] - meanTime) * (offsets[i] - meanOffset);
            var += (times[i] - meanTime) * (times[i] - meanTime);
        }

        double slope = var > 0 ? cov / var : 0;
        double intercept = meanOffset - slope * meanTime;

        var residuals = new List<double>(offsets.Count);
        for (int i = 0; i < offsets.Count; i++)
            residuals.Add(Math.Abs(offsets[i] - (intercept + slope * times[i])));

        residuals.Sort();

        return new Result(
            Percent: slope * 100,
            ChangeoverMs: Math.Abs(slope) * ChangeoverBeats * beat * 1000,
            JitterMs: residuals[residuals.Count / 2] * 1000,
            Coverage: coverage);
    }

    /// <summary>
    /// The error a changeover would actually have: not how well this grid describes the whole
    /// record, but how far a deck moves away from the music while a fade runs.
    ///
    /// <para>This matches what the mix does. At the mix point the planner reads one tempo, and the
    /// two records are kept at that tempo for the whole changeover; the tempo is not read again
    /// during it. So the grid that matters is a straight line at that tempo from that beat, and the
    /// number that matters is how far its last beat is from the record's own beat.</para>
    ///
    /// <para>Measured at several points in the record, because a tempo read over a short span is not
    /// consistently wrong, it is unreliable: at some points the end beats happen to be scattered the
    /// same way and the tempo is exactly right. The median over a record is the fair figure.</para>
    /// </summary>
    /// <param name="localBpm">Gets the analysis's tempo at a point, over a span of beats
    /// (<c>TrackAnalysis.BpmAt</c>). Passed in so the harness can compare spans without a
    /// rebuild.</param>
    /// <remarks>
    /// Measured as a difference of tempi, not by projecting a grid and looking for the beat nearest
    /// its end. This is on purpose: finding the "nearest" annotated beat needs a rule for when there
    /// is none, and any such rule discards exactly the samples that drifted furthest, which are the
    /// ones being measured. That would make the worse method look better, because it has more
    /// samples to discard. Nothing is discarded here.
    /// </remarks>
    public static Result OverChangeover(
        IReadOnlyList<double>? annotated,
        Func<double, double> localBpm,
        int beatsInChangeover)
    {
        if (annotated is not { Count: >= 16 } || beatsInChangeover < 4)
            return default;

        double beat = MedianGap(annotated);
        if (beat <= 0)
            return default;

        double changeover = beatsInChangeover * beat;

        // The window the two tempi are read over is not the changeover: it is as much of the record
        // as is available, up to the changeover's length. A thirty-second excerpt is shorter than a
        // sixteen-bar changeover, and requiring the full length would discard nine tracks in ten
        // and keep only the fast ones. The error is a percentage and does not depend on the window
        // length; only the drift computed from it does, and that is calculated afterwards.
        double window = Math.Min(changeover, (annotated[^1] - annotated[0]) / 2);
        if (window < 8 * beat)
            return default;

        var drifts = new List<double>();
        int attempted = 0;

        double from = annotated[0], to = annotated[^1];
        for (double at = from + window / 2; at + window / 2 <= to; at += Math.Max((to - from) / 12, beat))
        {
            attempted++;

            double ours = localBpm(at);
            double theirs = LocalBpm(annotated, at, window);
            if (ours <= 0 || theirs <= 0)
                continue;

            // Compare at the same octave. A record read at double tempo is not drifting because it
            // is 256 against an annotated 128; it drifts only if 256 is not exactly twice 128.
            while (ours > theirs * 1.5) theirs *= 2;
            while (ours < theirs / 1.5) theirs /= 2;

            // The deck is kept at this grid's tempo for the whole changeover while the record plays
            // at its own: the gap grows at the difference between them, and this is the gap at the
            // end. The error is measured over the window the record allows; the changeover length
            // is the one the planner would actually use.
            drifts.Add(Math.Abs(ours - theirs) / theirs * changeover);
        }

        if (drifts.Count == 0)
            return new Result(0, 0, 0, 0);

        drifts.Sort();
        return new Result(
            Percent: 0,
            ChangeoverMs: drifts[drifts.Count / 2] * 1000,
            JitterMs: drifts[^1] * 1000,
            Coverage: (double)drifts.Count / Math.Max(1, attempted));
    }

    /// <summary>The annotated tempo around a point, over the same span and with the same least
    /// squares fit as the analysis, so both numbers are computed the same way and their difference
    /// comes from the beats, not from the method.</summary>
    private static double LocalBpm(IReadOnlyList<double> beats, double seconds, double span)
    {
        double half = span / 2;
        var window = new List<double>();
        foreach (double b in beats)
            if (b >= seconds - half && b <= seconds + half)
                window.Add(b);

        if (window.Count < 4)
            return 0;

        // Numbered against the median gap, so a beat that the annotator did not mark adds one step
        // to the numbering and does not change the tempo.
        double period = MedianGap(window);
        if (period <= 0)
            return 0;

        var index = new double[window.Count];
        for (int i = 1; i < window.Count; i++)
            index[i] = index[i - 1] + Math.Max(1, Math.Round((window[i] - window[i - 1]) / period));

        double meanIndex = index.Average(), meanTime = window.Average();
        double covariance = 0, variance = 0;
        for (int i = 0; i < window.Count; i++)
        {
            covariance += (index[i] - meanIndex) * (window[i] - meanTime);
            variance += (index[i] - meanIndex) * (index[i] - meanIndex);
        }

        double fitted = variance > 0 ? covariance / variance : 0;
        return fitted > 0 ? 60.0 / fitted : 0;
    }

    private static double MedianGap(IReadOnlyList<double> beats)
    {
        var gaps = new List<double>(beats.Count - 1);
        for (int i = 1; i < beats.Count; i++)
            if (beats[i] - beats[i - 1] > 0)
                gaps.Add(beats[i] - beats[i - 1]);

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }
}

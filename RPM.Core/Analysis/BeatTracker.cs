namespace RPM.Core.Analysis;

/// <summary>
/// Places the beats, given the onset envelope and the tempo. A greedy pass that takes the next big
/// peak about one beat away drifts and never recovers. This class uses the dynamic-programming
/// tracker of Ellis (2007) instead. It scores every possible beat sequence over the whole track at
/// once and takes the best one. Each beat is worth the onset strength under it, minus a penalty for
/// being at the wrong distance from the previous beat. So the grid continues through a bar of
/// silence and is exactly in step again on the other side.
/// </summary>
public static class BeatTracker
{
    /// <summary>How strong the spacing penalty is. A higher value gives a stiffer grid that ignores
    /// syncopation and holds the tempo. A lower value gives a grid that follows every peak. 100 is
    /// the tightness from Ellis, and it suits a DJ grid, which should be an even ruler and not a
    /// list of onsets.</summary>
    private const double Tightness = 100;

    /// <summary>
    /// Beat positions as frame indices into the onset envelope. Empty when there is nothing to
    /// track. The caller turns them into seconds with the frame rate.
    /// </summary>
    public static int[] Track(ReadOnlySpan<float> onsets, double bpm, double frameRate)
    {
        if (onsets.Length < 8 || bpm <= 0 || frameRate <= 0)
            return [];

        double period = 60.0 * frameRate / bpm;      // frames between beats
        if (period < 2 || period > onsets.Length)
            return [];

        int n = onsets.Length;
        var bestScore = new double[n];
        var backlink = new int[n];

        // How far back a predecessor may be: half a beat to twice a beat. Outside that range the
        // penalty is larger than any onset strength.
        int searchFrom = (int)Math.Round(period * 0.5);
        int searchTo = (int)Math.Round(period * 2.0);

        for (int i = 0; i < n; i++)
        {
            double best = double.NegativeInfinity;
            int bestFrom = -1;

            int lo = Math.Max(0, i - searchTo);
            int hi = i - searchFrom;
            for (int j = lo; j <= hi; j++)
            {
                // Log-Gaussian on the ratio of the actual gap to the wanted one. It treats "twice as
                // long" and "half as long" the same way, which a plain difference would not.
                double ratio = Math.Log((i - j) / period);
                double transition = -Tightness * ratio * ratio;
                double candidate = bestScore[j] + transition;
                if (candidate > best)
                {
                    best = candidate;
                    bestFrom = j;
                }
            }

            if (bestFrom < 0)
            {
                // No legal predecessor yet: this frame can only be the first beat of the track.
                bestScore[i] = onsets[i];
                backlink[i] = -1;
            }
            else
            {
                bestScore[i] = onsets[i] + best;
                backlink[i] = bestFrom;
            }
        }

        // Start the walk back from the best ending beat, but only look at the last stretch of the
        // track. The running score grows with every beat added, so the maximum is always near the
        // end, and searching the whole array only costs time.
        int tail = Math.Max(0, n - (int)Math.Round(period * 2));
        int end = tail;
        for (int i = tail; i < n; i++)
            if (bestScore[i] > bestScore[end])
                end = i;

        var beats = new List<int>();
        for (int at = end; at >= 0; at = backlink[at])
        {
            beats.Add(at);
            if (backlink[at] < 0)
                break;
        }
        beats.Reverse();
        return [.. beats];
    }

    /// <summary>
    /// Moves each beat off the frame grid and onto the true position of the onset peak, by fitting a
    /// parabola through the peak frame and its neighbours. One frame is 23 ms at the analysis rate.
    /// That is a large error at the top of the tempo range, and a grid that is 23 ms out puts a
    /// beat-matched mix audibly behind. Returns fractional frame positions.
    /// </summary>
    public static double[] Refine(ReadOnlySpan<float> onsets, IReadOnlyList<int> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var refined = new double[beats.Count];
        for (int i = 0; i < beats.Count; i++)
        {
            int at = beats[i];
            refined[i] = at;
            if (at <= 0 || at >= onsets.Length - 1)
                continue;

            double before = onsets[at - 1], peak = onsets[at], after = onsets[at + 1];
            // Only a real local peak can be interpolated. A beat on a flat stretch (a bar of silence
            // that the grid continued through) stays where the tracker put it.
            if (peak < before || peak < after)
                continue;

            double curvature = before - 2 * peak + after;
            if (Math.Abs(curvature) < 1e-9)
                continue;

            double offset = 0.5 * (before - after) / curvature;
            if (Math.Abs(offset) <= 0.5)
                refined[i] = at + offset;
        }
        return refined;
    }

    /// <summary>
    /// Replaces the tracked beats with a rigid grid at the measured tempo, placed where the tracked
    /// beats sit: one period, one phase, and a beat every period from the start of the track to the
    /// end.
    ///
    /// <para>The tracker may put a beat anywhere from half a beat to two beats after the previous
    /// one. That lets it continue through a bar of silence, but it also lets it drop a beat or add
    /// an extra one. That breaks later steps, because everything that lines up a mix counts beats by
    /// <em>index</em>: bar lines are every fourth entry in the list. One dropped beat rotates the bar
    /// phase for the rest of the track, and every changeover after it lands on the wrong beat of the
    /// bar. A track with more beats has more chances to drop one, so this shows first at the top of
    /// the tempo range.</para>
    ///
    /// <para>The grid also makes <see cref="TrackAnalysis.Bpm"/> agree with
    /// <see cref="TrackAnalysis.BeatTimes"/>. A mix changes the deck speed by a ratio calculated from
    /// the tempo, and then lands the drop on a beat taken from the grid. If the two disagree by half
    /// a percent, the tracks meet on the bar line and drift apart afterwards, so the beat-match fails
    /// within the eight bars it is needed for.</para>
    ///
    /// <para>Only the phase is taken from the tracked beats. The period comes from
    /// <paramref name="bpm"/>, which was measured by folding the whole track onto one beat
    /// (<see cref="TempoEstimator.PinDown"/>). A line fitted through a few hundred tracked beats
    /// cannot improve on it, because the tracker may have placed some beats on the offbeat or
    /// skipped them. What the tracker knows and the fold does not is where the "one" of the grid
    /// goes.</para>
    /// </summary>
    /// <param name="beats">Refined (fractional) beat positions from <see cref="Refine"/>.</param>
    /// <param name="frameRate">Frames per second of the envelope the beats index into.</param>
    /// <param name="bpm">The measured tempo, which sets the spacing of the grid.</param>
    /// <param name="frames">Length of the envelope, so the grid can be carried to the end of it.</param>
    public static double[] Regularize(IReadOnlyList<double> beats, double frameRate, double bpm, int frames)
    {
        ArgumentNullException.ThrowIfNull(beats);
        double period = bpm > 0 && frameRate > 0 ? 60.0 * frameRate / bpm : 0;
        if (beats.Count < 4 || period < 2 || frames < 2)
            return [.. beats];

        // Where the grid sits: the average position of the tracked beats within a beat. The average
        // is taken around a circle, not as a plain mean. "One frame after the beat" and "one frame
        // before the next beat" are the same position, and a plain mean of them is half a beat out.
        double x = 0, y = 0;
        foreach (double at in beats)
        {
            double turns = at / period;
            var (sin, cos) = Math.SinCos(2 * Math.PI * (turns - Math.Floor(turns)));
            x += cos;
            y += sin;
        }
        if (x * x + y * y < 1e-12)
            return [.. beats];

        double angle = Math.Atan2(y, x);
        if (angle < 0)
            angle += 2 * Math.PI;
        double phase = angle / (2 * Math.PI) * period;

        // The grid covers the whole track. The tracker starts at the first onset it is sure of and
        // stops at the last, but the intro and outro are where a changeover needs a bar line.
        int last = (int)Math.Floor((frames - 1 - phase) / period);
        if (last < 0)
            return [.. beats];

        var grid = new double[last + 1];
        for (int k = 0; k <= last; k++)
            grid[k] = phase + k * period;
        return grid;
    }
}

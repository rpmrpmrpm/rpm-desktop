namespace RPM.Core.Analysis;

/// <summary>
/// The tempo part of a beat-tracking state space: which beat lengths the search may consider, and
/// what it costs to change from one to another at a beat boundary.
///
/// <para>Shared by <see cref="BeatDbn"/> and <see cref="BarDbn"/> because the arithmetic is the same
/// in both and must match the same reference implementation. It has details that are easy to get
/// slightly wrong. Everything else about the two searches differs.</para>
/// </summary>
public static class TempoStates
{
    /// <summary>
    /// A transition weight below this is removed, not kept as a small value.
    ///
    /// <para>This is the distance from 1.0 to the next double, which is what <c>numpy.spacing(1)</c>
    /// returns and what madmom compares against. It matters more than it seems: over intervals of
    /// 14 to 55 frames it removes 884 of the 1764 possible tempo changes. The largest of those still
    /// scores <c>-36</c> in logs, which a search will accept when everything else is worse, and over
    /// a whole record that sometimes happens. Keeping them gives a different search.</para>
    /// </summary>
    private const double Vanishing = 2.220446049250313e-16;

    /// <summary>
    /// The beat lengths that the search covers, in whole frames, shortest first.
    ///
    /// <para>The limits are rounded to whole frames and both ends are included. At 50 frames a second
    /// and 55..215 BPM this gives 14 to 55, which is forty-two lengths. Both roundings are
    /// half-to-even, as in numpy and in <see cref="Math.Round(double)"/>.</para>
    /// </summary>
    public static int[] Intervals(double frameRate, double minBpm, double maxBpm)
    {
        if (frameRate <= 0 || minBpm <= 0 || maxBpm < minBpm)
            return [];

        int shortest = (int)Math.Round(frameRate * 60.0 / maxBpm);
        int longest = (int)Math.Round(frameRate * 60.0 / minBpm);
        if (shortest < 2 || longest < shortest)
            return [];

        var intervals = new int[longest - shortest + 1];
        for (int i = 0; i < intervals.Length; i++)
            intervals[i] = shortest + i;
        return intervals;
    }

    /// <summary>
    /// The cost, in logs, of leaving a beat of one length and starting one of another length.
    /// The index is <c>from * intervals.Length + to</c>. The value is
    /// <see cref="double.NegativeInfinity"/> where the change is not allowed at all.
    ///
    /// <para>The weight is an exponential of how far the tempo moves, <c>exp(-lambda * |to/from -
    /// 1|)</c>, so a record that holds its tempo is preferred and one that changes tempo pays for it.
    /// Each row is normalised so that leaving a beat at a given tempo is a probability distribution
    /// over where the next beat arrives.</para>
    ///
    /// <para>The order of the two steps matters. Anything below <see cref="Vanishing"/> is removed
    /// <b>before</b> the row is normalised, not after. This changes the remaining weights by about
    /// one part in 10^16, which is nothing, but it removes half the transitions, which is not.</para>
    /// </summary>
    public static double[] LogChange(int[] intervals, double lambda)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        int count = intervals.Length;
        var change = new double[count * count];

        for (int from = 0; from < count; from++)
        {
            double total = 0;
            for (int to = 0; to < count; to++)
            {
                double weight = Math.Exp(-lambda * Math.Abs((double)intervals[to] / intervals[from] - 1.0));
                if (weight <= Vanishing)
                    weight = 0;

                change[from * count + to] = weight;
                total += weight;
            }

            for (int to = 0; to < count; to++)
            {
                double weight = change[from * count + to];
                change[from * count + to] =
                    weight > 0 && total > 0 ? Math.Log(weight / total) : double.NegativeInfinity;
            }
        }

        return change;
    }

    /// <summary>The logistic function, computed in the form that does not overflow on either side of
    /// zero.</summary>
    public static double Sigmoid(double logit) =>
        logit >= 0 ? 1 / (1 + Math.Exp(-logit)) : Math.Exp(logit) / (1 + Math.Exp(logit));
}

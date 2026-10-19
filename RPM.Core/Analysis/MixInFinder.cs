namespace RPM.Core.Analysis;

/// <summary>
/// Where a record starts, when the previous record has already finished.
///
/// <para>A record mixed in over another one is cued <em>early</em>, so its intro plays under the
/// record before it (see <see cref="Mixing.TransitionPlanner.MixInPoint"/>). A record that starts
/// after silence has no record to play under, so its intro would be several bars of nothing
/// useful. It starts where the record itself starts.</para>
///
/// <para>
/// This is not where the sound starts. An intro is often a loop (the same bar or the same eight
/// bars repeated) so that a DJ can hold it under the previous record for as long as needed. The
/// record does not begin until that loop breaks. So the code looks for the first
/// <em>deviation</em>: the first bar that differs from the phrase before it. A kick-only opening
/// is the same case with the simplest possible loop. The bar where the deviation lands is the bar
/// the record starts on.
/// </para>
///
/// <para>The intro is skipped however long it is: two minutes of hi-hats are skipped, not played.
/// If the record never breaks at all, there is nothing to skip to, and at most
/// <see cref="MaxIntroSeconds"/> of intro plays before the record starts anyway.</para>
/// </summary>
public static class MixInFinder
{
    /// <summary>The longest intro that plays for a record with no deviation to start on. After this
    /// many seconds the record starts anyway.</summary>
    public const double MaxIntroSeconds = 60;

    /// <summary>The start point is never later than this share of the record, however little it
    /// seems to change. Two fifths into a record is the track itself, not an intro.</summary>
    private const double MostOfARecord = 0.4;

    /// <summary>
    /// How different a bar has to be from the one a phrase earlier to count as the record deviating,
    /// summed across the three bands as a share of what the record normally carries in each.
    ///
    /// <para>The value is loose enough to ignore small differences in a loop that is played live, and
    /// tight enough that a new part (a voice, a chord, a bassline, open hi-hats) passes it. A new part
    /// alone moves one band by most of its normal level.</para>
    /// </summary>
    private const double Deviation = 0.6;

    /// <summary>Below this share of the record's body a bar has nothing in it. The search
    /// starts at the first bar above this level, because silence before the sound is not a loop that
    /// the record breaks out of.</summary>
    private const double Silent = 0.08;

    /// <summary>The phrase lengths tried when the analyser could not find the record's own. Eight
    /// comes first because dance music is usually built in eight-bar phrases. Four is for a short
    /// intro that an eight-bar step would jump over.</summary>
    private static readonly int[] LoopBars = [8, 4];

    /// <summary>
    /// The bar line the record comes in on: the first bar that deviates from the phrase before it.
    ///
    /// <para>If there is no deviation, the fallbacks are, in order: the first bar where the middle of
    /// the record arrives, the end of the intro found by the analyser, and the start of the record.
    /// All of them are snapped to a bar line, because the new record must start on one.</para>
    /// </summary>
    public static double WhereItStarts(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        double fallback = Math.Max(0, analysis.IntroEndSeconds);

        var bass = analysis.BassCurve;
        var mid = analysis.MidCurve;
        var top = analysis.TopCurve;
        if (mid.Count == 0)
            return Snap(analysis, fallback);            // analysed before the bands were measured

        var lines = BarLines(analysis);
        if (lines.Count < 4)
            return Snap(analysis, MidComesIn(analysis) ?? fallback);

        // How much of each band each bar holds, and what the record normally holds in it.
        var overBars = new[] { PerBar(bass, lines), PerBar(mid, lines), PerBar(top, lines) };
        var body = new[] { Median(overBars[0]), Median(overBars[1]), Median(overBars[2]) };
        if (body.All(b => b <= 0))
            return Snap(analysis, MidComesIn(analysis) ?? fallback);

        int bars = overBars[0].Length;

        // The first bar with anything in it. What the record does before that is silence, not a loop
        // it is about to break out of.
        int first = 0;
        while (first < bars && Quiet(overBars, body, first))
            first++;
        if (first >= bars)
            return Snap(analysis, fallback);

        // However long the intro is, it is skipped. The only limit is that two fifths into a record
        // is the track itself, not its opening.
        double limit = analysis.DurationSeconds > 0
            ? analysis.DurationSeconds * MostOfARecord
            : double.MaxValue;

        // Try every phrase length that makes sense, and the earliest deviation wins. Use the record's
        // own phrase length if it is known, because a loop only repeats at the length it was written in.
        int[] loops = analysis.Phrases.IsKnown ? [analysis.Phrases.Bars] : LoopBars;
        int found = int.MaxValue;

        foreach (int loop in loops)
        {
            if (loop < 1 || first + loop >= bars)
                continue;

            // A bar breaks the intro when it differs from BOTH the bar one phrase earlier and the
            // record so far.
            //
            // Neither test works alone. Against the phrase before it, a loop with a fill is seen as
            // a break twice: at the fill, and one phrase later when the fill is the bar it is
            // compared with. Against the record so far, a loop that has one loud bar is seen as a
            // break every time round. Together the tests mean: the record does something new, and it
            // is not only one bar that happens to be different.
            bool Breaks(int bar) =>
                Deviates(overBars, body, bar, bar - loop) &&
                Apart(overBars, body, bar, Reference(overBars, first, bar)) > Deviation;

            for (int b = first + loop; b < bars && b < found; b++)
            {
                if (lines[b] > limit)
                    break;
                if (!Breaks(b))
                    continue;

                // The break must continue. One different bar is a fill. Two different bars mean the
                // record has moved on, which is what is wanted.
                if (b + 1 < bars && !Breaks(b + 1))
                    continue;

                found = b;
                break;
            }
        }

        if (found < int.MaxValue)
            return Math.Max(0, lines[found]);

        // Nothing broke: the record does not change, so there is no deviation to start on. Use the
        // point where the middle of the record arrives, but never later than MaxIntroSeconds.
        double start = MidComesIn(analysis) ?? fallback;
        return Snap(analysis, Math.Min(start, MaxIntroSeconds));
    }

    /// <summary>How much of the record's middle band has to be present before the record counts as
    /// started. It is the fallback for a record with no usable beat grid to count bars on.</summary>
    private const double Arrived = 0.5;

    /// <summary>How long the middle band has to stay present, so that a single hit is not taken as
    /// the record starting. It is about one bar at common dance tempos.</summary>
    private const double HoldSeconds = 2;

    /// <summary>The first time when the middle of the record arrives and stays. Null if it never
    /// does, or if there is no middle band to measure.</summary>
    private static double? MidComesIn(TrackAnalysis analysis)
    {
        var mid = analysis.MidCurve;
        if (mid.Count == 0)
            return null;

        double step = EnergyProfile.StepSeconds;
        double body = Median(mid);
        if (body <= 0)
            return null;

        int hold = Math.Max(1, (int)Math.Round(HoldSeconds / step));
        double wanted = body * Arrived;
        int from = (int)(Math.Max(0, analysis.AudioStartSeconds) / step);

        for (int at = from; at + hold <= mid.Count; at++)
        {
            bool holds = true;
            for (int i = at; i < at + hold && holds; i++)
                holds = mid[i] >= wanted;

            if (holds)
                return at * step;
        }

        return null;
    }

    /// <summary>Every bar line the grid names, in seconds.</summary>
    private static List<double> BarLines(TrackAnalysis analysis)
    {
        var lines = new List<double>();
        var beats = analysis.BeatTimes;
        int perBar = analysis.BeatsPerBar;
        if (beats.Count == 0 || perBar < 1)
            return lines;

        for (int beat = Math.Max(0, analysis.FirstDownbeat); beat < beats.Count; beat += perBar)
            lines.Add(beats[beat]);
        return lines;
    }

    /// <summary>One curve reduced to a level per bar.</summary>
    private static double[] PerBar(IReadOnlyList<float> curve, List<double> lines)
    {
        int bars = Math.Max(0, lines.Count - 1);
        var levels = new double[bars];
        if (curve.Count == 0)
            return levels;

        double step = EnergyProfile.StepSeconds;
        for (int b = 0; b < bars; b++)
        {
            int from = Math.Clamp((int)(lines[b] / step), 0, curve.Count - 1);
            int to = Math.Clamp((int)(lines[b + 1] / step), from + 1, curve.Count);

            double sum = 0;
            for (int i = from; i < to; i++)
                sum += curve[i];
            levels[b] = sum / (to - from);
        }

        return levels;
    }

    /// <summary>True when bar <paramref name="a"/> is not what bar <paramref name="b"/> was.</summary>
    private static bool Deviates(double[][] bands, double[] body, int a, int b) =>
        Apart(bands, body, a, [bands[0][b], bands[1][b], bands[2][b]]) > Deviation;

    /// <summary>How far a bar sits from a set of levels, summed across the bands as a share of what
    /// the record normally carries in each.</summary>
    private static double Apart(double[][] bands, double[] body, int bar, double[] from)
    {
        double apart = 0;
        for (int band = 0; band < bands.Length; band++)
        {
            if (body[band] <= 0)
                continue;
            apart += Math.Abs(bands[band][bar] - from[band]) / body[band];
        }
        return apart;
    }

    /// <summary>The record's level in each band before bar <paramref name="upto"/>: the median bar
    /// of each band so far, so one loud or one silent bar does not become the standard for the
    /// comparison.</summary>
    private static double[] Reference(double[][] bands, int first, int upto)
    {
        var levels = new double[bands.Length];
        int count = Math.Max(1, upto - first);

        for (int band = 0; band < bands.Length; band++)
        {
            var window = new double[count];
            Array.Copy(bands[band], first, window, 0, count);
            levels[band] = Median(window);
        }

        return levels;
    }

    /// <summary>True when there is nothing in this bar in any band.</summary>
    private static bool Quiet(double[][] bands, double[] body, int bar)
    {
        for (int band = 0; band < bands.Length; band++)
            if (body[band] > 0 && bands[band][bar] > body[band] * Silent)
                return false;
        return true;
    }

    /// <summary>Moves to a bar line at or before the point found. A part starts ON the bar, and a
    /// reading that is half a step late would start the record half a step into the bar.</summary>
    private static double Snap(TrackAnalysis analysis, double seconds) =>
        Math.Max(0, analysis.BarAtOrBefore(Math.Max(0, seconds)));

    private static double Median(IReadOnlyList<float> curve) => Median([.. curve.Select(v => (double)v)]);

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}

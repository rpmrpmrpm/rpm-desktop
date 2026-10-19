namespace RPM.Core.Analysis;

/// <summary>
/// A record's tempo measured one stretch at a time, not once for the whole record, and the beat
/// numbering that follows from that method.
///
/// <para><b>Why not one number.</b> <see cref="ModelBeatGrid"/> fits one straight line through every
/// beat the model read. As an estimator this works very well: the error of the model's twenty
/// millisecond frame is random noise, and least squares over five hundred beats gives a period good
/// to about four ten thousandths of a percent, a hundred times finer than a changeover can hear. So
/// no observed error is a resolution error. Each one is one of two other things: the beats were
/// <em>numbered</em> wrongly, or the record really does not hold one tempo. This class measures
/// both, and the same arithmetic answers both questions.</para>
///
/// <para><b>Numbering.</b> A beat's number must not be worked out by dividing its distance from the
/// first beat by one period measured over the whole track. That works until it fails: an error of a
/// tenth of a percent in the period is half a beat of slip by the five hundredth beat, and once one
/// beat is numbered wrongly the line fitted through the numbering is consistent with itself and
/// wrong. No later refinement can recover from that. Here each beat's number is decided from the gap
/// to the previous beat, divided by a period measured within a couple of bars of that gap, so errors
/// do not accumulate and the numbering cannot drift.</para>
///
/// <para><b>Drift.</b> After the beats are numbered, a line is fitted inside each window, and the
/// windows together show the record's tempo over time. Records sequenced to a click give the same
/// answer in every window. A band, a vinyl rip or a record with tempo automation does not, and this
/// shows the difference between those two cases.</para>
/// </summary>
public static class TempoWindows
{
    /// <summary>
    /// How many beats a window is measured over. Sixteen bars of four, which is the length of a
    /// changeover: the length over which <see cref="Mixing.TransitionPlanner"/> asks two records to
    /// hold together, and so the length over which "what tempo is this record" is the question.
    ///
    /// <para>A long window costs nothing here. Sixty-four beats of twenty millisecond frames fit a
    /// period to about a hundredth of a percent, so the window could be much shorter before
    /// resolution mattered. A longer window gives immunity to a few beats that the model misplaced,
    /// and costs the ability to see a tempo change inside the window.</para>
    /// </summary>
    public const int WindowBeats = 64;

    /// <summary>How far apart windows start: half a window, so every stretch of the record is covered
    /// by two windows and the reading between two anchors is never an extrapolation.</summary>
    public const int HopBeats = WindowBeats / 2;

    /// <summary>With fewer beats than this there is no window worth fitting a line in. The whole
    /// reading is treated as one window, and this becomes the single global fit.</summary>
    public const int MinWindowBeats = 16;

    /// <summary>
    /// One window's reading: the straight line <c>seconds = Phase + Period × beat number</c> that best
    /// explains the beats inside it, reported at the window's own middle so that neighbouring windows
    /// can be joined and neither one has to describe ground that the other measured.
    /// </summary>
    /// <param name="Index">The beat number where the window is anchored: its middle.</param>
    /// <param name="Seconds">Where that beat falls, on this window's line.</param>
    /// <param name="Period">How long a beat is here, in seconds.</param>
    /// <param name="Density">
    /// The share of the beats on the window's line that the model actually reported, 0..1. It is one
    /// for a window that was read straight through.
    ///
    /// <para>A breakdown shows itself through this value. The model finds nothing where there is
    /// nothing to find, so a window over a breakdown holds a few beats on each side of a hole, and a
    /// line fitted through it is a bridge and not a measurement: it is right at the two ends and says
    /// nothing about the middle. The line is still the best guess for that stretch and is still used,
    /// but it gets no vote on the record's tempo.</para>
    /// </param>
    public readonly record struct Anchor(double Index, double Seconds, double Period, double Density);

    /// <summary>
    /// A whole record read this way.
    ///
    /// <para><see cref="Anchors"/> is in increasing order of both index and time, which makes
    /// <see cref="TimeAt"/> monotone and so makes a bent grid safe: every beat number still names
    /// exactly one time, later than the one before it, so counting bars across a bend gives the same
    /// result as counting bars across a straight line. <see cref="BeatTracker.Regularize"/> exists to
    /// protect this property, and it holds across the bend.</para>
    /// </summary>
    /// <param name="Index">The beat number given to each beat of the reading, in the reading's own
    /// order. It starts at zero and never decreases.</param>
    /// <param name="Anchors">One per window, in order.</param>
    /// <param name="Period">The record's tempo as one number: the middle of what the windows said.
    /// This is the value that goes in the table and that a crate is sorted on.</param>
    /// <param name="Spread">How far apart the windows' readings are, as a share of
    /// <see cref="Period"/>: zero on a sequenced record, and the size of the drift on a record that
    /// wanders. A tenth is ignored at each end, so one badly read window does not decide the
    /// result for the record.</param>
    public readonly record struct Reading(
        int[] Index,
        Anchor[] Anchors,
        double Period,
        double Spread)
    {
        public static readonly Reading None = new([], [], 0, 0);

        public bool IsUsable => Period > 0 && Anchors.Length > 0;

        /// <summary>
        /// Where a beat number falls, in seconds, on the record's own clock including its bends.
        ///
        /// <para>The line is straight between the anchors, and it continues beyond the end anchors at
        /// their own periods. This is how the grid reaches the silence before the first beat the model
        /// heard and the tail after the last. The index may be fractional, so that a tempo folded by an
        /// octave can be read from the same curve: at half the tempo, beat <c>k</c> of the new grid is
        /// beat <c>2k</c> of this one.</para>
        /// </summary>
        public double TimeAt(double index)
        {
            var anchors = Anchors;
            if (anchors.Length == 0)
                return 0;

            if (anchors.Length == 1 || index <= anchors[0].Index)
                return anchors[0].Seconds + (index - anchors[0].Index) * anchors[0].Period;

            var last = anchors[^1];
            if (index >= last.Index)
                return last.Seconds + (index - last.Index) * last.Period;

            // Find the segment that contains the index. A record has a few dozen anchors, so a
            // linear walk is cheaper than the arithmetic needed to avoid it. This is called once per
            // beat while a grid is built, never on the audio thread.
            int at = 0;
            while (at + 2 < anchors.Length && anchors[at + 1].Index <= index)
                at++;

            var from = anchors[at];
            var to = anchors[at + 1];
            double span = to.Index - from.Index;
            return span <= 0
                ? from.Seconds
                : from.Seconds + (to.Seconds - from.Seconds) * (index - from.Index) / span;
        }
    }

    /// <summary>
    /// Reads <paramref name="beats"/> (the model's beat times, in order) as a numbering and a set of
    /// windows.
    /// </summary>
    public static Reading Read(IReadOnlyList<double> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        if (beats.Count < 2)
            return Reading.None;

        // A beat that the reader marked twice is removed before anything else looks at the record.
        // See Merge, and see Number for what happens when it is not removed.
        beats = Merge(beats) ?? beats;

        var local = LocalPeriods(beats);
        if (local.Length == 0)
            return Reading.None;

        var index = Number(beats, local);
        var anchors = FitWindows(beats, index);
        if (anchors.Length == 0)
            return Reading.None;

        // Only windows that read their own beats get a vote (see Anchor.Density). If none of them
        // did, all windows vote: a record that the model read thinly all the way through still has a
        // tempo, and it is the one that those thin readings agree on.
        var periods = Voting(anchors, DensityToVote) is { Length: > 0 } voting
            ? voting
            : Voting(anchors, 0);

        Array.Sort(periods);
        double period = periods[periods.Length / 2];
        if (period <= 0)
            return Reading.None;

        int tail = periods.Length / 10;
        double spread = (periods[periods.Length - 1 - tail] - periods[tail]) / period;

        return new Reading(index, anchors, period, spread);
    }

    /// <summary>
    /// How much of a window the model must have read before the window may say what the record's
    /// tempo is. Three quarters: a bar missed here and there is a normal reading, and a window that
    /// is a quarter hole is a window over a breakdown.
    /// </summary>
    private const double DensityToVote = 0.75;

    private static double[] Voting(Anchor[] anchors, double floor) =>
        [.. anchors.Where(a => a.Density >= floor).Select(a => a.Period)];

    /// <summary>How far a gap may be from the middle of its neighbours and still count as one beat.
    /// A quarter: well outside anything a record does to its own pulse, and well inside the gap
    /// left where the model missed a beat, which is twice as long.</summary>
    private const double GapTolerance = 0.25;

    /// <summary>
    /// Two beats closer together than this share of a beat are one beat read twice.
    ///
    /// <para>The value is a third, and it comes from what a reader can produce, not from preference.
    /// <see cref="BeatModelDecoder"/> requires two peaks to be three frames apart to count as two
    /// beats, so the closest pair it can report is sixty milliseconds apart. That is between a
    /// fifteenth and a quarter of a beat across the whole tempo range. Nothing in real music comes
    /// near this: a rubato ballad that wanders by a fifth of a beat still has its closest pair at
    /// nearly two thirds of a beat, and merging those would turn a record that this app must refuse
    /// to beat-match into one that it would try to match.</para>
    /// </summary>
    public const double SameBeatWithin = 1.0 / 3;

    /// <summary>
    /// Beats that the reader marked twice, merged into the one beat they are, at their midpoint. The
    /// result is null when there were none, which is true for most records.
    ///
    /// <para>This must happen before <see cref="Number"/>. A pair eighty milliseconds apart is a
    /// fifth of a beat at 128 BPM, so the numbering rounds the step between them to zero. A step of
    /// zero cannot be held in a numbering, so it is raised to one and the false beat becomes a real
    /// one. Every later beat is then numbered one too high, and the line fitted through that numbering
    /// is consistent with itself and wrong. Nothing downstream can recover from that.</para>
    ///
    /// <para><b>What it is worth, and where.</b> On <see cref="BarDbn"/>'s beats it changes nothing:
    /// the readings are identical to the last digit on both corpora, because a search whose state
    /// space is one beat cannot leave two beats a third of a beat apart. On peak-picked beats it is
    /// worth 3.2 points of Accuracy1 and 3.3 of Accuracy2 on GiantSteps Tempo, and 0.8 and 1.0 on
    /// GTZAN. So it is kept for the path that needs it: the picker is what
    /// <see cref="BeatModelDecoder.Bars"/> uses when the search declines, and a fallback that
    /// silently reads a record at twice its tempo is worse than no fallback.</para>
    ///
    /// <para><b>Do not loosen the rule.</b> At six tenths of a beat instead of a third, the merge
    /// appeared to give the bar search 2.7 points of Accuracy2, but it merged nothing that was
    /// doubled, because the bar search leaves nothing doubled to merge. What it merged were real
    /// beats on records whose tempo moves, where six tenths of the typical gap is a normal gap. That
    /// is smoothing, not a gain. The threshold is therefore set by what a reader can physically
    /// produce, not by what scores well.</para>
    /// </summary>
    private static IReadOnlyList<double>? Merge(IReadOnlyList<double> beats)
    {
        double period = LongEnoughGap(beats);
        if (period <= 0)
            return null;

        double shortest = period * SameBeatWithin;

        bool any = false;
        for (int i = 1; i < beats.Count && !any; i++)
            any = beats[i] - beats[i - 1] < shortest;

        if (!any)
            return null;

        var kept = new List<double>(beats.Count) { beats[0] };
        for (int i = 1; i < beats.Count; i++)
        {
            if (beats[i] - kept[^1] < shortest)
                kept[^1] = (kept[^1] + beats[i]) / 2;
            else
                kept.Add(beats[i]);
        }

        return kept.Count >= 2 ? kept : null;
    }

    /// <summary>
    /// A gap long enough to be a whole beat: the seventy-fifth percentile of the gaps.
    ///
    /// <para>It is not the median, and this is the key point of this function. Where a reader has
    /// marked every beat twice, the gaps alternate short and long in equal numbers, so the median gap
    /// is a coin toss between the two and is as often short as long. A rule based on that number
    /// would merge nothing and the record would be read at twice its tempo. At the seventy-fifth
    /// percentile, even when half the gaps are false, the estimate falls on a true one.</para>
    ///
    /// <para>It also works for the opposite fault. A reader that missed half the beats leaves gaps of
    /// one beat and of two beats, the estimate lands on two, and a third of two beats is still
    /// shorter than one beat, so nothing real is merged. The estimate only has to be within a factor
    /// of three of a beat, and no reading of a record puts it outside that.</para>
    /// </summary>
    private static double LongEnoughGap(IReadOnlyList<double> beats)
    {
        var gaps = new List<double>(beats.Count - 1);
        for (int i = 1; i < beats.Count; i++)
            if (beats[i] - beats[i - 1] > 0)
                gaps.Add(beats[i] - beats[i - 1]);

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        return gaps[(int)(gaps.Count * 0.75)];
    }

    /// <summary>
    /// How long a beat is in the neighbourhood of each gap.
    ///
    /// <para>This is the mean of the nearby gaps that look like single beats, not their median. The
    /// model's beats sit on a twenty millisecond frame, so at 128 BPM (a beat of 23.44 frames) the
    /// gaps it reports are 23 frames more often than 24, and the median is therefore short by nearly
    /// two percent. The mean has no such bias, because the roundings cancel. The median is used only
    /// to decide which gaps to average.</para>
    /// </summary>
    private static double[] LocalPeriods(IReadOnlyList<double> beats)
    {
        int count = beats.Count - 1;
        var gaps = new double[count];
        for (int i = 0; i < count; i++)
            gaps[i] = beats[i + 1] - beats[i];

        var local = new double[count];
        int half = Math.Max(MinWindowBeats, WindowBeats) / 2;
        var slice = new double[Math.Min(count, half * 2 + 1)];

        for (int i = 0; i < count; i++)
        {
            int from = Math.Max(0, i - half);
            int to = Math.Min(count - 1, i + half);
            int n = to - from + 1;

            Array.Copy(gaps, from, slice, 0, n);
            Array.Sort(slice, 0, n);
            double middle = slice[n / 2];

            double sum = 0;
            int counted = 0;
            if (middle > 0)
                for (int k = from; k <= to; k++)
                    if (Math.Abs(gaps[k] - middle) <= middle * GapTolerance)
                    {
                        sum += gaps[k];
                        counted++;
                    }

            local[i] = counted > 0 ? sum / counted : middle;
        }

        return local;
    }

    /// <summary>
    /// Which beat of the record each reading is.
    ///
    /// <para>The numbering goes one step at a time, and each step is decided against the period
    /// measured next to it. This is the main purpose of the windows. A number worked out as
    /// <c>(time − first beat) ÷ one period</c> multiplies any error in that period by the number of
    /// beats that have passed, so a tenth of a percent is half a beat of slip five hundred beats in,
    /// and the record ends up numbered wrongly by one. Deciding each step from one gap and a local
    /// period multiplies nothing: the error stays the error of a single rounding, and a rounding is
    /// wrong only if a beat length is out by half.</para>
    ///
    /// <para>A gap of several beats (a breakdown where the model found nothing) is numbered the same
    /// way and is just as safe, because the period it is divided by was measured on each side of
    /// it.</para>
    /// </summary>
    private static int[] Number(IReadOnlyList<double> beats, double[] local)
    {
        var index = new int[beats.Count];
        for (int i = 1; i < beats.Count; i++)
        {
            double period = local[i - 1];
            int step = period > 0 ? (int)Math.Round((beats[i] - beats[i - 1]) / period) : 1;
            index[i] = index[i - 1] + Math.Max(1, step);
        }
        return index;
    }

    /// <summary>
    /// A line fitted inside each window, reported at the window's middle.
    ///
    /// <para>Windows are counted in beats of the reading, not in seconds, so that two machines
    /// reading the same track cut the windows in the same places. The shared pool sees two machines
    /// as agreeing about a record only when they write the same text, and a grid whose bends depended
    /// on where a second happened to fall would cause permanent disagreement.</para>
    /// </summary>
    private static Anchor[] FitWindows(IReadOnlyList<double> beats, int[] index)
    {
        int count = beats.Count;
        if (count < MinWindowBeats)
            return FitOne(beats, index, 0, count - 1) is { } only ? [only] : [];

        var anchors = new List<Anchor>(count / HopBeats + 2);
        for (int from = 0; ; from += HopBeats)
        {
            int to = Math.Min(count - 1, from + WindowBeats - 1);

            // The last window is moved back to full length instead of being run short, so the end of
            // a record is measured over the same span as the rest of it.
            if (to - from + 1 < WindowBeats)
                from = Math.Max(0, count - WindowBeats);

            if (FitOne(beats, index, from, Math.Min(count - 1, from + WindowBeats - 1)) is { } anchor
                && (anchors.Count == 0 || anchor.Index > anchors[^1].Index))
                anchors.Add(anchor);

            if (from + WindowBeats >= count)
                break;
        }

        return [.. anchors];
    }

    /// <summary>Least squares through (beat number, time) over one span, reported at its middle. The
    /// result is null when every beat in the span has the same number, which leaves nothing to
    /// fit.</summary>
    private static Anchor? FitOne(IReadOnlyList<double> beats, int[] index, int from, int to)
    {
        int n = to - from + 1;
        if (n < 2)
            return null;

        double meanIndex = 0, meanTime = 0;
        for (int i = from; i <= to; i++)
        {
            meanIndex += index[i];
            meanTime += beats[i];
        }
        meanIndex /= n;
        meanTime /= n;

        double covariance = 0, variance = 0;
        for (int i = from; i <= to; i++)
        {
            double k = index[i] - meanIndex;
            covariance += k * (beats[i] - meanTime);
            variance += k * k;
        }
        if (variance < 1e-9)
            return null;

        double period = covariance / variance;
        if (period <= 0)
            return null;

        int spanned = index[to] - index[from] + 1;
        double density = spanned > 0 ? Math.Min(1, (double)n / spanned) : 0;
        return new Anchor(meanIndex, meanTime, period, density);
    }
}

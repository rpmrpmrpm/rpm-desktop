namespace RPM.Core.Analysis;

/// <summary>
/// Where a record's phrases begin: how many bars long they are, and which bar the first one starts
/// on.
/// </summary>
/// <param name="Bars">Bars to a phrase: 8, 16 or 32, or 0 when the record has no phrasing worth
/// the name (live, free-time, or simply not built that way).</param>
/// <param name="FirstBar">Which bar of the track the first phrase starts on, counted from the first
/// downbeat. Always less than <paramref name="Bars"/>.</param>
/// <param name="Confidence">How far ahead of the alternatives the answer was, 0..1.</param>
public readonly record struct PhraseGrid(int Bars, int FirstBar, double Confidence)
{
    public bool IsKnown => Bars > 0;
}

/// <summary>
/// The parts of a record's shape that are not its beat grid: where its phrases fall, how steady it
/// is played, where the beat comes back after a breakdown, and how loud it is at the two points a
/// mix actually meets it.
///
/// <para>Every one of these is arithmetic over curves the analyser has already built: no decode of
/// its own, no model, and a tiny share of what one track costs. They are here rather than inside
/// <see cref="TrackAnalyzer"/> because each is a judgement with a threshold in it, and a judgement
/// is easier to read and test on its own.</para>
/// </summary>
public static class TrackShape
{
    /// <summary>
    /// What <see cref="TempoStability"/> answers when nothing measured it: a record read from the
    /// pool rather than from a disk, or one with too few beats to say.
    ///
    /// <para>This is its own value rather than the bottom of the scale, because the two mean
    /// opposite things: the bottom is "this record wanders and cannot be held on a tempo", and this
    /// is "no idea, treat it as any other record". Most records are steady, so an unknown that was
    /// read as the worst case would shorten fades across a whole library that the pool had answered
    /// for.</para>
    /// </summary>
    public const double Unmeasured = 0;

    /// <summary>The phrase lengths worth testing. Dance music is built in eights and their multiples,
    /// and a "phrase" of four bars is only a bar line with extra steps.</summary>
    private static readonly int[] PhraseLengths = [8, 16, 32];

    /// <summary>How much better the winning phase must be than the average bar before the answer is
    /// believed at all. Below it the record has no phrasing this can see.</summary>
    private const double MinPhraseLead = 1.35;

    /// <summary>How close to the best a shorter phrase length has to come to be preferred over it.
    /// Four fifths: a real difference in how the record is built beats it, and the arithmetic
    /// advantage a longer spacing gets from sampling fewer bars does not.</summary>
    private const double NearEnough = 0.8;

    /// <summary>
    /// Where the phrases fall.
    ///
    /// <para>A phrase boundary is where the record <em>changes</em>: a part comes in, the bass drops
    /// out, the vocal starts. So the bass curve is reduced to one figure per bar, the size of the step
    /// between consecutive bars is the evidence, and the question becomes: at which spacing, and at
    /// which offset, do those steps line up? Every candidate spacing is scored against the average
    /// bar, so a record with no phrasing scores nothing and is answered as such rather than being
    /// given a phrase grid it does not have.</para>
    ///
    /// <para>It is read from the bass rather than the whole band, as with the cue points: what
    /// marks a phrase in dance music is the kick and the bassline arriving or leaving, and a full-band
    /// meter cannot tell that from a shout over the same drums.</para>
    /// </summary>
    /// <param name="bassLevels">The bass curve, one reading every <see cref="EnergyProfile.StepSeconds"/>.</param>
    /// <param name="beatTimes">The beat grid.</param>
    /// <param name="firstDownbeat">Index into <paramref name="beatTimes"/> of the first bar line.</param>
    /// <param name="beatsPerBar">Beats to a bar.</param>
    public static PhraseGrid Phrases(
        IReadOnlyList<float> bassLevels,
        IReadOnlyList<double> beatTimes,
        int firstDownbeat,
        int beatsPerBar)
    {
        ArgumentNullException.ThrowIfNull(bassLevels);
        ArgumentNullException.ThrowIfNull(beatTimes);

        if (beatsPerBar <= 0 || bassLevels.Count < 4)
            return default;

        var bars = BarLevels(bassLevels, beatTimes, firstDownbeat, beatsPerBar);

        // Two phrases of the shortest candidate is the least that can show a repeat. Below it the
        // strongest spacing is whichever one happens to land on the loudest bar.
        if (bars.Length < PhraseLengths[0] * 2 + 1)
            return default;

        // How much the record changed at each bar line. Absolute, because a part leaving is as much a
        // boundary as a part arriving: a breakdown starts a phrase.
        var change = new double[bars.Length];
        for (int b = 1; b < bars.Length; b++)
            change[b] = Math.Abs(bars[b] - bars[b - 1]);

        double average = change.Skip(1).DefaultIfEmpty(0).Average();
        if (average <= 0)
            return default;

        // Each candidate length is scored, and then the SHORTEST one that explains the record is
        // taken.
        //
        // Comparing lengths on their scores alone always answers thirty-two. A longer spacing
        // samples fewer bars, so it can land on the three biggest changes in the record and skip
        // everything that would count against it, while eights are scored against every eighth bar,
        // including the ones where nothing happens. On a real library every record came back as
        // thirty-twos, which says nothing about the music.
        //
        // If a record's sixteens work, its eights work as well, so the shorter answer is the true
        // one. It is also the safer place to land a changeover: the wait for the next boundary is
        // shorter, and being wrong about it costs less.
        var scores = new Dictionary<int, (int Phase, double Score)>();

        foreach (int length in PhraseLengths)
        {
            if (bars.Length < length * 2)
                continue;

            int bestPhaseHere = 0;
            double bestScoreHere = 0;

            for (int phase = 0; phase < length; phase++)
            {
                double sum = 0;
                int count = 0;
                for (int b = phase; b < change.Length; b += length)
                {
                    // The first bar is skipped rather than scored as no change: nothing precedes it,
                    // so how much the record changed there is unknowable. Counting it as zero drags
                    // down whichever spacing happens to start at the top of the track.
                    if (b == 0)
                        continue;

                    sum += change[b];
                    count++;
                }

                if (count < 2)
                    continue;

                double score = sum / count / average;
                if (score > bestScoreHere + 0.001)
                {
                    bestScoreHere = score;
                    bestPhaseHere = phase;
                }
            }

            if (bestScoreHere > 0)
                scores[length] = (bestPhaseHere, bestScoreHere);
        }

        if (scores.Count == 0)
            return default;

        double strongest = scores.Values.Max(v => v.Score);

        int bestLength = 0, bestPhase = 0;
        double bestScore = 0;
        foreach (int length in PhraseLengths)
        {
            if (!scores.TryGetValue(length, out var candidate))
                continue;

            // Near enough to the best is the same answer at a shorter spacing.
            if (candidate.Score < strongest * NearEnough)
                continue;

            bestLength = length;
            bestPhase = candidate.Phase;
            bestScore = candidate.Score;
            break;
        }

        if (bestLength == 0 || bestScore < MinPhraseLead)
            return default;

        // The lead over an average bar, mapped onto 0..1: at the threshold it is nothing, at twice
        // the average bar it is certain.
        double confidence = Math.Clamp((bestScore - MinPhraseLead) / (2.0 - MinPhraseLead), 0, 1);
        return new PhraseGrid(bestLength, bestPhase, confidence);
    }

    /// <summary>One bass figure per bar, from the first downbeat on.</summary>
    private static double[] BarLevels(
        IReadOnlyList<float> bassLevels, IReadOnlyList<double> beatTimes, int firstDownbeat, int beatsPerBar)
    {
        if (beatTimes.Count <= firstDownbeat + beatsPerBar)
            return [];

        var bars = new List<double>((beatTimes.Count - firstDownbeat) / beatsPerBar);

        for (int beat = Math.Max(0, firstDownbeat); beat + beatsPerBar <= beatTimes.Count - 1; beat += beatsPerBar)
        {
            double from = beatTimes[beat];
            double to = beatTimes[beat + beatsPerBar];

            int start = (int)(from / EnergyProfile.StepSeconds);
            int end = (int)Math.Ceiling(to / EnergyProfile.StepSeconds);
            if (start >= bassLevels.Count)
                break;

            end = Math.Min(end, bassLevels.Count);
            double sum = 0;
            int count = 0;
            for (int i = start; i < end; i++, count++)
                sum += bassLevels[i];

            bars.Add(count > 0 ? sum / count : 0);
        }

        return [.. bars];
    }

    /// <summary>
    /// How steadily the record is played, 0..1: 1 is a machine and 0 is a band that speeds up.
    ///
    /// <para>It decides how hard a deck may be pulled and how long a fade may run. A sequenced
    /// record can be bent two percent and held there for thirty-two bars. A live one drifts under the
    /// pull, so the same fade ends somewhere other than where it started.</para>
    ///
    /// <para><b>Measured against the grid, not from it.</b> The obvious reading (how much the
    /// intervals between beats vary) answers 1 for every record, because
    /// <see cref="BeatTracker.Regularize"/> replaces the tracked beats with a straight line through
    /// them before anything downstream sees them. The line is the right thing to mix on and the wrong
    /// thing to measure steadiness with: it is even by construction. The evidence is the difference
    /// between the two: where the beats were actually heard, against where the ruler says they should
    /// be. A sequenced record's beats sit on the line all the way through. A drifting record's beats
    /// wander off it and back.</para>
    /// </summary>
    /// <param name="tracked">The beats as they were heard, before the grid was straightened.</param>
    /// <param name="grid">The straight line through them: <see cref="TrackAnalysis.BeatTimes"/>.</param>
    public static double TempoStability(IReadOnlyList<double> tracked, IReadOnlyList<double> grid)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(grid);

        if (tracked.Count < 8 || grid.Count < 8)
            return 0;

        double period = (grid[^1] - grid[0]) / (grid.Count - 1);
        if (period <= 0)
            return 0;

        // How far each heard beat sits from the nearest line of the ruler, as a share of one beat.
        // The nearest line, not the matching one: if the tracker dropped a beat, every beat after it
        // would otherwise count as drift.
        //
        // The ruler is walked rather than divided by one period, because ModelBeatGrid bends the
        // grid on a record that does not hold a tempo. Dividing would measure such a record against
        // a straight line that the grid deliberately is not, and a record whose beats the ruler
        // follows exactly would be reported as the least steady thing in the library. That is
        // backwards, and it would shorten every fade on it.
        var offsets = new double[tracked.Count];
        int at = 0;
        for (int i = 0; i < tracked.Count; i++)
        {
            // Both lists are in order, so the search carries on from where the last one finished.
            while (at + 1 < grid.Count && grid[at + 1] <= tracked[i])
                at++;

            double away = Math.Abs(tracked[i] - grid[at]);
            if (at + 1 < grid.Count)
                away = Math.Min(away, Math.Abs(grid[at + 1] - tracked[i]));

            // Measured against the beat it landed in rather than the record's average, for the same
            // reason.
            double here = at + 1 < grid.Count ? grid[at + 1] - grid[at]
                : at > 0 ? grid[at] - grid[at - 1]
                : period;

            offsets[i] = here > 0 ? away / here : 0;
        }

        double drift = Median(offsets);

        // A fiftieth of a beat is a sequencer and the odd late snare. A fifth is a record whose beats
        // are somewhere else entirely from where a ruler would put them, which is more than a fade of
        // thirty-two bars can survive.
        //
        // The result is floored just above zero, because zero is reserved for Unmeasured. A reading
        // borrowed from the pool cannot carry this value: it is the difference between where the
        // beats were heard and where the ruler puts them, and only the ruler travels. So zero has to
        // mean "nobody measured it". Without the floor, the loosest record ever pressed and a record
        // nobody has listened to would give the same number, and the reader would have to treat an
        // unknown as the worst case: short fades and no pull, on every record the pool answered for.
        return Math.Max(Unmeasured + 0.01, Math.Clamp(1 - (drift - 0.02) / (0.20 - 0.02), 0, 1));
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0)
            return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>The most drops a record is credited with. Past three they are not drops, they are the
    /// arrangement.</summary>
    private const int MostDrops = 3;

    /// <summary>How much louder the bottom end has to get, as a multiple of what it was, to count as
    /// the beat coming back rather than a bar that happens to hit harder.</summary>
    private const double DropRise = 2.2;

    /// <summary>
    /// Where the beat comes back in: the end of a breakdown.
    ///
    /// <para>This is the best place in a record to land another one. It is not the intro and it is not
    /// the body: it is the bar where the kick returns after being gone, and mixing into it is the move
    /// a listener hears as deliberate. It is also the strongest arrival point the record has, so a
    /// changeover timed to it sounds like a peak rather than a handover.</para>
    ///
    /// <para>A drop is found as a large rise in the bass across a bar line where the bars before it
    /// were quiet, snapped to that bar line. Records with no breakdown have none, and get an empty
    /// list rather than a best guess.</para>
    /// </summary>
    public static IReadOnlyList<double> Drops(
        IReadOnlyList<float> bassLevels,
        IReadOnlyList<double> beatTimes,
        int firstDownbeat,
        int beatsPerBar)
    {
        ArgumentNullException.ThrowIfNull(bassLevels);
        ArgumentNullException.ThrowIfNull(beatTimes);

        if (beatsPerBar <= 0 || beatTimes.Count < beatsPerBar * 4)
            return [];

        var bars = BarLevels(bassLevels, beatTimes, firstDownbeat, beatsPerBar);
        if (bars.Length < 8)
            return [];

        double body = Median(bars);
        if (body <= 0)
            return [];

        var found = new List<(double Time, double Rise)>();

        // From the second bar on, and never in the last four: a rise at the very end of a record is
        // the outro's last chorus, and nothing is going to be mixed into it.
        for (int b = 2; b < bars.Length - 4; b++)
        {
            double before = (bars[b - 1] + bars[b - 2]) / 2;
            if (before >= body * 0.6)
                continue;                       // it never went away, so nothing came back

            if (bars[b] < body * 0.9 || bars[b] < before * DropRise)
                continue;

            int beat = firstDownbeat + b * beatsPerBar;
            if (beat >= beatTimes.Count)
                break;

            found.Add((beatTimes[beat], bars[b] / Math.Max(before, 1e-6)));
        }

        return [.. found.OrderByDescending(d => d.Rise).Take(MostDrops).Select(d => d.Time).Order()];
    }

    /// <summary>How much of the record is averaged for a level at a cue: a few bars either way, so
    /// one loud bar does not decide it.</summary>
    private const double CueWindowSeconds = 6;

    /// <summary>
    /// How loud the record is at one moment, on the same 0..1 scale as
    /// <see cref="TrackAnalysis.Intensity"/>.
    ///
    /// <para>Levelling knows what a record averages over its whole length. A changeover does not meet
    /// that average: it meets the outgoing record's outro and the incoming record's intro, and those
    /// can be six decibels away from the level the levelling settled on. Knowing both levels lets a
    /// fade arrive at the same loudness it left.</para>
    /// </summary>
    public static double LevelAt(IReadOnlyList<float> levels, double seconds)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0 || seconds < 0)
            return 0;

        int half = Math.Max(1, (int)(CueWindowSeconds / 2 / EnergyProfile.StepSeconds));
        int at = (int)(seconds / EnergyProfile.StepSeconds);
        int start = Math.Clamp(at - half, 0, levels.Count - 1);
        int end = Math.Clamp(at + half, start + 1, levels.Count);

        double sum = 0;
        for (int i = start; i < end; i++)
            sum += levels[i];

        return EnergyProfile.Rescale(sum / (end - start));
    }
}

namespace RPM.Core.Analysis;

/// <summary>
/// Turns what a beat model found into the grid that the rest of the app mixes against: a beat every
/// period from the start of the track to the end, a bar line every so many beats, and one tempo for
/// the table.
///
/// <para>The model is better than the DSP at the two things the DSP does worst: which octave the
/// tempo is in, and where the "one" of the bar falls. It is worse at the one thing the DSP does
/// perfectly, which is placing a beat to the sample. Its beats arrive on a twenty millisecond frame
/// grid, so two beats of a record with a steady tempo can be a frame apart. A beat-match built on
/// those beats drifts by more than the fifteen milliseconds that <see cref="Mixing.TransitionPlanner"/>
/// allows for a whole changeover.</para>
///
/// <para>So the model is used for what it knows, and the grid is rebuilt for what it does not know.
/// The grid is fitted to <see cref="TempoWindows"/> (the record read one stretch at a time), not to
/// one line through the whole record, for two reasons. First, a beat's <em>number</em> worked out
/// against one global period accumulates any error in that period, and a numbering that has slipped
/// gives a line that is consistent with itself but wrong. Second, some records do not hold one
/// tempo, and a straight line through them does not estimate anything.</para>
///
/// <para>The grid has one beat per index, with no dropped or doubled beats. This stops a single
/// missing beat from rotating the bar phase for the rest of the record, and it is the same rule
/// that <see cref="BeatTracker.Regularize"/> applies. The beats are not always evenly spaced: that
/// is given up only on records where keeping it would cost more than a changeover can allow.</para>
/// </summary>
public static class ModelBeatGrid
{
    /// <summary>A model read that has been fitted to a grid. <see cref="IsUsable"/> is false when
    /// there was not enough to fit, and the caller should fall back to the DSP.</summary>
    /// <param name="ObservedFrom">The first beat the model actually read, as opposed to the first beat
    /// of the fitted grid. The grid is extended back to the start of the record and on to the end (see
    /// the type's remarks), and afterwards it is not possible to tell which part the model heard.</param>
    /// <param name="ObservedTo">The last beat the model actually read.</param>
    public readonly record struct Result(
        double[] BeatTimes,
        double Bpm,
        double BpmConfidence,
        int FirstDownbeat,
        int BeatsPerBar,
        double DownbeatConfidence,
        double ObservedFrom = 0,
        double ObservedTo = 0)
    {
        public static readonly Result None = new([], 0, 0, 0, 4, 0);

        public bool IsUsable => Bpm > 0 && BeatTimes.Length >= MinBeats;
    }

    /// <summary>Fewer beats than this is not a tempo: it is a fragment, or the model finding pulses
    /// in something that has none. It is the same eight that <see cref="TrackAnalysis.CanBeatMatch"/>
    /// asks for.</summary>
    public const int MinBeats = 8;

    /// <summary>The smallest and largest bar lengths accepted, in beats. Two to eight covers every
    /// bar length that records are counted in. A value outside this range is a misread, not an
    /// unusual time signature, and accepting it would set every fade in the mix to the wrong
    /// length.</summary>
    private const int MinBeatsPerBar = 2, MaxBeatsPerBar = 8;

    /// <summary>
    /// How much one beat may vary in length, as a share of a beat, before the record is no longer
    /// worth beat-matching.
    ///
    /// <para>It is measured from beat to beat, not against the whole track, because that is the
    /// question a changeover asks. A mix lasts eight or sixteen bars, and what matters is whether the
    /// record holds its tempo over that time, not whether it ends at the tempo it started at. Real
    /// records can drift a long way over four minutes and still hold their tempo well over eight
    /// bars: one had beats up to 61% of a beat away from a line fitted through the whole record,
    /// while consecutive beats agreed to within 5%.</para>
    ///
    /// <para>Fifteen percent is where a beat-match becomes impossible, not where it becomes untidy.
    /// <see cref="Mixing.TransitionPlanner"/> allows fifteen milliseconds of drift in total for a
    /// changeover, and a record whose beats wander by a seventh of a beat uses all of that inside a
    /// single bar. Such a record is still played and faded, but its grid is not locked to another
    /// record's.</para>
    /// </summary>
    private const double ScatterAtZeroConfidence = 0.15;

    /// <summary>
    /// Fits <paramref name="reading"/> to a grid covering <paramref name="durationSeconds"/>, using
    /// only the model's own reading.
    /// </summary>
    public static Result Fit(BeatReading reading, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return Build(reading, [], durationSeconds, fold: false);
    }

    /// <summary>How many of the model's bar lines must agree on one phase before its reading of the
    /// bar is used instead of the DSP's. Below this the model did not find a bar, and reporting that
    /// is better than naming a wrong one, because every fade in a mix is measured in bars.</summary>
    private const double MinBarAgreement = 0.5;

    /// <summary>
    /// The model read against the grid that the DSP already built: the model's tempo and the model's
    /// bar, slid onto the DSP's beat placement.
    ///
    /// <para>This is what the model is for, and <see cref="Fit"/> alone is not enough. Each reading
    /// is better at a different half of the same question. The model knows what to count (for
    /// example that a record is 139 and not 93, and where the "one" of the bar falls), because those
    /// are musical judgements. The DSP knows where the sound actually is: it places a beat against
    /// the onset itself and is right to the sample, while the model thinks in twenty millisecond
    /// frames and never looks at the waveform.</para>
    ///
    /// <para><b>The tempo is the model's, and only the phase can change.</b> A least-squares fit
    /// cannot remove a <em>systematic</em> offset, for example the model reporting every beat a frame
    /// or two early. That offset is corrected by sliding the grid onto the onsets (see
    /// <see cref="Slide"/>), not by changing how fast the record is.</para>
    ///
    /// <para><b>Why the DSP tempo is not kept.</b> Keeping the DSP tempo and letting the model move
    /// it only by an octave does not work, because the two readings can differ by more than a
    /// power of two. A record whose onset autocorrelation locks onto the dotted quarter note reads
    /// two thirds of the true tempo, and no power of two reaches the model's answer from there. For
    /// example, a DSP reading of 92.913 against a model reading of 139.360 was matched to 185.826
    /// by <see cref="TempoEstimator.MatchLevel"/>, a tempo that neither reading had given, and
    /// <see cref="TempoEstimator.Fold"/> halved that back to 92.913. The record would be stored two
    /// thirds too slow, marked as modelled, at full confidence, and every fade planned against it
    /// would be half as long as the music it covers.</para>
    ///
    /// <para><see cref="TempoEstimator.FoldDown"/> still applies at the top of the window. A reading
    /// of 256 next to a library of 128s is a factor of two that no pitch fader reaches, and the
    /// window is the library's convention. This changes the tempo by a power of two, which is a
    /// matter of counting, not of speed. The bottom of the window is not applied: on 1697 annotated
    /// tracks it never corrected a reading and it broke 37 (see <see cref="TempoEstimator.FoldDown"/>
    /// for the count).</para>
    /// </summary>
    /// <param name="dspBeats">The regularised DSP grid. It is a straight line, and here it is only a
    /// set of onset-accurate times to slide the model's line onto. It may be empty, which leaves the
    /// model's own phase unchanged.</param>
    public static Result Against(
        BeatReading reading,
        IReadOnlyList<double> dspBeats,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(dspBeats);
        return Build(reading, dspBeats, durationSeconds, fold: true);
    }

    /// <summary>
    /// Builds the grid: the record read a window at a time, folded into the library's tempo window,
    /// slid onto the onsets, and straightened again wherever the bend was not worth keeping.
    /// </summary>
    private static Result Build(
        BeatReading reading, IReadOnlyList<double> dspBeats, double durationSeconds, bool fold)
    {
        var beats = reading.BeatTimes;
        if (beats.Count < MinBeats || durationSeconds <= 0)
            return Result.None;

        var windows = TempoWindows.Read(beats);
        if (!windows.IsUsable)
            return Result.None;

        double bpm = 60.0 / windows.Period;
        if (bpm is <= 0 or > 400)
            return Result.None;

        // The tempo, counted in the library's window. Nothing below may change it. A fold changes
        // the octave, which stretches every beat of the grid by the same factor and does not move
        // any beat relative to the music.
        double level = fold ? TempoEstimator.FoldDown(bpm) : bpm;
        if (level <= 0)
            return Result.None;

        double ratio = bpm / level;

        var grid = Lay(windows, ratio, durationSeconds);
        if (grid.Length < MinBeats)
            return Result.None;

        Slide(grid, dspBeats);

        // Straightened where the bend was not worth keeping. Where it was straightened, the tempo
        // reported is the line's own value to the last decimal, not the average of the windows. The
        // two agree to within rounding, and the rounding matters: the planner calculates its tempo
        // pull from the BPM while it lands the drop on a beat of the grid, so a record whose two
        // values disagree would meet on the bar line and drift apart after it.
        if (Straighten(grid, durationSeconds) is { Length: >= MinBeats } straight)
        {
            grid = straight;
            level = (grid.Length - 1) * 60.0 / (grid[^1] - grid[0]);
        }

        if (grid.Length < MinBeats)
            return Result.None;

        // How well the record held that tempo, beat to beat. None of the steps above change it: it
        // is a reading of the model's own beats, not of the grid built from them.
        double scatter = BeatToBeatScatter(beats, windows.Period);
        double confidence = scatter < 0
            ? 0
            : Math.Clamp(1 - scatter / (ScatterAtZeroConfidence * windows.Period), 0, 1);

        var (beatsPerBar, firstDownbeat, agreement) = FindBars(reading.DownbeatTimes, grid);

        return agreement < MinBarAgreement
            ? new Result(grid, level, confidence, 0, 4, 0, beats[0], beats[^1])
            : new Result(grid, level, confidence, firstDownbeat, beatsPerBar, agreement, beats[0], beats[^1]);
    }

    /// <summary>
    /// The beats laid out along the record's own clock, extended to both ends of it.
    ///
    /// <para>The model reports beats where it hears them, which on a record that fades in from
    /// silence is not the first bar. A changeover needs a bar line exactly where the record is
    /// quietest, so the line is continued back to the start and on to the end, at the tempo of the
    /// nearest window. This is the best estimate for parts of the record that were not measured.</para>
    /// </summary>
    private static double[] Lay(TempoWindows.Reading windows, double ratio, double durationSeconds)
    {
        // The position of the record's first beat is found by walking out from the reading, not
        // solved for: the map from beat number to time is bent, so there is no formula to invert.
        int first = 0;
        while (first > -MaxBeats && windows.TimeAt(first * ratio) > 0)
            first--;
        while (first < MaxBeats && windows.TimeAt(first * ratio) < 0)
            first++;

        var grid = new List<double>(1024);
        for (int k = first; ; k++)
        {
            double at = windows.TimeAt(k * ratio);
            if (at > durationSeconds)
                break;
            grid.Add(at);

            // A period that has collapsed would loop here for ever. It cannot happen from a fitted
            // line, whose period is positive by construction, but the whole app depends on this grid
            // and the guard is one comparison.
            if (grid.Count > MaxBeats)
                break;
        }

        return [.. grid];
    }

    /// <summary>The most beats a grid may hold. This is four hours at 400 BPM, which is more than
    /// <see cref="Mixing.TransitionPlanner"/> will mix and more than a model will read.</summary>
    private const int MaxBeats = 400 * 60 * 4;

    /// <summary>
    /// How far the model's line may be slid onto the DSP's onsets.
    ///
    /// <para>Two of the model's twenty millisecond frames. The correction is for the model reporting
    /// every beat consistently early or late, and that error is limited by the resolution the model
    /// works in. A larger difference is not a coarse reading of the same beat but a different beat,
    /// and following it would undo the tempo that this method must not change.</para>
    /// </summary>
    private const double MaxSnapSeconds = 0.04;

    /// <summary>
    /// How much the offsets may differ from each other before the DSP is taken to describe a
    /// different beat. One of the model's frames: a systematic bias puts every beat at the
    /// <em>same</em> offset, so a spread wider than the resolution of the bias is something else.
    /// </summary>
    private const double MaxSpreadSeconds = 0.02;

    /// <summary>
    /// The grid slid onto the beats that the DSP placed against the onsets themselves.
    ///
    /// <para>There is one offset for the whole record, and it has to be one. The correction is for
    /// the model's own frame bias (every beat reported a frame or two early). That is a property of
    /// the model, not of the music, so it is the same number wherever in the record it is measured.
    /// An offset that varied along the record would be something else: a grid slid further at the
    /// end than at the start has a different slope, which means the DSP would be setting the tempo.
    /// That is not allowed, because on ordinary dance records the DSP can read the dotted quarter
    /// note as the beat and still report full confidence. For this reason the tempo comes from the
    /// model or not at all.</para>
    ///
    /// <para>So this moves every beat by the same amount, and a bent grid stays exactly as bent as
    /// it was.</para>
    /// </summary>
    private static void Slide(double[] grid, IReadOnlyList<double> dspBeats)
    {
        if (dspBeats.Count < MinBeats || grid.Length == 0)
            return;

        double offset = OffsetOver(grid, dspBeats);
        if (offset == 0)
            return;

        for (int k = 0; k < grid.Length; k++)
            grid[k] += offset;
    }

    /// <summary>
    /// How far the DSP's onsets are from the grid over one stretch of the record, or zero when they
    /// give no usable answer.
    ///
    /// <para>Only DSP beats that are already within <see cref="MaxSnapSeconds"/> of a grid beat
    /// vote, and the median of those offsets is the move. Both parts matter when the DSP has a
    /// different tempo: at two thirds of the model's tempo, every other DSP beat is close to a grid
    /// beat and the ones between are half a beat away, so the filter keeps the near ones and the
    /// median ignores the rest.</para>
    ///
    /// <para>The votes must also agree with each other, which is what
    /// <see cref="MaxSpreadSeconds"/> checks. A DSP line with a different tempo crosses the model's
    /// line instead of running beside it, so its near-misses cover the whole window over the length
    /// of a record instead of holding one offset. The middle of such a spread measures nothing, and
    /// following it would move the grid by tens of milliseconds without evidence. Where the two
    /// lines are parallel, which is the case this method is for, every offset is the same and the
    /// spread is zero.</para>
    /// </summary>
    private static double OffsetOver(double[] grid, IReadOnlyList<double> dspBeats)
    {
        var offsets = new List<double>(dspBeats.Count);
        foreach (double beat in dspBeats)
        {
            double off = beat - Nearest(grid, beat);
            if (Math.Abs(off) <= MaxSnapSeconds)
                offsets.Add(off);
        }

        if (offsets.Count < MinBeats)
            return 0;

        offsets.Sort();

        // Ignore a tenth of the offsets at each end, so one badly placed beat does not decide the
        // result for the stretch.
        int tail = offsets.Count / 10;
        double spread = offsets[offsets.Count - 1 - tail] - offsets[tail];
        return spread <= MaxSpreadSeconds ? offsets[offsets.Count / 2] : 0;
    }

    /// <summary>The grid beat closest to a time. It uses binary search: the grid is in order, and
    /// this is called once per DSP beat per window.</summary>
    private static double Nearest(double[] grid, double seconds)
    {
        int at = Array.BinarySearch(grid, seconds);
        if (at >= 0)
            return grid[at];

        at = ~at;
        if (at == 0)
            return grid[0];
        if (at >= grid.Length)
            return grid[^1];

        return seconds - grid[at - 1] <= grid[at] - seconds ? grid[at - 1] : grid[at];
    }

    /// <summary>
    /// The grid straightened back into one line, wherever the bend was not worth keeping.
    ///
    /// <para>A bend is worth keeping only when a straight line would put a beat somewhere that a
    /// changeover could hear is wrong. Most records are sequenced to a click and all their windows
    /// give the same tempo. For those, the least-squares line through the whole grid passes through
    /// every beat and the bend is noise. A record that really changes tempo is more than a
    /// changeover's whole allowance away from that line, so it is left bent.</para>
    ///
    /// <para>This is not only for tidiness. Two machines that analyse the same record are seen to
    /// agree by the shared pool only when they write the same text. A grid with a bend that nobody can
    /// hear carries whatever each machine's model happened to round, so the machines would disagree on
    /// every record that was sequenced to a click.</para>
    /// </summary>
    /// <returns>The straight line, or null to keep the bend.</returns>
    private static double[]? Straighten(double[] grid, double durationSeconds)
    {
        if (grid.Length < MinBeats)
            return null;

        double meanIndex = (grid.Length - 1) / 2.0;
        double meanTime = 0;
        for (int k = 0; k < grid.Length; k++)
            meanTime += grid[k];
        meanTime /= grid.Length;

        double covariance = 0, variance = 0;
        for (int k = 0; k < grid.Length; k++)
        {
            double i = k - meanIndex;
            covariance += i * (grid[k] - meanTime);
            variance += i * i;
        }
        if (variance < 1e-9)
            return null;

        double period = covariance / variance;
        if (period <= 0)
            return null;

        double phase = meanTime - period * meanIndex;

        double worst = 0;
        for (int k = 0; k < grid.Length; k++)
            worst = Math.Max(worst, Math.Abs(grid[k] - (phase + k * period)));

        if (worst > Mixing.TransitionPlanner.MaxDriftSeconds)
            return null;

        // The line is rebuilt, not overwritten in place, because a straight line reaches the ends
        // of the record on its own terms: the bent grid was extended at the end windows' periods, so
        // the line may have one beat more or fewer in the silence at either end.
        int first = (int)Math.Ceiling(-phase / period - 1e-9);
        int last = (int)Math.Floor((durationSeconds - phase) / period + 1e-9);
        if (last < first)
            return null;

        var straight = new double[last - first + 1];
        for (int k = first; k <= last; k++)
            straight[k - first] = phase + k * period;
        return straight;
    }

    /// <summary>
    /// How much the length of a beat varies from one beat to the next, in seconds. Negative when
    /// there were not enough single-beat gaps to tell.
    ///
    /// <para>Only gaps of a single beat count. A gap where the model missed a beat is twice as long
    /// as its neighbours, and counting it would make a model that skipped a beat look like a record
    /// with unsteady tempo.</para>
    /// </summary>
    private static double BeatToBeatScatter(IReadOnlyList<double> beats, double period)
    {
        double sum = 0, sumSquares = 0;
        int counted = 0;

        for (int i = 1; i < beats.Count; i++)
        {
            double gap = beats[i] - beats[i - 1];

            // Accept gaps from half a beat to one and a half beats. Outside that range the model
            // missed a beat or found one twice, which is a different fault from an unsteady record.
            // The range must be this wide: a narrower one would reject exactly the long and short
            // beats that show the record wanders, and an unsteady track would look steady.
            if (gap < period * 0.5 || gap > period * 1.5)
                continue;

            sum += gap;
            sumSquares += gap * gap;
            counted++;
        }

        if (counted < MinBeats)
            return -1;

        double mean = sum / counted;
        return Math.Sqrt(Math.Max(0, sumSquares / counted - mean * mean));
    }

    /// <summary>
    /// How many beats to a bar, where the first bar line falls, and how consistently the model put
    /// its bar lines on that same beat of the bar.
    ///
    /// <para>The bar lines are counted against the grid itself, not worked out from the tempo. The
    /// two agree only while the grid is a straight line, and the grid is straight only on records
    /// that kept their line. So a bar line is the beat of the grid nearest to where the model heard
    /// it, which is correct in both cases.</para>
    ///
    /// <para>The bar length is the most common distance between consecutive bar lines, not the
    /// average, because a model that misses one bar line reports a gap of two bars, and no record is
    /// in seven.</para>
    /// </summary>
    private static (int BeatsPerBar, int FirstDownbeat, double Confidence) FindBars(
        IReadOnlyList<double> downbeats, double[] grid)
    {
        if (downbeats.Count < 2 || grid.Length == 0)
            return (4, 0, 0);

        // Each bar line as a beat number on the grid.
        var at = new int[downbeats.Count];
        for (int i = 0; i < downbeats.Count; i++)
            at[i] = NearestIndex(grid, downbeats[i]);

        var gaps = new int[downbeats.Count - 1];
        for (int i = 1; i < downbeats.Count; i++)
            gaps[i - 1] = at[i] - at[i - 1];

        int beatsPerBar = Mode(gaps, MinBeatsPerBar, MaxBeatsPerBar);
        if (beatsPerBar <= 0)
            return (4, 0, 0);

        // Which beat of the bar the model's lines land on. On a record that the model read cleanly
        // this is the same every time. Where it varies, the most common value is the bar phase, and
        // how often it occurs shows how much the reading can be trusted.
        var phases = new int[at.Length];
        for (int i = 0; i < at.Length; i++)
            phases[i] = ((at[i] % beatsPerBar) + beatsPerBar) % beatsPerBar;

        int phase = Mode(phases, 0, beatsPerBar - 1);
        if (phase < 0)
            return (beatsPerBar, 0, 0);

        int agreed = 0;
        foreach (int p in phases)
            if (p == phase)
                agreed++;

        // The first bar line on the grid is the first beat whose number has that phase. The grid is
        // numbered from zero, so this is the phase itself.
        int firstDownbeat = phase < grid.Length ? phase : 0;
        return (beatsPerBar, firstDownbeat, (double)agreed / phases.Length);
    }

    /// <summary>
    /// Which beat of the grid is closest to a time, counted on past both ends of it at the end
    /// spacings, so the answer may be negative or past the last beat.
    ///
    /// <para>The answer has to be allowed outside the grid. The grid starts at the first beat at or
    /// after the file's first sample, and a line fitted through beats that the model rounded to its
    /// frame can put that beat a fraction of a millisecond before zero. So the first bar line of a
    /// record is often one beat before anything the grid holds. Clamping it to the first beat would
    /// file it under the wrong bar phase, and <see cref="FindBars"/> would read a record whose bar
    /// lines all agree as one whose bar lines are split between two phases.</para>
    /// </summary>
    private static int NearestIndex(double[] grid, double seconds)
    {
        int at = Array.BinarySearch(grid, seconds);
        if (at >= 0)
            return at;

        at = ~at;
        if (at == 0)
            return grid.Length < 2
                ? 0
                : -(int)Math.Round((grid[0] - seconds) / (grid[1] - grid[0]));

        if (at >= grid.Length)
            return grid.Length < 2
                ? grid.Length - 1
                : grid.Length - 1 + (int)Math.Round((seconds - grid[^1]) / (grid[^1] - grid[^2]));

        return seconds - grid[at - 1] <= grid[at] - seconds ? at - 1 : at;
    }

    /// <summary>The most common value in <paramref name="values"/> within the given range, or -1 when
    /// none of them is in it.</summary>
    private static int Mode(int[] values, int lowest, int highest)
    {
        if (highest < lowest)
            return -1;

        var counts = new int[highest - lowest + 1];
        foreach (int value in values)
            if (value >= lowest && value <= highest)
                counts[value - lowest]++;

        int best = -1, bestCount = 0;
        for (int i = 0; i < counts.Length; i++)
            if (counts[i] > bestCount)
            {
                bestCount = counts[i];
                best = i + lowest;
            }
        return best;
    }
}

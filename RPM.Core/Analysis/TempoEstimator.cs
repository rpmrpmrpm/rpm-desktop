namespace RPM.Core.Analysis;

/// <summary>
/// Reads a tempo from an <see cref="OnsetEnvelope"/> by autocorrelation. A track at 120 BPM has
/// onsets half a second apart, so its envelope correlates strongly with itself shifted by half a
/// second. It also correlates when shifted by a quarter of a second and by a whole second, which is
/// the main difficulty. All these lags score, and the tempo prior chooses the one that a person
/// would tap.
/// </summary>
public static class TempoEstimator
{
    /// <summary>The window in which a tempo is reported. A tempo outside it is folded in by doubling
    /// or halving, as a DJ does with a 75 BPM record.</summary>
    public const double MinBpm = 70, MaxBpm = 180;

    /// <summary>The centre of the prior, and its width in octaves. 120 BPM with a fairly loose
    /// spread: the prior is meant to break ties between a tempo and its double, not to overrule a
    /// record that really is at 90 or 160.</summary>
    private const double PriorCentreBpm = 124, PriorWidthOctaves = 0.9;

    /// <summary>The tempo, in BPM, and how sure the autocorrelation was (the winning peak's share
    /// of the runner-up: 1 = a clean lock, near 0 = the curve had no rhythm in it).</summary>
    public readonly record struct Result(double Bpm, double Confidence)
    {
        public static readonly Result None = new(0, 0);
        public bool IsKnown => Bpm > 0;
    }

    public static Result Estimate(ReadOnlySpan<float> onsets, double frameRate)
    {
        if (onsets.Length < 64 || frameRate <= 0)
            return Result.None;

        // Search every lag that could be a beat between MinBpm/2 and MaxBpm*2. The half and double
        // are searched on purpose, so that folding can bring a strong 60 BPM lock up to 120 and the
        // search does not miss it.
        int minLag = Math.Max(1, (int)Math.Floor(frameRate * 60.0 / (MaxBpm * 2)));
        int maxLag = Math.Min(onsets.Length / 2, (int)Math.Ceiling(frameRate * 60.0 / (MinBpm / 2)));
        if (maxLag <= minLag)
            return Result.None;

        var score = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            for (int i = lag; i < onsets.Length; i++)
                sum += onsets[i] * onsets[i - lag];

            // Normalise by the overlap length, or long lags (fewer terms) would always lose.
            double correlation = sum / (onsets.Length - lag);
            double bpm = 60.0 * frameRate / lag;
            score[lag] = correlation * Prior(bpm);
        }

        // A beat lag also correlates at 2x, 3x and so on. Add those in, so a tempo whose harmonics
        // all line up beats a lag that scores only once. This stops the half-tempo lock.
        var combed = new double[maxLag + 1];
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = score[lag];
            for (int multiple = 2; multiple <= 4; multiple++)
            {
                int at = lag * multiple;
                if (at > maxLag) break;
                sum += score[at] / multiple;       // the weight falls with distance
            }
            combed[lag] = sum;
        }

        int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
            if (combed[lag] > combed[bestLag])
                bestLag = lag;

        if (combed[bestLag] <= 0)
            return Result.None;

        // The lag is a whole number of frames, and at 43 frames a second one frame is 2.5% of the
        // tempo at 120 BPM, which is too coarse to beat-match. Fit a parabola through the peak and
        // its two neighbours and take its vertex. This puts the tempo within a tenth of a percent
        // of the true value.
        double refinedLag = bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double before = combed[bestLag - 1], peak = combed[bestLag], after = combed[bestLag + 1];
            double curvature = before - 2 * peak + after;
            if (Math.Abs(curvature) > 1e-12)
            {
                double offset = 0.5 * (before - after) / curvature;
                if (Math.Abs(offset) <= 1)
                    refinedLag = bestLag + offset;
            }
        }

        // How sure the reading is, measured against the BACKGROUND and not against the runner-up.
        // The runner-up is nearly always the half or double tempo. That is not doubt about the beat,
        // only about which level to count it at, and the prior and the fold have already decided
        // that. A record with a beat stands well above the typical lag; a spoken-word track or a
        // rubato ballad barely rises above it.
        var sorted = new double[maxLag - minLag + 1];
        Array.Copy(combed, minLag, sorted, 0, sorted.Length);
        Array.Sort(sorted);
        double background = Math.Max(sorted[sorted.Length / 2], 1e-9);
        double standsOut = combed[bestLag] / background;
        double confidence = Math.Clamp((standsOut - 1.2) / 2.0, 0, 1);
        return new Result(PinDown(onsets, frameRate, Fold(60.0 * frameRate / refinedLag)), confidence);
    }

    /// <summary>
    /// Refines a tempo by asking, for every tempo within <paramref name="range"/> of the estimate,
    /// how much of the envelope beats at exactly that rate.
    ///
    /// <para>Autocorrelation answers "what spacing repeats", and a parabola through the winning lag
    /// gets that to within about half a percent. That is enough to tap along to but not enough to
    /// mix: half a percent is 78 ms of drift over eight bars, a fifth of a beat, which is two kicks
    /// that can be heard separately. Over a whole track there is no such tolerance: being half a
    /// percent out puts the last beat of the grid a second away from the record's, so the score drops
    /// sharply on either side of the true tempo. That sharpness is what makes the measurement
    /// work.</para>
    /// </summary>
    public static double PinDown(ReadOnlySpan<float> onsets, double frameRate, double bpm, double range = 0.025)
    {
        if (bpm <= 0 || frameRate <= 0 || onsets.Length < 64)
            return bpm;

        double mean = 0;
        for (int i = 0; i < onsets.Length; i++)
            mean += onsets[i];
        mean /= onsets.Length;

        // Coarse enough to land on the right peak (they are about a tenth of a percent wide), then
        // once more inside it for the decimal.
        double best = Sweep(onsets, mean, frameRate, bpm * (1 - range), bpm * (1 + range), bpm * 0.0002);
        return Sweep(onsets, mean, frameRate, best * 0.9997, best * 1.0003, best * 0.00002);
    }

    /// <summary>How finely one beat is divided when the envelope is folded onto it. Thirty-two bins
    /// is 9 ms at 140 BPM, well inside one onset peak, so a fold that lines up stays in a few bins
    /// and one that does not is spread over all of them.</summary>
    private const int FoldBins = 32;

    /// <summary>
    /// How well the envelope folds onto a beat of <paramref name="from"/>..<paramref name="to"/> BPM,
    /// in steps of <paramref name="step"/>, and which of them folds best.
    ///
    /// <para>Every frame of the track is put into the bin for where it falls <em>within</em> a beat,
    /// and the winner is the tempo whose fold is most concentrated. At the true tempo every kick in
    /// the record lands in the same bin, however far into the track it is. Half a percent out, the
    /// last kick is two beats away from the first, so the fold is spread flat. This difference is
    /// large, which is why the measurement is sharp.</para>
    ///
    /// <para>Phase does not matter here, and that is important. Laying down a grid and adding up what
    /// it lands on needs a guessed phase. At 140 BPM, eight guesses are 53 ms apart and an onset peak
    /// is about that wide, so which tempo wins depends on which one was offered a phase that fitted.
    /// This can pin a record at 139.98 to 141, which is three quarters of a percent out, 200 ms of
    /// drift over sixteen bars, and a beat-match that fails in the middle. Folding cannot get a
    /// phase wrong because it never picks one. It also cannot cancel, unlike a single Fourier term
    /// at the beat rate: on a record whose offbeat hits as hard as its downbeat (most records), the
    /// two arrive half a turn apart and almost cancel each other.</para>
    /// </summary>
    private static double Sweep(ReadOnlySpan<float> onsets, double mean, double frameRate, double from, double to, double step)
    {
        double bestBpm = from, bestScore = -1;
        Span<double> fold = stackalloc double[FoldBins];

        for (double bpm = from; bpm <= to; bpm += step)
        {
            double period = 60.0 * frameRate / bpm;
            if (period < 2 || period > onsets.Length)
                continue;

            fold.Clear();
            for (int i = 0; i < onsets.Length; i++)
            {
                double turns = i / period;
                int bin = (int)((turns - Math.Floor(turns)) * FoldBins);
                fold[bin < FoldBins ? bin : FoldBins - 1] += onsets[i] - mean;
            }

            // Every candidate folds the same total, so the sum of squares IS how concentrated it is:
            // one tall bin scores higher than thirty-two level ones.
            double score = 0;
            for (int b = 0; b < FoldBins; b++)
                score += fold[b] * fold[b];

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = bpm;
            }
        }
        return bestBpm;
    }

    /// <summary>A log-normal bump on tempo: 124 BPM is more likely than 62 or 248 for the same
    /// evidence. Returns a multiplier in (0, 1].</summary>
    private static double Prior(double bpm)
    {
        double octaves = Math.Log2(bpm / PriorCentreBpm) / PriorWidthOctaves;
        return Math.Exp(-0.5 * octaves * octaves);
    }

    /// <summary>Brings a tempo into <see cref="MinBpm"/>..<see cref="MaxBpm"/> by doubling or
    /// halving: 65 BPM becomes 130, and 200 becomes 100. It is the same record, counted
    /// differently.</summary>
    public static double Fold(double bpm)
    {
        if (bpm <= 0)
            return 0;
        while (bpm < MinBpm) bpm *= 2;
        while (bpm > MaxBpm) bpm /= 2;
        return bpm;
    }

    /// <summary>
    /// Halves a tempo down to <see cref="MaxBpm"/> and leaves a slow record unchanged.
    ///
    /// <para>This is half of <see cref="Fold"/>, kept as its own function because only half of it
    /// held up when measured. <c>tools/TempoBench</c> ran the analyser over 1697 annotated tracks and
    /// recorded what each end of the window did:</para>
    ///
    /// <list type="table">
    /// <item><term>halving above 180</term><description>fired 176 times, fixed 73, broke 89</description></item>
    /// <item><term>doubling below 70</term><description>fired 49 times, fixed <b>0</b>, broke 37</description></item>
    /// </list>
    ///
    /// <para>The lower end never helped: a record read at 62 is a ballad or a piece of classical music
    /// that really runs at 62, and doubling it says the record is twice as fast as it is. The reading
    /// was not ambiguous. The model had found a slow beat, which music can have.</para>
    ///
    /// <para>The upper end is a closer decision, and it is kept. Its 73 fixes and 89 breaks come from
    /// the two corpora counting differently, not from different tempos: GTZAN annotators wrote fast
    /// rock in half time and Ballroom annotators wrote the tempo that the dancers dance to, so the
    /// same rule suits one corpus and not the other. The numbers are nearly equal, and it is the
    /// library's convention, which has value on its own. On house, disco and pop it almost never
    /// fires.</para>
    /// </summary>
    public static double FoldDown(double bpm)
    {
        if (bpm <= 0)
            return 0;
        while (bpm > MaxBpm) bpm /= 2;
        return bpm;
    }

    /// <summary>
    /// The same tempo counted at the octave nearest to <paramref name="reference"/>: against a record
    /// at 150, a reading of 74 becomes 148.
    ///
    /// <para>The octave at which a tempo is reported is a matter of counting, not of speed: a record
    /// is not faster because it is called 148 instead of 74, and the reading can land on either. The
    /// window that <see cref="Fold"/> uses spans a factor of two and a half, so two counts of the
    /// same tempo can both be inside it: 74 and 148 are both valid answers and the estimator has to
    /// pick one. Pairing two records without this step turns a one percent difference into a factor
    /// of two, which no pitch fader reaches, and a mix that should be beat-matched becomes a
    /// blend.</para>
    /// </summary>
    public static double MatchLevel(double bpm, double reference)
    {
        if (bpm <= 0 || reference <= 0)
            return bpm;

        // Use ratios, not differences: halving is as far from a tempo as doubling it, and a plain
        // subtraction does not show that.
        while (bpm * 2 <= reference * Math.Sqrt(2)) bpm *= 2;
        while (bpm / 2 >= reference / Math.Sqrt(2)) bpm /= 2;
        return bpm;
    }

    /// <summary>
    /// The octave at which the record is danced, which is not always the octave at which it is
    /// reported. It is used for measuring bars: a bar is four beats of what the room is counting, and
    /// a reading one octave too low makes every bar twice as long as the bar being danced to, so an
    /// eight-bar changeover would be sixteen bars long.
    ///
    /// <para>The result is the octave nearest to <see cref="PriorCentreBpm"/>, the same prior used
    /// for the estimate. It leaves anything from 88 to 175 unchanged and only doubles readings that
    /// are too slow to dance to. It is a weaker claim than correcting the estimate: it decides how
    /// long a bar lasts, not what the record is.</para>
    /// </summary>
    public static double DanceLevel(double bpm)
    {
        if (bpm <= 0)
            return 0;
        while (bpm * 2 <= PriorCentreBpm * Math.Sqrt(2)) bpm *= 2;
        while (bpm / 2 >= PriorCentreBpm / Math.Sqrt(2)) bpm /= 2;
        return bpm;
    }
}

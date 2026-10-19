namespace RPM.Core.Analysis;

/// <summary>
/// Which of the four beats in the bar is the "one".
///
/// <para>The obvious answer is the loudest beat. It works on a rock track but fails on the music
/// this class is for. Four-on-the-floor music has a kick on all four beats, so every phase hits
/// equally hard and the result is a coin toss. A track with an offbeat intro can be worse than a
/// coin toss. What marks a bar line is the <em>harmony</em>: chords and bass notes change on the
/// one, whether or not anything is louder there.</para>
///
/// <para>So each beat is scored on two things: how much the chroma changes at that beat, and how
/// hard the beat hits. The four phases are compared by their total score. The two scores are put on
/// the same scale before they are added, because their raw units are unrelated.</para>
/// </summary>
public static class DownbeatDetector
{
    /// <summary>
    /// The distance in beats between the two stretches of music that are compared, and the weight of
    /// each comparison.
    ///
    /// <para>Comparing one beat with the one before it shows where the harmony moves. On many tracks
    /// the harmony moves every two beats, which fixes the half-bar but leaves the one and the three
    /// looking the same. Comparing a whole bar with the bar before it separates them: a chord that
    /// arrives on the one and holds makes the four beats after it as different as possible from the
    /// four before, and nothing like that happens on the three. So the bar-length comparison has the
    /// highest weight. When all three scales agree, the result can be trusted.</para>
    /// </summary>
    private static readonly (int Beats, double Weight)[] HarmonyScales = [(1, 0.5), (2, 0.75), (4, 1.0)];

    /// <summary>The weight of the onset strength. Strength is still used because it is correct for
    /// rock, funk and anything else with an accented one, and because it gives a result on a track
    /// whose harmony never moves.</summary>
    private const double StrengthWeight = 0.4;

    /// <summary>
    /// The gap between the winner and the runner-up, in standard errors, that counts as chance, and
    /// the gap that counts as certain.
    ///
    /// <para>Four numbers drawn from noise are not equal. The largest of four standard normal values
    /// is about one standard error above the second largest, and often more. So a gap of one or two
    /// is what having no bar line looks like, and the confidence must start above the noise level,
    /// not at zero. Otherwise the detector would be wrong about half the time and still report high
    /// confidence, instead of admitting that it does not know.</para>
    /// </summary>
    private const double NoiseGap = 1.6, DecisiveGap = 3.6;

    /// <param name="FirstDownbeat">Index into the beat list. Every beat whose index has the same
    /// remainder modulo the bar length is a downbeat.</param>
    /// <param name="Confidence">0..1. Zero means the four phases could not be told apart. Then the
    /// caller should align on beats and not on a bar that may not exist.</param>
    public readonly record struct Result(int FirstDownbeat, double Confidence);

    /// <param name="onsets">The onset envelope the beats were tracked on.</param>
    /// <param name="beats">Beat positions, as frame indices into <paramref name="onsets"/>.</param>
    /// <param name="onsetFrameRate">Frames per second of <paramref name="onsets"/>.</param>
    /// <param name="chroma">The chromagram, one row per frame (see
    /// <see cref="ChromaAnalyzer.ComputeFrames"/>). Pass an empty list to score on strength alone.</param>
    /// <param name="chromaFrameRate">Frames per second of <paramref name="chroma"/>.</param>
    public static Result Find(
        ReadOnlySpan<float> onsets,
        IReadOnlyList<int> beats,
        double onsetFrameRate,
        IReadOnlyList<double[]> chroma,
        double chromaFrameRate,
        int beatsPerBar = 4)
    {
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(chroma);
        if (beats.Count < beatsPerBar * 2 || beatsPerBar < 2)
            return new Result(0, 0);

        // The strongest value near each beat, not the value exactly on it. The grid can be one frame
        // away from a spiky envelope, and reading a single frame would turn a clear accent into noise.
        var strength = new double[beats.Count];
        for (int i = 0; i < beats.Count; i++)
        {
            double peak = 0;
            for (int at = beats[i] - 1; at <= beats[i] + 1; at++)
                if (at >= 0 && at < onsets.Length)
                    peak = Math.Max(peak, onsets[at]);
            strength[i] = peak * peak;
        }

        var perBeatChroma = BeatChroma(beats, onsetFrameRate, chroma, chromaFrameRate);

        var scores = new double[beatsPerBar];
        double weights = StrengthWeight * StrengthWeight;
        Add(scores, PhaseScores(strength, beatsPerBar), StrengthWeight);
        foreach (var (span, weight) in HarmonyScales)
        {
            Add(scores, PhaseScores(Novelty(perBeatChroma, span), beatsPerBar), weight);
            weights += weight * weight;
        }

        // Return to the scale of a single cue, so that "standard errors" means the same however many
        // cues were added.
        double norm = Math.Sqrt(weights);
        for (int p = 0; p < beatsPerBar; p++)
            scores[p] /= norm;

        int best = 0, second = -1;
        for (int p = 1; p < beatsPerBar; p++)
            if (scores[p] > scores[best])
                best = p;
        for (int p = 0; p < beatsPerBar; p++)
            if (p != best && (second < 0 || scores[p] > scores[second]))
                second = p;

        // Both cues are already in standard errors, so the gap is too. It is the distance between
        // the winner and its nearest rival, measured against how much the beats scatter by themselves.
        double gap = scores[best] - scores[second];
        double confidence = Math.Clamp((gap - NoiseGap) / (DecisiveGap - NoiseGap), 0, 1);
        return new Result(best, confidence);
    }

    /// <summary>Where in the beat the harmony is read from. The first sixth is skipped because a
    /// drum hit is broadband and would hide the chord under it. The window stops before the next
    /// beat for the same reason.</summary>
    private const double ReadFrom = 1.0 / 6, ReadTo = 5.0 / 6;

    private static void Add(double[] into, double[] scores, double weight)
    {
        for (int i = 0; i < into.Length; i++)
            into[i] += weight * scores[i];
    }

    /// <summary>The harmony under each beat, one twelve-bin vector per beat.</summary>
    private static double[][] BeatChroma(
        IReadOnlyList<int> beats, double onsetFrameRate,
        IReadOnlyList<double[]> chroma, double chromaFrameRate)
    {
        var perBeat = new double[beats.Count][];
        for (int i = 0; i < beats.Count; i++)
            perBeat[i] = new double[12];
        if (chroma.Count == 0 || onsetFrameRate <= 0 || chromaFrameRate <= 0 || beats.Count < 2)
            return perBeat;

        // Every window has the same number of frames. A window from one beat to the next would be ten
        // frames on some beats and eleven on others, because a beat is not a whole number of chroma
        // frames. That alternation has its own rhythm, and the fold would read it as a bar line. The
        // detector would then measure its own rounding.
        double beatFrames = (beats[^1] - beats[0]) / (double)(beats.Count - 1) / onsetFrameRate * chromaFrameRate;
        int window = Math.Max(1, (int)((ReadTo - ReadFrom) * beatFrames));
        int skip = (int)(ReadFrom * beatFrames);

        for (int i = 0; i < beats.Count; i++)
        {
            int from = (int)Math.Round(beats[i] / onsetFrameRate * chromaFrameRate) + skip;
            perBeat[i] = Sum(chroma, from, window);
        }
        return perBeat;
    }

    /// <summary>
    /// How different the <paramref name="span"/> beats starting here are from the
    /// <paramref name="span"/> beats before them. With a span of one it means "the chord changed on
    /// this beat". With a span of four it means "this bar is not like the last bar", which is what
    /// marks a one. The value is one minus the cosine similarity of the summed chroma.
    /// </summary>
    private static double[] Novelty(double[][] perBeat, int span)
    {
        var change = new double[perBeat.Length];
        for (int i = span; i + span <= perBeat.Length; i++)
        {
            var ahead = new double[12];
            var behind = new double[12];
            for (int k = 0; k < span; k++)
                for (int q = 0; q < 12; q++)
                {
                    ahead[q] += perBeat[i + k][q];
                    behind[q] += perBeat[i - span + k][q];
                }
            change[i] = 1 - CosineSimilarity(ahead, behind);
        }
        return change;
    }

    private static double[] Sum(IReadOnlyList<double[]> chroma, int from, int frames)
    {
        var total = new double[12];
        for (int f = Math.Max(0, from); f < Math.Min(chroma.Count, from + frames); f++)
            for (int i = 0; i < 12; i++)
                total[i] += chroma[f][i];
        return total;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < 12; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na <= 1e-12 || nb <= 1e-12)
            return 1;                          // silence on either side: no change to report
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>
    /// A cue, per beat, turned into one number per position in the bar: how far the average of that
    /// position is above the average of all four, counted in standard errors.
    ///
    /// <para>The unit is standard errors and not raw size, because the question is not "is this phase
    /// bigger" but "is it bigger than the scatter from bar to bar can explain". On a track where
    /// every beat is the same, the four means differ by a tiny amount and the scatter is much wider,
    /// so the score is zero, which is correct. Dividing by the spread of the four means instead
    /// would scale that tiny difference up into a winner that looks confident.</para>
    /// </summary>
    private static double[] PhaseScores(double[] perBeat, int beatsPerBar)
    {
        var mean = new double[beatsPerBar];
        var count = new int[beatsPerBar];
        foreach (var (value, i) in perBeat.Select((v, i) => (v, i)))
        {
            mean[i % beatsPerBar] += value;
            count[i % beatsPerBar]++;
        }
        for (int p = 0; p < beatsPerBar; p++)
            mean[p] /= Math.Max(1, count[p]);

        double grand = 0;
        for (int p = 0; p < beatsPerBar; p++)
            grand += mean[p];
        grand /= beatsPerBar;

        // How much the beats scatter around the average of their own position: the noise in this cue.
        double residual = 0;
        for (int i = 0; i < perBeat.Length; i++)
        {
            double d = perBeat[i] - mean[i % beatsPerBar];
            residual += d * d;
        }
        int freedom = perBeat.Length - beatsPerBar;
        if (freedom < 1)
            return new double[beatsPerBar];
        double variance = residual / freedom;

        var scores = new double[beatsPerBar];
        for (int p = 0; p < beatsPerBar; p++)
        {
            double standardError = Math.Sqrt(variance / Math.Max(1, count[p]));
            scores[p] = standardError <= 1e-12 ? 0 : (mean[p] - grand) / standardError;
        }
        return scores;
    }
}

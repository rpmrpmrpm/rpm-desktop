namespace RPM.Core.Analysis;

/// <summary>
/// Names the key behind a chromagram by correlating it with the twenty-four key profiles
/// (Krumhansl-Schmuckler, in Temperley's revision, whose weights come from how well listeners
/// rated each note as fitting each key). Every profile is rotated to each of the twelve tonics, the
/// best correlation wins, and the size of its lead is the confidence.
/// </summary>
public static class KeyDetector
{
    // Temperley's revision of the Krumhansl-Kessler profiles. Index 0 is the tonic, then one
    // semitone per step. Major puts its weight on degrees 1, 3 and 5. Minor lowers the third and
    // flattens degrees 6 and 7.
    private static readonly double[] MajorProfile =
        [5.0, 2.0, 3.5, 2.0, 4.5, 4.0, 2.0, 4.5, 2.0, 3.5, 1.5, 4.0];

    private static readonly double[] MinorProfile =
        [5.0, 2.0, 3.5, 4.5, 2.0, 4.0, 2.0, 4.5, 3.5, 2.0, 1.5, 4.0];

    /// <summary>The key, and how clearly it beat the runner-up (0 = a coin toss between two keys,
    /// 1 = only one key fits at all). The key is unknown when the chromagram is empty or flat.</summary>
    public readonly record struct Result(MusicalKey Key, double Confidence)
    {
        public static readonly Result None = new(MusicalKey.Unknown, 0);
    }

    public static Result Detect(IReadOnlyList<double> chroma)
    {
        ArgumentNullException.ThrowIfNull(chroma);
        if (chroma.Count != 12)
            return Result.None;

        double total = 0;
        foreach (var v in chroma) total += v;
        if (total <= 0)
            return Result.None;

        var best = Result.None;
        double bestScore = double.NegativeInfinity, runnerUp = double.NegativeInfinity;

        for (int tonic = 0; tonic < 12; tonic++)
        {
            foreach (bool minor in (ReadOnlySpan<bool>)[false, true])
            {
                double score = Correlate(chroma, minor ? MinorProfile : MajorProfile, tonic);
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    best = new Result(new MusicalKey(tonic, minor), 0);
                }
                else if (score > runnerUp)
                {
                    runnerUp = score;
                }
            }
        }

        if (double.IsNegativeInfinity(bestScore) || bestScore <= 0)
            return Result.None;

        // Correlations are in [-1, 1]. The gap between the best and second-best key shows whether
        // the answer is a real key or a guess. It is scaled so that a lead of 0.1 already gives full
        // confidence, because real music rarely separates its two best-fitting keys by more than that.
        double confidence = double.IsNegativeInfinity(runnerUp) ? 1 : Math.Clamp((bestScore - runnerUp) * 10, 0, 1);
        return best with { Confidence = confidence };
    }

    /// <summary>Pearson correlation between the chromagram and one profile rotated to
    /// <paramref name="tonic"/>.</summary>
    private static double Correlate(IReadOnlyList<double> chroma, double[] profile, int tonic)
    {
        double meanChroma = 0, meanProfile = 0;
        for (int i = 0; i < 12; i++)
        {
            meanChroma += chroma[i];
            meanProfile += profile[i];
        }
        meanChroma /= 12;
        meanProfile /= 12;

        double covariance = 0, varChroma = 0, varProfile = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[i] - meanChroma;
            double p = profile[(i - tonic + 12) % 12] - meanProfile;
            covariance += c * p;
            varChroma += c * c;
            varProfile += p * p;
        }

        double denominator = Math.Sqrt(varChroma * varProfile);
        return denominator < 1e-12 ? 0 : covariance / denominator;
    }
}

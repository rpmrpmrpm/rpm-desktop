namespace RPM.Core.Splitting;

/// <summary>
/// Aligns an album's expected track durations (from MusicBrainz) with the silence candidates
/// found in the file. It handles two hard cases: silences that are not track boundaries (quiet
/// passages, gaps between vinyl sides), and boundaries with no silence at all (gapless albums,
/// live records). For the second case, a virtual boundary is placed at the expected offset and
/// snapped to the quietest nearby moment.
/// </summary>
public static class BoundaryAligner
{
    public sealed record AlignmentResult(IReadOnlyList<SplitTrack> Tracks, double MeanErrorSeconds);

    /// <summary>
    /// Matches the N-1 expected internal boundaries (cumulative track lengths, rescaled to the
    /// real duration of the file) against the candidate silences, using a monotonic dynamic
    /// programming search. Boundaries with no match become <see cref="BoundarySource.DurationAligned"/>
    /// cuts.
    /// </summary>
    public static AlignmentResult Align(
        IReadOnlyList<(string Title, string? Artist, double LengthSeconds)> expected,
        IReadOnlyList<SilenceCandidate> candidates,
        double totalSeconds,
        float[] envelopeDb,
        double windowSeconds,
        double toleranceSeconds = 5.0)
    {
        if (expected.Count == 0 || totalSeconds <= 0)
            return new AlignmentResult(Array.Empty<SplitTrack>(), 0);

        // Global drift: a different vinyl speed or mastering moves every boundary by the same proportion.
        double sumExpected = expected.Sum(e => e.LengthSeconds);
        double scale = sumExpected > 0 ? totalSeconds / sumExpected : 1.0;

        // Expected internal boundaries (between track i and i+1), rescaled.
        int nBounds = expected.Count - 1;
        var target = new double[nBounds];
        double cum = 0;
        for (int i = 0; i < nBounds; i++)
        {
            cum += sumExpected > 0 ? expected[i].LengthSeconds : totalSeconds / expected.Count;
            target[i] = cum * (sumExpected > 0 ? scale : 1.0);
        }

        var mids = candidates.Select(c => c.MidSec).OrderBy(m => m).ToArray();
        var (assignment, meanError) = Assign(target, mids, toleranceSeconds);

        // Boundary times: matched → candidate mid; unmatched → expected offset snapped to a local minimum.
        var bounds = new (double Time, bool Virtual)[nBounds];
        for (int i = 0; i < nBounds; i++)
        {
            bounds[i] = assignment[i] >= 0
                ? (mids[assignment[i]], false)
                : (SilenceDetector.SnapToLocalMinimum(envelopeDb, windowSeconds, target[i], searchRadiusSec: 3.0), true);
        }

        var tracks = new List<SplitTrack>(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            double start = i == 0 ? 0 : bounds[i - 1].Time;
            double end = i == nBounds ? totalSeconds : bounds[i].Time;
            // Overlapping bounds give a degenerate alignment. Keep the end after the start.
            if (end <= start)
                end = Math.Min(totalSeconds, start + 1);
            bool anyVirtual = (i > 0 && bounds[i - 1].Virtual) || (i < nBounds && bounds[i].Virtual);
            tracks.Add(new SplitTrack(
                start, end,
                expected[i].Title,
                expected[i].Artist,
                (uint)(i + 1),
                Confidence: anyVirtual ? 0.5 : Math.Max(0.6, 0.95 - meanError / 10.0),
                anyVirtual ? BoundarySource.DurationAligned : BoundarySource.Silence));
        }
        return new AlignmentResult(tracks, meanError);
    }

    /// <summary>Monotonic assignment of targets to candidate midpoints, by dynamic programming over
    /// (target i, candidate j). A candidate can be skipped for free. A target can match a candidate
    /// within the tolerance, at a cost equal to their difference. A target can also stay unmatched,
    /// at a cost equal to the tolerance. An in-tolerance match is therefore never more expensive
    /// than leaving the target unmatched.</summary>
    private static (int[] Assignment, double MeanError) Assign(double[] target, double[] mids, double tolerance)
    {
        int n = target.Length, m = mids.Length;
        var dp = new double[n + 1, m + 1];
        var choice = new byte[n + 1, m + 1]; // 0=skip cand, 1=match, 2=unmatched target

        for (int i = 1; i <= n; i++)
            dp[i, 0] = dp[i - 1, 0] + tolerance;
        for (int i = 1; i <= n; i++)
        {
            if (i > 0) choice[i, 0] = 2;
            for (int j = 1; j <= m; j++)
            {
                double best = dp[i, j - 1]; byte pick = 0;           // candidate j unused
                double unmatched = dp[i - 1, j] + tolerance;         // target i has no silence
                if (unmatched < best) { best = unmatched; pick = 2; }
                double diff = Math.Abs(target[i - 1] - mids[j - 1]);
                if (diff <= tolerance)
                {
                    double matched = dp[i - 1, j - 1] + diff;
                    if (matched < best) { best = matched; pick = 1; }
                }
                dp[i, j] = best; choice[i, j] = pick;
            }
        }

        var assignment = new int[n];
        Array.Fill(assignment, -1);
        double errSum = 0; int matchedCount = 0;
        for (int i = n, j = m; i > 0;)
        {
            switch (choice[i, j])
            {
                case 0: j--; break;
                case 1:
                    assignment[i - 1] = j - 1;
                    errSum += Math.Abs(target[i - 1] - mids[j - 1]);
                    matchedCount++;
                    i--; j--; break;
                default: i--; break;
            }
        }
        return (assignment, matchedCount > 0 ? errSum / matchedCount : 0);
    }
}

using RPM.Core.Splitting;

namespace RPM.Core.Metadata.Providers;

/// <summary>
/// Chooses which disc of a multi-disc release a recording belongs to. This gives the automatic choice
/// in the disc picker, which the user can still override. The disc-per-album import makes the same
/// choice when it writes "disc N of M". Track lengths decide when they are known. Otherwise the disc
/// with the closest track count wins.
/// </summary>
public static class DiscMatcher
{
    /// <summary>The disc numbers the release lists, in ascending order. Tracks with no disc number (0)
    /// are skipped. For a single-disc release the list is empty.</summary>
    public static IReadOnlyList<uint> Discs(ReleaseDetail detail) =>
        detail.Tracks.Where(t => t.Disc > 0).Select(t => t.Disc).Distinct().OrderBy(d => d).ToList();

    /// <summary>The tracks on one disc, in disc order.</summary>
    public static IReadOnlyList<ReleaseTrackInfo> TracksOf(ReleaseDetail detail, uint disc) =>
        detail.Tracks.Where(t => t.Disc == disc).OrderBy(t => t.DiscPosition).ToList();

    /// <summary>
    /// The disc that best fits the recording, or 0 when the release is single-disc. Pass the detected
    /// track lengths when they are known (the split and vinyl path has them). Pass just the count when
    /// they are not (a fresh CD rip knows how many tracks the disc has, but not their catalogue lengths).
    /// </summary>
    public static uint BestDisc(ReleaseDetail detail, IReadOnlyList<double>? detectedLengths, int detectedCount)
    {
        var discs = Discs(detail);
        if (discs.Count <= 1)
            return discs.Count == 1 ? discs[0] : 0;

        uint best = discs[0];
        double bestScore = double.NegativeInfinity;
        foreach (var d in discs)
        {
            var tracks = TracksOf(detail, d);
            double score = detectedLengths is { Count: > 0 }
                ? ReleaseMatcher.Measure(tracks.Select(t => t.LengthSeconds).ToList(), detectedLengths).Score
                // No lengths: the disc whose track count is closest is the best guess.
                : 1.0 / (1 + Math.Abs(tracks.Count - detectedCount));

            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }
}

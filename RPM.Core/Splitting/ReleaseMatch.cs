namespace RPM.Core.Splitting;

/// <summary>
/// The basis of a match: what the score was built from. A score alone hides the difference between
/// "the track lengths line up" and "we only had a track count". These are different evidence, so the
/// basis is stored with the score, and the UI reads it first.
/// </summary>
public enum MatchBasis
{
    /// <summary>Nothing to compare: an empty listing, or nothing detected.</summary>
    None,

    /// <summary>The track counts differ, so only the album's total running time could be weighed.
    /// This is the weakest basis. It usually means a listing for a different edition.</summary>
    TotalOnly,

    /// <summary>The counts agree but the catalogue gives no per-track times. The strongest single
    /// signal, the right number of tracks, is there. The lengths could not confirm it. This is a
    /// real match that is not yet proven. It must not be shown as the low score that a wrong count
    /// earns.</summary>
    CountOnly,

    /// <summary>The counts agree and the listing has times: the real comparison, with drift divided
    /// out.</summary>
    Lengths,
}

/// <summary>
/// How well a release's track listing matches what was detected in the audio.
/// </summary>
/// <param name="Score">0..1. Above <see cref="ReleaseMatcher.GoodMatch"/> is worth trusting. Read
/// <see cref="Basis"/> first, though. A <see cref="MatchBasis.CountOnly"/> score is deliberately
/// below that band, because a count is evidence and not proof.</param>
/// <param name="Basis">What the score was built from.</param>
/// <param name="ExpectedCount">How many tracks the release lists.</param>
/// <param name="DetectedCount">How many tracks were found in the audio.</param>
/// <param name="MeanErrorSeconds">Average distance between a detected track and its counterpart, after
/// drift is removed. NaN when no times were paired (<see cref="MatchBasis.CountOnly"/> or
/// <see cref="MatchBasis.TotalOnly"/>).</param>
/// <param name="DriftFactor">How much faster or slower the record ran than the catalogue says. 1.0 is
/// exact. 1.02 means a deck running 2% fast. This is a fact about the recording, not a fault in the
/// release.</param>
public sealed record ReleaseMatch(
    double Score,
    MatchBasis Basis,
    int ExpectedCount,
    int DetectedCount,
    double MeanErrorSeconds,
    double DriftFactor)
{
    /// <summary>The release lists exactly as many tracks as were detected. This must be true before
    /// the lengths can line up at all.</summary>
    public bool CountMatches => ExpectedCount > 0 && ExpectedCount == DetectedCount;
}

/// <summary>
/// Scores a candidate release against the tracks detected in a recording, so the search results can
/// say which pressing is the one on the turntable.
///
/// <para>The purpose is not to find an album by its lengths. It is to check the albums that the user's
/// search already returned. An album search can return a 1977 UK pressing, a 1984 CD and a 2013
/// reissue of the same album, and they differ by seconds per track. The recording shows which one it
/// is.</para>
///
/// <para>Speed drift is removed before anything is compared. A turntable running slightly fast
/// stretches every track by the same proportion. So a whole-album drift factor is a property of the
/// deck, not evidence against the release. A wrong release is wrong track by track, in both
/// directions, and drift cannot explain that away.</para>
///
/// <para>The score is honest about what it could check. A listing with the right count but no times
/// is not a low match. It is a strong match that the lengths could not confirm. The
/// <see cref="MatchBasis"/> says so, so the score does not show a misleading low percentage.</para>
/// </summary>
public static class ReleaseMatcher
{
    /// <summary>The largest per-track error, after drift, that still counts as a plausible match.
    /// Detected boundaries sit in silence, and catalogue times are rounded to the second. A few
    /// seconds of slack is therefore needed.</summary>
    public const double DefaultToleranceSeconds = 6.0;

    /// <summary>Above this score, a length match is good enough to fill in the split dialog.</summary>
    public const double GoodMatch = 0.7;

    /// <summary>What a right track count is worth on its own, with no times to confirm it. This is
    /// below <see cref="GoodMatch"/>, because a count is strong evidence and not proof. It is still
    /// well above the range that a wrong count can reach. The count must be right before any length
    /// can line up.</summary>
    public const double CountOnlyScore = 0.6;

    /// <summary>
    /// Measures <paramref name="expectedLengths"/> (the release's track times, nulls where the
    /// catalogue has none) against <paramref name="detectedLengths"/> (what was found in the audio).
    /// </summary>
    public static ReleaseMatch Measure(
        IReadOnlyList<double?> expectedLengths,
        IReadOnlyList<double> detectedLengths,
        double toleranceSeconds = DefaultToleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(expectedLengths);
        ArgumentNullException.ThrowIfNull(detectedLengths);

        int expected = expectedLengths.Count;
        int detected = detectedLengths.Count;
        if (expected == 0 || detected == 0)
            return new ReleaseMatch(0, MatchBasis.None, expected, detected, double.NaN, 1);

        double detectedTotal = detectedLengths.Sum();
        double expectedTotal = expectedLengths.Sum(l => l ?? 0);

        // Counts differ: the listing is for a different edition, such as one with a bonus track, a side
        // that was not recorded, or two songs that the detector joined together. Only the total running
        // time is left to weigh, under a ceiling that falls as the gap widens.
        if (expected != detected)
        {
            double drift = expectedTotal > 0 ? detectedTotal / expectedTotal : 1;
            return new ReleaseMatch(
                TotalOnlyScore(detectedTotal, expectedTotal, Math.Abs(expected - detected)),
                MatchBasis.TotalOnly, expected, detected, double.NaN, drift);
        }

        // Counts agree, so pair them off. Drift is measured over the pairs only. A track that the
        // catalogue gives no time for adds real seconds to the recording but nothing to the listing.
        // Counting it would make the deck look wildly fast.
        double pairedDetected = 0, pairedExpected = 0;
        int paired = 0;
        for (int i = 0; i < detected; i++)
        {
            if (expectedLengths[i] is not { } exp || exp <= 0)
                continue;
            pairedDetected += detectedLengths[i];
            pairedExpected += exp;
            paired++;
        }

        // The right number of tracks, but the catalogue names no times for any of them. This is common
        // for vinyl on MusicBrainz. The count is the strongest single signal, so this is a real match
        // that the lengths could not confirm. It is scored as such and labelled CountOnly.
        if (paired == 0)
            return new ReleaseMatch(CountOnlyScore, MatchBasis.CountOnly, expected, detected, double.NaN, 1);

        double drift2 = pairedExpected > 0 ? pairedDetected / pairedExpected : 1;

        double sumError = 0;
        for (int i = 0; i < detected; i++)
        {
            if (expectedLengths[i] is not { } exp || exp <= 0)
                continue;   // No catalogue time for this track. The other tracks still count.
            sumError += Math.Abs(detectedLengths[i] - exp * drift2);
        }

        double meanError = sumError / paired;
        double closeness = Math.Clamp(1 - meanError / toleranceSeconds, 0, 1);

        // Half the score is for having the right number of tracks, and half is for the lengths being
        // right. So a listing that fits loosely still ranks above one that cannot fit at all.
        return new ReleaseMatch(0.5 + 0.5 * closeness, MatchBasis.Lengths, expected, detected, meanError, drift2);
    }

    /// <summary>
    /// A count that does not match can only be scored on total running time. The ceiling falls as the
    /// gap widens. One track out means two songs joined together or a missed side, and the record on
    /// the deck may still be this one. Many tracks out means a different release. This limit keeps a
    /// vinyl detection that is off by one side from looking like a different release.
    /// </summary>
    private static double TotalOnlyScore(double detectedTotal, double expectedTotal, int countDelta)
    {
        if (expectedTotal <= 0 || detectedTotal <= 0)
            return 0;

        double ceiling = Math.Clamp(0.55 - 0.1 * (countDelta - 1), 0.2, 0.55);
        double totalError = Math.Abs(detectedTotal - expectedTotal) / detectedTotal;
        double totalScore = Math.Clamp(1 - totalError * 4, 0, 1);
        return totalScore * ceiling;
    }
}

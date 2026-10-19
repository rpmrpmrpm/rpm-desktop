using RPM.Core.Splitting;

namespace RPM.Core.Recording;

/// <summary>
/// Picks the stretch of a recorded record to take a noise profile from: the quietest gap between two
/// tracks on the same side.
///
/// <para>The tracks are trimmed to their own sound before this is asked
/// (<see cref="SideRegions.TrimTracksToMusic"/>), so the space between two of them is empty groove,
/// which is what a profile has to describe. A gap that contains a side join is skipped: it holds the
/// needle lift, the flip and the next needle drop, and none of those is the record's noise. The
/// lead-in and the run-out are never offered for the same reason, and because the lead-in is often
/// the most scratched groove on the side.</para>
///
/// <para>The quietest gap wins rather than the longest. The lathe cuts every gap on a side to about
/// the same length, so an unusually long "gap" is more likely a quiet passage the trim did not count
/// as music, and a profile taken from music makes the gate remove the music.</para>
/// </summary>
public static class QuietStretch
{
    /// <summary>The shortest stretch a profile is taken from. Shorter than this and the profile
    /// describes one moment of the groove rather than its noise.</summary>
    public const double MinSeconds = 0.5;

    /// <summary>Left off each end of a gap, so the tail of one track and the first sound of the next
    /// stay out of the profile.</summary>
    public const double EdgeSeconds = 0.2;

    /// <summary>The most of a gap that is selected, from its middle. A few seconds of groove is a
    /// complete profile; more only widens the selection on screen.</summary>
    public const double MaxSeconds = 4;

    /// <summary>
    /// The stretch to select, or null when no gap between two tracks is long enough.
    /// </summary>
    /// <param name="tracks">The tracks in order, as the editor holds them.</param>
    /// <param name="sideJoins">Where one recorded side gives way to the next, in seconds.</param>
    /// <param name="envelopeDb">The analyzer's level envelope, one value per window.</param>
    /// <param name="windowSeconds">The length of one envelope window.</param>
    public static TrackSpan? Pick(
        IReadOnlyList<TrackSpan> tracks,
        IReadOnlyList<double> sideJoins,
        float[] envelopeDb,
        double windowSeconds)
    {
        TrackSpan? best = null;
        double bestLevel = double.PositiveInfinity;

        for (int i = 0; i + 1 < tracks.Count; i++)
        {
            double gapStart = tracks[i].End;
            double gapEnd = tracks[i + 1].Start;

            // Inclusive at both ends: a join exactly on a track's edge still means the other side of
            // the gap is a different side of the record.
            if (sideJoins.Any(join => join >= gapStart && join <= gapEnd))
                continue;

            double from = gapStart + EdgeSeconds;
            double to = gapEnd - EdgeSeconds;
            if (to - from < MinSeconds)
                continue;

            if (to - from > MaxSeconds)
            {
                double middle = (from + to) / 2;
                from = middle - MaxSeconds / 2;
                to = middle + MaxSeconds / 2;
            }

            double level = MedianDb(envelopeDb, windowSeconds, from, to);
            if (level < bestLevel)
            {
                bestLevel = level;
                best = new TrackSpan(from, to);
            }
        }

        return best;
    }

    /// <summary>The median envelope level between two times. A median, so one click inside the gap
    /// does not make an otherwise empty groove look loud.</summary>
    private static double MedianDb(float[] envelopeDb, double windowSeconds, double from, double to)
    {
        int first = Math.Clamp((int)(from / windowSeconds), 0, envelopeDb.Length);
        int last = Math.Clamp((int)Math.Ceiling(to / windowSeconds), first, envelopeDb.Length);
        if (last <= first)
            return double.PositiveInfinity;

        var levels = envelopeDb[first..last];
        Array.Sort(levels);
        return levels[levels.Length / 2];
    }
}

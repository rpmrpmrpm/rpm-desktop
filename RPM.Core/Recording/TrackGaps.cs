using RPM.Core.Splitting;

namespace RPM.Core.Recording;

/// <summary>The gap a record was cut with: how long the silence between two tracks runs, and how many
/// of the silences found agreed on it.</summary>
public sealed record GapProfile(double LengthSeconds, double ToleranceSeconds, int Support);

/// <summary>
/// Tells an inter-track gap from a silence inside a song.
///
/// <para>Silence alone does not mean "new track". Plenty of songs hold a beat, drop to nothing before a
/// last chorus, or fade to the noise floor and come back. Length does mean something, though. The gap
/// between tracks is cut by a lathe, so on any one record it is close to the same length every time. A
/// pause that belongs to the music is whatever length the music needed. So the gap is the silence
/// length that <b>repeats</b>, and the odd ones out are the song.</para>
///
/// <para>The filter is a floor, not a window around that length. A pause inside a song is shorter than
/// the record's gap. A silence <i>longer</i> than the gap is still a gap. Examples are the run-in to a
/// side's last track, and a deliberate pause between movements. Cutting those loose would weld two
/// tracks together, which is the worse of the two mistakes.</para>
/// </summary>
public static class TrackGaps
{
    /// <summary>How much two gaps may differ and still count as the same length. This is proportional,
    /// because a 3 s gap varies more than a 1 s gap. A lathe is consistent, but not exact.</summary>
    public const double DefaultRelativeTolerance = 0.35;

    /// <summary>Floor under the proportional tolerance, for short gaps.</summary>
    public const double DefaultAbsoluteToleranceSeconds = 0.4;

    /// <summary>Silences that must agree before a length counts as this record's gap. Two is the least
    /// that can be called a repeat; below it there is no pattern, only a coincidence.</summary>
    public const int MinimumSupport = 2;

    /// <summary>
    /// The repeated silence length among <paramref name="candidates"/>, or null when nothing repeats.
    /// In that case the caller should keep every candidate rather than guess. No pattern is not the
    /// same as no gaps.
    /// </summary>
    public static GapProfile? Profile(
        IReadOnlyList<SilenceCandidate> candidates,
        double relativeTolerance = DefaultRelativeTolerance,
        double absoluteTolerance = DefaultAbsoluteToleranceSeconds,
        int minimumSupport = MinimumSupport)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count < minimumSupport)
            return null;

        var lengths = candidates.Select(Length).ToList();

        double bestSeed = 0;
        int bestSupport = 0;
        foreach (var seed in lengths)
        {
            double tolerance = Tolerance(seed, relativeTolerance, absoluteTolerance);
            int support = lengths.Count(l => Math.Abs(l - seed) <= tolerance);

            // Most agreement wins. On a tie the longer length wins: a gap is a deliberate silence, and
            // it is the pauses in the music that are the short ones.
            if (support > bestSupport || (support == bestSupport && seed > bestSeed))
            {
                bestSupport = support;
                bestSeed = seed;
            }
        }

        if (bestSupport < minimumSupport)
            return null;

        // The gap length is the median of the silences that agreed, not the seed that found them. One
        // long outlier in the cluster should not drag the gap out with it.
        double seedTolerance = Tolerance(bestSeed, relativeTolerance, absoluteTolerance);
        var agreeing = lengths.Where(l => Math.Abs(l - bestSeed) <= seedTolerance).Order().ToList();
        double median = agreeing[agreeing.Count / 2];

        return new GapProfile(median, Tolerance(median, relativeTolerance, absoluteTolerance), bestSupport);
    }

    /// <summary>Keeps the silences long enough to be this record's gap, dropping the pauses that belong
    /// to the music.</summary>
    public static IReadOnlyList<SilenceCandidate> KeepGaps(
        IReadOnlyList<SilenceCandidate> candidates, GapProfile profile)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        double floor = profile.LengthSeconds - profile.ToleranceSeconds;
        return candidates.Where(c => Length(c) >= floor).ToList();
    }

    /// <summary>How far above the record's own noise floor a silence may sit and still count as a gap.
    /// A groove with nothing cut into it lands on the floor. A pause inside a song keeps a reverb tail,
    /// a room tone, or an amplifier hum: a few dB of something.</summary>
    public const double DefaultFloorToleranceDb = 4.0;

    /// <summary>
    /// The record's noise floor, from the two things that know it, taking whichever heard less.
    ///
    /// <para>Neither estimate is trustworthy alone. The lead-in is where the needle lands and where the
    /// record gets handled, so it is often the most scratched groove on the side, and it reads far too
    /// loud. The silences' own floors are only as good as the silences. If most of them turn out to be
    /// pauses in songs, their middle is not the floor either. But the two fail in <i>opposite</i>
    /// directions. A scratched lead-in reads high, and real gaps read low. So the quieter of the two is
    /// the better answer, whichever one went wrong.</para>
    ///
    /// <para>The gap floors are taken at the median, not the minimum. The quietest silence in a vinyl
    /// recording is usually the stretch before the needle landed. That is electrical silence, and it is
    /// quieter than any groove ever gets.</para>
    /// </summary>
    public static double? EstimateNoiseFloor(IReadOnlyList<SilenceCandidate> candidates, double? leadInDb)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        double? fromGaps = null;
        if (candidates.Count > 0)
        {
            var floors = candidates.Select(c => c.FloorDb).Order().ToList();
            fromGaps = floors[floors.Count / 2];
        }

        return (leadInDb, fromGaps) switch
        {
            (double lead, double gaps) => Math.Min(lead, gaps),
            (double lead, null) => lead,
            (null, double gaps) => gaps,
            _ => null,
        };
    }

    /// <summary>
    /// Keeps the silences that reach the record's own noise floor.
    ///
    /// <para>The deciding question a length cannot answer is whether this silence is <i>empty</i>.
    /// Between two tracks the groove carries nothing but surface noise, so it sits on the floor
    /// measured from the lead-in. A song holding a beat almost never gets that far down, because the
    /// tail of the last chord is still there. Depth and repeated length disagree about different
    /// mistakes, which is why both are worth applying.</para>
    /// </summary>
    public static IReadOnlyList<SilenceCandidate> KeepAtNoiseFloor(
        IReadOnlyList<SilenceCandidate> candidates, double noiseFloorDb,
        double toleranceDb = DefaultFloorToleranceDb)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        double ceiling = noiseFloorDb + toleranceDb;
        var atFloor = candidates.Where(c => c.FloorDb <= ceiling).ToList();

        // Every silence rejected means the floor we were handed does not describe this recording.
        // Keeping them all is the safe read: a false split is editable, a missing one is invisible.
        return atFloor.Count == 0 ? candidates : atFloor;
    }

    /// <summary>Profiles and filters in one go. Returns the input untouched when no length
    /// repeats.</summary>
    public static IReadOnlyList<SilenceCandidate> KeepRepeatedGaps(
        IReadOnlyList<SilenceCandidate> candidates,
        double relativeTolerance = DefaultRelativeTolerance,
        double absoluteTolerance = DefaultAbsoluteToleranceSeconds,
        int minimumSupport = MinimumSupport)
    {
        var profile = Profile(candidates, relativeTolerance, absoluteTolerance, minimumSupport);
        return profile is null ? candidates : KeepGaps(candidates, profile);
    }

    private static double Length(SilenceCandidate c) => c.EndSec - c.StartSec;

    private static double Tolerance(double length, double relative, double absolute) =>
        Math.Max(absolute, relative * length);
}

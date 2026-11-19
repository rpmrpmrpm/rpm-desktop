using RPM.Core.Analysis;

namespace RPM.Core.Library;

/// <summary>
/// What the sound model heard on a shelf, and the other facts the six feeling lists need beyond the
/// meter's three numbers. <see cref="MoodShelf.Build"/> takes it as one argument.
/// </summary>
/// <param name="FeelingsByPath">Each heard track's scores, by relative path
/// (<see cref="LibraryDatabase.QuerySoundFeelings"/>). A track the sound pass has not reached is absent,
/// and is in none of the four feeling lists. That is the normal state of a shelf the pass is still
/// walking, and of a cloud-only track, which the pass cannot open.</param>
/// <param name="HolidayPaths">The tracks that are holiday music, Christmas or New Year
/// (<see cref="HolidayMusic.IsHoliday"/>).</param>
/// <param name="NewYearPaths">The holiday tracks that are about the new year
/// (<see cref="HolidayMusic.IsNewYear"/>). The New Year's Eve list opens with them.</param>
/// <param name="ZenFolders">The records that are Zen records (<see cref="MoodBands.ZenFolders"/>).</param>
/// <param name="HolidaySeason">Whether it is the holiday season now (<see cref="HolidayMusic.InSeason"/>).
/// Out of it, the holiday songs are kept out of every list.</param>
/// <param name="NewYearsEveTime">Whether the New Year's Eve list is offered now
/// (<see cref="HolidayMusic.IsNewYearsEveTime"/>). Out of those days it is empty.</param>
/// <param name="MelancholyShown">Whether Melancholy is on screen. Only then does it take its songs out
/// of Chill: a song must not leave Chill for a list nobody can open.</param>
/// <param name="ChristmasTime">Whether the Merry Christmas list is offered now
/// (<see cref="HolidayMusic.IsChristmasTime"/>). Out of those days it is empty.</param>
public sealed record ShelfHearing(
    IReadOnlyDictionary<string, FeelingScores> FeelingsByPath,
    IReadOnlySet<string> HolidayPaths,
    IReadOnlySet<string> NewYearPaths,
    IReadOnlySet<string> ZenFolders,
    bool HolidaySeason,
    bool NewYearsEveTime,
    bool MelancholyShown,
    bool ChristmasTime = false)
{
    /// <summary>A shelf nobody has heard, out of season: the six lists are empty and nothing else
    /// changes. What a shelf is before the sound pass reaches it.</summary>
    public static readonly ShelfHearing Nothing = new(
        new Dictionary<string, FeelingScores>(),
        new HashSet<string>(),
        new HashSet<string>(),
        new HashSet<string>(),
        HolidaySeason: false,
        NewYearsEveTime: false,
        MelancholyShown: false);

    /// <summary>Whether this track is left out of every list because it is holiday music out of
    /// season.</summary>
    public bool OutOfSeason(string relativePath) => !HolidaySeason && HolidayPaths.Contains(relativePath);

    /// <summary>Whether this track is Christmas music: holiday music that is not about the new year. The
    /// New Year's Eve list holds none, and the Merry Christmas list holds nothing else.</summary>
    public bool IsChristmas(string relativePath) =>
        HolidayPaths.Contains(relativePath) && !NewYearPaths.Contains(relativePath);
}

/// <summary>
/// Where each heard track sits in its own genre for each feeling, as a percentile of the feeling's score.
///
/// <para>The shape of <see cref="MoodBands"/>, for scores and not for the meter's numbers: a genre with
/// <see cref="MoodBands.LeastForOwnCut"/> heard tracks is cut against itself, and any other track against
/// everything heard. A tie counts as half. Pure and database-free.</para>
/// </summary>
internal sealed class FeelingBands
{
    /// <summary>The four feelings cut within a genre, with the kind each one is.</summary>
    internal static readonly IReadOnlyList<(Feeling Feeling, SmartPlaylistKind Kind)> WithinGenre =
    [
        (Feeling.Melancholy, SmartPlaylistKind.Melancholy),
        (Feeling.Happy, SmartPlaylistKind.Happy),
        (Feeling.Angry, SmartPlaylistKind.Angry),
        (Feeling.Romantic, SmartPlaylistKind.Romantic),
    ];

    private const string Everything = "\0all";

    private readonly Dictionary<(string Cohort, Feeling Feeling), double[]> _sorted;

    private FeelingBands(Dictionary<(string Cohort, Feeling Feeling), double[]> sorted) => _sorted = sorted;

    /// <summary>Sorts every feeling's scores, per genre and over everything heard.</summary>
    /// <param name="heard">Each heard track with its genre (null for none the app recognises) and its
    /// scores.</param>
    internal static FeelingBands Cut(IReadOnlyList<(string? Genre, FeelingScores Scores)> heard)
    {
        var byGenre = new Dictionary<string, List<FeelingScores>>(StringComparer.Ordinal);
        foreach (var (genre, scores) in heard)
        {
            if (genre is null)
                continue;
            if (!byGenre.TryGetValue(genre, out var list))
                byGenre[genre] = list = [];
            list.Add(scores);
        }

        var sorted = new Dictionary<(string, Feeling), double[]>();
        foreach (var (feeling, _) in WithinGenre)
        {
            sorted[(Everything, feeling)] = Sorted(heard.Select(h => h.Scores), feeling);
            foreach (var (genre, list) in byGenre)
                if (list.Count >= MoodBands.LeastForOwnCut)
                    sorted[(genre, feeling)] = Sorted(list, feeling);
        }

        return new FeelingBands(sorted);

        static double[] Sorted(IEnumerable<FeelingScores> scores, Feeling feeling)
        {
            var values = scores.Select(s => s.For(feeling)).ToArray();
            Array.Sort(values);
            return values;
        }
    }

    /// <summary>Where <paramref name="score"/> sits for <paramref name="feeling"/> in the track's own
    /// genre, 0..1, or among everything heard where the genre is too small or unknown.</summary>
    internal double RankWithin(string? genre, Feeling feeling, double score)
    {
        var sorted = genre is not null && _sorted.TryGetValue((genre, feeling), out var own)
            ? own
            : _sorted[(Everything, feeling)];

        if (sorted.Length == 0)
            return 0.5;

        int below = LowerBound(sorted, score, inclusive: false);
        int upTo = LowerBound(sorted, score, inclusive: true);
        return (below + upTo) / 2.0 / sorted.Length;
    }

    /// <summary>How many values are under <paramref name="value"/>, or at most it when
    /// <paramref name="inclusive"/>.</summary>
    private static int LowerBound(double[] sorted, double value, bool inclusive)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (sorted[mid] < value || (inclusive && sorted[mid] == value)) low = mid + 1; else high = mid;
        }
        return low;
    }
}

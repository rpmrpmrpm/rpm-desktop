using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A mood list narrowed to one era.
///
/// <para>The bands are cut on how a record SOUNDS, not on when it was made, so a genre's quiet end
/// can cover forty years, and the playlist keeps changing style. This step prevents that. It is
/// tested through <see cref="MoodShelf.Build"/> and the list it returns, which is what the playlists
/// actually read.</para>
///
/// <para>The tests are strict about the two ways it could silently do harm: emptying a list, and
/// dropping songs that have no year in their tags.</para>
/// </summary>
public class MoodEraTests
{
    /// <summary>
    /// A shelf whose numbers rise evenly, with a year on every track. It is the same ramp
    /// <c>MoodShelfTests</c> uses, so the percentiles are known and the calm quarter is the bottom
    /// quarter.
    ///
    /// <para>One record every two tracks and one act per record, so neither of
    /// <c>MoodList.Spread</c>'s caps applies and everything dropped here was dropped by the era alone.
    /// The year is also written into the PATH, so a test can read back which decade was kept.</para>
    /// </summary>
    /// <param name="yearAt">The year for the track at that position in the ramp.</param>
    private static List<MeasuredMood> Ramp(int count, Func<int, int> yearAt)
    {
        var rows = new List<MeasuredMood>(count);
        for (var i = 0; i < count; i++)
        {
            var at = (double)i / (count - 1);
            var year = yearAt(i);
            rows.Add(new MeasuredMood(
                $"y{year}/rec-{i / 2}/{i}.flac", $"rec-{i / 2}",
                at, at, at, false, $"act {i / 2}", year, null,
                // No loudness: this file is about eras, not loudness.
                null));
        }
        return rows;
    }

    /// <summary>A collection with no catalogue facts. These tests are about the era window, and a
    /// flavour narrowing on top would be a second thing changing.</summary>
    private static readonly Dictionary<string, CatalogueFacts> NoCatalogue = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> FiledAs(IEnumerable<MeasuredMood> rows, string genre)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            map[r.Folder] = genre;
        return map;
    }

    private static IReadOnlyList<string> CalmOf(List<MeasuredMood> rows) =>
        MoodShelf.Build(rows, FiledAs(rows, "Rock"), NoCatalogue, Likes.None).Calm.WholeList.Songs;

    /// <summary>
    /// End to end: a pool that is mostly one decade with a few modern records in it, for example a
    /// "Northern Soul" list that includes Daft Punk, or a swing list of nineties singers.
    /// </summary>
    [Fact]
    public void A_scattering_of_another_decade_is_dropped()
    {
        // Four in five from the sixties, one in five from the 2000s, all the way up the ramp, so the
        // calm quarter holds both and the narrowing has something to do.
        //
        // The ramp is long enough that the sixties ALONE satisfy SmartPlaylists.EnoughInOneEra.
        // Otherwise the window would widen to the 2000s to find enough tracks, and this test would
        // be checking that a window with every reason to grow did not grow.
        var calm = CalmOf(Ramp(2400, i => i % 5 == 0 ? 2001 : 1965));

        Assert.NotEmpty(calm);
        Assert.Contains(calm, p => p.StartsWith("y1965/", StringComparison.Ordinal));
        Assert.DoesNotContain(calm, p => p.StartsWith("y2001/", StringComparison.Ordinal));
    }

    /// <summary>An undated song is kept whatever the window is. It cannot be placed in a decade, so it
    /// is no evidence of a mixed list, and dropping it would punish a shelf for its tags, not for its
    /// music.</summary>
    [Fact]
    public void An_undated_song_is_kept_while_a_dated_outlier_is_not()
    {
        // The brackets round the remainder are required. Without them C# reads
        // "i % 5 switch { ... }" as "i % (5 switch { ... })": the switch applies to the 5, returns
        // 1965, and every year becomes i % 1965, which for a ramp of eight hundred is i. It compiles,
        // it runs, and it silently returns a shelf with eight hundred different years in it.
        // Long enough that the sixties alone satisfy EnoughInOneEra (see the test above).
        var calm = CalmOf(Ramp(2400, i => (i % 5) switch
        {
            0 => 2001,   // the outlier decade
            1 => 0,      // nobody typed a year
            _ => 1965,   // what the shelf is mostly about
        }));

        Assert.Contains(calm, p => p.StartsWith("y1965/", StringComparison.Ordinal));
        Assert.Contains(calm, p => p.StartsWith("y0/", StringComparison.Ordinal));
        Assert.DoesNotContain(calm, p => p.StartsWith("y2001/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A shelf with no years on it is left exactly as it is.
    ///
    /// <para>Narrowing on the few tracks that do have a year would drop most of a badly tagged
    /// library because of a fault in the tags. Here the 2000s records stay, because there is not
    /// enough dated music to say what era the shelf is about.</para>
    /// </summary>
    [Fact]
    public void A_shelf_with_too_few_years_on_it_is_left_alone()
    {
        var calm = CalmOf(Ramp(800, i => i % 40 == 0 ? 2001 : 0));

        Assert.Contains(calm, p => p.StartsWith("y0/", StringComparison.Ordinal));
        Assert.Contains(calm, p => p.StartsWith("y2001/", StringComparison.Ordinal));
    }

    /// <summary>A pool that is already one decade keeps all of it. The floor is a minimum to narrow
    /// TO, never a quota to cut down to.</summary>
    [Fact]
    public void A_pool_that_is_already_one_era_is_not_cut_down()
    {
        var oneEra = CalmOf(Ramp(800, _ => 1965));
        var undated = CalmOf(Ramp(800, _ => 0));

        Assert.Equal(undated.Count, oneEra.Count);
    }

    /// <summary>Two reads of one shelf narrow to the same era. The earliest decade wins a tie, so a
    /// shelf split evenly between two cannot give different answers on two launches. Every generated
    /// list's ORDER BY follows the same rule.</summary>
    [Fact]
    public void One_shelf_narrows_to_one_era_every_time()
    {
        // Split evenly, and each half alone satisfies EnoughInOneEra, so the window has no reason to
        // widen and the tie rule decides. That is what this test checks.
        var rows = Ramp(2400, i => i % 2 == 0 ? 1975 : 1995);

        var first = CalmOf(rows);
        var again = CalmOf(rows);

        Assert.Equal(first, again);
        Assert.Contains(first, p => p.StartsWith("y1975/", StringComparison.Ordinal));
        Assert.DoesNotContain(first, p => p.StartsWith("y1995/", StringComparison.Ordinal));
    }

    /// <summary>Narrowing never empties a list. A row that has music in it must not disappear
    /// because of when the music was made.</summary>
    [Theory]
    [InlineData(1900)]
    [InlineData(1965)]
    [InlineData(2026)]
    public void Narrowing_never_empties_a_list(int year)
    {
        Assert.NotEmpty(CalmOf(Ramp(800, _ => year)));
    }
}

using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What a song's words are allowed to do to where it sits in the Chill list.
///
/// <para>The property that carries most of these: a song with no reading must sit exactly where it
/// would sit without the lyric lift. A shelf with no readings must produce the same list as without
/// the lift, and a shelf where a tenth has been read must not split into the read tenth and the
/// rest.
/// </para>
/// </summary>
public class LyricLiftTests
{
    private static MeasuredMood Row(string path, double? valence, double at = 0.1) =>
        new(path, path, at, at, at, IsExplicit: false, Artist: path, Year: 1990,
            LyricValence: valence, Loudness: null);

    /// <summary>A shelf of unread songs, plus however many read ones the test wants.</summary>
    private static List<MeasuredMood> Shelf(int unread, params double[] read)
    {
        var rows = new List<MeasuredMood>();
        for (var i = 0; i < unread; i++)
            rows.Add(Row($"unread-{i:D3}", null));
        for (var i = 0; i < read.Length; i++)
            rows.Add(Row($"read-{i:D3}", read[i]));
        return rows;
    }

    private static double[] Spread(int count, double from = -2, double to = 2) =>
        [.. Enumerable.Range(0, count).Select(i => from + ((to - from) * i / (count - 1.0)))];

    // ── what it must not do ──

    [Fact]
    public void A_song_nobody_has_read_is_not_moved()
    {
        var rows = Shelf(100, Spread(50));
        var lift = LyricLift.Over(rows);

        foreach (var row in rows.Where(r => r.LyricValence is null))
            Assert.Equal(0, lift.For(row));
    }

    [Fact]
    public void A_shelf_nobody_has_read_moves_nothing()
    {
        var rows = Shelf(100);
        var lift = LyricLift.Over(rows);

        Assert.Equal(0, lift.Read);
        Assert.All(rows, r => Assert.Equal(0, lift.For(r)));
    }

    /// <summary>Too few readings to be a position rather than an accident of what has been played.
    /// </summary>
    [Fact]
    public void Too_few_readings_move_nothing()
    {
        var rows = Shelf(100, Spread(LyricLift.LeastRead - 1));
        var lift = LyricLift.Over(rows);

        Assert.Equal(0, lift.Read);
        Assert.All(rows, r => Assert.Equal(0, lift.For(r)));
    }

    /// <summary>
    /// The bias this whole design exists to avoid, asserted directly.
    ///
    /// <para>Real readings average well above the middle of the lexicon's scale, +0.43 over 1,184
    /// songs on a real library. Added as a scaled reading rather than as a place among the others, almost
    /// every song that had one would be lifted above every song that had not, and the Chill list would
    /// quietly become "songs you have played", because a sheet is only fetched when a record is played.
    /// </para>
    /// </summary>
    [Fact]
    public void A_shelf_whose_readings_are_all_positive_still_moves_half_of_them_down()
    {
        // Every reading positive, as a real shelf's are.
        var rows = Shelf(100, Spread(60, from: 0.1, to: 3.0));
        var lift = LyricLift.Over(rows);

        var read = rows.Where(r => r.LyricValence is not null).ToList();
        var up = read.Count(r => lift.For(r) > 0);
        var down = read.Count(r => lift.For(r) < 0);

        Assert.InRange(up, 25, 35);
        Assert.InRange(down, 25, 35);
    }

    /// <summary>The lift is bounded, so the words can reorder songs the measurements put together and
    /// can never carry one across the band.</summary>
    [Fact]
    public void No_song_is_moved_further_than_the_weight()
    {
        var rows = Shelf(20, Spread(80, from: -4, to: 4));
        var lift = LyricLift.Over(rows);

        Assert.All(rows, r => Assert.InRange(lift.For(r), -LyricLift.Weight, LyricLift.Weight));
    }

    // ── what it does ──

    [Fact]
    public void Better_words_are_lifted_over_worse_ones()
    {
        var rows = Shelf(50, Spread(40));
        var lift = LyricLift.Over(rows);

        var bleak = rows.First(r => r.LyricValence == -2);
        var bright = rows.First(r => r.LyricValence == 2);

        Assert.True(lift.For(bleak) < 0);
        Assert.True(lift.For(bright) > 0);
        Assert.True(lift.For(bright) > lift.For(bleak));
    }

    /// <summary>A reading every read song shares says nothing about which of them is better, so it
    /// places all of them in the middle rather than all of them at one end.</summary>
    [Fact]
    public void Readings_that_all_agree_move_nobody()
    {
        var rows = Shelf(50, [.. Enumerable.Repeat(1.5, 40)]);
        var lift = LyricLift.Over(rows);

        Assert.All(rows, r => Assert.Equal(0, lift.For(r), 10));
    }

    /// <summary>The lift is a place among the collection's own readings, not a point on the lexicon's
    /// scale. Two shelves whose readings sit in different parts of that scale but in the same ORDER
    /// must lift their songs the same way, because a collection is only ever compared with itself.
    /// </summary>
    [Fact]
    public void The_lift_is_a_place_in_this_collection_and_not_a_point_on_a_scale()
    {
        var low = LyricLift.Over(Shelf(0, Spread(40, from: -3.0, to: -1.0)));
        var high = LyricLift.Over(Shelf(0, Spread(40, from: 1.0, to: 3.0)));

        // The best-read song of each shelf, whatever its reading was.
        Assert.Equal(low.For(Row("x", -1.0)), high.For(Row("x", 3.0)), 10);
        Assert.Equal(low.For(Row("x", -3.0)), high.For(Row("x", 1.0)), 10);
    }
}

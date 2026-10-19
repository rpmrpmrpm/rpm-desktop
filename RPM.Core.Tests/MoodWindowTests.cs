using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// Where each reading of a generated list starts in its pool.
///
/// <para>A reading must not be an offset in songs stepped by a fixed fifty. The pool is whatever the
/// narrowing leaves, and a pool whose size divides fifty would then never move: a list showing eleven
/// songs from a pool of twenty-five could never show the other fourteen, however many times the
/// button was pressed.</para>
/// </summary>
public class MoodWindowTests
{
    private static MoodList.Song Song(int n) =>
        new($"{n:D3}.flac", "Rock", $"rec-{n:D3}", $"act {n:D3}", 1990, false, [],
            new MoodList.Sound(0.5, 0.5, 0.5), WithinGenreBy: 0);

    private static MoodPool Pool(int songs) => new([.. Enumerable.Range(0, songs).Select(Song)]);

    /// <summary>Pool sizes that divide fifty. A fixed fifty-song step would never move on these.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    public void A_pool_that_divides_the_old_step_still_moves(int songs)
    {
        var pool = Pool(songs);
        var shown = Math.Max(1, songs / 3);

        var first = pool.Window(shown, 0);
        var next = pool.Window(shown, 1);

        Assert.NotEqual(first, next);
    }

    /// <summary>
    /// Every song in the pool can be reached.
    ///
    /// <para>This also keeps the button correct: <c>SmartPlaylistCanRegenerate</c> offers a new
    /// list whenever the pool is larger than the list, and that is only right if the extra songs can
    /// actually be shown.</para>
    /// </summary>
    [Theory]
    [InlineData(10, 3)]
    [InlineData(25, 11)]
    [InlineData(37, 9)]
    [InlineData(50, 50)]
    public void Enough_readings_reach_every_song_in_the_pool(int songs, int shown)
    {
        var pool = Pool(songs);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var reading = 0; reading < songs; reading++)
            foreach (var path in pool.Window(shown, reading))
                seen.Add(path);

        Assert.Equal(songs, seen.Count);
    }

    /// <summary>A reading is a number, so the same reading asked twice gives the same list. The pane
    /// reads a subtitle without advancing and the list itself with advancing, and the two must
    /// agree.</summary>
    [Fact]
    public void One_reading_asked_twice_is_the_same_list()
    {
        var pool = Pool(25);

        Assert.Equal(pool.Window(11, 3), pool.Window(11, 3));
    }

    /// <summary>
    /// Two readings in a row show different songs.
    ///
    /// <para>How MUCH they differ depends on the caps, not on the step. Here every song is on its own
    /// record, so one step of the start rotates the whole list; on a shelf of few records with many
    /// tracks each, the same step gives a different song from every record that has one to spare.
    /// Either way, pressing the button gives something new.</para>
    /// </summary>
    [Fact]
    public void Two_readings_in_a_row_show_different_songs()
    {
        var pool = Pool(60);

        var first = pool.Window(20, 0).ToHashSet(StringComparer.Ordinal);
        var next = pool.Window(20, 1);

        Assert.Contains(next, p => !first.Contains(p));
    }

    // ── a record's turn, which is what a refresh actually uses ──

    /// <summary>A pool where the records hold different numbers of songs, as in a real one.
    /// </summary>
    private static MoodPool Records(params int[] songsEach)
    {
        var songs = new List<MoodList.Song>();
        for (var record = 0; record < songsEach.Length; record++)
            for (var n = 0; n < songsEach[record]; n++)
                songs.Add(new($"rec-{record:D2}/{n:D2}.flac", "Rock", $"rec-{record:D2}",
                    $"act {record:D2}", 1990, false, [], new MoodList.Sound(0.5, 0.5, 0.5),
                    WithinGenreBy: 0));

        return new MoodPool(songs);
    }

    /// <summary>
    /// The main case: a record that showed one song in one reading is not used up for the next one.
    ///
    /// <para>For example, a list of 36 songs over 14 records, one of them holding 13, shows 14 songs
    /// and leaves 22 unshown. Every record with a song to spare gives a different one on the next
    /// reading, so the number of songs that change is the number of records that had songs
    /// left.</para>
    /// </summary>
    [Fact]
    public void Every_record_with_a_song_to_spare_gives_a_different_one()
    {
        var pool = Records(13, 5, 3, 3, 2, 2, 1, 1);

        var first = pool.Window(50, 0);
        var second = pool.Window(50, 1);

        Assert.Equal(8, first.Count);
        Assert.Equal(8, second.Count);

        // Six of the eight records hold more than one song, so six songs are new.
        Assert.Equal(6, second.Count(p => !first.Contains(p)));
    }

    /// <summary>Readings continue until the largest record has given all its songs, and then start
    /// again. Nothing in the pool is out of reach.</summary>
    [Fact]
    public void Enough_readings_reach_every_song_on_every_record()
    {
        var pool = Records(13, 5, 3, 3, 2, 2, 1, 1);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var reading = 0; reading < 13; reading++)
            foreach (var path in pool.Window(50, reading))
                seen.Add(path);

        Assert.Equal(13 + 5 + 3 + 3 + 2 + 2 + 1 + 1, seen.Count);
    }

    /// <summary>A record with one song repeats it, which is correct for a record with nothing else
    /// to give. It is not a reason to drop it from the list.</summary>
    [Fact]
    public void A_record_with_one_song_keeps_showing_it()
    {
        var pool = Records(1, 1, 1);

        Assert.Equal(3, pool.Window(50, 0).Count);
        Assert.Equal(3, pool.Window(50, 7).Count);
    }

    /// <summary>A pool of one has one reading, and asking again is not an error.</summary>
    [Fact]
    public void A_pool_of_one_song_is_not_a_division_by_anything()
    {
        var pool = Pool(1);

        Assert.Equal(pool.Window(1, 0), pool.Window(1, 7));
        Assert.Single(pool.Window(1, 3));
    }
}

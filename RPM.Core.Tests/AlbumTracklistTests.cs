using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>Disc-by-disc reading order for a merged multi-disc album, whose rows arrive in file-name
/// order and would otherwise interleave (1, 1, 2, 2, …).</summary>
public class AlbumTracklistTests
{
    private static TrackInfo T(uint disc, uint track, string? subtitle = null) => new()
    {
        FilePath = $"/m/{track:00} disc{disc}.flac",
        Extension = ".flac",
        Title = $"d{disc}t{track}",
        DiscNumber = disc,
        TrackNumber = track,
        DiscSubtitle = subtitle,
    };

    /// <summary>The order the library hands a two-disc folder back in: file names sorted, so both
    /// track 1s come first.</summary>
    private static TrackInfo[] Interleaved() =>
        [T(1, 1), T(2, 1), T(1, 2), T(2, 2), T(1, 3), T(2, 3)];

    [Fact]
    public void Discs_come_apart_in_order()
    {
        var discs = AlbumTracklist.ByDisc(Interleaved());

        Assert.Equal(2, discs.Count);
        Assert.Equal(1u, discs[0].DiscNumber);
        Assert.Equal(2u, discs[1].DiscNumber);
        Assert.Equal(["d1t1", "d1t2", "d1t3"], discs[0].Tracks.Select(t => t.Title));
        Assert.Equal(["d2t1", "d2t2", "d2t3"], discs[1].Tracks.Select(t => t.Title));
    }

    [Fact]
    public void InDiscOrder_is_disc_one_whole_then_disc_two()
        => Assert.Equal(["d1t1", "d1t2", "d1t3", "d2t1", "d2t2", "d2t3"],
                        AlbumTracklist.InDiscOrder(Interleaved()).Select(t => t.Title));

    [Fact]
    public void A_single_disc_album_is_one_section_so_no_divider_is_drawn()
    {
        var discs = AlbumTracklist.ByDisc([T(1, 2), T(1, 1), T(1, 3)]);

        Assert.Single(discs);
        Assert.Equal(["d1t1", "d1t2", "d1t3"], discs[0].Tracks.Select(t => t.Title));
    }

    [Fact]
    public void Untagged_disc_numbers_count_as_disc_one()
    {
        var discs = AlbumTracklist.ByDisc([T(0, 1), T(1, 2)]);

        Assert.Single(discs);
        Assert.Equal(1u, discs[0].DiscNumber);
    }

    [Fact]
    public void Untracked_files_keep_their_incoming_order_after_the_numbered_ones()
    {
        var discs = AlbumTracklist.ByDisc([T(1, 0) with { Title = "first loose" },
                                           T(1, 2),
                                           T(1, 0) with { Title = "second loose" },
                                           T(1, 1)]);

        Assert.Equal(["d1t1", "d1t2", "first loose", "second loose"],
                     discs[0].Tracks.Select(t => t.Title));
    }

    [Fact]
    public void The_disc_takes_the_first_subtitle_the_release_gives_it()
    {
        var discs = AlbumTracklist.ByDisc([T(1, 1), T(2, 1, "B-Sides"), T(2, 2, "B-Sides")]);

        Assert.Null(discs[0].Subtitle);
        Assert.Equal("B-Sides", discs[1].Subtitle);
    }

    [Fact]
    public void No_tracks_is_no_discs()
        => Assert.Empty(AlbumTracklist.ByDisc([]));

    // ── works and their movements ──

    private static TrackInfo W(string title, string? work = null) => new()
    {
        FilePath = $"/m/{title}.flac",
        Extension = ".flac",
        Title = title,
        Work = work,
    };

    /// <summary>The movements of one work are one group, headed by the work; a piece belonging to
    /// nothing is a group of its own with no work to head it.</summary>
    [Fact]
    public void The_movements_of_a_work_are_one_group()
    {
        var groups = AlbumTracklist.ByWork([
            W("Allegro", "Sonata KV 333"),
            W("Andante", "Sonata KV 333"),
            W("Allegretto", "Sonata KV 333"),
            W("Fantasy KV 397"),
        ]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Sonata KV 333", groups[0].Work);
        Assert.Equal(3, groups[0].Tracks.Count);
        Assert.Null(groups[1].Work);
        Assert.Equal("Fantasy KV 397", groups[1].Tracks[0].Title);
    }

    /// <summary>A run, not everything that names the work: a recital that comes back to a composition
    /// after playing something else is two performances of it, and one group each.</summary>
    [Fact]
    public void A_work_returned_to_later_is_a_second_group()
    {
        var groups = AlbumTracklist.ByWork([
            W("Nocturne I", "Nocturnes Op. 9"),
            W("Mazurka", "Mazurkas Op. 17"),
            W("Nocturne II", "Nocturnes Op. 9"),
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Equal(["Nocturnes Op. 9", "Mazurkas Op. 17", "Nocturnes Op. 9"],
                     groups.Select(g => g.Work));
    }

    /// <summary>A record with no works in it is one group with no heading, so a view can draw every
    /// album the same way and only the header differs.</summary>
    [Fact]
    public void An_album_with_no_works_is_one_anonymous_group()
    {
        var groups = AlbumTracklist.ByWork([W("Dreams"), W("Go Your Own Way"), W("The Chain")]);

        Assert.Single(groups);
        Assert.Null(groups[0].Work);
        Assert.Equal(3, groups[0].Tracks.Count);
    }

    /// <summary>Blank and null both mean "part of nothing", and a listing writing one where it means
    /// the other must not split the group in two.</summary>
    [Fact]
    public void A_blank_work_is_no_work()
    {
        var groups = AlbumTracklist.ByWork([W("One", ""), W("Two", null), W("Three", "   ")]);

        Assert.Single(groups);
        Assert.Null(groups[0].Work);
    }

    [Fact]
    public void No_tracks_is_no_groups()
        => Assert.Empty(AlbumTracklist.ByWork([]));

    /// <summary>"From here" is the clicked track and everything after it, in the listing's own order.
    /// A DJ set started on a track is given this list, and it decides which record starts it.</summary>
    [Fact]
    public void From_a_track_is_that_track_and_the_rest()
    {
        var list = new[] { W("One"), W("Two"), W("Three"), W("Four") };

        var tail = AlbumTracklist.From(list, list[1]);

        Assert.Equal(["Two", "Three", "Four"], tail.Select(t => t.Title));
    }

    /// <summary>Matched by file path, not by reference: the row a view was clicked in is rebuilt from
    /// the mirror and is a different object holding the same file.</summary>
    [Fact]
    public void From_matches_the_track_by_path()
    {
        var list = new[] { W("One"), W("Two"), W("Three") };

        var tail = AlbumTracklist.From(list, W("Two"));

        Assert.Equal(["Two", "Three"], tail.Select(t => t.Title));
    }

    /// <summary>A track the listing does not hold plays alone rather than not at all.</summary>
    [Fact]
    public void From_a_track_outside_the_listing_is_that_track_alone()
    {
        var stray = W("Elsewhere");

        var tail = AlbumTracklist.From([W("One"), W("Two")], stray);

        Assert.Equal([stray.FilePath], tail.Select(t => t.FilePath));
    }

    [Fact]
    public void From_the_last_track_is_that_track_alone()
    {
        var list = new[] { W("One"), W("Two") };

        Assert.Equal(["Two"], AlbumTracklist.From(list, list[1]).Select(t => t.Title));
    }

    // ── the two columns a record's tracklist is drawn in (the album list) ──

    /// <summary>Plain tracks, all one line tall: the cut is the middle, with the odd line landing in the
    /// left column so it is the taller of the two.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(5, 3)]
    [InlineData(42, 21)]
    public void ColumnCut_splits_plain_tracks_down_the_middle(int tracks, int expected)
        => Assert.Equal(expected, AlbumTracklist.ColumnCut([.. Enumerable.Repeat(1, tracks)]));

    /// <summary>A disc divider is two lines tall, so the cut moves to keep the two columns level: four
    /// tracks and a divider in front of each disc is 12 units, and the left column takes the first six.
    /// Cutting on the NUMBER of lines instead would put three lines (five units) against three lines
    /// (seven), and the two columns of a multi-disc set would have different heights.</summary>
    [Fact]
    public void ColumnCut_counts_a_divider_as_the_two_lines_it_is_drawn_as()
    {
        //          divider  t  t  divider  t  t
        int[] units = [2, 1, 1, 2, 1, 1];

        var cut = AlbumTracklist.ColumnCut(units);

        Assert.Equal(3, cut);
        Assert.Equal(units.Take(cut).Sum(), units.Skip(cut).Sum());
    }

    /// <summary>Whatever the lines are, the left column is the taller one and it is never more than a
    /// single line past half. That is the extra space the album list reserves for a record that has
    /// dividers (see AlbumListEntry.ReservedTrackHeight). Three dividers in a row is the case that
    /// needs it: they split four units against two, because a line cannot be cut down the middle.</summary>
    [Theory]
    [InlineData(new[] { 2, 1, 1, 1, 2, 1 })]
    [InlineData(new[] { 2, 1 })]
    [InlineData(new[] { 2, 2, 2 })]
    [InlineData(new[] { 1, 1, 1, 1, 1, 1, 1 })]
    public void ColumnCut_never_leaves_the_right_column_the_taller_one(int[] units)
    {
        var cut = AlbumTracklist.ColumnCut(units);

        var left = units.Take(cut).Sum();
        var right = units.Skip(cut).Sum();

        Assert.True(left >= right);
        Assert.InRange(left, (units.Sum() + 1) / 2, (units.Sum() + 1) / 2 + 1);
    }
}

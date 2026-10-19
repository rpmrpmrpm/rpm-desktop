using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// A record's block reserves the space its tracklist will take before any track has been read from
/// the disk.
///
/// <para>This lets the album list be an ordinary virtualized list. A block that changes height when
/// its tracks arrive moves every record below it, and half a screen of blocks doing that during a
/// scroll makes the list shake. The reserve is only useful while it is EXACT, which these tests
/// check. The arithmetic behind the two columns is AlbumTracklist.ColumnCut, tested in Core.</para>
/// </summary>
public class AlbumListBlockHeightTests
{
    /// <summary>The line height the reserve is counted in. 22 is the entry's own fallback, used when
    /// there is no Application.</summary>
    private const double Line = 22;

    private static TrackInfo Track(string title, uint disc = 0)
        => new()
        {
            FilePath = $"/music/Artist/Record/{title}.flac",
            Extension = ".flac",
            Title = title,
            Artist = "Artist",
            AlbumArtist = "Artist",
            Album = "Record",
            DiscNumber = disc,
        };

    private static AlbumListEntry Entry(int trackCount, int headerLines = 0)
        => AlbumListEntry.Album("Artist/Record", "Record", "1979", "Artist", trackCount, headerLines);

    [Fact]
    public void A_plain_record_reserves_half_its_tracks_in_lines()
        => Assert.Equal(6 * Line, Entry(trackCount: 11).ReservedTrackHeight);

    [Fact]
    public void A_multi_disc_set_reserves_its_dividers_and_a_line_of_slack()
        // Two discs of five: ten tracks and two dividers of two lines each is 14 units, seven per column,
        // plus one line because two-unit lines cannot always be split down the middle.
        => Assert.Equal(8 * Line, Entry(trackCount: 10, headerLines: 4).ReservedTrackHeight);

    /// <summary>The slack is only for records that have dividers. A plain record splits exactly, and a
    /// spare line under every block in the collection would be an unexplained gap.</summary>
    [Fact]
    public void A_plain_record_gets_no_slack()
        => Assert.Equal(5 * Line, Entry(trackCount: 10).ReservedTrackHeight);

    [Fact]
    public void A_record_nobody_has_counted_yet_reserves_nothing()
        => Assert.Equal(0, Entry(trackCount: 0).ReservedTrackHeight);

    [Fact]
    public void A_single_disc_record_draws_no_dividers_so_every_line_is_a_track()
    {
        var lines = AlbumTrackItem.Build([Track("One"), Track("Two"), Track("Three")]);

        Assert.All(lines, l => Assert.Equal(1, l.LineUnits));
        Assert.Equal(3, lines.Count);
    }

    /// <summary>The lines the second column gets, cut the way <see cref="AlbumListEntry.SetLines"/> cuts
    /// them. SetLines itself also writes the summary, which needs the string table that this test host
    /// does not have.</summary>
    private static int SecondColumnLines(IReadOnlyList<AlbumTrackItem> lines)
        => lines.Count - AlbumTracklist.ColumnCut([.. lines.Select(l => l.LineUnits)]);

    /// <summary>A record of one line has nothing for the second column, so it spans both. Limited to
    /// half the width, its title would be cut short next to an empty half.</summary>
    [Fact]
    public void A_record_of_one_line_spans_both_columns()
    {
        var lines = AlbumTrackItem.Build([Track("The Day Before You Came")]);

        Assert.Equal(0, SecondColumnLines(lines));
        Assert.Equal(2, AlbumListEntry.LeftSpanFor(SecondColumnLines(lines)));
    }

    [Fact]
    public void A_record_of_two_lines_keeps_two_columns()
    {
        var lines = AlbumTrackItem.Build([Track("One"), Track("Two")]);

        Assert.Equal(1, SecondColumnLines(lines));
        Assert.Equal(1, AlbumListEntry.LeftSpanFor(SecondColumnLines(lines)));
    }
}

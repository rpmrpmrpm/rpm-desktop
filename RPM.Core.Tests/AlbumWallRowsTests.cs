using RPM.Core.Library;
using Xunit;

namespace RPM.Core.Tests;

/// <summary>
/// The numbers here are from a real wall: nine rows of 172.5pt, 14pt apart, with a 2671.5pt drawer
/// open on row 0. That wall is 4163.5pt long. An estimate that gives 4164 while row 0 is on screen and
/// 1942 when row 0 is not would move the reader from 3046 back to the top, so the length must not
/// depend on which rows are on screen.
/// </summary>
public class AlbumWallRowsTests
{
    private const double Row = 172.5;
    private const double Gap = 14;

    /// <summary>What the drawer ADDS to its row, not the row's total: the wall's two row heights are
    /// 172.5 and 2671.5, and the difference between them is the drawer.</summary>
    private const double Band = 2671.5 - Row;

    private const int Count = 9;

    [Fact]
    public void WallHeight_is_the_same_whichever_rows_are_on_screen()
        => Assert.Equal(4163.5, AlbumWallRows.WallHeight(Count, Row, Gap, bandRow: 0, Band), 3);

    [Fact]
    public void WallHeight_with_nothing_open_is_the_plain_stack()
        => Assert.Equal(9 * Row + 8 * Gap, AlbumWallRows.WallHeight(Count, Row, Gap, bandRow: -1, 0), 3);

    [Fact]
    public void WallHeight_of_an_empty_wall_is_nought_not_one_row()
        => Assert.Equal(0, AlbumWallRows.WallHeight(0, Row, Gap, bandRow: -1, 0));

    [Fact]
    public void RowTop_of_the_first_row_is_the_top()
        => Assert.Equal(0, AlbumWallRows.RowTop(0, Row, Gap, bandRow: 0, Band));

    [Fact]
    public void RowTop_below_the_open_row_carries_the_drawer()
        => Assert.Equal(Row + Band + Gap, AlbumWallRows.RowTop(1, Row, Gap, bandRow: 0, Band), 3);

    [Fact]
    public void RowTop_above_the_open_row_does_not()
        => Assert.Equal(3 * (Row + Gap), AlbumWallRows.RowTop(3, Row, Gap, bandRow: 5, Band), 3);

    [Fact]
    public void RowHeight_is_uniform_but_for_the_open_row()
    {
        Assert.Equal(Row + Band, AlbumWallRows.RowHeight(4, Row, bandRow: 4, Band), 3);
        Assert.Equal(Row, AlbumWallRows.RowHeight(3, Row, bandRow: 4, Band), 3);
    }

    [Fact]
    public void RowAt_reads_back_every_row_it_placed()
    {
        for (var band = -1; band < Count; band++)
        {
            for (var i = 0; i < Count; i++)
            {
                var top = AlbumWallRows.RowTop(i, Row, Gap, band, Band);
                var height = AlbumWallRows.RowHeight(i, Row, band, Band);
                Assert.Equal(i, AlbumWallRows.RowAt(top, Count, Row, Gap, band, Band));
                Assert.Equal(i, AlbumWallRows.RowAt(top + height / 2, Count, Row, Gap, band, Band));
            }
        }
    }

    [Fact]
    public void RowAt_anywhere_inside_the_open_drawer_is_still_its_own_row()
    {
        Assert.Equal(0, AlbumWallRows.RowAt(0, Count, Row, Gap, bandRow: 0, Band));
        Assert.Equal(0, AlbumWallRows.RowAt(1500, Count, Row, Gap, bandRow: 0, Band));
        Assert.Equal(0, AlbumWallRows.RowAt(Row + Band, Count, Row, Gap, bandRow: 0, Band));
        Assert.Equal(1, AlbumWallRows.RowAt(Row + Band + Gap, Count, Row, Gap, bandRow: 0, Band));
    }

    [Fact]
    public void RowAt_clamps_to_the_wall_at_both_ends()
    {
        Assert.Equal(0, AlbumWallRows.RowAt(-500, Count, Row, Gap, bandRow: 0, Band));
        Assert.Equal(Count - 1, AlbumWallRows.RowAt(99999, Count, Row, Gap, bandRow: 0, Band));
    }

    [Fact]
    public void RowAt_an_empty_wall_names_no_row()
        => Assert.Equal(-1, AlbumWallRows.RowAt(0, 0, Row, Gap, bandRow: -1, 0));

    /// <summary>The reader is at 3046 on a wall whose real end is 4163.5. That offset is inside the
    /// wall, and it names a row that is really there.</summary>
    [Fact]
    public void The_offset_that_used_to_be_clamped_away_is_inside_the_wall()
    {
        var height = AlbumWallRows.WallHeight(Count, Row, Gap, bandRow: 0, Band);
        Assert.True(3046 < height);
        Assert.Equal(2, AlbumWallRows.RowAt(3046, Count, Row, Gap, bandRow: 0, Band));
    }

    [Fact]
    public void RowsIn_holds_every_row_the_viewport_touches_and_no_more()
    {
        // A 675pt viewport over rows 1..4, with the drawer open above it on row 0.
        var top = AlbumWallRows.RowTop(1, Row, Gap, bandRow: 0, Band);
        var (first, last) = AlbumWallRows.RowsIn(top, top + 675, Count, Row, Gap, bandRow: 0, Band);
        Assert.Equal(1, first);
        Assert.Equal(4, last);
    }

    [Fact]
    public void RowsIn_over_the_open_drawer_is_that_row_alone()
    {
        var (first, last) = AlbumWallRows.RowsIn(400, 1075, Count, Row, Gap, bandRow: 0, Band);
        Assert.Equal(0, first);
        Assert.Equal(0, last);
    }

    [Fact]
    public void RowsIn_of_an_empty_wall_or_an_upside_down_band_holds_nothing()
    {
        Assert.Equal((-1, -1), AlbumWallRows.RowsIn(0, 100, 0, Row, Gap, -1, 0));
        Assert.Equal((-1, -1), AlbumWallRows.RowsIn(100, 0, Count, Row, Gap, -1, 0));
    }

    /// <summary>
    /// A second real wall: six rows of 246.5pt, 14pt apart, with a 2499pt drawer open on row 0, so the
    /// wall is 4048pt long. The reader is at 3373, with the open record and its drawer above the top of
    /// the screen, and clicks another record. Detaching the old drawer makes the wall 1549 long, with its
    /// end at 874: the scroller cannot stay at 3373 any more and is clamped, so the content under the
    /// click moves.
    /// </summary>
    private const double WideRow = 246.5;
    private const double WideBand = 2499;
    private const int WideCount = 6;

    [Fact]
    public void OffsetHolding_keeps_the_top_row_where_it_was_when_the_drawer_is_detached()
    {
        const double offset = 3373;
        var anchor = AlbumWallRows.RowAt(offset, WideCount, WideRow, Gap, bandRow: 0, WideBand);
        Assert.Equal(3, anchor);

        var was = AlbumWallRows.RowTop(anchor, WideRow, Gap, bandRow: 0, WideBand);
        var now = AlbumWallRows.RowTop(anchor, WideRow, Gap, bandRow: -1, 0);
        var held = AlbumWallRows.OffsetHolding(offset, was, now);

        // At the new end rather than clamped to it, and (the purpose of this) the row the reader had
        // at the top of the viewport is still exactly as far up the screen as before.
        Assert.Equal(874, held, 3);
        Assert.Equal(was - offset, now - held, 3);
    }

    [Fact]
    public void OffsetHolding_leaves_a_reader_above_the_drawer_alone()
    {
        // The open row is BELOW the reader: nothing above them moved, so neither do they.
        var was = AlbumWallRows.RowTop(1, WideRow, Gap, bandRow: 4, WideBand);
        var now = AlbumWallRows.RowTop(1, WideRow, Gap, bandRow: -1, 0);
        Assert.Equal(300, AlbumWallRows.OffsetHolding(300, was, now), 3);
    }

    [Fact]
    public void OffsetHolding_never_walks_above_the_top_of_the_wall()
    {
        // A drawer longer than everything the reader had above them: the top of the wall is as far as it
        // goes, not a negative offset the scroller would refuse anyway.
        var was = AlbumWallRows.RowTop(3, WideRow, Gap, bandRow: 0, WideBand);
        var now = AlbumWallRows.RowTop(3, WideRow, Gap, bandRow: -1, 0);
        Assert.Equal(0, AlbumWallRows.OffsetHolding(100, was, now), 3);
    }
}

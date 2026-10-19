using RPM.Core.Library;

namespace RPM.Core.Tests;

public class AlbumGridLayoutTests
{
    [Theory]
    [InlineData(800, 160, 5)]   // exact fit
    [InlineData(830, 160, 5)]   // 5.19 → nearest is 5, remainder stretches the cards
    [InlineData(880, 160, 6)]   // 5.5 → rounds up (away from zero), 6 slightly smaller cards
    [InlineData(159, 160, 1)]   // narrower than one target still shows one
    [InlineData(0, 160, 1)]     // not yet measured
    [InlineData(-5, 160, 1)]    // bogus width
    public void ColumnsForTarget_rounds_to_nearest_and_never_below_one(double width, double target, int expected)
        => Assert.Equal(expected, AlbumGridLayout.ColumnsForTarget(width, target));

    [Fact]
    public void ColumnsForTarget_zero_target_falls_back_to_min()
        => Assert.Equal(1, AlbumGridLayout.ColumnsForTarget(800, 0));

    /// <summary>
    /// The count is raised until the row's share fits under the stride ceiling, so the stretch never has
    /// to clamp and the row always fills the panel. Without this the leftover space is an empty strip
    /// down the right-hand side, worst at the top of the size slider where the target IS the ceiling.
    /// </summary>
    [Theory]
    [InlineData(1656, 276, 276, 6)]   // exactly six at the ceiling: the rounding is already right
    [InlineData(1700, 276, 276, 7)]   // 6.16 rounds to 6, whose share is 283: over. Seven instead
    [InlineData(1790, 276, 276, 7)]   // 6.49 rounds to 6, share 298. Seven, and the cards come down
    [InlineData(1300, 166, 276, 8)]   // an ordinary size: the ceiling is nowhere near, nothing changes
    [InlineData(2000, 166, 276, 12)]  // 12.05 → 12, share 166. Untouched
    public void ColumnsForTarget_never_asks_for_a_card_wider_than_the_ceiling(
        double width, double target, double maxStride, int expected)
    {
        var columns = AlbumGridLayout.ColumnsForTarget(width, target, maxStride: maxStride);
        Assert.Equal(expected, columns);

        // This is the purpose of the rule: the stretch is now under the ceiling, so it does not clamp
        // and the row fills the panel to within the floor's own rounding.
        var stride = AlbumGridLayout.StrideFor(width, columns, maxStride);
        Assert.True(stride <= maxStride);
        Assert.True(width - columns * stride < columns);
    }

    [Theory]
    [InlineData(800, 5, 400, 160)]   // 800 / 5 = 160 exact
    [InlineData(830, 5, 400, 166)]   // 830 / 5 = 166 exact
    [InlineData(833, 5, 400, 166)]   // 833 / 5 = 166.6 → floored to 166 so the row never wraps
    [InlineData(1000, 1, 280, 280)]  // one card in a wide panel is capped at the max stride
    [InlineData(0, 5, 280, 280)]     // unmeasured → the cap (widest allowed)
    public void StrideFor_fills_the_row_floored_and_capped(double width, int columns, double max, double expected)
        => Assert.Equal(expected, AlbumGridLayout.StrideFor(width, columns, max));

    [Fact]
    public void StrideFor_bad_column_count_is_treated_as_one()
        => Assert.Equal(600, AlbumGridLayout.StrideFor(600, 0, 800));

    [Theory]
    [InlineData(40, 12, 900, false)]   // scrolled into the middle, showing row 12: correct
    [InlineData(40, 0, 0, false)]      // at the top, showing row 0: correct
    [InlineData(40, 0, 0.4, false)]    // a fraction of a pixel of residue is still the top
    [InlineData(40, -1, 900, true)]    // rows, a viewport, and not one of them realised: blank wall
    [InlineData(40, 7, 0, true)]       // scrolled to the top and row 7 is the first one showing
    [InlineData(40, 7, 0.4, true)]     // ditto, with ChangeView residue
    public void AnchorLost_reads_the_two_invariants(int rows, int first, double offset, bool expected)
        => Assert.Equal(expected, AlbumGridLayout.AnchorLost(rows, first, offset, everRealized: true));

    [Theory]
    [InlineData(0, false)]        // row 0 sitting exactly on the top of the viewport: correct
    [InlineData(-1.5, false)]     // a pixel and a half of rounding is not a lost anchor
    [InlineData(-140, true)]      // row 0 laid out above the viewport: the wall the user cannot scroll up to
    [InlineData(96, true)]        // row 0 pushed below the top with the scrollbar already at zero
    public void AnchorLost_at_the_top_row_zero_must_be_at_the_top(double firstRowTop, bool expected)
        => Assert.Equal(expected, AlbumGridLayout.AnchorLost(
            40, firstRealizedRow: 0, verticalOffset: 0, everRealized: true, firstRowTop: firstRowTop));

    [Fact]
    public void AnchorLost_row_zero_off_screen_below_the_top_is_the_users_own_scrolling()
        => Assert.False(AlbumGridLayout.AnchorLost(
            40, firstRealizedRow: 0, verticalOffset: 900, everRealized: true, firstRowTop: -900));

    [Fact]
    public void AnchorLost_a_shelf_that_shrank_under_a_parked_scroller_has_lost_its_place()
        // The album list's own case: a search narrows ten thousand records to three, and all three are
        // laid out far above the band that a scroll position deep in the old list asks to be filled.
        => Assert.True(AlbumGridLayout.AnchorLost(3, firstRealizedRow: -1, verticalOffset: 15000,
                                                  everRealized: true));

    [Fact]
    public void AnchorLost_a_wall_that_has_never_been_laid_out_is_not_broken()
        => Assert.False(AlbumGridLayout.AnchorLost(40, -1, 0, everRealized: false));

    [Fact]
    public void AnchorLost_an_empty_wall_has_no_place_to_lose()
        => Assert.False(AlbumGridLayout.AnchorLost(0, -1, 0, everRealized: true));

    [Theory]
    // 800px of viewport, a 220px row: the middle of the row against the middle of the screen.
    [InlineData(0, 220, 800, false)]      // first row on screen: the drawer has the whole wall below it
    [InlineData(280, 220, 800, false)]    // middle at 390, just above the line
    [InlineData(400, 220, 800, true)]     // middle at 510, below it, so scroll it up first
    [InlineData(690, 220, 800, true)]     // the bottom row, with no room at all to open into
    [InlineData(-100, 220, 800, false)]   // scrolled partly off the top, so it opens where it is
    public void LiftsBeforeOpening_only_the_bottom_half_moves_the_wall(
        double rowTop, double rowHeight, double viewport, bool expected)
        => Assert.Equal(expected, AlbumGridLayout.LiftsBeforeOpening(rowTop, rowHeight, viewport));

    [Theory]
    [InlineData(400, 220, 0)]              // no viewport measured yet
    [InlineData(400, 0, 800)]              // no row measured yet
    [InlineData(double.NaN, 220, 800)]     // nothing to place it by
    public void LiftsBeforeOpening_an_unmeasured_wall_is_left_alone(
        double rowTop, double rowHeight, double viewport)
        => Assert.False(AlbumGridLayout.LiftsBeforeOpening(rowTop, rowHeight, viewport));

    [Theory]
    // The drawer's own template margin is 0,2,0,8: 10 pixels, top and bottom together.
    [InlineData(310, 2, 8, 300)]      // a measure said 310; the box inside that margin is 300
    [InlineData(300, 0, 0, 300)]      // no margin, so the two are the same number
    [InlineData(6, 2, 8, 0)]          // a measure smaller than its own margin has no box left
    [InlineData(double.NaN, 2, 8, 0)] // never measured: the caller reads 0 as "nothing to unfold"
    public void DrawerLength_is_the_box_inside_the_margin_not_the_room_it_asked_for(
        double desired, double top, double bottom, double expected)
        => Assert.Equal(expected, AlbumGridLayout.DrawerLength(desired, top, bottom));

    [Fact]
    public void DrawerLength_a_neighbour_the_same_length_asks_for_no_move()
    {
        // Switching drawers tweens ActualHeight (which excludes the margin) to this. If it were given
        // the raw measure, the two would differ by the margin every time, and the drawer would grow
        // 10 pixels and shrink back on the release to Auto, even on a switch that changes nothing.
        const double onScreen = 284;   // what the open drawer's ActualHeight reads
        Assert.Equal(onScreen, AlbumGridLayout.DrawerLength(onScreen + 10, 2, 8));
    }

    // A home row: sleeves of 160 with 8 of padding either side and a gap of 6 between cards, so a
    // card is 176 wide and the next one starts 182 along. The middle of card i is 182i + 88.
    [Theory]
    [InlineData(0, 0, 88)]        // the first card, row at rest
    [InlineData(2, 0, 452)]       // the third, still where it was laid out
    [InlineData(2, 364, 88)]      // …and the same card with the row scrolled two cards along
    [InlineData(5, 500, 498)]     // a card the keys brought on screen
    public void CardMiddleOnScreen_is_the_index_read_against_the_offset(
        int index, double offset, double expected)
        => Assert.Equal(expected, AlbumGridLayout.CardMiddleOnScreen(
            index, stride: 182, sleeve: 160, padding: 8,
            offset: offset, viewport: 900, clearance: 10));

    // A 400-point row, which holds two cards and a bit.
    [Theory]
    [InlineData(0, 364)]      // the first card, scrolled off the left end
    [InlineData(4, 0)]        // the fifth, out past the right end: its middle is at 816
    [InlineData(8, 0)]        // and one the row has never been scrolled to at all
    public void CardMiddleOnScreen_a_card_off_either_end_points_at_nothing(int index, double offset)
        => Assert.Null(AlbumGridLayout.CardMiddleOnScreen(
            index, stride: 182, sleeve: 160, padding: 8,
            offset: offset, viewport: 400, clearance: 10));

    [Fact]
    public void CardMiddleOnScreen_a_middle_within_half_a_triangle_of_the_edge_points_at_nothing()
    {
        // The mark is 20 wide and is drawn from its middle, so a middle 6 points in would be a triangle
        // cut in half by the row's left edge. There is nothing to aim at from there.
        Assert.Null(AlbumGridLayout.CardMiddleOnScreen(
            0, stride: 182, sleeve: 160, padding: 8, offset: 82, viewport: 900, clearance: 10));

        // Two points further along and it is whole, so it is drawn.
        Assert.Equal(10, AlbumGridLayout.CardMiddleOnScreen(
            0, stride: 182, sleeve: 160, padding: 8, offset: 78, viewport: 900, clearance: 10));
    }

    [Theory]
    [InlineData(0, 182, 160, 0)]        // no row measured yet
    [InlineData(-1, 182, 160, 900)]     // no such card
    [InlineData(0, 0, 160, 900)]        // cards not sized yet
    [InlineData(0, 182, 0, 900)]        // no sleeve measured yet
    public void CardMiddleOnScreen_an_unmeasured_row_points_at_nothing(
        int index, double stride, double sleeve, double viewport)
        => Assert.Null(AlbumGridLayout.CardMiddleOnScreen(
            index, stride, sleeve, padding: 8, offset: 0, viewport: viewport, clearance: 10));

    [Fact]
    public void LiftsBeforeOpening_a_row_exactly_on_the_middle_opens_where_it_is()
        // "Above or on the centre: just expand." Exactly on the line counts as on it.
        => Assert.False(AlbumGridLayout.LiftsBeforeOpening(rowTop: 290, rowHeight: 220, viewportHeight: 800));

    [Fact]
    public void ShelfReplaced_a_search_that_narrows_the_shelf_replaces_it()
        => Assert.True(AlbumGridLayout.ShelfReplaced(
            ["ABBA/Gold", "Celine Dion/A New Day", "Various/BOOOM!"],
            ["Various/BOOOM!"]));

    [Fact]
    public void ShelfReplaced_the_same_records_in_the_same_order_are_the_same_shelf()
        // A cover arriving, a favourite marked, an album retagged: the blocks stay, and the reader
        // keeps their scroll position.
        => Assert.False(AlbumGridLayout.ShelfReplaced(
            ["ABBA/Gold", "Oasis/Greatest Hits"],
            ["abba/gold", "Oasis/Greatest Hits"]));

    [Fact]
    public void ShelfReplaced_the_same_records_regrouped_are_a_different_shelf()
        => Assert.True(AlbumGridLayout.ShelfReplaced(
            ["ABBA/Gold", "Oasis/Greatest Hits"],
            ["Oasis/Greatest Hits", "ABBA/Gold"]));

    [Fact]
    public void ShelfReplaced_the_first_feed_of_a_list_is_a_replacement()
        => Assert.True(AlbumGridLayout.ShelfReplaced([], ["Various/BOOOM!"]));

    [Fact]
    public void ShelfReplaced_an_empty_shelf_fed_nothing_is_no_change()
        => Assert.False(AlbumGridLayout.ShelfReplaced([], []));

    [Fact]
    public void Chunk_rows_are_column_wide_with_a_short_last_row()
    {
        var rows = AlbumGridLayout.Chunk(Enumerable.Range(0, 7).ToList(), 3);

        Assert.Equal(3, rows.Count);
        Assert.Equal([0, 1, 2], rows[0]);
        Assert.Equal([3, 4, 5], rows[1]);
        Assert.Equal([6], rows[2]);           // remainder, not padded
    }

    [Fact]
    public void Chunk_never_drops_items_on_a_bad_column_count()
    {
        var rows = AlbumGridLayout.Chunk(Enumerable.Range(0, 4).ToList(), 0);

        Assert.Equal(4, rows.Count);          // treated as one column
        Assert.Equal(4, rows.Sum(r => r.Count));
    }

    [Fact]
    public void Chunk_of_nothing_is_no_rows()
        => Assert.Empty(AlbumGridLayout.Chunk(Array.Empty<int>(), 4));

    [Fact]
    public void SplitTwoColumns_fills_left_down_first_and_left_takes_the_odd_extra()
    {
        var (left, right) = AlbumGridLayout.SplitTwoColumns(Enumerable.Range(1, 11).ToList());

        Assert.Equal([1, 2, 3, 4, 5, 6], left);   // 1-6 down the left
        Assert.Equal([7, 8, 9, 10, 11], right);    // 7-11 down the right
    }

    [Fact]
    public void SplitTwoColumns_single_track_is_all_left()
    {
        var (left, right) = AlbumGridLayout.SplitTwoColumns(new[] { 42 });

        Assert.Equal([42], left);
        Assert.Empty(right);
    }

    // A disc divider is written "H" here, a track "t1"/"t2"…; the split is asked to break on "H".
    private static string[] TwoDiscs(int first, int second) =>
        [.. new[] { "H" }.Concat(Enumerable.Range(1, first).Select(i => $"a{i}"))
                         .Concat(["H"]).Concat(Enumerable.Range(1, second).Select(i => $"b{i}"))];

    [Fact]
    public void SplitTwoColumns_breaks_at_the_disc_divider_so_each_column_is_one_disc()
    {
        var (left, right) = AlbumGridLayout.SplitTwoColumns(TwoDiscs(5, 5), i => i == "H");

        Assert.Equal(["H", "a1", "a2", "a3", "a4", "a5"], left);
        Assert.Equal(["H", "b1", "b2", "b3", "b4", "b5"], right);
    }

    [Fact]
    public void SplitTwoColumns_keeps_the_balanced_break_when_the_discs_are_lopsided()
    {
        // Disc 2 has two tracks: breaking there would leave the right column nearly empty, so the
        // columns split evenly instead. The discs still read in order, one after the other.
        var (left, right) = AlbumGridLayout.SplitTwoColumns(TwoDiscs(10, 2), i => i == "H");

        Assert.Equal(7, left.Count);
        Assert.Equal(7, right.Count);
        Assert.Equal("a6", left[^1]);
    }

    [Fact]
    public void SplitTwoColumns_never_leaves_a_divider_as_the_left_columns_last_row()
    {
        // Eight lines, so the balanced break is 4, and line 3 is disc 2's divider, too near the top
        // for the boundary rule to apply. The break still moves by one, so the divider starts the right
        // column instead of sitting alone at the bottom of the left one.
        var (left, right) = AlbumGridLayout.SplitTwoColumns(TwoDiscs(2, 4), i => i == "H");

        Assert.Equal(["H", "a1", "a2"], left);
        Assert.Equal(["H", "b1", "b2", "b3", "b4"], right);
    }

    [Fact]
    public void SplitTwoColumns_without_a_break_rule_is_the_plain_balanced_split()
    {
        var (left, right) = AlbumGridLayout.SplitTwoColumns(TwoDiscs(5, 5));

        Assert.Equal(6, left.Count);
        Assert.Equal(6, right.Count);
    }

    // ── Fit: the pinch's sizing, which is the slider's with a ceiling on the column count ──

    [Fact]
    public void Fit_fills_the_row_edge_to_edge()
    {
        // A phone's wall: 362 points of usable width, asking for cards about 170 across.
        var (columns, stride) = AlbumGridLayout.Fit(362, 170, maxColumns: 4);

        Assert.Equal(2, columns);
        Assert.Equal(181, stride);
        Assert.True(columns * stride <= 362, "the last card must not be pushed off the edge");
    }

    [Fact]
    public void Fit_rounds_to_the_nearest_column_rather_than_down()
    {
        // 2.6 columns' worth: three slightly narrower cards, not two wide ones with a gutter.
        var (columns, _) = AlbumGridLayout.Fit(362, 140, maxColumns: 4);

        Assert.Equal(3, columns);
    }

    [Fact]
    public void Fit_will_not_go_past_the_column_ceiling()
    {
        // Pinched all the way in. Without the clamp this is nine columns of tiny cards.
        var (columns, stride) = AlbumGridLayout.Fit(362, 40, maxColumns: 4);

        Assert.Equal(4, columns);
        Assert.Equal(90, stride);
    }

    [Fact]
    public void Fit_at_one_column_gives_the_card_the_whole_width()
    {
        var (columns, stride) = AlbumGridLayout.Fit(362, 400, maxColumns: 4);

        Assert.Equal(1, columns);
        Assert.Equal(362, stride);
    }

    [Fact]
    public void Fit_never_answers_fewer_columns_for_a_smaller_target()
    {
        var previous = 0;

        for (var target = 400; target >= 40; target -= 10)
        {
            var (columns, _) = AlbumGridLayout.Fit(362, target, maxColumns: 4);

            Assert.True(columns >= previous, $"target {target} went backwards: {columns} after {previous}");
            previous = columns;
        }
    }

    [Fact]
    public void Fit_answers_one_column_before_the_panel_has_been_measured()
    {
        var (columns, stride) = AlbumGridLayout.Fit(0, 170, maxColumns: 4);

        Assert.Equal(1, columns);
        Assert.Equal(1, stride);
    }
}

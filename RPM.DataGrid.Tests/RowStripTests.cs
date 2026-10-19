using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

public class RowStripTests
{
    private static readonly RowStrip Rows = new(24);

    [Fact]
    public void A_million_rows_have_a_height_without_being_measured()
    {
        Assert.Equal(24_000_000, Rows.TotalHeight(1_000_000));
        Assert.Equal(0, Rows.TotalHeight(0));
        Assert.Equal(0, Rows.TotalHeight(-5));
    }

    [Fact]
    public void A_row_height_of_nothing_is_refused()
    {
        Assert.Equal(1, new RowStrip(0).RowHeight);
        Assert.Equal(1, new RowStrip(-10).RowHeight);
    }

    [Fact]
    public void The_range_covers_what_the_viewport_touches()
    {
        Assert.Equal((0, 10), Rows.RangeAt(0, 240, 1000, overscan: 0));
        Assert.Equal((0, 11), Rows.RangeAt(0, 241, 1000, overscan: 0));
    }

    [Fact]
    public void A_partly_scrolled_row_is_still_in_range()
    {
        // Scrolled 10px into row 0: rows 0..10 are all at least partly on screen.
        Assert.Equal((0, 11), Rows.RangeAt(10, 240, 1000, overscan: 0));
    }

    [Fact]
    public void The_range_is_clamped_to_the_list()
    {
        Assert.Equal((0, 5), Rows.RangeAt(0, 1000, 5, overscan: 0));
        Assert.Equal((0, 0), Rows.RangeAt(0, 240, 0));
        Assert.Equal((0, 5), Rows.RangeAt(0, 240, 5, overscan: 10));
    }

    [Fact]
    public void Overscan_widens_the_range_both_ways()
    {
        var (first, end) = Rows.RangeAt(24 * 100, 240, 1000, overscan: 2);

        Assert.Equal(98, first);
        Assert.Equal(112, end);
    }

    /// <summary>Before the first layout the viewport has no height. No rows is the answer, and the
    /// next layout asks again with a real one.</summary>
    [Fact]
    public void An_unmeasured_viewport_shows_nothing()
    {
        Assert.Equal((0, 0), Rows.RangeAt(0, 0, 1000));
        Assert.Equal((0, 0), Rows.RangeAt(0, double.NaN, 1000));
    }

    [Fact]
    public void A_row_far_down_is_reached_by_arithmetic()
    {
        var (first, end) = Rows.RangeAt(24 * 900_000, 240, 1_000_000, overscan: 0);

        Assert.Equal(900_000, first);
        Assert.Equal(900_010, end);
    }

    [Fact]
    public void The_top_of_a_row_is_where_it_is_drawn()
    {
        Assert.Equal(0, Rows.TopOf(0));
        Assert.Equal(240, Rows.TopOf(10));
    }

    [Fact]
    public void A_point_names_the_row_under_it()
    {
        Assert.Equal(0, Rows.RowAt(0, 100));
        Assert.Equal(0, Rows.RowAt(23.9, 100));
        Assert.Equal(1, Rows.RowAt(24, 100));
        Assert.Equal(-1, Rows.RowAt(-1, 100));
        Assert.Equal(-1, Rows.RowAt(24 * 100, 100));
    }

    [Fact]
    public void A_row_already_on_screen_does_not_move_the_list()
    {
        Assert.Equal(100, Rows.ScrollToShow(5, 100, 240, 1000));
    }

    [Fact]
    public void A_row_above_the_viewport_is_brought_to_the_top()
    {
        Assert.Equal(24 * 3, Rows.ScrollToShow(3, 24 * 10, 240, 1000));
    }

    [Fact]
    public void A_row_below_the_viewport_is_brought_to_the_bottom()
    {
        // Row 20's bottom is 504; a 240-tall viewport showing it ends there, so it starts at 264.
        Assert.Equal(264, Rows.ScrollToShow(20, 0, 240, 1000));
    }

    [Fact]
    public void Showing_a_row_never_scrolls_past_the_end_of_the_list()
    {
        var at = Rows.ScrollToShow(99, 0, 240, 100);

        Assert.Equal(24 * 100 - 240, at);
        Assert.True(at >= 0);
    }

    [Fact]
    public void Showing_a_row_in_a_list_shorter_than_the_viewport_stays_at_the_top()
    {
        Assert.Equal(0, Rows.ScrollToShow(2, 0, 1000, 5));
    }
}

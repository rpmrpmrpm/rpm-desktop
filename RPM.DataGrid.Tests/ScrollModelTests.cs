using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

public class ScrollModelTests
{
    private static ScrollModel Sized(
        double extentW = 2000, double extentH = 24000,
        double viewW = 800, double viewH = 480)
    {
        var s = new ScrollModel();
        s.Measure(extentW, extentH, viewW, viewH);
        return s;
    }

    [Fact]
    public void It_starts_at_the_top_left()
    {
        var s = Sized();

        Assert.Equal(0, s.X);
        Assert.Equal(0, s.Y);
    }

    [Fact]
    public void The_limits_are_the_content_less_the_viewport()
    {
        var s = Sized();

        Assert.Equal(1200, s.MaxX);
        Assert.Equal(23520, s.MaxY);
    }

    [Fact]
    public void Content_that_fits_cannot_be_scrolled()
    {
        var s = Sized(extentW: 100, extentH: 100);

        Assert.Equal(0, s.MaxX);
        Assert.Equal(0, s.MaxY);
        Assert.False(s.MoveTo(50, 50));
    }

    [Fact]
    public void Moving_is_clamped_at_both_ends()
    {
        var s = Sized();

        s.MoveTo(-100, -100);
        Assert.Equal(0, s.X);
        Assert.Equal(0, s.Y);

        s.MoveTo(99999, 99999);
        Assert.Equal(s.MaxX, s.X);
        Assert.Equal(s.MaxY, s.Y);
    }

    [Fact]
    public void A_move_that_changes_nothing_reports_nothing()
    {
        var s = Sized();

        Assert.True(s.MoveTo(100, 100));
        Assert.False(s.MoveTo(100, 100));
        Assert.False(s.MoveTo(100.001, 100));
    }

    [Fact]
    public void Scrolled_is_raised_once_per_move()
    {
        var s = Sized();
        var raised = 0;
        s.Scrolled += () => raised++;

        s.MoveTo(10, 10);
        s.MoveTo(10, 10);
        s.By(0, 10);

        Assert.Equal(2, raised);
    }

    /// <summary>
    /// A window getting wider must not leave the strip scrolled past its own end, which is what a
    /// stored offset plus a new viewport does if nobody re-clamps.
    /// </summary>
    [Fact]
    public void Measuring_brings_the_offsets_back_inside()
    {
        var s = Sized();
        s.MoveTo(s.MaxX, s.MaxY);

        s.Measure(2000, 24000, 1900, 23900);

        Assert.Equal(100, s.X);
        Assert.Equal(100, s.Y);
    }

    [Fact]
    public void A_measurement_with_nothing_in_it_is_survived()
    {
        var s = new ScrollModel();

        s.Measure(double.NaN, double.NaN, double.NaN, double.NaN);

        Assert.Equal(0, s.X);
        Assert.Equal(0, s.Y);
        Assert.Equal(0, s.MaxY);
    }

    [Fact]
    public void One_detent_of_the_wheel_moves_three_rows()
    {
        var s = Sized();

        s.Wheel(-ScrollModel.DetentUnits, horizontal: false, rowHeight: 24);

        Assert.Equal(72, s.Y);
    }

    [Fact]
    public void The_wheel_scrolls_up_as_well()
    {
        var s = Sized();
        s.MoveTo(0, 500);

        s.Wheel(ScrollModel.DetentUnits, horizontal: false, rowHeight: 24);

        Assert.Equal(428, s.Y);
    }

    /// <summary>
    /// A trackpad reports whatever it measured rather than whole detents. It has to move
    /// proportionally, or a flick becomes a series of three-row jumps.
    /// </summary>
    [Fact]
    public void A_fraction_of_a_detent_moves_a_fraction_of_the_distance()
    {
        var s = Sized();

        s.Wheel(-ScrollModel.DetentUnits / 4, horizontal: false, rowHeight: 24);

        Assert.Equal(18, s.Y);
    }

    [Fact]
    public void A_hundred_small_scrolls_add_up_to_the_same_as_one_big_one()
    {
        var a = Sized();
        var b = Sized();

        for (var i = 0; i < 100; i++)
            a.Wheel(-ScrollModel.DetentUnits / 100, horizontal: false, rowHeight: 24);

        b.Wheel(-ScrollModel.DetentUnits, horizontal: false, rowHeight: 24);

        Assert.Equal(b.Y, a.Y, 6);
    }

    [Fact]
    public void A_sideways_wheel_moves_the_strip()
    {
        var s = Sized();

        s.Wheel(-ScrollModel.DetentUnits, horizontal: true, rowHeight: 24);

        Assert.Equal(48, s.X);
        Assert.Equal(0, s.Y);
    }

    [Fact]
    public void A_wheel_event_of_nothing_does_nothing()
    {
        var s = Sized();

        Assert.False(s.Wheel(0, horizontal: false, rowHeight: 24));
        Assert.False(s.Wheel(double.NaN, horizontal: false, rowHeight: 24));
    }

    [Fact]
    public void Showing_a_row_moves_as_little_as_it_can()
    {
        var s = Sized(extentH: 24 * 1000);
        var rows = new RowStrip(24);

        s.ShowRow(500, 1000, rows);
        Assert.Equal(24 * 500 + 24 - 480, s.Y);

        var was = s.Y;
        s.ShowRow(501, 1000, rows);
        Assert.Equal(was + 24, s.Y);
    }

    [Fact]
    public void Showing_a_row_already_in_view_moves_nothing()
    {
        var s = Sized();
        var rows = new RowStrip(24);

        s.MoveTo(0, 240);
        Assert.False(s.ShowRow(12, 1000, rows));
    }

    [Fact]
    public void Showing_a_column_brings_it_in_from_either_side()
    {
        var s = Sized();

        s.MoveTo(500, 0);

        s.ShowColumn(100, 50);                 // off to the left
        Assert.Equal(100, s.X);

        // 700..900 against a viewport of 100..900: it already ends exactly at the edge.
        Assert.False(s.ShowColumn(700, 200));
        Assert.Equal(100, s.X);

        s.ShowColumn(1000, 100);               // 1100 is past 900, so the viewport ends at 1100
        Assert.Equal(300, s.X);
    }

    [Fact]
    public void A_pointer_inside_the_viewport_does_not_auto_scroll()
    {
        var s = Sized();

        Assert.Equal(0, s.AutoScrollRate(0));
        Assert.Equal(0, s.AutoScrollRate(240));
        Assert.Equal(0, s.AutoScrollRate(480));
    }

    [Fact]
    public void A_pointer_below_the_viewport_scrolls_down_faster_the_further_out_it_is()
    {
        var s = Sized();

        var near = s.AutoScrollRate(480 + 20);
        var far = s.AutoScrollRate(480 + 60);

        Assert.True(near > 0);
        Assert.True(far > near);
    }

    [Fact]
    public void A_pointer_above_the_viewport_scrolls_up()
    {
        var s = Sized();

        Assert.True(s.AutoScrollRate(-20) < 0);
    }

    [Fact]
    public void The_auto_scroll_rate_stops_climbing_at_its_maximum()
    {
        var s = Sized();

        Assert.Equal(1200, s.AutoScrollRate(480 + 80));
        Assert.Equal(1200, s.AutoScrollRate(480 + 8000));
    }

    [Fact]
    public void An_auto_scroll_tick_moves_by_the_rate_over_the_time()
    {
        var s = Sized();

        // Fully outside: 1200px a second for 100ms is 120px.
        s.AutoScrollTick(480 + 100, 100);

        Assert.Equal(120, s.Y, 6);
    }

    [Fact]
    public void An_auto_scroll_tick_inside_the_viewport_does_nothing()
    {
        var s = Sized();

        Assert.False(s.AutoScrollTick(240, 100));
        Assert.Equal(0, s.Y);
    }
}

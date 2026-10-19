using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

public class GridColumnsTests
{
    /// <summary>Text columns of the given widths, named "c0", "c1" and so on.</summary>
    private static GridColumns Strip(params double[] widths)
    {
        var columns = new List<GridColumn>();
        for (var i = 0; i < widths.Length; i++)
        {
            var name = $"c{i}";
            columns.Add(GridColumn.Text(name, widths[i], _ => name));
        }

        return new GridColumns(columns);
    }

    [Fact]
    public void Two_columns_with_the_same_id_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new GridColumns([
            GridColumn.Text("title", 100, _ => null),
            GridColumn.Text("title", 100, _ => null),
        ]));

        Assert.Contains("title", ex.Message);
    }

    [Fact]
    public void The_total_is_the_widths_of_the_visible_columns()
    {
        var strip = Strip(100, 50, 25);
        Assert.Equal(175, strip.TotalWidth);

        strip.Show(strip.ById("c1")!, false);
        Assert.Equal(125, strip.TotalWidth);
        Assert.Equal(2, strip.Visible.Count);
    }

    [Fact]
    public void Left_edges_run_from_zero()
    {
        var strip = Strip(100, 50, 25);

        Assert.Equal(0, strip.LeftOf(0));
        Assert.Equal(100, strip.LeftOf(1));
        Assert.Equal(150, strip.LeftOf(2));
    }

    /// <summary>A width nobody set must not poison every offset after it, which is what NaN in a
    /// running sum does.</summary>
    [Fact]
    public void An_unmeasured_width_counts_as_nothing()
    {
        var strip = Strip(100, double.NaN, 25);

        Assert.Equal(125, strip.TotalWidth);
        Assert.Equal(100, strip.LeftOf(2));
    }

    [Fact]
    public void A_hidden_column_is_not_in_the_range_or_the_offsets()
    {
        var strip = Strip(100, 50, 25);
        strip.Show(strip.ById("c0")!, false);

        Assert.Equal("c1", strip.Visible[0].Id);
        Assert.Equal(0, strip.LeftOf(0));
        Assert.Equal(50, strip.LeftOf(1));
    }

    [Fact]
    public void The_range_covers_what_the_viewport_touches()
    {
        var strip = Strip(100, 100, 100, 100, 100);

        // Viewport 0..250 touches columns 0, 1 and 2; overscan adds one on the far side.
        var (start, end) = strip.RangeAt(0, 250, overscan: 0);
        Assert.Equal(0, start);
        Assert.Equal(3, end);
    }

    [Fact]
    public void The_range_moves_with_the_scroll()
    {
        var strip = Strip(100, 100, 100, 100, 100);

        var (start, end) = strip.RangeAt(250, 150, overscan: 0);
        Assert.Equal(2, start);           // 250 is inside column 2
        Assert.Equal(4, end);             // 400 is the edge of column 4, which is not touched
    }

    [Fact]
    public void Overscan_widens_the_range_without_running_off_the_ends()
    {
        var strip = Strip(100, 100, 100, 100, 100);

        Assert.Equal((0, 4), strip.RangeAt(0, 250, overscan: 1));
        Assert.Equal((0, 5), strip.RangeAt(0, 250, overscan: 5));
    }

    /// <summary>
    /// Before the first layout nothing has a size. Answering "no columns visible" then draws an
    /// empty grid that never recovers, so the answer is "all of them".
    /// </summary>
    [Fact]
    public void An_unmeasured_viewport_puts_everything_in_range()
    {
        var strip = Strip(100, 100, 100);

        Assert.Equal((0, 3), strip.RangeAt(0, 0));
        Assert.Equal((0, 3), strip.RangeAt(0, double.NaN));
    }

    [Fact]
    public void Slots_carry_absolute_offsets()
    {
        var strip = Strip(100, 50, 25, 75);
        var slots = new List<ColumnSlot>();

        strip.SlotsInto((1, 3), slots);

        Assert.Equal(2, slots.Count);
        Assert.Equal(new ColumnSlot(1, 100, 50), slots[0]);
        Assert.Equal(new ColumnSlot(2, 150, 25), slots[1]);
    }

    [Fact]
    public void Hit_test_names_the_column_and_how_far_into_it()
    {
        var strip = Strip(100, 50, 25);

        Assert.Equal(0, strip.HitTest(0, out var a));
        Assert.Equal(0, a);

        Assert.Equal(1, strip.HitTest(120, out var b));
        Assert.Equal(20, b);

        Assert.Equal(2, strip.HitTest(174.9, out _));
    }

    [Fact]
    public void Hit_test_past_the_last_column_is_nothing()
    {
        var strip = Strip(100, 50);

        Assert.Equal(-1, strip.HitTest(150, out _));
        Assert.Equal(-1, strip.HitTest(1000, out _));
        Assert.Equal(-1, strip.HitTest(-1, out _));
    }

    [Fact]
    public void An_edge_is_found_within_the_tolerance()
    {
        var strip = Strip(100, 50, 25);

        Assert.Equal(0, strip.EdgeAt(98, 4));
        Assert.Equal(0, strip.EdgeAt(102, 4));
        Assert.Equal(1, strip.EdgeAt(150, 4));
        Assert.Equal(-1, strip.EdgeAt(120, 4));
    }

    [Fact]
    public void A_column_that_cannot_be_resized_has_no_edge()
    {
        var strip = new GridColumns([
            GridColumn.Tick("tick", 30, _ => TickState.Off),      // CanResize is false
            GridColumn.Text("name", 100, _ => null),
        ]);

        Assert.Equal(-1, strip.EdgeAt(30, 4));
        Assert.Equal(1, strip.EdgeAt(130, 4));
    }

    [Fact]
    public void Resizing_clamps_to_the_minimum_and_says_whether_it_moved()
    {
        var strip = Strip(100, 50);
        var first = strip.ById("c0")!;

        Assert.True(strip.Resize(first, 60));
        Assert.Equal(60, first.Width);

        Assert.True(strip.Resize(first, 1));
        Assert.Equal(first.MinWidth, first.Width);

        Assert.False(strip.Resize(first, first.MinWidth));
    }

    [Fact]
    public void Resizing_moves_every_offset_after_it()
    {
        var strip = Strip(100, 50, 25);
        strip.Resize(strip.ById("c0")!, 200);

        Assert.Equal(200, strip.LeftOf(1));
        Assert.Equal(250, strip.LeftOf(2));
        Assert.Equal(275, strip.TotalWidth);
    }

    [Fact]
    public void Changed_is_raised_once_per_change()
    {
        var strip = Strip(100, 50);
        var raised = 0;
        strip.Changed += () => raised++;

        strip.Resize(strip.ById("c0")!, 120);
        strip.Show(strip.ById("c1")!, false);
        strip.Show(strip.ById("c1")!, false);      // already hidden

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Moving_a_column_changes_the_display_order()
    {
        var strip = Strip(10, 20, 30);

        Assert.True(strip.Move(0, 2));
        Assert.Equal(["c1", "c2", "c0"], strip.All.Select(c => c.Id));
        Assert.Equal(20, strip.LeftOf(1));
    }

    [Fact]
    public void Moving_a_column_nowhere_changes_nothing()
    {
        var strip = Strip(10, 20);

        Assert.False(strip.Move(1, 1));
        Assert.False(strip.Move(9, 0));
    }

    /// <summary>
    /// A stored session order is applied by id. The point of the test is what happens to columns the
    /// stored order does not mention: a version that added a column must not lose it, and one that
    /// removed a column must not fail to load.
    /// </summary>
    [Fact]
    public void A_stored_order_is_applied_and_survives_a_changed_column_set()
    {
        var strip = Strip(10, 20, 30, 40);

        strip.Order(["c2", "c0", "gone"]);

        Assert.Equal(["c2", "c0", "c1", "c3"], strip.All.Select(c => c.Id));
    }

    [Fact]
    public void An_order_that_changes_nothing_raises_nothing()
    {
        var strip = Strip(10, 20);
        var raised = 0;
        strip.Changed += () => raised++;

        strip.Order(["c0", "c1"]);

        Assert.Equal(0, raised);
    }
}

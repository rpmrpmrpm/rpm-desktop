using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;

namespace RPM.DataGrid.Tests;

/// <summary>
/// What a point is on. Written down and checked, rather than left in a pointer handler where the
/// only way to find out is to point at it.
/// </summary>
public class GridHitTestTests
{
    private sealed record Node(int Depth, bool Expandable, TickState Tick, bool HasButton);

    private static GridFrame Frame(params object?[] rows) =>
        new()
        {
            Columns = new GridColumns(
            [
                GridColumn.Tick("tick", 30, r => ((Node)r!).Tick),
                GridColumn.GlyphButton("go", 26, r => new GlyphCell(((Node)r!).HasButton ? "" : null)),
                GridColumn.Tree("name", 200, r =>
                {
                    var n = (Node)r!;
                    return new TreeCell(n.Depth, n.Expandable ? TreeMarks.Expandable : TreeMarks.None, "Name");
                }, header: "Name"),
                GridColumn.Text("folder", 100, _ => "x", header: "Folder"),
            ]),
            Source = new ListSource(rows),
            Selection = new GridSelection(),
            Width = 400,
            Height = 240,
        };

    private static Node Plain => new(0, false, TickState.Off, true);

    /// <summary>The y of the middle of a row on screen, with the header above it.</summary>
    private static double RowY(GridFrame f, int row) => f.RowsTop + f.Rows.TopOf(row) + f.Rows.RowHeight / 2;

    [Fact]
    public void A_point_past_the_last_row_is_nothing()
    {
        var f = Frame(Plain, Plain);

        Assert.Equal(GridHit.Nothing, GridHitTest.At(f, 200, 200));
    }

    [Fact]
    public void A_point_past_the_last_column_is_the_row_and_no_column()
    {
        var f = Frame(Plain);
        var hit = GridHitTest.At(f, 380, RowY(f, 0));

        Assert.Equal(GridPart.Row, hit.Part);
        Assert.Equal(0, hit.Row);
        Assert.Equal(-1, hit.Column);
    }

    [Fact]
    public void A_point_on_a_text_cell_is_its_row()
    {
        var f = Frame(Plain, Plain, Plain);
        var hit = GridHitTest.At(f, 300, RowY(f, 1));

        Assert.Equal(GridPart.Row, hit.Part);
        Assert.Equal(1, hit.Row);
        Assert.Equal(3, hit.Column);
    }

    [Fact]
    public void A_point_in_the_middle_of_a_tick_column_is_the_tick()
    {
        var f = Frame(Plain);
        var hit = GridHitTest.At(f, 15, RowY(f, 0));

        Assert.Equal(GridPart.Tick, hit.Part);
        Assert.Equal(0, hit.Column);
    }

    /// <summary>
    /// The corners of a tick column are the row, not the tick. A thirty-pixel column holding a
    /// fourteen-pixel box has room either side, and a click there should select the row the same as
    /// a click anywhere else on it.
    /// </summary>
    [Fact]
    public void The_edges_of_a_tick_column_are_the_row()
    {
        var f = Frame(Plain);

        Assert.Equal(GridPart.Row, GridHitTest.At(f, 1, RowY(f, 0)).Part);
        Assert.Equal(GridPart.Row, GridHitTest.At(f, 29, RowY(f, 0)).Part);
    }

    /// <summary>A row that cannot be ticked has no tick to press, so its whole column is the row.</summary>
    [Fact]
    public void A_row_with_no_tick_has_no_tick_to_hit()
    {
        var f = Frame(new Node(0, false, TickState.None, true));

        Assert.Equal(GridPart.Row, GridHitTest.At(f, 15, RowY(f, 0)).Part);
    }

    [Fact]
    public void A_point_on_a_glyph_button_is_the_button()
    {
        var f = Frame(Plain);
        var hit = GridHitTest.At(f, 43, RowY(f, 0));

        Assert.Equal(GridPart.Button, hit.Part);
        Assert.Equal(1, hit.Column);
    }

    [Fact]
    public void A_row_whose_button_is_not_there_is_just_the_row()
    {
        var f = Frame(new Node(0, false, TickState.Off, HasButton: false));

        Assert.Equal(GridPart.Row, GridHitTest.At(f, 43, RowY(f, 0)).Part);
    }

    [Fact]
    public void A_point_on_an_expander_is_the_expander()
    {
        var f = Frame(new Node(0, Expandable: true, TickState.Off, true));

        // The tree column starts at 56; its expander sits a cell's padding in.
        var hit = GridHitTest.At(f, 56 + 8 + 3, RowY(f, 0));

        Assert.Equal(GridPart.Expander, hit.Part);
        Assert.Equal(2, hit.Column);
    }

    [Fact]
    public void An_expander_moves_right_with_the_depth()
    {
        var shallow = Frame(new Node(0, true, TickState.Off, true));
        var deep = Frame(new Node(2, true, TickState.Off, true));

        var at = 56 + 8 + 3;

        Assert.Equal(GridPart.Expander, GridHitTest.At(shallow, at, RowY(shallow, 0)).Part);
        Assert.Equal(GridPart.Row, GridHitTest.At(deep, at, RowY(deep, 0)).Part);
        Assert.Equal(GridPart.Expander, GridHitTest.At(deep, at + 2 * 14, RowY(deep, 0)).Part);
    }

    [Fact]
    public void A_tree_row_with_no_children_has_no_expander()
    {
        var f = Frame(new Node(0, Expandable: false, TickState.Off, true));

        Assert.Equal(GridPart.Row, GridHitTest.At(f, 56 + 8 + 3, RowY(f, 0)).Part);
    }

    [Fact]
    public void A_row_that_has_not_arrived_has_no_parts_to_press()
    {
        var f = Frame(null, null);

        Assert.Equal(GridPart.Row, GridHitTest.At(f, 15, RowY(f, 0)).Part);
        Assert.Equal(GridPart.Row, GridHitTest.At(f, 56 + 11, RowY(f, 0)).Part);
    }

    [Fact]
    public void A_point_in_the_header_is_a_header()
    {
        var f = Frame(Plain);
        var hit = GridHitTest.At(f, 150, 8);

        Assert.Equal(GridPart.Header, hit.Part);
        Assert.Equal(2, hit.Column);
        Assert.Equal(-1, hit.Row);
    }

    [Fact]
    public void A_point_on_a_column_edge_in_the_header_is_the_edge()
    {
        var f = Frame(Plain);

        // The tree column ends at 256.
        var hit = GridHitTest.At(f, 256, 8);

        Assert.Equal(GridPart.HeaderEdge, hit.Part);
        Assert.Equal(2, hit.Column);
    }

    /// <summary>A column that cannot be resized has no edge to grab, so the pointer there is on the
    /// header it is over.</summary>
    [Fact]
    public void The_edge_of_a_fixed_column_is_not_grabbable()
    {
        var f = Frame(Plain);

        Assert.Equal(GridPart.Header, GridHitTest.At(f, 30, 8).Part);
    }

    [Fact]
    public void With_the_header_off_the_top_of_the_control_is_the_first_row()
    {
        var f = Frame(Plain, Plain);
        f.ShowHeader = false;

        var hit = GridHitTest.At(f, 300, 5);

        Assert.Equal(0, hit.Row);
    }

    [Fact]
    public void Scrolling_down_changes_which_row_a_point_is_on()
    {
        var f = Frame([.. Enumerable.Repeat<object?>(Plain, 100)]);

        Assert.Equal(0, GridHitTest.At(f, 300, f.RowsTop + 5).Row);

        f.ScrollY = 24 * 10;
        Assert.Equal(10, GridHitTest.At(f, 300, f.RowsTop + 5).Row);
    }

    [Fact]
    public void Scrolling_sideways_changes_which_column_a_point_is_on()
    {
        var f = Frame(Plain);

        Assert.Equal(0, GridHitTest.At(f, 15, RowY(f, 0)).Column);

        f.ScrollX = 56;
        Assert.Equal(2, GridHitTest.At(f, 15, RowY(f, 0)).Column);
    }

    [Fact]
    public void A_point_that_is_not_a_number_is_nothing()
    {
        var f = Frame(Plain);

        Assert.Equal(GridHit.Nothing, GridHitTest.At(f, double.NaN, 10));
        Assert.Equal(GridHit.Nothing, GridHitTest.At(f, 10, double.NaN));
    }

    /// <summary>
    /// A drag aims at the nearest gap between rows, not at the top of whatever row the pointer is
    /// inside. Halfway down a row is already aiming at the gap below it.
    /// </summary>
    [Fact]
    public void A_drop_aims_at_the_nearest_gap()
    {
        var f = Frame([.. Enumerable.Repeat<object?>(Plain, 20)]);

        Assert.Equal(0, GridHitTest.DropAt(f, f.RowsTop + 2, 0, 20));
        Assert.Equal(1, GridHitTest.DropAt(f, f.RowsTop + 20, 0, 20));
        Assert.Equal(3, GridHitTest.DropAt(f, f.RowsTop + 24 * 3 + 2, 0, 20));
    }

    /// <summary>
    /// The queue's drag is clamped inside one record, so a drop past the end of it is not offered.
    /// Without the clamp, a drop a pixel past the last track of a record would give those files the
    /// next record's album.
    /// </summary>
    [Fact]
    public void A_drop_is_clamped_to_the_run_it_is_allowed_in()
    {
        var f = Frame([.. Enumerable.Repeat<object?>(Plain, 20)]);

        Assert.Equal(5, GridHitTest.DropAt(f, f.RowsTop, 5, 9));
        Assert.Equal(9, GridHitTest.DropAt(f, f.RowsTop + 24 * 19, 5, 9));
    }
}

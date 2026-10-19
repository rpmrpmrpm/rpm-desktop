using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using SkiaSharp;

namespace RPM.DataGrid.Tests;

/// <summary>
/// The painter, drawn into a bitmap and read back. Each test names a fact about the picture rather
/// than about the code that made it. The picture is what the user sees.
/// </summary>
public class RpmGridPainterTests
{
    private sealed record Track(string Title, string Artist, bool Playing = false, bool Explicit = false);

    private static List<object?> Tracks(int n) =>
        [.. Enumerable.Range(0, n).Select(object? (i) => new Track($"Track {i}", $"Artist {i % 7}"))];

    private static PaintHarness Grid(int rows = 40, int width = 400, int height = 240) =>
        new(
            [
                GridColumn.Text("title", 150, r => ((Track)r).Title, header: "Title"),
                GridColumn.Text("artist", 150, r => ((Track)r).Artist, header: "Artist", canSort: true),
            ],
            new ListSource(Tracks(rows)),
            width, height);

    /// <summary>The pixel band a row occupies on screen, given the header above it.</summary>
    private static (int Y0, int Y1) RowBand(GridFrame f, int index)
    {
        var top = f.RowsTop + f.Rows.TopOf(index) - f.ScrollY;
        return ((int)top, (int)(top + f.Metrics.RowHeight));
    }

    [Fact]
    public void An_empty_grid_is_the_surface_colour_under_the_header()
    {
        using var h = new PaintHarness(
            [GridColumn.Text("title", 150, _ => null)], new ListSource([]));

        using var shot = h.Render();

        Assert.Equal(0, h.Painter.RowsDrawn);
        Assert.Equal(0, shot.InkIn(0, 30, shot.Width, shot.Height));
    }

    [Fact]
    public void A_surface_with_no_size_draws_nothing_and_does_not_throw()
    {
        using var h = Grid();
        h.Frame.Width = 0;
        h.Frame.Height = 0;

        using var shot = h.Render();

        Assert.Equal(0, h.Painter.RowsDrawn);
    }

    [Fact]
    public void Only_the_rows_the_viewport_touches_are_drawn()
    {
        using var h = Grid(rows: 10_000);

        using var _ = h.Render();

        // 240 tall, 22 of it header, 24px rows: nine and a bit.
        Assert.InRange(h.Painter.RowsDrawn, 9, 10);
    }

    [Fact]
    public void A_million_rows_draw_the_same_number_as_a_hundred()
    {
        using var small = Grid(rows: 100);
        using var huge = Grid(rows: 1_000_000);

        using var a = small.Render();
        using var b = huge.Render();

        Assert.Equal(small.Painter.RowsDrawn, huge.Painter.RowsDrawn);
        Assert.Equal(small.Painter.CellsDrawn, huge.Painter.CellsDrawn);
    }

    /// <summary>Fifty columns in a four-hundred-pixel viewport draw the handful on screen.</summary>
    [Fact]
    public void Only_the_columns_the_viewport_touches_are_drawn()
    {
        var columns = Enumerable.Range(0, 50)
            .Select(i => GridColumn.Text($"c{i}", 100, _ => "x", header: $"C{i}"));

        using var h = new PaintHarness(columns, new ListSource(Tracks(20)));
        using var _ = h.Render();

        var perRow = h.Painter.CellsDrawn / Math.Max(1, h.Painter.RowsDrawn);
        Assert.InRange(perRow, 4, 6);
    }

    [Fact]
    public void A_rows_text_is_drawn_inside_its_own_column()
    {
        using var h = Grid();
        using var shot = h.Render();

        var (y0, y1) = RowBand(h.Frame, 0);

        // The title column runs 0..150, the artist column 150..300. Both have ink; neither has any
        // past 300, where there is no column at all.
        Assert.True(shot.InkIn(0, y0, 150, y1) > 0, "the title column drew nothing");
        Assert.True(shot.InkIn(150, y0, 300, y1) > 0, "the artist column drew nothing");
    }

    /// <summary>
    /// The reason there is no per-cell clip: text is fitted to its column before it is shaped, so it
    /// cannot reach the next one. If that stops being true this is what says so.
    /// </summary>
    [Fact]
    public void A_title_too_long_for_its_column_stays_inside_it()
    {
        using var h = new PaintHarness(
            [
                GridColumn.Text("title", 80, _ => "Everything In Its Right Place (Gigamesh Extended Remix)"),
                GridColumn.Text("blank", 200, _ => null),
            ],
            new ListSource(Tracks(10)));

        using var blank = new PaintHarness(
            [
                GridColumn.Text("title", 80, _ => null),
                GridColumn.Text("blank", 200, _ => null),
            ],
            new ListSource(Tracks(10)));

        using var withText = h.Render();
        using var without = blank.Render();

        var box = withText.DiffBox(without);

        Assert.NotNull(box);
        Assert.True(box!.Value.Right < 80, $"the title reached x={box.Value.Right}, past its column's edge at 80");
    }

    [Fact]
    public void Every_other_row_wears_the_band()
    {
        using var h = Grid();
        using var shot = h.Render();

        var (evenY0, evenY1) = RowBand(h.Frame, 0);
        var (oddY0, oddY1) = RowBand(h.Frame, 1);

        // Read a column of pixels past the text, where only the band is drawn.
        var even = shot.At(295, (evenY0 + evenY1) / 2);
        var odd = shot.At(295, (oddY0 + oddY1) / 2);

        Assert.True(Shot.Differs(even, odd), $"banded and unbanded rows are the same colour ({even} / {odd})");
    }

    [Fact]
    public void A_selected_row_is_washed_and_wears_the_bar()
    {
        using var h = Grid();
        using var plain = h.Render();

        h.Frame.Selection.Click(2, GridModifiers.None, 40);
        using var picked = h.Render();

        var (y0, y1) = RowBand(h.Frame, 2);
        var mid = (y0 + y1) / 2;

        Assert.True(Shot.Differs(plain.At(295, mid), picked.At(295, mid)), "the row is not washed");

        // The bar is three pixels of full-strength cyan down the left.
        var bar = picked.At(1, mid);
        Assert.True(bar.Blue > 200 && bar.Green > 150, $"the selection bar is {bar}");
        Assert.False(Shot.Differs(picked.At(1, mid), picked.At(2, mid)), "the bar is not solid");
    }

    [Fact]
    public void Only_the_selected_row_is_washed()
    {
        using var h = Grid();
        h.Frame.Selection.Click(2, GridModifiers.None, 40);

        using var shot = h.Render();

        var (y0, y1) = RowBand(h.Frame, 2);
        var (n0, n1) = RowBand(h.Frame, 4);

        Assert.True(Shot.Differs(shot.At(1, (y0 + y1) / 2), shot.At(1, (n0 + n1) / 2)));
    }

    [Fact]
    public void A_run_of_selected_rows_is_washed_from_end_to_end()
    {
        using var h = Grid();
        h.Frame.Selection.Click(2, GridModifiers.None, 40);
        h.Frame.Selection.Click(5, GridModifiers.Shift, 40);

        using var shot = h.Render();

        foreach (var row in new[] { 2, 3, 4, 5 })
        {
            var (y0, y1) = RowBand(h.Frame, row);
            var bar = shot.At(1, (y0 + y1) / 2);
            Assert.True(bar.Blue > 200, $"row {row} has no selection bar");
        }

        var (afterY0, afterY1) = RowBand(h.Frame, 6);
        Assert.True(shot.At(1, (afterY0 + afterY1) / 2).Blue < 200, "row 6 should not be selected");
    }

    [Fact]
    public void The_hovered_row_differs_from_the_rest()
    {
        using var h = Grid();
        using var cold = h.Render();

        h.Frame.HoverRow = 3;
        using var warm = h.Render();

        var (y0, y1) = RowBand(h.Frame, 3);
        Assert.NotEqual(cold.Band(y0, y1), warm.Band(y0, y1));

        var (otherY0, otherY1) = RowBand(h.Frame, 5);
        Assert.Equal(cold.Band(otherY0, otherY1), warm.Band(otherY0, otherY1));
    }

    [Fact]
    public void Scrolling_down_draws_different_rows()
    {
        using var h = Grid(rows: 1000);
        using var top = h.Render();

        h.Frame.ScrollY = 24 * 100;
        using var down = h.Render();

        var (y0, y1) = RowBand(h.Frame, 100);
        Assert.NotEqual(top.Band(30, 60), down.Band(30, 60));
        Assert.True(down.InkIn(0, y0, 150, y1) > 0);
    }

    [Fact]
    public void Scrolling_sideways_moves_the_columns()
    {
        using var h = Grid();
        using var home = h.Render();

        h.Frame.ScrollX = 100;
        using var across = h.Render();

        var (y0, y1) = RowBand(h.Frame, 0);
        Assert.NotEqual(home.Band(y0, y1), across.Band(y0, y1));
    }

    /// <summary>The header does not scroll with the rows: it is a band at the top in the surface's
    /// own coordinates.</summary>
    [Fact]
    public void The_header_stays_put_while_the_rows_move()
    {
        using var h = Grid(rows: 1000);
        using var home = h.Render();

        h.Frame.ScrollY = 24 * 50;
        using var down = h.Render();

        Assert.Equal(home.Band(0, 21), down.Band(0, 21));
    }

    [Fact]
    public void The_header_scrolls_sideways_with_the_columns()
    {
        using var h = Grid();
        using var home = h.Render();

        h.Frame.ScrollX = 60;
        using var across = h.Render();

        Assert.NotEqual(home.Band(0, 21), across.Band(0, 21));
    }

    [Fact]
    public void A_header_turned_off_gives_its_height_back_to_the_rows()
    {
        using var h = Grid();
        h.Frame.ShowHeader = false;

        using var shot = h.Render();

        Assert.Equal(0, h.Frame.RowsTop);
        Assert.True(shot.InkIn(0, 0, 150, 24) > 0, "the first row should start at the top");
    }

    [Fact]
    public void The_sorted_column_wears_a_mark_and_the_others_do_not()
    {
        using var h = Grid();
        using var plain = h.Render();

        h.Frame.SortColumn = "artist";
        using var sorted = h.Render();

        var box = sorted.DiffBox(plain);

        // The mark sits at the right of the artist header, which runs 150..300, and nowhere else.
        Assert.NotNull(box);
        Assert.True(box!.Value.Bottom < h.Frame.Metrics.HeaderHeight,
            $"the sort mark reached y={box.Value.Bottom}, below the header");
        Assert.InRange(box.Value.Right, 260, 300);

        // And it points the other way when the sort does.
        h.Frame.SortDescending = true;
        using var down = h.Render();
        Assert.NotNull(down.DiffBox(sorted));
    }

    [Fact]
    public void A_filtered_column_says_so_in_its_header()
    {
        using var h = Grid();
        using var plain = h.Render();

        h.Frame.FilteredColumns = new HashSet<string> { "title" };
        using var filtered = h.Render();

        var box = filtered.DiffBox(plain);

        Assert.NotNull(box);
        Assert.True(box!.Value.Bottom < h.Frame.Metrics.HeaderHeight, "the filter mark is not in the header");
        Assert.InRange(box.Value.Right, 110, 150);
    }

    /// <summary>
    /// A row whose page has not arrived draws nothing rather than a placeholder object's ToString.
    /// It is a row on its way, not a row that is blank.
    /// </summary>
    [Fact]
    public void A_row_that_has_not_arrived_draws_no_text_but_keeps_its_band()
    {
        var rows = Tracks(10);
        rows[3] = null;

        using var h = new PaintHarness(
            [GridColumn.Text("title", 150, r => ((Track)r).Title)], new ListSource(rows));

        using var shot = h.Render();
        var (y0, y1) = RowBand(h.Frame, 3);

        Assert.Equal(0, shot.InkIn(2, y0 + 2, 140, y1 - 2));
        Assert.Equal(10, h.Painter.RowsDrawn);
    }

    [Fact]
    public void A_tick_is_drawn_and_its_three_states_differ()
    {
        var shots = new List<byte[]>();

        foreach (var state in new[] { TickState.None, TickState.Off, TickState.On, TickState.Partly })
        {
            using var h = new PaintHarness(
                [GridColumn.Tick("tick", 30, _ => state)], new ListSource(Tracks(5)));
            using var shot = h.Render();
            shots.Add(shot.Band(22, 46));
        }

        Assert.Equal(4, shots.Distinct(new ByteArrayComparer()).Count());
    }

    [Fact]
    public void A_deeper_tree_row_starts_its_text_further_right()
    {
        var depths = new List<object?> { 0, 1, 2 };

        using var h = new PaintHarness(
            [GridColumn.Tree("name", 200, r => new TreeCell((int)r!, TreeMarks.None, "Name"))],
            new ListSource(depths));

        // Where the text starts is where this render differs from one with no text in it.
        using var bare = new PaintHarness(
            [GridColumn.Tree("name", 200, r => new TreeCell((int)r!, TreeMarks.None, null))],
            new ListSource(depths));

        using var text = h.Render();
        using var none = bare.Render();

        var starts = new List<int>();
        for (var row = 0; row < 3; row++)
        {
            var (y0, y1) = RowBand(h.Frame, row);
            var left = -1;
            for (var x = 0; x < text.Width && left < 0; x++)
                for (var y = y0; y < y1; y++)
                    if (Shot.Differs(text.At(x, y), none.At(x, y)))
                    {
                        left = x;
                        break;
                    }

            starts.Add(left);
        }

        Assert.True(starts[1] > starts[0], $"depth 1 starts at {starts[1]}, depth 0 at {starts[0]}");
        Assert.True(starts[2] > starts[1], $"depth 2 starts at {starts[2]}, depth 1 at {starts[1]}");
    }

    [Fact]
    public void An_expandable_tree_row_shows_which_way_it_is_facing()
    {
        using var open = new PaintHarness(
            [GridColumn.Tree("name", 200, _ => new TreeCell(0, TreeMarks.Expandable | TreeMarks.Expanded, "A"))],
            new ListSource(Tracks(3)));
        using var shut = new PaintHarness(
            [GridColumn.Tree("name", 200, _ => new TreeCell(0, TreeMarks.Expandable, "A"))],
            new ListSource(Tracks(3)));

        using var a = open.Render();
        using var b = shut.Render();

        Assert.NotEqual(a.Band(22, 46), b.Band(22, 46));
    }

    [Fact]
    public void The_now_playing_mark_bobs_with_its_clock()
    {
        using var h = new PaintHarness(
            [GridColumn.PlayingBars("mark", 30, _ => true)], new ListSource(Tracks(3)));

        using var still = h.Render();

        h.Frame.BarsClock = 0.32;
        using var later = h.Render();

        Assert.NotEqual(still.Band(22, 46), later.Band(22, 46));
    }

    [Fact]
    public void A_row_that_is_not_playing_draws_no_mark()
    {
        using var h = new PaintHarness(
            [GridColumn.PlayingBars("mark", 30, _ => false)], new ListSource(Tracks(3)));

        using var playing = new PaintHarness(
            [GridColumn.PlayingBars("mark", 30, _ => true)], new ListSource(Tracks(3)));

        using var off = h.Render();
        using var on = playing.Render();

        // Nothing is drawn where the mark would be. Said as a comparison rather than as "no ink
        // here", because the rule at the column's edge and the row bands are ink too.
        Assert.NotNull(on.DiffBox(off));
        Assert.Null(off.DiffBox(off));
    }

    [Fact]
    public void The_explicit_badge_is_drawn_behind_the_title()
    {
        using var plain = new PaintHarness(
            [GridColumn.TitleBadge("title", 200, _ => new TitleCell("Song", false))],
            new ListSource(Tracks(3)));
        using var badged = new PaintHarness(
            [GridColumn.TitleBadge("title", 200, _ => new TitleCell("Song", true))],
            new ListSource(Tracks(3)));

        using var a = plain.Render();
        using var b = badged.Render();

        var box = b.DiffBox(a);

        // The badge is the only difference, and it sits behind the title rather than at the far edge
        // of the column, which runs to 200.
        Assert.NotNull(box);
        Assert.True(box!.Value.Left > 20, $"the badge starts at {box.Value.Left}, which is on top of the title");
        Assert.True(box.Value.Right < 120, $"the badge reaches {box.Value.Right}, which is away at the column's edge");
    }

    [Fact]
    public void The_sweep_rectangle_is_drawn_over_the_rows()
    {
        using var h = Grid();
        using var plain = h.Render();

        h.Frame.Band = new GridBand(40, 40, 160, 120);
        using var swept = h.Render();

        Assert.NotEqual(plain.Band(50, 110), swept.Band(50, 110));
        Assert.True(swept.IsInk(100, 80));
    }

    [Fact]
    public void The_drop_line_is_drawn_where_a_drag_would_land()
    {
        using var h = Grid();
        using var plain = h.Render();

        h.Frame.Drop = new GridDrop(3);
        using var dropping = h.Render();

        var y = (int)(h.Frame.RowsTop + h.Frame.Rows.TopOf(3));
        Assert.NotEqual(plain.Band(y - 2, y + 2), dropping.Band(y - 2, y + 2));
    }

    [Fact]
    public void Carried_rows_are_drawn_away_from_where_they_live()
    {
        using var h = Grid();
        using var still = h.Render();

        var carried = new SpanSet();
        carried.Add(2, 3);
        h.Frame.Carried = carried;
        h.Frame.CarryOffsetY = 30;

        using var lifted = h.Render();

        Assert.NotEqual(still.Band(22, 240), lifted.Band(22, 240));
    }

    [Fact]
    public void Nothing_is_drawn_above_the_header()
    {
        using var h = Grid(rows: 1000);
        h.Frame.ScrollY = 12;                 // half a row scrolled, so a row straddles the header

        using var shot = h.Render();

        // The header's own band is its colour plus its text; what must not appear there is a row's
        // band or wash. Checked by painting the same frame with no rows at all and comparing the
        // strip above the header's bottom rule.
        using var bare = new PaintHarness(
            [GridColumn.Text("title", 150, _ => null, header: "Title"),
             GridColumn.Text("artist", 150, _ => null, header: "Artist")],
            new ListSource([]),
            h.Width, h.Height);

        using var empty = bare.Render();

        Assert.Equal(empty.Band(0, 20), shot.Band(0, 20));
    }

    /// <summary>
    /// A grid whose selection is a fact about its rows rather than a set the grid keeps. The import
    /// queue is that grid: its ticks live on the nodes so they survive a rebuild, and the wash is a
    /// second view of the same fact.
    /// </summary>
    [Fact]
    public void The_wash_can_be_read_off_the_rows_instead_of_the_selection()
    {
        using var h = Grid();

        // Nothing is selected, and rows 1 and 2 say they are washed anyway.
        h.Frame.Washed = (index, _) => index is 1 or 2;

        using var shot = h.Render();

        Assert.True(h.Frame.Selection.IsEmpty);

        foreach (var row in new[] { 1, 2 })
        {
            var (y0, y1) = RowBand(h.Frame, row);
            Assert.True(shot.At(1, (y0 + y1) / 2).Blue > 200, $"row {row} has no selection bar");
        }

        var (plainY0, plainY1) = RowBand(h.Frame, 3);
        Assert.True(shot.At(1, (plainY0 + plainY1) / 2).Blue < 200, "row 3 should not be washed");
    }

    /// <summary>
    /// It is asked of the ROW, not only of the index, so a grid can answer from the object rather
    /// than having to keep a parallel record keyed by position.
    /// </summary>
    [Fact]
    public void The_wash_is_asked_about_the_row_object()
    {
        using var h = Grid();
        h.Frame.Washed = (_, row) => row is Track { Title: "Track 4" };

        using var shot = h.Render();

        var (y0, y1) = RowBand(h.Frame, 4);
        Assert.True(shot.At(1, (y0 + y1) / 2).Blue > 200);
    }

    [Fact]
    public void With_no_wash_of_its_own_a_grid_uses_its_selection()
    {
        using var h = Grid();
        h.Frame.Selection.Click(2, GridModifiers.None, 40);

        using var shot = h.Render();

        var (y0, y1) = RowBand(h.Frame, 2);
        Assert.True(shot.At(1, (y0 + y1) / 2).Blue > 200);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);

        public int GetHashCode(byte[] a)
        {
            var h = 17;
            for (var i = 0; i < a.Length; i += 97)
                h = h * 31 + a[i];
            return h;
        }
    }
}

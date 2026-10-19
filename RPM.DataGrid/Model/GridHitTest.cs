using RPM.DataGrid.Paint;

namespace RPM.DataGrid.Model;

/// <summary>What a point is on.</summary>
public enum GridPart
{
    /// <summary>Past the last row, or on no column at all.</summary>
    Nothing,

    /// <summary>A row, on a cell with nothing of its own to press.</summary>
    Row,

    /// <summary>A tick's box.</summary>
    Tick,

    /// <summary>A tree row's expander.</summary>
    Expander,

    /// <summary>A tree row's drag handle. The only place a row can be picked up.</summary>
    Grip,

    /// <summary>A glyph button.</summary>
    Button,

    /// <summary>A column header.</summary>
    Header,

    /// <summary>The draggable edge between two column headers.</summary>
    HeaderEdge,

    /// <summary>The filter mark at the right of a column header.</summary>
    HeaderFilter,
}

/// <summary>What is under a point.</summary>
/// <param name="Part">What kind of thing.</param>
/// <param name="Row">Which row, or -1 when the point is not on one.</param>
/// <param name="Column">Which visible column, or -1.</param>
public readonly record struct GridHit(GridPart Part, int Row, int Column)
{
    public static readonly GridHit Nothing = new(GridPart.Nothing, -1, -1);

    /// <summary>Whether the point is on a row at all, whatever part of it.</summary>
    public bool OnRow => Row >= 0;
}

/// <summary>
/// What a point in the grid is on.
///
/// <para>Pure arithmetic, kept out of the control (like the column offsets) so it can be unit
/// tested. "Does a click on the left half of a thirty-pixel column hit the tick" has one right
/// answer, and a hit test inside a pointer handler can only be checked by hand.</para>
///
/// <para>Beyond geometry it needs to know what KIND each column is, because a tick, an expander and
/// a button have their own areas inside a cell. So it takes the frame, which has that
/// information.</para>
/// </summary>
public static class GridHitTest
{
    /// <summary>
    /// What is at a point, in the surface's own coordinates (the same ones pointer events use).
    /// </summary>
    /// <param name="frame">The grid as it is currently drawn.</param>
    /// <param name="x">Across, from the left of the control.</param>
    /// <param name="y">Down, from the top of the control.</param>
    public static GridHit At(GridFrame frame, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (double.IsNaN(x) || double.IsNaN(y))
            return GridHit.Nothing;

        var stripX = x + frame.ScrollX;

        if (frame.ShowHeader && y < frame.RowsTop)
            return Header(frame, stripX);

        var listY = y - frame.RowsTop + frame.ScrollY;
        var row = frame.Rows.RowAt(listY, frame.Source.Count);
        if (row < 0)
            return GridHit.Nothing;

        var column = frame.Columns.HitTest(stripX, out var within);
        if (column < 0)
            return new GridHit(GridPart.Row, row, -1);

        var part = PartOf(frame, row, column, within);
        return new GridHit(part, row, column);
    }

    /// <summary>
    /// Which part of a cell the point is in. Anything that is not one of a cell's own pressable
    /// areas is the row, so a click in the empty half of a column selects rather than doing nothing.
    /// </summary>
    private static GridPart PartOf(GridFrame frame, int row, int column, double within)
    {
        var columns = frame.Columns.Visible;
        if (column >= columns.Count)
            return GridPart.Row;

        var definition = columns[column];
        var m = frame.Metrics;

        switch (definition.Kind)
        {
            case CellKind.Tick:
            case CellKind.GlyphButton:
            {
                // Centred in its column, and pressable over its own box plus a little on each side:
                // a fourteen-pixel target is small, and a click that misses goes to the row.
                var side = definition.Kind == CellKind.Tick ? m.TickSize : m.IconSize + 6;
                var half = side / 2 + 3;
                var middle = definition.Width / 2;

                // A row that has not loaded has nothing to press, and neither has one whose cell is
                // blank. Keep the test as "unless there is something there", not "if there is
                // something there and it is blank": the second form looks the same but lets a row
                // that is still loading offer a tick that stands for nothing.
                if (frame.Source.Row(row) is not { } item || definition.Read(item).IsBlank)
                    return GridPart.Row;

                return Math.Abs(within - middle) <= half
                    ? definition.Kind == CellKind.Tick ? GridPart.Tick : GridPart.Button
                    : GridPart.Row;
            }

            case CellKind.Tree:
            {
                if (frame.Source.Row(row) is not { } item)
                    return GridPart.Row;

                var value = definition.Read(item);
                var left = m.CellPadX + value.Depth * m.Indent;

                if ((value.Marks & TreeMarks.Expandable) != 0
                    && within >= left - 2 && within <= left + m.ExpanderSize + 4)
                {
                    return GridPart.Expander;
                }

                // The grip is where the state glyph is, one expander's width in, and it is only shown
                // on the row under the pointer (HoverRow), so it is only hit there.
                var glyph = left + m.ExpanderSize + 4;
                if ((value.Marks & TreeMarks.Draggable) != 0
                    && definition.GripGlyph is { Length: > 0 }
                    && row == frame.HoverRow
                    && within >= glyph - 2 && within <= glyph + m.IconSize + 4)
                {
                    return GridPart.Grip;
                }

                return GridPart.Row;
            }

            default:
                return GridPart.Row;
        }
    }

    private static GridHit Header(GridFrame frame, double stripX)
    {
        var edge = frame.Columns.EdgeAt(stripX, frame.Metrics.ResizeGrab);
        if (edge >= 0)
            return new GridHit(GridPart.HeaderEdge, -1, edge);

        var column = frame.Columns.HitTest(stripX, out var within);
        if (column < 0)
            return GridHit.Nothing;

        // The filter mark sits at the right of the header, inside the padding. It has its own
        // target, so a click meant to filter by this column is never read as a sort.
        var definition = frame.Columns.Visible[column];
        if (definition.CanFilter)
        {
            var mark = definition.Width - frame.Metrics.HeaderPadX - frame.Metrics.HeaderMarkSize;
            if (within >= mark - 3)
                return new GridHit(GridPart.HeaderFilter, -1, column);
        }

        return new GridHit(GridPart.Header, -1, column);
    }

    /// <summary>
    /// Which gap between rows a drag holding rows is aiming at: how many rows sit above the line.
    /// It is the row boundary nearest the pointer, not the top of the row under the pointer, so the
    /// line appears where the user expects it.
    /// </summary>
    public static int DropAt(GridFrame frame, double y, int first, int last)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var listY = y - frame.RowsTop + frame.ScrollY;
        var slot = (int)Math.Round(listY / frame.Rows.RowHeight);

        return Math.Clamp(slot, Math.Max(0, first), Math.Min(frame.Source.Count, last));
    }
}

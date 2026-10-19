namespace RPM.DataGrid.Model;

/// <summary>
/// Which rows a viewport can see, and where each one sits.
///
/// <para>Every row is the same height, by design. It makes every question here arithmetic instead
/// of a search, and it lets the grid know the total height of a million rows without measuring any
/// of them. Both of RPM's grids set a fixed row height in markup.</para>
/// </summary>
/// <param name="rowHeight">How tall one row is. Must be positive.</param>
public readonly struct RowStrip(double rowHeight)
{
    /// <summary>How tall one row is.</summary>
    public double RowHeight { get; } = rowHeight > 0 ? rowHeight : 1;

    /// <summary>How tall the whole list is.</summary>
    public double TotalHeight(int count) => Math.Max(0, count) * RowHeight;

    /// <summary>Where a row's top edge is, measured from the top of the list.</summary>
    public double TopOf(int row) => row * RowHeight;

    /// <summary>
    /// The rows that intersect the viewport, as a half-open range.
    /// </summary>
    /// <param name="scrollY">How far down the list is scrolled.</param>
    /// <param name="viewportHeight">How much height the rows are drawn into.</param>
    /// <param name="count">How many rows there are.</param>
    /// <param name="overscan">Extra rows on each side, so a scroll does not reach the edge of what
    /// has been prepared.</param>
    public (int First, int End) RangeAt(double scrollY, double viewportHeight, int count, int overscan = 1)
    {
        if (count <= 0)
            return (0, 0);

        // Before the first layout, nothing is known about the viewport. A guess of one screen would
        // be wrong in either direction, so return no rows; the next layout asks again.
        if (double.IsNaN(viewportHeight) || viewportHeight <= 0)
            return (0, 0);

        var from = double.IsNaN(scrollY) ? 0 : Math.Max(0, scrollY);

        var first = (int)Math.Floor(from / RowHeight);
        var end = (int)Math.Ceiling((from + viewportHeight) / RowHeight);

        first = Math.Clamp(first - overscan, 0, count);
        end = Math.Clamp(end + overscan, first, count);

        return (first, end);
    }

    /// <summary>Which row a y in list coordinates falls in, or -1 past the last one.</summary>
    public int RowAt(double y, int count)
    {
        if (count <= 0 || double.IsNaN(y) || y < 0)
            return -1;

        var row = (int)Math.Floor(y / RowHeight);
        return row >= count ? -1 : row;
    }

    /// <summary>
    /// How far the list must scroll for a row to be fully on screen, or the offset unchanged if it
    /// already is. Used by keyboard navigation and by the jump to the playing track.
    /// </summary>
    public double ScrollToShow(int row, double scrollY, double viewportHeight, int count)
    {
        if (count <= 0 || viewportHeight <= 0)
            return scrollY;

        row = Math.Clamp(row, 0, count - 1);

        var top = TopOf(row);
        var bottom = top + RowHeight;
        var limit = Math.Max(0, TotalHeight(count) - viewportHeight);

        if (top < scrollY)
            return Math.Clamp(top, 0, limit);

        if (bottom > scrollY + viewportHeight)
            return Math.Clamp(bottom - viewportHeight, 0, limit);

        return scrollY;
    }
}

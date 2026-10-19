namespace RPM.DataGrid.Paint;

/// <summary>
/// The sizes the grid draws at, in logical pixels.
///
/// <para>The defaults match what the two grids set in markup: 24px rows on the library table, 22px
/// on the queue, a 22px header, 12px text and 11px headers. The host overrides what differs, so the
/// painter does not have to guess.</para>
/// </summary>
public readonly record struct GridMetrics
{
    /// <summary>How tall one row is. The same for every row; see <c>RowStrip</c>.</summary>
    public double RowHeight { get; init; } = 24;

    /// <summary>How tall the column header band is.</summary>
    public double HeaderHeight { get; init; } = 22;

    /// <summary>Cell text size.</summary>
    public float FontSize { get; init; } = 12;

    /// <summary>Column header text size.</summary>
    public float HeaderFontSize { get; init; } = 11;

    /// <summary>How far a cell's content sits in from the column's edges.</summary>
    public float CellPadX { get; init; } = 8;

    /// <summary>How far a column header's text sits in.</summary>
    public float HeaderPadX { get; init; } = 8;

    /// <summary>An icon glyph's size.</summary>
    public float IconSize { get; init; } = 10.5f;

    /// <summary>The sort arrow and filter mark in a header.</summary>
    public float HeaderMarkSize { get; init; } = 9;

    /// <summary>How far one level of the tree indents.</summary>
    public float Indent { get; init; } = 14;

    /// <summary>The expander triangle's width.</summary>
    public float ExpanderSize { get; init; } = 8;

    /// <summary>The tick box's side.</summary>
    public float TickSize { get; init; } = 14;

    /// <summary>The explicit badge's side.</summary>
    public float BadgeSize { get; init; } = 13;

    /// <summary>The gap between a title and its badge.</summary>
    public float BadgeGap { get; init; } = 6;

    /// <summary>The bar down the left of a selected row.</summary>
    public float SelectionBarWidth { get; init; } = 3;

    /// <summary>How near a column's edge the pointer has to be to start a resize.</summary>
    public float ResizeGrab { get; init; } = 4;

    /// <summary>How far the pointer has to travel before a press becomes a drag.</summary>
    public float DragSlop { get; init; } = 5;

    /// <summary>The now-playing mark: how wide one bar is, and the gap between them.</summary>
    public float BarWidth { get; init; } = 2.5f;

    public float BarGap { get; init; } = 2;

    public GridMetrics()
    {
    }
}

using RPM.DataGrid.Model;

namespace RPM.DataGrid.Paint;

/// <summary>The rectangle a rubber-band sweep is drawing, in the rows area's own coordinates.</summary>
public readonly record struct GridBand(double X1, double Y1, double X2, double Y2);

/// <summary>Where a drag would put what it is carrying.</summary>
/// <param name="Row">The row index the line sits above. Equal to the count to mean "after the last".</param>
public readonly record struct GridDrop(int Row);

/// <summary>
/// Everything one paint needs.
///
/// <para>A class the control owns and fills, not a struct built per frame: it is written once a
/// frame and read once, so a new object per frame would cost an allocation and gain
/// nothing.</para>
/// </summary>
public sealed class GridFrame
{
    /// <summary>The columns and their widths.</summary>
    public required GridColumns Columns { get; init; }

    /// <summary>The rows the source holds.</summary>
    public required IGridSource Source { get; set; }

    /// <summary>Which rows are selected, and where the focus is.</summary>
    public required GridSelection Selection { get; init; }

    /// <summary>
    /// Whether a row is washed (drawn as selected), asked of the row itself at the moment it is
    /// drawn.
    ///
    /// <para>Null for a grid that owns its selection, which is the library table. The import queue
    /// is the other kind: there the ticks ARE the selection, they are stored on the nodes so they
    /// survive the tree being rebuilt, and the wash is a second view of the same state. It must be
    /// asked at paint time: setting the wash only on the rows on screen when the ticks changed
    /// would miss every row that scrolls into view later.</para>
    /// </summary>
    public Func<int, object?, bool>? Washed { get; set; }

    /// <summary>Whether the row at an index is drawn as selected.</summary>
    public bool IsWashed(int index, object? row) =>
        Washed is { } ask ? ask(index, row) : Selection.Contains(index);

    /// <summary>
    /// Where an <see cref="CellKind.Art"/> cell's picture comes from: the host's cache, asked by the
    /// key the cell carries.
    ///
    /// <para>Answers from memory or returns null. It is called inside the paint, once per visible
    /// art cell, so it must not read a file, decode anything or wait. The host fetches a cover that
    /// is not cached yet off the dispatcher, and invalidates the grid when it arrives.</para>
    ///
    /// <para>Null on a grid with no art column, which is every grid except the wizard's.</para>
    /// </summary>
    public Func<string, SkiaSharp.SKImage?>? Art { get; set; }

    /// <summary>Row geometry. Rebuilt when the row height changes.</summary>
    public RowStrip Rows { get; set; } = new(24);

    public GridPalette Palette { get; set; } = new();

    public GridMetrics Metrics { get; set; } = new();

    /// <summary>How far the strip is scrolled sideways.</summary>
    public double ScrollX { get; set; }

    /// <summary>How far the list is scrolled down.</summary>
    public double ScrollY { get; set; }

    /// <summary>How big the surface is.</summary>
    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>
    /// Whether the painter paints its own background before drawing the rows.
    ///
    /// <para>False when the host element paints it instead, through an ordinary XAML Background.
    /// This is required, not a preference: on the Uno head the Skia element draws into the window's
    /// canvas, and a TRANSLUCENT background there is blended over what was already in the buffer
    /// instead of replacing it, so the previous frame shows through. A XAML brush is painted by the
    /// framework as part of the scene, which does not have that problem.</para>
    ///
    /// <para>True by default, because a painter that draws into its own surface (as every test in
    /// this library does) owns its background.</para>
    /// </summary>
    public bool PaintsOwnGround { get; set; } = true;

    /// <summary>Whether the column header band is drawn at all.</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>The row the pointer is over, or -1.</summary>
    public int HoverRow { get; set; } = -1;

    /// <summary>The visible column the pointer is over, or -1.</summary>
    public int HoverColumn { get; set; } = -1;

    /// <summary>The column header the pointer is over, or -1.</summary>
    public int HoverHeader { get; set; } = -1;

    /// <summary>The row of a glyph button being held down, or -1.</summary>
    public int PressedRow { get; set; } = -1;

    /// <summary>Its column, or -1.</summary>
    public int PressedColumn { get; set; } = -1;

    /// <summary>The column the list is sorted by, or null.</summary>
    public string? SortColumn { get; set; }

    /// <summary>Whether that sort is descending.</summary>
    public bool SortDescending { get; set; }

    /// <summary>The columns with a filter on them, so their headers can say so.</summary>
    public IReadOnlyCollection<string>? FilteredColumns { get; set; }

    /// <summary>Seconds since the now-playing bars started to animate.</summary>
    public double BarsClock { get; set; }

    /// <summary>The sweep rectangle, when one is being dragged.</summary>
    public GridBand? Band { get; set; }

    /// <summary>Where a row drag would drop, when one is running.</summary>
    public GridDrop? Drop { get; set; }

    /// <summary>The rows a drag is carrying, drawn offset from where they live.</summary>
    public SpanSet? Carried { get; set; }

    /// <summary>How far the carried rows are drawn from their own places.</summary>
    public double CarryOffsetY { get; set; }

    /// <summary>Where the rows area starts, below the header.</summary>
    public double RowsTop => ShowHeader ? Metrics.HeaderHeight : 0;

    /// <summary>How tall the rows area is.</summary>
    public double RowsHeight => Math.Max(0, Height - RowsTop);
}

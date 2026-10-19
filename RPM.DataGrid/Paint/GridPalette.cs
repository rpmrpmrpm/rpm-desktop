using SkiaSharp;

namespace RPM.DataGrid.Paint;

/// <summary>
/// Every colour the grid draws with.
///
/// <para>Filled by the host from the app's own resource dictionary, not declared here.
/// <c>Themes/Generic.xaml</c> and <c>Themes/GridSelection.xaml</c> stay the one place the app's
/// colours are defined, and a theme change refills this so the two always agree.</para>
///
/// <para>The defaults match those files, so a painter built without a host (in a test) still draws
/// the right colours. Keep them in step when the theme changes.</para>
/// </summary>
public readonly record struct GridPalette
{
    /// <summary>Behind everything. <c>GridSurfaceBrush</c>.</summary>
    public SKColor Surface { get; init; } = 0x33000000;

    /// <summary>Every other row, over the surface. <c>GridRowBandBrush</c>.</summary>
    public SKColor RowBand { get; init; } = 0x0EFFFFFF;

    /// <summary>A selected row. <c>TableViewRowBackgroundSelected</c>.</summary>
    public SKColor RowSelected { get; init; } = 0x2404C8FF;

    /// <summary>A selected row with the pointer on it.</summary>
    public SKColor RowSelectedHover { get; init; } = 0x3004C8FF;

    /// <summary>An unselected row with the pointer on it.</summary>
    public SKColor RowHover { get; init; } = 0x0AFFFFFF;

    /// <summary>The bar down the left of a selected row.
    /// <c>TableViewRowSelectionIndicatorBrush</c>.</summary>
    public SKColor SelectionBar { get; init; } = 0xFF04C8FF;

    /// <summary>The lines between columns, and the grid's own edge. <c>ListEdgeBrush</c>.</summary>
    public SKColor Rule { get; init; } = 0x19FFFFFF;

    /// <summary>Cell text.</summary>
    public SKColor Text { get; init; } = 0xE6FFFFFF;

    /// <summary>Text in a row whose page has not arrived.</summary>
    public SKColor TextDim { get; init; } = 0x59FFFFFF;

    /// <summary>Behind the column headers. <c>CardTitleBarBrush</c>.</summary>
    public SKColor Header { get; init; } = 0x40000000;

    /// <summary>A column header with the pointer on it.</summary>
    public SKColor HeaderHover { get; init; } = 0x14FFFFFF;

    /// <summary>Column header text.</summary>
    public SKColor HeaderText { get; init; } = 0xFFFFFFFF;

    /// <summary>The sort arrow and the filter mark.</summary>
    public SKColor HeaderMark { get; init; } = 0xCCFFFFFF;

    /// <summary>A tick's box when it is off.</summary>
    public SKColor TickEdge { get; init; } = 0x8AFFFFFF;

    /// <summary>A tick's fill when it is on or partly on. The queue's own azure.</summary>
    public SKColor TickFill { get; init; } = 0xB00371D0;

    /// <summary>The mark inside a tick.</summary>
    public SKColor TickMark { get; init; } = 0xFFFFFFFF;

    /// <summary>The square an art cell draws while its cover has not arrived, and for a record that
    /// has no cover at all. Faint: it marks where a cover goes, it is not a missing-image
    /// warning.</summary>
    public SKColor ArtWell { get; init; } = 0x14FFFFFF;

    /// <summary>The expander triangle.</summary>
    public SKColor Expander { get; init; } = 0xB3FFFFFF;

    /// <summary>The line a drag would drop rows on.</summary>
    public SKColor DropLine { get; init; } = 0xFF04C8FF;

    /// <summary>The rectangle a rubber-band sweep draws.</summary>
    public SKColor BandFill { get; init; } = 0x2204C8FF;

    /// <summary>Its outline.</summary>
    public SKColor BandEdge { get; init; } = 0x8804C8FF;

    /// <summary>The focus ring on the row the keyboard is on.</summary>
    public SKColor FocusRing { get; init; } = 0x66FFFFFF;

    public GridPalette()
    {
    }
}

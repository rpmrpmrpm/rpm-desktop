using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.DataGrid.Paint;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// Reads the grid's colours out of the app's own resource dictionaries.
///
/// <para><c>Themes/Generic.xaml</c> and <c>Themes/GridSelection.xaml</c> are the only place the
/// app's colours are written down. The painter takes a <see cref="GridPalette"/> of
/// <see cref="SKColor"/> values. This class is the only code that knows about both.</para>
///
/// <para>If a key is missing or is not a solid brush, the palette's own default is kept rather than
/// black. A renamed resource should leave the grid looking slightly wrong, not unreadable.</para>
/// </summary>
internal static class GridTheme
{
    public static GridPalette Read()
    {
        var p = new GridPalette();

        return p with
        {
            Surface = Colour("GridSurfaceBrush", p.Surface),
            RowBand = Colour("GridRowBandBrush", p.RowBand),
            RowSelected = Colour("TableViewRowBackgroundSelected", p.RowSelected),
            RowSelectedHover = Colour("TableViewRowBackgroundSelectedPointerOver", p.RowSelectedHover),
            SelectionBar = Colour("TableViewRowSelectionIndicatorBrush", p.SelectionBar),
            Rule = Colour("ListEdgeBrush", p.Rule),
            Header = Colour("CardTitleBarBrush", p.Header),
        };
    }

    private static SKColor Colour(string key, SKColor fallback)
    {
        if (Application.Current?.Resources is not { } resources)
            return fallback;

        if (!resources.TryGetValue(key, out var found))
            return fallback;

        return found switch
        {
            SolidColorBrush brush => Convert(brush.Color),
            Windows.UI.Color colour => Convert(colour),
            _ => fallback,
        };
    }

    private static SKColor Convert(Windows.UI.Color c) => new(c.R, c.G, c.B, c.A);
}

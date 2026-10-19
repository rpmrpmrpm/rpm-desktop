using System.Collections.Specialized;
using RPM.Desktop.Presentation.Main.Artists.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.Artists;

/// <summary>
/// The layout of the All tracks listing. Songs go in equal columns, and group headings span the full
/// width. Positions come from <see cref="ArtistTrackGrid"/>.
///
/// <para>Heights are fixed here, not measured. A row is <see cref="RowHeight"/> tall whatever its
/// content asks for. So the length of the listing never depends on which rows happen to be
/// realized.</para>
///
/// <para>Only the items inside the realization band are asked for on each pass. The repeater recycles
/// every element the layout did not ask for during that pass (<c>ItemsRepeater.MeasureOverride</c>
/// clears each layout-owned element that is an auto-recycle candidate and was not kept alive), so
/// nothing is held here between passes except the grid.</para>
/// </summary>
internal sealed partial class ArtistTrackLayout : VirtualizingLayout
{
    public double MinItemWidth { get; set; } = 290;

    public double ColumnSpacing { get; set; } = 6;

    public double RowHeight { get; set; } = 30;

    public double RowSpacing { get; set; } = 1;

    public double HeaderHeight { get; set; } = 40;

    private sealed class State
    {
        /// <summary>Null when the items changed since it was built.</summary>
        public ArtistTrackGrid? Grid;
        public int First = -1;
        public int Last = -1;
        public double Width;
        public double CellWidth;
    }

    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
        => context.LayoutState = new State();

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
        => context.LayoutState = null;

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        if (context.LayoutState is State state)
            state.Grid = null;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        if (context.LayoutState is not State state)
            context.LayoutState = state = new State();

        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : MinItemWidth;
        var columns = ArtistTrackGrid.Columns(width, MinItemWidth, ColumnSpacing);

        // Rebuilt only when the items or the column count changed. A scroll reuses it.
        var count = context.ItemCount;
        if (state.Grid is not { } grid || grid.Count != count || grid.ColumnCount != columns)
        {
            var headers = new bool[count];
            for (var i = 0; i < count; i++)
                headers[i] = context.GetItemAt(i) is ArtistSongHeading;
            state.Grid = grid = new ArtistTrackGrid(headers, columns, RowHeight, RowSpacing, HeaderHeight);
        }

        state.Width = width;
        state.CellWidth = (width - (columns - 1) * ColumnSpacing) / columns;

        var band = context.RealizationRect;
        (state.First, state.Last) = grid.ItemsIn(band.Top, band.Bottom);

        for (var i = state.First; i >= 0 && i <= state.Last; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            element.Measure(grid.Column(i) < 0
                ? new Size(width, HeaderHeight)
                : new Size(state.CellWidth, RowHeight));
        }

        return new Size(width, grid.Height);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (context.LayoutState is not State { Grid: { } grid } state)
            return finalSize;

        for (var i = state.First; i >= 0 && i <= state.Last && i < grid.Count; i++)
        {
            var element = context.GetOrCreateElementAt(i);
            var column = grid.Column(i);
            element.Arrange(column < 0
                ? new Rect(0, grid.Top(i), state.Width, HeaderHeight)
                : new Rect(column * (state.CellWidth + ColumnSpacing), grid.Top(i), state.CellWidth, RowHeight));
        }

        return finalSize;
    }
}

/// <summary>Picks the template for an item of the All tracks listing: a group heading or a song.</summary>
public sealed partial class ArtistTrackTemplates : DataTemplateSelector
{
    public DataTemplate? Song { get; set; }

    public DataTemplate? Heading { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is ArtistSongHeading ? Heading : Song;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

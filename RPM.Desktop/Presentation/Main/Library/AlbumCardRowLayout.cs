using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// One row of the album wall: its cards side by side, all of them, in order.
/// </summary>
/// <remarks>
/// <para><b>Why not a horizontal <c>StackLayout</c>.</b> A <c>StackLayout</c> virtualizes: it realizes only the
/// cards it believes are in view, from the effective viewport it is told about. That viewport arrives after
/// the layout pass that needs it. When the wall scrolls and lays itself out in the same turn (a pane move
/// keeping the reader's row in place, see <c>AlbumWallView.SettleNow</c>), a row that has just come into view
/// still believes it is out of view, and realizes one card. The row is drawn with one card for a frame, and
/// the cards' slide, which waits for every card in view, does not start.</para>
///
/// <para>Nothing is saved by virtualizing a row. The wall's own layout realizes a row only when the row is in
/// view or about to be, and then every card in it is in view. A virtualizing layout would also cost a measure
/// of every row on every frame of a pane move, because the repeater measures again whenever its viewport
/// changes.</para>
///
/// <para><b>The row's list is replaced, never changed in place.</b> A repeater with a non-virtualizing layout
/// does not give back the element of a card that is replaced or removed from its list. The element stays
/// realized under its old card, and stays on screen where it was last placed. After a re-deal the wall showed
/// the same record twice, and the slide, which waits for each card's own element, gave up. A new list makes
/// the repeater clear every element and realize the row again in the same pass. See
/// <c>AlbumRow.Deal</c>.</para>
///
/// <para>A card sets its own width (<c>Metrics.Stride</c>), so a card is measured with unlimited width, as a
/// horizontal <c>StackLayout</c> measures it, and placed at the width it asks for.</para>
/// </remarks>
internal sealed partial class AlbumCardRowLayout : NonVirtualizingLayout
{
    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        var room = new Size(double.PositiveInfinity, availableSize.Height);
        double width = 0, height = 0;
        foreach (var child in context.Children)
        {
            child.Measure(room);
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        double x = 0;
        foreach (var child in context.Children)
        {
            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}

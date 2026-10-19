using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Scrolls the element that has the TV remote's focus into view, in every scroller above it, up to
/// Pegasus's own root (see PegasusPlayer.OnRingFrame).
///
/// <para><b>It sets each scroller's new offset.</b> Do not replace this with StartBringIntoView. On a long
/// track list on a record or list page, StartBringIntoView moved nothing: the focus ring went below the
/// bottom of the screen and the list stayed at its top. Here each scroller is given a new offset that is
/// worked out from where the element stands now.</para>
///
/// <para><b>Down the screen: centred, unless the move stays in the same row.</b> A move to another row puts
/// the element in the middle of the scroller's height, so there is content on both sides of it. A move
/// along the row (the new element has some height in common with the last one) scrolls the height only if
/// the element would be cut off. Centring on every sideways step moved the whole of home a little each time
/// between two cards of the same height. Across, a scroller only moves enough to show the element.</para>
/// </summary>
public static class RemoteScroll
{
    /// <summary>The space kept between an element and the scroller's edge when the element is only
    /// revealed, in points.</summary>
    private const double Air = 24;

    /// <summary>
    /// Scrolls <paramref name="target"/> into view in every scroller between it and
    /// <paramref name="stopAt"/>. When <paramref name="keepHeight"/> is true, the height moves only if the
    /// element would be cut off. Use it for a move along a row.
    /// </summary>
    public static void IntoView(FrameworkElement target, UIElement stopAt, bool keepHeight)
    {
        for (var node = VisualTreeHelper.GetParent(target);
             node is not null && !ReferenceEquals(node, stopAt);
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollViewer list)
                continue;

            Rect at;
            try
            {
                at = target.TransformToVisual(list).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
            }
            catch (ArgumentException)
            {
                return;   // the element was removed from the tree after this frame started
            }

            double? across = null, down = null;
            if (list.ScrollableWidth > 0)
            {
                var to = Revealed(list.HorizontalOffset, at.Left, at.Width, list.ViewportWidth, list.ScrollableWidth);
                if (Math.Abs(to - list.HorizontalOffset) > 0.5)
                    across = to;
            }
            if (list.ScrollableHeight > 0)
            {
                var to = keepHeight
                    ? Revealed(list.VerticalOffset, at.Top, at.Height, list.ViewportHeight, list.ScrollableHeight)
                    : Centred(list.VerticalOffset, at.Top, at.Height, list.ViewportHeight, list.ScrollableHeight);
                if (Math.Abs(to - list.VerticalOffset) > 0.5)
                    down = to;
            }

            if (across is not null || down is not null)
                list.ChangeView(across, down, null, disableAnimation: false);
        }
    }

    /// <summary>The offset that puts an element at <paramref name="start"/> (in viewport points, at the
    /// current <paramref name="offset"/>) in the middle of the viewport. The result stays within what the
    /// scroller can reach.</summary>
    internal static double Centred(double offset, double start, double length, double viewport, double scrollable) =>
        Math.Clamp(offset + start + (length / 2) - (viewport / 2), 0, scrollable);

    /// <summary>The offset nearest the current one that shows the whole element, with a little space at the
    /// edge it was cut off at. The result stays within what the scroller can reach. If the element is
    /// already wholly in view, the current offset is returned.</summary>
    internal static double Revealed(double offset, double start, double length, double viewport, double scrollable)
    {
        var to = offset;
        if (start < Air)
            to = offset + start - Air;
        else if (start + length > viewport - Air)
            to = offset + start + length - viewport + Air;
        return Math.Clamp(to, 0, scrollable);
    }
}

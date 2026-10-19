using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Moves a ScrollViewer's own vertical bar down from the top of the scroller, so that the bar starts level
/// with the first row of a screen. That screen's content runs up under something covering its top,
/// as Pegasus's home and wall do under the mini player.
///
/// <para>The bar is part of the scroller's template, and the template exists only after the scroller has
/// been laid out. So a caller must set the top when the figure changes, AND from the scroller's Loaded
/// and SizeChanged events. Whichever comes last finds the bar and writes it. If it is called before the
/// template exists, it does nothing.</para>
/// </summary>
internal static class ScrollerBar
{
    /// <summary>Moves the top of <paramref name="scroller"/>'s vertical bar down by <paramref
    /// name="top"/> points. The margin is written only when it changes, so this can be called on every
    /// size change.</summary>
    public static void StartAt(ScrollViewer scroller, double top)
    {
        if (VerticalBar(scroller) is not { } bar)
            return;

        var m = bar.Margin;
        if (Math.Abs(m.Top - top) > 0.5)
            bar.Margin = new Thickness(m.Left, top, m.Right, m.Bottom);
    }

    /// <summary>The scroller's own vertical bar: the first vertical ScrollBar in its template. The search
    /// does not go into the content, because scrollers inside the content have bars of their own. Null
    /// before the template is applied.</summary>
    private static ScrollBar? VerticalBar(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollBar { Orientation: Orientation.Vertical } bar)
                return bar;
            if (child is not ScrollContentPresenter && VerticalBar(child) is { } found)
                return found;
        }

        return null;
    }
}

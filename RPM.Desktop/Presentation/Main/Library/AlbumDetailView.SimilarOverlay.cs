using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The suggestions row on a card too short to make room for it: shut whenever a record opens, and when it is
/// opened, it slides up OVER the bottom of the page on smoked glass, instead of taking room from the panels
/// above it.
/// </summary>
/// <remarks>
/// <para><b>Which cards.</b> The ones on which the page, left to itself, would keep the row shut
/// (<see cref="AlbumPageDensity.HoldsOpenSimilar"/>), and which do not scroll. A page that scrolls has room
/// below for anything, and a tall card lays the row out as it always has.</para>
///
/// <para><b>How it lies over the page without moving it.</b> The page is laid out shut throughout. While the
/// row is up, the block keeps the height it has when shut: its top margin is made smaller by exactly the
/// row's height (<see cref="OverlayLift"/>), so the heading stands that much higher and the row fills the
/// space it left, over whatever is above. Nothing else is measured again. The block and its backdrop are
/// drawn above the columns (Canvas.ZIndex), and the slide is a render transform, which the compositor runs.
/// The page's own scroller clips the row while it is still below the card's foot.</para>
/// </remarks>
public sealed partial class AlbumDetailView
{
    /// <summary>Whether the row is up over the page.</summary>
    private bool _similarOverlay;

    /// <summary>Whether the row is on its way down. It is still drawn until it lands.</summary>
    private bool _overlayClosing;

    /// <summary>How far the heading stands above its shut place while the row is up: the row and the gap
    /// above it.</summary>
    private double _overlayLift;

    /// <summary>The air above the block when it is shut (see ApplyStacking, which writes it).</summary>
    private double _similarAir;

    private Storyboard? _overlayMotion;

    /// <summary>The slide's length, each way.</summary>
    private static readonly TimeSpan OverlaySlide = TimeSpan.FromMilliseconds(260);

    /// <summary>
    /// The room inside the glass around the heading and the sleeves while the row lies over the page. The
    /// glass itself stands on the edges of the cards above it (see <see cref="PlaceSimilarOverlay"/>), so it
    /// lines up with them; the row is drawn this far in from the glass's hairline.
    /// </summary>
    /// <remarks>Left and right are the cards' own inner room. The sleeves carry eight points of their own
    /// on each side (the scroller is pulled out by that much, see the markup), so a sleeve's picture starts
    /// on the heading's line, and its hover wash stays clear of the hairline.</remarks>
    private static readonly Thickness OverlayPad = new(16, 12, 16, 6);

    /// <summary>Whether this card lays the row over the page rather than making room for it.</summary>
    private bool SimilarOverlays =>
        _hasSimilar
        && !(GrooveStacked || _papersStacked)
        && !AlbumPageDensity.HoldsOpenSimilar(ActualHeight, _rung);

    /// <summary>How much higher the block's top stands than when it is shut: the row, the gap above it, and
    /// the room the glass keeps above and below (<see cref="OverlayPad"/>), so the block's footprint in the
    /// page stays the shut one.</summary>
    private double OverlayLift => _similarOverlay ? _overlayLift + OverlayPad.Top + OverlayPad.Bottom : 0;

    private void ToggleSimilarOverlay()
    {
        if (_similarOverlay && !_overlayClosing)
            CloseSimilarOverlay();
        else
            OpenSimilarOverlay();
    }

    private void OpenSimilarOverlay()
    {
        var from = _similarOverlay ? Shift(SimilarBlock).Y : double.NaN;
        _overlayMotion?.Stop();
        _overlayClosing = false;

        if (!_similarOverlay)
        {
            // The row's height, measured before it is drawn: the lift is the whole of it and the gap above.
            SimilarScroller.Visibility = Visibility.Visible;
            SimilarScroller.Measure(new Windows.Foundation.Size(
                Math.Max(1, SimilarBlock.ActualWidth - OverlayPad.Left - OverlayPad.Right), double.PositiveInfinity));
            _overlayLift = SimilarScroller.DesiredSize.Height + SimilarBlock.Spacing;
            _similarOverlay = true;
            from = OverlayLift;
        }

        PointSimilarChevron(true);
        PlaceSimilarOverlay();
        SlideOverlay(from, 0, landed: null);
    }

    private void CloseSimilarOverlay()
    {
        var from = Shift(SimilarBlock).Y;
        _overlayMotion?.Stop();
        _overlayClosing = true;
        PointSimilarChevron(false);

        SlideOverlay(from, OverlayLift, landed: DropSimilarOverlay);
    }

    /// <summary>The row back to shut at once, with no slide: a new record, a card that grew, or the end of
    /// a slide down.</summary>
    private void DropSimilarOverlay()
    {
        _overlayMotion?.Stop();
        _overlayMotion = null;
        var was = _similarOverlay;
        _similarOverlay = false;
        _overlayClosing = false;
        Shift(SimilarBlock).Y = 0;
        Shift(SimilarBackdrop).Y = 0;

        if (!was)
            return;

        if (_similarRow != SimilarState.Open)
        {
            SimilarScroller.Visibility = Visibility.Collapsed;
            PointSimilarChevron(false);
        }
        PlaceSimilarOverlay();
    }

    /// <summary>
    /// The block's top margin, its place above the columns, and the backdrop under it, for the state the row
    /// is in. Safe to run on any layout pass: it writes only what has changed.
    /// </summary>
    private void PlaceSimilarOverlay()
    {
        // Measured again on each pass while the row is up, because the sleeves are sized to the card's width
        // and a resize changes the row's height. Not during a slide, which is drawn from the lift it began
        // with.
        if (_similarOverlay && _overlayMotion is null && SimilarScroller.ActualHeight > 0)
            _overlayLift = SimilarScroller.ActualHeight + SimilarBlock.Spacing;

        var margin = SimilarBlock.Margin;
        var top = _similarAir - OverlayLift;
        if (Math.Abs(margin.Top - top) > 0.5)
            SimilarBlock.Margin = new Thickness(margin.Left, top, margin.Right, margin.Bottom);

        var pad = _similarOverlay ? OverlayPad : default;
        if (SimilarBlock.Padding != pad)
            SimilarBlock.Padding = pad;

        Canvas.SetZIndex(SimilarBlock, _similarOverlay ? 5 : 0);
        SimilarSeam.Opacity = _similarOverlay ? 0 : 1;
        SimilarBackdrop.Visibility = _similarOverlay ? Visibility.Visible : Visibility.Collapsed;
        // The glass ends where the cards above it end: the column's left edge, and on the right the scroll
        // lane short of the column (see ApplyStacking, where the row's sleeves are put on the same line).
        // The row only lies over a page that does not scroll, which always keeps the lane.
        if (_similarOverlay)
        {
            margin = SimilarBlock.Margin;
            SimilarBackdrop.Margin = new Thickness(margin.Left, margin.Top, margin.Right + ScrollLane, margin.Bottom);
        }
    }

    /// <summary>The block and its backdrop slide together, from <paramref name="from"/> points below their
    /// laid-out place to <paramref name="to"/>.</summary>
    private void SlideOverlay(double from, double to, Action? landed)
    {
        var board = new Storyboard();
        foreach (var element in new FrameworkElement[] { SimilarBlock, SimilarBackdrop })
        {
            var move = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(OverlaySlide),
                EasingFunction = new CubicEase { EasingMode = to < from ? EasingMode.EaseOut : EasingMode.EaseIn },
            };
            Storyboard.SetTarget(move, Shift(element));
            Storyboard.SetTargetProperty(move, "Y");
            board.Children.Add(move);
        }

        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_overlayMotion, board))
                return;
            _overlayMotion = null;

            // A stopped storyboard puts the value back, so the end is written by hand.
            board.Stop();
            Shift(SimilarBlock).Y = to;
            Shift(SimilarBackdrop).Y = to;
            landed?.Invoke();
            PlaceSimilarOverlay();
        };

        _overlayMotion = board;
        board.Begin();
    }

    /// <summary>The element's own slide. Made here rather than in the markup, and made again when something
    /// else has replaced it: the Skia head's SimilarSlide sets and clears the block's render transform.</summary>
    private static TranslateTransform Shift(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform shift)
            return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }
}

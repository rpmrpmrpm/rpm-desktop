using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

using Windows.Foundation;

namespace RPM.Desktop.Presentation.Coachmarks;

/// <summary>
/// Draws a first-run hint: a caption bubble and a curved arrow from it to a live control.
///
/// <para><b>Geometry is read from the controls, not stored.</b> Every callout is placed from the
/// target's bounds at the moment it is drawn (<see cref="UIElement.TransformToVisual"/>), so the
/// caller clears and rebuilds on any layout change and the arrow follows whatever the layout did
/// with its target. Nothing here remembers where anything was.</para>
///
/// <para><b>The host canvas carries no background</b>, so pointer events fall through to the
/// controls underneath. The bubble itself is not hit-testable either: the dismiss button is the one
/// piece of a callout that can be clicked, and the app behind it goes on working normally.</para>
/// </summary>
public sealed class CoachmarkPainter
{
    private readonly Canvas _canvas;
    private readonly FrameworkElement _clampArea;

    /// <param name="canvas">The canvas the shapes go into.</param>
    /// <param name="clampArea">
    /// What limits a bubble's placement. Pass the OVERLAY here, not the Canvas. A Canvas measures to zero,
    /// so its ActualWidth and ActualHeight are zero. Each bubble would then clamp into the top-left
    /// corner.
    /// </param>
    public CoachmarkPainter(Canvas canvas, FrameworkElement clampArea)
    {
        _canvas = canvas;
        _clampArea = clampArea;
    }

    /// <summary>The margin a bubble keeps from the edge of the overlay.</summary>
    private const double Edge = 14;

    /// <summary>The app's cyan accent brush. It is looked up from the application resources on each
    /// use.</summary>
    private static Brush Accent => (Brush)Application.Current.Resources["RpmCyanBrush"];

    /// <summary>Take every callout off. Called before a rebuild.</summary>
    public void Clear() => _canvas.Children.Clear();

    private bool _shown;
    private Storyboard? _fade;

    /// <summary>
    /// Fade the overlay in. Does nothing when it is already in, so the rebuilds that follow a
    /// resize or a scroll do not restart the animation: only a real hidden-to-shown change
    /// animates.
    /// </summary>
    public void Show()
    {
        if (_shown)
            return;
        _shown = true;
        Animate(to: 1.0);
    }

    /// <summary>
    /// Fade the overlay out.
    ///
    /// <para>The overlay stays VISIBLE, transparent and with its canvas cleared, rather than
    /// collapsing. A collapsed overlay measures zero. That breaks the bubble clamp on the next Show,
    /// and puts that hint in the corner. An empty overlay with no background takes no input, so
    /// leaving it up costs nothing.</para>
    /// </summary>
    public void Hide()
    {
        if (!_shown)
            return;
        _shown = false;
        Animate(to: 0.0);
    }

    private void Animate(double to)
    {
        // Read the live opacity BEFORE stopping the last storyboard, so a fade interrupted halfway
        // carries on from what is on screen instead of jumping.
        var from = _clampArea.Opacity;
        _fade?.Stop();

        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(320)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, _clampArea);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) => _clampArea.Opacity = to;   // settle, so a later Stop cannot snap it
        _fade = sb;
        sb.Begin();
    }

    /// <summary>
    /// Where <paramref name="target"/> is, in the canvas's coordinates, or null when it is not laid
    /// out or is collapsed. Null is the caller's cue to skip a callout that would point at nothing.
    /// </summary>
    public Rect? TryRect(FrameworkElement? target)
    {
        // A COLLAPSED element is not zero-sized, so checking the size is not enough. An element with an
        // explicit Width and Height keeps reporting them while it is collapsed. So a hint would draw a
        // bubble and an arrow to a control that nobody can see. The artwork grip is an example. It is a
        // 54 by 7 pill that is shown only while the pointer is on its strip.
        if (target is null || target.Visibility != Visibility.Visible
            || target.ActualWidth < 1 || target.ActualHeight < 1)
            return null;

        try
        {
            var origin = target.TransformToVisual(_canvas).TransformPoint(new Point(0, 0));
            return new Rect(origin.X, origin.Y, target.ActualWidth, target.ActualHeight);
        }
        catch
        {
            // TransformToVisual throws when the two are not in one tree yet, which happens before
            // the first arrange and during teardown. Both mean "not ready", which is what null says.
            return null;
        }
    }

    /// <summary>Where the arrow leaves the bubble.</summary>
    public enum ArrowAnchor
    {
        /// <summary>The point on the bubble's rim nearest the target.</summary>
        Auto,

        /// <summary>The middle of the bubble's bottom edge.</summary>
        BottomCenter,

        /// <summary>The middle of the bubble's left edge.</summary>
        LeftCenter,

        /// <summary>The middle of the bubble's right edge.</summary>
        RightCenter,
    }

    /// <summary>
    /// A caption bubble with its top-left at <paramref name="bubbleAt"/>, and an arrow from it to
    /// <paramref name="arrowTo"/>.
    /// </summary>
    /// <param name="onDismiss">Called when the dismiss button is pressed. Without one the callout
    /// has no dismiss button and is entirely click-through.</param>
    /// <param name="anchor">Which part of the bubble the arrow leaves from.</param>
    /// <param name="flipArch">Bows the arrow the other way. Use it where the default bow would curve the
    /// arrow through the control it points at.</param>
    public void AddCallout(
        string caption, Point bubbleAt, Point arrowTo, double bubbleWidth,
        Action? onDismiss = null,
        ArrowAnchor anchor = ArrowAnchor.Auto, bool flipArch = false)
    {
        var bubble = BuildBubble(caption, bubbleWidth, out var bw, out var bh);

        // Clamped against the overlay, which has a size; see the constructor.
        var areaW = _clampArea.ActualWidth > 1 ? _clampArea.ActualWidth : double.MaxValue;
        var areaH = _clampArea.ActualHeight > 1 ? _clampArea.ActualHeight : double.MaxValue;
        var left = Math.Max(Edge, Math.Min(bubbleAt.X, areaW - bw - Edge));
        var top = Math.Max(Edge, Math.Min(bubbleAt.Y, areaH - bh - Edge));
        Canvas.SetLeft(bubble, left);
        Canvas.SetTop(bubble, top);

        var rect = new Rect(left, top, bw, bh);
        var arrowFrom = anchor switch
        {
            ArrowAnchor.BottomCenter => new Point(left + bw / 2, top + bh),
            ArrowAnchor.LeftCenter => new Point(left, top + bh / 2),
            ArrowAnchor.RightCenter => new Point(left + bw, top + bh / 2),
            _ => NearestEdgePoint(rect, arrowTo),
        };
        DrawArrow(arrowFrom, arrowTo, Accent, flipArch);

        _canvas.Children.Add(bubble);

        // Added last, so that it sits over the bubble. It is the only part that takes clicks, so a click
        // anywhere else on the callout reaches the app.
        if (onDismiss is not null)
            AddDismissPill(left, top, onDismiss);
    }

    /// <summary>
    /// The caption bubble, measured so the caller can clamp it and anchor the arrow to it. Not
    /// hit-testable; see <see cref="AddDismissPill"/>.
    /// </summary>
    private static Border BuildBubble(string caption, double bubbleWidth, out double bw, out double bh)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = caption,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = new SolidColorBrush(Colors.White),
        });

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF7, 0x0E, 0x15, 0x20)),
            BorderBrush = Accent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(15, 11, 15, 11),
            MaxWidth = bubbleWidth,
            IsHitTestVisible = false,
            Child = content,
        };

        bubble.Measure(new Size(bubbleWidth, double.PositiveInfinity));
        bw = bubble.DesiredSize.Width;
        bh = bubble.DesiredSize.Height;
        return bubble;
    }

    /// <summary> The dismiss button: a small round disc over the bubble's top-left corner, and the only
    /// part of a callout that takes a click. It is a real Button, styled with BareButtonStyle so that only
    /// the disc is drawn. So the keyboard and a screen reader can also reach it and press it. A plain
    /// border would respond to a tap only. The button has an accessible name and a tooltip, from the
    /// Coach_Dismiss string.
    /// </summary>
    private void AddDismissPill(double left, double top, Action onDismiss)
    {
        const double pill = 24;
        var close = new Button
        {
            Style = (Style)Application.Current.Resources["BareButtonStyle"],
            Width = pill,
            Height = pill,
            CornerRadius = new CornerRadius(pill / 2),
            Content = new Border
            {
                CornerRadius = new CornerRadius(pill / 2),
                Background = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x0E, 0x15, 0x20)),
                BorderBrush = Accent,
                BorderThickness = new Thickness(1.5),
                Child = new TextBlock
                {
                    Text = "\uf00d",   // Font Awesome's times
                    FontFamily = new FontFamily(
                        "ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid"),
                    FontSize = 11,
                    Foreground = Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        close.Click += (_, _) => onDismiss();
        IconName.Set(close, Strings.Loc.Get("Coach_Dismiss"));

        Canvas.SetLeft(close, left - pill / 2);
        Canvas.SetTop(close, top - pill / 2);
        _canvas.Children.Add(close);
    }

    /// <summary>The point on the rim of <paramref name="rect"/> closest to
    /// <paramref name="toward"/>.</summary>
    private static Point NearestEdgePoint(Rect rect, Point toward)
    {
        var cx = Math.Clamp(toward.X, rect.Left, rect.Right);
        var cy = Math.Clamp(toward.Y, rect.Top, rect.Bottom);
        var dl = Math.Abs(cx - rect.Left);
        var dr = Math.Abs(cx - rect.Right);
        var dt = Math.Abs(cy - rect.Top);
        var db = Math.Abs(cy - rect.Bottom);
        var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

        if (min == dl) return new Point(rect.Left, cy);
        if (min == dr) return new Point(rect.Right, cy);
        if (min == dt) return new Point(cx, rect.Top);
        return new Point(cx, rect.Bottom);
    }

    /// <summary>
    /// The arrow: one quadratic curve from <paramref name="start"/> to <paramref name="end"/> with a
    /// small bow across it, and a filled triangle at the target.
    /// </summary>
    private void DrawArrow(Point start, Point end, Brush brush, bool flipArch)
    {
        var mx = (start.X + end.X) / 2;
        var my = (start.Y + end.Y) / 2;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));

        // The bow grows with the run but stays small on a short one, so a nearly straight arrow
        // reads as a line rather than a cramped hook.
        var bow = Math.Clamp(len * 0.16, 2, 40) * (flipArch ? -1.0 : 1.0);
        var ctrl = new Point(mx + (-dy / len) * bow, my + (dx / len) * bow);

        // The curve stops short of the target so the head's tip lands on it, instead of the line
        // running through the head and out the other side.
        const double headLen = 14.0;
        var tdx = end.X - ctrl.X;
        var tdy = end.Y - ctrl.Y;
        var tlen = Math.Max(1, Math.Sqrt(tdx * tdx + tdy * tdy));
        var ux = tdx / tlen;
        var uy = tdy / tlen;
        var lineEnd = new Point(end.X - ux * (headLen * 0.6), end.Y - uy * (headLen * 0.6));

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = ctrl, Point2 = lineEnd });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);

        _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = brush,
            StrokeThickness = 2.5,
            IsHitTestVisible = false,
        });

        var px = -uy;
        var py = ux;
        var baseCenter = new Point(end.X - ux * headLen, end.Y - uy * headLen);
        var head = new Polygon { Fill = brush, IsHitTestVisible = false };
        head.Points.Add(end);
        head.Points.Add(new Point(baseCenter.X + px * (headLen * 0.55), baseCenter.Y + py * (headLen * 0.55)));
        head.Points.Add(new Point(baseCenter.X - px * (headLen * 0.55), baseCenter.Y - py * (headLen * 0.55)));
        _canvas.Children.Add(head);
    }
}

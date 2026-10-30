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

    /// <summary>
    /// What a hint is drawn with (the bubble, its arrow, its buttons, the ring round the lit section), on
    /// a layer of its own over the dim and the sheet. So a hint can fade in on its own (<see cref="FadeIn"/>)
    /// while the dim stays where it is: fading the whole overlay would lift the dim off the app for a
    /// moment at every step.
    /// </summary>
    private readonly Canvas _marks = new();

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
        _canvas.Children.Add(_marks);
    }

    /// <summary>The margin a bubble keeps from the edge of the overlay.</summary>
    private const double Edge = 14;

    /// <summary>The app's cyan accent brush. It is looked up from the application resources on each
    /// use.</summary>
    private static Brush Accent => (Brush)Application.Current.Resources["RpmCyanBrush"];

    /// <summary>Take every callout off. Called before a rebuild.</summary>
    public void Clear()
    {
        _marks.Children.Clear();
        _canvas.Children.Clear();
        _canvas.Children.Add(_marks);
    }

    private Storyboard? _markFade;

    /// <summary>Fades the hint just drawn in, for a step of the tour that is not the one before it. The
    /// redraws that follow a resize or a scroll do not call it, so they do not flicker.</summary>
    public void FadeIn()
    {
        _markFade?.Stop();
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, _marks);
        Storyboard.SetTargetProperty(anim, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) => _marks.Opacity = 1;
        _markFade = sb;
        sb.Begin();
    }

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

    /// <summary>The dim laid over everything but the section a hint is about.</summary>
    private static readonly Windows.UI.Color Dim = ColorHelper.FromArgb(0xA6, 0x02, 0x05, 0x0A);

    /// <summary>
    /// Dims the window except <paramref name="lit"/>, which is ringed in the accent, so the eye goes to the
    /// section the hint is about. Drawn under the hint, and click-through like the rest of the overlay: the
    /// section it lights is usually the one the hint asks to be pressed.
    /// </summary>
    /// <param name="lit">The section, in the canvas's coordinates (see <see cref="TryRect"/>).</param>
    /// <param name="pad">How far the lit area reaches past the section on every side.</param>
    public void AddSpotlight(Rect lit, double pad = 6)
    {
        var w = _clampArea.ActualWidth;
        var h = _clampArea.ActualHeight;
        if (w < 1 || h < 1)
            return;

        var hole = new Rect(lit.X - pad, lit.Y - pad, lit.Width + 2 * pad, lit.Height + 2 * pad);

        // The whole window and the hole in it, filled even-odd: everything but the hole is dimmed.
        var shape = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shape.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, w, h) });
        shape.Children.Add(new RectangleGeometry { Rect = hole });

        _canvas.Children.Insert(0, new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = shape,
            Fill = new SolidColorBrush(Dim),
            IsHitTestVisible = false,
        });

        var ring = new Border
        {
            Width = hole.Width,
            Height = hole.Height,
            CornerRadius = new CornerRadius(8),
            BorderBrush = Accent,
            BorderThickness = new Thickness(1.5),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, hole.X);
        Canvas.SetTop(ring, hole.Y);
        _marks.Children.Insert(0, ring);
    }

    /// <summary>
    /// The end of the tour: the window dimmed, and a card in the middle of it with a heading, a line and a
    /// button. Unlike a hint it takes the clicks, so the app waits for the button.
    /// </summary>
    public void AddFinale(string title, string body, string button, Action onClose)
    {
        var w = _clampArea.ActualWidth;
        var h = _clampArea.ActualHeight;
        if (w < 1 || h < 1)
            return;

        _canvas.Children.Insert(0, new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Dim),
        });

        var close = new Button
        {
            Style = (Style)Application.Current.Resources["GlassPrimaryButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(28, 0, 28, 0),
            Content = button,
        };
        close.Click += (_, _) => onClose();

        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.Resources["RpmDisplayFontBold"],
            FontSize = 30,
            Foreground = new SolidColorBrush(Colors.White),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 14,
            LineHeight = 22,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xC4, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        close.Margin = new Thickness(0, 10, 0, 0);
        content.Children.Add(close);

        var card = new Border
        {
            Width = 460,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0xF7, 0x0E, 0x15, 0x20)),
            BorderBrush = Accent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(32, 28, 32, 26),
            Child = content,
        };
        card.Measure(new Size(460, double.PositiveInfinity));
        Canvas.SetLeft(card, (w - 460) / 2);
        Canvas.SetTop(card, Math.Max(Edge, (h - card.DesiredSize.Height) / 2));
        _marks.Children.Add(card);
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
    /// <param name="onNext">Called when the Next link under the bubble is pressed. Without one there is no
    /// Next: the hint is answered by pressing what it points at.</param>
    public void AddCallout(
        string caption, Point bubbleAt, Point arrowTo, double bubbleWidth,
        Action? onDismiss = null,
        ArrowAnchor anchor = ArrowAnchor.Auto, bool flipArch = false,
        Action? onNext = null)
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

        _marks.Children.Add(bubble);

        // Added last, so that it sits over the bubble. It is the only part that takes clicks, so a click
        // anywhere else on the callout reaches the app.
        if (onDismiss is not null)
            AddDismissPill(left, top, onDismiss);
        if (onNext is not null)
        {
            AddNext(left + bw, top + bh, onNext);
            AddBlocker();
        }
    }

    /// <summary>
    /// A clear sheet over the whole window, under the hint, that takes every click. A hint with a Next is
    /// moved on by its Next and nothing else: a press in the app behind it (opening a pane, switching a
    /// view) would change what the tour is pointing at from under it. Only the hint's own buttons, which
    /// are drawn over the sheet, still answer.
    /// </summary>
    private void AddBlocker()
    {
        var w = _clampArea.ActualWidth;
        var h = _clampArea.ActualHeight;
        if (w < 1 || h < 1)
            return;

        // A transparent brush, not none: an element with no fill is not hit-tested, so clicks would pass.
        _canvas.Children.Insert(0, new Rectangle
        {
            Width = w,
            Height = h,
            Fill = new SolidColorBrush(Colors.Transparent),
        });
    }

    /// <summary>"Next ›" under the bubble's bottom-right corner: a real button, like the dismiss disc, so the
    /// keyboard reaches it too.</summary>
    private void AddNext(double right, double bottom, Action onNext)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new TextBlock
        {
            Text = Strings.Loc.Get("Coach_Next"),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = "",   // Font Awesome's chevron-right
            FontFamily = new FontFamily("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid"),
            FontSize = 10,
            Foreground = Accent,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var next = new Button
        {
            Style = (Style)Application.Current.Resources["BareButtonStyle"],
            Padding = new Thickness(6, 4, 6, 4),
            Content = row,
        };
        next.Click += (_, _) => onNext();

        next.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(next, right - next.DesiredSize.Width);
        Canvas.SetTop(next, bottom + 8);
        _marks.Children.Add(next);
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
        _marks.Children.Add(close);
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

        _marks.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
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
        _marks.Children.Add(head);
    }
}

#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Mixing;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The volume arc, drawn and editable. It shows the whole fade across the width, the two level curves
/// over it, and the band along the bottom where the bass changes hands. Drag a point to move it. Drag
/// the edges of the band to move the handover. The deck colours match the timelines: amber for the
/// record leaving, and green for the record arriving. This makes the curve and its record read as one
/// thing.
///
/// <para>The editor never shows "no arc". With nothing drawn, it shows
/// <see cref="FadeArc.EqualPower"/>, which is exactly what the decks will do. So the first drag starts
/// from the real curve.</para>
/// </summary>
public sealed partial class MixArcEditor : Grid
{
    private const float Pad = 10;
    private const float BassLane = 14;
    private const float GrabPixels = 10;

    private readonly MixLabSurface? _surface;
    private readonly MixLabInputLayer _input = new();

    private readonly SKPaint _fill = new() { IsAntialias = true };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKFont _tiny = new() { Size = 10 };

    /// <summary>The record leaving. The colour is blue, not the amber that the timelines use for a
    /// mix-out. That amber is close to yellow. The handles that sit on a bar line are drawn in the
    /// yellow of the bar lines, so on an amber curve they disappeared. Blue and green are the two
    /// colours that yellow is easy to read against.</summary>
    private static readonly SKColor Outgoing = new(0x04, 0xC8, 0xFF);
    /// <summary>The record coming in.</summary>
    private static readonly SKColor Incoming = new(0x4C, 0xD9, 0x8A);
    private static readonly SKColor BassBand = new(0x5A, 0x8D, 0xFA);

    /// <summary>A handle that sits on a bar line. It is drawn in the yellow of the timelines' bar
    /// lines, so the dot is the same colour as the line under it.</summary>
    private static readonly SKColor Downbeat = new(0xF2, 0xC0, 0x4C);

    /// <summary>
    /// Whether a handle is on one of the changeover's bar lines.
    ///
    /// <para>This is measured in pixels, not in fractions of the fade. What the user judges is whether
    /// the dot sits on the line behind it. The same fraction looks like a touch across sixteen bars,
    /// but it looks like a clear miss across two.</para>
    /// </summary>
    private bool OnABar(double t)
    {
        if (_bars <= 0)
            return false;

        double bar = Math.Round(t * _bars);
        return bar >= 0 && bar <= _bars &&
               Math.Abs(XAt(t) - XAt(bar / _bars)) <= SnapPixels;
    }

    /// <summary>How near a bar line a handle must be dragged before it snaps to it: three pixels. This
    /// is about the accuracy of a hand that is aiming at the line.</summary>
    private const float SnapPixels = 3;

    private FadeArc _arc = FadeArc.EqualPower();
    private int _bars;
    private bool _enabled = true;

    private float _drawnWidth = 1, _drawnHeight = 1;

    // What the pointer holds: a point on one of the curves, or an edge of the bass band.
    private ArcSide _dragSide;
    private int _dragPoint = -1;
    private bool _dragBassStart, _dragBassEnd;
    private bool _touched;                      // whether this drag actually moved anything

    /// <summary>The arc being shown and edited. It is never null. When nothing is drawn, the arc is
    /// <see cref="FadeArc.EqualPower"/>.</summary>
    public FadeArc Arc
    {
        get => _arc;
        set
        {
            if (ReferenceEquals(_arc, value))
                return;
            _arc = value;
            Invalidate();
        }
    }

    /// <summary>How many bars the fade runs, for the bar lines behind the curves. Zero draws
    /// none.</summary>
    public int Bars
    {
        get => _bars;
        set
        {
            if (_bars == value)
                return;
            _bars = value;
            Invalidate();
        }
    }

    /// <summary>Whether the arc answers to the pointer. It is always drawn.</summary>
    public bool IsEditable
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Invalidate();
        }
    }

    /// <summary>Raised while a handle is being dragged, with the arc as it now is.</summary>
    public event Action<FadeArc>? ArcMoved;

    /// <summary>Raised when the hand lets go and the arc has really changed. This is the moment to plan
    /// again and to write one journal line, not one line for every pointer move.</summary>
    public event Action<FadeArc>? ArcCommitted;

    public MixArcEditor()
    {
        MinHeight = 110;

        if (SKCanvasElement.IsSupportedOnCurrentPlatform())
        {
            _surface = new MixLabSurface { Draw = Render };
            Children.Add(_surface);
        }

        Children.Add(_input);
        _input.PointerPressed += OnPressed;
        _input.PointerMoved += OnMoved;
        _input.PointerReleased += OnReleased;
        _input.PointerCaptureLost += (_, _) => EndDrag(commit: true);
        SizeChanged += (_, _) => Invalidate();
        Unloaded += (_, _) =>
        {
            _fill.Dispose();
            _line.Dispose();
            _tiny.Dispose();
        };
    }

    private void Invalidate() => _surface?.Invalidate();

    // ── drawing ───────────────────────────────────────────────────────────────

    private void Render(SKCanvas canvas, Size area)
    {
        float w = (float)area.Width, h = (float)area.Height;
        _drawnWidth = Math.Max(1, w);
        _drawnHeight = Math.Max(1, h);

        canvas.Clear(new SKColor(0x14, 0x16, 0x1A));
        if (w < 20 || h < 30)
            return;

        float top = Pad, bottom = h - Pad - BassLane;
        float height = bottom - top;

        // Bar lines, so "pull it out on the twelfth bar" is a place on this strip.
        if (_bars > 0)
        {
            _line.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x16);
            _line.StrokeWidth = 1;
            for (int bar = 1; bar < _bars; bar++)
            {
                float x = XAt((double)bar / _bars);
                canvas.DrawLine(x, top, x, bottom, _line);
            }
        }

        // The bass handover band along the bottom. The part to the left of it belongs to the record on
        // the left, the part to the right belongs to the record on the right, and the part between the
        // edges belongs to the crossing.
        float bandTop = bottom + 4;
        float x0 = XAt(_arc.BassSwapStart), x1 = XAt(_arc.BassSwapEnd);
        _fill.Color = BassBand.WithAlpha(0x33);
        canvas.DrawRect(x0, bandTop, Math.Max(1, x1 - x0), BassLane - 4, _fill);
        _line.Color = BassBand;
        _line.StrokeWidth = _dragBassStart ? 3 : 2;
        canvas.DrawLine(x0, bandTop, x0, bandTop + BassLane - 4, _line);
        _line.StrokeWidth = _dragBassEnd ? 3 : 2;
        canvas.DrawLine(x1, bandTop, x1, bandTop + BassLane - 4, _line);
        _fill.Color = BassBand.WithAlpha(0xAA);
        canvas.DrawText("bass", x0 + 3, bandTop + BassLane - 7, SKTextAlign.Left, _tiny, _fill);

        // The two curves, sampled onto the strip. The points that are drawn sit on top of the curves as
        // handles.
        DrawCurve(canvas, _arc.Outgoing, Outgoing, top, height, ArcSide.Outgoing);
        DrawCurve(canvas, _arc.Incoming, Incoming, top, height, ArcSide.Incoming);

        DrawBarCount(canvas, w, top);
    }

    /// <summary>
    /// The names of the columns are shown at their ends. The two curves are named outside this control,
    /// on the strip's own row. Both top corners are always covered by a curve at full level. So a
    /// legend drawn inside would sit on top of the thing it describes.
    /// </summary>
    private void DrawBarCount(SKCanvas canvas, float w, float top)
    {
        if (_bars <= 0)
            return;

        string bars = $"{_bars} bars";
        float width = _tiny.MeasureText(bars);
        float plateTop = top + 2;

        _fill.Color = new SKColor(0x14, 0x16, 0x1A, 0xDD);
        canvas.DrawRoundRect(new SKRect(w - width - 8, plateTop, w - 2, plateTop + 15), 3, 3, _fill);
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x77);
        canvas.DrawText(bars, w - 5, plateTop + 10.5f, SKTextAlign.Right, _tiny, _fill);
    }


    private void DrawCurve(
        SKCanvas canvas, IReadOnlyList<ArcPoint> points, SKColor color, float top, float height, ArcSide side)
    {
        if (points.Count == 0)
            return;

        _line.Color = _enabled ? color : color.WithAlpha(0x88);
        _line.StrokeWidth = 2;

        using var path = new SKPath();
        path.MoveTo(XAt(0), YAt(points[0].Level, top, height));
        foreach (var point in points)
            path.LineTo(XAt(point.T), YAt(point.Level, top, height));
        path.LineTo(XAt(1), YAt(points[^1].Level, top, height));
        canvas.DrawPath(path, _line);

        for (int i = 0; i < points.Count; i++)
        {
            bool hot = _enabled && _dragPoint == i && _dragSide == side;

            // A change is marked on a bar line. A level that changes on a downbeat sounds intended to
            // the listener. A level that changes two thirds of the way through a bar sounds like a hand
            // slipping.
            _fill.Color = hot ? SKColors.White : OnABar(points[i].T) ? Downbeat : color;
            canvas.DrawCircle(XAt(points[i].T), YAt(points[i].Level, top, height), hot ? 5 : 3.5f, _fill);
        }
    }

    private float XAt(double t) => Pad + (float)Math.Clamp(t, 0, 1) * (_drawnWidth - 2 * Pad);

    private static float YAt(double level, float top, float height) =>
        top + (float)(1 - Math.Clamp(level, 0, 1)) * height;

    private double TAt(double x) => Math.Clamp((x - Pad) / Math.Max(1, _drawnWidth - 2 * Pad), 0, 1);

    private double LevelAt(double y)
    {
        float top = Pad, bottom = _drawnHeight - Pad - BassLane;
        return Math.Clamp(1 - (y - top) / Math.Max(1, bottom - top), 0, 1);
    }

    // ── the pointer ───────────────────────────────────────────────────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_enabled)
            return;

        var at = e.GetCurrentPoint(_input).Position;
        float top = Pad, bottom = _drawnHeight - Pad - BassLane;

        // The edges of the bass band are checked first. They sit in their own lane, under the curves.
        if (at.Y > bottom)
        {
            double dx0 = Math.Abs(at.X - XAt(_arc.BassSwapStart));
            double dx1 = Math.Abs(at.X - XAt(_arc.BassSwapEnd));
            if (Math.Min(dx0, dx1) <= GrabPixels)
            {
                _dragBassStart = dx0 <= dx1;
                _dragBassEnd = !_dragBassStart;
                _touched = false;
                _input.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }

        // Otherwise, the nearest drawn point on either curve, if it is close enough to count.
        var (side, index, distance) = Nearest(at, top, bottom - top);
        if (index >= 0 && distance <= GrabPixels)
        {
            _dragSide = side;
            _dragPoint = index;
            _touched = false;
            _input.CapturePointer(e.Pointer);
            Invalidate();
            e.Handled = true;
        }
    }

    private (ArcSide Side, int Index, double Distance) Nearest(Point at, float top, float height)
    {
        ArcSide side = ArcSide.Outgoing;
        int index = -1;
        double best = double.MaxValue;

        foreach (var (points, which) in new[] { (_arc.Outgoing, ArcSide.Outgoing), (_arc.Incoming, ArcSide.Incoming) })
        {
            for (int i = 0; i < points.Count; i++)
            {
                double dx = at.X - XAt(points[i].T);
                double dy = at.Y - YAt(points[i].Level, top, height);
                double away = Math.Sqrt(dx * dx + dy * dy);
                if (away < best)
                {
                    best = away;
                    side = which;
                    index = i;
                }
            }
        }

        return (side, index, best);
    }

    /// <summary>
    /// A handle dragged within <see cref="SnapPixels"/> of a bar line snaps exactly onto that line.
    ///
    /// <para>Without this, the yellow would only be decoration. The default arc has nine handles spread
    /// over a changeover of sixteen bars. Only the two ends land on a bar by arithmetic. A hand cannot
    /// place a handle on a bar line by eye, to the pixel. A magnet three pixels wide makes the yellow
    /// mean something.</para>
    /// </summary>
    private double ToBar(double t)
    {
        if (_bars <= 0)
            return t;

        double bar = Math.Round(t * _bars);
        if (bar < 0 || bar > _bars)
            return t;

        double snapped = bar / _bars;
        return Math.Abs(XAt(t) - XAt(snapped)) <= SnapPixels ? snapped : t;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var at = e.GetCurrentPoint(_input).Position;

        if (_dragBassStart || _dragBassEnd)
        {
            double t = TAt(at.X);
            _arc = _dragBassStart
                ? _arc.WithBassSwap(t, _arc.BassSwapEnd)
                : _arc.WithBassSwap(_arc.BassSwapStart, t);
            _touched = true;
            ArcMoved?.Invoke(_arc);
            Invalidate();
            e.Handled = true;
            return;
        }

        if (_dragPoint < 0)
            return;

        _arc = _arc.MovePoint(_dragSide, _dragPoint, ToBar(TAt(at.X)), LevelAt(at.Y));
        _touched = true;
        ArcMoved?.Invoke(_arc);
        Invalidate();
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPoint < 0 && !_dragBassStart && !_dragBassEnd)
            return;
        _input.ReleasePointerCapture(e.Pointer);
        EndDrag(commit: true);
        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        bool touched = _touched;
        _dragPoint = -1;
        _dragBassStart = _dragBassEnd = false;
        _touched = false;
        Invalidate();

        if (commit && touched)
            ArcCommitted?.Invoke(_arc);
    }

}
#endif
#endif

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Radio's wheel: its entries down the right of the screen. They turn past a marker. The marker is drawn
/// like the needle of the radio pane's dial (RadioDial): a cyan needle with a glow behind it and a chevron
/// at its tip. Here it is laid on its side, so it points at the entry on it. The entry on the marker is
/// sharp. The entries around it are blurred, and the blur grows with their distance from the marker.
///
/// <para><b>Drawn with Skia, not laid out.</b> The blur of each entry depends on its distance from the
/// marker, and that distance changes on every frame of a turn. A XAML element has no blur of its own on
/// either head. A Skia paint does have blur, on both heads (see SkiaSurface). So the entries are text drawn
/// on one canvas. A turn is one value that eases over a few frames, with a repaint on each frame.</para>
///
/// <para>The entries do not wrap around. The first is at the top and the last at the bottom. A turn past
/// either end gives a short nudge, and then the wheel comes back. Further from the marker, an entry is
/// smaller, fainter, more blurred and set further right, as if it were on a drum that turns away from the
/// screen.</para>
/// </summary>
internal sealed partial class RadioWheel : Grid
{
    private readonly SkiaSurface _surface;
    private string[] _names = [];

    /// <summary>The entry on the marker. It has a fractional value during a turn, when the wheel is between
    /// two entries.</summary>
    private double _at;
    private double _from;
    private double _to;
    private double _t = 1;

    /// <summary>How far the wheel leans past an end, easing back to zero.</summary>
    private double _lean;

    private double _scale = 1;

    /// <summary>How long one turn takes, in milliseconds.</summary>
    private const double TurnMs = 220;

    private static readonly SKColor Cyan = new(0x04, 0xC8, 0xFF);

    public RadioWheel()
    {
        _surface = new SkiaSurface { Draw = Paint };
        Children.Add(_surface);
        IsHitTestVisible = false;
    }

    /// <summary>The entries, top to bottom.</summary>
    public void SetEntries(IReadOnlyList<string> names)
    {
        _names = [.. names];
        Jump(Math.Clamp((int)Math.Round(_to), 0, Math.Max(0, _names.Length - 1)));
    }

    /// <summary>The index of the entry the wheel is on, or is turning to.</summary>
    public int Selected => (int)Math.Round(_to);

    /// <summary>The scale that every size on the wheel is multiplied by. Setting it repaints the wheel.</summary>
    public double Sizing
    {
        get => _scale;
        set
        {
            _scale = value;
            _surface.Redraw();
        }
    }

    /// <summary>Puts an entry on the marker at once.</summary>
    public void Jump(int index)
    {
        _at = _from = _to = index;
        _t = 1;
        _lean = 0;
        _surface.Redraw();
    }

    /// <summary>Turns the wheel by one entry up (-1) or down (1). Past an end, it leans and comes back. A
    /// turn made during another one counts from the entry being turned to.</summary>
    public void Turn(int by)
    {
        var next = _to + by;
        if (next < 0 || next > _names.Length - 1)
        {
            _lean = 0.18 * by;
            _surface.Redraw();
            return;
        }

        _from = _at;
        _to = next;
        _t = 0;
        _surface.Redraw();
    }

    /// <summary>One frame of a turn. It repaints only while something is moving.</summary>
    public void Tick(double dt)
    {
        var moving = false;

        if (_t < 1)
        {
            _t = Math.Min(1, _t + dt / (TurnMs / 1000));
            _at = _from + (_to - _from) * PaneGlide.EaseAt(_t);
            moving = true;
        }

        if (_lean != 0)
        {
            _lean *= Math.Pow(0.002, dt);   // the lean shrinks to 0.2% of its value each second
            if (Math.Abs(_lean) < 0.002)
                _lean = 0;
            moving = true;
        }

        if (moving)
            _surface.Redraw();
    }

    // ── drawing ──

    private static SKTypeface? _face;
    private static SKTypeface Face => _face ??= Grids.GridFonts.UiFace();

    /// <summary>
    /// The marker, then the entries. The marker is level with the middle of the surface. Each entry stands
    /// one row from the next. Its size, ink, sideways position and blur depend on how far it is from the
    /// marker.
    /// </summary>
    private void Paint(SKCanvas canvas, Size size)
    {
        if (_names.Length == 0 || size.Width <= 0 || size.Height <= 0)
            return;

        var s = (float)_scale;
        var middle = (float)size.Height / 2;
        var right = (float)size.Width - 24 * s;

        // The needle runs from its tip on the left to the right edge. It has a glow, as the dial's needle does.
        var needleLength = 110 * s;
        var tip = right - needleLength;
        var textRight = tip - 22 * s;

        using (var glow = new SKPaint { IsAntialias = true, Color = Cyan.WithAlpha(0x33) })
            canvas.DrawRoundRect(new SKRect(tip, middle - 4.5f * s, right, middle + 4.5f * s), 4.5f * s, 4.5f * s, glow);
        using (var needle = new SKPaint { IsAntialias = true, Color = Cyan })
            canvas.DrawRect(new SKRect(tip, middle - 1 * s, right, middle + 1 * s), needle);
        using (var chevron = new SKPaint
        {
            IsAntialias = true,
            Color = Cyan,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * s,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
        })
        using (var path = new SKPath())
        {
            // The chevron from RadioDial._pointer, turned to point left at the entry.
            path.MoveTo(tip + 7 * s, middle - 6 * s);
            path.LineTo(tip, middle);
            path.LineTo(tip + 7 * s, middle + 6 * s);
            canvas.DrawPath(path, chevron);
        }

        var at = _at + _lean;
        var row = 58 * s;
        for (var i = 0; i < _names.Length; i++)
        {
            var away = i - at;
            var far = Math.Abs(away);
            if (far > 4.5)
                continue;

            var size1 = (float)(28 * s * (1 - 0.09 * Math.Min(far, 4)));
            var alpha = (float)Math.Clamp(1 - 0.2 * far, 0, 1) * (float)Math.Clamp(4.5 - far, 0, 1);
            var blur = (float)(2.6 * s * far);
            var drift = (float)(15 * s * Math.Pow(far, 1.6));

            using var font = new SKFont(Face, size1);
            var metrics = font.Metrics;
            var cap = metrics.CapHeight > 0 ? metrics.CapHeight : -metrics.Ascent * 0.7f;
            var baseline = middle + (float)(away * row) + cap / 2;

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha((byte)Math.Round(255 * alpha)),
            };
            if (blur > 0.3f)
                paint.ImageFilter = SKImageFilter.CreateBlur(blur, blur);

            canvas.DrawText(_names[i], textRight + drift, baseline, SKTextAlign.Right, font, paint);
        }
    }
}

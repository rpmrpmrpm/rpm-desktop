using Microsoft.UI.Xaml.Controls;
using SkiaSharp;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The album carousel's picture (see PegasusChrome.AlbumCarousel): the covers standing along an arc on a black
/// ground under a soft light, the curved floor they stand on, and, while it is open, the letter wheel over the
/// covers blurred. The Android TV app's AlbumCarousel.kt draws the same, with the same figures.
///
/// <para><b>Drawn with Skia, not laid out.</b> Every cover is turned in depth and tilted along the arc, and
/// both change on every frame of a turn. Neither head turns a XAML element in depth in the same way (the
/// WinUI head has projections, the Skia head does not), and a Skia canvas draws a picture through any
/// perspective on both. So each cover is four corners worked out here and its picture is drawn into them
/// (<see cref="Into"/>). The same canvas blurs the covers under the letter wheel. Nothing in this class
/// holds a decoded picture: it asks for each cover as it draws (<see cref="CoverAt"/>).</para>
///
/// <para><b>How a cover is placed</b>, as the TV's graphics layer places it: shrunk by its distance from the
/// front, turned about its upright middle so that it faces the front cover (the right edge of a cover on the
/// right comes nearer), seen through a camera <see cref="CameraShare"/> of the screen's height away, then
/// tilted along the arc, then moved to its place on the arc. The covers are drawn far ones first, so the
/// front one is over every other.</para>
/// </summary>
internal sealed partial class AlbumCarouselView : Grid
{
    private readonly SkiaSurface _surface;

    public AlbumCarouselView()
    {
        _surface = new SkiaSurface { Draw = Paint };
        Children.Add(_surface);
        IsHitTestVisible = false;
    }

    /// <summary>How many records the wall holds. Nothing is drawn but the ground while it is 0.</summary>
    public int Count { get; set; }

    /// <summary>Where the carousel stands, in records, counted without end: whole numbers are a record in front,
    /// and between them it is turning.</summary>
    public double Position { get; set; }

    /// <summary>The picture of the record at an endless place, or null while it is not decoded yet. The
    /// record's ground (<see cref="CoverGround"/>) stands in for it.</summary>
    public Func<int, SKImage?>? CoverAt { get; set; }

    /// <summary>The letter wheel's stops, and where it stands among them (whole numbers are a stop in front).
    /// Drawn only while <see cref="LettersOpen"/>.</summary>
    public IReadOnlyList<string> Letters { get; set; } = [];

    public double LettersAt { get; set; }

    public bool LettersOpen { get; set; }

    /// <summary>The collection's text scale (PegasusChrome.QueueScale), which the letters are written
    /// at.</summary>
    public double Sizing { get; set; } = 1;

    /// <summary>Asks for a repaint. See <see cref="SkiaSurface.Redraw"/>.</summary>
    public void Redraw() => _surface.Redraw();

    // ── the figures: the TV's (AlbumCarousel.kt), shares of the screen so they hold at any size ──

    /// <summary>How many covers stand to each side of the front one.</summary>
    public const int Slots = 5;

    /// <summary>The cover's edge as a share of the height, where the front cover's middle stands as a share
    /// of the height, and the arc's radius as a share of the width.</summary>
    public const double CardShare = 0.34;
    public const double ArcMiddle = 0.43;
    public const double ArcRadius = 0.9;

    /// <summary>The angle between two covers along the arc, how far each tilts along it (a share of that
    /// angle), and how far a cover beside the front one is turned toward it.</summary>
    private const double StepDegrees = 5.8;
    private const double Tilt = 0.81;
    private const double TurnDegrees = 32;

    /// <summary>How far away the camera is, as a share of the height: the TV's camera distance of 14 on a
    /// screen of density 2 and 1080 lines.</summary>
    private const double CameraShare = 1.85;

    /// <summary>How light the covers beside the front one are: home's card light (the TV's CARD_LIGHT). The
    /// front cover is at full light, and a cover coming round brightens as it comes.</summary>
    private const float CardLight = 0.75f;

    /// <summary>The letter tiles' edge as a share of the height, where the front tile's middle stands, how
    /// many stand to each side, and the angle between two along the arc.</summary>
    private const double LetterShare = 0.12;
    private const double LetterMiddle = 0.17;
    private const int LetterSlots = 7;
    private const double LetterStepDegrees = 4.6;

    /// <summary>The colour a cover is drawn on, seen while its picture is still on its way.</summary>
    private static readonly SKColor CoverGround = new(0x1A, 0x1A, 0x1A);

    /// <summary>The front cover's box in this view's own units, for the remote's stop and a press.</summary>
    public static Rect FrontRect(Size size)
    {
        var edge = size.Height * CardShare;
        return new Rect((size.Width - edge) / 2, (size.Height * ArcMiddle) - (edge / 2), edge, edge);
    }

    /// <summary>The front letter tile's box, for the remote's stop while the wheel is open.</summary>
    public static Rect FrontLetterRect(Size size)
    {
        var tile = size.Height * LetterShare;
        return new Rect((size.Width - tile) / 2, (size.Height * LetterMiddle) - (tile / 2), tile, tile);
    }

    /// <summary>The edge of the front cover, in this view's units: the size its picture is wanted at.</summary>
    public static double CoverEdge(Size size) => size.Height * CardShare;

    /// <summary>
    /// Which cover a press at <paramref name="x"/> lands on, as a number of records from the front (negative to
    /// the left): the place along the arc nearest to it, at most <see cref="Slots"/> away.
    /// </summary>
    public static int SlotAt(Size size, double x)
    {
        var radius = size.Width * ArcRadius;
        var along = Math.Clamp((x - (size.Width / 2)) / radius, -1, 1);
        var degrees = Math.Asin(along) * 180 / Math.PI;
        return (int)Math.Clamp(Math.Round(degrees / StepDegrees), -Slots, Slots);
    }

    // ── drawing ──

    /// <summary>The letters' face: the app's UI face at its semibold weight, as the TV sets them.</summary>
    private static SKTypeface? _face;
    private static SKTypeface Face => _face ??= SemiBold(Grids.GridFonts.UiFace());

    private static SKTypeface SemiBold(SKTypeface face) =>
        SKTypeface.FromFamilyName(face.FamilyName, SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal,
                                  SKFontStyleSlant.Upright) is { } heavier && heavier.FamilyName.Length > 0
            ? heavier
            : face;

    private static readonly SKSamplingOptions CoverSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private void Paint(SKCanvas canvas, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var w = (float)size.Width;
        var h = (float)size.Height;
        var px = h / 1080f;   // the TV's pixel, on a screen of this height
        var edge = (float)CoverEdge(size);
        var middle = (float)(size.Height * ArcMiddle);
        var radius = (float)(size.Width * ArcRadius);

        DrawGround(canvas, w, middle, edge, radius);

        if (Count <= 0)
            return;

        // The covers, blurred while the letters are over them, with the app's light veil (#33000000) on top.
        if (LettersOpen)
        {
            using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(16 * px, 16 * px) };
            canvas.SaveLayer(blur);
            DrawCovers(canvas, w, h, px, edge, middle, radius);
            canvas.Restore();
            using var veil = new SKPaint { Color = new SKColor(0, 0, 0, 0x33) };
            canvas.DrawRect(0, 0, w, h, veil);
            DrawLetters(canvas, w, h, px, radius);
            return;
        }

        DrawCovers(canvas, w, h, px, edge, middle, radius);
    }

    /// <summary>
    /// The ground: black, a soft light behind the front cover, and the floor the covers stand on. The floor is a
    /// circle of the arc's own radius whose top runs under the front cover's foot and falls away to both sides
    /// with the covers, lit faintly along that edge.
    /// </summary>
    private static void DrawGround(SKCanvas canvas, float w, float middle, float edge, float radius)
    {
        canvas.Clear(SKColors.Black);

        var light = new SKPoint(w / 2, middle - (edge * 0.1f));
        var reach = w * 0.32f;
        using (var shader = SKShader.CreateRadialGradient(
                   light, reach,
                   [new SKColor(0xFF, 0xFF, 0xFF, 0x66), new SKColor(0xFF, 0xFF, 0xFF, 0x24), SKColors.Transparent],
                   [0f, 0.35f, 1f], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            canvas.DrawCircle(light, reach, paint);

        var top = middle + (edge * 0.42f);
        var across = radius * 2;
        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, top), new SKPoint(0, top + across),
                   [new SKColor(0x2A, 0x2A, 0x2A), new SKColor(0x0C, 0x0C, 0x0C), SKColors.Black],
                   [0f, 0.04f, 0.3f], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            canvas.DrawOval(SKRect.Create((w - across) / 2, top, across, across), paint);
    }

    private void DrawCovers(SKCanvas canvas, float w, float h, float px, float edge, float middle, float radius)
    {
        var camera = h * (float)CameraShare;
        var at = Position;
        var first = (int)Math.Floor(at);

        // Far ones first, so each nearer cover is drawn over the ones behind it.
        var slots = new List<(int Slot, double K)>((Slots * 2) + 2);
        for (var j = -Slots; j <= Slots + 1; j++)
        {
            var slot = first + j;
            var k = slot - at;
            if (Math.Abs(k) <= Slots + 0.5)
                slots.Add((slot, k));
        }
        slots.Sort((a, b) => Math.Abs(b.K).CompareTo(Math.Abs(a.K)));

        using var shadow = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9 * px),
        };
        using var ground = new SKPaint { IsAntialias = true, Color = CoverGround };
        using var picture = new SKPaint { IsAntialias = true };
        using var shade = new SKPaint { IsAntialias = true };
        using var rim = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
        };

        foreach (var (slot, k) in slots)
        {
            var near = (float)Math.Min(Math.Abs(k), 1);
            var fade = (float)Math.Clamp(Slots + 0.5 - Math.Abs(k), 0, 1);
            var scale = 1 - (near * 0.16) - (Math.Max(Math.Abs(k) - 1, 0) * 0.035);
            var corners = Place(k, edge * scale, w / 2, middle, radius, camera, StepDegrees, TurnDegrees);

            using var outline = Outline(corners);

            shadow.Color = new SKColor(0, 0, 0, (byte)(150 * fade));
            canvas.Save();
            canvas.Translate(0, 6 * px);
            canvas.DrawPath(outline, shadow);
            canvas.Restore();

            ground.Color = CoverGround.WithAlpha((byte)(255 * fade));
            canvas.DrawPath(outline, ground);

            if (CoverAt?.Invoke(slot) is { } image)
            {
                picture.Color = SKColors.White.WithAlpha((byte)(255 * fade));
                canvas.Save();
                var into = Into(corners, image.Width, image.Height);
                canvas.Concat(in into);
                canvas.DrawImage(image, SKRect.Create(image.Width, image.Height), SKRect.Create(image.Width, image.Height),
                                 CoverSampling, picture);
                canvas.Restore();
            }

            // Every cover but the front one at the card light, brightening as it comes round.
            var dark = (1 - CardLight) * near * fade;
            if (dark > 0.002f)
            {
                shade.Color = new SKColor(0, 0, 0, (byte)(255 * dark));
                canvas.DrawPath(outline, shade);
            }

            rim.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(0x33 * fade));
            canvas.DrawPath(outline, rim);
        }
    }

    /// <summary>
    /// The letters as the covers stand, in the same style: tiles of the app's dark glass along the same arc,
    /// smaller and higher, the front one at full light and the rest at the card light, never ending. A decade
    /// is four figures and is written smaller to fit its tile.
    /// </summary>
    private void DrawLetters(SKCanvas canvas, float w, float h, float px, float radius)
    {
        var count = Letters.Count;
        if (count == 0)
            return;

        var tile = h * (float)LetterShare;
        var middle = h * (float)LetterMiddle;
        var camera = h * (float)CameraShare;
        var at = LettersAt;
        var first = (int)Math.Floor(at);

        var slots = new List<(int Slot, double K)>((LetterSlots * 2) + 2);
        for (var j = -LetterSlots; j <= LetterSlots + 1; j++)
        {
            var slot = first + j;
            var k = slot - at;
            if (Math.Abs(k) <= LetterSlots + 0.5)
                slots.Add((slot, k));
        }
        slots.Sort((a, b) => Math.Abs(b.K).CompareTo(Math.Abs(a.K)));

        var corner = 12 * px;
        using var shadow = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6 * px) };
        using var face = new SKPaint { IsAntialias = true };
        using var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        using var ink = new SKPaint { IsAntialias = true };
        using var shade = new SKPaint { IsAntialias = true };

        foreach (var (slot, k) in slots)
        {
            var letter = Letters[((slot % count) + count) % count];
            var near = (float)Math.Min(Math.Abs(k), 1);
            var fade = (float)Math.Clamp(LetterSlots + 0.5 - Math.Abs(k), 0, 1);
            var scale = 1 - (near * 0.16) - (Math.Max(Math.Abs(k) - 1, 0) * 0.03);
            var side = tile * (float)scale;
            var corners = Place(k, side, w / 2, middle, radius, camera, LetterStepDegrees, TurnDegrees);

            using (var outline = Outline(corners))
            {
                shadow.Color = new SKColor(0, 0, 0, (byte)(120 * fade));
                canvas.Save();
                canvas.Translate(0, 4 * px);
                canvas.DrawPath(outline, shadow);
                canvas.Restore();
            }

            // The tile and its letter are drawn square, through the tile's corners.
            canvas.Save();
            var into = Into(corners, side, side);
            canvas.Concat(in into);
            var box = SKRect.Create(side, side);
            face.Color = new SKColor(0x2A, 0x2A, 0x2E, (byte)(255 * fade));
            canvas.DrawRoundRect(box, corner, corner, face);
            rim.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(0x40 * fade));
            canvas.DrawRoundRect(box, corner, corner, rim);

            using var font = new SKFont(Face, (float)((letter.Length > 2 ? 30 : 52) * Sizing) * (float)scale)
            {
                Embolden = false,
                Subpixel = true,
            };
            var metrics = font.Metrics;
            var cap = metrics.CapHeight > 0 ? metrics.CapHeight : -metrics.Ascent * 0.7f;
            ink.Color = new SKColor(0xFF, 0xFF, 0xFF, (byte)(0xF2 * fade));
            canvas.DrawText(letter, side / 2, (side / 2) + (cap / 2), SKTextAlign.Center, font, ink);

            var dark = (1 - CardLight) * near * fade;
            if (dark > 0.002f)
            {
                shade.Color = new SKColor(0, 0, 0, (byte)(255 * dark));
                canvas.DrawRoundRect(box, corner, corner, shade);
            }
            canvas.Restore();
        }
    }

    // ── the geometry ──

    /// <summary>
    /// The four corners of a square of <paramref name="side"/> standing <paramref name="k"/> places from the
    /// front of an arc, top left first and clockwise: turned toward the front in depth, seen through the camera,
    /// tilted along the arc and moved to its place on it.
    /// </summary>
    private static SKPoint[] Place(double k, double side, double centreX, double middle, double radius,
                                   double camera, double stepDegrees, double turnDegrees)
    {
        var angle = k * stepDegrees * Math.PI / 180;
        var near = Math.Min(Math.Abs(k), 1);
        var x0 = centreX + (radius * Math.Sin(angle));
        var y0 = middle + (radius * (1 - Math.Cos(angle)));
        var tilt = angle * Tilt;
        var turn = -Math.Sign(k) * near * turnDegrees * Math.PI / 180;
        var (sinTurn, cosTurn) = Math.SinCos(turn);
        var (sinTilt, cosTilt) = Math.SinCos(tilt);
        var half = side / 2;

        var corners = new SKPoint[4];
        ReadOnlySpan<(double X, double Y)> square = [(-half, -half), (half, -half), (half, half), (-half, half)];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = square[i];

            // Turned about the upright middle: a positive turn takes the right edge away from the camera.
            var depth = x * sinTurn;
            var seen = camera / (camera + depth);
            var sx = x * cosTurn * seen;
            var sy = y * seen;

            // Then tilted along the arc, clockwise to the right of the front.
            corners[i] = new SKPoint(
                (float)(x0 + (sx * cosTilt) - (sy * sinTilt)),
                (float)(y0 + (sx * sinTilt) + (sy * cosTilt)));
        }
        return corners;
    }

    private static SKPath Outline(SKPoint[] corners)
    {
        var path = new SKPath();
        path.AddPoly(corners, close: true);
        return path;
    }

    /// <summary>
    /// The matrix that draws a <paramref name="width"/> by <paramref name="height"/> rectangle into four
    /// corners (top left first, clockwise): the projective map of the unit square onto them (Heckbert's
    /// square-to-quadrilateral), after the rectangle is scaled to the unit square.
    /// </summary>
    private static SKMatrix Into(SKPoint[] q, float width, float height)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y;
        double x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;

        var sx = x0 - x1 + x2 - x3;
        var sy = y0 - y1 + y2 - y3;
        double a, b, c, d, e, f, g, hh;
        if (Math.Abs(sx) < 1e-9 && Math.Abs(sy) < 1e-9)
        {
            (a, b, c) = (x1 - x0, x3 - x0, x0);
            (d, e, f) = (y1 - y0, y3 - y0, y0);
            (g, hh) = (0, 0);
        }
        else
        {
            var dx1 = x1 - x2;
            var dx2 = x3 - x2;
            var dy1 = y1 - y2;
            var dy2 = y3 - y2;
            var den = (dx1 * dy2) - (dx2 * dy1);
            g = ((sx * dy2) - (dx2 * sy)) / den;
            hh = ((dx1 * sy) - (sx * dy1)) / den;
            (a, b, c) = (x1 - x0 + (g * x1), x3 - x0 + (hh * x3), x0);
            (d, e, f) = (y1 - y0 + (g * y1), y3 - y0 + (hh * y3), y0);
        }

        var unit = new SKMatrix(
            (float)a, (float)b, (float)c,
            (float)d, (float)e, (float)f,
            (float)g, (float)hh, 1);
        return unit.PreConcat(SKMatrix.CreateScale(1 / width, 1 / height));
    }
}

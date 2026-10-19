using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A logo published with nothing behind it, and what has to be known about it before it can
/// be placed on a made ground.
///
/// <para><b>The mistake this prevents.</b> A ground drawn from the colours of the picture it will sit
/// under is a ground in the mark's own colours, which is the one thing it must not be. A logo can be
/// mostly transparent. If the picture is read whole, its "colours" are mostly the empty area around the
/// mark. The ground then ends up in the same family as the mark itself. The result is a square that
/// looks empty, and a station that appears to have no logo when the app is holding one.</para>
/// </summary>
public static class TransparentMark
{
    /// <summary>Whether the picture is a mark standing on NOTHING, rather than a tile that has soft
    /// corners.
    ///
    /// <para><b>Having an alpha channel is not enough to decide this.</b> <c>DiscStamp.HasAlpha</c>
    /// answers "is any pixel see-through". The answer is yes for many pictures that are solid squares
    /// with rounded corners. Nearly every apple-touch icon is like this. A plain blue square with white
    /// bars and four rounded corners has alpha. Ranked on that alone, it would beat the other logos, be
    /// called a mark, and get a ground that nothing can see.</para>
    ///
    /// <para><b>The border tells them apart.</b> A mark with nothing behind it is transparent all round
    /// its outside. A tile is opaque along every edge, and see-through only where the corners are cut.
    /// So the outer ring is sampled, and the question is how much of it is empty. A rounded tile comes
    /// out at about a tenth. A mark is empty nearly all the way round. There is nothing in between to
    /// argue about.</para>
    ///
    /// <para>This is cheap on purpose: one decode and a few hundred reads around one ring. It does not
    /// scan the whole picture. It is asked once for each candidate in a logo search.</para>
    /// </summary>
    public static bool StandsAlone(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width < 3 || bitmap.Height < 3)
                return false;

            if (bitmap.AlphaType == SKAlphaType.Opaque)
                return false;

            var empty = 0;
            var seen = 0;

            // Inset by a pixel: the very outermost row of a resampled picture is often a half-covered
            // fringe, and counting it reads as transparency on a picture that has none.
            var left = 1;
            var top = 1;
            var right = bitmap.Width - 2;
            var bottom = bitmap.Height - 2;

            for (var i = 0; i < BorderSamples; i++)
            {
                var t = (double)i / BorderSamples;

                foreach (var (x, y) in new[]
                {
                    ((int)(left + t * (right - left)), top),      // across the top
                    ((int)(left + t * (right - left)), bottom),   // and the bottom
                    (left,  (int)(top + t * (bottom - top))),     // down the left
                    (right, (int)(top + t * (bottom - top))),     // and the right
                })
                {
                    seen++;
                    if (bitmap.GetPixel(x, y).Alpha < Empty)
                        empty++;
                }
            }

            return seen > 0 && (double)empty / seen >= MostlyEmpty;
        }
        catch
        {
            return false;   // unreadable is not a mark
        }
    }

    /// <summary>How far round each edge the border is read. Enough that a mark whose wordmark runs to
    /// one edge is still read as a mark.</summary>
    private const int BorderSamples = 64;

    /// <summary>What counts as see-through. Not zero: the outside of a mark is often a faint trace of
    /// antialiasing, not nothing at all.</summary>
    private const byte Empty = 24;

    /// <summary>How much of the border must be empty. This is well above a rounded tile, which loses
    /// about a tenth of its border to its corners. It is well below a mark, which is empty nearly all
    /// the way round.</summary>
    private const double MostlyEmpty = 0.5;

    /// <summary>The colour the mark is drawn in: the average of the pixels that are really there.
    ///
    /// <para>Transparent pixels are skipped, and are not counted as any colour. A picture that is
    /// nine-tenths empty has almost no colour by any ordinary measure. Every ordinary measure, such as
    /// dominant, average or highlight, would then describe the empty area.</para>
    ///
    /// <para>Null where there is not enough of a mark to judge.</para>
    /// </summary>
    public static Color? Ink(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
                return null;

            long red = 0, green = 0, blue = 0, drawn = 0;
            foreach (var pixel in bitmap.Pixels)
            {
                if (pixel.Alpha < Solid)
                    continue;

                red += pixel.Red;
                green += pixel.Green;
                blue += pixel.Blue;
                drawn++;
            }

            if (drawn < Enough)
                return null;

            return Color.FromArgb(
                0xFF, (byte)(red / drawn), (byte)(green / drawn), (byte)(blue / drawn));
        }
        catch
        {
            return null;
        }
    }


    /// <summary>Whether this ink is light enough that what goes behind it should be dark. The app's own
    /// luminance test, so the mark and every other surface agree about what "light" is.</summary>
    public static bool IsLight(Color ink) => CoverColor.PrefersBlackInk(ink);

    /// <summary>The picture a transparent mark stands on: the same made figure that an unphotographed
    /// artist gets, in the mark's own hues, at a value well away from the mark's own.
    ///
    /// <para><b>Away from the ink. This is the opposite of what seems obvious.</b> A ground drawn from
    /// the colours of the picture it sits under is a ground in the mark's own tone. A mark and a ground
    /// of one tone do not separate. The mark disappears, and the station looks as though it has no
    /// logo. So the hues come from the picture, and the value is pushed the other way. A dark ground
    /// goes under a light mark, and a pale ground goes under a dark one.</para>
    ///
    /// <para>This is shared, because two surfaces stand marks on grounds: the now playing square and
    /// the station rows. A station whose badge is dark in one place and pale in the other would look
    /// like two different stations.</para>
    /// </summary>
    /// <param name="identity">What the arrangement is drawn from, so a station is the same square every
    /// time. Its name.</param>
    public static byte[]? Ground(byte[] bytes, string identity)
    {
        var seed = identity is { Length: > 0 } ? identity : "rpm";

        // The colour is read from the opaque pixels only. If there is too little of the mark to judge,
        // the palette is used instead. A record with no cover at all gets the same answer.
        if (Ink(bytes) is not { } ink)
            return SoftFigure.Sleeve(seed);

        var light = IsLight(ink);
        var colours = new List<Color>(3);

        foreach (var colour in new[] { CoverColor.Dominant(bytes), CoverColor.Highlight(bytes), CoverColor.Average(bytes) })
            if (colour is { } one)
                colours.Add(light ? CoverColor.Dim(one, UnderALightMark) : Lift(one, UnderADarkMark));

        return colours.Count > 0 ? SoftFigure.Tile(colours, seed) : SoftFigure.Sleeve(seed);
    }

    /// <summary>The mark and its ground as ONE square picture. The ground fills the square, and the
    /// mark stands in the middle with air around it.
    ///
    /// <para>The square above the tracklist draws these as two layers. That works there. The ground
    /// fades on its own clock, and the mark is held off the edges by a margin that the surface owns. A
    /// surface that can take only ONE picture cannot do that. The record on the deck is such a surface.
    /// Its label is a single Image. So it showed the mark alone, and a station's badge came out as a
    /// logo floating on black, while the square beside it stood on a made ground. The two pictures of
    /// one station were a hand's breadth apart.</para>
    ///
    /// <para>This is composed here, and not at either surface. That way, what goes on the label and
    /// what goes in the square are the same two things, arranged the same way.</para>
    /// </summary>
    /// <param name="mark">The logo, as its broadcaster drew it. Nothing is added to it. See <see
    /// cref="Ground"/> for why the separation is made entirely by the ground's value.</param>
    /// <param name="ground">The made ground from <see cref="Ground"/>.</param>
    /// <param name="edge">The square's side in pixels. The label is drawn at a couple of hundred points
    /// at most and spins, so this is small on purpose.</param>
    public static byte[]? Badge(byte[] mark, byte[] ground, int edge)
    {
        try
        {
            if (edge <= 0)
                return null;

            using var surface = SKSurface.Create(new SKImageInfo(edge, edge, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
                return null;

            var canvas = surface.Canvas;
            var whole = SKRect.Create(edge, edge);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

            using (var made = SKImage.FromEncodedData(ground))
            {
                if (made is null)
                    return null;

                // The ground fills the square. It is a background, and a background with bars down its
                // sides looks like a panel. SoftFigure draws squares, so the crop changes nothing in
                // practice. It costs nothing.
                canvas.DrawImage(made, Fill(made.Width, made.Height, edge), whole, sampling);
            }

            using (var badge = SKImage.FromEncodedData(mark))
            {
                if (badge is null)
                    return null;

                // The mark fits whole in the middle. A logo that reaches the corners has no room around
                // it. The label is also round, which is a second reason. See MarkShare.
                var side = edge * MarkShare;
                var scale = Math.Min(side / badge.Width, side / badge.Height);
                var width = (float)(badge.Width * scale);
                var height = (float)(badge.Height * scale);
                canvas.DrawImage(
                    badge,
                    SKRect.Create((edge - width) / 2, (edge - height) / 2, width, height),
                    sampling);
            }

            canvas.Flush();
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png is null || png.Size == 0 ? null : png.ToArray();
        }
        catch
        {
            return null;   // The surfaces then show the mark alone, as they did before
        }
    }

    /// <summary>How much of the badge's edge the mark may use. The rest is left as air.
    ///
    /// <para>The value is 0.7, and it comes from geometry rather than taste. The record's label is a
    /// circle. The largest square that fits inside a circle has a side of its diameter over the square
    /// root of two, which is 0.707 of the diameter. A mark drawn any larger has its corners cut off by
    /// the label's own edge. On a wordmark, that edge would cut the first and last letters of the
    /// station's name.</para>
    /// </summary>
    private const double MarkShare = 0.70;

    /// <summary>The part of a picture that fills a square: the middle of its long side.</summary>
    private static SKRect Fill(int width, int height, int edge)
    {
        if (width <= 0 || height <= 0)
            return SKRect.Create(Math.Max(width, 1), Math.Max(height, 1));

        var side = Math.Min(width, height);
        return SKRect.Create((width - side) / 2f, (height - side) / 2f, side, side);
    }

    /// <summary>A colour moved towards white by <paramref name="amount"/>. It is the counterpart of
    /// <see cref="CoverColor.Dim"/>, which only moves the other way.</summary>
    private static Color Lift(Color colour, double amount) => Color.FromArgb(
        colour.A,
        (byte)Math.Clamp(colour.R + ((255 - colour.R) * amount), 0, 255),
        (byte)Math.Clamp(colour.G + ((255 - colour.G) * amount), 0, 255),
        (byte)Math.Clamp(colour.B + ((255 - colour.B) * amount), 0, 255));

    /// <summary>How far down a ground goes under a pale mark, and how far up under a dark one.
    ///
    /// <para>It is further than it looks like it needs to be. This is the only thing that separates a
    /// mark from what it stands on. A ground a little off the ink reads as the same colour. The picture
    /// must still work when it is seen in a 34-point well, as well as in a square six times that size.
    /// But it must not go so far that the figure stops being a picture and becomes a flat panel.</para>
    /// </summary>
    private const double UnderALightMark = 0.22;

    private const double UnderADarkMark = 0.74;

    /// <summary>How opaque a pixel has to be to count as part of the mark. Not 255: a mark's edge is
    /// antialiased, and its outermost pixels are the ground showing through rather than the ink.</summary>
    private const byte Solid = 250;

    /// <summary>How much of a mark there has to be before its colour means anything. A handful of
    /// stray pixels in a picture that is otherwise empty is not a logo.</summary>
    private const int Enough = 64;

}

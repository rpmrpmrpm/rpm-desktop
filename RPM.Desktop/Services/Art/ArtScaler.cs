using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Cuts artwork bytes down to the size a surface actually shows.
///
/// <para>Album scans come off disk at whatever size the archive had. 3000 px sleeves are ordinary. A bitmap
/// decoded in full costs width × height × 4 bytes for as long as it is on screen, whatever few hundred pixels it
/// is drawn into. Fifty tiles of full scans is gigabytes of texture. A record that spins a full scan under a 72 px
/// label resamples the whole picture every frame. So every surface names the edge it needs, and decodes no more
/// than that.</para>
///
/// <para>The buckets are few and fixed on purpose. There is one thumb per bucket per picture, shared by every
/// surface in the bucket. This avoids a new size for every slider position.</para>
///
/// <para>Every failure returns the original bytes. Art that will not rescale is still art. Showing it at full size
/// is better than showing nothing.</para>
/// </summary>
public static class ArtScaler
{
    /// <summary>List rows: playlist tracks and other small chrome. 256 px covers a row of about 56 px at any sane
    /// display scale.</summary>
    public const int RowEdge = 256;

    /// <summary>Album-wall tiles, drawer covers and dialog previews. 640 px covers the wall's largest card, which is
    /// 260 px, at 2× scale.</summary>
    public const int TileEdge = 640;

    /// <summary>
    /// The smallest size the resident tier is ever cut to. The resident tier holds every record in the collection,
    /// decoded, for as long as the wall is up. So a tile is never blank and never waits for the disk.
    ///
    /// <para>The point of this floor is the following. At the small end of the wall's size slider, the resident
    /// picture is already the exact size that zoom asks for. It is not a placeholder waiting to be improved. So
    /// there is nothing to fetch, nothing to decode, and nothing to arrive late. Which size that is depends on the
    /// display. The ladder below is in PHYSICAL pixels. A 96-point tile needs 128 pixels on an ordinary screen, and
    /// 192 on a doubled one. With a fixed 128, every tile at every slider position on such a display would need a
    /// second read. That is the opposite of what the tier is for. <see cref="ResidentEdgeFor"/> is what the wall
    /// asks. This constant is where its answer starts.</para>
    ///
    /// <para>One picture at this edge is about 64 KB of pixels. Two rungs up the ladder, it is four times that. This
    /// is why the wall budgets the tier in bytes, not in records.</para>
    /// </summary>
    public const int TinyEdge = 128;

    /// <summary>
    /// The edge the resident tier must be cut at, so that a tile of <paramref name="smallestTile"/> points needs no
    /// second read on a display of <paramref name="rasterScale"/>. It is the first rung of the ladder that covers the
    /// smallest tile the surface draws. It is never below <see cref="TinyEdge"/>.
    /// </summary>
    public static int ResidentEdgeFor(double smallestTile, double rasterScale)
        => Math.Max(TinyEdge, DecodeEdgeFor(smallestTile, rasterScale));

    /// <summary>The memory that one decoded square takes: four bytes a pixel. The resident tier is held for the
    /// whole collection, so any change to its edge must be weighed against this number. Two rungs up the ladder take
    /// four times the memory for the whole collection.</summary>
    public static long BytesFor(int edge) => (long)edge * edge * 4;

    /// <summary>The playing cover pane and the record's label. This is the largest size the art is ever shown
    /// at.</summary>
    public const int DisplayEdge = 1280;

    /// <summary>The edge a wash is cut to before it is blurred. It is small on purpose. This is the one size where
    /// small costs nothing. The picture is about to lose every detail it has. A 3000 px scan and a 320 px one look
    /// the same once they are blurred.</summary>
    public const int WashEdge = 320;

    /// <summary>How hard <see cref="Wash"/> blurs, in pixels of the cut-down picture. It is about a twelfth of the
    /// edge. At that blur a sleeve stops being a sleeve and becomes its own colours.</summary>
    public const float WashSigma = 26f;

    /// <summary>
    /// The sizes a wall's cover is DECODED at. These are not the sizes a thumb is cut to. The ladder is coarse. So a
    /// wall whose tiles change size, such as the library window's size slider or the visualizer's wall, crosses a few
    /// boundaries. It does not ask for a new decode at every pixel of a drag.
    /// </summary>
    private static readonly int[] DecodeBuckets = [128, 192, 256, 384, 512, TileEdge];

    /// <summary>
    /// The pixel edge a cover should be decoded at, so that it is drawn <paramref name="logicalEdge"/> points wide on
    /// a display of <paramref name="rasterScale"/>.
    ///
    /// <para>The thumb on disk is <see cref="TileEdge"/>. That size suits the largest tile any wall draws, at 2×. A
    /// bitmap given no size decodes all of it, whatever size it is painted at. For a small tile that is often more
    /// than forty times the pixels that reach the screen. A small tile is exactly the setting that puts the most
    /// tiles on the wall at once. Both effects multiply. So a wall gets heavier the smaller its covers are asked to
    /// be.</para>
    ///
    /// <para>The result is in physical pixels, so a 2× display still gets a sharp tile. It is never above the thumb's
    /// own edge. Decoding larger than the source adds memory and no detail.</para>
    /// </summary>
    public static int DecodeEdgeFor(double logicalEdge, double rasterScale)
    {
        var wanted = logicalEdge * (rasterScale > 0 ? rasterScale : 1);
        foreach (var bucket in DecodeBuckets)
            if (bucket >= wanted)
                return bucket;
        return TileEdge;
    }

    /// <summary>
    /// A picture's own dimensions, read from its header alone. No pixels are decoded.
    /// </summary>
    public static (int Width, int Height) BoundsOf(byte[] bytes)
    {
        try
        {
            var info = SKBitmap.DecodeBounds(bytes);
            return (info.Width, info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// The same picture with its empty margins cut off: the smallest rectangle that holds every pixel anybody can
    /// see.
    ///
    /// <para><b>What this is for.</b> A logo is published on a canvas, not cropped to its lettering. The artist logos
    /// from the catalogues are all 800 by 310, whatever the mark inside them is. A mark that reads as two short stacked
    /// words sits in the middle of that canvas, with about 250 transparent pixels on each side. Drawn as it comes, the
    /// picture is flush with the left of the page, and the LETTERING is a third of the way across. It looks like a
    /// logo that was nudged by hand. It is also drawn small, because the height it is scaled to is mostly empty.</para>
    ///
    /// <para>The floor is above zero. A mark with a soft drop shadow fades to an alpha of one or two long before it
    /// reaches nothing. With a floor of zero, that faint shadow would count as content, and the crop would keep most
    /// of the margin.</para>
    ///
    /// <para>The original bytes are returned when there is nothing to cut, when the picture has no alpha to read, or
    /// when anything goes wrong. Cutting is an improvement, never a requirement.</para>
    /// </summary>
    public static byte[] Trim(byte[] bytes, byte floor = 8)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);

            // Four bytes a pixel, with alpha last. This is both formats a PNG decodes to here (RGBA and BGRA). Any
            // other layout, such as a 16-bit or an opaque format, has no margin to find. Reading it as though it did
            // would crop the picture on its blue channel.
            if (bitmap is null || bitmap.BytesPerPixel != 4 || bitmap.Width <= 0 || bitmap.Height <= 0)
                return bytes;

            var pixels = bitmap.GetPixelSpan();
            var stride = bitmap.RowBytes;
            if (pixels.Length < stride * bitmap.Height)
                return bytes;

            int left = bitmap.Width, right = -1, top = bitmap.Height, bottom = -1;
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (pixels[row + (x * 4) + 3] <= floor)
                        continue;

                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    bottom = y;   // Rows are walked downwards, so the last one to have content is the lowest.
                }
            }

            // Nothing visible in it at all, or nothing to cut. Either way, the picture as it came.
            if (right < left || bottom < top)
                return bytes;
            if (left == 0 && top == 0 && right == bitmap.Width - 1 && bottom == bitmap.Height - 1)
                return bytes;

            using var cut = new SKBitmap();
            if (!bitmap.ExtractSubset(cut, new SKRectI(left, top, right + 1, bottom + 1)))
                return bytes;

            // PNG always. What is being cut out is a picture with holes in it. Every other encoder here would fill
            // the holes with black.
            using var image = SKImage.FromBitmap(cut);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png is null || png.Size == 0 ? bytes : png.ToArray();
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary>
    /// The picture no larger than <paramref name="cap"/> pixels on the side <paramref name="side"/> names. It is
    /// averaged down to within a factor of two, then finished with a smooth cubic. It is still a picture with holes
    /// in it.
    ///
    /// <para><b>Why a logo needs its own cut.</b> Every other picture in the app reaches a surface already close to
    /// the size it is drawn at. A thumb cache cuts it there. A logo has no cache behind it. It comes from the catalogue
    /// 800 pixels wide, and it is drawn one inch tall. A four-fold reduction handed to the bitmap decoder causes the
    /// fault that <see cref="SamplingFor(double)"/> exists to prevent. Sixteen source pixels are read for each output
    /// pixel, and the rest are dropped unseen. On a photograph that shows as grain. On LETTERING, which is the whole
    /// of what a logo is, it shows as gravel along every stroke. A stroke thin enough disappears between two reads,
    /// and the mark looks compressed.</para>
    ///
    /// <para>The cap is set on the side that the surface measures. <see cref="Downscale"/> cannot do that. It caps the
    /// long side only, so a wide mark capped on its width comes back a third of the height it will be drawn at. See
    /// <see cref="MarkSide"/>.</para>
    ///
    /// <para><b>How it is sampled.</b> The picture is halved, and halved again, while more than a factor of two
    /// remains. Then it is finished with <see cref="SmoothCubic"/>. It is not sampled by
    /// <see cref="SamplingFor(double)"/>. That rule is for photographs, and neither of its answers suits a logo on its
    /// own. A mipmap chain alone averages averages, and three-pixel type comes back as a grey smear. Mitchell's cubic
    /// alone overshoots on both sides of every edge. On a stroke one output pixel wide, that overshoot is most of the
    /// stroke. The photograph rule also changes filters with the display's scale, because the drawn cap times the
    /// scale sets the factor. So the same mark was sampled one way at 100% and the other way at 150%.</para>
    ///
    /// <para>PNG always. What is being reduced is a picture drawn on nothing. Every other encoder here would fill that
    /// nothing with black. The result is never larger than the input. Blowing a mark up past its own resolution causes
    /// the same softness from the other end. So the caller should draw the size that comes BACK, not the size it
    /// asked for.</para>
    /// </summary>
    /// <param name="cap">The cap in pixels, on the side that <paramref name="side"/> names.</param>
    /// <param name="side">Which side the cap is measured on. See <see cref="MarkSide"/>.</param>
    public static byte[] FitMark(byte[] bytes, int cap, MarkSide side)
    {
        if (cap <= 0)
            return bytes;

        // Every level between the source and the last one. They are held so the loop can let each one go as soon as
        // the next is built. All of them are disposed on the way out, however this method leaves.
        SKBitmap? interim = null;
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return bytes;

            // Only ever DOWN. A mark blown up past its own resolution causes the same softness from the other end. So
            // the caller draws what comes back, not what it asked for.
            var measured = Measured(bitmap.Width, bitmap.Height, side);
            if (measured <= cap)
                return bytes;

            // Premultiplied at every step. This is not an incidental choice. A filter run on straight alpha reads the
            // colour of pixels that are not there. A mark on a transparent ground is mostly pixels that are not there.
            // Skia averages in premultiplied space, and the encode below turns the result back into the form a PNG
            // stores.
            //
            // Halved with a plain linear read, at exactly half size. That gives the mean of each 2x2 block, so it is
            // the same thing as a mipmap level. It is built here, not asked for, because Skia ignores the mipmap chain
            // once a cubic resampler is set, and the last step is cubic. The loop stops while the picture is still
            // larger than the target, so it never goes past it.
            var source = bitmap;
            while (Measured(source.Width, source.Height, side) > cap * 2)
            {
                var half = new SKImageInfo(
                    Math.Max(1, source.Width / 2), Math.Max(1, source.Height / 2),
                    SKColorType.Rgba8888, SKAlphaType.Premul);

                if (source.Resize(half, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
                        is not { } smaller)
                    break;

                interim?.Dispose();
                interim = smaller;
                source = smaller;
            }

            // The last step: a factor of two or less, with the cubic that does not ring. Both sides come from the
            // SOURCE proportions, not from whatever the halving left. So the rounding at each level cannot change the
            // mark's shape. The capped side is then set exactly, not rounded. A cap is never missed by a pixel.
            var k = (double)cap / measured;
            var wide = Math.Max(1, (int)Math.Round(bitmap.Width * k));
            var high = Math.Max(1, (int)Math.Round(bitmap.Height * k));

            if (Capped(bitmap.Width, bitmap.Height, side) is Axis.Height)
                high = cap;
            else
                wide = cap;

            var info = new SKImageInfo(wide, high, SKColorType.Rgba8888, SKAlphaType.Premul);

            using var scaled = source.Resize(info, new SKSamplingOptions(SmoothCubic));
            if (scaled is null)
                return bytes;

            using var image = SKImage.FromBitmap(scaled);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png is null || png.Size == 0 ? bytes : png.ToArray();
        }
        catch
        {
            return bytes;
        }
        finally
        {
            interim?.Dispose();
        }
    }

    /// <summary>
    /// Which side of a mark its cap is measured on.
    ///
    /// <para>It is given at every call, and it has no default. Every answer produces a picture, and the wrong side
    /// produces a picture at the wrong size. For example, a 512 by 128 lockup capped at 102 comes back as 102 by 26 on
    /// <see cref="Long"/>, and as 408 by 102 on <see cref="Short"/>. Only one of those is the number of pixels the
    /// surface draws. The other is left for the renderer to reduce at draw time. That is the fault this cut exists to
    /// avoid.</para>
    /// </summary>
    public enum MarkSide
    {
        /// <summary>The height. For a surface that gives a mark a fixed line and lets it run as wide as it needs to.
        /// Examples are the act's lettering on a card, and a station's mark on the feature plate.</summary>
        Height,

        /// <summary>The long side. For a mark fitted whole inside a box with <c>Stretch="Uniform"</c>. There, the long
        /// side is what reaches the box's edge.</summary>
        Long,

        /// <summary>The short side. For a mark that FILLS its box and is cropped by it, with
        /// <c>Stretch="UniformToFill"</c>. There, the short side is what has to reach the edge.</summary>
        Short,
    }

    /// <summary>The same cut, capped on the height. See <see cref="FitMark"/>.</summary>
    public static byte[] FitHeight(byte[] bytes, int height) => FitMark(bytes, height, MarkSide.Height);

    /// <summary>The side that <paramref name="side"/> names, in pixels.</summary>
    private static int Measured(int width, int height, MarkSide side) => side switch
    {
        MarkSide.Height => height,
        MarkSide.Long => Math.Max(width, height),
        _ => Math.Min(width, height),
    };

    private enum Axis { Width, Height }

    /// <summary>Which axis <paramref name="side"/> lands on for a picture of this shape. A square hits the width
    /// either way, which is the same number of pixels.</summary>
    private static Axis Capped(int width, int height, MarkSide side) => side switch
    {
        MarkSide.Height => Axis.Height,
        MarkSide.Long => width >= height ? Axis.Width : Axis.Height,
        _ => width <= height ? Axis.Width : Axis.Height,
    };

    /// <summary>
    /// The long-side cap to ask <see cref="ThumbCache"/> for, so that a picture of these proportions still has
    /// <paramref name="edge"/> pixels on its SHORT side.
    ///
    /// <para>Everything that draws a cover in a square crops it with <c>UniformToFill</c>. A crop shows the short
    /// side. The caps in this file are on the LONG side. That is right for a picture shown whole, and wrong for a
    /// picture shown square. For example, a landscape sleeve capped at 384 comes back as 384 by 274. Filling a 384
    /// square from that picture stretches it to about 1.4 times the size it was drawn at. Square sleeves, which are
    /// nearly all of them, are not affected. That is why only some covers looked soft and the rest were sharp.</para>
    /// </summary>
    public static int LongEdgeForShortSide(int width, int height, int edge)
    {
        if (width <= 0 || height <= 0)
            return edge;
        var longest = Math.Max(width, height);
        var shortest = Math.Min(width, height);
        return (int)Math.Ceiling(edge * (double)longest / shortest);
    }

    /// <summary>
    /// How a cut of <paramref name="factor"/> is sampled. This is the one decision that every reduction in the app
    /// goes through. It is the difference between a thumbnail and a thumbnail full of moire.
    ///
    /// <para>A cubic resampler reads a fixed sixteen source pixels for each destination pixel. That is the best answer
    /// when the picture barely shrinks. Past about half size, it is the WRONG answer. At a seventh of the size, sixteen
    /// pixels out of every fifty are not a filter. They are a sample. Everything between the pixels it reads is thrown
    /// away unseen. Fine rings come back as bands, lettering comes back as gravel, and nothing downstream can put back
    /// detail that was never read. It looks like a quality setting, but it is the fault itself.</para>
    ///
    /// <para>Past that point the picture is mipmapped instead. It is averaged down in halves. Each level is the honest
    /// mean of the level above it. The two levels around the wanted size are blended. Measured against a true area
    /// average of the same cut, a 10× reduction goes from 13.5 to 3.9 RMS by doing this. The error that remains is
    /// extra contrast, and that extra contrast is the aliasing that shows on screen.</para>
    /// </summary>
    public static SKSamplingOptions SamplingFor(double factor)
        => factor >= 2
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKCubicResampler.Mitchell);

    /// <summary>The sampling for a cut from <paramref name="sourceEdge"/> down to <paramref name="targetEdge"/>. See
    /// <see cref="SamplingFor(double)"/>.</summary>
    public static SKSamplingOptions SamplingFor(double sourceEdge, double targetEdge)
        => SamplingFor(targetEdge > 0 ? sourceEdge / targetEdge : 1);

    /// <summary>
    /// The cubic that lettering is reduced with. It is the B-spline, with B=1 and C=0. That is the smooth end of the
    /// cubic family.
    ///
    /// <para><see cref="SKCubicResampler.Mitchell"/> (B=1/3, C=1/3) is the ordinary bicubic. Its kernel goes negative
    /// on either side of the pixel it is centred on. That is why it looks sharp. A reduced edge comes back with a
    /// lighter line outside it and a darker one inside. On a photograph that reads as detail. On a stroke of type about
    /// as wide as one output pixel, the overshoot on both sides is most of the stroke. A mark full of thin strokes then
    /// comes back outlined and uneven.</para>
    ///
    /// <para>The B-spline has no negative lobe. Every source pixel it reads is added, and none is subtracted. So a
    /// reduced stroke is the weighted mean of what was really there. It is softer, and that is the trade. Softer is
    /// the right answer for type drawn at a fraction of the size it was published at.</para>
    /// </summary>
    public static readonly SKCubicResampler SmoothCubic = new(b: 1f, c: 0f);

    /// <summary>
    /// The picture no larger than <paramref name="maxEdge"/> on its long side. The same bytes are returned when they
    /// already fit. Otherwise the picture is re-encoded. The encoding is JPEG unless the source has alpha, because disc
    /// art is a transparent PNG and must stay one. See <see cref="Jpeg"/> for what the re-encode keeps that an ordinary
    /// encoder throws away.
    /// </summary>
    public static byte[] Downscale(byte[] bytes, int maxEdge) => Downscale(bytes, maxEdge, how: null);

    /// <summary>
    /// The same cut, sampled the way the caller asks, not the way <see cref="SamplingFor(double)"/> would.
    /// </summary>
    /// <param name="how">Null uses the app's own rule, which is what a photograph wants. Anything else is a caller
    /// saying it knows something about this picture that the factor does not show. Only <c>RemoteImage.Cut</c>
    /// passes one.</param>
    public static byte[] Downscale(byte[] bytes, int maxEdge, SKSamplingOptions? how)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null || Math.Max(bitmap.Width, bitmap.Height) <= maxEdge)
                return bytes;

            var k = (double)maxEdge / Math.Max(bitmap.Width, bitmap.Height);
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(bitmap.Width * k)),
                Math.Max(1, (int)Math.Round(bitmap.Height * k)),
                SKColorType.Rgba8888, SKAlphaType.Premul);
            using var scaled = bitmap.Resize(info, how ?? SamplingFor(1 / k));
            if (scaled is null)
                return bytes;

            if (bitmap.AlphaType != SKAlphaType.Opaque)
            {
                using var image = SKImage.FromBitmap(scaled);
                using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                return png is null || png.Size == 0 ? bytes : png.ToArray();
            }

            return Jpeg(scaled) ?? bytes;
        }
        catch
        {
            return bytes;
        }
    }

    /// <summary>
    /// The bytes of a thumb, with its COLOUR kept at full resolution.
    ///
    /// <para><b>Why this is not <c>image.Encode(Jpeg, q)</c>.</b> That encoder subsamples the colour channels by
    /// default. It uses 4:2:0, so colour is kept at half the picture's edge. A 640 px thumb then carries a 320 px
    /// record of what colour anything in it is. On a photograph nobody notices. That is what the format was designed
    /// around, and a face is broad areas of one colour. On a SLEEVE it shows. A sleeve is graphic design. For example,
    /// a sleeve of thin bright rings on a near-black ground loses its colour at half chroma. The rings sit next to the
    /// black, and they come back as grey dashes. Every wall in the app showed that.</para>
    ///
    /// <para>Keeping 4:4:4 makes the thumb about half as large again. That is a deliberate trade. The cache is derived
    /// and swept, and disk is the cheapest thing it uses. The quality also goes up. A low quantiser leaves ringing
    /// around a one-pixel line, which is the same fault by another route.</para>
    ///
    /// <para>Null where the encoder will not run at all. The caller then keeps the bytes it had.</para>
    /// </summary>
    internal static byte[]? Jpeg(SKBitmap bitmap)
    {
        using var pixels = bitmap.PeekPixels();
        if (pixels is not null)
        {
            using var stream = new SKDynamicMemoryWStream();
            var options = new SKJpegEncoderOptions(
                ThumbQuality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore);

            if (pixels.Encode(stream, options))
            {
                using var written = stream.DetachAsData();
                if (written is { Size: > 0 })
                    return written.ToArray();
            }
        }

        // The options were not applied. This happens with an older Skia, or with a pixel layout it will not take. A
        // subsampled thumb is still a thumb. This is the one path where the old fault is better than nothing.
        using var image = SKImage.FromBitmap(bitmap);
        using var plain = image.Encode(SKEncodedImageFormat.Jpeg, ThumbQuality);
        return plain is null || plain.Size == 0 ? null : plain.ToArray();
    }

    /// <summary>How hard a thumb is quantised. It is high, because what is kept is the thing a JPEG is worst at: a
    /// bright line one pixel wide on a dark ground. See <see cref="Jpeg"/>.</summary>
    private const int ThumbQuality = 92;

    /// <summary>
    /// The picture with its colour removed: the same photograph, in black and white.
    ///
    /// <para>This is what the artists wall draws. A wall of press shots is a wall of many different lighting setups.
    /// Boxes then read as a jumble, however carefully they are laid out. Drained to grey and washed in the one colour
    /// the picture gave up, they read as one set of boxes, each with a different photograph. That is what a wall of
    /// artists should look like.</para>
    ///
    /// <para>It is drained HERE, not on the surface. The reason is the same as for the wash, which is also blurred
    /// here. A saturation filter on a live layer runs again every frame, under an effect pipeline the machine may not
    /// have. This produces an ordinary picture that any surface can draw. It is asked of a thumbnail that has already
    /// been cut down, so it costs one small decode.</para>
    ///
    /// <para>Null when the picture will not decode. A colour photograph is then a finished tile without this, and the
    /// caller falls back to it.</para>
    /// </summary>
    public static byte[]? Grey(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
                return null;

            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            using var image = SKImage.FromBitmap(bitmap);
            using var paint = new SKPaint
            {
                // The sRGB luminance weights, spread across all three channels. These are the same weights used
                // for the ink choice. So a photograph that the wall calls dark is dark to both.
                ColorFilter = SKColorFilter.CreateColorMatrix(GreyMatrix),
            };
            surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);

            using var drained = surface.Snapshot();
            using var encoded = drained.Encode(SKEncodedImageFormat.Jpeg, 86);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static readonly float[] GreyMatrix =
    [
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0,       0,       0,       1, 0,
    ];

    /// <summary>
    /// The picture as a WASH: cut down small and blurred with a Gaussian blur. It is used for backdrops that wear a
    /// record's colours without showing the record.
    ///
    /// <para>It is blurred HERE, not on the surface that shows it. A composition blur runs again every frame that the
    /// panel is up, and it depends on an effect pipeline the machine may not have. This is one pass on a background
    /// thread. The result is an ordinary picture that any surface can draw. It is cut to <see cref="WashEdge"/> first,
    /// because the blurred result hardly depends on the size it started at.</para>
    ///
    /// <para>Null when the picture will not decode. A wash is decoration. Every surface that asks for one is complete
    /// without it.</para>
    /// </summary>
    public static byte[]? Wash(byte[] bytes)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
                return null;

            // Never UP. A 90 px cover becomes a 90 px wash. Blurring it larger would cost more and show less.
            var k = Math.Min(1.0, (double)WashEdge / Math.Max(bitmap.Width, bitmap.Height));
            var info = new SKImageInfo(
                Math.Max(1, (int)Math.Round(bitmap.Width * k)),
                Math.Max(1, (int)Math.Round(bitmap.Height * k)),
                SKColorType.Rgba8888, SKAlphaType.Premul);

            using var scaled = bitmap.Resize(info, SamplingFor(1 / k));
            if (scaled is null)
                return null;

            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            // CLAMP at the edges. This is the whole difference between a wash and a dark edge. A blur reads past the
            // picture it is given. If nothing clamps it, those reads return nothing. So the four sides come back
            // partly transparent. Over any background, that draws a dark rim around the one thing on screen that must
            // have no edges. Clamped, the border pixels stand in for what is past them.
            surface.Canvas.Clear(SKColors.Black);
            using var image = SKImage.FromBitmap(scaled);

            // The sigma is quoted for a full-size cut. A cover too small to reach WashEdge came through unscaled, so it
            // is blurred by proportionally less. If it were blurred by the full figure, a 120 px thumbnail would come
            // back as one flat colour, not as a soft picture of itself.
            var sigma = WashSigma * Math.Max(info.Width, info.Height) / WashEdge;
            using var paint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp),
            };
            surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);

            using var washed = surface.Snapshot();
            using var encoded = washed.Encode(SKEncodedImageFormat.Jpeg, 82);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

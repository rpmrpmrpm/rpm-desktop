using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The picture across the head of a Pegasus artist or genre page: a photograph covering the whole width
/// from the top of the screen, a colour laid across it from the left where the page asks for one, and its
/// foot faded out so the page's room shows through where the picture ends.
///
/// <para><b>Drawn into one bitmap, once.</b> The desktop pages fade their header with a composition mask
/// over a live copy of their band (ArtistDetailView.EnsureHeroFade). That draws the band again on every
/// frame. Here the fade is drawn into the picture on a worker thread, and the header is a single Image. A
/// new size draws it again.</para>
/// </summary>
internal static class PageHeaderArt
{
    /// <summary>The widest the bitmap is drawn, in pixels. The photographs it is made from are 1824 (a
    /// genre's) to 1920 (an artist's backdrop) wide, so drawing it wider only adds memory; the Image
    /// stretches it to the header.</summary>
    private const int WidestPixels = 1920;

    /// <summary>A drawn header: premultiplied BGRA pixels, row after row.</summary>
    public sealed record Drawn(byte[] Pixels, int Width, int Height);

    /// <summary>
    /// How big the bitmap is drawn for a header of this size in points: the header's own pixels, kept to
    /// <see cref="WidestPixels"/> across with the header's shape kept.
    /// </summary>
    public static (int Width, int Height) PixelsFor(double width, double height, double raster)
    {
        var wide = Math.Max(1, width * raster);
        var tall = Math.Max(1, height * raster);
        if (wide > WidestPixels)
        {
            tall *= WidestPixels / wide;
            wide = WidestPixels;
        }
        return ((int)Math.Round(wide), (int)Math.Round(tall));
    }

    /// <summary>
    /// Draws the header. Off the UI thread: it decodes the photograph.
    /// </summary>
    /// <param name="photo">The photograph, or null for a header with only the colour across it.</param>
    /// <param name="down">Where the photograph is held vertically when it is taller than the header: 0
    /// keeps its top (an artist's photograph, where the heads are), 0.5 its middle.</param>
    /// <param name="foot">The share of the header, from the bottom, that fades out.</param>
    /// <param name="across">The colour laid over the photograph from the left, as gradient stops from 0
    /// (the left edge) to 1, or null for none.</param>
    /// <returns>The pixels, or null where there is nothing
    /// to draw or the photograph does not decode.</returns>
    public static Drawn? Draw(
        byte[]? photo, int width, int height, double down, double foot,
        IReadOnlyList<(double Offset, Color Colour)>? across)
    {
        if (width <= 0 || height <= 0 || (photo is not { Length: > 0 } && across is null))
            return null;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            if (photo is { Length: > 0 })
            {
                using var image = SKImage.FromEncodedData(photo);
                if (image is null)
                    return null;

                // Covering the header, as UniformToFill does: centred across, held at `down` up and down.
                var scale = Math.Max((double)width / image.Width, (double)height / image.Height);
                var drawnWide = image.Width * scale;
                var drawnTall = image.Height * scale;
                var left = (width - drawnWide) / 2;
                var top = (height - drawnTall) * Math.Clamp(down, 0, 1);
                canvas.DrawImage(
                    image,
                    SKRect.Create((float)left, (float)top, (float)drawnWide, (float)drawnTall),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }

            if (across is { Count: > 0 })
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0), new SKPoint(width, 0),
                    [.. across.Select(stop => new SKColor(stop.Colour.R, stop.Colour.G, stop.Colour.B, stop.Colour.A))],
                    [.. across.Select(stop => (float)stop.Offset)],
                    SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader };
                canvas.DrawRect(SKRect.Create(width, height), paint);
            }

            // The foot: everything drawn so far is kept by a mask that is whole down to where the fade
            // starts and nothing at the bottom, on t²(3−2t) so the fade has no corner where it begins.
            var starts = 1 - Math.Clamp(foot, 0, 1);
            var colours = new SKColor[Steps + 2];
            var places = new float[Steps + 2];
            colours[0] = SKColors.White;
            places[0] = 0;
            for (var i = 0; i <= Steps; i++)
            {
                var t = (double)i / Steps;
                var kept = 1 - (t * t * (3 - (2 * t)));
                colours[i + 1] = SKColors.White.WithAlpha((byte)Math.Round(kept * 255));
                places[i + 1] = (float)(starts + ((1 - starts) * t));
            }
            using (var mask = SKShader.CreateLinearGradient(
                       new SKPoint(0, 0), new SKPoint(0, height), colours, places, SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = mask, BlendMode = SKBlendMode.DstIn })
            {
                canvas.DrawRect(SKRect.Create(width, height), paint);
            }
        }

        return new Drawn(bitmap.Bytes, width, height);
    }

    /// <summary>How many stops the foot's curve is drawn with.</summary>
    private const int Steps = 12;

    /// <summary>The drawn pixels as something an Image shows. On the UI thread.</summary>
    public static WriteableBitmap Show(Drawn drawn)
    {
        var bitmap = new WriteableBitmap(drawn.Width, drawn.Height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
            buffer.Write(drawn.Pixels, 0, drawn.Pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }
}

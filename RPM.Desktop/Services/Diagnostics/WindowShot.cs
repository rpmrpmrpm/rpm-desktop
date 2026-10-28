using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// A picture of the app, for a report a tester sends.
/// </summary>
public static class WindowShot
{
    /// <summary>The longest side of the picture, in pixels. A shot of a 5K display at full size is
    /// several megabytes of PNG and shows nothing the scaled one does not; the server refuses
    /// anything over 1.5 MB. Text at this width is still readable.</summary>
    public const int MaxSide = 1600;

    /// <summary>
    /// Renders <paramref name="element"/> and encodes it as PNG.
    ///
    /// <para>Call this when nothing is over the app that should not be in the shot: a flyout that is
    /// still closing, or the report window itself, is rendered along with everything else.</para>
    /// </summary>
    /// <returns>The PNG bytes, or null when there is nothing to render or the frame could not be
    /// read. A report without a picture is still worth sending, so this never throws.</returns>
    public static async Task<byte[]?> PngAsync(FrameworkElement element)
    {
        // A zero-sized render target crashes the Skia head rather than returning an empty picture.
        if (element.ActualWidth < 1 || element.ActualHeight < 1)
            return null;

        var (width, height) = Fit(element.ActualWidth, element.ActualHeight);

        try
        {
            var shot = new RenderTargetBitmap();
            await shot.RenderAsync(element, width, height);

            // What comes back is not always the size that was asked for: the element can change shape
            // between the measurement above and the render. The buffer is read at the size the shot
            // reports, never at the size requested, or the copy runs past the end of it.
            var pixelWidth = shot.PixelWidth;
            var pixelHeight = shot.PixelHeight;
            if (pixelWidth < 1 || pixelHeight < 1)
                return null;

            var buffer = await shot.GetPixelsAsync();
            var pixels = new byte[buffer.Length];
            using (var stream = buffer.AsStream())
                stream.ReadExactly(pixels);

            var info = new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (pixels.Length < info.BytesSize)
                return null;

            using var image = SKImage.FromPixelCopy(info, pixels);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch (Exception e) when (e is InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
            RPM.Core.Diagnostics.Scope.Said("feedback", $"no screenshot: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>The size to render at: the element's own size, scaled down so its longest side is at
    /// most <see cref="MaxSide"/>. A window smaller than that is rendered as it is.</summary>
    private static (int Width, int Height) Fit(double width, double height)
    {
        var scale = Math.Min(1.0, MaxSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}

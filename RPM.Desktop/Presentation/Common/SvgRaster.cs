using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Draws an SVG into a PNG. Code that reads pixels, such as the transparent-mark test and the
/// ground made under a mark, can then be given a drawing.
///
/// <para>This works on the WinUI head only. It draws through Win2D's <c>CanvasSvgDocument</c>. That is
/// the same Direct2D SVG renderer that the XAML <c>SvgImageSource</c> uses, so the PNG matches what the
/// row drew before. On the Skia head, <see cref="Png"/> returns null, and its callers keep the drawing
/// as a drawing.</para>
/// </summary>
internal static class SvgRaster
{
    /// <summary>The drawing as PNG bytes. The long side is <paramref name="longSide"/> pixels, and the
    /// shape comes from the viewBox, or from the width and height where there is no viewBox. Returns
    /// null where the SVG cannot be read or drawn. It always returns null on the Skia head.
    ///
    /// <para>The root's width and height are rewritten to the target size before the drawing. An SVG
    /// that sets a fixed size (for example <c>width="177" height="50"</c>) would otherwise be drawn at
    /// that size, in the corner of the target, instead of being scaled to fill it.</para>
    ///
    /// <para>Call it on a worker thread. It parses, draws and encodes.</para>
    /// </summary>
    public static byte[]? Png(byte[] svg, int longSide)
    {
#if RPM_WINUI
        try
        {
            if (longSide <= 0 || Sized(svg, longSide) is not { } sized)
                return null;

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            using var document = Microsoft.Graphics.Canvas.Svg.CanvasSvgDocument.LoadFromXml(device, sized.Xml);

            // 96 dpi, so the target's DIPs are pixels.
            using var target = new Microsoft.Graphics.Canvas.CanvasRenderTarget(
                device, sized.Width, sized.Height, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Microsoft.UI.Colors.Transparent);
                session.DrawSvg(document, new Windows.Foundation.Size(sized.Width, sized.Height));
            }

            // B8G8R8A8, premultiplied: the render target's default format.
            var pixels = target.GetPixelBytes();
            var info = new SKImageInfo(sized.Width, sized.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            if (pixels.Length < info.BytesSize)
                return null;

            using var bitmap = new SKBitmap(info);
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), info.BytesSize);

            using var image = SKImage.FromBitmap(bitmap);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png is null || png.Size == 0 ? null : png.ToArray();
        }
        catch
        {
            return null;   // the caller keeps the drawing and draws it as it did before
        }
#else
        _ = svg;
        _ = longSide;
        return null;
#endif
    }

    /// <summary>The SVG text with its root sized to the target, and that size in pixels.</summary>
    private readonly record struct SizedSvg(string Xml, int Width, int Height);

    /// <summary>
    /// Reads the drawing's shape and rewrites its root to fill a target of that shape whose long side
    /// is <paramref name="longSide"/>. Null where the root is not an <c>svg</c> element or no shape
    /// can be read from it.
    /// </summary>
    private static SizedSvg? Sized(byte[] svg, int longSide)
    {
        XDocument document;
        using (var stream = new MemoryStream(svg, writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
               {
                   // Many SVGs carry a DOCTYPE. Ignored rather than processed, so nothing is fetched.
                   DtdProcessing = DtdProcessing.Ignore,
                   XmlResolver = null,
               }))
        {
            document = XDocument.Load(reader);
        }

        if (document.Root is not { } root || root.Name.LocalName != "svg")
            return null;

        // The viewBox is the drawing's own coordinate space. Without one, the width and height are,
        // and they become the viewBox so the drawing scales with the new size.
        double boxWidth, boxHeight;
        if (Numbers((string?)root.Attribute("viewBox")) is [_, _, > 0 and var w, > 0 and var h])
        {
            boxWidth = w;
            boxHeight = h;
        }
        else if (Length((string?)root.Attribute("width")) is { } width
                 && Length((string?)root.Attribute("height")) is { } height)
        {
            boxWidth = width;
            boxHeight = height;
            root.SetAttributeValue("viewBox", FormattableString.Invariant($"0 0 {width} {height}"));
        }
        else
        {
            return null;
        }

        var scale = longSide / Math.Max(boxWidth, boxHeight);
        var pixelWidth = Math.Max(1, (int)Math.Round(boxWidth * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(boxHeight * scale));

        root.SetAttributeValue("width", pixelWidth.ToString(CultureInfo.InvariantCulture));
        root.SetAttributeValue("height", pixelHeight.ToString(CultureInfo.InvariantCulture));

        return new SizedSvg(root.ToString(SaveOptions.DisableFormatting), pixelWidth, pixelHeight);
    }

    /// <summary>A list of numbers separated by spaces or commas, as a viewBox is written. Null where any
    /// of them is not a number.</summary>
    private static double[]? Numbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var parts = text.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        }

        return numbers;
    }

    /// <summary>A width or height in user units: a bare number or one in <c>px</c>. Null for a
    /// percentage or any other unit, which says nothing about the drawing's shape.</summary>
    private static double? Length(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            value = value[..^2];

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
               && number > 0
            ? number
            : null;
    }
}

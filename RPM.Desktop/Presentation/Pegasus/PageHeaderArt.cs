using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The band across the top of a Pegasus artist or genre page, laid out as the Android TV app's PageBand: the
/// photograph in the right part of the screen, its own pixels taken away toward its left and its foot so the
/// page shows through rather than a line or a bar of colour; and in the left column the name (or the logo, or
/// the genre's wordmark), the first whole sentences of the article, the counts and the page's keys.
///
/// <para><b>The band stays where it is.</b> The rows under it scroll, and fade out under its foot rather than
/// being cut at a line (see <see cref="PageRowsFade"/>).</para>
///
/// <para><b>The photograph is drawn into one bitmap, once.</b> The desktop pages dissolve their header with a
/// composition mask over a live copy of their band (ArtistDetailView.EnsureHeroFade), which draws the band
/// again on every frame. Here the dissolve is drawn into the picture on a worker thread (see
/// <see cref="Draw"/>), and the photograph is a single Image. A new size draws it again.</para>
/// </summary>
internal static class PageHeaderArt
{
    // The TV's places (ArtistPage.kt), as shares of the screen's width and of the drawn band's height.

    /// <summary>The drawn band's height, as a share of the screen's width.</summary>
    private const double BandHeight = 0.27;

    /// <summary>The band as laid out runs this much further down than it is drawn, so the rows start under
    /// the writing.</summary>
    private const double BandFoot = 1.2;

    /// <summary>The photograph's width, as a share of the screen's, at the right.</summary>
    private const double PhotoShare = 0.5625;

    /// <summary>The left column's width, as a share of the screen's.</summary>
    private const double TextShare = 0.39;

    /// <summary>Where the name's box starts and how tall it is, and the gap under it, as shares of the drawn
    /// band's height.</summary>
    private const double NameTop = 0.19;
    private const double NameHeight = 0.26;
    private const double BioGap = 0.05;

    /// <summary>How much of the band's height, from its foot, the rows scroll under while they fade
    /// out.</summary>
    private const double RowsUnder = 0.3;

    /// <summary>How much of the photograph dissolves at its left and at its foot.</summary>
    private const double DissolveLeft = 0.45;
    private const double DissolveFoot = 0.3;

    /// <summary>
    /// Where the band's parts stand on a screen of some width, all in points.
    /// </summary>
    /// <param name="High">The band's height, which is the photograph's.</param>
    /// <param name="Photo">The photograph's width, at the right of the screen.</param>
    /// <param name="Column">The left column's width.</param>
    /// <param name="NameTop">Where the name's box starts, from the top of the screen.</param>
    /// <param name="NameHeight">The name's box's height.</param>
    /// <param name="Gap">The gap between the name's box and the article.</param>
    /// <param name="Under">How deep the rows fade where they go under the band.</param>
    public readonly record struct Places(
        double High, double Photo, double Column, double NameTop, double NameHeight, double Gap, double Under)
    {
        /// <summary>
        /// Where the rows start: the band's height less the depth they fade over, as on the TV, or lower
        /// where the column reaches further down (<paramref name="columnFoot"/>). A focused key in the column
        /// then never has rows passing under it. The rows' content starts <see cref="Under"/> below this.
        /// </summary>
        public double RowsTop(double columnFoot) => Math.Max(High - Under, columnFoot);
    }

    /// <summary>The band's places for a screen <paramref name="width"/> points wide.</summary>
    /// <param name="topKeys">The height of the strip at the top of the screen that holds the corner keys.
    /// The name's box is kept under it.</param>
    public static Places For(double width, double topKeys)
    {
        var drawn = width * BandHeight;
        var high = drawn * BandFoot;
        return new Places(
            high, width * PhotoShare, width * TextShare, Math.Max(drawn * NameTop, topKeys),
            drawn * NameHeight, drawn * BioGap, high * RowsUnder);
    }

    /// <summary>The widest the bitmap is drawn, in pixels. The photographs it is made from are at most 1920
    /// (an artist's backdrop) wide, so drawing it wider only adds memory; the Image stretches it to the
    /// photograph's box.</summary>
    private const int WidestPixels = 1920;

    /// <summary>A drawn photograph: premultiplied BGRA pixels, row after row.</summary>
    public sealed record Drawn(byte[] Pixels, int Width, int Height);

    /// <summary>
    /// How big the bitmap is drawn for a box of this size in points: the box's own pixels, kept to
    /// <see cref="WidestPixels"/> across with the box's shape kept.
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
    /// Draws the photograph's box. Off the UI thread: it decodes the photograph.
    /// </summary>
    /// <param name="photo">The photograph.</param>
    /// <param name="down">Where the photograph is held vertically when it is taller than the box: 0 keeps
    /// its top, 0.5 its middle.</param>
    /// <returns>The pixels, or null where there is nothing to draw or the photograph does not
    /// decode.</returns>
    public static Drawn? Draw(byte[]? photo, int width, int height, double down)
    {
        if (width <= 0 || height <= 0 || photo is not { Length: > 0 })
            return null;

        using var image = SKImage.FromEncodedData(photo);
        if (image is null)
            return null;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            // Covering the box, as UniformToFill does: centred across, held at `down` up and down.
            var scale = Math.Max((double)width / image.Width, (double)height / image.Height);
            var drawnWide = image.Width * scale;
            var drawnTall = image.Height * scale;
            var left = (width - drawnWide) / 2;
            var top = (height - drawnTall) * Math.Clamp(down, 0, 1);
            canvas.DrawImage(
                image,
                SKRect.Create((float)left, (float)top, (float)drawnWide, (float)drawnTall),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            // The dissolves: what is drawn is kept by a mask that rises from nothing over the left of the box,
            // and by one that falls to nothing over its foot.
            Dissolve(canvas, width, height, new SKPoint(0, 0), new SKPoint(width, 0), DissolveLeft, rising: true);
            Dissolve(canvas, width, height, new SKPoint(0, 0), new SKPoint(0, height), DissolveFoot, rising: false);
        }

        return new Drawn(bitmap.Bytes, width, height);
    }

    /// <summary>
    /// Draws a header across the whole width: the photograph covering it, a colour laid across it from the
    /// left where the page asks for one, and its foot faded out. Off the UI thread.
    /// </summary>
    public static Drawn? Draw(
        byte[]? photo, int width, int height, double down, double foot,
        IReadOnlyList<(double Offset, Windows.UI.Color Colour)>? across)
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

            Dissolve(canvas, width, height, new SKPoint(0, 0), new SKPoint(0, height), foot, rising: false);
        }

        return new Drawn(bitmap.Bytes, width, height);
    }

    /// <summary>How many stops a dissolve's curve is drawn with.</summary>
    private const int Steps = 12;

    /// <summary>
    /// Keeps what is on the canvas through a mask along one side, from <paramref name="start"/> to
    /// <paramref name="end"/>: over the first <paramref name="share"/> of it from nothing to whole
    /// (<paramref name="rising"/>), or over the last share from whole to nothing, on t²(3−2t) so the dissolve
    /// has no corner where it begins.
    /// </summary>
    private static void Dissolve(SKCanvas canvas, int width, int height, SKPoint start, SKPoint end, double share, bool rising)
    {
        share = Math.Clamp(share, 0, 1);
        var colours = new List<SKColor>(Steps + 2);
        var places = new List<float>(Steps + 2);

        // The rest of the side is whole: before the fall.
        if (!rising)
        {
            colours.Add(SKColors.White);
            places.Add(0);
        }
        for (var i = 0; i <= Steps; i++)
        {
            var t = (double)i / Steps;
            var eased = t * t * (3 - (2 * t));
            colours.Add(SKColors.White.WithAlpha((byte)Math.Round((rising ? eased : 1 - eased) * 255)));
            places.Add((float)(rising ? share * t : 1 - share + (share * t)));
        }
        // After the rise.
        if (rising)
        {
            colours.Add(SKColors.White);
            places.Add(1);
        }

        using var mask = SKShader.CreateLinearGradient(start, end, [.. colours], [.. places], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = mask, BlendMode = SKBlendMode.DstIn };
        canvas.DrawRect(SKRect.Create(width, height), paint);
    }

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

/// <summary>
/// The rows of an artist's or a genre's page fading out where they scroll under the band, rather than being
/// cut square at its foot (the TV's fadesAtTop).
///
/// <para>WinUI has no OpacityMask, so the pixels are taken away with composition, as the drawer's head does
/// (see PegasusChrome.TopFade): a live copy of the scroller's rendering, multiplied by a ramp from nothing to
/// whole, painted in a sprite over the top of the rows; and the live rows cut away in the same band, on the
/// wrapper around the scroller. The cut is never on the scroller that is copied, because a cut there would
/// cut the copy too.</para>
///
/// <para>The depth grows with the scroll, up to <see cref="Depth"/>. At rest nothing is under the band (the
/// rows' content starts that far down), so no band is cut and no copy is drawn.</para>
/// </summary>
internal sealed class PageRowsFade
{
    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _rows;
    private readonly FrameworkElement _host;

    private CompositionVisualSurface? _copy;
    private SpriteVisual? _sprite;
    private InsetClip? _cut;

    /// <summary>Set when the composition features used here are not available. The rows are then cut
    /// square, and still read.</summary>
    private bool _none;

    private double _depth;

    /// <param name="scroll">The page's scroller, which is copied.</param>
    /// <param name="rows">The wrapper around it, which is cut.</param>
    /// <param name="host">An element over the top of the rows, as wide as them, that paints the copy.</param>
    public PageRowsFade(ScrollViewer scroll, FrameworkElement rows, FrameworkElement host)
    {
        _scroll = scroll;
        _rows = rows;
        _host = host;
        scroll.ViewChanged += (_, _) => Update();
        rows.SizeChanged += (_, _) => Update();
    }

    /// <summary>How deep the rows fade at most, in points.</summary>
    public double Depth
    {
        get => _depth;
        set
        {
            if (Math.Abs(_depth - value) < 0.5)
                return;
            _depth = value;
            Update();
        }
    }

    /// <summary>Builds the copy, the ramp and the cut, the first time the rows are scrolled.</summary>
    private void Ensure()
    {
        if (_sprite is not null || _none)
            return;

        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;

            var copy = compositor.CreateVisualSurface();
            copy.SourceVisual = ElementCompositionPreview.GetElementVisual(_scroll);
            copy.SourceOffset = Vector2.Zero;

            // Painted at its own size from its own top left corner (see PegasusChrome.TopFade).
            var pixels = compositor.CreateSurfaceBrush(copy);
            pixels.Stretch = CompositionStretch.None;
            pixels.HorizontalAlignmentRatio = 0;
            pixels.VerticalAlignmentRatio = 0;

            // Only the ramp's alpha is read: nothing at the top of the band, everything at its foot.
            var ramp = compositor.CreateLinearGradientBrush();
            ramp.MappingMode = CompositionMappingMode.Relative;
            ramp.StartPoint = new Vector2(0, 0);
            ramp.EndPoint = new Vector2(0, 1);
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(0f, Microsoft.UI.Colors.Transparent));
            ramp.ColorStops.Add(compositor.CreateColorGradientStop(1f, Microsoft.UI.Colors.White));

            var mask = compositor.CreateMaskBrush();
            mask.Source = pixels;
            mask.Mask = ramp;

            var sprite = compositor.CreateSpriteVisual();
            sprite.Brush = mask;
            ElementCompositionPreview.SetElementChildVisual(_host, sprite);

            var cut = compositor.CreateInsetClip();
            ElementCompositionPreview.GetElementVisual(_rows).Clip = cut;

            _copy = copy;
            _sprite = sprite;
            _cut = cut;
        }
        catch
        {
            // A backend without visual surfaces or mask brushes. Tried once.
            _none = true;
        }
    }

    /// <summary>The band at the present scroll: as deep as the rows are scrolled, up to
    /// <see cref="Depth"/>.</summary>
    public void Update()
    {
        var depth = (float)Math.Clamp(_scroll.VerticalOffset, 0, _depth);
        if (depth <= 0 && _sprite is null)
            return;   // at rest, before it was ever needed: nothing to build

        Ensure();
        if (_sprite is null || _copy is null || _cut is null)
            return;

        var width = (float)_rows.ActualWidth;
        if (width <= 0)
            return;

        _cut.TopInset = depth;
        BandSurface.Fit(_copy, _sprite, width, depth, 0);
    }
}

using SkiaSharp;
using Windows.Foundation;

#if RPM_SKIA
using Uno.WinUI.Graphics2DSK;
#else
using SkiaSharp.Views.Windows;
#endif

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// What anything that paints with Skia needs from each head: a canvas, and a buffer that starts each
/// frame empty. The two heads supply these in different ways. This is the one place that knows how.
///
/// <para>On the Uno head the canvas is the app's own. <c>SKCanvasElement</c> draws straight into the
/// scene that the rest of the window is composed in. Its content is cached until <c>Invalidate</c> is
/// called, so a surface that nobody is touching costs nothing. On the WinUI head there is no such
/// element. <c>SKCanvasElement.IsSupportedOnCurrentPlatform()</c> returns false there, so a control that
/// creates one directly draws nothing and shows its own background instead. This type avoids that. Its
/// canvas comes from <c>SKXamlCanvas</c>, which renders to a bitmap and copies it to the screen.</para>
///
/// <para><b>It is shared, not written again for each control, because the WinUI side has a fix that is
/// only visible when it is missing.</b> SKXamlCanvas keeps one bitmap, and SkiaSharp does not clear it
/// between paints. So without the Clear in OnPaintSurface, every frame the surface has ever drawn stays
/// underneath. On the library grid, that shows as rows smeared across every offset the list has been
/// scrolled to. A second copy of this class could leave the fix out.</para>
///
/// <para>Both heads give out an <see cref="SKCanvas"/> and a size in logical units. This keeps the painter
/// the same on both. <c>SKXamlCanvas</c> does not do this by itself. It renders at device resolution, so
/// the canvas is scaled here. Setting <c>IgnorePixelScaling</c> would not help. It renders at logical size
/// and scales the result up, which blurs the text by the same factor that it was meant to sharpen.</para>
///
/// <para>A canvas is not a hit-test target on either head. Every caller puts a transparent <c>Border</c>
/// over it to receive the pointer. See <c>RpmGrid</c> and <c>WaveformEditor</c>.</para>
/// </summary>
#if RPM_SKIA
internal sealed partial class SkiaSurface : SKCanvasElement
{
    /// <summary>What to draw. The size is in logical pixels.</summary>
    public Action<SKCanvas, Size>? Draw { get; init; }

    protected override void RenderOverride(SKCanvas canvas, Size area) => Draw?.Invoke(canvas, area);

    /// <summary>Asks for a repaint. Asking twice before the next frame has the same effect as asking
    /// once, so callers need not avoid repeated calls.</summary>
    public void Redraw() => Invalidate();
}
#else
internal sealed partial class SkiaSurface : SKXamlCanvas
{
    public SkiaSurface()
    {
        // False, so the surface is device resolution and the scale is applied to the canvas below.
        IgnorePixelScaling = false;
        PaintSurface += OnPaintSurface;
    }

    /// <summary>What to draw. The size is in logical pixels.</summary>
    public Action<SKCanvas, Size>? Draw { get; init; }

    public void Redraw() => Invalidate();

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (Draw is not { } draw)
            return;

        // Replace the buffer. Do not draw on top of it. SKXamlCanvas keeps one bitmap and copies it to the
        // screen. It is not repainted as part of the XAML scene, and SkiaSharp does not clear it between
        // paints. Without this Clear, every frame the grid has drawn stays underneath, and the rows smear
        // across every offset the list has been scrolled to.
        //
        // Transparent, not a colour. The background belongs to the caller's own XAML Background on both
        // heads, and that is usually translucent. Clear replaces pixels whatever the blend mode is, so it
        // empties the buffer without putting a second background over the first.
        //
        // The Uno head does not need any of this. SKCanvasElement draws into the window's canvas, which
        // repaints the Background under it on every frame.
        e.Surface.Canvas.Clear(SKColors.Transparent);

        var scale = (float)Dpi;
        if (scale <= 0 || float.IsNaN(scale))
            scale = 1;

        var canvas = e.Surface.Canvas;
        canvas.Save();
        canvas.Scale(scale);
        draw(canvas, new Size(e.Info.Width / scale, e.Info.Height / scale));
        canvas.Restore();
    }
}
#endif

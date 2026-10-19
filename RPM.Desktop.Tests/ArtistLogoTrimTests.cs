using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// Trimming a logo down to its lettering.
///
/// <para>The catalogues publish every artist logo on one canvas size (800 by 310, whether the mark on
/// it is one long word or two short stacked ones), so the picture and the drawing on it have different
/// shapes. A page that puts the PICTURE flush with its left margin would show a short mark a third of
/// the way across. These tests check the trim that prevents this.</para>
/// </summary>
public class ArtistLogoTrimTests
{
    /// <summary>A mark sitting in the middle of a wide empty canvas comes back as the mark.</summary>
    [Fact]
    public void A_mark_on_a_canvas_is_cut_to_the_mark()
    {
        var canvas = Png(80, 40, new SKRectI(30, 8, 50, 32));

        var (width, height) = ArtScaler.BoundsOf(ArtScaler.Trim(canvas));

        Assert.Equal(20, width);
        Assert.Equal(24, height);
    }

    /// <summary>A picture already flush with its own edges is returned unchanged: no re-encode, and
    /// nothing cut off a mark that reaches the edge on purpose.</summary>
    [Fact]
    public void A_picture_with_no_margin_is_left_alone()
    {
        var full = Png(20, 10, new SKRectI(0, 0, 20, 10));

        Assert.Same(full, ArtScaler.Trim(full));
    }

    /// <summary>Nothing visible on it at all: there is no rectangle to cut to, and the bytes come back
    /// as they went in rather than as a picture of no width.</summary>
    [Fact]
    public void A_picture_of_nothing_is_left_alone()
    {
        var empty = Png(20, 10, ink: null);

        Assert.Same(empty, ArtScaler.Trim(empty));
    }

    /// <summary>The faintest edge of a drop shadow is margin, not lettering. A mark that fades to one or
    /// two of alpha would otherwise keep almost all of the canvas it was published on.</summary>
    [Fact]
    public void A_shadow_too_faint_to_see_is_margin()
    {
        var canvas = Png(80, 40, new SKRectI(30, 8, 50, 32), halo: 3);

        var (width, height) = ArtScaler.BoundsOf(ArtScaler.Trim(canvas));

        Assert.Equal(20, width);
        Assert.Equal(24, height);
    }

    /// <summary>A transparent canvas with one solid rectangle drawn on it, and optionally a band of
    /// almost-invisible pixels around that rectangle standing in for a soft shadow.</summary>
    private static byte[] Png(int width, int height, SKRectI? ink, int halo = 0)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            if (ink is { } block)
            {
                if (halo > 0)
                {
                    var around = new SKRectI(
                        block.Left - halo, block.Top - halo, block.Right + halo, block.Bottom + halo);
                    canvas.DrawRect(around, new SKPaint { Color = new SKColor(255, 255, 255, 2) });
                }

                canvas.DrawRect(block, new SKPaint { Color = SKColors.White });
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}

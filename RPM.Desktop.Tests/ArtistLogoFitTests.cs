using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// Scaling a logo down to the height it is drawn at.
///
/// <para>A mark (logo) arrives from the catalogue eight hundred pixels wide and is drawn about an inch
/// tall. That is a four-fold reduction with no thumbnail cache in front of it, and a bitmap decoder
/// does it by sampling, not filtering: it reads a few source pixels per output pixel and drops
/// everything between them. On lettering, which is all a logo is, that gives a noisy edge on every
/// stroke, and that is why such a mark looks badly compressed.</para>
///
/// <para>The picture below contains only detail too fine to survive the scaling, the same shape the
/// thumbnail tests use: hairline strokes on a transparent background, scaled to a seventh. Averaged,
/// they become one even tone. Sampled, they become bands of full and empty pixels.</para>
/// </summary>
public class ArtistLogoFitTests
{
    private const int SourceWidth = 1024;
    private const int SourceHeight = 700;
    private const int DrawnHeight = 100;

    [Fact]
    public void Strokes_too_fine_to_keep_are_averaged_away_rather_than_sampled()
    {
        var mark = ArtScaler.FitHeight(Strokes(), DrawnHeight);

        using var cut = SKBitmap.Decode(mark);
        var (low, high) = AlphaRange(cut);

        // Averaged in halves and then finished with the smooth cubic, this comes back as a narrow band
        // around the mean. Sampled with a cubic alone, the same scaling gives 0..255: every stroke
        // either read whole or missed entirely, which is exactly the noise.
        Assert.True(high - low <= 110, $"strokes came back as bands: {low}..{high}");
    }

    /// <summary>Scaled to the height that was asked for, with the mark's own proportions kept.
    /// Lettering must never be squashed to fit.</summary>
    [Fact]
    public void The_cut_is_to_the_height_asked_for_and_keeps_its_shape()
    {
        var (width, height) = ArtScaler.BoundsOf(ArtScaler.FitHeight(Strokes(), DrawnHeight));

        Assert.Equal(DrawnHeight, height);
        Assert.Equal(SourceWidth * DrawnHeight / SourceHeight, width, tolerance: 1);
    }

    /// <summary>A mark published shorter than the height it would be drawn at is returned unchanged.
    /// Enlarging it to fill the cap would make it soft in the other direction, and the surface is told
    /// to draw what came back rather than what it asked for.</summary>
    [Fact]
    public void A_mark_smaller_than_the_height_asked_for_is_left_alone()
    {
        var small = Strokes(width: 200, height: 60);

        Assert.Same(small, ArtScaler.FitHeight(small, DrawnHeight));
    }

    /// <summary>
    /// The scaled result holds no value lighter or darker than the mark it came from.
    ///
    /// <para>The ordinary bicubic (Mitchell's, B=1/3 and C=1/3) has a kernel that goes negative on
    /// each side of centre, so a reduced edge gets a halo: pixels outside the range of anything in the
    /// source. That makes a photograph look sharp. On lettering it outlines every stroke, and on a
    /// stroke about as wide as one output pixel the halo is most of the stroke. The B-spline used here
    /// has no negative lobe, so it cannot do this.</para>
    ///
    /// <para>Measured in the middle of the flat blocks as well as across their edges, and on ALPHA
    /// rather than on colour, because a mark has a transparent background and its edges are alpha
    /// edges. The two levels are kept away from 0 and 255 on purpose: an overshoot past either end is
    /// clipped by the format and would not show.</para>
    /// </summary>
    [Fact]
    public void The_cut_invents_no_value_lighter_or_darker_than_the_mark()
    {
        using var cut = SKBitmap.Decode(ArtScaler.FitHeight(Bars(), DrawnHeight));
        var (low, high) = AlphaRange(cut);

        Assert.True(low >= Faint - 2, $"the cut went below the faintest part of the mark: {low}");
        Assert.True(high <= Strong + 2, $"the cut went above the strongest part of the mark: {high}");
    }

    private const byte Faint = 64;
    private const byte Strong = 192;

    /// <summary>Wide blocks alternating between two alphas, neither of them at the ends of the range.
    /// Flat inside, sharp at every edge: the shape that shows a ringing filter.</summary>
    private static byte[] Bars()
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(SourceWidth, SourceHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 255, 255, Faint));
            for (var x = 0; x < SourceWidth; x += 128)
                canvas.DrawRect(
                    new SKRect(x, 0, x + 64, SourceHeight),
                    new SKPaint { Color = new SKColor(255, 255, 255, Strong), BlendMode = SKBlendMode.Src });
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Still a picture with a transparent background. A logo re-encoded as a JPEG comes back
    /// with its transparent background filled in black, which on a photograph band is a black box
    /// around the artist's name.</summary>
    [Fact]
    public void The_ground_the_mark_is_drawn_on_stays_empty()
    {
        using var cut = SKBitmap.Decode(ArtScaler.FitHeight(Block(), DrawnHeight));

        Assert.Equal(0, cut.GetPixel(0, 0).Alpha);
    }

    /// <summary>One solid mark in the middle of an empty canvas, the shape a catalogue publishes.</summary>
    private static byte[] Block()
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(SourceWidth, SourceHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawRect(new SKRect(300, 200, 700, 500), new SKPaint { Color = SKColors.White });
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Hairline vertical strokes on a transparent background, three pixels wide, finer than
    /// one pixel of the result, so only their average can survive the scaling.</summary>
    private static byte[] Strokes(int width = SourceWidth, int height = SourceHeight)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, x / 3 % 2 == 0 ? SKColors.White : SKColors.Transparent);
            }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>The faintest and strongest alpha in the middle of the result, away from the edges,
    /// where a filter's own overshoot appears.</summary>
    private static (byte Low, byte High) AlphaRange(SKBitmap bitmap)
    {
        byte low = 255, high = 0;
        for (var y = 4; y < bitmap.Height - 4; y++)
            for (var x = 4; x < bitmap.Width - 4; x++)
            {
                var alpha = bitmap.GetPixel(x, y).Alpha;
                low = Math.Min(low, alpha);
                high = Math.Max(high, alpha);
            }
        return (low, high);
    }
}

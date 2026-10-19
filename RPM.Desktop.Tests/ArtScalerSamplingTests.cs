using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// What scaling down to a thumbnail may do to detail it cannot keep.
///
/// <para>A picture reduced to less than about half its size has detail that does not fit. The right
/// way is to AVERAGE that detail; the wrong way is to sample it (read four source pixels per
/// destination pixel and ignore the rest). Both look like scaling, but sampling turns a sleeve's fine
/// rings into moire and its lettering into noise, and no filtering afterwards can restore pixels that
/// were never read.</para>
///
/// <para>The picture below contains only detail that cannot fit: three-pixel stripes, scaled to a
/// seventh. Averaged, it becomes flat grey. Sampled, it becomes bands of black and white, so the
/// fault is easy to detect.</para>
/// </summary>
public class ArtScalerSamplingTests
{
    private const int SourceEdge = 1024;
    private const int ThumbEdge = 137;

    [Fact]
    public void Detail_too_fine_to_keep_is_averaged_away_rather_than_sampled()
    {
        var thumb = ArtScaler.Downscale(Stripes(), ThumbEdge);

        using var cut = SKBitmap.Decode(thumb);
        Assert.Equal(ThumbEdge, Math.Max(cut.Width, cut.Height));

        var (low, high) = Range(cut);

        // Flat grey once the stripes are averaged; JPEG and the filter's own overshoot account for the
        // rest. Sampled, this picture comes back 0..255: the whole range, which is the moire itself.
        Assert.True(high - low <= 140, $"stripes came back as bands: {low}..{high}");
    }

    /// <summary>Vertical stripes three pixels wide, finer than one pixel of the thumbnail, so only
    /// their average can survive the scaling.</summary>
    private static byte[] Stripes()
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(SourceEdge, SourceEdge, SKColorType.Rgba8888, SKAlphaType.Premul));

        for (var y = 0; y < SourceEdge; y++)
            for (var x = 0; x < SourceEdge; x++)
                bitmap.SetPixel(x, y, x / 3 % 2 == 0 ? SKColors.Black : SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>The darkest and lightest pixel, away from the edges: a JPEG has ringing at a border,
    /// and that ringing is not what is being measured.</summary>
    private static (byte Low, byte High) Range(SKBitmap bitmap)
    {
        byte low = 255, high = 0;
        for (var y = 4; y < bitmap.Height - 4; y++)
            for (var x = 4; x < bitmap.Width - 4; x++)
            {
                var green = bitmap.GetPixel(x, y).Green;
                low = Math.Min(low, green);
                high = Math.Max(high, green);
            }
        return (low, high);
    }
}

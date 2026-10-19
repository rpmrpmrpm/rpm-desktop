using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// Reading how big a picture is off the front of it, without fetching the rest.
///
/// <para>What rests on this: a host that cuts a logo to order answers every request with exactly the
/// size it was asked for, so the only way to find out how much picture is really behind an address is
/// to ask for the asset bare and read its header. Fetching the whole thing to read twenty-four bytes
/// would be a download per candidate on a list that checks several. So what arrives is a kilobyte
/// from the start of the file (see RemoteImage.HeaderAsync), and the measurement has to work on that.</para>
///
/// <para>If it does not, nothing fails loudly: the bounds come back 0x0, the check is skipped, and
/// every logo goes back to being asked for at a size nobody can vouch for. That is the whole reason
/// this is pinned.</para>
/// </summary>
public class PictureHeaderTests
{
    private const int Front = 1024;

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void A_pictures_size_is_readable_from_the_front_of_it(SKEncodedImageFormat format)
    {
        var whole = Picture(617, 404, format);
        Assert.True(whole.Length > Front, "the fixture has to be longer than the piece being read");

        var (width, height) = ArtScaler.BoundsOf(whole[..Front]);

        Assert.Equal(617, width);
        Assert.Equal(404, height);
    }

    /// <summary>A picture SHORTER than the piece asked for arrives whole, and is still measured. This is
    /// the ordinary case for the pictures worth catching: the asset that turned out to be a browser-tab
    /// icon is a few hundred bytes.</summary>
    [Fact]
    public void A_picture_shorter_than_the_piece_is_measured_too()
    {
        var tiny = Flat(16, 16);
        Assert.True(tiny.Length < Front, $"a browser-tab icon is not {tiny.Length} bytes");

        Assert.Equal((16, 16), ArtScaler.BoundsOf(tiny));
    }

    /// <summary>And bytes that are not a picture at all measure nothing rather than throwing. That
    /// lets the caller ask for the address the ordinary way.</summary>
    [Fact]
    public void Something_that_is_not_a_picture_measures_nothing()
        => Assert.Equal((0, 0), ArtScaler.BoundsOf(new byte[Front]));

    /// <summary>One flat colour, which is what a browser-tab icon usually nearly is: a few hundred bytes,
    /// header and all.</summary>
    private static byte[] Flat(int width, int height)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.OrangeRed);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>Noise rather than a flat colour, so the encoders cannot compress the fixture down under
    /// the length the test is about.</summary>
    private static byte[] Picture(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

        var seed = 1u;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                seed = (seed * 1664525) + 1013904223;
                bitmap.SetPixel(x, y, new SKColor((byte)(seed >> 16), (byte)(seed >> 8), (byte)seed));
            }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return encoded.ToArray();
    }
}

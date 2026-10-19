using RPM.Desktop.Presentation.Common;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

// Bleed uses no palette and needs no dispatcher. It is a plain crop and scale over bytes, so unlike the
// figure and cover tests beside it, it does not need a PaletteStandIn.
public class PlaylistArtBleedTests
{
    private static byte[] Solid(int width, int height, SKColor colour, bool alpha = false)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888,
            alpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        surface!.Canvas.Clear(colour);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(alpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90);
        return encoded!.ToArray();
    }

    private static (int Width, int Height) Dimensions(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes);
        return (bitmap.Width, bitmap.Height);
    }

    // A small square picture is left untouched. A picture that needs no re-encoding is not re-encoded,
    // not even into a copy that looks the same.
    [Fact]
    public void A_square_picture_already_under_the_cap_is_returned_unchanged()
    {
        var bytes = Solid(80, 80, SKColors.CornflowerBlue);
        Assert.Same(bytes, PlaylistArt.Bleed(bytes, 200));
    }

    [Fact]
    public void A_landscape_picture_is_cropped_to_a_centred_square()
    {
        var bytes = Solid(200, 100, SKColors.Crimson);
        var (width, height) = Dimensions(PlaylistArt.Bleed(bytes, 1000));
        Assert.Equal(100, width);
        Assert.Equal(100, height);
    }

    [Fact]
    public void A_portrait_picture_is_cropped_to_a_centred_square()
    {
        var bytes = Solid(90, 300, SKColors.SeaGreen);
        var (width, height) = Dimensions(PlaylistArt.Bleed(bytes, 1000));
        Assert.Equal(90, width);
        Assert.Equal(90, height);
    }

    // Scaled down only past the cap, never up. A picture bigger than the largest edge the app composes a
    // cover at loses the excess.
    [Fact]
    public void A_picture_bigger_than_the_cap_is_scaled_down_to_it()
    {
        var bytes = Solid(800, 800, SKColors.Goldenrod);
        var (width, height) = Dimensions(PlaylistArt.Bleed(bytes, 200));
        Assert.Equal(200, width);
        Assert.Equal(200, height);
    }

    // A PNG with real transparency keeps it. Re-encoding a transparent picture as a JPEG would flatten it
    // onto black, which is not the picture the user chose.
    [Fact]
    public void Transparency_survives_the_crop()
    {
        var bytes = Solid(200, 200, SKColors.Transparent, alpha: true);
        using var bitmap = SKBitmap.Decode(PlaylistArt.Bleed(bytes, 100));
        Assert.NotEqual(SKAlphaType.Opaque, bitmap.AlphaType);
    }

    // Bytes that will not decode are neither a crash nor an empty result. The caller keeps what it had,
    // which for the file picker is the picture the user just chose.
    [Fact]
    public void Bytes_that_will_not_decode_come_back_unchanged()
    {
        byte[] garbage = [1, 2, 3, 4, 5];
        Assert.Same(garbage, PlaylistArt.Bleed(garbage, 200));
    }
}

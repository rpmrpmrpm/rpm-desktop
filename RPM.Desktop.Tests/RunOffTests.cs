using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// The wide artist photo drawn with its reflection under it (<see cref="RunOff"/>): which photos get
/// one, what the baked canvas holds, and how deep the surface's foot dissolve runs over it.
/// </summary>
public class RunOffTests
{
    private const int Wide = 320;
    private const int High = 180;

    // ── which photos ──

    [Theory]
    [InlineData(1920, 1080, true)]    // a fanart.tv backdrop
    [InlineData(1000, 1000, false)]   // a fanart.tv artistthumb
    [InlineData(600, 800, false)]     // an upright photo
    [InlineData(0, 0, false)]         // a picture that could not be measured
    public void Only_a_photo_wider_than_it_is_tall_gets_a_reflection(int width, int height, bool applies)
        => Assert.Equal(applies, RunOff.Applies(width, height));

    [Fact]
    public void A_square_photo_is_not_baked()
        => Assert.Null(RunOff.Bake(Gradient(400, 400)));

    [Fact]
    public void Bytes_that_are_not_a_picture_are_not_baked()
        => Assert.Null(RunOff.Bake([1, 2, 3, 4]));

    // ── the canvas ──

    [Fact]
    public void The_canvas_is_the_photos_width_and_twice_its_height()
    {
        using var canvas = SKBitmap.Decode(RunOff.Bake(Gradient(Wide, High)));

        Assert.Equal(Wide, canvas.Width);
        Assert.Equal(2 * High, canvas.Height);
    }

    /// <summary>Above the seam blur's reach the canvas is the photo as it was, give or take the JPEG.
    /// The reach is 50 of the phone's 393 points of width, which is 41 pixels here, so it starts at row
    /// 139 of 180.</summary>
    [Fact]
    public void The_photo_is_left_as_it_was_above_the_seam()
    {
        using var source = SKBitmap.Decode(Gradient(Wide, High));
        using var canvas = SKBitmap.Decode(RunOff.Bake(Gradient(Wide, High)));

        foreach (var y in new[] { 10, 60, 110, 130 })
            Assert.InRange(RowMean(canvas, y) - RowMean(source, y), -4, 4);
    }

    /// <summary>The photo is not blurred toward its foot. Stripes three pixels high keep their full
    /// contrast down to the seam blur's reach. The phone layout starts its blur at 0.6 of the height (row
    /// 108), which is too high for this one.</summary>
    [Fact]
    public void The_photo_is_sharp_down_to_the_seam()
    {
        using var canvas = SKBitmap.Decode(RunOff.Bake(Stripes(Wide, High)));

        foreach (var y in new[] { 20, 110, 125 })
            Assert.True(Contrast(canvas, y) > 150, $"row {y} was blurred: contrast {Contrast(canvas, y):0}");
    }

    /// <summary>Under the seam blur's reach, row h + k of the canvas is row h - 1 - k of the photo,
    /// turned over and blurred. A blur leaves a smooth ramp as it is, so on the gradient the two rows
    /// match; on the stripes the mirror is flat.</summary>
    [Fact]
    public void Under_the_photo_is_the_photo_turned_over_and_blurred()
    {
        using var ramp = SKBitmap.Decode(RunOff.Bake(Gradient(Wide, High)));
        foreach (var k in new[] { 80, 100 })
            Assert.InRange(RowMean(ramp, High + k) - RowMean(ramp, High - 1 - k), -4, 4);

        using var stripes = SKBitmap.Decode(RunOff.Bake(Stripes(Wide, High)));
        foreach (var k in new[] { 60, 80, 100 })
            Assert.True(Contrast(stripes, High + k) < 30,
                        $"row {High + k} of the mirror is not blurred: contrast {Contrast(stripes, High + k):0}");
    }

    // ── the layout ──

    /// <summary>The artist page's band is 4:3. A 1920 by 1080 photo on a band 440 wide is 247.5 tall,
    /// so 82.5 of the band's 330 is reflection, and the dissolve covers exactly that.</summary>
    [Fact]
    public void The_dissolve_covers_the_space_under_the_photo()
    {
        Assert.Equal(247.5, RunOff.PhotoHeight(440, 330, 16.0 / 9), 3);
        Assert.Equal(82.5, RunOff.FadeDepth(440, 330, 16.0 / 9), 3);
    }

    /// <summary>A photo nearly the box's own shape leaves a few points under it, and the dissolve does
    /// not shrink to those few points.</summary>
    [Fact]
    public void A_sliver_under_the_photo_still_gets_the_shortest_dissolve()
        => Assert.Equal(RunOff.MinFade, RunOff.FadeDepth(440, 330, 1.4));

    /// <summary>A box wider than the photo's shape has no room under it: the photo covers the box from
    /// the top, the mirror is below the box, and the dissolve is the shortest one.</summary>
    [Fact]
    public void A_box_wider_than_the_photo_shows_no_reflection()
    {
        Assert.True(RunOff.PhotoHeight(520, 260, 16.0 / 9) > 260);
        Assert.Equal(RunOff.MinFade, RunOff.FadeDepth(520, 260, 16.0 / 9));
    }

    /// <summary>The track pane's strip slides open from nothing. While it is shorter than the shortest
    /// dissolve, the dissolve is the whole strip.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 20)]
    [InlineData(47, 47)]
    public void The_dissolve_is_never_deeper_than_the_box(double boxHeight, double depth)
        => Assert.Equal(depth, RunOff.FadeDepth(340, boxHeight, 16.0 / 9));

    /// <summary>No jump in the dissolve while the strip slides: one point more strip is at most one
    /// point more dissolve.</summary>
    [Fact]
    public void The_dissolve_moves_smoothly_as_the_strip_opens()
    {
        var last = RunOff.FadeDepth(340, 0, 16.0 / 9);
        for (var h = 1; h <= 260; h++)
        {
            var depth = RunOff.FadeDepth(340, h, 16.0 / 9);
            Assert.InRange(depth - last, 0, 1.0001);
            last = depth;
        }
    }

    /// <summary>A box narrower than the canvas's own shape (half the photo's aspect) scales the canvas
    /// to its height, so the photo is half the box tall and wider than the box.</summary>
    [Fact]
    public void In_a_very_narrow_box_the_photo_is_half_the_box()
        => Assert.Equal(130, RunOff.PhotoHeight(200, 260, 16.0 / 9), 3);

    // ── helpers ──

    /// <summary>A photo whose every row is a different grey, dark at the top and light at the foot, so a
    /// row can be told from the one it is mirrored from. Smooth, so the JPEG keeps it.</summary>
    private static byte[] Gradient(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            var grey = (byte)(20 + 200 * y / Math.Max(1, height - 1));
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor(grey, grey, grey));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>A photo of horizontal black and white stripes three pixels high: any blur flattens
    /// it.</summary>
    private static byte[] Stripes(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, y / 3 % 2 == 0 ? SKColors.Black : SKColors.White);

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    /// <summary>The spread between the darkest and lightest row among the six from
    /// <paramref name="y"/>, which covers one black and one white stripe.</summary>
    private static double Contrast(SKBitmap bitmap, int y)
    {
        var rows = Enumerable.Range(y, 6).Select(row => RowMean(bitmap, row)).ToList();
        return rows.Max() - rows.Min();
    }

    /// <summary>The mean red channel along one row, away from the side edges.</summary>
    private static double RowMean(SKBitmap bitmap, int y)
    {
        double sum = 0;
        var n = 0;
        for (var x = 8; x < bitmap.Width - 8; x++, n++)
            sum += bitmap.GetPixel(x, y).Red;
        return sum / n;
    }
}

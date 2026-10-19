using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="CoverColor.Ground"/>: the tone the whole window stands on. It is the average colour of the
/// picture. Averaging cancels out the hues, so the result is muted. The rule keeps the picture's lightness,
/// which is its daylight or its night. It is not the dominant colour, which is the most saturated colour in
/// the frame and would paint the app in poster colours.
/// </summary>
public class CoverColorGroundTests
{
    /// <summary>HSL lightness, the measure the ground is held to.</summary>
    private static double Lightness(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
    }

    [Fact]
    public void A_night_photograph_leaves_the_app_dark()
    {
        var ground = CoverColor.Ground(Color.FromArgb(255, 28, 30, 40));

        Assert.True(Lightness(ground) < 0.12, $"the ground came out at {Lightness(ground):F2}");
    }

    [Fact]
    public void Daylight_lifts_the_ground()
    {
        var night = CoverColor.Ground(Color.FromArgb(255, 28, 30, 40));
        var daylight = CoverColor.Ground(Color.FromArgb(255, 150, 148, 130));

        Assert.True(Lightness(daylight) > Lightness(night) + 0.1,
                    $"daylight gave {Lightness(daylight):F2} against night's {Lightness(night):F2}");
    }

    [Fact]
    public void A_snowfield_does_not_turn_the_library_into_paper()
    {
        var ground = CoverColor.Ground(Color.FromArgb(255, 240, 244, 250));

        Assert.True(Lightness(ground) < 0.45, $"the ground came out at {Lightness(ground):F2}");
    }

    [Fact]
    public void A_muted_average_is_left_as_it_is()
    {
        // A muted average that is already a suitable ground: an olive grey with saturation 0.08. The rule
        // should leave such a colour alone, apart from a small change in its weight.
        var ground = CoverColor.Ground(Color.FromArgb(255, 112, 110, 95));

        Assert.InRange(Lightness(ground), 0.30, 0.38);
        Assert.True(ground.R > ground.B, "the olive was lost");
        Assert.True(Math.Max(ground.R, ground.G) - ground.B < 30, $"it came back louder than it went in: {ground}");
    }

    [Fact]
    public void The_hue_survives()
    {
        var ground = CoverColor.Ground(Color.FromArgb(255, 40, 60, 150));

        Assert.True(ground.B > ground.R && ground.B > ground.G, $"the blue was lost: {ground}");
    }

    [Fact]
    public void A_grey_average_stays_grey()
    {
        var ground = CoverColor.Ground(Color.FromArgb(255, 128, 128, 128));

        Assert.Equal(ground.R, ground.G);
        Assert.Equal(ground.G, ground.B);
    }

    [Fact]
    public void A_picture_shot_under_one_colour_of_light_is_still_taken_down_to_a_tone()
    {
        // An average this saturated means the whole photograph has that colour, for example a stage lit in
        // red. Even then the app is only tinted, not painted.
        var ground = CoverColor.Ground(Color.FromArgb(255, 200, 30, 30));

        var max = Math.Max(ground.R, Math.Max(ground.G, ground.B));
        var min = Math.Min(ground.R, Math.Min(ground.G, ground.B));
        Assert.True(max - min < 70, $"the ground is still paint: {ground}");
    }

    [Fact]
    public void The_average_is_the_whole_picture()
    {
        var dark = CoverColor.Average(Png(0x00, 0x00, 0x00, 0xFF));
        var light = CoverColor.Average(Png(0xFF, 0xFF, 0xFF, 0x00));

        Assert.NotNull(dark);
        Assert.NotNull(light);
        Assert.True(Lightness(dark.Value) < Lightness(light.Value),
                    $"{Lightness(dark.Value):F2} was not darker than {Lightness(light.Value):F2}");
    }

    /// <summary>A 2x2 greyscale image with the given pixel values, PNG-encoded.</summary>
    private static byte[] Png(byte a, byte b, byte c, byte d)
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 2);
        byte[] values = [a, b, c, d];
        for (var i = 0; i < 4; i++)
            bitmap.SetPixel(i % 2, i / 2, new SkiaSharp.SKColor(values[i], values[i], values[i]));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

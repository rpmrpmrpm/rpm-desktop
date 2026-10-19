using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="CoverColor.Family"/>: a colour taken from a photograph and moved towards one of the app's ten
/// palette tints. It keeps the hue family of the picture (a red cover gives a red pane). It does not keep
/// the exact hue angle, so the result does not depend on the exact colour of the photo. It must not change
/// the lightness, which is the photo's exposure and is what every caller dims against.
/// </summary>
public class CoverColorFamilyTests
{
    private static (double Hue, double Saturation, double Lightness) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        var span = max - min;
        if (span <= 0)
            return (0, 0, lightness);

        var saturation = lightness > 0.5 ? span / (2 - max - min) : span / (max + min);
        var hue = max == r ? ((g - b) / span) + (g < b ? 6 : 0)
                : max == g ? ((b - r) / span) + 2
                           : ((r - g) / span) + 4;
        return (hue * 60, saturation, lightness);
    }

    /// <summary>The shorter way round the hue wheel. This is the correct way to compare two hues.</summary>
    private static double Apart(double a, double b) => Math.Abs((((b - a) % 360) + 540) % 360 - 180);

    /// <summary>Azure, the palette's own blue.</summary>
    private static readonly Color Azure = Color.FromArgb(0xFF, 0x03, 0x71, 0xD0);

    [Fact]
    public void A_photograph_lands_nearer_the_palette_than_it_started()
    {
        // A dull sea blue, well off the palette's azure.
        var photograph = Color.FromArgb(255, 40, 96, 150);
        var moved = CoverColor.Family(photograph);

        var (was, _, _) = Hsl(photograph);
        var (now, _, _) = Hsl(moved);
        var (kin, _, _) = Hsl(Azure);

        Assert.True(Apart(now, kin) < Apart(was, kin),
                    $"started {Apart(was, kin):F1} degrees off azure and ended {Apart(now, kin):F1}");
    }

    [Fact]
    public void It_does_not_go_all_the_way_there()
    {
        // Two photographs of the same broad hue must not give the same colour.
        var one = CoverColor.Family(Color.FromArgb(255, 40, 96, 150));
        var two = CoverColor.Family(Color.FromArgb(255, 30, 120, 190));

        Assert.NotEqual(one, two);
    }

    [Fact]
    public void A_red_stays_red()
    {
        var moved = CoverColor.Family(Color.FromArgb(255, 190, 40, 30));
        var (hue, _, _) = Hsl(moved);

        // Anywhere in the red end of the wheel: nowhere near green or blue.
        Assert.True(hue < 40 || hue > 330, $"a red came back at hue {hue:F0}");
    }

    [Fact]
    public void The_exposure_is_left_alone()
    {
        var photograph = Color.FromArgb(255, 40, 96, 150);
        var moved = CoverColor.Family(photograph);

        Assert.Equal(Hsl(photograph).Lightness, Hsl(moved).Lightness, 2);
    }

    [Fact]
    public void A_grey_is_left_where_it_is()
    {
        // A grey has no hue to move. Pulling it towards a palette tint would paint it, not tint it.
        var grey = Color.FromArgb(255, 122, 120, 124);

        Assert.Equal(grey, CoverColor.Family(grey));
    }

    [Fact]
    public void A_palette_colour_comes_back_as_itself()
    {
        var moved = CoverColor.Family(Azure);

        Assert.True(Apart(Hsl(moved).Hue, Hsl(Azure).Hue) < 1,
                    $"azure moved to hue {Hsl(moved).Hue:F0} from {Hsl(Azure).Hue:F0}");
    }
}

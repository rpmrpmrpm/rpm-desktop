using System.Numerics;
using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The room is lit in the record's own colours. The failure to prevent is a colour on screen that is
/// nowhere on the sleeve. It happens the same way every time: two of the three slots come out too
/// alike, something has to fill the empty one, and a hue is TURNED to fill it. A rotation always
/// produces a colour, but one the record never had.
/// </summary>
public class RoomPaletteTests
{
    // The gold of a sleeve like 1991: the dominant tone and the vivid accent of that cover are both this,
    // because the cover really is mostly one colour.
    private static readonly Vector3 Gold = new(0.98f, 0.76f, 0.09f);

    // …and the red of the hair on it, which the extractor hands over in the third slot.
    private static readonly Vector3 Red = new(0.72f, 0.09f, 0.11f);

    /// <summary>Where a colour sits on the wheel, in degrees. 0 red, 60 yellow, 120 green, 240
    /// blue.</summary>
    private static double Hue(Vector3 c)
    {
        float max = Math.Max(c.X, Math.Max(c.Y, c.Z)), min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        if (max - min < 1e-4f)
            return -1;   // grey has no hue

        double h = max == c.X ? 60 * (((c.Y - c.Z) / (max - min) + 6) % 6)
                 : max == c.Y ? 60 * ((c.Z - c.X) / (max - min) + 2)
                              : 60 * ((c.X - c.Y) / (max - min) + 4);
        return (h + 360) % 360;
    }

    private static bool IsGreen(Vector3 c) => Hue(c) is > 75 and < 165;

    [Fact]
    public void A_one_colour_sleeve_takes_its_second_colour_from_the_sleeve()
    {
        // 1991 is a gold sleeve with red hair and black gloves. Its dominant colour and its accent are
        // both gold. Turning the accent forty degrees to make a second colour would give GREEN, which
        // is not on the record anywhere, while the red sits unused one slot along. So the second colour
        // is taken from the sleeve.
        var room = RoomPalette.From(Gold, Gold, Red);

        Assert.False(IsGreen(room.Glow), $"the accent came out green (h={Hue(room.Glow):F0}) for a gold sleeve");
        Assert.True(Hue(room.Glow) is < 40 or > 330,
                    $"the accent should be the sleeve's own red, and it is h={Hue(room.Glow):F0}");
    }

    [Fact]
    public void The_dominant_tone_is_still_the_sleeve_s_own()
    {
        // Whatever happens to the accent, the tint is the record's dominant colour and stays it.
        var room = RoomPalette.From(Gold, Gold, Red);

        Assert.True(Hue(room.Tint) is > 30 and < 70, $"the gold came out at h={Hue(room.Tint):F0}");
    }

    [Fact]
    public void A_third_colour_spent_on_the_accent_is_not_spent_twice()
    {
        // Promoted to the accent, it cannot also be the shade: two families painted the same colour are
        // one family, and the promotion avoids that.
        var room = RoomPalette.From(Gold, Gold, Red);

        Assert.True(Vector3.Distance(Vector3.Normalize(room.Glow), Vector3.Normalize(room.Shade)) > 0.22f,
                    "the accent and the shade came out as the same colour");
    }

    [Fact]
    public void A_sleeve_with_two_real_colours_keeps_both()
    {
        // The ordinary case: a cover of gold and red puts gold and red on the screen, and the
        // promotion never fires because the accent is already a second colour.
        var room = RoomPalette.From(Gold, Red, null);

        Assert.True(Hue(room.Tint) is > 30 and < 70, $"the tint is h={Hue(room.Tint):F0}, not the gold");
        Assert.True(Hue(room.Glow) is < 40 or > 330, $"the glow is h={Hue(room.Glow):F0}, not the red");
    }

    [Fact]
    public void A_sleeve_with_genuinely_one_colour_gets_a_grey_shade()
    {
        // No hue is turned to make a second colour: the glow keeps the sleeve's gold and the shade is grey.
        var room = RoomPalette.From(Gold, Gold, null);

        Assert.True(Hue(room.Glow) is > 30 and < 70, $"the glow is h={Hue(room.Glow):F0}, not the gold");
        Assert.Equal(RoomPalette.ShadeGrey, room.Shade);
    }

    [Theory]
    [InlineData(0.72f, 0.09f, 0.11f)]   // red
    [InlineData(0.36f, 0.28f, 0.20f)]   // brown
    [InlineData(0.90f, 0.50f, 0.15f)]   // orange
    [InlineData(0.90f, 0.42f, 0.62f)]   // pink
    public void A_warm_sleeve_with_no_third_colour_puts_no_green_on_screen(float r, float g, float b)
    {
        // If the shade were the dominant colour turned 152 degrees, then for red, orange or brown it
        // would be green or teal, and the second family of figures would come out in that colour.
        var sleeve = new Vector3(r, g, b);
        var room = RoomPalette.From(sleeve, sleeve, null);

        foreach (var colour in new[] { room.Tint, room.Glow, room.Shade })
            Assert.False(IsGreen(colour), $"{colour} is green (h={Hue(colour):F0})");
    }

    [Fact]
    public void A_sleeve_with_no_colour_at_all_gets_the_app_s_own()
    {
        Assert.Equal(RoomPalette.Fallback, RoomPalette.From(null, null, null));
    }
}

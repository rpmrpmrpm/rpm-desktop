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

    /// <summary>The two lights by name. The rules about which colours reach the screen hold under both,
    /// so those tests run under both.</summary>
    private static RoomLight Light(string name) => name switch
    {
        nameof(RoomLight.Stage) => RoomLight.Stage,
        nameof(RoomLight.Shelf) => RoomLight.Shelf,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    public static TheoryData<string> Lights => [nameof(RoomLight.Stage), nameof(RoomLight.Shelf)];

    /// <summary>How far a colour is from grey: its brightest channel less its darkest.</summary>
    private static float Chroma(Vector3 c) => Math.Max(c.X, Math.Max(c.Y, c.Z)) - Math.Min(c.X, Math.Min(c.Y, c.Z));

    private static float Peak(Vector3 c) => Math.Max(c.X, Math.Max(c.Y, c.Z));

    /// <summary>The distance between two hues in degrees, the short way round.</summary>
    private static double HueGap(Vector3 a, Vector3 b)
    {
        var d = Math.Abs(Hue(a) - Hue(b)) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void A_one_colour_sleeve_takes_its_second_colour_from_the_sleeve(string light)
    {
        // 1991 is a gold sleeve with red hair and black gloves. Its dominant colour and its accent are
        // both gold. Turning the accent forty degrees to make a second colour would give GREEN, which
        // is not on the record anywhere, while the red sits unused one slot along. So the second colour
        // is taken from the sleeve.
        var room = RoomPalette.From(Gold, Gold, Red, Light(light));

        Assert.False(IsGreen(room.Glow), $"the accent came out green (h={Hue(room.Glow):F0}) for a gold sleeve");
        Assert.True(Hue(room.Glow) is < 40 or > 330,
                    $"the accent should be the sleeve's own red, and it is h={Hue(room.Glow):F0}");
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void The_dominant_tone_is_still_the_sleeve_s_own(string light)
    {
        // Whatever happens to the accent, the tint is the record's dominant colour and stays it.
        var room = RoomPalette.From(Gold, Gold, Red, Light(light));

        Assert.True(Hue(room.Tint) is > 30 and < 70, $"the gold came out at h={Hue(room.Tint):F0}");
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void A_third_colour_spent_on_the_accent_is_not_spent_twice(string light)
    {
        // Promoted to the accent, it cannot also be the shade: two families painted the same colour are
        // one family, and the promotion avoids that.
        var room = RoomPalette.From(Gold, Gold, Red, Light(light));

        Assert.True(Vector3.Distance(Vector3.Normalize(room.Glow), Vector3.Normalize(room.Shade)) > 0.22f,
                    "the accent and the shade came out as the same colour");
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void A_sleeve_with_two_real_colours_keeps_both(string light)
    {
        // The ordinary case: a cover of gold and red puts gold and red on the screen, and the
        // promotion never fires because the accent is already a second colour.
        var room = RoomPalette.From(Gold, Red, null, Light(light));

        Assert.True(Hue(room.Tint) is > 30 and < 70, $"the tint is h={Hue(room.Tint):F0}, not the gold");
        Assert.True(Hue(room.Glow) is < 40 or > 330, $"the glow is h={Hue(room.Glow):F0}, not the red");
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void A_sleeve_with_genuinely_one_colour_gets_a_grey_shade(string light)
    {
        // No hue is turned to make a second colour: the glow keeps the sleeve's gold and the shade is grey.
        var room = RoomPalette.From(Gold, Gold, null, Light(light));

        Assert.True(Hue(room.Glow) is > 30 and < 70, $"the glow is h={Hue(room.Glow):F0}, not the gold");
        Assert.Equal(Light(light).Grey, room.Shade);
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

        foreach (var light in new[] { RoomLight.Stage, RoomLight.Shelf })
        {
            var room = RoomPalette.From(sleeve, sleeve, null, light);

            foreach (var colour in new[] { room.Tint, room.Glow, room.Shade })
                Assert.False(IsGreen(colour), $"{colour} is green (h={Hue(colour):F0})");
        }
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void A_sleeve_with_no_colour_at_all_gets_the_app_s_own(string light)
    {
        Assert.Equal(Light(light).Empty, RoomPalette.From(null, null, null, Light(light)));
    }

    /// <summary>
    /// Pegasus's room is lit exactly as it was before the library got a light of its own.
    /// </summary>
    /// <remarks>
    /// The numbers are written out, not read from <see cref="RoomLight.Stage"/>. A change to the library's
    /// light must never reach Pegasus, and a test that read the light it checks could not see that
    /// happen.
    /// </remarks>
    [Fact]
    public void Pegasus_keeps_its_own_light()
    {
        var room = RoomPalette.From(Gold, Red, null, RoomLight.Stage);

        AssertNear(new Vector3(0.6200f, 0.47155f, 0f), room.Tint);
        AssertNear(new Vector3(0.9000f, 0.020757f, 0.048669f), room.Glow);
        AssertNear(new Vector3(0.70f), room.Shade);
        AssertNear(new Vector3(0.0934f, 0.080394f, 0.033538f), room.Ground);
        Assert.Equal(RoomPalette.Fallback, RoomLight.Stage.Empty);

        static void AssertNear(Vector3 expected, Vector3 actual)
            => Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"expected {expected}, got {actual}");
    }

    /// <summary>
    /// The library's room is calmer than Pegasus's: every colour is nearer grey and less bright.
    /// </summary>
    /// <remarks>
    /// This is the reason the library has a light of its own. At Pegasus's strength a card on the home
    /// screen is brighter and more saturated than every album cover around it.
    /// </remarks>
    [Theory]
    [InlineData(0.98f, 0.76f, 0.09f, 0.72f, 0.09f, 0.11f)]   // gold and red
    [InlineData(0.36f, 0.28f, 0.20f, 0.85f, 0.45f, 0.15f)]   // brown and orange
    [InlineData(0.11f, 0.16f, 0.29f, 0.25f, 0.65f, 0.79f)]   // navy and cyan
    [InlineData(0.92f, 0.08f, 0.31f, 0.67f, 0.32f, 0.55f)]   // crimson and magenta, two palette colours
    public void The_library_room_is_calmer_than_Pegasus(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        var tone = new Vector3(r1, g1, b1);
        var accent = new Vector3(r2, g2, b2);
        var stage = RoomPalette.From(tone, accent, null, RoomLight.Stage);
        var shelf = RoomPalette.From(tone, accent, null, RoomLight.Shelf);

        foreach (var (name, loud, calm) in new[] { ("tint", stage.Tint, shelf.Tint), ("glow", stage.Glow, shelf.Glow) })
        {
            Assert.True(Peak(calm) < Peak(loud), $"the library's {name} {calm} is not darker than Pegasus's {loud}");
            Assert.True(Chroma(calm) < Chroma(loud), $"the library's {name} {calm} is not nearer grey than Pegasus's {loud}");
        }
    }

    /// <summary>
    /// The library's room keeps the hue that was read off the cover. Only the strength changes, so the
    /// card is the colour of the record and of the list's cover beside it.
    /// </summary>
    [Theory]
    [InlineData(0.36f, 0.28f, 0.20f)]   // brown
    [InlineData(0.11f, 0.16f, 0.29f)]   // navy
    [InlineData(0.68f, 0.82f, 0.22f)]   // lime, a palette colour
    [InlineData(0.67f, 0.32f, 0.55f)]   // magenta, a palette colour
    public void The_library_room_keeps_the_cover_s_hue(float r, float g, float b)
    {
        var read = new Vector3(r, g, b);
        var room = RoomPalette.From(read, read, null, RoomLight.Shelf);

        Assert.True(HueGap(read, room.Tint) < 3, $"the tint moved to h={Hue(room.Tint):F0} from h={Hue(read):F0}");
        Assert.True(HueGap(read, room.Glow) < 3, $"the glow moved to h={Hue(room.Glow):F0} from h={Hue(read):F0}");
    }

    /// <summary>
    /// Between the figures, a library card is a dark version of the cover's colour, as a genre card is.
    /// Pegasus's ground is nearly black, and bright figures on black are what look lit from inside.
    /// </summary>
    [Fact]
    public void The_library_ground_carries_the_cover_s_colour()
    {
        var stage = RoomPalette.From(Gold, Red, null, RoomLight.Stage);
        var shelf = RoomPalette.From(Gold, Red, null, RoomLight.Shelf);

        Assert.True(Peak(shelf.Ground) > Peak(stage.Ground) * 1.5f,
                    $"the library's ground {shelf.Ground} is as dark as Pegasus's {stage.Ground}");
        Assert.True(HueGap(Gold, shelf.Ground) < 10, $"the ground is h={Hue(shelf.Ground):F0}, not the gold");
    }

    /// <summary>A playlist with no cover is the app's blue in the library too, at the library's
    /// strength.</summary>
    [Fact]
    public void The_library_s_empty_room_is_the_app_s_blue_made_calmer()
    {
        var empty = RoomLight.Shelf.Empty;

        Assert.True(Hue(empty.Tint) is > 195 and < 220, $"the empty tint is h={Hue(empty.Tint):F0}, not the app's blue");
        Assert.True(Peak(empty.Glow) < Peak(RoomPalette.Fallback.Glow), "the empty room's glow is as bright as Pegasus's");
    }
}

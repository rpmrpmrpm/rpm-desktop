using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="RpmPalette.Nearest"/>: which of the app's ten accents a photograph's colour is.
///
/// <para>The palette itself is a XAML resource and needs a running app to read. So these tests check the
/// RULE rather than the loaded set: the Oklab reading each colour resolves to, and the weighting that
/// decides what "nearest" means. The distance is re-implemented here from those readings. That is the
/// point: if the weighting changes, these numbers show what changed.</para>
/// </summary>
public class RpmPaletteTests
{
    // The asset's ten, copied by name. They are not read from the asset, because the resource dictionary
    // exists only inside a running app. See the class note.
    private static readonly (string Name, Color Colour)[] Palette =
    [
        ("Vermilion", Rgb(0xD2, 0x31, 0x0E)),
        ("Rust", Rgb(0xCC, 0x6E, 0x11)),
        ("Gold", Rgb(0xEF, 0xAF, 0x12)),
        ("Lime", Rgb(0xAD, 0xD0, 0x38)),
        ("Forest", Rgb(0x13, 0x4C, 0x27)),
        ("Azure", Rgb(0x03, 0x71, 0xD0)),
        ("Indigo", Rgb(0x47, 0x50, 0xAE)),
        ("Magenta", Rgb(0xAC, 0x52, 0x8D)),
        ("Crimson", Rgb(0xEA, 0x15, 0x4E)),
        ("Red", Rgb(0xEE, 0x07, 0x09)),
    ];

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    /// <summary>The same sum <see cref="RpmPalette"/> makes, over the readings it makes it from. Empty
    /// for a tone with no colour in it to be near anything, which is the rule's own answer.</summary>
    private static string NearestName(Color colour)
    {
        var (l, chroma, hue) = RpmPalette.Oklch(colour);
        if (chroma < 0.018)
            return "";

        string best = "";
        var bestDistance = double.MaxValue;

        foreach (var (name, entry) in Palette)
        {
            var (el, ec, eh) = RpmPalette.Oklch(entry);

            var dh = Math.Abs(hue - eh);
            if (dh > Math.PI)
                dh = (2 * Math.PI) - dh;

            var dc = chroma - ec;
            var dl = (l - el) * 0.5;
            var distance = (dh * dh) + (dc * dc) + (dl * dl);

            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = name;
        }

        return best;
    }

    /// <summary>Every accent is its own nearest. Not a tautology worth skipping: it is what says the
    /// weighting has not been pushed so far that two of the ten have swallowed a third.</summary>
    [Fact]
    public void Every_palette_colour_answers_with_itself()
    {
        foreach (var (name, colour) in Palette)
            Assert.Equal(name, NearestName(colour));
    }

    /// <summary>A photograph is blue, or it is orange, and it lands on the accent a person would say it
    /// was.</summary>
    [Theory]
    [InlineData(0x7A, 0xC8, 0xFF, "Azure")]     // a bright blue backdrop
    [InlineData(0x6A, 0x4F, 0xB0, "Indigo")]    // and a violet one, which is the other blue
    [InlineData(0xFF, 0x8A, 0x2B, "Rust")]      // stage light, orange
    [InlineData(0xC9, 0x8E, 0x6A, "Rust")]      // and a skin tone, which is the same hue lit softly
    [InlineData(0xF2, 0xD9, 0x4E, "Gold")]      // a yellow backdrop
    [InlineData(0x8E, 0xC6, 0x3F, "Lime")]      // a green one, light
    [InlineData(0xE8, 0x3B, 0x5C, "Crimson")]   // a red jacket, pink-ish
    [InlineData(0x9B, 0x4F, 0xA0, "Magenta")]   // a purple wash
    [InlineData(0x8B, 0x1A, 0x12, "Vermilion")] // deep red
    [InlineData(0x5A, 0x10, 0x20, "Crimson")]   // maroon
    public void A_photographs_colour_lands_on_the_accent_it_reads_as(byte r, byte g, byte b, string expected)
        => Assert.Equal(expected, NearestName(Rgb(r, g, b)));

    /// <summary>
    /// A blue photograph is blue however it was lit. This is why the match asks about hue before anything
    /// else.
    ///
    /// <para>A straight distance is wrong here. With it, a dark navy comes out nearer FOREST than Azure. A
    /// weak blue and a dark green share only darkness, and in a straight sum that is the loudest term.</para>
    /// </summary>
    [Theory]
    [InlineData(0x7A, 0xC8, 0xFF)]   // bright
    [InlineData(0x0A, 0x4A, 0x8C)]   // vivid, and dark
    [InlineData(0x1E, 0x3A, 0x6B)]   // a night shot
    [InlineData(0x2A, 0x35, 0x4A)]   // and a muted navy, nearly grey
    public void A_blue_photograph_is_blue_however_it_was_lit(byte r, byte g, byte b)
        => Assert.Equal("Azure", NearestName(Rgb(r, g, b)));

    /// <summary>Lightness is weighted DOWN, not removed. Two greens of one hue at very different values are
    /// the pair the weight exists for. With the weight at zero they would collapse into one entry, and
    /// half the palette's greens would be unreachable.</summary>
    [Fact]
    public void A_dark_green_and_a_light_one_are_told_apart()
    {
        Assert.Equal("Forest", NearestName(Rgb(0x18, 0x55, 0x2E)));
        Assert.Equal("Lime", NearestName(Rgb(0xBC, 0xDC, 0x55)));
    }

    /// <summary>Oklab's own anchor: white is lightness 1 with no chroma at all. What says the transfer
    /// curve is being undone and the matrices are the right way round.</summary>
    [Fact]
    public void White_reads_as_lightness_one_and_no_colour()
    {
        var (l, a, b) = RpmPalette.Oklab(255, 255, 255);

        Assert.Equal(1.0, l, 3);
        Assert.Equal(0.0, a, 3);
        Assert.Equal(0.0, b, 3);
    }

    [Fact]
    public void Black_reads_as_nothing_at_all()
    {
        var (l, a, b) = RpmPalette.Oklab(0, 0, 0);

        Assert.Equal(0.0, l, 3);
        Assert.Equal(0.0, a, 3);
        Assert.Equal(0.0, b, 3);
    }

    /// <summary>
    /// A photograph with no colour in it is not nearer one hue than another, and is left alone.
    ///
    /// <para>Snapped anyway, it lands on whichever accent is least saturated, which is Forest, the dark
    /// green. So every monochrome press shot on the wall would come back washed in dark green for no
    /// reason anyone could give. A grey photograph gets a grey box.</para>
    /// </summary>
    [Theory]
    [InlineData(0x80, 0x80, 0x80)]   // a true grey
    [InlineData(0x8C, 0x84, 0x7C)]   // and the warm one a black-and-white scan actually reports
    [InlineData(0x78, 0x7E, 0x86)]   // and a cool one
    public void A_photograph_with_no_colour_in_it_is_left_alone(byte r, byte g, byte b)
        => Assert.Equal("", NearestName(Rgb(r, g, b)));

    /// <summary>The line is between a grey and a cast, not between a cast and a poster: a pale beige and
    /// a muted navy are colours, and the wall places them.</summary>
    [Theory]
    [InlineData(0xD6, 0xCE, 0xBE)]   // pale beige
    [InlineData(0x2A, 0x35, 0x4A)]   // muted navy
    [InlineData(0x6B, 0x6E, 0x50)]   // muted olive
    public void A_photograph_with_a_cast_to_it_is_still_placed(byte r, byte g, byte b)
        => Assert.NotEqual("", NearestName(Rgb(r, g, b)));
}

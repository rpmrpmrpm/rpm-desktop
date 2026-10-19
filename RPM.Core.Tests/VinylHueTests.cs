using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// What colour a record gets pressed in, given what its sleeve is made of. The case that matters
/// is the negative one: a sleeve with no colour must press black, not a hue read out of noise.
/// </summary>
public class VinylHueTests
{
    private static readonly Rgb Vivid = new(0xEA, 0x15, 0x4E);    // a strong crimson
    private static readonly Rgb Muted = new(0xB0, 0x8A, 0x5C);    // a washed seventies tan
    private static readonly Rgb Grey = new(0x80, 0x82, 0x84);     // a scan of a mono sleeve
    private static readonly Rgb Shadow = new(0x14, 0x08, 0x0A);   // colour read out of near-black

    // ── the ordinary case ──

    [Fact]
    public void The_highlight_wins_over_the_dominant()
    {
        // The dominant tone of a sleeve is very often the paper it was printed on.
        Assert.Equal(Vivid, VinylHue.Choose(Vivid, Muted));
    }

    [Fact]
    public void The_dominant_stands_in_when_there_is_no_highlight()
    {
        Assert.Equal(Muted, VinylHue.Choose(null, Muted));
    }

    [Fact]
    public void A_muted_sleeve_is_still_a_colour()
    {
        // Subtle is not absent. Pressing a washed sleeve black would treat one as the other.
        Assert.Equal(Muted, VinylHue.Choose(Muted, Muted));
    }

    // ── press it black ──

    [Fact]
    public void No_art_at_all_is_black()
    {
        Assert.Null(VinylHue.Choose(null, null));
    }

    [Fact]
    public void A_black_and_white_sleeve_is_black()
    {
        Assert.Null(VinylHue.Choose(Grey, Grey));
    }

    [Fact]
    public void A_colour_read_out_of_near_blackness_is_black()
    {
        // The tint normalises brightness, so a hue from the shadows would come back at full strength:
        // an almost-all-shadow sleeve would press a vivid record.
        Assert.Null(VinylHue.Choose(Shadow, Shadow));
    }

    [Fact]
    public void A_grey_highlight_falls_through_to_a_usable_dominant()
    {
        Assert.Equal(Vivid, VinylHue.Choose(Grey, Vivid));
    }

    [Fact]
    public void Pure_white_and_pure_black_are_both_black()
    {
        Assert.Null(VinylHue.Choose(new Rgb(255, 255, 255), null));
        Assert.Null(VinylHue.Choose(new Rgb(0, 0, 0), null));
    }

    // ── the measurements the rule is built on ──

    [Theory]
    [InlineData(0xFF, 0x00, 0x00, 1.0)]
    [InlineData(0x80, 0x80, 0x80, 0.0)]
    [InlineData(0x00, 0x00, 0x00, 0.0)]   // no divide by zero on black
    public void Saturation_is_hsv_s(byte r, byte g, byte b, double expected)
    {
        Assert.Equal(expected, new Rgb(r, g, b).Saturation, 3);
    }

    [Fact]
    public void Brightness_is_the_brightest_channel()
    {
        Assert.Equal(1.0, new Rgb(0x10, 0xFF, 0x20).Brightness, 3);
        Assert.Equal(0.0, new Rgb(0, 0, 0).Brightness, 3);
    }

    /// <summary>The thresholds are the rule. A change to either changes which sleeves press black, so
    /// they are pinned.</summary>
    [Fact]
    public void A_colour_exactly_on_the_thresholds_is_usable()
    {
        // Saturation 0.2 and brightness 0.2, just clear of both floors.
        var onEdge = new Rgb(0x33, 0x29, 0x29);
        Assert.True(onEdge.Saturation >= VinylHue.MinSaturation);
        Assert.True(onEdge.Brightness >= VinylHue.MinBrightness);
        Assert.True(VinylHue.Usable(onEdge));
    }
}

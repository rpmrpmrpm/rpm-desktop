using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

// A figure is drawn in colours from RpmPalette. RpmPalette reads its asset through a ResourceDictionary,
// which needs a dispatcher that this test host never has. See PaletteStandIn for why. See
// PaletteStandInCollection for why this class cannot run alongside another class that also holds one.
[Collection(PaletteStandInCollection.Name)]
public class PlaylistFigureTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    public void Dispose()
    {
        _palette.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly uint[] Seeds = [7u, 4242u, 98765u, 31337u, 555u, 123456789u, 1u, 2u, 3u];

    private static double Light(SKColor colour)
        => (0.2126 * colour.Red) + (0.7152 * colour.Green) + (0.0722 * colour.Blue);

    /// <summary>How much light a whole cover carries, and how much its middle and its four corners do.
    /// The three numbers a family can be recognised by without pinning its bytes: see
    /// <see cref="The_sun_is_brightest_in_its_middle"/>.</summary>
    private static (double Mean, double Centre, double Corners) Reading(int style, uint seed)
    {
        var cover = PlaylistArt.Figure(style, seed, CoverMark.None, mood: null).Cover;
        Assert.NotNull(cover);

        using var bitmap = SKBitmap.Decode(cover);
        Assert.True(bitmap.Width >= 64 && bitmap.Height >= 64);

        var mean = 0d;
        var counted = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                mean += Light(bitmap.GetPixel(x, y));
                counted++;
            }
        }

        var inset = bitmap.Width / 9;
        var far = bitmap.Width - inset;
        var corners = (Light(bitmap.GetPixel(inset, inset))
                       + Light(bitmap.GetPixel(far, inset))
                       + Light(bitmap.GetPixel(inset, far))
                       + Light(bitmap.GetPixel(far, far))) / 4;

        return (mean / counted, Light(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2)), corners);
    }

    // A figure is chosen by two numbers, not by a picture. The same two numbers must give the same picture
    // on every machine, today and after a restart. Each family is checked by its number. A family that
    // reads the clock or an unseeded random number would fail here.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void The_same_style_and_seed_draw_the_same_picture(int style)
    {
        var first = PlaylistArt.Figure(style, 4242u, CoverMark.None, mood: null);
        var second = PlaylistArt.Figure(style, 4242u, CoverMark.None, mood: null);

        Assert.NotNull(first.Cover);
        Assert.Equal(first.Cover, second.Cover);
    }

    [Fact]
    public void Every_family_off_one_name_is_a_different_picture()
    {
        var seeds = new uint[] { 11u, 22u, 33u };
        var expected = PlaylistArt.Styles * seeds.Length;
        var drawn = (from style in Enumerable.Range(0, PlaylistArt.Styles)
                     from seed in seeds
                     select Convert.ToBase64String(PlaylistArt.Figure(style, seed, CoverMark.None, mood: null).Cover!)).ToList();

        Assert.Equal(expected, drawn.Count);
        Assert.Equal(expected, drawn.Distinct().Count());
    }

    // There are eight families. The picker's tile grid and this test must both agree with
    // PlaylistFigure.Families. A change to one without the other is the drift this check catches.
    [Fact]
    public void There_are_eight_families()
        => Assert.Equal(8, PlaylistArt.Styles);

    // This test does not pin bytes. Hard geometry drawn by a new Skia version can differ at the pixel
    // level, and a picture of blurs and noise cannot promise byte-equal output across a library upgrade.
    // What it does check is that family zero is a source of light in the middle of the frame. So a
    // reordered switch still fails here, and a Skia that blurs slightly differently does not.
    [Fact]
    public void The_sun_is_brightest_in_its_middle()
    {
        foreach (var seed in Seeds)
        {
            var (_, centre, corners) = Reading(0, seed);
            Assert.True(centre > corners + 20,
                $"style 0, seed {seed}: centre {centre:F1} is not clear of the corners {corners:F1}");
        }
    }

    // And the same for the other end of the range: five is the lights out of focus, drawn on a night
    // ground, and it stays dark whatever colours it is handed. A swap that put a pale family at that
    // number fails this.
    [Fact]
    public void The_night_family_stays_dark()
    {
        foreach (var seed in Seeds)
        {
            var (mean, _, _) = Reading(5, seed);
            Assert.True(mean < 80, $"style 5, seed {seed}: mean light {mean:F1} is not a night");
        }
    }

    // Four is the leaning sheets. What makes it that rather than anything else in the set is that a line
    // drawn across the middle of it goes light, dark, light, dark several times over. This was measured,
    // not judged by eye: four crossings of its own mean, on a picture that is neither a night nor a sheet
    // of white.
    //
    // It does not prove the sheets LEAN, because a scan across the middle cannot tell a diagonal band from
    // an upright one, and it is not meant to. What it catches is a family with no banding at all landing on
    // this number. Nought, one, three and seven all come back with two crossings or fewer, whatever colours
    // they are handed.
    //
    // <b>It does not separate four from five or six.</b> Across all eight families, the night lights cross
    // up to eight times and the ripple ten, so a bound of five would not be a real line between them. The
    // sheets sit one crossing above such a bound. A colourway with a pale hot tone merges two of the six
    // tones, drops the stack to four, and fails. Both numbers here are wide on purpose. The arrangement is
    // written down and only the colours change, so this guards against a family swap, not against a
    // particular picture.
    [Fact]
    public void The_sheets_are_banded_across_the_frame()
    {
        foreach (var seed in Seeds)
        {
            var (mean, crossings) = Scan(4, seed);

            Assert.True(crossings >= 4,
                $"style 4, seed {seed}: {crossings} crossings is not a stack of sheets");
            Assert.True(mean is > 60 and < 200,
                $"style 4, seed {seed}: mean light {mean:F1} is neither the night nor the sheets");
        }
    }

    /// <summary>A line straight across the middle of a cover: how much light it carries on average, and how
    /// many times it crosses that average. A few counts of slack on each side mean that a gradient
    /// wandering over its own mean is not mistaken for a band.</summary>
    private static (double Mean, int Crossings) Scan(int style, uint seed)
    {
        var cover = PlaylistArt.Figure(style, seed, CoverMark.None, mood: null).Cover;
        Assert.NotNull(cover);

        using var bitmap = SKBitmap.Decode(cover);

        var row = new List<double>();
        for (var x = 0; x < bitmap.Width; x += 4)
            row.Add(Light(bitmap.GetPixel(x, bitmap.Height / 2)));

        var mean = row.Average();
        var crossings = 0;
        var above = row[0] > mean;

        foreach (var value in row)
        {
            if (above && value < mean - 6)
            {
                above = false;
                crossings++;
            }
            else if (!above && value > mean + 6)
            {
                above = true;
                crossings++;
            }
        }

        return (mean, crossings);
    }

    // An out-of-range style is wrapped, not refused. The style number has been through a database and a
    // picker, and a playlist with no picture is a worse result than the first family. Twenty is tested on
    // purpose, because some saved covers still hold that number.
    [Theory]
    [InlineData(99)]
    [InlineData(20)]
    [InlineData(-3)]
    public void An_unknown_style_still_draws_something(int style)
        => Assert.NotNull(PlaylistArt.Figure(style, 7u, CoverMark.None, mood: null).Cover);

    [Fact]
    public void A_figure_comes_with_the_wash_its_page_stands_on()
        => Assert.NotNull(PlaylistArt.Figure(0, 7u, CoverMark.None, mood: null).Wash);

    // The one case with no picture to draw. A grey cover would belong to no palette, so the answer is
    // nothing. The surface keeps whatever it already shows.
    [Fact]
    public void No_palette_is_no_cover()
    {
        var kept = RpmPalette.Override;
        try
        {
            RpmPalette.Override = [];
            Assert.Null(PlaylistArt.Figure(0, 7u, CoverMark.None, mood: null).Cover);
            Assert.Null(PlaylistArt.Figure(5, 4242u, CoverMark.None, mood: null).Cover);
        }
        finally
        {
            RpmPalette.Override = kept;
        }
    }
}

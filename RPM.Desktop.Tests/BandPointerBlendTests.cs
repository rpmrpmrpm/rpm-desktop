using RPM.Desktop.Presentation.Main.Library;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="BandPointer.Blend"/>: the triangle and the ring crossing into a sleeve's colour must cross the
/// way the drawer does, which is its ground fading in as a layer over the card plate.
///
/// <para>The fault these guard against: the colours were mixed channel by channel, alpha included, so from
/// the plate's thin white the triangle went half white half way through the cross, while the drawer beside
/// it never showed any white at all.</para>
/// </summary>
public class BandPointerBlendTests
{
    /// <summary>The card plate: white at 0x14.</summary>
    private static readonly Color Plate = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

    /// <summary>The dark row behind the drawer and the triangle.</summary>
    private static readonly Color Row = Color.FromArgb(0xFF, 3, 9, 23);

    private static readonly Color Sleeve = Color.FromArgb(0xFF, 116, 86, 55);

    /// <summary><paramref name="over"/> drawn on the opaque <paramref name="under"/>.</summary>
    private static (double R, double G, double B) Over(Color over, (double R, double G, double B) under)
    {
        var a = over.A / 255.0;
        return ((over.R * a) + (under.R * (1 - a)),
                (over.G * a) + (under.G * (1 - a)),
                (over.B * a) + (under.B * (1 - a)));
    }

    private static (double R, double G, double B) Of(Color c) => (c.R, c.G, c.B);

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void The_triangle_crosses_as_the_drawer_does(double t)
    {
        // The drawer: the plate on the row, then its ground at opacity t on top.
        var plateOnRow = Over(Plate, Of(Row));
        var drawer = (R: (Sleeve.R * t) + (plateOnRow.R * (1 - t)),
                      G: (Sleeve.G * t) + (plateOnRow.G * (1 - t)),
                      B: (Sleeve.B * t) + (plateOnRow.B * (1 - t)));

        // The triangle: one colour, drawn on the row.
        var triangle = Over(BandPointer.Blend(Plate, Sleeve, t), Of(Row));

        Assert.InRange(triangle.R, drawer.R - 1.5, drawer.R + 1.5);
        Assert.InRange(triangle.G, drawer.G - 1.5, drawer.G + 1.5);
        Assert.InRange(triangle.B, drawer.B - 1.5, drawer.B + 1.5);
    }

    [Fact]
    public void Between_two_opaque_colours_it_is_the_straight_mix()
    {
        var mixed = BandPointer.Blend(Color.FromArgb(255, 200, 100, 0), Color.FromArgb(255, 0, 100, 200), 0.5);

        Assert.Equal(Color.FromArgb(255, 100, 100, 100), mixed);
    }

    [Fact]
    public void The_ends_are_the_two_colours()
    {
        Assert.Equal(Plate, BandPointer.Blend(Plate, Sleeve, 0));
        Assert.Equal(Sleeve, BandPointer.Blend(Plate, Sleeve, 1));
    }
}

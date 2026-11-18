using RPM.Desktop.Presentation.Main.Library;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="BandPointer.Corners"/>: where the pointer triangle stops depends on its fill.
///
/// <para>The fault these guard against: a triangle that runs 2 px past the drawer's top edge, under the
/// drawer, while its fill is the card plate's thin white. That strip is painted twice, with the triangle's
/// alpha and then the drawer's, and shows as a bright line across the base of the triangle. With an opaque
/// fill the drawer hides the strip.</para>
/// </summary>
public class BandPointerOutlineTests
{
    private static readonly Color Plate = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    private static readonly Color Sleeve = Color.FromArgb(0xFF, 116, 86, 55);

    private static double Bottom(IReadOnlyList<Point> corners) => corners.Max(p => p.Y);

    /// <summary>The triangle's width at height <paramref name="y"/>, read off its two sides.</summary>
    private static double WidthAt(IReadOnlyList<Point> corners, double y)
    {
        var tip = corners.OrderBy(p => p.Y).First();
        var left = corners.OrderBy(p => p.X).First();
        var right = corners.OrderBy(p => p.X).Last();
        double At(Point end) => tip.X + ((end.X - tip.X) * (y - tip.Y) / (end.Y - tip.Y));
        return At(right) - At(left);
    }

    [Fact]
    public void An_opaque_fill_runs_past_the_drawer_edge()
    {
        Assert.True(Bottom(BandPointer.Corners(Sleeve)) > BandPointer.Join);
    }

    [Fact]
    public void A_translucent_fill_stops_at_the_drawer_edge()
    {
        Assert.Equal(BandPointer.Join, Bottom(BandPointer.Corners(Plate)));
    }

    [Fact]
    public void An_unknown_fill_stops_at_the_drawer_edge()
    {
        Assert.Equal(BandPointer.Join, Bottom(BandPointer.Corners(null)));
    }

    [Fact]
    public void Both_outlines_show_the_same_triangle_above_the_drawer()
    {
        var opaque = BandPointer.Corners(Sleeve);
        var translucent = BandPointer.Corners(Plate);

        Assert.Equal(opaque.OrderBy(p => p.Y).First(), translucent.OrderBy(p => p.Y).First());
        Assert.Equal(WidthAt(opaque, BandPointer.Join), WidthAt(translucent, BandPointer.Join), 6);
    }
}

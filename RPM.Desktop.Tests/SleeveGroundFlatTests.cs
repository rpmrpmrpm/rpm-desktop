using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="SleeveGround.Flat"/>: the three layers a record's ground is made of. They are the colour, the
/// blurred sleeve over it, and the veil inside the blur. Flat gives them as ONE colour, for the surfaces
/// that cannot hold three. The drawer's pointer triangle is ten pixels tall and the open card's ring is
/// two. Either one, painted a shade off the drawer's top edge, reads as a shadow under it.
///
/// <para>What these hold to is that it is not a fourth idea of the record's colour: it is what the
/// layers above it average to, so it moves when they move and agrees with them when they don't.</para>
/// </summary>
public class SleeveGroundFlatTests
{
    /// <summary>A ground whose sleeve is one colour all over. The mean and the band the top edge shows are
    /// then the same, which is the case these tests are about. That they can differ is the point of
    /// <see cref="The_edge_band_is_what_the_triangle_takes"/>.</summary>
    private static SleeveGround Of(Color mean, Color ground, double veil, bool wash) =>
        new(mean, mean, ground, wash ? [1, 2, 3] : null, veil);

    private static double Lightness(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return (Math.Max(r, Math.Max(g, b)) + Math.Min(r, Math.Min(g, b))) / 2;
    }

    [Fact]
    public void A_record_with_no_sleeve_flattens_to_its_ground()
    {
        var ground = Color.FromArgb(255, 40, 44, 52);
        var flat = Of(Color.FromArgb(255, 200, 100, 50), ground, veil: 0.4, wash: false).Flat;

        Assert.Equal(ground.R, flat.R);
        Assert.Equal(ground.G, flat.G);
        Assert.Equal(ground.B, flat.B);
    }

    [Fact]
    public void A_sleeve_pulls_the_flat_colour_halfway_towards_the_picture()
    {
        var ground = Color.FromArgb(255, 40, 40, 40);
        var flat = Of(Color.FromArgb(255, 200, 40, 40), ground, veil: 0, wash: true).Flat;

        // Half the ground and half the (unveiled) mean: 40 and 200 meet at 120.
        Assert.Equal(120, flat.R);
        Assert.Equal(40, flat.G);
    }

    [Fact]
    public void The_veil_takes_the_sleeve_back_down_again()
    {
        var ground = Color.FromArgb(255, 40, 40, 40);
        var mean = Color.FromArgb(255, 220, 220, 220);

        var bare = Of(mean, ground, veil: 0, wash: true).Flat;
        var veiled = Of(mean, ground, veil: 0.6, wash: true).Flat;

        Assert.True(Lightness(veiled) < Lightness(bare),
                    $"the veiled ground came out at {Lightness(veiled):F2} against {Lightness(bare):F2}");
    }

    [Fact]
    public void A_snowfield_under_its_own_veil_stays_dark_enough_to_write_on()
    {
        // The mean, the ground and the veil a white sleeve actually produces, read through the same
        // functions the surfaces use. The flat colour has to end up somewhere white text can live.
        var mean = Color.FromArgb(255, 235, 235, 230);
        var flat = Of(mean, CoverColor.Ground(mean), CoverColor.Veil(mean), wash: true).Flat;

        Assert.True(Lightness(flat) < 0.5, $"a white sleeve flattened to {Lightness(flat):F2}");
    }

    /// <summary>The drawer's top edge shows the middle band of the blur magnified, not the whole
    /// sleeve, so a triangle painted in the sleeve's average is a different colour from the edge it
    /// meets. It takes the band.</summary>
    [Fact]
    public void The_edge_band_is_what_the_triangle_takes()
    {
        var ground = Color.FromArgb(255, 40, 40, 40);
        var sleeve = new SleeveGround(
            Mean: Color.FromArgb(255, 40, 40, 40),      // averaged over the whole sleeve: near the ground
            Edge: Color.FromArgb(255, 220, 40, 40),     // the band on screen at the top: vivid
            Ground: ground, Wash: [1, 2, 3], Veil: 0);

        Assert.Equal(130, sleeve.Flat.R);   // half the ground, half the BAND — not half the mean
    }

    [Fact]
    public void It_never_leaves_the_byte_range()
    {
        var flat = Of(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255),
                      veil: 0, wash: true).Flat;

        Assert.Equal(255, flat.R);
        Assert.Equal(255, flat.A);
    }
}

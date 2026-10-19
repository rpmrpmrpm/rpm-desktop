using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// A box on the artists wall decodes its photograph at the size it is drawn, and at the edge that
/// covers the box whichever way round the photograph is.
///
/// <para><b>What this protects.</b> A decode with no size decodes the whole picture: a fanart.tv
/// backdrop is 1920 by 1080, so every box would hold 8.3MB for a picture drawn three hundred points
/// across. A missing size is hard to see in the code: the overload without it compiles, runs, and
/// looks right on screen.</para>
///
/// <para>And the edge cannot be chosen freely. The box crops with <c>UniformToFill</c> and
/// <c>RemoteImage.DecodeAsync</c> constrains the picture's SHORT side, so the edge must be the box's
/// LONG side or a landscape photograph comes back too small to cover it. Both rules are tested here
/// because breaking either shows only as a slightly soft picture.</para>
/// </summary>
public class ArtistTileDecodeEdgeTests
{
    private static ArtistTileMetrics Wide(double width)
    {
        var metrics = new ArtistTileMetrics();
        metrics.Set(width);
        return metrics;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(1.5)]
    public void The_edge_is_the_box_width_in_physical_pixels(double scale)
    {
        var metrics = Wide(300);

        Assert.Equal(ArtScaler.DecodeEdgeFor(300, scale), metrics.DecodeEdge(scale));
    }

    [Fact]
    public void The_edge_is_the_long_side_so_a_landscape_photograph_still_covers_the_box()
    {
        var metrics = Wide(300);

        // The box is sixteen by nine, so its height is the short side. Decoding to that would leave a
        // landscape picture (which these are) too narrow for the box.
        Assert.True(metrics.Height < metrics.Width, "the box stopped being wider than it is tall");
        Assert.Equal(ArtScaler.DecodeEdgeFor(metrics.Width, 2), metrics.DecodeEdge(2));
        Assert.NotEqual(ArtScaler.DecodeEdgeFor(metrics.Height, 2), metrics.DecodeEdge(2));
    }

    [Fact]
    public void A_wider_box_asks_for_more_pixels()
    {
        Assert.True(Wide(400).DecodeEdge(2) > Wide(200).DecodeEdge(2));
    }

    [Fact]
    public void The_edge_is_far_short_of_a_backdrop_decoded_whole()
    {
        // The size of a full decode: 1920 across, at four bytes a pixel. Anything near it means the
        // decode is not constrained.
        Assert.True(
            Wide(300).DecodeEdge(2) < 1920,
            "a box three hundred points across is asking for a whole 1920-wide backdrop again");
    }
}

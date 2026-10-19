using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// The size covers are embedded at. The folder image keeps the full scan; this is the other tier, and
/// it is copied once per track, so this arithmetic decides how many bytes a library spends on it.
/// </summary>
public class EmbeddedArtTests
{
    /// <summary>A scan bigger than the ceiling is the main case for this tier.</summary>
    [Theory]
    [InlineData(2500, 2500)]
    [InlineData(1500, 1500)]
    [InlineData(1001, 1001)]
    [InlineData(3000, 400)]   // the long edge counts, whichever edge it is
    [InlineData(400, 3000)]
    public void A_scan_past_the_ceiling_is_worth_scaling(int width, int height) =>
        Assert.True(EmbeddedArt.NeedsScaling(width, height));

    /// <summary>At or under it, the picture is embedded exactly as it is. Re-encoding a cover that
    /// already fits can only lose quality and gains nothing.</summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(600, 591)]
    [InlineData(220, 220)]
    public void A_cover_that_already_fits_is_left_alone(int width, int height)
    {
        Assert.False(EmbeddedArt.NeedsScaling(width, height));
        Assert.Equal((width, height), EmbeddedArt.Fit(width, height));
    }

    /// <summary>A size that could not be read is not a reason to guess at one.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 500)]
    [InlineData(2000, 0)]
    public void An_unreadable_size_is_not_scaled(int width, int height) =>
        Assert.False(EmbeddedArt.NeedsScaling(width, height));

    /// <summary>Most sleeves are square, and they stay square.</summary>
    [Fact]
    public void A_square_scan_lands_on_the_ceiling_exactly() =>
        Assert.Equal((1000, 1000), EmbeddedArt.Fit(2500, 2500));

    /// <summary>Shape is preserved: a sleeve photographed slightly off-square must not be stretched
    /// back to a square when written into the file.</summary>
    [Theory]
    [InlineData(1411, 1407, 1000, 997)]
    [InlineData(2000, 1000, 1000, 500)]
    [InlineData(1000, 2000, 500, 1000)]
    public void The_shape_survives_the_scaling(int width, int height, int outWidth, int outHeight) =>
        Assert.Equal((outWidth, outHeight), EmbeddedArt.Fit(width, height));

    /// <summary>A gatefold scanned as one wide strip is an unusual cover, but a zero-pixel one cannot be
    /// encoded at all, so the short edge is at least one.</summary>
    [Fact]
    public void An_extreme_aspect_never_rounds_an_edge_to_nothing()
    {
        var (width, height) = EmbeddedArt.Fit(60000, 10);
        Assert.Equal(1000, width);
        Assert.True(height >= 1);
    }

    /// <summary>The ceiling defines the tier. It is fixed by this test so that any change to it is
    /// deliberate, because it affects every track file in every library.</summary>
    [Fact]
    public void The_ceiling_is_a_thousand_pixels() => Assert.Equal(1000, EmbeddedArt.MaxEdge);
}

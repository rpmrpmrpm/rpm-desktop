using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>The cached frost's arithmetic: how big the plate is, how hard it is blurred, and which
/// part of it a given panel stands on.</summary>
public class FrostGeometryTests
{
    [Theory]
    [InlineData(30, 10)]
    [InlineData(16, 16.0 / 3)]
    [InlineData(0, 0.1)]     // never a no-op blur
    public void Blur_radius_scales_with_the_plate(double blurAmount, double expected)
        => Assert.Equal(expected, FrostGeometry.SigmaFor(blurAmount), 6);

    [Fact]
    public void Plate_is_a_third_of_the_window()
    {
        Assert.Equal((640, 360), FrostGeometry.PlateSize(1920, 1080));
        // A partial pixel of window still gets a whole plate pixel, so the panels reach the window's
        // edge.
        Assert.Equal((334, 251), FrostGeometry.PlateSize(1000, 751));
    }

    [Fact]
    public void An_empty_window_still_has_a_plate()
        => Assert.Equal((1, 1), FrostGeometry.PlateSize(0, 0));

    [Fact]
    public void A_panel_reads_its_own_place_on_the_plate()
    {
        var crop = FrostGeometry.CropFor(24, 240, 600, 480, FrostPlateMap.Nominal(640, 360));

        Assert.NotNull(crop);
        Assert.Equal(8, crop!.Value.X, 4);
        Assert.Equal(80, crop.Value.Y, 4);
        Assert.Equal(200, crop.Value.Width, 4);
        Assert.Equal(160, crop.Value.Height, 4);
        Assert.Equal(200, crop.Value.PixelWidth);
        Assert.Equal(160, crop.Value.PixelHeight);
    }

    [Fact]
    public void The_fractions_are_kept()
    {
        // 25 DIPs is eight and a third plate pixels. Rounding that to eight would move the frost a DIP
        // under the panel every time it is cut again.
        var crop = FrostGeometry.CropFor(25, 25, 300, 300, FrostPlateMap.Nominal(640, 360));

        Assert.NotNull(crop);
        Assert.Equal(25 / 3.0, crop!.Value.X, 4);
        Assert.Equal(100, crop.Value.Width, 4);
        Assert.Equal(100, crop.Value.PixelWidth);
    }

    [Fact]
    public void A_panel_over_the_edge_reads_what_is_there()
    {
        // Runs 60 DIPs (20 plate pixels) past the plate's right edge.
        var crop = FrostGeometry.CropFor(1800, 0, 180, 300, FrostPlateMap.Nominal(640, 360));

        Assert.NotNull(crop);
        Assert.Equal(600, crop!.Value.X, 4);
        Assert.Equal(40, crop.Value.Width, 4);
        Assert.Equal(40, crop.Value.PixelWidth);
    }

    [Fact]
    public void Nothing_to_read_is_no_crop()
    {
        Assert.Null(FrostGeometry.CropFor(0, 0, 0, 100, FrostPlateMap.Nominal(640, 360)));       // collapsed panel
        Assert.Null(FrostGeometry.CropFor(0, 0, 100, -1, FrostPlateMap.Nominal(640, 360)));
        Assert.Null(FrostGeometry.CropFor(4000, 0, 100, 100, FrostPlateMap.Nominal(640, 360)));  // off the window entirely
        Assert.Null(FrostGeometry.CropFor(0, 0, 100, 100, FrostPlateMap.Nominal(0, 0)));         // no plate yet
    }

    [Fact]
    public void A_plate_that_came_back_a_different_size_is_read_at_its_own_scale()
    {
        // A window snapped to the side of the screen can be 1278x1391 DIPs, with the plate requested
        // at 426x464 and returned at 426x465. Read at three DIPs a pixel, the bottom of the window is
        // at 463.7 of 465, so the frost would be four DIPs too high under the panel there. Read at the
        // plate's own scale it is on the last row.
        var plate = FrostPlateMap.For(426, 465, 1278, 1391);
        var crop = FrostGeometry.CropFor(0, 0, 1278, 1391, plate);

        Assert.NotNull(crop);
        Assert.Equal(0, crop!.Value.X, 4);
        Assert.Equal(426, crop.Value.Width, 4);
        Assert.Equal(465, crop.Value.Height, 4);
    }

    [Fact]
    public void A_plate_of_no_size_falls_back_to_the_nominal_scale()
    {
        var plate = FrostPlateMap.For(0, 0, 0, 0);

        Assert.Equal(FrostGeometry.PlateDip, plate.DipPerPixelX);
        Assert.Null(FrostGeometry.CropFor(0, 0, 100, 100, plate));
    }
}

using RPM.Core.Art;

using RPM.Desktop.Presentation.Main.Home;

namespace RPM.Desktop.Tests;

/// <summary>
/// How the window the saliency crop chose is squared to the banner's plate. The crop returns whatever shape
/// the people in the photograph need. The plate has one fixed shape. This is where the two are reconciled.
/// The rule is that rows are cut before columns are.
/// </summary>
public class HomeFeatureArtTests
{
    // The plate is 760 by 352, so its aspect ratio (width to height) is about 2.159.
    private const double Plate = 760, Aspect = Plate / HomeBanner.Height;

    [Fact]
    public void A_band_of_heads_takes_more_of_the_photograph_rather_than_losing_its_sides()
    {
        // A 1920 by 1080 press photo with two faces can give a crop band of 1920 by 385 at y=223, because
        // that is where the heads are. Trimming the width to square it would keep only the middle 832
        // columns. That is a strong zoom, with the heads cut off the top of the card. So rows are added to
        // make the shape instead.
        var band = new CropRect(0, 223, 1920, 385);

        var cut = HomeFeatureArt.Squared(band, Aspect, 1920, 1080);

        Assert.Equal(1920, cut.Width);                       // every column the crop kept
        Assert.InRange(cut.Height, 880, 895);                // …and the rows to square it: 1920 / 2.159
        Assert.Equal(Aspect, (double)cut.Width / cut.Height, 2);

        // Grown around the band, and pushed inside the photograph rather than off the top of it.
        Assert.True(cut.Y >= 0 && cut.Y + cut.Height <= 1080, $"the cut left the photograph at {cut.Y}");
        Assert.True(cut.Y <= 223 && cut.Y + cut.Height >= 223 + 385, "the band the crop chose is not inside the cut");
    }

    [Fact]
    public void A_window_taller_than_the_plate_still_loses_rows_from_the_middle()
    {
        // The other case: the crop chose these columns on purpose, so the rows are cut, evenly from both
        // ends.
        var tall = new CropRect(200, 100, 1000, 800);

        var cut = HomeFeatureArt.Squared(tall, Aspect, 1920, 1080);

        Assert.Equal(1000, cut.Width);
        Assert.InRange(cut.Height, 458, 468);                // 1000 / 2.159
        Assert.Equal(100 + ((800 - cut.Height) / 2), cut.Y);
        Assert.Equal(200, cut.X);
    }

    [Fact]
    public void A_photograph_with_no_rows_to_give_gives_up_width_instead()
    {
        // A wide band in a picture that is already close to the plate's shape. There are no spare rows, so
        // the width is cut instead. The cut stays inside the photograph.
        var band = new CropRect(0, 40, 1200, 100);

        var cut = HomeFeatureArt.Squared(band, Aspect, 1200, 200);

        Assert.True(cut.Width <= 1200 && cut.Height <= 200, $"the cut is {cut.Width}x{cut.Height}");
        Assert.Equal(Aspect, (double)cut.Width / cut.Height, 2);
        Assert.True(cut.X >= 0 && cut.X + cut.Width <= 1200);
        Assert.True(cut.Y >= 0 && cut.Y + cut.Height <= 200);
    }

    [Fact]
    public void A_window_already_the_plates_shape_is_left_where_it_is()
    {
        var right = new CropRect(408, 0, 1260, 584);         // 2.16:1, close to the plate's own shape

        var cut = HomeFeatureArt.Squared(right, Aspect, 1920, 1080);

        Assert.Equal(1260, cut.Width);
        Assert.InRange(cut.Height, 580, 588);
        Assert.InRange(cut.X, 406, 410);
        Assert.InRange(cut.Y, 0, 4);
    }
}

using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The artist page's three measurements. These tests pin not the numbers (they are design choices and
/// can change) but the properties that keep the page working at any width: the column is a share between
/// two bounds, a folded page states a width rather than leaving one, the band keeps a shape, and a whole
/// number of sleeves fills the shelf with nothing left over.
/// </summary>
public class ArtistPageLayoutTests
{
    // ── the two columns ──

    [Fact]
    public void Columns_TakesAShareOfThePanel()
    {
        var (folded, width) = ArtistPageLayout.Columns(1100);

        Assert.False(folded);
        Assert.Equal(Math.Round(1100 * ArtistPageLayout.AboutShare), width);
    }

    [Fact]
    public void Columns_NeverNarrowerThanTheFloorNorWiderThanTheCeiling()
    {
        for (var panel = ArtistPageLayout.AboutMin + ArtistPageLayout.ShelfRoom; panel <= 4000; panel += 7)
        {
            var (folded, width) = ArtistPageLayout.Columns(panel);

            Assert.False(folded);
            Assert.InRange(width, ArtistPageLayout.AboutMin, ArtistPageLayout.AboutMax);
        }
    }

    [Fact]
    public void Columns_LeavesTheShelfItsRoom()
    {
        // Whatever the column takes, what is left beside it is still a shelf.
        for (var panel = ArtistPageLayout.AboutMin + ArtistPageLayout.ShelfRoom; panel <= 4000; panel += 13)
        {
            var (_, width) = ArtistPageLayout.Columns(panel);
            Assert.True(panel - width >= ArtistPageLayout.ShelfRoom, $"panel {panel}");
        }
    }

    [Fact]
    public void Columns_FoldsWhenThereIsNoRoomForBoth()
    {
        var (folded, width) =
            ArtistPageLayout.Columns(ArtistPageLayout.AboutMin + ArtistPageLayout.ShelfRoom - 1);

        Assert.True(folded);
        // A folded page STATES that the column has no width, rather than leaving the caller to know.
        Assert.Equal(0, width);
    }

    [Fact]
    public void Columns_AnUnmeasuredPanelFolds()
    {
        // Nothing has been laid out yet: one column is the state that needs no room.
        Assert.True(ArtistPageLayout.Columns(0).Folded);
        Assert.True(ArtistPageLayout.Columns(double.NaN).Folded);
    }

    // ── the band across the top ──

    [Fact]
    public void BandHeight_IsFourByThreeAtEveryWidth()
    {
        // Three quarters written out, not BandShare read back: a test that restates the constant would
        // pass whatever the constant became. Half a point of slack is the rounding to whole points and
        // nothing else: a shape that only holds "between bounds" is not a shape, and this ratio is the
        // whole claim the band makes.
        for (var width = 1d; width <= 3000; width += 11)
            Assert.True(
                Math.Abs(ArtistPageLayout.BandHeight(width) - width * 3.0 / 4.0) <= 0.5,
                $"a band {width} wide came out {ArtistPageLayout.BandHeight(width)} tall");
    }

    [Fact]
    public void BandHeight_AndTheWallsShapeAreDeliberatelyDifferent()
    {
        // The band (4:3) and the wall's tiles (16:9) have different shapes on purpose, and this test
        // says so: if ArtistTileMetrics is pointed at BandShare to "tidy up", the wall's tiles change
        // shape and this fails.
        Assert.NotEqual(ArtistPageLayout.BandShare, ArtistPageLayout.TileShare);
        Assert.Equal(3.0 / 4.0, ArtistPageLayout.BandShare, 10);
        Assert.Equal(9.0 / 16.0, ArtistPageLayout.TileShare, 10);
    }

    [Fact]
    public void BandHeight_TakesAQuarterOffEitherKindOfPicture()
    {
        // What four by three costs, in one place, because the shape was chosen on this number: a
        // square loses a quarter of its height and a backdrop loses a quarter of its width. A square
        // band would cost backdrops about two fifths and squares nothing; 16:9 is the other way round.
        //
        // Both are computed the same way: scale the picture until it covers the band, then ask how
        // much of the direction being cut is kept.
        var band = ArtistPageLayout.BandHeight(1200);           // 1200 by 900

        var squareKeeps = band / 1200;                          // a square scaled to 1200 wide is 1200 tall
        Assert.Equal(0.75, squareKeeps, 3);

        var backdropTall = 1200 * 1080.0 / 1920.0;              // a backdrop scaled to 1200 wide is short
        var blownUp = band / backdropTall;                      // so it is scaled up until it covers
        Assert.Equal(0.75, 1 / blownUp, 3);
    }

    [Fact]
    public void BandHeight_TheColumnIsWhatBoundsIt()
    {
        // No clamp of its own: what keeps the band off the whole page is the column being clamped, so
        // the widest unfolded band follows from the arithmetic and is not a second limit.
        var widest = ArtistPageLayout.BandHeight(ArtistPageLayout.AboutMax);

        Assert.Equal(Math.Round(ArtistPageLayout.AboutMax * ArtistPageLayout.BandShare), widest);
        Assert.True(widest <= ArtistPageLayout.AboutMax,
            $"an unfolded band is {widest} tall over a column at most {ArtistPageLayout.AboutMax} wide");
    }

    [Fact]
    public void BandHeight_AFoldedBandIsThreeQuartersOfThePanelsWidth()
    {
        // Stated because it is the cost of a 4:3 band, and the cost is real. Folded, the band IS the
        // panel, so on the widest panel that still folds the card is 525 points tall (394 at 16:9, 700
        // if square). This is accepted rather than folding to a second shape: two shapes for one card
        // on one page is worse than one tall card.
        var panel = ArtistPageLayout.AboutMin + ArtistPageLayout.ShelfRoom;
        var folded = ArtistPageLayout.BandHeight(panel);

        Assert.Equal(525, folded);
        Assert.True(folded < panel, $"a folded band is {folded} tall on a {panel} panel");
    }

    [Fact]
    public void BandHeight_AnUnmeasuredBandHasNoHeight()
    {
        Assert.Equal(0, ArtistPageLayout.BandHeight(0));
        Assert.Equal(0, ArtistPageLayout.BandHeight(double.NaN));
    }

    // ── the lettering over the band ──

    [Fact]
    public void LogoMaxWidth_IsFortyTwoHundredthsOfTheBand()
    {
        // Written out rather than LogoWidthShare read back, for the reason the band's shape is: a test
        // that restates the constant agrees with whatever the constant becomes. 0.42 is three fifths
        // times seven tenths: the lettering is thirty per cent smaller than a 0.6 share.
        for (var width = 1d; width <= 3000; width += 7)
            Assert.True(
                Math.Abs(ArtistPageLayout.LogoMaxWidth(width) - width * 0.42) <= 0.5,
                $"a band {width} wide allowed {ArtistPageLayout.LogoMaxWidth(width)} of lettering");
    }

    [Fact]
    public void LogoMaxWidth_IsSevenTenthsOfWhatItWas()
    {
        // A mark is bounded both by a height cap and by this width. Only scaling BOTH by 0.7 makes every
        // mark 0.7 times the size, keeping its proportions, whichever bound applies to it, so the pair is
        // checked rather than the number alone. LogoCap lives in ArtistDetailView and is 72 * 0.7
        // rounded; it is restated, not read, so that this test notices if one of the two is changed alone.
        Assert.Equal(0.6 * 0.7, ArtistPageLayout.LogoWidthShare, 3);
        Assert.Equal(50d, Math.Round(72 * 0.7));
    }

    [Fact]
    public void LogoMaxWidth_LeavesTheRestOfTheBandAsPicture()
    {
        // The claim the share exists to make: a title is a caption on a photograph, so whatever the
        // band is, there is a real part of it the lettering cannot reach. Without this the height cap
        // is the only bound and a long name set as one word takes the whole frame.
        foreach (var width in new[] { 320d, 480, 700, 1200, 1920 })
        {
            var room = ArtistPageLayout.LogoMaxWidth(width);
            Assert.True(room < width, $"lettering could take the whole {width} band");
            Assert.True(width - room >= width / 3, $"a band {width} wide kept only {width - room} of picture");
        }
    }

    [Fact]
    public void LogoMaxWidth_BoundsALongNameByWidthRatherThanHeight()
    {
        // The case the bound is for: a name set as one long word, published about six and a half times
        // as wide as it is tall. At the 50-point height cap that needs 333 points across, and the widest
        // band in the app is a folded one at 700, so the height cap alone would let it cross nearly half
        // the photograph. The width is what limits it, and it comes out SHORTER than the cap as a
        // result: scaled, not cropped, because the drawing is uniform.
        const double capHeight = 50, markWide = 800, markHigh = 120;

        var room = ArtistPageLayout.LogoMaxWidth(ArtistPageLayout.AboutMin + ArtistPageLayout.ShelfRoom);
        var drawn = Math.Min(capHeight, room * markHigh / markWide);

        Assert.True(drawn < capHeight, $"a long mark was left at {drawn}, the full height cap");
        Assert.True(drawn * markWide / markHigh <= room + 0.5, "a bounded mark still ran past its room");
    }

    [Fact]
    public void LogoMaxWidth_NoShapeOfMarkEverRunsPastItsRoom()
    {
        // The invariant behind the one above, over every band the app builds and every proportion a
        // catalogue publishes, from squarish sleeve marks to the longest wordmarks. Whatever comes
        // back, the width it is drawn at is inside the room it was given.
        const double capHeight = 50;

        foreach (var band in new[] { 360d, 420, 520, 700, 900, 1400 })
        {
            var room = ArtistPageLayout.LogoMaxWidth(band);
            foreach (var aspect in new[] { 0.8, 1.5, 3, 5.33, 6.67, 10, 14 })
            {
                var drawn = Math.Min(capHeight, room / aspect);
                Assert.True(drawn * aspect <= room + 0.5,
                    $"a {aspect}:1 mark on a {band} band drew {drawn * aspect} across {room} of room");
            }
        }
    }

    [Fact]
    public void LogoMaxWidth_AnUnmeasuredBandGivesNothing()
    {
        // Same stated answer BandHeight gives, and the caller depends on it: no room means only the
        // height cap applies, rather than the lettering being cut to a width nobody has measured yet.
        Assert.Equal(0, ArtistPageLayout.LogoMaxWidth(0));
        Assert.Equal(0, ArtistPageLayout.LogoMaxWidth(double.NaN));
    }

    // ── a sleeve on the shelves ──

    [Fact]
    public void Sleeve_AWiderShelfNeverShowsFewerRecords()
    {
        // NOT "a wider shelf shows bigger records", which is what a shelf of one fixed sleeve would do
        // and is the wrong thing: room goes into MORE records at about the size the reader picked, not
        // into the same record scaled up. So the sleeve oscillates around the target as each new column
        // fits, and what actually holds all the way up is that the count never goes backwards.
        var last = 0d;
        for (var shelf = 200d; shelf <= 4000; shelf += 3)
        {
            var (stride, _) = ArtistPageLayout.Sleeve(shelf);
            var fit = Math.Floor((shelf - ArtistPageLayout.ShelfPadding) / stride);

            Assert.True(fit >= last, $"shelf {shelf} shows {fit} records where a narrower one showed {last}");
            last = fit;
        }
    }

    [Fact]
    public void Sleeve_FillsTheShelfWithAWholeNumberOfThem()
    {
        for (var shelf = 200d; shelf <= 3000; shelf += 9)
        {
            var (stride, _) = ArtistPageLayout.Sleeve(shelf);
            var room = shelf - ArtistPageLayout.ShelfPadding;

            var fit = Math.Floor(room / stride);
            Assert.True(fit >= 1, $"shelf {shelf} fits no sleeve");
            // Nothing left over that another whole sleeve would have taken.
            Assert.True(room - (fit * stride) < stride, $"shelf {shelf} has a sleeve's worth of dead room");
        }
    }

    [Fact]
    public void Sleeve_IsNeverAStampNorAPoster()
    {
        for (var shelf = 1d; shelf <= 4000; shelf += 7)
            Assert.InRange(
                ArtistPageLayout.Sleeve(shelf).Art, ArtistPageLayout.SleeveMin, ArtistPageLayout.SleeveMax);
    }

    [Fact]
    public void Sleeve_TheStrideIsTheSquareAndItsChrome()
    {
        var (stride, art) = ArtistPageLayout.Sleeve(900);
        Assert.Equal(ArtistPageLayout.SleeveChrome, stride - art);
    }

    [Fact]
    public void Sleeve_TheDrawersTriangleStandsUnderTheSquareItPointsAt()
    {
        // The geometry the open drawer's mark is placed by, read the way the page reads it: a shelf of
        // this width, the first sleeve, nothing scrolled. The mark belongs over the middle of the SQUARE,
        // which is the scroller's padding plus a card's own, plus half a sleeve.
        var (stride, art) = ArtistPageLayout.Sleeve(900);

        var middle = AlbumGridLayout.CardMiddleOnScreen(
            index: 0, stride + ArtistPageLayout.ShelfSpacing, art, ArtistPageLayout.SleeveLeft,
            offset: 0, viewport: 900, clearance: 10);

        Assert.Equal(ArtistPageLayout.SleeveLeft + (art / 2), middle);

        // And the sleeve after it, a stride and the shelf's own gap further along.
        Assert.Equal(
            ArtistPageLayout.SleeveLeft + stride + ArtistPageLayout.ShelfSpacing + (art / 2),
            AlbumGridLayout.CardMiddleOnScreen(
                index: 1, stride + ArtistPageLayout.ShelfSpacing, art, ArtistPageLayout.SleeveLeft,
                offset: 0, viewport: 900, clearance: 10));
    }

    [Fact]
    public void Sleeve_TheLeftOfTheFirstSleeveIsTheDrawersOwnLeftEdge()
        // 18 in both places: the drawer's markup margin (18,10,18,0) and this. They are the same edge, and
        // the triangle is placed in the drawer's space off a middle measured in the shelf's.
        => Assert.Equal(18, ArtistPageLayout.SleeveLeft);

    [Fact]
    public void Sleeve_AnUnmeasuredShelfAnswersTheTarget()
    {
        // A pass before the panel has a width must not leave a sleeve at zero: the shelves are filled
        // from it, and the records realised in that pass would be invisible squares.
        Assert.Equal(ArtistPageLayout.SleeveTarget, ArtistPageLayout.Sleeve(0).Art);
        Assert.Equal(ArtistPageLayout.SleeveTarget, ArtistPageLayout.Sleeve(double.NaN).Art);
    }
}

using RPM.Desktop.Presentation.Main.Artists;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the All tracks listing puts each song and letter header. One repeater draws both, so this
/// arithmetic is the entire layout: a header starts a new line across the full width, songs fill
/// the columns after it.
/// </summary>
public class ArtistTrackGridTests
{
    private const double Row = 30, Gap = 1, Head = 40;

    /// <summary>'H' for a header, 's' for a song.</summary>
    private static ArtistTrackGrid Grid(string kinds, int columns) =>
        new([.. kinds.Select(c => c == 'H')], columns, Row, Gap, Head);

    [Fact]
    public void Songs_with_no_headers_fill_the_columns_a_line_at_a_time()
    {
        var grid = Grid("sssss", columns: 2);

        Assert.Equal([0, 1, 0, 1, 0], Enumerable.Range(0, 5).Select(grid.Column));
        Assert.Equal([0, 0, 31, 31, 62], Enumerable.Range(0, 5).Select(grid.Top));
        Assert.Equal(92, grid.Height);
    }

    [Fact]
    public void A_header_closes_a_part_full_line_and_starts_its_songs_at_the_first_column()
    {
        var grid = Grid("HsssHss", columns: 2);

        Assert.Equal(-1, grid.Column(0));
        Assert.Equal(0, grid.Top(0));
        // Three songs under the first header: a full line and a line with one.
        Assert.Equal([40, 40, 71], new[] { grid.Top(1), grid.Top(2), grid.Top(3) });
        // The second header goes below the part-full line, not beside it.
        Assert.Equal(-1, grid.Column(4));
        Assert.Equal(102, grid.Top(4));
        Assert.Equal([0, 1], new[] { grid.Column(5), grid.Column(6) });
        Assert.Equal(142, grid.Top(5));
        Assert.Equal(172, grid.Height);
    }

    [Fact]
    public void A_header_after_a_full_line_adds_no_empty_line()
    {
        var grid = Grid("HssHs", columns: 2);

        Assert.Equal(71, grid.Top(3));
    }

    [Fact]
    public void The_band_finds_every_item_that_overlaps_it_and_no_other()
    {
        var grid = Grid("HsssHss", columns: 2);

        // Only the first header.
        Assert.Equal((0, 0), grid.ItemsIn(0, 40));
        // The line at 40 to 70, both songs on it.
        Assert.Equal((1, 2), grid.ItemsIn(45, 60));
        // From inside the one-song line to inside the second header.
        Assert.Equal((3, 4), grid.ItemsIn(80, 110));
        // Past the end.
        Assert.Equal((-1, -1), grid.ItemsIn(500, 600));
        // An empty band, which is what a repeater out of view receives.
        Assert.Equal((-1, -1), grid.ItemsIn(0, 0));
    }

    [Fact]
    public void An_empty_listing_has_no_height_and_nothing_in_any_band()
    {
        var grid = Grid("", columns: 3);

        Assert.Equal(0, grid.Height);
        Assert.Equal((-1, -1), grid.ItemsIn(0, 1000));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 1)]
    [InlineData(290, 1)]
    [InlineData(585, 1)]
    [InlineData(586, 2)]
    [InlineData(1000, 3)]
    public void Columns_are_how_many_minimum_widths_fit_and_never_fewer_than_one(double width, int expected)
        => Assert.Equal(expected, ArtistTrackGrid.Columns(width, 290, 6));
}

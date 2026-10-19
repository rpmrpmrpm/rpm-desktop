using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The size the album wall keeps every record's cover at, and what it costs to keep them.
///
/// <para>The tier exists so that, at the bottom of the size slider, a realized row has nothing to fetch.
/// The picture already in memory is the one that zoom asks for. The tier must be sized in physical
/// pixels. A fixed 128 does not hold on a doubled display: the smallest tile the slider offers wants 192
/// there, but the tier gives 128. So every tile at every slider position needs a second read.</para>
/// </summary>
public class ResidentTierTests
{
    /// <summary>The wall's own floor, which is what the tier has to cover to be worth anything.</summary>
    private const double MinArt = AlbumWallView.MinArt;

    [Fact]
    public void The_smallest_tile_needs_no_second_read_on_an_ordinary_display()
    {
        var resident = ArtScaler.ResidentEdgeFor(MinArt, rasterScale: 1);
        Assert.Equal(ArtScaler.DecodeEdgeFor(MinArt, 1), resident);
    }

    /// <summary>A doubled display asks for more physical pixels, and the tier has
    /// to follow, or it is a placeholder at every position of the slider.</summary>
    [Fact]
    public void The_smallest_tile_needs_no_second_read_on_a_doubled_display()
    {
        var resident = ArtScaler.ResidentEdgeFor(MinArt, rasterScale: 2);
        Assert.Equal(ArtScaler.DecodeEdgeFor(MinArt, 2), resident);
        Assert.True(resident > ArtScaler.TinyEdge, "128 is what the old fixed tier answered, and it was short");
    }

    [Fact]
    public void The_tier_never_drops_below_its_floor()
    {
        Assert.Equal(ArtScaler.TinyEdge, ArtScaler.ResidentEdgeFor(smallestTile: 8, rasterScale: 1));
        Assert.Equal(ArtScaler.TinyEdge, ArtScaler.ResidentEdgeFor(smallestTile: 0, rasterScale: 0));
    }

    /// <summary>An unmeasured scale must not answer a bigger tier than the display will ever need: the
    /// wall reads this before it is in a window.</summary>
    [Fact]
    public void An_unknown_display_answers_the_floor()
        => Assert.Equal(ArtScaler.TinyEdge, ArtScaler.ResidentEdgeFor(MinArt, rasterScale: 0));

    /// <summary>Why the wall's cap is in bytes: the same number of records is a different amount of
    /// memory at each rung, and the tier's rung now depends on the machine.</summary>
    [Fact]
    public void A_record_costs_four_times_as_much_two_rungs_up()
    {
        Assert.Equal(128L * 128 * 4, ArtScaler.BytesFor(128));
        Assert.Equal(4 * ArtScaler.BytesFor(128), ArtScaler.BytesFor(256));
    }

    /// <summary>The budget equals what the fixed tier spent on an ordinary display. So an ordinary display
    /// holds the same records as before, and a doubled display holds fewer records rather than more
    /// memory.</summary>
    [Fact]
    public void The_budget_holds_a_large_collection_at_the_floor()
    {
        const long budget = 320L * 1024 * 1024;
        Assert.True(budget / ArtScaler.BytesFor(ArtScaler.TinyEdge) >= 5000,
                    "an ordinary display must still hold every record a five-thousand cap used to");
        Assert.True(budget / ArtScaler.BytesFor(ArtScaler.ResidentEdgeFor(MinArt, 2)) >= 2000,
                    "and a doubled one must still hold a large collection, just not as large");
    }
}

using RPM.Desktop.Presentation.Main.Artists;

namespace RPM.Desktop.Tests;

/// <summary>
/// A queued box is only filled in if it is still the box that was queued, and still on screen.
///
/// <para><b>What this protects.</b> Starting a fetch on every realize, with no limit, would make a
/// fast scroll start one fetch per realized box and give pictures to elements the repeater is already
/// recycling. Uno cancels an image whose element no longer needs it, and under a debugger each of
/// those cancellations holds a thread at a point where a garbage collection cannot suspend it (291 in
/// a minute, with collections taking seconds and freeing nothing).</para>
///
/// <para>The part that is easy to get wrong is the identity check. A rebuild runs on every library
/// change and on every keystroke of the search, and it REPLACES the list, so an index queued against
/// the old list names a different artist in the new one. Trusting the index would sometimes put one
/// artist's photograph on another's box, only while typing: a bug that is reported as "it sometimes
/// shows the wrong picture" and is hard to reproduce.</para>
/// </summary>
public class ArtistWallTurnTests
{
    /// <summary>A stand-in for a box. The rule is about identity and bounds and knows nothing of tiles;
    /// a real ArtistTile cannot be built outside a XAML host.
    /// </summary>
    private sealed record Box(string Name);

    private static Box Tile(string name) => new(name);

    [Fact]
    public void A_box_still_in_its_place_and_on_screen_is_worth_the_turn()
    {
        var tiles = new[] { Tile("Aphex Twin"), Tile("Boards of Canada") };

        Assert.True(ArtistWallView.StillWanted(tiles, 1, tiles[1], realized: true));
    }

    [Fact]
    public void A_box_scrolled_off_screen_is_passed_over()
    {
        var tiles = new[] { Tile("Aphex Twin") };

        Assert.False(ArtistWallView.StillWanted(tiles, 0, tiles[0], realized: false));
    }

    [Fact]
    public void A_rebuild_under_the_loaders_leaves_the_index_naming_somebody_else()
    {
        var queued = Tile("Aphex Twin");
        var before = new[] { queued, Tile("Boards of Canada") };

        // What a keystroke in the search field does: the same positions, different artists.
        var after = new[] { Tile("Zappa"), Tile("Zorn") };

        Assert.True(ArtistWallView.StillWanted(before, 0, queued, realized: true));
        Assert.False(
            ArtistWallView.StillWanted(after, 0, queued, realized: true),
            "the index was trusted over the box itself, so a rebuild would put one artist's photograph on another's box");
    }

    [Fact]
    public void An_index_past_the_end_of_a_narrowed_wall_is_passed_over()
    {
        var queued = Tile("Aphex Twin");
        var before = new[] { Tile("A"), Tile("B"), queued };

        // A search that filters two thousand artists to two leaves every queued index past the end.
        Assert.False(ArtistWallView.StillWanted(new[] { Tile("A"), Tile("B") }, 2, queued, realized: true));
        Assert.True(ArtistWallView.StillWanted(before, 2, queued, realized: true));
    }

    [Fact]
    public void A_fresh_box_with_the_same_name_is_not_the_box_that_was_queued()
    {
        // Boxes are reused BY NAME across rebuilds, so a rebuild often returns the same object and the
        // turn stays valid. But a rebuild that creates a new box for the same artist must not be taken
        // for the old one: the queued turn belongs to an element that is gone, and filling it in would
        // write a picture into something that is not shown. Compare by reference, not by name.
        var queued = Tile("Aphex Twin");
        var remade = new[] { Tile("Aphex Twin") };

        Assert.False(
            ArtistWallView.StillWanted(remade, 0, queued, realized: true),
            "boxes are being compared by name; a remade box is a different box");
    }

    [Fact]
    public void A_negative_index_is_passed_over_rather_than_thrown_on()
    {
        var tiles = new[] { Tile("Aphex Twin") };

        Assert.False(ArtistWallView.StillWanted(tiles, -1, tiles[0], realized: true));
    }
}

using RPM.Core.Layout;

namespace RPM.Core.Tests;

public class RailDuckTests
{
    // A window of the size the app opens at: a 200-tall art band, so the disc's halo runs to about
    // 307, and the library card starts at 224 under it.
    private const double DiscBottom = 307;
    private const double RailTop = 224;
    private const double Gap = 14;
    private const double Tabs = 4 * 64;

    [Fact]
    public void Nothing_playing_leaves_the_tabs_in_the_corner()
        => Assert.Equal(0, RailDuck.Drop(discBottom: 0, RailTop, Gap, Tabs, railHeight: 600));

    [Fact]
    public void A_disc_that_ends_above_the_rail_is_not_dodged()
        => Assert.Equal(0, RailDuck.Drop(discBottom: 180, RailTop, Gap, Tabs, railHeight: 600));

    [Fact]
    public void The_tabs_clear_the_arc_by_the_gap()
        => Assert.Equal(97, RailDuck.Drop(DiscBottom, RailTop, Gap, Tabs, railHeight: 600));

    [Fact]
    public void A_rail_too_short_for_both_gives_what_room_it_has()
    {
        // 300 of rail, 256 of tabs: 44 is all there is to give, and the tabs stay on screen. They are
        // not pushed out of the pane to satisfy a disc that is about to move anyway.
        Assert.Equal(44, RailDuck.Drop(DiscBottom, RailTop, Gap, Tabs, railHeight: 300));
    }

    [Fact]
    public void A_rail_shorter_than_its_own_tabs_does_not_duck_at_all()
        => Assert.Equal(0, RailDuck.Drop(DiscBottom, RailTop, Gap, Tabs, railHeight: 120));
}

using RPM.Desktop.Presentation.Import;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the workbench does with the room it is given.
///
/// <para>Arithmetic, so it can be held to its promises without a window: the pane can now be dragged
/// from a sliver to the full width of the screen, and what the bench does with the room has to be
/// right at every width in between, not only at the two somebody happened to try.</para>
/// </summary>
public class WorkbenchLayoutTests
{
    [Theory]
    [InlineData(320, WorkbenchTier.Compact)]
    [InlineData(619, WorkbenchTier.Compact)]
    [InlineData(620, WorkbenchTier.Regular)]
    [InlineData(959, WorkbenchTier.Regular)]
    [InlineData(960, WorkbenchTier.Wide)]
    [InlineData(2400, WorkbenchTier.Wide)]
    public void Width_decides_the_tier(double width, WorkbenchTier expected)
    {
        Assert.Equal(expected, WorkbenchLayout.TierFor(width));
    }

    // Thirteen keys, each 32 wide, with a hairline in front of every third.
    private static IReadOnlyList<WorkbenchLayout.RailKey> Rail(int count = 13) =>
        [.. Enumerable.Range(0, count).Select(i => new WorkbenchLayout.RailKey(32, i % 3 == 0))];

    private static int Fits(double available, int count = 13) =>
        WorkbenchLayout.RailFits(Rail(count), available, spacing: 1, divider: 11, reserve: 150, menu: 32);

    [Fact]
    public void A_rail_with_room_for_everything_shows_everything()
    {
        Assert.Equal(13, Fits(2000));
    }

    [Fact]
    public void The_rail_gives_up_its_last_keys_first()
    {
        // Narrowing takes keys off the end one at a time, and never puts one back on the way down.
        var seen = 13;
        for (var width = 2000; width >= 200; width -= 10)
        {
            var fit = Fits(width);
            Assert.True(fit <= seen, $"the rail grew as it narrowed, at {width}");
            seen = fit;
        }
        Assert.True(seen < 13, "the rail never gave anything up");
    }

    [Fact]
    public void A_label_always_has_room_to_slide_out()
    {
        // What is on the rail, plus the widest label, is never wider than the rail itself. That is the
        // whole promise: point at any key and its words fit beside it.
        foreach (var width in new double[] { 300, 480, 700, 960, 1400 })
        {
            var fit = Fits(width);
            var used = fit * 32 + Math.Max(0, fit - 1) * 1
                       + Rail().Take(fit).Skip(1).Count(k => k.StartsGroup) * 12;
            var menu = fit < 13 ? 33 : 0;
            Assert.True(used + menu + 150 <= width, $"no room for a label at {width}");
        }
    }

    [Fact]
    public void The_menu_is_paid_for_while_anything_is_still_waiting()
    {
        // Room for exactly one key and no menu is not room for one key: the other twelve have to go
        // somewhere, so the rail shows nothing rather than lying about where they went.
        var oneKeyNoMenu = 32 + 150;
        Assert.Equal(0, Fits(oneKeyNoMenu));
        Assert.Equal(1, Fits(oneKeyNoMenu + 33));
        // …and a single-key rail needs no menu at all.
        Assert.Equal(1, Fits(oneKeyNoMenu, count: 1));
    }

    [Fact]
    public void The_tag_panel_fits_when_the_grid_still_has_room()
    {
        Assert.True(WorkbenchLayout.FitsTagPanel(paneWidth: 900, panelWidth: 250));
        Assert.True(WorkbenchLayout.FitsTagPanel(paneWidth: 610, panelWidth: 250));
    }

    [Fact]
    public void The_tag_panel_steps_aside_when_it_would_leave_no_grid()
    {
        Assert.False(WorkbenchLayout.FitsTagPanel(paneWidth: 500, panelWidth: 250));
        Assert.False(WorkbenchLayout.FitsTagPanel(paneWidth: 380, panelWidth: 250));
    }

    [Fact]
    public void A_panel_nobody_has_opened_never_fits()
    {
        Assert.False(WorkbenchLayout.FitsTagPanel(paneWidth: 2000, panelWidth: 0));
    }
}

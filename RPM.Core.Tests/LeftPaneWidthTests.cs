using RPM.Core.Layout;

namespace RPM.Core.Tests;

public class LeftPaneWidthTests
{
    private const double Fallback = 520;   // the ceiling the other views keep — where Import starts
    private const double Min = 220;

    [Fact]
    public void Never_dragged_opens_at_the_default()
        => Assert.Equal(Fallback, LeftPaneWidth.ForImport(remembered: 0, Fallback, room: 1600, Min));

    [Fact]
    public void A_dragged_width_is_taken_as_it_is_past_the_other_views_ceiling()
        => Assert.Equal(900, LeftPaneWidth.ForImport(remembered: 900, Fallback, room: 1600, Min));

    [Fact]
    public void A_narrow_row_lends_the_library_its_minimum_back()
        => Assert.Equal(780, LeftPaneWidth.ForImport(remembered: 900, Fallback, room: 1000, Min));

    [Fact]
    public void The_borrowed_width_comes_back_when_the_window_does()
    {
        // The clamp is on the answer, not on what is remembered: the same 900 that had to give way on a
        // 1000-wide row stands in full once there is room for it again.
        Assert.Equal(780, LeftPaneWidth.ForImport(remembered: 900, Fallback, room: 1000, Min));
        Assert.Equal(900, LeftPaneWidth.ForImport(remembered: 900, Fallback, room: 1600, Min));
    }

    [Fact]
    public void A_row_too_narrow_for_both_still_leaves_the_pane_its_minimum()
        => Assert.Equal(Min, LeftPaneWidth.ForImport(remembered: 900, Fallback, room: 300, Min));

    [Fact]
    public void A_width_under_the_minimum_is_lifted_to_it()
        => Assert.Equal(Min, LeftPaneWidth.ForImport(remembered: 80, Fallback, room: 1600, Min));

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void An_unmeasured_row_has_nothing_to_clamp_against(double room)
        => Assert.Equal(900, LeftPaneWidth.ForImport(remembered: 900, Fallback, room, Min));

    [Fact]
    public void An_unmeasured_row_with_nothing_remembered_still_opens_at_the_default()
        => Assert.Equal(Fallback, LeftPaneWidth.ForImport(remembered: 0, Fallback, room: 0, Min));
}

using RPM.Core.Layout;

namespace RPM.Core.Tests;

public class RecordDodgeTests
{
    // A 1400-wide window with the disc centred on its right edge, 100 down, radius 190: the size the
    // record has with a 200-tall art band.
    private const double Width = 1400;
    private const double CentreY = 100;
    private const double Radius = 190;

    private static double Disc(double y)
    {
        var dy = Math.Abs(y - CentreY);
        return dy < Radius ? Width - Math.Sqrt((Radius * Radius) - (dy * dy)) : double.PositiveInfinity;
    }

    private static double Idle(double y) => double.PositiveInfinity;

    [Fact]
    public void Nothing_playing_leaves_the_group_where_it_rests()
        => Assert.Equal(0, RecordDodge.Reserve(Idle, restRight: 1384, top: 240, height: 28));

    [Fact]
    public void A_group_below_the_disc_does_not_move()
        => Assert.Equal(0, RecordDodge.Reserve(Disc, restRight: 1384, top: 300, height: 28));

    [Fact]
    public void A_group_far_enough_left_does_not_move()
        => Assert.Equal(0, RecordDodge.Reserve(Disc, restRight: 1000, top: 240, height: 28));

    [Fact]
    public void A_group_under_the_arc_clears_it_by_the_gap()
    {
        // The import pane's X: 28 high, 16 in from the right edge, starting 240 down.
        var reserve = RecordDodge.Reserve(Disc, restRight: 1384, top: 240, height: 28);

        // Its top row is where the arc reaches furthest left over the group.
        var arcLeft = Disc(240);
        Assert.Equal(1384 + RecordDodge.Gap - arcLeft, reserve, precision: 6);
        Assert.True(1384 - reserve + RecordDodge.Gap <= arcLeft + 1e-9);
    }

    [Fact]
    public void The_leftmost_reach_over_the_group_counts_not_the_reach_at_its_top()
    {
        // A group that starts above the disc's centre and ends below it: the widest part of the circle
        // is inside the group, not at either edge.
        var reserve = RecordDodge.Reserve(Disc, restRight: 1384, top: 80, height: 40);

        Assert.Equal(1384 + RecordDodge.Gap - (Width - Radius), reserve, precision: 6);
    }
}

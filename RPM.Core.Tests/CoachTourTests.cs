using RPM.Core.Library;

namespace RPM.Core.Tests;

public class CoachTourTests
{
    [Fact]
    public void A_fresh_install_is_offered_the_first_mark()
    {
        Assert.Equal(CoachMark.AddMusic, CoachTour.Next([]));
    }

    [Fact]
    public void The_marks_come_in_order()
    {
        var seen = new List<string>();
        var got = new List<CoachMark>();

        while (CoachTour.Next(seen) is { } mark)
        {
            got.Add(mark);
            seen.Add(CoachTour.Key(mark));
        }

        Assert.Equal([CoachMark.AddMusic, CoachMark.ArtHeader, CoachMark.Views, CoachMark.SignIn], got);
    }

    [Fact]
    public void A_mark_seen_out_of_order_is_not_offered_again()
    {
        // The listener found the view switch on their own before the tour reached it.
        Assert.Equal(CoachMark.AddMusic, CoachTour.Next([CoachTour.Key(CoachMark.Views)]));
        Assert.Equal(CoachMark.ArtHeader,
                     CoachTour.Next([CoachTour.Key(CoachMark.Views), CoachTour.Key(CoachMark.AddMusic)]));
    }

    [Fact]
    public void The_tour_is_over_when_every_mark_is_seen()
    {
        var all = CoachTour.Order.Select(CoachTour.Key).ToList();

        Assert.Null(CoachTour.Next(all));
        Assert.True(CoachTour.Over(all));
    }

    [Fact]
    public void A_key_nobody_recognises_does_not_end_the_tour()
    {
        // A key written by an older build, or one that has been renamed. It must not count as the
        // whole tour being done.
        Assert.False(CoachTour.Over(["something-else"]));
        Assert.Equal(CoachMark.AddMusic, CoachTour.Next(["something-else"]));
    }

    /// <summary>What the caller goes through when a hint's control is not on screen: the rest of the
    /// tour, in order, so one hint that cannot be placed does not block the ones after it.</summary>
    [Fact]
    public void The_remaining_marks_are_the_unseen_ones_in_order()
    {
        Assert.Equal(CoachTour.Order, CoachTour.Remaining([]));

        Assert.Equal(
            [CoachMark.ArtHeader, CoachMark.SignIn],
            CoachTour.Remaining([CoachTour.Key(CoachMark.AddMusic), CoachTour.Key(CoachMark.Views)]));
    }

    [Fact]
    public void Nothing_remains_once_every_mark_is_seen()
        => Assert.Empty(CoachTour.Remaining([.. CoachTour.Order.Select(CoachTour.Key)]));

    [Fact]
    public void Every_mark_has_its_own_key()
    {
        var keys = CoachTour.Order.Select(CoachTour.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key)));
    }
}

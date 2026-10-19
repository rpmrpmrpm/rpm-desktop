using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a wall of covers keeps when a record scrolls off, and what it gives back.
///
/// <para>A virtualizing list creates and removes the row at the edge of its window while its size estimate
/// settles. This can happen eleven times in one second. If a cover's picture were given back on every
/// removal, each row that came back would have to be read and decoded again. That costs the UI thread time
/// for a row that never went anywhere. So a record that comes straight back must keep its picture. The
/// first test below checks this.</para>
/// </summary>
public class CoverHoldTests
{
    /// <summary>The library window's wall: a sharp layer over a resident layer. Pictures are given back
    /// after a grace time, and the sharp layer has a small budget.</summary>
    private static CoverHold Held => CoverHold.SharpOverResident;

    /// <summary>Entries are named by an int so the tests read as "which covers went".</summary>
    private static List<int> Drop(long now, params (int Cover, long Since, int Edge)[] offScreen)
        => Held.ToDrop(offScreen, now);

    /// <summary>The whole fix: a row bouncing on the edge of the realization window costs
    /// nothing.</summary>
    [Fact]
    public void A_record_that_just_left_keeps_its_picture()
        => Assert.Empty(Drop(now: 100, (Cover: 1, Since: 0, Edge: 256)));

    [Fact]
    public void A_record_that_has_been_gone_long_enough_gives_it_back()
        => Assert.Equal([1], Drop(now: 5_000, (Cover: 1, Since: 0, Edge: 256)));

    [Fact]
    public void The_grace_is_the_boundary()
    {
        var grace = (long)Held.Grace.TotalMilliseconds;
        Assert.Empty(Drop(grace - 1, (Cover: 1, Since: 0, Edge: 256)));
        Assert.Equal([1], Drop(grace, (Cover: 1, Since: 0, Edge: 256)));
    }

    /// <summary>Inside the grace period but over the budget, the oldest go first. The newest are the rows
    /// just off the top and bottom of the screen, and they are the ones about to be asked for
    /// again.</summary>
    [Fact]
    public void Past_the_budget_the_oldest_go_first()
    {
        var edge = 640;
        var each = ArtScaler.BytesFor(edge);
        var fits = (int)(Held.Budget / each);

        // One more than the budget holds, all of them well inside their grace, each left a tick apart.
        var entries = Enumerable.Range(0, fits + 1)
            .Select(i => (Cover: i, Since: (long)i, Edge: edge))
            .ToArray();

        var dropped = Held.ToDrop(entries, now: 10);

        Assert.Equal([0], dropped);
    }

    [Fact]
    public void A_small_cover_lets_more_records_keep_one()
    {
        Assert.True(Held.Budget / ArtScaler.BytesFor(256)
                    > Held.Budget / ArtScaler.BytesFor(640),
                    "the budget is in bytes precisely so the count follows the size");
    }

    [Fact]
    public void Nothing_off_screen_is_nothing_to_do()
        => Assert.Empty(Held.ToDrop(Array.Empty<(int, long, int)>(), now: 9_999));

    // ── and the other wall, which holds one picture rather than two ──

    /// <summary>The visualizer's wall has no layer under its cover. A rule that drops pictures on a timer
    /// would drop them even when memory is not short. Scroll for three seconds and turn round, and every
    /// row behind you would have lost its cover. With only a budget, nothing is dropped until memory is
    /// needed.</summary>
    [Fact]
    public void One_picture_is_never_given_back_for_age_alone()
        => Assert.Empty(CoverHold.OnePicture.ToDrop(
            [(Card: 1, Since: 0L, Edge: 384)], now: 60_000));

    [Fact]
    public void One_picture_is_given_back_under_pressure()
    {
        var edge = 384;
        var fits = (int)(CoverHold.OnePicture.Budget / ArtScaler.BytesFor(edge));
        var entries = Enumerable.Range(0, fits + 1)
            .Select(i => (Card: i, Since: (long)i, Edge: edge))
            .ToArray();

        Assert.Equal([0], CoverHold.OnePicture.ToDrop(entries, now: 10));
    }
}

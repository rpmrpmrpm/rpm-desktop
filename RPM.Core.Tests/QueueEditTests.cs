using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Editing the queue while it plays. The playing track is held as a position, so every removal above
/// it moves it. A mistake here does not throw: it plays the wrong song next.
/// </summary>
public class QueueEditTests
{
    private static QueueEdit.RemovalPlan Plan(int count, int current, params int[] remove) =>
        QueueEdit.PlanRemoval(count, current, remove);

    /// <summary>Highest first, always. Removing 1 then 3 from a five-track queue would take the wrong
    /// track second, because removing 1 has already pulled 3 down to 2.</summary>
    [Fact]
    public void Removals_come_back_highest_first()
    {
        var plan = Plan(5, -1, 1, 3, 0);
        Assert.Equal([3, 1, 0], plan.Removals);
    }

    /// <summary>Each track taken from above the playing one pulls it down by exactly one.</summary>
    [Theory]
    [InlineData(4, new[] { 0 }, 3)]
    [InlineData(4, new[] { 0, 1 }, 2)]
    [InlineData(4, new[] { 0, 1, 2, 3 }, 0)]
    public void The_playing_track_slides_down_past_each_removal_above_it(
        int current, int[] remove, int expected)
    {
        var plan = Plan(10, current, remove);
        Assert.False(plan.RemovedCurrent);
        Assert.Equal(expected, plan.CurrentIndex);
    }

    /// <summary>Removing below the playing track cannot move it: its position is unchanged.</summary>
    [Fact]
    public void Removals_below_the_playing_track_leave_it_where_it_is()
    {
        var plan = Plan(10, 2, 5, 7, 9);
        Assert.False(plan.RemovedCurrent);
        Assert.Equal(2, plan.CurrentIndex);
    }

    /// <summary>Taking out the playing track is not "the next track slides into the slot and plays".
    /// Playback stops, and there is no current index.</summary>
    [Fact]
    public void Removing_the_playing_track_stops_rather_than_sliding_onto_its_neighbour()
    {
        var plan = Plan(5, 2, 2);
        Assert.True(plan.RemovedCurrent);
        Assert.Equal(-1, plan.CurrentIndex);
    }

    /// <summary>A multi-select that includes the playing track stops playback too, however many of
    /// the other tracks were above it.</summary>
    [Fact]
    public void A_selection_containing_the_playing_track_stops()
    {
        var plan = Plan(6, 3, 0, 1, 3, 5);
        Assert.True(plan.RemovedCurrent);
        Assert.Equal(-1, plan.CurrentIndex);
        Assert.Equal([5, 3, 1, 0], plan.Removals);
    }

    /// <summary>Nothing playing stays nothing playing.</summary>
    [Fact]
    public void With_nothing_playing_there_is_nothing_to_track()
    {
        var plan = Plan(5, -1, 0, 4);
        Assert.False(plan.RemovedCurrent);
        Assert.Equal(-1, plan.CurrentIndex);
    }

    /// <summary>A stale selection is dropped and does not throw. The list view can pass rows that the
    /// queue has already moved past, and a queue edit is not worth failing over.</summary>
    [Fact]
    public void Positions_off_the_end_are_ignored()
    {
        var plan = Plan(3, 1, -1, 3, 99, 2);
        Assert.Equal([2], plan.Removals);
        Assert.False(plan.RemovedCurrent);
        Assert.Equal(1, plan.CurrentIndex);
    }

    /// <summary>The same position twice is one removal, otherwise the second would take its
    /// neighbour.</summary>
    [Fact]
    public void A_repeated_position_is_removed_once()
    {
        var plan = Plan(4, 3, 1, 1, 1);
        Assert.Equal([1], plan.Removals);
        Assert.Equal(2, plan.CurrentIndex);
    }

    [Fact]
    public void Removing_nothing_changes_nothing()
    {
        var plan = Plan(4, 2);
        Assert.Empty(plan.Removals);
        Assert.False(plan.RemovedCurrent);
        Assert.Equal(2, plan.CurrentIndex);
    }

    /// <summary>Clearing the queue while the player is using it: everything goes, and playback stops
    /// because the playing track was in the queue.</summary>
    [Fact]
    public void Removing_every_track_empties_the_queue_and_stops()
    {
        var plan = Plan(3, 1, 0, 1, 2);
        Assert.Equal([2, 1, 0], plan.Removals);
        Assert.True(plan.RemovedCurrent);
        Assert.Equal(-1, plan.CurrentIndex);
    }

    // ── reordering ──

    /// <summary>Dragging the playing track moves the playing marker with it. Nothing stops: the song
    /// carries on from its new place in the running order.</summary>
    [Fact]
    public void Dragging_the_playing_track_moves_the_marker_with_it()
    {
        var plan = QueueEdit.PlanMove(6, 4, from: 4, to: 1);

        Assert.Equal(4, plan.From);
        Assert.Equal(1, plan.To);
        Assert.Equal(1, plan.CurrentIndex);
    }

    /// <summary>Lifting a track from below the playing one and dropping it above pushes the playing
    /// track down one place. This half of the arithmetic is easy to miss: if the removal is applied
    /// but not the insertion, the marker ends up one row off.</summary>
    [Fact]
    public void A_track_carried_from_below_the_playing_one_to_above_it_pushes_it_down()
    {
        var plan = QueueEdit.PlanMove(6, 3, from: 0, to: 5);
        Assert.Equal(2, plan.CurrentIndex);
    }

    [Fact]
    public void A_track_carried_from_above_the_playing_one_to_below_it_pulls_it_up()
    {
        var plan = QueueEdit.PlanMove(6, 3, from: 5, to: 0);
        Assert.Equal(4, plan.CurrentIndex);
    }

    /// <summary>A drag entirely on one side of the playing track cannot move it.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(4, 5)]
    [InlineData(5, 4)]
    public void A_drag_that_steps_over_the_playing_track_leaves_it_alone(int from, int to)
    {
        var plan = QueueEdit.PlanMove(6, 3, from, to);
        Assert.Equal(3, plan.CurrentIndex);
    }

    /// <summary>Dropped where it was picked up: nothing to apply, and the caller can see this without
    /// comparing indexes itself.</summary>
    [Fact]
    public void Dropping_a_track_where_it_started_is_nothing()
    {
        var plan = QueueEdit.PlanMove(4, 2, from: 1, to: 1);

        Assert.True(plan.IsNoOp);
        Assert.Equal(2, plan.CurrentIndex);
    }

    /// <summary>A drag past either end lands at the end it was dragged towards and does not throw: a
    /// finger can leave the list, and the row it was carrying has to go somewhere.</summary>
    [Theory]
    [InlineData(-3, 0)]
    [InlineData(99, 3)]
    public void A_drag_off_the_end_lands_at_the_end(int to, int expected)
    {
        var plan = QueueEdit.PlanMove(4, -1, from: 1, to: to);
        Assert.Equal(expected, plan.To);
    }

    /// <summary>Nothing playing stays nothing playing through a reorder as well.</summary>
    [Fact]
    public void A_reorder_with_nothing_playing_tracks_nothing()
    {
        var plan = QueueEdit.PlanMove(5, -1, from: 0, to: 4);
        Assert.Equal(-1, plan.CurrentIndex);
    }

    /// <summary>A queue of one cannot be reordered, and asking is not an error.</summary>
    [Fact]
    public void A_queue_of_one_has_nothing_to_reorder()
    {
        var plan = QueueEdit.PlanMove(1, 0, from: 0, to: 0);
        Assert.True(plan.IsNoOp);
    }

    /// <summary>The plan is what the list itself does: remove at From, insert at To. If it were
    /// anything else, the marker would follow a different queue from the one on screen.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 4)]
    [InlineData(4, 2)]
    public void The_marker_follows_the_track_a_real_list_would_end_up_with(int from, int to)
    {
        var queue = new List<string> { "a", "b", "c", "d", "e" };

        for (var current = 0; current < queue.Count; current++)
        {
            var playing = queue[current];
            var plan = QueueEdit.PlanMove(queue.Count, current, from, to);

            var after = new List<string>(queue);
            var carried = after[plan.From];
            after.RemoveAt(plan.From);
            after.Insert(plan.To, carried);

            Assert.Equal(playing, after[plan.CurrentIndex]);
        }
    }
}

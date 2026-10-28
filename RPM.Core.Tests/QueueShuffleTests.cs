using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The Now playing card's shuffle key: what is still to come goes in another order, and the next press puts
/// it back. What has been played, and the song playing, never move.
/// </summary>
public class QueueShuffleTests
{
    /// <summary>A row of the queue. A class, so that two rows with one name are still two rows, as two
    /// copies of one record in the queue are.</summary>
    private sealed class Row(string name)
    {
        public override string ToString() => name;
    }

    private static List<Row> Rows(int count) => [.. Enumerable.Range(0, count).Select(i => new Row($"r{i}"))];

    [Fact]
    public void Rows_up_to_and_including_the_playing_one_stay_where_they_are()
    {
        var rows = Rows(10);
        for (var seed = 0; seed < 50; seed++)
        {
            var order = QueueShuffle.Shuffled(rows, 3, new Random(seed));
            Assert.Equal(rows.Take(4), order.Take(4));
        }
    }

    [Fact]
    public void A_shuffle_keeps_every_row_once()
    {
        var rows = Rows(12);
        var order = QueueShuffle.Shuffled(rows, 2, new Random(7));
        Assert.Equal(rows.Count, order.Count);
        Assert.Equal(rows.ToHashSet(ReferenceEqualityComparer.Instance), order.ToHashSet(ReferenceEqualityComparer.Instance));
    }

    /// <summary>A key that was pressed and changed nothing looks broken. Two rows to come have only one other
    /// order, so every seed has to find it.</summary>
    [Fact]
    public void A_shuffle_never_comes_back_in_the_order_it_went_in()
    {
        var rows = Rows(5);
        for (var seed = 0; seed < 200; seed++)
        {
            var order = QueueShuffle.Shuffled(rows, 2, new Random(seed));
            Assert.NotEqual(rows, order);
        }
    }

    [Fact]
    public void With_nothing_playing_every_row_may_move()
    {
        var rows = Rows(6);
        var firstRows = Enumerable.Range(0, 100)
            .Select(seed => QueueShuffle.Shuffled(rows, -1, new Random(seed))[0])
            .ToHashSet(ReferenceEqualityComparer.Instance);
        Assert.True(firstRows.Count > 1);
    }

    [Theory]
    [InlineData(0, -1, false)]
    [InlineData(1, -1, false)]
    [InlineData(2, -1, true)]
    [InlineData(5, 2, true)]
    [InlineData(5, 3, false)]   // one row to come has no other order
    [InlineData(5, 4, false)]
    public void A_shuffle_is_offered_only_with_two_rows_to_come(int count, int current, bool expected) =>
        Assert.Equal(expected, QueueShuffle.CanShuffle(Rows(count), current));

    /// <summary>The mixtape after an album is the radio's, picked to follow the album's end. Shuffling the album
    /// leaves it after the album, in the order the radio picked it.</summary>
    [Fact]
    public void Held_rows_keep_their_slots_and_the_rest_never_move_into_them()
    {
        var album = Rows(8);
        var mixtape = Rows(4);
        List<Row> rows = [.. album, .. mixtape];
        var held = mixtape.ToHashSet(ReferenceEqualityComparer.Instance);

        for (var seed = 0; seed < 50; seed++)
        {
            var order = QueueShuffle.Shuffled(rows, 0, new Random(seed), r => held.Contains(r));
            Assert.Equal(mixtape, order.Skip(8));
            Assert.Equal(album[0], order[0]);
            Assert.Equal(album.ToHashSet(ReferenceEqualityComparer.Instance),
                         order.Take(8).ToHashSet(ReferenceEqualityComparer.Instance));
            Assert.NotEqual(album, order.Take(8));
        }
    }

    [Fact]
    public void Held_rows_do_not_count_towards_a_shuffle()
    {
        var rows = Rows(6);
        var held = rows.Skip(2).ToHashSet(ReferenceEqualityComparer.Instance);   // one album row to come, then a radio

        Assert.False(QueueShuffle.CanShuffle(rows, 0, r => held.Contains(r)));
        Assert.Equal(rows, QueueShuffle.Shuffled(rows, 0, new Random(1), r => held.Contains(r)));
    }

    [Fact]
    public void Restoring_with_held_rows_gives_back_the_queue_as_it_was()
    {
        var before = Rows(10);
        var held = before.Skip(7).ToHashSet(ReferenceEqualityComparer.Instance);
        var shuffled = QueueShuffle.Shuffled(before, 1, new Random(4), r => held.Contains(r));

        Assert.Equal(before, QueueShuffle.Restored(shuffled, 1, before, r => held.Contains(r)));
    }

    [Fact]
    public void Too_few_rows_to_come_leave_the_queue_as_it_is()
    {
        var rows = Rows(4);
        Assert.Equal(rows, QueueShuffle.Shuffled(rows, 2, new Random(1)));
    }

    [Fact]
    public void Restoring_straight_away_gives_back_the_queue_as_it_was()
    {
        var before = Rows(9);
        var shuffled = QueueShuffle.Shuffled(before, 1, new Random(3));
        Assert.Equal(before, QueueShuffle.Restored(shuffled, 1, before));
    }

    /// <summary>The queue played on after the shuffle. The rows heard since stay heard, and what is still to
    /// come goes back into the order it had.</summary>
    [Fact]
    public void Restoring_after_playing_on_keeps_what_was_played()
    {
        var before = Rows(6);                                 // r0 playing
        var (r0, r1, r2, r3, r4, r5) = (before[0], before[1], before[2], before[3], before[4], before[5]);
        List<Row> shuffled = [r0, r4, r2, r5, r1, r3];

        var restored = QueueShuffle.Restored(shuffled, 2, before);   // r2 playing now

        Assert.Equal([r0, r4, r2, r1, r3, r5], restored);
    }

    /// <summary>A "play next" while the queue was shuffled was put where it is on purpose, so it stays
    /// there; the rest go back around it.</summary>
    [Fact]
    public void A_row_added_while_shuffled_keeps_its_slot()
    {
        var before = Rows(5);
        var (r0, r1, r2, r3, r4) = (before[0], before[1], before[2], before[3], before[4]);
        var added = new Row("added");
        List<Row> now = [r0, added, r3, r1, r4, r2];

        Assert.Equal([r0, added, r1, r2, r3, r4], QueueShuffle.Restored(now, 0, before));
    }

    [Fact]
    public void A_row_removed_while_shuffled_is_not_brought_back()
    {
        var before = Rows(5);
        var (r0, r1, r2, r3, r4) = (before[0], before[1], before[2], before[3], before[4]);
        List<Row> now = [r0, r4, r1, r2];   // r3 taken out

        Assert.Equal([r0, r1, r2, r4], QueueShuffle.Restored(now, 0, before));
    }

    /// <summary>One record queued twice is two rows, and each goes back to its own place.</summary>
    [Fact]
    public void Two_rows_of_one_record_each_go_back_to_their_own_place()
    {
        var a = new Row("same");
        var b = new Row("same");
        var c = new Row("other");
        List<Row> before = [a, c, b];

        Assert.Equal(before, QueueShuffle.Restored([b, a, c], -1, before));
    }
}

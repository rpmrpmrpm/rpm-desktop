using RPM.Desktop.Presentation.Import;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a drag inside a record is asking for.
///
/// <para>A record of rows 22px tall, drawn from y=0. The gap is a count of rows above the line: 0 is
/// in front of the first track, n is after the last one.</para>
/// </summary>
public class QueueReorderTests
{
    private static List<(double Top, double Bottom)> Rows(int n) =>
        Enumerable.Range(0, n).Select(i => (i * 22.0, i * 22.0 + 22)).ToList();

    private static List<bool> Carrying(int n, params int[] carried) =>
        Enumerable.Range(0, n).Select(carried.Contains).ToList();

    // ── which gap ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 0)]      // top half of the first row
    [InlineData(20, 1)]     // bottom half of it
    [InlineData(24, 1)]     // top half of the second
    [InlineData(42, 2)]     // bottom half of the second
    public void A_row_is_split_down_the_middle(double y, int expected)
    {
        Assert.Equal(expected, QueueReorder.SlotAt(Rows(4), true, true, y));
    }

    [Fact]
    public void Above_the_record_lands_in_front_of_it()
    {
        Assert.Equal(0, QueueReorder.SlotAt(Rows(4), true, true, -400));
    }

    [Fact]
    public void Below_the_record_lands_at_the_end_of_it()
    {
        Assert.Equal(4, QueueReorder.SlotAt(Rows(4), true, true, 5000));
    }

    /// <summary>The record runs on above and below what is drawn. So the line cannot offer a place outside
    /// the rows on screen, because there are tracks out there it would jump over.</summary>
    [Fact]
    public void A_record_scrolled_off_both_ends_keeps_the_line_inside_what_is_drawn()
    {
        Assert.Equal(1, QueueReorder.SlotAt(Rows(4), blockStartsRecord: false, blockEndsRecord: false, -400));
        Assert.Equal(3, QueueReorder.SlotAt(Rows(4), blockStartsRecord: false, blockEndsRecord: false, 5000));
    }

    [Fact]
    public void An_empty_block_asks_for_nothing()
    {
        Assert.Equal(0, QueueReorder.SlotAt([], true, true, 40));
    }

    // ── where the line is drawn ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_line_sits_on_the_boundary_the_gap_names()
    {
        var rows = Rows(4);
        Assert.Equal(0, QueueReorder.LineAt(rows, 0));
        Assert.Equal(44, QueueReorder.LineAt(rows, 2));
        Assert.Equal(88, QueueReorder.LineAt(rows, 4));
    }

    // ── the neighbours handed to the store ──────────────────────────────────────────────────────

    [Fact]
    public void The_neighbours_are_the_rows_either_side_of_the_gap()
    {
        var (above, below) = QueueReorder.Neighbours(Carrying(4), 2);
        Assert.Equal(1, above);
        Assert.Equal(2, below);
    }

    [Fact]
    public void The_start_and_the_end_of_the_record_have_one_neighbour_each()
    {
        Assert.Equal((null, 0), QueueReorder.Neighbours(Carrying(4), 0));
        Assert.Equal((3, null), QueueReorder.Neighbours(Carrying(4), 4));
    }

    /// <summary>A row cannot be dropped after itself: asking the store to fit it between itself and
    /// its neighbour is a gap of nothing to fit into.</summary>
    [Fact]
    public void A_row_being_carried_is_never_its_own_neighbour()
    {
        var (above, below) = QueueReorder.Neighbours(Carrying(5, 1, 2), 2);
        Assert.Equal(0, above);
        Assert.Equal(3, below);
    }

    // ── what the record would read like ─────────────────────────────────────────────────────────

    [Fact]
    public void The_carried_row_lands_in_the_gap()
    {
        Assert.Equal([0, 3, 1, 2], QueueReorder.Apply(Carrying(4, 3), 1));
    }

    [Fact]
    public void A_run_of_carried_rows_keeps_the_order_it_was_picked_up_in()
    {
        Assert.Equal([3, 0, 1, 2], QueueReorder.Apply(Carrying(4, 0, 1, 2), 4));
    }

    [Fact]
    public void Rows_from_all_over_the_record_come_together_at_the_gap()
    {
        Assert.Equal([1, 0, 4, 2, 3], QueueReorder.Apply(Carrying(5, 0, 4), 2));
    }

    // ── the drag that changed nothing ───────────────────────────────────────────────────────────

    [Fact]
    public void Putting_a_row_back_where_it_was_moves_nothing()
    {
        Assert.False(QueueReorder.Moves(Carrying(4, 1), 1));
        Assert.False(QueueReorder.Moves(Carrying(4, 1), 2));
    }

    [Fact]
    public void Putting_it_anywhere_else_moves_something()
    {
        Assert.True(QueueReorder.Moves(Carrying(4, 1), 0));
        Assert.True(QueueReorder.Moves(Carrying(4, 1), 3));
        Assert.True(QueueReorder.Moves(Carrying(4, 1), 4));
    }

    [Fact]
    public void A_run_dropped_on_its_own_edges_moves_nothing()
    {
        Assert.False(QueueReorder.Moves(Carrying(5, 1, 2), 1));
        Assert.False(QueueReorder.Moves(Carrying(5, 1, 2), 3));
        Assert.True(QueueReorder.Moves(Carrying(5, 1, 2), 4));
    }
}

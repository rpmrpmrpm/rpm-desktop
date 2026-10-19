using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

/// <summary>
/// What a drop at a given gap means for the rows it is carrying. The gap is a count of rows above
/// the line, so a block of rows 10..17 has gaps 10 through 18.
/// </summary>
public class GridDragBlockTests
{
    private static GridDragBlock Block(int first, int last, params (int First, int Last)[] carried)
    {
        var set = new SpanSet();
        foreach (var (a, b) in carried)
            set.Add(a, b);
        return new GridDragBlock(first, last, set);
    }

    [Fact]
    public void A_block_knows_how_many_rows_it_spans()
    {
        Assert.Equal(8, Block(10, 17, (12, 12)).Count);
    }

    [Fact]
    public void One_row_dropped_where_it_already_is_moves_nothing()
    {
        var block = Block(10, 17, (12, 12));

        Assert.False(block.Moves(12));
        Assert.False(block.Moves(13));
    }

    [Fact]
    public void One_row_dropped_anywhere_else_moves()
    {
        var block = Block(10, 17, (12, 12));

        Assert.True(block.Moves(10));
        Assert.True(block.Moves(11));
        Assert.True(block.Moves(14));
        Assert.True(block.Moves(18));
    }

    [Fact]
    public void A_run_dropped_where_it_already_is_moves_nothing()
    {
        var block = Block(10, 17, (12, 14));

        Assert.False(block.Moves(12));
        Assert.False(block.Moves(15));
    }

    [Fact]
    public void A_block_carrying_nothing_moves_nothing()
    {
        Assert.False(Block(10, 17).Moves(12));
    }

    [Fact]
    public void One_row_moved_down_comes_out_in_the_new_order()
    {
        var block = Block(10, 14, (10, 10));

        // 10 11 12 13 14, carrying 10, dropped after 12.
        Assert.Equal([11, 12, 10, 13, 14], block.Settled(13));
    }

    [Fact]
    public void One_row_moved_up_comes_out_in_the_new_order()
    {
        var block = Block(10, 14, (13, 13));

        Assert.Equal([10, 13, 11, 12, 14], block.Settled(11));
    }

    [Fact]
    public void A_row_dropped_at_the_top_goes_first()
    {
        var block = Block(10, 14, (12, 12));

        Assert.Equal([12, 10, 11, 13, 14], block.Settled(10));
    }

    [Fact]
    public void A_row_dropped_at_the_end_goes_last()
    {
        var block = Block(10, 14, (12, 12));

        Assert.Equal([10, 11, 13, 14, 12], block.Settled(15));
    }

    /// <summary>Three tracks dragged together stay in their own order, rather than reversing.</summary>
    [Fact]
    public void A_carried_run_keeps_its_own_order()
    {
        var block = Block(10, 16, (10, 12));

        Assert.Equal([13, 14, 10, 11, 12, 15, 16], block.Settled(15));
    }

    /// <summary>Rows picked up from either end of a record travel together and land together.</summary>
    [Fact]
    public void Rows_that_are_not_next_to_each_other_land_together()
    {
        var block = Block(10, 14, (10, 10), (14, 14));

        Assert.Equal([11, 10, 14, 12, 13], block.Settled(12));
    }

    [Fact]
    public void Settling_never_loses_or_repeats_a_row()
    {
        var block = Block(0, 9, (2, 4), (7, 7));

        for (var slot = 0; slot <= 10; slot++)
        {
            var settled = block.Settled(slot).ToList();
            Assert.Equal(10, settled.Count);
            Assert.Equal(Enumerable.Range(0, 10), settled.OrderBy(x => x));
        }
    }

    [Fact]
    public void The_neighbours_of_a_gap_skip_the_rows_being_carried()
    {
        var block = Block(10, 17, (12, 14));

        // Dropping after 15: above is 15, below is 16. Neither is carried.
        Assert.Equal((15, 16), block.Neighbours(16));
    }

    [Fact]
    public void The_neighbours_at_the_start_of_a_block_have_nothing_above()
    {
        var block = Block(10, 17, (12, 12));

        Assert.Equal((null, 10), block.Neighbours(10));
    }

    [Fact]
    public void The_neighbours_at_the_end_of_a_block_have_nothing_below()
    {
        var block = Block(10, 17, (12, 12));

        Assert.Equal((17, null), block.Neighbours(18));
    }

    /// <summary>
    /// A gap immediately after the carried rows: the row above is the last uncarried one before them,
    /// which is what the store is told to fit them after.
    /// </summary>
    [Fact]
    public void A_gap_against_the_carried_rows_looks_past_them()
    {
        var block = Block(10, 17, (12, 14));

        Assert.Equal((11, 15), block.Neighbours(13));
    }
}

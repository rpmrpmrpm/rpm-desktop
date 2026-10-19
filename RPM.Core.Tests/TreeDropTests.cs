using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>Where a drag over the playlist tree is asking to put what it is carrying.</summary>
public class TreeDropTests
{
    private const double Row = 24;

    /// <summary>A playlist cannot hold anything, so its whole height is a choice between the line above
    /// it and the line below. Offering "inside" on a row that cannot take children is a drop the tree
    /// has no way to represent.</summary>
    [Theory]
    [InlineData(0, TreeDropWhere.Before)]
    [InlineData(11, TreeDropWhere.Before)]
    [InlineData(13, TreeDropWhere.After)]
    [InlineData(24, TreeDropWhere.After)]
    public void A_row_that_cannot_hold_children_is_split_down_the_middle(double y, TreeDropWhere expected)
        => Assert.Equal(expected, TreeDrop.Where(y, Row, canTakeChildren: false));

    /// <summary>A folder keeps its edges for dropping BESIDE it. Without them there is no way to put a
    /// playlist above the first folder in a pane that is all folders, because every row would swallow
    /// the drop.</summary>
    [Theory]
    [InlineData(0, TreeDropWhere.Before)]
    [InlineData(5, TreeDropWhere.Before)]
    [InlineData(7, TreeDropWhere.Into)]
    [InlineData(12, TreeDropWhere.Into)]
    [InlineData(17, TreeDropWhere.Into)]
    [InlineData(19, TreeDropWhere.After)]
    [InlineData(24, TreeDropWhere.After)]
    public void A_folder_keeps_its_edges_for_dropping_beside_it(double y, TreeDropWhere expected)
        => Assert.Equal(expected, TreeDrop.Where(y, Row, canTakeChildren: true));

    /// <summary>A pointer past either end of the row still answers, because a drag that has run off the
    /// list is still asking for something.</summary>
    [Fact]
    public void Past_either_end_it_still_answers()
    {
        Assert.Equal(TreeDropWhere.Before, TreeDrop.Where(-40, Row, canTakeChildren: true));
        Assert.Equal(TreeDropWhere.After, TreeDrop.Where(400, Row, canTakeChildren: true));
    }

    /// <summary>A list that has not been arranged has no height to divide, and must not answer "inside"
    /// off a division by nothing.</summary>
    [Fact]
    public void A_row_with_no_height_yet_answers_before()
        => Assert.Equal(TreeDropWhere.Before, TreeDrop.Where(0, 0, canTakeChildren: true));

    /// <summary>Coming from another parent, the slot is simply the line that was drawn.</summary>
    [Theory]
    [InlineData(0, TreeDropWhere.Before, 0)]
    [InlineData(0, TreeDropWhere.After, 1)]
    [InlineData(3, TreeDropWhere.Before, 3)]
    [InlineData(3, TreeDropWhere.After, 4)]
    public void From_another_parent_the_slot_is_the_line_that_was_drawn(
        int beside, TreeDropWhere where, int expected)
        => Assert.Equal(expected, TreeDrop.Slot(beside, where, carried: -1));

    /// <summary>Moving a row DOWN its own list: the row leaves a slot above the target, so the target
    /// comes up one and the line drawn under it is one place nearer than it looks. Counting from the
    /// list with the row still in it would put a dragged row one place too far down.</summary>
    [Fact]
    public void Moving_down_its_own_list_counts_the_row_it_is_leaving()
    {
        // Five siblings, carrying the first, dropped under the third: it lands third, not fourth.
        Assert.Equal(2, TreeDrop.Slot(beside: 2, TreeDropWhere.After, carried: 0));

        // …and dropped above the third, it lands second.
        Assert.Equal(1, TreeDrop.Slot(beside: 2, TreeDropWhere.Before, carried: 0));
    }

    /// <summary>Moving UP its own list, nothing above it has moved, so the line is the slot.</summary>
    [Fact]
    public void Moving_up_its_own_list_takes_the_line_as_it_stands()
    {
        Assert.Equal(1, TreeDrop.Slot(beside: 1, TreeDropWhere.Before, carried: 4));
        Assert.Equal(2, TreeDrop.Slot(beside: 1, TreeDropWhere.After, carried: 4));
    }
}

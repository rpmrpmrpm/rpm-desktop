using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// The column of cards that are not there, standing behind an empty tab of the groove.
///
/// <para>It says "reviews go here, and there are none yet". So it must not look like a list that has ended,
/// and it must not look like a list that the window has cut off. The column ends by fading out. These tests
/// check the fade: solid at the top, gone at the bottom, and no card brighter than the one above it.</para>
/// </summary>
public class GrooveEmptyStateTests
{
    private static IReadOnlyList<GrooveGhost> Ghosts => new GrooveEmptyState("", "none", "write one", Action: null).Ghosts;

    [Fact]
    public void The_first_three_cards_stand_at_full_strength()
    {
        var ghosts = Ghosts;

        Assert.Equal(1, ghosts[0].Fade);
        Assert.Equal(1, ghosts[1].Fade);
        Assert.Equal(1, ghosts[2].Fade);
    }

    /// <summary>The fourth card is where the column starts to fade.</summary>
    [Fact]
    public void The_fourth_card_is_already_on_its_way_out()
        => Assert.True(Ghosts[3].Fade < 1);

    [Fact]
    public void The_last_card_is_not_there_at_all()
        => Assert.Equal(0, Ghosts[^1].Fade);

    /// <summary>No card is brighter than the card above it, and none is below zero. A fade with a step back
    /// up would look like a card that is loading, not a column that is fading out.</summary>
    [Fact]
    public void Nothing_grows_back_on_the_way_down()
    {
        var ghosts = Ghosts;

        for (var i = 1; i < ghosts.Count; i++)
        {
            Assert.True(ghosts[i].Fade <= ghosts[i - 1].Fade, $"ghost {i} came back stronger");
            Assert.InRange(ghosts[i].Fade, 0, 1);
        }
    }

    /// <summary>The placeholder is the same every time it is drawn. It is a fixed layout, not an animation,
    /// and the reviews tab and the comments tab show the same cards.</summary>
    [Fact]
    public void The_column_is_the_same_column_every_time()
    {
        var one = new GrooveEmptyState("", "none", "write one", Action: null).Ghosts;
        var two = new GrooveEmptyState("", "none", "write one", Action: null).Ghosts;

        Assert.Equal(one, two);
    }

    /// <summary>The action button under the invitation is shown only when the tab passes an action. The
    /// reviews tab has no writing box of its own. The comments tab has its box under the column. If the
    /// empty state showed a button on both tabs, there would be two ways into one box.</summary>
    [Fact]
    public void The_key_is_there_only_where_the_tab_hands_one_over()
    {
        Assert.True(new GrooveEmptyState("", "none", "be the first", "Write a review").HasAction);
        Assert.False(new GrooveEmptyState("", "none", "be the first", Action: null).HasAction);
        Assert.False(new GrooveEmptyState("", "none", "be the first", "").HasAction);
    }
}

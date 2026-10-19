using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Home;

namespace RPM.Desktop.Tests;

/// <summary>
/// How far one press of a home row's left or right key moves it. A step must leave some of the old view
/// on screen, or the reader loses their place. On a narrow pane the step must still be more than zero,
/// or the keys do nothing.
/// </summary>
public class HomeRowKeyTests
{
    [Fact]
    public void A_step_leaves_one_sleeve_of_what_was_on_screen()
        => Assert.Equal(800 - HomeView.WidestSleeve, HomeView.KeyStep(800));

    [Fact]
    public void A_pane_narrower_than_a_sleeve_still_moves()
        => Assert.Equal(HomeView.LeastStep, HomeView.KeyStep(120));

    [Fact]
    public void A_pane_with_no_width_yet_still_answers_a_step()
        => Assert.Equal(HomeView.LeastStep, HomeView.KeyStep(0));

    /// <summary>A row fades out at its edges more deeply than a list does. A cover is much wider than a
    /// list line is tall, so the list's fade depth would look like a thin smudge on a cover.</summary>
    [Fact]
    public void A_row_dissolves_deeper_than_a_list()
    {
        Assert.True(HomeView.RowHemDepth >= ScrollHem.Depth * 2);

        var (head, foot) = ScrollHem.Depths(offset: 300, scrollable: 900, HomeView.RowHemDepth);
        Assert.Equal(HomeView.RowHemDepth, head);
        Assert.Equal(HomeView.RowHemDepth, foot);
    }
}

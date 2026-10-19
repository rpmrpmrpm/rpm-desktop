using RPM.Desktop.Presentation.Pegasus;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The zoom an artist's card gets under the remote's focus in Pegasus (FocusLift). It matches the tvOS
/// app: 1.12 on a card small enough for it. The growth is capped at 18 points a side, which is less
/// than the 22 points between two cards. On a 2560 by 1440 screen a card is about 480 across, and a
/// 1.12 zoom on a card that size would grow it 29 points a side, over its neighbour.
/// </summary>
public class FocusLiftTests
{
    [Theory]
    [InlineData(150)]
    [InlineData(213)]
    [InlineData(300)]
    public void A_card_small_enough_zooms_as_the_tvOS_app_does(double edge)
    {
        Assert.Equal(FocusLift.Scale, FocusLift.ScaleFor(edge), 3);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(480)]
    [InlineData(900)]
    public void A_large_card_grows_no_more_than_the_gap_allows(double edge)
    {
        var grows = edge * (FocusLift.ScaleFor(edge) - 1) / 2;
        Assert.True(grows <= FocusLift.MostGrowth + 0.01, $"a card {edge} across grows {grows:F1} a side");
        Assert.True(grows < 22, "the zoomed photograph must stay inside the 22 between two cards");
    }
}

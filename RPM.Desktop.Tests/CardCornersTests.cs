using Microsoft.UI.Xaml;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// The corner a page inside one of the shell's cards wears at its foot.
///
/// <para>The value depends on what is at the foot of the card, so it is not a constant. The card's bottom
/// edge belongs to the page only while nothing else stands on it. The library's message band takes that
/// edge whenever there is news about the shelf. If a page kept its rounded corner in that case, the
/// card's corner would float a band's height above the card. These tests check which corner is rounded
/// for each of the two cases, and that the right-hand side (the rail's edge, not the card's) is square
/// in both.</para>
/// </summary>
public class CardCornersTests
{
    [Fact]
    public void FootOnTheCardKeepsTheCardsCorner()
    {
        var corners = CardCorners.RoundedLeft(CardFoot.CardEdge);

        Assert.Equal(CardCorners.Radius, corners.BottomLeft);
        Assert.Equal(CardCorners.Radius, corners.TopLeft);
    }

    [Fact]
    public void FootOnTheBandGivesTheCornerUp()
    {
        var corners = CardCorners.RoundedLeft(CardFoot.Band);

        Assert.Equal(0, corners.BottomLeft);
    }

    /// <summary>The top is always the card's, whatever is at the foot. A band under the page does not
    /// change the top corner.</summary>
    [Fact]
    public void TheHeadIsTheCardsInBothReadings()
    {
        Assert.Equal(CardCorners.RoundedLeft(CardFoot.CardEdge).TopLeft,
                     CardCorners.RoundedLeft(CardFoot.Band).TopLeft);
    }

    /// <summary>The right-hand side is the rail's edge, not the card's, and it is square in both cases.
    /// The band's own corners (0,0,0,8) are cut the same way, and the two must agree.</summary>
    [Theory]
    [InlineData(CardFoot.CardEdge)]
    [InlineData(CardFoot.Band)]
    public void TheRailSideIsSquare(CardFoot foot)
    {
        CornerRadius corners = CardCorners.RoundedLeft(foot);

        Assert.Equal(0, corners.TopRight);
        Assert.Equal(0, corners.BottomRight);
    }
}

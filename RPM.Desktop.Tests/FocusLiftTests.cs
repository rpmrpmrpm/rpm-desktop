using RPM.Desktop.Presentation.Pegasus;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The look of the remote's focus in Pegasus (FocusLift): a focused card grows by CardGrow about its
/// middle, and every other card and key is dimmed, keys more than cards. On the Skia head a scaled
/// element is cut at its parent's bounds, so an act's round photograph stands in a holder with room
/// round it; the growth has to stay inside that room.
/// </summary>
public class FocusLiftTests
{
    [Theory]
    [InlineData(150)]
    [InlineData(480)]
    [InlineData(900)]
    public void A_focused_photograph_grows_inside_its_holder(double edge)
    {
        var grows = edge * (FocusLift.CardGrow - 1) / 2;
        Assert.True(grows < edge * FocusLift.RoomShare, $"a photograph {edge} across grows {grows:F1} a side");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(480)]
    public void The_holder_and_the_photograph_margins_cancel(double edge)
    {
        // The holder is pulled back by its room and the photograph pushed in by the same room, so the
        // photograph stands where it would without a holder.
        Assert.Equal(-FocusLift.HolderMargin(edge).Left, FocusLift.PhotoMargin(edge).Left, 6);
        Assert.Equal(-FocusLift.HolderMargin(edge).Top, FocusLift.PhotoMargin(edge).Top, 6);
        Assert.Equal(edge * FocusLift.RoomShare, FocusLift.PhotoMargin(edge).Right, 6);
    }

    [Fact]
    public void Keys_without_the_focus_are_darker_than_cards()
    {
        Assert.InRange(FocusLift.CardLight, 0.01, 0.99);
        Assert.InRange(FocusLift.KeyLight, 0.01, 0.99);
        Assert.True(FocusLift.KeyLight < FocusLift.CardLight);
    }
}

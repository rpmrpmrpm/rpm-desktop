#if DEBUG
using RPM.Desktop.Presentation.MixLab;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where the mix lab puts a mark's tab when there is another mark beside it.
///
/// <para>This is worth testing because the fault is hard to see in the code and obvious on screen.
/// Two tabs drawn at the same place look like one tab, and only the one drawn last can be read. On
/// a deck that is coming in, this happens often. The cue is worked out from the intro end, so on a
/// record with an intro shorter than the fade, the two marks land within a bar of each other and the
/// "in" tab is always under the "cue" tab.</para>
/// </summary>
public class MixLabTabLayoutTests
{
    private const float First = float.NegativeInfinity;

    [Fact]
    public void FirstTabSitsUnderItsOwnLine()
    {
        // Half a tab's width to the left of the mark, which is what centres an 11-wide tab on it.
        Assert.Equal(94.5f, MixLabTimeline.TabLeft(x: 100, width: 11, placed: First, w: 400));
    }

    [Fact]
    public void ATabClearOfTheOneBeforeItDoesNotMove()
    {
        Assert.Equal(194.5f, MixLabTimeline.TabLeft(x: 200, width: 20, placed: 60, w: 400));
    }

    [Fact]
    public void ATabOverTheOneBeforeItIsPushedAlong()
    {
        // "cue" at 100 and "in" a second behind it: the second tab starts where the first one ended.
        float cue = MixLabTimeline.TabLeft(x: 100, width: 22, placed: First, w: 400);
        float in_ = MixLabTimeline.TabLeft(x: 104, width: 16, placed: cue + 22, w: 400);

        Assert.True(in_ >= cue + 22, "the second tab starts past the end of the first");
        Assert.Equal(cue + 22 + 2, in_);
    }

    [Fact]
    public void TwoMarksOnTheVerySameSecondStillGetTwoTabs()
    {
        // Two marks at the same x. The two tabs must still be separate rectangles.
        float one = MixLabTimeline.TabLeft(x: 100, width: 16, placed: First, w: 400);
        float two = MixLabTimeline.TabLeft(x: 100, width: 16, placed: one + 16, w: 400);

        Assert.NotEqual(one, two);
        Assert.True(two >= one + 16, "the second clears the first entirely");
    }

    [Fact]
    public void ATabAtTheLeftEdgeIsHeldInsideThePicture()
    {
        Assert.Equal(0, MixLabTimeline.TabLeft(x: 1, width: 16, placed: First, w: 400));
    }

    [Fact]
    public void ATabAtTheRightEdgeIsHeldInsideThePicture()
    {
        Assert.Equal(384, MixLabTimeline.TabLeft(x: 399, width: 16, placed: First, w: 400));
    }

    /// <summary>
    /// The one case where the layout may go off the picture. This is the smaller fault: see
    /// <see cref="MixLabTimeline.TabLeft"/>. A tab half over the edge is still a tab, but two tabs in
    /// one place are not.
    /// </summary>
    [Fact]
    public void ACrowdAtTheRightEdgeRunsOffRatherThanPilingUp()
    {
        float one = MixLabTimeline.TabLeft(x: 399, width: 16, placed: First, w: 400);
        float two = MixLabTimeline.TabLeft(x: 399, width: 16, placed: one + 16, w: 400);

        Assert.True(two > 400 - 16, "pushed past the edge rather than back onto its neighbour");
        Assert.True(two >= one + 16);
    }

    [Fact]
    public void ALaneOfMarksNeverOverlaps()
    {
        // Six marks bunched into forty pixels: whatever comes out, no two tabs may share a pixel.
        float[] at = [10, 12, 13, 30, 31, 44];
        float placed = First;
        var lefts = new List<(float Left, float Width)>();

        foreach (float x in at)
        {
            const float width = 18;
            float left = MixLabTimeline.TabLeft(x, width, placed, w: 400);
            placed = left + width;
            lefts.Add((left, width));
        }

        for (int i = 1; i < lefts.Count; i++)
            Assert.True(
                lefts[i].Left >= lefts[i - 1].Left + lefts[i - 1].Width,
                $"tab {i} at {lefts[i].Left} overlaps tab {i - 1} ending at " +
                $"{lefts[i - 1].Left + lefts[i - 1].Width}");
    }
}
#endif

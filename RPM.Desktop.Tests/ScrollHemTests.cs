using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// How deep a scroller's two hems are, which is the whole of what makes them worth having: a list that
/// fits wears neither, and a list that does not says so at the end it is hiding something behind.
/// </summary>
public class ScrollHemTests
{
    [Fact]
    public void A_list_that_fits_wears_neither_hem()
        => Assert.Equal((0d, 0d), ScrollHem.Depths(offset: 0, scrollable: 0, ScrollHem.Depth));

    [Fact]
    public void The_foot_is_up_and_the_head_is_not_at_the_top_of_a_long_list()
    {
        var (head, foot) = ScrollHem.Depths(offset: 0, scrollable: 400, ScrollHem.Depth);
        Assert.Equal(0, head);
        Assert.Equal(ScrollHem.Depth, foot);
    }

    [Fact]
    public void The_head_is_up_and_the_foot_is_not_at_the_end_of_one()
    {
        var (head, foot) = ScrollHem.Depths(offset: 400, scrollable: 400, ScrollHem.Depth);
        Assert.Equal(ScrollHem.Depth, head);
        Assert.Equal(0, foot);
    }

    [Fact]
    public void Both_are_up_in_the_middle()
    {
        var (head, foot) = ScrollHem.Depths(offset: 200, scrollable: 400, ScrollHem.Depth);
        Assert.Equal(ScrollHem.Depth, head);
        Assert.Equal(ScrollHem.Depth, foot);
    }

    /// <summary>A list with less than one hem's worth of scroll shows a shallower hem, not a full one. The
    /// depth is the measurement, which is why it is not an opacity.</summary>
    [Fact]
    public void A_hem_grows_with_the_little_there_is_to_scroll()
    {
        var (head, foot) = ScrollHem.Depths(offset: 6, scrollable: 10, ScrollHem.Depth);
        Assert.Equal(6, head);
        Assert.Equal(4, foot);
    }

    /// <summary>Uno's scrollers overshoot at both ends while a fling settles, and a negative depth is
    /// what BandSurface exists to keep away from the compositor.</summary>
    [Fact]
    public void Neither_hem_goes_negative_when_a_scroller_overshoots()
    {
        Assert.Equal((0d, ScrollHem.Depth), ScrollHem.Depths(offset: -30, scrollable: 400, ScrollHem.Depth));
        Assert.Equal((ScrollHem.Depth, 0d), ScrollHem.Depths(offset: 430, scrollable: 400, ScrollHem.Depth));
    }
}

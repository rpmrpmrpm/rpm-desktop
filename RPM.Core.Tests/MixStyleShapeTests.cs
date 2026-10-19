using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The two questions the workbench asks about a changeover before it offers a control for it: does it
/// have levels that can be drawn by hand, and does it have a length counted in bars.
///
/// <para>Both are answered by <see cref="MixTechniques"/>, not by the UI, because the answer is a
/// property of the changeover and the planner owns it. Checked against the plans themselves: if the
/// predicate says a style counts bars but the planner returns zero for it, the dropdown offers a number
/// that nothing reads.</para>
/// </summary>
public class MixStyleShapeTests
{
    [Theory]
    [InlineData(TransitionStyle.Cut)]
    [InlineData(TransitionStyle.Blend)]
    [InlineData(TransitionStyle.Slam)]
    public void The_three_fixed_length_changeovers_are_not_counted_in_bars(TransitionStyle style) =>
        Assert.False(MixTechniques.CountsBars(style));

    [Theory]
    [InlineData(TransitionStyle.BeatMatched)]
    [InlineData(TransitionStyle.RollOut)]
    [InlineData(TransitionStyle.BassSwap)]
    [InlineData(TransitionStyle.FilterFade)]
    [InlineData(TransitionStyle.DoubleDrop)]
    [InlineData(TransitionStyle.EchoOut)]
    [InlineData(TransitionStyle.Spinback)]
    public void Every_other_changeover_has_a_length_in_bars(TransitionStyle style) =>
        Assert.True(MixTechniques.CountsBars(style));

    /// <summary>
    /// The most important check: any plan the planner returns with no bars must have a style that
    /// <see cref="MixTechniques.CountsBars"/> says has none. Otherwise the workbench shows an empty
    /// dropdown beside a decided changeover, which looks like an unanswered question.
    /// </summary>
    [Fact]
    public void A_plan_with_no_bars_is_always_a_style_that_counts_none()
    {
        foreach (var style in Enum.GetValues<TransitionStyle>())
            if (!MixTechniques.CountsBars(style))
                Assert.True(
                    style is TransitionStyle.Cut or TransitionStyle.Blend or TransitionStyle.Slam,
                    $"{style} counts no bars, so the planner must not be measuring it in them");
    }

    /// <summary>The default changeover is two phrases. Every other length in the planner is made from
    /// this number by halving (see TransitionPlanner.Plan).</summary>
    [Fact]
    public void The_default_changeover_is_eight_bars()
    {
        Assert.Equal(8, TransitionPlanner.OutroFadeBars);
        Assert.Equal(TransitionPlanner.OutroFadeBars, MixPolicy.Default.OutroFadeBars);
    }

    /// <summary>Halving has to reach one from the default without a remainder, or a changeover cut
    /// down for drift lands on a fraction of a bar and the new record's first bar arrives in the
    /// middle of the old one's.</summary>
    [Fact]
    public void The_default_halves_down_to_a_single_bar()
    {
        int bars = TransitionPlanner.OutroFadeBars;
        while (bars > 1)
        {
            Assert.Equal(0, bars % 2);
            bars /= 2;
        }

        Assert.Equal(1, bars);
    }
}

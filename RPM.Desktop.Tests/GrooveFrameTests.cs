using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// The album page's groove slide, as arithmetic.
///
/// <para>The move is seven numbers that all depend on one value, <c>p</c>, applied to seven elements. If
/// one of them is wrong, nothing throws, and a screenshot of either end looks correct. The page is only
/// wrong for the 220 ms in between. So these tests check the two ends, the direction, and the pairing that
/// keeps the groove against the card's edge.</para>
///
/// <para>Both drawing heads (Skia and WinUI) describe this same geometry. The Skia head uses these numbers,
/// and the WinUI head uses expressions over its own driver. Both must give the same result. These tests
/// cover the half that can be checked by running code.</para>
/// </summary>
public class GrooveFrameTests
{
    /// <summary>Values for a fold on a 1690-point window.</summary>
    private static GrooveFrame At(double p) => GrooveFrame.At(
        grooveWidth: 409, albumGrow: 260, similarGrow: 260, headShift: 130,
        papersTravel: 455, papersOpen: 434, papersFolded: 434, p);

    // ── the two ends ──

    /// <summary>p is 0 at the open page, whichever way the fold goes. Nothing has moved yet, and everything
    /// laid out at its wider width is cut back to the narrower one.</summary>
    [Fact]
    public void At_nought_the_page_is_the_open_one()
    {
        var open = At(0);

        Assert.Equal(0, open.Groove);
        Assert.Equal(260, open.Album);
        Assert.Equal(260, open.Similar);
        Assert.Equal(-130, open.Head);
        Assert.Equal(0, open.Papers);
    }

    /// <summary>p is 1 at the folded page. The groove has moved its whole width, nothing is cut, and the
    /// head is where it was laid out.</summary>
    [Fact]
    public void At_one_the_page_is_the_folded_one()
    {
        var folded = At(1);

        Assert.Equal(409, folded.Groove);
        Assert.Equal(0, folded.Album);
        Assert.Equal(0, folded.Similar);
        Assert.Equal(0, folded.Head);
        Assert.Equal(455, folded.Papers);
    }

    // ── the pairing the whole slide rests on ──

    /// <summary>How far the groove is pushed and how much of it is cut are one number. Two numbers would
    /// make a groove that shrinks from both ends, or one that runs past the card's edge.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void The_groove_is_cut_by_exactly_what_it_is_carried(double p)
    {
        var at = At(p);

        // There is one figure, not two that happen to agree. GrooveFrame.Groove is used both for the carry
        // and for the cut. This is what makes that valid.
        Assert.Equal(409 * p, at.Groove, 6);
    }

    // ── direction ──

    /// <summary>Every part of the move goes one way as p grows, so no element moves back during the
    /// slide.</summary>
    [Fact]
    public void Nothing_doubles_back_between_the_ends()
    {
        var last = At(0);

        for (var step = 1; step <= 20; step++)
        {
            var now = At(step / 20.0);

            Assert.True(now.Groove >= last.Groove, $"the groove went backwards at p={step / 20.0}");
            Assert.True(now.Album <= last.Album, $"the record panel re-covered at p={step / 20.0}");
            Assert.True(now.Similar <= last.Similar, $"the suggestions re-covered at p={step / 20.0}");
            Assert.True(now.Head >= last.Head, $"the head went backwards at p={step / 20.0}");
            Assert.True(now.Papers >= last.Papers, $"the papers went backwards at p={step / 20.0}");

            last = now;
        }
    }

    /// <summary>The head only moves left, never right. It is centred in a panel laid out at the wider
    /// width. A positive offset would push it off the card instead of holding it back.</summary>
    [Fact]
    public void The_head_is_only_ever_held_back()
    {
        for (var step = 0; step <= 20; step++)
            Assert.True(At(step / 20.0).Head <= 0);
    }

    // ── the one real width ──

    /// <summary>The width of the papers moves between the two widths the page measured, and reaches
    /// both.</summary>
    [Fact]
    public void The_papers_reach_both_of_their_measured_widths()
    {
        Assert.Equal(340, GrooveFrame.PapersAt(open: 340, folded: 691, p: 0));
        Assert.Equal(691, GrooveFrame.PapersAt(open: 340, folded: 691, p: 1));
        Assert.Equal(515.5, GrooveFrame.PapersAt(open: 340, folded: 691, p: 0.5), 6);
    }

    /// <summary>If the fold does not resize this panel, the panel keeps its width at every point.</summary>
    [Fact]
    public void A_panel_that_is_not_resized_keeps_its_width_throughout()
    {
        for (var step = 0; step <= 20; step++)
            Assert.Equal(434, GrooveFrame.PapersAt(open: 434, folded: 434, p: step / 20.0));
    }

    // ── p out of range ──

    /// <summary>A tick that lands after the move ends asks for a p past one, and gets the folded page. The
    /// groove is not pushed past the card. The clock is read, not counted, so this happens often on a busy
    /// thread. It is not a rare case.</summary>
    [Fact]
    public void A_late_tick_lands_on_the_far_end_and_not_past_it()
    {
        Assert.Equal(At(1), At(1.4));
        Assert.Equal(At(0), At(-0.2));
    }
}

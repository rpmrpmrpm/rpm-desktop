using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Tests;

/// <summary>
/// The album wall's loader is allowed a share of each frame and no more.
///
/// <para>Yielding between jobs at the lowest dispatcher priority is not enough. The dispatcher drains its
/// low queue several times inside one frame. So three workers that each take a turn can still run nine
/// cover decodes in one 16 millisecond frame. A row that is realized can then take about 47 ms of the
/// drawing thread. The budget limits the work done in each frame, so the yield does what it is meant to
/// do.</para>
/// </summary>
public class FrameBudgetTests
{
    [Fact]
    public void Work_inside_the_share_never_waits()
    {
        var budget = new FrameBudget();
        Assert.Equal(0, budget.Spend(nowMs: 0, costMs: FrameBudget.ShareMs - 1));
    }

    [Fact]
    public void Work_past_the_share_waits_out_the_frame()
    {
        var budget = new FrameBudget();
        var wait = budget.Spend(nowMs: 2, costMs: FrameBudget.ShareMs);
        Assert.Equal(FrameBudget.FrameMs - 2, wait);
    }

    /// <summary>Several small jobs count toward the same cap as one large job. Otherwise nine cheap decodes
    /// could run together in one frame.</summary>
    [Fact]
    public void Small_jobs_accumulate()
    {
        var budget = new FrameBudget();
        var each = FrameBudget.ShareMs / 4;

        Assert.Equal(0, budget.Spend(0, each));
        Assert.Equal(0, budget.Spend(1, each));
        Assert.Equal(0, budget.Spend(2, each));
        Assert.True(budget.Spend(3, each) > 0, "the fourth spends the share and must wait");
    }

    /// <summary>Once a frame has been drawn, there is no reason to hold back work. The window moves to the
    /// next frame on its own, and the loader gets a full share for it.</summary>
    [Fact]
    public void A_new_frame_restores_the_share()
    {
        var budget = new FrameBudget();
        budget.Spend(0, FrameBudget.ShareMs);
        Assert.Equal(0, budget.Spend(FrameBudget.FrameMs, costMs: 1));
    }

    /// <summary>After a wait, the next window opens at the end of the wait, not when the job finished.
    /// Opening it at the job's end would give the loader a new share inside the frame it has just
    /// used.</summary>
    [Fact]
    public void The_next_window_opens_after_the_wait()
    {
        var budget = new FrameBudget();
        var wait = budget.Spend(0, FrameBudget.ShareMs);
        Assert.Equal(FrameBudget.FrameMs, wait);

        // Still inside the frame it waited out: no fresh share yet.
        Assert.True(budget.Spend(wait - 1, FrameBudget.ShareMs) > 0);
    }

    [Fact]
    public void The_share_leaves_most_of_the_frame_alone()
        => Assert.True(FrameBudget.ShareMs < FrameBudget.FrameMs / 2,
                       "a loader allowed half a frame is a loader that can drop every other one");
}

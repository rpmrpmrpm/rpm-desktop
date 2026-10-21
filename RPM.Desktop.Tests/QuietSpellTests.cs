using System.Diagnostics;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// The wait that turns a run of changes into one look at the answer.
///
/// <para>What hangs off it is the push that sends this computer's generated lists to the phone: an
/// upload run raises the library's change for every record it finishes, and a cut is every offered list
/// queried. The rule being tested is that a run of changes costs one cut, taken after the run.</para>
/// </summary>
[Collection(TimingCollection.Name)]
public class QuietSpellTests
{
    /// <summary>
    /// The quiet span these tests are written against.
    ///
    /// <para>The span must be long enough that the scheduler cannot decide the answer. A shorter span of 60
    /// ms failed under the full suite. There, a dozen tests share the cores, and a 20 ms delay often came
    /// back after 60 ms, so the span had already elapsed. The test passed on its own and failed with the
    /// suite. At 300 ms the same overshoot must be five times worse before it changes the answer.</para>
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Nothing_moving_is_nothing_to_do()
    {
        using var spell = new QuietSpell(Quiet);

        var waiting = spell.WaitAsync(CancellationToken.None);

        // A library nobody is touching never wakes this. Waiting for ever is the right answer, and the
        // only thing a test can say about for ever is that it has not happened yet.
        Assert.False(waiting.IsCompleted);
        await Task.Delay(Quiet * 3);
        Assert.False(waiting.IsCompleted);
    }

    [Fact]
    public async Task A_change_is_answered_after_the_quiet_span()
    {
        using var spell = new QuietSpell(Quiet);

        var waiting = spell.WaitAsync(CancellationToken.None);
        spell.Nudge();

        await waiting.WaitAsync(Quiet * 10);
        Assert.True(waiting.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_run_of_changes_is_one_answer_and_it_comes_after_the_run()
    {
        // Tried until one run can be read all the way through. A run the scheduler stalled proves
        // nothing either way and is not a failure; see RunOfChanges. Each attempt doubles the time scale.
        // The rule does not depend on the scale, and a machine busy enough to break a run at one scale
        // keeps to a slower one, so a later attempt can be read through.
        var quiet = Quiet;
        for (var attempt = 0; attempt < 5; attempt++, quiet *= 2)
        {
            if (await RunOfChanges(quiet))
                return;
        }

        Assert.Fail("In every attempt a delay came back later than the quiet span, so no run could "
                  + "say anything about the spell. The machine was too busy for this test.");
    }

    /// <summary>
    /// An upload run: a change, and another, and another, none of them far enough apart to be the end
    /// of anything. The wait must outlast all of them, and end once they stop.
    ///
    /// <para><b>Only what the clock proves is asserted.</b> The wait cannot end while the last change
    /// is younger than the quiet span: the spell restarts its timeout each time it takes a change, and
    /// it always takes one after it was made. So each check reads the wait first and the clock second,
    /// with the clock read before the change, which means the age it computes can only be too old,
    /// never too young. Where that age is inside the span, the wait must still be running, and that is
    /// asserted. Where it is not, a delay came back late: the run was broken by the scheduler, the
    /// spell is right to have ended, and the answer is false, meaning "this run proved nothing".</para>
    ///
    /// <para>An earlier version asserted after every delay. That made the test assert on the scheduler.
    /// Under the full suite, a 100 ms delay sometimes came back after more than 300 ms. The test then
    /// failed about one run in three, although the spell was correct.</para>
    /// </summary>
    private static async Task<bool> RunOfChanges(TimeSpan quiet)
    {
        using var spell = new QuietSpell(quiet);
        var clock = Stopwatch.StartNew();

        var waiting = spell.WaitAsync(CancellationToken.None);
        var readThrough = true;

        for (var i = 0; i < 6; i++)
        {
            var nudgedAt = clock.Elapsed;
            spell.Nudge();

            await Task.Delay(quiet / 3);

            var ended = waiting.IsCompleted;
            if (clock.Elapsed - nudgedAt >= quiet)
            {
                readThrough = false;
                break;
            }

            Assert.False(ended);
        }

        // Whatever the clock did, the changes have stopped, and a run that has stopped must end the
        // wait. Waited for here in both cases, which also means the spell is not disposed under it.
        await waiting.WaitAsync(quiet * 10);
        Assert.True(waiting.IsCompletedSuccessfully);

        return readThrough;
    }

    [Fact]
    public async Task A_change_raised_before_anybody_is_waiting_is_still_answered()
    {
        // The worker is off building the last cut when the next record lands. Nothing may be dropped on
        // the floor because nobody happened to be at the door.
        using var spell = new QuietSpell(Quiet);

        spell.Nudge();
        spell.Nudge();

        await spell.WaitAsync(CancellationToken.None).WaitAsync(Quiet * 10);
    }

    [Fact]
    public async Task A_change_says_so_when_the_span_went_quiet()
    {
        using var spell = new QuietSpell(Quiet);

        var waiting = spell.WaitAsync(CancellationToken.None);
        spell.Nudge();

        Assert.Equal(Woke.Quiet, await waiting.WaitAsync(Quiet * 10));
    }

    [Fact]
    public async Task An_ask_does_not_wait_out_the_quiet_span()
    {
        // The press that generates a new list. The answer is on screen already, so the wait ends on the
        // ask rather than thirty seconds after it.
        using var spell = new QuietSpell(Quiet);

        var waiting = spell.WaitAsync(CancellationToken.None);
        spell.AtOnce();

        Assert.Equal(Woke.AtOnce, await waiting.WaitAsync(Quiet * 10));
    }

    [Fact]
    public async Task An_ask_raised_before_anybody_is_waiting_is_still_an_ask()
    {
        // The press lands while the worker is inside the last push. What it asked for must survive
        // until the worker comes back to the door, reason and all.
        using var spell = new QuietSpell(Quiet);

        spell.AtOnce();

        Assert.Equal(Woke.AtOnce, await spell.WaitAsync(CancellationToken.None).WaitAsync(Quiet * 10));
    }

    [Fact]
    public async Task An_ask_is_answered_once_and_not_by_the_next_wait_as_well()
    {
        using var spell = new QuietSpell(Quiet);

        spell.AtOnce();
        Assert.Equal(Woke.AtOnce, await spell.WaitAsync(CancellationToken.None).WaitAsync(Quiet * 10));

        // The next change is an ordinary one, and must be waited out like one.
        var next = spell.WaitAsync(CancellationToken.None);
        spell.Nudge();

        Assert.Equal(Woke.Quiet, await next.WaitAsync(Quiet * 10));
    }

    [Fact]
    public async Task A_settle_is_sat_out_when_nobody_asks()
    {
        using var spell = new QuietSpell(Quiet);
        var clock = Stopwatch.StartNew();

        var settling = spell.SettleAsync(Quiet * 4, CancellationToken.None);

        // An ordinary change during the settle is not a reason to start early: the work that follows
        // the settle covers it.
        spell.Nudge();
        await Task.Delay(Quiet);

        // Asserted only while the settle's own span is provably not over: the same two reads in the
        // same order as RunOfChanges, and for the same reason. A delay that came back after the whole
        // span leaves nothing to say here, and the answer below must still be Quiet.
        var ended = settling.IsCompleted;
        if (clock.Elapsed < Quiet * 4)
            Assert.False(ended);

        Assert.Equal(Woke.Quiet, await settling.WaitAsync(Quiet * 20));
    }

    [Fact]
    public async Task A_settle_ends_the_moment_somebody_asks()
    {
        using var spell = new QuietSpell(Quiet);

        var settling = spell.SettleAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        spell.AtOnce();

        Assert.Equal(Woke.AtOnce, await settling.WaitAsync(Quiet * 20));
    }

    [Fact]
    public async Task The_settle_ends_when_the_work_is_called_off()
    {
        using var spell = new QuietSpell(Quiet);
        using var stop = new CancellationTokenSource();

        var settling = spell.SettleAsync(TimeSpan.FromMinutes(1), stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settling);
    }

    [Fact]
    public async Task The_wait_ends_when_the_work_is_called_off()
    {
        using var spell = new QuietSpell(Quiet);
        using var stop = new CancellationTokenSource();

        var waiting = spell.WaitAsync(stop.Token);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

using RPM.Core.Threading;

namespace RPM.Core.Tests;

/// <summary>
/// The waits that end by returning. Every one of these would be an exception under the framework's own
/// overloads, and the purpose of the type is that it is not.
/// </summary>
public class CancellableTests
{
    [Fact]
    public async Task Work_that_runs_comes_back_with_its_answer()
    {
        var (ran, value) = await Cancellable.RunAsync(() => 6 * 7, CancellationToken.None);

        Assert.True(ran);
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task A_token_that_has_already_fired_means_the_work_never_starts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var started = false;
        var (ran, value) = await Cancellable.RunAsync(
            () => { started = true; return 1; }, cts.Token);

        Assert.False(started);
        Assert.False(ran);
        Assert.Equal(0, value);
    }

    /// <summary>The reason the type exists: a cancelled run is an answer, not a throw.</summary>
    [Fact]
    public async Task A_cancelled_run_does_not_throw()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var done = await Cancellable.RunAsync(() => "never", cts.Token);

        Assert.False(done.Ran);
        Assert.Null(done.Value);
    }

    /// <summary>Cancellation after the work has begun does not reach into it. The caller asks the token
    /// again on the way out, and that check is the one that matters.</summary>
    [Fact]
    public async Task Work_already_running_is_left_to_finish()
    {
        using var cts = new CancellationTokenSource();
        using var begun = new ManualResetEventSlim();

        var run = Cancellable.RunAsync(
            () =>
            {
                begun.Set();
                Thread.Sleep(30);
                return 9;
            },
            cts.Token);

        begun.Wait(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var (ran, value) = await run;

        Assert.True(ran);
        Assert.Equal(9, value);
    }

    /// <summary>A null answer from work that ran is still an answer, and says so.</summary>
    [Fact]
    public async Task Nothing_found_is_not_the_same_as_nothing_run()
    {
        var done = await Cancellable.RunAsync(() => (string?)null, CancellationToken.None);

        Assert.True(done.Ran);
        Assert.Null(done.Value);
    }

    [Fact]
    public void An_open_gate_is_taken()
    {
        using var gate = new SemaphoreSlim(1, 1);

        Assert.True(Cancellable.Enter(gate, CancellationToken.None));
        Assert.Equal(0, gate.CurrentCount);
    }

    [Fact]
    public void A_held_gate_and_a_cancelled_token_take_nothing()
    {
        using var gate = new SemaphoreSlim(1, 1);
        gate.Wait();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(60));

        Assert.False(Cancellable.Enter(gate, cts.Token));

        // Nothing was taken, so the holder's count is still the holder's.
        Assert.Equal(0, gate.CurrentCount);
        gate.Release();
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task A_delay_that_is_cancelled_reports_it_rather_than_throwing()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        Assert.False(await Cancellable.DelayAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task A_delay_that_elapses_says_so()
        => Assert.True(await Cancellable.DelayAsync(1, CancellationToken.None));
}

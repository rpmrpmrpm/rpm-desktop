using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// The single entry point a service uses to start work that nobody waits for.
///
/// <para>These tests check its main property: work started from a thread that has a synchronization
/// context (every <c>Changed</c> handler, every <c>Loaded</c> and every click runs on one) must not
/// resume on that thread after any of its awaits. A bare <c>_ = SomethingAsync()</c> does resume
/// there, at every await, and on a borrowed shelf that would put the account sync, its JSON, a disk
/// write and a peer catalogue pull on the thread that draws the wall.
/// </para>
/// </summary>
public class BackgroundTests
{
    /// <summary>
    /// A stand-in for the UI thread's: it records anything posted to it, which is what a captured
    /// context does with a continuation.
    /// </summary>
    private sealed class CountingContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posted);

            // Run on the pool, so a failing test fails on the count instead of hanging on work posted
            // to a queue nothing processes. But install THIS context while it runs, because a real
            // dispatcher does that: the callback runs on its thread, where its context is current, so
            // the next await posts back to it as well. Without that line the stand-in would report one
            // post for a chain of three: after the first hop it would look like a bare pool thread and
            // the remaining awaits would capture nothing.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(this);
                d(state);
            });
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posted);
            d(state);
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> with a context installed, as the UI thread has one, and puts the
    /// thread back as it found it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing in here awaits, and that is required.</b> An await on this thread posts its own
    /// continuation to the context being counted, so a harness that awaited would count its own posts
    /// (two, neither from the work). It waits by blocking instead, so the count comes only from the
    /// work that was started.
    /// </remarks>
    private static int UnderAContext(Action body)
    {
        var was = SynchronizationContext.Current;
        var counting = new CountingContext();
        SynchronizationContext.SetSynchronizationContext(counting);
        try
        {
            body();
            return counting.Posted;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(was);
        }
    }

    [Fact]
    public void Work_does_not_come_back_to_the_thread_that_started_it()
    {
        var done = new ManualResetEventSlim(false);
        var onContext = true;
        var pooled = false;

        var posted = UnderAContext(() =>
        {
            Background.Run(async () =>
            {
                // Three awaits, which is what one of these chains looks like: a request, its body,
                // and whatever it starts in turn. Each one could resume on the captured context.
                await Task.Delay(1);
                await Task.Delay(1);
                await Task.Delay(1);

                onContext = SynchronizationContext.Current is not null;
                pooled = Thread.CurrentThread.IsThreadPoolThread;
                done.Set();
            });

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the work never ran");
            Thread.Sleep(50);   // let any continuation that WAS posted arrive before the count is read
        });

        Assert.False(onContext, "the work captured the starting thread's context");
        Assert.True(pooled, "the work did not end up on the thread pool");
        Assert.Equal(0, posted);
    }

    /// <summary>The control case for the test above: the same work, started with a bare discard, does
    /// come back. Three awaits, three posts.</summary>
    [Fact]
    public void The_bare_fire_and_forget_it_replaces_does_come_back()
    {
        var done = new ManualResetEventSlim(false);

        var posted = UnderAContext(() =>
        {
            _ = Resume();

            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the work never ran");
            Thread.Sleep(50);

            async Task Resume()
            {
                await Task.Delay(1);
                await Task.Delay(1);
                await Task.Delay(1);
                done.Set();
            }
        });

        Assert.True(posted >= 3,
            $"the old form posted {posted} continuations back to the starting thread; if that is no "
            + "longer how it behaves, the test above is pinning nothing");
    }

    [Fact]
    public async Task A_failure_is_swallowed_rather_than_left_to_be_raised_later()
    {
        var faults = new List<Exception>();
        void Caught(object? _, UnobservedTaskExceptionEventArgs e) { lock (faults) faults.Add(e.Exception!); }

        TaskScheduler.UnobservedTaskException += Caught;
        try
        {
            var ran = new TaskCompletionSource();
            Background.Run(async () =>
            {
                await Task.Delay(1);
                ran.SetResult();
                throw new InvalidOperationException("the host is shutting down");
            });

            await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);

            lock (faults)
                Assert.DoesNotContain(faults, f => f.ToString().Contains("the host is shutting down"));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Caught;
        }
    }

    [Fact]
    public async Task A_cancelled_piece_of_work_is_not_a_failure()
    {
        var ran = new TaskCompletionSource();

        Background.Run(async () =>
        {
            await Task.Delay(1);
            ran.SetResult();
            throw new OperationCanceledException();
        });

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);   // nothing to assert but that it did not bring the process down
    }

    /// <summary>
    /// A network failure is not logged, because NetworkReach alone reports whether there is a network,
    /// and a log line per failed request is the noise the connection budget and the debugger's own
    /// suppression exist to remove. Any other failure is still logged.
    /// </summary>
    [Fact]
    public async Task A_dead_line_is_not_reported_and_a_real_fault_is()
    {
        Assert.True(RPM.Core.Net.TransientHttp.LooksLikeTheLine(
            new HttpRequestException(HttpRequestError.NameResolutionError, "no dns")));
        Assert.False(RPM.Core.Net.TransientHttp.LooksLikeTheLine(
            new InvalidOperationException("a real fault")));

        // And both go through without bringing anything down.
        var both = new CountdownEvent(2);
        Background.Run(async () =>
        {
            await Task.Delay(1);
            both.Signal();
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "no dns");
        });
        Background.Run(async () =>
        {
            await Task.Delay(1);
            both.Signal();
            throw new InvalidOperationException("a real fault");
        });

        Assert.True(both.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(50);
    }
}

using Microsoft.UI.Dispatching;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// A dispatcher timer that is still there when it is due.
///
/// <para><b>What goes wrong without this.</b> A timer that is created, started, and then left alone stops
/// ticking. The dispatcher queue holds the NATIVE timer. But the Tick handler is carried by the managed
/// peer, and the only thing that refers to that peer is the handler's own closure. That is a cycle, and the
/// garbage collector is free to collect it. On the WinUI head it does. On the Skia head Uno's timers are
/// managed objects that its own dispatcher keeps alive. So the same code always worked there. That is why
/// nine sites in this app were written that way, and none of them looked wrong.</para>
///
/// <para>On the WinUI head, <see cref="Presentation.Shell.RevealGate"/>'s timer never ticked once. The
/// splash stayed up for the whole run. The gate could not reach even its own ceiling. That ceiling exists
/// so that a startup that never goes quiet still opens.</para>
///
/// <para><b>Why this is a type and not a rule.</b> The failure cannot be seen at the call site. The code
/// reads correctly, compiles, and works on the head where most of the development happens. So the holding
/// is not left for each caller to remember. These two methods do it, and they are the only way in.</para>
/// </summary>
internal static class Ticker
{
    /// <summary>Timers that are still going to tick. This is locked, because timers are started and
    /// released on dispatcher threads, and there is more than one dispatcher once a second window is
    /// open.</summary>
    private static readonly HashSet<DispatcherQueueTimer> Due = [];

    /// <summary>
    /// Runs <paramref name="then"/> once, <paramref name="after"/> from now, on the thread of
    /// <paramref name="queue"/>.
    /// </summary>
    /// <param name="queue">Null does nothing. A caller with no dispatcher has no thread to run on, so there
    /// is nothing to wait for.</param>
    public static void Once(DispatcherQueue? queue, TimeSpan after, Action then)
    {
        ArgumentNullException.ThrowIfNull(then);
        if (queue is null)
            return;

        var timer = queue.CreateTimer();
        timer.Interval = after;
        timer.IsRepeating = false;
        timer.Tick += (t, _) =>
        {
            t.Stop();
            // Released before the work, not after. If `then` throws, the timer must not stay held for the
            // life of the process.
            Release(t);
            then();
        };

        Hold(timer);
        timer.Start();
    }

    /// <summary>
    /// Runs <paramref name="each"/> every <paramref name="interval"/> until it returns false.
    /// </summary>
    /// <param name="each">True to keep going, false to stop. Returning false is the only way this timer
    /// ends. A caller that never returns false has asked for a timer that runs for the whole session. That
    /// can be the right choice, but it has to be stated here, not left to a Stop that somebody may not
    /// reach.</param>
    public static void Every(DispatcherQueue? queue, TimeSpan interval, Func<bool> each)
    {
        ArgumentNullException.ThrowIfNull(each);
        if (queue is null)
            return;

        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (t, _) =>
        {
            if (each())
                return;

            t.Stop();
            Release(t);
        };

        Hold(timer);
        timer.Start();
    }

    private static void Hold(DispatcherQueueTimer timer)
    {
        lock (Due)
            Due.Add(timer);
    }

    private static void Release(DispatcherQueueTimer timer)
    {
        lock (Due)
            Due.Remove(timer);
    }
}

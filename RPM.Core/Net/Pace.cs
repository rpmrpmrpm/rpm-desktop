using System.Diagnostics;

namespace RPM.Core.Net;

/// <summary>
/// Whether this process ran its own work on time while a measurement of the line was running.
///
/// <para><b>Why the gate asks.</b> A probe gets no answer for two different reasons. The line is down, or
/// the answer arrived and waited for a thread that this process did not have free. From inside the process
/// the two look the same: the budget runs out and nothing came back. Only the second one can be tested
/// from here, so it is tested. A probe that failed while the process was late on its own work says nothing
/// about the line, and <see cref="NetworkReach"/> does not store it.</para>
///
/// <para>Without this check, a busy app decides that it is offline. Requests time out because the
/// thread pool is full, the gate measures, the probe times out for the same reason, and every request
/// for the next seconds is refused without being sent.</para>
/// </summary>
public interface IPaceWatch
{
    /// <summary>True when the process ran its own work on time from the start of the watch until now.
    /// Asked once, after the probe has failed.</summary>
    bool KeptUp { get; }
}

/// <summary>The watches that <see cref="NetworkReach"/> is built with.</summary>
public static class Pace
{
    /// <summary>
    /// Watches the thread pool. One small work item is queued when the measurement starts. It is put on
    /// the pool's shared queue (not this thread's local queue), because that is where the probe's own
    /// network completions wait. If that item has not run within <see cref="ThreadPoolWatch.Late"/>,
    /// the probe's answer could not have run either.
    /// </summary>
    public static IPaceWatch OfThreadPool() => new ThreadPoolWatch();

    private sealed class ThreadPoolWatch : IPaceWatch
    {
        /// <summary>How long the queued item may wait before the process counts as late. A pool that is
        /// keeping up runs it in well under a millisecond. A pool with no free thread adds one thread about
        /// every half second, so a wait of a whole second means the pool was full. It is well under the
        /// probe's own budget, so a pool that was full for most of the measurement is caught.</summary>
        internal static readonly TimeSpan Late = TimeSpan.FromSeconds(1);

        private readonly long _queued = Stopwatch.GetTimestamp();

        /// <summary>When the item ran, or 0 while it has not run.</summary>
        private long _ran;

        public ThreadPoolWatch() =>
            ThreadPool.UnsafeQueueUserWorkItem(
                static watch => Volatile.Write(ref watch._ran, Stopwatch.GetTimestamp()),
                this,
                preferLocal: false);

        public bool KeptUp
        {
            get
            {
                var ran = Volatile.Read(ref _ran);

                // An item that has not run yet has waited until now. A probe that failed quickly (a refused
                // connection) is asked about before a healthy pool has had time to run the item, and that
                // wait is short, so it still counts as keeping up.
                var waited = ran == 0
                    ? Stopwatch.GetElapsedTime(_queued)
                    : Stopwatch.GetElapsedTime(_queued, ran);
                return waited < Late;
            }
        }
    }
}

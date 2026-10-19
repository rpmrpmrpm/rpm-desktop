using System.Runtime.InteropServices;
using RPM.Core.Diagnostics;
using RPM.Core.Threading;

namespace RpmScope.Collector;

/// <summary>
/// How much CPU time each thread in RPM gets.
///
/// <para>.NET does not give per-thread CPU time on macOS, but mach does: <c>task_threads</c> over
/// another process's task port, and <c>thread_info</c> on each thread. This reading separates two
/// kinds of long stall. Sixteen seconds of wall time with sixteen seconds of CPU is slow work (for
/// example a query with no index). Sixteen seconds of wall time with twelve milliseconds of CPU is a
/// thread that was not running, and making its work faster would not help. Both look the same from
/// outside, and process-wide totals cannot separate them: eight busy workers hide the one blocked
/// thread.</para>
///
/// <para><b>This needs the task port of another process, which needs root or the debugger
/// entitlement.</b> Without it this part reports nothing and logs that once. The rest of the
/// collector is not affected, which is why this is a separate loop and not a step in the other
/// one.</para>
/// </summary>
internal sealed class ThreadCpu(int pid, ScopeRing ring, ScopeNames names)
{
    private const int ThreadBasicInfo = 3;
    private const int ThreadBasicInfoCount = 10;

    [DllImport("libSystem.dylib")]
    private static extern int task_for_pid(uint targetTask, int pid, out uint task);

    [DllImport("libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("libSystem.dylib")]
    private static extern int task_threads(uint task, out IntPtr threads, out uint count);

    [DllImport("libSystem.dylib")]
    private static extern int thread_info(uint thread, int flavor, int[] info, ref uint count);

    [DllImport("libSystem.dylib")]
    private static extern int vm_deallocate(uint task, IntPtr address, uint size);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        if (task_for_pid(mach_task_self(), pid, out var task) != 0)
        {
            Console.Error.WriteLine(
                "[collector] no task port for that process, so per-thread processor time is off. " +
                "Run the collector with sudo to have it.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            Sample(task);
            if (!await Cancellable.DelayAsync(100, ct).ConfigureAwait(false))
                return;
        }
    }

    private void Sample(uint task)
    {
        if (task_threads(task, out var list, out var count) != 0)
            return;

        try
        {
            var threadName = names.Id("thread");

            for (var i = 0; i < count; i++)
            {
                var thread = (uint)Marshal.ReadInt32(list, i * sizeof(int));
                var info = new int[ThreadBasicInfoCount];
                var size = (uint)ThreadBasicInfoCount;

                if (thread_info(thread, ThreadBasicInfo, info, ref size) != 0)
                    continue;

                // thread_basic_info starts with user_time then system_time, each a time_value_t of
                // {seconds, microseconds}. Both are counted, because a thread in the kernel is still
                // running.
                var micros =
                    (long)info[0] * 1_000_000 + info[1] +
                    (long)info[2] * 1_000_000 + info[3];

                ring.Write(new ScopeRecord
                {
                    Nanos = ScopeClock.Nanos(),
                    Kind = (ushort)ScopeKind.ThreadCpu,
                    Thread = (ushort)(thread & 0xFFFF),
                    NameId = threadName,

                    // A total, not a delta. The reader subtracts two samples, so a missed sample does
                    // not give a wrong value.
                    A = micros * 1000,
                });
            }
        }
        finally
        {
            vm_deallocate(mach_task_self(), list, count * sizeof(int));
        }
    }
}

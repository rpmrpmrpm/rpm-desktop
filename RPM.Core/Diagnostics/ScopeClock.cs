using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RPM.Core.Diagnostics;

/// <summary>
/// The one clock both halves of the instrument read.
///
/// <para>Two processes have to agree about when a thing happened. The app writes what only it knows,
/// and the collector outside it writes what only the runtime knows. They must agree without
/// negotiating, because the moment they most need to agree is the moment the app is not answering.
/// On macOS <c>CLOCK_UPTIME_RAW</c> is <c>mach_absolute_time</c> in nanoseconds: system-wide,
/// monotonic, about twenty nanoseconds to read, and the same value in both processes because it
/// belongs to neither.</para>
///
/// <para>Elsewhere it is <see cref="Stopwatch"/> scaled. That is per process, so it is good enough only
/// for the tests: no collector runs on those platforms.</para>
/// </summary>
public static class ScopeClock
{
    private const int ClockUptimeRaw = 8;
    private const int ClockRealtime = 0;
    private const int DarwinThreadCpuTime = 16;

    /// <summary>Linux's <c>CLOCK_THREAD_CPUTIME_ID</c>. The number differs from Darwin's, so each
    /// platform has its own constant.</summary>
    private const int LinuxThreadCpuTime = 3;

    [DllImport("libSystem.dylib", EntryPoint = "clock_gettime_nsec_np")]
    private static extern ulong ClockGetTimeNsec(int clockId);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out TimeSpec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public nint Seconds;
        public nint Nanos;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(
        nint thread, out long created, out long exited, out long kernel, out long user);

    private static readonly bool Darwin =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst();

    private static readonly double TicksToNanos = 1_000_000_000.0 / Stopwatch.Frequency;

    /// <summary>Monotonic nanoseconds. The value written into every record.</summary>
    public static long Nanos() =>
        Darwin ? (long)ClockGetTimeNsec(ClockUptimeRaw) : (long)(Stopwatch.GetTimestamp() * TicksToNanos);

    /// <summary>
    /// Whether this platform will say how much processor a thread has had at all.
    ///
    /// <para>This is asked, not inferred from the reading. Zero is a real answer: the thread did not
    /// run. "This platform does not keep that number" must be told apart from zero, or a busy thread
    /// on Windows reads as parked. See <see cref="ThreadCpuNanos"/>.
    /// </para>
    /// </summary>
    public static bool SaysThreadCpu =>
        Darwin || OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>
    /// How much processor THIS thread has had, in nanoseconds. Negative where the platform will not
    /// say, which is not the same as zero and must never be printed as if it were.
    ///
    /// <para>This number splits a long stall in two, and the two halves need opposite fixes. A stall
    /// of 800 ms with 780 ms of processor time is work: make it cheaper, or move it off this thread. A
    /// stall of 800 ms with 4 ms of processor time is a thread that was not running. It was parked on a
    /// native lock, waiting for the disk, suspended for a collection or stopped by the debugger. Making
    /// its work faster would do nothing. Both look the same from outside, so wall time alone cannot say
    /// which one a stall was.</para>
    ///
    /// <para>Per thread, not per process: a process-wide total is swamped by busy workers, and the
    /// thread being measured is lost in it.</para>
    ///
    /// <para><b>Windows keeps this on the scheduler's tick</b>, which is about fifteen milliseconds, so
    /// the reading moves in steps rather than smoothly. A hold is only written down past 100 ms, and the
    /// question is whether the thread did nothing or most of the work. The step size is accurate enough
    /// for that. It is not accurate enough to compare two holds that differ by a few milliseconds.</para>
    /// </summary>
    public static long ThreadCpuNanos()
    {
        try
        {
            if (Darwin)
                return (long)ClockGetTimeNsec(DarwinThreadCpuTime);

            if (OperatingSystem.IsWindows())
            {
                // The pseudo-handle for the current thread: a constant, not something to close.
                if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
                    return Unknown;

                // Both halves count. A thread inside sqlite3_step is often in a read(2) as much as in
                // computation, and counting only the user half would report disk time as idle.
                return (kernel + user) * 100;   // FILETIME units are 100ns
            }

            if (OperatingSystem.IsLinux())
                return ClockGetTime(LinuxThreadCpuTime, out var t) == 0
                    ? (long)t.Seconds * 1_000_000_000L + t.Nanos
                    : Unknown;
        }
        catch (DllNotFoundException) { /* fall through to unknown */ }
        catch (EntryPointNotFoundException) { /* likewise */ }

        return Unknown;
    }

    /// <summary>What <see cref="ThreadCpuNanos"/> answers when the platform will not say. Negative, so
    /// that arithmetic on it comes out visibly wrong rather than quietly plausible.</summary>
    public const long Unknown = -1;

    /// <summary>
    /// Wall clock nanoseconds, written into a ring's header once so a viewer can label an axis.
    ///
    /// <para>Never written into a record. It is not monotonic: it steps when the machine is disciplined
    /// to a time server, and a trace ordered by it would put things in an order they did not happen in.</para>
    /// </summary>
    public static long UnixNanos() =>
        Darwin
            ? (long)ClockGetTimeNsec(ClockRealtime)
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
}

namespace RPM.Core.Crashes;

/// <summary>What a crashed run wrote about itself: which run it was, which build, and every unhandled
/// exception it had before it ended. <see cref="CrashRecorder"/> writes it, and the next launch's <see
/// cref="CrashHarvest"/> reads it.
/// </summary>
/// <param name="Pid">The process that crashed.</param>
/// <param name="RingStartUnixNanos">When the process's diagnostic ring started, or zero when it had no
/// ring. Together with <paramref name="Pid"/> this names the run, and the harvest uses it to find the
/// run's ring.</param>
/// <param name="ProcessStartUnixMs">When the process started. This names the run when there was no
/// ring.</param>
/// <param name="AppVersion">The version of the build that crashed. The next launch may be a newer
/// build.</param>
/// <param name="Head">"mac" or "windows".</param>
/// <param name="Failures">In the order they happened. The first one ended the run.</param>
public sealed record CrashFile(
    int Pid,
    long RingStartUnixNanos,
    long ProcessStartUnixMs,
    string AppVersion,
    string Head,
    IReadOnlyList<CrashFailure> Failures);

/// <param name="Where">Which handler saw it: "application", "appdomain", "start".</param>
/// <param name="Exception">Exception.ToString(), with inner exceptions and the stack.</param>
public sealed record CrashFailure(long AtUnixMs, string Where, string Exception);

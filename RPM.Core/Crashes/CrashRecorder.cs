using System.Diagnostics;
using System.Text.Json;
using RPM.Core.Diagnostics;

namespace RPM.Core.Crashes;

/// <summary>
/// Writes an unhandled exception to <c>&lt;pid&gt;.crash.json</c> and does nothing else.
///
/// <para>Called from the unhandled-exception handlers, in a process that is about to end. It reads no
/// ring, opens no connection and shows nothing. The next launch does all of that
/// (<see cref="CrashHarvest"/>). The file is written to a temporary name and then renamed, so a crash
/// file that exists is complete.</para>
///
/// <para>A second failure in the same run is appended to the same file. The lock is taken with a
/// two-second limit. A handler that cannot get the lock gives up, rather than keeping the process
/// alive.</para>
///
/// <para>The file is named after the process id and the process start time. A process id can be
/// reused, so a later run with the same id writes its own file instead of adding to an earlier run's
/// file. The folder is worked out when a crash is written, inside the guard. The recorder is made on
/// the first line of the entry point, and nothing there may stop the app from starting.</para>
/// </summary>
public sealed class CrashRecorder(Func<string> directory, int pid, long processStartUnixMs, string appVersion, string head)
{
    private static readonly TimeSpan LockLimit = TimeSpan.FromSeconds(2);

    internal object Gate { get; } = new();

    /// <summary>A recorder for this process. It reads the process start time once, at startup, so the
    /// handler has nothing to look up.</summary>
    public static CrashRecorder ForThisProcess(Func<string> directory, string appVersion, string head)
    {
        long start;
        try
        {
            using var self = Process.GetCurrentProcess();
            start = new DateTimeOffset(self.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return new CrashRecorder(directory, Environment.ProcessId, start, appVersion, head);
    }

    /// <summary>Never throws.</summary>
    public void Record(Exception exception, string where)
    {
        if (!Monitor.TryEnter(Gate, LockLimit))
            return;
        try
        {
            Write(exception, where, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch
        {
            // Nothing can be done about a crash file that will not write, in a process that is ending.
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    internal static string PathFor(string directory, int pid, long processStartUnixMs) =>
        Path.Combine(directory, $"{pid}-{processStartUnixMs}.crash.json");

    /// <summary>The crash file, or null when it is missing or is not a crash file. A file without a
    /// process id or without a failure is not a crash file. The recorder never writes either one, and the
    /// generated reader fills missing members with defaults rather than refusing them.
    ///
    /// <para>A missing file is checked for before it is read. The first crash of every run has no file
    /// yet. Catching a <see cref="FileNotFoundException"/> here would add a record to the diagnostic ring,
    /// and that record would then appear in the crash report.</para></summary>
    internal static CrashFile? TryRead(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CrashJson.Default.CrashFile)
                is { Pid: > 0, Failures.Count: > 0 } file ? file : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Write(Exception exception, string where, long atUnixMs)
    {
        var folder = directory();
        Directory.CreateDirectory(folder);
        var path = PathFor(folder, pid, processStartUnixMs);
        var failure = new CrashFailure(atUnixMs, where, exception.ToString());

        // The ring start is read now, not when the recorder is built. The recorder is made before the
        // ring is opened on the Mac head.
        var file = TryRead(path) is { } existing
            ? existing with { Failures = [.. existing.Failures, failure] }
            : new CrashFile(pid, Scope.StartedUnixNanos, processStartUnixMs, appVersion, head, [failure]);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, CrashJson.Default.CrashFile));
        File.Move(temporary, path, overwrite: true);
    }
}

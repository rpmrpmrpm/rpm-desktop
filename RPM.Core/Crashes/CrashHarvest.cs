using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPM.Core.Diagnostics;

namespace RPM.Core.Crashes;

/// <summary>Runs once per launch. It finds the runs that crashed since the last pass and writes one
/// pending report for each.
///
/// <para>A run is a process that has finished. It is found by its ring (<c>scope.&lt;pid&gt;.ring</c> in
/// <paramref name="scopeDirectory"/>), by its crash file (<c>&lt;pid&gt;.crash.json</c> in
/// <paramref name="crashDirectory"/>), or by both. A run crashed when there is a crash file, or when the
/// operating system wrote a report for it. One system report is not counted: a Windows fail-fast after
/// the ring's Closing record. That is the known crash on close. A run with neither is left for the next
/// launch, because the system's report can arrive after the user has started the app again. A run that
/// closed is settled once its report window has passed.</para>
///
/// <para>The crash id is worked out from the run: the process id and the ring start, or the process
/// start when there was no ring. Two copies of the app that harvest the same run write one pending file.
/// The file is created with <see cref="FileMode.CreateNew"/>, and the server drops a second send.</para>
///
/// <para>An I/O failure on one run leaves that run's files in place and moves on. A failure is never
/// read as "no crash".</para>
/// </summary>
public sealed class CrashHarvest(
    string crashDirectory,
    string scopeDirectory,
    int ownPid,
    IReadOnlySet<int> livePids,
    ISystemCrashReports system,
    CrashEnvironment environment)
{
    public const int MaxPending = 10;
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromDays(30);

    public const int MaxExceptionChars = 64_000;
    public const int MaxSystemReportChars = 1_000_000;
    public const int MaxRecentChars = 64_000;

    /// <summary>How long after a run's last record its system report may be dated. The report is written
    /// after the process has died. The ring's last record can be up to a second before that.</summary>
    public static readonly TimeSpan ReportLag = TimeSpan.FromMinutes(2);

    public static string PendingDirectoryIn(string crashDirectory) => Path.Combine(crashDirectory, "pending");

    private string PendingDirectory => PendingDirectoryIn(crashDirectory);
    private string HandledDirectory => Path.Combine(crashDirectory, "handled");

    public static string CrashIdFor(string runName) =>
        "crash_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runName)))[..24];

    private sealed record FinishedRun(int Pid, string Name, CrashFile? File, string? CrashFilePath, RingTail? Tail);

    /// <returns>The crash ids this pass wrote.</returns>
    public IReadOnlyList<string> Run(DateTimeOffset now)
    {
        var written = new List<string>();
        var runs = FindRuns();

        foreach (var run in runs)
        {
            try
            {
                if (Package(run, now) is { } id)
                    written.Add(id);
            }
            catch (Exception e)
            {
                // Any failure, not only I/O: one run that cannot be packaged must not stop the others.
                // A failure is not "no crash", so it is logged to the diagnostic ring.
                Scope.Said("crash", $"run {run.Name} left for the next launch: {e.GetType().Name}: {e.Message}");
            }
        }

        Tidy(runs, now);
        return written;
    }

    private bool Finished(int pid) => pid > 0 && pid != ownPid && !livePids.Contains(pid);

    private List<FinishedRun> FindRuns()
    {
        // A list, not a map keyed by pid: two runs with the same process id and no ring have two files.
        var files = new List<(CrashFile File, string Path)>();
        if (Directory.Exists(crashDirectory))
        {
            foreach (var temporary in Directory.GetFiles(crashDirectory, "*.crash.json.tmp"))
                if (Finished(PidOf(temporary)))
                    TryDelete(temporary);

            foreach (var path in Directory.GetFiles(crashDirectory, "*.crash.json"))
            {
                if (!Finished(PidOf(path)))
                    continue;
                if (CrashRecorder.TryRead(path) is { } file)
                    files.Add((file, path));
                else
                {
                    Scope.Said("crash", $"unreadable crash file removed: {Path.GetFileName(path)}");
                    TryDelete(path);
                }
            }
        }

        var runs = new List<FinishedRun>();
        if (Directory.Exists(scopeDirectory))
        {
            foreach (var ring in Directory.GetFiles(scopeDirectory, "scope.*.ring"))
            {
                var pid = PidOf(ring);
                if (!Finished(pid))
                    continue;

                RingTail tail;
                try { tail = RingTail.Read(ring, Path.ChangeExtension(ring, ".names")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    // ArgumentException: a ring of zero bytes, from a run that died while making it.
                    Scope.Said("crash", $"ring {Path.GetFileName(ring)} not read: {e.GetType().Name}");
                    continue;
                }

                var index = files.FindIndex(f => f.File.Pid == pid && f.File.RingStartUnixNanos == tail.StartUnixNanos);
                var match = index >= 0 ? files[index] : default;
                if (index >= 0)
                    files.RemoveAt(index);
                runs.Add(new FinishedRun(pid, $"{pid}-r{tail.StartUnixNanos}", match.File, match.Path, tail));
            }
        }

        // Crash files whose ring is gone, or whose run had none.
        foreach (var (file, path) in files)
        {
            var name = file.RingStartUnixNanos != 0 ? $"{file.Pid}-r{file.RingStartUnixNanos}" : $"{file.Pid}-p{file.ProcessStartUnixMs}";
            runs.Add(new FinishedRun(file.Pid, name, file, path, null));
        }

        return runs;
    }

    /// <returns>The crash id written, or null when the run is not packaged this pass.</returns>
    private string? Package(FinishedRun run, DateTimeOffset now)
    {
        var marker = Path.Combine(HandledDirectory, run.Name);
        if (File.Exists(marker))
        {
            if (run.CrashFilePath is not null)
                TryDelete(run.CrashFilePath);
            return null;
        }

        var closing = run.Tail?.Closing ?? false;
        var (from, to) = Window(run);
        var report = system.Find(run.Pid, from, to);

        if (run.File is null && report is null)
        {
            // No evidence yet, and the system's report can arrive after this launch. A run that closed
            // is settled once no report can match its window. A run that did not close (a force quit, a
            // power cut) is never settled here.
            if (closing && now > to)
                MarkHandled(marker);
            return null;
        }

        if (run.File is null && closing && report is { IsWindowsFailFast: true })
        {
            // The known crash on close on Windows: abort() in ucrtbase, after the app has stopped. It is
            // settled, so it is not filed on every close. Any other crash after Closing is reported.
            MarkHandled(marker);
            return null;
        }

        var id = CrashIdFor(run.Name);
        var pending = new PendingCrash(
            id,
            run.File?.Head ?? environment.Head,
            run.File?.AppVersion ?? environment.AppVersion,
            environment.Platform,
            environment.Architecture,
            CrashedAt(run),
            Cut(run.File is null ? null : SystemReportPrivacy.WithoutHomeFolders(string.Join("\n\n", run.File.Failures.Select(f => $"[{f.Where}] {f.Exception}"))), MaxExceptionChars),
            report is null ? null
                : report.Kind == SystemCrashReport.Ips ? IpsTrim.Fit(SystemReportPrivacy.Clean(report), MaxSystemReportChars)
                : Cut(SystemReportPrivacy.Clean(report), MaxSystemReportChars),
            report?.Kind,
            Cut(run.Tail is null ? null : SystemReportPrivacy.WithoutHomeFolders(string.Join('\n', run.Tail.Recent)), MaxRecentChars));

        Directory.CreateDirectory(PendingDirectory);
        var wrote = false;
        try
        {
            using var stream = new FileStream(Path.Combine(PendingDirectory, $"{id}.json"), FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, pending, CrashJson.Default.PendingCrash);
            wrote = true;
        }
        catch (IOException) when (File.Exists(Path.Combine(PendingDirectory, $"{id}.json")))
        {
            // Another copy of the app packaged this run first.
        }

        MarkHandled(marker);
        if (run.CrashFilePath is not null)
            TryDelete(run.CrashFilePath);
        return wrote ? id : null;
    }

    private static (DateTimeOffset From, DateTimeOffset To) Window(FinishedRun run)
    {
        var start = run.Tail is { } tail
            ? DateTimeOffset.FromUnixTimeMilliseconds(tail.StartUnixNanos / 1_000_000)
            : DateTimeOffset.FromUnixTimeMilliseconds(run.File!.ProcessStartUnixMs);
        var end = run.Tail is { } last
            ? DateTimeOffset.FromUnixTimeMilliseconds(last.LastUnixNanos / 1_000_000)
            : DateTimeOffset.FromUnixTimeMilliseconds(run.File!.Failures[^1].AtUnixMs);
        return (start - TimeSpan.FromSeconds(5), end + ReportLag);
    }

    private static long CrashedAt(FinishedRun run) =>
        run.File is { Failures: [var first, ..] }
            ? DateTimeOffset.FromUnixTimeMilliseconds(first.AtUnixMs).UtcTicks
            : DateTimeOffset.FromUnixTimeMilliseconds(run.Tail!.LastUnixNanos / 1_000_000).UtcTicks;

    private void MarkHandled(string marker)
    {
        Directory.CreateDirectory(HandledDirectory);
        File.WriteAllText(marker, "");
    }

    /// <summary>Removes markers for runs that are no longer on disk. Removes pending reports that are past
    /// the count or the age limit.</summary>
    private void Tidy(List<FinishedRun> runs, DateTimeOffset now)
    {
        try
        {
            if (Directory.Exists(HandledDirectory))
            {
                var current = runs.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var marker in Directory.GetFiles(HandledDirectory))
                    if (!current.Contains(Path.GetFileName(marker)))
                        TryDelete(marker);
            }

            if (Directory.Exists(PendingDirectory))
            {
                var pending = Directory.GetFiles(PendingDirectory, "*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
                foreach (var (path, index) in pending.Select((p, i) => (p, i)))
                    if (index >= MaxPending || now.UtcDateTime - File.GetLastWriteTimeUtc(path) > MaxPendingAge)
                        TryDelete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Housekeeping. The next launch tries again.
        }
    }

    /// <summary>Reads the process id from a file name such as "4242-1759262400000.crash.json", from its
    /// ".tmp" copy, or from "scope.4242.ring". Returns zero when there is no id.</summary>
    private static int PidOf(string path)
    {
        var name = Path.GetFileName(path);
        var part = name.StartsWith("scope.", StringComparison.Ordinal) ? name.Split('.')[1] : name.Split('.')[0].Split('-')[0];
        return int.TryParse(part, out var pid) ? pid : 0;
    }

    private static string? Cut(string? text, int max) =>
        text is null ? null : text.Length > max ? text[..max] : text;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

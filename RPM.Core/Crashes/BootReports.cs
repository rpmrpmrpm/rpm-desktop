using System.Globalization;
using RPM.Core.Cloud;

namespace RPM.Core.Crashes;

/// <summary>
/// Reports for runs that did not show the main window.
///
/// <para>A crash report needs a crash. A start-up that stops without throwing leaves no crash file, and
/// the person then quits an app that shows nothing. The question about crash reports is asked in the
/// main window, so it is not asked either. These reports cover that case.</para>
///
/// <para>Each run writes a marker, <c>&lt;pid&gt;-&lt;process start&gt;</c> in <c>directory</c>, when
/// it starts. It deletes the marker when its window is shown. So a marker states a fact whenever it is
/// read: that run has not shown its window. When its process has ended, it never did. The marker of an
/// ended run is deleted only after the server has taken its report, so a report that could not be sent
/// is tried again at the next launch, until <see cref="MaxAge"/>.</para>
///
/// <para>A report is a <see cref="CrashReport"/> whose first block is <c>[boot] WindowNotShown: ...</c>
/// with the last start-up step. The server groups these by that type. The blocks after it are the
/// run's unhandled exceptions, from its crash file and from <see cref="StartupCrashLog"/>. Home folder
/// paths are removed from every text, as in a crash report.</para>
/// </summary>
public sealed class BootReports(
    string directory,
    string scopeDirectory,
    string crashDirectory,
    string startupCrashLogFolder,
    ISystemCrashReports system,
    CrashEnvironment environment)
{
    /// <summary>An ended run older than this is not reported. Its marker is deleted.</summary>
    public static readonly TimeSpan MaxAge = CrashHarvest.MaxPendingAge;

    /// <summary>The id of the installation when the settings have none yet. Kept in the marker
    /// folder, so the count of machines stays right for these reports.</summary>
    private const string InstallIdFile = "install-id";

    /// <summary>One run of the app, named as its crash file names it.</summary>
    public sealed record Run(int Pid, long ProcessStartUnixMs)
    {
        internal string Name => string.Create(CultureInfo.InvariantCulture, $"{Pid}-{ProcessStartUnixMs}");

        internal DateTimeOffset Started => DateTimeOffset.FromUnixTimeMilliseconds(ProcessStartUnixMs);
    }

    /// <summary>Writes the marker of <paramref name="run"/>. Throws what the file system throws.</summary>
    public void Begin(Run run)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(MarkerOf(run), "");
    }

    /// <summary>Deletes the marker of a run that has shown its window. Throws what the file system
    /// throws.</summary>
    public void Shown(Run run) => File.Delete(MarkerOf(run));

    /// <summary>Whether <paramref name="run"/> has shown its window: its marker is gone.</summary>
    public bool IsShown(Run run) => !File.Exists(MarkerOf(run));

    /// <summary>The runs whose process has ended without showing the window. A process that is still
    /// running is not one of them, even when it is another copy of the app.</summary>
    public IReadOnlyList<Run> Ended(int ownPid, IReadOnlySet<int> live)
    {
        if (!Directory.Exists(directory))
            return [];

        var runs = new List<Run>();
        foreach (var path in Directory.GetFiles(directory))
            if (Parse(Path.GetFileName(path)) is { } run && run.Pid != ownPid && !live.Contains(run.Pid))
                runs.Add(run);
        return runs;
    }

    /// <summary>Deletes the marker of an ended run: its report has been taken, or it is too old to send.
    /// Throws what the file system throws.</summary>
    public void Settled(Run run) => File.Delete(MarkerOf(run));

    /// <summary>The id that the reports carry: <paramref name="known"/> when the settings have one,
    /// otherwise one kept in the marker folder. Throws what the file system throws.</summary>
    public string InstallId(string? known)
    {
        if (known is { Length: > 0 })
            return known;

        var path = Path.Combine(directory, InstallIdFile);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } kept)
            return kept;

        var made = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, made);
        return made;
    }

    /// <summary>The report for a run that is still running and has not shown its window.</summary>
    public CrashReport ForStarting(Run run, string installId, DateTimeOffset now)
    {
        var tail = Tail(run);
        var headline = string.Create(CultureInfo.InvariantCulture,
            $"[boot] WindowNotShown: the window was not shown {(now - run.Started).TotalSeconds:F0} s after launch. Last step: {LastStep(tail)}");
        return Report(run, "starting", installId, now, headline, tail, null);
    }

    /// <summary>The report for a run that ended without showing its window. It carries the system's
    /// crash report when there is one.</summary>
    public CrashReport ForEnded(Run run, string installId)
    {
        var tail = Tail(run);
        var end = tail is null ? run.Started : DateTimeOffset.FromUnixTimeMilliseconds(tail.LastUnixNanos / 1_000_000);
        var how = tail is null ? "it left no log"
            : tail.Closing ? "it was quit"
            : "it stopped without closing";
        var headline = string.Create(CultureInfo.InvariantCulture,
            $"[boot] WindowNotShown: the run ended before the window was shown ({how}, {(end - run.Started).TotalSeconds:F0} s after launch). Last step: {LastStep(tail)}");
        var report = system.Find(run.Pid, run.Started - TimeSpan.FromSeconds(5), end + CrashHarvest.ReportLag);
        return Report(run, "ended", installId, end, headline, tail, report);
    }

    private CrashReport Report(
        Run run, string phase, string installId, DateTimeOffset at, string headline, RingTail? tail,
        SystemCrashReport? report)
    {
        var to = tail is null ? at : DateTimeOffset.FromUnixTimeMilliseconds(tail.LastUnixNanos / 1_000_000);
        var blocks = new List<string> { headline };
        blocks.AddRange(Failures(run));
        blocks.AddRange(Recovered(run.Started, to));

        var exception = Cut(SystemReportPrivacy.WithoutHomeFolders(string.Join("\n\n", blocks)), CrashHarvest.MaxExceptionChars);
        var recent = tail is null ? null
            : Last(SystemReportPrivacy.WithoutHomeFolders(string.Join('\n', tail.Recent)), CrashHarvest.MaxRecentChars);
        var systemText = report is null ? null
            : report.Kind == SystemCrashReport.Ips ? IpsTrim.Fit(SystemReportPrivacy.Clean(report), CrashHarvest.MaxSystemReportChars)
            : Cut(SystemReportPrivacy.Clean(report), CrashHarvest.MaxSystemReportChars);

        // Full, so the server keeps the log: it is what says where the start-up stopped. No account goes
        // with it, because the caller sends no token.
        return ReportScope.Full.Crash(
            CrashHarvest.CrashIdFor($"boot-{phase}-{run.Name}"),
            environment.Head, environment.AppVersion, environment.Platform, environment.Architecture,
            at.UtcTicks, installId, exception, systemText, report?.Kind, recent);
    }

    /// <summary>The run's ring, or null when there is none. A ring that started before the process did
    /// belongs to an earlier process with the same id.</summary>
    private RingTail? Tail(Run run)
    {
        var ring = Path.Combine(scopeDirectory, string.Create(CultureInfo.InvariantCulture, $"scope.{run.Pid}.ring"));
        try
        {
            var tail = RingTail.Read(ring, Path.ChangeExtension(ring, ".names"));
            var started = DateTimeOffset.FromUnixTimeMilliseconds(tail.StartUnixNanos / 1_000_000);
            return started < run.Started - TimeSpan.FromSeconds(5) ? null : tail;
        }
        catch (Exception e) when (
            e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The detail of the last <c>boot</c> line in the ring, or "none".</summary>
    internal static string LastStep(RingTail? tail)
    {
        const string boot = " said boot: ";
        for (var i = (tail?.Recent.Count ?? 0) - 1; i >= 0; i--)
        {
            var line = tail!.Recent[i];
            var at = line.IndexOf(boot, StringComparison.Ordinal);
            if (at >= 0)
                return line[(at + boot.Length)..];
        }
        return "none";
    }

    /// <summary>The failures in the run's crash file, as the harvest writes them.</summary>
    private IEnumerable<string> Failures(Run run)
    {
        var path = CrashRecorder.PathFor(crashDirectory, run.Pid, run.ProcessStartUnixMs);
        CrashFile? file;
        try
        {
            if (!File.Exists(path))
                return [];
            file = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path), CrashJson.Default.CrashFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
        return file?.Failures.Select(f => $"[{f.Where}] {f.Exception}") ?? [];
    }

    /// <summary>The entries of <see cref="StartupCrashLog"/> written while the run was alive.</summary>
    private IEnumerable<string> Recovered(DateTimeOffset from, DateTimeOffset to)
    {
        try
        {
            var path = Path.Combine(startupCrashLogFolder, StartupCrashLog.FileName);
            return File.Exists(path) ? StartupCrashLog.Between(File.ReadAllText(path), from, to) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string MarkerOf(Run run) => Path.Combine(directory, run.Name);

    internal static Run? Parse(string name)
    {
        var parts = name.Split('-');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            ? new Run(pid, start)
            : null;
    }

    private static string? Cut(string? text, int max) =>
        text is null ? null : text.Length > max ? text[..max] : text;

    /// <summary>Keeps the end of the text, because the lines nearest the problem are the last ones.</summary>
    private static string Last(string text, int max) => text.Length > max ? text[^max..] : text;
}

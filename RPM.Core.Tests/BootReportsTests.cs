using RPM.Core.Crashes;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>Runs that did not show the main window: the markers that say so, and the reports built for
/// them.</summary>
public class BootReportsTests : IDisposable
{
    private const int Pid = 4242;

    private readonly string _root = Directory.CreateTempSubdirectory("boot-reports-").FullName;
    private readonly FakeSystemReports _system = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);

    private string Markers => Path.Combine(_root, "boot");
    private string ScopeDir => Path.Combine(_root, "scope");
    private string CrashDir => Path.Combine(_root, "crashes");
    private BootReports.Run Run => new(Pid, _started.ToUnixTimeMilliseconds());

    public BootReportsTests()
    {
        Directory.CreateDirectory(ScopeDir);
        Directory.CreateDirectory(CrashDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private BootReports Reports() => new(
        Markers, ScopeDir, CrashDir, _root, _system, new CrashEnvironment("0.7.5", "mac", "macOS 12.7", "x64"));

    private static readonly HashSet<int> NoneLive = [];

    /// <summary>Writes the run's ring: a clock sync at its start, then boot lines one second apart.</summary>
    private void Ring(bool closing, params string[] steps)
    {
        var ring = Path.Combine(ScopeDir, $"scope.{Pid}.ring");
        using var names = ScopeNames.Fresh(Path.ChangeExtension(ring, ".names"));
        using var writer = ScopeRing.Create(ring, 4096);
        var startNanos = _started.ToUnixTimeMilliseconds() * 1_000_000;
        writer.Write(new ScopeRecord { Kind = (ushort)ScopeKind.ClockSync, Nanos = 0, A = startNanos });
        for (var i = 0; i < steps.Length; i++)
            writer.Write(new ScopeRecord
            {
                Kind = (ushort)ScopeKind.Said, Nanos = (i + 1) * 1_000_000_000L,
                NameId = names.Id("boot"), Detail = names.Id(steps[i]),
            });
        if (closing)
            writer.Write(new ScopeRecord { Kind = (ushort)ScopeKind.Closing, Nanos = (steps.Length + 1) * 1_000_000_000L });
    }

    [Fact]
    public void A_run_that_has_begun_and_ended_is_found()
    {
        var reports = Reports();
        reports.Begin(Run);

        Assert.False(reports.IsShown(Run));
        Assert.Equal([Run], reports.Ended(ownPid: 1, NoneLive));
    }

    [Fact]
    public void A_run_that_is_still_running_is_not_found()
    {
        var reports = Reports();
        reports.Begin(Run);

        Assert.Empty(reports.Ended(ownPid: 1, live: new HashSet<int> { Pid }));
        Assert.Empty(reports.Ended(ownPid: Pid, NoneLive));
    }

    [Fact]
    public void A_run_that_showed_its_window_is_not_found()
    {
        var reports = Reports();
        reports.Begin(Run);
        reports.Shown(Run);

        Assert.True(reports.IsShown(Run));
        Assert.Empty(reports.Ended(ownPid: 1, NoneLive));
    }

    [Fact]
    public void A_settled_run_is_not_found_again()
    {
        var reports = Reports();
        reports.Begin(Run);
        reports.Settled(Run);

        Assert.Empty(reports.Ended(ownPid: 1, NoneLive));
    }

    [Fact]
    public void Files_that_are_not_markers_are_ignored()
    {
        var reports = Reports();
        reports.InstallId(null);
        File.WriteAllText(Path.Combine(Markers, "notes.txt"), "");
        File.WriteAllText(Path.Combine(Markers, "12-x"), "");

        Assert.Empty(reports.Ended(ownPid: 1, NoneLive));
    }

    [Fact]
    public void The_settings_install_id_is_used_when_there_is_one()
    {
        Assert.Equal("abc", Reports().InstallId("abc"));
    }

    [Fact]
    public void Without_one_an_id_is_made_once_and_kept()
    {
        var first = Reports().InstallId(null);
        var second = Reports().InstallId("");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_starting_report_names_the_last_boot_step_and_carries_the_log()
    {
        Ring(closing: false, "app ctor", "splash up (glass)", "library opening");

        var report = Reports().ForStarting(Run, "id", _started + TimeSpan.FromSeconds(40));

        Assert.StartsWith("[boot] WindowNotShown: the window was not shown 40 s after launch. Last step: library opening",
            report.Exception);
        Assert.Contains("said boot: splash up (glass)", report.Recent);
        Assert.Equal("full", report.Scope);
        Assert.Matches("^crash_[0-9a-f]{24}$", report.CrashId);
        Assert.Equal("x64", report.Architecture);
        Assert.Null(report.SystemReport);
    }

    [Fact]
    public void An_ended_report_says_how_the_run_ended()
    {
        Ring(closing: true, "app ctor", "OnLaunched");

        var report = Reports().ForEnded(Run, "id");

        Assert.StartsWith("[boot] WindowNotShown: the run ended before the window was shown (it was quit, 3 s after launch). Last step: OnLaunched",
            report.Exception);
    }

    [Fact]
    public void A_run_without_a_closing_record_stopped_without_closing()
    {
        Ring(closing: false, "app ctor");

        Assert.Contains("(it stopped without closing,", Reports().ForEnded(Run, "id").Exception);
    }

    [Fact]
    public void A_run_with_no_ring_says_it_left_no_log()
    {
        var report = Reports().ForEnded(Run, "id");

        Assert.Contains("(it left no log,", report.Exception);
        Assert.EndsWith("Last step: none", report.Exception);
        Assert.Null(report.Recent);
    }

    [Fact]
    public void A_ring_older_than_the_run_belongs_to_another_process()
    {
        Ring(closing: false, "app ctor");
        var later = new BootReports.Run(Pid, (_started + TimeSpan.FromHours(1)).ToUnixTimeMilliseconds());

        Assert.EndsWith("Last step: none", Reports().ForEnded(later, "id").Exception);
    }

    [Fact]
    public void The_starting_and_ended_reports_of_one_run_have_different_ids()
    {
        Ring(closing: false, "app ctor");
        var reports = Reports();

        Assert.NotEqual(reports.ForStarting(Run, "id", DateTimeOffset.UtcNow).CrashId, reports.ForEnded(Run, "id").CrashId);
        Assert.Equal(reports.ForEnded(Run, "id").CrashId, reports.ForEnded(Run, "id").CrashId);
    }

    [Fact]
    public void The_run_s_crash_file_and_its_recovered_exceptions_follow_the_headline()
    {
        Ring(closing: false, "app ctor", "OnLaunched", "host built");
        new CrashRecorder(() => CrashDir, Pid, Run.ProcessStartUnixMs, "0.7.5", "mac")
            .Record(new InvalidOperationException("fatal in start-up"), "appdomain");
        var inside = (_started + TimeSpan.FromSeconds(2)).LocalDateTime;
        var before = (_started - TimeSpan.FromHours(2)).LocalDateTime;
        StartupCrashLog.Append(_root, $"[{before:yyyy-MM-dd HH:mm:ss}] unhandled: System.Exception: an earlier run\n\n");
        StartupCrashLog.Append(_root, $"[{inside:yyyy-MM-dd HH:mm:ss}] unhandled: System.Exception: recovered in this run\n\n");

        var text = Reports().ForEnded(Run, "id").Exception!;

        Assert.StartsWith("[boot] WindowNotShown:", text);
        Assert.Contains("\n\n[appdomain] System.InvalidOperationException: fatal in start-up", text);
        Assert.Contains("unhandled: System.Exception: recovered in this run", text);
        Assert.DoesNotContain("an earlier run", text);
    }

    [Fact]
    public void An_ended_report_carries_the_system_report_for_the_run()
    {
        Ring(closing: false, "app ctor");
        _system.Report = new SystemCrashReport(SystemCrashReport.Wer, "Version=1\nEventType=APPCRASH\nUserName=someone\n");

        var report = Reports().ForEnded(Run, "id");

        Assert.Equal(SystemCrashReport.Wer, report.SystemReportKind);
        Assert.Contains("EventType=APPCRASH", report.SystemReport);
        Assert.DoesNotContain("someone", report.SystemReport);
        Assert.Equal(Pid, _system.AskedPid);
    }

    [Fact]
    public void The_home_folder_is_removed_from_the_texts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Ring(closing: false, $"library at {home}/Music");

        var report = Reports().ForStarting(Run, "id", DateTimeOffset.UtcNow);

        Assert.DoesNotContain(home, report.Exception);
        Assert.DoesNotContain(home, report.Recent);
    }

    private sealed class FakeSystemReports : ISystemCrashReports
    {
        public SystemCrashReport? Report { get; set; }
        public int AskedPid { get; private set; }

        public SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to)
        {
            AskedPid = pid;
            return Report;
        }
    }
}

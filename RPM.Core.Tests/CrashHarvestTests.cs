using System.Text.Json;
using RPM.Core.Crashes;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// The next launch's pass over crashed runs. The inputs are real files in a temp directory: rings
/// made with ScopeRing.Create, crash files written by CrashRecorder, and a fake for the operating
/// system's reports.
/// </summary>
[Collection(ScopeCollection.Name)]
public class CrashHarvestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"harvest-{Guid.NewGuid():N}");
    private string Crashes => Path.Combine(_root, "crashes");
    private string Rings => Path.Combine(_root, "scope");
    private string Pending => CrashHarvest.PendingDirectoryIn(Crashes);

    private readonly FakeSystemReports _system = new();
    private static readonly CrashEnvironment Here = new("0.4.0", "mac", "macOS 26.5", "arm64");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly long ProcessStart = Now.AddMinutes(-10).ToUnixTimeMilliseconds();

    public CrashHarvestTests()
    {
        Directory.CreateDirectory(Crashes);
        Directory.CreateDirectory(Rings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeSystemReports : ISystemCrashReports
    {
        public Dictionary<int, SystemCrashReport> Reports { get; } = [];

        public SystemCrashReport? Find(int pid, DateTimeOffset from, DateTimeOffset to) =>
            Reports.GetValueOrDefault(pid);
    }

    private CrashHarvest Harvest(int ownPid = 1, params int[] live) =>
        new(Crashes, Rings, ownPid, live.ToHashSet(), _system, Here);

    /// <summary>A finished run's ring, with one said line and, when asked, a Closing record. Returns
    /// the ring's start time, which names the run.</summary>
    private long Ring(int pid, bool closing) => Ring(pid, closing, "boot", "app ctor done");

    private long Ring(int pid, bool closing, string channel, string line)
    {
        using var names = ScopeNames.Fresh(Path.Combine(Rings, $"scope.{pid}.names"));
        using var ring = ScopeRing.Create(Path.Combine(Rings, $"scope.{pid}.ring"), 4096);
        ring.Write(new ScopeRecord
        {
            Kind = (ushort)ScopeKind.Said, Nanos = ScopeClock.Nanos(),
            NameId = names.Id(channel), Detail = names.Id(line),
        });
        if (closing)
            ring.Write(new ScopeRecord { Kind = (ushort)ScopeKind.Closing, Nanos = ScopeClock.Nanos() });
        using var reader = ScopeReader.Open(Path.Combine(Rings, $"scope.{pid}.ring"));
        return reader.StartUnixNanos;
    }

    /// <summary>A crash file as the dying process writes it, tied to the ring start given.</summary>
    private void CrashFile(int pid, long ringStart, string exception = "System.NullReferenceException: boom", long? processStart = null)
    {
        var start = processStart ?? ProcessStart;
        var file = new CrashFile(pid, ringStart, start, "0.3.9", "mac",
            [new CrashFailure(Now.AddMinutes(-9).ToUnixTimeMilliseconds(), "appdomain", exception)]);
        File.WriteAllText(CrashRecorder.PathFor(Crashes, pid, start), JsonSerializer.Serialize(file, CrashJson.Default.CrashFile));
    }

    private List<PendingCrash> PendingReports() =>
        Directory.Exists(Pending)
            ? [.. Directory.GetFiles(Pending, "*.json").Select(f => JsonSerializer.Deserialize(File.ReadAllText(f), CrashJson.Default.PendingCrash)!)]
            : [];

    [Fact]
    public void A_managed_crash_becomes_one_pending_report_and_its_crash_file_is_removed()
    {
        var start = Ring(500, closing: false);
        CrashFile(500, start);

        var ids = Harvest().Run(Now);

        var report = Assert.Single(PendingReports());
        Assert.Equal(Assert.Single(ids), report.CrashId);
        Assert.Contains("[appdomain] System.NullReferenceException: boom", report.Exception);
        Assert.Equal("0.3.9", report.AppVersion);           // the crashed build, not this one
        Assert.Equal("macOS 26.5", report.Platform);
        Assert.Contains("said boot: app ctor done", report.Recent);
        Assert.False(File.Exists(CrashRecorder.PathFor(Crashes, 500, ProcessStart)));
    }

    [Fact]
    public void A_native_crash_with_a_system_report_is_packaged_with_the_rings_lines()
    {
        Ring(501, closing: false);
        _system.Reports[501] = new SystemCrashReport(SystemCrashReport.Ips, "the .ips text");

        Harvest().Run(Now);

        var report = Assert.Single(PendingReports());
        Assert.Null(report.Exception);
        Assert.Equal("the .ips text", report.SystemReport);
        Assert.Equal(SystemCrashReport.Ips, report.SystemReportKind);
        Assert.Equal("0.4.0", report.AppVersion);            // no crash file: the running build
    }

    /// <summary>A crash while the app is ending is still a crash the user saw: macOS shows "rpm quit
    /// unexpectedly" for it.</summary>
    [Fact]
    public void A_native_crash_after_the_closing_record_is_packaged()
    {
        Ring(502, closing: true);
        _system.Reports[502] = new SystemCrashReport(SystemCrashReport.Ips, "SIGSEGV in exit");

        Harvest().Run(Now);

        var report = Assert.Single(PendingReports());
        Assert.Equal("SIGSEGV in exit", report.SystemReport);
        Assert.Contains("closing", report.Recent);            // the report shows it was on the way out
    }

    /// <summary>The stowed exception on a Windows window close is an APPCRASH, not a fail-fast, so it
    /// is reported.</summary>
    [Fact]
    public void A_windows_crash_after_the_closing_record_that_is_not_a_fail_fast_is_packaged()
    {
        Ring(509, closing: true);
        _system.Reports[509] = new SystemCrashReport(SystemCrashReport.Wer, Wer("APPCRASH", "c000027b"));

        Assert.Single(Harvest().Run(Now));
    }

    /// <summary>The known crash on close on Windows: abort() in ucrtbase after the app has stopped.
    /// It is common, so it is settled and not filed on every close.</summary>
    [Fact]
    public void A_windows_fail_fast_after_the_closing_record_is_not_packaged()
    {
        Ring(510, closing: true);
        _system.Reports[510] = new SystemCrashReport(SystemCrashReport.Wer, Wer("BEX64", "c0000409"));

        Assert.Empty(Harvest().Run(Now));
        Assert.Empty(PendingReports());
        Assert.True(File.Exists(Path.Combine(Crashes, "handled", RunName(510))));
    }

    [Fact]
    public void A_windows_fail_fast_while_the_app_runs_is_packaged()
    {
        Ring(511, closing: false);
        _system.Reports[511] = new SystemCrashReport(SystemCrashReport.Wer, Wer("BEX64", "c0000409"));

        Assert.Single(Harvest().Run(Now));
    }

    /// <summary>A crash on the way out can have its system report written after the next launch has
    /// harvested, so a run that closed is settled only once no report can match its window.</summary>
    [Fact]
    public void A_closed_run_with_no_report_yet_is_left_until_a_report_can_no_longer_arrive()
    {
        Ring(512, closing: true);

        Assert.Empty(Harvest().Run(Now));
        Assert.False(File.Exists(Path.Combine(Crashes, "handled", RunName(512))));

        _system.Reports[512] = new SystemCrashReport(SystemCrashReport.Ips, "arrived late");
        Assert.Single(Harvest().Run(Now));
    }

    [Fact]
    public void A_closed_run_with_no_report_is_settled_once_its_window_has_passed()
    {
        Ring(513, closing: true);

        Assert.Empty(Harvest().Run(DateTimeOffset.UtcNow + CrashHarvest.ReportLag + TimeSpan.FromMinutes(1)));

        Assert.True(File.Exists(Path.Combine(Crashes, "handled", RunName(513))));
    }

    /// <summary>A run without a Closing record and with no evidence is never settled by the window:
    /// a force quit or a power cut is not a normal close.</summary>
    [Fact]
    public void A_run_that_did_not_close_is_not_settled_by_its_window_passing()
    {
        Ring(514, closing: false);

        Assert.Empty(Harvest().Run(DateTimeOffset.UtcNow + CrashHarvest.ReportLag + TimeSpan.FromMinutes(1)));

        Assert.False(File.Exists(Path.Combine(Crashes, "handled", RunName(514))));
    }

    /// <summary>The run's name as the harvest writes its handled marker.</summary>
    private string RunName(int pid)
    {
        using var reader = ScopeReader.Open(Path.Combine(Rings, $"scope.{pid}.ring"));
        return $"{pid}-r{reader.StartUnixNanos}";
    }

    /// <summary>A Report.wer's lines with the event type and exception code given, in the order a
    /// BEX64 report writes them (the code is Sig[7] there and Sig[6] in an APPCRASH; it is read by name).</summary>
    private static string Wer(string eventType, string code) => string.Join("\r\n",
        "Version=1",
        $"EventType={eventType}",
        "Sig[0].Name=Application Name",
        "Sig[0].Value=rpm.exe",
        "Sig[3].Name=Fault Module Name",
        "Sig[3].Value=ucrtbase.dll",
        "Sig[7].Name=Exception Code",
        $"Sig[7].Value={code}");

    /// <summary>The operating system can write its report after the next launch has started.</summary>
    [Fact]
    public void A_run_with_no_evidence_yet_is_left_for_the_next_launch()
    {
        Ring(503, closing: false);

        Assert.Empty(Harvest().Run(Now));

        _system.Reports[503] = new SystemCrashReport(SystemCrashReport.Ips, "arrived late");
        Assert.Single(Harvest().Run(Now));
    }

    [Fact]
    public void A_run_is_packaged_once()
    {
        var start = Ring(504, closing: false);
        CrashFile(504, start);
        Harvest().Run(Now);
        Directory.Delete(Pending, recursive: true);           // sent and removed
        _system.Reports[504] = new SystemCrashReport(SystemCrashReport.Ips, "still on disk");

        Assert.Empty(Harvest().Run(Now));
    }

    [Fact]
    public void This_process_and_live_processes_are_left_alone()
    {
        Ring(7, closing: false);
        Ring(8, closing: false);
        _system.Reports[7] = new SystemCrashReport(SystemCrashReport.Ips, "own");
        _system.Reports[8] = new SystemCrashReport(SystemCrashReport.Ips, "live");

        Assert.Empty(Harvest(7, 8).Run(Now));
    }

    [Fact]
    public void Two_harvests_of_the_same_run_write_one_pending_report()
    {
        var start = Ring(505, closing: false);
        CrashFile(505, start);
        _system.Reports[505] = new SystemCrashReport(SystemCrashReport.Ips, "x");

        var first = Harvest(ownPid: 1).Run(Now);
        CrashFile(505, start);                                // the other copy read it before it was removed
        var second = Harvest(ownPid: 2).Run(Now);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(PendingReports());
    }

    [Fact]
    public void The_crash_id_is_the_same_for_the_same_run_every_time()
    {
        Assert.Equal(CrashHarvest.CrashIdFor("505-r123"), CrashHarvest.CrashIdFor("505-r123"));
        Assert.NotEqual(CrashHarvest.CrashIdFor("505-r123"), CrashHarvest.CrashIdFor("505-r124"));
        Assert.Matches("^crash_[0-9a-f]{24}$", CrashHarvest.CrashIdFor("505-r123"));
    }

    /// <summary>The disk was full when the process died.</summary>
    [Fact]
    public void A_crash_file_that_cannot_be_read_is_removed_and_the_other_runs_go_on()
    {
        File.WriteAllText(CrashRecorder.PathFor(Crashes, 506, 1), "{\"pid\":50");
        var start = Ring(507, closing: false);
        CrashFile(507, start);

        Assert.Single(Harvest().Run(Now));
        Assert.False(File.Exists(CrashRecorder.PathFor(Crashes, 506, 1)));
    }

    /// <summary>Valid JSON that is not a crash file the recorder wrote: an older or damaged file. It is
    /// removed like an unreadable one, and the pass goes on.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"pid":506,"ringStartUnixNanos":0,"processStartUnixMs":1,"appVersion":"0.3.1","head":"mac","failures":[]}""")]
    public void A_crash_file_with_no_failures_is_removed_and_the_other_runs_go_on(string text)
    {
        File.WriteAllText(CrashRecorder.PathFor(Crashes, 506, 1), text);
        var start = Ring(507, closing: false);
        CrashFile(507, start);

        Assert.Single(Harvest().Run(Now));
        Assert.False(File.Exists(CrashRecorder.PathFor(Crashes, 506, 1)));
    }

    /// <summary>A ring file of zero bytes: a run that died while making it.</summary>
    [Fact]
    public void An_empty_ring_is_skipped_and_the_other_runs_go_on()
    {
        File.WriteAllBytes(Path.Combine(Rings, "scope.511.ring"), []);
        var start = Ring(507, closing: false);
        CrashFile(507, start);

        Assert.Single(Harvest().Run(Now));
    }

    /// <summary>A Mac crash with hundreds of threads.</summary>
    [Fact]
    public void A_system_report_longer_than_the_limit_is_cut()
    {
        Ring(508, closing: false);
        _system.Reports[508] = new SystemCrashReport(SystemCrashReport.Ips, new string('x', CrashHarvest.MaxSystemReportChars + 10));

        Harvest().Run(Now);

        Assert.Equal(CrashHarvest.MaxSystemReportChars, Assert.Single(PendingReports()).SystemReport!.Length);
    }

    /// <summary>An .ips shaped like the ones macOS writes, over the limit: many threads, each with a
    /// deep stack. Thread 3 crashed.</summary>
    private static string LargeIps()
    {
        var threads = Enumerable.Range(0, 200).Select(t => new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = 1000 + t,
            ["frames"] = new System.Text.Json.Nodes.JsonArray([.. Enumerable.Range(0, 300).Select(f =>
                (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["imageIndex"] = f % 3,
                    ["imageOffset"] = (t * 100_000) + f,
                    ["symbol"] = $"Thread{t}Function{f}",
                })]),
        });
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["pid"] = 512,
            ["captureTime"] = "2026-09-30 22:05:00.0000 +0200",
            ["exception"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "EXC_BAD_ACCESS", ["signal"] = "SIGSEGV" },
            ["faultingThread"] = 3,
            ["threads"] = new System.Text.Json.Nodes.JsonArray([.. threads]),
            ["usedImages"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["name"] = "rpm" },
                new System.Text.Json.Nodes.JsonObject { ["name"] = "libSkiaSharp.dylib" },
                new System.Text.Json.Nodes.JsonObject { ["name"] = "libobjc.A.dylib" }),
        };
        return """{"app_name":"rpm","app_version":"0.4.0","bundleID":"com.rpm.desktop"}""" + "\n" + body.ToJsonString();
    }

    /// <summary>Cutting the text at the limit would leave the JSON unclosed and the server unable to
    /// read any of it. The report is made smaller by removing what matters least, and stays JSON.</summary>
    [Fact]
    public void A_large_mac_report_is_made_smaller_and_stays_readable()
    {
        var ips = LargeIps();
        Assert.True(ips.Length > CrashHarvest.MaxSystemReportChars);
        Ring(512, closing: false);
        _system.Reports[512] = new SystemCrashReport(SystemCrashReport.Ips, ips);

        Harvest().Run(Now);

        var kept = Assert.Single(PendingReports()).SystemReport!;
        Assert.True(kept.Length <= CrashHarvest.MaxSystemReportChars);
        var newline = kept.IndexOf('\n');
        using var header = JsonDocument.Parse(kept[..newline]);
        using var body = JsonDocument.Parse(kept[(newline + 1)..]);
        var root = body.RootElement;
        Assert.Equal(3, root.GetProperty("faultingThread").GetInt32());
        var top = root.GetProperty("threads")[3].GetProperty("frames")[0];
        Assert.Equal("Thread3Function0", top.GetProperty("symbol").GetString());
        Assert.Equal(3, root.GetProperty("usedImages").GetArrayLength());
        Assert.Equal("EXC_BAD_ACCESS", root.GetProperty("exception").GetProperty("type").GetString());
    }

    /// <summary>A stack overflow: one thread, tens of thousands of frames. The middle of its stack goes;
    /// where it crashed and where the thread began stay.</summary>
    [Fact]
    public void A_mac_report_with_one_very_deep_stack_keeps_its_top_and_bottom()
    {
        var frames = Enumerable.Range(0, 30_000).Select(f => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["imageIndex"] = 0, ["imageOffset"] = f, ["symbol"] = $"Recurse{f}",
        });
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["pid"] = 513,
            ["exception"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "EXC_BAD_ACCESS", ["signal"] = "SIGSEGV" },
            ["faultingThread"] = 0,
            ["threads"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["frames"] = new System.Text.Json.Nodes.JsonArray([.. frames]) }),
            ["usedImages"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "rpm" }),
        };
        var ips = """{"app_name":"rpm"}""" + "\n" + body.ToJsonString();
        Assert.True(ips.Length > CrashHarvest.MaxSystemReportChars);
        Ring(513, closing: false);
        _system.Reports[513] = new SystemCrashReport(SystemCrashReport.Ips, ips);

        Harvest().Run(Now);

        var kept = Assert.Single(PendingReports()).SystemReport!;
        Assert.True(kept.Length <= CrashHarvest.MaxSystemReportChars);
        using var parsed = JsonDocument.Parse(kept[(kept.IndexOf('\n') + 1)..]);
        var stack = parsed.RootElement.GetProperty("threads")[0].GetProperty("frames");
        Assert.Equal("Recurse0", stack[0].GetProperty("symbol").GetString());
        Assert.Equal("Recurse29999", stack[stack.GetArrayLength() - 1].GetProperty("symbol").GetString());
    }

    /// <summary>Two runs that had the same process id and no ring: two crash files, two reports.</summary>
    [Fact]
    public void Two_runs_with_the_same_process_id_and_no_ring_are_two_reports()
    {
        CrashFile(520, ringStart: 0, processStart: 1_000);
        CrashFile(520, ringStart: 0, processStart: 2_000);

        Assert.Equal(2, Harvest().Run(Now).Count);
    }

    /// <summary>The operating system's report is sent, but not the identifiers in it that name this
    /// machine or this user: the crash reporter's per-machine key, the boot and sleep ids, the user
    /// id, the trial rollouts, and the user name in home-folder paths.</summary>
    [Fact]
    public void Machine_identifiers_and_the_user_name_are_removed_from_a_mac_report()
    {
        Ring(530, closing: false);
        var ips = """{"app_name":"rpm","bug_type":"309"}""" + "\n" + """
            {
              "pid" : 530,
              "userID" : 501,
              "crashReporterKey" : "5DED7023-CD74-6BBD-8C10-491FE704BE1F",
              "bootSessionUUID" : "167AE016-C2E1-4B2B-B9C1-3829140A547C",
              "sleepWakeUUID" : "CC6A8D8A-7688-4B7D-926A-AE8BC4051A6E",
              "trialInfo" : {"rollouts" : [{"rolloutId" : "699d4a6bb3c59721f0815d4a"}]},
              "procPath" : "/Users/alex/Applications/rpm.app/Contents/MacOS/rpm",
              "exception" : {"type" : "EXC_BAD_ACCESS", "signal" : "SIGSEGV"},
              "faultingThread" : 0,
              "threads" : [{"frames" : [{"imageIndex" : 0, "imageOffset" : 4096}]}],
              "usedImages" : [{"name" : "rpm", "path" : "/Users/alex/Applications/rpm.app/Contents/MacOS/rpm"}]
            }
            """;
        _system.Reports[530] = new SystemCrashReport(SystemCrashReport.Ips, ips);

        Harvest().Run(Now);

        var kept = Assert.Single(PendingReports()).SystemReport!;
        foreach (var gone in new[] { "crashReporterKey", "5DED7023", "bootSessionUUID", "sleepWakeUUID", "userID", "699d4a6b", "alex" })
            Assert.DoesNotContain(gone, kept);
        Assert.Contains("/Users/~/Applications/rpm.app", kept);
        using var body = JsonDocument.Parse(kept[(kept.IndexOf('\n') + 1)..]);
        Assert.Equal(530, body.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public void The_user_name_is_removed_from_a_windows_report()
    {
        Ring(531, closing: false);
        _system.Reports[531] = new SystemCrashReport(SystemCrashReport.Wer,
            "Version=1\r\nAppPath=C:\\Users\\Zoë\\AppData\\Local\\rpm\\current\\rpm.exe\r\nSig[3].Value=coreclr.dll\r\n");

        Harvest().Run(Now);

        var kept = Assert.Single(PendingReports()).SystemReport!;
        Assert.DoesNotContain("Zoë", kept);
        Assert.Contains("C:\\Users\\~\\AppData\\Local\\rpm", kept);
    }

    /// <summary>The ring's lines and the exception are the app's own text, and they repeat file paths
    /// as they were given, so the user name in a home folder is removed from them as well.</summary>
    [Fact]
    public void The_user_name_is_removed_from_the_rings_lines_and_the_exception()
    {
        var start = Ring(532, closing: false, "scan", "scanning '/Users/alex/Music/new' failed (FileMissing)");
        CrashFile(532, start, exception:
            "System.IO.FileNotFoundException: Could not find file 'C:\\Users\\Zoë\\Music\\a.flac'");

        Harvest().Run(Now);

        var report = Assert.Single(PendingReports());
        Assert.DoesNotContain("alex", report.Recent);
        Assert.Contains("'/Users/~/Music/new'", report.Recent);
        Assert.DoesNotContain("Zoë", report.Exception);
        Assert.Contains("C:\\Users\\~\\Music\\a.flac", report.Exception);
    }

    [Fact]
    public void A_windows_report_keeps_the_crash_and_drops_the_lines_that_are_about_the_machine()
    {
        Ring(532, closing: false);
        var lines = new[]
        {
            "Version=1", "EventType=APPCRASH", "EventTime=134354617441536400", "ReportType=2",
            "Sig[0].Name=Application Name", "Sig[0].Value=rpm.exe",
            "Sig[3].Name=Fault Module Name", "Sig[3].Value=coreclr.dll",
            "DynamicSig[2].Name=Locale ID", "DynamicSig[2].Value=1043",
            "LoadedModule[0]=E:\\rpm\\rpm.exe",
            "OsInfo[0].Key=vermaj", "OsInfo[0].Value=10",
            "OsInfo[7].Key=geoid", "OsInfo[7].Value=176",
            "OsInfo[14].Key=tmsi", "OsInfo[14].Value=223921375",
            "ReportIdentifier=37ded90c-2e3e-4e82-b328-6da618461f2f",
            "IntegratorReportIdentifier=b652766b-3ee4-42be-827e-46125f7ab98f",
            "AppSessionGuid=00002500-0001-0051-077c-4108d052dd01",
            "BootId=4294967295", "ConsentKey=APPCRASH", "UI[2]=E:\\rpm\\rpm.exe",
            "Response.BucketId=d72fb8f8ae8163384fe11b663f8c2cea",
            "SomethingANewerWindowsAdds=abc",
        };
        _system.Reports[532] = new SystemCrashReport(SystemCrashReport.Wer, string.Join("\r\n", lines));

        Harvest().Run(Now);

        var kept = Assert.Single(PendingReports()).SystemReport!;
        foreach (var stays in new[]
                 {
                     "EventType=APPCRASH", "EventTime=134354617441536400", "Sig[0].Value=rpm.exe",
                     "Sig[3].Value=coreclr.dll", "LoadedModule[0]=E:\\rpm\\rpm.exe",
                     "OsInfo[0].Key=vermaj", "OsInfo[0].Value=10",
                 })
            Assert.Contains(stays, kept);
        foreach (var gone in new[]
                 {
                     "DynamicSig", "1043", "geoid", "176", "tmsi", "223921375", "ReportIdentifier", "37ded90c",
                     "b652766b", "AppSessionGuid", "BootId", "ConsentKey", "UI[2]", "Response.", "SomethingANewer",
                 })
            Assert.DoesNotContain(gone, kept);
    }

    [Fact]
    public void A_crash_file_with_no_ring_is_still_packaged()
    {
        CrashFile(509, ringStart: 0);

        Harvest().Run(Now);

        var report = Assert.Single(PendingReports());
        Assert.Null(report.Recent);
    }

    [Fact]
    public void Pending_reports_past_the_count_or_the_age_are_removed()
    {
        Directory.CreateDirectory(Pending);
        for (var i = 0; i < CrashHarvest.MaxPending + 3; i++)
        {
            var path = Path.Combine(Pending, $"crash_{i:x24}.json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddMinutes(-i));
        }
        var old = Path.Combine(Pending, $"crash_{99:x24}.json");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, Now.UtcDateTime - CrashHarvest.MaxPendingAge - TimeSpan.FromDays(1));

        Harvest().Run(Now);

        Assert.Equal(CrashHarvest.MaxPending, Directory.GetFiles(Pending).Length);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_leftover_temporary_file_from_a_dead_process_is_removed()
    {
        var leftover = CrashRecorder.PathFor(Crashes, 510, 1) + ".tmp";
        File.WriteAllText(leftover, "{\"pi");

        Harvest().Run(Now);

        Assert.False(File.Exists(leftover));
    }
}

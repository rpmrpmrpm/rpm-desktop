using System.Text;
using RPM.Core.Crashes;

namespace RPM.Core.Tests;

public class MacCrashReportsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ips-{Guid.NewGuid():N}");

    public MacCrashReportsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-09-30T20:00:00Z");
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-09-30T20:10:00Z");

    /// <summary>The shape macOS writes: one JSON header line, then the JSON body.</summary>
    private static string Ips(int pid, string captureTime) =>
        """{"app_name":"rpm","timestamp":"2026-09-30 22:05:00.00 +0200","app_version":"0.3.1","bundleID":"com.rpm.desktop","name":"rpm"}""" + "\n" +
        $$"""{"pid":{{pid}},"captureTime":"{{captureTime}}","procName":"rpm","exception":{"type":"EXC_BAD_ACCESS","signal":"SIGSEGV"},"faultingThread":0,"threads":[{"frames":[{"imageIndex":0,"imageOffset":4096}]}],"usedImages":[{"name":"libSkiaSharp.dylib"}]}""";

    private void Put(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    [Fact]
    public void The_report_for_the_same_process_inside_the_window_is_found()
    {
        Put("rpm-2026-09-30-220500.ips", Ips(4242, "2026-09-30 22:05:00.1234 +0200"));

        var found = new MacCrashReports(_dir).Find(4242, From, To);

        Assert.NotNull(found);
        Assert.Equal(SystemCrashReport.Ips, found!.Kind);
        Assert.Contains("libSkiaSharp.dylib", found.Text);
    }

    /// <summary>A process id is reused. The same pid an hour later is somebody else.</summary>
    [Fact]
    public void The_same_process_id_outside_the_window_is_not_this_run()
    {
        Put("rpm-2026-09-30-230500.ips", Ips(4242, "2026-09-30 23:05:00.0000 +0200"));

        Assert.Null(new MacCrashReports(_dir).Find(4242, From, To));
    }

    [Fact]
    public void Another_process_id_is_not_this_run()
    {
        Put("rpm-2026-09-30-220500.ips", Ips(5151, "2026-09-30 22:05:00.0000 +0200"));

        Assert.Null(new MacCrashReports(_dir).Find(4242, From, To));
    }

    [Fact]
    public void Reports_of_other_apps_are_not_read()
    {
        Put("kvo3-2026-09-30-220500.ips", Ips(4242, "2026-09-30 22:05:00.0000 +0200"));

        Assert.Null(new MacCrashReports(_dir).Find(4242, From, To));
    }

    [Fact]
    public void A_report_that_is_not_json_is_skipped()
    {
        Put("rpm-2026-09-30-220500.ips", "not a report");

        Assert.Null(new MacCrashReports(_dir).Find(4242, From, To));
    }

    [Fact]
    public void A_report_whose_capture_time_is_not_text_is_skipped()
    {
        Put("rpm-2026-09-30-220500.ips", "{\"app_name\":\"rpm\"}\n{\"pid\":4242,\"captureTime\":5}");

        Assert.Null(new MacCrashReports(_dir).Find(4242, From, To));
    }

    [Theory]
    [InlineData("2026-09-30 22:05:00.1234 +0200", "2026-09-30T20:05:00.1234Z")]
    [InlineData("2026-09-30 15:05:00.0000 -0500", "2026-09-30T20:05:00.0000Z")]
    public void Capture_times_are_read_with_their_offset(string text, string utc)
    {
        Assert.Equal(DateTimeOffset.Parse(utc), MacCrashReports.ParseTime(text));
    }
}

public class WindowsCrashReportsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wer-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-30T20:05:00Z");

    /// <summary>Report.wer as Windows writes it: UTF-16 with a byte order mark, key=value lines.
    /// Made by hand from the documented format; real reports have the same shape.</summary>
    private void Put(string session, DateTimeOffset at, string folder = "AppCrash_rpm.exe_0123abcd_4567ef")
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var lines = new[]
        {
            "Version=1",
            "EventType=APPCRASH",
            $"EventTime={at.ToFileTime()}",
            session,
            "Sig[0].Name=Application Name",
            "Sig[0].Value=rpm.exe",
            "Sig[3].Name=Fault Module Name",
            "Sig[3].Value=coreclr.dll",
            "AppPath=C:\\Users\\Zoë\\AppData\\Local\\rpm\\current\\rpm.exe",
        };
        File.WriteAllText(Path.Combine(dir, "Report.wer"), string.Join("\r\n", lines), Encoding.Unicode);
    }

    private SystemCrashReport? Find(int pid) =>
        new WindowsCrashReports([_root]).Find(pid, At.AddMinutes(-5), At.AddMinutes(5));

    [Theory]
    [InlineData("wer", "Sig[7].Name=Exception Code\r\nSig[7].Value=c0000409", true)]   // BEX64
    [InlineData("wer", "Sig[6].Name=Exception Code\r\nSig[6].Value=C0000409", true)]
    [InlineData("wer", "Sig[6].Name=Exception Code\r\nSig[6].Value=c000027b", false)]  // a stowed exception
    [InlineData("wer", "Sig[6].Name=Exception Offset\r\nSig[6].Value=c0000409", false)] // the code is read by name
    [InlineData("wer", "Version=1", false)]
    [InlineData("ips", "Sig[7].Name=Exception Code\r\nSig[7].Value=c0000409", false)]
    public void A_windows_fail_fast_is_read_from_the_exception_code(string kind, string text, bool failFast)
    {
        Assert.Equal(failFast, new SystemCrashReport(kind, text).IsWindowsFailFast);
    }

    [Fact]
    public void A_report_in_the_window_with_this_process_in_its_session_is_found()
    {
        Put("AppSessionGuid=00001092-0001-0012-a1b2-c3d4e5f60718", At);   // 0x1092 = 4242

        var found = Find(4242);

        Assert.Equal(SystemCrashReport.Wer, found!.Kind);
        Assert.Contains("Zoë", found.Text);
    }

    [Fact]
    public void A_session_naming_another_process_is_not_this_run()
    {
        Put("AppSessionGuid=000013ff-0001-0012-a1b2-c3d4e5f60718", At);

        Assert.Null(Find(4242));
    }

    [Fact]
    public void Without_a_session_the_time_window_decides()
    {
        Put("Consent=1", At);

        Assert.NotNull(Find(4242));
    }

    [Fact]
    public void A_report_outside_the_window_is_not_this_run()
    {
        Put("Consent=1", At.AddHours(1));

        Assert.Null(Find(4242));
    }

    [Fact]
    public void A_fail_fast_report_in_a_BEX64_folder_is_found()
    {
        Put("AppSessionGuid=00001092-0001-0012-a1b2-c3d4e5f60718", At, folder: "BEX64_rpm.exe_0123abcd_4567ef");

        Assert.NotNull(Find(4242));
    }

    [Fact]
    public void Another_apps_report_is_not_read()
    {
        Put("AppSessionGuid=00001092-0001-0012-a1b2-c3d4e5f60718", At, folder: "AppCrash_other.exe_0123abcd_4567ef");

        Assert.Null(Find(4242));
    }
}

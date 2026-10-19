using RPM.Core.Crashes;

namespace RPM.Core.Tests;

/// <summary>
/// startup-crash.txt keeps the newest failures and stays under its size cap.
/// </summary>
public class StartupCrashLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("startup-crash-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Path_ => Path.Combine(_dir, StartupCrashLog.FileName);

    private static string Entry(int n) =>
        $"[2026-10-09 04:{n % 60:00}:00] unhandled: System.InvalidOperationException: failure {n}\n" +
        new string('x', 4000) + "\n\n";

    [Fact]
    public void Entries_are_appended_in_order_while_the_file_is_under_the_cap()
    {
        StartupCrashLog.Append(_dir, Entry(1));
        StartupCrashLog.Append(_dir, Entry(2));

        Assert.Equal(Entry(1) + Entry(2), File.ReadAllText(Path_));
    }

    [Fact]
    public void A_file_past_the_cap_keeps_the_newest_whole_entries_and_stays_under_it()
    {
        for (var n = 0; n < 200; n++)
            StartupCrashLog.Append(_dir, Entry(n));

        var text = File.ReadAllText(Path_);

        Assert.True(text.Length <= StartupCrashLog.Cap, $"{text.Length} characters");
        Assert.StartsWith("[2026-10-09", text);
        Assert.EndsWith(Entry(199), text);
        Assert.DoesNotContain("failure 0\n", text);
        Assert.Contains("failure 198\n", text);
    }

    [Fact]
    public void The_cut_is_made_at_the_start_of_an_entry()
    {
        var text = string.Concat(Enumerable.Range(0, 100).Select(Entry));

        var kept = StartupCrashLog.Newest(text);

        Assert.StartsWith("[2026-10-09", kept);
        Assert.True(kept.Length <= StartupCrashLog.Cap / 2);
        Assert.EndsWith(Entry(99), kept);
    }
}

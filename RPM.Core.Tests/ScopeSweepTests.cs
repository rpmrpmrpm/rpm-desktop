using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// Every launch writes a ring named after its process. The sweep keeps the newest few runs and the
/// ones still running, and removes the rest.
/// </summary>
public class ScopeSweepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scope-sweep-{Guid.NewGuid():N}");
    private readonly DateTime _start = DateTime.UtcNow.AddHours(-1);

    public ScopeSweepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Run(int pid, int minutesAfterStart)
    {
        foreach (var extension in new[] { "ring", "names" })
        {
            var file = Path.Combine(_dir, $"scope.{pid}.{extension}");
            File.WriteAllText(file, "x");
            File.SetLastWriteTimeUtc(file, _start.AddMinutes(minutesAfterStart));
        }
    }

    private string[] Left() =>
        Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Finished_runs_past_the_newest_few_are_removed_and_nothing_else_is()
    {
        for (int pid = 101; pid <= 106; pid++)
            Run(pid, pid - 100);          // six finished runs, 101 the oldest

        Run(200, 0);                      // older than all of them, but still running
        Run(300, -5);                     // the oldest of all, and this process
        File.WriteAllText(Path.Combine(_dir, "scope.ring"), "a test fixture, no pid");
        File.WriteAllText(Path.Combine(_dir, "collector.400.ring"), "the collector's own");

        Scope.SweepFinishedRuns(_dir, ownPid: 300, live: new HashSet<int> { 200 });

        Assert.Equal(
            [
                "collector.400.ring",
                "scope.104.names", "scope.104.ring",
                "scope.105.names", "scope.105.ring",
                "scope.106.names", "scope.106.ring",
                "scope.200.names", "scope.200.ring",
                "scope.300.names", "scope.300.ring",
                "scope.ring",
            ],
            Left());
    }

    [Fact]
    public void A_folder_with_no_more_runs_than_are_kept_is_left_alone()
    {
        for (int pid = 1; pid <= Scope.KeptRuns; pid++)
            Run(pid, pid);

        Scope.SweepFinishedRuns(_dir, ownPid: 999, live: new HashSet<int>());

        Assert.Equal(Scope.KeptRuns * 2, Left().Length);
    }
}

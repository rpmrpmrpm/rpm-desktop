using System.Diagnostics;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// The list of running processes the ring sweep and the crash harvest read. It
/// is read from the operating system's own list, so reading it throws nothing, not even a caught
/// first-chance exception: those are a debugger stop each, and this runs at every launch.
/// </summary>
public class LivePidsTests
{
    [Fact]
    public void The_list_holds_this_process_and_a_child_that_is_running()
    {
        if (OperatingSystem.IsWindows())
            return;   // the child below is a Unix sleep; this process's own id is checked on every platform below

        using var child = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!;
        try
        {
            var live = Scope.LivePids();

            Assert.NotNull(live);
            Assert.Contains(Environment.ProcessId, live);
            Assert.Contains(child.Id, live);
        }
        finally
        {
            try { child.Kill(); } catch { }
        }
    }

    [Fact]
    public void A_child_that_has_ended_is_not_in_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var child = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false })!;
        child.WaitForExit();

        var live = Scope.LivePids();

        Assert.NotNull(live);
        Assert.DoesNotContain(child.Id, live);
    }

    [Fact]
    public void Reading_it_throws_nothing_at_all()
    {
        var thread = Environment.CurrentManagedThreadId;
        var thrown = new List<string>();
        void Seen(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
                lock (thrown) thrown.Add(e.Exception.GetType().Name);
        }

        AppDomain.CurrentDomain.FirstChanceException += Seen;
        HashSet<int>? live;
        try
        {
            live = Scope.LivePids();
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Seen;
        }

        Assert.Empty(thrown);
        Assert.NotNull(live);
        Assert.Contains(Environment.ProcessId, live);
    }
}

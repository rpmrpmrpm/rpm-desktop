using System.Diagnostics;
using RPM.Core.Crashes;

namespace RPM.Core.Tests;

/// <summary>
/// What the dying process writes: one file per run, complete or absent, with every failure the run
/// had.
/// </summary>
[Collection(ScopeCollection.Name)]
public class CrashRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"crash-recorder-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private CrashRecorder Recorder() => new(() => _dir, pid: 4242, processStartUnixMs: 1_000, appVersion: "0.3.1", head: "mac");

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException("outer", new ArgumentException("inner cause")); }
        catch (Exception e) { return e; }
    }

    [Fact]
    public void A_crash_writes_one_complete_file_named_after_the_process()
    {
        Recorder().Record(Thrown(), "appdomain");

        var path = CrashRecorder.PathFor(_dir, 4242, 1_000);
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_dir).Select(Path.GetFileName));

        var file = CrashRecorder.TryRead(path)!;
        Assert.Equal(4242, file.Pid);
        Assert.Equal(1_000, file.ProcessStartUnixMs);
        Assert.Equal("0.3.1", file.AppVersion);
        Assert.Equal("mac", file.Head);
        var failure = Assert.Single(file.Failures);
        Assert.Equal("appdomain", failure.Where);
        Assert.Contains("InvalidOperationException: outer", failure.Exception);
        Assert.Contains("inner cause", failure.Exception);
    }

    /// <summary>Two handlers can see one failure, and two threads can fail at once.</summary>
    [Fact]
    public void A_second_failure_in_the_same_run_is_added_to_the_same_file()
    {
        var recorder = Recorder();

        recorder.Record(Thrown(), "application");
        recorder.Record(new TimeoutException("second"), "appdomain");

        var file = CrashRecorder.TryRead(CrashRecorder.PathFor(_dir, 4242, 1_000))!;
        Assert.Equal(["application", "appdomain"], file.Failures.Select(f => f.Where));
    }

    /// <summary>A handler must never keep a dying process open.</summary>
    [Fact]
    public void A_recorder_that_cannot_take_the_lock_gives_up_within_the_limit()
    {
        var recorder = Recorder();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (recorder.Gate) { held.Set(); release.Wait(); }
        });
        holder.Start();
        held.Wait();

        var clock = Stopwatch.StartNew();
        recorder.Record(Thrown(), "appdomain");
        clock.Stop();

        release.Set();
        holder.Join();
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 4);
        Assert.False(File.Exists(CrashRecorder.PathFor(_dir, 4242, 1_000)));
    }

    [Fact]
    public void Recording_never_throws_when_the_folder_cannot_be_made()
    {
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "a file where the folder should be");

        new CrashRecorder(() => blocked, 1, 0, "0.3.1", "mac").Record(Thrown(), "appdomain");
    }

    /// <summary>A process id is reused. A later process with the same id that crashes before the
    /// earlier crash was harvested writes its own file, so neither crash is read as the other.</summary>
    [Fact]
    public void A_later_process_with_the_same_id_writes_its_own_file()
    {
        new CrashRecorder(() => _dir, 4242, 1_000, "0.3.1", "mac").Record(Thrown(), "appdomain");
        new CrashRecorder(() => _dir, 4242, 2_000, "0.3.2", "mac").Record(new TimeoutException("later"), "appdomain");

        var earlier = CrashRecorder.TryRead(CrashRecorder.PathFor(_dir, 4242, 1_000))!;
        var later = CrashRecorder.TryRead(CrashRecorder.PathFor(_dir, 4242, 2_000))!;
        Assert.Single(earlier.Failures);
        Assert.Equal("0.3.1", earlier.AppVersion);
        Assert.Contains("later", Assert.Single(later.Failures).Exception);
    }

    /// <summary>The first crash of a run has no file to add to. Asking for it must not throw, because a
    /// throw there becomes the last record in the ring that the crash report carries.</summary>
    [Fact]
    public void The_first_crash_of_a_run_throws_nothing_while_it_is_written()
    {
        var thread = Environment.CurrentManagedThreadId;
        var thrown = new List<string>();
        void Seen(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
                lock (thrown) thrown.Add(e.Exception.GetType().Name);
        }

        var failure = Thrown();
        AppDomain.CurrentDomain.FirstChanceException += Seen;
        try
        {
            Recorder().Record(failure, "appdomain");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Seen;
        }

        Assert.Empty(thrown);
        Assert.Single(CrashRecorder.TryRead(CrashRecorder.PathFor(_dir, 4242, 1_000))!.Failures);
    }

    /// <summary>The folder is worked out when a crash is written, not when the recorder is made: the
    /// recorder is made on the entry point's first line, and nothing there may stop the app starting.</summary>
    [Fact]
    public void A_folder_that_cannot_be_worked_out_stops_neither_making_nor_recording()
    {
        var recorder = new CrashRecorder(() => throw new IOException("no folder"), 1, 0, "0.3.1", "mac");

        recorder.Record(Thrown(), "appdomain");
    }
}

using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// Closing the app closes the library (<see cref="LibraryService.Dispose"/>). A pass that holds the library
/// across many calls, such as the cloud sync run or the cloud mirror, registers with
/// <see cref="LibraryService.StopBeforeClose"/>. The close stops it and waits for it before the database is
/// closed, so the pass is never in the middle of a read or a write on a closed connection.
/// </summary>
public class LibraryCloseStopsPassesTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-library-close-tests", Guid.NewGuid().ToString("n"));

    public LibraryCloseStopsPassesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A pass that reads and writes the library until it is told to stop, the way a sync run
    /// does. It records every refusal it meets at the gate.</summary>
    private sealed class Pass
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Running => _running.Task;
        public Task Work { get; private set; } = Task.CompletedTask;
        public int Calls;
        public Exception? Refused;

        public void Start(LibraryService library)
        {
            var db = library.Db;
            Work = Task.Run(() =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        _ = db.Count;
                        db.SetMeta("close-test", Calls.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (Interlocked.Increment(ref Calls) == 20)
                            _running.TrySetResult();
                    }
                }
                catch (Exception ex)
                {
                    Refused = ex;
                    _running.TrySetResult();
                }
            });
        }

        /// <summary>What the pass registers with the library: cancel, then wait for the work to end.</summary>
        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await Work;
        }
    }

    [Fact]
    public async Task A_pass_in_the_middle_of_its_reads_and_writes_is_stopped_before_the_library_closes()
    {
        var library = new LibraryService(TestSessions.SignedOut());
        library.Open(_dir);

        var pass = new Pass();
        library.StopBeforeClose(pass.StopAsync);
        pass.Start(library);
        await pass.Running.WaitAsync(TimeSpan.FromSeconds(10));

        // On another thread, as the window's close runs on its own.
        await Task.Run(library.Dispose);

        Assert.True(pass.Work.IsCompleted, "the close returned before the pass had stopped");
        Assert.Null(pass.Refused);
        Assert.True(pass.Calls >= 20);
        Assert.False(library.IsOpen);
    }

    [Fact]
    public async Task A_pass_that_does_not_stop_holds_the_close_back_only_for_the_wait()
    {
        var library = new LibraryService(TestSessions.SignedOut()) { HoldersWait = TimeSpan.FromMilliseconds(300) };
        library.Open(_dir);

        var never = new TaskCompletionSource();
        library.StopBeforeClose(() => never.Task);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(library.Dispose).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(library.IsOpen);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
    }
}

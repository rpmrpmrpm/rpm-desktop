using Microsoft.Extensions.Logging;
using RPM.Core.Cloud;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Cloud;
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

    // ---- PassLife, which every other pass uses to be stopped and waited for. ----

    [Fact]
    public async Task A_pass_life_cancels_and_waits_for_its_workers_and_refuses_new_ones()
    {
        var life = new RPM.Desktop.Services.Shell.PassLife();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;

        var worker = life.Run(async () =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, life.Token); }
            catch (OperationCanceledException) { }

            // Work still to do after the cancel, which the close must wait for.
            await Task.Delay(200);
            finished = true;
        });
        Assert.NotNull(worker);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await life.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(finished, "the close returned before its worker had finished");
        Assert.True(life.IsClosing);
        Assert.Null(life.Run(() => Task.CompletedTask));
        Assert.True(life.Token.IsCancellationRequested);
    }

    // ---- The cloud mirror, against a real library and a stand-in for the API. ----

    private (LibraryService Library, CloudMirrorService Mirror, Recorder<CloudMirrorService> Log) Mirror(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
    {
        var auth = TestSessions.SignedOut();
        auth.SeedSession(new AuthService.Session(
            UserId: "u1", Email: "someone@example.com", DisplayName: "Someone",
            AccessToken: "access", RefreshToken: "refresh", ExpiresUtc: DateTime.UtcNow.AddHours(1)));

        var library = new LibraryService(auth);
        library.Open(_dir);

        var http = new HttpClient(new Answering(answer)) { BaseAddress = new Uri("https://api.test/") };
        var log = new Recorder<CloudMirrorService>();
        var mirror = new CloudMirrorService(
            library, auth, new RpmCloudClient(http, _ => Task.FromResult<string?>("access")), log)
        {
            NetworkAllows = _ => ValueTask.FromResult(true),
        };
        return (library, mirror, log);
    }

    private static HttpResponseMessage Listing(int albums)
    {
        var files = string.Join(",", Enumerable.Range(1, albums).Select(i =>
            $$"""{"rpmId":"gid-{{i}}","sha256":"sha-{{i}}","relativePath":"Band {{i}}/Record {{i}}/01 One.flac","originalSize":1000,"title":"One","artist":"Band {{i}}","album":"Record {{i}}","addedAt":1000}"""));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"files":[{{files}}],"cursor":null}""", System.Text.Encoding.UTF8, "application/json"),
        };
    }

    private static Task<HttpResponseMessage> Unavailable() =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

    [Fact]
    public async Task A_close_during_the_catalogue_read_stops_the_pass()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (library, mirror, log) = Mirror(async (request, ct) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/cloud/list", StringComparison.Ordinal))
                return await Unavailable();

            // Held until the request is cancelled: the read that is in the air when the window closes.
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Listing(1);
        });

        var pass = mirror.RefreshAsync(force: true);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Task.Run(library.Dispose).WaitAsync(TimeSpan.FromSeconds(10));

        // Ended by the close, before the close returned: not left to the request's own timeout.
        Assert.True(pass.IsCompleted, "the close returned while the catalogue read was still in the air");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.Empty(log.Warnings);
        Assert.False(library.IsOpen);
    }

    [Fact]
    public async Task A_close_while_the_mirror_is_writing_waits_for_the_write()
    {
        var (library, mirror, log) = Mirror((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/cloud/list", StringComparison.Ordinal)
                ? Task.FromResult(Listing(3))
                : Unavailable());

        // Raised by the pass after its write and before it lets go of the mirror, so the close below starts
        // while the pass is still inside.
        Task? closing = null;
        var closedWhileInside = false;
        library.Changed += () =>
        {
            if (closing is not null) return;
            closing = Task.Run(library.Dispose);
            closedWhileInside = closing.Wait(TimeSpan.FromMilliseconds(300));
        };

        // The pass finishes its write, then meets the close's stop at its next step. That stop is how a pass
        // ends at a close, not a failure.
        try { await mirror.RefreshAsync(force: true); }
        catch (OperationCanceledException) { }

        Assert.NotNull(closing);
        await closing!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(closedWhileInside, "the library was closed while the mirror was still inside its pass");
        Assert.Empty(log.Warnings);
        Assert.False(library.IsOpen);
    }

    /// <summary>Answers every request through <paramref name="answer"/>.</summary>
    private sealed class Answering(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answer(request, ct);
    }

    /// <summary>Keeps every warning and error a service logs. A pass refused at the gate logs one.</summary>
    private sealed class Recorder<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return [.. _warnings]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning) return;
            lock (_warnings)
                _warnings.Add(formatter(state, exception) + (exception is null ? "" : ": " + exception.Message));
        }
    }
}

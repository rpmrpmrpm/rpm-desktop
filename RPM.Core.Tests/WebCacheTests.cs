using System.Net;
using System.Net.Http.Headers;
using System.Text;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The cache in front of the app's lookups: what it keeps, how long it believes it, and the things
/// it must not do: cache a POST, cache a temporary server error, or ask the same question
/// twice because four panes wanted the answer at once.
/// </summary>
public class WebCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-webcache-tests", Guid.NewGuid().ToString("N"));

    private readonly List<WebCacheStore> _stores = [];

    private WebCacheStore NewStore()
    {
        var store = new WebCacheStore(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
        _stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (var store in _stores)
            store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
        GC.SuppressFinalize(this);
    }

    // ── the store ──

    [Fact]
    public void Store_RoundTripsASmallBody()
    {
        var store = NewStore();
        store.Write(Entry("k1", "hello", expiresUtcTicks: 500));

        var read = store.Read("k1");

        Assert.NotNull(read);
        Assert.Equal("hello", Encoding.UTF8.GetString(read.Body));
        Assert.Equal("application/json", read.ContentType);
        Assert.Equal(500, read.ExpiresUtcTicks);
        Assert.Equal("art", read.Category);
    }

    [Fact]
    public void Store_RoundTripsABodyTooBigForTheDatabase()
    {
        var store = NewStore();
        var big = new byte[300 * 1024];
        Random.Shared.NextBytes(big);

        store.Write(Entry("k2", big));

        Assert.Equal(big, store.Read("k2")!.Body);
    }

    [Fact]
    public void Store_ReplacesTheAnswerUnderAKey()
    {
        var store = NewStore();
        store.Write(Entry("k3", "first"));
        store.Write(Entry("k3", "second"));

        Assert.Equal("second", Encoding.UTF8.GetString(store.Read("k3")!.Body));
    }

    [Fact]
    public void Store_RenewPushesTheDatesForward()
    {
        var store = NewStore();
        store.Write(Entry("k4", "body", expiresUtcTicks: 100));

        store.Renew("k4", fetchedUtcTicks: 900, expiresUtcTicks: 1000);

        var read = store.Read("k4")!;
        Assert.Equal(900, read.FetchedUtcTicks);
        Assert.Equal(1000, read.ExpiresUtcTicks);
        Assert.Equal("body", Encoding.UTF8.GetString(read.Body));
    }

    [Fact]
    public void Store_ClearEmptiesBothHalves()
    {
        var store = NewStore();
        store.Write(Entry("small", "body"));
        store.Write(Entry("large", new byte[200 * 1024]));
        Assert.True(store.BytesOnDisk() > 0);

        store.Clear();

        Assert.Null(store.Read("small"));
        Assert.Null(store.Read("large"));
    }

    [Fact]
    public void Store_ReadsNothingItWasNeverGiven()
    {
        Assert.Null(NewStore().Read("never-written"));
    }

    // ── the handler ──

    [Fact]
    public async Task SecondAskIsAnsweredFromTheStore()
    {
        var server = new FakeServer("{\"a\":1}");
        var client = Client(server, out _);

        var first = await client.GetStringAsync("https://example.test/facts");
        var second = await client.GetStringAsync("https://example.test/facts");

        Assert.Equal("{\"a\":1}", first);
        Assert.Equal("{\"a\":1}", second);
        Assert.Equal(1, server.Asks);
    }

    [Fact]
    public async Task ADifferentUrlIsADifferentQuestion()
    {
        var server = new FakeServer("{}");
        var client = Client(server, out _);

        await client.GetStringAsync("https://example.test/one");
        await client.GetStringAsync("https://example.test/two");

        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task ADifferentVaryIsADifferentNamespace()
    {
        var server = new FakeServer("{}");
        var store = NewStore();
        var client = new HttpClient(new HttpCacheHandler(store, inner: server));

        await Send(client, "https://example.test/art", WebCachePolicy.For("art") with { Vary = "nokey" });
        await Send(client, "https://example.test/art", WebCachePolicy.For("art") with { Vary = "key-abc" });
        await Send(client, "https://example.test/art", WebCachePolicy.For("art") with { Vary = "nokey" });

        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task AnAbsenceIsRememberedTooAndForLess()
    {
        var server = new FakeServer("", HttpStatusCode.NotFound);
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = Client(server, out _, clock);

        var first = await client.GetAsync("https://example.test/none");
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);

        // Six days on: still remembered. Eight: worth asking again, because catalogues gain entries.
        clock.Now = clock.Now.AddDays(6);
        await client.GetAsync("https://example.test/none");
        Assert.Equal(1, server.Asks);

        clock.Now = clock.Now.AddDays(2);
        await client.GetAsync("https://example.test/none");
        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task AnAnswerIsBelievedForAMonthAndNoLonger()
    {
        var server = new FakeServer("{}");
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = Client(server, out _, clock);

        await client.GetStringAsync("https://example.test/facts");

        clock.Now = clock.Now.AddDays(29);
        await client.GetStringAsync("https://example.test/facts");
        Assert.Equal(1, server.Asks);

        clock.Now = clock.Now.AddDays(2);
        await client.GetStringAsync("https://example.test/facts");
        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task AStaleAnswerIsRenewedRatherThanFetchedAgain()
    {
        var server = new FakeServer("{\"a\":1}") { ETag = "\"v1\"" };
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = Client(server, out _, clock);

        await client.GetStringAsync("https://example.test/facts");

        clock.Now = clock.Now.AddDays(31);
        var response = await client.GetAsync("https://example.test/facts");

        Assert.Equal("\"v1\"", server.SawIfNoneMatch);
        Assert.Equal("{\"a\":1}", await response.Content.ReadAsStringAsync());
        Assert.Equal(WebCacheSource.Revalidated, response.CacheSource());

        // And the renewal lasts a full period of its own: no third request.
        clock.Now = clock.Now.AddDays(10);
        await client.GetStringAsync("https://example.test/facts");
        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task AnUnreachableServiceIsAnsweredWithWhatIsRemembered()
    {
        var server = new FakeServer("{\"a\":1}");
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = Client(server, out _, clock);

        await client.GetStringAsync("https://example.test/facts");

        clock.Now = clock.Now.AddDays(40);
        server.Throw = true;
        var response = await client.GetAsync("https://example.test/facts");

        Assert.Equal("{\"a\":1}", await response.Content.ReadAsStringAsync());
        Assert.Equal(WebCacheSource.Stale, response.CacheSource());
    }

    [Fact]
    public async Task AServiceHavingABadMinuteIsAnsweredTheSameWay()
    {
        var server = new FakeServer("{\"a\":1}");
        var clock = new Clock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = Client(server, out _, clock);

        await client.GetStringAsync("https://example.test/facts");

        clock.Now = clock.Now.AddDays(40);
        server.Status = HttpStatusCode.InternalServerError;
        var response = await client.GetAsync("https://example.test/facts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebCacheSource.Stale, response.CacheSource());
    }

    [Fact]
    public async Task AFailureWithNothingRememberedIsStillAFailure()
    {
        var server = new FakeServer("", HttpStatusCode.InternalServerError);
        var client = Client(server, out _);

        var response = await client.GetAsync("https://example.test/facts");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // ...and is not remembered: the next ask goes out.
        await client.GetAsync("https://example.test/facts");
        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task FourPanesAskingAtOnceCostOneRequest()
    {
        var server = new FakeServer("{}") { Gate = new SemaphoreSlim(0, 4) };
        var client = Client(server, out _);

        var asks = Enumerable.Range(0, 4)
            .Select(_ => client.GetStringAsync("https://example.test/art"))
            .ToArray();

        // Four releases, so a cache that failed to share the flight finishes and fails the count
        // instead of hanging the suite.
        server.Gate!.Release(4);
        await Task.WhenAll(asks);

        Assert.Equal(1, server.Asks);
    }

    [Fact]
    public async Task OneCallerWalkingAwayDoesNotRobTheOthers()
    {
        var server = new FakeServer("{\"a\":1}") { Gate = new SemaphoreSlim(0, 4) };
        var client = Client(server, out _);

        using var leaving = new CancellationTokenSource();
        var abandoned = client.GetStringAsync("https://example.test/art", leaving.Token);
        var waiting = client.GetStringAsync("https://example.test/art");

        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        server.Gate!.Release(4);
        Assert.Equal("{\"a\":1}", await waiting);
        Assert.Equal(1, server.Asks);
    }

    [Fact]
    public async Task AServerThatAcceptsAndThenSaysNothingIsGivenUpOn()
    {
        // The client's own timeout cannot end a request it is sharing with other callers, so the handler
        // is given the same limit. Without it this hangs for the life of the process.
        var server = new FakeServer("{}") { Silent = true };
        var store = NewStore();
        var client = new HttpClient(new HttpCacheHandler(
            store, WebCachePolicy.For("test"), server, flightTimeout: TimeSpan.FromMilliseconds(150)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetStringAsync("https://example.test/facts"));

        // And the flight is not left in the way of the next caller.
        server.Silent = false;
        Assert.Equal("{}", await client.GetStringAsync("https://example.test/facts"));
    }

    [Fact]
    public async Task APostIsNotAQuestionWithACacheableAnswer()
    {
        var server = new FakeServer("{}");
        var client = Client(server, out _);

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/facts")
            {
                Content = new StringContent("{}"),
            };
            using var response = await client.SendAsync(request);
        }

        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task LookingAgainGoesPastTheCacheAndReplacesWhatWasThere()
    {
        var server = new FakeServer("first");
        var client = Client(server, out _);

        await client.GetStringAsync("https://example.test/art");

        server.Body = "second";
        using (var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/art").Refresh())
        using (var response = await client.SendAsync(request))
            Assert.Equal("second", await response.Content.ReadAsStringAsync());

        // ...and what came back replaces what was there, so browsing on from here is fast again.
        Assert.Equal("second", await client.GetStringAsync("https://example.test/art"));
        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task AClientWithNoDefaultPolicyCachesNothingUnlessAsked()
    {
        var server = new FakeServer("{}");
        var store = NewStore();
        var client = new HttpClient(new HttpCacheHandler(store, inner: server));

        await client.GetStringAsync("https://example.test/facts");
        await client.GetStringAsync("https://example.test/facts");
        Assert.Equal(2, server.Asks);

        await Send(client, "https://example.test/facts", WebCachePolicy.For("facts"));
        await Send(client, "https://example.test/facts", WebCachePolicy.For("facts"));
        Assert.Equal(3, server.Asks);
    }

    [Fact]
    public async Task ClearingTheCacheSendsTheNextAskOut()
    {
        var server = new FakeServer("{}");
        var client = Client(server, out var store);

        await client.GetStringAsync("https://example.test/facts");
        store.Clear();
        await client.GetStringAsync("https://example.test/facts");

        Assert.Equal(2, server.Asks);
    }

    [Fact]
    public async Task ACachedResponseSaysWhereItCameFrom()
    {
        var server = new FakeServer("{}");
        var client = Client(server, out _);

        using var cold = await client.GetAsync("https://example.test/facts");
        using var warm = await client.GetAsync("https://example.test/facts");

        Assert.Equal(WebCacheSource.Network, cold.CacheSource());
        Assert.Equal(WebCacheSource.Hit, warm.CacheSource());
    }

    [Fact]
    public async Task PicturesAreKeptWholeThroughTheCache()
    {
        var bytes = new byte[400 * 1024];
        Random.Shared.NextBytes(bytes);
        var server = new FakeServer(bytes, "image/jpeg");
        var client = Client(server, out _);

        var first = await client.GetByteArrayAsync("https://example.test/cover.jpg");
        var second = await client.GetAsync("https://example.test/cover.jpg");

        Assert.Equal(bytes, first);
        Assert.Equal(bytes, await second.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/jpeg", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, server.Asks);
    }

    // ── ten callers on one question ──

    /// <summary>
    /// Ten at once, which is what a wall of covers does, and one request between them.
    /// </summary>
    [Fact]
    public async Task TenPanesAskingAtOnceCostOneRequest()
    {
        var server = new FakeServer("{}") { Gate = new SemaphoreSlim(0, 10) };
        var client = Client(server, out _);

        // Called directly rather than through Task.Run: each one runs as far as the far end's own wait
        // before it yields, so all ten are on the flight before anything is released. Through Task.Run
        // the scheduler decides how many join in time, and the test would measure that instead.
        var asks = Enumerable.Range(0, 10)
            .Select(_ => client.GetStringAsync("https://example.test/art"))
            .ToArray();

        server.Gate!.Release(10);
        var answers = await Task.WhenAll(asks);

        Assert.All(answers, a => Assert.Equal("{}", a));
        Assert.Equal(1, server.Asks);
    }

    /// <summary>
    /// And when the answer is not one worth keeping.
    ///
    /// <para>This is the case the sharing has to carry on its own. A 200 is written to the store
    /// before the flight finishes, so a second asker finds it there whatever the flight table did.
    /// A 503 is not written anywhere, so if the flight is removed from the table before the last
    /// caller has left it, the next asker goes back to the network. A screenful of covers against a
    /// service that is failing is exactly when that happens.</para>
    /// </summary>
    [Fact]
    public async Task TenAskingAtOnceCostOneRequestEvenWhenTheAnswerIsNotWorthKeeping()
    {
        var server = new FakeServer("nope", HttpStatusCode.ServiceUnavailable) { Gate = new SemaphoreSlim(0, 10) };
        var client = Client(server, out _);

        // ASKED THE WAY THE APP ASKS: from threads with no synchronization context.
        //
        // The flight coalesces ten callers into one request only while they all reach it without
        // stopping on the way. The handler sends ONE kind of caller to the pool first: the thread that
        // draws, whose cache lookup is a SQLite query and a blob read taken on the thread the user is
        // waiting for. Everything else reads it where it stands and joins the flight directly.
        //
        // xUnit gives its test methods a synchronization context, which makes every caller here look
        // like the drawing thread and this property untestable as written. It is cleared for the ten,
        // which is what a screenful of covers is: art fetches off the pool, not off the deck.
        var was = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        Task<HttpResponseMessage>[] asks;
        try
        {
            asks = Enumerable.Range(0, 10)
                .Select(_ => client.GetAsync("https://example.test/art"))
                .ToArray();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(was);
        }

        server.Gate!.Release(10);
        var answers = await Task.WhenAll(asks);

        Assert.All(answers, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        Assert.Equal(1, server.Asks);

        foreach (var answer in answers)
            answer.Dispose();
    }

    /// <summary>
    /// A caller who cancels must not remove the flight from the table for the callers still on it.
    ///
    /// <para>This is the window the sharing turns on. Everyone else can only leave once the answer has
    /// arrived, but a caller who cancels leaves while the request is still running. If that caller
    /// removed the whole flight from the table on the way, the next pane asking the same question
    /// would find nothing there and start a second request for an answer that is already coming. A
    /// wall of covers cancels constantly: every row scrolled past does it.</para>
    /// </summary>
    [Fact]
    public async Task ACallerWhoWalksAwayDoesNotTakeTheFlightWithThem()
    {
        var server = new FakeServer("{\"art\":1}") { Gate = new SemaphoreSlim(0, 20) };
        var client = Client(server, out _);

        using var leaving = new CancellationTokenSource();

        // Asked from threads with no synchronization context, which is what a wall of covers is. See the
        // note in TenAskingAtOnce...: xUnit gives its tests a synchronization context, the handler sends
        // exactly that kind of caller to the pool before reading the cache, and a caller that stops on
        // the way has not joined the flight when this test cancels.
        var was = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);

        Task<string> walker;
        Task<string>[] staying;
        try
        {
            walker = client.GetStringAsync("https://example.test/art", leaving.Token);
            staying = Enumerable.Range(0, 9)
                .Select(_ => client.GetStringAsync("https://example.test/art"))
                .ToArray();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(was);
        }

        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => walker);

        // The request is still running with nine callers on it. This one joins them.
        SynchronizationContext.SetSynchronizationContext(null);
        Task<string> late;
        try
        {
            late = client.GetStringAsync("https://example.test/art");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(was);
        }

        server.Gate!.Release(20);
        await Task.WhenAll(staying);

        Assert.Equal("{\"art\":1}", await late);
        Assert.Equal(1, server.Asks);
    }

    /// <summary>
    /// Waves of callers on a service that is failing. An answer worth keeping is written to the store
    /// before its flight finishes, so the store would cover for the flight table either way. A 503 is
    /// written nowhere, which is what makes this measure the sharing itself.
    /// </summary>
    [Fact]
    public async Task WaveAfterWaveOnAnUnwellServiceCostsARequestPerWaveAndNotPerCaller()
    {
        var server = new FakeServer("late", HttpStatusCode.ServiceUnavailable)
        {
            Slow = TimeSpan.FromMilliseconds(30),
        };
        var client = Client(server, out _);

        for (var wave = 0; wave < 20; wave++)
        {
            var asking = Enumerable.Range(0, 10)
                .Select(async _ =>
                {
                    using var answer = await client.GetAsync("https://example.test/busy");
                })
                .ToArray();
            await Task.WhenAll(asking);
        }

        // Twenty waves, two hundred callers.
        Assert.InRange(server.Asks, 20, 25);
    }

    /// <summary>Ten different questions are ten requests. The sharing is per question, and a key
    /// that collided would quietly hand one album's cover to another.</summary>
    [Fact]
    public async Task TenDifferentQuestionsAreTenRequests()
    {
        var server = new FakeServer("{}") { Gate = new SemaphoreSlim(0, 10) };
        var client = Client(server, out _);

        var asks = Enumerable.Range(0, 10)
            .Select(i => client.GetStringAsync($"https://example.test/art/{i}"))
            .ToArray();

        server.Gate!.Release(10);
        await Task.WhenAll(asks);

        Assert.Equal(10, server.Asks);
    }

    /// <summary>When every caller cancels, the request is abandoned, and the next caller gets a new
    /// one rather than an answer nobody is waiting for.</summary>
    [Fact]
    public async Task WhenEverybodyWalksAwayTheRequestIsGivenUpOn()
    {
        var server = new FakeServer("{}") { Gate = new SemaphoreSlim(0, 20) };
        var client = Client(server, out _);

        using var leaving = new CancellationTokenSource();
        var abandoned = Enumerable.Range(0, 10)
            .Select(_ => client.GetStringAsync("https://example.test/art", leaving.Token))
            .ToArray();

        leaving.Cancel();
        foreach (var walked in abandoned)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => walked);

        server.Gate!.Release(20);
        Assert.Equal("{}", await client.GetStringAsync("https://example.test/art"));
    }

    // ── helpers ──

    private HttpClient Client(FakeServer server, out WebCacheStore store, Clock? clock = null)
    {
        store = NewStore();
        return new HttpClient(new HttpCacheHandler(
            store, WebCachePolicy.For("test"), server, clock is null ? null : () => clock.Now));
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string url, WebCachePolicy policy)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url).Cache(policy);
        return await client.SendAsync(request);
    }

    private static WebCacheEntry Entry(string key, string body, long expiresUtcTicks = 0) =>
        Entry(key, Encoding.UTF8.GetBytes(body), expiresUtcTicks);

    private static WebCacheEntry Entry(string key, byte[] body, long expiresUtcTicks = 0) =>
        new()
        {
            Key = key,
            Url = "https://example.test/" + key,
            Category = "art",
            Status = 200,
            ContentType = "application/json",
            FetchedUtcTicks = 1,
            ExpiresUtcTicks = expiresUtcTicks,
            Body = body,
        };

    private sealed class Clock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
    }

    /// <summary>One endpoint, counting what it is asked and answering however the test needs.</summary>
    private sealed class FakeServer(byte[] body, string contentType, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        private byte[] _body = body;

        public FakeServer(string body, HttpStatusCode status = HttpStatusCode.OK)
            : this(Encoding.UTF8.GetBytes(body), "application/json", status) { }

        private int _asks;

        /// <summary>Counted atomically: these tests put ten threads on one key at once, and a plain ++
        /// under that gives a count that undersells the very thing being measured.</summary>
        public int Asks => Volatile.Read(ref _asks);

        public HttpStatusCode Status { get; set; } = status;

        public string Body { set => _body = Encoding.UTF8.GetBytes(value); }

        public string? ETag { get; set; }

        /// <summary>What the last request carried, so a test can see the conditional go out.</summary>
        public string? SawIfNoneMatch { get; private set; }

        /// <summary>Set to answer as an unreachable host does.</summary>
        public bool Throw { get; set; }

        /// <summary>Set to answer as a host that accepts the connection and then never replies.</summary>
        public bool Silent { get; set; }

        /// <summary>How long the far end takes to answer. A real one takes a moment, and that moment is
        /// what gives the callers behind the first one time to board the same flight.</summary>
        public TimeSpan Slow { get; init; }

        /// <summary>Held closed to keep every caller in flight at once.</summary>
        public SemaphoreSlim? Gate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Gate is not null)
                await Gate.WaitAsync(ct);

            Interlocked.Increment(ref _asks);
            SawIfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values)
                ? string.Join(",", values)
                : null;

            if (Throw)
                throw new HttpRequestException("no route to host");

            if (Silent)
                await Task.Delay(Timeout.Infinite, ct);

            if (Slow > TimeSpan.Zero)
                await Task.Delay(Slow, ct);

            if (SawIfNoneMatch is not null && ETag is not null && SawIfNoneMatch == ETag)
                return new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };

            var response = new HttpResponseMessage(Status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(_body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            if (ETag is not null)
                response.Headers.TryAddWithoutValidation("ETag", ETag);
            return response;
        }
    }
}

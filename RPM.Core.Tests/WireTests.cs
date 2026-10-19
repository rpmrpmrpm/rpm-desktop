using System.Net;
using System.Reflection;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The one place a client that reaches outside this network is built, and the claim that goes with
/// it: there is no way to end up with one that skips the gate by being written the ordinary way.
///
/// <para>The socket handler with the connection budget on it must not be public. If it were, a
/// client could be built straight off it and reach public services with no gate in front of it.
/// The artwork fetcher is the case that matters: it sends a screenful of requests at a time, which
/// is what the gate exists for. These tests hold the shape of the pipeline each door produces,
/// because the shape is the rule.</para>
/// </summary>
public class WireTests
{
    /// <summary>The chain of handlers, outermost first.</summary>
    private static List<HttpMessageHandler> Pipeline(HttpMessageHandler outermost)
    {
        var chain = new List<HttpMessageHandler>();
        for (var handler = outermost; handler is not null;)
        {
            chain.Add(handler);
            handler = handler is DelegatingHandler wrapping ? wrapping.InnerHandler : null;
        }
        return chain;
    }

    /// <summary>The pipeline a built client actually holds, which is the only thing that says what
    /// a request through it will pass through.</summary>
    private static List<HttpMessageHandler> Pipeline(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(HttpMessageInvoker).GetField("handler", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Pipeline((HttpMessageHandler)field!.GetValue(client)!);
    }

    /// <summary>
    /// Every door out of here carries both of them, in this order: the catalogue guard, then the gate.
    ///
    /// <para>The guard is OUTERMOST on purpose. A request to a catalogue from a head that is not the
    /// API is refused whether or not there is a network, because the answer does not depend on one.
    /// Queueing it behind the gate would only make the mistake slower to find.</para>
    /// </summary>
    [Fact]
    public void EveryDoorOutOfHereHasTheGuardAndThenTheGateOnIt()
    {
        using var plain = Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(20));
        using var retrying = Wire.OutsideRetrying(Destination.Unaddressed, TimeSpan.FromSeconds(20));

        foreach (var chain in new[]
        {
            Pipeline(Wire.GatedSockets()),
            Pipeline(Wire.GatedStream(TimeSpan.FromSeconds(12))),
            Pipeline(plain),
            Pipeline(retrying),
        })
        {
            Assert.IsType<CatalogueGuardHandler>(chain[0]);
            Assert.IsType<OnlineGateHandler>(chain[1]);
        }
    }

    /// <summary>And the guard is not decoration: a client built the ordinary way refuses a catalogue
    /// outright, from any process that has not said it is the API.</summary>
    [Fact]
    public async Task ACatalogueIsRefusedThroughAnyDoorOutOfHere()
    {
        using var client = Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<RPM.Fingerprint.External.CatalogueReachedDirectlyException>(
            () => client.GetAsync("https://musicbrainz.org/ws/2/artist/?query=x"));
    }

    /// <summary>
    /// The gate is outermost, in front of the retries. A refusal is the app declining to open a
    /// socket, not a transient answer to be tried three times with a wait between. Inside the
    /// retries it would become three refusals and about a second of waiting, per request, for
    /// nothing.
    /// </summary>
    [Fact]
    public void TheGateIsInFrontOfTheRetriesAndNotBehindThem()
    {
        using var retrying = Wire.OutsideRetrying(Destination.Unaddressed, TimeSpan.FromSeconds(20));
        var chain = Pipeline(retrying);

        var gate = chain.FindIndex(h => h is OnlineGateHandler);
        var retries = chain.FindIndex(h => h is TransientRetryHandler);

        Assert.True(gate >= 0 && retries >= 0, "both handlers are on this client");
        Assert.True(gate < retries, "the gate must be outside the retries");
    }

    /// <summary>Every socket handler this app makes carries the ceiling. A pooled TLS connection
    /// retired is about three IOExceptions in the debugger's log, so the count of those is the count
    /// of connections opened.</summary>
    [Fact]
    public void EverySocketHandlerCarriesTheConnectionBudget()
    {
        foreach (var pipeline in Doors())
        {
            var sockets = pipeline.OfType<SocketsHttpHandler>().SingleOrDefault();
            Assert.NotNull(sockets);
            Assert.Equal(8, sockets!.MaxConnectionsPerServer);
        }

        static IEnumerable<List<HttpMessageHandler>> Doors()
        {
            yield return Pipeline(Wire.GatedSockets());
            yield return Pipeline(Wire.GatedStream(TimeSpan.FromSeconds(12)));
            yield return Pipeline(Wire.LocalSockets());
            using var plain = Wire.Outside(Destination.Unaddressed, TimeSpan.FromSeconds(20));
            yield return Pipeline(plain);
            using var retrying = Wire.OutsideRetrying(Destination.Unaddressed, TimeSpan.FromSeconds(20));
            yield return Pipeline(retrying);
        }
    }

    /// <summary>
    /// The LAN door is ungated, and that is why it has its own name: a peer's library is reachable on
    /// a network whose internet connection is down, and putting it behind the gate would take shared
    /// libraries away from exactly the people who still have them.
    /// </summary>
    [Fact]
    public void TheDoorOntoThisLanIsUngatedAndSaysSoInItsName()
    {
        var chain = Pipeline(Wire.LocalSockets());

        Assert.DoesNotContain(chain, h => h is OnlineGateHandler);
        Assert.Single(chain);
    }

    /// <summary>A live stream is a response that never completes, so nothing may retire the
    /// connection under it. It still goes through the gate and the budget like everything else.</summary>
    [Fact]
    public void TheStreamingDoorKeepsItsConnectionAndStillCarriesTheRest()
    {
        var chain = Pipeline(Wire.GatedStream(TimeSpan.FromSeconds(12)));
        var sockets = chain.OfType<SocketsHttpHandler>().Single();

        Assert.IsType<CatalogueGuardHandler>(chain[0]);
        Assert.IsType<OnlineGateHandler>(chain[1]);
        Assert.Equal(8, sockets.MaxConnectionsPerServer);
        Assert.Equal(Timeout.InfiniteTimeSpan, sockets.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), sockets.ConnectTimeout);
        Assert.True(sockets.AllowAutoRedirect);
    }

    /// <summary>The raw socket handler is not reachable from outside this class at all. If it were,
    /// clients could be built from it with no gate.</summary>
    [Fact]
    public void TheRawSocketHandlerCannotBeAskedForByName()
    {
        var budget = typeof(Wire).GetMethod("Budget", BindingFlags.Public | BindingFlags.Static);
        Assert.Null(budget);
    }

    [Fact]
    public void ABaseAddressAlwaysEndsInASlashSoThatARelativePathIsNotSwallowed()
    {
        using var client = Wire.Outside(
            Destination.Service("https://accounts.rpmrpmrpm.com"), TimeSpan.FromSeconds(20));
        Assert.Equal("https://accounts.rpmrpmrpm.com/", client.BaseAddress?.ToString());

        using var trailing = Wire.Outside(
            Destination.Service("https://accounts.rpmrpmrpm.com/"), TimeSpan.FromSeconds(20));
        Assert.Equal("https://accounts.rpmrpmrpm.com/", trailing.BaseAddress?.ToString());
    }
}

/// <summary>
/// The gate with several callers on it at once, which is what a screen actually does: a wall of
/// covers, a page of reviews and a token refresh all go out in the same handful of milliseconds.
/// </summary>
public class GateUnderLoadTests
{
    private sealed class Counting : HttpMessageHandler
    {
        public int Sent;
        public Func<HttpRequestMessage, HttpResponseMessage> Answer =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(Answer(request));
        }
    }

    /// <summary>Ten requests arriving together, with nothing measured yet, cost one probe.</summary>
    [Fact]
    public async Task TenRequestsAtOnceCostOneProbeBetweenThem()
    {
        var probes = 0;
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ => { Interlocked.Increment(ref probes); await release.Task; return true; },
            now: () => DateTimeOffset.UtcNow);

        var inner = new Counting();
        using var client = new HttpClient(new OnlineGateHandler(reach, inner));

        var asking = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => client.GetAsync("https://api.rpmrpmrpm.com/thing")))
            .ToList();

        await NetworkReachTests.Until(() => Volatile.Read(ref probes) == 1, "the one probe to start");
        release.SetResult();

        var answers = await Task.WhenAll(asking);

        Assert.All(answers, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, Volatile.Read(ref probes));
        Assert.Equal(10, inner.Sent);

        foreach (var answer in answers)
            answer.Dispose();
    }

    /// <summary>
    /// With nothing out there, ten requests cost one probe and no sockets at all. This is the whole
    /// reason the class exists: a laptop opened with the wifi off would otherwise produce a name
    /// lookup, a socket failure and three retries for every pass in the app, inside one second.
    /// </summary>
    [Fact]
    public async Task TenRequestsWithNothingOutThereOpenNoSockets()
    {
        var probes = 0;
        var reach = new NetworkReach(
            hasNetwork: () => false,
            ask: _ => { Interlocked.Increment(ref probes); return Task.FromResult(false); },
            now: () => DateTimeOffset.UtcNow);

        var inner = new Counting();
        using var client = new HttpClient(new OnlineGateHandler(reach, inner));

        var answers = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => client.GetAsync("https://api.rpmrpmrpm.com/thing"))));

        Assert.Equal(0, inner.Sent);
        Assert.Equal(0, Volatile.Read(ref probes));   // no interface up is answer enough
        Assert.All(answers, r =>
        {
            Assert.True(OnlineGateHandler.WasRefused(r));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        });

        foreach (var answer in answers)
            answer.Dispose();
    }

    /// <summary>
    /// Ten requests failing the line at the same moment retire the answer once between them, rather
    /// than each starting its own probe. The answer to "is the network down" must not be ten more
    /// requests.
    /// </summary>
    [Fact]
    public async Task TenFailuresAtOnceDoNotBecomeTenProbes()
    {
        var probes = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            // Takes a moment, as a probe does. With an instant probe the ten doubts do not overlap at all:
            // each finds the answer already measured again and honestly retires it. The test would then pin
            // the scheduler instead of the coalescing.
            ask: async _ => { Interlocked.Increment(ref probes); await Task.Delay(60); return true; },
            now: () => DateTimeOffset.UtcNow);

        // The ten are held at the far end until all ten are there, so that they fail together the
        // way a line going down actually fails them: in the same millisecond, not one after another.
        var allTen = new TaskCompletionSource();
        var arrived = 0;
        var first = true;

        var inner = new Blocking(async () =>
        {
            if (Interlocked.Exchange(ref first, false))
                return;

            if (Interlocked.Increment(ref arrived) == 10)
                allTen.TrySetResult();

            await allTen.Task;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "the line went");
        });

        using var client = new HttpClient(new OnlineGateHandler(reach, inner))
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        // One that works, so there is a held answer for the ten to doubt.
        (await client.GetAsync("https://api.rpmrpmrpm.com/first")).Dispose();
        Assert.Equal(1, Volatile.Read(ref probes));

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            try { (await client.GetAsync($"https://api.rpmrpmrpm.com/{i}")).Dispose(); }
            catch (HttpRequestException) { }
        })));

        await Task.Delay(300);

        // One of the ten found a held answer to retire and ran the probe. The other nine arrived while
        // that probe was still running, found the answer already retired, and started nothing. Ten
        // probes would mean every failure had started its own.
        Assert.InRange(Volatile.Read(ref probes), 2, 3);
    }

    private sealed class Blocking(Func<Task> act) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await act();
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    /// <summary>
    /// A refusal is not a transient answer. Behind the retries it would be tried three times with a
    /// wait between: ten requests becoming thirty refusals and a dozen seconds of sleeping, for a
    /// question that was settled before the first one.
    /// </summary>
    [Fact]
    public async Task ARefusalIsNotRetried()
    {
        var reach = new NetworkReach(
            hasNetwork: () => false,
            ask: _ => Task.FromResult(false),
            now: () => DateTimeOffset.UtcNow);

        var inner = new Counting();
        // The real shape: the gate outside, the retries inside.
        using var client = new HttpClient(
            new OnlineGateHandler(reach, new TransientRetryHandler(inner)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("https://api.rpmrpmrpm.com/thing");
        started.Stop();

        Assert.True(OnlineGateHandler.WasRefused(response));
        Assert.Equal(0, inner.Sent);
        Assert.True(started.ElapsedMilliseconds < 500, $"the refusal took {started.ElapsedMilliseconds} ms");
    }
}

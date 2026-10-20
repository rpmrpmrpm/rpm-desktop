using System.Net;
using System.Net.NetworkInformation;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The one question the app asks, so that each service does not find out for itself, and the
/// method that asks it.
///
/// <para>Most of these tests are about not asking: no probe when there is no interface to probe
/// over, one probe shared by everyone who wants an answer at the same moment, and an answer kept long
/// enough that a pass over four hundred tracks measures once.</para>
/// </summary>
public class NetworkReachTests
{
    /// <summary>Waits for something a background measurement will do, instead of sleeping and
    /// hoping. Fails the test instead of hanging it.</summary>
    internal static async Task Until(Func<bool> holds, string what)
    {
        var giveUpAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!holds())
        {
            Assert.True(DateTime.UtcNow < giveUpAt, $"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }

    /// <summary>A clock the test moves by hand. Half of these tests are about the holding time, and a
    /// test that waited for it in real seconds would be slow and unreliable.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void TheReadingIsTheThreeAnswers()
    {
        Assert.Equal(Reach.Online, ReachReading.For(hasNetwork: true, internetAnswered: true));
        Assert.Equal(Reach.LocalOnly, ReachReading.For(hasNetwork: true, internetAnswered: false));
        Assert.Equal(Reach.Offline, ReachReading.For(hasNetwork: false, internetAnswered: false));
    }

    /// <summary>An answer decides it, whatever the interface list said. A machine whose adapters
    /// cannot be read but whose request came back is online.</summary>
    [Fact]
    public void AnAnswerOutranksTheInterfaceList()
    {
        Assert.Equal(Reach.Online, ReachReading.For(hasNetwork: false, internetAnswered: true));
    }

    /// <summary>No interface up is enough of an answer. Probing anyway would be the name lookup this
    /// class exists to prevent.</summary>
    [Fact]
    public async Task WithNoNetworkNothingIsAsked()
    {
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => false,
            ask: _ => { asks++; return Task.FromResult(true); },
            now: () => DateTimeOffset.UtcNow);

        Assert.False(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.Offline, reach.Now);
        Assert.Equal(0, asks);
    }

    [Fact]
    public async Task ANetworkWithNothingBeyondItIsItsOwnAnswer()
    {
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => Task.FromResult(false),
            now: () => DateTimeOffset.UtcNow);

        Assert.False(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.LocalOnly, reach.Now);
    }

    [Fact]
    public async Task AServiceThatAnswersOpensTheDoor()
    {
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => Task.FromResult(true),
            now: () => DateTimeOffset.UtcNow);

        Assert.True(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.Online, reach.Now);
    }

    /// <summary>Before anything has been measured the answer is "online". A window that has not
    /// checked yet must not show a band telling the user they are offline.</summary>
    [Fact]
    public void NothingMeasuredYetIsNotABandOnScreen()
    {
        var reach = new NetworkReach(
            hasNetwork: () => false,
            ask: _ => Task.FromResult(false),
            now: () => DateTimeOffset.UtcNow);

        Assert.Equal(Reach.Online, reach.Now);
        Assert.False(reach.Settled);
    }

    /// <summary>The main purpose of the class: a pass that would ask four hundred times asks once.</summary>
    [Fact]
    public async Task AnAnswerIsHeldRatherThanMeasuredAgain()
    {
        var clock = new Clock();
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => { asks++; return Task.FromResult(false); },
            now: () => clock.Now);

        for (var i = 0; i < 400; i++)
            Assert.False(await reach.AllowAsync(CancellationToken.None));

        Assert.Equal(1, asks);
    }

    /// <summary>The answer is kept for a while, not for ever: the user is waiting for it to
    /// change.</summary>
    [Fact]
    public async Task AnAnswerOfNotOnlineIsLookedAtAgainSoon()
    {
        var clock = new Clock();
        var answers = new Queue<bool>([false, true]);
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => Task.FromResult(answers.Count > 0 ? answers.Dequeue() : true),
            now: () => clock.Now);

        Assert.False(await reach.AllowAsync(CancellationToken.None));

        clock.Advance(TimeSpan.FromSeconds(11));

        Assert.True(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.Online, reach.Now);
    }

    /// <summary>Everyone asking at once waits on the same measurement.</summary>
    [Fact]
    public async Task EverybodyWaitsOnTheOneProbe()
    {
        var asks = 0;
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ => { Interlocked.Increment(ref asks); await release.Task; return true; },
            now: () => DateTimeOffset.UtcNow);

        var waiting = Enumerable.Range(0, 20)
            .Select(_ => reach.AllowAsync(CancellationToken.None).AsTask())
            .ToList();

        release.SetResult();
        var answers = await Task.WhenAll(waiting);

        Assert.All(answers, Assert.True);
        Assert.Equal(1, asks);
    }

    /// <summary>A caller that gives up does not cancel the measurement for the others.</summary>
    [Fact]
    public async Task ACallerGivingUpDoesNotCancelTheProbeForTheRest()
    {
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ => { await release.Task; return true; },
            now: () => DateTimeOffset.UtcNow);

        using var giveUp = new CancellationTokenSource();
        var abandoned = reach.AllowAsync(giveUp.Token).AsTask();
        var patient = reach.AllowAsync(CancellationToken.None).AsTask();

        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        release.SetResult();
        Assert.True(await patient);
    }

    /// <summary>A failed request does not decide anything on its own (a service that is briefly
    /// unavailable does not mean the user is offline); it only discards the held answer.</summary>
    [Fact]
    public async Task DoubtRetiresTheAnswerWithoutOverturningIt()
    {
        var clock = new Clock();
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => { asks++; return Task.FromResult(true); },
            now: () => clock.Now);

        Assert.True(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(1, asks);

        reach.Doubt();
        Assert.Equal(Reach.Online, reach.Now);       // still online until something says otherwise

        Assert.True(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(2, asks);                        // but measured again, not trusted
    }

    /// <summary>The band listens to this, so it must fire on a change and not on every measurement
    /// that agrees with the previous one.</summary>
    [Fact]
    public async Task ChangedFiresOnTheChangeOnly()
    {
        var clock = new Clock();
        var online = false;
        var seen = new List<Reach>();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => Task.FromResult(online),
            now: () => clock.Now);
        reach.Changed += seen.Add;

        await reach.AllowAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(11));
        await reach.AllowAsync(CancellationToken.None);   // same answer again

        online = true;
        clock.Advance(TimeSpan.FromSeconds(11));
        await reach.AllowAsync(CancellationToken.None);

        Assert.Equal([Reach.LocalOnly, Reach.Online], seen);
    }

    /// <summary>A probe that throws counts as a measurement of "no", not as an exception that reaches
    /// the app. This class is the one place where failures are expected.</summary>
    [Fact]
    public async Task AProbeThatThrowsIsAnAnswer()
    {
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => throw new HttpRequestException("no route to host"),
            now: () => DateTimeOffset.UtcNow);

        Assert.False(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.LocalOnly, reach.Now);
    }

    // ---- what several threads at once do to it ------------------------------------------------

    /// <summary>
    /// A listener that throws must not break the measurement.
    ///
    /// <para>If the announcement were a bare <c>Changed?.Invoke</c> inside the measurement, a
    /// listener that threw would fault the one Task every caller in the app is awaiting. The
    /// exception would then come out of <c>HttpClient.SendAsync</c> for all of them at once, as
    /// whatever the listener threw and not as an <c>HttpRequestException</c>. One listener resolving
    /// a service from a host that is shutting down is enough to cause it.</para>
    /// </summary>
    [Fact]
    public async Task AListenerThatThrowsDoesNotFaultTheMeasurementEverybodyIsWaitingOn()
    {
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ => { await release.Task; return false; },
            now: () => DateTimeOffset.UtcNow);

        reach.Changed += _ => throw new InvalidOperationException("the host is shutting down");

        var waiting = Enumerable.Range(0, 10)
            .Select(_ => reach.AllowAsync(CancellationToken.None).AsTask())
            .ToList();

        release.SetResult();

        Assert.All(await Task.WhenAll(waiting), Assert.False);
        Assert.Equal(Reach.LocalOnly, reach.Now);
    }

    /// <summary>The listeners after it in the list are still notified.</summary>
    [Fact]
    public async Task AListenerThatThrowsDoesNotCostTheOnesBehindItTheirNews()
    {
        var told = new List<string>();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => Task.FromResult(false),
            now: () => DateTimeOffset.UtcNow);

        reach.Changed += _ => told.Add("first");
        reach.Changed += _ => throw new InvalidOperationException("boom");
        reach.Changed += _ => told.Add("third");

        await reach.AllowAsync(CancellationToken.None);

        Assert.Equal(["first", "third"], told);
    }

    /// <summary>
    /// A doubt starts the measurement, so the next request does not have to wait for it.
    ///
    /// <para>If a doubt only discarded the answer, the next request would wait at the gate for the
    /// whole probe, using up its own <c>HttpClient.Timeout</c> without sending anything. Two of this
    /// app's clients have an eight second timeout, which a long probe could use up completely.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADoubtLooksAgainAtOnceRatherThanLeavingItToTheNextRequest()
    {
        var clock = new Clock();
        var asked = new TaskCompletionSource();
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ =>
            {
                if (Interlocked.Increment(ref asks) == 2)
                    asked.TrySetResult();
                return Task.FromResult(true);
            },
            now: () => clock.Now);

        Assert.True(await reach.AllowAsync(CancellationToken.None));

        reach.Doubt();

        // Nobody has called AllowAsync since. The measurement happens anyway.
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, asks);
    }

    /// <summary>
    /// Ten requests that fail together on the line are one doubt, not ten measurements.
    ///
    /// <para>They fail in the same millisecond (that is what a line going down looks like), and each
    /// of them calls <see cref="NetworkReach.Doubt"/>. If each started a probe, the answer to "is the
    /// network down" would be ten more requests.</para>
    /// </summary>
    [Fact]
    public async Task TenDoubtsAtOnceAreOneMeasurement()
    {
        var asks = 0;
        var held = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            // Every measurement after the first one hangs, so all ten doubts arrive while the
            // measurement started by the first of them is still running.
            ask: async _ =>
            {
                if (Interlocked.Increment(ref asks) >= 2)
                    await held.Task;
                return true;
            },
            now: () => DateTimeOffset.UtcNow);

        Assert.True(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(1, asks);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(reach.Doubt)));

        await Until(() => Volatile.Read(ref asks) >= 2, "the doubt to start a measurement");
        await Task.Delay(100);

        // One of the ten found an answer to discard. The other nine found it already discarded and
        // a measurement already running, and made no request.
        Assert.Equal(2, Volatile.Read(ref asks));

        held.SetResult();
    }

    /// <summary>
    /// Ten threads arriving with nothing measured get one probe between them, and none of them holds
    /// a lock while it runs.
    /// </summary>
    [Fact]
    public async Task TenThreadsArrivingTogetherShareOneProbeAndNoneOfThemBlocks()
    {
        var asks = 0;
        var probing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ =>
            {
                Interlocked.Increment(ref asks);
                probing.TrySetResult();
                await release.Task;
                return true;
            },
            now: () => DateTimeOffset.UtcNow);

        var waiting = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => reach.AllowAsync(CancellationToken.None).AsTask()))
            .ToList();

        await probing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The measurement is running. Reading the current answer must not wait for it: nothing here
        // holds the gate during the probe, so an eleventh thread asking a cheap question gets an answer.
        var read = OnItsOwnThread(() => reach.Now);
        Assert.Equal(Reach.Online, await read.WaitAsync(TimeSpan.FromSeconds(2)));

        release.SetResult();
        var answers = await Task.WhenAll(waiting);
        Assert.All(answers, Assert.True);
        Assert.Equal(1, asks);
    }

    /// <summary>
    /// Runs a read on its own thread. The walk test below blocks a pool thread on purpose, and in a
    /// full parallel run the pool can take longer than a read's two seconds to add another thread, so
    /// a Task.Run read could time out even with nothing in NetworkReach holding it.
    /// </summary>
    private static Task<T> OnItsOwnThread<T>(Func<T> read)
        => Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>
    /// The interface walk does not run under the lock either. It is a system call on every
    /// measurement, and under the gate it would make ten threads wait behind one getifaddrs.
    /// </summary>
    [Fact]
    public async Task TheInterfaceWalkDoesNotHoldTheGate()
    {
        var walking = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reach = new NetworkReach(
            hasNetwork: () =>
            {
                walking.TrySetResult();
                release.Task.Wait();
                return false;
            },
            ask: _ => Task.FromResult(false),
            now: () => DateTimeOffset.UtcNow);

        var measuring = Task.Run(() => reach.RefreshAsync());
        await walking.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var read = OnItsOwnThread(() => (reach.Now, reach.Settled));
        var (now, settled) = await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(Reach.Online, now);   // the assumed one, nothing measured yet
        Assert.False(settled);

        release.SetResult();
        Assert.Equal(Reach.Offline, await measuring);
    }

    // ---- the network changing during a measurement --------------------------------------------

    /// <summary>
    /// A measurement that was running when the addresses changed may not store its answer as
    /// fresh.
    ///
    /// <para>Otherwise a cable pulled during a probe would be answered "online" (true, but about a
    /// network that has gone), and that answer would then be kept for the thirty seconds an online
    /// reading is held, with the watchdog not running because the reading was online.</para>
    /// </summary>
    [Fact]
    public async Task AnAnswerMeasuredBeforeTheAddressesChangedIsNotHeldAsFresh()
    {
        var clock = new Clock();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ =>
            {
                // The first probe measures the network as it is now: there is a line out. The
                // second measures it after the cable is pulled.
                if (Interlocked.Increment(ref asks) == 1)
                {
                    started.SetResult();
                    await release.Task;
                    return true;
                }

                return false;
            },
            now: () => clock.Now);

        var first = reach.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The cable is pulled while that probe is still running.
        reach.AddressesChanged();

        release.SetResult();
        Assert.Equal(Reach.Online, await first);      // still answered, correctly for that moment

        // But nothing was stored, so the next caller measures the network as it is now, instead
        // of getting thirty seconds of a reading that was already out of date.
        Assert.False(await reach.AllowAsync(CancellationToken.None));
        Assert.Equal(Reach.LocalOnly, reach.Now);
    }

    /// <summary>A caller arriving after the change does not get the outdated measurement.</summary>
    [Fact]
    public async Task ACallerArrivingAfterTheChangeGetsANewMeasurement()
    {
        var release = new TaskCompletionSource();
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ =>
            {
                if (Interlocked.Increment(ref asks) == 1)
                    await release.Task;
                return false;
            },
            now: () => DateTimeOffset.UtcNow);

        var stale = reach.RefreshAsync();
        reach.AddressesChanged();

        var fresh = reach.RefreshAsync();
        Assert.NotSame(stale, fresh);

        release.SetResult();
        await Task.WhenAll(stale, fresh);
        Assert.Equal(2, asks);
    }

    /// <summary>
    /// A probe that never stops does not block the app.
    ///
    /// <para>Every request in the app waits on the one measurement. If only the caller's own
    /// <c>HttpClient.Timeout</c> could end that wait, a probe that ignored its token would close the
    /// gate for everybody for as long as it liked, and each caller would get its own timeout after
    /// waiting at the gate without sending anything. So the measurement enforces the time budget
    /// itself, and does not rely on the probe to respect it.</para>
    /// </summary>
    [Fact]
    public async Task AProbeThatWillNotStopDoesNotHoldEverybodyElse()
    {
        var neverAnswers = new TaskCompletionSource<bool>();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            // Takes the token and ignores it, which is what a client with no timeout does.
            ask: _ => neverAnswers.Task,
            now: () => DateTimeOffset.UtcNow);

        var waiting = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => reach.AllowAsync(CancellationToken.None).AsTask()))
            .ToList();

        // The budget is four seconds. Ten is a limit that checks "this returned", not "this was fast".
        var answers = await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(answers, Assert.False);
        Assert.Equal(Reach.LocalOnly, reach.Now);   // a network, and nothing beyond it answered

        neverAnswers.TrySetResult(true);
    }

    /// <summary>A probe that throws long after it was abandoned does not cause an unexpected
    /// exception later.</summary>
    [Fact]
    public async Task AProbeThatFailsAfterItWasGivenUpOnIsNotRaisedLater()
    {
        var late = new TaskCompletionSource<bool>();
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => late.Task,
            now: () => DateTimeOffset.UtcNow);

        var faults = new List<Exception>();
        void Caught(object? _, UnobservedTaskExceptionEventArgs e) { lock (faults) faults.Add(e.Exception!); }
        TaskScheduler.UnobservedTaskException += Caught;
        try
        {
            Assert.False(await reach.AllowAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

            late.TrySetException(new HttpRequestException("the probe fell over eventually"));
            await Task.Delay(100);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);

            lock (faults)
                Assert.DoesNotContain(faults, f => f.ToString().Contains("the probe fell over eventually"));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Caught;
        }
    }

    /// <summary>
    /// Everything above, at the same time, for a while: ten callers asking, one doubting, one being
    /// told the addresses changed. The test checks that it finishes, that no exception escapes, and
    /// that the number of probes follows the number of events, not the number of callers.
    /// </summary>
    [Fact]
    public async Task TheWholeThingUnderLoadFinishesAndDoesNotStorm()
    {
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: async _ => { Interlocked.Increment(ref asks); await Task.Yield(); return true; },
            now: () => DateTimeOffset.UtcNow);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // The callers stop on the clock, not only on the token. While the answer is held, AllowAsync
        // returns without awaiting anything, so a caller that did not yield would keep a pool thread for
        // the whole run. Ten of them on a small CI machine leave no thread to run the token's timer or the
        // Task.Delay below, and the test then waits for the pool to grow instead of testing the gate.
        var end = Environment.TickCount64 + 2000;
        var work = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && Environment.TickCount64 < end)
            {
                try { await reach.AllowAsync(stop.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { faults.Add(ex); }
                await Task.Yield();
            }
        })).ToList();

        work.Add(Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && Environment.TickCount64 < end)
            {
                try { reach.Doubt(); await Task.Delay(5, stop.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { faults.Add(ex); }
            }
        }));

        work.Add(Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested && Environment.TickCount64 < end)
            {
                try { reach.AddressesChanged(); await Task.Delay(7, stop.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { faults.Add(ex); }
            }
        }));

        await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Empty(faults);
        // Two seconds of doubting every 5 ms and changing addresses every 7 ms is a few hundred
        // events. Ten callers calling AllowAsync in a loop throughout is tens of thousands of calls.
        // The probe count must follow the first number.
        Assert.InRange(asks, 1, 3000);
    }

}

/// <summary>
/// The gate as an <see cref="HttpClient"/> handler: what a request does when there is no network,
/// and what it tells the gate when it fails.
/// </summary>
public class OnlineGateHandlerTests
{
    private sealed class Counting : HttpMessageHandler
    {
        public int Sent;
        public Func<HttpRequestMessage, HttpResponseMessage> Answer =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(Answer(request));
        }
    }

    private static NetworkReach Reachable(bool online) =>
        new(hasNetwork: () => true, ask: _ => Task.FromResult(online), now: () => DateTimeOffset.UtcNow);

    [Fact]
    public async Task WithNothingOutThereNoSocketIsOpened()
    {
        var inner = new Counting();
        using var client = new HttpClient(new OnlineGateHandler(Reachable(online: false), inner));

        using var response = await client.GetAsync("https://api.rpmrpmrpm.com/cloud/list");

        Assert.Equal(0, inner.Sent);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(OnlineGateHandler.WasRefused(response));
    }

    [Fact]
    public async Task WithALineOutTheRequestGoesOut()
    {
        var inner = new Counting();
        using var client = new HttpClient(new OnlineGateHandler(Reachable(online: true), inner));

        using var response = await client.GetAsync("https://api.rpmrpmrpm.com/cloud/list");

        Assert.Equal(1, inner.Sent);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(OnlineGateHandler.WasRefused(response));
    }

    /// <summary>The service's own 503 is not the gate's, and must not be read as one: the gate's
    /// means "we did not ask", the service's is an answer.</summary>
    [Fact]
    public async Task AServicesOwnRefusalIsNotTheGates()
    {
        var inner = new Counting { Answer = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var client = new HttpClient(new OnlineGateHandler(Reachable(online: true), inner));

        using var response = await client.GetAsync("https://api.rpmrpmrpm.com/cloud/list");

        Assert.False(OnlineGateHandler.WasRefused(response));
    }

    /// <summary>
    /// A request that failed the way a lost line fails discards the held answer AND starts the
    /// measurement itself, so the next caller finds an answer ready instead of waiting at the gate
    /// for the whole probe, using up its own timeout.
    /// </summary>
    [Fact]
    public async Task ALostLineTellsTheGateToLookAgainAtOnce()
    {
        var asks = 0;
        var reach = new NetworkReach(
            hasNetwork: () => true,
            ask: _ => { Interlocked.Increment(ref asks); return Task.FromResult(true); },
            now: () => DateTimeOffset.UtcNow);

        // Throws once (the line is down), and then the line comes back.
        var sends = 0;
        var inner = new Counting
        {
            Answer = _ => Interlocked.Increment(ref sends) == 1
                ? throw new HttpRequestException(HttpRequestError.ConnectionError, "no route to host")
                : new HttpResponseMessage(HttpStatusCode.OK),
        };
        using var client = new HttpClient(new OnlineGateHandler(reach, inner));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("https://api.rpmrpmrpm.com/cloud/list"));

        // Nobody has requested anything since. The measurement happens anyway.
        await NetworkReachTests.Until(() => Volatile.Read(ref asks) == 2, "the doubt to measure");

        // The next request does not measure again: it finds the answer the doubt already got, and
        // goes straight out.
        using var response = await client.GetAsync("https://api.rpmrpmrpm.com/cloud/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Volatile.Read(ref asks));
        Assert.Equal(2, sends);
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no route to host");
    }
}

/// <summary>
/// What counts as being on a network. This separates "only what is on this disk" from "this disk
/// and the shared libraries on your network".
/// </summary>
public class LocalNetworkTests
{
    private static IPAddress[] Addresses(params string[] text) => [.. text.Select(IPAddress.Parse)];

    [Fact]
    public void AnAddressSomebodyCouldSendToIsANetwork()
    {
        Assert.True(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Wireless80211, Addresses("192.168.1.24", "fe80::1")));

        Assert.True(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Ethernet, Addresses("2a02:a03f::9")));
    }

    /// <summary>The main case: a Mac with the wifi switched off still has AWDL and a Thunderbolt
    /// bridge up, and both have only a link-local address.</summary>
    [Fact]
    public void AnInterfaceWithOnlyALinkLocalAddressIsNotANetwork()
    {
        Assert.False(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Ethernet, Addresses("fe80::aede:48ff:fe00:1122")));
    }

    /// <summary>A 169.254 address means the DHCP request got no answer: a cable is plugged in but
    /// there is no network.</summary>
    [Fact]
    public void AnAddressAMachineGaveItselfIsNotANetwork()
    {
        Assert.False(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Ethernet, Addresses("169.254.13.7")));
    }

    [Fact]
    public void LoopbacksTunnelsAndDownInterfacesAreNotNetworks()
    {
        Assert.False(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Loopback, Addresses("127.0.0.1")));

        Assert.False(LocalNetwork.CarriesALan(
            isUp: true, NetworkInterfaceType.Tunnel, Addresses("10.8.0.2")));

        Assert.False(LocalNetwork.CarriesALan(
            isUp: false, NetworkInterfaceType.Wireless80211, Addresses("192.168.1.24")));
    }

    [Fact]
    public void AnInterfaceWithNoAddressAtAllIsNotANetwork()
    {
        Assert.False(LocalNetwork.CarriesALan(isUp: true, NetworkInterfaceType.Ethernet, []));
    }
}

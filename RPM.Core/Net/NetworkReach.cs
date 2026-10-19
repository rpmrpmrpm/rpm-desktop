using System.Net.NetworkInformation;

namespace RPM.Core.Net;

/// <summary>
/// The one place the app asks whether there is anything out there, and the door every other attempt goes
/// through.
///
/// <para>Without this class, every service that needed the network found out for itself, at the same
/// moment, by trying. A laptop with the wifi off caused a name lookup, a socket failure and three retries
/// in each of these: the cloud queue, the library database, the fingerprint pass, the loudness pool, the
/// artwork fetch, the supporter check and the token refresh. All of this happened within the first second.
/// The debugger stopped on each failure, so the app could not be run under one.</para>
///
/// <para>So the question is asked once. <see cref="AllowAsync"/> gives the answer. It is shared by everyone
/// who asks within a few seconds, and <see cref="OnlineGateHandler"/> checks it before any socket is
/// opened. A pass that loops over four hundred tracks asks once at the top, and then does its work without
/// asking again.</para>
///
/// <para>Three answers, not two. See <see cref="Reach"/>. The middle one matters: a machine on a network
/// with no line out still has every shared library on that network, and saying "offline" would hide
/// them.</para>
///
/// <para>Events here can arrive in any order: address changes, probe results, and callers asking at the
/// same time. Each comment below states the ordering it depends on. Keep those rules when changing this
/// class.</para>
/// </summary>
public sealed class NetworkReach : IDisposable
{
    /// <summary>How long an answer of "online" stands before it is worth asking again. Long, because while
    /// it holds every call goes straight out and the cost of being wrong is one failed request that
    /// <see cref="Doubt"/> then acts on.</summary>
    private static readonly TimeSpan HoldOnline = TimeSpan.FromSeconds(30);

    /// <summary>How long an answer of "not online" stands. Short, because this one is what the person is
    /// waiting to see change.</summary>
    private static readonly TimeSpan HoldAway = TimeSpan.FromSeconds(10);

    /// <summary>How often the watchdog measures again while there is nothing out there. Nobody calls
    /// <see cref="AllowAsync"/> in that state, because the passes have stopped. Without this, the offline
    /// band would stay up until something asked.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The limit on one probe request. It is in front of a person watching a band, not a batch job, and a
    /// network that is going to answer answers well inside this.
    /// </summary>
    /// <remarks>
    /// This number and <see cref="ProbeBudget"/> come out of a caller's own
    /// <see cref="HttpClient.Timeout"/>. A request that arrives while nothing is measured waits at the
    /// gate, on the caller's token. The tightest client in the app has an eight-second timeout (the lyrics
    /// and the generated covers). A whole measurement must take much less than that, or the gate cancels
    /// requests it was built to let through. Hence a budget of four seconds, and probes that run together
    /// rather than one after another.
    /// </remarks>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The limit on a whole measurement, however many services it asks.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(4);

    private readonly Func<bool> _hasNetwork;
    private readonly Func<CancellationToken, Task<bool>> _ask;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();

    private Reach _reach = Reach.Online;
    private DateTimeOffset? _measuredAt;
    private Task<Reach>? _flight;
    private Timer? _watchdog;
    private IDisposable? _owned;
    private bool _listening;
    private bool _disposed;

    /// <summary>
    /// Which world the current measurement was started against. Bumped whenever this machine's addresses
    /// change, which is the one event that can make an answer wrong while it is still being worked out.
    /// </summary>
    private int _epoch;

    /// <summary>The epoch <see cref="_flight"/> was started in.</summary>
    private int _flightEpoch;

    /// <param name="hasNetwork">Whether any interface other than loopback is up. Cheap, and asked on every
    /// measurement, so that a cable going in is noticed.</param>
    /// <param name="ask">The probe: true when something outside the LAN answered. Any answer counts, even a
    /// refusal. A 403 from the service means the internet is working.</param>
    /// <param name="now">The clock, for tests.</param>
    public NetworkReach(
        Func<bool> hasNetwork,
        Func<CancellationToken, Task<bool>> ask,
        Func<DateTimeOffset> now)
    {
        _hasNetwork = hasNetwork;
        _ask = ask;
        _now = now;
    }

    /// <summary>The last reading. <see cref="Reach.Online"/> until the first measurement lands, which is
    /// the right way round: an app that has not looked yet must not put a band up saying the user is
    /// offline.</summary>
    public Reach Now
    {
        get { lock (_gate) return _reach; }
    }

    /// <summary>Whether anything has actually been measured yet, as against assumed.</summary>
    public bool Settled
    {
        get { lock (_gate) return _measuredAt is not null; }
    }

    /// <summary>Raised when the reading changes, on whichever thread the measurement finished on. A window
    /// listening to this marshals it itself.</summary>
    /// <remarks>A listener that throws is caught, and the others are still told. See <see cref="Announce"/>
    /// for why each listener has its own catch.</remarks>
    public event Action<Reach>? Changed;

    /// <summary>
    /// The door. True when it is worth going out, false when the caller should return what it has and not
    /// open a socket.
    ///
    /// <para>While an answer is fresh, this is cheap. When it is not, everyone asking at once waits for the
    /// one probe. A caller that gives up does not cancel that probe for the others.</para>
    /// </summary>
    public async ValueTask<bool> AllowAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_measuredAt is { } at && _now() - at < (_reach == Reach.Online ? HoldOnline : HoldAway))
                return _reach == Reach.Online;
        }

        return await RefreshAsync().WaitAsync(ct).ConfigureAwait(false) == Reach.Online;
    }

    /// <summary>
    /// Looks again now, whatever was last read. One measurement is in flight at a time and every caller
    /// gets that one.
    /// </summary>
    /// <remarks>
    /// <para>Takes no cancellation token on purpose. The flight belongs to all of its callers, so none of
    /// them may cancel it. A caller that wants to stop waiting uses its own token. See
    /// <see cref="AllowAsync"/>.</para>
    /// <para>A flight started before the machine's addresses changed is not handed out again. It measures a
    /// network that no longer exists. The point of noticing a cable going in is to stop answering as if it
    /// had not gone.</para>
    /// </remarks>
    public Task<Reach> RefreshAsync()
    {
        lock (_gate)
        {
            if (_flight is { IsCompleted: false } flight && _flightEpoch == _epoch)
                return flight;

            var epoch = _epoch;
            _flightEpoch = epoch;

            // It runs on a thread of its own, not inline, because this call holds the gate. A measurement
            // can finish without awaiting anything. "No interface is up" is one such case. Run inline, it
            // would walk the interface list and call every listener while holding the lock that every
            // request in the app waits on.
            return _flight = Task.Run(() => MeasureAsync(epoch));
        }
    }

    /// <summary>
    /// Called by a caller whose request failed the way a lost line fails. This does not lower the reading.
    /// One failed request is not a verdict, and a service that is briefly unwell does not mean the user is
    /// offline. It retires the held answer and starts a measurement.
    /// </summary>
    /// <remarks>
    /// It starts the measurement here, rather than leaving it to the next caller. If the answer were only
    /// retired, the next request would run the whole measurement inline, using its own HttpClient.Timeout,
    /// and it would send nothing. Starting it here means the probe runs alongside the failures. By the time
    /// the next request asks, an answer is usually ready.
    /// </remarks>
    public void Doubt()
    {
        bool look;
        lock (_gate)
        {
            look = _reach == Reach.Online && _measuredAt is not null;
            if (look)
                _measuredAt = null;
        }

        if (look)
            LookAgain();
    }

    /// <summary>
    /// Starts looking on its own: every <see cref="WatchEvery"/> while there is nothing out there, and
    /// immediately whenever the machine's addresses change.
    ///
    /// <para>Not done in the constructor, so that a test can hold one of these without a timer going off in
    /// it. The app calls this once, at startup.</para>
    /// </summary>
    public void StartWatching()
    {
        lock (_gate)
        {
            if (_disposed || _watchdog is not null)
                return;

            _watchdog = new Timer(_ =>
            {
                if (Now != Reach.Online)
                    LookAgain();
            }, null, WatchEvery, WatchEvery);
        }

        // A cable plugged in, wifi joined, or a VPN started. This is guarded because support differs by
        // platform. If the platform cannot raise this event, the timer above still covers the same cases,
        // just more slowly.
        try
        {
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            _listening = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        LookAgain();
    }

    /// <summary>Hands a resource to <see cref="Dispose"/>, so it is released with this object. Watching
    /// uses it for the probe client, which would otherwise outlive this object.</summary>
    internal void Owns(IDisposable resource)
    {
        bool late;
        lock (_gate)
        {
            late = _disposed;
            if (!late)
                _owned = resource;
        }

        if (late)
            resource.Dispose();
    }

    private void OnAddressChanged(object? sender, EventArgs e) => AddressesChanged();

    /// <summary>
    /// Called when this machine's addresses change: a cable plugged in, wifi joined, or a VPN started. It
    /// retires the held answer and measures again at once.
    ///
    /// <para>A measurement that is already running was started against the old addresses. So it is not
    /// handed to anyone who asks after this point, and when it finishes, it does not mark its answer as
    /// fresh. Without this, a cable pulled while a probe was running would be answered "online", and that
    /// answer would stand for the next thirty seconds.</para>
    /// </summary>
    public void AddressesChanged()
    {
        lock (_gate)
        {
            _measuredAt = null;
            _epoch++;
        }

        LookAgain();
    }

    /// <summary>A probe that ran past its budget. Nobody waits for it now, but it may still fail. A failure
    /// that nobody observes is raised later on the finalizer thread, so this observes it.</summary>
    private static void Unwatched(Task running) =>
        _ = running.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>A measurement that nobody waits for. Any fault is observed here, so that it is not left to
    /// the finalizer thread. See <see cref="Announce"/>, which catches listener faults.</summary>
    private void LookAgain()
    {
        _ = RefreshAsync().ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<Reach> MeasureAsync(int epoch)
    {
        bool has;
        try { has = _hasNetwork(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { has = true; }

        // If no interface is up, that is answer enough. Asking anyway would cause the name lookup this
        // class exists to stop. The check is cheap and repeated, so a cable plugged in is found on the next
        // measurement.
        bool answered = false;
        if (has)
        {
            using var budget = new CancellationTokenSource(ProbeBudget);
            using var giveUpOnTheClock = new CancellationTokenSource();
            try
            {
                // The probe is raced against the budget. It is not only given a token that expires.
                //
                // Every request in the app waits on this flight. AllowAsync has no other answer to give, so
                // while a measurement runs, the gate is closed to every caller. The only thing that frees a
                // caller is its own HttpClient.Timeout. A probe that ignores its token, such as a delegate
                // installed by a platform head, or a client with no timeout set, could hold the whole app
                // for as long as it liked. Each caller would then time out at the gate, having sent
                // nothing. So the bound is enforced here, not requested. Whatever the probe does, this
                // returns within ProbeBudget.
                var asking = _ask(budget.Token);
                var expired = Task.Delay(ProbeBudget, giveUpOnTheClock.Token);

                if (await Task.WhenAny(asking, expired).ConfigureAwait(false) == asking)
                    answered = await asking.ConfigureAwait(false);
                else
                    Unwatched(asking);
            }
            // This catches every exception, including the probe's own timeout. A measurement that throws
            // counts as "no". This is the one place where the app is allowed to catch everything.
            catch
            {
                answered = false;
            }
            finally
            {
                giveUpOnTheClock.Cancel();
            }
        }

        var reach = ReachReading.For(has, answered);

        bool changed;
        lock (_gate)
        {
            // The addresses changed while this measurement was running. The reading still goes back to
            // whoever is waiting. It was true a moment ago, and it is the best answer available. But
            // nothing is stored, so the next caller measures the network as it now is, and does not reuse
            // this answer for thirty seconds.
            if (_epoch != epoch)
                return reach;

            changed = _reach != reach;
            _reach = reach;
            _measuredAt = _now();
        }

        if (changed)
            Announce(reach);

        return reach;
    }

    /// <summary>
    /// Tells the listeners, one at a time, each behind its own catch.
    ///
    /// <para>Each listener has its own catch. If the event were raised with a bare <c>Changed?.Invoke</c>,
    /// a listener that threw would fault the measurement that every caller is waiting on. The exception
    /// would then reach every caller of <see cref="HttpClient.SendAsync"/>, as the listener's own exception
    /// type and not as an <see cref="HttpRequestException"/>. One listener that resolves a service from a
    /// host that is shutting down is enough to cause this.</para>
    ///
    /// <para>The listeners are called one at a time, for the same reason. A listener that throws must not
    /// stop the listeners after it from getting the news.</para>
    /// </summary>
    private void Announce(Reach reach)
    {
        if (Changed is not { } listeners)
            return;

        foreach (var listener in listeners.GetInvocationList())
        {
            try
            {
                ((Action<Reach>)listener)(reach);
            }
            catch
            {
                // This includes cancellation. A listener that gave up does not need this news, so nothing
                // is passed on.
            }
        }
    }

    public void Dispose()
    {
        Timer? watchdog;
        IDisposable? owned;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watchdog = _watchdog;
            _watchdog = null;
            owned = _owned;
            _owned = null;
        }

        watchdog?.Dispose();
        owned?.Dispose();
        if (_listening)
        {
            try { NetworkChange.NetworkAddressChanged -= OnAddressChanged; }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            _listening = false;
        }
    }

    // ---- the app's own ----------------------------------------------------------------------

    private static NetworkReach? _shared;
    private static readonly object SharedGate = new();

    /// <summary>
    /// The app's gate. Every client the app builds asks this one, including the ones built from static
    /// fields that no container reaches.
    ///
    /// <para>Nobody needs to install one. The first read creates the real gate, which measures against the
    /// production service. So a head that does not set up a gate still works. A test that builds a cached
    /// client gets a gate that measures, not a missing gate.</para>
    /// </summary>
    public static NetworkReach Shared
    {
        get
        {
            lock (SharedGate)
                return _shared ??= Watching(Probes);
        }
    }

    /// <summary>
    /// What the probe asks. Both are asked at once. See <see cref="Watching"/>.
    ///
    /// <para>There are two probes, and the second is the point. If the probe asked only rpm's own service,
    /// an hour of that service being unwell would look like "no internet" on every machine. The catalogues,
    /// the artwork and the stations would then be treated as offline too.</para>
    ///
    /// <para>The second probe is Wikipedia. The app really does read Wikipedia from the machine, in the
    /// reader's own language. No head except the API may reach a catalogue (see
    /// <see cref="RPM.Fingerprint.External.CatalogueReach"/>). So a probe of a catalogue would send
    /// requests from every machine, on a loop, for an answer that nobody reads.</para>
    /// </summary>
    private static readonly string[] Probes = ["https://api.rpmrpmrpm.com/", "https://www.wikipedia.org/"];

    /// <summary>
    /// Makes a reading against the given services, and starts watching. The probe client is kept plain: no
    /// cache, no retries and no gate, because the gate depends on its results. A gate on the probe would
    /// call itself.
    /// </summary>
    /// <remarks>
    /// The probes are <b>raced, not asked one after another</b>. If they ran in turn, a network that
    /// accepts connections but never answers would cost one timeout per service. That total would come out
    /// of the timeout of whichever request was waiting at the gate. For the tightest clients, that is the
    /// whole timeout, spent without sending a byte. Raced, a measurement costs one timeout however many
    /// services it asks. The first answer wins. The others are left to finish, not cancelled.
    /// </remarks>
    public static NetworkReach Watching(params string[] probeUrls)
    {
        var probe = new HttpClient { Timeout = ProbeTimeout };

        var reach = new NetworkReach(
            hasNetwork: LocalNetwork.IsPresent,
            ask: ct => AnybodyAnswersAsync(probe, probeUrls, ct),
            now: () => DateTimeOffset.UtcNow);

        reach.Owns(probe);
        reach.StartWatching();
        return reach;
    }

    /// <summary>True as soon as any one of them answers.</summary>
    private static async Task<bool> AnybodyAnswersAsync(
        HttpClient probe, string[] probeUrls, CancellationToken ct)
    {
        // THE LOSERS ARE LEFT TO FINISH, not cancelled.
        //
        // Cancelling them would cost a first-chance TaskCanceledException for each probe round, every
        // round. That is a throw by design. Nothing breaks, because AnswersAsync catches it. But a debugger
        // is told about every throw, whether or not anything catches it, and each one stops the process.
        // Every throw that is removed makes the app easier to debug.
        //
        // A probe that is left alone against a service that is up simply succeeds a moment later, and
        // throws nothing. A throw happens only when a service is really unreachable, and that is the case
        // the reading is about.
        //
        // Nothing waits for them. AnswersAsync never throws, so no task is left unobserved. There is also
        // no linked token source that could be disposed while a probe still uses it.
        var asking = probeUrls.Select(url => AnswersAsync(probe, url, ct)).ToList();

        while (asking.Count > 0)
        {
            var done = await Task.WhenAny(asking).ConfigureAwait(false);
            asking.Remove(done);

            if (done.Result)
                return true;
        }

        return false;
    }

    /// <summary>One service, asked. Never throws: a probe that failed is a probe that said no.</summary>
    private static async Task<bool> AnswersAsync(HttpClient probe, string url, CancellationToken ct)
    {
        try
        {
            using var res = await probe.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Any answer means the line is up. A 403 or 404 from the service means the internet is working.
            // Only a failure to reach the service at all means it is not.
            return true;
        }
        catch
        {
            return false;
        }
    }
}

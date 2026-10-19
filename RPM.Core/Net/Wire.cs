namespace RPM.Core.Net;

/// <summary>
/// How a client that reaches outside this network is built. Every such client is built here, so the gate is
/// fitted in one place, and no client can skip it by being written the ordinary way.
///
/// <para>Clients that only talk to this LAN must not be built here. A peer's library can be reached on a
/// network with no line out, and putting it behind the gate would take shared libraries away from the
/// people who still have them. <see cref="RPM.Core.Sharing.PeerClient"/> builds its own client from
/// <see cref="LocalSockets"/>, which is ungated. A door that reaches the internet must be asked for by
/// name.</para>
/// </summary>
public static class Wire
{
    /// <summary>
    /// How many connections are kept open to one host. The framework default is int.MaxValue, so a
    /// SocketsHttpHandler opens a connection for every request it is given. A wall that asks for a
    /// screenful of pictures at once would open forty.
    ///
    /// <para>The cost is not bandwidth. Each pooled TLS connection has a read waiting on it, so the pool
    /// notices when the far end goes away. When the pool retires a connection, that read is aborted, and
    /// the abort shows up as an <see cref="IOException"/> from inside SslStream. The debugger logs each
    /// one, even though nothing has failed. A retired connection causes about three of these, whatever the
    /// timeout. So the number of these exceptions equals the number of connections opened, which is why
    /// this limit matters.</para>
    ///
    /// <para>Eight is more parallelism than any screen in this app can use. It is also few enough that a
    /// burst reuses connections instead of opening one per request.</para>
    /// </summary>
    private const int PerHost = 8;

    /// <summary>How long an unused connection stays in the pool before it is closed. Two minutes is long
    /// enough to cover a person reading one screen and moving to the next. Each closed connection causes
    /// the <see cref="IOException"/> described on <c>PerHost</c>, so a longer idle time means fewer of
    /// them.</summary>
    private static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A socket handler with the connection budget set. It is the ONLY way one is made in this app.
    /// </summary>
    /// <remarks>
    /// <b>Private, on purpose.</b> If it were public, other code could build clients straight from it with
    /// no gate in front. That is how public services were reached without the gate, the artwork fetcher
    /// among them. Reaching outside is asked for by name instead: <see cref="GatedSockets"/>,
    /// <see cref="GatedStream"/>, <see cref="Outside"/> and <see cref="OutsideRetrying"/>. Staying on this
    /// LAN is <see cref="LocalSockets"/>, whose name says that it is ungated.
    /// </remarks>
    /// <summary>How much longer than the request's own allowance the client waits before giving up on it
    /// entirely. Room for the queue and the measurement, and no more.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(60);

    private static SocketsHttpHandler Budget(
        System.Net.Security.SslClientAuthenticationOptions? ssl = null,
        TimeSpan? connectTimeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = PerHost,
            PooledConnectionIdleTimeout = IdleFor,
        };

        if (ssl is not null)
            handler.SslOptions = ssl;

        if (connectTimeout is { } wait)
            handler.ConnectTimeout = wait;

        return handler;
    }

    /// <summary>
    /// A connection to this LAN and nowhere else. It has the connection budget but no gate. The name says
    /// "Local" so that it is clearly the exception. The app should not gain a third client that reaches the
    /// internet without the gate.
    /// </summary>
    /// <param name="ssl">How certificates are checked. A peer pins its own certificate, which is why this
    /// method exists. See <see cref="RPM.Core.Sharing.PeerCertificate"/>.</param>
    /// <param name="connectTimeout">How long a machine on this network gets to answer a socket.</param>
    /// <remarks>Ungated on purpose. A peer's library can be reached on a network whose line is down.
    /// Putting it behind the gate would take shared libraries away from the people who still have
    /// them.</remarks>
    public static SocketsHttpHandler LocalSockets(
        System.Net.Security.SslClientAuthenticationOptions? ssl = null,
        TimeSpan? connectTimeout = null) => Budget(ssl, connectTimeout);

    /// <summary>The gate, around a pipeline built elsewhere, such as a cache above the sockets. Fit it
    /// outermost, so that a refusal never reaches the retries.</summary>
    /// <param name="afterAdmission">How long the request itself gets, timed from the moment the gate lets
    /// it through. Omitted, the timing is left to <see cref="HttpClient.Timeout"/>, which counts the queue
    /// as well as the request. See the note in <see cref="OnlineGateHandler"/>.</param>
    public static HttpMessageHandler Gate(HttpMessageHandler inner, TimeSpan? afterAdmission = null) =>
        new CatalogueGuardHandler(new OnlineGateHandler(() => NetworkReach.Shared, inner, afterAdmission));

    /// <summary>The gate over a plain budgeted socket handler: what a caller that only wants "a way out"
    /// should ask for, so that neither the gate nor the budget can be forgotten.</summary>
    public static HttpMessageHandler GatedSockets() => Gate(Budget());

    /// <summary>
    /// A way out for a caller that keeps one response open for hours, such as an internet radio stream. It
    /// is gated and budgeted like the other clients. It differs in its pooling: a live stream never
    /// completes, and the pool would otherwise close the connection in the middle of a song.
    /// </summary>
    /// <param name="connectTimeout">Long enough for a slow mirror to answer, short enough that a dead
    /// station falls through to the next reconnect rather than hanging the tune.</param>
    public static HttpMessageHandler GatedStream(TimeSpan connectTimeout)
    {
        var handler = Budget(connectTimeout: connectTimeout);
        handler.AllowAutoRedirect = true;
        handler.MaxAutomaticRedirections = 5;
        handler.PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan;
        return Gate(handler);
    }

    /// <summary>A client for a service outside the LAN. Gated, and nothing else: one attempt, and the
    /// caller's own handling of what came back.</summary>
    /// <param name="where">Which service, as a <see cref="Destination"/>. A plain string would let a caller
    /// aim a client at this app's own API without the key.</param>
    public static HttpClient Outside(Destination where, TimeSpan timeout) =>
        Build(where, timeout, Gate(Budget(), timeout));

    /// <summary>The same, but it also retries the answers that are not real answers. See
    /// <see cref="TransientRetryHandler"/>. This is for the app's own service, whose edge has several ways
    /// of saying "not now".</summary>
    public static HttpClient OutsideRetrying(Destination where, TimeSpan timeout) =>
        Build(where, timeout, Gate(new TransientRetryHandler(Budget()), timeout));

    /// <remarks>
    /// <b>The client's own timeout is a backstop, not the real timing.</b> The gate times each request from
    /// the moment it is admitted. This timeout is longer than the caller's figure, so it does not cancel
    /// requests that are only waiting in a queue. It is not infinite, so that a request lost somewhere the
    /// gate cannot see still ends.
    /// </remarks>
    private static HttpClient Build(Destination where, TimeSpan timeout, HttpMessageHandler pipeline)
    {
        var client = new HttpClient(pipeline) { Timeout = timeout + Slack };
        where.Aim(client);
        return client;
    }
}

/// <summary>
/// Refuses a request to a music catalogue from any process that is not the RPM API.
///
/// <para>It sits outside the online gate, so a refused request is refused at once. It is not queued behind
/// a network that may or may not be there. The answer does not depend on the network, and waiting for one
/// would only make the mistake slower to find.</para>
///
/// <para>It is fitted here because every client that leaves the network is built here. A URL built by hand
/// and given to one of these clients is checked the same way as a catalogue client's URL. That is the case
/// that matters most: this rule exists for hand-built URLs.
/// <see cref="RPM.Fingerprint.External.CatalogueReach"/> lists the allowed hosts, and why.</para>
/// </summary>
internal sealed class CatalogueGuardHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is { } address)
            RPM.Fingerprint.External.CatalogueReach.Check(address);

        return base.SendAsync(request, ct);
    }
}

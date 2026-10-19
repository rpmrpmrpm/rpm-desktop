using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace RPM.Core.Net;

/// <summary>
/// Three separate questions. The middle one is why there are three.
///
/// <list type="number">
/// <item><description><b>May this request be sent again?</b> <see cref="Replay"/>, and it is the only door
/// to that answer.</description></item>
/// <item><description><b>Is this failure the line rather than the service on it?</b>
/// <see cref="LooksLikeTheLine"/>, which <see cref="NetworkReach.Doubt"/> acts on.</description></item>
/// <item><description><b>How long before asking again?</b> <see cref="Backoff"/>.</description></item>
/// </list>
///
/// <para>One question for all three would give wrong answers. A 502 would count as a dead line, but the
/// line carried the 502 perfectly well. A 500 would count as grounds to send a <c>POST</c> a second time.
/// See <see cref="Replay"/> for that second case.</para>
///
/// <para>The iOS app asks the same three questions in its own code. Where the two differ, one of them has a
/// fault. The platform is not the cause.</para>
/// </summary>
public static class TransientHttp
{
    /// <summary>
    /// Whether THIS request may be sent again, and on what came back.
    ///
    /// <para><b>This is the only door to that answer.</b> It decides the correctness of
    /// <see cref="TransientRetryHandler"/>. A check on the body size alone is not enough, because the
    /// request itself matters. For example, a <c>POST</c> to <c>albums/{id}/comments</c> that gets a 500
    /// means the service received the comment and failed afterwards. Sending it again would post the
    /// comment twice. The same holds for <c>POST albums/{id}/plays</c>, which increments a counter.</para>
    ///
    /// <para>Two facts decide it, and both are needed: whether a body is still in hand to send, and whether
    /// sending this request twice is the same as sending it once. Neither has a safe default, so neither is
    /// optional. <see cref="Of"/> takes both.</para>
    /// </summary>
    public readonly struct Replay
    {
        private readonly bool _bodyInHand;
        private readonly bool _harmlessTwice;

        private Replay(bool bodyInHand, bool harmlessTwice)
        {
            _bodyInHand = bodyInHand;
            _harmlessTwice = harmlessTwice;
        }

        /// <summary>Sent once, whatever comes back. A body too large to hold in memory gets this, and so
        /// does a caller that wants one attempt and no retries.</summary>
        public static readonly Replay Never = new(bodyInHand: false, harmlessTwice: false);

        /// <param name="method">The request's own method. It is asked for, not guessed, because the method
        /// decides whether a retry is safe. A retry of a POST could add a second comment to an
        /// album.</param>
        /// <param name="bodyInHand">True when there is nothing to send, or when the body is held in memory
        /// and can be sent again. False when it was too large to hold, or its length was never
        /// stated.</param>
        public static Replay Of(HttpMethod method, bool bodyInHand) =>
            new(bodyInHand, HarmlessTwice(method));

        /// <summary>Whether it is worth asking again after this answer.</summary>
        /// <remarks>
        /// A status in <see cref="NeverArrived"/> means the request did not get through, so a retry is safe
        /// for any method. A 500 means the service received the request and failed, so a retry is safe only
        /// for a request that may be sent twice.
        /// </remarks>
        public bool After(HttpStatusCode status)
        {
            if (!_bodyInHand)
                return false;

            if (NeverArrived(status))
                return true;

            return ItThrew(status) && _harmlessTwice;
        }

        /// <summary>Whether it is worth asking again after this failure.</summary>
        /// <remarks>
        /// A failure with no answer does not show whether the request ran. The exception is when the
        /// framework knows the request never left this machine: a name that did not resolve, a connection
        /// that was never made, or a handshake that failed. Those are safe for any method. Anything else is
        /// safe only for a request that may be sent twice.
        /// </remarks>
        public bool After(Exception failure)
        {
            if (!_bodyInHand)
                return false;

            // The status arrived as an exception. EnsureSuccessStatusCode throws the status instead of
            // returning it. A 503 is still a 503, however it reaches this method. Treating it as a failure
            // with no answer would refuse to retry a POST that the edge has just said it never delivered.
            if (failure is HttpRequestException { StatusCode: { } status })
                return After(status);

            if (!PassesOnItsOwn(failure))
                return false;

            return _harmlessTwice || NeverLeft(failure);
        }
    }

    /// <summary>
    /// Whether a failure is the <b>line</b> rather than the service at the other end of it. What
    /// <see cref="NetworkReach.Doubt"/> acts on, and a narrower question than
    /// <see cref="Replay.After(Exception)"/>.
    ///
    /// <para>A 502, a 503 and a refusal are the service talking, and the line carried them perfectly well.
    /// Treating them as evidence of a dead line would make one unwell server look like no internet on every
    /// machine. What counts is a name that does not resolve, a connection that is not made, a socket that
    /// goes away, and a request that runs out of time.</para>
    /// </summary>
    public static bool LooksLikeTheLine(Exception failure) => failure switch
    {
        // A send that got an answer, whatever the answer was, is a line that is carrying.
        HttpRequestException { StatusCode: not null } => false,
        HttpRequestException http => http.HttpRequestError is
            HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError
            or HttpRequestError.ProxyTunnelError
            or HttpRequestError.Unknown,
        // HttpClient's own timeout arrives as a cancellation nobody asked for.
        TaskCanceledException canceled => canceled.InnerException is TimeoutException,
        TimeoutException => true,
        SocketException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>
    /// How long to wait after the attempt numbered <paramref name="attempt"/> failed. One-based.
    ///
    /// <para>Doubles per attempt, and <b>half of each wait is random</b>. The random half matters. If ten
    /// threads fail together on one endpoint and all wait the same time, they all retry at the same
    /// instant, and this can repeat up to three times. A fixed jitter of a quarter second spreads only the
    /// first round, so the random part grows with the wait instead. Attempt one falls somewhere in 200 to
    /// 400 ms, and attempt two in 400 to 800 ms. The ten requests no longer arrive together.</para>
    /// </summary>
    /// <param name="retryAfter">The server's own number, when it gave one. The server knows how busy it is
    /// better than this method does. It is not defaulted, so a caller that has a response in hand must pass
    /// it. The value is obeyed and then spread out. Otherwise ten callers told the same five seconds would
    /// all come back in the same millisecond.</param>
    public static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return Capped(delta + Spread(SpreadOnServersNumber));

        var doubled = FirstWait * Math.Pow(2, Math.Max(1, attempt) - 1);
        return Capped(doubled / 2 + Spread(doubled / 2));
    }

    /// <summary>Somewhere in <c>[0, width)</c>.</summary>
    private static TimeSpan Spread(TimeSpan width) =>
        TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * width.TotalMilliseconds);

    private static TimeSpan Capped(TimeSpan wait) => wait < LongestWait ? wait : LongestWait;

    /// <summary>How far apart a number the server gave everybody is spread.</summary>
    private static readonly TimeSpan SpreadOnServersNumber = TimeSpan.FromMilliseconds(500);

    /// <summary>The first wait, before the random half is taken out of it.</summary>
    private static readonly TimeSpan FirstWait = TimeSpan.FromMilliseconds(400);

    /// <summary>The longest this will sit on a request, whatever <c>Retry-After</c> says. Callers are
    /// background passes and windows waiting to draw, not batch jobs.</summary>
    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether this status means the service said "not now", rather than answering about what was sent. Use
    /// this to word a failure message. "The recognition service is unavailable" reads very differently from
    /// "that file could not be read". Use it for nothing else.
    /// </summary>
    /// <remarks>It is deliberately NOT the retry question. Whether to send it again depends on what the
    /// request was, and <see cref="Replay"/> is the only thing that answers that.</remarks>
    public static bool SaysNotNow(HttpStatusCode status) => NeverArrived(status) || ItThrew(status);

    /// <summary>The statuses that mean the request never reached the service. Safe to send again whatever
    /// it was.</summary>
    private static bool NeverArrived(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout            // 408
            or HttpStatusCode.TooManyRequests              // 429
            or HttpStatusCode.BadGateway                   // 502
            or HttpStatusCode.ServiceUnavailable           // 503
            or HttpStatusCode.GatewayTimeout;              // 504

    /// <summary>The service answered and threw. It got the request.</summary>
    private static bool ItThrew(HttpStatusCode status) => status is HttpStatusCode.InternalServerError;

    /// <summary>Whether sending this method twice has the same effect as sending it once. Only GET and HEAD
    /// count. Every other method, including DELETE and PUT, is not guessed at here. The caller
    /// decides.</summary>
    private static bool HarmlessTwice(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head;

    /// <summary>
    /// The failures that pass on their own, so a retry may help. Not the ones that need a change in the
    /// world, such as a network coming back. Those are <see cref="NetworkReach"/>'s job. A timeout is never
    /// retried, because it has already used the whole time limit, and a retry would use it again for the
    /// same result.
    /// </summary>
    /// <remarks>Never asked about a failure carrying a status: <see cref="Replay.After(Exception)"/> sends
    /// those to the status rule before it gets here.</remarks>
    private static bool PassesOnItsOwn(Exception failure) => failure switch
    {
        HttpRequestException => true,
        // A cancellation is either the caller leaving or the client's own clock. Neither is worth asking
        // again about, and the second one has no time left to ask in.
        OperationCanceledException => false,
        TimeoutException => false,
        SocketException => true,
        IOException => true,
        _ => false,
    };

    /// <summary>Whether the framework knows this failure happened before a byte left this machine. The only
    /// grounds on which a request that must not be sent twice is sent again.</summary>
    private static bool NeverLeft(Exception failure) =>
        failure is HttpRequestException
        {
            StatusCode: null,
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError,
        };

    /// <summary>An <see cref="HttpClient"/> for the rpm service, with the retries in place and, outside
    /// them, the gate that keeps it from trying at all when there is nothing out there. See
    /// <see cref="Wire"/>, which is where every client that leaves this network is built.</summary>
    public static HttpClient Client(Destination where, TimeSpan timeout) =>
        Wire.OutsideRetrying(where, timeout);
}

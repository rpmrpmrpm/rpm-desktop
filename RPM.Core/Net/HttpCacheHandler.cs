using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

using RPM.Core.Threading;

namespace RPM.Core.Net;

/// <summary>
/// Keeps the answers to the app's lookups, so the same question is not asked of the internet twice in a
/// month.
///
/// <para>What goes through here is interface data: the station directory, the artwork service, and the
/// encyclopedias behind the track pane. This data is slow to fetch. MusicBrainz answers one caller per
/// second and refuses the rest. It is also asked again on every launch, by panes that must open at once.
/// None of it changes on a timescale that a person notices. Anything that belongs to the user does not go
/// through here, such as the cloud copy of the library, a peer's library, or a stream. So the cache is
/// opt-in for each client, not used by every request.</para>
///
/// <para>Three behaviours are worth naming:</para>
/// <list type="bullet">
/// <item><description><b>One flight per question.</b> Four panes drawing the same album ask for its art at
/// the same moment; one request goes out and all four get the answer. Without this, the cache's first
/// benefit is lost in the burst that needs it most: not hitting a rate-limited
/// service.</description></item>
/// <item><description><b>A stale answer is renewed, not refetched.</b> Where the server sent an ETag, an
/// expired row costs a conditional request and a 304 rather than the body again.</description></item>
/// <item><description><b>A stale answer beats no answer.</b> If the service is unreachable or unwell, what
/// is remembered is served. A month-old station list is a working radio pane; an error is
/// not.</description></item>
/// </list>
/// </summary>
public sealed class HttpCacheHandler : DelegatingHandler
{
    /// <summary>The largest body held in memory to be cached. Above it the request is sent again with the
    /// cache out of the way, and the answer streams to the caller as it would without the cache.</summary>
    private const long MaxBodyBytes = 32L * 1024 * 1024;

    private readonly IWebCacheStore _store;
    private readonly WebCachePolicy? _default;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _flightTimeout;
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);

    /// <param name="store">Where answers are kept. This handler does not dispose it, because one store is
    /// shared by every client in the app.</param>
    /// <param name="defaultPolicy">Applied to every GET through this client that does not carry its own.
    /// Null leaves the client uncached unless a request asks.</param>
    /// <param name="inner">The rest of the pipeline. When not given, a gated handler with a connection
    /// limit is used, see <see cref="Wire.GatedSockets"/>. Never pass a bare handler, because it has no
    /// limit on the sockets it opens.</param>
    /// <param name="now">The clock, for tests.</param>
    /// <param name="flightTimeout">The longest a shared request may run. <see cref="HttpClient.Timeout"/>
    /// cannot do this job. It works by cancelling the token it passes down, and a request shared by several
    /// callers must not depend on any one caller's token. Pass the client's own timeout here. Otherwise, a
    /// request to a host that accepts the connection and then never answers runs until the process
    /// ends.</param>
    public HttpCacheHandler(
        IWebCacheStore store,
        WebCachePolicy? defaultPolicy = null,
        HttpMessageHandler? inner = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? flightTimeout = null)
        : base(inner ?? Wire.GatedSockets())
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _default = defaultPolicy;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _flightTimeout = flightTimeout ?? TimeSpan.FromSeconds(100);   // HttpClient's own default
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var policy = Policy(request);

        // Only a GET is a question with a cacheable answer.
        if (policy is null || request.Method != HttpMethod.Get)
            return await base.SendAsync(request, ct);

        var key = WebCache.KeyFor(request, policy);

        // The "look again" action skips what is remembered. The new answer still replaces the old row.
        // Otherwise the button would fetch fresh data, and the next caller would get the old row.
        var refresh = request.Options.TryGetValue(WebCache.BypassOption, out var bypass) && bypass;

        // THE LOOKUP IS A DISK READ, SO IT DOES NOT HAPPEN ON THE CALLER'S THREAD.
        //
        // Code before an async method's first await runs on the thread of whoever called it. This lookup
        // was before that point: a SQLite query and a blob read, behind a lock shared with every other
        // caller, including the callers that are WRITING blobs. A picture asked for from the drawing thread
        // did its lookup there, and it could wait there behind someone else's write. A stack trace taken
        // during a long UI stall showed the lookup on the drawing thread, inside WebCacheStore.Read.
        //
        // <b>The await resumes on the caller's context, on purpose.</b> Continuing on a worker thread would
        // be simpler, but it breaks the flight below. Ten callers asking at once reach the flight at
        // slightly different moments. If one arrives after a fast answer has landed, it starts a second
        // flight. Moving the hop to the top of the method turned "ten callers cost one request" into two
        // requests.
        //
        // ONLY THE THREAD THAT DRAWS PAYS FOR THIS. A context means a thread that belongs to someone. In
        // this app, the only such thread is the one that draws. It goes to the pool for the read and comes
        // back. Every other caller is already on a thread where the disk may be used, so it reads in place
        // and behaves as it did before. This matters, because the yield has a cost: the flight below
        // combines ten callers into one request only while they reach it without stopping. A caller that
        // yields on the way can arrive after a fast answer has landed, and ask again.
        //
        // So the drawing thread accepts a slightly wider window in exchange for never doing file IO.
        // Nothing else is traded.
        WebCacheEntry? entry;

        if (refresh)
        {
            entry = null;
        }
        else if (SynchronizationContext.Current is null)
        {
            entry = _store.Read(key);
        }
        else
        {
            entry = (await Cancellable.RunAsync(() => _store.Read(key), ct)).Value;
        }

        if (entry is { } found && _now().UtcTicks < found.ExpiresUtcTicks)
            return Rebuild(found, request, WebCacheSource.Hit);

        // One request per question, however many callers are asking it. The flight has a token of its own
        // so that the first caller walking away does not cancel the answer everybody else is waiting for;
        // each caller still observes its own token while waiting, and the last one to leave takes the
        // flight with it.
        var flight = Board(key, request, policy, refresh);

        Fetched fetched;
        try
        {
            fetched = await flight.Task.WaitAsync(ct);
        }
        catch
        {
            Leave(key, flight, abandoned: true);
            throw;
        }
        Leave(key, flight, abandoned: false);

        // A body too large to hold was never buffered. The flight sent a copy of the request, so the
        // caller's own request has not been sent yet. It goes now, with the cache out of the way.
        if (fetched.TooLarge)
        {
            request.Refresh();
            return await base.SendAsync(request, ct);
        }

        return fetched.ToResponse(request);
    }

    /// <summary>
    /// Joins the flight for this question, starting one if there is none.
    ///
    /// <para>The loop is for the narrow case where the flight found in the table was abandoned by its last
    /// caller between the lookup and the join. Its answer is never coming, so it is taken out of the table
    /// and the question is asked again.</para>
    /// </summary>
    private Flight Board(string key, HttpRequestMessage request, WebCachePolicy policy, bool refresh)
    {
        while (true)
        {
            // Built first and then offered to the table. GetOrAdd's factory would run once for each racing
            // caller, and only one result is kept. Ten callers on one key would make ten flights and throw
            // nine away. Each Flight starts a timer in its constructor, so the discarded flights would
            // leave nine live timers that nobody disposes.
            var mine = new Flight(_flightTimeout, token => Fly(key, request, policy, refresh, token));

            var flight = _flights.GetOrAdd(key, mine);
            if (ReferenceEquals(flight, mine))
                return flight;      // ours, and it is already counted as having one caller

            mine.Dispose();

            // Joining is refused by a flight whose last caller has already gone, which is what makes this
            // safe against the caller that is at this moment taking it off the table: either we get on
            // before it leaves, or we are told no and ask the question again.
            if (flight.Join() && !flight.Abandoned)
                return flight;

            _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
        }
    }

    /// <summary>
    /// Stops waiting for a flight, and cancels it if that was the last caller who wanted the answer. A
    /// request nobody is waiting for is a socket held open for nothing.
    /// </summary>
    /// <remarks>
    /// Only the last caller to leave takes the flight off the table. Removing it on every leave caused a
    /// bug. With ten callers waiting on one question, the first one to get its answer removed the flight
    /// while nine were still waiting. The next caller then found no flight and asked the network again. The
    /// store usually hid this, because a cached answer is written before the flight completes, so the next
    /// asker hit the cache. It showed when the answer was not cached, for example a service that returns
    /// 503 to a screenful of requests.
    /// </remarks>
    private void Leave(string key, Flight flight, bool abandoned)
    {
        if (!flight.Leave())
            return;

        _flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));

        if (abandoned)
            flight.Abandon();

        flight.Dispose();
    }

    private WebCachePolicy? Policy(HttpRequestMessage request) =>
        request.Options.TryGetValue(WebCache.PolicyOption, out var policy) ? policy : _default;

    /// <summary>The one request the waiting callers share.</summary>
    private async Task<Fetched> Fly(
        string key, HttpRequestMessage original, WebCachePolicy policy, bool refresh, CancellationToken ct)
    {
        // Between the caller's read and this flight starting, another flight may have filed the answer.
        // Cheaper to look again than to ask the network.
        var entry = _store.Read(key);
        var now = _now();
        if (!refresh && entry is not null && now.UtcTicks < entry.ExpiresUtcTicks)
            return Fetched.FromEntry(entry, WebCacheSource.Hit);

        var request = WebCache.CloneGet(original);
        if (refresh)
        {
            // Send an unconditional request. A "not modified" reply is the one answer this caller cannot
            // use.
        }
        else if (entry?.ETag is { Length: > 0 } etag)
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        else if (entry?.LastModified is { Length: > 0 } modified)
            request.Headers.TryAddWithoutValidation("If-Modified-Since", modified);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service could not be reached. An old answer, however old, is better than an error.
            if (entry is not null)
                return Fetched.FromEntry(entry, WebCacheSource.Stale);
            throw;
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (status == (int)HttpStatusCode.NotModified && entry is not null)
            {
                var expires = now.UtcTicks + policy.AgeFor(entry.Status).Ticks;
                _store.Renew(key, now.UtcTicks, expires);
                return Fetched.FromEntry(entry with { FetchedUtcTicks = now.UtcTicks, ExpiresUtcTicks = expires },
                    WebCacheSource.Revalidated);
            }

            // The service is unwell rather than answering. An old answer beats an error page.
            if (status >= 500 && entry is not null)
                return Fetched.FromEntry(entry, WebCacheSource.Stale);

            if (response.Content.Headers.ContentLength > MaxBodyBytes)
                return Fetched.OverSize;

            byte[] body;
            try
            {
                await response.Content.LoadIntoBufferAsync(MaxBodyBytes);
                body = await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException)
            {
                return Fetched.OverSize;   // longer than it said, or would not say
            }

            var fetched = Fetched.FromResponse(response, body, WebCacheSource.Network);

            if (IsCacheable(status) && !IsPartial(response))
            {
                _store.Write(new WebCacheEntry
                {
                    Key = key,
                    Url = request.RequestUri?.ToString() ?? "",
                    Category = policy.Category,
                    Status = status,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    ETag = response.Headers.ETag?.ToString(),
                    LastModified = response.Content.Headers.LastModified?.ToString("R"),
                    FetchedUtcTicks = now.UtcTicks,
                    ExpiresUtcTicks = now.UtcTicks + policy.AgeFor(status).Ticks,
                    Body = body,
                });
            }

            return fetched;
        }
    }

    /// <summary>What is worth keeping: an answer, and the two ways of saying "there is no such thing".
    /// Everything else is a moment in the life of a service rather than a fact about a record.</summary>
    private static bool IsCacheable(int status) => status is 200 or 203 or 404 or 410;

    /// <summary>
    /// The RPM API's mark on an answer that is only part of one: a source could not be asked, or had not
    /// answered when the API's wait ran out. Never filed. This cache keeps an answer for thirty days and
    /// ignores Cache-Control on purpose, so a partial answer filed here would stand in for the whole one
    /// for a month.
    /// </summary>
    private static bool IsPartial(HttpResponseMessage response) => response.Headers.Contains("X-RPM-Partial");

    private static HttpResponseMessage Rebuild(WebCacheEntry entry, HttpRequestMessage request, WebCacheSource source) =>
        Fetched.FromEntry(entry, source).ToResponse(request);

    /// <summary>
    /// One request several callers are waiting on, and the count of how many still are.
    ///
    /// <para>The request is not made until somebody reads <see cref="Task"/>, which is what makes it safe
    /// to build one of these speculatively: two callers can both offer a flight to the table, only one of
    /// them is kept, and the one that was not never sends anything.</para>
    /// </summary>
    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly Lazy<Task<Fetched>> _task;
        private int _waiting;

        /// <summary>Made with one caller already on it: the one making it. A flight nobody is on is a
        /// flight that has finished, and <see cref="Join"/> refuses those.</summary>
        public Flight(TimeSpan timeout, Func<CancellationToken, Task<Fetched>> fly)
        {
            _cancellation = new CancellationTokenSource(timeout);
            _task = new Lazy<Task<Fetched>>(() => fly(_cancellation.Token));
            _waiting = 1;
        }

        /// <summary>The request, started by the first read of this property.</summary>
        public Task<Fetched> Task => _task.Value;

        /// <summary>Whether it has been given up on, by its last caller or by the clock.</summary>
        public bool Abandoned => _cancellation.IsCancellationRequested;

        /// <summary>Gets on, if there is still anybody on it to get on with.</summary>
        /// <returns>False when the last caller has already left, which means this flight is on its way off
        /// the table and its answer, if any, belongs to nobody. The caller asks again.</returns>
        public bool Join()
        {
            while (true)
            {
                var waiting = Volatile.Read(ref _waiting);
                if (waiting <= 0)
                    return false;

                if (Interlocked.CompareExchange(ref _waiting, waiting + 1, waiting) == waiting)
                    return true;
            }
        }

        /// <returns>True when that was the last caller waiting on it.</returns>
        public bool Leave() => Interlocked.Decrement(ref _waiting) <= 0;

        public void Abandon()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Releases the timer the constructor armed. Called by the last caller to leave, and by a
        /// caller whose offer of a flight lost the race to the table.</summary>
        public void Dispose()
        {
            try { _cancellation.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// An answer, off the wire or out of the store, in a form that can be handed to several callers at
    /// once, each of whom gets their own <see cref="HttpResponseMessage"/>, because a response is
    /// disposable and shared ownership of one ends in a caller reading a stream somebody else closed.
    /// </summary>
    private sealed record Fetched(
        int Status,
        byte[] Body,
        string? ContentType,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers,
        WebCacheSource Source,
        bool TooLarge = false)
    {
        public static readonly Fetched OverSize = new(0, [], null, [], WebCacheSource.Network, TooLarge: true);

        public static Fetched FromEntry(WebCacheEntry entry, WebCacheSource source) =>
            new(entry.Status, entry.Body, entry.ContentType, [], source);

        public static Fetched FromResponse(HttpResponseMessage response, byte[] body, WebCacheSource source) =>
            new((int)response.StatusCode,
                body,
                response.Content.Headers.ContentType?.ToString(),
                [.. response.Headers],
                source);

        public HttpResponseMessage ToResponse(HttpRequestMessage request)
        {
            var response = new HttpResponseMessage((HttpStatusCode)Status)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Body),
            };

            foreach (var header in Headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);

            // This is set on the response as well as in the options, for callers that cannot see this
            // assembly's types. For example, the MusicBrainz client paces its requests against a rate
            // limit, and an answer from the store does not touch that limit.
            if (Source != WebCacheSource.Network)
                response.Headers.TryAddWithoutValidation(WebCache.SourceHeader, Source.ToString());

            if (ContentType is { Length: > 0 } type
                && MediaTypeHeaderValue.TryParse(type, out var parsed))
                response.Content.Headers.ContentType = parsed;

            request.Options.Set(WebCache.SourceOption, Source);
            return response;
        }
    }
}

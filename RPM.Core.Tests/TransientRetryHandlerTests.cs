using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The handler that acts on <see cref="TransientHttp.Replay"/>: how many times it asks, what it
/// sends the second time, and what it must not send twice at all.
/// </summary>
public class TransientRetryHandlerTests
{
    /// <summary>
    /// An answer the endpoint gives only once the request's own token is cancelled. A test that gives up
    /// (its own token, or the client's timeout) then knows the handler has the cancelled token in hand
    /// before it could ask again, whichever timer fires first.
    /// </summary>
    private sealed record AfterGivingUp(HttpStatusCode Status);

    /// <summary>
    /// The far end, written down. Each entry in <paramref name="answers"/> is what the next attempt
    /// gets: a status to answer with, an exception to fail with, or <see cref="AfterGivingUp"/>.
    /// </summary>
    private sealed class Endpoint : HttpMessageHandler
    {
        private readonly Queue<object> _answers;

        public Endpoint(params object[] answers) => _answers = new Queue<object>(answers);

        /// <summary>Every request as it was sent (method, headers and body), so that a test can say what
        /// the second attempt carried and not merely that there was one.</summary>
        public List<(HttpMethod Method, string? Body, HttpRequestMessage Message)> Sent { get; } = [];

        /// <summary>The responses handed out, so that a test can say the ones thrown away were disposed
        /// and not left holding a connection.</summary>
        public List<HttpResponseMessage> Handed { get; } = [];

        public int Attempts => Sent.Count;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Sent.Add((request.Method, body, request));

            var answer = _answers.Count > 0 ? _answers.Dequeue() : HttpStatusCode.OK;
            if (answer is Exception failure)
                throw failure;

            if (answer is AfterGivingUp held)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                answer = held.Status;
            }

            var response = new HttpResponseMessage((HttpStatusCode)answer)
            {
                RequestMessage = request,
                Content = new StringContent("answer " + Sent.Count),
            };
            Handed.Add(response);
            return response;
        }
    }

    private static HttpClient Client(Endpoint endpoint) =>
        new(new TransientRetryHandler(endpoint)) { Timeout = TimeSpan.FromSeconds(30) };

    private static HttpRequestMessage Post(string body = "{}") =>
        new(HttpMethod.Post, "https://example.test/thing") { Content = new StringContent(body) };

    // ---- how many times ----------------------------------------------------------------------

    [Fact]
    public async Task AnAnswerIsReturnedWithoutAskingTwice()
    {
        var endpoint = new Endpoint(HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.GetAsync("https://example.test/thing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, endpoint.Attempts);
    }

    [Fact]
    public async Task AReadIsAskedThreeTimesAndNoMore()
    {
        var endpoint = new Endpoint(
            HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway,
            HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.GetAsync("https://example.test/thing");

        Assert.Equal(TransientRetryHandler.MaxAttempts, endpoint.Attempts);
        // The last answer is handed back rather than swallowed: after three 502s the caller needs to see
        // it.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ASecondAttemptThatWorksIsTheAnswer()
    {
        var endpoint = new Endpoint(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.GetAsync("https://example.test/thing");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, endpoint.Attempts);
        Assert.Equal("answer 2", await response.Content.ReadAsStringAsync());
    }

    // ---- what must not be sent twice ---------------------------------------------------------

    [Fact]
    public async Task APostIsNotReplayedWhenTheServiceThrew()
    {
        // A 500 is the service saying it received the request. Sending it again would post the comment
        // twice.
        var endpoint = new Endpoint(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.SendAsync(Post());

        Assert.Equal(1, endpoint.Attempts);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task APostIsReplayedWhenTheEdgeSaysItNeverArrived()
    {
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.SendAsync(Post());

        Assert.Equal(2, endpoint.Attempts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task APostIsNotReplayedAfterAFailureThatMayHaveArrived()
    {
        var endpoint = new Endpoint(new IOException("the connection went away"), HttpStatusCode.OK);
        using var http = Client(endpoint);

        await Assert.ThrowsAnyAsync<Exception>(() => http.SendAsync(Post()));

        Assert.Equal(1, endpoint.Attempts);
    }

    [Fact]
    public async Task APostIsReplayedAfterAFailureThatNeverLeftThisMachine()
    {
        var endpoint = new Endpoint(
            new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
            HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.SendAsync(Post());

        Assert.Equal(2, endpoint.Attempts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AReadIsReplayedAfterADroppedConnection()
    {
        var endpoint = new Endpoint(new SocketException((int)SocketError.ConnectionReset), HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.GetAsync("https://example.test/thing");

        Assert.Equal(2, endpoint.Attempts);
    }

    [Fact]
    public async Task ThreeFailuresThrowTheLastOne()
    {
        var endpoint = new Endpoint(
            new SocketException(), new SocketException(), new IOException("the third one"));
        using var http = Client(endpoint);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(
            () => http.GetAsync("https://example.test/thing"));

        Assert.Equal(TransientRetryHandler.MaxAttempts, endpoint.Attempts);
        Assert.Contains("the third one", thrown.ToString());
    }

    [Fact]
    public async Task ABodyTooLargeToHoldIsSentTheOnce()
    {
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        var big = new ByteArrayContent(new byte[TransientRetryHandler.MaxReplayableBytes + 1]);
        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, "https://example.test/upload") { Content = big });

        Assert.Equal(1, endpoint.Attempts);
    }

    [Fact]
    public async Task ABodyThatWillNotSayHowLongItIsIsSentTheOnce()
    {
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        // A stream of unknown length: nothing to hold and nothing to send again.
        var streamed = new StreamContent(new MemoryStream([1, 2, 3]));
        streamed.Headers.ContentLength = null;

        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://example.test/thing") { Content = streamed });

        Assert.Equal(1, endpoint.Attempts);
    }

    // ---- what the second attempt carries ------------------------------------------------------

    [Fact]
    public async Task TheSecondAttemptCarriesTheSameBody()
    {
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.SendAsync(Post("{\"albumId\":\"abc\"}"));

        Assert.Equal(2, endpoint.Attempts);
        Assert.Equal("{\"albumId\":\"abc\"}", endpoint.Sent[0].Body);
        Assert.Equal("{\"albumId\":\"abc\"}", endpoint.Sent[1].Body);
    }

    [Fact]
    public async Task TheSecondAttemptCarriesTheSameMethodHeadersAndContentType()
    {
        var endpoint = new Endpoint(HttpStatusCode.GatewayTimeout, HttpStatusCode.OK);
        using var http = Client(endpoint);

        var request = Post();
        request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "a-token");
        request.Headers.TryAddWithoutValidation("X-RPM-Test", "kept");

        using var response = await http.SendAsync(request);

        var second = endpoint.Sent[1].Message;
        Assert.Equal(HttpMethod.Post, second.Method);
        Assert.Equal("Bearer a-token", second.Headers.Authorization?.ToString());
        Assert.Equal("kept", second.Headers.GetValues("X-RPM-Test").Single());
        Assert.Equal("application/json", second.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TheSecondAttemptCarriesTheRequestsOptions()
    {
        // The cache and the gate both read options off a request. A clone that dropped them would
        // send an uncacheable, ungoverned second attempt and nothing would say so.
        var key = new HttpRequestOptionsKey<string>("rpm.test.option");
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        var request = Post();
        request.Options.Set(key, "carried");

        using var response = await http.SendAsync(request);

        Assert.True(endpoint.Sent[1].Message.Options.TryGetValue(key, out var carried));
        Assert.Equal("carried", carried);
    }

    [Fact]
    public async Task AnswersThrownAwayAreDisposedRatherThanLeftHoldingAConnection()
    {
        var endpoint = new Endpoint(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var http = Client(endpoint);

        using var response = await http.GetAsync("https://example.test/thing");

        Assert.Equal(3, endpoint.Handed.Count);
        // The two discarded ones: their content is gone.
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => endpoint.Handed[0].Content.ReadAsStringAsync());
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(
            () => endpoint.Handed[1].Content.ReadAsStringAsync());
        // The one handed back is not.
        Assert.Equal("answer 3", await response.Content.ReadAsStringAsync());
    }

    // ---- giving up ---------------------------------------------------------------------------

    [Fact]
    public async Task ACallerWhoWalksAwayIsNotAskedAgainOnTheirBehalf()
    {
        // The first answer, a 503, comes back after the caller has given up. A handler that does not
        // read the token in its wait before the second attempt would send that attempt.
        var endpoint = new Endpoint(new AfterGivingUp(HttpStatusCode.ServiceUnavailable), HttpStatusCode.OK);
        using var http = Client(endpoint);
        using var giveUp = new CancellationTokenSource();

        var asking = http.GetAsync("https://example.test/thing", giveUp.Token);
        giveUp.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.Equal(1, endpoint.Attempts);
    }

    [Fact]
    public async Task TheClientsOwnClockEndsIt()
    {
        // The first answer, a 503, comes back after the client's timeout has fired. A handler that does
        // not read the token in its wait before the second attempt would send that attempt. Nothing here
        // depends on the timeout firing before the backoff ends.
        var endpoint = new Endpoint(
            new AfterGivingUp(HttpStatusCode.ServiceUnavailable), HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var http = new HttpClient(new TransientRetryHandler(endpoint))
        {
            Timeout = TimeSpan.FromMilliseconds(150),
        };

        await Assert.ThrowsAnyAsync<TaskCanceledException>(
            () => http.GetAsync("https://example.test/thing"));

        // A handler that went on after the client's timeout would send the second and third attempts.
        Assert.True(endpoint.Attempts <= 1, $"{endpoint.Attempts} attempts were sent after the client's timeout");
    }

    // ---- several callers at once --------------------------------------------------------------

    [Fact]
    public async Task TenCallersOnOneFailingEndpointDoNotComeBackInTheSameInstant()
    {
        // What this pins is that the wait is not a constant. Ten threads hit a service that is saying
        // 503. If every one of them waits the same 400 ms, the service gets ten requests in one
        // millisecond, three times over, which is the shape of a retry storm.
        var when = new System.Collections.Concurrent.ConcurrentBag<long>();
        var start = System.Diagnostics.Stopwatch.GetTimestamp();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var http = new HttpClient(new TransientRetryHandler(new Timed(when, start)))
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            using var response = await http.GetAsync("https://example.test/thing");
        }));

        // 10 callers × 3 attempts, and the second-attempt times are spread rather than stacked.
        var seconds = when.OrderBy(t => t).ToList();
        Assert.Equal(30, seconds.Count);
        Assert.True(seconds[^1] - seconds[0] > 100,
            $"every attempt landed inside {seconds[^1] - seconds[0]} ms of the first");
    }

    /// <summary>Always says 503, and writes down when it was asked.</summary>
    private sealed class Timed(System.Collections.Concurrent.ConcurrentBag<long> when, long start)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            when.Add((long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = new StringContent(""),
            });
        }
    }
}

using System.Net.Http.Headers;

namespace RPM.Core.Net;

/// <summary>
/// Tries a request again when the answer was not really an answer.
///
/// <para>Everything rpm asks of its own service goes through one Lambda behind CloudFront. That pair has a
/// few ways to say "not now" that have nothing to do with the request: a cold container that took too long,
/// a 502 or 503 from the edge, or a connection dropped when a laptop wakes up. Left alone, each of these
/// reaches the caller as a failure and is logged as a problem with the collection. Nearly all of them
/// succeed on the next attempt.</para>
///
/// <para><b>This class does not decide what may be sent again.</b> It asks
/// <see cref="TransientHttp.Replay"/> and follows the answer. That question has two parts: whether the body
/// is held in memory, and whether sending the request twice has the same effect as sending it once. Both
/// parts must be true before a second attempt is made.</para>
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    /// <summary>Attempts in total, the first one included.</summary>
    public const int MaxAttempts = 3;

    /// <summary>A body larger than this is sent once and not held in memory to be sent again.</summary>
    public const long MaxReplayableBytes = 8L * 1024 * 1024;

    /// <param name="inner">What the retries sit on. There is no default: a bare handler has no connection
    /// limit, so the caller must pass the handler it uses.</param>
    public TransientRetryHandler(HttpMessageHandler inner) : base(inner) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var (replay, body, bodyHeaders) = await PrepareAsync(request, ct);

        for (int attempt = 1; ; attempt++)
        {
            bool last = attempt == MaxAttempts;

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(
                    attempt == 1 ? request : Clone(request, body, bodyHeaders), ct);
            }
            catch (Exception ex) when (!last && !ct.IsCancellationRequested && replay.After(ex))
            {
                await Task.Delay(TransientHttp.Backoff(attempt, retryAfter: null), ct);
                continue;
            }

            if (last || !replay.After(response.StatusCode))
                return response;

            var wait = TransientHttp.Backoff(attempt, response.Headers.RetryAfter);
            response.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    /// <summary>
    /// Reads the body into memory once, so a second attempt has something to send. It also decides, from
    /// the body and the method, whether a second attempt is allowed at all. A body of unknown or too large
    /// a length is not read, and the request is sent once.
    /// </summary>
    private static async Task<(TransientHttp.Replay Replay, byte[]? Body, HttpContentHeaders? Headers)>
        PrepareAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null)
            return (TransientHttp.Replay.Of(request.Method, bodyInHand: true), null, null);

        long? length = request.Content.Headers.ContentLength;
        if (length is null or > MaxReplayableBytes)
            return (TransientHttp.Replay.Never, null, null);

        // Buffers the content in place: the original request still sends normally afterwards.
        var body = await request.Content.ReadAsByteArrayAsync(ct);
        return (TransientHttp.Replay.Of(request.Method, bodyInHand: true), body, request.Content.Headers);
    }

    /// <summary>
    /// A fresh message carrying the same request. The one already sent cannot be reused: the pipeline has
    /// had its content, and the headers on it belong to that attempt.
    /// </summary>
    private static HttpRequestMessage Clone(
        HttpRequestMessage request, byte[]? body, HttpContentHeaders? bodyHeaders)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };

        foreach (var header in request.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);

        foreach (var option in (IDictionary<string, object?>)request.Options)
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;

        if (body is not null)
        {
            copy.Content = new ByteArrayContent(body);
            copy.Content.Headers.Clear();
            if (bodyHeaders is not null)
                foreach (var header in bodyHeaders)
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }
}

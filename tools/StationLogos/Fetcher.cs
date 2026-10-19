using System.Collections.Concurrent;
using System.Net;

namespace StationLogos;

/// <summary>
/// The HTTP client for the crawl, with its limits.
///
/// <para>This crawl sends requests to about eleven thousand third-party websites that did not agree
/// to it, so it must cause them as little load as possible: one request at a time per host, however
/// many run in parallel overall; a short timeout, so a dead site costs seconds, not minutes; a small
/// read limit, so a measurement is two kilobytes, not a megabyte; and one attempt only. A site that
/// did not answer is recorded as such and tried again on another day, not retried now.</para>
/// </summary>
public sealed class Fetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _all;
    private readonly TimeSpan _limit;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perHost = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="atOnce">How many requests may be in flight across the whole crawl.</param>
    /// <param name="seconds">How long any one request may take, connect included.</param>
    public Fetcher(int atOnce, int seconds)
    {
        _all = new SemaphoreSlim(atOnce, atOnce);
        _limit = TimeSpan.FromSeconds(seconds);

        var handler = new SocketsHttpHandler
        {
            // Eight is the app's own per-server limit. This crawl uses much less, because OnePerHost
            // below allows exactly one request per site at a time.
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Min(seconds, 10)),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(seconds) };

        // Identifies the crawler and gives a contact address. A crawler that does not identify
        // itself should be blocked.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "RPM-LogoCrawler/1.0 (+https://rpmrpmrpm.com; station logo directory)");
    }

    /// <summary>One request per site at a time, whatever the crawl's overall parallelism is. A
    /// broadcaster with forty stations still gets one request at a time.</summary>
    private SemaphoreSlim OnePerHost(string host)
        => _perHost.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// The first <paramref name="cap"/> bytes of an address, and the response headers.
    ///
    /// <para>A range request where the host supports it, and a shortened read where it does not:
    /// either way the connection is closed once enough has been read, so measuring a two-megabyte
    /// photograph downloads only two kilobytes.</para>
    /// </summary>
    public async Task<(int Status, byte[] Body, string? Type, long Declared, string? Why)> HeadBytesAsync(
        string url, int cap, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (0, [], null, 0, "not an http address");

        var host = uri.Host.ToLowerInvariant();
        var one = OnePerHost(host);

        await _all.WaitAsync(ct);
        try
        {
            await one.WaitAsync(ct);
            try
            {
                // A separate deadline over the WHOLE request, headers and body.
                //
                // HttpClient.Timeout does not cover this. With ResponseHeadersRead it ends when the
                // headers arrive, and reading the stream afterwards has no deadline, so a host that
                // answers and then sends its body very slowly blocks the request forever. This does
                // happen in real crawls.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(_limit);
                var token = deadline.Token;

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (cap > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, cap - 1);

                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token);

                var status = (int)response.StatusCode;
                var type = response.Content.Headers.ContentType?.MediaType;
                var declared = response.Content.Headers.ContentLength ?? 0;

                if (status is >= 400)
                    return (status, [], type, declared, null);

                using var stream = await response.Content.ReadAsStreamAsync(token);
                var want = cap > 0 ? cap : 8 * 1024 * 1024;
                var buffer = new byte[Math.Min(want, 8 * 1024 * 1024)];
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled), token);
                    if (read == 0)
                        break;
                    filled += read;
                }

                return (status, buffer[..filled], type, declared == 0 ? filled : declared, null);
            }
            finally
            {
                one.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // This method's deadline fired, not the caller's: this host took too long.
            return (0, [], null, 0, "timed out");
        }
        catch (HttpRequestException ex)
        {
            return (0, [], null, 0, Short(ex));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (0, [], null, 0, Short(ex));
        }
        finally
        {
            _all.Release();
        }
    }

    /// <summary>A whole body, for a picture that has been chosen.</summary>
    public async Task<(int Status, byte[] Body, string? Type, string? Why)> WholeAsync(
        string url, CancellationToken ct)
    {
        var (status, body, type, _, why) = await HeadBytesAsync(url, 0, ct);
        return (status, body, type, why);
    }

    /// <summary>A page, as text, cut to <paramref name="cap"/>. Every declaration this crawl reads is
    /// in the head.</summary>
    public async Task<(int Status, string? Text, string? Why)> TextAsync(
        string url, int cap, CancellationToken ct)
    {
        var (status, body, type, _, why) = await HeadBytesAsync(url, cap, ct);
        if (why is not null || body.Length == 0)
            return (status, null, why);

        // The host's charset if it gives one, UTF-8 otherwise. A byte that is not valid becomes a
        // replacement character, not an error: the text is only read for link tags.
        var encoding = System.Text.Encoding.UTF8;
        if (type is not null)
        {
            try
            {
                var charset = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                    type + "").CharSet;
                if (!string.IsNullOrWhiteSpace(charset))
                    encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
            }
        }

        return (status, encoding.GetString(body), null);
    }

    private static string Short(Exception ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Length > 120 ? message[..120] : message;
    }

    public void Dispose()
    {
        _http.Dispose();
        _all.Dispose();
        foreach (var one in _perHost.Values)
            one.Dispose();
    }
}

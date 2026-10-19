using System.Net;
using System.Text;
using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The media server is the half of casting that a Cast device sees, so it is worth pinning down
/// exactly what it puts on the wire: a receiver fetches these URLs itself, and a header it dislikes
/// fails silently on the receiver with no error anywhere on this machine.
/// </summary>
public class CastServerTests : IDisposable
{
    private readonly CastServer _server = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private string Start()
    {
        var url = _server.Start(IPAddress.Loopback);
        Assert.True(_server.IsRunning);
        return url;
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Serves_The_Current_Stream()
    {
        var baseUrl = Start();
        var payload = Encoding.ASCII.GetBytes("this-is-not-really-mp3-but-the-server-cannot-tell");

        _server.StreamId = "abc";
        _server.OnStream = async (id, body, ct) =>
        {
            Assert.Equal("abc", id);
            await body.WriteAsync(payload, ct);
        };

        using var response = await _http.GetAsync($"{baseUrl}/stream/abc.mp3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>A live stream's length is genuinely unknown: it ends when the track does. Announcing
    /// a length we do not have, or advertising Range support we do not implement, is how a receiver
    /// ends up waiting for bytes that never come.
    ///
    /// <para>Read off the socket rather than through HttpClient: HttpClient buffers the body and then
    /// reports a Content-Length it worked out itself, so asserting through it would only prove that
    /// HttpClient can count.</para></summary>
    [Fact]
    public async Task The_Stream_Declares_No_Length_And_No_Range_Support()
    {
        var baseUrl = Start();
        var port = new Uri(baseUrl).Port;
        _server.StreamId = "abc";
        _server.OnStream = async (_, body, ct) => await body.WriteAsync(new byte[64], ct);

        var headers = await RawHeadersAsync(port, "GET /stream/abc.mp3 HTTP/1.1\r\nHost: localhost\r\n\r\n");

        Assert.Contains("200 OK", headers);
        Assert.Contains("Content-Type: audio/mpeg", headers);
        Assert.Contains("Accept-Ranges: none", headers);
        Assert.DoesNotContain("Content-Length", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transfer-Encoding", headers, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sends a raw request and returns the response's header block verbatim.</summary>
    private static async Task<string> RawHeadersAsync(int port, string request)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        await stream.FlushAsync();

        var buffer = new byte[2048];
        var seen = new StringBuilder();
        while (seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) is false)
        {
            int n = await stream.ReadAsync(buffer);
            if (n <= 0) break;
            seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }

        var text = seen.ToString();
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    /// <summary>Every seek and every track change mints a new stream. A receiver that comes back for
    /// the old one must be told it is gone: attaching it to the new track's audio would play the
    /// wrong song with no error at all.</summary>
    [Fact]
    public async Task Refuses_A_Stream_Id_That_Is_No_Longer_Current()
    {
        var baseUrl = Start();
        _server.StreamId = "second";
        _server.OnStream = async (_, body, ct) => await body.WriteAsync(new byte[8], ct);

        using var stale = await _http.GetAsync($"{baseUrl}/stream/first.mp3");
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);

        using var current = await _http.GetAsync($"{baseUrl}/stream/second.mp3");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task Refuses_Any_Stream_When_Nothing_Is_Playing()
    {
        var baseUrl = Start();   // no StreamId, no handler

        using var response = await _http.GetAsync($"{baseUrl}/stream/anything.mp3");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Serves_The_Cover_Art()
    {
        var baseUrl = Start();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };
        _server.Artwork = jpeg;
        _server.ArtworkContentType = "image/jpeg";

        using var response = await _http.GetAsync($"{baseUrl}/art/anything.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(jpeg, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Says_So_When_The_Track_Has_No_Art()
    {
        var baseUrl = Start();

        using var response = await _http.GetAsync($"{baseUrl}/art/anything.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Receivers probe with HEAD before committing to a fetch.</summary>
    [Fact]
    public async Task Answers_A_HEAD_Probe_Without_Streaming()
    {
        var baseUrl = Start();
        var streamed = false;
        _server.StreamId = "abc";
        _server.OnStream = async (_, body, ct) => { streamed = true; await body.WriteAsync(new byte[8], ct); };

        using var request = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/stream/abc.mp3");
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.False(streamed, "a HEAD probe must not start pulling audio out of the ring");
    }

    /// <summary>Receivers reconnect. If the earlier fetch were left running, two encoders would be
    /// pulling from the same ring and the audio would be torn in half.</summary>
    [Fact]
    public async Task A_Reconnect_Supersedes_The_Earlier_Fetch()
    {
        var baseUrl = Start();
        var firstCancelled = new TaskCompletionSource<bool>();
        var connections = 0;

        _server.StreamId = "abc";
        _server.OnStream = async (_, body, ct) =>
        {
            var mine = Interlocked.Increment(ref connections);
            try
            {
                // Hold the connection open, as a real stream does, until superseded or the test ends.
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                if (mine == 1) firstCancelled.SetResult(true);
                throw;
            }
        };

        using var firstCts = new CancellationTokenSource();
        var first = _http.GetAsync($"{baseUrl}/stream/abc.mp3", HttpCompletionOption.ResponseHeadersRead, firstCts.Token);
        await Task.Delay(200);

        using var secondCts = new CancellationTokenSource();
        var second = _http.GetAsync($"{baseUrl}/stream/abc.mp3", HttpCompletionOption.ResponseHeadersRead, secondCts.Token);

        Assert.True(await Task.WhenAny(firstCancelled.Task, Task.Delay(5000)) == firstCancelled.Task,
                    "the first fetch should have been cancelled when the receiver reconnected");

        firstCts.Cancel();
        secondCts.Cancel();
        try { (await second).Dispose(); } catch { }
        try { (await first).Dispose(); } catch { }
    }

    [Fact]
    public async Task Unknown_Routes_Are_Not_Found()
    {
        var baseUrl = Start();

        using var response = await _http.GetAsync($"{baseUrl}/../etc/passwd");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The URL we hand the receiver has to be an address the receiver can reach us on. A
    /// machine has several, and the loopback one is never the answer for a real speaker.</summary>
    [Fact]
    public void Picks_The_Address_That_Routes_To_The_Device()
    {
        // 8.8.8.8 is not contacted (a UDP connect only asks the routing table which interface it
        // would leave by), but it does force a real, externally-routable answer.
        var address = CastServer.LocalAddressFor(IPAddress.Parse("8.8.8.8"));

        Assert.False(IPAddress.IsLoopback(address), "a loopback URL is unreachable from a speaker");
    }
}

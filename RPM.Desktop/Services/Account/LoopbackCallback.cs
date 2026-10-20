using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Receives the end of a social sign-in on Windows and Linux, where there is no system sign-in sheet.
///
/// <para>The app listens on <c>127.0.0.1</c> on a port the system chooses and tells the accounts service
/// that port when it starts the sign-in. When the browser is done, the website sends it to
/// <c>http://127.0.0.1:{port}/auth?sessionId=…&amp;code=…</c>. That request can only come from a browser on
/// this computer, so the one-time code reaches the app that opened the browser and nobody else. This is
/// the loopback redirect of RFC 8252, section 7.3. The macOS app uses <see cref="MacWebAuth"/> instead.</para>
///
/// <para>The listener speaks just enough HTTP for one GET from a browser: it reads the request line,
/// answers with a short page and closes the connection. A request for another path (a browser also asks
/// for <c>/favicon.ico</c>) gets 404 and the listener keeps waiting.</para>
/// </summary>
internal sealed partial class LoopbackCallback : IDisposable
{
    /// <summary>How long one connection may take to send its request line. A connection that sends
    /// nothing must not stop the browser's own request from being read.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;

    private LoopbackCallback(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The port the browser comes back to.</summary>
    public int Port { get; }

    /// <summary>Starts listening on <c>127.0.0.1</c> on a free port.</summary>
    public static LoopbackCallback Open()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackCallback(listener);
    }

    /// <summary>
    /// Waits until the browser comes back for <paramref name="sessionId"/>, answers it with
    /// <paramref name="page"/> and returns the one-time code. Throws <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled first.
    /// </summary>
    public async Task<string> WaitForCodeAsync(string sessionId, string page, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();

            string? requestLine;
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(RequestTimeout);
                try { requestLine = await ReadRequestLineAsync(stream, limit.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
            }

            if (Parse(requestLine) is { } back && back.SessionId == sessionId)
            {
                await RespondAsync(stream, "200 OK", page, ct);
                return back.Code;
            }
            await RespondAsync(stream, "404 Not Found", "", ct);
        }
    }

    /// <summary>
    /// The session id and the code in a request line such as
    /// <c>GET /auth?sessionId=abc&amp;code=xyz HTTP/1.1</c>, or null when the line is anything else.
    /// </summary>
    internal static (string SessionId, string Code)? Parse(string? requestLine)
    {
        var parts = requestLine?.Split(' ');
        if (parts is not { Length: 3 } || parts[0] != "GET") return null;

        var target = parts[1];
        var query = target.IndexOf('?');
        if (query < 0 || target[..query] != "/auth") return null;

        string? sessionId = null, code = null;
        foreach (var pair in target[(query + 1)..].Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0) continue;
            var value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            switch (pair[..equals])
            {
                case "sessionId": sessionId = value; break;
                case "code": code = value; break;
            }
        }
        return string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(code) ? null : (sessionId, code);
    }

    /// <summary>The page the browser shows after a sign-in: a title and one line, in the app's language.</summary>
    internal static string Page(string title, string body) =>
        "<!doctype html><html><head><meta charset=\"utf-8\">" +
        $"<title>{WebUtility.HtmlEncode(title)}</title>" +
        "<style>body{font-family:system-ui,sans-serif;background:#111;color:#eee;display:flex;" +
        "align-items:center;justify-content:center;height:100vh;margin:0}main{text-align:center}</style>" +
        $"</head><body><main><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(body)}</p></main></body></html>";

    /// <summary>Reads up to the end of the first line. A request line longer than 8 KB is not a sign-in.</summary>
    private static async Task<string?> ReadRequestLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
        }
        return null;
    }

    private static async Task RespondAsync(Stream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(body, ct);
        }
        catch (IOException)
        {
            // The browser closed the connection first. The code was already read, so the sign-in goes on.
        }
    }

    public void Dispose() => _listener.Stop();
}

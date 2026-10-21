using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RPM.Core.Audio;

/// <summary>
/// The tiny HTTP server a Cast receiver pulls its audio from.
///
/// <para>Casting is not an audio output that the app can write to. The receiver is a client: the app
/// gives it a URL and it fetches the media itself. To cast <i>our</i> post-EQ audio, and not the
/// original file (which would bypass the whole audio pipeline), the app must publish that audio at a
/// URL on the local network. This server provides that URL.</para>
///
/// <para>It serves only two things: the live encoded stream, and the track's cover art (which the
/// receiver shows on the television). Kestrel would add a framework reference and a hosting model for
/// two routes that need no Range support and no keep-alive.</para>
///
/// <para>Streams are addressed by an id for each track. A receiver that reconnects to a stream we have
/// already moved on from gets a 404. Without this, it would attach to the wrong track, which would
/// happen on every seek.</para>
/// </summary>
public sealed class CastServer : IDisposable
{
    /// <summary>Writes the encoded audio body until the track ends or <paramref name="cancellation"/>
    /// is cancelled. Blocking on <paramref name="body"/> is intended: the receiver's full buffer pushes
    /// back through the TCP window, and this is what paces the whole pipeline.</summary>
    public delegate Task StreamHandler(string streamId, Stream body, CancellationToken cancellation);

    private const int MaxRequestBytes = 8 * 1024;

    private readonly object _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Cancels the connection that is currently receiving the audio stream. A receiver that
    /// reconnects (they often do) replaces its own earlier connection, so the two do not compete for
    /// the ring.</summary>
    private CancellationTokenSource? _liveStream;

    /// <summary>Supplies the encoded audio. Set by <see cref="CastSink"/>.</summary>
    public StreamHandler? OnStream { get; set; }

    /// <summary>The only stream id we will serve: the track playing now. A receiver that asks for
    /// the previous one (receivers reconnect, and a seek makes a new stream) is told that the track is
    /// gone, so it is not attached to the wrong audio.</summary>
    public string? StreamId { get; set; }

    /// <summary>Cover art for the current track, or null. It is served unchanged.</summary>
    public byte[]? Artwork { get; set; }

    public string ArtworkContentType { get; set; } = "image/jpeg";

    /// <summary>How long a request for a cover waits for a cover that is still being read from disk.
    /// It is longer than reading a picture from a tag normally takes, and short enough that a receiver
    /// asking for a cover that will never arrive does not wait on the connection for long.</summary>
    internal static readonly TimeSpan ArtworkWait = TimeSpan.FromSeconds(5);

    /// <summary>Set while the cover is being read and cleared when it has been read. See
    /// <see cref="ExpectArtwork"/>.</summary>
    private TaskCompletionSource? _artworkPending;

    /// <summary>
    /// Marks that a cover is being read, so that a receiver that fetches the URL before the tags
    /// have been read waits for it and is not told there is none.
    ///
    /// <para>The URL goes out in the LOAD message before the picture is read, so the music starts
    /// without waiting for it. This method keeps a receiver that asks for the picture early from
    /// being told there is none.</para>
    /// </summary>
    public void ExpectArtwork()
    {
        lock (_gate)
        {
            _artworkPending?.TrySetResult();
            _artworkPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Artwork = null;
        }
    }

    /// <summary>The cover, or null when the track has none. In both cases it releases every waiting
    /// request: "there is no picture" is a valid answer, and a receiver should not keep a connection
    /// open for it.</summary>
    public void SupplyArtwork(byte[]? artwork, string contentType)
    {
        TaskCompletionSource? pending;
        lock (_gate)
        {
            Artwork = artwork;
            ArtworkContentType = contentType;
            pending = _artworkPending;
            _artworkPending = null;
        }
        pending?.TrySetResult();
    }

    /// <summary>Waits for a cover that is still being read, if there is one. It returns as soon as there
    /// is an answer, including the answer that there is no cover.</summary>
    public async Task WaitForArtworkAsync(CancellationToken cancellation)
    {
        Task pending;
        lock (_gate)
        {
            if (_artworkPending is not { } tcs) return;
            pending = tcs.Task;
        }

        try { await pending.WaitAsync(ArtworkWait, cancellation); }
        catch { /* it is taking too long, or we are shutting down: serve what we have, if anything */ }
    }

    /// <summary>The MIME type of what <see cref="OnStream"/> writes.</summary>
    public string StreamContentType { get; set; } = "audio/mpeg";

    /// <summary>The base URL that the receiver should fetch from, for example
    /// <c>http://192.168.1.5:51001</c>. It is empty until <see cref="Start"/> is called.</summary>
    public string BaseUrl { get; private set; } = "";

    public bool IsRunning => _listener is not null;

    /// <summary>
    /// Starts listening on an ephemeral port and returns the base URL.
    /// </summary>
    /// <param name="advertiseAddress">The address to put <i>in the URL</i>: the one that the receiver
    /// can reach. The server listens on every interface, but a machine can have several addresses
    /// (VPN, Docker, a second network card) and only one of them has a route from the speaker. Use
    /// <see cref="LocalAddressFor"/> to choose it.</param>
    public string Start(IPAddress advertiseAddress)
    {
        ArgumentNullException.ThrowIfNull(advertiseAddress);

        lock (_gate)
        {
            if (_listener is not null) return BaseUrl;

            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();

            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = $"http://{advertiseAddress}:{port}";

            var token = _cts.Token;
            _ = Task.Run(() => AcceptLoop(listener, token));
            return BaseUrl;
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts, live;
        lock (_gate)
        {
            listener = _listener; _listener = null;
            cts = _cts; _cts = null;
            live = _liveStream; _liveStream = null;
            BaseUrl = "";
        }

        try { live?.Cancel(); } catch { }
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { live?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    /// <summary>The address on this machine that <paramref name="deviceAddress"/> can reach. It is found
    /// by asking the routing table which interface would be used to reach the device. A UDP "connect"
    /// sends no data and only resolves the route. Picking from the interface list instead could return
    /// a VPN or Docker address, which the speaker cannot reach.</summary>
    public static IPAddress LocalAddressFor(IPAddress deviceAddress)
    {
        ArgumentNullException.ThrowIfNull(deviceAddress);
        try
        {
            using var probe = new Socket(deviceAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(deviceAddress, 9);   // discard port; no packet is sent
            if (probe.LocalEndPoint is IPEndPoint local && !IPAddress.IsLoopback(local.Address))
                return local.Address;
        }
        catch { }
        return IPAddress.Loopback;   // a speaker cannot reach this; it shows that no route was found
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellation); }
            catch { return; }   // stopped, or the listener died

            _ = Task.Run(() => ServeAsync(client, cancellation), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                // Use a small send buffer, so that a completed write means the receiver has taken the data.
                //
                // The sink works out how far ahead of the speaker it is by counting the bytes written to
                // this socket. With the default buffer sizes that count is wrong. The kernel's send buffer
                // and the receiver's window together can hold megabytes, so a write returns long before
                // the speaker has received the bytes. Twenty-five seconds of Dolby (about two megabytes)
                // can be "sent" while the speaker holds almost none of it. The sink then believes the
                // speaker is full and stops sending. The speaker runs out of audio, and the music stops
                // while every number on this side looks healthy.
                //
                // With the buffer limited to 32 KB (a fraction of a second of audio), a write blocks until
                // the receiver is really taking the data, and the count matches what the speaker has.
                try { client.SendBufferSize = 32 * 1024; } catch { /* not fatal: the count is less accurate */ }

                await using var stream = client.GetStream();

                var request = await ReadRequestLineAsync(stream, cancellation);
                if (request is null) return;
                var (method, target) = request.Value;

                if (method is "OPTIONS")
                {
                    await WriteHeadersAsync(stream, "204 No Content", contentType: null, cancellation);
                    return;
                }
                if (method is not ("GET" or "HEAD"))
                {
                    await WriteHeadersAsync(stream, "405 Method Not Allowed", contentType: null, cancellation);
                    return;
                }

                if (RouteId(target, "/stream/") is { } streamId)
                {
                    await ServeStreamAsync(streamId, stream, method is "HEAD", cancellation);
                    return;
                }

                if (RouteId(target, "/art/") is not null)
                {
                    // The URL goes out in the LOAD message before the tags are read, so a receiver
                    // can easily arrive first. See ExpectArtwork.
                    await WaitForArtworkAsync(cancellation);

                    if (Artwork is { Length: > 0 } art)
                    {
                        await WriteHeadersAsync(stream, "200 OK", ArtworkContentType, cancellation,
                                                art.Length, ForeverCache);
                        if (method is "GET")
                            await stream.WriteAsync(art, cancellation);
                        return;
                    }
                }

                await WriteHeadersAsync(stream, "404 Not Found", contentType: null, cancellation, 0);
            }
            catch { /* the receiver hung up; that is a normal way for a stream to end */ }
        }
    }

    private async Task ServeStreamAsync(string streamId, Stream body, bool headOnly, CancellationToken cancellation)
    {
        var handler = OnStream;
        var current = StreamId;
        if (handler is null || current is null || !string.Equals(streamId, current, StringComparison.Ordinal))
        {
            await WriteHeadersAsync(body, "404 Not Found", contentType: null, cancellation, 0);
            return;
        }

        // No Content-Length and no Range: the stream is live and its length is unknown (it ends when
        // the track ends). "The body runs until the connection closes" is the internet radio
        // convention, and it is what the receiver expects for a live stream.
        await WriteHeadersAsync(body, "200 OK", StreamContentType, cancellation);
        if (headOnly) return;

        // Replace any earlier connection for the audio: a receiver that reconnects must not compete
        // with its own previous fetch for the ring.
        var link = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _liveStream;
            _liveStream = link;
        }

        // Cancel it, but leave the disposing to the handler that owns it. Its token still has active
        // registrations, and disposing it here would throw inside the other handler's await.
        try { previous?.Cancel(); } catch { }

        try
        {
            await handler(streamId, body, link.Token);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_liveStream, link)) _liveStream = null;
            }
            link.Dispose();
        }
    }

    /// <summary>Extracts the id from <c>/stream/&lt;id&gt;.&lt;ext&gt;</c>, ignoring the query string and
    /// the extension. The extension is there for receivers that inspect the URL, and it changes with
    /// the audio format. Returns null when the target is not that route.</summary>
    private static string? RouteId(string target, string prefix)
    {
        var path = target.Split('?', 2)[0];
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;

        var id = path[prefix.Length..];
        int dot = id.LastIndexOf('.');
        if (dot >= 0) id = id[..dot];
        return id.Length == 0 ? null : id;
    }

    /// <summary>Reads the request line and discards the headers (none are needed: no Range, no
    /// conditional GET, no keep-alive). Returns null for a malformed or oversized request.</summary>
    private static async Task<(string Method, string Target)?> ReadRequestLineAsync(
        Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[MaxRequestBytes];
        int filled = 0;

        while (filled < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(filled), cancellation);
            if (n <= 0) return null;
            filled += n;

            var text = Encoding.ASCII.GetString(buffer, 0, filled);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end < 0) continue;

            var line = text.Split("\r\n", 2)[0].Split(' ');
            return line.Length < 2 ? null : (line[0].ToUpperInvariant(), line[1]);
        }
        return null;   // the headers are larger than any valid request
    }

    /// <summary>
    /// How long a response may be cached.
    ///
    /// <para>The stream must never be cached: it is live and different every time, and a cached copy
    /// would replay a track that has finished. A cover is the opposite. Its URL contains a key made
    /// from its contents (see <c>CastSink.KeyFor</c>), so the same URL always gives the same picture.
    /// Allowing caching means an album's cover is sent over the network only once, not once per
    /// track.</para>
    /// </summary>
    private const string NeverCache = "no-cache, no-store";

    private const string ForeverCache = "public, max-age=31536000, immutable";

    private static async Task WriteHeadersAsync(
        Stream stream, string status, string? contentType, CancellationToken cancellation,
        long? contentLength = null, string cache = NeverCache)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Server: RPaMp\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: ").Append(cache).Append("\r\n")
            .Append("Accept-Ranges: none\r\n")
            // CORS headers are a cheap precaution. Google documents CORS as required only for adaptive
            // streams and media with tracks, but a CORS failure is invisible and hard to diagnose.
            .Append("Access-Control-Allow-Origin: *\r\n")
            .Append("Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n")
            .Append("Access-Control-Allow-Headers: Content-Type, Accept-Encoding, Range\r\n");

        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentLength is long len) head.Append("Content-Length: ").Append(len).Append("\r\n");
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation);
        await stream.FlushAsync(cancellation);
    }

    public void Dispose() => Stop();
}

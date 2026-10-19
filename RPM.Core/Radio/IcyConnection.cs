using System.Net.Http.Headers;
using RPM.Core.Audio;

namespace RPM.Core.Radio;

/// <summary>
/// Opens one station and hands back a decoder for it.
///
/// <para>The connection is made here, and not left to ffmpeg, for one reason: the station's now-playing
/// title. ICY metadata is mixed into the audio bytes, and ffmpeg's http protocol handles it internally. The
/// command line never shows a title change that happens in the middle of the stream. Reading the socket
/// here, removing the metadata (<see cref="IcyStream"/>) and piping the audio into ffmpeg costs one extra
/// copy. In return the title can be shown.</para>
///
/// <para>HLS stations are handed to ffmpeg whole. Their URL is a playlist of segments, not a byte stream,
/// so there are no interleaved bytes to remove and nothing to gain.</para>
///
/// <para>This class is public because the socket and the decoder differ by platform. The desktop pipes the
/// bytes into ffmpeg (<see cref="Open"/>). The phone has no ffmpeg, so it passes the same bytes to
/// AudioToolbox instead (<see cref="OpenBytes"/>). The GET, the playlist hop, the removal of the metadata
/// and the title are the same on both, and they live here.</para>
/// </summary>
public static class IcyConnection
{
    /// <summary>Long enough for a slow server to answer, and short enough that a dead station fails and the
    /// next reconnect can start, rather than hanging the tune.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    /// <summary>Its own client, not the directory's. This one holds a response open for hours, and a
    /// per-request timeout would cut the music off at whatever it was set to.</summary>
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        // Built by Wire, not here. A hand-made handler has no limit on the number of sockets it can
        // open. Every other client in the app has that limit.
        var http = new HttpClient(RPM.Core.Net.Wire.GatedStream(ConnectTimeout))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RPM/1.0");
        return http;
    });

    /// <summary>Connects and returns a decoder, or null when the station cannot be reached. A null means
    /// the caller should wait and try again, not give up.</summary>
    /// <param name="onTitle">Called with each announcement, already read by <see cref="IcyFields"/>. The
    /// raw ICY string never leaves this class, so a caller cannot print the raw fields by mistake.</param>
    public static IPcmSource? Open(RadioStation station, int sampleRate, int channels, Action<Fielded> onTitle)
    {
        // HLS: ffmpeg opens the station itself. Its reconnect options cover a dropped segment download,
        // which this class cannot see from outside.
        if (station.IsHls)
            return Decode(station, null, sampleRate, channels);

        var stream = OpenBytes(station, onTitle);
        return stream is null ? null : Decode(station, stream, sampleRate, channels);
    }

    /// <summary>
    /// The station's audio bytes, with the metadata removed and ready for whatever decodes them, or null
    /// when it cannot be reached.
    ///
    /// <para>Null for an HLS station as well. Its URL is a playlist of segments, not a byte stream, so
    /// there are no bytes to return here. A caller with no other way to play one must say so, and not treat
    /// the station as simply down.</para>
    /// </summary>
    /// <param name="onTitle">As on <see cref="Open"/>: the announcement already read.</param>
    public static Stream? OpenBytes(RadioStation station, Action<Fielded> onTitle)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(onTitle);

        if (station.IsHls)
            return null;

        try
        {
            return Connect(station.StreamUrl, allowPlaylistHop: true, onTitle);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// One GET with <c>Icy-MetaData: 1</c>, unwrapping a playlist if that is what came back. Returns the
    /// audio bytes with any metadata already removed.
    /// </summary>
    private static Stream? Connect(string url, bool allowPlaylistHop, Action<Fielded> onTitle)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        // Headers only: the body is the music and is read for as long as the user listens.
        var response = Client.Value.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            return null;
        }

        var body = response.Content.ReadAsStream();

        // Some stations still send a .pls or .m3u file that points at the real stream. Look at the
        // first bytes rather than trust the content type, which is often text/plain in both cases.
        var head = new byte[256];
        int taken = Fill(body, head);
        if (allowPlaylistHop &&
            StreamPlaylist.LooksLikePlaylist(response.Content.Headers.ContentType?.MediaType, head.AsSpan(0, taken)))
        {
            var text = System.Text.Encoding.UTF8.GetString(head, 0, taken) + ReadRest(body);
            response.Dispose();
            var target = StreamPlaylist.FirstUrl(text);
            // Only one hop: a playlist that points to another playlist could loop forever.
            return target is null ? null : Connect(target, allowPlaylistHop: false, onTitle);
        }

        // The first bytes are audio that has already left the socket, so they are put back at the front.
        Stream audio = new PrefixedStream(head, taken, body, response);

        if (int.TryParse(First(response.Headers, "icy-metaint"), out int metaInt) && metaInt > 0)
        {
            // Until the first metadata block arrives there is no title, and the station's name is shown
            // instead. The block comes after at most one metaint of audio, about a second or two.
            var icy = new IcyStream(audio, metaInt);
            icy.TitleChanged += raw => onTitle(IcyFields.Read(raw));
            audio = icy;
        }

        return audio;
    }

    private static IPcmSource Decode(RadioStation station, Stream? input, int sampleRate, int channels) =>
        new FfmpegPcmSource(new FfmpegPcmSource.Options
        {
            Source = station.StreamUrl,
            Input = input,
            SampleRate = sampleRate,
            Channels = channels,
            Label = station.Name,
            // Only used when ffmpeg owns the connection (HLS). On the piped path the socket is ours, so
            // reconnecting is the job of the source.
            InputArgs = input is null
                ?
                [
                    "-user_agent", "RPM/1.0",
                    "-reconnect", "1",
                    "-reconnect_streamed", "1",
                    "-reconnect_delay_max", "5",
                ]
                : null,
        });

    /// <summary>Reads up to <paramref name="into"/>.Length bytes, or fewer at the end of the
    /// stream.</summary>
    private static int Fill(Stream stream, byte[] into)
    {
        int taken = 0;
        while (taken < into.Length)
        {
            int n = stream.Read(into, taken, into.Length - taken);
            if (n <= 0) break;
            taken += n;
        }
        return taken;
    }

    /// <summary>The rest of a playlist, with a size cap. It is only read for a response that already looks
    /// like a few lines of text.</summary>
    private static string ReadRest(Stream stream)
    {
        var buffer = new byte[8192];
        int taken = Fill(stream, buffer);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, taken);
    }

    private static string? First(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// The bytes already read, then the rest of the socket. Finding out whether a response is a playlist
    /// means reading part of it, and an HTTP response body cannot be rewound. So the bytes that were read
    /// are put back at the front.
    /// </summary>
    private sealed class PrefixedStream(byte[] head, int headLength, Stream rest, IDisposable owner) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at < headLength)
            {
                int take = Math.Min(count, headLength - _at);
                Array.Copy(head, _at, buffer, offset, take);
                _at += take;
                return take;
            }
            return rest.Read(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { rest.Dispose(); } catch { }
                try { owner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

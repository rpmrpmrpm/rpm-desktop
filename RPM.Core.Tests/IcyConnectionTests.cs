using System.Net;
using System.Text;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The station's connection on its own: the part of tuning that has no decoder.
///
/// <para>
/// It is tested on its own because two unrelated decoders share it: the desktop sends its output
/// into ffmpeg, and the phone gives the same bytes to AudioToolbox. Both need the bytes to be the
/// audio and only the audio. The ICY metadata is mixed into the same stream, and a de-interleaving
/// error of one byte gives a click every few seconds on one platform and silence on the other.
/// </para>
///
/// <para>No ffmpeg and no real audio here: the payload is a known pattern, so "the bytes came back
/// exactly" is a stronger check than "it decoded to something audible".</para>
/// </summary>
public class IcyConnectionTests : IDisposable
{
    private const int MetaInt = 1024;

    private readonly List<HttpListener> _servers = [];

    public void Dispose()
    {
        foreach (var server in _servers)
            try { server.Close(); } catch { }

        GC.SuppressFinalize(this);
    }

    /// <summary>A payload that shows any interleaving mistake: every byte encodes its own
    /// position.</summary>
    private static byte[] Payload(int length)
    {
        var audio = new byte[length];
        for (var i = 0; i < length; i++) audio[i] = (byte)(i % 251);
        return audio;
    }

    /// <summary>Inserts the metadata the way a SHOUTcast server does: <see cref="MetaInt"/> bytes of
    /// audio, a length byte, then that many sixteen-byte chunks of text.</summary>
    private static byte[] Interleave(byte[] audio, string title)
    {
        var block = Encoding.UTF8.GetBytes($"StreamTitle='{title}';StreamUrl='';");
        var blocks = (block.Length + 15) / 16;
        var padded = new byte[blocks * 16];
        block.CopyTo(padded, 0);

        var wire = new List<byte>(audio.Length + audio.Length / MetaInt * (padded.Length + 1));

        for (var at = 0; at < audio.Length; at += MetaInt)
        {
            var take = Math.Min(MetaInt, audio.Length - at);
            wire.AddRange(audio.AsSpan(at, take).ToArray());

            if (take < MetaInt) break;      // a partial run is the end of the payload, not a boundary

            // The first block holds the title; the rest say "unchanged", which is what a real station
            // sends while a song plays.
            if (at == 0) { wire.Add((byte)blocks); wire.AddRange(padded); }
            else wire.Add(0);
        }

        return [.. wire];
    }

    /// <summary>Serves <paramref name="body"/> on a free port and returns the port's base URL.</summary>
    private string Serve(byte[] body, string contentType, params (string Name, string Value)[] headers)
    {
        var server = LoopbackHttp.Start(out var root);
        _servers.Add(server);

        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try { context = await server.GetContextAsync(); }
                catch { return; }           // the listener was closed; the test is over

                try
                {
                    var response = context.Response;
                    response.ContentType = contentType;

                    foreach (var (name, value) in headers)
                        response.Headers.Add(name, value);

                    response.SendChunked = true;
                    await response.OutputStream.WriteAsync(body);
                    response.OutputStream.Close();
                }
                catch { /* the reader disconnected first */ }
            }
        });

        return root;
    }

    private static RadioStation Station(string url, bool hls = false) => new()
    {
        Uuid = "icy",
        Name = "Test Radio",
        StreamUrl = url,
        Codec = "MP3",
        IsHls = hls,
    };

    private static byte[] ReadAll(Stream stream)
    {
        using var into = new MemoryStream();
        stream.CopyTo(into);
        return into.ToArray();
    }

    [Fact]
    public void The_Audio_Comes_Back_Without_The_Metadata_In_It()
    {
        var audio = Payload(MetaInt * 5 + 300);
        var root = Serve(
            Interleave(audio, "Test Tone - The B Side"),
            "audio/mpeg",
            ("icy-metaint", MetaInt.ToString()));

        string? announced = null;
        using var stream = IcyConnection.OpenBytes(Station(root + "live.mp3"), heard => announced = heard.Spoken);

        Assert.NotNull(stream);
        Assert.Equal(audio, ReadAll(stream));
        Assert.Equal("Test Tone - The B Side", announced);
    }

    /// <summary>A real title line from 99.9 Kiss Country, in iHeart's comma-separated format.</summary>
    private const string IheartListed =
        "title=\"Good Vibes\",artist=\"CHRIS JANSON\",url=\"song_spot=\"F\" MediaBaseId=\"0\" " +
        "itunesTrackId=\"0\" amgTrackId=\"-1\" amgArtistId=\"0\" TAID=\"0\" TPID=\"68828468\" cartcutId=\"0\" " +
        "amgArtworkURL=\"https://i.iheart.com/v3/catalog/track/68828468?ops=fit(200,200),format(%22jpeg%22)\" " +
        "length=\"00:02:42\" unsID=\"-1\" spotInstanceId=\"d27aa236-4e8c-40ed-84ef-c16a35bd8f11\"\"";

    /// <summary>The raw ICY string does not leave the connection. The callback gets a value already
    /// parsed by IcyFields, so no caller can show the encoder's raw fields by mistake.</summary>
    [Fact]
    public void The_Title_Arrives_Already_Read()
    {
        var root = Serve(
            Interleave(Payload(MetaInt * 3), IheartListed),
            "audio/mpeg",
            ("icy-metaint", MetaInt.ToString()));

        Fielded? heard = null;
        using var stream = IcyConnection.OpenBytes(Station(root + "live.mp3"), h => heard = h);
        ReadAll(stream!);

        Assert.Equal("CHRIS JANSON - Good Vibes", heard!.Value.Spoken);
        Assert.Equal("CHRIS JANSON", heard!.Value.Named!.Value.Artist);
        Assert.Equal("Good Vibes", heard!.Value.Named!.Value.Title);
    }

    /// <summary>The home screen's radio card, for a station that is not tuned. The peek parses the
    /// title the same way as the tuner, so it does not show the raw fields.</summary>
    [Fact]
    public void A_Peek_Returns_The_Announcement_Read()
    {
        var root = Serve(
            Interleave(Payload(MetaInt * 3), IheartListed),
            "audio/mpeg",
            ("icy-metaint", MetaInt.ToString()));

        Assert.Equal("CHRIS JANSON - Good Vibes", IcyPeek.Title(Station(root + "live.mp3")));
    }

    [Fact]
    public void A_Station_That_Sends_No_Metadata_Comes_Back_Untouched()
    {
        // Many Icecast servers never send icy-metaint. Then there is no title to show, and that is the
        // only difference: no metadata is mixed in, so nothing may be removed.
        var audio = Payload(4096);
        var root = Serve(audio, "audio/mpeg");

        var announced = 0;
        using var stream = IcyConnection.OpenBytes(Station(root + "live.mp3"), _ => announced++);

        Assert.NotNull(stream);
        Assert.Equal(audio, ReadAll(stream));
        Assert.Equal(0, announced);
    }

    [Fact]
    public void A_Url_That_Is_Really_A_Playlist_Is_Followed_To_The_Stream()
    {
        // The directory resolves most of these, but a station that moved since its last check returns
        // a playlist at connect time: a few lines of text instead of audio.
        var audio = Payload(MetaInt * 2);
        var streamRoot = Serve(audio, "audio/mpeg");

        var playlist = Encoding.UTF8.GetBytes(
            $"[playlist]\nNumberOfEntries=1\nFile1={streamRoot}live.mp3\nTitle1=Test Radio\n");

        // Vague on purpose: most servers send a playlist as text/plain, and many also send their
        // audio as text/plain.
        var pointerRoot = Serve(playlist, "text/plain");

        using var stream = IcyConnection.OpenBytes(Station(pointerRoot + "station.pls"), _ => { });

        Assert.NotNull(stream);
        Assert.Equal(audio, ReadAll(stream));
    }

    [Fact]
    public void An_Hls_Station_Has_No_Byte_Stream_To_Give()
    {
        // Not a failure to retry: its URL is a playlist of segments. A caller that cannot demux it must
        // be told so, rather than retrying with back-off against a station that is working.
        var root = Serve(Payload(64), "application/vnd.apple.mpegurl");

        Assert.Null(IcyConnection.OpenBytes(Station(root + "live.m3u8", hls: true), _ => { }));
    }

    [Fact]
    public void A_Station_That_Cannot_Be_Reached_Comes_Back_Null()
    {
        // Port 1, rather than an ephemeral port that a probe opened and closed: the kernel reuses
        // ephemeral ports as soon as they are free, so another test server in this assembly could
        // answer on it. Port 1 is below the kernel's ephemeral range and cannot be bound without root,
        // so the connection is always refused.
        Assert.Null(IcyConnection.OpenBytes(Station("http://127.0.0.1:1/live.mp3"), _ => { }));
    }
}

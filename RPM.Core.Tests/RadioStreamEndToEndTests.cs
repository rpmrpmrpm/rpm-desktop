using System.Diagnostics;
using System.Net;
using System.Text;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// Internet radio end to end: a real HTTP server broadcasting a real MP3 with real ICY metadata
/// interleaved into it, decoded by the real ffmpeg through the real connect path.
///
/// <para>This is the one part of the radio that cannot be proved with a fake. The audio and the
/// station's now-playing text travel down the same socket, spliced together. If the byte counting
/// is wrong by one, the metadata is decoded as audio (a click every few seconds) or the audio is
/// read as metadata (silence). Only a genuine interleaved stream through a genuine decoder catches
/// that. Skipped silently where ffmpeg is not installed.</para>
/// </summary>
public class RadioStreamEndToEndTests : IDisposable
{
    private const int Rate = 44100, Channels = 2;
    private const int MetaInt = 8192;
    private const string Title = "Test Tone - The B Side";

    private static readonly bool FfmpegAvailable = ProbeFfmpeg();

    private readonly string _root;
    private HttpListener? _listener;

    public RadioStreamEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"radiotest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-version");
            using var probe = Process.Start(psi);
            probe!.WaitForExit(5000);
            return probe.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>An MP3 of a plain tone, for which a decoder produces clearly non-zero samples.</summary>
    private byte[] MakeMp3(double seconds = 6)
    {
        var path = Path.Combine(_root, "tone.mp3");
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-f", "lavfi",
                     "-i", $"sine=frequency=440:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                     // lavfi's sine comes out at about -21 dB. It is raised well clear of that, so
                     // "there is audio here" and "this is silence" cannot be confused.
                     "-filter:a", "volume=15dB",
                     "-ac", "2", "-ar", "44100", "-b:a", "128k", "-y", path,
                 })
            psi.ArgumentList.Add(arg);

        using var encode = Process.Start(psi);
        encode!.WaitForExit(20000);
        return File.ReadAllBytes(path);
    }

    /// <summary>Splices the metadata in the way a SHOUTcast server does: <see cref="MetaInt"/> bytes of
    /// audio, a length byte, then that many sixteen-byte chunks of text.</summary>
    private static byte[] Interleave(byte[] audio, string title)
    {
        var block = Encoding.UTF8.GetBytes($"StreamTitle='{title}';StreamUrl='';");
        int blocks = (block.Length + 15) / 16;
        var padded = new byte[blocks * 16];
        block.CopyTo(padded, 0);

        var wire = new List<byte>(audio.Length + audio.Length / MetaInt * (padded.Length + 1));
        for (int at = 0; at < audio.Length; at += MetaInt)
        {
            int take = Math.Min(MetaInt, audio.Length - at);
            wire.AddRange(audio.AsSpan(at, take).ToArray());
            if (take < MetaInt)
                break;                       // a partial run is the end of the file, not a boundary

            // The first block carries the title; the rest say "unchanged", which is what a real
            // station sends for the length of a song.
            if (at == 0) { wire.Add((byte)blocks); wire.AddRange(padded); }
            else wire.Add(0);
        }
        return [.. wire];
    }

    /// <summary>Starts a station on a free port and returns its URL. Serves the same bytes to every
    /// request, so a reconnect gets a stream too.</summary>
    private string StartStation(byte[] wire, bool sendMetaInt = true)
    {
        _listener = LoopbackHttp.Start(out var url);

        _ = Task.Run(async () =>
        {
            while (_listener?.IsListening == true)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }             // the listener closed under us; the test is over

                try
                {
                    var response = context.Response;
                    response.ContentType = "audio/mpeg";
                    response.Headers.Add("icy-name", "Test Radio");
                    if (sendMetaInt)
                        response.Headers.Add("icy-metaint", MetaInt.ToString());
                    response.SendChunked = true;
                    await response.OutputStream.WriteAsync(wire);
                    response.OutputStream.Close();
                }
                catch { /* the listener hung up first */ }
            }
        });

        return url + "live.mp3";
    }

    private static RadioStation Station(string url) => new()
    {
        Uuid = "e2e",
        Name = "Test Radio",
        StreamUrl = url,
        Codec = "MP3",
        BitrateKbps = 128,
    };

    /// <summary>Reads until there is audible sound or the source gives up.</summary>
    private static (int Frames, float Peak) Drain(RadioStreamSource radio, int frames)
    {
        var buffer = new float[4096 * Channels];
        int got = 0;
        float peak = 0;
        while (got < frames)
        {
            int n = radio.Read(buffer, 4096);
            if (n <= 0) break;
            for (int i = 0; i < n * Channels; i++)
                peak = Math.Max(peak, Math.Abs(buffer[i]));
            got += n;
        }
        return (got, peak);
    }

    [Fact]
    public void A_Station_Is_Decoded_And_Says_What_It_Is_Playing()
    {
        if (!FfmpegAvailable) return;

        var url = StartStation(Interleave(MakeMp3(), Title));
        using var radio = new RadioStreamSource(Station(url), Rate, Channels);

        var (frames, peak) = Drain(radio, Rate * 2);

        Assert.True(frames >= Rate * 2, $"only {frames} frames came out of the station");
        // A 440 Hz tone at full level. Near-silence here would mean that the metadata blocks were fed
        // to the decoder, or that the audio was read as metadata.
        Assert.True(peak > 0.2f, $"the stream decoded to near-silence (peak {peak:F3})");
        Assert.Equal(Title, radio.Title);
    }

    [Fact]
    public void A_Station_That_Sends_No_Metadata_Still_Plays()
    {
        if (!FfmpegAvailable) return;

        // Many Icecast servers never send icy-metaint. There is no title to show, and that is the
        // only difference it may make.
        var url = StartStation(MakeMp3(), sendMetaInt: false);
        using var radio = new RadioStreamSource(Station(url), Rate, Channels);

        var (frames, peak) = Drain(radio, Rate);

        Assert.True(frames >= Rate, $"only {frames} frames came out of the station");
        Assert.True(peak > 0.2f, $"the stream decoded to near-silence (peak {peak:F3})");
        Assert.Null(radio.Title);
    }

    [Fact]
    public void A_Station_Url_That_Is_Really_A_Playlist_Is_Followed_To_The_Stream()
    {
        if (!FfmpegAvailable) return;

        // The directory resolves most of these, but a station that has moved since its last check
        // gives a playlist address at connect time, and a decoder pointed at it produces nothing.
        var audio = Interleave(MakeMp3(), Title);
        var streamUrl = StartStation(audio);

        var playlist = Path.Combine(_root, "station.pls");
        File.WriteAllText(playlist, $"[playlist]\nNumberOfEntries=1\nFile1={streamUrl}\nTitle1=Test Radio\n");

        // A second server, giving out the playlist that points to the first.
        var pointer = LoopbackHttp.Start(out var pointerRoot);
        _ = Task.Run(async () =>
        {
            try
            {
                var context = await pointer.GetContextAsync();
                context.Response.ContentType = "text/plain";      // vague on purpose: most servers are
                var body = Encoding.UTF8.GetBytes(File.ReadAllText(playlist));
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.OutputStream.Close();
            }
            catch { }
        });

        try
        {
            using var radio = new RadioStreamSource(Station($"{pointerRoot}station.pls"), Rate, Channels);

            var (frames, peak) = Drain(radio, Rate);

            Assert.True(frames >= Rate, $"only {frames} frames came out of the station");
            Assert.True(peak > 0.2f, $"the playlist was decoded instead of the stream it points at (peak {peak:F3})");
        }
        finally
        {
            try { pointer.Close(); } catch { }
        }
    }
}

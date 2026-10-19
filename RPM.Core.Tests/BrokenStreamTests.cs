using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RPM.Core.Audio;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Telling a record that finished from a stream that died.
///
/// <para>
/// The failure this prevents: when the computer serving a shared library leaves the network, the
/// player goes through the whole queue in silence. Every track "ends" as soon as it starts, because a
/// decoder that has run out looks the same from above however it ran out. Unless the decoder says
/// which it was, the player reads every one as a record that played through, and advances.
/// </para>
///
/// <para>
/// The trap underneath is that ffmpeg exits <b>zero</b> after a connection is reset mid-track. It
/// says so on stderr and then reports success, so an exit code is not the answer on its own. These
/// hold both halves: the reading is what ffmpeg said, and a file that simply ends still simply ends.
/// </para>
/// </summary>
public class BrokenStreamTests : IDisposable
{
    private readonly string _root;
    private static readonly bool FfmpegAvailable = ProbeFfmpeg();

    public BrokenStreamTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmstream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── what is read over a network, and what is not ──

    [Theory]
    [InlineData("http://192.168.1.9:7777/rpm/track/abc", true)]
    [InlineData("https://example-bucket.s3.eu-west-1.amazonaws.com/x.flac?sig=1", true)]
    [InlineData("/Users/somebody/Music/Album/01 Track.flac", false)]
    [InlineData("C:\\Music\\01 Track.flac", false)]
    [InlineData(null, false)]
    public void ASourceIsReadOverTheNetworkOrOffADisk(string? source, bool network)
    {
        Assert.Equal(network, FfmpegPcmSource.IsNetwork(source));
    }

    /// <summary>ffmpeg's own reconnect is the first answer to a connection that drops briefly, and it is
    /// meaningless on a file: every one of those arguments is an input option for a protocol.</summary>
    [Fact]
    public void OnlyANetworkSourceIsGivenTheReconnectArguments()
    {
        var args = FfmpegPcmSource.ReconnectArgs("https://example.test/track.flac");

        Assert.NotNull(args);
        Assert.Contains("-reconnect", args!);
        Assert.Contains("-reconnect_streamed", args);
        Assert.Contains("-reconnect_delay_max", args);

        Assert.Null(FfmpegPcmSource.ReconnectArgs("/Users/somebody/Music/01 Track.flac"));
    }

    /// <summary>Which of the two network routes a URL is. A computer that has left the network is
    /// given up on and the record is looked for in the cloud; there is nothing to give up on when the
    /// cloud is where it was already coming from.</summary>
    [Fact]
    public void ATrackServedByAnotherComputerIsToldFromAStoreLink()
    {
        Assert.True(SharingProtocol.IsPeerTrackUrl(
            "https://192.168.1.9:7777/rpm/track/RPM-1234?t=token"));
        Assert.False(SharingProtocol.IsPeerTrackUrl(
            "https://example-bucket.s3.eu-west-1.amazonaws.com/u/1/RPM-1234.flac?X-Amz-Signature=abc"));
        Assert.False(SharingProtocol.IsPeerTrackUrl(null));
    }

    // ── the decoder's own word ──

    /// <summary>A file that runs out has ended. This is how everything above reads it, and this must
    /// not change: a local file is never read as a failure, whatever ffmpeg reported about its
    /// end.</summary>
    [Fact]
    public void AFileThatRunsOutHasEnded()
    {
        if (!FfmpegAvailable) return;

        var path = MakeTone(seconds: 2);
        using var source = Open(path);
        DrainAll(source);

        Assert.Equal(PcmState.Ended, source.State);
        Assert.Null(source.Fault);
    }

    /// <summary>The same for a stream that is served whole: no complaint, so nothing to report.</summary>
    [Fact]
    public void AStreamThatIsServedWholeHasEnded()
    {
        if (!FfmpegAvailable) return;

        var bytes = File.ReadAllBytes(MakeTone(seconds: 2));
        using var server = new OneShotServer(bytes, serve: bytes.Length);
        using var source = Open(server.Url);
        DrainAll(source);

        Assert.Equal(PcmState.Ended, source.State);
    }

    /// <summary>
    /// And the one this exists for: the other computer stopped mid-record. The decode runs out early
    /// and says so, which is what stops the player calling it "the next song".
    /// </summary>
    [Fact]
    public void AStreamThatStopsArrivingHasFailed()
    {
        if (!FfmpegAvailable) return;

        var bytes = File.ReadAllBytes(MakeTone(seconds: 2));
        using var server = new OneShotServer(bytes, serve: bytes.Length / 3);
        using var source = Open(server.Url);
        DrainAll(source);

        Assert.Equal(PcmState.Failed, source.State);
        Assert.False(string.IsNullOrWhiteSpace(source.Fault));
    }

    // ── harness ──

    private static FfmpegPcmSource Open(string source) =>
        new(new FfmpegPcmSource.Options
        {
            Source = source,
            SampleRate = 44100,
            Channels = 2,
            // No reconnect: this is about what a decoder reports once it HAS run out, and ffmpeg
            // spends seconds retrying a host that is gone before it gets there.
            InputArgs = null,
        });

    private static void DrainAll(IPcmSource source)
    {
        var block = new float[4096];
        while (source.Read(block, block.Length / 2) > 0) { }
    }

    private string MakeTone(int seconds)
    {
        var path = Path.Combine(_root, $"tone-{seconds}s.wav");
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"sine=frequency=220:duration={seconds}", path,
                 })
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        return path;
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// One HTTP request, answered with as much of a file as it is told to send and then dropped:
    /// the socket is closed with a zero linger, which is a connection reset rather than
    /// an orderly close. That is what a laptop being shut looks like to the machine reading from it.
    /// </summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;

        public OneShotServer(byte[] body, int serve)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/track.wav";

            _thread = new Thread(() =>
            {
                try
                {
                    using var client = _listener.AcceptTcpClient();
                    using var stream = client.GetStream();

                    var request = new byte[4096];
                    if (stream.Read(request, 0, request.Length) <= 0)
                        return;

                    // The whole length is declared whatever is actually sent: the reader is then
                    // waiting on bytes that never come, which is the case under test.
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        $"Content-Length: {body.Length}\r\n" +
                        "Content-Type: audio/wav\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, serve);
                    stream.Flush();

                    if (serve < body.Length)
                        client.LingerState = new LingerOption(true, 0);   // reset, not an orderly close
                }
                catch { /* the reader went away first; nothing here outlives the test */ }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public string Url { get; }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _thread.Join(2000);
        }
    }
}

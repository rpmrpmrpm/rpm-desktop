using System.Diagnostics;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The claim the whole feature rests on: a borrowed track is a URL, and the decoder opens it exactly
/// as it opens a file.
///
/// <para>
/// Worth a real ffmpeg rather than a mock, because the thing that could be wrong is not in our code.
/// The library is served over TLS with a certificate no authority issued (see
/// <see cref="PeerCertificate"/>), and a decoder that refused such a certificate would leave a
/// feature that passes every unit test and plays no music. So the test is: press a tone, serve it,
/// and have ffmpeg read it back over the wire, head and middle both.
/// </para>
///
/// <para>Skipped where ffmpeg is not installed, like the other end-to-end tests here.</para>
/// </summary>
public class PeerPlaybackEndToEndTests : IDisposable
{
    private readonly PeerIdentity _owner = PeerIdentity.Create();
    private readonly PeerIdentity _borrower = PeerIdentity.Create();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"peerplay-{Guid.NewGuid():N}");

    private static readonly bool FfmpegAvailable = Probe("ffmpeg") && Probe("ffprobe");

    /// <summary>One tone, pressed once for the whole class. Two decodes of the same file is the
    /// point; two encodes of two files is CPU this suite's timing-sensitive tests have to share.</summary>
    private readonly Lazy<string> _tone;

    public PeerPlaybackEndToEndTests()
    {
        Directory.CreateDirectory(_root);
        _tone = new Lazy<string>(() =>
        {
            var file = Path.Combine(_root, "tone.flac");
            Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=4", file);
            return file;
        });
    }

    public void Dispose()
    {
        _owner.Dispose();
        _borrower.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_decoder_opens_a_borrowed_track_over_tls_as_it_opens_a_file()
    {
        if (!FfmpegAvailable) return;

        var file = _tone.Value;

        using var server = new LibraryServer(_owner, new PairingBoard(PairingTimes.Standard, () => DateTime.UtcNow))
        {
            Name = "the other computer",
            Trusted = () => [new TrustedPeer
            {
                DeviceId = _borrower.DeviceId,
                PublicKey = _borrower.PublicKey,
                Name = "here",
            }],
            OnTrack = (_, _, _) => Task.FromResult<TrackBytes?>(new TrackBytes(file, null)),
        };
        server.Start();

        using var client = new PeerClient(_borrower, "127.0.0.1", server.Port, _owner.PublicKey, () => "");
        var url = await client.TrackUrlAsync("11111111-2222-3333-4444-555555555555");

        Assert.NotNull(url);

        // The whole track, read the way the player reads one.
        var duration = Run("ffprobe", "-v", "error",
            "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", url!);

        Assert.True(double.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var seconds),
                    $"ffprobe said: {duration}");
        Assert.InRange(seconds, 3.5, 4.5);
    }

    [Fact]
    public async Task A_borrowed_track_seeks_under_a_real_decoder()
    {
        if (!FfmpegAvailable) return;

        var file = _tone.Value;

        using var server = new LibraryServer(_owner, new PairingBoard(PairingTimes.Standard, () => DateTime.UtcNow))
        {
            Trusted = () => [new TrustedPeer
            {
                DeviceId = _borrower.DeviceId,
                PublicKey = _borrower.PublicKey,
                Name = "here",
            }],
            OnTrack = (_, _, _) => Task.FromResult<TrackBytes?>(new TrackBytes(file, null)),
        };
        server.Start();

        using var client = new PeerClient(_borrower, "127.0.0.1", server.Port, _owner.PublicKey, () => "");
        var url = await client.TrackUrlAsync("11111111-2222-3333-4444-555555555555");

        // Dropping the needle three seconds in is a range request against the same URL. A server that
        // ignored Range would still play, but from the beginning, so it would be silently wrong.
        // That is why this is measured rather than assumed.
        var decoded = Path.Combine(_root, "tail.wav");
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", "3", "-i", url!, decoded);

        var duration = Run("ffprobe", "-v", "error",
            "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", decoded);

        Assert.True(double.TryParse(duration, System.Globalization.CultureInfo.InvariantCulture, out var seconds),
                    $"ffprobe said: {duration}");
        Assert.InRange(seconds, 0.5, 1.5);
    }

    private static bool Probe(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi);
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        var error = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit(60_000);

        Assert.True(p.ExitCode == 0, $"{exe} failed ({p.ExitCode}): {error}");
        return output;
    }
}

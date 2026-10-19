using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// The waveform computed in a streaming pass, not by loading the whole track. The result must be the
/// same picture: a streamed fold and a whole-track decode must agree bucket for bucket, or "cheaper"
/// would mean "different", and the lab draws bar lines against these.
/// </summary>
public class PeakScannerTests : IDisposable
{
    private static readonly bool FfmpegAvailable = ProbeFfmpeg();
    private readonly string _root;

    public PeakScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"peaks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_streamed_scan_draws_the_same_waveform_as_a_whole_track_decode()
    {
        if (!FfmpegAvailable) return;

        const int rate = 22050;
        var path = WriteWav("tone", seconds: 6, rate: 44100);

        var streamed = PeakScanner.Scan(path, rate);
        Assert.NotNull(streamed);

        var loaded = new WavePeaks.Accumulator(rate);
        loaded.Add(PcmDecoder.DecodeMono(path, rate));
        var whole = loaded.ToPeaks();

        Assert.Equal(whole.Count, streamed!.Count);
        Assert.Equal(whole.BucketSeconds, streamed.BucketSeconds, 9);

        for (int i = 0; i < whole.Count; i++)
        {
            double at = i * whole.BucketSeconds;
            Assert.True(whole.Range(at, at + whole.BucketSeconds, out var wMin, out var wMax));
            Assert.True(streamed.Range(at, at + streamed.BucketSeconds, out var sMin, out var sMax));
            Assert.Equal(wMin, sMin, 5);
            Assert.Equal(wMax, sMax, 5);
        }
    }

    [Fact]
    public void A_file_that_will_not_decode_draws_nothing_rather_than_throwing()
    {
        if (!FfmpegAvailable) return;

        Assert.Null(PeakScanner.Scan(Path.Combine(_root, "not-here.wav"), 22050));
    }

    // ── harness ───────────────────────────────────────────────────────────────

    private string WriteWav(string name, double seconds, int rate)
    {
        var samples = new float[(int)(rate * seconds)];
        var rng = new Random(1234);
        for (int i = 0; i < samples.Length; i++)
        {
            // A tone with a little noise on it: a flat sine has the same extremes in every bucket,
            // so a bucketing mistake would not be noticed.
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * i / rate)
                                 + 0.05 * (rng.NextDouble() * 2 - 1));
        }

        var path = Path.Combine(_root, $"{name}.wav");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * sizeof(short);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // mono
        writer.Write(rate);
        writer.Write(rate * 2);            // byte rate
        writer.Write((short)2);            // block align
        writer.Write((short)16);           // bits
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in samples)
            writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue));

        return path;
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch { return false; }
    }
}

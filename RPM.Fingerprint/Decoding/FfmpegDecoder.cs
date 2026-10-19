using System.Diagnostics;

namespace RPM.Fingerprint.Decoding;

/// <summary>Mono PCM audio decoded into a float array at a known sample rate.</summary>
public sealed record DecodedAudio(float[] Samples, int SampleRate, double DurationSeconds);

/// <summary>
/// Decodes any audio (or video) file by shelling to a local ffmpeg binary and reading raw
/// 32-bit float PCM from stdout. ffmpeg handles every codec we'd realistically encounter
/// (MP3, AAC/M4A, OGG/Vorbis, Opus, FLAC, WAV, ALAC, WMA, even audio tracks in MP4 video).
///
/// Used by the slice path in <c>identify-clip</c>; the main fingerprint pipeline goes through
/// fpcalc directly (which has its own libavcodec-based decoder).
/// </summary>
public static class FfmpegDecoder
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    public static DecodedAudio Decode(string path, int targetSampleRate, double? startSec = null, double? lengthSec = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        // -ss before -i is the fast seek (keyframe-aligned, ~instant)
        if (startSec is double s)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(s.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        if (lengthSec is double l)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(l.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-vn");                              // ignore any video stream
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");   // mono
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(targetSampleRate.ToString());
        psi.ArgumentList.Add("-f");  psi.ArgumentList.Add("f32le"); // raw 32-bit float little-endian
        psi.ArgumentList.Add("-");                                 // write to stdout

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start ffmpeg ({FfmpegPath})");

        // Drain stderr concurrently so the pipe doesn't fill and deadlock
        var stderrTask = proc.StandardError.ReadToEndAsync();

        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();

        string stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg exited with code {proc.ExitCode} for '{Path.GetFileName(path)}': {FfmpegSaid.Short(stderr)}");

        byte[] bytes = ms.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException(
                $"ffmpeg returned no audio for '{Path.GetFileName(path)}': {FfmpegSaid.Short(stderr)}");

        int sampleCount = bytes.Length / 4;
        var samples = new float[sampleCount];
        Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * 4);

        double duration = (double)sampleCount / targetSampleRate;
        return new DecodedAudio(samples, targetSampleRate, duration);
    }

    /// <summary>
    /// Decodes like <see cref="Decode"/>, but streams the PCM to <paramref name="onChunk"/> in a
    /// reused buffer instead of materializing the whole file. An 80-minute rip never holds more
    /// than one chunk in memory. Chunks are whole samples (a multiple of 4 bytes).
    /// </summary>
    public static void DecodeStreaming(
        string path, int targetSampleRate, Action<ReadOnlyMemory<float>> onChunk,
        double? startSec = null, double? lengthSec = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (startSec is double s)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(s.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        if (lengthSec is double l)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(l.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(targetSampleRate.ToString());
        psi.ArgumentList.Add("-f");  psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start ffmpeg ({FfmpegPath})");

        var stderrTask = proc.StandardError.ReadToEndAsync();

        var bytes = new byte[1 << 16];
        var floats = new float[bytes.Length / 4];
        int carry = 0; // bytes of a partial trailing sample kept for the next read
        long totalBytes = 0;
        var stdout = proc.StandardOutput.BaseStream;
        int read;
        while ((read = stdout.Read(bytes, carry, bytes.Length - carry)) > 0)
        {
            int available = carry + read;
            int whole = available / 4 * 4;
            if (whole > 0)
            {
                Buffer.BlockCopy(bytes, 0, floats, 0, whole);
                onChunk(floats.AsMemory(0, whole / 4));
                totalBytes += whole;
            }
            carry = available - whole;
            if (carry > 0)
                Buffer.BlockCopy(bytes, whole, bytes, 0, carry);
        }
        proc.WaitForExit();

        string stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"ffmpeg exited with code {proc.ExitCode} for '{Path.GetFileName(path)}': {FfmpegSaid.Short(stderr)}");
        if (totalBytes == 0)
            throw new InvalidOperationException(
                $"ffmpeg returned no audio for '{Path.GetFileName(path)}': {FfmpegSaid.Short(stderr)}");
    }
}

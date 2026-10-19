using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Fingerprint;

namespace RPM.Core.Recording;

/// <summary>
/// Applies a session's <see cref="RestoreSettings"/> to its album, off the UI thread and into a new
/// file. The raw album is never changed, so a change to the settings only needs a new render.
///
/// <para>The same <see cref="RestoreChain"/> the monitor uses does the work here. Because the chain
/// is one type, the preview and the final cut always use the same code.</para>
/// </summary>
public static class VinylRestorer
{
    /// <summary>Override to point at a specific ffmpeg; defaults to the bundled-then-PATH lookup.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>The processed album inside a session folder, beside the raw one.</summary>
    public const string RestoredFileName = "album-restored.flac";

    private const int Rate = 44100;
    private const int Channels = 2;
    private const int BlockFrames = 8192;

    /// <summary>
    /// Renders <paramref name="sourcePath"/> through <paramref name="settings"/> into
    /// <paramref name="destPath"/>. <paramref name="progress"/> is reported 0..1 by frames written.
    ///
    /// <para>The gate delays its output. That delay is removed here, not left in the file. The track
    /// boundaries are found on the raw waveform and applied to this file, so a shifted album would
    /// move every cut by the same amount.</para>
    /// </summary>
    public static async Task RenderAsync(
        string sourcePath, RestoreSettings settings, string destPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        double totalSeconds = DurationOf(sourcePath);
        var chain = new RestoreChain(settings, Rate, Channels);

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        Process? encoder = null;
        try
        {
            encoder = StartEncoder(destPath);
            var stdin = encoder.StandardInput.BaseStream;
            var errorTask = encoder.StandardError.ReadToEndAsync(CancellationToken.None);

            using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options
            {
                Source = sourcePath,
                SampleRate = Rate,
                Channels = Channels,
                Label = Path.GetFileNameWithoutExtension(sourcePath),
            });

            var buffer = new float[BlockFrames * Channels];
            var pcm = new byte[buffer.Length * sizeof(short)];
            int skip = chain.LatencyFrames;
            long framesOut = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int frames = source.Read(buffer, BlockFrames);
                if (frames <= 0)
                {
                    if (source.State is PcmState.Waiting) { await Task.Delay(3, ct); continue; }
                    break;
                }

                chain.Process(buffer, frames);

                // Drop the chain's own latency off the front, so the restored album lines up with the
                // raw one sample for sample.
                int dropped = Math.Min(skip, frames);
                skip -= dropped;
                framesOut += await WriteAsync(stdin, buffer, dropped, frames - dropped, pcm, ct);
                progress?.Report(totalSeconds <= 0 ? 0 : Math.Min(1, framesOut / (double)Rate / totalSeconds));
            }

            // Flush the chain's tail. It holds the same number of frames that were skipped at the start.
            int tail = chain.LatencyFrames;
            while (tail > 0)
            {
                int frames = Math.Min(BlockFrames, tail);
                Array.Clear(buffer, 0, frames * Channels);
                chain.Process(buffer, frames);
                int dropped = Math.Min(skip, frames);
                skip -= dropped;
                await WriteAsync(stdin, buffer, dropped, frames - dropped, pcm, ct);
                tail -= frames;
            }

            stdin.Flush();
            stdin.Close();
            await encoder.WaitForExitAsync(ct);

            if (encoder.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg failed writing the restored album: {FfmpegSaid.Short(await errorTask)}");

            progress?.Report(1);
        }
        catch
        {
            // A half-rendered album must never be mistaken for a finished one: the splitter would cut
            // a record that stops in the middle.
            try { if (encoder is { HasExited: false }) encoder.Kill(); } catch { }
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            throw;
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    /// <summary>What a preview render produced: the file to play, and how many clicks were repaired
    /// in it. <see cref="Repairs"/> is zero when click repair is off.</summary>
    /// <param name="Repairs">Clicks repaired in the previewed span.</param>
    /// <param name="RepairedPercent">How much of the span the repairs cover. Read it with the count:
    /// a hundred repairs sounds like a lot, but they may cover only a small part of the record.</param>
    public readonly record struct Preview(string Path, int Repairs, double RepairedPercent);

    /// <summary>Renders a short span through the chain into a temp WAV and returns its path. The
    /// caller deletes it when the preview is over.</summary>
    public static async Task<Preview> RenderPreviewAsync(
        string sourcePath, double startSeconds, double lengthSeconds,
        RestoreSettings settings, CancellationToken ct)
    {
        var chain = new RestoreChain(settings, Rate, Channels);
        string destPath = Path.Combine(Path.GetTempPath(), $"rpm-preview-{Guid.NewGuid():N}.wav");

        // Decode the latency ahead of the span too, so the preview starts at the requested time and
        // not while the gate is still filling up.
        double lead = chain.LatencyFrames / (double)Rate;
        double from = Math.Max(0, startSeconds - lead);

        using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options
        {
            Source = sourcePath,
            SampleRate = Rate,
            Channels = Channels,
            StartSeconds = from,
            LengthSeconds = lengthSeconds + (startSeconds - from),
        });

        var encoder = StartEncoder(destPath, wav: true);
        try
        {
            var stdin = encoder.StandardInput.BaseStream;
            var errorTask = encoder.StandardError.ReadToEndAsync(CancellationToken.None);
            var buffer = new float[BlockFrames * Channels];
            var pcm = new byte[buffer.Length * sizeof(short)];
            int skip = chain.LatencyFrames + (int)((startSeconds - from) * Rate);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int frames = source.Read(buffer, BlockFrames);
                if (frames <= 0)
                {
                    if (source.State is PcmState.Waiting) { await Task.Delay(3, ct); continue; }
                    break;
                }
                chain.Process(buffer, frames);
                int dropped = Math.Min(skip, frames);
                skip -= dropped;
                await WriteAsync(stdin, buffer, dropped, frames - dropped, pcm, ct);
            }

            stdin.Flush();
            stdin.Close();
            await encoder.WaitForExitAsync(ct);
            if (encoder.ExitCode != 0)
                throw new InvalidOperationException(
                    $"ffmpeg failed writing the preview: {FfmpegSaid.Short(await errorTask)}");

            // Read from the chain, not from ffmpeg's log output. The chain counts its own repairs.
            long span = Math.Max(1, (long)(lengthSeconds * Rate));
            return new Preview(
                destPath, chain.ClicksRepaired, 100.0 * chain.ClickSamplesRepaired / span);
        }
        catch
        {
            try { if (!encoder.HasExited) encoder.Kill(); } catch { }
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            throw;
        }
        finally { encoder.Dispose(); }
    }

    /// <summary>Decodes the given stretch and measures its noise floor.</summary>
    public static async Task<NoiseProfile> MeasureProfileAsync(
        string sourcePath, double startSeconds, double lengthSeconds, CancellationToken ct)
    {
        using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options
        {
            Source = sourcePath,
            SampleRate = Rate,
            Channels = Channels,
            StartSeconds = startSeconds,
            LengthSeconds = lengthSeconds,
        });

        var all = new List<float>((int)(lengthSeconds * Rate * Channels));
        var buffer = new float[BlockFrames * Channels];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int frames = source.Read(buffer, BlockFrames);
            if (frames <= 0)
            {
                if (source.State is PcmState.Waiting) { await Task.Delay(3, ct); continue; }
                break;
            }
            all.AddRange(buffer.AsSpan(0, frames * Channels));
        }

        return NoiseProfile.Measure(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(all), Channels, Rate);
    }

    /// <summary>Writes <paramref name="frames"/> frames of <paramref name="buffer"/> starting at
    /// frame <paramref name="from"/> as 16-bit PCM. Returns the frames written.</summary>
    /// <remarks>The latency skip is applied by the caller rather than here: a ref parameter is not
    /// allowed on an async method, and a counter passed by value would not carry across blocks.</remarks>
    private static async Task<int> WriteAsync(
        Stream stdin, float[] buffer, int from, int frames, byte[] pcm, CancellationToken ct)
    {
        if (frames <= 0) return 0;

        int b = 0;
        for (int i = from * Channels; i < (from + frames) * Channels; i++)
        {
            short v = (short)(Math.Clamp(buffer[i], -1f, 1f) * short.MaxValue);
            pcm[b++] = (byte)(v & 0xFF);
            pcm[b++] = (byte)((v >> 8) & 0xFF);
        }
        await stdin.WriteAsync(pcm.AsMemory(0, b), ct);
        return frames;
    }

    private static Process StartEncoder(string outputPath, bool wav = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(Rate.ToString());
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(Channels.ToString());
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
        if (!wav)
        {
            psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("flac");
            psi.ArgumentList.Add("-compression_level"); psi.ArgumentList.Add("5");
        }
        psi.ArgumentList.Add(outputPath);

        return Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg to restore the album");
    }

    private static double DurationOf(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            return f.Properties?.Duration.TotalSeconds ?? 0;
        }
        catch { return 0; }
    }
}

using System.Diagnostics;
using System.Globalization;
using RPM.Fingerprint;

namespace RPM.Core.Audio;

/// <summary>Decodes a whole file to mono float samples for offline analysis. This is the one place in the
/// app that wants a track in memory rather than a block at a time. ffmpeg does the decoding, the downmix
/// and the resampling, the same way it does for playback. So anything the player can open, the analyser can
/// read.
/// </summary>
public static class PcmDecoder
{
    /// <summary>Reads <paramref name="path"/> as mono float PCM at <paramref name="sampleRate"/>. Returns
    /// an empty array when the file will not decode. A broken file must not stop a mix. It just gets no
    /// analysis.
    /// </summary>
    public static float[] DecodeMono(string path, int sampleRate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var psi = new ProcessStartInfo(ToolPaths.Ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("f32le");
        psi.ArgumentList.Add("-");

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null)
                return [];
            _ = proc.StandardError.ReadToEndAsync(ct);   // drain, or a full pipe stalls the decode

            using var buffer = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(buffer, 1 << 16);
            proc.WaitForExit();

            var bytes = buffer.GetBuffer();
            int samples = (int)(buffer.Length / sizeof(float));
            var mono = new float[samples];
            Buffer.BlockCopy(bytes, 0, mono, 0, samples * sizeof(float));
            return mono;
        }
        catch (OperationCanceledException) { return []; }
        catch { return []; }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(); } catch { }
            proc?.Dispose();
        }
    }
}

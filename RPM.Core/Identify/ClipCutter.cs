using System.Diagnostics;
using RPM.Fingerprint;

namespace RPM.Core.Identify;

/// <summary>
/// Cuts a short mp3 clip out of an audio file with ffmpeg. The clip is the input for the paid AudD
/// layer, which recognises audio bytes rather than fingerprints. A 15 s slice from inside the song
/// (past the intro) at 128 kbps is about 240 KB.
/// </summary>
public static class ClipCutter
{
    public const double ClipSeconds = 15;

    /// <summary>A clip of a whole file that is one track.</summary>
    public static byte[] CutClip(string path, double durationSeconds) => CutClip(path, 0, durationSeconds);

    /// <summary>
    /// A clip of one track inside a longer file, such as a recorded side that has not been cut yet:
    /// the track runs from <paramref name="trackStartSeconds"/> for <paramref name="durationSeconds"/>.
    /// </summary>
    public static byte[] CutClip(string path, double trackStartSeconds, double durationSeconds)
    {
        double start = ClipStart(trackStartSeconds, durationSeconds);

        string tempPath = Path.Combine(Path.GetTempPath(), $"md-clip-{Guid.NewGuid():N}.mp3");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolPaths.Ffmpeg,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add(start.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(ClipSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("128k");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp3");
            psi.ArgumentList.Add(tempPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start ffmpeg for clip");
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg clip failed: {FfmpegSaid.Short(err)}");

            return File.ReadAllBytes(tempPath);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Where the clip starts, in the file's time: a third of the way into the track, past an intro or
    /// a fade, but early enough that the whole clip stays inside the track. A track shorter than a
    /// clip starts at its own beginning.
    /// </summary>
    public static double ClipStart(double trackStartSeconds, double durationSeconds)
        => trackStartSeconds
           + Math.Max(0, Math.Min(durationSeconds / 3, Math.Max(0, durationSeconds - ClipSeconds - 1)));
}

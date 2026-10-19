namespace RPM.Fingerprint.Chroma;

/// <summary>
/// Top-level facade for the Chromaprint-based fingerprint pipeline.
///
/// Shells out to <c>fpcalc</c> (libchromaprint) to produce REAL Chromaprint sub-fingerprints,
/// giving us bit-for-bit compatibility with AcoustID's stored data and the AcoustID API.
/// This means we can:
///   • Import AcoustID's public database dump and have our matcher work on those rows directly
///   • Submit fingerprints back to AcoustID
///   • Query AcoustID's free API with the exact same fingerprints we use locally
/// </summary>
public sealed record ChromaFingerprinterConfig
{
    // There are no tunables yet, because fpcalc owns the algorithm parameters. This is kept as a record so
    // that future additions (for example a -length override) have a place to go.
}

public static class ChromaFingerprinter
{
    public sealed record Result(double DurationSeconds, ChromaFingerprint Fingerprint);

    /// <summary>
    /// Shell out to fpcalc, get the raw Chromaprint sub-fingerprints, wrap in a ChromaFingerprint.
    /// </summary>
    public static Result FingerprintFile(string path, ChromaFingerprinterConfig cfg)
    {
        var fp = FpcalcRunner.Run(path);
        return new Result(fp.DurationSeconds, new ChromaFingerprint(fp.SubFingerprints));
    }

    /// <summary>
    /// Slice variant: extracts a slice via ffmpeg into a temp file, then runs fpcalc on it.
    /// fpcalc doesn't have a built-in slice option so we materialize a temp .wav.
    /// </summary>
    public static Result FingerprintFileSlice(string path, ChromaFingerprinterConfig cfg, double startSec, double lengthSec)
    {
        // Decode the slice via ffmpeg into a temp WAV file, then fpcalc that.
        // We use WAV (lossless) so fpcalc gets clean audio.
        string tempPath = Path.Combine(Path.GetTempPath(), $"mdfp-slice-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Decoding.FfmpegDecoder.FfmpegPath,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(lengthSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add(tempPath);

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start ffmpeg for slice");
            string err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg slice failed: {FfmpegSaid.Short(err)}");

            return FingerprintFile(tempPath, cfg);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

}

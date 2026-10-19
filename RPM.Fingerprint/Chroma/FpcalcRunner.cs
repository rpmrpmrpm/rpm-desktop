using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using RPM.Fingerprint.Decoding;

namespace RPM.Fingerprint.Chroma;

/// <summary>Why a run of fpcalc ended without a fingerprint.</summary>
public enum FpcalcFailure
{
    /// <summary>fpcalc opened the file, read it, and there was no fingerprint to be had. This is the
    /// only kind that is a finding about the file, and so the only kind worth writing down.</summary>
    Unreadable,

    /// <summary>fpcalc could not be run, or would not take the arguments it was given. A broken
    /// install or our own bug; it says the same thing about every file in the library.</summary>
    Unavailable,

    /// <summary>fpcalc could not open the file. A drive that went away, another program holding it,
    /// or a row that outlived its bytes: all of these may be untrue again by the next pass.</summary>
    Unopened,

    /// <summary>fpcalc failed in a way this code does not recognise. Deliberately not a verdict:
    /// an unfamiliar error is a reason to look, not a reason to condemn a file for good.</summary>
    Unknown,
}

/// <summary>An fpcalc run that produced no fingerprint, and what kind of failure it was.</summary>
public sealed class FpcalcException(FpcalcFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FpcalcFailure Failure { get; } = failure;

    /// <summary>True when the failure is a fact about the file itself, and so is worth remembering
    /// against it. False for everything else, which must be left to be tried again.</summary>
    public bool IsVerdictOnFile => Failure == FpcalcFailure.Unreadable;
}

/// <summary>
/// Produces real Chromaprint fingerprints by shelling out to the local <c>fpcalc</c> binary
/// (the official Chromaprint CLI, distributed via libchromaprint / acoustid).
///
/// We use <c>fpcalc -json -raw &lt;file&gt;</c> which returns the raw uint32 sub-fingerprint
/// sequence directly. These are the same values that AcoustID's database stores. This gives us
/// **bit-for-bit Chromaprint compatibility** without having to reimplement the algorithm,
/// the classifier filters, the quantization thresholds, or the compressed binary format.
///
/// Bundled `fpcalc` ships at ~500 KB. The desktop app already bundles ffmpeg; this is the
/// same kind of native binary dependency, just smaller.
/// </summary>
public static class FpcalcRunner
{
    /// <summary>Override to point at a specific fpcalc; defaults to the bundled-then-PATH lookup.</summary>
    public static string FpcalcPath { get; set; } = ToolPaths.Fpcalc;

    public sealed record Result(double DurationSeconds, uint[] SubFingerprints);

    /// <summary>
    /// Returns the base64-encoded compressed Chromaprint fingerprint, as the AcoustID API expects.
    /// This runs fpcalc a second time, unlike <see cref="Run"/>. The encoding matches AcoustID's required
    /// input format exactly. A managed implementation of the compressor would avoid the second run, but
    /// it is not written. Use this only when the base64 form is needed.
    /// </summary>
    public sealed record CompressedResult(double DurationSeconds, string Base64Fingerprint);

    public static CompressedResult RunCompressed(string filePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FpcalcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-json");
        // No -raw → fpcalc emits the base64 compressed form, ready for AcoustID API.
        psi.ArgumentList.Add("-length");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add(filePath);

        using var proc = Start(psi);
        var stderrTask = proc.StandardError.ReadToEndAsync();
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (ParseCompressed(stdout) is { } answer)
            return answer;
        throw Failed(filePath, proc.ExitCode, stderr);
    }

    /// <param name="lowPriority">Run fpcalc below normal CPU priority. This is for the library-wide
    /// background pass, which must not slow the user's work. Interactive recognition leaves it off.</param>
    public static Result Run(string filePath, bool lowPriority = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FpcalcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-json");
        psi.ArgumentList.Add("-raw");
        // Use -signed for byte-compatibility with AcoustID's pg_acoustid database dump,
        // which stores sub-fingerprints as PostgreSQL int4[] (signed 32-bit). Same bit
        // patterns either way, but using -signed makes our JSON parsing line up with
        // their dump format trivially.
        psi.ArgumentList.Add("-signed");
        // Override the default -length 120 truncation. We want the FULL song fingerprinted
        // so verify-all and clip identification cover the entire track, not just the first
        // 2 minutes.
        psi.ArgumentList.Add("-length");
        psi.ArgumentList.Add("0");
        psi.ArgumentList.Add(filePath);

        using var proc = Start(psi);

        if (lowPriority)
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* the process may have exited already; the fingerprint still counts */ }

        var stderrTask = proc.StandardError.ReadToEndAsync();
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        string stderr = stderrTask.GetAwaiter().GetResult();

        // What came back decides, not what the exit code says about it. A great many FLACs in a real
        // collection carry an ID3v1 tag glued on the end, left there by some ripper or tagger years
        // ago; ffmpeg reads the whole song, reaches those last 128 bytes, calls them a frame header
        // that is not one, and fails. fpcalc passes that code up having already printed a complete
        // fingerprint of the complete song. Refusing it would be throwing away a good reading over a
        // tag. So the reading is taken when there is one, and the exit code is only consulted for
        // what to say when there is not.
        if (ParseRaw(stdout) is { } answer)
            return answer;
        throw Failed(filePath, proc.ExitCode, stderr);
    }

    /// <summary>
    /// Reads what fpcalc printed, or nothing at all when it printed no whole reading.
    ///
    /// <para>Parse: <c>{ "duration": 294.0, "fingerprint": [123, 456, ...] }</c></para>
    /// </summary>
    private static Result? ParseRaw(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("duration", out var durationEl) ||
                !root.TryGetProperty("fingerprint", out var fpArray) ||
                fpArray.ValueKind != JsonValueKind.Array ||
                fpArray.GetArrayLength() == 0)
                return null;

            var subFps = new uint[fpArray.GetArrayLength()];
            int i = 0;
            foreach (var element in fpArray.EnumerateArray())
            {
                // Chromaprint sub-fingerprints are uint32, but JSON numbers may parse as int64.
                // Cast safely. Chromaprint fingerprints span the full uint32 range.
                long v = element.GetInt64();
                subFps[i++] = unchecked((uint)v);
            }
            return new Result(durationEl.GetDouble(), subFps);
        }
        catch (JsonException)
        {
            // Cut off mid-object, which is what a run that died partway through looks like.
            return null;
        }
    }

    /// <summary>The same reading of stdout, for the compressed form AcoustID wants.</summary>
    private static CompressedResult? ParseCompressed(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("duration", out var durationEl) ||
                !root.TryGetProperty("fingerprint", out var fpEl) ||
                fpEl.GetString() is not { Length: > 0 } fingerprint)
                return null;
            return new CompressedResult(durationEl.GetDouble(), fingerprint);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts fpcalc, or says plainly that there is no fpcalc to start.</summary>
    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi)
                ?? throw new FpcalcException(FpcalcFailure.Unavailable, $"failed to start fpcalc ({psi.FileName})");
        }
        catch (Win32Exception ex)
        {
            // Nothing at the resolved path: the bundled copy never shipped with this build, or
            // ToolPaths fell through to the bare name and PATH has no fpcalc either.
            throw new FpcalcException(
                FpcalcFailure.Unavailable, $"failed to start fpcalc ({psi.FileName}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Works out what kind of failure this was, from what fpcalc said about it.
    ///
    /// <para>The exit code cannot do this on its own. fpcalc answers 2 both for a file it could not
    /// read and for an option it did not recognise. Those two want opposite treatment. One is a
    /// finding to remember against the file. The other is a bug that would otherwise be written down
    /// against every file in the library. So the message decides.</para>
    ///
    /// <para>Anything unfamiliar is <see cref="FpcalcFailure.Unknown"/> rather than
    /// <see cref="FpcalcFailure.Unreadable"/>. Guessing wrong in that direction is cheap, because the file
    /// is read again on the next pass. Guessing wrong in the other direction marks a track as
    /// unfingerprintable for as long as nobody edits it.</para>
    /// </summary>
    internal static FpcalcFailure Classify(string stderr) =>
        Mentions(stderr, "Could not open the input file") ? FpcalcFailure.Unopened :
        Mentions(stderr, "Could not find any audio stream") ||
        Mentions(stderr, "Error decoding the audio source") ||
        Mentions(stderr, "Could not calculate fingerprint") ||
        Mentions(stderr, "Unable to calculate fingerprint") ? FpcalcFailure.Unreadable :
        Mentions(stderr, "Unknown option") ||
        Mentions(stderr, "No input files") ||
        Mentions(stderr, "Missing argument") ||
        Mentions(stderr, "Invalid value") ? FpcalcFailure.Unavailable :
        FpcalcFailure.Unknown;

    private static FpcalcException Failed(string filePath, int exitCode, string stderr)
    {
        // Classified on the whole of it, reported from a little of it. fpcalc carries libavcodec, so
        // a damaged file makes it print the same per-packet flood that ffmpeg does. The phrase that says
        // which kind of failure this was can be anywhere in there.
        var said = FfmpegSaid.Short(stderr);
        return new FpcalcException(Classify(stderr), exitCode != 0
            ? $"fpcalc exited with code {exitCode} for '{Path.GetFileName(filePath)}': {said}"
            : $"fpcalc returned no output for '{Path.GetFileName(filePath)}': {said}");
    }

    private static bool Mentions(string stderr, string phrase) =>
        stderr.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}

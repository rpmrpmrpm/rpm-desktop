using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using RPM.Fingerprint;

namespace RPM.Core.Metadata;

/// <summary>The audio stream facts that ffmpeg reports for a file. Every field is optional: a partial
/// result is still more useful than none.</summary>
public sealed record MediaFacts(
    double DurationSeconds,
    string? Codec,
    int SampleRate,
    int Channels,
    int BitrateKbps);

/// <summary>
/// The second opinion on a file's audio stream, read with ffmpeg rather than TagLib.
///
/// <para>
/// TagLib parses containers itself. A file laid out in a way it does not expect makes it throw for the
/// whole file, not only for the part it cannot follow. The track then has no length, no sample rate
/// and no channel count. Such a track cannot be laid out, queued or scrubbed, even though the file
/// plays correctly. ffmpeg reads these files without trouble.
/// </para>
///
/// <para>
/// ffmpeg is asked only when TagLib returns nothing. Each probe starts a subprocess, which is slow
/// across a large library, so TagLib goes first. The probe reads only the header: no output file is
/// named, so ffmpeg prints what it found and exits without decoding any frames.
/// </para>
/// </summary>
public static class MediaProbe
{
    /// <summary>Can be replaced in tests. By default the bundled ffmpeg is used, or else the one on
    /// PATH.</summary>
    public static string FfmpegPath { get; set; } = ToolPaths.Ffmpeg;

    /// <summary>How long ffmpeg gets to answer before the file is written off as unreadable. A header
    /// read takes milliseconds, so a file that takes this long is not going to answer.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // "  Duration: 00:03:47.85, start: 0.000000, bitrate: 1010 kb/s"
    private static readonly Regex DurationLine = new(
        @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

    // "  Stream #0:0[0x1](und): Audio: eac3 (ec-3 / 0x332D6365), 48000 Hz, 5.1(side), fltp, 768 kb/s"
    private static readonly Regex AudioStreamLine = new(
        @"Stream #\d+:\d+.*?:\s*Audio:\s*(?<codec>[^\s,(]+)(?<rest>[^\r\n]*)", RegexOptions.Compiled);

    /// <summary>The result of asking ffmpeg. "ffmpeg found no audio in this file" means the file is
    /// bad. "There is no ffmpeg" says nothing about the file. The two must not be mixed up.</summary>
    public enum Outcome
    {
        /// <summary>ffmpeg found an audio stream.</summary>
        Found,

        /// <summary>ffmpeg ran and could see no audio, or it gave up. The file is the problem.</summary>
        NoAudio,

        /// <summary>ffmpeg is not installed or would not start. Nothing is known about the file, so
        /// nothing may be concluded about it.</summary>
        Unavailable,
    }

    /// <summary>
    /// What ffmpeg makes of <paramref name="path"/>, or null when it will not start, will not answer,
    /// or found no audio in there. Never throws.
    /// </summary>
    public static MediaFacts? Read(string path) => Probe(path, out var facts) is Outcome.Found ? facts : null;

    /// <summary>
    /// <see cref="Read"/>, and also whether a null answer was the file's fault. Callers that must
    /// decide "corrupt or not" need that. Callers that only want the facts can use <see cref="Read"/>.
    /// Never throws.
    /// </summary>
    public static Outcome Probe(string path, out MediaFacts? facts)
    {
        facts = null;
        string output;
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);

            using var proc = Process.Start(psi);
            if (proc is null)
                return Outcome.Unavailable;

            // ffmpeg with no output file prints what it found and exits non-zero ("At least one
            // output file must be specified"). The exit code is ignored. The stream description
            // on stderr is what is needed.
            var stderr = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit((int)Patience.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                // Ten seconds is too long for a header read, so the file will not answer. The
                // timeout is about the file, not about ffmpeg.
                return Outcome.NoAudio;
            }
            output = stderr.GetAwaiter().GetResult();
        }
        catch
        {
            return Outcome.Unavailable;   // no ffmpeg on this machine, or it would not start
        }

        var stream = AudioStreamLine.Match(output);
        if (!stream.Success)
            return Outcome.NoAudio;   // ffmpeg could not see audio in it either

        var rest = stream.Groups["rest"].Value;
        facts = new MediaFacts(
            Duration(output),
            stream.Groups["codec"].Value,
            Number(rest, @"(\d+)\s*Hz"),
            ChannelsIn(rest),
            Number(rest, @"(\d+)\s*kb/s"));
        return Outcome.Found;
    }

    private static double Duration(string output)
    {
        var m = DurationLine.Match(output);
        if (!m.Success)
            return 0;   // a live stream, or a header too damaged to say

        var inv = CultureInfo.InvariantCulture;
        return int.Parse(m.Groups[1].Value, inv) * 3600
             + int.Parse(m.Groups[2].Value, inv) * 60
             + double.Parse(m.Groups[3].Value, inv);
    }

    /// <summary>The channel count behind ffmpeg's layout name. ffmpeg prints "5.1(side)", not
    /// "6".</summary>
    private static int ChannelsIn(string rest) => rest switch
    {
        _ when Contains(rest, "mono") => 1,
        _ when Contains(rest, "stereo") => 2,
        _ when Contains(rest, "7.1.4") => 12,
        _ when Contains(rest, "9.1") => 10,
        _ when Contains(rest, "7.1") => 8,
        _ when Contains(rest, "6.1") => 7,
        _ when Contains(rest, "5.1") => 6,
        _ when Contains(rest, "5.0") => 5,
        _ when Contains(rest, "quad") || Contains(rest, "4.0") => 4,
        _ when Contains(rest, "2.1") => 3,
        // An unnamed layout is printed as its own count: "6 channels".
        _ => Number(rest, @"(\d+)\s*channels"),
    };

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static int Number(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}

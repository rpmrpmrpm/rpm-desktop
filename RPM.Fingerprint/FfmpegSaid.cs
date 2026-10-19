using System.Text.RegularExpressions;

namespace RPM.Fingerprint;

/// <summary>
/// What ffmpeg wrote about a failure, reduced to the useful part.
///
/// <para>ffmpeg's stderr is not one message, it is a continuous log. One damaged AAC frame makes
/// libavcodec write an error per packet (<c>channel element 3.12 is not allocated</c>, then the same
/// sentence about a different element), hundreds of times before the process stops. Without this
/// class the whole output would go into the exception message, and from there into two places: the
/// failure reason stored for the track and shown on the cloud settings page, and the debug output at
/// every app start, because the cloud queue continues where it stopped and reads the failed file
/// again.</para>
///
/// <para>The console formatter indents the lines of a logged exception by splitting on
/// <see cref="Environment.NewLine"/>, and ffmpeg ends its lines with a bare newline. So every line
/// starts at the left margin with no category and no level, and a hundred lines look like another
/// library writing to the console, under one <c>warn:</c> header far above.</para>
///
/// <para>The result is the first few DISTINCT lines. The first lines, because ffmpeg reports a failure
/// starting with the cause: <c>Rematrix is needed between 30 channels and stereo</c> is the line a
/// person can act on, and <c>Error while filtering: Invalid argument</c>, three lines later, is only
/// a result of it. Distinct as defined by <see cref="Shape"/>: the log contexts and the numbers are
/// removed before two lines are compared, so four hundred errors about different channel elements
/// count as one, and the line after them is still reached.</para>
///
/// <para>No decision is made from this result. The callers that read the wording to decide what to
/// do next (<c>CanonicalAudio</c>, on whether to try again as mp4, and
/// <c>FpcalcRunner.Classify</c>, on whether the file or the command line was at fault) get the full
/// stderr and read it before this point.</para>
///
/// <para>Also used for fpcalc, which is not ffmpeg but uses the same libavcodec and writes the same
/// repeated errors.</para>
///
/// <para>Placed next to <see cref="ToolPaths"/> for the same reason: Fingerprint is the leaf
/// assembly, so every caller of these tools can reference it.</para>
/// </summary>
public static partial class FfmpegSaid
{
    /// <summary>How many distinct lines are kept. Enough for a cause and the two lines after it, and
    /// short enough for a log line and a database column.</summary>
    private const int Keep = 3;

    /// <summary>The maximum length of the result before it is cut.</summary>
    private const int Limit = 300;

    /// <summary>
    /// One line of reason from the full stderr. Empty when the tool wrote nothing, which happens when
    /// a process exits without any output.
    /// </summary>
    public static string Short(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return "";

        var kept = new List<string>(Keep);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Split on the line feed only. ffmpeg ends its lines with a line feed and uses a carriage
        // return alone to redraw a progress line, so splitting on the pair would return the whole
        // output as one line.
        foreach (var raw in stderr.Split('\n'))
        {
            var line = Address().Replace(raw, "[$1]").Trim();
            if (line.Length == 0 || !seen.Add(Shape(line)))
                continue;

            kept.Add(line);
            if (kept.Count == Keep)
                break;
        }

        var said = string.Join("; ", kept);
        if (said.Length <= Limit)
            return said;

        // Never cut between the halves of a surrogate pair: a filename can contain any character,
        // and half a character must not go into a log or a database column.
        int cut = char.IsHighSurrogate(said[Limit - 1]) ? Limit - 1 : Limit;
        return said[..cut].TrimEnd() + "...";
    }

    /// <summary>
    /// A line with everything that changes between repeats removed: the log contexts and every
    /// number. What is left is the sentence, and two lines with the same sentence count as one.
    /// </summary>
    private static string Shape(string line) =>
        Numbers().Replace(Context().Replace(line, ""), "#");

    /// <summary>Removes the pointer from a log context, which is a different number in every run and
    /// is not useful: <c>[aac @ 0000029d732739c0]</c> becomes <c>[aac]</c>. The tag stays, because it
    /// says which stream or filter reported the error.</summary>
    [GeneratedRegex(@"\[\s*([^\]@]*?)\s*@\s*(?:0x)?[0-9a-fA-F]{4,}\s*\]")]
    private static partial Regex Address();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Context();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}

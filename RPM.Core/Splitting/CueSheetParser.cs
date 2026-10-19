using System.Globalization;

namespace RPM.Core.Splitting;

/// <summary>One TRACK entry of a cue sheet. <see cref="Index01Seconds"/> is the track start
/// (INDEX 01); INDEX 00 (pregap start) is only used when no INDEX 01 exists.</summary>
public sealed record CueTrack(uint Number, string? Title, string? Performer, double Index01Seconds);

/// <summary>Parsed cue sheet: album-level TITLE/PERFORMER plus the ordered tracks.</summary>
public sealed record CueSheet(
    string? Performer,
    string? Title,
    string? AudioFileName,
    IReadOnlyList<CueTrack> Tracks);

/// <summary>
/// Small cue sheet parser for the single FILE album rip layout written by EAC and XLD.
/// It takes text and returns records. It does no file I/O, so tests can call it directly.
/// </summary>
public static class CueSheetParser
{
    /// <summary>Parses cue text. Returns null when the sheet has no usable TRACK/INDEX data.</summary>
    public static CueSheet? Parse(string cueText)
    {
        if (string.IsNullOrWhiteSpace(cueText))
            return null;

        string? albumPerformer = null, albumTitle = null, fileName = null;
        var tracks = new List<CueTrack>();

        uint number = 0;
        string? title = null, performer = null;
        double? index01 = null, index00 = null;
        bool inTrack = false;

        void FlushTrack()
        {
            if (!inTrack) return;
            var start = index01 ?? index00;
            if (start is not null)
                tracks.Add(new CueTrack(number, title, performer, start.Value));
            title = null; performer = null; index01 = null; index00 = null; inTrack = false;
        }

        foreach (var rawLine in cueText.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0) continue;

            if (StartsWithKeyword(line, "REM")) continue;

            if (StartsWithKeyword(line, "FILE"))
            {
                // A sheet with one FILE per track is not a single album file. Keep only the first FILE
                // line.
                fileName ??= ExtractQuoted(line) ?? SecondToken(line);
                continue;
            }

            if (StartsWithKeyword(line, "TRACK"))
            {
                FlushTrack();
                inTrack = true;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                number = parts.Length >= 2 && uint.TryParse(parts[1], out var n) ? n : (uint)(tracks.Count + 1);
                continue;
            }

            if (StartsWithKeyword(line, "TITLE"))
            {
                var v = ExtractQuoted(line) ?? RestAfterKeyword(line, "TITLE");
                if (inTrack) title = v; else albumTitle = v;
                continue;
            }

            if (StartsWithKeyword(line, "PERFORMER"))
            {
                var v = ExtractQuoted(line) ?? RestAfterKeyword(line, "PERFORMER");
                if (inTrack) performer = v; else albumPerformer = v;
                continue;
            }

            if (inTrack && StartsWithKeyword(line, "INDEX"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && TryParseMsf(parts[2], out var seconds))
                {
                    if (parts[1] == "01") index01 = seconds;
                    else if (parts[1] == "00") index00 = seconds;
                }
            }
        }
        FlushTrack();

        return tracks.Count == 0 ? null : new CueSheet(albumPerformer, albumTitle, fileName, tracks);
    }

    /// <summary>Turns a parsed sheet into a split plan for the given audio file. Each track ends
    /// where the next one starts. The last track runs to <paramref name="totalSeconds"/>.</summary>
    public static SplitPlan ToPlan(CueSheet cue, string audioPath, string extension, double totalSeconds)
    {
        var ordered = cue.Tracks.OrderBy(t => t.Index01Seconds).ToList();
        var tracks = new List<SplitTrack>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            double end = i + 1 < ordered.Count ? ordered[i + 1].Index01Seconds : totalSeconds;
            // Skip entries with zero or negative length.
            if (end <= t.Index01Seconds) continue;
            tracks.Add(new SplitTrack(
                t.Index01Seconds, end,
                t.Title,
                t.Performer ?? cue.Performer,
                t.Number == 0 ? (uint)(tracks.Count + 1) : t.Number,
                Confidence: 1.0,
                BoundarySource.CueSheet));
        }
        return new SplitPlan(audioPath, extension, totalSeconds, cue.Title, cue.Performer, Year: 0, ReleaseMbid: null, tracks);
    }

    /// <summary>MM:SS:FF, where FF is a CD frame (75 frames per second). MM can be larger than 99
    /// on a long rip.</summary>
    private static bool TryParseMsf(string msf, out double seconds)
    {
        seconds = 0;
        var parts = msf.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)) return false;
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var f)) return false;
        if (s >= 60 || f >= 75) return false;
        seconds = m * 60 + s + f / 75.0;
        return true;
    }

    private static bool StartsWithKeyword(string line, string keyword) =>
        line.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == keyword.Length || line[keyword.Length] == ' ');

    private static string? ExtractQuoted(string line)
    {
        int first = line.IndexOf('"');
        if (first < 0) return null;
        int last = line.LastIndexOf('"');
        return last > first ? line[(first + 1)..last] : null;
    }

    private static string? RestAfterKeyword(string line, string keyword)
    {
        var rest = line.Length > keyword.Length ? line[keyword.Length..].Trim() : "";
        return rest.Length == 0 ? null : rest;
    }

    private static string? SecondToken(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }
}

namespace RPM.Core.Import;

/// <summary>Makes strings safe to use as a single file/folder name on Windows, macOS and Linux.</summary>
public static class PathSanitizer
{
    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxSegmentLength = 120;

    /// <summary>
    /// Sanitises one path segment: path separators become spaces ("AC/DC" → "AC DC"), other
    /// invalid/control characters are removed ("What?!" → "What!"), whitespace collapses,
    /// trailing dots/spaces are trimmed, Windows reserved names dodged, length capped.
    /// Returns <paramref name="fallback"/> when nothing survives.
    /// </summary>
    public static string SanitizeSegment(string? segment, string fallback = "Unknown")
    {
        if (string.IsNullOrWhiteSpace(segment))
            return fallback;

        var sb = new System.Text.StringBuilder(segment.Length);
        bool lastWasSpace = false;
        foreach (char c in segment)
        {
            char ch = c;
            if (ch is '/' or '\\' || char.IsWhiteSpace(ch))
                ch = ' ';
            else if (char.IsControl(ch) || Array.IndexOf(InvalidChars, ch) >= 0)
                continue;
            if (ch == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }
            sb.Append(ch);
        }

        string result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0)
            return fallback;
        if (result.Length > MaxSegmentLength)
            result = result[..MaxSegmentLength].TrimEnd(' ', '.');
        if (WindowsReservedNames.Contains(result))
            result += "_";
        return result;
    }
}

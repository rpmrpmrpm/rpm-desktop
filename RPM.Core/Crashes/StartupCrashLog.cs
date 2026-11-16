namespace RPM.Core.Crashes;

/// <summary><c>startup-crash.txt</c> in the app's local-data folder. It is a plain-text record of a
/// failure that ended the process. The last-chance handlers on both heads write it, and a person reads
/// it. The crash harvest does not read it.
///
/// <para>Entries are appended, because the run worth reading is usually the run before the one being
/// watched. The file is capped at <see cref="Cap"/> characters. When a new entry would take the file
/// past the cap, the oldest entries are dropped. The newest half of the cap is kept, cut at the start of
/// an entry. Each entry starts on a new line with <c>[</c> and its time. That is where the cut is
/// made.</para>
/// </summary>
public static class StartupCrashLog
{
    public const string FileName = "startup-crash.txt";

    /// <summary>The most the file holds, in characters. That is a few hundred stack traces.</summary>
    internal const int Cap = 256 * 1024;

    /// <summary>Adds one entry. It throws whatever the file system throws. The handlers that call this
    /// are already inside a try block.</summary>
    public static void Append(string folder, string entry)
    {
        var path = Path.Combine(folder, FileName);
        var held = new FileInfo(path);

        if (!held.Exists || held.Length + entry.Length <= Cap)
        {
            File.AppendAllText(path, entry);
            return;
        }

        File.WriteAllText(path, Newest(File.ReadAllText(path)) + entry);
    }

    /// <summary>The entries of <paramref name="text"/> whose time is from <paramref name="from"/> to
    /// <paramref name="to"/>, trimmed. An entry's time is local time to the second, so the range is
    /// widened by one second at each end. An entry whose time cannot be read is left out.</summary>
    internal static IEnumerable<string> Between(string text, DateTimeOffset from, DateTimeOffset to)
    {
        var first = from.LocalDateTime.AddSeconds(-1);
        var last = to.LocalDateTime.AddSeconds(1);

        // Each entry starts with "[" at the start of the text or of a line.
        var start = text.StartsWith('[') ? 0 : text.IndexOf("\n[", StringComparison.Ordinal);
        if (start < 0)
            yield break;
        if (text[start] == '\n')
            start++;

        while (true)
        {
            var next = text.IndexOf("\n[", start, StringComparison.Ordinal);
            var entry = (next < 0 ? text[start..] : text[start..next]).Trim();

            if (entry.Length > 21 && entry[20] == ']'
                && DateTime.TryParseExact(entry[1..20], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var at)
                && at >= first && at <= last)
                yield return entry;

            if (next < 0)
                yield break;
            start = next + 1;
        }
    }

    /// <summary>The newest whole entries of <paramref name="text"/> that fit in half the cap.</summary>
    internal static string Newest(string text)
    {
        var from = Math.Max(0, text.Length - Cap / 2);
        if (from == 0)
            return text;

        var cut = text.IndexOf("\n[", from - 1, StringComparison.Ordinal);
        return cut < 0 ? "" : text[(cut + 1)..];
    }
}

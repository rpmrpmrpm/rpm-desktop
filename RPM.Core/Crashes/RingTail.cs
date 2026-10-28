using RPM.Core.Diagnostics;

namespace RPM.Core.Crashes;

/// <summary>
/// What a finished run's diagnostic ring says about how it ended.
/// </summary>
/// <param name="StartUnixNanos">From the ring header. Names the run together with its process id.</param>
/// <param name="LastUnixNanos">The wall time of the last record the run wrote.</param>
/// <param name="Closing">Whether the run wrote a <see cref="ScopeKind.Closing"/> record, which means it
/// had begun ending normally.</param>
/// <param name="Recent">The last <see cref="RecentLines"/> records of the kinds a person can read
/// (said, throw, input, closing), oldest first, one line each.</param>
public sealed record RingTail(long StartUnixNanos, long LastUnixNanos, bool Closing, IReadOnlyList<string> Recent)
{
    public const int RecentLines = 300;

    private const int Chunk = 4096;

    /// <summary>How many characters of activity a live report carries. The API's
    /// <c>FeedbackLimits.MaxRecent</c>, and the same cap a crash report's recent lines get.</summary>
    public const int MaxActivityChars = 64_000;

    /// <summary>
    /// What the running app has been doing, as one text ready to send with a report the user wrote
    /// while the app is still up. Home folder paths are removed, the same as in a crash report. A
    /// text over <see cref="MaxActivityChars"/> keeps its last characters, because the lines nearest
    /// the problem are the ones at the end.
    /// </summary>
    /// <returns>Null when there is no readable ring or it has nothing a person can read. A report
    /// with no activity log is still a report, so this is never a failure.</returns>
    public static string? ActivityForThisRun(string scopeDirectory, int pid)
    {
        if (ForThisRun(scopeDirectory, pid) is not { Recent.Count: > 0 } tail)
            return null;

        var text = SystemReportPrivacy.WithoutHomeFolders(string.Join('\n', tail.Recent));
        return text.Length > MaxActivityChars ? text[^MaxActivityChars..] : text;
    }

    /// <summary>
    /// The running process's own ring.
    ///
    /// <para>The ring is being written while this reads it. That is safe: the reader stops at the
    /// cursor, so every record it returns was finished before the read began. Records written after
    /// that are not in the tail, which is what "the last things rpm did" means here.</para>
    /// </summary>
    private static RingTail? ForThisRun(string scopeDirectory, int pid)
    {
        var ring = Path.Combine(scopeDirectory, $"scope.{pid}.ring");
        try
        {
            return Read(ring, Path.ChangeExtension(ring, ".names"));
        }
        catch (Exception e) when (
            e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the whole ring. It throws <see cref="IOException"/> or <see
    /// cref="InvalidDataException"/> when the file is not a readable ring. The harvest leaves such a run
    /// alone.
    /// </summary>
    public static RingTail Read(string ringPath, string namesPath)
    {
        using var reader = ScopeReader.Open(ringPath);

        // Open only a table that is there: ScopeNames.Open creates one otherwise.
        using var names = File.Exists(namesPath) ? ScopeNames.Open(namesPath) : null;

        var syncMono = reader.StartMonoNanos;
        var syncUnix = reader.StartUnixNanos;
        long last = syncUnix;
        var closing = false;
        var recent = new Queue<string>(RecentLines);

        var buffer = new ScopeRecord[Chunk];
        for (var from = reader.Oldest; from < reader.Written; from += Chunk)
        {
            var count = reader.Read(from, buffer);
            for (var i = 0; i < count; i++)
            {
                var record = buffer[i];
                var kind = (ScopeKind)record.Kind;

                // The ring clock stops while the machine sleeps. Each record is placed against the last
                // clock sync at or before it.
                if (kind == ScopeKind.ClockSync)
                {
                    syncMono = record.Nanos;
                    syncUnix = record.A;
                }

                var wall = syncUnix + (record.Nanos - syncMono);
                last = wall;

                if (kind == ScopeKind.Closing)
                    closing = true;

                if (Label(kind) is not { } label)
                    continue;

                if (recent.Count == RecentLines)
                    recent.Dequeue();
                recent.Enqueue(Line(wall, label, Shown(kind, names?.Name(record.NameId)), names?.Name(record.Detail)));
            }
        }

        return new RingTail(reader.StartUnixNanos, last, closing, [.. recent]);
    }

    private static string? Label(ScopeKind kind) => kind switch
    {
        ScopeKind.Said => "said",
        ScopeKind.Throw => "throw",
        ScopeKind.Input => "input",
        ScopeKind.Closing => "closing",
        _ => null,
    };

    /// <summary>The name that a record is reported under. The ring records every key pressed in the main
    /// window by name. A report that kept those names would let text typed into a search box be read
    /// back. So a key that types a character is reported as "key". Modifiers, navigation keys and
    /// function keys keep their names, because a stuck modifier is diagnosed from them. The ring file
    /// itself is not changed.
    /// </summary>
    private static string? Shown(ScopeKind kind, string? name) =>
        kind == ScopeKind.Input && name is not null && TypesACharacter(name) ? "key" : name;

    /// <summary>A <c>VirtualKey</c> name that types a character. That is a letter, a digit on the main
    /// keys or the number pad, a number-pad operator, or a punctuation key. Punctuation keys have no name
    /// in the enum, so they are printed as a code.</summary>
    private static bool TypesACharacter(string key) =>
        (key.Length == 1 && char.IsAsciiLetterUpper(key[0]))
        || (key.StartsWith("Number", StringComparison.Ordinal) && char.IsAsciiDigit(key[^1]))
        || key.All(char.IsAsciiDigit)
        || key is "Multiply" or "Add" or "Separator" or "Subtract" or "Decimal" or "Divide";

    /// <summary>Formats one record: the UTC time in yyyy-MM-dd HH:mm:ss.fff form, then the kind, the name
    /// and the detail.</summary>
    private static string Line(long unixNanos, string label, string? name, string? detail)
    {
        var when = DateTimeOffset.FromUnixTimeMilliseconds(unixNanos / 1_000_000).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var line = name is null ? $"{when} {label}" : $"{when} {label} {name}";
        return detail is null ? line : $"{line}: {detail}";
    }
}

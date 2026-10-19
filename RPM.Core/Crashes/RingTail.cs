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

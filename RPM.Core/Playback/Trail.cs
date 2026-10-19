using System.Net.Http;
using System.Text;

namespace RPM.Core.Playback;

/// <summary>
/// What the app did, in order, so that "it does not play" can be explained.
///
/// <para>
/// The play path is a chain of steps that can each fail quietly. A URL is signed, an asset's tracks are
/// loaded, a reader is built and started, and a source is handed to the engine. Each step has a
/// <c>catch</c> that turns a failure into a false or a null, because a music player that throws a dialog at
/// the listener is worse than one that moves on. The cost is a track that does nothing when it is tapped,
/// with no way to find out why. There is not even a message, because a failure that happens in under half a
/// second never gets as far as showing a spinner. The one line of error text in the app only appears on the
/// now-playing screen.
/// </para>
///
/// <para>
/// So each of those steps records what it did and what happened, here. The trail keeps the last <see
/// cref="DefaultKept"/> lines in memory and hands them out on request: to the screen that shows them, and
/// to a file, so that a failure at four in the afternoon can still be read at five.
/// </para>
///
/// <para>This type has no platform code in it: the ring, the line format and the reading of an exception
/// chain. It can be tested without a device, like <see cref="StartGate"/> and <see
/// cref="PlaybackFailure"/>. The platform head owns the file and the console.</para>
/// </summary>
public sealed class Trail
{
    /// <summary>How many lines are held. Enough for a dozen attempts at starting a track, which is more
    /// than anybody makes before giving up and looking at the log.</summary>
    public const int DefaultKept = 400;

    /// <summary>How much of an exception chain is worth reading. Past this depth the entries are framework
    /// plumbing, and a message that fills the screen is one nobody reads.</summary>
    private const int Depth = 5;

    private const int MessageLimit = 300;

    private readonly int _kept;
    private readonly Func<DateTimeOffset> _now;
    private readonly Queue<string> _lines = new();
    private readonly Queue<string> _unwritten = new();
    private readonly object _gate = new();

    /// <param name="kept">Lines held in memory.</param>
    /// <param name="now">The clock, injected so a test can hold it still.</param>
    public Trail(int kept = DefaultKept, Func<DateTimeOffset>? now = null)
    {
        _kept = Math.Max(1, kept);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Lines written but not yet taken by whatever is saving them permanently. This is bounded
    /// like the ring. A log that nobody drains must not grow without limit.</summary>
    private int Backlog => _kept * 4;

    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>
    /// Records one thing that happened.
    /// </summary>
    /// <param name="topic">Which part of the app is speaking, such as <c>play</c>, <c>open</c> or
    /// <c>fetch</c>. Keep it short and reuse it, so that a trail can be read down one column.</param>
    /// <param name="said">What happened, in the fewest words that still identify it.</param>
    public void Write(string topic, string said)
    {
        var line = Line(_now(), topic, said);

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _kept) _lines.Dequeue();

            _unwritten.Enqueue(line);
            while (_unwritten.Count > Backlog) _unwritten.Dequeue();
        }

        Written?.Invoke(line);
    }

    /// <summary>Raised for each line, so a head can print it to the console as it happens and not only when
    /// somebody goes looking.</summary>
    public event Action<string>? Written;

    /// <summary>Everything held, oldest first.</summary>
    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return [.. _lines]; }
    }

    /// <summary>The last <paramref name="count"/> lines, oldest first. This is the number that fits on a
    /// card in a settings screen.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_gate) return _lines.Count <= count ? [.. _lines] : [.. _lines.Skip(_lines.Count - count)];
    }

    /// <summary>The whole trail as one block of text. This is what gets copied into a bug report.</summary>
    public string Text() => string.Join(Environment.NewLine, Lines);

    /// <summary>Takes the lines that have not been saved yet, and forgets them. The caller is now
    /// responsible for saving them.</summary>
    public string[] TakeUnwritten()
    {
        lock (_gate)
        {
            if (_unwritten.Count == 0) return [];

            var taken = _unwritten.ToArray();
            _unwritten.Clear();
            return taken;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _unwritten.Clear();
        }
    }

    /// <summary>
    /// The shape of one line: the time to the millisecond, the topic in a column, then what happened.
    ///
    /// <para>The time has milliseconds because the value most often read from a line is how long something
    /// took. A slow start is usually a second or more. There is no date. A trail is read while the problem
    /// is still happening, and the saved file has its own timestamp.</para>
    /// </summary>
    public static string Line(DateTimeOffset at, string topic, string said)
        => $"{at:HH:mm:ss.fff}  {Column(topic)}  {said}";

    private static string Column(string topic)
    {
        topic = (topic ?? "").Trim();
        return topic.Length >= 6 ? topic : topic.PadRight(6);
    }

    /// <summary>
    /// An exception as one readable line: the chain, including the innermost cause, with the status code
    /// written out where there is one.
    ///
    /// <para>The inner exception is the important part. <c>HttpRequestException</c> says "An error occurred
    /// while sending the request" whatever went wrong. The real cause, such as no route to the host, a name
    /// that does not resolve or a refused connection, is one level down. A status code is the most useful
    /// fact about a failed fetch, and it is not in the message at all.</para>
    /// </summary>
    public static string Describe(Exception? error)
    {
        if (error is null) return "(none)";

        var said = new StringBuilder();

        var at = error;
        for (var depth = 0; at is not null && depth < Depth; depth++, at = at.InnerException)
        {
            if (said.Length > 0) said.Append(" <- ");

            said.Append(at.GetType().Name);

            if (at is HttpRequestException { StatusCode: { } code })
                said.Append('(').Append((int)code).Append(')');

            said.Append(": ").Append(Flatten(at.Message));
        }

        if (at is not null) said.Append(" <- ...");

        return said.ToString();
    }

    /// <summary>A message on one line, cut to a reasonable length. Some messages arrive as a paragraph with
    /// a stack trace in the middle.</summary>
    private static string Flatten(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "(no message)";

        var said = message.Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (said.Contains("  ", StringComparison.Ordinal))
            said = said.Replace("  ", " ", StringComparison.Ordinal);

        return said.Length <= MessageLimit ? said : said[..MessageLimit] + "…";
    }
}

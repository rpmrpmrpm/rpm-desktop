using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// One fact on the status line: either a literal already worth reading (a size, a running time) or a
/// string key and the number it is about.
/// </summary>
/// <param name="StringKey">The string to look up, or empty when <paramref name="Literal"/> says it all.</param>
/// <param name="Count">What the string is about. Ignored for a literal.</param>
/// <param name="Literal">A fact that needs no translating.</param>
public readonly record struct StatusFact(string StringKey, int Count = 0, string? Literal = null);

/// <summary>
/// What the foot of the import pane says about the queue.
///
/// <para>Facts, in the order a person checks them: how much is here, how big it is, how long it runs,
/// and then what is wrong with it. The problems are not added into one "needs attention" number. They
/// are three different jobs, and one total would hide which one applies.</para>
///
/// <para><see cref="Facts"/> is pure and separate from the control. "What does the status line say for an
/// empty queue" has one right answer, and it can be checked without a window. <see cref="Describe"/>
/// gives the same list with the words in it.</para>
/// </summary>
public static class QueueStatus
{
    /// <summary>The facts, in order. Empty when there is no queue to describe.</summary>
    public static IReadOnlyList<StatusFact> Facts(QueueChipCounts counts)
    {
        if (counts.Total <= 0)
            return [];

        var facts = new List<StatusFact>
        {
            counts.Total == 1
                ? new StatusFact("Queue_StatusFileOne")
                : new StatusFact("Queue_StatusFiles", counts.Total),
        };

        if (counts.Bytes > 0)
            facts.Add(new StatusFact("", Literal: ByteSize.Human(counts.Bytes)));

        if (counts.Seconds >= 1)
            facts.Add(new StatusFact("", Literal: Duration(counts.Seconds)));

        if (counts.NeedsRecognition > 0)
            facts.Add(new StatusFact("Queue_StatusNotIdentified", counts.NeedsRecognition));

        if (counts.Duplicates > 0)
            facts.Add(counts.Duplicates == 1
                ? new StatusFact("Queue_StatusDuplicateOne")
                : new StatusFact("Queue_StatusDuplicates", counts.Duplicates));

        if (counts.Unreadable > 0)
            facts.Add(counts.Unreadable == 1
                ? new StatusFact("Queue_StatusUnreadableOne")
                : new StatusFact("Queue_StatusUnreadable", counts.Unreadable));

        return facts;
    }

    /// <summary>The status line itself.</summary>
    public static string Describe(QueueChipCounts counts) =>
        string.Join(", ", Facts(counts).Select(Say));

    private static string Say(StatusFact fact) =>
        fact.Literal ?? (fact.Count > 0 ? Loc.Format(fact.StringKey, fact.Count) : Loc.Get(fact.StringKey));

    /// <summary>How long the queue runs, in clock format. Hours are shown only when there are some. Without
    /// that, "12:04" would look like a time of day, not 12 minutes and 4 seconds.</summary>
    public static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}

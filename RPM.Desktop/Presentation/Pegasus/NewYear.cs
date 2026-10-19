namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The one record rpm puts on by itself, and the one moment it will do it.
///
/// <para>At midnight on the first of January, if the collection has ABBA's "Happy New Year" in it, the app
/// plays it and Pegasus shows a greeting over the room. It is the only thing in rpm that starts playing
/// something nobody asked for, so every part of the rule is narrow: one record, one minute of one night,
/// and nothing at all if that record is not in the library.</para>
///
/// <para><b>Midnight, not New Year's Day.</b> The window is <see cref="Grace"/> long and no longer. A
/// person who opens rpm at nine in the morning on the first has not reached a special moment, and a room
/// that announces midnight then would be wrong about the time. The window is minutes rather than seconds
/// only because the clock is checked on a poll, and a laptop brought in from a party takes a while to
/// wake.</para>
///
/// <para>Local time throughout, because midnight is a local event. It is when the people in the room say it
/// is, not when a server in another country says so.</para>
///
/// <para>A function of the clock and two tags. The once-a-year memory is kept in <see cref="NewYearGate"/>,
/// next door. There is no window, no library and no player in this code. That is what makes a thing that
/// happens once a year testable at all.</para>
/// </summary>
internal static class NewYear
{
    /// <summary>The record. Both halves have to match. The title is a common phrase, so many collections
    /// hold another song with that name. The artist alone would match every ABBA track.</summary>
    internal const string Title = "Happy New Year";
    internal const string Artist = "ABBA";

    /// <summary>How long after midnight still counts as the stroke. It is long enough to cover a poll
    /// interval and a machine waking up, and short enough to be the stroke itself. See the class
    /// remark.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    /// <summary>Whether this moment is the stroke of the new year.</summary>
    internal static bool IsTheStroke(DateTimeOffset now)
        => now.Month == 1 && now.Day == 1 && now.TimeOfDay < Grace;

    /// <summary>Whether this is the record: the title exactly, and ABBA among whoever the artist tag names.
    /// The two fields are read differently on purpose. A title field holds one title. An artist field is
    /// often a list, so it is read as a list (see <see cref="TagWord"/>).</summary>
    internal static bool IsTheRecord(string? title, string? artist)
        => TagWord.Is(title, Title) && TagWord.Names(artist, Artist);
}

/// <summary>
/// Remembers that this year has been seen in, so the stroke is answered once and not once every time the
/// clock is looked at through the minute after midnight.
///
/// <para>Split from the rule next door because it is the only part with a memory. It follows the same split
/// as <see cref="RPM.Core.Ripping.DiscOfferGate"/>: whether a moment has arrived and whether it has already
/// been answered are different questions, and only the second one needs to remember anything.</para>
///
/// <para>Two calls rather than one, and that is the point of it. The caller asks whether the year is
/// <see cref="Due"/>, looks for the record, and only then says <see cref="Celebrated"/>, once it has found
/// one and put it on. A year in which the record was not in the library does not count as done. So a
/// library still opening at midnight gets another look on the next poll, and does not lose its New Year to
/// a race.</para>
///
/// <para>The memory lasts as long as the app runs. Restarting rpm inside the same two minutes would greet
/// the year again. That window is too narrow to be worth writing a setting to disk for, and the failure it
/// guards against is being wished a happy new year twice.</para>
/// </summary>
internal sealed class NewYearGate
{
    /// <summary>The year already seen in, or zero. Zero is safe because no real year is zero, so the first
    /// check always passes.</summary>
    private int _celebrated;

    /// <summary>Whether the greeting is owed right now, the stroke, in a year that has not had
    /// one.</summary>
    internal bool Due(DateTimeOffset now)
        => NewYear.IsTheStroke(now) && _celebrated != now.Year;

    /// <summary>Marks the year as seen in. Called after the greeting is actually on screen, never
    /// before.</summary>
    internal void Celebrated(DateTimeOffset now) => _celebrated = now.Year;
}

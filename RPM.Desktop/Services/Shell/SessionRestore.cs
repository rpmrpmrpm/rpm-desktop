namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The rules for reading a stored session back.
///
/// <para>Everything in <see cref="SessionService"/> is a note about a world that may have changed. Between
/// one run and the next, a file may be deleted, a record offloaded, a friend's computer unpaired, a library
/// pointed somewhere else, a column renamed, or a screen unplugged. So nothing stored is trusted on the way
/// in. Every value is checked against what is actually here. Anything that no longer makes sense is
/// dropped, and the default is used instead. A restore that cannot be honoured is not an error. It is the
/// app opening the way a fresh install opens, which is always a valid state.</para>
///
/// <para>This code is pure. It is kept apart from the service and from the page, so it can be tested
/// without an app, a window or a library.</para>
/// </summary>
public static class SessionRestore
{
    /// <summary>The longest search text carried across a restart. A stored search is a convenience, not a
    /// document. A longer one is most likely an accidental paste, so it is not reopened.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>How many queued tracks are saved. A queue is usually a record or two. A DJ set over a large
    /// collection can have thousands, and a session file is not the place for a playlist. Past this, the
    /// tail is dropped. The queue still opens on what was playing.</summary>
    public const int MaxQueue = 500;

    /// <summary>The smallest and largest sizes a stored window may have, in physical pixels. Outside this
    /// range it was not a window anyone was using. It may be a zero from an older file, a size saved while
    /// the window was minimised, or a number from a display setup that no longer exists.</summary>
    public const int MinWindow = 400;
    public const int MaxWindow = 30000;

    /// <summary>How much of the window must be on a screen to be reachable: enough of the top edge to see
    /// it and to drag it back.</summary>
    public const int MinVisible = 200;
    public const int MinVisibleHeight = 80;

    /// <summary>
    /// The stored window frame, checked against the desktop as it is NOW.
    ///
    /// <para>The case this exists for is a second monitor. A window left on a screen that is not connected
    /// this morning has a stored position off the side of everything. Putting it back there would open the
    /// app invisibly: running and in the task switcher, but on no screen. So the frame must land on the
    /// desktop by enough to see and grab. Otherwise the whole frame is refused, and the window opens where
    /// the system puts it.</para>
    ///
    /// <para>Windows only. macOS lets AppKit remember its own frames. That does all of this, and also
    /// handles the menu bar. See <see cref="WindowPlacement"/>.</para>
    /// </summary>
    public static (int Left, int Top, int Width, int Height)? WindowFrame(
        int left, int top, int width, int height,
        (int Left, int Top, int Right, int Bottom) desktop)
    {
        if (width is < MinWindow or > MaxWindow || height is < MinWindow or > MaxWindow)
            return null;

        var onScreenWidth = Math.Min(left + width, desktop.Right) - Math.Max(left, desktop.Left);
        var onScreenHeight = Math.Min(top + height, desktop.Bottom) - Math.Max(top, desktop.Top);
        if (onScreenWidth < MinVisible || onScreenHeight < MinVisibleHeight)
            return null;

        return (left, top, width, height);
    }

    /// <summary>
    /// An enum stored BY NAME. A name that no longer resolves (a value renamed or removed since the file
    /// was written) is read as nothing stored, which gives the default.
    ///
    /// <para>A NUMBER is refused outright, even though the framework would accept it. Storing by name is
    /// the whole point of storing these as text. A number written by an older build would be read as
    /// whichever value now sits at that position. That would restore something plausible rather than
    /// nothing, and it must not happen.</para>
    /// </summary>
    public static T? Enum<T>(string? name) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(name) || !char.IsLetter(name[0]) && name[0] != '_')
            return null;
        return System.Enum.TryParse<T>(name, ignoreCase: false, out var value) && System.Enum.IsDefined(value)
            ? value
            : null;
    }

    /// <summary>The library search box. Trimmed, and dropped when it is longer than a search.</summary>
    public static string Search(string? stored)
    {
        var text = stored?.Trim() ?? "";
        return text.Length is 0 or > MaxSearchLength ? "" : text;
    }

    /// <summary>
    /// The left pane's remembered star split. Both sides must be real, positive numbers close enough to
    /// each other, or the pair is refused together. Half a split is not a split. A stored zero (from an
    /// older file or a partial write) would open the app with the library squeezed into the corner.
    /// </summary>
    public static (double Left, double Right)? Split(double left, double right)
    {
        if (!Sane(left) || !Sane(right))
            return null;
        return (left, right);

        static bool Sane(double v) => double.IsFinite(v) && v is >= 0.2 and <= 8;
    }

    /// <summary>A remembered scroll offset. Never negative, never infinite, and never past what the view
    /// can scroll. A shelf can shrink after it is saved (records deleted, or a search still in force). Then
    /// the offset lands at the bottom, not in empty space.</summary>
    public static double Scroll(double stored, double scrollable)
    {
        if (!double.IsFinite(stored) || stored <= 0 || !double.IsFinite(scrollable) || scrollable <= 0)
            return 0;
        return Math.Min(stored, scrollable);
    }

    /// <summary>A remembered column width. Outside the range a column can be dragged to, the column uses
    /// the width that the markup gives it.</summary>
    public static double? ColumnWidth(double stored)
        => double.IsFinite(stored) && stored is >= 24 and <= 1200 ? stored : null;


    /// <summary>
    /// A stored sort, checked against the columns that the table can sort on. A column that has been
    /// renamed or dropped, or that never belonged to the mirror, leaves the table in its own default order.
    /// </summary>
    public static string? SortColumn(string? stored, Func<string, bool> sortable)
    {
        ArgumentNullException.ThrowIfNull(sortable);
        return string.IsNullOrWhiteSpace(stored) ? null : sortable(stored) ? stored : null;
    }

    /// <summary>
    /// The queue, as much of it as still exists.
    ///
    /// <para>Each entry is looked up in the library that is open NOW. Tracks are stored as the mirror's own
    /// relative paths. So if the library points at a different folder, a track was deleted, or a record was
    /// on a friend's shelf, that entry resolves to nothing and is left out. What remains is still a queue,
    /// in the same order.</para>
    /// </summary>
    public static IReadOnlyList<T> Queue<T>(IReadOnlyList<string>? stored, Func<string, T?> resolve)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(resolve);
        if (stored is not { Count: > 0 })
            return [];

        var kept = new List<T>(Math.Min(stored.Count, MaxQueue));
        foreach (var path in stored.Take(MaxQueue))
        {
            if (!string.IsNullOrWhiteSpace(path) && resolve(path) is { } track)
                kept.Add(track);
        }
        return kept;
    }

    /// <summary>
    /// The needle's position, once the queue has been read back.
    ///
    /// <para>The cursor is stored as a POSITION in a queue that may have lost tracks on the way in. So it
    /// is only used when the track at that position is the same track. The caller checks this by passing
    /// the stored path and the restored queue. Otherwise the queue opens on the first track, with the
    /// needle at the start. A queue is worth keeping even when the record that was playing is gone. Playing
    /// something that the listener did not leave playing is worse than not resuming at all.</para>
    /// </summary>
    public static (int Index, double Seconds) Cursor(
        int storedIndex, double storedSeconds, string? storedPath, IReadOnlyList<string> restored)
    {
        ArgumentNullException.ThrowIfNull(restored);
        if (restored.Count == 0)
            return (-1, 0);

        // The stored position that still holds the stored track is the normal case. Otherwise the track is
        // searched for, because tracks removed before it may have moved it.
        var at = -1;
        if (!string.IsNullOrWhiteSpace(storedPath))
        {
            if (storedIndex >= 0 && storedIndex < restored.Count && Same(restored[storedIndex]))
            {
                at = storedIndex;
            }
            else
            {
                for (var i = 0; i < restored.Count && at < 0; i++)
                    if (Same(restored[i]))
                        at = i;
            }
        }

        bool Same(string path) => string.Equals(path, storedPath, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
            return (0, 0);

        var seconds = double.IsFinite(storedSeconds) && storedSeconds > 1 ? storedSeconds : 0;
        return (at, seconds);
    }
}

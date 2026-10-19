namespace RPM.Core.Cloud;

/// <summary>One folder as a row in a list that shows a single level of the tree.</summary>
/// <param name="Name">The folder's name: the last step of its own path.</param>
/// <param name="Path">The full path down to the folder, outermost folder first. Stepping into it opens
/// this path, and a playlist made inside it is filed under this path.</param>
/// <param name="Playlists">How many playlists are inside the folder, counting its subfolders too. The
/// total is used, not only the playlists directly in it. Someone deciding whether to step in wants to
/// know this. A folder that holds only folders would otherwise look empty.</param>
public sealed record PlaylistFolderRow(string Name, IReadOnlyList<string> Path, int Playlists);

/// <summary>The playlist tree as a phone can walk it: one folder at a time, going in and back out.
///
/// <para>The desktop's icon view reads the same tree in the same way: one level on screen, a step in, a
/// step back. This is that, in a list. It is not the desktop's tree, though. The cloud stores a playlist
/// and the folders it sits in, but it stores no folders of its own. So a folder exists here only when
/// something is filed in it or below it. A folder that somebody made on their computer and has not put
/// anything in yet cannot be shown on the phone. There is nothing to draw and nothing to step into.
/// </para>
///
/// <para>Everything here is worked out from the folder paths in hand, each time it is asked. Nothing is
/// remembered between calls. That is what makes the open folder safe. It is not a pointer to something
/// that might have gone. See <see cref="StillOpen"/>. It handles someone standing in a folder whose last
/// playlist was just deleted on another device. They end up in the nearest parent folder that still has
/// playlists, not in front of a list that will never fill.
/// </para>
/// </summary>
public static class PlaylistFolderTree
{
    /// <summary>Two folder names are the same name when they read the same, ignoring case. The tree that
    /// holds them matches names the same way (<see cref="Library.LibraryDatabase.EnsurePlaylistFolder"/>).
    /// A phone that disagreed would draw one folder as two.</summary>
    private static readonly StringComparer Names = StringComparer.CurrentCultureIgnoreCase;

    /// <summary>Whether a playlist filed at <paramref name="folder"/> is directly in
    /// <paramref name="at"/>: one of the rows that a list showing that folder draws.</summary>
    public static bool IsIn(IReadOnlyList<string> folder, IReadOnlyList<string> at)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(at);

        return folder.Count == at.Count && Beneath(folder, at);
    }

    /// <summary>Whether <paramref name="folder"/> is in <paramref name="at"/> or anywhere below it. A
    /// folder counts as beneath itself, because what is in it is part of what it holds.</summary>
    public static bool IsUnder(IReadOnlyList<string> folder, IReadOnlyList<string> at)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(at);

        return folder.Count >= at.Count && Beneath(folder, at);
    }

    private static bool Beneath(IReadOnlyList<string> folder, IReadOnlyList<string> at)
    {
        for (var i = 0; i < at.Count; i++)
            if (!Names.Equals(folder[i], at[i]))
                return false;

        return true;
    }

    /// <summary>
    /// The folders directly inside <paramref name="at"/>, by name, each with everything beneath it
    /// counted.
    /// </summary>
    /// <param name="folders">The folder path of every playlist, repeats included: one entry per playlist,
    /// not per folder. The counting happens here, so every entry is needed.</param>
    /// <param name="at">The folder being looked at. Empty means the top of the tree.</param>
    public static IReadOnlyList<PlaylistFolderRow> FoldersIn(
        IEnumerable<IReadOnlyList<string>> folders, IReadOnlyList<string> at)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(at);

        // The key is the child's name and nothing else. Within one folder, a name identifies the folder,
        // so a walkable tree needs no extra labels. Two folders called "90s" are never on screen together,
        // because you stand in one place at a time.
        var counts = new Dictionary<string, int>(Names);
        var spelling = new Dictionary<string, string>(Names);

        foreach (var folder in folders)
        {
            // Keep only playlists that are deeper than here and down this branch. Anything else is not a
            // child of this folder.
            if (folder.Count <= at.Count || !Beneath(folder, at))
                continue;

            var name = folder[at.Count];
            counts[name] = counts.GetValueOrDefault(name) + 1;

            // The first spelling seen wins. A folder whose playlists disagree about case is still one row,
            // drawn one way. It does not change when the listing order changes.
            spelling.TryAdd(name, name);
        }

        return
        [
            .. counts
                .Select(entry => new PlaylistFolderRow(
                    spelling[entry.Key], [.. at, spelling[entry.Key]], entry.Value))
                .OrderBy(row => row.Name, Names),
        ];
    }

    /// <summary>
    /// Where somebody standing at <paramref name="at"/> actually is, given the playlists in hand: the
    /// deepest part of that path that still holds anything.
    ///
    /// <para>The open folder is part of the screen's state. The tree does not belong to the screen. A
    /// playlist deleted on a computer, a shelf swapped, or a listing that came back different can each
    /// empty the folder somebody is looking at. If the path were kept as it was, that person would be left
    /// in a place that no longer exists, and the back button would be the only way out. So this is worked
    /// out again on every rebuild instead of being trusted. The desktop's own grid works the same
    /// way.</para>
    /// </summary>
    public static IReadOnlyList<string> StillOpen(
        IReadOnlyList<string> at, IEnumerable<IReadOnlyList<string>> folders)
    {
        ArgumentNullException.ThrowIfNull(at);
        ArgumentNullException.ThrowIfNull(folders);

        if (at.Count == 0) return at;

        var all = folders as IReadOnlyList<IReadOnlyList<string>> ?? [.. folders];

        for (var depth = at.Count; depth > 0; depth--)
        {
            var here = at.Take(depth).ToList();
            if (all.Any(folder => IsUnder(folder, here)))
                return here;
        }

        return [];
    }
}

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// The cloud entries the playlist tree's menu offers for a selection of several playlists: which
/// playlists go into the cloud, which come out, and how each entry is labelled.
///
/// <para>The menu acts on the selection only when the row that was right-clicked is part of it. A
/// right-click on a row outside the selection is about that row alone, as in a file browser.</para>
/// </summary>
/// <param name="ToAdd">The node ids of the selected playlists that are not in the cloud.</param>
/// <param name="ToRemove">The node ids of the selected playlists that are in the cloud.</param>
internal sealed record PlaylistCloudPick(IReadOnlyList<long> ToAdd, IReadOnlyList<long> ToRemove)
{
    /// <summary>One selected row, as the cloud entries see it.</summary>
    /// <param name="NodeId">The stored node, or null for a row that has none (an app folder, a
    /// generated list).</param>
    /// <param name="CanAdd">The row is a playlist that is not in the cloud.</param>
    /// <param name="CanRemove">The row is a playlist that is in the cloud.</param>
    internal readonly record struct Row(long? NodeId, bool CanAdd, bool CanRemove);

    /// <summary>
    /// The cloud entries for <paramref name="selected"/>, or null when the menu is for the clicked row
    /// alone.
    /// </summary>
    /// <param name="selected">Every selected row, in any order.</param>
    /// <param name="clickedIsSelected">Whether the right-clicked row is one of them.</param>
    /// <returns>Null when the clicked row is outside the selection, or when fewer than two selected rows
    /// are playlists that can go into or come out of the cloud. Folders, generated lists and Now playing
    /// are left out of the counts, because none of them can be in the cloud.</returns>
    public static PlaylistCloudPick? For(IReadOnlyList<Row> selected, bool clickedIsSelected)
    {
        if (!clickedIsSelected)
            return null;

        var playlists = selected.Where(r => r.NodeId is not null && (r.CanAdd || r.CanRemove)).ToList();
        if (playlists.Count < 2)
            return null;

        return new PlaylistCloudPick(
            [.. playlists.Where(r => r.CanAdd).Select(r => r.NodeId!.Value)],
            [.. playlists.Where(r => r.CanRemove).Select(r => r.NodeId!.Value)]);
    }

    /// <summary>The string key for the add entry. One playlist uses the label of the single-row menu,
    /// so the menu never says "Add 1 playlists to cloud".</summary>
    public string AddKey => ToAdd.Count == 1 ? "Playlists_AddToCloud" : "Playlists_AddManyToCloud";

    /// <summary>The string key for the remove entry, chosen as <see cref="AddKey"/> is.</summary>
    public string RemoveKey => ToRemove.Count == 1 ? "Playlists_RemoveFromCloud" : "Playlists_RemoveManyFromCloud";
}

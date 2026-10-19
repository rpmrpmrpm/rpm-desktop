using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// An open list keeps pointing at the right list after the tree underneath it has been rewritten.
///
/// <para>A borrowed shelf's listing is rewritten by deleting every <c>pl_node</c> and inserting them all
/// again (<c>LibraryDatabase.MirrorPlaylistTree</c>). So every folder and playlist gets a new rowid, and
/// the old ids are given to different rows. An open list that still holds its old id asks the store for a
/// playlist that no longer exists. It gets no tracks back and shows as an empty list. On a borrowed library
/// the songs appear and then vanish a moment after opening. This is tested directly because the failure is
/// silent and looks like a playlist with nothing in it.</para>
/// </summary>
public class PlaylistRelocateTests
{
    private static Row Of(PlaylistRowKind kind, long? id, params string[] path) => new(kind, path, id);

    private readonly record struct Row(PlaylistRowKind Kind, IReadOnlyList<string> Path, long? NodeId);

    private static int Match(IReadOnlyList<Row> tree, Row open) =>
        PlaylistModel.Match(
            tree.Select(r => (r.Kind, r.Path, r.NodeId)).ToList(),
            open.Kind, open.Path, open.NodeId);

    /// <summary>A folder with two lists in it, at whatever ids are given.</summary>
    private static Row[] Shelf(long folder, long relax, long work) =>
    [
        Of(PlaylistRowKind.Folder, folder, "Shared"),
        Of(PlaylistRowKind.Playlist, relax, "Shared", "Relax"),
        Of(PlaylistRowKind.Playlist, work, "Shared", "Work"),
    ];

    [Fact]
    public void A_list_is_found_again_after_every_id_in_the_tree_has_changed()
    {
        var opened = Shelf(1, 2, 3)[1];

        // The peer's listing landed: same names, same shape, all new ids.
        var after = Shelf(40, 41, 42);

        Assert.Equal(1, Match(after, opened));
        Assert.Equal(41, after[Match(after, opened)].NodeId);
    }

    /// <summary>
    /// The ids are not only stale after a rewrite. They are REUSED: the same rowid space is handed out again,
    /// so the id a list was opened with can now belong to a different list. Matching on it would show the wrong
    /// songs under the right name, which is worse than showing none.
    /// </summary>
    [Fact]
    public void The_id_it_was_opened_with_is_not_followed_to_a_different_list()
    {
        var opened = Shelf(1, 2, 3)[1];

        // "Work" now has the id that "Relax" had.
        var after = Shelf(9, 7, 2);

        var at = Match(after, opened);
        Assert.Equal(7, after[at].NodeId);
        Assert.Equal("Relax", after[at].Path[^1]);
    }

    /// <summary>The one case the path cannot answer. A rename keeps the row and its id, so the id still
    /// belongs to this list. That is why the id is the fallback and not the main rule.</summary>
    [Fact]
    public void A_list_renamed_while_it_is_open_is_followed_by_its_id()
    {
        var opened = Shelf(1, 2, 3)[1];

        Row[] after =
        [
            Of(PlaylistRowKind.Folder, 1, "Shared"),
            Of(PlaylistRowKind.Playlist, 2, "Shared", "Unwind"),
            Of(PlaylistRowKind.Playlist, 3, "Shared", "Work"),
        ];

        Assert.Equal(1, Match(after, opened));
    }

    /// <summary>Two lists of the same name in two folders are two different lists, and the path is what
    /// tells them apart. A name on its own would follow the wrong one.</summary>
    [Fact]
    public void Two_lists_of_one_name_in_two_folders_are_told_apart()
    {
        var opened = Of(PlaylistRowKind.Playlist, 2, "Mine", "Relax");

        Row[] after =
        [
            Of(PlaylistRowKind.Folder, 30, "Theirs"),
            Of(PlaylistRowKind.Playlist, 31, "Theirs", "Relax"),
            Of(PlaylistRowKind.Folder, 32, "Mine"),
            Of(PlaylistRowKind.Playlist, 33, "Mine", "Relax"),
        ];

        Assert.Equal(3, Match(after, opened));
    }

    /// <summary>A FOLDER and a playlist of the same name are not each other. The kind is part of the
    /// question, and a tree carries both at the same level.</summary>
    [Fact]
    public void A_folder_is_not_a_playlist_of_the_same_name()
    {
        var opened = Of(PlaylistRowKind.Playlist, 2, "Shared", "Relax");

        Row[] after =
        [
            Of(PlaylistRowKind.Folder, 30, "Shared"),
            Of(PlaylistRowKind.Folder, 31, "Shared", "Relax"),
        ];

        Assert.Equal(-1, Match(after, opened));
    }

    /// <summary>A list that really has been deleted. Nothing found is what backs the reader out to the
    /// tree, and it has to stay distinguishable from a list that merely moved id.</summary>
    [Fact]
    public void A_list_that_is_gone_is_gone()
    {
        var opened = Shelf(1, 2, 3)[1];

        Row[] after =
        [
            Of(PlaylistRowKind.Folder, 40, "Shared"),
            Of(PlaylistRowKind.Playlist, 42, "Shared", "Work"),
        ];

        Assert.Equal(-1, Match(after, opened));
    }

    /// <summary>A list moved into another folder is a path change with the id kept. It has the same shape
    /// as a rename, so it gets the same answer.</summary>
    [Fact]
    public void A_list_dragged_into_another_folder_is_followed_by_its_id()
    {
        var opened = Of(PlaylistRowKind.Playlist, 2, "Shared", "Relax");

        Row[] after =
        [
            Of(PlaylistRowKind.Folder, 1, "Shared"),
            Of(PlaylistRowKind.Folder, 4, "Evenings"),
            Of(PlaylistRowKind.Playlist, 2, "Evenings", "Relax"),
        ];

        Assert.Equal(2, Match(after, opened));
    }
}

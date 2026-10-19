using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Walking the playlist tree one folder at a time, as the phone does.
///
/// <para>The tree is not stored anywhere: the cloud holds playlists and the chain each one sits in, and
/// every folder on screen is worked out from those chains, every time. So the questions worth asking
/// are the ones about a tree that is derived rather than held: what counts as being IN a folder as
/// against under it, whether a name means the same thing in two different branches, and what happens to
/// somebody standing in a folder at the moment it stops existing.</para>
/// </summary>
public class PlaylistFolderTreeTests
{
    private static IReadOnlyList<string>[] Folders(params string[][] chains) =>
        [.. chains.Select(chain => (IReadOnlyList<string>)chain)];

    private static IReadOnlyList<string> At(params string[] path) => path;

    // ── in, and under ──

    [Fact]
    public void A_playlist_nobody_filed_is_at_the_top_of_the_tree()
        => Assert.True(PlaylistFolderTree.IsIn([], At()));

    [Fact]
    public void A_playlist_in_a_folder_is_not_at_the_top_of_the_tree()
        => Assert.False(PlaylistFolderTree.IsIn(["Rock"], At()));

    [Fact]
    public void A_playlist_deeper_down_is_not_IN_the_folder_it_passes_through()
        => Assert.False(PlaylistFolderTree.IsIn(["Rock", "90s"], At("Rock")));

    /// <summary>...but it IS under it, which is what a folder's count is of.</summary>
    [Fact]
    public void A_playlist_deeper_down_is_under_the_folder_it_passes_through()
        => Assert.True(PlaylistFolderTree.IsUnder(["Rock", "90s"], At("Rock")));

    [Fact]
    public void A_folder_holds_what_is_directly_in_it_as_well()
        => Assert.True(PlaylistFolderTree.IsUnder(["Rock"], At("Rock")));

    [Fact]
    public void Nothing_in_one_branch_is_under_another()
        => Assert.False(PlaylistFolderTree.IsUnder(["Pop", "90s"], At("Rock")));

    [Fact]
    public void Everything_is_under_the_top_of_the_tree()
        => Assert.True(PlaylistFolderTree.IsUnder(["Rock", "90s"], At()));

    [Fact]
    public void Case_is_not_what_tells_two_folders_apart()
        => Assert.True(PlaylistFolderTree.IsIn(["rock"], At("Rock")));

    // ── one level at a time ──

    [Fact]
    public void The_top_of_the_tree_shows_the_folders_at_the_top_of_the_tree()
    {
        var rows = PlaylistFolderTree.FoldersIn(
            Folders(["Rock"], ["Pop"], [], ["Rock", "90s"]), At());

        Assert.Equal(["Pop", "Rock"], rows.Select(r => r.Name));
    }

    /// <summary>The whole point of walking rather than flattening: inside Rock there is one 90s, and it
    /// is not the one inside Pop. A list showing one place at a time never has to tell them apart,
    /// because they are never on it together.</summary>
    [Fact]
    public void Two_folders_of_one_name_in_different_branches_never_meet()
    {
        var chains = Folders(["Rock", "90s"], ["Pop", "90s"]);

        Assert.Equal(["90s"], PlaylistFolderTree.FoldersIn(chains, At("Rock")).Select(r => r.Name));
        Assert.Equal(["90s"], PlaylistFolderTree.FoldersIn(chains, At("Pop")).Select(r => r.Name));
    }

    /// <summary>And stepping into one goes to that one.</summary>
    [Fact]
    public void Stepping_into_a_folder_carries_the_whole_way_down_to_it()
    {
        var row = Assert.Single(
            PlaylistFolderTree.FoldersIn(Folders(["Rock", "90s"], ["Pop", "90s"]), At("Rock")));

        Assert.Equal(["Rock", "90s"], row.Path);
    }

    [Fact]
    public void A_folder_appears_once_however_many_playlists_are_in_it()
    {
        var rows = PlaylistFolderTree.FoldersIn(
            Folders(["Rock"], ["Rock"], ["Rock"]), At());

        Assert.Equal("Rock", Assert.Single(rows).Name);
    }

    /// <summary>A folder somebody made and has not put anything in is not a folder the phone can show:
    /// the cloud stores playlists and their chains, and stores no folders of its own.</summary>
    [Fact]
    public void A_folder_holding_nothing_is_not_a_folder_the_phone_knows_about()
        => Assert.Empty(PlaylistFolderTree.FoldersIn(Folders([], []), At()));

    // ── what a folder says it holds ──

    [Fact]
    public void A_folder_counts_what_is_directly_in_it()
    {
        var row = Assert.Single(PlaylistFolderTree.FoldersIn(Folders(["Rock"], ["Rock"]), At()));

        Assert.Equal(2, row.Playlists);
    }

    /// <summary>Everything beneath it, not just what is directly in it. Otherwise a folder of folders would
    /// otherwise read as empty to somebody deciding whether to step into it.</summary>
    [Fact]
    public void A_folder_counts_everything_beneath_it()
    {
        var row = Assert.Single(PlaylistFolderTree.FoldersIn(
            Folders(["Rock"], ["Rock", "90s"], ["Rock", "90s", "British"]), At()));

        Assert.Equal(3, row.Playlists);
    }

    // ── standing in a folder that goes ──

    [Fact]
    public void A_folder_that_still_holds_something_is_where_you_stay()
        => Assert.Equal(
            ["Rock"],
            PlaylistFolderTree.StillOpen(At("Rock"), Folders(["Rock"])));

    /// <summary>The failure this exists for: the last playlist in the folder somebody is looking at is
    /// deleted on their computer, and without this they are left standing in a place that no longer
    /// exists, in front of a list that will never fill.</summary>
    [Fact]
    public void A_folder_whose_last_playlist_has_gone_puts_you_in_the_one_that_held_it()
        => Assert.Equal(
            ["Rock"],
            PlaylistFolderTree.StillOpen(At("Rock", "90s"), Folders(["Rock"])));

    [Fact]
    public void A_whole_branch_going_puts_you_back_at_the_top_of_the_tree()
        => Assert.Empty(PlaylistFolderTree.StillOpen(At("Rock", "90s"), Folders(["Pop"])));

    [Fact]
    public void An_account_with_nothing_left_in_it_puts_you_at_the_top_of_the_tree()
        => Assert.Empty(PlaylistFolderTree.StillOpen(At("Rock"), Folders()));

    /// <summary>A folder is still open when something is anywhere beneath it, not only directly in
    /// it: a folder of folders is a place you can stand.</summary>
    [Fact]
    public void A_folder_holding_only_folders_is_still_a_place_you_can_stand()
        => Assert.Equal(
            ["Rock"],
            PlaylistFolderTree.StillOpen(At("Rock"), Folders(["Rock", "90s"])));

    [Fact]
    public void The_top_of_the_tree_is_always_somewhere_you_can_be()
        => Assert.Empty(PlaylistFolderTree.StillOpen(At(), Folders()));
}

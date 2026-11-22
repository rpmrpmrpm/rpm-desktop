using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Row = RPM.Desktop.Presentation.Main.LeftPane.Models.PlaylistCloudPick.Row;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="PlaylistCloudPick"/>: what the playlist tree's menu offers for the cloud when several
/// playlists are selected.
/// </summary>
public class PlaylistCloudPickTests
{
    private static Row NotInCloud(long id) => new(id, CanAdd: true, CanRemove: false);
    private static Row InCloud(long id) => new(id, CanAdd: false, CanRemove: true);
    private static Row Folder(long id) => new(id, CanAdd: false, CanRemove: false);
    private static readonly Row AppFolder = new(null, CanAdd: false, CanRemove: false);

    [Fact]
    public void Several_playlists_not_in_the_cloud_are_added_together()
    {
        var pick = PlaylistCloudPick.For([NotInCloud(14), NotInCloud(15), NotInCloud(16)], clickedIsSelected: true);

        Assert.NotNull(pick);
        Assert.Equal([14L, 15L, 16L], pick.ToAdd);
        Assert.Empty(pick.ToRemove);
        Assert.Equal("Playlists_AddManyToCloud", pick.AddKey);
    }

    [Fact]
    public void A_mixed_selection_offers_both_entries_with_their_own_counts()
    {
        var pick = PlaylistCloudPick.For([NotInCloud(14), NotInCloud(15), InCloud(4), InCloud(5)], clickedIsSelected: true);

        Assert.NotNull(pick);
        Assert.Equal([14L, 15L], pick.ToAdd);
        Assert.Equal([4L, 5L], pick.ToRemove);
    }

    [Fact]
    public void One_playlist_left_to_add_uses_the_single_label()
    {
        var pick = PlaylistCloudPick.For([NotInCloud(14), InCloud(4), InCloud(5)], clickedIsSelected: true);

        Assert.NotNull(pick);
        Assert.Equal("Playlists_AddToCloud", pick.AddKey);
        Assert.Equal("Playlists_RemoveManyFromCloud", pick.RemoveKey);
    }

    [Fact]
    public void A_right_click_outside_the_selection_is_about_that_row_alone()
    {
        Assert.Null(PlaylistCloudPick.For([NotInCloud(14), NotInCloud(15)], clickedIsSelected: false));
    }

    [Fact]
    public void One_selected_row_keeps_the_single_row_menu()
    {
        Assert.Null(PlaylistCloudPick.For([NotInCloud(14)], clickedIsSelected: true));
    }

    [Fact]
    public void Folders_and_app_folders_do_not_count_as_playlists()
    {
        Assert.Null(PlaylistCloudPick.For([NotInCloud(14), Folder(7), AppFolder], clickedIsSelected: true));

        var pick = PlaylistCloudPick.For([NotInCloud(14), NotInCloud(15), Folder(7), AppFolder], clickedIsSelected: true);
        Assert.NotNull(pick);
        Assert.Equal([14L, 15L], pick.ToAdd);
    }
}

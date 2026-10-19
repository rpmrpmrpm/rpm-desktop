using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// The owner's generated lists on a borrowed shelf, drawn as the same lists are drawn on the owner's
/// own shelf.
///
/// <para>On a borrowed shelf they are stored playlists (see <c>BorrowedMoodLists</c>), and what each one
/// IS is carried in its cover (<see cref="PlaylistCover.Generated"/>). These tests check how the rows
/// use that: how the row is drawn, the flavours shown beside their mood on the grid, and the owner's app
/// folders drawn like this app's own.</para>
///
/// <para>Tested with a stand-in node rather than <c>PlaylistTreeItem</c>, which builds XAML objects and
/// cannot be created in a test host.</para>
/// </summary>
public class BorrowedGeneratedRowTests
{
    /// <summary>A row, reduced to what the rules read: its kind, what it is drawn as, and what it holds.
    /// </summary>
    private sealed class Node(PlaylistRowKind kind, SmartPlaylistId? face, params Node[] children)
    {
        public PlaylistRowKind Kind { get; } = kind;
        public SmartPlaylistId? Face { get; } = face;
        public IReadOnlyList<Node> Children { get; } = children;
    }

    private static Node Folder(params Node[] children) => new(PlaylistRowKind.Folder, null, children);

    private static Node Lent(SmartPlaylistId id, params Node[] children)
        => new(PlaylistRowKind.Playlist,
               PlaylistTreeItem.FaceOf(PlaylistRowKind.Playlist, default, new PlaylistCover.Generated(id)),
               children);

    private static Node Made() => new(PlaylistRowKind.Playlist, null);

    private static List<Node> TilesFor(params Node[] rows)
        => PlaylistIcons.TilesFor(rows, n => n.Kind, n => n.Children);

    private static bool HoldsGeneratedListsOnly(Node folder)
        => PlaylistModel.HoldsGeneratedListsOnly(folder, n => n.Kind, n => n.Face is not null, n => n.Children);

    private static readonly SmartPlaylistId Chill = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
    private static readonly SmartPlaylistId SlowBurn = SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock");

    // ── what a row is drawn as ──

    [Fact]
    public void A_borrowed_generated_list_is_drawn_as_the_list_it_is()
        => Assert.Equal(
            SlowBurn,
            PlaylistTreeItem.FaceOf(PlaylistRowKind.Playlist, default, new PlaylistCover.Generated(SlowBurn)));

    [Theory]
    [InlineData(typeof(PlaylistCover.Automatic))]
    [InlineData(typeof(PlaylistCover.Picture))]
    public void A_list_somebody_made_is_drawn_as_itself(Type chosen)
        => Assert.Null(PlaylistTreeItem.FaceOf(
            PlaylistRowKind.Playlist, default, (PlaylistCover)Activator.CreateInstance(chosen)!));

    [Fact]
    public void A_generated_row_is_drawn_as_itself()
        => Assert.Equal(Chill, PlaylistTreeItem.FaceOf(PlaylistRowKind.Smart, Chill, new PlaylistCover.Automatic()));

    /// <summary>Only a list is drawn as a list. A folder is drawn as a folder.</summary>
    [Fact]
    public void A_folder_is_never_drawn_as_a_generated_list()
        => Assert.Null(PlaylistTreeItem.FaceOf(PlaylistRowKind.Folder, default, new PlaylistCover.Generated(Chill)));

    // ── the grid ──

    /// <summary>
    /// A flavour sits under its mood's list, and a tile has no chevron. On the user's own shelf the
    /// genre versions of a mood are shown beside it on the grid; on a borrowed one they must be too, or
    /// they exist but the grid has no way to reach them.
    /// </summary>
    [Fact]
    public void A_moods_flavours_stand_beside_it_on_the_grid()
    {
        var flavour = Lent(SlowBurn);
        var mood = Lent(Chill, flavour);

        Assert.Equal([mood, flavour], TilesFor(mood));
    }

    /// <summary>The same rule on the user's own shelf, where the mood is a generated row.</summary>
    [Fact]
    public void A_generated_rows_flavours_stand_beside_it_on_the_grid()
    {
        var flavour = new Node(PlaylistRowKind.Smart, SlowBurn);
        var mood = new Node(PlaylistRowKind.Smart, Chill, flavour);

        Assert.Equal([mood, flavour], TilesFor(mood));
    }

    /// <summary>A folder's contents are reached by opening it, which the grid can do.</summary>
    [Fact]
    public void A_folders_contents_stay_behind_it()
    {
        var folder = Folder(Lent(Chill));

        Assert.Equal([folder], TilesFor(folder));
    }

    // ── the owner's app folders ──

    [Fact]
    public void A_folder_holding_only_the_owners_generated_lists_is_one_of_the_owners_app_folders()
        => Assert.True(HoldsGeneratedListsOnly(Folder(Lent(Chill, Lent(SlowBurn)), Folder(Lent(SlowBurn)))));

    [Fact]
    public void A_folder_with_one_list_somebody_made_in_it_is_theirs()
        => Assert.False(HoldsGeneratedListsOnly(Folder(Lent(Chill), Made())));

    /// <summary>
    /// The owner's Moods and Decades folders behave as the user's own do in the list view: a click
    /// expands them and they offer no menu. A folder somebody made still opens as its songs.
    /// </summary>
    [Theory]
    [InlineData(PlaylistRowKind.SmartFolder, false, true)]
    [InlineData(PlaylistRowKind.Folder, true, true)]
    [InlineData(PlaylistRowKind.Folder, false, false)]
    [InlineData(PlaylistRowKind.Playlist, true, false)]
    [InlineData(PlaylistRowKind.Smart, false, false)]
    public void The_owners_app_folders_are_app_folders_on_both_shelves(
        PlaylistRowKind kind, bool holdsGeneratedListsOnly, bool appFolder)
        => Assert.Equal(appFolder, PlaylistTreeItem.AppFolder(kind, holdsGeneratedListsOnly));

    /// <summary>An empty folder says nothing about who made it, so it is drawn as a folder, and so is
    /// one that holds nothing but empty folders.</summary>
    [Fact]
    public void An_empty_folder_is_a_folder()
    {
        Assert.False(HoldsGeneratedListsOnly(Folder()));
        Assert.False(HoldsGeneratedListsOnly(Folder(Folder())));
    }
}

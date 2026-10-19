using RPM.Core.Library;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Tests;

/// <summary>
/// The owner's generated lists as the borrowed shelf draws them.
///
/// <para>On a borrowed shelf a generated list IS a playlist: a fixed order of records with a name. This
/// side cannot rebuild it, because it is built from percentiles over the owner's whole collection, and
/// this machine has only a listing of that collection. Adding them to the borrowed playlist tree lets
/// the panel draw them without any special code.</para>
/// </summary>
public class BorrowedMoodListTests
{
    private static PeerMoodList List(string kind, string? genre = null, int decade = 0, params string[] ids)
        => new() { Kind = kind, Genre = genre, Decade = decade, RpmIds = ids };

    /// <summary>One that says where it sits, which is what an owner on a current build sends.</summary>
    private static PeerMoodList Filed(string kind, string?[] folder, params string[] ids)
        => new() { Kind = kind, Folder = [.. folder!], RpmIds = ids };

    /// <summary>A mood cut to one genre, filed where the owner's pane files it: one step below the
    /// mood's own list, the step being that list's row (SmartPlaylistFace.FolderChain).</summary>
    private static PeerMoodList Flavour(string kind, string genre, string?[] folder, params string[] ids)
        => new() { Kind = kind, Genre = genre, Folder = [.. folder!], RpmIds = ids };

    private static bool IsFolder(PlaylistTreeRow row) => row.Kind == PlaylistNodeKind.Folder;

    private static PlaylistTreeRow Holding(IReadOnlyList<PlaylistTreeRow> rows, string rpmId)
        => rows.Single(r => !IsFolder(r) && r.Items.Any(i => i.RpmId == rpmId));

    /// <summary>The shaping, with a stand-in for the naming. What each list is CALLED goes through the
    /// app's string table, and this host has no resource loader for it; the rearrangement below does
    /// not, which is why the two are separate arguments.</summary>
    private static IReadOnlyList<PlaylistTreeRow> Rows(params PeerMoodList[] lists)
        => BorrowedMoodLists.AsTree(lists, "Moods", l => $"{l.Kind}/{l.Genre}/{l.Decade}");

    [Fact]
    public void The_lists_arrive_as_playlists_under_a_folder_of_their_own()
    {
        var rows = Rows(
            List("Calm", "Rock", 0, "a", "b"),
            List("Danceable", null, 1980, "c"));

        var folder = Assert.Single(rows, r => r.Kind == PlaylistNodeKind.Folder);
        Assert.Equal(2, rows.Count(r => r.Kind != PlaylistNodeKind.Folder));
        Assert.All(rows.Where(r => r.Kind != PlaylistNodeKind.Folder), r => Assert.Equal(folder.Id, r.ParentId));
    }

    [Fact]
    public void A_generated_row_can_never_be_numbered_like_one_of_the_owners_own()
    {
        // The owner's playlist ids start at one. A collision would put a generated list where one of
        // their own lists should be, on a tree that is replaced whole every time it changes.
        var rows = Rows(
            List("Calm", null, 0, "a"),
            List("Energetic", null, 0, "b"),
            List("Sleep", null, 0, "c"));

        Assert.All(rows, r => Assert.True(r.Id < 0));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Every_record_in_a_list_crosses_in_order()
    {
        // What can be PLAYED is decided at play time: on the owner's network every one of these can be
        // requested from their machine, and away from it only the ones they put in the cloud resolve.
        var rows = Rows(List("Calm", null, 0, "a", "b", "c"));

        var list = Assert.Single(rows, r => r.Kind != PlaylistNodeKind.Folder);
        Assert.Equal(["a", "b", "c"], list.Items.Select(i => i.RpmId));
    }

    [Fact]
    public void A_shelf_with_no_lists_puts_no_empty_folder_on_screen()
    {
        // What every older build sends, and what a collection that has not been analysed sends.
        Assert.Empty(Rows());
    }

    [Fact]
    public void A_list_with_nothing_in_it_is_not_drawn()
    {
        // A row that promises music but opens empty. The borrower could not tell that apart from a
        // list whose records it simply cannot reach, which is a real state and is shown differently.
        Assert.Empty(Rows(List("Calm", null, 0)));
    }

    [Fact]
    public void Each_list_is_called_what_the_caller_says_to_call_it()
    {
        // The name is the app's own name for the same list: "Slow Burn" on a friend's shelf and
        // "Slow Burn" on your own are the same idea, and two names would suggest two different ideas.
        // It is passed as an argument because it goes through the string table and this rearrangement
        // does not.
        var rows = Rows(List("Calm", "Rock", 1970, "a"));

        Assert.Equal("Calm/Rock/1970", Assert.Single(rows, r => r.Kind != PlaylistNodeKind.Folder).Name);
    }

    [Fact]
    public void The_folder_is_called_what_the_caller_says_to_call_it()
        => Assert.Equal("Moods", Assert.Single(Rows(List("Calm", null, 0, "a")), r => r.Kind == PlaylistNodeKind.Folder).Name);

    // ── the owner's own arrangement ──

    /// <summary>
    /// The shelf the owner actually has. Every list says where it sits, so the moods, the flavours
    /// under each of them and the eras beside them arrive as three places, not as one flat pile.
    /// </summary>
    [Fact]
    public void The_owners_arrangement_crosses_with_the_lists()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"], "a"),
            Flavour("Calm", "Rock", ["Moods", "Chill"], "b"),
            Filed("Decade", ["Decades"], "c"));

        var moods = Assert.Single(rows, r => IsFolder(r) && r.Name == "Moods");
        var decades = Assert.Single(rows, r => IsFolder(r) && r.Name == "Decades");

        Assert.Null(moods.ParentId);
        Assert.Null(decades.ParentId);

        var chill = Holding(rows, "a");
        Assert.Equal(moods.Id, chill.ParentId);
        Assert.Equal(chill.Id, Holding(rows, "b").ParentId);
        Assert.Equal(decades.Id, Holding(rows, "c").ParentId);
    }

    // ── a mood and its flavours ──

    /// <summary>
    /// The step a flavour sits under is its mood's own list, so it goes under that list and no folder
    /// is made for it. Read as a folder, every mood with flavours would appear twice: a "Chill" list
    /// and a "Chill" folder beside it, with the same name and only one of them playable.
    /// </summary>
    [Fact]
    public void A_flavour_hangs_under_its_moods_list_and_no_second_row_is_made()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"], "a"),
            Flavour("Calm", "Rock", ["Moods", "Chill"], "b"),
            Flavour("Calm", "Jazz", ["Moods", "Chill"], "c"));

        Assert.Single(rows, IsFolder);
        var chill = Holding(rows, "a");
        Assert.Equal(chill.Id, Holding(rows, "b").ParentId);
        Assert.Equal(chill.Id, Holding(rows, "c").ParentId);
    }

    /// <summary>Which of the two is listed first is not a fact about the shelf, and the shape must not
    /// depend on it.</summary>
    [Fact]
    public void A_flavour_listed_before_its_mood_still_hangs_under_it()
    {
        var rows = Rows(
            Flavour("Calm", "Rock", ["Moods", "Chill"], "b"),
            Filed("Calm", ["Moods"], "a"));

        Assert.Single(rows, IsFolder);
        Assert.Equal(Holding(rows, "a").Id, Holding(rows, "b").ParentId);
    }

    /// <summary>A mood whose own list was not sent (too short to offer, or empty) leaves its flavours
    /// with no list to sit under. The step stays a folder: it is the only thing left that shows which
    /// mood they belong to.</summary>
    [Fact]
    public void A_flavour_whose_mood_did_not_come_keeps_the_moods_name_as_a_folder()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"]),
            Flavour("Calm", "Rock", ["Moods", "Chill"], "b"));

        var chill = Assert.Single(rows, r => IsFolder(r) && r.Name == "Chill");
        Assert.Equal(chill.Id, Holding(rows, "b").ParentId);
    }

    /// <summary>One mood's flavours go under that mood and not under another mood that is in the same
    /// place.</summary>
    [Fact]
    public void A_flavour_hangs_under_its_own_mood_and_no_other()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"], "a"),
            Filed("Energetic", ["Moods"], "e"),
            Flavour("Energetic", "Rock", ["Moods", "Adrenaline"], "b"));

        Assert.Equal(Holding(rows, "e").Id, Holding(rows, "b").ParentId);
    }

    // ── what each list is ──

    /// <summary>
    /// Every list carries the generated list it was built from, so it shows that list's own picture
    /// (the same one the owner sees) instead of the grid of covers a user-made playlist shows.
    /// </summary>
    [Fact]
    public void Each_list_carries_the_generated_list_it_is()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"], "a"),
            Flavour("Calm", "Rock", ["Moods", "Chill"], "b"),
            List("Decade", null, 1970, "c"));

        Assert.Equal(
            new PlaylistCover.Generated(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)),
            Holding(rows, "a").Cover);
        Assert.Equal(
            new PlaylistCover.Generated(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock")),
            Holding(rows, "b").Cover);
        Assert.Equal(
            new PlaylistCover.Generated(SmartPlaylistId.In(1970)),
            Holding(rows, "c").Cover);
    }

    /// <summary>A folder is not a generated list, so it is drawn as a folder.</summary>
    [Fact]
    public void A_folder_carries_no_generated_list()
        => Assert.All(
            Rows(Filed("Calm", ["Moods"], "a")).Where(IsFolder),
            r => Assert.IsType<PlaylistCover.Automatic>(r.Cover));

    /// <summary>A kind this build does not know means the owner has a newer build. The list still
    /// plays under the owner's name for it, and is drawn as a user-made playlist, not with another
    /// list's picture.</summary>
    [Fact]
    public void A_kind_this_build_does_not_know_is_drawn_as_a_made_playlist()
        => Assert.IsType<PlaylistCover.Automatic>(Holding(Rows(Filed("Windswept", ["Moods"], "a")), "a").Cover);

    /// <summary>A folder exists only because a list says it is inside it. Nothing else creates one, so
    /// a heading with nothing under it cannot appear.</summary>
    [Fact]
    public void A_place_no_list_names_is_not_made()
    {
        var rows = Rows(Filed("Calm", ["Moods"], "a"));

        Assert.Single(rows, r => r.Kind == PlaylistNodeKind.Folder);
        Assert.DoesNotContain(rows, r => r.Kind == PlaylistNodeKind.Folder && r.Name == "Decades");
    }

    /// <summary>Two lists in one place share it rather than each making their own.</summary>
    [Fact]
    public void One_place_is_made_once()
    {
        var rows = Rows(
            Filed("Calm", ["Moods", "Chill"], "a"),
            Filed("Calm", ["Moods", "Chill"], "b"));

        Assert.Equal(2, rows.Count(r => r.Kind == PlaylistNodeKind.Folder));
        Assert.Equal(2, rows.Count(r => r.Kind != PlaylistNodeKind.Folder));
    }

    /// <summary>Every row, folder or list, gets a number no playlist tree uses: the owner's own ids
    /// start at one, and a collision would put a generated row inside somebody's own folder.</summary>
    [Fact]
    public void No_generated_row_can_be_numbered_like_one_of_the_owners_own()
    {
        var rows = Rows(
            Filed("Calm", ["Moods"], "a"),
            Filed("Calm", ["Moods", "Chill"], "b"),
            Filed("Decade", ["Decades"], "c"));

        Assert.All(rows, r => Assert.True(r.Id < 0));
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
    }

    /// <summary>An owner whose build is older than the field sends nothing about where its lists go,
    /// and gets one flat folder for all of them.</summary>
    [Fact]
    public void An_owner_who_sends_no_arrangement_gets_the_flat_one()
    {
        var rows = Rows(List("Calm", "Rock", 0, "a"), List("Decade", null, 1970, "b"));

        var folder = Assert.Single(rows, r => r.Kind == PlaylistNodeKind.Folder);
        Assert.Equal("Moods", folder.Name);
        Assert.All(rows.Where(r => r.Kind != PlaylistNodeKind.Folder), r => Assert.Equal(folder.Id, r.ParentId));
    }
}

using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The parts of the tree a playlist needs in order to travel: where a node sits, which cloud ids this
/// library stands for, what it has let go of, and putting a folder chain back.
///
/// <para>A playlist made on a phone is taken in here as well as being written out. The whole risk is
/// the pair of mistakes that makes possible: taking back in something the user deleted, and filing
/// something somewhere other than where it was made.</para>
/// </summary>
public class PlaylistCloudTravelTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PlaylistCloudTravelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmtravel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── where a node sits ──

    [Fact]
    public void A_playlist_at_the_top_of_the_tree_has_no_folder_chain()
    {
        var playlist = _db.CreatePlaylist(null, "Loose");

        Assert.Empty(_db.PlaylistFolderChain(playlist));
    }

    [Fact]
    public void A_chain_reads_outermost_first_and_leaves_the_playlist_itself_out()
    {
        var rock = _db.CreatePlaylistFolder(null, "Rock");
        var nineties = _db.CreatePlaylistFolder(rock, "90s");
        var playlist = _db.CreatePlaylist(nineties, "On the train");

        Assert.Equal(["Rock", "90s"], _db.PlaylistFolderChain(playlist));
    }

    /// <summary>A folder is a node too, and asking where one sits is asking about its parents.</summary>
    [Fact]
    public void A_folder_has_a_chain_of_its_own()
    {
        var rock = _db.CreatePlaylistFolder(null, "Rock");
        var nineties = _db.CreatePlaylistFolder(rock, "90s");

        Assert.Equal(["Rock"], _db.PlaylistFolderChain(nineties));
    }

    [Fact]
    public void A_node_that_is_not_there_has_no_chain()
        => Assert.Empty(_db.PlaylistFolderChain(9999));

    // ── putting a chain back ──

    [Fact]
    public void An_empty_chain_is_the_top_of_the_tree_and_makes_nothing()
    {
        Assert.Null(_db.EnsurePlaylistFolder([]));
        Assert.DoesNotContain(_db.GetPlaylistNodes(), n => n.Kind == PlaylistNodeKind.Folder);
    }

    [Fact]
    public void A_chain_that_is_not_here_yet_is_made_whole()
    {
        var deepest = _db.EnsurePlaylistFolder(["Rock", "90s"]);

        Assert.NotNull(deepest);
        Assert.Equal(["Rock"], _db.PlaylistFolderChain(deepest!.Value));
        Assert.Equal("90s", _db.GetPlaylistNodes().Single(n => n.Id == deepest).Name);
    }

    /// <summary>The point of the whole thing: a second playlist filed in the same place goes into the
    /// same folder, not into a second one wearing the same name.</summary>
    [Fact]
    public void A_chain_that_is_already_here_is_found_rather_than_made_again()
    {
        var first = _db.EnsurePlaylistFolder(["Rock", "90s"]);
        var second = _db.EnsurePlaylistFolder(["Rock", "90s"]);

        Assert.Equal(first, second);
        Assert.Equal(2, _db.GetPlaylistNodes().Count(n => n.Kind == PlaylistNodeKind.Folder));
    }

    [Fact]
    public void Case_is_not_what_tells_two_folders_apart()
    {
        var made = _db.EnsurePlaylistFolder(["Rock"]);
        var found = _db.EnsurePlaylistFolder(["rock"]);

        Assert.Equal(made, found);
    }

    /// <summary>The same name in two places is two folders, because it is two places.</summary>
    [Fact]
    public void The_same_name_under_a_different_parent_is_a_different_folder()
    {
        var underRock = _db.EnsurePlaylistFolder(["Rock", "90s"]);
        var underPop = _db.EnsurePlaylistFolder(["Pop", "90s"]);

        Assert.NotEqual(underRock, underPop);
    }

    /// <summary>A playlist is a leaf. Matching one would end the chain inside something that cannot
    /// hold folders, so the folder is made beside it.</summary>
    [Fact]
    public void A_playlist_of_that_name_is_not_mistaken_for_a_folder()
    {
        var playlist = _db.CreatePlaylist(null, "Rock");
        var folder = _db.EnsurePlaylistFolder(["Rock"]);

        Assert.NotEqual(playlist, folder!.Value);
        Assert.Equal(PlaylistNodeKind.Folder, _db.GetPlaylistNodes().Single(n => n.Id == folder).Kind);
    }

    // ── which cloud ids this library stands for ──

    [Fact]
    public void A_library_that_has_never_synced_a_playlist_claims_nothing()
        => Assert.Empty(_db.PlaylistCloudIds());

    [Fact]
    public void A_synced_playlist_is_claimed()
    {
        var playlist = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(playlist, "cloud-1", 7);

        Assert.Contains("cloud-1", _db.PlaylistCloudIds());
    }

    /// <summary>Sync switched off is not the same as never having been there. The node still IS that
    /// playlist, and taking its cloud copy in as a new one would put a second copy beside it.</summary>
    [Fact]
    public void A_playlist_taken_out_of_the_cloud_still_claims_its_id()
    {
        var playlist = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(playlist, "cloud-1", 7);
        _db.SetPlaylistOutOfCloud(playlist);

        Assert.Contains("cloud-1", _db.PlaylistCloudIds());
    }

    // ── what it has let go of ──

    [Fact]
    public void Nothing_is_let_go_of_to_begin_with()
        => Assert.Empty(_db.CloudPlaylistsLetGo());

    /// <summary>The failure the note exists for: the row that remembered the cloud id is gone, so
    /// without this the next sync run would meet the playlist again and put it straight back.</summary>
    [Fact]
    public void Deleting_a_synced_playlist_writes_down_that_it_was_let_go()
    {
        var playlist = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(playlist, "cloud-1", 7);
        _db.DeletePlaylistNode(playlist);

        Assert.Contains("cloud-1", _db.CloudPlaylistsLetGo());
        Assert.DoesNotContain("cloud-1", _db.PlaylistCloudIds());
    }

    /// <summary>Deleting a folder deletes the playlists under it, and each of those is let go of just
    /// as surely as one deleted on its own.</summary>
    [Fact]
    public void Deleting_a_folder_lets_go_of_everything_synced_beneath_it()
    {
        var rock = _db.CreatePlaylistFolder(null, "Rock");
        var nineties = _db.CreatePlaylistFolder(rock, "90s");
        var one = _db.CreatePlaylist(rock, "One");
        var two = _db.CreatePlaylist(nineties, "Two");
        _db.SetPlaylistInCloud(one, "cloud-1", 7);
        _db.SetPlaylistInCloud(two, "cloud-2", 7);

        _db.DeletePlaylistNode(rock);

        var gone = _db.CloudPlaylistsLetGo();
        Assert.Contains("cloud-1", gone);
        Assert.Contains("cloud-2", gone);
    }

    [Fact]
    public void Deleting_a_playlist_that_was_never_in_the_cloud_lets_go_of_nothing()
    {
        _db.DeletePlaylistNode(_db.CreatePlaylist(null, "Loose"));

        Assert.Empty(_db.CloudPlaylistsLetGo());
    }

    /// <summary>Taking a playlist out of the cloud is the other way one leaves. The delete of the cloud
    /// copy is best effort and may not have happened (being offline is the normal case), so a copy
    /// still up there must not read as a playlist nobody claims.</summary>
    [Fact]
    public void Taking_a_playlist_out_of_the_cloud_writes_down_that_it_was_let_go()
    {
        var playlist = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(playlist, "cloud-1", 7);
        _db.SetPlaylistOutOfCloud(playlist);

        Assert.Contains("cloud-1", _db.CloudPlaylistsLetGo());
    }

    /// <summary>And the way back. Putting it in the cloud again is somebody saying they want it, which
    /// is exactly what the note said they did not.</summary>
    [Fact]
    public void Putting_a_playlist_back_in_the_cloud_clears_the_note()
    {
        var playlist = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(playlist, "cloud-1", 7);
        _db.SetPlaylistOutOfCloud(playlist);
        _db.SetPlaylistInCloud(playlist, "cloud-1", 9);

        Assert.Empty(_db.CloudPlaylistsLetGo());
    }

    /// <summary>Taking one in is a fresh node claiming an old id, and it goes through the same call,
    /// so a playlist deleted here and made again on a phone comes back rather than being refused for
    /// ever by a note about a copy that no longer exists.</summary>
    [Fact]
    public void A_playlist_taken_in_under_a_let_go_id_clears_the_note()
    {
        var first = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(first, "cloud-1", 7);
        _db.DeletePlaylistNode(first);

        var second = _db.CreatePlaylist(null, "On the train");
        _db.SetPlaylistInCloud(second, "cloud-1", 9);

        Assert.Empty(_db.CloudPlaylistsLetGo());
        Assert.Contains("cloud-1", _db.PlaylistCloudIds());
    }
    // ── what the account takes with it ──

    /// <summary>The one thing a sign-out must get right: what the cloud lent this disk goes, what
    /// the listener made here stays.</summary>
    [Fact]
    public void Signing_out_drops_the_playlists_the_account_lent_and_keeps_the_listeners_own()
    {
        var mine = _db.CreatePlaylist(null, "Made here");
        _db.SetPlaylistInCloud(mine, "cloud-mine", 7);   // pushed up, still this machine's

        var theirs = _db.CreatePlaylist(null, "Made on the phone");
        _db.SetPlaylistInCloud(theirs, "cloud-theirs", 7);
        _db.MarkPlaylistAdopted(theirs);

        Assert.Equal(1, _db.ForgetAdoptedPlaylists());

        var left = _db.GetPlaylistNodes().Where(n => !n.IsNowPlaying).Select(n => n.Name).ToList();
        Assert.Contains("Made here", left);
        Assert.DoesNotContain("Made on the phone", left);
    }

    /// <summary>The failure this whole thing is about: a playlist taken in from the account must not
    /// be written down as let go when the account leaves. Signing back in has to bring it down
    /// again, and a let-go note is the one fact that would stop it, on every computer, for good.</summary>
    [Fact]
    public void Signing_out_does_not_write_down_that_the_account_playlists_were_let_go()
    {
        var theirs = _db.CreatePlaylist(null, "Made on the phone");
        _db.SetPlaylistInCloud(theirs, "cloud-theirs", 7);
        _db.MarkPlaylistAdopted(theirs);

        _db.ForgetAdoptedPlaylists();

        Assert.Empty(_db.CloudPlaylistsLetGo());
        Assert.DoesNotContain("cloud-theirs", _db.PlaylistCloudIds());
    }

    [Fact]
    public void The_songs_of_a_dropped_playlist_go_with_it()
    {
        var theirs = _db.CreatePlaylist(null, "Made on the phone");
        _db.AddSongsToPlaylist(theirs, [(null, "Band/Record/01.mp3"), (null, "Band/Record/02.mp3")], AlreadyInPlaylist.AddAnother);
        _db.MarkPlaylistAdopted(theirs);

        Assert.Equal(2, _db.GetPlaylistItems(theirs).Count);

        _db.ForgetAdoptedPlaylists();

        Assert.Empty(_db.GetPlaylistItems(theirs));
    }

    [Fact]
    public void A_playlist_is_this_machines_own_until_something_says_otherwise()
    {
        // The default the migration gives every existing row, and the safe one: a sign-out that
        // guessed the other way would delete playlists nobody had ever put in a cloud.
        _db.CreatePlaylist(null, "Loose");

        Assert.Equal(0, _db.ForgetAdoptedPlaylists());
        Assert.Contains("Loose", _db.GetPlaylistNodes().Select(n => n.Name));
    }

    [Fact]
    public void Signing_out_twice_drops_nothing_the_second_time()
    {
        var theirs = _db.CreatePlaylist(null, "Made on the phone");
        _db.MarkPlaylistAdopted(theirs);

        Assert.Equal(1, _db.ForgetAdoptedPlaylists());
        Assert.Equal(0, _db.ForgetAdoptedPlaylists());
    }
}

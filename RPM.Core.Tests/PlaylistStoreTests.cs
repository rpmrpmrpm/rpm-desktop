using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The playlist tree (schema v8): folders and playlists, songs kept by RPM id, nested playlists,
/// cycle guarding, cascade delete, and the flatten that turns a playlist into a play order. A pinned
/// "Now playing" playlist exists from the start.
/// </summary>
public class PlaylistStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PlaylistStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private LibraryTrack Seed(string relativePath, string rpmId)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            RpmId = rpmId,
            SizeBytes = 1,
            ModifiedUtcTicks = 1,
        });
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    [Fact]
    public void NowPlaying_IsSeeded_AndPinned()
    {
        var nowPlaying = _db.NowPlayingNodeId();
        var node = _db.GetPlaylistNodes().Single(n => n.Id == nowPlaying);

        Assert.True(node.IsNowPlaying);
        Assert.Equal(PlaylistNodeKind.Playlist, node.Kind);
        Assert.Null(node.ParentId);
    }

    [Fact]
    public void NowPlaying_CannotBeRenamedOrDeleted()
    {
        var id = _db.NowPlayingNodeId();
        var before = _db.GetPlaylistNodes().Single(n => n.Id == id).Name;

        _db.RenamePlaylistNode(id, "Something else");
        _db.DeletePlaylistNode(id);

        var after = _db.GetPlaylistNodes().SingleOrDefault(n => n.Id == id);
        Assert.NotNull(after);
        Assert.Equal(before, after!.Name);
    }

    [Fact]
    public void CreateFolder_And_Playlist_Nest()
    {
        var folder = _db.CreatePlaylistFolder(null, "Road trips");
        var playlist = _db.CreatePlaylist(folder, "Summer 2026");

        var nodes = _db.GetPlaylistNodes();
        var f = nodes.Single(n => n.Id == folder);
        var p = nodes.Single(n => n.Id == playlist);

        Assert.Equal(PlaylistNodeKind.Folder, f.Kind);
        Assert.Null(f.ParentId);
        Assert.Equal(PlaylistNodeKind.Playlist, p.Kind);
        Assert.Equal(folder, p.ParentId);
    }

    [Fact]
    public void Rename_ChangesName()
    {
        var id = _db.CreatePlaylist(null, "Old");
        _db.RenamePlaylistNode(id, "New");
        Assert.Equal("New", _db.GetPlaylistNodes().Single(n => n.Id == id).Name);
    }

    /// <summary>What the playlists pane shows under its heading. Folders are not playlists. The pinned
    /// Now playing node is the player's queue, not one of the user's lists. A nested playlist
    /// reference adds no time of its own, because its songs are already counted where they live.</summary>
    [Fact]
    public void PlaylistTotals_count_the_users_lists_and_their_running_time()
    {
        SeedWithDuration("A/1.mp3", "gid-1", 120);
        SeedWithDuration("A/2.mp3", "gid-2", 90.5);

        var folder = _db.CreatePlaylistFolder(null, "Road trips");
        var summer = _db.CreatePlaylist(folder, "Summer");
        var winter = _db.CreatePlaylist(null, "Winter");
        _db.AddSongsToPlaylist(summer, [("gid-1", "A/1.mp3"), ("gid-2", "A/2.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddSongsToPlaylist(winter, [("gid-1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(winter, summer);          // nesting adds no time of its own

        var (playlists, seconds) = _db.QueryPlaylistTotals();
        Assert.Equal(2, playlists);                  // the folder and Now playing are not counted
        Assert.Equal(330.5, seconds, 3);             // 120 + 90.5 in Summer, 120 again in Winter
    }

    [Fact]
    public void PlaylistTotals_of_a_fresh_library_are_zero()
    {
        var (playlists, seconds) = _db.QueryPlaylistTotals();
        Assert.Equal(0, playlists);                  // only the pinned Now playing exists
        Assert.Equal(0, seconds);
    }

    private void SeedWithDuration(string relativePath, string rpmId, double seconds)
    {
        Seed(relativePath, rpmId);
        var track = _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
        _db.Upsert(track with { DurationSeconds = seconds });
    }

    [Fact]
    public void AddSongs_Then_Flatten_ResolvesByRpmId_InOrder()
    {
        Seed("A/1.mp3", "gid-1");
        Seed("A/2.mp3", "gid-2");
        var pl = _db.CreatePlaylist(null, "Two");
        _db.AddSongsToPlaylist(pl, [("gid-2", "A/2.mp3"), ("gid-1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        var order = _db.FlattenPlaylistTracks(pl).Select(t => t.RelativePath).ToList();
        Assert.Equal(new[] { "A/2.mp3", "A/1.mp3" }, order);
    }

    [Fact]
    public void Flatten_FallsBackToRelPath_WhenNoRpmId()
    {
        Seed("A/1.mp3", "gid-1");
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [(null, "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Single(_db.FlattenPlaylistTracks(pl));
    }

    [Fact]
    public void Flatten_SkipsSongsNoLongerInLibrary()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("ghost", "gone/x.mp3")], AlreadyInPlaylist.AddAnother);
        Assert.Empty(_db.FlattenPlaylistTracks(pl));
    }

    [Fact]
    public void NestedPlaylist_FlattensInPlace()
    {
        Seed("A/1.mp3", "g1");
        Seed("A/2.mp3", "g2");
        var inner = _db.CreatePlaylist(null, "Inner");
        _db.AddSongsToPlaylist(inner, [("g2", "A/2.mp3")], AlreadyInPlaylist.AddAnother);

        var outer = _db.CreatePlaylist(null, "Outer");
        _db.AddSongsToPlaylist(outer, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);
        Assert.True(_db.AddPlaylistRef(outer, inner));

        var order = _db.FlattenPlaylistTracks(outer).Select(t => t.RelativePath).ToList();
        Assert.Equal(new[] { "A/1.mp3", "A/2.mp3" }, order);
    }

    [Fact]
    public void AddPlaylistRef_RefusesDirectCycle()
    {
        var pl = _db.CreatePlaylist(null, "P");
        Assert.False(_db.AddPlaylistRef(pl, pl));
    }

    [Fact]
    public void AddPlaylistRef_RefusesIndirectCycle()
    {
        var a = _db.CreatePlaylist(null, "A");
        var b = _db.CreatePlaylist(null, "B");
        Assert.True(_db.AddPlaylistRef(a, b));    // A contains B
        Assert.False(_db.AddPlaylistRef(b, a));   // B containing A would loop
    }

    [Fact]
    public void Delete_RemovesSubtree_AndDanglingRefs()
    {
        var folder = _db.CreatePlaylistFolder(null, "F");
        var child = _db.CreatePlaylist(folder, "Child");
        var other = _db.CreatePlaylist(null, "Other");
        _db.AddPlaylistRef(other, child);   // Other references a playlist about to die

        _db.DeletePlaylistNode(folder);

        var nodes = _db.GetPlaylistNodes();
        Assert.DoesNotContain(nodes, n => n.Id == folder);
        Assert.DoesNotContain(nodes, n => n.Id == child);
        // The reference to the deleted child is gone from Other.
        Assert.Empty(_db.GetPlaylistItems(other));
    }

    [Fact]
    public void Reorder_RewritesItemOrder()
    {
        Seed("A/1.mp3", "g1");
        Seed("A/2.mp3", "g2");
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3"), ("g2", "A/2.mp3")], AlreadyInPlaylist.AddAnother);

        var items = _db.GetPlaylistItems(pl);
        _db.ReorderPlaylistItems(pl, [items[1].Id, items[0].Id]);

        var order = _db.FlattenPlaylistTracks(pl).Select(t => t.RelativePath).ToList();
        Assert.Equal(new[] { "A/2.mp3", "A/1.mp3" }, order);
    }

    /// <summary>What makes the open playlist's rows removable: each row's own item id in stored order,
    /// and the kind of each. The detail list uses these to match its rows to the stored items. A
    /// nested playlist is one item however many songs it stands for, so the ids cannot be matched
    /// against a flattened order, which is longer.</summary>
    [Fact]
    public void Items_carry_their_own_id_and_kind_in_order()
    {
        Seed("A/1.mp3", "g1");
        Seed("A/2.mp3", "g2");
        var nested = _db.CreatePlaylist(null, "Nested");
        _db.AddSongsToPlaylist(nested, [("g1", "A/1.mp3"), ("g2", "A/2.mp3")], AlreadyInPlaylist.AddAnother);

        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(pl, nested);

        var items = _db.GetPlaylistItems(pl);

        Assert.Equal(
            new[] { PlaylistItemKind.Song, PlaylistItemKind.Playlist },
            items.Select(i => i.ItemKind));
        Assert.Equal(nested, items[1].RefPlaylistId);
        Assert.All(items, i => Assert.True(i.Id > 0));
        Assert.Equal(items.Select(i => i.Id).Distinct().Count(), items.Count);

        // Two items, three tracks: a list holding a reference cannot have its rows numbered by item.
        Assert.Equal(3, _db.FlattenPlaylistTracks(pl).Count);
    }

    [Fact]
    public void RemoveItem_DropsIt()
    {
        Seed("A/1.mp3", "g1");
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);
        var item = _db.GetPlaylistItems(pl).Single();

        _db.RemovePlaylistItem(item.Id);
        Assert.Empty(_db.GetPlaylistItems(pl));
    }

    // ── playlists in the cloud ──

    [Fact]
    public void A_playlist_starts_out_of_the_cloud()
    {
        var pl = _db.CreatePlaylist(null, "P");
        Assert.Equal(PlaylistCloud.None, _db.GetPlaylistCloud(pl));
    }

    /// <summary>
    /// The cloud id is the name the playlist has in the cloud, so it is written once and kept.
    /// Pushing the same playlist again after removing it must replace what is stored. It must not
    /// leave a second copy under a new id that nothing ever cleans up.
    /// </summary>
    [Fact]
    public void The_cloud_id_is_written_once_and_survives_being_taken_out()
    {
        var pl = _db.CreatePlaylist(null, "P");

        _db.SetPlaylistInCloud(pl, "cloud-1", 100);
        Assert.Equal(new PlaylistCloud("cloud-1", true, 100), _db.GetPlaylistCloud(pl));

        // A second push with a different id must not rename it in the cloud.
        _db.SetPlaylistInCloud(pl, "cloud-2", 200);
        Assert.Equal(new PlaylistCloud("cloud-1", true, 200), _db.GetPlaylistCloud(pl));

        _db.SetPlaylistOutOfCloud(pl);
        var after = _db.GetPlaylistCloud(pl);
        Assert.False(after.Selected);
        Assert.Equal("cloud-1", after.CloudId);   // kept, so a re-add replaces rather than duplicates
    }

    [Fact]
    public void Only_the_playlists_the_user_put_in_the_cloud_are_listed()
    {
        var up = _db.CreatePlaylist(null, "Up");
        _db.CreatePlaylist(null, "Down");
        var folder = _db.CreatePlaylistFolder(null, "F");
        _db.SetPlaylistInCloud(up, "cloud-1", 1);
        _db.SetPlaylistInCloud(folder, "cloud-2", 1);   // a folder is not a thing to play

        var listed = _db.CloudSelectedPlaylists();
        Assert.Equal("Up", Assert.Single(listed).Name);
    }

    /// <summary>
    /// Removing one playlist from the cloud must not remove tracks that another playlist needs. This
    /// is the set of tracks to keep, and it follows nesting: a synced playlist needs everything it
    /// plays.
    /// </summary>
    [Fact]
    public void Tracks_another_cloud_playlist_still_plays_are_held_back()
    {
        var shared = Seed("A/shared.mp3", "g-shared");
        var onlyMine = Seed("A/mine.mp3", "g-mine");
        var nested = Seed("A/nested.mp3", "g-nested");

        var leaving = _db.CreatePlaylist(null, "Leaving");
        _db.AddSongsToPlaylist(leaving, [("g-shared", "A/shared.mp3"), ("g-mine", "A/mine.mp3")], AlreadyInPlaylist.AddAnother);

        var inner = _db.CreatePlaylist(null, "Inner");
        _db.AddSongsToPlaylist(inner, [("g-nested", "A/nested.mp3")], AlreadyInPlaylist.AddAnother);

        var staying = _db.CreatePlaylist(null, "Staying");
        _db.AddSongsToPlaylist(staying, [("g-shared", "A/shared.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(staying, inner);

        _db.SetPlaylistInCloud(leaving, "cloud-leaving", 1);
        _db.SetPlaylistInCloud(staying, "cloud-staying", 1);

        var held = _db.TrackIdsHeldByOtherCloudPlaylists(exceptNodeId: leaving);

        Assert.Contains(shared.Id, held);        // Staying plays it too
        Assert.Contains(nested.Id, held);        // reached through Staying's nested playlist
        Assert.DoesNotContain(onlyMine.Id, held);// only Leaving wanted this one

        // The playlist being removed does not count as a reason to keep its own tracks.
        Assert.DoesNotContain(onlyMine.Id, _db.TrackIdsHeldByOtherCloudPlaylists(exceptNodeId: leaving));
    }

    /// <summary>
    /// A playlist is working while any track it plays is queued or uploading, including a track
    /// reached through a nested playlist. It is not finished until everything it plays has arrived.
    /// </summary>
    [Fact]
    public void A_playlist_is_working_while_anything_it_plays_is_on_its_way()
    {
        var direct = Seed("A/direct.mp3", "g-direct");
        var nested = Seed("A/nested.mp3", "g-nested");

        var inner = _db.CreatePlaylist(null, "Inner");
        _db.AddSongsToPlaylist(inner, [("g-nested", "A/nested.mp3")], AlreadyInPlaylist.AddAnother);

        var outer = _db.CreatePlaylist(null, "Outer");
        _db.AddSongsToPlaylist(outer, [("g-direct", "A/direct.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(outer, inner);

        Assert.False(_db.PlaylistHasTracksInFlight(outer));

        _db.SetCloudState(direct.Id, CloudState.Queued);
        Assert.True(_db.PlaylistHasTracksInFlight(outer));

        _db.SeedSynced(direct.Id, "h1", 1, 1);
        Assert.False(_db.PlaylistHasTracksInFlight(outer));

        // Reached only through the nested playlist, and it still counts.
        _db.SetCloudState(nested.Id, CloudState.Uploading);
        Assert.True(_db.PlaylistHasTracksInFlight(outer));
        Assert.True(_db.PlaylistHasTracksInFlight(inner));

        _db.SeedSynced(nested.Id, "h2", 1, 1);
        Assert.False(_db.PlaylistHasTracksInFlight(outer));
    }

    /// <summary>A cycle must not hang the check. The flatten guards against it, and this check runs
    /// on every tick of an upload.</summary>
    [Fact]
    public void A_cycle_does_not_hang_the_in_flight_check()
    {
        var a = _db.CreatePlaylist(null, "A");
        var b = _db.CreatePlaylist(null, "B");
        _db.AddPlaylistRef(a, b);
        _db.AddPlaylistRef(b, a);   // refused as a cycle, but the guard is what is being relied on

        Assert.False(_db.PlaylistHasTracksInFlight(a));
    }

    /// <summary>A playlist that is not in the cloud holds nothing back: its tracks are in the cloud
    /// for some other reason.</summary>
    [Fact]
    public void A_playlist_out_of_the_cloud_holds_nothing()
    {
        Seed("A/1.mp3", "g1");
        var pl = _db.CreatePlaylist(null, "Local only");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Empty(_db.TrackIdsHeldByOtherCloudPlaylists(exceptNodeId: 0));
    }

    // ── which playlists hold an artist's songs ──

    [Fact]
    public void PlaylistsHolding_CountsOnlyTheSongsAskedAbout()
    {
        var a = Seed("Kanye/Graduation/01 Good Morning.mp3", "g1");
        var b = Seed("Kanye/Graduation/02 Champion.mp3", "g2");
        var other = Seed("Blur/Parklife/01 Girls.mp3", "b1");

        var mine = _db.CreatePlaylist(null, "Drive");
        var theirs = _db.CreatePlaylist(null, "Britpop");

        _db.AddSongsToPlaylist(mine, [(a.RpmId, a.RelativePath), (other.RpmId, other.RelativePath),
                                      (b.RpmId, b.RelativePath)], AlreadyInPlaylist.AddAnother);
        _db.AddSongsToPlaylist(theirs, [(other.RpmId, other.RelativePath)], AlreadyInPlaylist.AddAnother);

        var held = _db.PlaylistsHolding(new HashSet<string> { "g1", "g2" }, new HashSet<string>());

        var one = Assert.Single(held);
        Assert.Equal(mine, one.NodeId);
        Assert.Equal("Drive", one.Name);
        Assert.Equal(2, one.Songs);
    }

    [Fact]
    public void PlaylistsHolding_FindsAnEntryKeptOnlyByItsPath()
    {
        var song = Seed("Kanye/Graduation/01 Good Morning.mp3", "g1");
        var list = _db.CreatePlaylist(null, "Old");

        // An entry from before the file had an id: the path is the only way to find it.
        _db.AddSongsToPlaylist(list, [(null, song.RelativePath)], AlreadyInPlaylist.AddAnother);

        Assert.Empty(_db.PlaylistsHolding(new HashSet<string> { "g1" }, new HashSet<string>()));

        var held = _db.PlaylistsHolding(
            new HashSet<string> { "g1" }, new HashSet<string> { song.RelativePath });
        Assert.Equal(list, Assert.Single(held).NodeId);
    }


    [Fact]
    public void PlaylistsHolding_IgnoresTheNowPlayingQueue()
    {
        var song = Seed("Kanye/Graduation/01 Good Morning.mp3", "g1");
        _db.AddSongsToPlaylist(_db.NowPlayingNodeId(), [(song.RpmId, song.RelativePath)], AlreadyInPlaylist.AddAnother);

        Assert.Empty(_db.PlaylistsHolding(new HashSet<string> { "g1" }, new HashSet<string>()));
    }

    [Fact]
    public void PlaylistsHolding_DoesNotFollowANestedPlaylist()
    {
        var song = Seed("Kanye/Graduation/01 Good Morning.mp3", "g1");
        var inner = _db.CreatePlaylist(null, "Inner");
        var outer = _db.CreatePlaylist(null, "Outer");

        _db.AddSongsToPlaylist(inner, [(song.RpmId, song.RelativePath)], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(outer, inner);

        var held = _db.PlaylistsHolding(new HashSet<string> { "g1" }, new HashSet<string>());
        Assert.Equal(inner, Assert.Single(held).NodeId);
    }

    [Fact]
    public void PlaylistsHolding_IsMostOfTheArtistFirst()
    {
        var a = Seed("Kanye/Graduation/01 Good Morning.mp3", "g1");
        var b = Seed("Kanye/Graduation/02 Champion.mp3", "g2");

        var few = _db.CreatePlaylist(null, "Few");
        var many = _db.CreatePlaylist(null, "Many");
        _db.AddSongsToPlaylist(few, [(a.RpmId, a.RelativePath)], AlreadyInPlaylist.AddAnother);
        _db.AddSongsToPlaylist(many, [(a.RpmId, a.RelativePath), (b.RpmId, b.RelativePath)], AlreadyInPlaylist.AddAnother);

        var held = _db.PlaylistsHolding(new HashSet<string> { "g1", "g2" }, new HashSet<string>());

        Assert.Equal([many, few], held.Select(p => p.NodeId));
    }

    [Fact]
    public void PlaylistsHolding_AsksNothingWhenThereIsNothingToAskAbout()
        => Assert.Empty(_db.PlaylistsHolding(new HashSet<string>(), new HashSet<string>()));

    // ── a song the playlist already holds ──

    [Fact]
    public void LeaveIt_DoesNotAddASongThePlaylistAlreadyHolds()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        var added = _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.LeaveIt);

        Assert.Equal(0, added);
        Assert.Single(_db.GetPlaylistItems(pl));
    }

    [Fact]
    public void AddAnother_AddsASecondCopy()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        var added = _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(1, added);
        Assert.Equal(["g1", "g1"], _db.GetPlaylistItems(pl).Select(i => i.RpmId));
    }

    [Fact]
    public void LeaveIt_AddsTheNewSongsOfABatchInOrder_AndSkipsTheOnesThere()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g2", "A/2.mp3")], AlreadyInPlaylist.AddAnother);

        var added = _db.AddSongsToPlaylist(pl,
            [("g1", "A/1.mp3"), ("g2", "A/2.mp3"), ("g3", "A/3.mp3")], AlreadyInPlaylist.LeaveIt);

        Assert.Equal(2, added);
        Assert.Equal(["g2", "g1", "g3"], _db.GetPlaylistItems(pl).Select(i => i.RpmId));
    }

    [Fact]
    public void LeaveIt_PutsASongGivenTwiceInOneCallInOnce()
    {
        var pl = _db.CreatePlaylist(null, "P");

        var added = _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3"), ("g1", "A/1.mp3")], AlreadyInPlaylist.LeaveIt);

        Assert.Equal(1, added);
        Assert.Single(_db.GetPlaylistItems(pl));
    }

    /// <summary>An entry added before the file had an id is kept by its path alone. The same song added
    /// again, now with an id, is still the same song.</summary>
    [Fact]
    public void AnEntryKeptByPathAlone_IsTheSameSong()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [(null, "A/1.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(1, _db.CountAlreadyInPlaylist(pl, [("g1", "A/1.mp3")]));
        Assert.Equal(0, _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.LeaveIt));
    }

    /// <summary>A file that moved still has its id, and the entry is still that song.</summary>
    [Fact]
    public void AnEntryWithTheSameId_IsTheSameSong_WhateverItsPath()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "Old/1.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(1, _db.CountAlreadyInPlaylist(pl, [("g1", "New/1.mp3")]));
    }

    [Fact]
    public void Count_SaysHowManyOfTheSongsAreThere()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3"), ("g3", "A/3.mp3")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(2, _db.CountAlreadyInPlaylist(pl,
            [("g1", "A/1.mp3"), ("g2", "A/2.mp3"), ("g3", "A/3.mp3")]));
        Assert.Equal(0, _db.CountAlreadyInPlaylist(pl, [("g2", "A/2.mp3")]));
    }

    /// <summary>A song in a nested playlist is in that playlist, not this one: the same rule
    /// <see cref="LibraryDatabase.PlaylistsHolding"/> follows.</summary>
    [Fact]
    public void ASongInANestedPlaylist_IsNotCounted()
    {
        var inner = _db.CreatePlaylist(null, "Inner");
        var outer = _db.CreatePlaylist(null, "Outer");
        _db.AddSongsToPlaylist(inner, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother);
        _db.AddPlaylistRef(outer, inner);

        Assert.Equal(0, _db.CountAlreadyInPlaylist(outer, [("g1", "A/1.mp3")]));
    }

    /// <summary>The user can be asked about a playlist that is deleted before they answer. pl_item
    /// has no foreign key, so without the check, rows would be written under a node that does not
    /// exist.</summary>
    [Fact]
    public void NothingIsWrittenToAPlaylistThatWasDeleted()
    {
        var pl = _db.CreatePlaylist(null, "P");
        _db.DeletePlaylistNode(pl);

        Assert.Equal(0, _db.AddSongsToPlaylist(pl, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother));
        Assert.Empty(_db.GetPlaylistItems(pl));
    }

    [Fact]
    public void NothingIsWrittenToAFolder()
    {
        var folder = _db.CreatePlaylistFolder(null, "F");

        Assert.Equal(0, _db.AddSongsToPlaylist(folder, [("g1", "A/1.mp3")], AlreadyInPlaylist.AddAnother));
        Assert.Empty(_db.GetPlaylistItems(folder));
    }
}

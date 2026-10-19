using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// A friend's playlists, carried across with their records.
///
/// <para>
/// A wall of somebody's albums without their playlists is their music with their listening taken out,
/// so the tree travels with the listing. What is pinned here is the two halves of that: the tree
/// arrives whole and stays put when it has not moved, and what the borrower is shown narrows to what
/// they can actually play the moment its owner leaves the network.
/// </para>
///
/// <para>
/// The narrowing is derived from the SONGS, never from a flag on the playlist. A list of twenty
/// records where nineteen are in its owner's cloud and one is on their laptop stops nineteen tracks
/// in, and stopping partway is worse than not being offered at all.
/// </para>
/// </summary>
public class PeerPlaylistShareTests : IDisposable
{
    private const string Friend = "FRIEND1";

    private readonly string _theirs;
    private readonly string _here;
    private readonly LibraryDatabase _lender;
    private readonly LibraryDatabase _shelf;

    public PeerPlaylistShareTests()
    {
        _theirs = Path.Combine(Path.GetTempPath(), $"plshare-lender-{Guid.NewGuid():N}");
        _here = Path.Combine(Path.GetTempPath(), $"plshare-shelf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_theirs);
        Directory.CreateDirectory(_here);
        _lender = new LibraryDatabase(MirrorLocation.InFolder(_theirs));
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_here));
    }

    public void Dispose()
    {
        _lender.Dispose();
        _shelf.Dispose();
        try { Directory.Delete(_theirs, recursive: true); } catch { }
        try { Directory.Delete(_here, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <param name="inCloud">Their storage has a copy, which is what decides whether the record is
    /// still there once they have gone home.</param>
    private static PeerTrack Track(string rpmId, string path, bool inCloud) => new()
    {
        RpmId = rpmId,
        RelativePath = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = "Band",
        AlbumArtist = "Band",
        Album = "Record",
        TrackNumber = 1,
        DurationSeconds = 200,
        SizeBytes = 5_000_000,
        InCloud = inCloud,
    };

    /// <summary>Puts a listing on the borrowed shelf, marked the way a guest's is: everything the
    /// owner's cloud holds is a cloud row here.</summary>
    private void Lend(params PeerTrack[] listing) =>
        _shelf.MirrorPeerLibrary(
            Friend, PeerLibraryMirror.ToCatalogRows(listing),
            cloudOnly: PeerLibraryMirror.CloudBackedIds(listing));

    /// <summary>The lender's tree, over the wire and into the borrowed shelf (the whole journey the
    /// real thing makes, so nothing in the middle can be wrong in a way these tests do not see).</summary>
    private int Carry() =>
        _shelf.MirrorPlaylistTree(PeerCatalogBuilder.To(PeerCatalogBuilder.Playlists(_lender.PlaylistTree(), new Dictionary<long, string>())));

    private static PlaylistNode? Named(IReadOnlyList<PlaylistNode> nodes, string name) =>
        nodes.FirstOrDefault(n => n.Name == name);

    // ── the tree arrives ──

    [Fact]
    public void Their_folders_playlists_and_the_order_of_them_all_arrive()
    {
        var folder = _lender.CreatePlaylistFolder(null, "Driving");
        var road = _lender.CreatePlaylist(folder, "Road trips");
        var sunday = _lender.CreatePlaylist(null, "Sunday morning");

        _lender.AddSongsToPlaylist(road, [("gid-1", "Band/Record/01 One.flac"),
                                          ("gid-2", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(sunday, [("gid-3", "Band/Record/03 Three.flac")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(3, Carry());

        var nodes = _shelf.GetPlaylistNodes();
        var here = Named(nodes, "Driving");
        var there = Named(nodes, "Road trips");

        Assert.NotNull(here);
        Assert.NotNull(there);
        Assert.Equal(PlaylistNodeKind.Folder, here!.Kind);
        Assert.Equal(here.Id, there!.ParentId);
        Assert.NotNull(Named(nodes, "Sunday morning"));

        var items = _shelf.GetPlaylistItems(there.Id);
        Assert.Equal(["gid-1", "gid-2"], items.Select(i => i.RpmId));
    }

    [Fact]
    public void A_playlist_nested_inside_another_still_points_at_it_on_the_other_side()
    {
        var outer = _lender.CreatePlaylist(null, "Everything");
        var inner = _lender.CreatePlaylist(null, "Late night");
        _lender.AddSongsToPlaylist(inner, [("gid-1", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);
        Assert.True(_lender.AddPlaylistRef(outer, inner));

        Carry();
        Lend(Track("gid-1", "Band/Record/01 One.flac", inCloud: true));

        var nodes = _shelf.GetPlaylistNodes();
        var reference = _shelf.GetPlaylistItems(Named(nodes, "Everything")!.Id).Single();

        Assert.Equal(PlaylistItemKind.Playlist, reference.ItemKind);
        Assert.Equal(Named(nodes, "Late night")!.Id, reference.RefPlaylistId);

        // …and it resolves through the nesting, which is the only thing the reference is for.
        Assert.Equal(["gid-1"],
            _shelf.FlattenPlaylistTracks(Named(nodes, "Everything")!.Id).Select(t => t.RpmId));
    }

    [Fact]
    public void The_queue_is_nobodys_business_but_the_machine_it_is_playing_on()
    {
        _lender.AddSongsToPlaylist(_lender.NowPlayingNodeId(), [("gid-9", "Band/Record/09 Nine.flac")], AlreadyInPlaylist.AddAnother);
        var mine = _shelf.NowPlayingNodeId();
        _shelf.AddSongsToPlaylist(mine, [("gid-1", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);

        _lender.CreatePlaylist(null, "Sunday morning");
        Carry();

        // Theirs did not travel…
        Assert.DoesNotContain(PeerCatalogBuilder.Playlists(_lender.PlaylistTree(), new Dictionary<long, string>()),
                              p => p.Items.Any(i => i.RpmId == "gid-9"));

        // …and mine was not carried away by a pass that replaced everything around it.
        Assert.Equal(mine, _shelf.NowPlayingNodeId());
        Assert.Equal(["gid-1"], _shelf.GetPlaylistItems(mine).Select(i => i.RpmId));
    }

    [Fact]
    public void A_tree_that_has_not_moved_is_left_alone()
    {
        var road = _lender.CreatePlaylist(null, "Road trips");
        _lender.AddSongsToPlaylist(road, [("gid-1", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(1, Carry());
        var before = Named(_shelf.GetPlaylistNodes(), "Road trips")!.Id;

        // The listing moves for reasons that have nothing to do with the playlists, a record added,
        // a sleeve found, and the tree must not be rewritten under the panel every time.
        Assert.Equal(0, Carry());
        Assert.Equal(before, Named(_shelf.GetPlaylistNodes(), "Road trips")!.Id);

        _lender.RenamePlaylistNode(road, "Long drives");
        Assert.Equal(1, Carry());
        Assert.NotNull(Named(_shelf.GetPlaylistNodes(), "Long drives"));
        Assert.Null(Named(_shelf.GetPlaylistNodes(), "Road trips"));
    }

    [Fact]
    public void A_node_whose_parent_did_not_travel_settles_like_any_other()
    {
        // A listing can hold a row with nothing above it in the listing: one filed under the pinned
        // queue, which never travels, or under a folder its owner has since deleted. The mirror files
        // it at the top rather than pointing it at nothing.
        var queue = _lender.NowPlayingNodeId();
        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(queue, "Filed under the queue"),
                                   [("gid-1", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(null, "Sunday morning"),
                                   [("gid-2", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);

        Assert.Equal(2, Carry());
        Assert.NotNull(Named(_shelf.GetPlaylistNodes(), "Filed under the queue"));

        // …and it has to STAY settled. Read as a tree walked from the root alone, that row is missing
        // from the listing and present in the mirror, so the two never match and the whole tree is
        // written again on every pull, folding every folder in the panel shut, five minutes apart.
        Assert.Equal(0, Carry());
        Assert.Equal(0, Carry());
    }

    [Fact]
    public void A_whole_tree_of_folders_and_nesting_settles_after_one_pass()
    {
        var driving = _lender.CreatePlaylistFolder(null, "Driving");
        var road = _lender.CreatePlaylist(driving, "Road trips");
        var late = _lender.CreatePlaylist(null, "Late night");
        var every = _lender.CreatePlaylist(null, "Everything");
        _lender.CreatePlaylistFolder(null, "Empty");

        _lender.AddSongsToPlaylist(road, [("gid-1", "Band/Record/01 One.flac"),
                                          ("gid-2", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(late, [(null, "Band/Record/03 Three.flac")], AlreadyInPlaylist.AddAnother);   // no id, path only
        Assert.True(_lender.AddPlaylistRef(every, road));
        Assert.True(_lender.AddPlaylistRef(every, late));

        Assert.Equal(5, Carry());   // two folders and three playlists
        Assert.Equal(0, Carry());
    }

    [Fact]
    public void A_playlist_that_changed_changes_the_version_of_the_listing()
    {
        var road = _lender.CreatePlaylist(null, "Road trips");
        var before = PeerCatalog.VersionOf([], [], PeerCatalogBuilder.Playlists(_lender.PlaylistTree(), new Dictionary<long, string>()), []);

        _lender.RenamePlaylistNode(road, "Long drives");
        var after = PeerCatalog.VersionOf([], [], PeerCatalogBuilder.Playlists(_lender.PlaylistTree(), new Dictionary<long, string>()), []);

        // Otherwise the borrower is told "still abc123" and waits for the owner's next import to hear
        // about a rename, possibly for ever.
        Assert.NotEqual(before, after);
    }

    // ── and narrows when its owner goes home ──

    [Fact]
    public void While_they_are_here_the_whole_tree_is_on_offer()
    {
        Given_two_playlists_one_of_which_needs_their_disk();

        var names = _shelf.GetPlaylistNodes().Select(n => n.Name).ToList();
        Assert.Contains("In the cloud", names);
        Assert.Contains("On their laptop", names);
    }

    [Fact]
    public void Once_they_are_gone_a_playlist_that_needs_their_disk_is_not_offered()
    {
        Given_two_playlists_one_of_which_needs_their_disk();

        _shelf.CloudRowsOnly = true;

        var names = _shelf.GetPlaylistNodes().Select(n => n.Name).ToList();
        Assert.Contains("In the cloud", names);
        Assert.DoesNotContain("On their laptop", names);

        // The queue is this machine's own and has nothing to do with whose shelf is on screen.
        Assert.Contains(_shelf.GetPlaylistNodes(), n => n.IsNowPlaying);
    }

    [Fact]
    public void One_song_on_their_disk_is_enough_to_take_the_whole_list_away()
    {
        var mixed = _lender.CreatePlaylist(null, "Nearly all up there");
        _lender.AddSongsToPlaylist(mixed, [("gid-cloud", "Band/Record/01 One.flac"),
                                           ("gid-disk", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        Carry();
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));

        _shelf.CloudRowsOnly = true;

        // A list that plays nineteen of twenty and then stops is worse than one that is not there.
        Assert.Null(Named(_shelf.GetPlaylistNodes(), "Nearly all up there"));
    }

    [Fact]
    public void A_folder_goes_with_the_last_playlist_under_it_and_stays_for_the_first()
    {
        var away = _lender.CreatePlaylistFolder(null, "Not travelling");
        var stays = _lender.CreatePlaylistFolder(null, "Travelling");
        var deep = _lender.CreatePlaylistFolder(stays, "Deep");

        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(away, "Laptop only"),
                                   [("gid-disk", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(deep, "Up there"),
                                   [("gid-cloud", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);

        Carry();
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));

        _shelf.CloudRowsOnly = true;
        var names = _shelf.GetPlaylistNodes().Select(n => n.Name).ToList();

        Assert.DoesNotContain("Not travelling", names);   // nothing under it survived
        Assert.Contains("Travelling", names);             // something several levels down did
        Assert.Contains("Deep", names);
    }

    [Fact]
    public void A_playlist_is_only_as_reachable_as_the_one_it_points_at()
    {
        var outer = _lender.CreatePlaylist(null, "Everything");
        var inner = _lender.CreatePlaylist(null, "Laptop only");
        _lender.AddSongsToPlaylist(inner, [("gid-disk", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(outer, [("gid-cloud", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);
        Assert.True(_lender.AddPlaylistRef(outer, inner));

        Carry();
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));

        _shelf.CloudRowsOnly = true;
        var names = _shelf.GetPlaylistNodes().Select(n => n.Name).ToList();

        Assert.DoesNotContain("Laptop only", names);
        Assert.DoesNotContain("Everything", names);   // its own songs are up there; what it plays is not
    }

    [Fact]
    public void What_plays_and_what_shows_are_read_off_the_same_row()
    {
        var mixed = _lender.CreatePlaylist(null, "Nearly all up there");
        _lender.AddSongsToPlaylist(mixed, [("gid-cloud", "Band/Record/01 One.flac"),
                                           ("gid-disk", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);
        Carry();
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));

        var here = Named(_shelf.GetPlaylistNodes(), "Nearly all up there")!.Id;
        Assert.Equal(2, _shelf.FlattenPlaylistTracks(here).Count);

        _shelf.CloudRowsOnly = true;

        // The panel is no longer offering this list at all, and if something reaches it anyway, a
        // queue built a minute ago, a row still on screen, it plays what can be played and nothing
        // that cannot.
        Assert.Equal(["gid-cloud"], _shelf.FlattenPlaylistTracks(here).Select(t => t.RpmId));
    }

    [Fact]
    public void The_subtitle_counts_what_the_panel_below_it_is_drawing()
    {
        Given_two_playlists_one_of_which_needs_their_disk();

        Assert.Equal(2, _shelf.QueryPlaylistTotals().Playlists);

        _shelf.CloudRowsOnly = true;
        Assert.Equal(1, _shelf.QueryPlaylistTotals().Playlists);
    }

    [Fact]
    public void An_artists_page_names_only_the_lists_that_are_on_the_shelf()
    {
        Given_two_playlists_one_of_which_needs_their_disk();

        var ids = new HashSet<string>(["gid-cloud", "gid-disk"], StringComparer.Ordinal);
        var paths = new HashSet<string>(
            ["Band/Record/01 One.flac", "Band/Record/02 Two.flac"], StringComparer.OrdinalIgnoreCase);

        Assert.Equal(2, _shelf.PlaylistsHolding(ids, paths).Count);

        // A page pointing at a list the panel two tabs away is not showing is a shelf pointing at
        // nothing.
        _shelf.CloudRowsOnly = true;
        var holding = _shelf.PlaylistsHolding(ids, paths);
        Assert.Equal("In the cloud", Assert.Single(holding).Name);
    }

    [Fact]
    public void A_narrowed_shelf_says_how_many_lists_it_is_holding_back()
    {
        Given_two_playlists_one_of_which_needs_their_disk();

        // Nothing held back while they are here.
        Assert.Equal(0, _shelf.PlaylistsOutOfReach());

        _shelf.CloudRowsOnly = true;

        // One of the two. Without this number the pane just empties, and an empty pane where a
        // friend's twelve lists were reads as the app having lost them.
        Assert.Equal(1, _shelf.PlaylistsOutOfReach());

        _shelf.CloudRowsOnly = false;
        Assert.Equal(0, _shelf.PlaylistsOutOfReach());
    }

    // ── their generated lists ──

    /// <summary>
    /// A generated list arrives as a stored list, and what it IS goes into the same row: it is what
    /// makes the borrowed copy wear the list's own picture. Read back from the mirror, not from the
    /// rows handed to it, because the mirror is what every surface reads.
    /// </summary>
    [Fact]
    public void A_generated_list_keeps_what_it_is_on_the_shelf()
    {
        var chill = new PlaylistCover.Generated(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm));
        _shelf.MirrorPlaylistTree([Generated(-1, null, "Chill", chill, "gid-1")]);

        Assert.Equal(chill, Named(_shelf.GetPlaylistNodes(), "Chill")!.Cover);
    }

    /// <summary>
    /// A shelf mirrored before its lists carried what they are has exactly the same names and the same
    /// songs. If the comparison left the cover out it would match, the tree would never be written
    /// again, and those lists would wear quilts for good.
    /// </summary>
    [Fact]
    public void A_tree_that_only_learned_what_its_lists_are_is_written_again()
    {
        _shelf.MirrorPlaylistTree([Generated(-1, null, "Chill", new PlaylistCover.Automatic(), "gid-1")]);

        var chill = new PlaylistCover.Generated(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm));
        Assert.Equal(1, _shelf.MirrorPlaylistTree([Generated(-1, null, "Chill", chill, "gid-1")]));
        Assert.Equal(0, _shelf.MirrorPlaylistTree([Generated(-1, null, "Chill", chill, "gid-1")]));
    }

    /// <summary>
    /// A flavour hangs under its mood's list. A list is offered only when every song on it can be
    /// reached, so once the owner has gone the mood can be out of reach while a flavour is not. The
    /// flavour is then filed where the mood was rather than left under a row that is not drawn, which
    /// the pane would read as a list with nowhere to go and not show at all.
    /// </summary>
    [Fact]
    public void A_flavour_still_in_reach_moves_up_when_its_mood_is_not()
    {
        var moods = new PlaylistTreeRow(-1, null, PlaylistNodeKind.Folder, "Moods", 0, [], new PlaylistCover.Automatic());
        _shelf.MirrorPlaylistTree(
        [
            moods,
            Generated(-2, -1, "Chill",
                new PlaylistCover.Generated(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)), "gid-cloud", "gid-disk"),
            Generated(-3, -2, "Slow Burn",
                new PlaylistCover.Generated(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock")), "gid-cloud"),
        ]);
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));

        var here = _shelf.GetPlaylistNodes();
        Assert.Equal(Named(here, "Chill")!.Id, Named(here, "Slow Burn")!.ParentId);

        _shelf.CloudRowsOnly = true;
        var away = _shelf.GetPlaylistNodes();

        Assert.Null(Named(away, "Chill"));
        Assert.Equal(Named(away, "Moods")!.Id, Named(away, "Slow Burn")!.ParentId);
    }

    /// <summary>A tree of folders is untouched by the moving up: a folder is kept whenever anything
    /// under it is, so nothing in it ever points at a row that is not drawn.</summary>
    [Fact]
    public void Nothing_in_a_tree_of_folders_is_moved()
    {
        var shown = new HashSet<long> { 1, 2, 3 };
        PlaylistNode Node(long id, long? parent, PlaylistNodeKind kind) =>
            new(id, parent, kind, $"n{id}", 0, false, new PlaylistCover.Automatic());
        IReadOnlyList<PlaylistNode> nodes =
            [Node(1, null, PlaylistNodeKind.Folder), Node(2, 1, PlaylistNodeKind.Folder), Node(3, 2, PlaylistNodeKind.Playlist)];

        Assert.Equal(nodes, LibraryDatabase.UnderWhatIsShown(nodes, shown));
    }

    private static PlaylistTreeRow Generated(
        long id, long? parent, string name, PlaylistCover cover, params string[] rpmIds) =>
        new(id, parent, PlaylistNodeKind.Playlist, name, 0,
            [.. rpmIds.Select(r => new PlaylistTreeEntry(PlaylistItemKind.Song, r, null, null))], cover);

    // ── what crosses with each record ──

    /// <summary>
    /// The explicit flag crosses with the record, so a borrowed record, song or list wears the same E
    /// it wears on the owner's shelf. The cloud listing has carried it all along; this one did not, and
    /// every borrowed row read as having no flag at all.
    /// </summary>
    [Fact]
    public void The_explicit_flag_crosses_with_the_record()
    {
        var listing = PeerCatalogBuilder.From(
        [
            new LibraryTrack
            {
                RelativePath = "Band/Record/01 One.flac", Folder = "Band/Record", FileName = "01 One.flac",
                Extension = ".flac", RpmId = "gid-explicit", Advisory = Models.ContentAdvisory.Explicit,
            },
        ]);

        Assert.Equal((int)Models.ContentAdvisory.Explicit, Assert.Single(listing).Advisory);

        Lend(listing[0]);
        Assert.Equal(Models.ContentAdvisory.Explicit, _shelf.FindByRpmId("gid-explicit")!.Advisory);
    }

    /// <summary>Their tree with one list they put in the cloud and one that needs their laptop, taken
    /// the whole way across.</summary>
    private void Given_two_playlists_one_of_which_needs_their_disk()
    {
        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(null, "In the cloud"),
                                   [("gid-cloud", "Band/Record/01 One.flac")], AlreadyInPlaylist.AddAnother);
        _lender.AddSongsToPlaylist(_lender.CreatePlaylist(null, "On their laptop"),
                                   [("gid-disk", "Band/Record/02 Two.flac")], AlreadyInPlaylist.AddAnother);

        Carry();
        Lend(Track("gid-cloud", "Band/Record/01 One.flac", inCloud: true),
             Track("gid-disk", "Band/Record/02 Two.flac", inCloud: false));
    }
}

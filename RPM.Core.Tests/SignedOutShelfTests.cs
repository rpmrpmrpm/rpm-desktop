using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What the library shows once there is no account behind it.
///
/// <para>A record whose file the user deleted lives only in the store, and the store answers nobody
/// who is not signed in. If it stayed on the wall it would be a sleeve that plays nothing, because
/// the rows are ordinary rows and an ordinary read would return them.</para>
///
/// <para>The rule is <see cref="LibraryDatabase.LocalRowsOnly"/>, the mirror image of
/// <see cref="LibraryDatabase.CloudRowsOnly"/>. These tests are mostly about the fact that
/// <b>nothing is deleted</b>: the row is the only thing that knows the music went up there, and it
/// comes back whole the moment the account does.</para>
/// </summary>
public class SignedOutShelfTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public SignedOutShelfTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpm-signedout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private LibraryTrack Add(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            Artist = "Fleetwood Mac",
            AlbumArtist = "Fleetwood Mac",
            Album = "Rumours",
            Title = Path.GetFileNameWithoutExtension(relativePath),
            SizeBytes = 5_000_000,
            DurationSeconds = 200,
            ModifiedUtcTicks = 1,
        });
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    /// <summary>Puts a track in the cloud and removes the local file, which is what "the user deleted
    /// it to make room" leaves behind.</summary>
    private LibraryTrack Offloaded(string relativePath)
    {
        var track = Add(relativePath);
        _db.SeedSynced(track.Id, "hash-" + track.Id, 1, 1);
        _db.MarkOffloaded(track.Id, "rpm-" + track.Id);
        return track;
    }

    // ── the wall, the list, the search ──

    [Fact]
    public void A_record_that_is_only_in_the_cloud_leaves_the_wall()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Add("Fleetwood Mac/Rumours/2.mp3");

        _db.LocalRowsOnly = true;

        Assert.Single(_db.Query());
        Assert.Equal(1, _db.QueryFolderTrackCounts().Single().TrackCount);
        Assert.Equal(200, _db.QueryTotalDurationSeconds());
    }

    /// <summary>A whole record that is only in the cloud is not a thinner record: it is one that is
    /// not on the shelf at all.</summary>
    [Fact]
    public void A_record_entirely_in_the_cloud_is_not_on_the_shelf()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Offloaded("Fleetwood Mac/Rumours/2.mp3");

        _db.LocalRowsOnly = true;

        Assert.Empty(_db.Query());
        Assert.Empty(_db.QueryFolderTrackCounts());
        Assert.Empty(_db.QueryAlbumFacts());
        Assert.Empty(_db.QueryArtistCredits());
    }

    /// <summary>The central rule. The row is the only thing that knows where the music went, so it is
    /// hidden and never dropped, and signing back in is enough to have it all again.</summary>
    [Fact]
    public void Nothing_is_deleted_and_it_all_comes_back_with_the_account()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Add("Fleetwood Mac/Rumours/2.mp3");

        _db.LocalRowsOnly = true;
        Assert.Single(_db.Query());

        // Still on the disk while it is hidden: the sync's own read is not narrowed. Otherwise a
        // signed-out pass would see the row as gone and delete the cloud copy with it.
        Assert.Equal(2, _db.Snapshot().Count);

        _db.LocalRowsOnly = false;
        Assert.Equal(2, _db.Query().Count);
    }

    [Fact]
    public void The_track_table_hides_it_too()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Add("Fleetwood Mac/Rumours/2.mp3");

        var query = new TrackTableQuery();

        Assert.Equal(2, _db.QueryTrackIds(query).Count);

        _db.LocalRowsOnly = true;
        Assert.Single(_db.QueryTrackIds(query));
    }

    /// <summary>Both narrowings at once: a borrowed shelf whose owner has gone home, read by somebody
    /// who is not signed in. The answer is nothing, which is the truth and not a case nobody
    /// wrote.</summary>
    [Fact]
    public void Both_narrowings_at_once_answer_nothing()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Add("Fleetwood Mac/Rumours/2.mp3");

        _db.CloudRowsOnly = true;
        _db.LocalRowsOnly = true;

        Assert.Empty(_db.Query());
    }

    // ── the lists the library writes itself ──

    /// <summary>A generated list is offered to be played, so it must not be half unplayable. The same
    /// reasoning applies to a borrowed shelf's favourites.</summary>
    [Fact]
    public void A_generated_list_does_not_offer_what_cannot_play()
    {
        Offloaded("Fleetwood Mac/Rumours/1.mp3");
        Add("Fleetwood Mac/Rumours/2.mp3");

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.RecentlyAdded);
        Assert.Equal(2, _db.QuerySmartPlaylist(id, nowUtcTicks: 0, advance: true).Count);

        _db.LocalRowsOnly = true;
        Assert.Single(_db.QuerySmartPlaylist(id, nowUtcTicks: 0, advance: true));
    }

    /// <summary>
    /// A playlist the listener made keeps its place in the tree and loses only the songs that cannot
    /// play. This is deliberately NOT the borrowed-shelf rule, which hides the whole list. It is their
    /// own list on their own shelf, and one that plays nine of its ten songs is still theirs.
    /// </summary>
    [Fact]
    public void A_listeners_own_playlist_keeps_its_place_and_drops_the_song_that_cannot_play()
    {
        var gone = Offloaded("Fleetwood Mac/Rumours/1.mp3");
        var here = Add("Fleetwood Mac/Rumours/2.mp3");

        var list = _db.CreatePlaylist(null, "Driving");
        _db.AddSongsToPlaylist(list, [("rpm-" + gone.Id, gone.RelativePath), (null, here.RelativePath)], AlreadyInPlaylist.AddAnother);

        Assert.Equal(2, _db.FlattenPlaylistTracks(list).Count);

        _db.LocalRowsOnly = true;

        Assert.Contains(_db.GetPlaylistNodes(), n => n.Id == list);
        var played = _db.FlattenPlaylistTracks(list);
        Assert.Single(played);
        Assert.Equal(here.RelativePath, played[0].RelativePath);
    }

    // ── the rule, read twice ──

    /// <summary>
    /// The narrowing is written twice: once as SQL for the statements that filter, and once in C# for
    /// the reads that walk rows one at a time. Two spellings of one rule can drift apart. A playlist
    /// holding the whole shelf plays exactly what the shelf shows, under every combination, or one of
    /// the two has changed.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_sql_and_the_row_by_row_rule_agree(bool cloudOnly, bool localOnly)
    {
        var offloaded = Offloaded("Fleetwood Mac/Rumours/1.mp3");
        var local = Add("Fleetwood Mac/Rumours/2.mp3");

        var list = _db.CreatePlaylist(null, "Everything");
        _db.AddSongsToPlaylist(list, [
            ("rpm-" + offloaded.Id, offloaded.RelativePath),
            (null, local.RelativePath),
        ], AlreadyInPlaylist.AddAnother);

        _db.CloudRowsOnly = cloudOnly;
        _db.LocalRowsOnly = localOnly;

        Assert.Equal(
            _db.Query().Select(t => t.RelativePath).OrderBy(p => p, StringComparer.Ordinal),
            _db.FlattenPlaylistTracks(list).Select(t => t.RelativePath).OrderBy(p => p, StringComparer.Ordinal));
    }
}

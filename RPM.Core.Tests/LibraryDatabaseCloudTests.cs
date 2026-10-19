using RPM.Core.Cloud;
using RPM.Core.Library;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Tests;

/// <summary>
/// The cloud columns are owned by the cloud code, not by the file. Sync and import rebuild a row from
/// what they read from disk, and they must not be able to change where its bytes are stored. A mirror
/// that lost the fact that a track was uploaded would upload it again, and one that lost the fact that
/// a track was offloaded would delete the row for a file it had removed on purpose.
/// </summary>
public class LibraryDatabaseCloudTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseCloudTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdcloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack Track(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
        };
    }

    private LibraryTrack Add(string relativePath)
    {
        _db.Upsert(Track(relativePath));
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    /// <summary>A track with album tags, which the art key is built from. The file name must be
    /// unique in the test's library: <see cref="Add"/> finds its row by searching for it.</summary>
    private LibraryTrack AddOnAlbum(string relativePath, string? album, string? albumArtist = null, string? artist = null)
    {
        _db.Upsert(Track(relativePath) with { Album = album, AlbumArtist = albumArtist, Artist = artist });
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    [Fact]
    public void A_new_track_is_local_and_unselected()
    {
        var track = Add("a.mp3");
        Assert.Equal(CloudState.Local, track.CloudState);
        Assert.False(track.CloudSelected);
        Assert.Null(track.ContentHash);
        Assert.False(track.IsOffloaded);
    }

    [Fact]
    public void Marking_synced_records_where_the_bytes_went()
    {
        var track = Add("a.mp3");
        _db.SeedSynced(track.Id, "abc123", 4_800_000, 42);

        var row = _db.Query()[0];
        Assert.Equal(CloudState.Synced, row.CloudState);
        Assert.Equal("abc123", row.ContentHash);
        Assert.Equal(4_800_000, row.CanonicalSizeBytes);
        Assert.Equal(42, row.CloudSyncedUtcTicks);
        Assert.Null(row.CloudError);
    }

    [Fact]
    public void An_upsert_cannot_reset_cloud_state()
    {
        var track = Add("a.mp3");
        _db.SeedSynced(track.Id, "abc123", 4_800_000, 42);

        // What sync does after the user retags the file: rebuild the row from the file and upsert
        // it. FromTrackInfo knows nothing about the cloud, so its defaults are Local/null, and they
        // must not overwrite the stored cloud state.
        _db.Upsert(Track("a.mp3") with { SizeBytes = 6_000_000, ModifiedUtcTicks = 99 });

        var row = _db.Query()[0];
        Assert.Equal(6_000_000, row.SizeBytes);           // the file facts did update
        Assert.Equal(99, row.ModifiedUtcTicks);
        Assert.Equal(CloudState.Synced, row.CloudState);  // the cloud facts did not
        Assert.Equal("abc123", row.ContentHash);
    }

    [Fact]
    public void A_move_keeps_cloud_state()
    {
        var track = Add("old/a.mp3");
        _db.SeedSynced(track.Id, "abc123", 1, 1);

        _db.Move("old/a.mp3", Track("new/a.mp3"));

        var row = _db.Query()[0];
        Assert.Equal("new/a.mp3", row.RelativePath);
        Assert.Equal(track.Id, row.Id);
        Assert.Equal(CloudState.Synced, row.CloudState);
        Assert.Equal("abc123", row.ContentHash);
    }

    [Fact]
    public void Offloading_only_applies_to_a_track_that_is_really_up_there()
    {
        var track = Add("a.mp3");

        // Never uploaded. Offloading it would delete the user's only copy.
        _db.MarkOffloaded(track.Id);
        Assert.Equal(CloudState.Local, _db.Query()[0].CloudState);

        _db.SeedSynced(track.Id, "abc123", 1, 1);
        _db.MarkOffloaded(track.Id);
        Assert.Equal(CloudState.CloudOnly, _db.Query()[0].CloudState);
        Assert.True(_db.Query()[0].IsOffloaded);
    }

    [Fact]
    public void An_error_carries_its_reason_and_a_retry_clears_it()
    {
        var track = Add("a.mp3");
        _db.SetCloudState(track.Id, CloudState.Error, "quota exceeded");

        Assert.Equal(CloudState.Error, _db.Query()[0].CloudState);
        Assert.Equal("quota exceeded", _db.Query()[0].CloudError);

        _db.SeedSynced(track.Id, "abc123", 1, 1);
        Assert.Null(_db.Query()[0].CloudError);
    }

    [Fact]
    public void Selection_is_what_the_selected_music_mode_acts_on()
    {
        var a = Add("a.mp3");
        var b = Add("b.mp3");
        var c = Add("c.mp3");

        _db.SetCloudSelected([a.Id, c.Id], true);

        Assert.True(_db.Query("a")[0].CloudSelected);
        Assert.False(_db.Query("b")[0].CloudSelected);
        Assert.True(_db.Query("c")[0].CloudSelected);

        _db.SetCloudSelected([a.Id], false);
        Assert.False(_db.Query("a")[0].CloudSelected);
        Assert.True(_db.Query("c")[0].CloudSelected);
        _ = b;
    }

    [Fact]
    public void The_worker_asks_for_its_work_list_by_state()
    {
        var a = Add("a.mp3");
        var b = Add("b.mp3");
        Add("c.mp3");

        _db.SetCloudState(a.Id, CloudState.Queued);
        _db.SetCloudState(b.Id, CloudState.Queued);

        var queued = _db.QueryByCloudState(CloudState.Queued);
        Assert.Equal(2, queued.Count);
        Assert.Equal(["a.mp3", "b.mp3"], queued.Select(t => t.RelativePath));
        Assert.Empty(_db.QueryByCloudState(CloudState.Uploading));
    }

    [Fact]
    public void Used_bytes_counts_what_the_cloud_actually_holds()
    {
        var a = Add("a.mp3");
        var b = Add("b.mp3");
        var c = Add("c.mp3");

        Assert.Equal(0, _db.CloudBytesUsed());

        // The canonical blob is what is stored. The original file is bigger because of its tags and
        // cover art, and none of that goes into the shared object.
        _db.SeedSynced(a.Id, "h1", 4_800_000, 1);
        _db.SeedSynced(b.Id, "h2", 4_900_000, 1);
        _db.MarkOffloaded(b.Id);                     // offloaded still counts: it is still stored
        _db.SetCloudState(c.Id, CloudState.Queued);  // queued does not: it is not uploaded yet

        Assert.Equal(9_700_000, _db.CloudBytesUsed());
    }

    /// <summary>
    /// The mirror is stored in the user's music folder and is kept across releases. A v2 database
    /// (with no cloud columns) must open in place, keep its rows, and read as Local.
    /// </summary>
    [Fact]
    public void A_pre_cloud_mirror_upgrades_in_place()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdv2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, LibraryDatabase.DataFolderName));
        var dbPath = MirrorLocation.InFolder(root).DatabasePath;

        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString()))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    folder TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL COLLATE NOCASE,
                    extension TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    title TEXT COLLATE NOCASE,
                    artist TEXT COLLATE NOCASE,
                    album_artist TEXT COLLATE NOCASE,
                    album TEXT COLLATE NOCASE,
                    genre TEXT COLLATE NOCASE,
                    track_number INTEGER NOT NULL DEFAULT 0,
                    disc_number INTEGER NOT NULL DEFAULT 0,
                    year INTEGER NOT NULL DEFAULT 0,
                    grooves_id TEXT
                );
                INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                    modified_utc_ticks, grooves_id)
                VALUES ('old/song.mp3', 'old', 'song.mp3', '.mp3', 123, 456, 'gid-existing');
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));
            var row = Assert.Single(db.Query());

            Assert.Equal("old/song.mp3", row.RelativePath);
            Assert.Equal("gid-existing", row.RpmId);   // the row is kept, with its identity
            Assert.Equal(CloudState.Local, row.CloudState); // an upgrade does not upload anything
            Assert.Null(row.ContentHash);
            Assert.False(row.CloudSelected);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── the badge on an album tile ──

    /// <summary>
    /// Work in progress has priority over the finished states. For an album that is half uploaded,
    /// "uploading" is more useful than "partly uploaded", and it is the only state the user waits
    /// for. So the pulsing arrow is shown instead of the outline cloud or the filled cloud.
    /// </summary>
    [Fact]
    public void An_album_with_tracks_on_their_way_reads_as_working()
    {
        var up = Add("Artist/Album/up.mp3");
        var going = Add("Artist/Album/going.mp3");
        _db.SeedSynced(up.Id, "h1", 1, 1);
        _db.SetCloudSelected([going.Id], selected: true);
        _db.SetCloudState(going.Id, CloudState.Queued);

        var rollup = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(1, rollup.InFlight);
        Assert.Equal(AlbumCloudBadge.Working, rollup.Badge);

        // Uploading counts the same as queued: both are in progress.
        _db.SetCloudState(going.Id, CloudState.Uploading);
        Assert.Equal(AlbumCloudBadge.Working, _db.GetAlbumCloudRollup("Artist/Album").Badge);
    }

    /// <summary>When the last track is uploaded, the album shows the filled cloud.</summary>
    [Fact]
    public void An_album_that_has_finished_stops_working()
    {
        var a = Add("Artist/Album/a.mp3");
        var b = Add("Artist/Album/b.mp3");
        _db.SetCloudState(a.Id, CloudState.Queued);
        _db.SetCloudState(b.Id, CloudState.Queued);
        Assert.Equal(AlbumCloudBadge.Working, _db.GetAlbumCloudRollup("Artist/Album").Badge);

        _db.SeedSynced(a.Id, "h1", 1, 1);
        Assert.Equal(AlbumCloudBadge.Working, _db.GetAlbumCloudRollup("Artist/Album").Badge);

        _db.SeedSynced(b.Id, "h2", 1, 1);
        Assert.Equal(AlbumCloudBadge.Full, _db.GetAlbumCloudRollup("Artist/Album").Badge);
    }

    // ── the same rollup for a whole shelf, in one pass ──

    /// <summary>
    /// The batched read and the single read must give the same result, and only this test checks
    /// that. They are two separate statements over the same columns. A badge that is right when an
    /// album is opened and wrong when the shelf is read again is worse than one that is always wrong,
    /// because nobody would look for the bug.
    /// </summary>
    [Fact]
    public void The_batched_rollup_agrees_with_the_one_record_reading()
    {
        var up = Add("A/One/up.mp3");
        var going = Add("A/One/going.mp3");
        _db.SeedSynced(up.Id, "h1", 1, 1);
        _db.SetCloudSelected([going.Id], selected: true);
        _db.SetCloudState(going.Id, CloudState.Queued);

        Add("B/Two/a.mp3");
        var only = Add("C/Three/a.mp3");
        _db.SeedSynced(only.Id, "h2", 1, 1);

        string[] folders = ["A/One", "B/Two", "C/Three"];
        var batched = _db.GetAlbumCloudRollups(folders);

        foreach (var folder in folders)
            Assert.Equal(_db.GetAlbumCloudRollup(folder), batched[folder]);
    }

    /// <summary>A folder with no tracks is missing from the result rather than present and empty. A
    /// caller that asks about a removed album must handle that case, instead of getting a zero that
    /// looks like a real value.</summary>
    [Fact]
    public void A_folder_that_is_not_there_comes_back_missing()
    {
        Add("A/One/a.mp3");

        var batched = _db.GetAlbumCloudRollups(["A/One", "Nowhere/At/All"]);

        Assert.True(batched.ContainsKey("A/One"));
        Assert.False(batched.ContainsKey("Nowhere/At/All"));
    }

    /// <summary>The folder column is NOCASE, so the shelf and the mirror can use different capitals
    /// for the same album. An ordinal dictionary here would lose the badge without any
    /// error.</summary>
    [Fact]
    public void Capitals_do_not_lose_a_record_its_mark()
    {
        Add("Artist/Album/a.mp3");

        var batched = _db.GetAlbumCloudRollups(["ARTIST/ALBUM"]);

        Assert.Equal(_db.GetAlbumCloudRollup("Artist/Album"), Assert.Single(batched).Value);
    }

    /// <summary>An empty request reads nothing: an empty shelf must not run a query, and must never
    /// run one with no WHERE clause.</summary>
    [Fact]
    public void Asking_about_no_records_reads_nothing()
    {
        Add("Artist/Album/a.mp3");
        Assert.Empty(_db.GetAlbumCloudRollups([]));
    }

    /// <summary>Above SQLite's parameter limit the request is split, and the split must not lose or
    /// repeat an album. Nine hundred per batch, so this needs two batches.</summary>
    [Fact]
    public void A_shelf_past_the_parameter_ceiling_is_read_in_one_go()
    {
        var folders = new List<string>();
        for (var i = 0; i < 1000; i++)
        {
            var folder = $"Artist/Album{i:0000}";
            Add($"{folder}/a.mp3");
            folders.Add(folder);
        }

        var batched = _db.GetAlbumCloudRollups(folders);

        Assert.Equal(1000, batched.Count);
        Assert.All(folders, f => Assert.Equal(1, batched[f].Total));
    }

    /// <summary>A failed track is not in flight: it is waiting for a backoff. A badge that pulsed for
    /// ever would say the upload was still running when it had stopped.</summary>
    [Fact]
    public void A_failed_track_is_not_in_flight()
    {
        var track = Add("Artist/Album/a.mp3");
        _db.SetCloudSelected([track.Id], selected: true);
        _db.MarkCloudAttemptFailed(track.Id, "no route to host", 5_000);

        var rollup = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(0, rollup.InFlight);
        Assert.Equal(AlbumCloudBadge.Partial, rollup.Badge);
    }

    /// <summary>
    /// A track that was being uploaded when the app was killed is put back in the queue.
    ///
    /// <para>The worker sets Uploading and the worker clears it, so if the app is killed in between
    /// the row is left as Uploading. The queue reads Queued and Error, and QueueForUpload only
    /// promotes Local. Without the reclaim, that track never reaches the cloud and its album shows
    /// "uploading" for ever.</para>
    /// </summary>
    [Fact]
    public void An_interrupted_upload_is_put_back_in_the_queue()
    {
        var track = Add("Artist/Album/a.mp3");
        _db.SetCloudSelected([track.Id], selected: true);
        _db.SetCloudState(track.Id, CloudState.Uploading);

        // Without the reclaim, the queue does nothing with it.
        Assert.Equal(0, _db.QueueForUpload(everything: true));
        Assert.Empty(_db.QueryDueForUpload(long.MaxValue));

        Assert.Equal(1, _db.ReclaimInterruptedUploads());

        Assert.Equal(CloudState.Queued, _db.Query("a")[0].CloudState);
        Assert.Single(_db.QueryDueForUpload(long.MaxValue));
    }

    [Fact]
    public void Reclaiming_leaves_every_other_state_where_it_was()
    {
        var local = Add("Artist/Album/a.mp3");
        var synced = Add("Artist/Album/b.mp3");
        var offloaded = Add("Artist/Album/c.mp3");

        _db.SeedSynced(synced.Id, "h1", 1, 1);
        _db.SeedSynced(offloaded.Id, "h2", 1, 1);
        _db.MarkOffloaded(offloaded.Id);

        // Nothing was interrupted, so nothing changes, including a track whose local file was
        // removed.
        Assert.Equal(0, _db.ReclaimInterruptedUploads());
        Assert.Equal(CloudState.Local, _db.Query("a")[0].CloudState);
        Assert.Equal(CloudState.Synced, _db.Query("b")[0].CloudState);
        Assert.Equal(CloudState.CloudOnly, _db.Query("c")[0].CloudState);
    }

    // ── which albums the cloud holds, for repairing missing covers ──

    /// <summary>
    /// One row per album, with a file to read the cover from, so a repair pass can ask the server
    /// which covers it already has and send only the others.
    /// </summary>
    [Fact]
    public void Cloud_albums_are_listed_once_each_with_a_file_to_read_the_cover_from()
    {
        var a1 = AddOnAlbum("Manu Chao/Clandestino/one.mp3", albumArtist: "Manu Chao", album: "Clandestino");
        var a2 = AddOnAlbum("Manu Chao/Clandestino/two.mp3", albumArtist: "Manu Chao", album: "Clandestino");
        var b = AddOnAlbum("Other/Record/three.mp3", artist: "Someone", album: "Record");

        _db.SeedSynced(a1.Id, "h1", 1, 1);
        _db.SeedSynced(a2.Id, "h2", 1, 1);
        _db.SeedSynced(b.Id, "h3", 1, 1);

        var albums = _db.CloudAlbumsWithLocalFiles();

        Assert.Equal(2, albums.Count);
        var clandestino = albums.Single(x => x.Album == "Clandestino");
        Assert.Equal("Manu Chao", clandestino.AlbumArtist);
        Assert.Equal("Manu Chao/Clandestino/one.mp3", clandestino.RelativePath);   // lowest path, so it is stable

        // Both artist fields are returned as stored. Which one names the album is decided only by the
        // art key's rule. If the rule were also here, a repair could key an album differently from
        // the upload that first stored it.
        var record = albums.Single(x => x.Album == "Record");
        Assert.Null(record.AlbumArtist);
        Assert.Equal("Someone", record.Artist);
    }

    /// <summary>
    /// One folder is one album, even when its tracks do not all have the same album-artist spelling.
    /// The art repair keys an album by those tags. If a folder were returned once per spelling, there
    /// would be two keys for one cover.jpg, each writing its own store time to that file and each
    /// making the other look out of date, so the cover would be uploaded again on every run.
    /// </summary>
    [Fact]
    public void A_folder_is_one_album_whatever_its_odd_track_is_tagged()
    {
        var most = new[] { "one", "two", "three" }
            .Select(n => AddOnAlbum($"LP/Churches/{n}.mp3", albumArtist: "LP", artist: "L.P.", album: "Churches"))
            .ToList();
        var odd = AddOnAlbum("LP/Churches/four.mp3", albumArtist: "LP*", artist: "L.P.", album: "Churches");

        foreach (var track in most.Append(odd))
            _db.SeedSynced(track.Id, $"h{track.Id}", 1, 1);

        var album = Assert.Single(_db.CloudAlbumsWithLocalFiles());
        Assert.Equal("LP", album.AlbumArtist);          // the spelling most of the tracks have
        Assert.Equal("Churches", album.Album);
        Assert.Equal("LP/Churches/one.mp3", album.RelativePath);
    }

    /// <summary>
    /// A track in one album's folder but tagged with another album does not change the folder's album.
    /// This is a deduplicated track (one file on two albums), and the folder belongs to the album that
    /// most of its tracks are on.
    /// </summary>
    [Fact]
    public void A_stray_track_does_not_take_the_folder_it_sits_in()
    {
        var own = new[] { "one", "two" }
            .Select(n => AddOnAlbum($"Prince/Parade/{n}.mp3", albumArtist: "Prince", album: "Parade"))
            .ToList();
        var shared = AddOnAlbum(
            "Prince/Parade/kiss.mp3", albumArtist: "Various Artists", artist: "Prince", album: "Pose");

        foreach (var track in own.Append(shared))
            _db.SeedSynced(track.Id, $"h{track.Id}", 1, 1);

        var album = Assert.Single(_db.CloudAlbumsWithLocalFiles());
        Assert.Equal("Parade", album.Album);
        Assert.Equal("Prince", album.AlbumArtist);
    }

    /// <summary>
    /// A blank album-artist tag is not an album artist. <c>??</c> would key the album under the empty
    /// string even though the track artist is available. That gives two keys for one album, and the
    /// cover is stored where nothing looks for it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_album_artist_falls_back_to_the_track_artist(string? albumArtist)
        => Assert.Equal(
            RPM.Core.Cloud.AlbumArt.KeyFor("Someone", "Record"),
            RPM.Core.Cloud.AlbumArt.KeyForTrack(albumArtist, "Someone", "Record"));

    [Fact]
    public void An_album_artist_wins_over_the_track_artist()
        => Assert.Equal(
            RPM.Core.Cloud.AlbumArt.KeyFor("Various Artists", "Now 22"),
            RPM.Core.Cloud.AlbumArt.KeyForTrack("Various Artists", "Snap!", "Now 22"));

    /// <summary>
    /// An offloaded track has no bytes on this machine, so there is nothing to read a cover from.
    /// Listing it would only cause a repair attempt that cannot succeed.
    /// </summary>
    [Fact]
    public void An_offloaded_album_has_no_file_to_read_a_cover_from()
    {
        var track = AddOnAlbum("A/gone.mp3", albumArtist: "A", album: "Gone");
        _db.SeedSynced(track.Id, "h1", 1, 1);
        _db.MarkOffloaded(track.Id);

        Assert.Empty(_db.CloudAlbumsWithLocalFiles());
    }

    /// <summary>
    /// The offloaded albums are listed separately, for the art pass. They have no file to read a
    /// cover from, but a picture chosen for one is still written into its album folder. This machine
    /// shows the album with that sidecar file, so it must be in the cloud too.
    /// </summary>
    [Fact]
    public void Offloaded_albums_are_listed_once_each_with_the_path_their_folder_is_at()
    {
        var a1 = AddOnAlbum("A/Gone/one.mp3", albumArtist: "A", album: "Gone");
        var a2 = AddOnAlbum("A/Gone/two.mp3", albumArtist: "A", album: "Gone");
        var here = AddOnAlbum("B/Here/three.mp3", albumArtist: "B", album: "Here");

        _db.SeedSynced(a1.Id, "h1", 1, 1);
        _db.SeedSynced(a2.Id, "h2", 1, 1);
        _db.SeedSynced(here.Id, "h3", 1, 1);
        _db.MarkOffloaded(a1.Id);
        _db.MarkOffloaded(a2.Id);

        var albums = _db.OffloadedAlbums();

        var gone = Assert.Single(albums);
        Assert.Equal("Gone", gone.Album);
        Assert.Equal("A", gone.AlbumArtist);
        Assert.Equal("A/Gone/one.mp3", gone.RelativePath);   // lowest path, so it is stable

        // The two lists do not overlap: an album whose files are still here belongs to the other
        // list.
        Assert.DoesNotContain(_db.CloudAlbumsWithLocalFiles(), x => x.Album == "Gone");
    }

    /// <summary>Only albums that are really in the cloud. A queued track's album cannot be missing a
    /// cover yet.</summary>
    [Fact]
    public void A_track_that_is_not_in_the_cloud_yet_is_not_listed()
    {
        var track = AddOnAlbum("A/queued.mp3", albumArtist: "A", album: "Queued");
        _db.SetCloudState(track.Id, CloudState.Queued);

        Assert.Empty(_db.CloudAlbumsWithLocalFiles());
    }

    // ── switching sync mode ──

    /// <summary>
    /// The queue belongs to the mode that built it. Turning syncing off must empty it. Otherwise the
    /// remaining rows are uploaded the next time anything starts the worker.
    /// </summary>
    [Fact]
    public void Turning_sync_off_empties_the_queue()
    {
        var queued = Add("queued.mp3");
        var failed = Add("failed.mp3");
        var flight = Add("flight.mp3");

        _db.SetCloudState(queued.Id, CloudState.Queued);
        _db.MarkCloudAttemptFailed(failed.Id, "no route to host", 5_000);
        _db.SetCloudState(flight.Id, CloudState.Uploading);

        Assert.Equal(3, _db.Dequeue(onlyUnselected: false));

        foreach (var row in _db.Query())
        {
            Assert.Equal(CloudState.Local, row.CloudState);
            Assert.Null(row.CloudError);
            Assert.Equal(0, row.CloudAttempts);
        }
    }

    /// <summary>
    /// Emptying the queue does not delete anything: a track whose bytes are in the cloud stays there,
    /// because changing a setting never means "delete my cloud copies".
    /// </summary>
    [Fact]
    public void Emptying_the_queue_leaves_what_is_already_in_the_cloud_alone()
    {
        var up = Add("up.mp3");
        var off = Add("off.mp3");
        _db.SeedSynced(up.Id, "h1", 1, 1);
        _db.SeedSynced(off.Id, "h2", 1, 1);
        _db.MarkOffloaded(off.Id);

        Assert.Equal(0, _db.Dequeue(onlyUnselected: false));

        Assert.Equal(CloudState.Synced, _db.Query("up")[0].CloudState);
        Assert.Equal(CloudState.CloudOnly, _db.Query("off")[0].CloudState);
    }

    /// <summary>
    /// From "All music" to "selected music": what the user picked by hand stays queued, and the rest
    /// of the library that "All music" had added is removed from the queue.
    /// </summary>
    [Fact]
    public void Going_back_to_selected_music_only_keeps_the_picked_tracks_queued()
    {
        var picked = Add("picked.mp3");
        var swept = Add("swept.mp3");
        _db.SetCloudSelected([picked.Id], true);
        _db.SetCloudState(picked.Id, CloudState.Queued);
        _db.SetCloudState(swept.Id, CloudState.Queued);

        Assert.Equal(1, _db.Dequeue(onlyUnselected: true));

        Assert.Equal(CloudState.Queued, _db.Query("picked")[0].CloudState);
        Assert.Equal(CloudState.Local, _db.Query("swept")[0].CloudState);
    }

    /// <summary>
    /// "All music" does not ask the user to pick anything, so after switching to "selected music" the
    /// files are in the cloud with nothing selected. Selecting them makes the album wall's cloud
    /// badges and its "Remove from cloud" match what is actually stored.
    /// </summary>
    [Fact]
    public void Coming_back_from_all_music_adopts_what_is_already_uploaded()
    {
        var up = Add("up.mp3");
        var off = Add("off.mp3");
        var local = Add("local.mp3");
        _db.SeedSynced(up.Id, "h1", 1, 1);
        _db.SeedSynced(off.Id, "h2", 1, 1);
        _db.MarkOffloaded(off.Id);

        Assert.Equal(2, _db.SelectWhatIsInCloud());

        Assert.True(_db.Query("up")[0].CloudSelected);
        Assert.True(_db.Query("off")[0].CloudSelected);
        Assert.False(_db.Query("local")[0].CloudSelected);   // never uploaded, so not selected

        Assert.Equal(0, _db.SelectWhatIsInCloud());          // and a second call changes nothing
        _ = local;
    }

    // ── removing from the cloud ──

    /// <summary>
    /// Removal must clear the failure count too. Otherwise a track removed after five failed uploads
    /// and then added again would already be past the retry limit and would never be uploaded.
    /// </summary>
    [Fact]
    public void Forgetting_the_cloud_clears_the_selection_the_error_and_the_backoff()
    {
        var track = Add("a.mp3");
        _db.SetCloudSelected([track.Id], true);
        _db.MarkCloudAttemptFailed(track.Id, "server said no", 9_000);

        _db.ForgetCloud(track.Id);

        var row = _db.Query()[0];
        Assert.Equal(CloudState.Local, row.CloudState);
        Assert.False(row.CloudSelected);
        Assert.Null(row.CloudError);
        Assert.Equal(0, row.CloudAttempts);

        // Queued again on the next run, not blocked by an old backoff.
        Assert.Equal(0, _db.QueueForUpload(everything: false));   // unpicked, so not this mode's work
        Assert.Equal(1, _db.QueueForUpload(everything: true));
    }

    /// <summary>
    /// In a mirror older than v11, every cloud selection was made by hand, because playlists could not
    /// add anything to the cloud. So the upgrade pins all of them. Otherwise a synced playlist could
    /// later remove albums the user chose themselves.
    /// </summary>
    [Fact]
    public void An_upgrade_pins_every_selection_that_already_existed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mdv10-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, LibraryDatabase.DataFolderName));

        using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = MirrorLocation.InFolder(root).DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString()))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // The v10 shape: every cloud column except cloud_pinned, which is what v11 adds.
            cmd.CommandText =
                """
                CREATE TABLE tracks (
                    id INTEGER PRIMARY KEY,
                    relative_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    folder TEXT NOT NULL COLLATE NOCASE,
                    file_name TEXT NOT NULL COLLATE NOCASE,
                    extension TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    modified_utc_ticks INTEGER NOT NULL,
                    duration_seconds REAL NOT NULL DEFAULT 0,
                    title TEXT COLLATE NOCASE,
                    artist TEXT COLLATE NOCASE,
                    album_artist TEXT COLLATE NOCASE,
                    album TEXT COLLATE NOCASE,
                    genre TEXT COLLATE NOCASE,
                    track_number INTEGER NOT NULL DEFAULT 0,
                    disc_number INTEGER NOT NULL DEFAULT 0,
                    disc_count INTEGER NOT NULL DEFAULT 0,
                    disc_subtitle TEXT,
                    year INTEGER NOT NULL DEFAULT 0,
                    grooves_id TEXT,
                    content_hash TEXT,
                    cloud_state INTEGER NOT NULL DEFAULT 0,
                    cloud_selected INTEGER NOT NULL DEFAULT 0,
                    canonical_size_bytes INTEGER NOT NULL DEFAULT 0,
                    cloud_synced_utc_ticks INTEGER NOT NULL DEFAULT 0,
                    cloud_error TEXT,
                    cloud_attempts INTEGER NOT NULL DEFAULT 0,
                    cloud_next_attempt_utc_ticks INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                    modified_utc_ticks, grooves_id, cloud_state, cloud_selected)
                VALUES ('a/picked.mp3', 'a', 'picked.mp3', '.mp3', 1, 1, 'gid-picked', 3, 1),
                       ('a/loose.mp3',  'a', 'loose.mp3',  '.mp3', 1, 1, 'gid-loose',  0, 0);
                PRAGMA user_version = 10;
                """;
            cmd.ExecuteNonQuery();
        }

        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));

            var picked = db.Query("picked")[0];
            Assert.True(picked.CloudSelected);
            Assert.True(picked.CloudPinned);            // the user's own choice, so it is pinned
            Assert.Equal(CloudState.Synced, picked.CloudState);

            var loose = db.Query("loose")[0];
            Assert.False(loose.CloudSelected);
            Assert.False(loose.CloudPinned);            // nothing was chosen, so nothing is pinned

            // The playlist tree gets its cloud columns added rather than being rebuilt.
            var playlist = db.CreatePlaylist(null, "P");
            Assert.Equal(PlaylistCloud.None, db.GetPlaylistCloud(playlist));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── picked by hand vs added by a playlist ──

    /// <summary>
    /// An album or a track the user picked is pinned; a playlist's tracks are not. This is how "remove
    /// the tracks nothing else needs" works: a playlist removed from the cloud removes its own tracks,
    /// and keeps the ones the user chose themselves.
    /// </summary>
    [Fact]
    public void Picking_by_hand_pins_a_track_and_a_playlist_does_not()
    {
        var byHand = Add("hand.mp3");
        var byPlaylist = Add("playlist.mp3");

        _db.SetCloudSelected([byHand.Id], selected: true, pinned: true);
        _db.SetCloudSelected([byPlaylist.Id], selected: true, pinned: false);

        Assert.True(_db.Query("hand")[0].CloudSelected);
        Assert.True(_db.Query("hand")[0].CloudPinned);
        Assert.True(_db.Query("playlist")[0].CloudSelected);
        Assert.False(_db.Query("playlist")[0].CloudPinned);
    }

    /// <summary>A playlist can set the pin but never clear it: a track in the cloud because the user
    /// picked its album cannot be removed by the playlist.</summary>
    [Fact]
    public void A_playlist_cannot_unpin_what_an_album_pinned()
    {
        var track = Add("a.mp3");
        _db.SetCloudSelected([track.Id], selected: true, pinned: true);

        _db.SetCloudSelected([track.Id], selected: true, pinned: false);

        Assert.True(_db.Query()[0].CloudPinned);
    }

    /// <summary>Removing an album clears its pin on a track that a synced playlist still uses, but
    /// does not remove the track from the cloud. The playlist decides that.</summary>
    [Fact]
    public void Unpinning_keeps_the_track_in_the_cloud()
    {
        var track = Add("a.mp3");
        _db.SetCloudSelected([track.Id], selected: true, pinned: true);
        _db.SeedSynced(track.Id, "h1", 1, 1);

        _db.UnpinCloud([track.Id]);

        var row = _db.Query()[0];
        Assert.False(row.CloudPinned);
        Assert.True(row.CloudSelected);                  // still selected for the cloud
        Assert.Equal(CloudState.Synced, row.CloudState); // and still in the cloud
    }

    /// <summary>Unselecting is the user's decision. A track that is not selected cannot be pinned, so
    /// the pin is cleared too.</summary>
    [Fact]
    public void Unselecting_clears_the_pin_too()
    {
        var track = Add("a.mp3");
        _db.SetCloudSelected([track.Id], selected: true, pinned: true);

        _db.SetCloudSelected([track.Id], selected: false);

        Assert.False(_db.Query()[0].CloudSelected);
        Assert.False(_db.Query()[0].CloudPinned);
    }

    [Fact]
    public void Forgetting_the_cloud_clears_the_pin()
    {
        var track = Add("a.mp3");
        _db.SetCloudSelected([track.Id], selected: true, pinned: true);

        _db.ForgetCloud(track.Id);

        Assert.False(_db.Query()[0].CloudPinned);
    }

    /// <summary>A removed track leaves the album's roll-up. This turns off the tile's cloud badge and
    /// removes "Remove from cloud" from its menu.</summary>
    [Fact]
    public void Removing_an_album_empties_its_cloud_rollup()
    {
        var a = Add("Artist/Album/a.mp3");
        var b = Add("Artist/Album/b.mp3");
        _db.SeedSynced(a.Id, "h1", 1, 1);
        _db.SetCloudSelected([b.Id], true);
        _db.SetCloudState(b.Id, CloudState.Queued);

        var before = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(2, before.Total);
        Assert.True(before.CanRemove);

        foreach (var track in _db.FolderTracks("Artist/Album"))
            _db.ForgetCloud(track.Id);

        var after = _db.GetAlbumCloudRollup("Artist/Album");
        Assert.Equal(0, after.InSet);
        Assert.False(after.CanRemove);
        Assert.Equal(AlbumCloudBadge.None, after.Badge);
    }

    // ── renditions: the head clip and the free tier bitrate, made on this machine ──

    /// <summary>A synced track with the audio still on disk: the input of the catch-up pass.</summary>
    private LibraryTrack AddSyncedWithId(string relativePath, string rpmId, long syncedAt = 1)
    {
        _db.Upsert(Track(relativePath) with { RpmId = rpmId });
        var row = _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
        _db.SeedSynced(row.Id, "hash-" + rpmId, 1, syncedAt);
        return _db.Query(Path.GetFileNameWithoutExtension(relativePath))[0];
    }

    [Fact]
    public void A_synced_track_needs_renditions_until_it_has_them()
    {
        var track = AddSyncedWithId("a.mp3", "gid-a");

        Assert.Single(_db.CloudTracksWithLocalFiles(10));

        _db.NoteRenditions(track.Id, 99);

        Assert.Empty(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>The result is stored so that it is kept when the app closes. A set held only in memory
    /// would make every launch encode and upload the same oldest tracks again.</summary>
    [Fact]
    public void A_track_that_has_renditions_is_not_asked_for_again_in_the_next_session()
    {
        var track = AddSyncedWithId("a.mp3", "gid-a");
        _db.NoteRenditions(track.Id, 99);
        _db.Dispose();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));
        Assert.Empty(reopened.CloudTracksWithLocalFiles(10));
    }

    [Fact]
    public void Only_the_track_that_was_done_comes_off_the_list()
    {
        var a = AddSyncedWithId("a.mp3", "gid-a", syncedAt: 1);
        AddSyncedWithId("b.mp3", "gid-b", syncedAt: 2);

        _db.NoteRenditions(a.Id, 99);

        var due = _db.CloudTracksWithLocalFiles(10);
        Assert.Equal(["gid-b"], due.Select(t => t.RpmId));
    }

    /// <summary>A failed rendition writes nothing, so the next run tries the track again. This is why
    /// the client reports whether the upload succeeded rather than ignoring a failure.</summary>
    [Fact]
    public void A_track_whose_renditions_failed_is_still_due()
    {
        AddSyncedWithId("a.mp3", "gid-a");

        // Nothing written: what the service does when MakeRenditionsAsync returns false.
        Assert.Single(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>Only tracks whose audio is still on disk can be encoded again, and only tracks with
    /// an id have a name in the cloud to store them under.</summary>
    [Fact]
    public void A_track_with_no_rpm_id_is_never_asked_for()
    {
        var row = Add("a.mp3");
        _db.SeedSynced(row.Id, "h", 1, 1);

        Assert.Empty(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>Renditions are made by this machine from a file it has; they are not read from the
    /// file. So a retag, which rebuilds the row from disk, must not queue the track again.</summary>
    [Fact]
    public void An_upsert_cannot_forget_that_renditions_were_made()
    {
        var track = AddSyncedWithId("a.mp3", "gid-a");
        _db.NoteRenditions(track.Id, 99);

        _db.Upsert(Track("a.mp3") with { RpmId = "gid-a", ModifiedUtcTicks = 99 });

        Assert.Empty(_db.CloudTracksWithLocalFiles(10));
    }

    /// <summary>
    /// The id of a row that the store has is the store's name for that track, and a scan must not
    /// change it. After a retag a file can have no id, and sync creates a new one. Storing it would
    /// leave the row marked "in the cloud" under a name the account does not have, and nothing would
    /// fix it later: the catalogue pass skips ids that the listing does not contain.
    /// </summary>
    [Fact]
    public void An_upsert_cannot_rename_a_track_the_store_is_holding()
    {
        AddSyncedWithId("a.mp3", "gid-a");

        _db.Upsert(Track("a.mp3") with { RpmId = "freshly-minted", ModifiedUtcTicks = 99 });

        Assert.Equal("gid-a", _db.Query("a")[0].RpmId);
    }

    /// <summary>The other case: the cloud has nothing under a local-only track's id, so writing a new
    /// id is the normal repair for a file that was copied outside the app.</summary>
    [Fact]
    public void An_upsert_renames_a_track_that_is_only_here()
    {
        _db.Upsert(Track("b.mp3") with { RpmId = "gid-b" });

        _db.Upsert(Track("b.mp3") with { RpmId = "gid-b-fresh", ModifiedUtcTicks = 99 });

        Assert.Equal("gid-b-fresh", _db.Query("b")[0].RpmId);
    }

    /// <summary>What the rendition pass matches against the catalogue when the store rejects a row's
    /// id: the same audio under another name means the local id is stale, not that the store lost the
    /// track.</summary>
    [Fact]
    public void The_rendition_list_carries_the_hash_the_catalog_is_matched_on()
    {
        AddSyncedWithId("a.mp3", "gid-a");

        Assert.Equal("hash-gid-a", _db.CloudTracksWithLocalFiles(10)[0].ContentHash);
    }

    /// <summary>
    /// Converting a row to a <see cref="RPM.Core.Models.TrackInfo"/> keeps its cloud state. Every
    /// track list in the app is built from these, and this conversion is the only place they get it.
    /// Without it a line could show only what its file contains, never the cloud state.
    /// </summary>
    [Fact]
    public void A_track_carries_its_cloud_state_into_the_view_model()
    {
        var queued = Add("queued.mp3");
        _db.SetCloudSelected([queued.Id], selected: true);
        _db.SetCloudState(queued.Id, CloudState.Queued);

        var info = _db.Query("queued")[0].ToTrackInfo(_root);

        Assert.Equal(CloudState.Queued, info.CloudState);
        Assert.True(info.CloudSelected);
        Assert.True(info.CloudInSet);
    }

    /// <summary>Selected, or actually in the cloud: the same rule the album roll-up uses, so a
    /// track's menu and its album's menu offer the same action for the same file.</summary>
    [Fact]
    public void In_set_means_chosen_or_up_there()
    {
        var local = Add("local.mp3").ToTrackInfo(_root);
        Assert.False(local.CloudInSet);

        var synced = Add("synced.mp3");
        _db.SeedSynced(synced.Id, "h", 1, 1);
        Assert.True(_db.Query("synced")[0].ToTrackInfo(_root).CloudInSet);
    }
}

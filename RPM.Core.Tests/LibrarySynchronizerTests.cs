using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class LibrarySynchronizerTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibrarySynchronizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Stands in for the files' embedded tags: absolute path → the RPM id stamped
    /// in it. Sync writes here through <see cref="FakeStamp"/> and reads back through
    /// <see cref="FakeRead"/>, so ids travel exactly as they do through real files, without
    /// needing TagLib to accept these one-byte fixtures.</summary>
    private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);

    private string Touch(string relative, string content = "x")
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Moves the file on disk the way the user would. The id in it travels along.</summary>
    private string MoveOnDisk(string fromRelative, string toRelative)
    {
        string from = Path.Combine(_root, fromRelative.Replace('/', Path.DirectorySeparatorChar));
        string to = Path.Combine(_root, toRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        if (_ids.Remove(from, out var id))
            _ids[to] = id;
        return to;
    }

    /// <summary>Copies the file on disk. The copy carries the source's id, which sync has
    /// to notice and repair.</summary>
    private string CopyOnDisk(string fromRelative, string toRelative)
    {
        string from = Path.Combine(_root, fromRelative.Replace('/', Path.DirectorySeparatorChar));
        string to = Path.Combine(_root, toRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
        if (_ids.TryGetValue(from, out var id))
            _ids[to] = id;
        return to;
    }

    /// <summary>Fake tag reader: derives the title from the file name, never touches TagLib.</summary>
    private TrackInfo FakeRead(string file) => new()
    {
        FilePath = file,
        Extension = Path.GetExtension(file).ToLowerInvariant(),
        Title = Path.GetFileNameWithoutExtension(file),
        Artist = "Test Artist",
        RpmId = _ids.GetValueOrDefault(file),
        Source = MetadataSource.Tags,
    };

    /// <summary>Fake stamper, standing in for the tag write.</summary>
    private string FakeStamp(string file, bool force)
    {
        if (!force && _ids.TryGetValue(file, out var existing))
            return existing;
        var id = Guid.NewGuid().ToString("D");
        _ids[file] = id;
        return id;
    }

    /// <summary>Fake writer for a PARTICULAR id, the one the store knows a track by. Stands in for
    /// the tag write the same way <see cref="FakeStamp"/> does.</summary>
    private bool FakeKeep(string file, string id)
    {
        _ids[file] = id;
        return true;
    }

    private SyncResult Sync() =>
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: FakeStamp, keepId: FakeKeep);

    [Fact]
    public void Adds_new_files_to_the_mirror()
    {
        Touch("Artist/Album/01 - a.mp3");
        Touch("Artist/Album/02 - b.flac");
        Touch("Artist/notes.txt"); // not audio, ignored

        var result = Sync();

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, _db.Count);
        Assert.Equal("01 - a", _db.Query("01 - a")[0].Title);
    }

    [Fact]
    public void Unchanged_files_are_not_reread()
    {
        Touch("a.mp3");
        Sync();

        int reads = 0;
        var result = LibrarySynchronizer.Sync(_db, f => { reads++; return FakeRead(f); }, stampId: FakeStamp);

        Assert.Equal(0, reads);
        Assert.Equal(new SyncResult(0, 0, 0), result);
    }

    /// <summary>A row from a mirror that predates the work columns says so: its movement number is
    /// null, and sync reads the file once. The read writes a number, and the row is left alone from
    /// then on. This keeps a library of a million tracks from being read again on every launch.</summary>
    [Fact]
    public void A_row_that_has_never_been_asked_about_its_work_is_read_once()
    {
        Touch("a.mp3");
        Sync();

        // Wind the row back to what an upgraded v24 mirror holds: everything else settled, the work
        // never looked at.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_root, ".rpm", "library.db")}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET movement_no = NULL";
            cmd.ExecuteNonQuery();
        }

        int reads = 0;
        LibrarySynchronizer.Sync(_db, f => { reads++; return FakeRead(f); }, stampId: FakeStamp);
        Assert.Equal(1, reads);

        // Read, answered, settled: nothing asks again.
        reads = 0;
        LibrarySynchronizer.Sync(_db, f => { reads++; return FakeRead(f); }, stampId: FakeStamp);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Changed_files_are_updated()
    {
        var path = Touch("a.mp3");
        Sync();

        File.WriteAllText(path, "different content, new size");
        var result = LibrarySynchronizer.Sync(
            _db, f => FakeRead(f) with { Title = "retagged" }, stampId: FakeStamp);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal("retagged", _db.Query()[0].Title);
    }

    [Fact]
    public void Vanished_files_are_removed()
    {
        var path = Touch("Artist/a.mp3");
        Touch("Artist/b.mp3");
        Sync();

        File.Delete(path);
        var result = Sync();

        Assert.Equal(1, result.Removed);
        Assert.Single(_db.Query());
        Assert.Equal("Artist/b.mp3", _db.Query()[0].RelativePath);
    }

    [Fact]
    public void Unreadable_files_still_land_in_the_mirror()
    {
        Touch("broken.mp3");
        var result = LibrarySynchronizer.Sync(
            _db, _ => throw new InvalidOperationException("corrupt"), stampId: FakeStamp);

        Assert.Equal(1, result.Added);
        var row = _db.Query()[0];
        Assert.Equal("broken.mp3", row.RelativePath);
        Assert.Null(row.Title);
    }

    [Fact]
    public void Data_folder_itself_is_never_ingested()
    {
        // A stray audio file inside .rpm must not become a library track.
        Touch(".rpm/leak.mp3");
        Touch("real.mp3");

        var result = Sync();

        Assert.Equal(1, result.Added);
        Assert.Equal("real.mp3", _db.Query()[0].RelativePath);
    }

    [Fact]
    public void Files_without_an_id_are_stamped()
    {
        var path = Touch("Artist/a.mp3");
        Sync();

        var id = _db.Query()[0].RpmId;
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal(id, _ids[path]); // the id in the mirror is the id in the file
    }

    [Fact]
    public void A_library_from_before_ids_is_backfilled_on_the_next_sync()
    {
        // A row the pre-id build wrote: no id, and the file on disk carries none either.
        var path = Touch("Artist/a.mp3");
        _db.Upsert(LibraryDatabase.FromTrackInfo(FakeRead(path), _root));
        Assert.Null(_db.Query()[0].RpmId);

        var result = Sync();

        Assert.Equal(1, result.Updated);
        Assert.False(string.IsNullOrWhiteSpace(_db.Query()[0].RpmId));
    }

    [Fact]
    public void A_file_moved_by_hand_keeps_its_row()
    {
        Touch("Artist/Album/a.mp3");
        Sync();
        var before = _db.Query()[0];

        MoveOnDisk("Artist/Album/a.mp3", "Somewhere Else/renamed.mp3");
        var result = Sync();

        Assert.Equal(new SyncResult(0, 0, 0, Moved: 1), result);
        var row = Assert.Single(_db.Query());
        Assert.Equal(before.Id, row.Id);               // same row, not a delete plus an add
        Assert.Equal(before.RpmId, row.RpmId);
        Assert.Equal("Somewhere Else/renamed.mp3", row.RelativePath);
    }

    [Fact]
    public void A_file_copied_by_hand_gets_an_id_of_its_own()
    {
        Touch("Artist/a.mp3");
        Sync();
        var original = _db.Query()[0];

        // A copy made outside the app carries the source's id: two files, one identity.
        CopyOnDisk("Artist/a.mp3", "Artist/a copy.mp3");
        Sync();

        var rows = _db.Query().OrderBy(r => r.RelativePath).ToList();
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].RpmId, rows[1].RpmId);
        Assert.Empty(_db.DuplicateRpmIds());

        // The original keeps its identity; the copy is the one that gets re-stamped.
        var kept = rows.Single(r => r.RelativePath == "Artist/a.mp3");
        Assert.Equal(original.Id, kept.Id);
        Assert.Equal(original.RpmId, kept.RpmId);
    }

    [Fact]
    public void A_move_and_a_delete_in_one_pass_are_told_apart()
    {
        Touch("Artist/a.mp3");
        Touch("Artist/b.mp3");
        Sync();
        var a = _db.Query().Single(r => r.RelativePath == "Artist/a.mp3");

        MoveOnDisk("Artist/a.mp3", "Moved/a.mp3");
        File.Delete(Path.Combine(_root, "Artist", "b.mp3"));
        var result = Sync();

        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Added);
        var row = Assert.Single(_db.Query());
        Assert.Equal(a.Id, row.Id);
        Assert.Equal("Moved/a.mp3", row.RelativePath);
    }

    [Fact]
    public void A_file_that_cannot_hold_an_id_is_never_reread()
    {
        // Ogg-FLAC, or anything too corrupt for TagLib to open: it can never take an id, so the
        // mirror records that it cannot, and sync stops opening it. Without the marker its row
        // would look unstamped forever and every sync would re-read it.
        Touch("unsupported.ogg");
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: (_, _) => null);

        Assert.Equal(RpmTag.NoCarrier, _db.Query()[0].RpmId);

        int reads = 0, stamps = 0;
        var result = LibrarySynchronizer.Sync(
            _db,
            f => { reads++; return FakeRead(f); },
            stampId: (_, _) => { stamps++; return null; });

        Assert.Equal(0, reads);
        Assert.Equal(0, stamps);
        Assert.Equal(new SyncResult(0, 0, 0), result);
    }

    [Fact]
    public void A_file_that_cannot_hold_an_id_is_tried_again_once_it_changes()
    {
        var path = Touch("unsupported.ogg");
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: (_, _) => null);

        // Re-encoded to something that can hold an id: size/mtime moved, so it is a candidate again.
        File.WriteAllText(path, "now a container we can tag");
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: FakeStamp);

        Assert.True(RpmTag.IsId(_db.Query()[0].RpmId));
    }

    [Fact]
    public void The_no_carrier_marker_is_not_an_identity()
    {
        // Two files that cannot hold an id both carry the marker. It must not read as "these are
        // the same file": neither a duplicate to re-stamp, nor a move to match against.
        Touch("a.ogg");
        Touch("b.ogg");
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: (_, _) => null);
        Assert.Empty(_db.DuplicateRpmIds());

        File.Delete(Path.Combine(_root, "a.ogg"));
        Touch("c.ogg");
        var result = LibrarySynchronizer.Sync(_db, FakeRead, stampId: (_, _) => null);

        Assert.Equal(0, result.Moved); // c.ogg is a new file, not a.ogg moved
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    /// <summary>
    /// A file whose row says its bytes are in the cloud comes back from a retag without its id, and
    /// gets the one the store knows it by, not a new one. Creating a new id here would leave the
    /// library showing tracks as backed up under names the account does not have: the id IS the
    /// track's name in the cloud, and the row is the only thing that still remembers it.
    /// </summary>
    [Fact]
    public void A_file_that_lost_its_id_gets_back_the_name_the_store_knows_it_by()
    {
        var path = Touch("Artist/Album/01 - a.flac");
        Sync();

        var row = _db.Query()[0];
        _db.SeedSynced(row.Id, "hash", 1, 1);
        var itsCloudName = _db.Query()[0].RpmId;

        // Retagged by anything at all: the file changed and the id went with the tags.
        _ids.Remove(path);
        File.WriteAllText(path, "retagged");

        Sync();

        Assert.Equal(itsCloudName, _db.Query()[0].RpmId);
        Assert.Equal(itsCloudName, _ids[path]);   // and the file carries it again
    }

    /// <summary>A file that cannot be given its name back still does not cost the row that name. The
    /// tag write is best effort; the row is the datasource, and it is the row the cloud is read
    /// from.</summary>
    [Fact]
    public void A_file_that_cannot_take_the_name_back_leaves_the_row_holding_it()
    {
        var path = Touch("Artist/Album/01 - a.flac");
        Sync();

        var row = _db.Query()[0];
        _db.SeedSynced(row.Id, "hash", 1, 1);
        var itsCloudName = _db.Query()[0].RpmId;

        _ids.Remove(path);
        File.WriteAllText(path, "retagged");

        LibrarySynchronizer.Sync(_db, FakeRead, stampId: FakeStamp, keepId: (_, _) => false);

        Assert.Equal(itsCloudName, _db.Query()[0].RpmId);
    }

    /// <summary>Nothing is holding a local track's id, so an unstamped file gets a fresh one exactly
    /// as before.</summary>
    [Fact]
    public void A_local_file_that_lost_its_id_is_stamped_afresh()
    {
        var path = Touch("Artist/Album/01 - a.flac");
        Sync();
        var before = _db.Query()[0].RpmId;

        _ids.Remove(path);
        File.WriteAllText(path, "retagged");

        Sync();

        Assert.NotEqual(before, _db.Query()[0].RpmId);
        Assert.True(RpmTag.IsId(_db.Query()[0].RpmId));
    }

    [Fact]
    public void Real_files_survive_a_hand_move_end_to_end()
    {
        // The other tests fake the tag I/O. This one runs the real TagReader over real MP3s:
        // sync stamps the id into the file itself, the user moves the file, and the next sync
        // recognises it by the id it reads back out.
        TouchMp3("Artist/Album/a.mp3");
        TouchMp3("Artist/Album/b.mp3");
        LibrarySynchronizer.Sync(_db);

        var before = _db.Query().Single(r => r.RelativePath == "Artist/Album/a.mp3");
        Assert.False(string.IsNullOrWhiteSpace(before.RpmId));

        MoveOnDisk("Artist/Album/a.mp3", "Reorganized By Hand/a.mp3");
        var result = LibrarySynchronizer.Sync(_db);

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Added);

        var row = _db.Query().Single(r => r.RelativePath == "Reorganized By Hand/a.mp3");
        Assert.Equal(before.Id, row.Id);
        Assert.Equal(before.RpmId, row.RpmId);
    }

    /// <summary>A minimal valid MPEG-1 Layer III frame (silence), enough for TagLib to accept
    /// the file and write a real UFID frame into it.</summary>
    private string TouchMp3(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00; // 128kbps 44.1kHz
        File.WriteAllBytes(path, frame);
        return path;
    }

    [Fact]
    public void A_new_file_that_never_had_an_id_is_an_add_not_a_move()
    {
        Touch("Artist/a.mp3");
        Sync();
        File.Delete(Path.Combine(_root, "Artist", "a.mp3"));
        Touch("Artist/unrelated.mp3");

        var result = Sync();

        Assert.Equal(0, result.Moved);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
    }

    // ---- Cloud-only tracks: rows whose file is missing on purpose. ----

    /// <summary>Puts a track in the library, uploads it, and deletes the local file, like a listener
    /// making room on the disk for an album that is safely in the cloud.</summary>
    private LibraryTrack Offload(string relative)
    {
        Touch(relative);
        Sync();
        var track = _db.Query(Path.GetFileNameWithoutExtension(relative))[0];
        _db.SeedSynced(track.Id, "hash-of-" + relative, 1234, DateTime.UtcNow.Ticks);
        _db.MarkOffloaded(track.Id);
        File.Delete(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return track;
    }

    [Fact]
    public void An_offloaded_track_is_not_deleted_just_because_its_file_is_gone()
    {
        var track = Offload("Artist/Album/01 - a.mp3");

        var result = Sync();

        // Without this the first sync after an offload wipes the user's whole cloud library.
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, _db.Count);
        Assert.Equal(CloudState.CloudOnly, _db.Query()[0].CloudState);
        Assert.Equal(track.Id, _db.Query()[0].Id);
        Assert.Equal("hash-of-Artist/Album/01 - a.mp3", _db.Query()[0].ContentHash);
    }

    [Fact]
    public void An_offloaded_track_survives_repeated_syncs()
    {
        Offload("a.mp3");

        for (int i = 0; i < 3; i++)
            Assert.Equal(0, Sync().Removed);

        Assert.Equal(1, _db.Count);
        Assert.Equal(CloudState.CloudOnly, _db.Query()[0].CloudState);
    }

    [Fact]
    public void A_genuinely_deleted_track_is_still_removed()
    {
        Touch("gone.mp3");
        Touch("stays.mp3");
        Sync();
        File.Delete(Path.Combine(_root, "gone.mp3"));

        // The offload exemption must not become a blanket "never delete anything".
        Assert.Equal(1, Sync().Removed);
        Assert.Equal(1, _db.Count);
        Assert.Equal("stays.mp3", _db.Query()[0].RelativePath);
    }

    [Fact]
    public void A_restored_file_puts_the_track_back_on_disk()
    {
        var track = Offload("Artist/Album/01 - a.mp3");

        // The user restored it: same path, same id in its tags.
        Touch("Artist/Album/01 - a.mp3");

        Sync();

        var row = _db.Query()[0];
        Assert.Equal(track.Id, row.Id);                  // same row, not a new one
        Assert.Equal(CloudState.Synced, row.CloudState); // both copies exist now
        Assert.Equal("hash-of-Artist/Album/01 - a.mp3", row.ContentHash);
        Assert.Equal(1, _db.Count);
    }

    [Fact]
    public void An_adopted_row_whose_file_arrives_becomes_the_librarys_own()
    {
        // A track this machine has never held, read out of the user's cloud catalogue.
        _db.MirrorCloudLibrary(
        [
            new CloudTrack("gid-1", "sha-1", "Artist/Album/01 - a.mp3", 5_000_000,
                "a", "Test Artist", "Album", 1000),
        ]);
        Assert.True(_db.Query()[0].CloudAdopted);

        // Its file turns up on this disk, brought down or copied in by hand.
        Touch("Artist/Album/01 - a.mp3");

        Sync();

        var row = _db.Query()[0];
        Assert.Equal(CloudState.Synced, row.CloudState);
        // It does not depend on the cloud listing: signing out must not remove it.
        Assert.False(row.CloudAdopted);
        Assert.Equal(0, _db.ForgetAdopted());
        Assert.Equal(1, _db.Count);
    }

    [Fact]
    public void Adopted_rows_are_not_read_as_deleted_files()
    {
        Touch("Artist/Album/01 - a.mp3");
        Sync();

        _db.MirrorCloudLibrary(
        [
            new CloudTrack("gid-2", "sha-2", "Elsewhere/Other/02 - b.mp3", 5_000_000,
                "b", "Other Artist", "Other", 1000),
        ]);

        // Their files are meant to be missing: the same exemption an offloaded row gets.
        Assert.Equal(0, Sync().Removed);
        Assert.Equal(2, _db.Count);
    }

    [Fact]
    public void A_restored_file_dropped_in_a_different_folder_is_recognized_by_its_id()
    {
        var track = Offload("Artist/Album/01 - a.mp3");
        string offloadedPath = Path.Combine(_root, "Artist", "Album", "01 - a.mp3");

        // The file comes back somewhere else, carrying the id it was stamped with.
        var restored = Touch("Restored/01 - a.mp3");
        _ids[restored] = _ids[offloadedPath];

        var result = Sync();

        Assert.Equal(1, _db.Count);                       // one track, not a ghost plus a copy
        Assert.Equal(1, result.Moved);
        var row = _db.Query()[0];
        Assert.Equal(track.Id, row.Id);
        Assert.Equal("Restored/01 - a.mp3", row.RelativePath);
        Assert.Equal(CloudState.Synced, row.CloudState);
    }

    [Fact]
    public void An_offloaded_row_does_not_swallow_an_unrelated_new_file()
    {
        Offload("Artist/Album/01 - a.mp3");

        // A different track appears. It has no id of its own yet, and it must not be mistaken for
        // the offloaded one coming home.
        Touch("Other/02 - b.mp3");

        var result = Sync();

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Moved);
        Assert.Equal(2, _db.Count);
        Assert.Equal(CloudState.CloudOnly, _db.Query("01 - a")[0].CloudState);
        Assert.Equal(CloudState.Local, _db.Query("02 - b")[0].CloudState);
    }

    // ---- The library folder going missing is not the library being deleted. ----

    [Fact]
    public void A_library_folder_that_is_not_mounted_does_not_empty_the_mirror()
    {
        Touch("Artist/Album/01 - a.mp3");
        Touch("Artist/Album/02 - b.mp3");
        Sync();
        Assert.Equal(2, _db.Count);

        // The external drive is unplugged. Every file "vanishes" at once.
        Directory.Delete(Path.Combine(_root, "Artist"), recursive: true);
        Directory.Delete(Path.Combine(_root, LibraryDatabase.DataFolderName), recursive: true);
        Directory.Delete(_root, recursive: true);

        Assert.Throws<DirectoryNotFoundException>(Sync);

        // Nothing was touched. Plugging the drive back in gets the library back.
        Directory.CreateDirectory(_root);
        Touch("Artist/Album/01 - a.mp3");
        Touch("Artist/Album/02 - b.mp3");
        Sync();
        Assert.Equal(2, _db.Count);
    }

    /// <summary>A folder the scan could not list (permissions, a share that dropped) says nothing
    /// about the files in it. Reading it as empty removed every row under it.</summary>
    [Fact]
    public void Rows_under_a_folder_that_cannot_be_listed_are_kept()
    {
        if (OperatingSystem.IsWindows())
            return;

        Touch("Open/01 - a.mp3");
        Touch("Locked/01 - b.mp3");
        Touch("Locked/Disc 2/01 - c.mp3");
        Sync();
        Assert.Equal(3, _db.Count);

        var locked = Path.Combine(_root, "Locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var result = Sync();

            Assert.Equal(0, result.Removed);
            Assert.Equal(3, _db.Count);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Readable again, nothing to do: the rows were never touched.
        Assert.Equal(new SyncResult(0, 0, 0), Sync());
    }

    /// <summary>A known track whose file changed and then would not read keeps its tags. The failed
    /// read is not an answer, and the next pass tries again.</summary>
    [Fact]
    public void A_track_whose_tags_cannot_be_read_again_keeps_the_ones_it_had()
    {
        var path = Touch("01 - a.mp3");
        Sync();
        Assert.Equal("01 - a", _db.Query()[0].Title);

        File.WriteAllText(path, "changed on disk, new size");
        var failed = LibrarySynchronizer.Sync(
            _db, _ => throw new IOException("locked"), stampId: FakeStamp, keepId: FakeKeep);

        Assert.Equal(new SyncResult(0, 0, 0), failed);
        Assert.Equal("01 - a", _db.Query()[0].Title);
        Assert.Equal("Test Artist", _db.Query()[0].Artist);

        // Still pending, so a readable file is picked up on the next pass.
        var retried = LibrarySynchronizer.Sync(
            _db, f => FakeRead(f) with { Title = "retagged" }, stampId: FakeStamp, keepId: FakeKeep);

        Assert.Equal(1, retried.Updated);
        Assert.Equal("retagged", _db.Query()[0].Title);
    }

    [Fact]
    public void A_library_folder_that_came_up_empty_does_not_empty_the_mirror()
    {
        Touch("a.mp3");
        Touch("b.mp3");
        Sync();

        // The folder is there but the files are not, a mount that is still starting, a cloud
        // drive in the middle of a restore, or the wrong root. It is not two deletions.
        File.Delete(Path.Combine(_root, "a.mp3"));
        File.Delete(Path.Combine(_root, "b.mp3"));

        Assert.Throws<InvalidOperationException>(Sync);
        Assert.Equal(2, _db.Count);
    }

    [Fact]
    public void A_fully_offloaded_library_syncs_fine_even_though_no_file_is_there()
    {
        // Every track is in the cloud and none is on disk. That is not a folder that came up empty;
        // it is exactly what offloading a whole library looks like, and it must keep working.
        Offload("a.mp3");
        Offload("b.mp3");

        var result = Sync();

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, _db.Count);
        Assert.All(_db.Query(), t => Assert.Equal(CloudState.CloudOnly, t.CloudState));
    }

    [Fact]
    public void An_empty_library_is_still_allowed_to_be_empty()
    {
        // Nothing in the mirror, nothing on disk: a new library, not a lost one.
        var result = Sync();
        Assert.Equal(0, result.Added);
        Assert.Equal(0, _db.Count);
    }

    [Fact]
    public void Cloud_state_survives_the_user_retagging_a_synced_file()
    {
        Touch("a.mp3");
        Sync();
        var track = _db.Query()[0];
        _db.SeedSynced(track.Id, "hash-1", 999, DateTime.UtcNow.Ticks);

        // Re-tagging rewrites the file, so sync sees a changed file and upserts the row. That
        // upsert must not reset the track to Local: its bytes are still in the cloud.
        File.WriteAllText(Path.Combine(_root, "a.mp3"), "retagged, longer content");
        Sync();

        var row = _db.Query()[0];
        Assert.Equal(CloudState.Synced, row.CloudState);
        Assert.Equal("hash-1", row.ContentHash);
    }

    // ── quarantine: music copied into the collection from outside ─────────────────────────────────

    private SyncResult SyncQuarantining() =>
        LibrarySynchronizer.Sync(_db, FakeRead, stampId: FakeStamp, quarantineForeign: true);

    /// <summary>Gives the mirror something in it, so quarantine is not sitting out the adoption pass.</summary>
    private void EstablishLibrary()
    {
        Touch("Established/Album/01 - known.mp3");
        Sync();
    }

    private string InboxPath(string relative) =>
        Path.Combine(_root, LibraryInbox.FolderName, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void A_file_copied_into_the_collection_goes_to_the_inbox()
    {
        EstablishLibrary();
        Touch("Nirvana/Nevermind/01 - smells.mp3");

        var result = SyncQuarantining();

        Assert.Equal(1, result.Quarantined);
        Assert.Equal(0, result.Added);
        // Not on the shelf, and not where it was dropped.
        Assert.Equal(1, _db.Count);
        Assert.False(File.Exists(Path.Combine(_root, "Nirvana", "Nevermind", "01 - smells.mp3")));
        Assert.True(File.Exists(InboxPath("Nirvana/Nevermind/01 - smells.mp3")));
    }

    /// <summary>The collection ends up as if the copy had never landed in it.</summary>
    [Fact]
    public void The_folders_a_copy_left_behind_are_cleaned_up()
    {
        EstablishLibrary();
        Touch("Nirvana/Nevermind/01 - smells.mp3");

        SyncQuarantining();

        Assert.False(Directory.Exists(Path.Combine(_root, "Nirvana")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Established", "Album")));
    }

    /// <summary>The disaster case. A user points the app at the folder of music they already have:
    /// every file in it is unknown and unstamped, which is exactly what a copied-in file looks like.
    /// Quarantining here would sweep somebody's whole collection into the inbox on first run.</summary>
    [Fact]
    public void Adopting_a_library_for_the_first_time_quarantines_nothing()
    {
        Touch("Nirvana/Nevermind/01 - smells.mp3");
        Touch("Pixies/Doolittle/01 - debaser.mp3");

        var result = SyncQuarantining();

        Assert.Equal(0, result.Quarantined);
        Assert.Equal(2, result.Added);
        Assert.True(File.Exists(Path.Combine(_root, "Nirvana", "Nevermind", "01 - smells.mp3")));
    }

    /// <summary>The other way the same disaster arrives: a library whose files were filed before ids
    /// were ever stamped. Those have rows, and a row at the file's own path is the collection saying
    /// it knows this one.</summary>
    [Fact]
    public void A_file_the_mirror_already_knows_is_never_quarantined()
    {
        Touch("Legacy/Album/01 - old.mp3");
        Sync();
        _ids.Clear();   // as if it had never been stamped

        var result = SyncQuarantining();

        Assert.Equal(0, result.Quarantined);
        Assert.True(File.Exists(Path.Combine(_root, "Legacy", "Album", "01 - old.mp3")));
    }

    /// <summary>A file the user rearranged by hand carries its id, so it is a move, the row follows
    /// it, and nothing is swept anywhere.</summary>
    [Fact]
    public void A_file_moved_by_hand_is_still_a_move()
    {
        EstablishLibrary();
        Touch("Artist/Album/01 - a.mp3");
        Sync();
        MoveOnDisk("Artist/Album/01 - a.mp3", "Artist/Better Album/01 - a.mp3");

        var result = SyncQuarantining();

        Assert.Equal(0, result.Quarantined);
        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(Path.Combine(_root, "Artist", "Better Album", "01 - a.mp3")));
    }

    /// <summary>Everything in the inbox is waiting to be imported, not filed. A row for it would put
    /// un-filed music on the shelf, and the moment the import moved the file out, that row would
    /// read as a track the user deleted.</summary>
    [Fact]
    public void What_is_waiting_in_the_inbox_never_enters_the_mirror()
    {
        EstablishLibrary();
        Touch($"{LibraryInbox.FolderName}/Nirvana/01 - smells.mp3");

        var result = SyncQuarantining();

        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Quarantined);
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, _db.Count);
        Assert.True(File.Exists(InboxPath("Nirvana/01 - smells.mp3")));
    }

    /// <summary>A quarantined file is not re-quarantined into itself on the next pass, and the one
    /// already waiting is left where it is.</summary>
    [Fact]
    public void A_second_pass_leaves_the_inbox_alone()
    {
        EstablishLibrary();
        Touch("Nirvana/01 - smells.mp3");
        SyncQuarantining();

        var second = SyncQuarantining();

        Assert.Equal(0, second.Quarantined);
        Assert.Equal(1, _db.Count);
        Assert.True(File.Exists(InboxPath("Nirvana/01 - smells.mp3")));
    }

    /// <summary>Quarantine is off unless the caller asks for it: the same foreign file is adopted
    /// where it lies, which is what every other caller of Sync still gets.</summary>
    [Fact]
    public void Without_quarantine_a_copied_in_file_is_adopted_as_before()
    {
        EstablishLibrary();
        Touch("Nirvana/01 - smells.mp3");

        var result = Sync();

        Assert.Equal(1, result.Added);
        Assert.True(File.Exists(Path.Combine(_root, "Nirvana", "01 - smells.mp3")));
    }

    [Fact]
    public void A_track_the_store_is_holding_is_never_read_as_a_deletion()
    {
        // Deleting music from the disk is how somebody makes room, and it says nothing about what
        // they want kept in the cloud. The file goes, the row stays and becomes a cloud row, and the
        // row is the only thing that knows the recording is in the store. Reading it as "the user
        // deleted this track" would drop the row and the pointer to the stored copy with it.
        Touch("Band/Record/01 One.flac");
        Touch("Band/Record/02 Two.flac");   // the folder is not emptied; sync refuses that outright
        Sync();

        var track = _db.Query().Single(t => t.FileName.StartsWith("01", StringComparison.Ordinal));
        _db.SeedSynced(track.Id, "sha", 1000, DateTime.UtcNow.Ticks);

        File.Delete(Path.Combine(_root, "Band", "Record", "01 One.flac"));

        var result = Sync();

        Assert.Equal(0, result.Removed);

        var still = _db.Query().Single(t => t.RpmId == track.RpmId);
        Assert.Equal(CloudState.CloudOnly, still.CloudState);
    }

    /// <summary>
    /// The whole of "delete music to make room, and get it back later", from this end: the file the
    /// user removed leaves a row this machine can offer to fetch again, and nothing in the library
    /// treats it as a track to be taken out of the cloud.
    /// </summary>
    [Fact]
    public void A_file_the_user_removed_to_make_room_is_offered_back_rather_than_forgotten()
    {
        Touch("Band/Record/01 One.flac");
        Touch("Band/Record/02 Two.flac");
        Sync();

        var track = _db.Query().Single(t => t.FileName.StartsWith("01", StringComparison.Ordinal));
        _db.SeedSynced(track.Id, "sha", 4_000_000, DateTime.UtcNow.Ticks);

        File.Delete(Path.Combine(_root, "Band", "Record", "01 One.flac"));
        Sync();

        // This machine's own, so the bring-back offers it, and it is not one of the catalogue rows
        // another install put on the wall, which this computer has no business downloading.
        var mine = _db.QueryOffloadedHere();
        Assert.Single(mine);
        Assert.Equal(track.RpmId, mine[0].RpmId);
        Assert.Equal(0, _db.FromElsewhereTally().Count);

        // And the record is still a record: the album keeps both its songs, one of them streaming.
        Assert.Equal(2, _db.FolderTracks("Band/Record").Count);
    }

    [Fact]
    public void A_track_that_was_never_in_the_store_still_goes_when_its_file_does()
    {
        // The other half. A file the user deleted, of a track nowhere else, is a deletion, and a
        // library that never forgot anything would fill up with rows for music that is gone.
        Touch("Band/Record/01 One.flac");
        Touch("Band/Record/02 Two.flac");
        Sync();

        File.Delete(Path.Combine(_root, "Band", "Record", "01 One.flac"));

        Assert.Equal(1, Sync().Removed);
        Assert.Single(_db.Query());
    }

    [Fact]
    public void Offloading_is_written_down_before_the_file_is_let_go_of()
    {
        Touch("Band/Record/01 One.flac");
        Sync();

        var track = _db.Query().Single();

        // A row nobody has uploaded is not one to delete a file for, and MarkOffloaded says so.
        // The caller checks this before it touches the disk.
        Assert.False(_db.MarkOffloaded(track.Id));

        _db.SeedSynced(track.Id, "sha", 1000, DateTime.UtcNow.Ticks);
        Assert.True(_db.MarkOffloaded(track.Id));

        // And again, for the pass that arrives second. It is right about where the bytes are.
        Assert.True(_db.MarkOffloaded(track.Id));
    }

    [Fact]
    public void A_row_that_was_rebuilt_under_a_new_number_is_still_found_by_its_own_id()
    {
        // An upload takes minutes. A sync that runs while a file is being moved into place reads it as
        // gone, drops the row and adds it again with a new row id. The upload still holds the old
        // number, so everything it writes lands on no row, and the step after the writing is a
        // delete.
        Touch("Band/Record/01 One.flac");
        Touch("Band/Record/02 Two.flac");
        Sync();

        var track = _db.Query().Single(t => t.FileName.StartsWith("01", StringComparison.Ordinal));
        _db.SeedSynced(track.Id, "sha", 1000, DateTime.UtcNow.Ticks);

        var staleId = track.Id;
        var rpmId = track.RpmId!;

        // The row goes and comes back: same file, same id in it, a different number in this table.
        _db.RemoveMany([track.RelativePath]);
        Sync();

        var rebuilt = _db.Query().Single(t => t.RpmId == rpmId);
        Assert.NotEqual(staleId, rebuilt.Id);

        // The stale number alone finds nothing, so the file would be deleted and the row would never
        // know. The track's own id finds the row.
        _db.SeedSynced(rebuilt.Id, "sha", 1000, DateTime.UtcNow.Ticks);
        Assert.False(_db.MarkOffloaded(staleId));
        Assert.True(_db.MarkOffloaded(staleId, rpmId));

        Assert.Equal(CloudState.CloudOnly, _db.Query().Single(t => t.RpmId == rpmId).CloudState);
    }
}

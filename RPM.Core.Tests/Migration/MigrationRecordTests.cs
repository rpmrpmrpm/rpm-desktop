using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>
/// The record of an import from another app: one row per track the source listed, with its state, so
/// a long import continues after a restart and a second run imports only what is new.
/// </summary>
public class MigrationRecordTests : IDisposable
{
    private const long Now = 638_000_000_000_000_000;

    private readonly string _root;
    private LibraryDatabase _db;

    public MigrationRecordTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmmigration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static SourceLibrary Library(SourceKind kind = SourceKind.MusicBee, string id = "lib-1", params string[] playlists) => new(
        kind, id, "MusicBee", [], [.. playlists.Select(p => new SourcePlaylist(p, p, [], false, [], []))], []);

    [Fact]
    public void Tables_are_created_on_open_without_a_version_step()
    {
        var path = MirrorLocation.InFolder(_root).DatabasePath;
        _db.Dispose();
        long before;
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "DROP TABLE migration_playlist; DROP TABLE migration_track; DROP TABLE migration; PRAGMA user_version;";
            before = Convert.ToInt64(cmd.ExecuteScalar());
        }

        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a"]);

        Assert.Equal(1, record.Counts().Found);
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(before, Convert.ToInt64(version.ExecuteScalar()));
    }

    [Fact]
    public void Open_twice_reuses_the_migration()
    {
        var first = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        first.AddFound(["a", "b"]);
        first.Note("a", MigrationTrackState.Imported, trackId: 7);

        var second = _db.OpenMigration(Library(), MigrationDestination.CloudOnly);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(MigrationDestination.CloudOnly, second.Destination);
        Assert.Equal(1, second.Counts().Imported);
        Assert.NotEqual(first.Id, _db.OpenMigration(Library(id: "lib-2"), MigrationDestination.ThisComputer).Id);
        Assert.NotEqual(first.Id, _db.OpenMigration(Library(SourceKind.ITunes), MigrationDestination.ThisComputer).Id);
    }

    [Fact]
    public void Add_found_keeps_existing_states()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a", "b"]);
        record.Note("a", MigrationTrackState.Imported, trackId: 7);

        record.AddFound(["a", "b", "c"]);

        Assert.Equal(3, record.Counts().Found);
        Assert.Equal(["b", "c"], record.Due(Now, 10).Select(r => r.SourceId));
        Assert.Equal(7, record.Rows(MigrationTrackState.Imported).Single().TrackId);
    }

    [Fact]
    public void Due_is_in_source_order()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["z", "a", "m"]);
        record.AddFound(["b"]);

        Assert.Equal(["z", "a", "m", "b"], record.Due(Now, 10).Select(r => r.SourceId));
        Assert.Equal(["z", "a"], record.Due(Now, 2).Select(r => r.SourceId));
    }

    [Fact]
    public void Due_skips_waiting_backoff_and_gives_up_after_five()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a"]);

        record.NoteFailure("a", "Migration_ReasonReadFailed", Now);
        Assert.Empty(record.Due(Now, 10));
        Assert.Single(record.Due(Now + TimeSpan.FromMinutes(3).Ticks, 10));

        var at = Now;
        for (var i = 0; i < 4; i++)
        {
            at += TimeSpan.FromHours(1).Ticks;
            record.NoteFailure("a", "Migration_ReasonReadFailed", at);
        }

        Assert.Empty(record.Due(at + TimeSpan.FromDays(1).Ticks, 10));
        Assert.Equal(1, record.Counts().NotImported);
        Assert.Equal("Migration_ReasonReadFailed", record.Rows(MigrationTrackState.NotImported).Single().Reason);
    }

    [Fact]
    public void Permanent_not_imported_is_not_due()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a"]);

        record.Note("a", MigrationTrackState.NotImported, reason: "Migration_ReasonFileNotFound", detail: "/m/a.mp3");

        Assert.Empty(record.Due(Now, 10));
        var row = record.Rows(MigrationTrackState.NotImported).Single();
        Assert.Equal("/m/a.mp3", row.Detail);
        Assert.Equal(1, record.Counts().NotImported);
    }

    [Fact]
    public void Retry_resets_attempts()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a"]);
        record.Note("a", MigrationTrackState.NotImported, reason: "Migration_ReasonFileNotFound");

        record.Retry(["a"]);

        var due = Assert.Single(record.Due(Now, 10));
        Assert.Equal(MigrationTrackState.Found, due.State);
        Assert.Equal(0, due.Attempts);
    }

    [Fact]
    public void An_unfinished_import_is_found_without_opening_it()
    {
        Assert.Null(_db.UnfinishedMigration(Library()));

        var record = _db.OpenMigration(Library(), MigrationDestination.CloudOnly);
        record.AddFound(["a", "b", "c"]);
        record.Note("a", MigrationTrackState.Imported, 1);

        var found = _db.UnfinishedMigration(Library());
        Assert.NotNull(found);
        Assert.Equal(MigrationDestination.CloudOnly, found.Destination);
        Assert.Equal(new MigrationCounts(3, 1, 0, 0, 2), found.Counts());
        Assert.Null(_db.UnfinishedMigration(Library(id: "other")));

        record.Finish(Now);
        Assert.Null(_db.UnfinishedMigration(Library()));
    }

    [Fact]
    public void Start_over_checks_every_track_again_but_keeps_playlists_and_downloads_that_wait()
    {
        var record = _db.OpenMigration(Library(playlists: "p1"), MigrationDestination.CloudOnly);
        record.AddFound(["a", "b", "c", "d"]);
        record.Note("a", MigrationTrackState.Imported, 1);
        record.Note("b", MigrationTrackState.NotImported, reason: MigrationReason.FileNotFound);
        record.Note("c", MigrationTrackState.Downloaded, 3);
        record.NotePlaylist("p1", 9, ["a"]);

        record.StartOver();

        Assert.Equal(["a", "b", "d"], record.Rows(MigrationTrackState.Found).Select(r => r.SourceId));
        Assert.Equal(3, record.Rows(MigrationTrackState.Downloaded).Single().TrackId);
        Assert.Equal(9, record.Playlist("p1")?.PlaylistId);
    }

    [Fact]
    public void Counts_add_up_to_found()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a", "b", "c", "d", "e"]);
        record.Note("a", MigrationTrackState.Imported, trackId: 1);
        record.Note("b", MigrationTrackState.AlreadyInRpm, trackId: 2);
        record.Note("c", MigrationTrackState.NotImported, reason: "Migration_ReasonFileNotFound");
        record.NoteFailure("d", "Migration_ReasonReadFailed", Now);

        var counts = record.Counts();

        Assert.Equal(new MigrationCounts(Found: 5, Imported: 1, AlreadyInRpm: 1, NotImported: 1, Waiting: 2), counts);
    }

    [Fact]
    public void Track_ids_cover_imported_and_already_in_rpm()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        record.AddFound(["a", "b", "c"]);
        record.Note("a", MigrationTrackState.Imported, trackId: 1);
        record.Note("b", MigrationTrackState.AlreadyInRpm, trackId: 2);

        Assert.Equal(new Dictionary<string, long> { ["a"] = 1, ["b"] = 2 }, record.TrackIds());
    }

    [Fact]
    public void Playlist_files_get_a_library_key_from_their_paths()
    {
        var one = _db.OpenMigration(Library(SourceKind.PlaylistFiles, "", "/p/b.m3u", "/p/a.m3u"), MigrationDestination.ThisComputer);
        var same = _db.OpenMigration(Library(SourceKind.PlaylistFiles, "", "/p/a.m3u", "/p/b.m3u"), MigrationDestination.ThisComputer);
        var other = _db.OpenMigration(Library(SourceKind.PlaylistFiles, "", "/p/c.m3u"), MigrationDestination.ThisComputer);

        Assert.Equal(one.Id, same.Id);
        Assert.NotEqual(one.Id, other.Id);
    }

    [Fact]
    public void Playlist_entries_round_trip()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.ThisComputer);
        Assert.Null(record.Playlist("p1"));

        record.NotePlaylist("p1", 42, ["a", "b", "a"]);

        var (playlistId, entries) = record.Playlist("p1")!.Value;
        Assert.Equal(42, playlistId);
        Assert.Equal(["a", "b", "a"], entries);
    }

    [Fact]
    public void Unfinished_migrations_are_listed_until_finished()
    {
        var record = _db.OpenMigration(Library(), MigrationDestination.CloudOnly);
        record.AddFound(["a"]);

        var open = Assert.Single(_db.UnfinishedMigrations());
        Assert.Equal((SourceKind.MusicBee, "lib-1", "MusicBee", MigrationDestination.CloudOnly), (open.Kind, open.LibraryId, open.Name, open.Destination));

        record.Finish(Now);
        Assert.Empty(_db.UnfinishedMigrations());

        // A second run opens it again.
        _db.OpenMigration(Library(), MigrationDestination.CloudOnly);
        Assert.Single(_db.UnfinishedMigrations());
    }
}

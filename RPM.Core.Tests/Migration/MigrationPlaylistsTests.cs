using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Playlists brought by an import, and the report at the end of it.</summary>
public class MigrationPlaylistsTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public MigrationPlaylistsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmmigpl-{Guid.NewGuid():N}");
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

    private static SourceTrack Track(string id, string title, string path = "", string artist = "Band") =>
        new(id, new LocalFile(path.Length > 0 ? path : "/m/" + id), title, artist, "Record", artist, 1, 1, 1, 100, 0, false, 0, null, null);

    private static SourcePlaylist List(string id, string name, string[] tracks, string[]? folder = null, bool smart = false, string[]? unresolved = null) =>
        new(id, name, folder ?? [], smart, tracks, unresolved ?? []);

    private LibraryTrack Row(string title, string? rel = null)
    {
        rel ??= $"Band/Record/{title}.mp3";
        _db.Upsert(new LibraryTrack
        {
            RelativePath = rel, Folder = Path.GetDirectoryName(rel)!.Replace('\\', '/'), FileName = Path.GetFileName(rel), Extension = ".mp3",
            SizeBytes = 1, ModifiedUtcTicks = 1, Title = title, Artist = "Band", Album = "Record", RpmId = Guid.NewGuid().ToString("D"),
        });
        return _db.FindByRelativePath(rel)!;
    }

    /// <summary>A record in which a, b and c were imported as the rows of the same names.</summary>
    private (SourceLibrary Library, LibraryDatabase.MigrationRecord Record) Imported(SourceKind kind, SourcePlaylist[] playlists, params string[] notImported)
    {
        var tracks = new[] { Track("a", "A"), Track("b", "B"), Track("c", "C") }.Concat(notImported.Select(n => Track(n, n.ToUpperInvariant()))).ToArray();
        var library = new SourceLibrary(kind, "lib", "MusicBee", tracks, playlists, []);
        var record = _db.OpenMigration(library, MigrationDestination.ThisComputer);
        record.AddFound(tracks.Select(t => t.SourceId));
        foreach (var id in new[] { "a", "b", "c" })
            record.Note(id, MigrationTrackState.Imported, Row(id.ToUpperInvariant()).Id);
        foreach (var id in notImported)
            record.Note(id, MigrationTrackState.NotImported, reason: MigrationReason.FileNotFound, detail: "/m/" + id);
        return (library, record);
    }

    private PlaylistNode Node(string name) => _db.GetPlaylistNodes().Single(n => n.Name == name && n.Kind == PlaylistNodeKind.Playlist);

    private string[] Titles(PlaylistNode node) =>
        [.. _db.GetPlaylistItems(node.Id).Select(i => _db.Query().Single(t => t.RpmId == i.RpmId).Title!)];

    [Fact]
    public void Playlists_keep_order_and_folders()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Late", ["c", "a", "b"], ["Moods", "Evening"])]);

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "MusicBee"));

        var node = Node("Late");
        Assert.Equal(["C", "A", "B"], Titles(node));
        Assert.Equal(["Moods", "Evening"], _db.PlaylistFolderChain(node.Id));
        Assert.Equal(new PlaylistOutcome("Late", 3, 0, false, false), outcome);
    }

    [Fact]
    public void Playlist_keeps_repeated_entries()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Party", ["a", "b", "a"])]);

        MigrationPlaylists.Apply(_db, library, record, "MusicBee");

        Assert.Equal(["A", "B", "A"], Titles(Node("Party")));
    }

    [Fact]
    public void Name_clash_gets_source_name_in_brackets()
    {
        _db.CreatePlaylist(null, "Road trip");
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Road trip", ["a"])]);

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "MusicBee"));

        Assert.True(outcome.Renamed);
        Assert.Equal("Road trip (MusicBee)", outcome.Name);
        Assert.Equal(["A"], Titles(Node("Road trip (MusicBee)")));
        Assert.Empty(_db.GetPlaylistItems(Node("Road trip").Id));
    }

    [Fact]
    public void Second_run_appends_new_entries_only()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Mix", ["a", "b"])]);
        MigrationPlaylists.Apply(_db, library, record, "MusicBee");
        var node = Node("Mix");
        // The person took B out of the playlist in rpm.
        _db.RemovePlaylistItem(_db.GetPlaylistItems(node.Id).Single(i => i.RpmId == _db.Query().Single(t => t.Title == "B").RpmId).Id);

        var again = library with { Playlists = [List("p1", "Mix", ["a", "b", "c"])] };
        MigrationPlaylists.Apply(_db, again, record, "MusicBee");

        Assert.Equal(["A", "C"], Titles(node));
        Assert.Single(_db.GetPlaylistNodes().Where(n => n.Kind == PlaylistNodeKind.Playlist && !n.IsNowPlaying));
    }

    [Fact]
    public void Tracks_that_arrive_after_a_stop_take_their_places_in_a_playlist_nobody_changed()
    {
        // A run stopped at "cloud full" adds the playlist with the tracks that arrived; Continue brings the rest.
        var tracks = new[] { Track("a", "A"), Track("b", "B"), Track("c", "C") };
        var library = new SourceLibrary(SourceKind.MusicBee, "lib", "MusicBee", tracks, [List("p1", "Mix", ["b", "a", "c", "b"])], []);
        var record = _db.OpenMigration(library, MigrationDestination.ThisComputer);
        record.AddFound(tracks.Select(t => t.SourceId));
        record.Note("a", MigrationTrackState.Imported, Row("A").Id);
        MigrationPlaylists.Apply(_db, library, record, "MusicBee");
        Assert.Equal(["A"], Titles(Node("Mix")));

        record.Note("b", MigrationTrackState.Imported, Row("B").Id);
        record.Note("c", MigrationTrackState.Imported, Row("C").Id);
        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "MusicBee"));

        Assert.Equal(["B", "A", "C", "B"], Titles(Node("Mix")));
        Assert.Equal(4, outcome.Entries);
    }

    [Fact]
    public void A_playlist_another_device_already_imported_is_linked_not_made_twice()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Mix", ["b", "a"])]);
        _db.CreatePlaylist(null, "Mix");   // the person's own
        var twin = _db.CreatePlaylist(null, "Mix (MusicBee)");   // taken in from the web's import of the same library
        _db.AddSongsToPlaylist(twin, [Song("B"), Song("A")], AlreadyInPlaylist.AddAnother);

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "MusicBee"));

        Assert.Equal(2, _db.GetPlaylistNodes().Count(n => n.Kind == PlaylistNodeKind.Playlist && !n.IsNowPlaying));
        Assert.Equal(twin, record.Playlist("p1")?.PlaylistId);
        Assert.Equal("Mix (MusicBee)", outcome.Name);
    }

    private (string? RpmId, string RelPath) Song(string title)
    {
        var row = _db.Query().Single(t => t.Title == title);
        return (row.RpmId, row.RelativePath);
    }

    [Fact]
    public void Smart_playlist_becomes_plain_and_is_reported()
    {
        var (library, record) = Imported(SourceKind.ITunes, [List("p1", "Top Rated", ["a", "b"], smart: true)]);

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "iTunes"));

        Assert.True(outcome.WasSmart);
        Assert.Equal(["A", "B"], Titles(Node("Top Rated")));
    }

    [Fact]
    public void Unresolved_and_not_imported_entries_are_counted()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [List("p1", "Mix", ["a", "x"], unresolved: ["999"])], "x");

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "MusicBee"));

        Assert.Equal(1, outcome.Entries);
        Assert.Equal(2, outcome.Unmatched);
    }

    [Fact]
    public void Playlist_file_entry_matches_by_its_last_folders_and_file_name()
    {
        Row("One", "Band/Record/01 One.wav");
        const string path = @"C:\Users\Test\Music\Band\Record\01 One.wav";
        var library = new SourceLibrary(SourceKind.PlaylistFiles, "", "", [Track(path, "01 One", path)],
            [List("/p/mix.m3u", "mix", [path])], []);
        var record = _db.OpenMigration(library, MigrationDestination.ThisComputer);
        record.AddFound([path]);
        record.Note(path, MigrationTrackState.NotImported, reason: MigrationReason.FileNotFound, detail: path);

        var outcome = Assert.Single(MigrationPlaylists.Apply(_db, library, record, "Playlist files"));

        Assert.Equal((1, 0), (outcome.Entries, outcome.Unmatched));
        Assert.Equal(["One"], Titles(Node("mix")));
    }

    [Fact]
    public void Report_lists_every_track_not_imported()
    {
        var (library, record) = Imported(SourceKind.MusicBee, [], "x", "y");
        record.Note("c", MigrationTrackState.AlreadyInRpm, reason: MigrationReason.LowerQualityInRpm, detail: "/m/c.flac");
        var skipped = library with { Skipped = [new SourceSkip("Radio", SkipReason.NotAFile)] };

        var report = MigrationReports.Build(skipped, record, []);

        Assert.Equal("MusicBee", report.SourceName);
        Assert.Equal(new MigrationCounts(5, 2, 1, 2, 0), report.Counts);
        Assert.Equal([("Band - X", MigrationReason.FileNotFound, "/m/x"), ("Band - Y", MigrationReason.FileNotFound, "/m/y")],
            report.NotImported.Select(n => (n.What, n.ReasonKey, n.Detail)));
        Assert.Equal(["/m/c.flac"], report.LowerQualityInRpm);
        Assert.Equal("Radio", Assert.Single(report.Skipped).What);
    }
}

using Microsoft.Data.Sqlite;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Opening a mirror written by an older build: a copy of the file is taken before any migration step
/// runs, the steps run as one transaction, and the copy is removed once the file is past the copy's
/// version.
/// </summary>
public class LibraryUpgradeTests : IDisposable
{
    private readonly string _root;

    public LibraryUpgradeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdupg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, LibraryDatabase.DataFolderName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string DataFolder => Path.Combine(_root, LibraryDatabase.DataFolderName);

    private string CopyAt(long version) => Path.Combine(DataFolder, $"library.v{version}.backup.db");

    private string[] CopiesOnDisk() =>
        Directory.GetFiles(DataFolder, "library.v*").Select(Path.GetFileName).ToArray()!;

    private static SqliteConnection Raw(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c;
    }

    private static long UserVersion(string path)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<string> Columns(string path, string table)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }

    private static List<string> Titles(string path)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT title FROM tracks ORDER BY id";
        using var r = cmd.ExecuteReader();
        var titles = new List<string>();
        while (r.Read()) titles.Add(r.GetString(0));
        return titles;
    }

    private static void Run(string path, string sql)
    {
        using var c = Raw(path);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// A v2 mirror (the tracks table before the rename of grooves_id) with one track, and a trigger
    /// that refuses every UPDATE of a track. The v14 step backfills added_utc_ticks with an UPDATE,
    /// so the migration fails there, after the v23 rename and every column added before it.
    /// </summary>
    private void SeedV2WithARefusingTrigger()
    {
        Run(MirrorLocation.InFolder(_root).DatabasePath,
            """
            PRAGMA journal_mode = WAL;
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
                                modified_utc_ticks, title, grooves_id)
            VALUES ('a/x.mp3', 'a', 'x.mp3', '.mp3', 10, 20, 'First', 'gid-1');
            CREATE TRIGGER refuse_updates BEFORE UPDATE ON tracks
            BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;
            PRAGMA user_version = 2;
            """);
    }

    [Fact]
    public void A_failed_migration_leaves_the_file_as_it_was_and_keeps_the_copy()
    {
        SeedV2WithARefusingTrigger();
        var path = MirrorLocation.InFolder(_root).DatabasePath;

        var failed = Assert.Throws<SqliteException>(() => new LibraryDatabase(MirrorLocation.InFolder(_root)));
        Assert.Contains("refused by the test", failed.Message);

        // One transaction: the rename and the columns added before the failing step are gone too.
        Assert.Equal(2, UserVersion(path));
        var columns = Columns(path, "tracks");
        Assert.Contains("grooves_id", columns);
        Assert.DoesNotContain("rpm_id", columns);
        Assert.DoesNotContain("added_utc_ticks", columns);

        // The copy is the file as it was before the attempt, and nothing else is left beside it.
        Assert.Equal(["library.v2.backup.db"], CopiesOnDisk());
        Assert.Equal(2, UserVersion(CopyAt(2)));
        Assert.Equal(["First"], Titles(CopyAt(2)));
        Assert.Contains("grooves_id", Columns(CopyAt(2), "tracks"));
    }

    [Fact]
    public void The_copy_is_removed_once_the_migration_commits()
    {
        SeedV2WithARefusingTrigger();
        var path = MirrorLocation.InFolder(_root).DatabasePath;
        Assert.Throws<SqliteException>(() => new LibraryDatabase(MirrorLocation.InFolder(_root)));
        Assert.True(File.Exists(CopyAt(2)));

        Run(path, "DROP TRIGGER refuse_updates");
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
            Assert.Equal("First", Assert.Single(db.Query()).Title);

        Assert.Empty(CopiesOnDisk());
        Assert.True(UserVersion(path) > 2);
    }

    [Fact]
    public void A_copy_from_an_earlier_attempt_is_kept_rather_than_taken_again()
    {
        SeedV2WithARefusingTrigger();
        var path = MirrorLocation.InFolder(_root).DatabasePath;
        Assert.Throws<SqliteException>(() => new LibraryDatabase(MirrorLocation.InFolder(_root)));

        // Written between the attempts, as an older build that can still open the file would.
        Run(path,
            """
            INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                modified_utc_ticks, title, grooves_id)
            VALUES ('a/y.mp3', 'a', 'y.mp3', '.mp3', 10, 20, 'Second', 'gid-2');
            """);
        Assert.Throws<SqliteException>(() => new LibraryDatabase(MirrorLocation.InFolder(_root)));

        Assert.Equal(["First"], Titles(CopyAt(2)));
        Assert.Equal(["First", "Second"], Titles(path));
    }

    [Fact]
    public void A_new_library_takes_no_copy()
    {
        using (new LibraryDatabase(MirrorLocation.InFolder(_root))) { }
        Assert.Empty(CopiesOnDisk());
    }

    [Fact]
    public void An_open_removes_copies_below_the_files_version_and_leaves_the_rest()
    {
        using (new LibraryDatabase(MirrorLocation.InFolder(_root))) { }
        var current = UserVersion(MirrorLocation.InFolder(_root).DatabasePath);

        // Left by migrations that committed but whose copy could not be deleted, and by a copy that
        // was being written when the process ended.
        File.WriteAllText(CopyAt(40), "x");
        File.WriteAllText(CopyAt(current - 1), "x");
        File.WriteAllText(CopyAt(current - 1) + ".part", "x");
        File.WriteAllText(CopyAt(current - 1) + ".part-journal", "x");
        // Not below the file's version, so not known to be finished.
        File.WriteAllText(CopyAt(current), "x");
        File.WriteAllText(CopyAt(current + 1), "x");

        using (new LibraryDatabase(MirrorLocation.InFolder(_root))) { }

        Assert.Equal(
            new[] { $"library.v{current}.backup.db", $"library.v{current + 1}.backup.db" }.Order(),
            CopiesOnDisk().Order());
    }
}

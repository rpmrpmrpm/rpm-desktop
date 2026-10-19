using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Where a library's database is kept: in the music folder on this computer's own disks, on this
/// computer when the folder is on a network volume, and the copy across between the two. A test
/// cannot mount a share, so the network volume is simulated through the internal <c>For</c> that
/// takes the volume's answer; the real question is asked of a local folder only.
/// </summary>
public class MirrorLocationTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _music;
    private readonly string _thisComputer;

    public MirrorLocationTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"mirrorloc-{Guid.NewGuid():N}");
        _music = Path.Combine(_tmp, "music");
        _thisComputer = Path.Combine(_tmp, "local", MirrorLocation.LibrariesFolderName);
        Directory.CreateDirectory(_music);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // A test that makes the folder's .rpm read-only gives it back, or the delete below fails.
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(Path.GetDirectoryName(InFolderFile)!, (UnixFileMode)0b111_101_101); } catch { }
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string InFolderFile => Path.Combine(_music, LibraryDatabase.DataFolderName, LibraryDatabase.DataFileName);

    private string IdFile => Path.Combine(_music, LibraryDatabase.DataFolderName, MirrorLocation.IdFileName);

    private MirrorLocation OnNetwork(string? root = null) =>
        MirrorLocation.For(root ?? _music, _thisComputer, DriveType.Network);

    private MirrorLocation OnLocalDisk() =>
        MirrorLocation.For(_music, _thisComputer, DriveType.Fixed);

    private static LibraryTrack Track(string relativePath) => new()
    {
        RelativePath = relativePath,
        Folder = relativePath[..relativePath.LastIndexOf('/')],
        FileName = relativePath[(relativePath.LastIndexOf('/') + 1)..],
        Extension = Path.GetExtension(relativePath),
        SizeBytes = 5_000_000,
        ModifiedUtcTicks = 1,
    };

    private static void Add(MirrorLocation where, string relativePath)
    {
        using var db = new LibraryDatabase(where);
        db.Upsert(Track(relativePath));
    }

    private static List<string> Paths(MirrorLocation where)
    {
        using var db = new LibraryDatabase(where);
        return db.Query().Select(t => t.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A second path to the same folder. A symbolic link on macOS and Linux. On Windows a directory
    /// junction, because a symbolic link there needs Developer Mode or an elevated shell and fails with
    /// "a required privilege is not held by the client" on a PC without either. .NET has no call
    /// that makes a junction, so <c>mklink /J</c> does.
    /// </summary>
    private static void LinkFolder(string alias, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(alias, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in (string[])["/c", "mklink", "/J", alias, target])
            start.ArgumentList.Add(argument);

        using var mklink = Process.Start(start)!;
        var said = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, $"mklink /J failed ({mklink.ExitCode}): {said}");
    }

    private static List<string> PathsInFile(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT relative_path FROM tracks ORDER BY relative_path";
        using var r = cmd.ExecuteReader();
        var paths = new List<string>();
        while (r.Read()) paths.Add(r.GetString(0));
        return paths;
    }

    [Fact]
    public void A_folder_on_a_network_volume_keeps_its_database_on_this_computer()
    {
        var where = OnNetwork();
        Add(where, "A/B/01.flac");

        Assert.True(where.OnThisComputer);
        var id = MirrorLocation.LibraryId.Read(_music);
        Assert.NotNull(id);
        Assert.Equal(Path.Combine(_thisComputer, id, LibraryDatabase.DataFileName), where.DatabasePath);
        Assert.True(File.Exists(where.DatabasePath));

        // Nothing SQLite opens is in the music folder: no database, no WAL, no shared-memory index.
        Assert.Equal([MirrorLocation.IdFileName],
            Directory.GetFiles(Path.Combine(_music, LibraryDatabase.DataFolderName)).Select(Path.GetFileName));
    }

    [Fact]
    public void The_same_folder_reached_by_another_path_opens_the_same_database()
    {
        Add(OnNetwork(), "A/B/01.flac");

        // The share mounted again under another name.
        var alias = Path.Combine(_tmp, "music-1");
        LinkFolder(alias, _music);

        var again = OnNetwork(alias);
        Assert.Equal(OnNetwork().DatabasePath, again.DatabasePath);
        Assert.Equal(["A/B/01.flac"], Paths(again));
    }

    [Fact]
    public void A_network_folders_own_database_is_copied_to_this_computer_once_and_left_as_it_was()
    {
        // A library used in this folder before: an older build, or a folder moved to the NAS.
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        var before = File.ReadAllBytes(InFolderFile);

        var where = OnNetwork();
        Assert.Equal(new MirrorLocation.Carried(InFolderFile, OnNetworkVolume: true), where.CarryFrom);
        Assert.Equal(["A/B/01.flac"], Paths(where));

        // Written on this computer only, and not copied over again on the next open.
        Add(where, "A/B/02.flac");
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], Paths(OnNetwork()));

        Assert.Equal(before, File.ReadAllBytes(InFolderFile));
        Assert.Equal(["A/B/01.flac"], PathsInFile(InFolderFile));
    }

    /// <summary>
    /// The share's own library.db with rows that are only in its -wal, as an older build leaves it
    /// when it ends without a checkpoint or while another computer has it open. A copy of the main file
    /// alone loses the rows in the WAL.
    ///
    /// <para>Two things catch a copy that opens the share's file with SQLite. On every platform, the
    /// share's files are compared afterwards: SQLite closing a WAL file folds the -wal into the main
    /// file and deletes it, which is a write to the share. On macOS and Linux the share's .rpm is also
    /// made read-only, which is what a WebDAV mount does: SQLite cannot create the -shm it
    /// needs there, and the open fails with SQLITE_CANTOPEN. Windows has no mode bits to do that
    /// with.</para>
    /// </summary>
    [Fact]
    public void A_share_library_with_rows_only_in_its_wal_is_copied_whole_without_sqlite_opening_it_there()
    {
        // Built in a folder of its own, then its main file and -wal are copied to the share while the
        // connection is still open, so the -wal holds the second row and there is no -shm beside them.
        var seed = Path.Combine(_tmp, "seed");
        Add(MirrorLocation.InFolder(seed), "A/B/01.flac");
        var seedFile = MirrorLocation.InFolder(seed).DatabasePath;
        using (var live = new SqliteConnection($"Data Source={seedFile};Pooling=False"))
        {
            live.Open();
            using (var cmd = live.CreateCommand())
            {
                cmd.CommandText =
                    """
                    PRAGMA wal_autocheckpoint = 0;
                    INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes, modified_utc_ticks)
                    VALUES ('A/B/02.flac', 'A/B', '02.flac', '.flac', 10, 20);
                    """;
                cmd.ExecuteNonQuery();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(InFolderFile)!);
            File.Copy(seedFile, InFolderFile);
            File.Copy(seedFile + "-wal", InFolderFile + "-wal");
        }
        Assert.True(new FileInfo(InFolderFile + "-wal").Length > 0);

        var where = OnNetwork();
        var shareFolder = Path.GetDirectoryName(InFolderFile)!;
        var before = Directory.GetFiles(shareFolder).Select(Path.GetFileName).Order().ToArray();
        var main = File.ReadAllBytes(InFolderFile);
        var wal = File.ReadAllBytes(InFolderFile + "-wal");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(shareFolder, (UnixFileMode)0b101_101_101);

        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], Paths(where));

        // Nothing was written beside the share's file: no -shm, and both files as they were.
        Assert.Equal(before, Directory.GetFiles(shareFolder).Select(Path.GetFileName).Order().ToArray());
        Assert.Equal(main, File.ReadAllBytes(InFolderFile));
        Assert.Equal(wal, File.ReadAllBytes(InFolderFile + "-wal"));

        // The copy on this computer stands on its own: no -wal of the copy was left behind for the
        // move to strand.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(where.DatabasePath)!, "*.part*"));
    }

    /// <summary>
    /// Opening a new folder's library, and opening it again, throws nothing, handled or not. A
    /// debugger set to break on thrown exceptions stops on each one, and <c>LibraryService.Open</c>
    /// runs on the interface thread. Reading a <c>library.json</c> that does not exist yet must not
    /// throw first-chance exceptions. Only this thread's exceptions are counted, because other tests
    /// run at the same time.
    /// </summary>
    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Fixed)]
    public void Opening_a_new_folders_library_throws_nothing(DriveType volume)
    {
        // The SQLite driver's static constructor runs once per process, on its first connection, and
        // on Windows throws and catches inside the driver. It is not this code's, and it would be
        // counted here if this test were the process's first connection.
        using (new SqliteConnection()) { }

        var thread = Environment.CurrentManagedThreadId;
        var thrown = new List<string>();
        void Seen(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
                lock (thrown) thrown.Add($"{e.Exception.GetType().Name}: {e.Exception.Message}");
        }

        AppDomain.CurrentDomain.FirstChanceException += Seen;
        try
        {
            MirrorLocation.For(_music, _thisComputer, volume);
            using (new LibraryDatabase(MirrorLocation.For(_music, _thisComputer, volume))) { }
            using (new LibraryDatabase(MirrorLocation.For(_music, _thisComputer, volume))) { }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Seen;
        }

        Assert.Empty(thrown);
    }

    [Fact]
    public void A_damaged_database_on_the_share_refuses_the_open_and_leaves_nothing_on_either_side()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InFolderFile)!);
        var junk = new byte[8192];
        new Random(23).NextBytes(junk);
        File.WriteAllBytes(InFolderFile, junk);
        var where = OnNetwork();

        Assert.ThrowsAny<Exception>(() => new LibraryDatabase(where));

        Assert.Equal(junk, File.ReadAllBytes(InFolderFile));
        Assert.Equal([LibraryDatabase.DataFileName, MirrorLocation.IdFileName],
            Directory.GetFiles(Path.GetDirectoryName(InFolderFile)!).Select(Path.GetFileName).Order());
        var local = Path.GetDirectoryName(where.DatabasePath)!;
        Assert.Empty(Directory.Exists(local) ? Directory.GetFiles(local) : []);
    }

    [Fact]
    public void A_network_folders_database_from_an_older_build_is_migrated_on_this_computer_only()
    {
        using (var seed = new SqliteConnection($"Data Source={EnsureDataFolder(InFolderFile)};Pooling=False"))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText =
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
                PRAGMA user_version = 2;
                """;
            cmd.ExecuteNonQuery();
        }

        var where = OnNetwork();
        using (var db = new LibraryDatabase(where))
            Assert.Equal("gid-1", Assert.Single(db.Query()).RpmId);

        // The folder's file is still the version 2 an older build on another computer can open, and
        // the upgrade's copy was taken and removed beside this computer's file, not in the folder.
        Assert.Equal(2L, UserVersion(InFolderFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(InFolderFile)!, "library.v*"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(where.DatabasePath)!, "library.v*"));
    }

    [Fact]
    public void A_folder_moved_off_a_network_volume_gets_this_computers_database()
    {
        Add(OnNetwork(), "A/B/01.flac");
        var local = OnNetwork().DatabasePath;

        var where = OnLocalDisk();
        Assert.False(where.OnThisComputer);
        Assert.Equal(InFolderFile, where.DatabasePath);
        Assert.Equal(new MirrorLocation.Carried(local, OnNetworkVolume: false), where.CarryFrom);
        Assert.Equal(["A/B/01.flac"], Paths(where));
        Assert.True(File.Exists(InFolderFile));

        // This computer's copy is left where it is.
        Assert.True(File.Exists(local));
    }

    [Fact]
    public void A_folder_that_has_a_database_is_never_given_another()
    {
        Add(OnNetwork(), "A/B/01.flac");
        Add(MirrorLocation.InFolder(_music), "C/D/01.flac");

        var where = OnLocalDisk();
        Assert.Null(where.CarryFrom);
        Assert.Equal(["C/D/01.flac"], Paths(where));
    }

    [Fact]
    public void A_folder_on_a_local_disk_with_no_id_keeps_its_database_in_the_folder()
    {
        var where = OnLocalDisk();
        Add(where, "A/B/01.flac");

        Assert.Equal(InFolderFile, where.DatabasePath);
        Assert.Null(where.CarryFrom);
        Assert.False(File.Exists(IdFile));
        Assert.False(Directory.Exists(_thisComputer));
    }

    [Fact]
    public void The_real_volume_check_keeps_a_temporary_folder_in_the_folder()
    {
        var where = MirrorLocation.For(_music, _thisComputer);
        Assert.False(where.OnThisComputer);
        Assert.Equal(InFolderFile, where.DatabasePath);
    }

    [Fact]
    public void A_missing_music_folder_is_refused_rather_than_created()
    {
        var missing = Path.Combine(_tmp, "gone");
        Assert.Throws<DirectoryNotFoundException>(() => MirrorLocation.For(missing, _thisComputer));
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"id\":42}")]
    [InlineData("{\"id\":\"../../../etc\"}")]
    [InlineData("{\"id\":\"0123456789ABCDEF0123456789ABCDEF\"}")]
    public void An_id_file_that_does_not_hold_an_id_refuses_the_open(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IdFile)!);
        File.WriteAllText(IdFile, text);

        var refused = Assert.Throws<InvalidDataException>(() => OnNetwork());
        Assert.Contains(IdFile, refused.Message);

        // Not replaced: a new id would leave this computer's database for the old one behind.
        Assert.Equal(text, File.ReadAllText(IdFile));
    }

    [Fact]
    public void An_unreadable_id_file_does_not_stop_a_local_folder_that_has_its_database()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        File.WriteAllText(IdFile, "not json");

        Assert.Equal(["A/B/01.flac"], Paths(OnLocalDisk()));
    }

    [Fact]
    public void Opens_racing_on_the_first_id_never_get_two_ids()
    {
        var ids = new string?[16];
        var refusals = new Exception?[16];
        Parallel.For(0, ids.Length, i =>
        {
            try { ids[i] = MirrorLocation.LibraryId.ReadOrCreate(_music); }
            catch (Exception e) { refusals[i] = e; }
        });

        // One id, the one in the file. An open that read while the winner was still writing fails
        // loudly instead of making another.
        var got = Assert.Single(ids.OfType<string>().Distinct());
        Assert.Equal(got, MirrorLocation.LibraryId.Read(_music));
        Assert.All(refusals.OfType<Exception>(), e => Assert.IsAssignableFrom<IOException>(e));
    }

    [Fact]
    public void Opens_racing_on_the_first_copy_open_one_database_and_leave_no_unfinished_copy()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        var where = OnNetwork();

        var seen = new List<string>[4];
        Parallel.For(0, seen.Length, i => seen[i] = Paths(where));

        Assert.All(seen, paths => Assert.Equal(["A/B/01.flac"], paths));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(where.DatabasePath)!, "*.part*"));
    }

    [Fact]
    public void A_copy_left_unfinished_by_a_process_that_ended_is_removed_by_the_next_copy()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        var where = OnNetwork();
        var folder = Path.GetDirectoryName(where.DatabasePath)!;
        Directory.CreateDirectory(folder);
        var leftover = Path.Combine(folder, $"{LibraryDatabase.DataFileName}.{Guid.NewGuid():N}.part");
        File.WriteAllText(leftover, "half a copy");
        File.WriteAllText(leftover + "-journal", "x");
        // A copy from a share takes the share's -wal along, so an interrupted one leaves that too.
        File.WriteAllText(leftover + "-wal", "x");

        Assert.Equal(["A/B/01.flac"], Paths(where));
        Assert.Empty(Directory.GetFiles(folder, "*.part*"));
    }

    [Fact]
    public void A_legacy_data_folder_is_moved_before_the_id_is_written()
    {
        var legacy = Path.Combine(_music, ".grooves");
        Directory.CreateDirectory(legacy);
        using (var seed = new SqliteConnection(
                   $"Data Source={Path.Combine(legacy, LibraryDatabase.DataFileName)};Pooling=False"))
        {
            seed.Open();
            using var cmd = seed.CreateCommand();
            cmd.CommandText = "CREATE TABLE x (y INTEGER)";
            cmd.ExecuteNonQuery();
        }

        var where = OnNetwork();

        Assert.False(Directory.Exists(legacy));
        Assert.Equal(new MirrorLocation.Carried(InFolderFile, OnNetworkVolume: true), where.CarryFrom);
        Assert.True(File.Exists(IdFile));
    }

    [Fact]
    public void A_network_folder_with_only_its_id_file_is_an_existing_library()
    {
        Add(OnNetwork(), "A/B/01.flac");

        Assert.True(LibraryDatabase.ExistsUnder(_music));
        Assert.Equal(LibraryFolderKind.ExistingLibrary, LibraryFolder.Classify(_music));
    }

    [Theory]
    [InlineData(@"\\nas\music\Albums", true)]
    [InlineData(@"\\?\UNC\nas\music\Albums", true)]
    [InlineData(@"\\?\unc\nas\music", true)]
    [InlineData(@"//nas/music", true)]
    [InlineData(@"C:\Music", false)]
    [InlineData(@"\\?\C:\Music", false)]
    [InlineData(@"\\.\C:\Music", false)]
    [InlineData(@"\\?\Volume{0b1c2d3e-0000-0000-0000-000000000000}\Music", false)]
    public void A_windows_path_on_a_share_is_told_from_one_on_a_drive(string path, bool share)
    {
        Assert.Equal(share, MirrorLocation.IsWindowsShare(path));
    }

    [Theory]
    [InlineData(@"C:\Music", @"C:\")]
    [InlineData(@"z:\Music", @"Z:\")]
    [InlineData(@"\\?\D:\Music", @"D:\")]
    [InlineData(@"\\.\E:\Music", @"E:\")]
    [InlineData(@"\\nas\music", null)]
    [InlineData(@"\\?\Volume{0b1c2d3e-0000-0000-0000-000000000000}\Music", null)]
    public void A_windows_paths_drive_is_the_one_asked_about(string path, string? drive)
    {
        Assert.Equal(drive, MirrorLocation.WindowsDriveRoot(path));
    }

    private static string EnsureDataFolder(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }

    private static long UserVersion(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

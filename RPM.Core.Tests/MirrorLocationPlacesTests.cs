using Microsoft.Data.Sqlite;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A music folder on this computer's own disk that keeps its database in the database folder
/// (<see cref="DatabasePlaces.OffMusicDisk"/>), the way back, and every state of the two places in between.
/// Everything happens in a temporary folder. A local disk is stood in through the internal <c>For</c> that
/// takes the volume's answer.
/// </summary>
public class MirrorLocationPlacesTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _music;
    private readonly string _databases;

    public MirrorLocationPlacesTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"dbplaces-{Guid.NewGuid():N}");
        _music = Path.Combine(_tmp, "music");
        _databases = Path.Combine(_tmp, "fast", "libraries");
        Directory.CreateDirectory(_music);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string DataFolder => Path.Combine(_music, LibraryDatabase.DataFolderName);

    private string InFolderFile => Path.Combine(DataFolder, LibraryDatabase.DataFileName);

    private string IdFile => Path.Combine(DataFolder, MirrorLocation.IdFileName);

    private string Id => MirrorLocation.LibraryId.Read(_music)!;

    private string OffDiskFile => MirrorLocation.OffDiskPath(_databases, Id);

    private DatabasePlaces On => new(_databases, OffMusicDisk: true);

    private DatabasePlaces Off => new(_databases, OffMusicDisk: false);

    private MirrorLocation Local(DatabasePlaces places) => MirrorLocation.For(_music, places, DriveType.Fixed);

    private static LibraryTrack Track(string relativePath) => new()
    {
        RelativePath = relativePath,
        Folder = relativePath[..relativePath.LastIndexOf('/')],
        FileName = relativePath[(relativePath.LastIndexOf('/') + 1)..],
        Extension = Path.GetExtension(relativePath),
        SizeBytes = 5_000_000,
        ModifiedUtcTicks = 1,
    };

    /// <summary>Opens the location, adds a track, and returns the file that was opened.</summary>
    private static string Add(MirrorLocation where, string relativePath)
    {
        using var db = new LibraryDatabase(where);
        db.Upsert(Track(relativePath));
        return db.DatabasePath;
    }

    private static (string Opened, List<string> Paths) Open(MirrorLocation where)
    {
        using var db = new LibraryDatabase(where);
        return (db.DatabasePath, db.Query().Select(t => t.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList());
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

    private static string[] Names(string folder) =>
        Directory.Exists(folder)
            ? Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray()
            : [];

    [Fact]
    public void Turning_it_on_moves_the_database_and_renames_the_old_one_aside()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");

        var where = Local(On);
        Assert.True(where.OnThisComputer);
        Assert.Equal(OffDiskFile, where.DatabasePath);
        Assert.Equal(new MirrorLocation.Carried(InFolderFile, OnNetworkVolume: false), where.CarryFrom);

        var (opened, paths) = Open(where);
        Assert.Equal(OffDiskFile, opened);
        Assert.Equal(["A/B/01.flac"], paths);

        // The old file is kept, renamed, as one whole file with the same rows.
        Assert.False(File.Exists(InFolderFile));
        Assert.Equal(["A/B/01.flac"], PathsInFile(InFolderFile + LibraryDatabase.MovedSuffix));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(OffDiskFile)!, "*.part*"));
    }

    [Fact]
    public void Only_the_database_leaves_the_music_folder()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Directory.CreateDirectory(Path.Combine(DataFolder, "rips"));
        File.WriteAllText(Path.Combine(DataFolder, "rips", "disc.json"), "{}");
        File.WriteAllText(Path.Combine(DataFolder, "duplicates.json"), "[]");

        Open(Local(On));

        Assert.Equal(
            ["duplicates.json", "library.db.moved", MirrorLocation.IdFileName, "rips"],
            Names(DataFolder));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(DataFolder, "rips", "disc.json")));
        Assert.Equal(
            [MirrorLocation.ClaimFileName, LibraryDatabase.DataFileName],
            Names(Path.GetDirectoryName(OffDiskFile)!));
    }

    [Fact]
    public void A_new_library_with_the_switch_on_starts_in_the_database_folder()
    {
        var opened = Add(Local(On), "A/B/01.flac");

        Assert.Equal(OffDiskFile, opened);
        Assert.Equal([MirrorLocation.IdFileName], Names(DataFolder));
    }

    [Fact]
    public void Rows_that_are_only_in_the_wal_are_moved_too()
    {
        // The folder's database as a live one leaves it: the second row only in its -wal.
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

            Directory.CreateDirectory(DataFolder);
            File.Copy(seedFile, InFolderFile);
            File.Copy(seedFile + "-wal", InFolderFile + "-wal");
        }

        var (opened, paths) = Open(Local(On));

        Assert.Equal(OffDiskFile, opened);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], paths);
        Assert.Equal(["library.db.moved", MirrorLocation.IdFileName], Names(DataFolder));
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], PathsInFile(InFolderFile + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void A_database_another_program_has_open_stays_where_it_is_until_the_next_open()
    {
        // Windows only: the move asks Windows whether another handle has the file open, and Windows refuses to
        // rename a file that is open. On macOS and Linux an idle connection holds no lock, so nothing stops the move.
        if (!OperatingSystem.IsWindows())
            return;
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");

        using (var other = new SqliteConnection($"Data Source={InFolderFile};Pooling=False"))
        {
            other.Open();
            var (opened, paths) = Open(Local(On));

            Assert.Equal(InFolderFile, opened);
            Assert.Equal(["A/B/01.flac"], paths);
            Assert.False(File.Exists(OffDiskFile));
            Assert.False(File.Exists(InFolderFile + LibraryDatabase.MovedSuffix));
        }

        Assert.Equal(OffDiskFile, Open(Local(On)).Opened);
    }

    [Fact]
    public void A_damaged_database_is_never_moved_or_renamed()
    {
        Directory.CreateDirectory(DataFolder);
        var junk = new byte[8192];
        new Random(5).NextBytes(junk);
        File.WriteAllBytes(InFolderFile, junk);

        Assert.ThrowsAny<Exception>(() => new LibraryDatabase(Local(On)));

        Assert.Equal(junk, File.ReadAllBytes(InFolderFile));
        Assert.False(File.Exists(OffDiskFile));
        Assert.False(File.Exists(InFolderFile + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void With_the_switch_on_the_database_folders_copy_is_opened_from_then_on()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");

        var where = Local(On);
        Assert.Null(where.CarryFrom);
        Assert.Empty(where.SetAside);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], Open(where).Paths);
    }

    [Fact]
    public void Turning_it_off_carries_the_current_database_back_and_renames_the_other_aside()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");
        var off = OffDiskFile;

        var (opened, paths) = Open(Local(Off));

        Assert.Equal(InFolderFile, opened);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], paths);
        Assert.False(File.Exists(off));
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], PathsInFile(off + LibraryDatabase.MovedSuffix));

        // The copy renamed aside when it was turned on is still there, as it was.
        Assert.Equal(["A/B/01.flac"], PathsInFile(InFolderFile + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void On_off_and_on_again_keeps_every_write_and_never_opens_an_old_copy()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");
        Add(Local(Off), "A/B/03.flac");
        Add(Local(On), "A/B/04.flac");
        Add(Local(Off), "A/B/05.flac");

        Assert.Equal(["A/B/01.flac", "A/B/02.flac", "A/B/03.flac", "A/B/04.flac", "A/B/05.flac"],
            Open(Local(Off)).Paths);

        // Each old copy is kept under a name of its own. None of them is called library.db.
        Assert.Equal(
            ["library.db", "library.db.moved", "library.db.moved.2", MirrorLocation.IdFileName],
            Names(DataFolder));
        Assert.Equal(
            ["folder.json", "library.db.moved", "library.db.moved.2"],
            Names(Path.GetDirectoryName(OffDiskFile)!));
    }

    [Fact]
    public void When_both_places_hold_one_the_newer_is_kept_in_the_database_folder()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Open(Local(On));

        // An older build that knows nothing of the switch carried it back into the folder and wrote there.
        File.Copy(OffDiskFile, InFolderFile);
        Add(MirrorLocation.InFolder(_music), "F/F/01.flac");

        var where = Local(On);
        Assert.Equal(new MirrorLocation.Carried(InFolderFile, OnNetworkVolume: false), where.CarryFrom);
        Assert.Equal([OffDiskFile], where.SetAside);

        var (opened, paths) = Open(where);
        Assert.Equal(OffDiskFile, opened);
        Assert.Equal(["A/B/01.flac", "F/F/01.flac"], paths);
        Assert.Equal(["A/B/01.flac"], PathsInFile(OffDiskFile + LibraryDatabase.OlderSuffix));
        Assert.Equal(["A/B/01.flac", "F/F/01.flac"], PathsInFile(InFolderFile + ".moved.2"));
    }

    [Fact]
    public void When_both_places_hold_one_and_the_database_folders_is_newer_the_folders_is_set_aside()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Open(Local(On));
        File.Copy(OffDiskFile, InFolderFile);

        // Written through the database folder's copy after the folder's copy was made.
        Add(MirrorLocation.For(_music, On, DriveType.Network), "L/L/01.flac");

        var where = Local(On);
        Assert.Null(where.CarryFrom);
        Assert.Equal([InFolderFile], where.SetAside);

        Assert.Equal(["A/B/01.flac", "L/L/01.flac"], Open(where).Paths);
        Assert.False(File.Exists(InFolderFile));
        Assert.Equal(["A/B/01.flac"], PathsInFile(InFolderFile + LibraryDatabase.OlderSuffix));
    }

    [Fact]
    public void Two_identical_copies_keep_the_one_the_switch_asks_for()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Open(Local(On));

        // A move whose rename did not happen: both places hold the same database.
        File.Copy(OffDiskFile, InFolderFile);

        var where = Local(On);
        Assert.Null(where.CarryFrom);
        Assert.Equal([InFolderFile], where.SetAside);
        Assert.Equal(OffDiskFile, Open(where).Opened);
    }

    [Fact]
    public void With_the_switch_off_a_newer_copy_in_the_database_folder_is_carried_back()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Open(Local(On));
        File.Copy(OffDiskFile, InFolderFile);
        Add(MirrorLocation.For(_music, On, DriveType.Network), "L/L/01.flac");
        var off = OffDiskFile;

        var (opened, paths) = Open(Local(Off));

        Assert.Equal(InFolderFile, opened);
        Assert.Equal(["A/B/01.flac", "L/L/01.flac"], paths);
        Assert.Equal(["A/B/01.flac"], PathsInFile(InFolderFile + LibraryDatabase.OlderSuffix));
        Assert.Equal(["A/B/01.flac", "L/L/01.flac"], PathsInFile(off + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void An_older_copy_that_is_in_use_is_never_opened_in_place_of_the_newer()
    {
        // Windows only: the move asks Windows whether another handle has the file open, and Windows refuses to
        // rename a file that is open. On macOS and Linux an idle connection holds no lock, so nothing stops the move.
        if (!OperatingSystem.IsWindows())
            return;
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Open(Local(On));
        File.Copy(OffDiskFile, InFolderFile);
        Add(MirrorLocation.InFolder(_music), "F/F/01.flac");

        using (var other = new SqliteConnection($"Data Source={OffDiskFile};Pooling=False"))
        {
            other.Open();
            var (opened, paths) = Open(Local(On));

            Assert.Equal(InFolderFile, opened);
            Assert.Equal(["A/B/01.flac", "F/F/01.flac"], paths);
        }
    }

    [Fact]
    public void A_lost_id_file_is_found_again_from_the_claim_with_the_switch_on()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");
        var id = Id;
        File.Delete(IdFile);

        var (opened, paths) = Open(Local(On));

        Assert.Equal(id, Id);
        Assert.Equal(MirrorLocation.OffDiskPath(_databases, id), opened);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], paths);
    }

    [Fact]
    public void A_lost_id_file_is_found_again_from_the_claim_with_the_switch_off()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");
        File.Delete(IdFile);

        var (opened, paths) = Open(Local(Off));

        Assert.Equal(InFolderFile, opened);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], paths);
    }

    [Fact]
    public void A_claim_for_another_folder_is_not_taken()
    {
        var other = Path.Combine(_tmp, "other");
        Directory.CreateDirectory(other);
        using (var db = new LibraryDatabase(MirrorLocation.For(other, On, DriveType.Fixed)))
            db.Upsert(Track("O/O/01.flac"));

        Assert.Empty(Open(Local(On)).Paths);
        Assert.NotEqual(MirrorLocation.LibraryId.Read(other), Id);
    }

    [Fact]
    public void An_id_file_that_holds_no_id_keeps_the_database_in_the_music_folder()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        File.WriteAllText(IdFile, "not json");

        var where = Local(On);
        Assert.Equal(InFolderFile, where.DatabasePath);
        Assert.Null(where.CarryFrom);
        Assert.Equal(["A/B/01.flac"], Open(where).Paths);
        Assert.False(Directory.Exists(_databases));
        Assert.Equal("not json", File.ReadAllText(IdFile));
    }

    [Fact]
    public void A_new_database_folder_takes_the_database_from_the_one_chosen_before()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Add(Local(On), "A/B/02.flac");
        var before = OffDiskFile;

        var moved = Path.Combine(_tmp, "faster", "libraries");
        var places = new DatabasePlaces(moved, OffMusicDisk: true, Before: [_databases]);
        var (opened, paths) = Open(MirrorLocation.For(_music, places, DriveType.Fixed));

        Assert.Equal(MirrorLocation.OffDiskPath(moved, Id), opened);
        Assert.Equal(["A/B/01.flac", "A/B/02.flac"], paths);
        Assert.False(File.Exists(before));
        Assert.True(File.Exists(before + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void A_network_library_follows_the_database_folder_too()
    {
        Add(MirrorLocation.For(_music, _databases, DriveType.Network), "N/N/01.flac");

        var moved = Path.Combine(_tmp, "faster", "libraries");
        var places = new DatabasePlaces(moved, Before: [_databases]);
        var where = MirrorLocation.For(_music, places, DriveType.Network);

        Assert.Equal(MirrorLocation.OffDiskPath(moved, Id), where.DatabasePath);
        Assert.Equal(["N/N/01.flac"], Open(where).Paths);
        Assert.True(File.Exists(OffDiskFile + LibraryDatabase.MovedSuffix));
    }

    [Fact]
    public void A_stray_wal_with_no_database_beside_it_is_renamed_before_a_database_is_made()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(InFolderFile + "-wal", "not a wal");

        Add(Local(Off), "A/B/01.flac");

        Assert.Equal("not a wal", File.ReadAllText(InFolderFile + LibraryDatabase.StraySuffix + "-wal"));
        Assert.Equal(["A/B/01.flac"], Open(Local(Off)).Paths);
    }

    [Fact]
    public void Without_notes_the_newer_database_is_told_by_its_files()
    {
        string Bare(string name)
        {
            var file = Path.Combine(_tmp, name);
            Directory.CreateDirectory(_tmp);
            using var c = new SqliteConnection($"Data Source={file};Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE x (y INTEGER)";
            cmd.ExecuteNonQuery();
            return file;
        }

        var older = Bare("older.db");
        var newer = Bare("newer.db");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(newer, MirrorLocation.Newest([older, newer]));
        Assert.Equal(newer, MirrorLocation.Newest([newer, older]));
    }

    [Fact]
    public void The_read_ahead_finds_the_database_where_the_switch_keeps_it()
    {
        Add(MirrorLocation.InFolder(_music), "A/B/01.flac");
        Assert.Equal(InFolderFile, MirrorLocation.OwnDiskDatabase(_music, On));

        Open(Local(On));
        Assert.Equal(OffDiskFile, MirrorLocation.OwnDiskDatabase(_music, On));
        Assert.Null(MirrorLocation.OwnDiskDatabase(_music, Off));
    }

    [Fact]
    public void A_network_folder_is_never_called_spinning_or_not()
    {
        Assert.Null(SpinningDisk.Holds(@"\\nas\music\Albums"));
        Assert.Null(SpinningDisk.Holds(""));
    }

    [Fact]
    public void Asking_about_a_local_folder_never_throws()
    {
        // The answer depends on the machine's disks, so only the platform rule is checked.
        var answer = SpinningDisk.Holds(Path.Combine(_tmp, "not-made-yet"));
        if (!OperatingSystem.IsWindows())
            Assert.Null(answer);
    }

    [Fact]
    public void The_app_settings_name_the_places()
    {
        var local = Path.Combine(_tmp, "appdata");
        Directory.CreateDirectory(local);
        var fallback = Path.Combine(local, MirrorLocation.LibrariesFolderName);

        Assert.Equal(new DatabasePlaces(fallback), DatabasePlaces.FromAppSettings(local));

        File.WriteAllText(Path.Combine(local, "settings.json"),
            """{"LibraryPath":"X","LibraryDatabaseFolder":"E:\\dbs","LibraryDatabaseOffMusicDisk":true,"LibraryDatabaseFoldersBefore":["F:\\old"]}""");
        var places = DatabasePlaces.FromAppSettings(local);

        Assert.Equal(@"E:\dbs", places.ThisComputer);
        Assert.True(places.OffMusicDisk);
        Assert.Equal([@"F:\old", fallback], places.Before);
    }
}

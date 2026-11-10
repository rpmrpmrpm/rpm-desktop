using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>A library whose files are kept as they are: where the choice is stored, the guard that
/// refuses changes, and the ids that are derived without writing.</summary>
public class FileKeepingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rpm-kept-{Guid.NewGuid():N}");

    public FileKeepingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        KeptFiles.Close(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_mode_round_trips_through_the_marker_and_the_database()
    {
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        Assert.Equal(FileHandling.Organised, FileKeeping.Read(_root, db));

        FileKeeping.Write(_root, db, FileHandling.Kept);
        Assert.True(File.Exists(FileKeeping.MarkerPath(_root)));
        Assert.Equal(FileHandling.Kept, FileKeeping.Read(_root, db));
        Assert.Equal(FileHandling.Kept, FileKeeping.Read(_root, null));

        FileKeeping.Write(_root, db, FileHandling.Organised);
        Assert.False(File.Exists(FileKeeping.MarkerPath(_root)));
        Assert.Equal(FileHandling.Organised, FileKeeping.Read(_root, db));
    }

    [Fact]
    public void Either_place_saying_kept_keeps_the_library()
    {
        // Another computer's database, or a marker someone removed: one is enough.
        using var db = new LibraryDatabase(MirrorLocation.InFolder(_root));
        db.SetMeta(FileKeeping.MetaKey, "kept");
        Assert.Equal(FileHandling.Kept, FileKeeping.Read(_root, db));

        db.SetMeta(FileKeeping.MetaKey, null);
        Directory.CreateDirectory(Path.GetDirectoryName(FileKeeping.MarkerPath(_root))!);
        File.WriteAllText(FileKeeping.MarkerPath(_root), "not json");
        Assert.Equal(FileHandling.Kept, FileKeeping.Read(_root, db));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_is_kept()
    {
        Assert.Equal(FileHandling.Kept, FileKeeping.Read(Path.Combine(_root, "not-there"), null));
    }

    [Fact]
    public void The_guard_refuses_files_in_a_kept_folder_and_allows_rpm_data_and_new_files()
    {
        var song = Path.Combine(_root, "Artist", "Album", "01 Song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(song)!);
        File.WriteAllText(song, "x");

        Assert.False(KeptFiles.Covers(song));
        KeptFiles.Open(_root);

        Assert.True(KeptFiles.Covers(song));
        Assert.True(KeptFiles.Covers(Path.Combine(_root, "Artist")));
        Assert.True(KeptFiles.Covers(_root));
        Assert.Throws<KeptFileException>(() => KeptFiles.Refuse(song, "written"));
        Assert.Throws<KeptFileException>(() => KeptFiles.RefuseReplace(song, "replaced"));

        Assert.False(KeptFiles.Covers(Path.Combine(_root, ".rpm", "library.db")));
        Assert.False(KeptFiles.Covers(Path.Combine(_root, "Artist", "Album", ".rpm-copy-1234.flac")));
        KeptFiles.RefuseReplace(Path.Combine(_root, "New Artist", "New Album", "01.flac"), "created");

        // A sibling folder whose name starts the same is not inside the kept one.
        Assert.False(KeptFiles.Covers(_root + "-old" + Path.DirectorySeparatorChar + "song.flac"));

        KeptFiles.Close(_root);
        Assert.False(KeptFiles.Covers(song));
        KeptFiles.Refuse(song, "written");
    }

    [Fact]
    public void Derived_ids_are_stable_across_systems_and_differ_per_library()
    {
        var mac = KeptIds.For("aaaa", "Björk/Debut/01 Human Behaviour.flac");
        var windows = KeptIds.For("aaaa", "Björk\\Debut\\01 Human Behaviour.flac");
        Assert.Equal(mac, windows);
        Assert.True(Guid.TryParseExact(mac, "D", out var guid));
        Assert.Equal(8, guid.Version);

        Assert.NotEqual(mac, KeptIds.For("bbbb", "Björk/Debut/01 Human Behaviour.flac"));
        Assert.NotEqual(mac, KeptIds.For("aaaa", "Björk/Debut/02 Crying.flac"));
        Assert.Equal(mac, KeptIds.For("aaaa", "Björk/Debut/01 Human Behaviour.flac"));
    }
}

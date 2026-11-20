using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The startup reads the library's file into the system's cache before SQLite opens it. The read must
/// touch only a database on this computer's own disk, must never create anything, and must have stopped
/// before the file is opened, because closing a handle in the same process drops SQLite's locks.
/// </summary>
public class MirrorReadAheadTests : IDisposable
{
    private readonly string _root;

    public MirrorReadAheadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmahead-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        MirrorReadAhead.Finish();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_folder_with_no_database_answers_nothing_and_is_left_as_it_was()
    {
        Assert.Null(MirrorLocation.OwnDiskDatabase(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void A_folder_that_is_not_there_answers_nothing()
    {
        Assert.Null(MirrorLocation.OwnDiskDatabase(Path.Combine(_root, "gone")));
        Assert.Null(MirrorLocation.OwnDiskDatabase(""));
    }

    [Fact]
    public void A_folder_with_a_database_answers_the_file_the_library_opens()
    {
        using (new LibraryDatabase(MirrorLocation.InFolder(_root))) { }

        Assert.Equal(MirrorLocation.InFolder(_root).DatabasePath, MirrorLocation.OwnDiskDatabase(_root));
    }

    [Fact]
    public void The_library_opens_and_works_after_a_read_ahead()
    {
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
            db.Upsert(Track("a.flac"));

        MirrorReadAhead.Start(_root);

        // The constructor stops the read before it opens the file.
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(_root)))
        {
            db.Upsert(Track("b.flac"));
            Assert.Equal(2, db.Query().Count);
        }
    }

    [Fact]
    public void Finish_returns_when_nothing_was_started()
    {
        MirrorReadAhead.Finish();
        MirrorReadAhead.Finish();
    }

    [Fact]
    public void A_read_ahead_of_a_folder_with_no_database_creates_nothing()
    {
        MirrorReadAhead.Start(_root);
        MirrorReadAhead.Finish();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    private static LibraryTrack Track(string path) => new()
    {
        RelativePath = path,
        Folder = "",
        FileName = path,
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        DurationSeconds = 240,
    };
}

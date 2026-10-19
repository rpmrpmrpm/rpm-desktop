using Microsoft.Data.Sqlite;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// A read of the collection that fails is never an empty answer.
///
/// <para>A failed read must not return an empty list. If it did, a database that stopped answering would
/// draw an empty wall, an empty genre page and an empty album band. Those say "you have no music".
/// <see cref="LibraryService.Read{T}"/> is the one way a screen reads a collection. These tests check what
/// each kind of failure means, and which mirror it must come from before the problem screen is
/// shown.</para>
/// </summary>
public class LibraryReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rpm-read-{Guid.NewGuid():N}");
    private readonly List<LibraryDatabase> _extra = [];

    private readonly LibraryService _library =
        new(TestSessions.SignedOut());

    public LibraryReadTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "own"));
        _library.Open(Path.Combine(_root, "own"));
    }

    public void Dispose()
    {
        _library.Dispose();
        foreach (var db in _extra)
            db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
        GC.SuppressFinalize(this);
    }

    private LibraryDatabase Another(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var db = new LibraryDatabase(MirrorLocation.InFolder(folder));
        _extra.Add(db);
        return db;
    }

    /// <summary>What SQLite raises for a damaged file. The code is SQLITE_CORRUPT.</summary>
    private static SqliteException Malformed() => new("database disk image is malformed", 11);

    [Fact]
    public void An_answer_is_handed_back_and_nothing_is_wrong()
    {
        var asked = _library.Read(_library.Db, "a count", db => db.CountAlbumFolders(TrackScope.Anywhere));

        Assert.True(asked.Answers(out var albums, out _));
        Assert.Equal(0, albums);
        Assert.Null(_library.Trouble);
    }

    [Fact]
    public void A_refusal_of_the_own_mirror_is_trouble_and_raised_once()
    {
        var raised = 0;
        _library.Troubled += () => raised++;

        var first = _library.Read<int>(_library.Db, "a count", _ => throw Malformed());
        var second = _library.Read<int>(_library.Db, "another count", _ => throw Malformed());

        Assert.IsType<Asked<int>.Unanswered>(first);
        Assert.IsType<Asked<int>.Unanswered>(second);
        var failed = Assert.IsType<LibraryOpening.Trouble.ReadFailed>(_library.Trouble);
        Assert.Equal(_library.Root, failed.Folder);
        Assert.Contains("malformed", failed.Detail);

        // The screen left up over a broken mirror keeps reading it. One problem screen, not one per read.
        Assert.Equal(1, raised);
    }

    /// <summary>The disk went away while the app was running. The screen shown is the same as at
    /// launch.</summary>
    [Fact]
    public void A_refusal_with_the_folder_gone_is_a_missing_folder()
    {
        var unplugged = Path.Combine(_root, "unplugged");
        Directory.CreateDirectory(unplugged);
        _library.Open(unplugged);
        var mirror = _library.Db;

        // The mirror is closed before the folder is deleted. Windows will not delete a folder while SQLite
        // holds a file open. (macOS will.) The read below throws without touching the mirror, so a closed
        // mirror can stand in for the open one. It is still the service's own mirror.
        mirror.Dispose();
        Directory.Delete(unplugged, recursive: true);

        _library.Read<int>(mirror, "a count", _ => throw Malformed());

        var missing = Assert.IsType<LibraryOpening.Trouble.FolderMissing>(_library.Trouble);
        Assert.Equal(unplugged, missing.Folder);
    }

    /// <summary>A mirror closed during the read means the collection is being swapped. That is not a
    /// failure. The real gate throws here, not a stand-in.</summary>
    [Fact]
    public void A_closed_mirror_is_unanswered_and_says_nothing()
    {
        var raised = 0;
        _library.Troubled += () => raised++;
        var closed = Another("closed");
        closed.Dispose();

        var asked = _library.Read(closed, "a count", db => db.CountAlbumFolders(TrackScope.Anywhere));

        Assert.IsType<Asked<int>.Unanswered>(asked);
        Assert.Null(_library.Trouble);
        Assert.Equal(0, raised);
    }

    /// <summary>A defect in the code around the read is not a fact about the collection. It is not caught,
    /// so it reaches a crash report, and it does not become an empty list.</summary>
    [Fact]
    public void Anything_else_the_read_throws_is_not_caught()
    {
        Assert.Throws<NullReferenceException>(() =>
            _library.Read<int>(_library.Db, "a count", _ => throw new NullReferenceException()));
        Assert.Throws<IOException>(() =>
            _library.Read<int>(_library.Db, "a count", _ => throw new IOException("a locked song")));

        Assert.Null(_library.Trouble);
    }

    /// <summary>A refusal that arrives after "Try again" or a folder change has opened another mirror is
    /// about a collection nobody is looking at. It is decided by which mirror failed, not by when the
    /// refusal arrives.</summary>
    [Fact]
    public void A_refusal_from_a_replaced_mirror_changes_nothing()
    {
        var raised = 0;
        _library.Troubled += () => raised++;
        var before = _library.Db;
        _library.Open(Another("next").Root);

        _library.Read<int>(before, "a count", _ => throw Malformed());

        Assert.Null(_library.Trouble);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Opening_a_collection_clears_the_trouble()
    {
        _library.Read<int>(_library.Db, "a count", _ => throw Malformed());
        Assert.NotNull(_library.Trouble);

        _library.Open(_library.Root!);

        Assert.Null(_library.Trouble);
    }

    /// <summary>A borrowed shelf that refuses is put down, and the user's own collection comes back. The
    /// problem screen names the user's own folder, so it is not shown for a borrowed shelf.</summary>
    [Fact]
    public void A_refusal_of_a_borrowed_shelf_puts_it_down()
    {
        var raised = 0;
        _library.Troubled += () => raised++;
        var borrowed = Another("borrowed");
        _library.ViewPeer("peer-device", borrowed);

        _library.Read<int>(_library.Shelf, "a count", _ => throw Malformed());

        Assert.False(_library.IsPeerView);
        Assert.Same(_library.Db, _library.Shelf);
        Assert.Null(_library.Trouble);
        Assert.Equal(0, raised);
    }
}

/// <summary>
/// Refusals from a real database. The database is broken by dropping its tracks table through a second
/// connection while the mirror is open.
///
/// <para>The album wall's index and the artists wall read without a catch around them. So a refusal escaped
/// the rebuild instead of reaching the problem screen. Both now go through Read. About 230 call sites still
/// read or write with nothing around them. The mirror's own connection reports those
/// (<c>MirrorConnection</c>).</para>
/// </summary>
public class MirrorRefusalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rpm-index-{Guid.NewGuid():N}");

    private readonly LibraryService _library =
        new(TestSessions.SignedOut());

    public MirrorRefusalTests()
    {
        Directory.CreateDirectory(_root);
        _library.Open(_root);
    }

    public void Dispose()
    {
        _library.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
        GC.SuppressFinalize(this);
    }

    private void DropTracks() => DropTracks(_root);

    private static void DropTracks(string root)
    {
        using var raw = new SqliteConnection($"Data Source={MirrorLocation.InFolder(root).DatabasePath};Pooling=False");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "DROP TABLE tracks";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_refused_wall_rebuild_is_trouble_and_keeps_the_last_reading()
    {
        using var index = new LibraryAlbumIndex(_library);
        await index.RebuildAsync(null);
        var before = index.Albums;
        Assert.Null(_library.Trouble);

        DropTracks();
        await index.RebuildAsync(null);

        Assert.IsType<LibraryOpening.Trouble.ReadFailed>(_library.Trouble);
        Assert.Same(before, index.Albums);
    }

    [Fact]
    public async Task A_refused_artists_rebuild_is_trouble()
    {
        using var index = new LibraryAlbumIndex(_library);

        DropTracks();
        await index.RebuildArtistsAsync();

        Assert.IsType<LibraryOpening.Trouble.ReadFailed>(_library.Trouble);
    }

    /// <summary>A read with nothing around it. The exception still reaches the caller, and the problem
    /// screen is shown before that happens. The last-chance handler asks <c>Reported</c> to tell this apart
    /// from a crash.</summary>
    [Fact]
    public void A_command_with_nothing_around_it_still_raises_the_trouble()
    {
        DropTracks();

        var thrown = Assert.ThrowsAny<SqliteException>(() => _library.Db.CountAlbumFolders(TrackScope.Anywhere));

        Assert.IsType<LibraryOpening.Trouble.ReadFailed>(_library.Trouble);
        Assert.True(_library.Reported(thrown));
    }

    /// <summary>The mirror reports the refusal as it leaves the command. Then Read's catch sees the same
    /// exception. So there is one problem screen and one log line.</summary>
    [Fact]
    public void A_refusal_seen_by_the_command_and_by_Read_is_counted_once()
    {
        var raised = 0;
        _library.Troubled += () => raised++;
        DropTracks();

        var asked = _library.Read(_library.Db, "a count", db => db.CountAlbumFolders(TrackScope.Anywhere));

        Assert.IsType<Asked<int>.Unanswered>(asked);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_borrowed_shelf_command_with_nothing_around_it_puts_the_shelf_down()
    {
        var folder = Path.Combine(_root, "borrowed");
        Directory.CreateDirectory(folder);
        using var borrowed = new LibraryDatabase(MirrorLocation.InFolder(folder));
        _library.ViewPeer("peer-device", borrowed);
        DropTracks(folder);

        Assert.ThrowsAny<SqliteException>(() => _library.Shelf.CountAlbumFolders(TrackScope.Anywhere));

        Assert.False(_library.IsPeerView);
        Assert.Null(_library.Trouble);
    }

    /// <summary>A broken constraint is caused by the code, not by the file. The collection is still
    /// readable, so there is no problem screen. Read lets the error go, like any other defect.</summary>
    [Fact]
    public void A_constraint_error_is_not_a_refusal()
    {
        var constraint = new SqliteException("UNIQUE constraint failed: tracks.relative_path", 19);

        Assert.Null(MirrorFaults.Of(constraint));
        Assert.Same(constraint, Assert.Throws<SqliteException>(() =>
            _library.Read<int>(_library.Db, "a write", _ => throw constraint)));
        Assert.Null(_library.Trouble);
        Assert.False(_library.Reported(constraint));
    }
}

public class MirrorFaultTests
{
    [Fact]
    public void A_closed_mirror_is_closed() =>
        Assert.Equal(MirrorFault.Closed, MirrorFaults.Of(new ObjectDisposedException("reads")));

    [Fact]
    public void An_sqlite_error_is_broken() =>
        Assert.Equal(MirrorFault.Broken, MirrorFaults.Of(new SqliteException("disk I/O error", 10)));

    /// <summary>The mirror touches no file except its own, and SQLite reports its own disk errors as
    /// SqliteException. So an IOException in a read is about some other file.</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    public void Anything_else_is_not_the_mirror(Type type) =>
        Assert.Null(MirrorFaults.Of((Exception)Activator.CreateInstance(type)!));
}

public class FolderChangeTests
{
    [Theory]
    [InlineData(FolderChange.NotEmpty, "Settings_MusicFolderNotEmpty")]
    [InlineData(FolderChange.Overlaps, "Settings_MusicFolderOverlaps")]
    [InlineData(FolderChange.Failed, "Settings_MusicFolderFailed")]
    [InlineData(FolderChange.Cancelled, null)]
    [InlineData(FolderChange.Opened, null)]
    public void Only_a_change_that_did_nothing_says_why(FolderChange change, string? key) =>
        Assert.Equal(key, change.StatusKey());
}

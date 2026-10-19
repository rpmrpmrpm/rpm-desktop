using RPM.Core.Library;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// Changing the music folder while the app is running. It is one button on the library settings page,
/// but it changes the whole collection.
/// </summary>
public class LibrarySwapTests : IDisposable
{
    private readonly string _first;
    private readonly string _second;
    private readonly string _borrowed;
    private readonly LibraryService _library = new(TestSessions.SignedOut());
    private readonly LibraryDatabase _shelf;

    public LibrarySwapTests()
    {
        _first = Fresh("first");
        _second = Fresh("second");
        _borrowed = Fresh("borrowed");
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_borrowed));
    }

    private static string Fresh(string what)
    {
        var path = Path.Combine(Path.GetTempPath(), $"swap-{what}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        _library.Dispose();
        _shelf.Dispose();
        foreach (var path in new[] { _first, _second, _borrowed })
            try { Directory.Delete(path, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Opening_another_folder_moves_the_collection_and_says_so()
    {
        var opened = new List<string>();
        _library.Open(_first);
        _library.Opened += opened.Add;

        _library.Open(_second);

        Assert.Equal(_second, _library.Root);
        Assert.Equal(_second, _library.Db.Root);
        Assert.Equal([_second], opened);
    }

    [Fact]
    public void The_collection_that_was_open_is_closed_behind_it()
    {
        _library.Open(_first);
        var first = _library.Db;

        _library.Open(_second);

        // The old collection is closed on a background thread, so it is not closed when Open returns. These
        // tests check that it does close, and that the new collection works the whole time.
        Assert.NotSame(first, _library.Db);
        Assert.Equal(0, _library.Db.Count);

        var closed = SpinWait.SpinUntil(
            () => { try { first.Query(); return false; } catch (ObjectDisposedException) { return true; } },
            TimeSpan.FromSeconds(5));
        Assert.True(closed, "the collection that was replaced was never closed");
    }

    [Fact]
    public void A_folder_that_cannot_be_opened_leaves_the_collection_as_it_was()
    {
        _library.Open(_first);
        var first = _library.Db;

        // A file where the mirror's folder has to go: creating .rpm under it cannot succeed.
        var blocked = Path.Combine(Path.GetTempPath(), $"swap-blocked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, ".rpm"), "not a folder");

        try
        {
            Assert.ThrowsAny<Exception>(() => _library.Open(blocked));

            // The app must still be on the old collection, and that collection must still answer. The old
            // mirror must be closed only after the new one is built. If it were closed first, every read
            // would throw ObjectDisposedException until the app was restarted.
            Assert.Same(first, _library.Db);
            Assert.Equal(_first, _library.Root);
            Assert.Empty(_library.Query());
        }
        finally
        {
            try { Directory.Delete(blocked, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_borrowed_shelf_is_put_down_when_the_collection_changes()
    {
        _library.Open(_first);
        _library.ViewPeer("THEIRS", _shelf);

        _library.Open(_second);

        // The shelf on screen belonged to the collection that was closed. It is dropped, not disposed,
        // because the peer store owns it and closes it.
        Assert.False(_library.IsPeerView);
        Assert.Null(_library.ViewingPeer);
        Assert.Same(_library.Db, _library.Shelf);
        Assert.Equal(_second, _library.ShelfRoot);
        Assert.Empty(_shelf.Query());   // still open, and still the peer store's
    }
}

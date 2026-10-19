using RPM.Core.Library;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// Looking at somebody else's collection, and what that must not move.
///
/// <para> A borrowed shelf is never put in place of the user's own mirror. Everything in the app that
/// writes anything uses that mirror: the import, the sync, the cloud, the analysis passes, and the play
/// counts. None of them asks whose collection is on screen, and none of them should have to. If a cloud
/// mirror pass ran while a friend's shelf was open, it would write the user's own rows into the friend's
/// database.
/// </para>
///
/// <para>These tests check the seam between the two sides. The read side follows the shelf, and the write
/// side never moves.</para>
/// </summary>
public class BorrowedShelfTests : IDisposable
{
    private readonly string _mine;
    private readonly string _theirs;
    // No session: the mirror is opened as a signed-out app opens it. These tests are about that case
    // anyway, and every row in them is a file on disk.
    private readonly LibraryService _library = new(TestSessions.SignedOut());
    private readonly LibraryDatabase _shelf;

    public BorrowedShelfTests()
    {
        _mine = Path.Combine(Path.GetTempPath(), $"own-{Guid.NewGuid():N}");
        _theirs = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mine);
        Directory.CreateDirectory(_theirs);

        _library.Open(_mine);
        _shelf = new LibraryDatabase(MirrorLocation.InFolder(_theirs));
    }

    public void Dispose()
    {
        _library.Dispose();
        _shelf.Dispose();
        try { Directory.Delete(_mine, recursive: true); } catch { }
        try { Directory.Delete(_theirs, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_collection_that_is_written_to_is_never_the_borrowed_one()
    {
        _library.ViewPeer("THEIRS", _shelf);

        Assert.True(_library.IsPeerView);
        Assert.Equal("THEIRS", _library.ViewingPeer);

        // What everything that writes holds.
        Assert.Equal(_mine, _library.Root);
        Assert.Same(_library.Db, _library.Db);
        Assert.NotSame(_shelf, _library.Db);

        // What everything that draws holds.
        Assert.Same(_shelf, _library.Shelf);
        Assert.Equal(_theirs, _library.ShelfRoot);
    }

    [Fact]
    public void Coming_back_out_puts_the_shelf_back_to_this_ones_own()
    {
        _library.ViewPeer("THEIRS", _shelf);
        _library.ViewOwn();

        Assert.False(_library.IsPeerView);
        Assert.Null(_library.ViewingPeer);
        Assert.Same(_library.Db, _library.Shelf);
        Assert.Equal(_mine, _library.ShelfRoot);
    }

    [Fact]
    public void Opening_a_borrowed_shelf_is_not_a_library_being_opened()
    {
        // Opened means "the collection this machine keeps is now that folder". The inbox watcher and the
        // sharing server re-aim when this happens. If a borrowed shelf were followed, an empty listing
        // would be offered to the network for as long as the user looked at it.
        var opened = new List<string?>();
        _library.Opened += root => opened.Add(root);

        _library.ViewPeer("THEIRS", _shelf);
        _library.ViewOwn();

        Assert.Empty(opened);
    }

    [Fact]
    public void What_is_drawn_follows_the_shelf_and_what_is_counted_does_not()
    {
        _library.Db.UpsertMany([Row("Mine/Album/01 Mine.flac", "Mine")]);
        _shelf.UpsertMany([Row("Theirs/Album/01 Theirs.flac", "Theirs")]);

        _library.ViewPeer("THEIRS", _shelf);

        Assert.Equal("Theirs", Assert.Single(_library.ShelfQuery()).Title);
        Assert.Equal("Mine", Assert.Single(_library.Query()).Title);
        Assert.Equal(1, _library.Count);
    }

    /// <summary>
    /// A borrowed record says which computer it came from in its own path.
    ///
    /// <para>The path is what lets the queue and the information pane find the one collection that knows
    /// the record. A borrowed record has no file on this disk to read tags from, and its row is not in the
    /// user's own mirror. Once the user switches back, it is not in the shelf on screen either. Without the
    /// path, the album behind a borrowed track would be unknown. The track would play alone, and the pane
    /// would have only a file name to show.</para>
    /// </summary>
    [Fact]
    public void A_borrowed_record_names_the_computer_it_came_from_in_its_path()
    {
        var path = Path.Combine(
            RPM.Desktop.Services.Sharing.PeerLibraryStore.RootFor("THEIRS"),
            "Band", "Record", "01 One.flac");

        Assert.Equal("THEIRS", RPM.Desktop.Services.Sharing.PeerLibraryStore.DeviceIdFor(path));
    }

    [Fact]
    public void A_record_that_is_not_on_a_borrowed_shelf_names_nobody()
    {
        Assert.Null(RPM.Desktop.Services.Sharing.PeerLibraryStore.DeviceIdFor(
            Path.Combine(Path.GetTempPath(), "Music", "Band", "Record", "01 One.flac")));

        Assert.Null(RPM.Desktop.Services.Sharing.PeerLibraryStore.DeviceIdFor(""));
    }

    private static LibraryTrack Row(string relative, string title) => new()
    {
        RelativePath = relative,
        Folder = relative[..relative.LastIndexOf('/')],
        FileName = relative[(relative.LastIndexOf('/') + 1)..],
        Extension = ".flac",
        Title = title,
        Artist = "Band",
        Album = "Album",
    };
}

/// <summary>
/// Music that ended up inside a borrowed shelf's folder.
///
/// <para>Music must not be filed under a borrowed shelf's folder, and the import's destination must never
/// be that folder. If it were, a record imported while a friend's collection was on screen would land in a
/// folder that is deleted whole, recursively and without a recycle bin, when that computer is forgotten.
/// That could destroy the user's only copy of an album.</para>
/// </summary>
public class StrandedMusicTests : IDisposable
{
    private readonly string _library;
    private readonly string _peers;

    public StrandedMusicTests()
    {
        _library = Path.Combine(Path.GetTempPath(), $"lib-{Guid.NewGuid():N}");
        _peers = Path.Combine(Path.GetTempPath(), $"peers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_library);
        Directory.CreateDirectory(_peers);
    }

    public void Dispose()
    {
        try { Directory.Delete(_library, recursive: true); } catch { }
        try { Directory.Delete(_peers, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Stranded(string relative)
    {
        var path = Path.Combine(_peers, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio");
        return path;
    }

    [Fact]
    public void A_record_filed_into_a_borrowed_shelf_is_carried_out_to_the_inbox()
    {
        var one = Stranded("PEER1/library/Kanye West/BULLY (Deluxe)/01 - King.flac");
        var two = Stranded("PEER1/library/Kanye West/BULLY (Deluxe)/02 - Preacher Man.flac");

        Assert.Equal(2, RPM.Desktop.Services.Sharing.PeerLibraryStore.Rescue(_library, _peers));

        Assert.False(File.Exists(one));
        Assert.False(File.Exists(two));

        var inbox = RPM.Core.Library.LibraryInbox.PathFor(_library);
        Assert.True(File.Exists(Path.Combine(inbox, "01 - King.flac")));
        Assert.True(File.Exists(Path.Combine(inbox, "02 - Preacher Man.flac")));
    }

    [Fact]
    public void Two_records_with_the_same_file_name_both_come_out()
    {
        Stranded("PEER1/library/A/Record/01 - Track.flac");
        Stranded("PEER2/library/B/Record/01 - Track.flac");

        Assert.Equal(2, RPM.Desktop.Services.Sharing.PeerLibraryStore.Rescue(_library, _peers));

        var inbox = RPM.Core.Library.LibraryInbox.PathFor(_library);
        Assert.True(File.Exists(Path.Combine(inbox, "01 - Track.flac")));
        Assert.True(File.Exists(Path.Combine(inbox, "01 - Track (2).flac")));
    }

    [Fact]
    public void What_a_borrowed_shelf_is_actually_made_of_is_left_alone()
    {
        // Rows and cached sleeves. Carrying those out would fill the user's inbox with a friend's
        // album art on every launch.
        var db = Stranded("PEER1/library/.rpm/library.db");
        var art = Stranded("PEER1/art/9928e213e1574cfc1226faabbd71b462.img");

        Assert.Equal(0, RPM.Desktop.Services.Sharing.PeerLibraryStore.Rescue(_library, _peers));

        Assert.True(File.Exists(db));
        Assert.True(File.Exists(art));
    }
}

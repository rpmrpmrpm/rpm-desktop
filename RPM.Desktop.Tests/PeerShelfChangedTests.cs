using RPM.Core.Library;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// A write to a borrowed shelf's own database is announced only while that shelf is on screen.
///
/// <para>A pull from another computer writes to that computer's borrowed shelf. If that write were
/// announced as a change to the user's own collection, home would re-read it and show its loader for
/// about a second after every launch. See <see cref="LibraryService.NotifyPeerShelfChanged"/>.</para>
/// </summary>
public class PeerShelfChangedTests : IDisposable
{
    private const string Friend = "FRIEND-ON-SCREEN";

    private readonly string _root;
    private readonly string _friendRoot;
    private readonly LibraryService _library = new(TestSessions.SignedOut());
    private readonly LibraryDatabase _friendShelf;
    private int _raised;

    public PeerShelfChangedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"own-{Guid.NewGuid():N}");
        _friendRoot = Path.Combine(Path.GetTempPath(), $"friend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_friendRoot);

        _library.Open(_root);
        _friendShelf = new LibraryDatabase(MirrorLocation.InFolder(_friendRoot));

        // After Open, which raises once for itself.
        _library.Changed += () => Interlocked.Increment(ref _raised);
    }

    public void Dispose()
    {
        _library.Dispose();
        _friendShelf.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_friendRoot, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Not_announced_while_the_users_own_collection_is_on_screen()
    {
        _library.NotifyPeerShelfChanged(Friend);

        Assert.Equal(0, _raised);
    }

    [Fact]
    public void Announced_while_that_shelf_is_on_screen()
    {
        _library.ViewPeer(Friend, _friendShelf);
        _raised = 0;

        _library.NotifyPeerShelfChanged(Friend);

        Assert.Equal(1, _raised);
    }

    [Fact]
    public void Not_announced_while_another_borrowed_shelf_is_on_screen()
    {
        _library.ViewPeer("SOMEBODY-ELSE", _friendShelf);
        _raised = 0;

        _library.NotifyPeerShelfChanged(Friend);

        Assert.Equal(0, _raised);
    }

    [Fact]
    public void Not_announced_after_going_back_to_the_users_own_collection()
    {
        _library.ViewPeer(Friend, _friendShelf);
        _library.ViewOwn();
        _raised = 0;

        _library.NotifyPeerShelfChanged(Friend);

        Assert.Equal(0, _raised);
    }
}

using RPM.Desktop.Presentation.Common;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// Every test class that points <see cref="PlaylistCoverStore.RootOverride"/> at a folder of its own,
/// serialised.
///
/// <para><see cref="PlaylistCoverStore.RootOverride"/> is one process-global static. This project has no
/// <c>xunit.runner.json</c>, no <c>[CollectionBehavior]</c>, and no <c>[Collection]</c> by default. So
/// every test class is its own collection, and xunit runs collections in parallel. If two classes each
/// hold the override during their own tests, one class's <c>Dispose</c> can set it to null in the middle
/// of the other class's write. Each read in that moment resolves to the real <c>LocalData.Dir</c>. The
/// covers it finds, or fails to find, there belong to neither test.</para>
///
/// <para>Every test class that sets <c>RootOverride</c> belongs in this one collection. The collection is
/// named for the seam, not for any one test class. So xunit runs them one after another instead of racing
/// them.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class PlaylistCoverRootCollection
{
    /// <summary>The collection's name. Each class writes
    /// <c>[Collection(PlaylistCoverRootCollection.Name)]</c>,
    /// so no class has to retype a string literal that must match exactly.</summary>
    public const string Name = "PlaylistCoverStore root override";
}

[Collection(PlaylistCoverRootCollection.Name)]
public class PlaylistCoverStoreTests : IDisposable
{
    private static readonly byte[] Cover = [1, 2, 3, 4];
    private static readonly byte[] Wash = [9, 9];

    // Two separate own-library roots, standing in for two local libraries that one install can open.
    // SegmentOf reads only the root's own text, so a real LibraryDatabase is not needed to test it.
    private const string LibraryA = "/Users/test/MusicA";
    private const string LibraryB = "/Users/test/MusicB";

    private static string Own(string root) => PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, root, null)!;
    private static string Peer(string id) => PlaylistCoverStore.SegmentOf(PlaylistShelf.Borrowed, null, id)!;

    /// <summary>A folder of this test's own, never the real <c>LocalData.Dir</c>. See
    /// <see cref="PlaylistCoverStore.RootOverride"/> for why. A test that deletes what the developer
    /// actually has stored is a worse bug than the one it is meant to catch.</summary>
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rpm-covers-" + Guid.NewGuid().ToString("N"));

    public PlaylistCoverStoreTests()
    {
        Directory.CreateDirectory(_root);
        PlaylistCoverStore.RootOverride = _root;
    }

    public void Dispose()
    {
        PlaylistCoverStore.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void What_is_written_is_what_comes_back()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(7, own, Cover, Wash);

        var (cover, wash) = PlaylistCoverStore.Read(7, own);
        Assert.Equal(Cover, cover);
        Assert.Equal(Wash, wash);
    }

    [Fact]
    public void A_playlist_with_no_master_reads_as_nothing()
        => Assert.Null(PlaylistCoverStore.Read(4242, Own(LibraryA)).Cover);

    // Every library numbers its playlists from one, so a friend's third list and the listener's third list
    // are both node 3. If the cache were keyed on the id alone, whichever cover was written first would be
    // shown for both.
    [Fact]
    public void The_same_node_number_on_two_machines_is_two_pictures()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(3, own, Cover, null);
        PlaylistCoverStore.Write(3, Peer("abc123"), [7, 7, 7], null);

        Assert.Equal(Cover, PlaylistCoverStore.Read(3, own).Cover);
        Assert.Equal(new byte[] { 7, 7, 7 }, PlaylistCoverStore.Read(3, Peer("abc123")).Cover);
    }

    // Two local libraries also number their playlists from one. The store's root is per installation, not
    // per library. Without a segment for the own tree, opening library B showed library A's chosen cover on
    // B's playlist 3.
    [Fact]
    public void The_same_node_number_on_two_local_libraries_is_two_pictures()
    {
        PlaylistCoverStore.Write(3, Own(LibraryA), Cover, null);
        PlaylistCoverStore.Write(3, Own(LibraryB), [7, 7, 7], null);

        Assert.Equal(Cover, PlaylistCoverStore.Read(3, Own(LibraryA)).Cover);
        Assert.Equal(new byte[] { 7, 7, 7 }, PlaylistCoverStore.Read(3, Own(LibraryB)).Cover);
    }

    // Safe("..") and Safe("///") both sanitise to "". So a borrowed peer whose id is all punctuation cannot
    // be answered. SegmentOf returns null rather than falling back to anybody's own folder. A store call
    // given that segment is a no-op, and it never touches the own folder.
    [Fact]
    public void A_peer_id_with_no_alphanumerics_never_reaches_the_users_own_folder()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(3, own, Cover, null);

        Assert.Null(PlaylistCoverStore.SegmentOf(PlaylistShelf.Borrowed, null, ".."));
        Assert.Null(PlaylistCoverStore.SegmentOf(PlaylistShelf.Borrowed, null, "///"));

        PlaylistCoverStore.Write(3, null, [7, 7, 7], null);

        Assert.Equal(Cover, PlaylistCoverStore.Read(3, own).Cover);
        Assert.Null(PlaylistCoverStore.Read(3, null).Cover);
    }

    // Nothing is stored at the bare root. An own tree asked about with no library open cannot be named, so
    // it gets the same "no picture" answer as an unnamed peer.
    [Fact]
    public void An_own_tree_with_no_library_open_has_no_segment()
        => Assert.Null(PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, null, null));

    [Fact]
    public void Forgetting_takes_the_wash_with_the_cover()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(8, own, Cover, Wash);
        PlaylistCoverStore.Forget(8, own);

        var (cover, wash) = PlaylistCoverStore.Read(8, own);
        Assert.Null(cover);
        Assert.Null(wash);
    }

    [Fact]
    public void The_sweep_keeps_the_living_and_drops_the_rest()
    {
        PlaylistCoverStore.Write(1, Own(LibraryA), Cover, null);
        PlaylistCoverStore.Write(2, Own(LibraryA), Cover, null);
        PlaylistCoverStore.Write(5, Peer("gone"), Cover, null);
        PlaylistCoverStore.Write(5, Peer("here"), Cover, null);

        PlaylistCoverStore.Sweep([1], ["here"], LibraryA);

        Assert.NotNull(PlaylistCoverStore.Read(1, Own(LibraryA)).Cover);
        Assert.Null(PlaylistCoverStore.Read(2, Own(LibraryA)).Cover);
        Assert.Null(PlaylistCoverStore.Read(5, Peer("gone")).Cover);
        // A borrowed shelf belongs to someone else. This machine cannot tell which of their playlists still
        // exist, so a peer still in the address book keeps everything under it.
        Assert.NotNull(PlaylistCoverStore.Read(5, Peer("here")).Cover);
    }

    // Sweeping library A must never touch what library B has on disk. Sweep has no registry of every own
    // library on this machine to check B's files against. Without that check, a sweep could silently bring
    // back the collision between the two libraries.
    [Fact]
    public void Sweeping_one_library_leaves_a_different_library_untouched()
    {
        PlaylistCoverStore.Write(9, Own(LibraryA), Cover, null);
        PlaylistCoverStore.Write(9, Own(LibraryB), [7, 7, 7], null);

        // From library A's point of view, node 9 no longer exists.
        PlaylistCoverStore.Sweep([], [], LibraryA);

        Assert.Null(PlaylistCoverStore.Read(9, Own(LibraryA)).Cover);
        Assert.Equal(new byte[] { 7, 7, 7 }, PlaylistCoverStore.Read(9, Own(LibraryB)).Cover);
    }

    // The age gate. The test backdates the file rather than sleeping. A .tmp younger than TmpAgeGate may be
    // a write still in flight, and Sweep must leave it alone. One older than the gate is an orphan from a
    // process that died mid-write, and Sweep removes it.
    [Fact]
    public void The_sweep_leaves_a_fresh_tmp_alone_but_drops_a_stale_one()
    {
        var dir = Path.Combine(PlaylistCoverStore.Root, Own(LibraryA));
        Directory.CreateDirectory(dir);

        var fresh = Path.Combine(dir, "1.jpg.tmp");
        var stale = Path.Combine(dir, "2.jpg.tmp");
        File.WriteAllBytes(fresh, [1]);
        File.WriteAllBytes(stale, [1]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - PlaylistCoverStore.TmpAgeGate - TimeSpan.FromMinutes(1));

        PlaylistCoverStore.Sweep([], [], LibraryA);

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(stale));
    }

    // Read returns null both for "nothing chosen" and for "could not tell". That is fine for drawing, but
    // wrong for a cloud push. A push must not read a temporary I/O fault as "the user removed their
    // picture" and delete it. ReadForPush tells the three states apart. The safety of PushPlaylistAsync's
    // switch depends on this being true.

    [Fact]
    public void ReadForPush_reports_a_stored_master_as_present()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(11, own, Cover, null);

        var present = Assert.IsType<PlaylistCoverStore.MasterRead.Present>(
            PlaylistCoverStore.ReadForPush(11, own));
        Assert.Equal(Cover, present.Cover);
    }

    [Fact]
    public void ReadForPush_reports_no_master_as_absent()
        => Assert.IsType<PlaylistCoverStore.MasterRead.Absent>(
            PlaylistCoverStore.ReadForPush(4242, Own(LibraryA)));

    // A master that exists but cannot be read right now must return Unreadable, never Absent. A caller
    // turns Absent into a cloud DELETE, so this case must never look like "nothing chosen".
    [Fact]
    public void A_master_that_exists_but_will_not_read_is_unreadable_not_absent()
    {
        var own = Own(LibraryA);
        PlaylistCoverStore.Write(12, own, Cover, null);

        var path = Path.Combine(PlaylistCoverStore.Root, own, "12.jpg");

        // FileShare.None makes the next read of this file fail with a sharing violation. This stands in for
        // a permissions blip or disk fault, which ReadForPush must tell apart from an empty folder. The
        // test does not need to break the disk.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsType<PlaylistCoverStore.MasterRead.Unreadable>(
                PlaylistCoverStore.ReadForPush(12, own));
        }

        // Once the lock is released, the same call reads the master normally. Unreadable is a result for
        // this one attempt, not a mark left on the file.
        var present = Assert.IsType<PlaylistCoverStore.MasterRead.Present>(
            PlaylistCoverStore.ReadForPush(12, own));
        Assert.Equal(Cover, present.Cover);
    }

    // A zero-byte file at the master path is not Absent either. This is a third case of the same bug. Write
    // never creates one, because it checks cover.Length == 0 before it opens a handle. So an empty file
    // there is a state this store cannot explain. It could come from a crash between allocation and flush,
    // a quarantine, or an external truncation. It is not proof that nothing was chosen. Reading it as
    // Absent would delete a cloud cover based on a file the store never wrote.
    [Fact]
    public void A_zero_byte_master_is_unreadable_not_absent()
    {
        var own = Own(LibraryA);
        var dir = Path.Combine(PlaylistCoverStore.Root, own);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "13.jpg"), []);

        Assert.IsType<PlaylistCoverStore.MasterRead.Unreadable>(
            PlaylistCoverStore.ReadForPush(13, own));
    }
}

using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Library;

/// <summary>
/// Removes playlist covers that nothing uses, for whichever library tree is open.
///
/// <para>Every library numbers its playlists from one. <see cref="PlaylistCovers"/> documents this for a
/// borrowed shelf, which can share a node id with the listener's own tree. It is equally true for a second
/// local library, because the store's folder sits beside the settings: one folder per install, not one per
/// library. Once a tree has a folder of its own (see <see cref="PlaylistCoverStore.SegmentOf"/>), the tree
/// that has just come to the front is the one whose unused covers can be told apart from a choice that
/// somebody made.</para>
///
/// <para>This runs on <see cref="LibraryService.Opened"/>, because that is the moment a different tree
/// comes to the front. A sweep has to know which tree is open before it can tell "removed from this
/// list" apart from "never was on this list". A tree-change notification fires far more often and does
/// not say which tree is open. A timer does not say either.</para>
///
/// <para>The sweep runs off the UI thread. <c>Opened</c> is raised inline at the end of
/// <c>LibraryService.Open</c>, and every caller of <c>Open</c> is on the UI thread. A sweep run there
/// would block the UI on a SQLite read, a directory listing and any number of deletes. Nothing after
/// <c>Open</c> depends on the sweep having finished. <see cref="PlaylistCoverStore.Sweep"/> removes an
/// own-tree master only when its node id is missing from the set of living nodes it is given. A cover
/// drawn after the sweep starts belongs to a node that is in that set, sweep or no sweep. The race on
/// <c>*.tmp</c> files is handled by the age check in <see cref="PlaylistCoverStore.TmpAgeGate"/>, not by
/// timing.</para>
///
/// <para><b>Switching libraries quickly causes a second race. It is avoided by capturing the values, not by
/// reading them live.</b> <see cref="LibraryService.Open"/> swaps its database under its own gate. A second
/// Open can land while a queued sweep is still waiting for the thread pool. Suppose library A opens and its
/// sweep is queued, and then the user switches to library B before the queued work runs. A sweep that read
/// <c>library.Db</c> and <c>peers.Peers</c> at execution time would walk B's tree while it believed it was
/// working for A's root. No exception would warn of this, because <c>Db</c> returns a different, valid
/// instance either way. So the values are read on the thread that still sees the right library: the thread
/// that <c>Opened</c> fires on, synchronously, before anything goes to <c>Task.Run</c>. See <see
/// cref="Queue"/>, which does this capture.</para>
/// </summary>
public sealed class PlaylistCoverSweepService
{
    public PlaylistCoverSweepService(LibraryService library, PeerBook peers)
    {
        library.Opened += root => Queue(library, peers, root);

        // This service can be created after a library is already open. Without this check, that library
        // would never get its first sweep. InboxWatchService does the same catch-up. This code runs on the
        // UI thread, like the Opened handler above, so the same synchronous capture applies.
        if (library.Root is { } open)
            Queue(library, peers, open);
    }

    /// <summary>
    /// Captures what one sweep needs to name this open. It runs synchronously, before any of the work is
    /// handed to a background thread.
    ///
    /// <para>The database instance is captured here, not read inside the background task. <c>library.Db</c>
    /// can point somewhere else by the time a queued <see cref="Task.Run"/> runs. Reading it late would let
    /// a sweep for root A walk whichever library is open when the task finally runs. Capturing the <see
    /// cref="LibraryDatabase"/> object itself means the background work always reads the database that
    /// matches <paramref name="root"/>, even if <see cref="LibraryService"/> has since moved on or closed
    /// it.</para>
    ///
    /// <para>The peer list is captured the same way, for the same reason, though it matters less. A
    /// snapshot taken now is at most a few seconds old when the sweep runs. A peer paired or removed in
    /// that window is judged against the same staleness that a peer's own tree already has everywhere
    /// else.</para>
    /// </summary>
    private static void Queue(LibraryService library, PeerBook peers, string root)
    {
        var db = library.Db;
        var peerIds = peers.Peers.Select(p => p.DeviceId).ToList();
        _ = Task.Run(() => Run(db, peerIds, root));
    }

    private static void Run(LibraryDatabase db, IReadOnlyList<string> peerIds, string root)
    {
        try
        {
            PlaylistCoverStore.Sweep([.. db.GetPlaylistNodes().Select(n => n.Id)], peerIds, root);
        }
        catch
        {
            // The captured database may be disposed, because its library closed or was replaced before this
            // task ran. Another read may also have failed. Either way this returns without calling Sweep. A
            // library that could not be read means "we did not sweep". It must never mean "we swept and
            // nothing was in use". An empty set of living nodes passed to Sweep would delete every cover in
            // this segment.
        }
    }
}

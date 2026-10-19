using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The other direction: playlists made somewhere else, taken into this machine's own tree.
///
/// <para>
/// The phone can write a playlist now. A playlist written on a phone that never reached a computer would be
/// a playlist you cannot see on the machine where you keep your music. So a sync run reads the cloud
/// listing and takes in whatever no node here claims. This is the same shape the album marks use, one
/// direction at a time. The decision itself is in <see cref="CloudPlaylistIntake"/>, so it can be read and
/// tested without a database or a network.
/// </para>
///
/// <para>
/// Once taken in, this machine owns the playlist. It gets a node with the cloud id written against it. That
/// stops the next run from taking it in again. The ordinary push then keeps the cloud copy in step with the
/// node.
/// </para>
///
/// <para>
/// A playlist already here is nearly always this machine's own last push coming back. Treating that as news
/// would be the machine arguing with itself. What separates the two is the stamp the STORE put on it. Every
/// push records the stamp it was given, so a copy stamped later than that was written by something else.
/// That happens for real, since the phone can reorder a playlist. Only the ORDER is ever taken from it, and
/// only when the two hold exactly the same songs. See <see cref="CloudPlaylistOrder"/> for why a difference
/// in membership is never treated as a reorder.
/// </para>
/// </summary>
public sealed partial class CloudSyncService
{
    /// <summary>How many playlists one run may take in. A person makes one playlist at a time. A listing
    /// that suddenly holds fifty is a first sign-in on a new computer. The rest are taken in on the next
    /// pass, so nobody is kept waiting for their music in the meantime.</summary>
    private const int PlaylistsInPerRun = 20;

    /// <summary>
    /// One pass over the cloud's playlists, taking in the ones this machine has never seen.
    ///
    /// <para>This is called after the tracks have been pulled. That order matters. A playlist is taken in
    /// whole or not at all (see <see cref="PlaylistIntake.Incomplete"/>). So the run that brings down a
    /// phone-made playlist's songs is the run that can take in the playlist itself.</para>
    /// </summary>
    private async Task PullCloudPlaylistsAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;

        IReadOnlyList<CloudPlaylist> theirs;
        try
        {
            theirs = await _cloud.ListPlaylistsAsync(CloudShelf.Own, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the playlists");
            return;
        }

        if (theirs.Count == 0) return;

        // This is one lookup per rpm id, and the results are kept. The alternative would be a set of every
        // id in the library. What is being asked about is the songs these playlists name, which is a few
        // hundred at most. The rows found here are the same rows the items are written from a moment later.
        var found = new Dictionary<string, LibraryTrack?>(StringComparer.Ordinal);
        LibraryTrack? Resolve(string rpmId)
        {
            if (found.TryGetValue(rpmId, out var known)) return known;
            return found[rpmId] = db.FindByRpmId(rpmId);
        }

        var readings = CloudPlaylistIntake.Read(
            theirs, db.PlaylistCloudIds(), db.CloudPlaylistsLetGo(), id => Resolve(id) is not null);

        var taken = 0;
        var waiting = 0;

        foreach (var reading in readings)
        {
            if (ct.IsCancellationRequested) break;

            if (reading.Verdict == PlaylistIntake.Incomplete)
            {
                waiting++;
                continue;
            }

            if (reading.Verdict != PlaylistIntake.Take) continue;
            if (taken >= PlaylistsInPerRun) break;

            TakeIn(db, reading.Playlist, Resolve);
            taken++;
        }

        // This is said even when nothing was taken in. "Waiting for songs" is the state that somebody
        // wonders about when a playlist they made on their phone is not on their computer yet.
        if (waiting > 0)
            _log.LogInformation(
                "[Cloud] {Waiting} playlist(s) not taken in yet: they name songs this library does not have",
                waiting);

        // Playlists this machine already owns, whose cloud copy has moved on since we last wrote it.
        var reordered = await ApplyCloudOrdersAsync(db, theirs, Resolve, ct);

        if (taken == 0 && reordered == 0) return;

        if (taken > 0)
            _log.LogInformation("[Cloud] Took in {Taken} playlist(s) made elsewhere", taken);

        if (reordered > 0)
            _log.LogInformation("[Cloud] Merged {Count} playlist(s) changed elsewhere", reordered);

        // The tree on screen is built from the database. Nothing else would tell it that a node arrived.
        // This is the same call that AddPlaylistToCloudAsync makes, for the same reason. It is safe to call
        // from this thread. PlaylistModel.OnLibraryChanged puts its own reload on the dispatcher, wherever
        // the notice came from.
        _library.NotifyChanged();
    }

    /// <summary>
    /// Reconciles every playlist this machine already owns whose cloud copy has moved on since the copy
    /// this machine last agreed with.
    ///
    /// <para>
    /// <b>This is the one place a playlist is reconciled.</b> The push does not do it, on purpose. See
    /// <c>WritePlaylistOrderAsync</c>. Reconciling means writing songs into the tree. Two places that both
    /// add and remove rows from the same list would end up disagreeing.
    /// </para>
    ///
    /// <para>
    /// Three lists go in: what this machine last published, what it holds now, and what is in the cloud.
    /// <see cref="PlaylistMerge"/> turns them into the one list that both devices should end up with. That
    /// list is applied here, and published back if the other device has not seen it. A song added on a phone
    /// and a song added on this computer both survive. A song that either of them removed stays removed.
    /// </para>
    ///
    /// <para>
    /// The stamp decides whether to look at all. A library upgrading into this has no stamp recorded for
    /// anything. A zero is read as "never recorded", not as "very old". The cloud's current stamp is
    /// adopted as the baseline, and the list is left exactly as it is. Deciding on the first run that every
    /// playlist in the tree has changed elsewhere would be the one way this could go badly wrong at scale.
    /// </para>
    ///
    /// <para>
    /// The stamp is written whether or not anything changed. It says which cloud copy this machine has
    /// SEEN, not which one it agreed with. If it were left unwritten after a refused apply, every pass
    /// would reconsider the same copy forever.
    /// </para>
    /// </summary>
    private async Task<int> ApplyCloudOrdersAsync(
        LibraryDatabase db, IReadOnlyList<CloudPlaylist> theirs, Func<string, LibraryTrack?> resolve,
        CancellationToken ct)
    {
        var held = db.PlaylistCloudStamps();
        var changed = 0;

        foreach (var playlist in theirs)
        {
            if (ct.IsCancellationRequested) break;

            if (!held.TryGetValue(playlist.PlaylistId, out var mine)) continue;

            // Never recorded, or not newer than what we wrote. Either way there is nothing to take.
            if (mine.Stamp == 0 || playlist.UpdatedAt <= mine.Stamp)
            {
                if (mine.Stamp == 0 && playlist.UpdatedAt > 0)
                    db.NotePlaylistPublished(
                        mine.NodeId, playlist.UpdatedAt, playlist.TrackIds,
                        PlaylistCloudState.MetaOf(playlist.Name, playlist.FolderChain));

                continue;
            }

            var items = db.GetPlaylistItems(mine.NodeId);

            // Only songs are considered, and every one of them must be nameable. A playlist that holds
            // another playlist cannot be named by the cloud, because it travels as ids. One of those is
            // enough to make the merge a guess. A guess about which songs somebody keeps is not a guess to
            // make. So the playlist is left alone, and its stamp is still written, so it is not
            // reconsidered forever.
            if (items.Any(item => item.ItemKind != PlaylistItemKind.Song
                               || item.RpmId is not { Length: > 0 }))
            {
                db.SetPlaylistCloudStamp(mine.NodeId, playlist.UpdatedAt);
                _log.LogDebug(
                    "[Cloud] \"{Name}\" holds something the cloud cannot name; left as it is",
                    playlist.Name);
                continue;
            }

            var local = items.Select(item => item.RpmId!).ToList();
            var merged = PlaylistMerge.Songs(mine.Published, local, playlist.TrackIds);

            // Every song in the merge has to be one this library actually holds. Nothing is done until they
            // all are.
            //
            // This is the guard the whole thing depends on. A song added on a phone reaches this machine as
            // an id before its audio does. If it were applied now, its row could not be written. The list
            // here would come out one song short, and the merged list would still be published as what this
            // machine holds. So the NEXT push would read the missing song as a removal and take it out of
            // the cloud. The phone's addition would vanish a minute after it was made, and the sync log
            // would say everything went fine.
            //
            // So the playlist is left entirely alone, stamp included, and tried again on the next pass. The
            // audio is usually down within one pass, because the same run's own pull brings it. This is the
            // same all-or-nothing rule that PlaylistIntake.Incomplete applies to a playlist arriving for
            // the first time.
            var waiting = merged.Count(id => resolve(id) is null);
            if (waiting > 0)
            {
                _log.LogInformation(
                    "[Cloud] \"{Name}\" changed elsewhere but names {Waiting} song(s) this library " +
                    "does not have yet; left as it is",
                    playlist.Name, waiting);
                continue;
            }

            // Applied first, published second. If the write fails, the tree is still right, and the next
            // run publishes it. In the other order, a machine could announce a list it does not hold.
            var moved = ApplyList(db, mine.NodeId, items, merged, resolve);
            if (moved) changed++;

            // The cloud's copy is now what this machine has agreed with, whatever happens to the write
            // below.
            db.NotePlaylistPublished(
                mine.NodeId, playlist.UpdatedAt, playlist.TrackIds,
                PlaylistCloudState.MetaOf(playlist.Name, playlist.FolderChain));

            // There is nothing of ours to tell them. The merge came out as what they already hold.
            if (Same(merged, playlist.TrackIds)) continue;

            try
            {
                var stamped = await _cloud.PutPlaylistAsync(
                    new CloudPlaylist(
                        playlist.PlaylistId, playlist.Name, merged, playlist.FolderChain),
                    ifUnchangedSince: playlist.UpdatedAt, ct);

                if (stamped > 0)
                    db.NotePlaylistPublished(
                        mine.NodeId, stamped, merged,
                        PlaylistCloudState.MetaOf(playlist.Name, playlist.FolderChain));

                _log.LogInformation(
                    "[Cloud] Merged \"{Name}\": {Mine} here and {Theirs} there make {Merged}",
                    playlist.Name, local.Count, playlist.TrackIds.Count, merged.Count);
            }
            catch (PlaylistConflictException)
            {
                // A third write landed while this one was in flight. The tree holds the merge, and the
                // recorded stamp is theirs. So the next pass reads their newest copy as news, and merges
                // again from here, which is the right place to start.
                _log.LogDebug("[Cloud] \"{Name}\" moved again mid-merge; the next run picks it up",
                              playlist.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "[Cloud] Could not publish the merged playlist {Name}", playlist.Name);
            }
        }

        return changed;
    }

    /// <summary>
    /// Makes the tree hold <paramref name="wanted"/>: songs taken out, songs put in, and the whole thing
    /// left in the order given.
    /// </summary>
    /// <returns>Whether anything actually moved.</returns>
    private bool ApplyList(
        LibraryDatabase db, long nodeId, IReadOnlyList<PlaylistItem> items,
        IReadOnlyList<string> wanted, Func<string, LibraryTrack?> resolve)
    {
        var have = items.Select(item => item.RpmId!).ToList();
        if (Same(have, wanted)) return false;

        // Item ids are grouped per rpm id, in the order this library holds them. A song held twice keeps
        // both of its rows. The second name gets the second row.
        var spare = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!spare.TryGetValue(item.RpmId!, out var queue))
                spare[item.RpmId!] = queue = new Queue<long>();

            queue.Enqueue(item.Id);
        }

        var ordered = new List<long>(wanted.Count);
        var missing = new List<LibraryTrack>();

        foreach (var id in wanted)
        {
            if (spare.TryGetValue(id, out var queue) && queue.Count > 0)
            {
                ordered.Add(queue.Dequeue());
                continue;
            }

            // Somebody else put this song in. This library must have the file, or the row would name
            // something it cannot play. The pull brings the audio down in the same run, so a song whose
            // file is not here yet is picked up on the next pass.
            if (resolve(id) is { } track) missing.Add(track);
        }

        // Whatever is left over is what the merge says has gone.
        foreach (var queue in spare.Values)
            while (queue.Count > 0)
                db.RemovePlaylistItem(queue.Dequeue());

        if (missing.Count > 0)
        {
            // AddAnother: the merge has already decided how many copies of each song the list holds. A
            // second copy in the cloud's list is a second copy here.
            db.AddSongsToPlaylist(nodeId, missing.Select(t => ((string?)t.RpmId, t.RelativePath)),
                AlreadyInPlaylist.AddAnother);

            // These rows are the cloud's doing, not the user's. So they are marked wanted and unpinned,
            // exactly as TakeIn marks them. The playlist holds them up there. Nobody picked them one by
            // one, and taking the playlist out of the cloud releases them again.
            db.SetCloudSelected([.. missing.Select(t => t.Id)], selected: true, pinned: false);

            // The ids of the rows just written are not known here. So the order is settled from the list as
            // it now stands. This is the same walk, against items that all exist.
            ordered = Settle(db.GetPlaylistItems(nodeId), wanted);
        }

        if (ordered.Count > 0) db.ReorderPlaylistItems(nodeId, ordered);

        return true;
    }

    /// <summary>The item ids of <paramref name="items"/>, in the order <paramref name="wanted"/> names
    /// them. Anything it does not name is left on the end, not dropped.</summary>
    private static List<long> Settle(IReadOnlyList<PlaylistItem> items, IReadOnlyList<string> wanted)
    {
        var spare = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = item.RpmId ?? "";
            if (!spare.TryGetValue(id, out var queue))
                spare[id] = queue = new Queue<long>();

            queue.Enqueue(item.Id);
        }

        var ordered = new List<long>(items.Count);

        foreach (var id in wanted)
            if (spare.TryGetValue(id, out var queue) && queue.Count > 0)
                ordered.Add(queue.Dequeue());

        foreach (var queue in spare.Values)
            while (queue.Count > 0)
                ordered.Add(queue.Dequeue());

        return ordered;
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;

        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>Writes one cloud playlist into the tree: the folders it was made in, the playlist, its
    /// songs in order, and the cloud id it answers to.</summary>
    private void TakeIn(LibraryDatabase db, CloudPlaylist playlist, Func<string, LibraryTrack?> resolve)
    {
        var parent = db.EnsurePlaylistFolder(playlist.FolderChain);
        var nodeId = db.CreatePlaylist(parent, playlist.Name);

        // Every id resolves, because that is what Take means. So nothing is dropped here, and the order is
        // the order that was written. Items are kept by rpm id, with the relative path as the fallback.
        // Every other way of filling a playlist keys its songs this way too.
        var songs = new List<LibraryTrack>(playlist.TrackIds.Count);
        foreach (var id in playlist.TrackIds)
            if (resolve(id) is { } track)
                songs.Add(track);

        db.AddSongsToPlaylist(nodeId, songs.Select(t => ((string?)t.RpmId, t.RelativePath)),
            AlreadyInPlaylist.AddAnother);   // The cloud's list, copied as it is, duplicates included.

        // The tracks are already up there, or the playlist could not have named them. But this library may
        // not have been the one that put them there. They are marked wanted and unpinned, exactly as
        // AddPlaylistToCloudAsync marks them. The playlist holds them in the cloud, the user did not pick
        // them one by one, and taking the playlist back out later releases them again.
        db.SetCloudSelected([.. songs.Select(t => t.Id)], selected: true, pinned: false);

        // Last, and this line makes the whole thing settle. The node now claims the cloud id. So the next
        // run reads AlreadyHere, and does not take in a second copy. The note saying an older copy of this
        // playlist was let go is also cleared, because the playlist is wanted.
        db.SetPlaylistInCloud(nodeId, playlist.PlaylistId, DateTime.UtcNow.Ticks);

        // The playlist belongs to the account, not to this disk, so signing out takes it with the account.
        // This is the only place a playlist is adopted. Every other way of getting one is somebody making
        // it here.
        db.MarkPlaylistAdopted(nodeId);
    }
}

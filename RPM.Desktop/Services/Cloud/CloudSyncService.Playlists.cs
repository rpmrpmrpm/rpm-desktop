using RPM.Core.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// The playlists. These travel as lists of ids, not as audio. A playlist is pushed up whole whenever it
/// changes, so the phone sees the same running order as this machine.
/// </summary>
public sealed partial class CloudSyncService
{
    public Task AddPlaylistToCloudAsync(long playlistNodeId) =>
        ErrandAsync(_ => AddPlaylistToCloudCoreAsync(playlistNodeId), CancellationToken.None);

    private async Task AddPlaylistToCloudCoreAsync(long playlistNodeId)
    {
        if (!_library.IsOpen) return;
        var db = _library.Db;

        var tracks = db.FlattenPlaylistTracks(playlistNodeId);

        // The id the playlist is stored under. It is kept for the playlist's whole life, so re-syncing
        // replaces what is up there instead of leaving a second copy behind.
        var cloudId = db.GetPlaylistCloud(playlistNodeId).CloudId ?? Guid.NewGuid().ToString("D");

        db.SetCloudSelected([.. tracks.Select(t => t.Id)], selected: true, pinned: false);
        db.SetPlaylistInCloud(playlistNodeId, cloudId, DateTime.UtcNow.Ticks);
        _library.NotifyChanged();

        await PushPlaylistAsync(playlistNodeId, CancellationToken.None);
        await KickAsync();
    }

    /// <summary>
    /// Takes a playlist out of the cloud, and with it every track that was only up there for its sake. A
    /// track the user picked by hand, or one that another synced playlist still plays, stays.
    /// </summary>
    public Task RemovePlaylistFromCloudAsync(long playlistNodeId) =>
        ErrandAsync(_ => RemovePlaylistFromCloudCoreAsync(playlistNodeId), CancellationToken.None);

    private async Task RemovePlaylistFromCloudCoreAsync(long playlistNodeId)
    {
        if (!_library.IsOpen) return;
        var db = _library.Db;

        var stored = db.GetPlaylistCloud(playlistNodeId);
        var tracks = db.FlattenPlaylistTracks(playlistNodeId);

        MarkLeaving(null, playlistNodeId, leaving: true);
        try
        {
            // The playlist is removed from the books first. What the playlist itself holds must not count
            // as a reason to keep its own tracks. A failed delete below should also not leave it looking
            // synced.
            db.SetPlaylistOutOfCloud(playlistNodeId);

            if (stored.CloudId is { Length: > 0 } cloudId)
            {
                try
                {
                    await _cloud.DeletePlaylistAsync(cloudId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Cloud] Could not remove the playlist {Id} from the cloud", cloudId);
                    _status = _status with { LastError = new CloudTrouble.PlaylistNotRemoved(Faults.Note("cloud", "removing a playlist", ex)) };
                }
            }

            var keep = db.TrackIdsHeldByOtherCloudPlaylists(exceptNodeId: playlistNodeId);
            foreach (var track in tracks)
                if (track.CloudPinned)
                    keep.Add(track.Id);   // Picked by hand, so the playlist does not own it.

            await RemoveTracksAsync(tracks, keep, "playlist");
        }
        finally
        {
            MarkLeaving(null, playlistNodeId, leaving: false);
        }
    }

    /// <summary>
    /// Writes every cloud playlist's current order up again. This is called once a sync run has uploaded
    /// something, so a playlist whose tracks have just arrived stops naming ids that the cloud did not have
    /// when it was first written.
    /// </summary>
    private async Task PushCloudPlaylistsAsync(CancellationToken ct)
    {
        if (!_library.IsOpen || !_auth.IsLoggedIn) return;

        foreach (var node in _library.Db.CloudSelectedPlaylists())
        {
            if (ct.IsCancellationRequested) return;
            await PushPlaylistAsync(node.Id, ct);
        }
    }

    /// <summary>
    /// One playlist, written to the cloud: its order when that has changed, and its cover always.
    ///
    /// <para>This is best effort. A playlist that will not go up is worth a line in the log, not a failed
    /// sync. The audio is what is being backed up.</para>
    /// </summary>
    private async Task PushPlaylistAsync(long playlistNodeId, CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;

        var db = _library.Db;
        var stored = db.GetPlaylistCloud(playlistNodeId);
        if (stored.CloudId is not { Length: > 0 } cloudId)
            return;

        var name = db.GetPlaylistNodes().FirstOrDefault(n => n.Id == playlistNodeId)?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return;

        // Only tracks that have an id are included. A track with no rpm id cannot be in the cloud either.
        // Naming it would name something that will never arrive.
        var trackIds = db.FlattenPlaylistTracks(playlistNodeId)
            .Select(t => t.RpmId)
            .Where(RPM.Core.Metadata.RpmTag.IsId)
            .Select(id => id!)
            .ToList();

        // The position is recorded so that another of the user's machines can put the playlist back in the
        // same place. It is read here, not passed in, because this method runs from a menu click and from
        // the background loop, and the tree may change between the two.
        var folder = db.PlaylistFolderChain(playlistNodeId);

        await WritePlaylistOrderAsync(db, playlistNodeId, cloudId, name, trackIds, folder, ct);

        await PushPlaylistCoverAsync(playlistNodeId, cloudId, name, ct);
    }

    /// <summary>
    /// The order, written up. It is written only when it has actually moved, and never on top of somebody
    /// else's change.
    ///
    /// <para>
    /// <b>Both halves of that matter.</b> Writing every cloud playlist on every run would overwrite a song
    /// added on a phone. That song would vanish at the next sync, with no trace of why. Staying quiet when
    /// the local list is what was last published leaves an inbound change alone, long enough for the pull
    /// to take it in. Naming the copy being replaced stops the write landing anyway, when the phone changed
    /// it in the same second.
    /// </para>
    ///
    /// <para>
    /// A refusal is not handled here, on purpose. Reconciling means writing songs into the tree: adding,
    /// removing and reordering. That work belongs to the pull, which does it for every other inbound
    /// change. If the write is refused, the next pull sees a stamp newer than ours, merges the two, and the
    /// run after that publishes the result. This method must not decide on its own answer, or there would
    /// be two places that reconcile a playlist.
    /// </para>
    /// </summary>
    private async Task WritePlaylistOrderAsync(
        LibraryDatabase db, long nodeId, string cloudId, string name,
        IReadOnlyList<string> trackIds, IReadOnlyList<string> folder, CancellationToken ct)
    {
        var state = db.PlaylistCloudStamps().GetValueOrDefault(cloudId);

        var published = state?.Published ?? PlaylistBase.Unknown;
        var meta = PlaylistCloudState.MetaOf(name, folder);

        // Nothing here has moved since the last publish, so there is nothing to say. Silence lets a change
        // made elsewhere stand until the pull can take it in. On an ordinary run this skips every playlist
        // in the tree, which saves a great many writes.
        //
        // The name and the folder count as changes too. They are part of what was published. A rename that
        // never goes up is a rename that only ever happened on one computer.
        if (published.Matches(trackIds)
            && string.Equals(state?.SentMeta, meta, StringComparison.Ordinal))
            return;

        try
        {
            // Null, not zero, for the first push of a playlist this machine has no stamp for. A library
            // upgrading into this holds none at all. A precondition of zero would say "there should be
            // nothing there" about a playlist that has been in the cloud for a year.
            var stamped = await _cloud.PutPlaylistAsync(
                new CloudPlaylist(cloudId, name, trackIds, folder),
                ifUnchangedSince: state is { Stamp: > 0 } known ? known.Stamp : null, ct);

            db.SetPlaylistInCloud(nodeId, cloudId, DateTime.UtcNow.Ticks);

            // The store's own stamp for what was just written, and the list that went with it, are recorded
            // together because they are one fact. The stamp tells the next pull that this push is not
            // somebody else's change. The list is what a later change is measured against.
            if (stamped > 0) db.NotePlaylistPublished(nodeId, stamped, trackIds, meta);
        }
        catch (PlaylistConflictException clash)
        {
            // Nothing is written down here, not even the stamp. Recording the stamp would tell the next
            // pull that there is nothing new. That is the opposite of true, so the change would be lost.
            _log.LogInformation(
                "[Cloud] \"{Name}\" was changed elsewhere ({Theirs} songs there, {Mine} here); " +
                "the next pull merges the two",
                name, clash.Current.TrackIds.Count, trackIds.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not store the playlist {Name}", name);
            _status = _status with { LastError = new CloudTrouble.PlaylistNotStored(name, Faults.Note("cloud", "storing a playlist", ex)) };
            Changed?.Invoke();
        }
    }

    /// <summary>The playlist's picture, when the desktop has drawn one. It is sent on every push, not only
    /// when the order has moved, because a cover can be chosen without the songs changing at all.</summary>
    private async Task PushPlaylistCoverAsync(
        long playlistNodeId, string cloudId, string name, CancellationToken ct)
    {
        // Only the own tree is read. This is not because every writer of a master is gated on it
        // (PlaylistCovers.MadeAsync also writes one, from the draw path, and not only through
        // PlaylistModel's explicit choice). It is because SegmentOf(PlaylistShelf.Own, ...) is the only
        // segment this call ever asks for. Nothing here can read a peer's tree.
        //
        // The segment is read here, synchronously, before the background hop below. This is the same
        // capture-before-Task.Run pattern that PlaylistModel.Operations.cs uses for its own SetCoverAsync.
        // The library can move on to a different tree before a queued read runs. Reading _library late
        // could then target whichever library is open at that point, rather than the one this push is
        // about.
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);

        // Use ReadForPush, not Read. This call acts on the answer rather than drawing with it. Read's null
        // merges "nothing chosen" and "could not tell", and this call must not confuse the two. See
        // PlaylistCoverStore.MasterRead for the full reasoning.
        //
        // The read runs on a background thread, not inline. PushPlaylistAsync is reached in two ways. One
        // is the background sync loop, which is already on its own thread. The other is
        // AddPlaylistToCloudAsync, which is called straight from a menu click with no Task.Run around it. A
        // read that is only slow on the second path would still be a disk read on the UI thread there.
        var master = await Task.Run(() => PlaylistCoverStore.ReadForPush(playlistNodeId, segment));

        switch (master)
        {
            case PlaylistCoverStore.MasterRead.Present present:
                try
                {
                    await _cloud.PutPlaylistArtAsync(cloudId, present.Cover, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // This is kept apart from the order's own error on purpose. A cover that did not upload
                    // is a playlist that still plays. Reporting it through the same LastError would tell
                    // the user their playlist failed to sync, when only its picture did.
                    _log.LogWarning(ex, "[Cloud] Could not store the cover for playlist {Name}", name);
                }
                break;

            case PlaylistCoverStore.MasterRead.Absent:
                // This is not "nothing new to send". A playlist put back on the automatic cover has just
                // had its master forgotten locally. The cloud still holds the picture that replaced it, and
                // every other device would go on showing that picture as current. This is best effort, and
                // quiet. It runs on every push of every playlist that never had a picture, which is most of
                // them. Deleting something that is already gone is the state that is wanted here.
                try
                {
                    await _cloud.DeletePlaylistArtAsync(cloudId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "[Cloud] Could not remove the cover for playlist {Name}", name);
                }
                break;

            case PlaylistCoverStore.MasterRead.Unreadable:
                // Neither an upload nor a delete. A read that failed is not a "the user removed it", and
                // must not be treated as one. The cloud is left exactly as it was. The next pass asks
                // again, kicked by the same triggers as this one.
                break;
        }
    }

    private async Task RefreshQuotaAsync(CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;

        try
        {
            var quota = await _cloud.GetQuotaAsync(ct);
            _status = _status with
            {
                UsedBytes = quota.UsedBytes,
                QuotaBytes = quota.QuotaBytes,
                Unlimited = quota.Unlimited,
            };

            // The store answering at all is also the moment the app learns the ceiling has been lifted.
            // That is the one thing that puts "all my music" back.
            RestoreSyncAllIfBought();

            // A store with room again is no longer full, and the message that said it was full must go too.
            // The queue picks up on the next pass, and nothing else would clear the line.
            if (!_status.Space.IsFull && _status.LastError is CloudTrouble.StorageFull)
                _status = _status with { LastError = null };

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the quota");
        }
    }

    private void Report(bool running, LibraryDatabase db)
    {
        _status = _status with
        {
            Running = running,
            Pending = db.CountByCloudState(CloudState.Queued),
            Failed = db.CountByCloudState(CloudState.Error),
        };
        Changed?.Invoke();
    }
}

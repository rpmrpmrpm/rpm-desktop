using RPM.Core.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Bringing tracks back down, and taking them off the cloud again. This works on one track or on a whole
/// album.
/// </summary>
public sealed partial class CloudSyncService
{
    public Task RestoreAsync(LibraryTrack track, CancellationToken ct = default) =>
        ErrandAsync(work => RestoreOneCoreAsync(track, work), ct);

    private async Task RestoreOneCoreAsync(LibraryTrack track, CancellationToken ct)
    {
        if (!_library.IsOpen) return;
        if (!RPM.Core.Metadata.RpmTag.IsId(track.RpmId)) return;

        _jobs.StartBatch(CloudOp.Restore);
        _jobs.Expect(1);
        try
        {
            await RestoreWatchedAsync(track, ct);
        }
        finally
        {
            _jobs.EndBatch();
        }

        _library.NotifyChanged();
    }

    /// <summary>
    /// Brings a set of tracks down as one piece of work: one batch, counted against the whole set, with
    /// several files coming down at once.
    ///
    /// <para>The record's menu calls the single-track path in a loop. Each call would open its own batch of
    /// one. A twelve-track album would then show "0 of 1 tracks done" twelve times, and every file would
    /// wait for the one before it to land. The set is the unit of work. So it is also the unit that the
    /// chip counts and the workers share.</para>
    ///
    /// <para>A track that will not come down does not stop the others. Its row says so, the reason is
    /// logged, and the rest of the record still arrives.</para>
    /// </summary>
    public Task<(int Restored, int Failed)> RestoreAsync(
        IReadOnlyList<LibraryTrack> tracks, CancellationToken ct = default) =>
        ErrandAsync(work => RestoreManyCoreAsync(tracks, work), ct);

    private async Task<(int Restored, int Failed)> RestoreManyCoreAsync(
        IReadOnlyList<LibraryTrack> tracks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (!_library.IsOpen) return (0, 0);

        var wanted = tracks.Where(t => RPM.Core.Metadata.RpmTag.IsId(t.RpmId)).ToList();
        if (wanted.Count == 0) return (0, 0);

        var restored = 0;
        var failed = 0;

        _jobs.StartBatch(CloudOp.Restore);
        _jobs.Expect(wanted.Count);
        try
        {
            // This is the same three lanes that uploads use, and for the same reason: enough to fill a home
            // connection, but not so many that playback runs short of disk and CPU.
            using var slots = new SemaphoreSlim(Concurrency, Concurrency);
            await Task.WhenAll(wanted.Select(async track =>
            {
                if (!await Cancellable.WaitAsync(slots, ct))
                    return;
                try
                {
                    await RestoreWatchedAsync(track, ct);
                    Interlocked.Increment(ref restored);
                }
                catch (OperationCanceledException)
                {
                    // The user stopped it. The row goes back in the line, and it is not written down as a
                    // failure, because the user caused it on purpose.
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _log.LogWarning(ex, "[Cloud] Could not bring {Path} back down", track.RelativePath);
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        finally
        {
            _jobs.EndBatch();
        }

        _library.NotifyChanged();
        return (restored, failed);
    }

    /// <summary>
    /// One track's trip down, watched. The row is on screen as soon as the track is taken up. The row ends
    /// as finished, refused, or back in the line.
    ///
    /// <para>It throws whatever the download threw. Callers disagree about what a refusal means. A menu
    /// item on one song passes it back to the page, and a record keeps going. So each caller decides at its
    /// own level, and this method does not decide for both.</para>
    /// </summary>
    private async Task RestoreWatchedAsync(LibraryTrack track, CancellationToken ct)
    {
        _jobs.Begin(track.Id, CloudOp.Restore, track.Title ?? track.FileName, track.Album);
        _jobs.Stage(track.Id, CloudStage.Restoring, 0.4);
        try
        {
            await RestoreCoreAsync(track, ct);
            _jobs.Complete(track.Id, track.CanonicalSizeBytes);
        }
        catch (OperationCanceledException)
        {
            _jobs.Abandon(track.Id);
            throw;
        }
        catch (Exception ex)
        {
            _jobs.Fail(track.Id, FaultText.Say(Faults.Note("cloud", "bringing a track back", ex)), CloudFailure.InFull(ex));
            throw;
        }
    }

    /// <summary>Fetches the file back down, with nothing said about it. A removal restores an offloaded
    /// track before it deletes the cloud copy. That download is a step inside the removal the user asked
    /// for. It is not a second action on their library.</summary>
    private async Task RestoreCoreAsync(LibraryTrack track, CancellationToken ct)
    {
        // Read the open library once, so the file is written under the same collection whose row is updated
        // afterwards. If the library were read twice, a change made in settings during the download would
        // put the file in one place and the row in another.
        var db = _library.Db;
        var path = Path.Combine(db.Root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        await _cloud.RestoreAsync(track.RpmId!, path, ct);

        db.SetCloudState(track.Id, CloudState.Synced);

        // The file is on this disk now, so the row belongs to this library. It no longer lives and dies
        // with the cloud listing that put it here. See LibraryDatabase.MirrorCloudLibrary.
        db.ClearAdopted(track.Id);

        // The track is also in step with the store on both clocks. The download wrote the file from the
        // store's own sidecar, so its new modified time is not an edit made here. Without this, every
        // restored track would send its tags straight back up.
        db.NoteCloudInStep(track.Id, track.CloudUpdatedAt, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Removes a track from the cloud. This happens only from an explicit action. A file that merely went
    /// missing on disk keeps its cloud copy, because "my drive was not plugged in" and "delete my music"
    /// look exactly alike from here.
    /// </summary>
    public Task RemoveFromCloudAsync(LibraryTrack track, CancellationToken ct = default) =>
        ErrandAsync(work => RemoveFromCloudCoreAsync(track, work), ct);

    private async Task RemoveFromCloudCoreAsync(LibraryTrack track, CancellationToken ct)
    {
        if (!_library.IsOpen) return;

        _jobs.StartBatch(CloudOp.Remove);
        _jobs.Expect(1);
        try
        {
            await RemoveOneAsync(track, ct);
        }
        finally
        {
            _jobs.EndBatch();
        }

        await RefreshQuotaAsync(ct);
        _library.NotifyChanged();
    }

    /// <summary>
    /// One track's half of a removal. The quota read and the library notification are left to the caller,
    /// so that taking a sixteen-track album out of the cloud does not reload the library sixteen times.
    /// </summary>
    private async Task RemoveOneAsync(LibraryTrack track, CancellationToken ct)
    {
        var db = _library.Db;

        var plan = CloudRemovalPlan.For(CloudRemovalIntent.TakeOutOfCloud, track.CloudState, track.RpmId);

        // Nothing was ever stored under this id, or the track never went up. There is no cloud copy to
        // delete. The row can still hold a selection or a place in the queue, so those are cleared, and
        // that is all. Nothing is watched and nothing is logged, because no bytes moved.
        if (plan is not (CloudRemoval.DeleteInCloud or CloudRemoval.BringHomeThenDelete))
        {
            db.ForgetCloud(track.Id);
            return;
        }

        _jobs.Begin(track.Id, CloudOp.Remove, track.Title ?? track.FileName, track.Album);
        try
        {
            // The file is downloaded to this disk first. Deleting the cloud copy of an offloaded track would
            // delete the only copy there is. The status names what is really happening: the file is coming
            // down. A removal that sits on "removing" for four minutes looks stuck.
            if (plan == CloudRemoval.BringHomeThenDelete)
            {
                _jobs.Stage(track.Id, CloudStage.Restoring, 0.2);
                await RestoreCoreAsync(track, ct);
            }

            _jobs.Stage(track.Id, CloudStage.Removing, 0.7);
            await _cloud.DeleteAsync(track.RpmId!, ct);

            db.ForgetCloud(track.Id);
            _jobs.Complete(track.Id, track.CanonicalSizeBytes);
        }
        catch (OperationCanceledException)
        {
            _jobs.Abandon(track.Id);
            throw;
        }
        catch (Exception ex)
        {
            // The callers decide what a failure means for the batch. An album keeps going, but a single
            // track's menu action does not. Either way, the row says so and the log book records it.
            _jobs.Fail(track.Id, FaultText.Say(Faults.Note("cloud", "removing a track from the cloud", ex)), CloudFailure.InFull(ex));
            throw;
        }
    }

    /// <summary>
    /// Deletes these tracks' cloud copies outright, without bringing anything down first.
    ///
    /// <para>The one caller deletes a record from this computer and from the account, because the user
    /// asked for both. <see cref="RemoveFromCloudAsync"/> downloads an offloaded track to this disk before it
    /// deletes
    /// the cloud copy. That is needed because "take this out of the cloud" must never destroy the only
    /// copy. Here, destroying it is exactly what was asked for. Downloading a record in order to throw it
    /// away is not wanted.</para>
    ///
    /// <para>Tracks that a synced playlist still plays are deleted like any other. Elsewhere, taking an
    /// album out of the cloud spares those tracks so the playlist keeps working. This is a deletion,
    /// though, and the local rows are going too. Sparing the cloud copy would leave the record to come back
    /// on the next catalogue pass as a cloud album, which is the opposite of what was asked.</para>
    ///
    /// <para>A track that will not delete does not stop the rest. The reason is shown on
    /// <see cref="Status"/>, and the album's other tracks still go.</para>
    /// </summary>
    public Task DeleteFromCloudAsync(IReadOnlyList<LibraryTrack> tracks, CancellationToken ct = default) =>
        ErrandAsync(work => DeleteFromCloudCoreAsync(tracks, work), ct);

    private async Task DeleteFromCloudCoreAsync(IReadOnlyList<LibraryTrack> tracks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (!_library.IsOpen) return;

        // This covers everything the account holds for these tracks. That includes tracks that were
        // actually uploaded, and tracks that were only chosen or queued. Those have nothing to delete, only
        // a place in the line.
        var going = tracks
            .Where(t => CloudRemovalPlan.IsInTheCloudsHands(t.CloudState))
            .ToList();
        if (going.Count == 0) return;

        var db = _library.Db;
        Exception? firstFailure = null;
        var failed = 0;

        // The records these tracks belong to show the leaving mark while the work runs. Removal writes
        // nothing to the mirror during the run. Without this mark, the wall's card would sit unchanged over
        // a record whose cloud copy is being deleted.
        var folders = going
            .Select(t => t.Folder)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var folder in folders)
            MarkLeaving(folder, null, leaving: true);

        _jobs.StartBatch(CloudOp.Remove);
        _jobs.Expect(going.Count);
        try
        {
            foreach (var track in going)
            {
                // Nothing was ever stored under this id, or nothing has gone up yet. There is no cloud
                // object to delete, only a selection and a place in the queue to clear. Nothing is
                // downloaded here, and no playlist spares the track. See CloudRemovalIntent.
                if (CloudRemovalPlan.For(CloudRemovalIntent.DeleteEverywhere, track.CloudState, track.RpmId)
                    != CloudRemoval.DeleteInCloud)
                {
                    db.ForgetCloud(track.Id);
                    continue;
                }

                _jobs.Begin(track.Id, CloudOp.Remove, track.Title ?? track.FileName, track.Album);
                try
                {
                    await _cloud.DeleteAsync(track.RpmId!, ct);
                    db.ForgetCloud(track.Id);
                    _jobs.Complete(track.Id, track.CanonicalSizeBytes);
                }
                catch (Exception ex)
                {
                    failed++;
                    firstFailure ??= ex;
                    _jobs.Fail(track.Id, FaultText.Say(Faults.Note("cloud", "deleting a track from the cloud", ex)), CloudFailure.InFull(ex));
                    _log.LogWarning(ex, "[Cloud] Could not delete {Path} from the cloud", track.RelativePath);
                }
            }
        }
        finally
        {
            _jobs.EndBatch();
            foreach (var folder in folders)
                MarkLeaving(folder, null, leaving: false);
        }

        _status = _status with
        {
            LastError = firstFailure is null
                ? null
                : new CloudTrouble.TracksNotRemoved(failed, going.Count, Faults.Read(firstFailure)),
        };

        await RefreshQuotaAsync(CancellationToken.None);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds a whole album to the cloud: picks every one of its tracks and starts the queue. This only does
    /// anything in "selected" mode. In "all" mode the album is already going up. In "none" mode the menu
    /// that calls this is not shown.
    /// </summary>
    public Task AddAlbumToCloudAsync(IReadOnlyList<long> trackIds) =>
        ErrandAsync(_ => AddAlbumToCloudCoreAsync(trackIds), CancellationToken.None);

    private async Task AddAlbumToCloudCoreAsync(IReadOnlyList<long> trackIds)
    {
        if (!_library.IsOpen || trackIds.Count == 0) return;
        // The album is pinned, because the user picked it. It stays up even if a synced playlist that
        // happened to include some of its tracks is later taken back out.
        _library.Db.SetCloudSelected(trackIds, selected: true, pinned: true);
        _library.NotifyChanged();
        await KickAsync();
    }

    /// <summary>
    /// Puts ONE track in the cloud. This is the same action as for an album, applied to the one song the
    /// user picked.
    ///
    /// <para>The track is pinned, like an album's tracks. The user asked for this file by name, so it stays
    /// up even if a synced playlist that included it is later taken back out.</para>
    /// </summary>
    public Task AddTrackToCloudAsync(long trackId) =>
        ErrandAsync(_ => AddTrackToCloudCoreAsync(trackId), CancellationToken.None);

    private async Task AddTrackToCloudCoreAsync(long trackId)
    {
        if (!_library.IsOpen) return;
        _library.Db.SetCloudSelected([trackId], selected: true, pinned: true);
        _library.NotifyChanged();
        await KickAsync();
    }

    /// <summary>
    /// The shared body of every "take these out of the cloud" action.
    ///
    /// <para>
    /// A track in <paramref name="keep"/> is not deleted. It only loses the claim the caller had on it,
    /// because something else in the user's library still wants it up there. Everything else has its cloud
    /// copy removed and its row forgotten.
    /// </para>
    /// </summary>
    private async Task RemoveTracksAsync(IReadOnlyList<LibraryTrack> tracks, HashSet<long> keep, string what)
    {
        var db = _library.Db;

        Exception? firstFailure = null;
        var failed = 0;
        var kept = new List<long>();

        // A sixteen-track album coming out of the cloud is one batch like any other. The chip stays up for
        // the whole of it, rather than blinking once per track.
        _jobs.StartBatch(CloudOp.Remove);
        _jobs.Expect(tracks.Count);
        try
        {

        foreach (var track in tracks)
        {
            var plan = CloudRemovalPlan.For(
                CloudRemovalIntent.TakeOutOfCloud, track.CloudState, track.RpmId,
                heldElsewhere: keep.Contains(track.Id));

            if (plan == CloudRemoval.UnpinOnly)
            {
                kept.Add(track.Id);
                continue;
            }

            try
            {
                if (plan is CloudRemoval.DeleteInCloud or CloudRemoval.BringHomeThenDelete)
                    await RemoveOneAsync(track, CancellationToken.None);
                else
                    db.ForgetCloud(track.Id);   // Only chosen or queued. Nothing is up there.
            }
            catch (Exception ex)
            {
                failed++;
                firstFailure ??= ex;
                _log.LogWarning(ex, "[Cloud] Could not remove {Path} from the cloud", track.RelativePath);
            }
        }

        }
        finally
        {
            _jobs.EndBatch();
        }

        if (kept.Count > 0)
        {
            db.UnpinCloud(kept);
            _log.LogInformation(
                "[Cloud] {Kept} of the {What}'s tracks stay in the cloud — something else there still plays them",
                kept.Count, what);
        }

        _status = _status with
        {
            LastError = firstFailure is null
                ? null
                : new CloudTrouble.TracksNotRemoved(failed, tracks.Count, Faults.Read(firstFailure)),
        };

        await RefreshQuotaAsync(CancellationToken.None);
        _library.NotifyChanged();
        Changed?.Invoke();
    }

    // ── playlists ──

    /// <summary>
    /// Puts a playlist in the cloud. Every track it plays goes up, and the order itself is stored beside
    /// them.
    ///
    /// <para>The tracks are picked, but not pinned. They are up there because this playlist is. So taking
    /// the playlist back out takes them with it, unless the user picked them by hand as well, or another
    /// synced playlist plays them too.</para>
    ///
    /// <para>The order is written straight away, before any audio has been uploaded. A playlist that names
    /// tracks still on their way is not broken, because a reader skips an id it does not have yet. Writing
    /// the order now means the playlist exists in the cloud even if the upload takes all night.
    /// <see cref="PushCloudPlaylistsAsync"/> writes it again as the tracks arrive.</para>
    /// </summary>
}

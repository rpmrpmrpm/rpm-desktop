using RPM.Core.Import;
using RPM.Core.Cloud;
using RPM.Core.Files;
using RPM.Core.Library;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Sends the collection up: one track at a time, its album art beside it, the renditions the server
/// still owes, and the repairs that a half-finished earlier run left behind.
/// </summary>
public sealed partial class CloudSyncService
{
    private async Task UploadOneAsync(LibraryDatabase db, LibraryTrack track, CancellationToken ct)
    {
        // The run's own library, not whichever library is open when this track is reached. A relative
        // path belongs to the mirror it was read from. Joining it to a root that has since changed in the
        // settings would name a file in the wrong collection.
        var path = Path.Combine(db.Root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
        {
            // A row whose bytes are in the store is expected to have no file. That is what offloading is,
            // and another pass may have done it while this track waited in the queue. Describing it as
            // "on disk, not in the cloud" would be wrong in the way that costs most. Sync reads such a row
            // as a track the user deleted, and drops it. That takes the only pointer to the copy in the
            // store with it.
            if (track.CloudState is CloudState.CloudOnly)
                return;

            // Otherwise the file really did go away between being queued and being reached. This is not an
            // error worth reporting. The next sync sets the right state.
            db.SetCloudState(track.Id, CloudState.Local);
            return;
        }

        // A track must have an id before it can have a place in the cloud, because the id is its name
        // there. Sync gives every track an id, so this happens only when the file cannot hold one, or when
        // the import has not reached it yet. The refusal is final only when it repeats, like any other.
        if (!RPM.Core.Metadata.RpmTag.IsId(track.RpmId))
        {
            var sentence = Loc.Get(CloudFailure.NoId);
            db.MarkCloudAttemptFailed(track.Id, sentence, CloudRetrySchedule.AfterFailure(
                new CloudFailureKind(CloudFailure.NoId, Permanent: true),
                sentence, track.CloudError, track.CloudAttempts, DateTime.UtcNow));
            return;
        }

        // Something is writing the file right now: the import, a tag edit, the analysis tags, or another
        // program. ffmpeg would read straight through the write, so the track waits a minute instead. This
        // is not a failure, and it is not shown as one.
        if (FileHandles.IsBeingWritten(path))
        {
            Defer(db, track, "is being written");
            return;
        }

        // Compared after the transcode. A writer can still start between this check and the one above. A
        // file that changed while ffmpeg read it gives a copy that is not the file.
        var stamp = FileStamp.Of(path);

        // This check comes before any work is spent on the track. Three uploads run at once, so the upload
        // that fills the store is often not the last one started. The other two are already inside ffmpeg.
        // Without this check, each would finish a full transcode and then be refused a moment later. The
        // run ends as soon as the store is full, and this is where that is detected.
        if (_status.Space.IsFull)
            throw new CloudQuotaExceededException(_status.UsedBytes, _status.QuotaBytes);

        var workingDir = Path.Combine(Path.GetTempPath(), "rpm-cloud");
        CanonicalFile? canonical = null;

        _jobs.Begin(track.Id, CloudOp.Upload, track.Title ?? track.FileName, track.Album);

        try
        {
            db.SetCloudState(track.Id, CloudState.Uploading);
            _status = _status with { CurrentTitle = track.Title ?? track.FileName };
            Changed?.Invoke();

            // Both halves are made here, from the file as it is now: the anonymous audio that everyone
            // shares, and the tags that belong only to this user.
            //
            // The chip is told which of the two is happening, not one word for both. They can be minutes
            // apart on a large FLAC, and they fail for different reasons. A transcode that stalls is a
            // fault on this machine, and an upload that stalls is a fault on the connection. The fractions
            // are the split between the two steps. They are not measured, because neither step reports its
            // own progress.
            _jobs.Stage(track.Id, CloudStage.Transcoding, 0.15);
            canonical = await CanonicalAudio.CreateAsync(path, workingDir, ct);

            // Written while it was read. The copy may be torn, and its hash would name audio that nobody
            // has. Discard it (the finally block deletes it) and try again once the write is over.
            if (FileStamp.Of(path) != stamp)
            {
                Defer(db, track, "changed while it was being read");
                return;
            }

            // The second limit: a store with room, but not room for this track. The run stops here rather
            // than sending it. The server would refuse the same bytes at "complete", after the upload,
            // having used the user's connection for all of it.
            //
            // This is not a per-track skip. A track that does not fit means the queue has reached the
            // limit. Walking the rest of the library to find a smaller track is the same wasted pass under
            // another name. Room is checked again on the next run, when the numbers are fresh.
            if (!_status.Space.Fits(canonical.SizeBytes))
                throw new CloudQuotaExceededException(_status.UsedBytes, _status.QuotaBytes);

            // The account may already hold this exact recording, stored by another of the user's installs.
            // The same CD ripped on two machines gives the same canonical audio. Storing it again would add
            // a second catalogue row and a second charge against the same storage. Every record would also
            // show the song twice on the other screens. So take that identity instead. The hashes match, so
            // the two files are the same track.
            if (await AdoptCloudIdentityAsync(db, track, path, canonical.Sha256, canonical.SizeBytes, ct))
            {
                _jobs.Complete(track.Id, canonical.SizeBytes);
                return;
            }

            // The file's own tags, plus the one mark that the file cannot carry: whether the listener has
            // made this song a favourite. That mark belongs to the row. The sidecar is the private part of
            // the upload, so a library rebuilt from the cloud gets its favourites back.
            var sidecar = TagSidecar.FromFile(path, db.Root)
                .WithFavorite(track.IsFavorite, track.FavoritedUtcTicks);

            _jobs.Stage(track.Id, CloudStage.Uploading, 0.55);
            var outcome = await _cloud.UploadAsync(
                track.RpmId!, canonical, sidecar, new FileInfo(path).Length, ct);

            db.MarkSynced(track.Id, canonical.Sha256, canonical.SizeBytes, DateTime.UtcNow.Ticks);

            // The upload makes the renditions as its last step. Recording that here stops the catch-up pass
            // from picking up a track that never needs it. Without this, every track the app has ever
            // uploaded would be queued to be encoded again on a later run.
            if (outcome.Renditions)
                db.NoteRenditions(track.Id, DateTime.UtcNow.Ticks);

            await UploadAlbumArtAsync(path, sidecar, ct);

            // What the store says it holds now. This is the number that the next track is measured against.
            _status = _status with
            {
                UsedBytes = outcome.Quota.UsedBytes,
                QuotaBytes = outcome.Quota.QuotaBytes,
                Unlimited = outcome.Quota.Unlimited,
            };

            if (outcome.Deduplicated)
                _log.LogInformation("[Cloud] Already in the store, linked not uploaded: {Path}", track.RelativePath);

            // The local file stays where it is. An upload is a second copy, never a move. rpm does not
            // remove the user's music, and a track that is in both places costs only disk space.
            _jobs.Complete(track.Id, canonical.SizeBytes);
        }
        catch (OperationCanceledException)
        {
            db.SetCloudState(track.Id, CloudState.Queued);   // Paused mid-flight: back in the line.
            _jobs.Abandon(track.Id);                         // Neither done nor refused: not kept as history.
            throw;
        }
        catch (CloudQuotaExceededException)
        {
            db.SetCloudState(track.Id, CloudState.Queued);
            _jobs.Abandon(track.Id);
            throw;   // Stops the whole run, not just this track.
        }
        catch (Exception ex)
        {
            // A failure on a file that was still being written says nothing about the file itself. Without
            // this check, whole imported albums would be refused for good. The import or a tag edit was still
            // writing them, ffmpeg read half a file, and the refusal was recorded as a fault in the audio.
            if (FileStamp.Of(path) != stamp || FileHandles.IsBeingWritten(path))
            {
                _log.LogInformation(ex, "[Cloud] {Path} failed while it was being written", track.RelativePath);
                Defer(db, track, "was being written when the upload failed");
                return;
            }

            _log.LogWarning(ex, "[Cloud] Upload failed: {Path}", track.RelativePath);

            // What the user sees and what the queue does next are two different readings of the same
            // failure. The row stores a short message for the user, not the exception text. Exception text
            // would put ffmpeg's error output in front of a user who asked for their music to be backed up.
            // It would also send a file that cannot be encoded round the retry schedule again and again, at
            // every launch.
            var failure = CloudFailure.Classify(ex.Message);
            var sentence = Loc.Get(failure.MessageKey);
            db.MarkCloudAttemptFailed(
                track.Id,
                sentence,
                CloudRetrySchedule.AfterFailure(
                    failure, sentence, track.CloudError, track.CloudAttempts, DateTime.UtcNow));
            _jobs.Fail(track.Id, sentence, CloudFailure.InFull(ex));
        }
        finally
        {
            if (canonical is not null)
                try { File.Delete(canonical.Path); } catch { }

            _status = _status with { CurrentTitle = null };
            Changed?.Invoke();
        }
    }

    /// <summary>Puts a track whose file was written during its upload back in the queue, to be tried again
    /// in a minute. No failure is counted, and nothing is written to the failure log. The job leaves the
    /// chip as abandoned.
    /// </summary>
    private void Defer(LibraryDatabase db, LibraryTrack track, string why)
    {
        db.DeferCloudUpload(track.Id, CloudRetrySchedule.NextAttemptTicks(0, DateTime.UtcNow));
        _jobs.Abandon(track.Id);
        _log.LogInformation("[Cloud] {Path} {Why}, upload deferred", track.RelativePath, why);
    }

    /// <summary>Takes over the id that the account already stores this recording under, rather than
    /// uploading a second copy of it.
    ///
    /// <para>This is what "an album is a set of files" means once a user has two computers. The canonical
    /// hash is the audio with the tags stripped off. So two rips of one CD by the same encoder match
    /// exactly, and the second machine has nothing to add by storing them again. The file is stamped with
    /// the cloud's id, so the mirror, the file and the store all name the track the same way from then on.
    /// </para>
    ///
    /// <para>Nothing is claimed that is not known. The hash comes from this machine's own transcode of its
    /// own file, and it is the store's key. A file that cannot be stamped is uploaded as itself.</para>
    /// </summary>
    /// <returns>True when the row now stands as synced under somebody else's id and the upload
    /// is not needed.</returns>
    private async Task<bool> AdoptCloudIdentityAsync(
        LibraryDatabase db, LibraryTrack track, string path, string sha256, long canonicalSize,
        CancellationToken ct)
    {
        var theirs = _mirror.KnownId(sha256);
        if (!CloudAdoption.WorthVerifying(theirs, track.RpmId)) return false;

        // Asked before anything is written down, not only before a file is deleted.
        //
        // The whole decision rests on a hash index built from a catalogue listing that can be ten minutes
        // old. Adopting marks the track as in the cloud without sending a byte. So a stale answer would
        // write a row that says "backed up" about music the store does not have. Nothing corrects that
        // afterwards, because the catalogue pass skips ids that the listing does not contain. The library
        // would show the record as safe for ever. The only symptom would be the rendition pass encoding the
        // same files into a 404 on every run.
        _jobs.Stage(track.Id, CloudStage.Verifying, 0.5);

        var stamped = track with { RpmId = theirs, ContentHash = sha256 };
        var vouches = Vouched(await VerifyAsync([stamped], ct), stamped);

        if (!CloudAdoption.MayAdopt(theirs, track.RpmId, vouches))
        {
            _log.LogInformation(
                "[Cloud] {File} looked like a copy of {Id}, but the store does not hold that any more — sending it as itself",
                track.FileName, theirs);
            return false;
        }

        if (!RPM.Core.Metadata.TagReader.WriteRpmId(path, theirs!))
            return false;

        db.SetRpmId(track.Id, theirs!);
        db.MarkSynced(track.Id, sha256, canonicalSize, DateTime.UtcNow.Ticks);

        _log.LogInformation(
            "[Cloud] {File} is already in the cloud under {Id} — the copy up there is this track, so nothing was sent",
            track.FileName, theirs);

        return true;
    }

    /// <summary>
    /// Puts the album's cover in the cloud, once per album per run.
    ///
    /// <para>The sidecar cannot carry this. It records the art <em>embedded in the file</em>, because that
    /// is what a restore has to write back. The library shows embedded art or the album folder's image, and
    /// most ripped albums have only the second. Without this, an album whose cover is on screen right now
    /// reaches the cloud with nothing. That is what the phone showed.</para>
    ///
    /// <para>Best effort throughout. A sleeve that will not upload must never fail a track that did. The
    /// audio is the thing being backed up.</para>
    /// </summary>
    private async Task UploadAlbumArtAsync(string path, TagSidecar sidecar, CancellationToken ct)
    {
        var albumKey = AlbumArt.KeyForTrack(sidecar.AlbumArtist, sidecar.Artist, sidecar.Album);

        // Checked per run, not per session. A user who fixes a cover and syncs again should see it
        // replaced.
        if (!_artDone.Add(albumKey))
            return;

        try
        {
            if (AlbumArt.ForFile(path) is not { } cover)
                return;

            if (await _cloud.UploadAlbumArtAsync(albumKey, cover, ct))
                _log.LogInformation("[Cloud] Album art for {Album} ({Source}, {Bytes} bytes)",
                    sidecar.Album, cover.Source, cover.Bytes.Length);
            else
                _log.LogInformation(
                    "[Cloud] No album art worth storing for {Album} — {Bytes} bytes from {Source}",
                    sidecar.Album, cover.Bytes.Length, cover.Source);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The track is backed up either way. This is only the sleeve, so it is logged rather than
            // treated as a failed upload. It is never silent, because an album with no cover in the cloud
            // would otherwise leave no trace at all.
            _log.LogWarning(ex, "[Cloud] Could not store album art for {Album}", sidecar.Album);
        }
    }

    /// <summary>
    /// Sends up the sleeves of albums that are in the cloud without one.
    ///
    /// <para>Art is stored only while a track uploads. So an album whose cover failed at that time keeps
    /// no record of the failure, and it is never tried again. It sits in the cloud with no cover, and the
    /// phone draws a blank square. This happened because the content type of an embedded <c>image/jpg</c>
    /// cover was sent as it was, into a slot signed for <c>image/jpeg</c>.</para>
    ///
    /// <para>One listing of what is already stored, then at most <see cref="ArtRepairPerRun"/> uploads. So
    /// a library with hundreds of missing covers heals over a few runs, not in one sync that takes an
    /// afternoon. Best effort throughout. This is about the sleeve, not the music.</para>
    /// </summary>
    private const int ArtRepairPerRun = 25;

    /// <summary>
    /// Makes the head clips and the default rendition for tracks uploaded before either existed.
    ///
    /// <para>
    /// A few per run, at the end of a sync, on the machine that already holds the originals and already has
    /// ffmpeg. The alternative would be a cloud function that transcodes the whole library and bills by the
    /// second. That is a cost for audio that is already on a disk here.
    /// </para>
    ///
    /// <para>Best effort and silent. A track with no clip plays as normal, but starts a little more slowly.
    /// The server has already recorded that it wants one.</para>
    ///
    /// <para>The mirror records which tracks are done (<c>renditions_utc_ticks</c>). So a library that is
    /// already caught up finds nothing to do here, and the deck stays quiet. Progress that was kept only in
    /// memory would be lost when the app closes, and every launch would encode the same tracks
    /// again.</para>
    /// </summary>
    private async Task CatchUpRenditionsAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;
        var root = db.Root;

        // Counted separately, because they mean different things in the log: what this run encoded, what it
        // tried and could not encode, and what turned out not to be in the cloud at all. One shared count
        // would be misleading, because a run that made nothing could still report a number.
        var made = 0;
        var refused = 0;
        var forGood = 0;
        var putBack = 0;
        var denied = 0;
        var renamed = 0;
        var tried = 0;

        // This is what happens when a rendition fails. Read what was said. Show the user the short
        // sentence, not ffmpeg's output. If the same bytes through the same encoder can only give the same
        // answer, write that on the row, so this pass never asks again. This is shared, because the refusal
        // arrives in two ways: as an answer from the client, or as an exception thrown from around it.
        // Treating the two differently is how one of them went unrecorded.
        void Refuse(long trackId, string relativePath, string? said, string? fullError)
        {
            var failure = CloudFailure.Classify(said);
            var sentence = Loc.Get(failure.MessageKey);

            if (failure.Permanent)
            {
                db.MarkCloudPartial(trackId, sentence);
                _log.LogWarning(
                    "[Cloud] {Path} cannot be given renditions and will not be asked again: {Said}",
                    relativePath, said);
                forGood++;
            }

            _jobs.Fail(trackId, sentence, fullError);
            refused++;
        }

        foreach (var (id, rpmId, relativePath, title, album, contentHash)
                 in db.CloudTracksWithLocalFiles(RenditionsPerRun * 4))
        {
            if (ct.IsCancellationRequested || tried >= RenditionsPerRun) break;

            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;

            // Named as what it is, on the track it belongs to. Transcoding is a step of putting a track in
            // the cloud, so it belongs on that track's row, not in silence that the user has to guess at.
            _jobs.Begin(id, CloudOp.Upload, title ?? Path.GetFileName(relativePath), album);
            _jobs.Stage(id, CloudStage.Transcoding, 0.3);
            try
            {
                // Written down only on success, and in the mirror rather than in a field of this service. A
                // value kept only in memory would be lost when the process exits. Every launch would then
                // re-encode and re-upload the same oldest tracks.
                var result = await _cloud.MakeRenditionsAsync(rpmId, path, ct);
                var outcome = result.Outcome;

                if (outcome == RenditionOutcome.Made)
                {
                    db.NoteRenditions(id, DateTime.UtcNow.Ticks);
                    _jobs.Complete(id);
                    made++;
                }
                else if (outcome == RenditionOutcome.NotInTheCloud)
                {
                    // The row says the cloud has this track, and the store, asked about that exact id, says
                    // it does not. Left alone, it is encoded into the same 404 on every run, and the
                    // library goes on showing a record as backed up that is not.
                    //
                    // Two very different things look like this from here. The run's own catalogue tells
                    // them apart. Either the account holds this exact audio under another name (a row whose
                    // id drifted, which is put right without saying anything about the account), or it has
                    // never heard of the audio at all (which is where the ceiling and the warning below
                    // belong). A hash index that is minutes old can only be wrong in the cheap direction.
                    // The upload path asks the store about the id before it takes it over, so a stale "yes"
                    // costs one upload, not a wrong row.
                    var underAnotherName =
                        CloudRowRepair.ExplainedByTheCatalog(rpmId, _mirror.KnownId(contentHash));

                    if (underAnotherName
                        || CloudRowRepair.BelongsBackInTheQueue(CloudState.Synced, outcome, putBack))
                    {
                        // Put back where it was before anybody offered it. The queue takes it from there if
                        // it still belongs, and the upload takes over the id that the store has.
                        db.SetCloudState(id, CloudRowRepair.PutBackAs);
                        _jobs.Fail(id, Loc.Get("Cloud_TrackNotInCloud"), underAnotherName
                            ? $"The store holds this audio under {_mirror.KnownId(contentHash)}, not under {rpmId}."
                            : $"The store has no track under {rpmId}.");

                        if (underAnotherName)
                        {
                            _log.LogWarning(
                                "[Cloud] {Path} is in the cloud under {Theirs}, not under {RpmId} which " +
                                "this row asked about — back in the line, and the upload takes that id over",
                                relativePath, _mirror.KnownId(contentHash), rpmId);
                            renamed++;
                        }
                        else
                        {
                            _log.LogWarning(
                                "[Cloud] {Path} said it was in the cloud, but the account has no track under {RpmId} — it goes back in the line",
                                relativePath, rpmId);
                            putBack++;
                            denied++;
                        }
                    }
                    else
                    {
                        // Past the ceiling. This many denials in one run is not a handful of bad rows. The
                        // rows are left exactly as they are until somebody has looked. See
                        // CloudRowRepair.MostPerRun.
                        denied++;
                        _jobs.Fail(id, Loc.Get("Cloud_TrackNotInCloud"), $"The store has no track under {rpmId}.");
                    }
                }
                else
                {
                    // Nothing was made. Whether that is worth another run depends on what was said, not on
                    // how the answer got here. A dropped connection is tried again next time. A file that
                    // ffmpeg refuses is written down and let go of.
                    Refuse(id, relativePath, result.Said, result.FullError);
                }
            }
            catch (OperationCanceledException)
            {
                _jobs.Abandon(id);
                throw;
            }
            catch (Exception ex)
            {
                // Best-effort work. A track with no head clip still plays normally. The row records the
                // failure, and the log records it, but nothing else changes.
                _log.LogWarning(ex, "[Cloud] Could not make renditions for {Path}", relativePath);

                // Written on the track when the file itself is what the encoder refused. An example is
                // thirty channels asked down to stereo, or audio that will not decode. That takes the track
                // off this pass for good (see LibraryDatabase.CloudTracksWithLocalFiles). The same bytes
                // through the same ffmpeg always give the same answer. Asking again at every launch would
                // re-encode the same bad file over and over.
                //
                // A dropped connection is different. It is left alone and tried again on the next run.
                Refuse(id, relativePath, ex.Message, CloudFailure.InFull(ex));
            }
            tried++;
        }

        if (made > 0)
            _log.LogInformation("[Cloud] Made renditions for {Count} of the {Tried} tracks tried", made, tried);

        // The two kinds of refusal are logged apart. A permanent refusal would otherwise repeat the same
        // line on every run, and that looks like a new fault.
        if (refused > 0)
            _log.LogInformation(
                "[Cloud] {Count} tracks could not be given renditions this run " +
                "({ForGood} of them for good, {Again} to be tried again)",
                refused, forGood, refused - forGood);

        if (putBack > 0)
            _log.LogWarning(
                "[Cloud] {Count} tracks said they were in the cloud and are not — back in the line", putBack);

        // This is a different fault with a different cure, so it is logged apart from the line above. These
        // tracks are in the cloud, under ids that their rows have lost. Nothing is missing, and nothing
        // will be uploaded twice. The upload pass takes the store's id over (CloudAdoption) and sends
        // nothing.
        if (renamed > 0)
            _log.LogInformation(
                "[Cloud] {Count} tracks are in the cloud under ids their rows had lost — back in the line to take them over",
                renamed);

        // The account is the cause, not the rows. This is said loudly, and the response is to do nothing.
        // Re-uploading a whole collection because the store answered 404 to everything is the one outcome
        // to avoid, even more than a stale row.
        if (CloudRowRepair.SomethingIsWrongWithTheAccount(denied))
        {
            _status = _status with { LastError = new CloudTrouble.ManyTracksMissing() };
            _log.LogError(
                "[Cloud] The store says it holds none of {Count} tracks this library believes are up there. " +
                "Nothing more has been changed — this is an account or a connection, not a row",
                denied);
            Changed?.Invoke();
        }
    }

    /// <summary>How many tracks get renditions per sync run. Encoding uses real CPU on the user's own
    /// machine, and this runs behind their music. A handful per run catches a library up over a few
    /// sessions. That is soon enough for something that only makes playback start faster.
    /// </summary>
    private const int RenditionsPerRun = 10;

    /// <summary>How many rows are reconciled per sync run. This is five requests' worth of catalogue rows.
    /// It is enough for an ordinary library to be caught up in one run. The cap stops a first run over a
    /// library that has drifted for a year from making many requests in a burst. Rows left over are found
    /// again on the next run, because the answer is read from the server, not remembered here.
    /// </summary>
    private const int CatalogPushPerRun = 500;

    /// <summary>How many sidecars are rewritten per run. This is far smaller than the catalogue cap, on
    /// purpose. A catalogue row is a dozen short fields. A sidecar holds the file's whole tag set, with its
    /// cover encoded inside it, so each one is megabytes.
    /// </summary>
    private const int SidecarPushPerRun = 40;

    /// <summary>Makes the cloud agree with the library again: what tracks are called, where they are
    /// filed, what the files are, and the private tag data that a restore rebuilds them from.
    ///
    /// <para><b>This is the only thing that carries an edit off this machine.</b> A track's names are
    /// written exactly once, by its first upload. An upload that matches an encoding somebody already has
    /// never reaches that write. Retagging a file does not change its canonical audio, so it causes no
    /// upload at all. Correcting a misspelled artist, retitling a track, or re-filing the whole library
    /// under a new naming template would otherwise never leave the desktop. Every phone would keep drawing
    /// the library as it was on the day it was uploaded.</para>
    ///
    /// <para>The work is decided by comparison rather than by dirty flags. See <see
    /// cref="CatalogReconciler"/> for why that is the right approach when there are several unrelated edit
    /// paths in the app. It also means that a library which drifted under an older build repairs itself.
    /// </para>
    ///
    /// <para>Best-effort and silent, like the rest of the catch-up work. Whatever does not get through is
    /// found again on the next run.</para>
    /// </summary>
    private async Task RefreshCatalogAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;

        IReadOnlyList<CloudTrack> cloud;
        try
        {
            cloud = await _cloud.ListAsync(CloudShelf.Own, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read the catalog to refresh it");
            return;
        }

        // The listing is the whole catalogue and it is in hand. The records that this machine does not have
        // are as much a part of the user's library as the ones it does. So they are adopted here, rather
        // than waiting for the mirror service's own clock to come round.
        _mirror.Adopt(cloud, ct);

        await SyncMetadataAsync(db, cloud, ct);
    }

    /// <summary>The metadata half of a sync, over a catalogue that somebody has already read. It decides
    /// who is right about each track, what has to be sent, and what has to be taken in.
    ///
    /// <para>This is kept separate from the upload run. An install that sends nothing still draws the
    /// cloud's records, can still be edited, and still has to hear about corrections made elsewhere.
    /// Hanging this off "is there anything to upload" left such an install unaware of changes. It now runs
    /// whenever the catalogue is read, by whoever reads it. See <see
    /// cref="CloudMirrorService.CatalogRead"/>.</para>
    ///
    /// <para><b>One pass at a time.</b> Two callers reach this: the upload run (<see
    /// cref="RefreshCatalogAsync"/>) and the mirror's catalogue read (<see cref="MetadataOnlyPassAsync"/>).
    /// The second checks that no run is going, but a kick can start one just after that check. Then two
    /// passes would write the same rows and the same sidecars, and could stamp a row with an older time
    /// after a newer one. A pass that finds another one running returns. That pass is doing the same work,
    /// and the next catalogue read comes round within a few minutes.</para>
    /// </summary>
    private async Task SyncMetadataAsync(LibraryDatabase db, IReadOnlyList<CloudTrack> cloud, CancellationToken ct)
    {
        if (!await _metadataPass.WaitAsync(0, ct))
            return;

        try
        {
            await SyncMetadataOnceAsync(db, cloud, ct);
        }
        finally
        {
            _metadataPass.Release();
        }
    }

    /// <summary>Held by the metadata pass that is running, if one is. See <see cref="SyncMetadataAsync"/>.
    /// This is not disposed. It never allocates a wait handle, and a pass may still be releasing it while
    /// the service is being disposed.</summary>
    private readonly SemaphoreSlim _metadataPass = new(1, 1);

    /// <summary>The pass itself. Reached only through <see cref="SyncMetadataAsync"/>, which holds
    /// <see cref="_metadataPass"/> for it.</summary>
    private async Task SyncMetadataOnceAsync(LibraryDatabase db, IReadOnlyList<CloudTrack> cloud, CancellationToken ct)
    {
        var root = db.Root;

        // Taken before the rows are read. Every stamp that this pass writes for this machine's side of a
        // row is this time. So an edit made while the pass is running is newer than the stamp, and it still
        // reads as changed here afterwards.
        var readAt = DateTime.UtcNow.Ticks;

        // Both kinds of row that this library has an opinion about: the tracks it holds, and the tracks it
        // has only in the cloud. The second kind matters, because those can be edited here too. A retitled
        // track, or a re-filed record with no local file, is a correction like any other. The mirror is the
        // only place it exists until this pass sends it.
        var offloaded = db.QueryByCloudState(CloudState.CloudOnly, limit: 100_000);

        // Who is right about each track, this library or the store. This is decided per row, in both
        // directions. See CloudSyncPlan for why neither machine's clock is ever compared with the other's,
        // and why the machine in front of the user wins a genuine tie.
        var mine = new List<LibraryTrack>(db.QueryByCloudState(CloudState.Synced, limit: 100_000));
        mine.AddRange(offloaded);

        // The record that each of those folders is, so the rows going up carry the pressing the album is
        // linked to. One query for the whole pass, rather than a lookup per track. An album's rows all read
        // the same answer, and there are far fewer albums than tracks.
        var albums = db.GetAlbumMetaMany(
            mine.Select(t => t.Folder).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase));

        var plan = CloudSyncPlan.Make(mine, cloud, CatalogPushPerRun, albums);

        // Corrections made elsewhere come in first. A row taken in is a row this machine has nothing more
        // to say about. Doing it the other way round would push the stale wording up, and then immediately
        // read it back down.
        var pulled = await ApplyPullsAsync(db, root, plan.Pull, ct);

        var pushFailed = false;
        if (plan.Push.Count > 0)
        {
            // What each row is being sent for. A push that keeps coming back for the same row means a field
            // that one side writes and the other cannot read back. The count on its own cannot tell that
            // apart from a library that is genuinely being corrected.
            if (_log.IsEnabled(LogLevel.Debug))
            {
                var listed = cloud.ToDictionary(t => t.RpmId, StringComparer.Ordinal);
                foreach (var row in plan.Push)
                    if (listed.TryGetValue(row.RpmId, out var theirs))
                        _log.LogDebug("[Cloud] {RpmId} disagrees on {Fields}",
                            row.RpmId, CatalogReconciler.Explain(row, theirs));
            }

            try
            {
                var pushed = await _cloud.PushCatalogAsync(plan.Push, ct);
                if (pushed.Updated > 0)
                {
                    _log.LogInformation("[Cloud] Brought {Count} catalog rows up to date", pushed.Updated);

                    // The store now says what this machine says, on both clocks. The second of those is the
                    // reason the push hands its stamps back. A write moves the store's stamp. A machine
                    // that recorded the stamp from the listing it read at the top of the run would see the
                    // next listing as another machine's change, on every run, for every row it had ever
                    // corrected. That turned one unfixable difference into rows that were pulled and pushed
                    // again at every start.
                    //
                    // A server that predates the per-row stamps sends none, and those rows fall back to the
                    // listing's own stamps. This is the older behaviour, kept for that server.
                    var sent = plan.Push.Select(r => r.RpmId).ToHashSet(StringComparer.Ordinal);

                    // A row that still owes its sidecar takes the store's stamp only. Its local stamp is
                    // the one record that the sidecar has not gone up. The sidecar passes below handle far
                    // fewer rows than this push sends, at most 40 and 20 per run. See
                    // CloudSyncPlan.OwesSidecar.
                    //
                    // The other rows are stamped with readAt, not with the time now. What went up is the
                    // row as it was read. An edit made since then is later than readAt, so it still reads
                    // as changed here. If the row were stamped with the current time, that edit would read
                    // as already sent, and a pull could overwrite it.
                    foreach (var track in mine)
                        if (track.RpmId is { Length: > 0 } id && sent.Contains(id))
                        {
                            var wrote = pushed.Stamps.TryGetValue(id, out var s) ? s : StampOf(cloud, id);
                            if (CloudSyncPlan.OwesSidecar(track))
                                db.NoteCloudStamp(track.Id, wrote);
                            else
                                db.NoteCloudInStep(track.Id, wrote, readAt);
                        }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pushFailed = true;
                _log.LogWarning(ex, "[Cloud] Could not push the catalog");
            }
        }

        await RefreshSidecarsAsync(db, root, plan.Sidecars, ct);

        // Only the cloud-only rows that this run actually corrected are handled here. The pass
        // compares each sidecar with the row, which means downloading it. A sidecar is the file's whole tag
        // set, with its cover encoded inside it. So handing the pass every offloaded track would cost a
        // request to the service, a fetch from storage, and about a megabyte, for every cloud-only track, on
        // every run. The cap only limits what is written, so a library with nothing to edit would fetch
        // everything and write nothing. A row that nobody edited cannot have a sidecar to patch, so this is
        // the list of the rows that somebody did edit.
        //
        // The rows edited here whose sidecar is still owed from an earlier run are included too, once the
        // catalogue agrees with them. The push above sends up to 500 rows, and this pass fetches 20.
        // Without the earlier rows, the rest of a large edit would never reach a sidecar. The plan decides
        // which rows qualify. See CloudSyncDecision.CloudOnlySidecars. A row that the push failed to send
        // waits as well. Patching its sidecar and stamping it would leave the catalogue behind with nothing
        // saying so.
        var corrected = plan.Push.Select(row => row.RpmId).ToHashSet(StringComparer.Ordinal);
        var due = plan.CloudOnlySidecars
            .Where(id => !(pushFailed && corrected.Contains(id)))
            .ToHashSet(StringComparer.Ordinal);
        await RefreshCloudOnlySidecarsAsync(
            db, [.. offloaded.Where(t => t.RpmId is { Length: > 0 } id && due.Contains(id))], readAt, ct);

        // And the marks on whole records, which have no catalogue row to ride on. This is the same pass,
        // because it asks the same question about a bigger thing. See CloudSyncService.AlbumMarks.
        await SyncAlbumMarksAsync(db, ct);

        // Play history is handled the same way. These rows belong to the listener, not to the music, and
        // they are kept in step between the listener's machines. See CloudSyncService.Plays.
        await SyncPlaysAsync(db, ct);

        // Radio listens are handled the same way. See CloudSyncService.StationListens.
        await SyncStationListensAsync(ct);

        // Saved stations are kept in step in the same way. See CloudSyncService.StationFavorites.
        await SyncStationFavoritesAsync(ct);

        // Playlists made somewhere else, on a phone or on another of the user's computers, are pulled here.
        //
        // This happens here, not beside the push, and the difference matters. The push runs once per
        // session, and again after any run that uploaded. That is right for it, because a playlist this
        // machine owns only changes when this machine changes it. Taking playlists in is the opposite. It
        // is news from elsewhere, and a computer left on all day would never hear about it. The album marks
        // are read on every pass for the same reason.
        //
        // This must come after ApplyPullsAsync above. A playlist is taken in whole or not at all. So the
        // pass that brings down the songs of a phone-made playlist is also the pass that can take the
        // playlist itself. See CloudPlaylistIntake.
        await PullCloudPlaylistsAsync(db, ct);

        // The rows taken in have changed, and the mirror pass has already read past them. Read the mirror
        // once more, so that the catalogue's stamps for those rows are the ones this library now holds.
        if (pulled > 0)
            await _mirror.RefreshAsync(force: true, ct);
    }

    /// <summary>
    /// The store's stamp on a row, as the listing read by this run showed it.
    ///
    /// <para>This is the OLD stamp, not the current time. The push has just written the row, so the store's
    /// stamp is newer than anything this machine can know without asking again. Recording the stamp that
    /// was seen means the next listing reads as changed since the push's own write. The comparison then
    /// finds nothing to do, because by then the two agree. Claiming a stamp that was never seen would be
    /// the one way to miss somebody else's edit that landed in the same second.</para>
    /// </summary>
    private static long StampOf(IReadOnlyList<CloudTrack> cloud, string rpmId)
        => cloud.FirstOrDefault(t => string.Equals(t.RpmId, rpmId, StringComparison.Ordinal))
                ?.UpdatedAt ?? 0;

    /// <summary>How many cloud-only sidecars are patched per run. A sidecar holds the file's whole tag set,
    /// with its cover encoded inside it, so this number is kept small. The pass only has work to do just
    /// after somebody edited a record that this machine does not hold.</summary>
    private const int CloudOnlySidecarsPerRun = 20;

    /// <summary>
    /// Writes an edit made to a cloud-only track into the sidecar that the store keeps beside it.
    ///
    /// <para>The catalogue push above is what every device draws. The sidecar is what a download
    /// eventually writes into the file. Without this, correcting a misspelled artist on a record that is
    /// only in the cloud would look right everywhere, and then come back with the old spelling on the day
    /// the user brought it home. The ordinary sidecar refresh cannot do it, because it reads the file, and
    /// there is no file.</para>
    ///
    /// <para>Each sidecar is fetched, compared, and written only when it actually differs. Otherwise every
    /// run would re-upload a megabyte per track, for ever. This is best effort, like the rest of the
    /// catch-up work.</para>
    ///
    /// <para><b>The rows passed in are only the rows that somebody edited.</b> These are the rows this run
    /// just corrected, and the rows whose sidecar is still owed from an earlier run. Fetching is not free.
    /// Every track looked at is a request to the service, a fetch from storage, and a megabyte down the
    /// wire. Handed the whole offloaded list, a library with nothing to do would fetch all of it and write
    /// none of it.</para>
    ///
    /// <para><b>The cap counts fetches, not writes.</b> The list includes rows owed from earlier runs. A
    /// fetch that finds the sidecar already agreeing costs the same request as one that writes.</para>
    ///
    /// <para>A row that owed its sidecar is stamped once the sidecar is written, found to agree, or found
    /// not to exist. That stamp is what takes it off the list. A fetch or write that failed leaves it owed
    /// for the next run.</para>
    /// </summary>
    /// <param name="readAt">When the rows in <paramref name="tracks"/>
    /// were read. The stamp, so an edit made after the read is still owed.</param>
    private async Task RefreshCloudOnlySidecarsAsync(
        LibraryDatabase db, IReadOnlyList<LibraryTrack> tracks, long readAt, CancellationToken ct)
    {
        if (tracks.Count == 0) return;

        var sent = 0;
        var asked = 0;

        foreach (var track in tracks)
        {
            if (ct.IsCancellationRequested || asked >= CloudOnlySidecarsPerRun) break;
            if (track.RpmId is not { Length: > 0 } id) continue;
            asked++;

            try
            {
                var sidecar = await _cloud.GetSidecarAsync(id, ct);

                bool done;
                if (sidecar is null || sidecar.Matches(track))
                {
                    done = true;    // no sidecar under this id, or it already agrees: nothing to write
                }
                else if (await _cloud.RefreshSidecarAsync(id, sidecar.With(track), ct))
                {
                    done = true;
                    sent++;
                }
                else
                {
                    done = false;
                }

                if (done && CloudSyncPlan.OwesSidecar(track))
                    db.NoteCloudMetadata(track.Id, readAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[Cloud] Could not patch the sidecar of {Path}", track.RelativePath);
            }
        }

        if (sent > 0)
            _log.LogInformation(
                "[Cloud] Wrote {Count} edits into the sidecars of tracks this machine does not hold", sent);
    }

    /// <summary>
    /// Sends the tags of files that have been written since they were uploaded.
    ///
    /// <para>The sidecar is the private half. It holds the full tag set, plus the cover embedded in the
    /// file, and a restore puts it back. Without this, a track retagged a year after upload would come back
    /// with the tags it had on its first day.</para>
    /// </summary>
    private async Task RefreshSidecarsAsync(
        LibraryDatabase db, string root, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var byId = new HashSet<string>(ids, StringComparer.Ordinal);
        var sent = 0;

        foreach (var track in db.QueryByCloudState(CloudState.Synced, limit: 100_000))
        {
            if (ct.IsCancellationRequested || sent >= SidecarPushPerRun) break;

            if (track.RpmId is not { Length: > 0 } id || !byId.Contains(id)) continue;

            var path = Path.Combine(root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // Offloaded, or moved away since the mirror was last scanned. There are no tags to read.
            if (!File.Exists(path)) continue;

            try
            {
                // Taken before the file is read, and used as the stamp below. A write to the file after
                // this moment is not in the sidecar being sent. Its mtime is later than the stamp, so the
                // file still reads as owed. A stamp taken after the send would hide a write that was never
                // sent.
                var readAt = DateTime.UtcNow.Ticks;
                var sidecar = (await Task.Run(() => TagSidecar.FromFile(path, root)))
                    .WithFavorite(track.IsFavorite, track.FavoritedUtcTicks);

                if (await _cloud.RefreshSidecarAsync(id, sidecar, ct))
                {
                    // The file is now no newer than what the cloud holds. That stops the next run from
                    // sending it again. This is the same column that the upload stamps, with the same
                    // meaning.
                    db.NoteCloudMetadata(track.Id, readAt);
                    sent++;
                }

                // The cover that the library shows may have changed with the tags. Covers are stored per
                // album, not per track, so this asks once per album, and the set drops the repeats.
                await UploadAlbumArtAsync(path, sidecar, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[Cloud] Could not refresh the sidecar for {Path}", track.RelativePath);
            }
        }

        if (sent > 0)
            _log.LogInformation("[Cloud] Sent the tags of {Count} edited tracks", sent);
    }

    private async Task RepairAlbumArtAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (!_auth.IsLoggedIn) return;
        var root = db.Root;

        Dictionary<string, long> stored;
        try
        {
            stored = (await _cloud.GetAlbumArtCatalogAsync(CloudShelf.Own, ct))
                .ToDictionary(c => c.AlbumKey, c => c.UpdatedAt, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not read which album covers are stored");
            return;
        }

        var moved = 0;

        // One row per folder from both lists, so a folder cannot arrive here under two album keys. See
        // LibraryDatabase.CloudAlbumsIn for what that cost. The two lists are still two lists. A folder
        // that holds both synced and offloaded tracks is in each of them, so the folder is claimed here as
        // well as the key.
        //
        // Offloaded albums are included in this pass too. They have no file to read art from. A picture
        // chosen for one is still written into its album folder, and this machine shows the record from
        // that folder. So the phone should not be left holding the old picture.
        foreach (var (albumArtist, artist, album, relativePath) in
                 db.CloudAlbumsWithLocalFiles().Concat(db.OffloadedAlbums()))
        {
            if (ct.IsCancellationRequested || moved >= ArtRepairPerRun)
                break;

            var key = AlbumArt.KeyForTrack(albumArtist, artist, album);
            if (!_artDone.Add(key))
                continue;

            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(path);

            // The folder is where both pictures are read from and written to. Its cover.jpg is the only
            // record of when this end last changed. A second album in the same folder would reconcile
            // against the first album's file.
            if (folder is { Length: > 0 } && !_artFolders.Add(folder))
                continue;

            stored.TryGetValue(key, out var theirs);
            stored.TryGetValue(AlbumArt.DiscKeyForTrack(albumArtist, artist, album), out var theirDisc);

            var cover = await ReconcilePictureAsync(
                new PictureSlot(
                    key, album, "cover",
                    () => File.Exists(path) ? AlbumArt.ForFile(path) : Local(folder, AlbumArt.ForFolder),
                    () => folder is { Length: > 0 } && Directory.Exists(folder)
                        ? FolderArtWriter.FindFolderImage(folder) ?? path
                        : path,
                    () => folder is { Length: > 0 } && Directory.Exists(folder),
                    bytes => FolderArtWriter.WriteCover(folder!, bytes)
                        ? FolderArtWriter.FindFolderImage(folder!)
                        : null),
                theirs, ct);

            // The label printed on the record. It is never stored in a tag or a sidecar. So the folder is
            // the only place it can be read from, and the only place it can be written.
            var disc = ct.IsCancellationRequested || moved + cover.Moved >= ArtRepairPerRun
                ? default
                : await ReconcilePictureAsync(
                    new PictureSlot(
                        AlbumArt.DiscKeyForTrack(albumArtist, artist, album), album, "disc label",
                        () => Local(folder, AlbumArt.DiscForFolder),
                        () => folder is { Length: > 0 } ? FolderArtWriter.FindDiscImage(folder) : null,
                        () => folder is { Length: > 0 } && Directory.Exists(folder),
                        bytes => FolderArtWriter.ReplaceDisc(folder!, bytes)
                            ? FolderArtWriter.FindDiscImage(folder!)
                            : null),
                    theirDisc, ct);

            moved += cover.Moved + disc.Moved;

            // Only when a picture came down. The card on the wall may be holding a picture that is now out
            // of date. An upload changes nothing on this screen. Only one folder is named, so one card
            // re-reads its cover, and the rest of the wall does not move.
            if (cover.Pulled || disc.Pulled)
                AlbumArtChanged?.Invoke(FolderOf(relativePath));
        }

        if (moved > 0)
            _log.LogInformation("[Cloud] {Count} album pictures were missing or out of date", moved);

        static AlbumCover? Local(string? folder, Func<string, AlbumCover?> read)
            => folder is { Length: > 0 } && Directory.Exists(folder) ? read(folder) : null;

        static string FolderOf(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            return slash < 0 ? "" : relativePath[..slash];
        }
    }

    /// <summary>One picture of one album, as this pass has to see it: what it is called, where to read it
    /// from here, which file's timestamp says when it was last changed here, whether it has anywhere to
    /// land, and how to put a downloaded one in its place.</summary>
    /// <param name="HasSomewhereToLand">Whether the album folder is on this disk at all. This is asked
    /// BEFORE a download, not after. An offloaded record has no folder, so fetching its picture first would
    /// pull a megabyte on every run only to throw it away. The store keeps showing the picture either way. That is
    /// where the wall reads a cloud-only record's cover from.</param>
    /// <param name="Write">Writes the
    /// picture into the album folder and hands back the file it landed in, or null when it would not
    /// go.</param>
    private sealed record PictureSlot(
        string StoreKey, string? Album, string What,
        Func<AlbumCover?> Read, Func<string?> TimestampPath,
        Func<bool> HasSomewhereToLand, Func<byte[], string?> Write);

    /// <summary>
    /// Makes one picture the same here and in the cloud, whichever end has the newer one.
    ///
    /// <para>Up when this machine's copy is newer, or when the store has none. A corrected sleeve reaches
    /// every other device. Down when the store's copy is newer. A sleeve corrected at another computer
    /// reaches this one. Without the download half, an album could look right on one machine and wrong on
    /// the next, for ever.</para>
    ///
    /// <para>A downloaded picture is stamped with the store's own time, not with the current time.
    /// Otherwise the file this run just wrote would look newer than what it came from. The next run would
    /// send it straight back up, and two machines would trade the same picture for as long as both are
    /// signed in.</para>
    /// </summary>
    /// <returns>Whether a picture moved, and in which direction. Only a
    /// download changes anything on this screen.</returns>
    private async Task<(int Moved, bool Pulled)> ReconcilePictureAsync(
        PictureSlot slot, long theirs, CancellationToken ct)
    {
        var minePath = slot.TimestampPath();
        var mine = minePath is { Length: > 0 } && File.Exists(minePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(minePath)).ToUnixTimeMilliseconds()
            : 0;

        try
        {
            // The store's copy is newer, so bring it down into the album's own folder. Every local reader,
            // such as the wall, the turntable and the editor, then sees it without asking the network
            // again.
            if (theirs > 0 && theirs > mine && slot.HasSomewhereToLand())
            {
                if (await _cloud.GetAlbumArtUrlAsync(CloudShelf.Own, slot.StoreKey, ct) is not { Length: > 0 } url)
                    return default;

                var bytes = await Pictures.GetByteArrayAsync(url, ct);
                if (bytes.Length == 0 || slot.Write(bytes) is not { } written)
                    return default;

                Stamp(written, theirs);
                _log.LogInformation("[Cloud] Took down the {What} for {Album}", slot.What, slot.Album);
                return (1, true);
            }

            // Ours is newer, or the store has none at all.
            if (theirs > 0 && mine > 0 && mine <= theirs)
                return default;
            if (slot.Read() is not { } picture)
                return default;

            if (!await _cloud.UploadAlbumArtAsync(slot.StoreKey, picture, ct))
                return default;

            _log.LogInformation("[Cloud] Sent up the {What} for {Album} ({Source})",
                slot.What, slot.Album, picture.Source);
            return (1, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not reconcile the {What} for {Album}", slot.What, slot.Album);
            return default;
        }
    }

    /// <summary>The client that pictures are downloaded with. The URLs are presigned, so no header of ours
    /// is sent. The timeout is short, because a sleeve is never worth holding a sync run up for.</summary>
    private static readonly System.Net.Http.HttpClient Pictures =
        RPM.Core.Net.Wire.Outside(RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(20));

    /// <summary>Puts the store's own time on a picture that was just downloaded, so this end and that one
    /// read as the same age, and neither sends it back.
    ///
    /// <para>The value is read back rather than assumed. This stamp is the only thing that stops the next
    /// run from fetching the same picture again. If the stamp does not take, the cost is not one extra
    /// round trip. It is one round trip on every run for as long as the file is there. So a failed stamp is
    /// logged.</para>
    /// </summary>
    private void Stamp(string path, long unixMs)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime);

            var kept = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds();
            if (kept != unixMs)
                _log.LogWarning(
                    "[Cloud] {Path} kept the date {Kept} rather than the store's {Store}, so this picture will be fetched again next run",
                    path, kept, unixMs);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "[Cloud] Could not date {Path} by the store's clock, so this picture will be fetched again next run", path);
        }
    }

}

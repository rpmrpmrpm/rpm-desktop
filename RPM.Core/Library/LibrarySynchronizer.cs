using RPM.Core.Cloud;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Scanning;

namespace RPM.Core.Library;

/// <summary>What a sync pass changed in the mirror. <paramref name="Moved"/> counts rows whose file was
/// recognized at a new path by its RPM id. Those rows keep their row identity, so they are not counted as a
/// removal plus an addition. <paramref name="Quarantined"/> counts files that were copied into the
/// collection from outside. Those files were moved to the inbox instead of being adopted where they lay
/// (see <see cref="LibraryInbox"/>).</summary>
public sealed record SyncResult(int Added, int Updated, int Removed, int Moved = 0, int Quarantined = 0);

/// <summary>Progress snapshot while tags of new/changed files are read.</summary>
public sealed record SyncProgress(int Processed, int Total, string CurrentFile);

/// <summary>Reconciles the SQLite mirror with the library folder. Files that appeared or changed (their
/// size or modified time differs) get their tags read again into the mirror. Rows whose file has vanished
/// are dropped. An unchanged file costs one dictionary lookup, with no tag I/O. So a sync over a large
/// library that rarely changes is fast.
///
/// Files carry an RPM id in their own tags (<see cref="RpmTag"/>). This is what lets the mirror survive the
/// user rearranging the folder by hand. A file that shows up at a new path with a known id is a move, so
/// its row is repointed rather than dropped and created again. Sync also stamps the id. Files with no id,
/// such as older imports, get one. Files that share an id (copied outside the app) get a new one.
///
/// <para>There is one exception, and it matters. A file whose row says its bytes are in the cloud is given
/// back the id that the store knows it by, not a new one. That id is the track's name in the store. The row
/// is the only thing on this computer that still records it. See <see cref="CloudName"/>.
/// </para>
///
/// <para>One row breaks the rule that a row has a file: a cloud-only row
/// (<see cref="CloudState.CloudOnly"/>), whose bytes exist only in the cloud. Its file is missing by
/// design, so it is never counted as vanished and never removed. A file that turns up carrying its id is
/// that track coming back. The row is repointed at that file and set back to
/// <see cref="CloudState.Synced"/>.
/// </para>
///
/// <para><b>Foreign files.</b> A file that turns up in the collection with no id, at a path that no row
/// knows, was copied in from outside. It may have come from a file manager, a sync client or another music
/// app. Adopting it where it lies would spoil a curated library. The file would keep whatever folder name
/// and tags it arrived with, and no later scan could work out what it was meant to be. With
/// <c>quarantineForeign</c>, the file is moved into <see cref="LibraryInbox"/> instead. The import queue
/// then picks it up, and it goes through the same identification and filing as anything the user added
/// through the app.
/// </para>
/// </summary>
public static class LibrarySynchronizer
{
    /// <param name="readTrack">Tag reader override for tests; defaults to
    /// <see cref="TagReader.Read"/>.</param>
    /// <param name="stampId">Stamps an RPM id into a file and returns it. When force is true, a fresh id
    /// is minted even if the file already carries one. Override for tests; defaults to
    /// <see cref="TagReader.StampRpmId"/>.
    /// </param>
    /// <param name="keepId">Writes a particular id into a file: the one that the store already knows that
    /// track by. Override for tests; defaults to <see cref="TagReader.WriteRpmId"/>.
    /// </param>
    /// <param name="quarantineForeign">Moves files that were copied into the collection from outside into
    /// <see cref="LibraryInbox"/>, rather than adopting them where they lie. This is off by default,
    /// because the caller knows whether this mirror has ever been reconciled. See the guard on an empty
    /// mirror below.
    /// </param>
    public static SyncResult Sync(
        LibraryDatabase db,
        Func<string, TrackInfo>? readTrack = null,
        IProgress<SyncProgress>? progress = null,
        CancellationToken ct = default,
        Func<string, bool, string?>? stampId = null,
        bool quarantineForeign = false,
        Func<string, string, bool>? keepId = null)
    {
        readTrack ??= TagReader.Read;
        stampId ??= TagReader.StampRpmId;
        keepId ??= TagReader.WriteRpmId;

        var known = db.Snapshot();

        // A library on an external drive that is not plugged in does not scan as an empty library. It scans
        // as a folder that is not there. Reconciling against it would read every track as deleted and empty
        // the mirror. That is how a user would lose their library by unplugging a disk. Nothing is missing
        // if the place it lives is missing.
        if (!Directory.Exists(db.Root))
            throw new DirectoryNotFoundException(
                $"The library folder is not there: {db.Root}. Nothing was changed.");

        // The inbox is not part of the collection. Everything in it is waiting to be imported. A row for it
        // would put unfiled music on the shelf. Also, once the import moved a file out, that row would look
        // like a track the user had deleted.
        var scan = AudioFileScanner.Scan(db.Root);
        var onDisk = scan.Files
            .Where(f => !LibraryInbox.Contains(
                Path.GetRelativePath(db.Root, f).Replace(Path.DirectorySeparatorChar, '/')))
            .ToList();

        // Folders the scan could not list: no permission, a share that dropped, or a folder removed during
        // the scan. Their files are not in onDisk. Without this check, every row under them would be read
        // as deleted below, and removed along with its plays, favourites and identity. The paths are in the
        // mirror's relative form so they compare with row paths. The root itself comes out as ".".
        var unlisted = scan.Unreadable
            .Select(d => Path.GetRelativePath(db.Root, d).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        bool UnderUnlisted(string relative) => unlisted.Exists(
            d => d == "." || relative.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase));

        if (unlisted.Count > 0 && RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said(
                "sync", $"could not list {unlisted.Count} folder(s), rows under them kept: " +
                        string.Join(", ", unlisted.Take(5)));

        // Ids held by more than one row WHOSE FILE IS ON DISK.
        //
        // A duplicated id makes a row pending. That is correct where this pass can do something about it:
        // the second file to claim an id is stamped again with one of its own. The pass can only stamp a
        // file it has. An id shared with a row whose file is NOT here (an offloaded row, or a row from
        // another computer) cannot be fixed by anybody. There is nothing to write a new id into, and the
        // row's id is its name in the store, which must not be minted again.
        //
        // If this check asked the database for every duplicate id, the answer would include those rows. The
        // file on disk would then come back pending on EVERY sync and never settle. Each sync would report
        // rows as updated, which raises LibraryService.Changed. That makes the home screen discard the
        // screen it has just built and read the whole shelf again. On a 13,000-track library, six FLAC
        // files of one album were re-read on every launch. That re-read took 6,802 ms, against 1,038 ms for
        // the first read. All 58 of its pictures were fetched a second time, at the moment the app became
        // visible. This check does not repair the duplicate. It only stops the re-read that has nothing to do.
        var onDiskRows = new HashSet<string>(
            onDisk.Select(f => Path.GetRelativePath(db.Root, f).Replace(Path.DirectorySeparatorChar, '/')),
            StringComparer.OrdinalIgnoreCase);

        var duplicateIds = known
            .Where(kv => onDiskRows.Contains(kv.Key) && RpmTag.IsId(kv.Value.RpmId))
            .GroupBy(kv => kv.Value.RpmId!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        // A mirror with nothing in it is a library being adopted. The user pointed the app at a folder of
        // music they already had. Every file in it is unknown and has no stamp, which is exactly what a
        // copied-in file looks like. Quarantining here would move somebody's whole collection into the
        // inbox on the first run. The first pass takes the folder as it is. From then on, anything that
        // appears in it is something that arrived.
        bool quarantine = quarantineForeign && known.Count > 0;

        // The folder is there, but every track that should be in it is gone. This is not a user deleting
        // their collection one file at a time. It is a mount that came up empty, a cloud drive still
        // restoring, or a wrong root. Reconciling against it would empty the mirror.
        //
        // Offloaded rows are left out of the count, because their files are meant to be missing. A library
        // that the user has fully offloaded has no audio in it at all, and that is not a fault. Rows held
        // by another of the user's computers are left out for the same reason.
        int expectedOnDisk = known.Count(kv => !kv.Value.Elsewhere);
        if (onDisk.Count == 0 && expectedOnDisk > 0)
            throw new InvalidOperationException(
                $"The library folder is empty but the mirror holds {expectedOnDisk} tracks that should " +
                $"be in it: {db.Root}. Nothing was changed.");

        // Diff pass: which files need a tag read? These are new files, changed files, files that were never
        // stamped, and files that share their id with another row. The last two are the cases this sync
        // exists to repair. A row already marked NoCarrier counts as settled. Re-reading it on every sync
        // would never do anything but fail again.
        var pending = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Rows whose bytes were offloaded to the cloud, but whose file is on disk again. The user restored
        // it, or copied it back in. The cloud copy is still there, so the track is Synced, not CloudOnly,
        // and playback should read the disk instead of streaming.
        var reappeared = new List<long>();

        // Rows that belonged to another computer, and whose file is now here. This happens when the file is
        // imported from that machine, or when the same rip was made on this one. From that moment the row
        // belongs to this library. If it stayed marked, it would vanish the next time that computer's
        // listing changed.
        var arrived = new List<long>();

        foreach (var file in onDisk)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(db.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            seen.Add(relative);

            var fi = new FileInfo(file);
            if (known.TryGetValue(relative, out var row))
            {
                if (row.CloudState == CloudState.CloudOnly)
                    reappeared.Add(row.Id);

                if (row.PeerAdopted)
                    arrived.Add(row.Id);

                if (row.SizeBytes == fi.Length
                    && row.ModifiedUtcTicks == fi.LastWriteTimeUtc.Ticks
                    && row.RpmId is { } id // null = never stamped. An empty id is not a valid one.
                    && !duplicateIds.Contains(id)
                    // null = the file's stream facts have never been read. This is true of every row in a
                    // mirror written before the schema had these columns. Reading them needs a tag read,
                    // which this gate exists to avoid. So each track is read once, and never again, the
                    // same way the id stamp works. A file that gives no answer stores zero. It then
                    // settles, rather than being re-read on every pass forever.
                    && row.Channels is not null
                    // The same arrangement, one column over. null = the file's advisory flag has never been
                    // checked. It cannot be inferred from a changed file, because the files that already
                    // carry the right flag are the ones that nothing ever rewrote.
                    && row.Advisory is not null
                    // And once more, for the work, the movement and the composer. null = never read. One
                    // tag read per track is enough, and then a stored number settles it.
                    && row.MovementNumber is not null
                    && !row.Elsewhere)
                    continue;

                // A row that comes back pending on a library where nothing has happened will come back
                // pending on every launch, forever. Each time, this pass claims that the shelf moved. That
                // sends every view in the app back to the database. Six such rows on a 13,000-track library
                // caused a full re-read of the home screen at the moment it became visible. So the reason
                // is recorded instead of guessed. To read it, use: rpmscope log --channel sync
                if (RPM.Core.Diagnostics.Scope.On)
                {
                    var why =
                        row.SizeBytes != fi.Length ? "size"
                        : row.ModifiedUtcTicks != fi.LastWriteTimeUtc.Ticks ? "mtime"
                        : row.RpmId is not { } had ? "no id"
                        : duplicateIds.Contains(had) ? "duplicate id"
                        : row.Channels is null ? "channels never read"
                        : row.Advisory is null ? "advisory never read"
                        : row.MovementNumber is null ? "movement never read"
                        : row.Elsewhere ? "elsewhere"
                        : "nothing (a condition above has moved)";

                    RPM.Core.Diagnostics.Scope.Said("sync", $"re-reading {relative}: {why}");
                }
            }

            pending.Add(file);
        }

        // Where each id sits right now. An id can still be on disk, in which case a second file that claims
        // it is a copy. An id can be orphaned, because the file that had it has vanished. A new file that
        // claims it is then that file, moved. Or an id can be offloaded. An offloaded row looks like an
        // orphan but is not one. It is missing its file on purpose and must never be dropped. A file that
        // carries its id really is that track coming home, so it is tracked separately rather than ignored.
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        var orphanedById = new Dictionary<string, string>(StringComparer.Ordinal);
        var offloadedById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, row) in known)
        {
            if (!RpmTag.IsId(row.RpmId) || duplicateIds.Contains(row.RpmId!))
                continue;
            if (seen.Contains(path))
                claimed[row.RpmId!] = path;
            else if (row.Elsewhere)
                offloadedById[row.RpmId!] = path;
            else
                orphanedById[row.RpmId!] = path;
        }

        // A cloud-only track's file is supposed to be missing. That is what cloud-only means. Without this
        // rule, the first sync would read every one of them as deleted and drop the rows. The user's
        // library would then vanish silently.
        //
        // A row under a folder that the scan could not list is also left out. Its file was not looked for,
        // so it was not found missing either.
        var missing = known
            .Where(kv => !seen.Contains(kv.Key) && !kv.Value.Elsewhere && !UnderUnlisted(kv.Key))
            .ToList();

        // A row that the store is holding is never a deletion either, whatever its file did.
        //
        // Synced means both copies exist. If the file is no longer here, the user has taken it off this
        // disk. Deleting music to make room is how somebody is meant to make room. It says nothing about
        // what they want kept in the cloud. Their copy stays up there until they delete the track in rpm
        // and say so. So the row becomes what it now is, a cloud row. The album stays in the library,
        // playable, and it can be brought back down.
        //
        // The other reason this must not count as a deletion: a drive that was not plugged in, or a folder
        // that has moved, looks exactly like this from here. The row is the only thing that knows the
        // recording is in the store. This rule stops an album from going missing in that gap.
        var stranded = missing.Where(kv => kv.Value.CloudState == CloudState.Synced).ToList();

        var gone = new HashSet<string>(
            missing.Where(kv => kv.Value.CloudState != CloudState.Synced).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);

        int added = 0, updated = 0, moved = 0, quarantined = 0;
        var upserts = new List<LibraryTrack>(pending.Count);
        for (int i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = pending[i];
            progress?.Report(new SyncProgress(i, pending.Count, Path.GetFileName(file)));

            var relative = Path.GetRelativePath(db.Root, file).Replace(Path.DirectorySeparatorChar, '/');

            TrackInfo info;
            try
            {
                info = readTrack(file);
            }
            catch
            {
                // A row that already exists keeps what it has. A read that throws says nothing about the
                // tags. The file may be locked, half copied, or on a share that dropped. Writing the empty
                // result over the row would erase its title, artist and album. So the row is left as it
                // was, size and date included, and the next pass reads it again.
                if (known.ContainsKey(relative))
                    continue;

                // An unreadable new file. Record the path with whatever the file name gives us. The sync
                // must not fail because of it.
                info = new TrackInfo { FilePath = file, Extension = Path.GetExtension(file).ToLowerInvariant() };
            }
            string? id = info.RpmId;

            // This file carries none of our ids and sits where no row expects it, so it was copied into the
            // collection from outside. It goes to the inbox to be identified and filed like anything else.
            // It is not adopted under whatever folder name it arrived with.
            //
            // A file that will not move is left alone and not mirrored. The next pass tries again. The
            // usual reason a move fails here is that the copy putting the file there is still running. Half
            // a file must not go into the collection.
            if (quarantine && !RpmTag.IsId(id) && !known.ContainsKey(relative))
            {
                if (LibraryInbox.Quarantine(db.Root, file) is not null)
                    quarantined++;
                continue;
            }

            // Another file already holds this id. This is a copy made outside the app. Without a check, two
            // files would claim one row. The first file to claim an id keeps it. Every later file gets an
            // id of its own.
            bool isCopy = RpmTag.IsId(id)
                && claimed.TryGetValue(id!, out var holder)
                && !holder.Equals(relative, StringComparison.OrdinalIgnoreCase);

            if (!RpmTag.IsId(id) || isCopy)
            {
                // Except where the row is the last thing that remembers the store's name for this track. A
                // file can come back from a retag without the id it went in with. The tags were rewritten,
                // and the id went with them. Minting a fresh id here would make records show as backed up
                // under names that the account never had. The id IS the track's name in the store. So it is
                // written back into the file and kept on the row. See CloudName. The tag write is best
                // effort. The row is not.
                var itsCloudName = known.TryGetValue(relative, out var claimant)
                    ? CloudName.TheStoreCallsIt(claimant.CloudState, claimant.RpmId)
                    : null;

                if (itsCloudName is not null)
                {
                    keepId(file, itsCloudName);
                    id = itsCloudName;
                }
                else
                {
                    // Stamping rewrites the file, so its size and modified time change. FromTrackInfo reads
                    // them again below, so it must run after this. Otherwise the next sync would see a
                    // difference. A file that cannot take a stamp (an unsupported container, or too corrupt
                    // to open) is marked as such. This is the last sync that will ever open it.
                    id = stampId(file, isCopy) ?? RpmTag.NoCarrier;
                }

                info = info with { RpmId = id };
            }

            var track = LibraryDatabase.FromTrackInfo(info, db.Root);

            // An offloaded track's file has been found back on disk somewhere else. The id says it is that
            // track, so the row follows the file. A second row does not appear beside it.
            if (RpmTag.IsId(id) && offloadedById.TryGetValue(id!, out var offloadedAt)
                && !offloadedAt.Equals(track.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                db.Move(offloadedAt, track);
                reappeared.Add(known[offloadedAt].Id);
                offloadedById.Remove(id!);
                claimed[id!] = track.RelativePath;
                moved++;
                continue;
            }

            if (RpmTag.IsId(id) && !known.ContainsKey(track.RelativePath)
                && orphanedById.TryGetValue(id!, out var from) && gone.Contains(from))
            {
                // Same id, the file is gone from its old path, and it is now present at a new one. The user
                // moved it. The row is repointed in place, so its identity and everything attached to it is
                // kept.
                db.Move(from, track);
                gone.Remove(from);
                orphanedById.Remove(id!);
                claimed[id!] = track.RelativePath;
                moved++;
                continue;
            }

            if (RpmTag.IsId(id))
                claimed[id!] = track.RelativePath;

            upserts.Add(track);
            if (known.ContainsKey(track.RelativePath)) updated++; else added++;
        }
        db.UpsertMany(upserts);

        db.RemoveMany(gone);

        // The file is not here, and the store has it, so the row is a cloud row now. It is kept, not
        // dropped. See the note where stranded is built. MarkOffloaded is used rather than a plain state
        // write. That way the check that the store holds this track is part of the UPDATE itself.
        foreach (var (_, row) in stranded)
            db.MarkOffloaded(row.Id, row.RpmId);

        // The upsert above leaves cloud columns alone on purpose. So a row whose file came back is still
        // marked CloudOnly. It now has both copies.
        //
        // It is also this library's own row from now on. A row adopted from the cloud catalogue lives and
        // dies with that listing (see LibraryDatabase.MirrorCloudLibrary). Once its file is on this disk,
        // it is a track like any other. It must not vanish because somebody signed out.
        foreach (var id in reappeared)
        {
            db.SetCloudState(id, CloudState.Synced);
            db.ClearAdopted(id);
        }

        foreach (var id in arrived)
            db.ClearPeerAdopted(id);

        // Files that left for the inbox usually leave their folders behind. The aim is that the collection
        // ends up as if the copy had never landed there.
        if (quarantined > 0)
            Import.OrganizeApplier.CleanupEmptyFolders(db.Root);

        progress?.Report(new SyncProgress(pending.Count, pending.Count, ""));
        return new SyncResult(added, updated, gone.Count, moved, quarantined);
    }
}

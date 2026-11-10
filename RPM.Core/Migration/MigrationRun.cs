using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Migration;

/// <summary>What the run needs from the cloud. Ratings and plays are not here: the cloud sync sends
/// them on its own passes.</summary>
public interface IMigrationCloud
{
    bool SignedIn { get; }

    /// <summary>Bytes used, the plan's limit, and whether the plan has no limit; null when the quota
    /// could not be read (no connection, or the server failed). Null is not "full".</summary>
    Task<(long Used, long Quota, bool Unlimited)?> QuotaAsync(CancellationToken ct);

    /// <summary>Whether the upload workers can take a file of this size now.</summary>
    CloudUploads CanUpload(long bytes);

    /// <summary>Moves the date added of tracks the cloud holds earlier (unix seconds), at most 200.</summary>
    Task PutTrackAddedAsync(IReadOnlyList<(string RpmId, long AddedAtUnix)> rows, CancellationToken ct);

    /// <summary>Asks the cloud sync to look for tracks to upload now, instead of at its next pass.</summary>
    void RequestUpload();

    /// <summary>Puts a playlist in the cloud, as "Add to cloud" on the playlist does.</summary>
    void AddPlaylist(long playlistNodeId);
}

/// <summary>What the upload workers can do now.</summary>
public enum CloudUploads
{
    /// <summary>They upload what is chosen for the cloud.</summary>
    Ready,

    /// <summary>Nothing is uploaded: the person is signed out, or the cloud is turned off.</summary>
    Off,

    /// <summary>The plan's storage has no room for the file.</summary>
    Full,
}

/// <summary>How a run ended.</summary>
public enum RunEnd
{
    /// <summary>Every track is imported, already in rpm, or not imported with a final reason.</summary>
    Finished,

    /// <summary>The person paused (or the app is closing). Continue starts where it stopped.</summary>
    Paused,

    /// <summary>The media server no longer accepts the sign-in. The person signs in again and continues.</summary>
    SignInExpired,

    /// <summary>The disk is full. The track that did not fit and the rest are still waiting.</summary>
    DiskFull,

    /// <summary>The cloud storage of the plan is full. The albums that did not fit are still waiting.</summary>
    CloudFull,

    /// <summary>A cloud-only import cannot upload: the person signed out, or turned the cloud off. The
    /// downloaded files that wait for the cloud stay in the library until the import continues.</summary>
    CloudOff,

    /// <summary>An error nothing expected stopped the run (<see cref="MigrationRun.Failure"/>). What was
    /// done is in the record, and Continue starts where it stopped.</summary>
    Failed,
}

/// <summary>The stage an import is in.</summary>
public enum MigrationStage
{
    Reading,
    Tracks,
    Playlists,
    Finishing,

    /// <summary>The media server or the cloud gives no answer. The run tries again by itself.</summary>
    Waiting,
}

/// <summary>What the progress view shows. <paramref name="TimeLeft"/> is null until a few albums are
/// done.</summary>
public sealed record MigrationProgress(
    MigrationStage Stage, MigrationCounts Counts, int Total, long BytesDone, long BytesTotal, string? CurrentAlbum, TimeSpan? TimeLeft);

/// <summary>
/// Imports the tracks of another app's library, one album at a time.
///
/// <para>For each track: get the file (it is on this computer, or it is downloaded into the staging
/// folder); see whether rpm has the same song already (<see cref="Match"/>); if so, merge the details
/// (<see cref="MigrationMerge"/>); if not, file it (<see cref="LibraryFiler"/>, always a copy of a file
/// on this computer) and write its details. Every outcome is written to the record at once, so a stop
/// at any point continues where it was.</para>
///
/// <para>Cloud only (media servers): a downloaded file is filed into the library, chosen for the cloud,
/// and noted as <see cref="MigrationTrackState.Downloaded"/>. The cloud upload workers send it. A sweep
/// that reads the row's state (<see cref="Sweep"/>) deletes the file once the row is
/// <see cref="CloudState.Synced"/>, never before. No more than <see cref="MaxStaged"/> such files wait at
/// once, so the disk never holds the library.</para>
///
/// <para>The run never moves, renames or deletes a file of the other app. The only file it deletes is a
/// copy it downloaded itself.</para>
/// </summary>
public sealed class MigrationRun(
    LibraryDatabase db,
    SourceLibrary library,
    LibraryDatabase.MigrationRecord record,
    FilingRules rules,
    IMediaServer? server,
    IMigrationCloud cloud,
    string stagingRoot)
{
    /// <summary>The canonical audio hash. Replaced in tests, where ffmpeg may be missing.</summary>
    internal Func<string, CancellationToken, Task<string>> Hash { get; init; } = CanonicalAudio.HashAsync;

    internal Func<long> Now { get; init; } = () => DateTime.UtcNow.Ticks;

    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>How often the run looks at the cloud while it waits for uploads.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The most downloaded files that wait for the cloud at once in a cloud-only import.</summary>
    internal int MaxStaged { get; init; } = 3;

    /// <summary>The name shown in brackets on a playlist renamed because rpm already had one of that
    /// name. The source's name unless the caller gives a translated label (playlist files).</summary>
    public string SourceLabel { get; init; } = library.Name;

    /// <summary>What happened to each source playlist, once the run has added them.</summary>
    public IReadOnlyList<PlaylistOutcome> Playlists { get; private set; } = [];

    /// <summary>The error that ended the run as <see cref="RunEnd.Failed"/>; null otherwise.</summary>
    public Exception? Failure { get; private set; }

    private readonly Dictionary<string, SourceTrack> _bySource = library.Tracks
        .GroupBy(t => t.SourceId, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private LibraryIndex _index = null!;
    private readonly List<(DateTime At, long Bytes)> _albumTimes = [];
    private string? _currentAlbum;

    public async Task<RunEnd> RunAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        CleanStaging();
        record.AddFound(library.Tracks.Select(t => t.SourceId));
        record.ReopenFailed();
        RefreshPlays();
        _index = LibraryIndex.Build(db);
        var albums = AlbumBatch.Group(library.Tracks);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Sweep();
                Report(progress, MigrationStage.Tracks);

                var due = record.Due(Now(), int.MaxValue)
                    .Where(r => r.State != MigrationTrackState.Downloaded)
                    .Select(r => r.SourceId)
                    .ToHashSet(StringComparer.Ordinal);

                if (due.Count == 0)
                {
                    // Nothing to work on now: wait for the cloud to take the downloaded files, or for the
                    // next try of a track that failed. Nothing left at all: the run is done.
                    if (record.Rows(MigrationTrackState.Downloaded).Count > 0)
                    {
                        if (UploadsBlocked() is { } blocked)
                            return Stop(blocked, progress);
                        cloud.RequestUpload();
                        await Delay(PollInterval, ct);
                        continue;
                    }
                    var retry = record.Rows(MigrationTrackState.NotImported)
                        .Where(r => r.Attempts < CloudRetrySchedule.MaxAttempts)
                        .Select(r => r.NextTryUtcTicks)
                        .DefaultIfEmpty(0)
                        .Min();
                    if (retry == 0)
                        break;
                    await Delay(TimeSpan.FromTicks(Math.Max(0, retry - Now())), ct);
                    continue;
                }

                foreach (var album in albums)
                {
                    var work = album.Where(t => due.Contains(t.SourceId)).ToList();
                    if (work.Count == 0)
                        continue;

                    if (record.Destination == MigrationDestination.CloudOnly)
                    {
                        // An unreadable quota is not a full one: wait and read it again.
                        bool? fits;
                        for (var waits = 0; (fits = await FitsAsync(work, ct)) is null; waits++)
                        {
                            Report(progress, MigrationStage.Waiting);
                            await Delay(ConnectionWait(waits), ct);
                        }
                        if (fits is false)
                        {
                            // The albums that fitted finish first: their files are downloaded and wait for
                            // the upload, and the report must count them as imported, not as waiting.
                            return Stop(await DrainAsync(progress, ct) ?? RunEnd.CloudFull, progress);
                        }
                    }

                    _currentAlbum = string.IsNullOrWhiteSpace(work[0].Album) ? work[0].Title : work[0].Album;
                    if (await AlbumAsync(work, progress, ct) is { } end)
                        return Stop(end, progress);
                    _albumTimes.Add((DateTime.UtcNow, work.Sum(t => t.SizeBytes)));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RunEnd.Paused;
        }
        catch (Exception e)
        {
            // Not "paused": the person did not pause it, and must see that something went wrong.
            Failure = e;
            return RunEnd.Failed;
        }

        AddPlaylists(progress);
        Report(progress, MigrationStage.Finishing);
        record.Finish(Now());
        return RunEnd.Finished;
    }

    /// <summary>The run stops before the end. The playlists get the tracks that are in rpm so far (a later
    /// run puts the rest in their places); a sign-in that expired adds nothing, because the person
    /// continues at once.</summary>
    private RunEnd Stop(RunEnd end, IProgress<MigrationProgress> progress)
    {
        if (end is RunEnd.CloudFull or RunEnd.DiskFull or RunEnd.CloudOff)
            AddPlaylists(progress);
        return end;
    }

    /// <summary>How long to wait for a server or the cloud that gives no answer: 5 seconds, doubling
    /// to at most a minute.</summary>
    private static TimeSpan ConnectionWait(int waits) => TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(waits, 4))));

    /// <summary>A second run writes the source's current play totals for the tracks an earlier run
    /// brought in. They replace the old ones on the source library's device, so nothing is counted
    /// twice; the cloud sync sends what changed.</summary>
    private void RefreshPlays()
    {
        var rows = new List<LibraryDatabase.TrackPlayRow>();
        foreach (var (sourceId, trackId) in record.TrackIds())
        {
            if (!_bySource.TryGetValue(sourceId, out var track) || track.Plays <= 0 && track.LastPlayed is null)
                continue;
            if (db.FindById(trackId)?.RpmId is { Length: > 0 } rpmId)
                rows.Add(new LibraryDatabase.TrackPlayRow(rpmId, library.DeviceId, track.Plays, track.LastPlayed?.UtcTicks ?? 0, 0));
        }
        if (rows.Count > 0)
            db.ApplyImportedPlays(library.DeviceId, rows);
    }

    /// <summary>Adds the source's playlists with the tracks that are in rpm now. A cloud-only import also
    /// puts them in the cloud: their tracks are only there, and the person's other devices play from it.
    /// An import to this computer leaves them here, as any playlist made in rpm.</summary>
    private void AddPlaylists(IProgress<MigrationProgress> progress)
    {
        Report(progress, MigrationStage.Playlists);
        Playlists = MigrationPlaylists.Apply(db, library, record, SourceLabel);

        if (record.Destination != MigrationDestination.CloudOnly || cloud.CanUpload(0) == CloudUploads.Off)
            return;
        var nodes = db.GetPlaylistNodes().Select(n => n.Id).ToHashSet();
        foreach (var playlist in library.Playlists)
            if (record.Playlist(playlist.SourceId) is { } made && nodes.Contains(made.PlaylistId))
                cloud.AddPlaylist(made.PlaylistId);
    }

    // ── one album ──

    private async Task<RunEnd?> AlbumAsync(IReadOnlyList<SourceTrack> work, IProgress<MigrationProgress> progress, CancellationToken ct)
    {
        var plays = new List<LibraryDatabase.TrackPlayRow>();
        var added = new List<(string RpmId, long AddedAtUnix)>();
        try
        {
            foreach (var track in work)
            {
                // A server that gives no answer is waited for, and the same track tried again. It costs
                // the track no attempt: the network being down says nothing about the file.
                for (var waits = 0; ; waits++)
                {
                    ct.ThrowIfCancellationRequested();
                    var step = await TrackAsync(track, plays, added, ct);
                    Report(progress, MigrationStage.Tracks);
                    if (step.End is not null)
                        return step.End;
                    if (!step.Again)
                        break;
                    Report(progress, MigrationStage.Waiting);
                    await Delay(ConnectionWait(waits), ct);
                }
            }
            return null;
        }
        finally
        {
            // Written even when the album stops part-way, so the tracks that landed have their plays.
            if (plays.Count > 0)
                db.ApplyImportedPlays(library.DeviceId, plays);
            await SendAddedAsync(added);
        }
    }

    /// <summary>What one track's turn gives: go on, stop the run, or wait and try the same track again.</summary>
    private readonly record struct Step(RunEnd? End = null, bool Again = false)
    {
        public static readonly Step Next = default;

        public static implicit operator Step(RunEnd end) => new(end);
    }

    private async Task<Step> TrackAsync(
        SourceTrack track, List<LibraryDatabase.TrackPlayRow> plays, List<(string, long)> added, CancellationToken ct)
    {
        string path;
        var staged = false;
        switch (track.File)
        {
            case LocalFile local:
                if (LocalPaths.Find(local.Path) is not { } onDisk)
                {
                    record.Note(track.SourceId, MigrationTrackState.NotImported, reason: MigrationReason.FileNotFound, detail: local.Path);
                    return Step.Next;
                }
                path = onDisk;
                break;

            case RemoteFile remote:
                if (record.Destination == MigrationDestination.CloudOnly && await WaitForRoomAsync(ct) is { } blocked)
                    return blocked;
                switch (await DownloadAsync(track, remote, ct))
                {
                    case Downloaded d:
                        path = d.Path;
                        staged = true;
                        break;
                    case RunEnd end:
                        return end;
                    case NoAnswer:
                        return new Step(Again: true);
                    default:
                        return Step.Next;
                }
                break;

            default:
                throw new InvalidOperationException("A source track's file is local or remote.");
        }

        try
        {
            TrackInfo info;
            try
            {
                info = WithSourceTags(TagReader.Read(path), track);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                record.Note(track.SourceId, MigrationTrackState.NotImported, reason: MigrationReason.ReadFailed, detail: path);
                return Step.Next;
            }

            if (await Match(info, path, ct) is { } existing)
            {
                Merge(existing, track, plays, added);
                var lower = Lossless(info.Extension, info.CodecDescription) && !Lossless(existing.Extension, existing.CodecDescription);
                record.Note(track.SourceId, MigrationTrackState.AlreadyInRpm, existing.Id,
                    reason: lower ? MigrationReason.LowerQualityInRpm : null,
                    detail: lower ? (track.File as LocalFile)?.Path ?? path : null);
                return Step.Next;
            }

            LibraryTrack row;
            if (!staged && LibraryInbox.IsInside(rules.Root, path) is false && IsUnder(rules.Root, path))
            {
                // Already inside the library folder: taken where it is and not renamed, because renaming
                // it would break the other app's path.
                db.Upsert(LibraryDatabase.FromTrackInfo(info, rules.Root));
                row = db.FindByRelativePath(Path.GetRelativePath(rules.Root, path).Replace('\\', '/'))
                    ?? db.FindByRelativePath(Path.GetRelativePath(rules.Root, path))
                    ?? throw new InvalidOperationException("The row was not written.");
            }
            else
            {
                var filing = rules with { Mode = staged ? OrganizeMode.Move : OrganizeMode.Copy };
                var filed = LibraryFiler.File(db, [info], filing, staged ? [stagingRoot] : [], null, (_, _) => { }, ct)[0];
                if (!filed.Result.Success || filed.Row is null)
                {
                    if (filed.Result.DiskFull)
                        return RunEnd.DiskFull;
                    record.Note(track.SourceId, MigrationTrackState.NotImported, reason: MigrationReason.CouldNotCopy, detail: filed.Result.Error);
                    return Step.Next;
                }
                staged = false;   // moved into the library: no longer a staged copy
                row = db.FindByRelativePath(filed.Row.RelativePath) ?? filed.Row;
            }

            Merge(row, track, plays, added);
            _index.Add(db.FindById(row.Id) ?? row);

            if (record.Destination == MigrationDestination.CloudOnly && track.File is RemoteFile)
            {
                db.SetCloudSelected([row.Id], selected: true);
                record.Note(track.SourceId, MigrationTrackState.Downloaded, row.Id);
                cloud.RequestUpload();
            }
            else
            {
                record.Note(track.SourceId, MigrationTrackState.Imported, row.Id);
            }
            return Step.Next;
        }
        finally
        {
            if (staged)
                TryDelete(path);
        }
    }

    /// <summary>Writes the merged details onto the row, and collects its plays and its date for the cloud.</summary>
    private void Merge(LibraryTrack row, SourceTrack track, List<LibraryDatabase.TrackPlayRow> plays, List<(string, long)> added)
    {
        var merged = MigrationMerge.Merge(
            new RpmSide(row.Rating, row.IsFavorite, row.FavoritedUtcTicks, row.AddedUtcTicks, row.LastPlayedUtcTicks), track);

        if (merged.Rating > 0)
            db.SetImportedRating(row.Id, merged.Rating);
        if (merged.Favorite && !row.IsFavorite)
            db.SetTrackFavorite(row.RelativePath, true, merged.FavoritedUtcTicks);
        db.SetAddedEarlier(row.Id, merged.AddedUtcTicks);

        if (row.RpmId is not { Length: > 0 } rpmId)
            return;
        if (track.Plays > 0 || track.LastPlayed is not null)
            plays.Add(new LibraryDatabase.TrackPlayRow(rpmId, library.DeviceId, track.Plays, track.LastPlayed?.UtcTicks ?? 0, 0));
        if (row.CloudState is CloudState.Synced or CloudState.CloudOnly && merged.AddedUtcTicks > 0 && merged.AddedUtcTicks < row.AddedUtcTicks)
            added.Add((rpmId, PlayClock.Seconds(merged.AddedUtcTicks)));
    }

    /// <summary>
    /// The library row that is the same song, or null. In order: a file inside the library folder is
    /// its own row; a row with the same artist, album and title and a length within two seconds (this
    /// stands in for a fingerprint); a row whose canonical audio hash is the same (a cloud track whose
    /// tags differ). The hash is only worked out when a hashed row of about the same length exists,
    /// because it reads the whole file.
    /// </summary>
    private async Task<LibraryTrack?> Match(TrackInfo info, string path, CancellationToken ct)
    {
        if (IsUnder(rules.Root, path)
            && db.FindByRelativePath(Path.GetRelativePath(rules.Root, path).Replace('\\', '/')) is { } here)
            return here;

        if (_index.SameSong(info) is { } song)
            return db.FindById(song.Id) ?? song;

        if (_index.HasHashedNear(info.DurationSeconds))
        {
            string hash;
            try { hash = await Hash(path, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { return null; }
            if (_index.ByHash(hash) is { } same)
                return db.FindById(same.Id) ?? same;
        }
        return null;
    }

    // ── files ──

    private sealed record Downloaded(string Path);

    /// <summary>The server gave no answer at all: it is switched off, asleep, or the network is down.</summary>
    private sealed record NoAnswer;

    /// <summary>The file in the staging folder, or the end of the run, or null when the track was noted
    /// as failed. A download is written to a <c>.part</c> file and renamed only when its length is the
    /// size the server gave, so a stop never leaves half a file under the real name.</summary>
    private async Task<object?> DownloadAsync(SourceTrack track, RemoteFile remote, CancellationToken ct)
    {
        if (server is null)
            throw new InvalidOperationException("A media server track needs the server to download it.");

        Directory.CreateDirectory(stagingRoot);
        var name = StagedName(track.SourceId) + remote.Extension;
        var final = Path.Combine(stagingRoot, name);
        var part = final + ".part";
        if (File.Exists(final) && (remote.Size <= 0 || new FileInfo(final).Length == remote.Size))
            return new Downloaded(final);

        try
        {
            await using (var source = await server.OpenAsync(remote, ct))
            await using (var target = File.Create(part))
                await source.CopyToAsync(target, ct);

            if (remote.Size > 0 && new FileInfo(part).Length != remote.Size)
            {
                TryDelete(part);
                record.NoteFailure(track.SourceId, MigrationReason.ReadFailed, Now());
                return null;
            }
            File.Move(part, final, overwrite: true);
            return new Downloaded(final);
        }
        catch (MediaServerSignInExpired)
        {
            TryDelete(part);
            return RunEnd.SignInExpired;
        }
        catch (Exception e) when (Unanswered(e, ct))
        {
            TryDelete(part);
            return new NoAnswer();
        }
        catch (IOException e) when (OrganizeApplier.IsDiskFull(e))
        {
            TryDelete(part);
            return RunEnd.DiskFull;
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            TryDelete(part);
            record.NoteFailure(track.SourceId, MigrationReason.ReadFailed, Now());
            return null;
        }
    }

    /// <summary>A request that got no answer from the server: no connection, or no reply in time. A
    /// reply with an error status is an answer, and counts against the track.</summary>
    private static bool Unanswered(Exception e, CancellationToken ct) => e switch
    {
        HttpRequestException { StatusCode: null } => true,
        TaskCanceledException when !ct.IsCancellationRequested => true,   // HttpClient's timeout
        _ => false,
    };

    /// <summary>Waits until every downloaded file of this import has reached the cloud and been swept.
    /// Returns why it cannot, when the cloud cannot take them.</summary>
    private async Task<RunEnd?> DrainAsync(IProgress<MigrationProgress> progress, CancellationToken ct)
    {
        while (true)
        {
            Sweep();
            Report(progress, MigrationStage.Tracks);
            if (record.Rows(MigrationTrackState.Downloaded).Count == 0)
                return null;
            if (UploadsBlocked() is { } blocked)
                return blocked;
            cloud.RequestUpload();
            await Delay(PollInterval, ct);
        }
    }

    /// <summary>Waits while <see cref="MaxStaged"/> downloaded files wait for the cloud. Returns why the
    /// run must stop, when the cloud cannot take them.</summary>
    private async Task<RunEnd?> WaitForRoomAsync(CancellationToken ct)
    {
        while (true)
        {
            Sweep();
            if (record.Rows(MigrationTrackState.Downloaded).Count < MaxStaged)
                return null;
            if (UploadsBlocked() is { } blocked)
                return blocked;
            cloud.RequestUpload();
            await Delay(PollInterval, ct);
        }
    }

    /// <summary>Why the downloaded files that wait cannot reach the cloud now, or null when they can:
    /// the person signed out or turned the cloud off, or the plan has no room for one of them. Without
    /// this the run would wait for an upload that never starts.</summary>
    private RunEnd? UploadsBlocked()
    {
        foreach (var waiting in record.Rows(MigrationTrackState.Downloaded))
        {
            if (waiting.TrackId is not { } id || db.FindById(id) is not { } row || row.CloudState is CloudState.Synced or CloudState.CloudOnly)
                continue;
            switch (cloud.CanUpload(row.SizeBytes))
            {
                case CloudUploads.Off:
                    return RunEnd.CloudOff;
                case CloudUploads.Full:
                    return RunEnd.CloudFull;
            }
        }
        return null;
    }

    /// <summary>
    /// For each downloaded track of a cloud-only import: once its row is <see cref="CloudState.Synced"/>,
    /// it is marked as only in the cloud and the file is deleted; a row that is already only in the cloud
    /// (the app stopped between the two) has its file deleted. A row whose upload the cloud refused for
    /// good is not imported: the record says why, and the downloaded copy and its row are removed,
    /// because the person chose not to keep the files on this computer. A later run tries it again. The
    /// state is read from the row each time, so it does not matter in which order the upload and this
    /// sweep happen.
    /// </summary>
    private void Sweep()
    {
        foreach (var waiting in record.Rows(MigrationTrackState.Downloaded))
        {
            if (waiting.TrackId is not { } id || db.FindById(id) is not { } row)
            {
                record.Note(waiting.SourceId, MigrationTrackState.NotImported, reason: MigrationReason.Removed);
                continue;
            }

            if (row.CloudState == CloudState.Error && db.UploadGivenUp(row.Id, Now()))
            {
                // Noted first: a stop before the removal leaves the row in the library, which is safe.
                record.Note(waiting.SourceId, MigrationTrackState.NotImported, reason: MigrationReason.UploadFailed, detail: row.CloudError);
                db.Remove(row.RelativePath);
                TryDelete(Path.Combine(rules.Root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                continue;
            }

            if (row.CloudState == CloudState.Synced)
                db.MarkOffloaded(row.Id, row.RpmId);
            if ((db.FindById(row.Id)?.CloudState ?? row.CloudState) != CloudState.CloudOnly)
                continue;

            TryDelete(Path.Combine(rules.Root, row.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            record.Note(waiting.SourceId, MigrationTrackState.Imported, row.Id);
        }
    }

    /// <summary>Whether the album fits in the cloud plan, counting the files still waiting to upload; null
    /// when the quota could not be read.</summary>
    private async Task<bool?> FitsAsync(IReadOnlyList<SourceTrack> album, CancellationToken ct)
    {
        if (await cloud.QuotaAsync(ct) is not { } space)
            return null;
        var (used, quota, unlimited) = space;
        if (unlimited)
            return true;
        var waiting = record.Rows(MigrationTrackState.Downloaded)
            .Sum(r => r.TrackId is { } id ? db.FindById(id)?.SizeBytes ?? 0 : 0);
        return used + waiting + album.Sum(t => t.SizeBytes) <= quota;
    }

    private async Task SendAddedAsync(List<(string RpmId, long AddedAtUnix)> added)
    {
        if (added.Count == 0 || !cloud.SignedIn)
            return;
        try
        {
            foreach (var batch in added.Chunk(200))
                await cloud.PutTrackAddedAsync(batch, CancellationToken.None);
        }
        catch (Exception e) when (e is HttpRequestException or CloudRejectedException or InvalidOperationException)
        {
            // Best effort: a second run sends the dates again, and the store only moves them earlier.
        }
    }

    private void CleanStaging()
    {
        if (!Directory.Exists(stagingRoot))
            return;
        foreach (var part in Directory.EnumerateFiles(stagingRoot, "*.part"))
            TryDelete(part);
    }

    // ── small rules ──

    /// <summary>
    /// Tags the file does not have are taken from what the other app says. When anything was taken, the
    /// track is marked so the filer writes the tags into rpm's copy (<see cref="MetadataSource.Manual"/>);
    /// otherwise the copy would land with no title, and the library row read from it would have none.
    /// The other app's own file is never written.
    /// </summary>
    private static TrackInfo WithSourceTags(TrackInfo info, SourceTrack track)
    {
        var filled = info with
        {
            Title = Blank(info.Title) ? NullIfBlank(track.Title) : info.Title,
            Artist = Blank(info.Artist) ? NullIfBlank(track.Artist) : info.Artist,
            Album = Blank(info.Album) ? NullIfBlank(track.Album) : info.Album,
            AlbumArtist = Blank(info.AlbumArtist) ? NullIfBlank(track.AlbumArtist) : info.AlbumArtist,
            TrackNumber = info.TrackNumber == 0 && track.TrackNumber > 0 ? (uint)track.TrackNumber : info.TrackNumber,
            DiscNumber = info.DiscNumber == 0 && track.DiscNumber > 0 ? (uint)track.DiscNumber : info.DiscNumber,
            DurationSeconds = info.DurationSeconds > 0 ? info.DurationSeconds : track.DurationSeconds,
        };
        var changed = filled.Title != info.Title || filled.Artist != info.Artist || filled.Album != info.Album
            || filled.AlbumArtist != info.AlbumArtist || filled.TrackNumber != info.TrackNumber || filled.DiscNumber != info.DiscNumber;
        return changed ? filled with { Source = MetadataSource.Manual } : filled;
    }

    private static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>Whether a file holds lossless audio, by its type: FLAC, WAV, AIFF, Monkey's Audio,
    /// WavPack, and Apple Lossless inside an .m4a.</summary>
    internal static bool Lossless(string? extension, string? codec)
    {
        var ext = (extension ?? "").ToLowerInvariant();
        return ext is ".flac" or ".wav" or ".aiff" or ".aif" or ".ape" or ".wv" or ".alac"
            || ext is ".m4a" && (codec ?? "").Contains("lossless", StringComparison.OrdinalIgnoreCase)
            || ext is ".m4a" && (codec ?? "").Contains("alac", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != path && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string StagedName(string sourceId) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceId)))[..24];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Report(IProgress<MigrationProgress> progress, MigrationStage stage)
    {
        var counts = record.Counts();
        var total = library.Tracks.Sum(t => t.SizeBytes);
        var doneIds = record.Rows(MigrationTrackState.Imported).Concat(record.Rows(MigrationTrackState.AlreadyInRpm))
            .Concat(record.Rows(MigrationTrackState.NotImported).Where(r => r.Attempts >= CloudRetrySchedule.MaxAttempts));
        var done = doneIds.Sum(r => _bySource.TryGetValue(r.SourceId, out var t) ? t.SizeBytes : 0);
        progress.Report(new MigrationProgress(stage, counts, counts.Found, done, total, _currentAlbum, TimeLeft(total - done)));
    }

    /// <summary>Bytes left divided by the speed over the last ten albums; null until three albums are done.</summary>
    private TimeSpan? TimeLeft(long bytesLeft)
    {
        if (_albumTimes.Count < 3 || bytesLeft <= 0)
            return null;
        var recent = _albumTimes.TakeLast(Math.Min(10, _albumTimes.Count)).ToList();
        var seconds = (recent[^1].At - recent[0].At).TotalSeconds;
        var bytes = recent.Skip(1).Sum(a => a.Bytes);
        return seconds <= 0 || bytes <= 0 ? null : TimeSpan.FromSeconds(bytesLeft / (bytes / seconds));
    }

    /// <summary>The library's songs, by what the matching asks: artist, album and title; the title alone,
    /// in its loose form; and the canonical audio hash of the tracks the cloud holds.</summary>
    private sealed class LibraryIndex
    {
        private readonly Dictionary<string, List<LibraryTrack>> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<LibraryTrack>> _byTitle = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LibraryTrack> _byHash = new(StringComparer.Ordinal);
        private readonly SortedSet<double> _hashedLengths = [];

        public static LibraryIndex Build(LibraryDatabase db)
        {
            var index = new LibraryIndex();
            foreach (var row in db.Query())
                index.Add(row);
            return index;
        }

        public void Add(LibraryTrack row)
        {
            var key = MigrationEstimate.Key(row.Artist, row.Album, row.Title);
            if (!_byKey.TryGetValue(key, out var rows))
                _byKey[key] = rows = [];
            rows.Add(row);
            var title = MigrationEstimate.Loose(row.Title);
            if (title.Length > 0)
            {
                if (!_byTitle.TryGetValue(title, out var same))
                    _byTitle[title] = same = [];
                same.Add(row);
            }
            if (row.ContentHash is { Length: > 0 } hash)
            {
                _byHash.TryAdd(hash, row);
                _hashedLengths.Add(row.DurationSeconds);
            }
        }

        /// <summary>
        /// The row that is the same song, or null. First the same artist, album and title. Then, for a song
        /// whose tags rpm corrected after its own import (an artist's spelling, an album found by the
        /// identify step): the same title in its loose form, with the artist or the album still the same
        /// in their loose forms, and the same track number when both have one. A title alone is not
        /// enough: "Intro" is on many records.
        /// </summary>
        public LibraryTrack? SameSong(TrackInfo info)
        {
            bool Close(LibraryTrack r) => Math.Abs(r.DurationSeconds - info.DurationSeconds) <= MigrationEstimate.LengthTolerance;

            if (_byKey.TryGetValue(MigrationEstimate.Key(info.Artist, info.Album, info.Title), out var rows) && rows.FirstOrDefault(Close) is { } exact)
                return exact;

            if (!_byTitle.TryGetValue(MigrationEstimate.Loose(info.Title), out var titled))
                return null;
            var artist = MigrationEstimate.Loose(info.Artist);
            var album = MigrationEstimate.Loose(info.Album);
            return titled.FirstOrDefault(r => Close(r)
                && (artist.Length > 0 && MigrationEstimate.Loose(r.Artist) == artist || album.Length > 0 && MigrationEstimate.Loose(r.Album) == album)
                && (info.TrackNumber == 0 || r.TrackNumber == 0 || r.TrackNumber == info.TrackNumber));
        }

        public bool HasHashedNear(double seconds) =>
            _hashedLengths.GetViewBetween(seconds - MigrationEstimate.LengthTolerance, seconds + MigrationEstimate.LengthTolerance).Count > 0;

        public LibraryTrack? ByHash(string hash) => _byHash.GetValueOrDefault(hash);
    }
}

using RPM.Core.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Cloud;

/// <summary>What the cloud engine is doing, for the UI to show without asking.</summary>
public sealed record CloudSyncStatus(
    bool Running,
    int Pending,
    int Failed,
    string? CurrentTitle,
    long UsedBytes,
    long QuotaBytes,
    CloudTrouble? LastError,
    /// <summary>Unlimited storage, which the user gets by supporting rpm. The server decides this, not the
    /// app.</summary>
    bool Unlimited = false)
{
    public static readonly CloudSyncStatus Idle = new(false, 0, 0, null, 0, 0, null);

    /// <summary>The storage space. It answers the question "may this be uploaded?". See <see
    /// cref="CloudSpace"/>.</summary>
    public CloudSpace Space => new(UsedBytes, QuotaBytes, Unlimited);

    public double UsedFraction => Space.UsedFraction;
}

/// <summary>
/// Uploads what the user asked to keep in the cloud, and downloads it again when the user asks for that.
/// The local file is never touched. An upload makes a second copy. It does not move the file.
///
/// <para>The queue is the mirror itself. A track's <see cref="CloudState"/> is its place in the line. So
/// the work survives the app being closed halfway through a hundred gigabytes of uploads. Nothing here
/// holds a list in memory that a crash would lose.</para>
///
/// <para>Three uploads run at once. More would not go faster on a typical home connection. It would make
/// the app's own playback stutter, and a background upload must never do that.</para>
/// </summary>
public sealed partial class CloudSyncService : IDisposable
{
    /// <summary>Enough to saturate a home connection without starving playback of disk and CPU.</summary>
    private const int Concurrency = 3;


    private readonly LibraryService _library;
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly RpmCloudClient _cloud;
    private readonly CloudJobService _jobs;

    /// <summary>The other direction: what the cloud holds and this machine does not. The catalogue pass
    /// fetches the whole listing, and one listing serves both directions.</summary>
    private readonly CloudMirrorService _mirror;

    private readonly ILogger<CloudSyncService> _log;

    /// <summary>What this pass asks before it runs, and again before each of its two expensive
    /// halves. See Governor.</summary>
    private readonly Shell.Sweep _sweep;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Albums whose cover has already been handled in this run. A record with sixteen tracks then
    /// needs one cover upload, not sixteen.</summary>
    private readonly HashSet<string> _artDone = new(StringComparer.Ordinal);

    /// <summary>Album folders whose pictures have already been handled in this run. A folder holds one
    /// cover.jpg. That file is both the source of the album's cover and the record of when it last changed
    /// here. If two albums reconcile the same folder, they overwrite each other's answer. They then swap
    /// pictures with the store on every run. The first album to reach a folder owns it.</summary>
    private readonly HashSet<string> _artFolders = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>Whether this session has already asked the server which album covers it holds. See
    /// <see cref="RepairAlbumArtAsync"/>.</summary>
    private bool _artRepairChecked;

    /// <summary>Whether the synced playlists have been written again in this session. They are written
    /// once, because a playlist can be edited with no music change behind the edit, and nothing else would
    /// send that edit.</summary>
    private bool _playlistsPushed;

    /// <summary>When the catalogue was last reconciled with the cloud. The pass runs on its own clock, not
    /// on each kick, because a kick happens on every library change.</summary>
    private DateTime _lastCatalogPass = DateTime.MinValue;


    /// <summary>
    /// What is being taken out of the cloud right now.
    ///
    /// <para>Uploading shows its progress in the queue. A track is in the Queued or Uploading state, and
    /// anything can see that. Removal writes nothing while it runs, and the row changes only at the end.
    /// Deleting sixteen server copies takes long enough to look as if nothing is happening. So the folders
    /// and playlists being removed are held here for as long as the work lasts, and the badge reads them
    /// from here.</para>
    /// </summary>
    private readonly HashSet<string> _leavingFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _leavingPlaylists = new();

    /// <summary>True while this album folder is being taken out of the cloud.</summary>
    public bool IsAlbumLeaving(string folder)
    {
        lock (_leavingFolders)
            return _leavingFolders.Contains(folder);
    }

    /// <summary>True while this playlist is being taken out of the cloud.</summary>
    public bool IsPlaylistLeaving(long nodeId)
    {
        lock (_leavingPlaylists)
            return _leavingPlaylists.Contains(nodeId);
    }

    private void MarkLeaving(string? folder, long? nodeId, bool leaving)
    {
        if (folder is { Length: > 0 })
            lock (_leavingFolders)
            {
                if (leaving) _leavingFolders.Add(folder); else _leavingFolders.Remove(folder);
            }

        if (nodeId is { } id)
            lock (_leavingPlaylists)
            {
                if (leaving) _leavingPlaylists.Add(id); else _leavingPlaylists.Remove(id);
            }

        Changed?.Invoke();
    }

    private CloudSyncStatus _status = CloudSyncStatus.Idle;

    /// <summary>The mode that the queue was last brought in line with. <see
    /// cref="SettingsService.Changed"/> fires for every setting. This field is how a real mode change is
    /// told apart from another setting, such as dragging the cover-size slider.</summary>
    private CloudSyncMode _mode;

    /// <summary>Raised whenever <see cref="Status"/> changes. This is the UI's only subscription.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised when the store has no room left, so the queue has stopped.
    ///
    /// <para>The engine's part is done by the time this is raised. Nothing more is being sent, and "all my
    /// music" has already been reduced to the user's selection. This event only lets somebody tell the
    /// user. That is the shell's job and not this service's. A background worker must not put a window on
    /// screen. The shell also shows this message only once a week, not once per pass. See <see
    /// cref="CloudFullNotice"/>.</para>
    /// </summary>
    public event Action? StorageFull;

    /// <summary>Raised with a library folder whose cover or disc label has just been downloaded from the
    /// cloud. The picture was chosen for this record on another computer.
    ///
    /// <para>The event gives one folder, not a general "library changed" signal. The wall keeps one card
    /// per folder and reuses it. So only that card re-reads its picture, and nothing else on screen moves.
    /// A full rebuild would make every album in the window blink, because one of them got a new
    /// cover.</para>
    /// </summary>
    public event Action<string>? AlbumArtChanged;

    public CloudSyncStatus Status => _status;

    /// <summary>Whether anything may still be sent up. This is true when there is room left, unlimited
    /// room, or a storage limit that nobody has read yet. An unread limit refuses nothing. See <see
    /// cref="CloudSpace"/>.
    ///
    /// <para>Every control asks this one question. The menus grey out "add to cloud" based on it, and the
    /// marks stop pulsing based on it. It changes on <see cref="Changed"/>, like everything else here: a
    /// quota read, an upload that lands, a record that leaves the cloud, or a support purchase. So a view
    /// that already follows that event follows this value too.</para>
    /// </summary>
    public bool CanUpload => !_status.Space.IsFull;

    /// The reverse of <see cref="CanUpload"/>. It suits places that read better that way, mainly the
    /// badges. There, "stopped" is what a queued track has to show about itself.
    public bool UploadsStopped => _status.Space.IsFull;

    public CloudSyncService(
        LibraryService library,
        AuthService auth,
        SettingsService settings,
        RpmCloudClient cloud,
        CloudJobService jobs,
        CloudMirrorService mirror,
        Shell.Governor governor,
        ILogger<CloudSyncService> log)
    {
        _library = library;
        _auth = auth;
        _settings = settings;
        _cloud = cloud;
        _jobs = jobs;
        _mirror = mirror;
        _log = log;

        // This is the second wave. What this account holds elsewhere is something the user will look at,
        // and would notice if it were missing. So it runs ahead of the measuring passes, and behind the
        // screen. It uses the library lane, not the network lane, because its cost on this machine is
        // database reads. The requests are slow, but they hold nothing on this machine.
        _sweep = governor.Pass(
            "cloud sync", Shell.Lane.Library, Shell.Wave.Second, notBefore: Shell.Phase.Ready);

        _mode = settings.CloudMode;
        _settings.Changed += OnSettingsChanged;
        _library.Changed += () => Shell.Background.Run(() => KickAsync());
        _library.Opened += OnLibraryOpened;

        // The run holds the library for minutes. It must be stopped before the library is closed, not left to
        // find the connection closed under it.
        _library.StopBeforeClose(CloseAsync);

        // The metadata sync runs when the catalogue is read, not only when there is an upload to do. See
        // SyncMetadataAsync. The mirror service does not know about this. It announces what it read, and
        // this service listens.
        _mirror.CatalogRead += catalog => _ = MetadataOnlyPassAsync(catalog);
    }

    /// <summary>The metadata sync, run from a catalogue that the mirror service has just read. It is for a
    /// machine whose upload run will not happen.
    ///
    /// <para>It is skipped while a sync run is going. That run reads its own listing and does the same work
    /// with it. Doing both would mean two passes writing the same rows.</para>
    /// </summary>
    private Task MetadataOnlyPassAsync(IReadOnlyList<CloudTrack> catalog)
    {
        // Started under the gate, the same as a run, so a close cannot start between the check and the
        // record of the pass. CloseAsync waits for the pass that is recorded here.
        lock (_metadataGate)
        {
            if (_closing.IsCancellationRequested) return Task.CompletedTask;
            if (!_auth.IsLoggedIn || !_library.IsOpen) return Task.CompletedTask;
            if (_worker is { IsCompleted: false }) return Task.CompletedTask;
            if (_metadataRun is { IsCompleted: false }) return Task.CompletedTask;

            var db = _library.Db;
            return _metadataRun = Task.Run(async () =>
            {
                try
                {
                    await SyncMetadataAsync(db, catalog, _closing.Token);
                }
                catch (OperationCanceledException) when (_closing.IsCancellationRequested)
                {
                    // The app is closing. The pass runs again from the next catalogue read.
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Cloud] The metadata pass did not finish");
                }
            });
        }
    }

    /// <summary>The metadata pass in progress, or a finished one. See <see cref="MetadataOnlyPassAsync"/>.
    /// Guarded by <see cref="_metadataGate"/>.</summary>
    private Task? _metadataRun;

    private readonly object _metadataGate = new();

    /// <summary>Cancelled once, when the library is about to be closed (<see cref="CloseAsync"/>). From then
    /// on nothing starts: no run, no metadata pass and no retry wake.</summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>
    /// Stops this service for good before the library is closed. <see cref="LibraryService.Dispose"/> calls
    /// it through <see cref="LibraryService.StopBeforeClose"/>, and waits for it.
    ///
    /// <para>The order matters. The closing token is cancelled first, so no new run, metadata pass or wake
    /// can start. The gate is then taken, which waits for a kick that already passed its checks to finish
    /// starting its run. Only then is the run read and cancelled, so the run that is waited for is the last
    /// one. A run cancelled in the middle of a database call finishes that call and then stops.</para>
    /// </summary>
    internal async Task CloseAsync()
    {
        _closing.Cancel();
        Interlocked.Exchange(ref _wake, null)?.Cancel();

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Stop();
        }
        finally
        {
            _gate.Release();
        }

        Task? metadata;
        Task[] errands;
        lock (_metadataGate)
        {
            metadata = _metadataRun;
            errands = [.. _errands];
        }

        if (_worker is { } worker)
            try { await worker.ConfigureAwait(false); } catch { /* the run's own failure is not the close's business */ }
        if (metadata is not null)
            await metadata.ConfigureAwait(false);
        await Task.WhenAll(errands).ConfigureAwait(false);
    }

    /// <summary>The work a person started (a restore, a playlist added, a settings change) that has not ended.
    /// Guarded by <see cref="_metadataGate"/>. <see cref="CloseAsync"/> waits for every one of them.</summary>
    private readonly HashSet<Task> _errands = [];

    /// <summary>
    /// Runs work that a person started, so that <see cref="CloseAsync"/> can stop it and wait for it.
    ///
    /// <para>Refused once the close has begun: the work throws <see cref="OperationCanceledException"/> and
    /// never touches the library. Otherwise it is recorded under the same lock as the close's check, and
    /// runs on a token that the close cancels as well as the caller's.</para>
    /// </summary>
    private async Task<T> ErrandAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_metadataGate)
        {
            _closing.Token.ThrowIfCancellationRequested();
            _errands.Add(done.Task);
        }

        try
        {
            using var closingOrCaller = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
            return await work(closingOrCaller.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_metadataGate)
                _errands.Remove(done.Task);
            done.SetResult();
        }
    }

    /// <inheritdoc cref="ErrandAsync{T}"/>
    private Task ErrandAsync(Func<CancellationToken, Task> work, CancellationToken ct) =>
        ErrandAsync(async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, ct);

    /// <summary>A different collection was opened, so the run in progress is working on a library that
    /// does not belong to the user.
    ///
    /// <para>The run is cancelled rather than left to finish. It holds the old mirror, which is closed
    /// after this and refuses every call from then on. A track that it has half uploaded belongs to a
    /// collection that nobody is looking at. The <see cref="LibraryService.Changed"/> event that Open also
    /// raises starts a fresh run over the new collection.</para>
    /// </summary>
    private void OnLibraryOpened(string root)
    {
        // Check which library the run belongs to, not when the event arrived. Open raises Changed before
        // this method runs. That kick can already have started a run over the NEW collection by the time
        // this line reads the field. So cancelling on the event alone would stop the run that is correct.
        var running = _runningFor;
        if (running is null || ReferenceEquals(running, _library.Db))
            return;

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* the app is closing; the run is going anyway */ }
    }

    /// <summary>The mirror that the run in progress is working on, or null when nothing is running.
    /// OnLibraryOpened reads this to tell a run over the collection that has just been closed from a run
    /// over the collection that replaced it.</summary>
    private volatile LibraryDatabase? _runningFor;

    /// <summary>Brings the queue in line with the current mode and starts working through it. Safe to call
    /// often. A run that is already under way keeps going.
    /// </summary>
    public async Task KickAsync()
    {
        if (_settings.CloudMode == CloudSyncMode.None) return;
        if (!_auth.IsLoggedIn) return;
        if (!_library.IsOpen) return;

        // A pause is a standing instruction, not a single cancellation. This line makes it one. Kicks
        // arrive from many places: every library edit, every catalogue read that the mirror finishes on its
        // own clock, and every restore. A pause that only cancelled the token would be undone by the next
        // kick. Then pressing pause would stop the upload for a few seconds, and it would start again on
        // its own.
        if (_paused) return;
        if (_closing.IsCancellationRequested) return;

        // The queue is written under the gate, behind the closing check, for the same reason as the run
        // below: CloseAsync takes the gate, so a kick that got past the check above cannot write after the
        // close has finished waiting.
        LibraryDatabase db;
        await _gate.WaitAsync();
        try
        {
            if (_closing.IsCancellationRequested || !_library.IsOpen) return;
            db = _library.Db;
            db.QueueForUpload(everything: _settings.CloudMode == CloudSyncMode.All);
        }
        finally
        {
            _gate.Release();
        }

        // The queue is written either way. It lives in the database and records what is owed. But no worker
        // starts while the network is unreachable. A run that starts offline costs a quota round trip, a
        // transcode and a failed PUT for each track, and all of them end where they began. When the network
        // comes back, a kick starts this again. See the app's network listener.
        if (!await RPM.Core.Net.NetworkReach.Shared.AllowAsync(CancellationToken.None)) return;

        await _gate.WaitAsync();
        try
        {
            // Read under the gate. CloseAsync takes the gate after it has cancelled the token, so a kick
            // that reaches this line after that sees it, and a kick that got here first has finished
            // starting its run before CloseAsync reads the run to stop it.
            if (_closing.IsCancellationRequested) return;

            if (_worker is { IsCompleted: false }) return;   // already running

            // The chip is raised here when it can be, not when the first track reaches the transcoder.
            // Between the two, a whole-library pass queues the tracks, and a quota round trip happens. The
            // user's "add album to cloud" would have been under way for all of that with nothing on screen.
            // This is one indexed COUNT, and it is used only when the answer is yes, there is work. A kick
            // that finds nothing due must not show a chip, and the library raises a kick on every change.
            //
            // This is an EARLY raise. It is never the only one. Two kicks arrive together on an "add album"
            // (the library's own Changed, and the caller's await). Whichever reaches this line first may
            // run before the other's QueueForUpload has landed. So the count can read zero on a run that
            // has plenty to do. RunAsync opens the batch itself as soon as it finds a wave. That is the
            // guarantee. This line is only a head start.
            //
            // Not while the store is full. The queue exists, but none of it can go anywhere. A chip that
            // spins over work that cannot start would claim that the app is doing something it is not.
            var early = !_status.Space.IsFull && db.CountByCloudState(CloudState.Queued) > 0;
            if (early)
                _jobs.StartBatch(CloudOp.Upload);

            _cts = new CancellationTokenSource();
            _runningFor = db;
            var token = _cts.Token;
            Task worker = Task.Run(async () =>
            {
                try { await RunAsync(db, token, early); }
                finally { if (ReferenceEquals(_runningFor, db)) _runningFor = null; }
            });
            _worker = worker;

            // This runs after the worker has finished, and not from inside it. A kick from inside the run
            // finds the run still going and is refused, which loses a retry.
            Shell.Background.Run(async () =>
            {
                try { await worker; }
                catch { /* the run's own failure is not this wake's business */ }
                WakeForRetries(db);
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Kicks the queue again when the next track that waits out a backoff or a deferral comes due.
    ///
    /// <para>Without this, a retry waited for something else to kick the queue. Examples are a library
    /// change, the network coming back, or the next launch. A first refusal is not final (see <see
    /// cref="CloudRetrySchedule.AfterFailure"/>), so the second attempt must actually happen.</para>
    ///
    /// <para>The time is read from the mirror when a run ends. It is not carried over from the failure that
    /// set it. So a wake lost to a run that was still going is recalculated at that run's end. Only one
    /// wake runs at a time. A newer wake replaces the older one.</para>
    /// </summary>
    private void WakeForRetries(LibraryDatabase db)
    {
        if (_closing.IsCancellationRequested) return;
        if (_paused || !_library.IsOpen || !ReferenceEquals(_library.Db, db)) return;

        // Nothing in the queue goes anywhere while the store is full. The run that frees room kicks the
        // queue.
        if (_status.Space.IsFull) return;

        var now = DateTime.UtcNow.Ticks;
        if (db.NextCloudRetryTicks(now) is not { } due) return;

        var wake = new CancellationTokenSource();
        Interlocked.Exchange(ref _wake, wake)?.Cancel();

        var delay = TimeSpan.FromTicks(Math.Max(due - now, MinimumWake.Ticks));
        Shell.Background.Run(async () =>
        {
            if (await Cancellable.DelayAsync(delay, wake.Token))
                await KickAsync();
        });
    }

    /// <summary>The pending retry wake, replaced by each newer one.</summary>
    private CancellationTokenSource? _wake;

    /// <summary>A floor under the wake, so a due time a moment away does not become a busy loop.</summary>
    private static readonly TimeSpan MinimumWake = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the user has stopped the queue by hand.
    ///
    /// <para>Read by <see cref="KickAsync"/>, which is the only thing that starts work. It is cleared only
    /// by something the user did: <see cref="ResumeAsync"/>, or choosing a sync mode, which is an
    /// instruction to sync in its own right. It is deliberately NOT saved to disk. The pause lasts for this
    /// session only. A queue that was silently stopped for ever by an old button press is the worse of the
    /// two failures.</para>
    /// </summary>
    private volatile bool _paused;

    /// <summary>Whether the engine is stopped by hand. The chip reads it to decide whether it is
    /// offering pause or resume.</summary>
    public bool IsPaused => _paused;

    /// <summary>
    /// Stops after the uploads in flight finish, and stays stopped. Their tracks stay queued.
    ///
    /// <para>The queue lives in the database, so nothing is lost: the track in flight goes back in the
    /// line and <see cref="ResumeAsync"/> picks it up from there.</para>
    /// </summary>
    public void Pause()
    {
        _paused = true;
        Stop();
    }

    /// <summary>Starts again where the pause left off.</summary>
    public Task ResumeAsync()
    {
        _paused = false;
        return KickAsync();
    }

    /// <summary>Cancels the run without changing <see cref="_paused"/>. This is for the app's own reasons
    /// to stop. Those are not the user pressing pause, so they must not leave the queue stopped
    /// afterwards.</summary>
    private void Stop() => _cts?.Cancel();

    /// <summary>The account was deleted on the server, and this computer has signed out. Waits for the run
    /// in progress to end. Then resets the library to "never uploaded" (<see
    /// cref="LibraryDatabase.ForgetCloudAccount"/>), and forgets what this service remembered about the
    /// account in this session.
    /// </summary>
    /// <remarks>This runs only after sign-out, and it refuses
    /// otherwise. When signed out, <see cref="KickAsync"/> starts nothing. So the run awaited here is the
    /// last one, and nothing writes the rows while they are reset. When signed in, a kick could start a new
    /// run between the wait and the reset. This method is reached through <see
    /// cref="Account.AccountDeletion"/>.</remarks>
    public Task ForgetDeletedAccountAsync() =>
        ErrandAsync(_ => ForgetDeletedAccountCoreAsync(), CancellationToken.None);

    private async Task ForgetDeletedAccountCoreAsync()
    {
        if (_auth.IsLoggedIn)
            throw new InvalidOperationException("The account is forgotten after signing out, not before.");

        await StopAsync();

        _artDone.Clear();
        _artFolders.Clear();
        _artRepairChecked = false;
        _playlistsPushed = false;
        _lastCatalogPass = DateTime.MinValue;
        _status = CloudSyncStatus.Idle;

        if (_library.IsOpen)
        {
            // Off the UI thread: one transaction, but over every row of a large library.
            var db = _library.Db;
            var forgotten = await Task.Run(db.ForgetCloudAccount);
            _log.LogInformation("[Cloud] Account forgotten: {Reset} tracks reset, {Dropped} cloud-only tracks dropped",
                forgotten.TracksReset, forgotten.TracksDropped);
            _library.NotifyChanged();
        }

        Changed?.Invoke();
    }

    /// <summary>Stops the run, and waits until the worker is really gone. Reconciling the queue needs this,
    /// before it may rewrite the rows that the worker is holding.</summary>
    private async Task StopAsync()
    {
        Stop();
        if (_worker is { } worker)
            try { await worker; } catch { }
    }

    private void OnSettingsChanged()
    {
        // Every rule about what a settings change means is in CloudSyncReaction. Read it before changing
        // anything here.
        var reaction = CloudSyncReaction.For(_mode, _settings.CloudMode);

        if (reaction.HasFlag(CloudReaction.ApplyMode))
            Shell.Background.Run(() => ErrandAsync(_ => ApplyModeAsync(_settings.CloudMode), CancellationToken.None));

        if (reaction.HasFlag(CloudReaction.KickQueue))
            Shell.Background.Run(() => KickAsync());
    }

    /// <summary>
    /// Moves the library from one sync mode to the next.
    ///
    /// <para>A mode says what belongs in the cloud from now on. So the queue left over from the previous
    /// mode is not the new mode's queue, and it must not be allowed to keep draining into it. Switching
    /// away from "all music" in the middle of a library must stop the upload of the rest of it. Nothing
    /// already in the cloud is deleted here. That is <see cref="DeleteFromCloudAsync"/>'s job, and it
    /// happens only when the user asks for it by name.</para>
    /// </summary>
    private async Task ApplyModeAsync(CloudSyncMode mode)
    {
        _mode = mode;

        // Picking a sync mode is an instruction to sync, so it ends a pause. Otherwise a user who paused
        // and then chose "all my music" would get no music and no reason. The mode on screen would say one
        // thing, and the engine would do nothing.
        _paused = false;

        // The worker holds rows that are in flight. Stop it before rewriting them. Otherwise an upload that
        // lands a moment later would mark a track as Synced, even though the switch just removed it from
        // the queue.
        await StopAsync();

        if (!_library.IsOpen)
            return;

        var db = _library.Db;

        switch (mode)
        {
            case CloudSyncMode.None:
                // Nothing more goes up. What is already in the cloud stays there. Turning syncing off is
                // not the same as asking for the cloud copies to be deleted.
                _log.LogInformation("[Cloud] Sync off — {Count} tracks taken out of the queue", db.Dequeue(onlyUnselected: false));
                break;

            case CloudSyncMode.Selected:
                // Adopt what the cloud already holds as the selection, then drop everything the old
                // mode had queued that nobody picked.
                var adopted = db.SelectWhatIsInCloud();
                var dropped = db.Dequeue(onlyUnselected: true);
                _log.LogInformation(
                    "[Cloud] Selected music — {Adopted} already-uploaded tracks adopted into the selection, {Dropped} unpicked tracks taken out of the queue",
                    adopted, dropped);
                break;

            case CloudSyncMode.All:
                break;   // Everything is wanted, so nothing in the queue is out of place.
        }

        _status = _status with { LastError = null };
        _library.NotifyChanged();

        if (mode != CloudSyncMode.None)
            await KickAsync();
        else
            Report(running: false, db);
    }

    /// <param name="batch">Whether the kick that started this run has already opened the chip's batch. If
    /// it has not, this method opens one itself as soon as it finds work. See KickAsync.</param>
    private async Task RunAsync(LibraryDatabase db, CancellationToken ct, bool batch)
    {
        try
        {
            // Asked once for the whole pass, at the top. The steps below are sub-passes, and each of them
            // asks again. This first check keeps the whole pass out of the startup.
            using (var start = await _sweep.Next(ct))
            {
                if (!start.Go)
                    return;
            }

            // These are cleared on each run. So a user who replaces a cover.jpg and syncs again gets it
            // uploaded again, and is not told that it was done in an earlier session.
            _artDone.Clear();
            _artFolders.Clear();

            // A row still marked 'uploading' was interrupted. This run is the only one, and it has not
            // picked up a track yet. Without this step the row is stranded. The queue would show it as
            // Queued and Error, and QueueForUpload only promotes Local rows. So nothing would ever look at
            // it again, and one track would quietly never reach the cloud.
            if (db.ReclaimInterruptedUploads() is > 0 and var reclaimed)
                _log.LogInformation(
                    "[Cloud] {Count} uploads were interrupted last time and are back in the queue", reclaimed);

            // Refusals that were made final after one attempt, by the rule that applied before a refusal
            // had to repeat. This matches nothing once they are gone. See
            // LibraryDatabase.ReleaseUnconfirmedRefusals.
            if (db.ReleaseUnconfirmedRefusals(DateTime.UtcNow.Ticks) is > 0 and var released)
                _log.LogInformation(
                    "[Cloud] {Count} tracks refused once are back in the queue for a second attempt", released);

            await RefreshQuotaAsync(ct);

            var uploadedSomething = false;

            // This is the limit. A store with no room refuses every upload. Sending them would mean a
            // library's worth of transcodes, requests and 413 errors, and each would end where it began.
            // The only result would be a retry counter that climbs on tracks that did nothing wrong.
            //
            // The rest of the pass still runs. The catalogue, the playlists, the covers, the renditions and
            // the local sweep either do not use the quota or take room rather than use it. A cover and a
            // rendition are not charged to anyone. A playlist is a list of ids. Stopping them would leave a
            // full library out of date, and unplayable on the phone. Only the charged audio stops. That is
            // the whole meaning of "full".
            var noted = false;
            if (_status.Space.IsFull)
                noted = NoteStorageFull();

            while (!_status.Space.IsFull && !ct.IsCancellationRequested)
            {
                // The query itself leaves out tracks that have used up their attempts. See
                // QueryDueForUpload.
                var due = db.QueryDueForUpload(DateTime.UtcNow.Ticks, limit: Concurrency * 4);

                if (due.Count == 0) break;

                Report(running: true, db);

                // The batch is open by the time a track is touched, whatever the early count said.
                if (!batch)
                {
                    _jobs.StartBatch(CloudOp.Upload);
                    batch = true;
                }
                // Work is taken twelve tracks at a time. The job list must know how many are still queued.
                _jobs.Expect(db.CountByCloudState(CloudState.Queued));
                uploadedSomething = true;

                using var slots = new SemaphoreSlim(Concurrency, Concurrency);
                var running = due.Select(async track =>
                {
                    if (!await Cancellable.WaitAsync(slots, ct))
                        return;
                    try { await UploadOneAsync(db, track, ct); }
                    finally { slots.Release(); }
                });

                await Task.WhenAll(running);
            }

            // The store filled up during the pass. The loop stops itself when an upload reports that the
            // store is full. This line tells the user the same thing. It is said once per run. A store that
            // was already full when the run started was handled above.
            if (_status.Space.IsFull && !noted)
                NoteStorageFull();

            // The playlists are written again, so that they point at music the cloud holds. An order that
            // was first stored while its audio was still queued is then sent again. The playlists are not
            // written only after an upload. A playlist that is renamed, reordered or extended, with no new
            // music, causes no upload. Without this step it would never be sent, and the phone would keep
            // an old order. A playlist is a name and a list of ids, so writing the synced ones again costs
            // very little.
            if (CloudPassCadence.DueOncePerSession(uploadedSomething, _playlistsPushed))
            {
                _playlistsPushed = true;
                await PushCloudPlaylistsAsync(ct);
            }

            // Once per session, and again after any run that uploaded. The queue is kicked on every library
            // change. Asking the server which covers it holds on each kick would mean one request per
            // keystroke of a rename.
            if (CloudPassCadence.DueOncePerSession(uploadedSomething, _artRepairChecked))
            {
                _artRepairChecked = true;

                // Asked again here, not only at the top. By now the pass may have run for minutes. The two
                // sub-passes below are the expensive part. The art repair reads every offloaded album. The
                // rendition catch-up runs an ffmpeg transcode for each track on the user's own machine.
                using (var repair = await _sweep.Next(ct))
                {
                    if (!repair.Go)
                        return;
                }

                await RepairAlbumArtAsync(db, ct);

                // Head clips are a transcode per track, run by ffmpeg on the user's own machine. This is
                // cloud work like any other, so it must show on the job list. Otherwise a quiet library
                // could sit encoding with nothing on screen to say so.
                if (!batch && db.CloudTracksWithLocalFiles(1).Count > 0)
                {
                    _jobs.StartBatch(CloudOp.Upload);
                    batch = true;
                }

                // The head clips are checked the same way. A library uploaded before they existed has none.
                // This machine is the cheapest place to make them.
                using (var clips = await _sweep.Next(ct))
                {
                    if (!clips.Go)
                        return;
                }

                await CatchUpRenditionsAsync(db, ct);
            }

            // The track names, the folders they are filed in, and the file details.
            //
            // This runs on its own clock, not inside the block above. That block runs once per session and
            // after uploads. A metadata edit is neither of those. Retagging an album produces no upload at
            // all, because the canonical audio does not change. So a run started by that edit would find
            // nothing to do, and the correction would wait for a restart.
            //
            // It is throttled, because the kick fires on every library change. A rename typed one letter at
            // a time must not cause a catalogue listing for each keystroke.
            if (CloudPassCadence.DueOnTheClock(uploadedSomething, _lastCatalogPass, DateTime.UtcNow))
            {
                _lastCatalogPass = DateTime.UtcNow;
                await RefreshCatalogAsync(db, ct);
            }

        }
        catch (OperationCanceledException)
        {
            // Paused, or the app is closing. The queue is in the database; it will pick up again.
        }
        catch (CloudQuotaExceededException ex)
        {
            // Nothing else will fit. Stopping is the whole response. Sending a library's worth of
            // uploads that cannot land helps nobody. NoteStorageFull, below, is what tells the user.
            _log.LogWarning("[Cloud] Storage full: {Used} of {Quota}", ex.UsedBytes, ex.QuotaBytes);

            // Only when the refusal came with numbers. A 413 response whose body could not be read carries
            // zeroes. Writing those zeroes over the last quota reading would turn a known full store into
            // an unknown one. An unknown store reads as "room, probably" everywhere else.
            if (ex.QuotaBytes > 0)
                _status = _status with { UsedBytes = ex.UsedBytes, QuotaBytes = ex.QuotaBytes };

            NoteStorageFull();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Cloud] Sync stopped");
            _status = _status with { LastError = new CloudTrouble.SyncStopped(Faults.Note("cloud", "sync pass", ex)) };
        }
        finally
        {
            await RefreshQuotaAsync(CancellationToken.None);
            Report(running: false, db);

            // This checks which of two endings this was. A run that the user stopped did not finish.
            // Calling EndBatch for it would mark the whole list as done, even though the queue was mostly
            // untouched. Only a pause by hand counts. Closing the app, or a mode change that stops the
            // worker, is not the user saying "stop here and show me where I got to". Those leave nothing on
            // screen to read anyway.
            if (batch)
            {
                if (_paused)
                    _jobs.PauseBatch();
                else
                    _jobs.EndBatch();
            }
        }
    }

    /// <summary>
    /// What running out of room does: stop, say so, and stop asking for more than can be had.
    ///
    /// <para>"All my music" is a standing instruction to upload the collection. A full store turns that
    /// into an instruction that can only keep failing. Every library change would queue the rest again, and
    /// every pass would hit the same limit. So the mode drops to the user's own selection, which is the
    /// most that can still be honoured. The drop is written down, so that room bought later restores what
    /// the user actually asked for. Nothing already in the cloud is removed, and nothing is deleted here at
    /// all.</para>
    ///
    /// <para>The window belongs to the shell. See <see cref="StorageFull"/>.</para>
    /// </summary>
    /// <returns>True, always, so a run can record that it has said this once.</returns>
    private bool NoteStorageFull()
    {
        _log.LogInformation(
            "[Cloud] Storage is full ({Used} of {Quota}) — nothing more is being sent up",
            _status.UsedBytes, _status.QuotaBytes);

        _status = _status with { LastError = new CloudTrouble.StorageFull() };

        if (CloudFullPolicy.WhenFull(_settings.CloudMode) is var fallback && fallback != _settings.CloudMode)
        {
            _log.LogInformation("[Cloud] Storage is full — dropping from all music to the chosen tracks");

            // This order matters. The flag is read when the mode changes, and setting the mode raises
            // SettingsService.Changed, which rewrites the queue.
            _settings.SyncAllPausedByFull = true;
            _settings.CloudMode = fallback;
        }

        StorageFull?.Invoke();
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Puts "all my music" back once the limit is gone.
    ///
    /// <para>This happens only with unlimited room, and only over the mode that the drop left behind. Room
    /// freed by removing a record does not count as a way back. A few gigabytes would send the whole
    /// library against the same limit again. A mode the user has chosen since then is theirs, so it is not
    /// restored over. Tracks the user picked resume on their own once they fit, because that is decided per
    /// file, not per mode.</para>
    /// </summary>
    private void RestoreSyncAllIfBought()
    {
        if (!CloudFullPolicy.ShouldRestoreAll(
                _settings.SyncAllPausedByFull, _status.Space.Unlimited, _settings.CloudMode))
            return;

        _log.LogInformation("[Cloud] Storage is unlimited now — putting all music back on");
        _settings.SyncAllPausedByFull = false;
        _settings.CloudMode = CloudSyncMode.All;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _library.Opened -= OnLibraryOpened;
        _closing.Cancel();
        _cts?.Cancel();
        _cts?.Dispose();
        _gate.Dispose();
    }
}

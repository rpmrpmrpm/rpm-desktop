using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Keeps this install's library showing the whole collection: what is on this disk, and what the user's
/// other installs have put in the cloud.
///
/// <para>
/// The mirror is a copy of one folder only. So a second computer signed into the same account shows only
/// its own drive, and a library offloaded from the first machine shows nowhere at all. The music goes up,
/// the local copies are removed, and no other install can see that anything exists. This service fixes
/// that.
/// </para>
///
/// <para>
/// So the catalogue is read and adopted into the mirror as rows with no local file
/// (<see cref="LibraryDatabase.MirrorCloudLibrary"/>). Everything downstream, including the wall, the list,
/// the data table, search, the queue and the playlists, is built on rows. None of it needs to know that a
/// row has no file. A row with no file on disk plays by streaming, the same as an offloaded one does.
/// </para>
///
/// <para>
/// The catalogue is read on a timer, not pushed. The library is written from whichever machine the user is
/// at, and the other machines find out by asking. This is the same arrangement the phone uses.
/// </para>
/// </summary>
public sealed partial class CloudMirrorService : IDisposable
{
    /// <summary>
    /// How often the timer checks whether a read is due. The read itself happens at most once every
    /// <see cref="CloudPassCadence.MirrorInterval"/>, counted from the last read
    /// (<see cref="CloudPassCadence.MirrorPassDue"/>). A tick that finds no read due returns at once.
    ///
    /// <para>A full listing is several requests, so the read is not a poll to run every minute. A change
    /// made on another machine is not urgent here. Anything the user does that could matter (signing in,
    /// opening a library, or a sync run finishing) refreshes the catalogue on its own. So this timer is
    /// only the floor.</para>
    /// </summary>
    private static readonly TimeSpan Tick = CloudPassCadence.MirrorTick;

    private readonly LibraryService _library;
    private readonly AuthService _auth;
    private readonly RpmCloudClient _cloud;
    private readonly ILogger<CloudMirrorService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The library the last pass ran against. Opening a different library is a reason to read the
    /// catalogue again. Re-opening the same library is not, although <see cref="LibraryService.Changed"/>
    /// fires for it on every import.</summary>
    private string? _mirroredRoot;

    private DateTime _lastPass = DateTime.MinValue;

    /// <summary>True while this service is the one writing to the mirror. This stops the library change it
    /// raises from causing another catalogue read.</summary>
    private bool _writing;

    public CloudMirrorService(
        LibraryService library,
        AuthService auth,
        RpmCloudClient cloud,
        ILogger<CloudMirrorService> log)
    {
        _library = library;
        _auth = auth;
        _cloud = cloud;
        _log = log;

        _auth.Changed += OnAuthChanged;
        _library.Changed += OnLibraryChanged;

        // A pass writes the whole catalogue into the library. It must be stopped before the library is
        // closed, not left to find the connection closed under it.
        _library.StopBeforeClose(CloseAsync);

        Shell.Background.Run(() => TickAsync());
    }

    /// <summary>
    /// Stops this service for good before the library is closed. <see cref="LibraryService.Dispose"/> calls
    /// it through <see cref="LibraryService.StopBeforeClose"/>, and waits for it.
    ///
    /// <para>The closing token is cancelled first. That stops the tick, cancels a catalogue read in the air,
    /// stops a write in <see cref="Apply"/> at its next batch of albums, and stops every later pass before it
    /// takes the gate. The gate is then taken, which waits for a pass that is inside to reach that point and
    /// let go. A stopped write leaves whole batches committed and does not prune (see
    /// <c>LibraryDatabase.MirrorCloudLibrary</c>); the next pass finishes it.</para>
    /// </summary>
    internal async Task CloseAsync()
    {
        _closing.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
    }

    /// <summary>Raised after a pass has changed something, so a page showing the library can draw again.
    /// The library's own <see cref="LibraryService.Changed"/> is raised too. This event is for anything
    /// that needs to know that the cloud was the cause.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised with the whole catalogue every time it is read here.
    ///
    /// <para>This keeps the metadata sync separate from the upload run. An install can have no opinion at
    /// all about what should be uploaded. The user may never have turned syncing on, or may have turned it
    /// off. Even so, the install is a full member of the collection. It shows the cloud's records, it can
    /// be edited, and its edits must reach the other installs. If the catalogue pass depended on "is there
    /// anything to upload", such an install would send nothing, and would not receive corrections
    /// made elsewhere.</para>
    ///
    /// <para>The sync service listens. This service has no dependency in the other direction. It knows
    /// nothing about who is listening, or what they do with the data.</para>
    /// </summary>
    public event Action<IReadOnlyList<CloudTrack>>? CatalogRead;

    /// <summary>
    /// Reads the catalogue and brings the mirror in line with it.
    ///
    /// <para>This is quiet about failures. A listing that will not come means the machine is offline, or a
    /// token has expired. The library on this disk is still a library. The read is tried again on the next
    /// tick.</para>
    /// </summary>
    /// <param name="force">Ignore the interval. The user asked, or something changed that makes the held
    /// answer wrong.</param>
    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        // The `--gallery` harness runs on a made-up library whose files are not on disk. A real account's
        // catalogue must not be copied into it, because that would put the user's own records in the middle
        // of the demo.
        if (GalleryMode.Active) return;

        if (_closing.IsCancellationRequested) return;
        if (!_auth.IsLoggedIn || !_library.IsOpen) return;
        if (!CloudPassCadence.MirrorPassDue(force, _lastPass, DateTime.UtcNow)) return;

        // Every pass stops when the library is about to be closed, whoever started it and with whatever token.
        using var closingOrCaller = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        ct = closingOrCaller.Token;

        if (!await NetworkAllows(ct)) return;

        if (!await _gate.WaitAsync(0, ct)) return;   // A pass is already running and sees the same data.
        try
        {
            IReadOnlyList<CloudTrack> catalog;
            try
            {
                catalog = await _cloud.ListAsync(CloudShelf.Own, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[Cloud] Could not read the catalog to mirror it");
                return;
            }

            // Checked under the gate and before the write. CloseAsync waits for the gate, so a close that has
            // begun is either seen here or waits for this write to finish.
            ct.ThrowIfCancellationRequested();

            _lastPass = DateTime.UtcNow;
            Apply(catalog, ct);
            CatalogRead?.Invoke(catalog);

            if (await BackfilledAsync(ct))
            {
                // The store has filled in what it knew, and the rows in hand are now out of date. The
                // catalogue is read once more, so the lengths, track numbers and formats are applied to
                // this library now, not at the next tick.
                try
                {
                    var again = await _cloud.ListAsync(CloudShelf.Own, ct);
                    ct.ThrowIfCancellationRequested();
                    Apply(again, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "[Cloud] Could not re-read the catalog after the backfill");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether this session has already asked the store to fill in its own catalogue.</summary>
    private bool _backfilled;

    /// <summary>
    /// Asks the store to complete the catalogue rows from the sidecars it holds.
    ///
    /// <para>A track uploaded before the catalogue held more than a name has no length, no track number and
    /// no format. Without them the library cannot put the record in order, and the track info pane is
    /// empty. The sidecar beside each of those tracks has all of this. The work is done on the server, it
    /// can be repeated safely, and it is cheap. It is done once per session, and only when there is
    /// something to fix.</para>
    /// </summary>
    /// <returns>True when rows were actually updated, so the caller reads the catalogue again.</returns>
    private async Task<bool> BackfilledAsync(CancellationToken ct)
    {
        if (_backfilled || !_library.IsOpen) return false;

        try
        {
            if (_library.Db.CountAdoptedWithoutFacts() == 0) return false;

            _backfilled = true;
            var (examined, updated) = await _cloud.BackfillCatalogAsync(ct);

            if (updated > 0)
                _log.LogInformation(
                    "[Cloud] The store filled in {Updated} of {Examined} catalog rows from their sidecars",
                    updated, examined);

            return updated > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "[Cloud] Could not have the catalog backfilled");
            return false;
        }
    }

    /// <summary>
    /// The same pass, over a catalogue that somebody else has already fetched. This is the sync run's own
    /// listing, which is the whole thing and at most a few minutes old. One listing does for both.
    /// </summary>
    /// <param name="catalog">The whole catalogue.</param>
    /// <param name="ct">The caller's stop. The write stops at its next batch of albums when it is
    /// cancelled.</param>
    public void Adopt(IReadOnlyList<CloudTrack> catalog, CancellationToken ct)
    {
        if (GalleryMode.Active) return;
        if (!_auth.IsLoggedIn || !_library.IsOpen) return;

        _lastPass = DateTime.UtcNow;
        Apply(catalog, ct);
    }

    /// <summary>
    /// What the account already stores, by the hash of the audio: canonical SHA-256 → the id it is filed
    /// under.
    ///
    /// <para>The upload worker reads this before it sends anything. Two installs that rip the same CD
    /// produce the same canonical audio. If it were stored twice, there would be two catalogue rows, two
    /// quota charges, and one record drawn with every song on it twice. So the second machine takes the
    /// first one's identity instead. See <see cref="KnownId"/>.</para>
    /// </summary>
    private Dictionary<string, string> _byHash = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The id the account already holds this exact audio under, or null when it holds none.
    ///
    /// <para>This is as of the last catalogue read. It is the best answer available without a request per
    /// track. A stale "no" costs nothing except an upload that would have happened anyway.</para>
    /// </summary>
    public string? KnownId(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;

        lock (_byHashGate)
            return _byHash.TryGetValue(sha256.Trim(), out var id) ? id : null;
    }

    private readonly object _byHashGate = new();

    /// <summary>Whether a pass may go to the network now. <see cref="RPM.Core.Net.NetworkReach.Shared"/> in
    /// the app; a test stands in its own answer, because the shared gate measures the real internet.</summary>
    internal Func<CancellationToken, ValueTask<bool>> NetworkAllows { get; init; } =
        ct => RPM.Core.Net.NetworkReach.Shared.AllowAsync(ct);

    /// <param name="catalog">The whole catalogue.</param>
    /// <param name="ct">Stops the write at its next batch of albums. A stop is not a failure: it is thrown
    /// on, not logged.</param>
    private void Apply(IReadOnlyList<CloudTrack> catalog, CancellationToken ct)
    {
        var byHash = new Dictionary<string, string>(catalog.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var track in catalog)
        {
            // The first writer wins. The listing is stable, so a hash that the account holds twice always
            // resolves to the same one of them.
            if (!string.IsNullOrWhiteSpace(track.Sha256))
                byHash.TryAdd(track.Sha256.Trim(), track.RpmId);
        }

        lock (_byHashGate) _byHash = byHash;

        CloudMirrorResult result;
        try
        {
            result = _library.Db.MirrorCloudLibrary(catalog, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "[Cloud] Could not mirror the catalog into the library");
            return;
        }

        _mirroredRoot = _library.Root;

        if (result.Total == 0)
            return;

        _log.LogInformation(
            "[Cloud] The library took {Adopted} records' worth of tracks from the cloud, corrected {Updated} and dropped {Forgotten}",
            result.Adopted, result.Updated, result.Forgotten);

        Notify();
    }

    /// <summary>
    /// <see cref="Forget"/>, under the same gate as a pass and stopped by the same close.
    ///
    /// <para>Under the gate, because a pass that read the catalogue before the sign-out would otherwise write
    /// it into the library after the rows were removed. The pass finishes first, and then its rows go too.
    /// Stopped by the close, because the library is about to be closed: a close that has begun is seen
    /// either before the gate is taken (the wait is cancelled) or by <see cref="CloseAsync"/>, which waits
    /// for the gate.</para>
    /// </summary>
    private async Task ForgetAsync()
    {
        await _gate.WaitAsync(_closing.Token).ConfigureAwait(false);
        try
        {
            if (_closing.IsCancellationRequested || _auth.IsLoggedIn) return;
            Forget();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes the adopted rows again. This happens when signing out, and when signing in as somebody else.
    ///
    /// <para>The rows come from the cloud, not from this disk. Without a token there is nothing to play them
    /// from. Leaving them would also show the next person to sign in a wall of somebody else's
    /// records.</para>
    /// </summary>
    private void Forget()
    {
        if (!_library.IsOpen) return;

        int dropped;
        int playlists;
        try
        {
            dropped = _library.Db.ForgetAdopted();

            // The playlists the account lent to this machine are removed as well. The listener's own
            // playlists are kept, because they were made here. See ForgetAdoptedPlaylists. This must not go
            // through the ordinary delete, because that would tell the cloud the playlists were let go.
            playlists = _library.Db.ForgetAdoptedPlaylists();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Cloud] Could not drop the adopted rows");
            return;
        }

        _mirroredRoot = null;
        _lastPass = DateTime.MinValue;

        if (dropped == 0 && playlists == 0) return;

        _log.LogInformation(
            "[Cloud] {Count} cloud-only tracks and {Playlists} playlist(s) left the library with the account",
            dropped, playlists);
        Notify();
    }

    private void Notify()
    {
        _writing = true;
        try { _library.NotifyChanged(); }
        finally { _writing = false; }

        Changed?.Invoke();
    }

    private void OnAuthChanged()
    {
        if (_auth.IsLoggedIn)
            Shell.Background.Run(() => RefreshAsync(force: true));
        else
            Shell.Background.Run(ForgetAsync);
    }

    /// <summary>
    /// A library was opened. Reading the catalogue against a mirror this service has not seen before is the
    /// whole point. It is how a fresh install shows the collection before a single file has been brought
    /// down.
    ///
    /// <para>This is not done on every change. The library raises a change for every import, retag and play
    /// count. This service raises its own change at the end of a pass, so it does not need to respond to
    /// those.</para>
    /// </summary>
    private void OnLibraryChanged()
    {
        if (_writing || !_library.IsOpen) return;
        if (string.Equals(_mirroredRoot, _library.Root, StringComparison.Ordinal)) return;

        Shell.Background.Run(() => RefreshAsync(force: true));
    }

    /// <summary>The floor. The catalogue is read on its own clock, so an edit made on another machine
    /// arrives without anybody pressing anything.</summary>
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(Tick);
        try
        {
            while (await Cancellable.TickAsync(timer, _closing.Token))
                await RefreshAsync(ct: _closing.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing. The tick itself does not throw, but the refresh behind it still can.
        }
    }

    public void Dispose()
    {
        _auth.Changed -= OnAuthChanged;
        _library.Changed -= OnLibraryChanged;
        _closing.Cancel();
        _closing.Dispose();
        _gate.Dispose();
    }
}

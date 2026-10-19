using System.Collections.Concurrent;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Gets a track's <see cref="TrackAnalysis"/>. The result comes from the library database if it is there
/// and still describes the file on disk. Otherwise the track is decoded, the analyser runs over it, and the
/// result is stored on the way back.
///
/// <para>Analysis costs a whole decode, so it never happens on the UI thread and never twice for the same
/// file at once: concurrent askers for one track share a single running job. The DJ starts a set on
/// whatever is already analysed and takes the rest as it lands, so the wait is only ever for the first
/// record.</para>
/// </summary>
public sealed partial class TrackAnalysisService : IDisposable
{
    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<string, Task<TrackAnalysis?>> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One upgrade at a time, across every set that asks for one. The whole point of the pass is
    /// that it happens behind music that is already playing, and four concurrent decodes of whole tracks is
    /// not behind anything.</summary>
    private readonly SemaphoreSlim _upgrading = new(1, 1);

    /// <summary>Paths this run has already offered to the model, so a track the model declines is not
    /// offered again every time it turns up in a set. It is retried on the next launch, which is cheap
    /// because the only tracks a model declines are ones too short to read.</summary>
    private readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);

    // The trained readers for the beat grid and the key. They are built once and shared. Each holds its
    // weights in memory and queues callers behind them, so analyses that run at the same time take turns
    // instead of each loading a copy. Both are best-effort. If a model file is missing, the DSP passes do
    // the work instead.
    private readonly OnnxBeatModel _beats = new();
    private readonly KeyModelService _keys = new();

    /// <summary>
    /// Which machine reads the beats: this one, or the cloud.
    ///
    /// <para>Decided once at startup by <see cref="DjModelService"/> and read here per track. A machine
    /// that passed the test opens the weights beside it; a machine that failed never opens them at all and
    /// sends each window to the store instead, which is the same graph and the same answer for a fraction
    /// of the memory. See <see cref="CloudBeatModel"/>.</para>
    /// </summary>
    private IBeatModel? Beats =>
        _models.CanRunLocally ? _beats : CloudBeats;

    /// <summary>The hard ceiling on decodes at once. What is actually used at any moment is <see
    /// cref="AnalysisThrottle"/>'s answer, which is this or less.</summary>
    private readonly SemaphoreSlim _slots = new(AnalysisThrottle.MaxSlots);

    /// <summary>The background sweep in progress, and the handle that stops it. Replaced whole when another
    /// list arrives: the new playlist is the one being listened to, and the old one's remaining tracks are
    /// of no interest to anybody.</summary>
    private CancellationTokenSource? _sweep;

    /// <summary>How long to wait before asking again, when the machine does not have enough memory to
    /// spare.</summary>
    private static readonly TimeSpan DeferredRetry = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The shared pool, when there is one. Optional in the same way everything about the pool is: a machine
    /// that is signed out, offline or built without it analyses exactly as it always did.
    ///
    /// <para>A property rather than a constructor argument for the same reason <see cref="Transport"/> is
    /// one. This service is built early and by a great many things, and the pool client is built on the
    /// cloud client, which is built on a sign-in.</para>
    /// </summary>
    public Cloud.RecordingPoolClient? Pool { get; set; }

    /// <summary>
    /// The beat model in the cloud, for a machine that cannot run one. Set alongside <see cref="Pool"/>,
    /// and for the same reason. It is built on the cloud client, which is built on a sign-in. Null means a
    /// machine that cannot read beats at all, which analyses with the DSP passes exactly as it did before
    /// any model existed.
    /// </summary>
    public CloudBeatModel? CloudBeats { get; set; }

    /// <summary>
    /// The fingerprinter, for naming a record the pool might already know under another rip.
    ///
    /// <para>A property for the same reason the two above are: this service is built before anything is
    /// signed in, and the fingerprinter is built on the recognition backend, which is built on a sign-in.
    /// Null is a machine that does not fingerprint, which reads its own records exactly as it always
    /// did.</para>
    /// </summary>
    public Catalog.FingerprintService? Fingerprints { get; set; }

    public TrackAnalysisService(LibraryService library, DjModelService models)
    {
        _library = library;
        _models = models;
    }

    private readonly DjModelService _models;

    /// <summary>
    /// What the player is doing, for the throttle to read: playing at all, and mixing.
    ///
    /// <para>A property rather than a constructor argument because the dependency runs the other way: the
    /// DJ is built on this service, so this service cannot be built on him. He sets it when he is made, and
    /// until somebody does, analysis simply assumes an idle machine.</para>
    /// </summary>
    public Func<(bool Playing, bool Mixing)>? Transport { get; set; }

    /// <summary>
    /// What the machine looks like right now.
    ///
    /// <para>The memory figure comes from the garbage collector's view of the machine. The collector
    /// refreshes it when it collects, not when it is asked. So it lags, and in a process that has just
    /// started it may be missing. Both are fine for what it is used for. This decides whether to start
    /// another decode. It does not decide whether an allocation will succeed.</para>
    /// </summary>
    private MachineState Machine()
    {
        var (playing, mixing) = Transport?.Invoke() ?? (false, false);
        var memory = GC.GetGCMemoryInfo();
        long available = memory.TotalAvailableMemoryBytes > 0
            ? Math.Max(0, memory.TotalAvailableMemoryBytes - memory.MemoryLoadBytes)
            : 0;

        return new MachineState(Environment.ProcessorCount, available, playing, mixing);
    }

    /// <summary>
    /// Drops the current thread to the bottom of the queue for the length of one analysis, and puts it back
    /// afterwards.
    ///
    /// <para>This is the cheapest throttle there is. The work still runs, but it gives way the moment
    /// anything else needs the core. That is the arrangement wanted between a background sweep and the UI.
    /// Windows honours thread priority. Linux and macOS ignore it for an unprivileged process. That is why
    /// it is not the only throttle here. On those systems the slot count and the pause between tracks do
    /// the work.</para>
    ///
    /// <para>Restoring matters: these are pool threads, and one left at the bottom of the queue would go
    /// back to the pool that way and slow down whatever it was next given.</para>
    /// </summary>
    private readonly struct Yielding : IDisposable
    {
        private readonly ThreadPriority _was;

        public Yielding()
        {
            _was = Thread.CurrentThread.Priority;
            try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
            catch { /* not every host lets a thread say so */ }
        }

        public void Dispose()
        {
            try { Thread.CurrentThread.Priority = _was; }
            catch { }
        }
    }

    /// <summary>Raised on a background thread each time a track finishes analysing. The DJ's crate listens
    /// to it, so a record analysed after the set started still gets beat-matched.</summary>
    public event Action<TrackAnalysis>? Analyzed;

    /// <summary>What is already known about a file, without analysing anything. Null when it has never been
    /// analysed, or when the file has changed since it was.</summary>
    public TrackAnalysis? Cached(string absolutePath)
    {
        if (!_library.IsOpen || !File.Exists(absolutePath))
            return null;

        var file = new FileInfo(absolutePath);
        var relative = Relative(absolutePath);
        if (relative is null)
            return null;

        long length, modified;
        try
        {
            (length, modified) = (file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch (IOException)
        {
            return null;   // gone since the check above: there is no file for a reading to describe
        }

        // Unanswered is nothing known. A refusal has put the problem screen up over the app
        // (LibraryService.Read).
        return _library.Read(_library.Db, "a file's cached analysis",
                db => db.GetTrackAnalysis(relative, length, modified, absolutePath))
            .Match(found => found, _ => null);
    }

    /// <summary>
    /// The analysis for a file: cached if possible, computed if not. Null when the file cannot be decoded.
    /// The DJ still plays such a track. It blends the track instead of matching it.
    /// </summary>
    public Task<TrackAnalysis?> GetAsync(string absolutePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);

        if (Cached(absolutePath) is { } cached)
            return Task.FromResult<TrackAnalysis?>(cached);

        // One job per file: two playlists containing the same track, or a re-entrant caller, wait on the
        // same decode rather than starting a second one.
        return _running.GetOrAdd(absolutePath, path => Task.Run(async () =>
        {
            try
            {
                if (!await Cancellable.WaitAsync(_slots, ct).ConfigureAwait(false))
                    return null;
                try { return Analyze(path, ct); }
                finally { _slots.Release(); }
            }
            catch (OperationCanceledException) { return null; }
            finally { _running.TryRemove(path, out _); }
        }));
    }

    /// <summary>
    /// Analyses a whole list in the background. Nothing is awaited by the caller: the DJ is already
    /// playing, and each finished analysis is announced through <see cref="Analyzed"/>.
    ///
    /// <para>This list <em>replaces</em> the one before it. A listener who starts another playlist has said
    /// what they want analysed. That is not the records they have just moved away from. So the previous
    /// sweep is stopped where it stands, including the track it is in the middle of, and the machine goes
    /// to the new list instead. Without this, the old list would run to the end first. That means several
    /// tracks in flight at once, each needing a decode plus nine model windows, all for records nobody is
    /// going to hear.</para>
    /// </summary>
    /// <param name="urgent">
    /// True for the records of a set that is playing. The pace stops being a negotiation: no pause between
    /// tracks, and no waiting for the machine to look less busy.
    ///
    /// <para>On a machine short of memory, that wait could last for ever. The set would go on playing and
    /// quietly stop beat-matching, which is the failure everything here is built to prevent. Dropping the
    /// wait is safe, because the question has already been answered somewhere better. <see
    /// cref="DjModelService"/> measured this machine at startup. A machine that is reading records for
    /// itself has been shown to have the memory for one. A machine that has not is not analysing here at
    /// all, and its windows go to the store.</para>
    /// </param>
    public void AnalyzeInBackground(
        IEnumerable<string> absolutePaths, CancellationToken ct = default, bool urgent = false)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);

        // Snapshotted here: the caller's list may be a live collection, and this is about to be read from
        // another thread for as long as the sweep lasts.
        var wanted = absolutePaths.Where(path => Cached(path) is null).ToList();

        var fresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref _sweep, fresh);
        Abandon(previous);

        if (wanted.Count == 0)
        {
            // Nothing to analyse, but the list still counted: the sweep before it is stopped either way,
            // which is most of what this method is for.
            Interlocked.CompareExchange(ref _sweep, null, fresh);
            fresh.Dispose();
            return;
        }

        _ = Task.Run(() => SweepAsync(wanted, urgent, fresh.Token), fresh.Token);
    }

    /// <summary>Stops a sweep and lets go of its handle. Cancelling a source somebody is still inside is
    /// the point; disposing it under them is not, so the disposal waits for the cancel to have been
    /// delivered.</summary>
    private static void Abandon(CancellationTokenSource? source)
    {
        if (source is null)
            return;

        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
        source.Dispose();
    }

    /// <summary>
    /// Works through one list at whatever pace the machine can stand.
    ///
    /// <para>The pace is read once for how many run at a time. That is fixed for the length of the sweep.
    /// It is read again before every track, for the pause between them and for whether the machine has the
    /// memory at all. So a sweep that started on an idle machine slows down when a record is put on, which
    /// is when it matters.</para>
    /// </summary>
    private async Task SweepAsync(IReadOnlyList<string> paths, bool urgent, CancellationToken ct)
    {
        var opening = urgent
            ? AnalysisThrottle.ForDirectAsk(Machine())
            : AnalysisThrottle.ForBackground(Machine());

        Said.Line("analysis",
            $"{(urgent ? "reading a set's" : "sweeping")} {paths.Count} tracks: " +
            $"{opening.Slots} at once on {Environment.ProcessorCount} cores, " +
            $"{Machine().AvailableMemoryBytes / (1024 * 1024)} MB free, " +
            $"{opening.Breath.TotalSeconds:F0}s between tracks{(opening.Deferred ? ", waiting for memory" : "")}");

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = opening.Slots,
            CancellationToken = ct,
        };

        // One question for the whole list before a file is opened. A sweep is the case the pool was built
        // for: a fresh install facing a library everybody else has already been through.
        await AdoptFromPoolAsync(paths, ct).ConfigureAwait(false);

        try
        {
            await Parallel.ForEachAsync(paths, options, async (path, token) =>
            {
                // A sweep is stopped whenever another list replaces it. This is normal, and it happens on
                // every change of queue. Every wait in here can be cancelled. Each cancel shows up as an
                // exception, and the handler at the top of this track catches it, so this track simply
                // stops. The loop still ends on its own token (the outer catch below). No exception leaves
                // the delegate.
                try
                {
                    // Asked again: the sweep may have been queued behind another one that did this track,
                    // or an ordinary analysis may have.
                    if (Cached(path) is not null)
                        return;

                    // A set's records never wait. Everything else asks the machine first.
                    var pace = urgent ? opening : await AffordableAsync(token).ConfigureAwait(false);

                    if (!await Cancellable.WaitAsync(_slots, token).ConfigureAwait(false))
                        return;
                    try
                    {
                        using var yielding = new Yielding();
                        Analyze(path, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One track that will not read is not a reason to abandon the list.
                        Said.Line("analysis", $"{Path.GetFileName(path)} failed: {ex.Message}");
                    }
                    finally
                    {
                        _slots.Release();
                    }

                    if (pace.Breath > TimeSpan.Zero)
                        await Cancellable.DelayAsync(pace.Breath, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Said.Line("analysis", "sweep put down; the list it was on is no longer the one playing");
        }
    }

    /// <summary>One record of a set: where its file is, and what the store calls it. The second is only
    /// needed for a record that is not on this disk. See <see cref="AnalyzeForSet"/>.</summary>
    public readonly record struct SetRecord(string AbsolutePath, string? RpmId);

    /// <summary>
    /// Reads the records of a set that is playing. The one entry point a DJ set uses, and the only one that
    /// treats the list as something somebody is waiting for.
    ///
    /// <para>Two kinds of record, and the difference is whether this machine holds the file:</para>
    ///
    /// <para>A record on this disk is decoded and read here, at once, with no pause between tracks and no
    /// waiting for a better moment. See the <c>urgent</c> parameter of <see cref="AnalyzeInBackground"/>.
    /// Whether the beat model runs on this machine or in the cloud is settled elsewhere, and that does not
    /// change the shape of this. Either way the reading is stored against the file and published to the
    /// pool. So it is worked out once, and never again, by anybody.</para>
    ///
    /// <para>A record that is only in the cloud has nothing here to decode. The store is asked to read its
    /// own copy, and the answer is watched for in the pool. It should arrive while the record is playing
    /// now, because the whole look-ahead is timed around that.</para>
    /// </summary>
    public void AnalyzeForSet(IReadOnlyList<SetRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var here = new List<string>(records.Count);
        var away = new List<SetRecord>();
        foreach (var record in records)
        {
            if (File.Exists(record.AbsolutePath))
                here.Add(record.AbsolutePath);
            else if (!string.IsNullOrWhiteSpace(record.RpmId))
                away.Add(record);
        }

        // Order matters: the analyser is started before the audio by the caller, so the shortest possible
        // set (two tracks, nothing cached) still has its second record read before it is needed.
        if (here.Count > 0)
        {
            AnalyzeInBackground(here, ct, urgent: true);

            // And behind that, the records read before this machine had any model at all: they have a
            // stored analysis, so nothing above will touch them, and it is the worse of the two readings
            // the app can produce.
            UpgradeInBackground(here, ct);
        }

        if (away.Count > 0)
            _ = Task.Run(() => AwaitStoreAsync(away, ct));
    }

    /// <summary>How long the store is given to read a set's cloud records. A blob it already holds is a
    /// decode and a pass, so an answer normally lands inside a record; past this the set has moved on and
    /// is blending them, which is what it does with any record it cannot match.</summary>
    private static readonly TimeSpan StorePatience = TimeSpan.FromMinutes(6);

    /// <summary>How often the pool is asked whether the store has finished.</summary>
    private static readonly TimeSpan StoreCheck = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the store to read the records this machine has no file for, and watches the pool until the
    /// answers arrive.
    ///
    /// <para>Nothing waits on this. Each reading that lands is stored against the row and announced like
    /// any other, so the DJ takes it onto a record still in his crate.</para>
    /// </summary>
    private async Task AwaitStoreAsync(IReadOnlyList<SetRecord> records, CancellationToken ct)
    {
        if (Pool is null || !_library.IsOpen)
            return;

        var waiting = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // path → relative
        foreach (var record in records)
            if (Relative(record.AbsolutePath) is { } relative)
                waiting[record.AbsolutePath] = relative;

        if (waiting.Count == 0)
            return;

        await Pool.EnsureAnalysedAsync([.. records.Select(r => r.RpmId!)], ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + StorePatience;
        while (waiting.Count > 0 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            IReadOnlyDictionary<string, TrackAnalysis> known;
            try
            {
                known = await Pool
                    .KnownAnalysesAsync(_library.Db, [.. waiting.Select(w => (w.Key, w.Value))], ct: ct)
                    .ConfigureAwait(false);
            }
            catch { return; }

            foreach (var (path, analysis) in known)
            {
                // Only write this down when the reading is complete. A grid published by an older analyser
                // has no edges. Storing one would mark the row as current. A track could then mix into its
                // own trailing silence for ever, if its file ever came back to this disk. Announce it
                // either way. The set is playing now, and an edgeless grid is a beat-match, where the
                // alternative is a blend. Nothing is lost by asking the pool again next time. That is one
                // batched question.
                if (analysis.HasEdges)
                    Store(path, analysis, sampleHash: null);

                Analyzed?.Invoke(analysis);
                waiting.Remove(path);
            }

            if (waiting.Count == 0)
            {
                Said.Line("analysis", "the store read every cloud record this set was missing");
                return;
            }

            if (!await Cancellable.DelayAsync(StoreCheck, ct).ConfigureAwait(false))
                return;
        }

        if (waiting.Count > 0)
            Said.Line("analysis", $"the store has not read {waiting.Count} of this set's cloud records yet; " +
                            "they are blended rather than matched");
    }

    /// <summary>How many tracks are asked about in one question. The endpoint's own ceiling, and a page of
    /// a sweep.</summary>
    private const int PoolPage = 50;

    /// <summary>
    /// Takes whatever the pool already knows about a list, before any of it is decoded.
    ///
    /// <para>Each track adopted saves a decode and a pass through the models, which this machine never has
    /// to spend. On a fresh install facing a library that other people have already heard, that is most of
    /// the sweep. Everything the pool has nothing for is left exactly where it was, for the sweep to read
    /// in the ordinary way.</para>
    /// </summary>
    private async Task AdoptFromPoolAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (Pool is null || !_library.IsOpen)
            return;

        foreach (var page in paths.Chunk(PoolPage))
        {
            if (ct.IsCancellationRequested)
                return;

            var asking = new List<(string AbsolutePath, string RelativePath)>(page.Length);
            foreach (var path in page)
                if (Relative(path) is { } relative)
                    asking.Add((path, relative));

            if (asking.Count == 0)
                continue;

            IReadOnlyDictionary<string, TrackAnalysis> known;
            try { known = await Pool.KnownAnalysesAsync(_library.Db, asking, ct: ct).ConfigureAwait(false); }
            catch { return; }

            int taken = 0;
            foreach (var (path, analysis) in known)
            {
                // Only a COMPLETE reading may be taken without reading the file.
                //
                // Nothing here decodes, so nothing here can fill in what the pool is missing. A reading
                // stored from this pass is stamped with the current analyser, and that stamp is what stops
                // anything looking at that track again. A reading published by an older analyser would
                // leave the row marked as current, but permanently short of whatever that analyser did not
                // measure. It would lack the edges, if they were not measured then, and it would lack the
                // bands, if they were not measured then.
                //
                // Left alone, the track is analysed in the ordinary way a moment later, and that path has
                // the decode in its hands and fills in everything the pool was missing.
                if (!analysis.HasEdges || !analysis.HasBands)
                    continue;

                // Stored without a sample hash: nothing here has decoded the file, so this machine has no
                // name of its own for the audio yet. The row is keyed on the file, which is enough for
                // every later lookup on this disk.
                Store(path, analysis, sampleHash: null);
                Analyzed?.Invoke(analysis);
                taken++;
            }

            if (taken > 0)
                Said.Line("analysis", $"{taken} of {asking.Count} came from the pool, unread");
        }
    }

    /// <summary>
    /// Waits until the machine can carry another analysis, and returns the pace to take it at.
    ///
    /// <para>What is being waited for is memory. One track through the beat model peaks near a gigabyte,
    /// and starting one on a machine that has not got it does not fail. It pages, and takes the music down
    /// with it. Waiting is free and the sweep has nowhere to be.</para>
    /// </summary>
    private async Task<AnalysisPace> AffordableAsync(CancellationToken ct)
    {
        var pace = AnalysisThrottle.ForBackground(Machine());

        // A machine that passed the startup test does not wait for anything. It was measured running a real
        // window, with the process watched while it ran, and it carried that window. A moment where the
        // collector's view of free memory reads low is not new information. Waiting on it would leave the
        // records behind the one playing unread for an entire session.
        if (_models.CanRunLocally)
            return pace with { Deferred = false };

        while (pace.Deferred)
        {
            if (!await Cancellable.DelayAsync(DeferredRetry, ct).ConfigureAwait(false))
                return pace;
            pace = AnalysisThrottle.ForBackground(Machine());
        }
        return pace;
    }

    /// <summary>True when a trained reading is available for a record. That is true with weights on this
    /// machine, or with the store's model standing in for the beat model on a machine that cannot carry
    /// one. The key model stays here either way. Ten megabytes of small convolutions is not what failed the
    /// test, and the key model has its own fall back to the chromagram.</summary>
    private bool HasModel =>
        (_models.CanRunLocally && _beats.IsAvailable) || CloudBeats?.IsAvailable == true || _keys.IsAvailable;

    /// <summary>
    /// Whether a record can be read at all right now. This is true with weights here, or with the store's
    /// model standing in for them. False is the machine that has neither. In that case <see
    /// cref="DjService"/> tells the listener, rather than starting a mix that cannot mix.
    /// </summary>
    public bool CanReadBeats =>
        (_models.CanRunLocally && _beats.IsAvailable) || CloudBeats?.IsAvailable == true;

    /// <summary>The least time the upgrade pass leaves the machine alone between tracks, whatever the
    /// throttle says the machine could stand. This pass is the lowest-value work in the app. Every track it
    /// touches already has an answer. So it is slow on purpose, even on an idle machine.</summary>
    private static readonly TimeSpan LeastBreath = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads these tracks again with the models, for the ones a model could improve.
    ///
    /// <para>What this catches is the row nothing else will: analysed by the DSP before the weights were on
    /// this machine, still current, and therefore served from the cache for ever. The set being played is
    /// the list worth spending it on. A track on a deck tonight is a track worth having a real grid for,
    /// and each one done is one the cache never has to do again.</para>
    ///
    /// <para>Everything about it is deliberately slow. One decode at a time, behind the same slots the
    /// ordinary analyses take, with a pause between tracks: the music is the foreground and this is not. A
    /// finished upgrade goes out through <see cref="Analyzed"/> like any other, so the DJ adopts it onto
    /// the deck it belongs to even if that record is the one playing.</para>
    /// </summary>
    public void UpgradeInBackground(IEnumerable<string> absolutePaths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        if (!HasModel)
            return;

        // Snapshotted on the caller's thread: the list belongs to the set that asked, and the set may be
        // torn down while this is still working through it.
        var wanted = absolutePaths.Where(WantsUpgrade).ToList();
        if (wanted.Count == 0)
            return;

        _ = Task.Run(() => UpgradeAsync(wanted, ct));
    }

    /// <summary>Whether this track has a stored reading that a model would improve on.</summary>
    private bool WantsUpgrade(string absolutePath) =>
        Cached(absolutePath) is { } stored && TrackAnalysis.WantsModelUpgrade(stored.Version, HasModel);

    private async Task UpgradeAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        // Two sets started in a row would otherwise put two of these loops on the machine at once, which is
        // the one thing the whole design is trying not to do.
        if (!await Cancellable.WaitAsync(_upgrading, ct).ConfigureAwait(false))
            return;

        try
        {
            foreach (var path in paths)
            {
                if (ct.IsCancellationRequested)
                    return;

                // Asked again rather than trusted from the snapshot: the earlier loop, or an ordinary
                // analysis, may have done this one while this loop was waiting its turn.
                lock (_offered)
                {
                    if (!_offered.Add(path))
                        continue;
                }
                if (!WantsUpgrade(path))
                    continue;

                var pace = default(AnalysisPace);
                try
                {
                    pace = await AffordableAsync(ct).ConfigureAwait(false);
                    if (!await Cancellable.WaitAsync(_slots, ct).ConfigureAwait(false))
                        return;
                    try
                    {
                        using var yielding = new Yielding();
                        Analyze(path, ct);
                    }
                    finally { _slots.Release(); }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // One track that will not read is not a reason to stop improving the rest.
                    Said.Line("analysis", $"upgrade failed for {Path.GetFileName(path)}: {ex.Message}");
                }

                var breath = pace.Breath > LeastBreath ? pace.Breath : LeastBreath;
                if (!await Cancellable.DelayAsync(breath, ct).ConfigureAwait(false))
                    return;
            }
        }
        finally
        {
            _upgrading.Release();
        }
    }

    private TrackAnalysis? Analyze(string absolutePath, CancellationToken ct)
    {
        if (!File.Exists(absolutePath))
            return null;

        var mono = PcmDecoder.DecodeMono(absolutePath, TrackAnalyzer.AnalysisRate, ct);
        if (mono.Length == 0 || ct.IsCancellationRequested)
            return null;

        // Worked out before the analyser runs, off the buffer it is about to read: this is what the audio
        // is called, as opposed to what the file is called. Costs one pass over an array already in memory.
        var sampleHash = AudioKey.OfSamples(mono, TrackAnalyzer.AnalysisRate, AudioKey.Ffmpeg);

        // Somebody has already heard this record, under another name. A re-tagged file, a second copy, a
        // library that moved: the stamp missed, the sound did not. Taking the stored reading skips the
        // models, which is nearly all of what an analysis costs.
        if (Adopt(absolutePath, sampleHash) is { } adopted)
        {
            Said.Line("analysis",
                $"adopted {Path.GetFileName(absolutePath)} from a row that already knew " +
                $"this audio: {adopted.Bpm:F1} BPM, {(adopted.Modelled ? "model" : "dsp")}");
            Analyzed?.Invoke(adopted);
            return adopted;
        }

        // The same question goes to the pool. The sample hash is worked out by now. This is the broadest
        // name this machine has, and the last chance not to spend the models on a record that somebody else
        // has already read.
        if (FromPool(absolutePath, sampleHash, ct) is { } shared && Describes(shared, mono, absolutePath))
        {
            // Whatever the pool did not know, this machine measures for itself before taking the rest. The
            // edges matter most. A reading published by an older analyser has none. Adopting it whole would
            // mark the row as current without them. A track would then mix into its own trailing silence
            // for ever, because nothing would look at it again. The decode is already done and the samples
            // are in hand, so this costs one pass.
            bool wasShort = !shared.HasEdges || !shared.HasBands;
            shared = Complete(shared, mono, TrackAnalyzer.AnalysisRate);

            Said.Line("analysis",
                $"{Path.GetFileName(absolutePath)} came from the pool: " +
                $"{shared.Bpm:F1} BPM, {(shared.Modelled ? "model" : "dsp")}" +
                $"{(wasShort ? ", the rest measured here" : "")}");

            Store(absolutePath, shared, sampleHash);

            // The pool learns what it was missing. The next machine to meet this record gets the whole
            // reading, and does not measure the same halves again.
            if (shared.HasEdges && Relative(absolutePath) is { } filled && _library.IsOpen)
                Pool?.PublishAnalysis(_library.Db, filled, shared);

            Analyzed?.Invoke(shared);
            return shared;
        }

        // Nobody has this file. But somebody may have this record. Another rip of the same CD has bytes and
        // decoded samples that are nothing like ours, because the two drives read at different offsets. The
        // fingerprint's name is for that case. It is asked last, because it is the only key whose answer
        // cannot be trusted as it stands. It names the music, so what comes back has to be fitted onto this
        // copy before anything is mixed on it.
        if (FromPoolByFingerprint(absolutePath, mono, ct) is { } matched)
        {
            Store(absolutePath, matched, sampleHash);

            // Published under THIS copy's own names as well, so the next machine to meet these exact bytes
            // finds them without going through any of the above.
            if (Relative(absolutePath) is { } fuzzy && _library.IsOpen)
                Pool?.PublishAnalysis(_library.Db, fuzzy, matched);

            Analyzed?.Invoke(matched);
            return matched;
        }

        var started = DateTime.UtcNow;
        var analysis = TrackAnalyzer.Analyze(absolutePath, mono, TrackAnalyzer.AnalysisRate, Beats, _keys, ct);

        // Said out loud, and specifically whether the models did it. The two paths disagree about tempo by
        // an octave on some records and about the bar by a beat on others, so "what did this track analyse
        // as" is not answerable without knowing which of them answered. A model that quietly failed to load
        // produces a complete, plausible, worse analysis rather than an error.
        Said.Line("analysis",
            $"{(analysis.Modelled ? "model" : "dsp  ")} {Path.GetFileName(absolutePath)}: " +
            $"{analysis.Bpm:F1} BPM (conf {analysis.BpmConfidence:F2}), {analysis.BeatsPerBar}/bar, " +
            $"downbeat #{analysis.FirstDownbeat} (conf {analysis.DownbeatConfidence:F2}), " +
            $"{(analysis.Key.IsKnown ? $"{analysis.Key.Camelot} {analysis.Key.Name}" : "no key")} " +
            $"(conf {analysis.KeyConfidence:F2}), {(DateTime.UtcNow - started).TotalSeconds:F1}s");

        // Store it even when nothing was found: "this file has no tempo" is worth remembering, and the file
        // stamp is what stops it being re-decoded on every set.
        Store(absolutePath, analysis, sampleHash);

        // Said to the world as well as to the mirror. The decode and the models have been spent; the next
        // person to meet this record should not have to spend them again.
        if (Relative(absolutePath) is { } published && _library.IsOpen)
            Pool?.PublishAnalysis(_library.Db, published, analysis);

        Analyzed?.Invoke(analysis);
        return analysis;
    }

    /// <summary>
    /// What the pool holds for this file, if anything.
    ///
    /// <para>Synchronous, because this sits inside a decode that is already running on a background thread
    /// and has nothing else to be doing. Bounded by the client's own patience, so a slow answer costs a few
    /// seconds rather than the analysis.</para>
    /// </summary>
    private TrackAnalysis? FromPool(string absolutePath, string? sampleHash, CancellationToken ct)
    {
        if (Pool is null || !_library.IsOpen || Relative(absolutePath) is not { } relative)
            return null;

        try
        {
            // The sample hash is handed over rather than read back off the row: it was worked out from the
            // buffer a moment ago and has not been written down yet, and it is the name most likely to find
            // this record in the pool.
            return Pool.KnownAnalysisAsync(_library.Db, absolutePath, relative, sampleHash, ct)
                .GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What the pool holds for this <em>recording</em>, as opposed to this file. This is the answer found
    /// under the fingerprint's name, fitted onto this copy.
    ///
    /// <para>This is the rung that closes the case the exact keys cannot: the same CD ripped on two
    /// computers. Their files differ, and their decoded samples differ by the drives' read offsets. The id
    /// in their tags is made up separately on each machine. So every exact name misses, and the second
    /// machine spends the models on a record that the first one has already read.</para>
    ///
    /// <para>Nothing that arrives this way is adopted as it stands: the grid is fitted onto this copy by
    /// <see cref="GridRealign"/> against its own onsets, and then handed to the analyser in place of a
    /// model, so every curve in the result is measured from the file actually here.</para>
    /// </summary>
    private TrackAnalysis? FromPoolByFingerprint(
        string absolutePath, float[] mono, CancellationToken ct)
    {
        if (Pool is null || !_library.IsOpen || Relative(absolutePath) is not { } relative)
            return null;

        // The name has to exist before it can be asked for. The background pass reaches every track
        // eventually. This record is being played now, so it is named now. That takes one fpcalc run and
        // one request. It is set against the ten or twenty seconds of model time that the lookup is trying
        // to save.
        long songId = 0;
        try { songId = Fingerprints?.NameNowAsync(relative, ct).GetAwaiter().GetResult() ?? 0; }
        catch { }

        if (songId == 0)
            return null;                    // nothing to ask under

        TrackAnalysis? borrowed;
        try
        {
            borrowed = Pool.KnownAnalysisAsync(_library.Db, absolutePath, relative, sampleHash: null, ct,
                                               fuzzyOnly: true)
                           .GetAwaiter().GetResult();
        }
        catch { return null; }

        if (borrowed is null)
            Said.Line("analysis",
                $"{Path.GetFileName(absolutePath)} is song:{songId} to the store, and nobody " +
                "has read that recording yet");

        if (borrowed is null)
            return null;

        // These are this copy's own onsets and edges, measured from the buffer already in hand. That takes
        // an FFT and a walk. They turn somebody else's grid into one that describes this file. The edges
        // say roughly how far apart the two rips are, and the onsets say exactly.
        var onsets = OnsetEnvelope.Compute(mono);
        var edges = AudioEdges.Find(mono, TrackAnalyzer.AnalysisRate);
        double duration = (double)mono.Length / TrackAnalyzer.AnalysisRate;

        var fitted = GridRealign.Fit(
            borrowed, duration, edges, onsets, OnsetEnvelope.FrameRate(TrackAnalyzer.AnalysisRate));

        if (!fitted.Kept || BorrowedBeats.Of(fitted.Analysis) is not { } grid)
        {
            // This may be a different master, a different edit, or a match that this record does not
            // support. The models run here instead. What they produce is published under both names, so the
            // pool learns that these two are not the same recording after all.
            Said.Line("analysis",
                $"{Path.GetFileName(absolutePath)} matched a pool record by fingerprint and its " +
                $"grid did not fit this rip (beats agree {fitted.Agreement:F2}); reading it here instead");
            return null;
        }

        // The borrowed grid goes in through the same door a model uses. So everything else about the record
        // is measured here, against that grid: the bands, the cues, the phrases, the drops, and the edges.
        // Nothing is carried across from a rip that starts at a different sample, except two things that
        // normally need a model. Those are the tempo octave and the bar phase. The key is also carried,
        // because it is a property of the music.
        var analysis = TrackAnalyzer.Analyze(
            absolutePath, mono, TrackAnalyzer.AnalysisRate,
            new BorrowedBeats(grid),
            BorrowedKey.Of(fitted.Analysis) is { } key ? new BorrowedKey(key) : null,
            ct);

        Said.Line("analysis",
            $"{Path.GetFileName(absolutePath)} came from the pool BY FINGERPRINT: " +
            $"song:{songId}, {analysis.Bpm:F1} BPM, {analysis.BeatsPerBar}/bar, " +
            $"{(analysis.Key.IsKnown ? analysis.Key.Camelot : "no key")}, grid moved " +
            $"{fitted.ShiftSeconds:+0.000;-0.000;0.000}s onto this rip (beats agree {fitted.Agreement:F2}); " +
            "the models were not run");

        return analysis;
    }

    /// <summary>
    /// Fills in whatever a borrowed reading arrived without.
    ///
    /// <para>An analysis that is borrowed rather than made is stamped with the current analyser, which is
    /// what makes it worth borrowing. Nothing re-reads a record that is already known. That is also what
    /// makes a gap in it permanent: a grid published before the edges were measured would leave the row
    /// current and edgeless for the life of the library, and a changeover would go on being timed to bar
    /// lines after the last note.</para>
    ///
    /// <para>The one thing needed to close it is the decoded audio, and every caller here is holding it.
    /// The beat span cannot be recovered this way, so it is not guessed at. It says which part of somebody
    /// else's grid was measured. This machine did not measure any of it.</para>
    /// </summary>
    private static TrackAnalysis Complete(TrackAnalysis analysis, ReadOnlySpan<float> mono, int sampleRate)
    {
        // The edges, when whoever published this reading had not measured them.
        if (!analysis.HasEdges && AudioEdges.Find(mono, sampleRate) is { IsKnown: true } edges)
        {
            // The cues come from a reading that did not know where the audio was, so they are held inside
            // it here exactly as the analyser holds its own.
            double introEnd = Math.Clamp(analysis.IntroEndSeconds, edges.StartSeconds, edges.EndSeconds);
            double outroStart = Math.Clamp(analysis.OutroStartSeconds, introEnd, edges.EndSeconds);

            analysis = analysis with
            {
                AudioStartSeconds = edges.StartSeconds,
                AudioEndSeconds = edges.EndSeconds,
                IntroEndSeconds = introEnd,
                OutroStartSeconds = outroStart,
            };
        }

        // The bands, and everything read off them. A reading published before the analyser measured these
        // has none. Adopting it as it stands would leave the row current, and permanently without them.
        // This is the same trap as with the edges, for a different field.
        //
        // The bands are the record split three ways. The shape is arithmetic over the bass curve, against
        // the grid that just arrived. Both come off the buffer already in hand. So filling them in costs
        // one pass and no model. That is the whole reason a borrowed reading is still worth having.
        if (!analysis.HasBands)
        {
            var bands = EnergyProfile.ComputeBands(mono, sampleRate);
            if (bands.Bass.Length > 0)
                analysis = analysis with
                {
                    BassCurve = bands.Bass,
                    MidCurve = bands.Mid,
                    TopCurve = bands.Top,
                    VocalCurve = bands.Vocal,
                    EnergyCurve = analysis.EnergyCurve.Count > 0 ? analysis.EnergyCurve : bands.Full,
                    Phrases = TrackShape.Phrases(
                        bands.Bass, analysis.BeatTimes, analysis.FirstDownbeat, analysis.BeatsPerBar),
                    // Tempo stability cannot be measured from a borrowed reading. The beats as they were
                    // heard carry it, and all that arrived is a straight line through somebody else's
                    // beats. So the value is kept as it arrived. Zero reads as "not measured" wherever it
                    // is used.
                    TempoStability = analysis.TempoStability,
                    DropSeconds = TrackShape.Drops(
                        bands.Bass, analysis.BeatTimes, analysis.FirstDownbeat, analysis.BeatsPerBar),
                    IntroLevel = TrackShape.LevelAt(bands.Full, analysis.IntroEndSeconds),
                    OutroLevel = TrackShape.LevelAt(bands.Full, analysis.OutroStartSeconds),
                };
        }

        return analysis;
    }

    /// <summary>
    /// Whether a borrowed reading is a reading of the record in <paramref name="mono"/> at all.
    ///
    /// <para>The pool is asked under a bundle of names. A bundle is a claim that all of those names stand
    /// for one record. Three of them are exact: the bytes, the samples, and the id in the tags. One is not.
    /// The song id is matched by ear, by a service. Where it names two different records with one number,
    /// the pool fuses them, and afterwards it serves either one's reading for the other. Nothing downstream
    /// can tell the difference. What comes back is a complete, confident, plausible analysis of the wrong
    /// record. It gets stored with this file's own size and date, so nothing ever looks at the track
    /// again.</para>
    ///
    /// <para>The cheapest thing two readings of one record must agree on is how long it is, and the decode
    /// is already in hand here, so the check costs a subtraction. A reading of a record of a different
    /// length is refused and the track is measured properly instead.</para>
    /// </summary>
    private static bool Describes(TrackAnalysis borrowed, float[] mono, string absolutePath)
    {
        double heard = mono.Length / (double)TrackAnalyzer.AnalysisRate;
        if (borrowed.DurationSeconds <= 0 ||
            Math.Abs(borrowed.DurationSeconds - heard) <= TrackAnalysis.SameRecordSeconds)
            return true;

        Said.Line("analysis",
            $"the pool's reading for {Path.GetFileName(absolutePath)} is of a " +
            $"{borrowed.DurationSeconds:F1}s record and this one plays {heard:F1}s: read here instead");
        return false;
    }

    /// <summary>
    /// The stored reading for this audio, put onto this file's row and returned. Null when no row on this
    /// machine describes this audio.
    ///
    /// <para>The row is written under this file's own stamp, so the next play answers from the fast path
    /// and never decodes at all. The numbers are not written into this file's tags. Any tags they were
    /// written into belong to a different file.</para>
    /// </summary>
    private TrackAnalysis? Adopt(string absolutePath, string? sampleHash)
    {
        if (sampleHash is null || !_library.IsOpen)
            return null;

        TrackAnalysis? found;
        try { found = _library.Db.GetTrackAnalysisBySampleHash(sampleHash, absolutePath); }
        catch { return null; }

        if (found is null)
            return null;

        Store(absolutePath, found, sampleHash);
        return found;
    }

    /// <summary>Writes an analysis onto a file's row, stamped with what the file is right now. Never
    /// throws: a mirror that will not take the row must not lose the analysis for this set.</summary>
    private void Store(string absolutePath, TrackAnalysis analysis, string? sampleHash)
    {
        if (!_library.IsOpen || Relative(absolutePath) is not { } relative)
            return;

        try
        {
            var file = new FileInfo(absolutePath);
            _library.Db.SaveTrackAnalysis(relative, file.Length, file.LastWriteTimeUtc.Ticks, analysis, sampleHash);
        }
        catch { }
    }

    /// <summary>The path as the library stores it. Null for a file outside the library root. Such a file
    /// can still be analysed, but it cannot be cached against a row.</summary>
    private string? Relative(string absolutePath)
    {
        var root = _library.Root;
        if (string.IsNullOrEmpty(root))
            return null;

        var full = Path.GetFullPath(absolutePath);
        var rooted = Path.GetFullPath(root);
        if (!full.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.GetRelativePath(rooted, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Lets go of the weights.
    ///
    /// <para>This is worth doing even at the end of a process. The two sessions hold ninety megabytes
    /// between them. They are loaded on the first track analysed. Without this call they would stay in
    /// memory for the rest of the app's life, whether or not anything is analysed again. Both models are
    /// disposed here, along with the rest of this class.</para>
    /// </summary>
    public void Dispose()
    {
        Abandon(Interlocked.Exchange(ref _sweep, null));
        _beats.Dispose();
        _keys.Dispose();
        _upgrading.Dispose();
        _slots.Dispose();
    }
}

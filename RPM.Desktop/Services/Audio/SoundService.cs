using System.Diagnostics;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Library;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;
using RPM.Onnx;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Measures each track's sound with the CLAP audio model, in the background, for the feeling lists
/// (Melancholy, Happy, Angry, Romantic).
///
/// <para><b>Where the model runs</b> follows the beat model's verdict (<see cref="DjModelService"/>),
/// measured once per session. A computer that can run the beat model runs this one too: it needs about
/// 250 MB against about 1 GB, and has no deadline. A computer that cannot sends each record's features
/// to the store instead (<see cref="Cloud.CloudSoundModel"/>), and a signed-out one measures nothing until
/// somebody signs in. A computer turned away only for free memory at startup is not one that cannot: it
/// hears its own records and waits for room before each (<see cref="SoundModelPlace"/>).</para>
///
/// <para><b>The pool first.</b> A page of tracks is looked up in the shared pool before any file is
/// opened. A record somebody has already heard is stored without a decode. A record this machine hears is
/// published, so nobody else has to.</para>
///
/// <para><b>Slowly, like the loudness pass.</b> One track at a time on every machine, a governor slot per
/// track, and a pause between tracks. The processor lane would admit a second track on a large machine, but
/// a whole library takes hours the first time, and the listener is using the computer meanwhile.</para>
///
/// <para>A record is heard as three ten-second clips (see <see cref="SoundClips"/>), each decoded on its
/// own, so a long record costs no more than a short one.</para>
/// </summary>
public sealed partial class SoundService : IDisposable
{
    /// <summary>How many tracks are taken from the database, and asked about in the pool, at a time.</summary>
    private const int BatchSize = 16;

    /// <summary>The pause between tracks, as in the loudness pass. A track is about a second of work on
    /// two cores, and the pause keeps the pass from using the machine all the time.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromMilliseconds(400);

    private readonly LibraryService _library;
    private readonly DjModelService _models;
    private readonly Cloud.RecordingPoolClient _pool;
    private readonly Cloud.CloudSoundModel _cloud;
    private readonly ILogger<SoundService> _log;
    private readonly OnnxSoundModel _local = new();
    private readonly Shell.Sweep _sweep;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>The pass's workers, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    /// <summary>The library whose stale readings were last cleared. Cleared once per opened library and not
    /// on every round: a round starts on every LibraryService.Changed, imports and syncs raise that often,
    /// and the delete holds the library's write gate.</summary>
    private LibraryDatabase? _cleared;

    /// <summary>Raised on a background thread when a batch has been stored. The feeling lists can use it to
    /// know that there is more to rank.</summary>
    public event Action? Progressed;

    public SoundService(
        LibraryService library,
        DjModelService models,
        Cloud.RecordingPoolClient pool,
        Cloud.CloudSoundModel cloud,
        Shell.Governor governor,
        ILogger<SoundService> log)
    {
        _library = library;
        _models = models;
        _pool = pool;
        _cloud = cloud;
        _log = log;
        _sweep = governor.Pass("sound", Shell.Lane.Processor, Shell.Wave.Last, notBefore: Shell.Phase.Ready);

        _library.Changed += () => Shell.Background.Run(() => KickAsync());

        // The library may already be open when this object is built. Waiting only for the event would then
        // mean the pass never starts. See LoudnessService for the same call.
        Shell.Background.Run(() => KickAsync());

        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>How many tracks still wait for a sound reading. Zero when no library is open.</summary>
    public Asked<int> Pending => _library.IsOpen
        ? _library.Read(_library.Db, "how many tracks wait for sound", db => db.CountTracksNeedingSound())
        : Asked.Answer(0);

    /// <summary>Starts the pass, or lets a running one continue. Safe to call as often as needed.</summary>
    public async Task KickAsync()
    {
        if (!_library.IsOpen)
            return;
        // The demo library's rows have no files behind them: every one would be decoded and come back with
        // nothing to hear.
        if (GalleryMode.Active)
            return;

        var db = _library.Db;

        await _gate.WaitAsync();
        try
        {
            if (_worker is { IsCompleted: false })
                return;

            _cts = _life.Linked();
            var ct = _cts.Token;
            _worker = _life.Run(() => RunAsync(db, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops after the track being measured. The next <see cref="KickAsync"/> continues.</summary>
    public void Pause() => _cts?.Cancel();

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            // No database read and no model before the governor allows it, as in the loudness pass.
            ModelVerdict verdict;
            using (var start = await _sweep.Next(ct))
            {
                if (!start.Go)
                    return;
                if (!ReferenceEquals(_cleared, db))
                {
                    db.ForgetSoundOfMissingTracks();
                    _cleared = db;
                }

                // Where the model runs. The verdict is measured once per session. If the beat model test has
                // not run yet, VerdictAsync runs it now, without asking the governor, and it needs about a
                // gigabyte for a second or two. Waiting for it inside this pass's slot keeps that work
                // counted in the processor lane. If the test is already running under its own slot, the
                // lane admitted this slot beside it, and the wait is the rest of that test.
                verdict = await _models.VerdictAsync().WaitAsync(ct);
            }

            // The verdict without its moment: a computer turned away only for free memory at startup hears
            // its own records and waits for room before each one. See SoundModelPlace.
            ISoundModel model = SoundModelPlace.For(verdict) == ModelPlace.Local && _local.IsAvailable ? _local : _cloud;
            if (!model.IsAvailable)
            {
                Said.Line("sound", "this computer does not run the sound model and nobody is signed in; " +
                                   "no sound is measured until somebody signs in.");
                return;
            }
            if (ReferenceEquals(model, _local) && verdict.Block == ModelBlock.TooLittleFreeMemory)
                Said.Line("sound", "the beat model test found too little free memory, which says nothing about this " +
                                   "computer; the sound model runs here and waits for memory before each record.");

            // Tracks this run cannot reach, almost always a row whose file is gone or on a drive that is not
            // plugged in. Without this list the same track would come back in every batch.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int heard = 0, borrowed = 0, nothing = 0;
            var started = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                List<SoundWork> work;
                using (var pick = await _sweep.Next(ct))
                {
                    if (!pick.Go)
                        return;
                    work = db.QueryTracksNeedingSound(BatchSize).Where(w => !skipped.Contains(w.RelativePath)).ToList();
                }

                if (work.Count == 0)
                    break;

                // One request for the whole batch, before any file is opened.
                var known = await _pool.KnownSoundsAsync(db, work, ct);

                foreach (var item in work)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    // Room for the model before the slot is asked for, so a pass that is waiting for memory
                    // does not hold the processor lane meanwhile. A pool hit needs no model.
                    if (ReferenceEquals(model, _local)
                        && !known.ContainsKey(item.RelativePath)
                        && !await RoomAsync(ct))
                        return;

                    // A slot per track, a pool hit included: the write takes the library's gate, and the
                    // governor decides when the interface can spare it.
                    using var slot = await _sweep.Next(ct);
                    if (!slot.Go)
                        return;

                    if (known.TryGetValue(item.RelativePath, out var fromPool))
                    {
                        db.SaveTrackSound(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, fromPool);
                        borrowed++;
                        continue;
                    }

                    switch (MeasureOne(db, model, item, ct))
                    {
                        case MeasureStep.Stored:
                            heard++;
                            break;
                        case MeasureStep.StoredNothing:
                            nothing++;
                            break;
                        case MeasureStep.Unreachable:
                            skipped.Add(item.RelativePath);
                            break;
                        case MeasureStep.ModelDidNotAnswer:
                            // Nothing is written: a model that did not answer says nothing about the record.
                            // The pass stops for now, and the next kick (a library change or the next launch)
                            // asks again.
                            Said.Line("sound", $"the sound model did not answer; pass stopped after {heard} " +
                                               $"heard, {borrowed} from the pool.");
                            return;
                    }

                    if (!await Cancellable.DelayAsync(Breath, ct))
                        return;
                }

                Progressed?.Invoke();
            }

            Said.Line("sound",
                $"pass done: {heard} heard, {borrowed} from the pool, {nothing} with nothing to hear, " +
                $"{skipped.Count} not reachable, {started.Elapsed.TotalSeconds:F0}s, " +
                $"on {(ReferenceEquals(model, _local) ? "this computer" : "the cloud")}");
        }
        catch (OperationCanceledException)
        {
            // Paused, or the app is closing. Every track already measured is stored.
        }
        catch (ObjectDisposedException)
        {
            // The library was closed while the pass ran. The next kick continues with the next library.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[sound] Pass stopped unexpectedly");
        }
        finally
        {
            // The pass reads a library once and then only new records, so the session would sit idle holding
            // its memory. It is opened again by the next track.
            _local.Release();
        }
    }

    /// <summary>How often a pass that is waiting for memory looks again.</summary>
    private static readonly TimeSpan RoomPoll = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits until this computer has room to hear a record (<see cref="SoundModelPlace.HasRoom"/>).
    /// False when the pass was stopped meanwhile.
    ///
    /// <para>The model's session is closed while the pass waits: it holds about 140 MB of weights that
    /// the rest of the computer can use, and opening it again takes a second.</para>
    /// </summary>
    private async Task<bool> RoomAsync(CancellationToken ct)
    {
        if (SoundModelPlace.HasRoom(FreeMemory(refresh: false)))
            return true;

        bool told = false;
        while (true)
        {
            // Asked again with a fresh reading. The collector updates its figures only when it collects, and
            // an idle app may not collect for minutes: a stale reading would keep the pass waiting after the
            // memory came back, or let it start on a reading from before a browser filled the machine.
            var free = FreeMemory(refresh: true);
            if (SoundModelPlace.HasRoom(free))
            {
                if (told)
                    Said.Line("sound", $"memory is free again ({free / (1024 * 1024)} MB); hearing goes on.");
                return true;
            }

            if (!told)
            {
                _local.Release();
                Said.Line("sound",
                    $"waiting for memory: {free / (1024 * 1024)} MB free, about " +
                    $"{(SoundModelPlace.ModelBytes + AnalysisThrottle.HeadroomBytes) / (1024 * 1024)} MB needed.");
                told = true;
            }

            if (!await Cancellable.DelayAsync(RoomPoll, ct))
                return false;
        }
    }

    /// <summary>The memory the computer could still hand out, read as <c>TrackAnalysisService</c> reads it,
    /// or zero when the runtime does not say.</summary>
    /// <param name="refresh">Collect the youngest generation first, a few milliseconds, so the figure is
    /// from now and not from the last collection.</param>
    private static long FreeMemory(bool refresh)
    {
        if (refresh)
            GC.Collect(0);
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0
            ? Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes)
            : 0;
    }

    private enum MeasureStep { Stored, StoredNothing, Unreachable, ModelDidNotAnswer }

    /// <summary>Hears one track and stores what it found, or says why nothing was stored.</summary>
    private MeasureStep MeasureOne(LibraryDatabase db, ISoundModel model, SoundWork item, CancellationToken ct)
    {
        var root = _library.Root;
        if (root is null)
            return MeasureStep.Unreachable;

        var path = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return MeasureStep.Unreachable;

        var clock = Stopwatch.StartNew();
        var clips = SoundClips.Starts(item.DurationSeconds)
            .Select(start => PcmDecoder.DecodeMonoClip(path, ClapFeatures.SampleRate, start, SoundClips.Seconds, ct))
            .ToList();
        ct.ThrowIfCancellationRequested();

        var result = SoundReader.Read(model, clips, ct);
        switch (result.Outcome)
        {
            case SoundOutcome.Measured:
                db.SaveTrackSound(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, result.Embedding);
                _pool.PublishSound(db, item.RelativePath, result.Embedding!);
                Said.Line("sound", $"{Path.GetFileName(path)}: heard in {clock.Elapsed.TotalSeconds:F1}s");
                return MeasureStep.Stored;

            case SoundOutcome.NothingToHear:
                // An empty decode is also what a file on a drive that has just gone away gives. Only a file
                // that is still there is written down as having nothing to hear.
                if (!File.Exists(path))
                    return MeasureStep.Unreachable;
                db.SaveTrackSound(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, null);
                Said.Line("sound", $"{Path.GetFileName(path)}: nothing to hear");
                return MeasureStep.StoredNothing;

            default:
                return MeasureStep.ModelDidNotAnswer;
        }
    }

    public void Dispose()
    {
        Pause();
        _cts?.Dispose();
        _gate.Dispose();
        _local.Dispose();
    }
}

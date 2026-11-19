using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Catalog;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Measures the tracks that are only in the cloud, through the store: the level, the arousal numbers, the
/// sound (CLAP) and the fingerprint.
///
/// <para>A track that was uploaded and then deleted from the disk, or that another of the user's computers
/// uploaded, has no file on this computer. The loudness, sound and fingerprint passes all decode the file,
/// so each of them leaves such a track out. The store holds the only copy, so the store measures it, with the
/// desktop's own code: the pass sends the track's rpm id and what it lacks, and gets back the readings in the
/// same request (see <see cref="StoredReading"/>). One download in the store serves all four.</para>
///
/// <para>The pool is asked first for the sound. A record that was heard before its file was deleted, here or
/// on another computer, is borrowed, and the store is not asked to hear it again.</para>
///
/// <para><b>One request at a time, and no slot is held while it waits.</b> The work is done in the store, and
/// this computer only waits for the answer. The governor's slot is taken for the page read and for each
/// write. There is no pause between tracks: a pause protects this computer, which does no work here. The
/// store's daily limit sets the pace.</para>
/// </summary>
public sealed class CloudOnlyMeasureService : IDisposable
{
    /// <summary>How many tracks are read from the database, and asked about in the pool, at a time.</summary>
    private const int BatchSize = 16;

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly RecordingPoolClient _pool;
    private readonly FingerprintService _fingerprints;
    private readonly ILogger<CloudOnlyMeasureService> _log;
    private readonly Sweep _sweep;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>The pass's workers, stopped and waited for before the library is closed.</summary>
    private readonly PassLife _life = new();

    /// <summary>The fingerprint setting as last acted on. <see cref="SettingsService.Changed"/> fires for every
    /// setting, so this tells a real change apart from any other.</summary>
    private bool _fingerprinting;

    /// <summary>The tracks the store said it holds no copy of, for the library they were asked about. They are
    /// not asked about again in this session. A round starts on every library change, and without this each
    /// round would ask about all of them again. Read and written only by the pass's worker, and
    /// <see cref="KickAsync"/> starts one worker at a time.</summary>
    private (LibraryDatabase Db, HashSet<string> Paths)? _noCopy;

    /// <summary>The day on which the store refused because this account's limit for the day was used up. The
    /// store counts its days in UTC. No request is sent again until the next one. Read and written only by the
    /// pass's worker.</summary>
    private DateOnly? _refusedOn;

    /// <summary>Raised on a background thread when a page has been stored.</summary>
    public event Action? Progressed;

    public CloudOnlyMeasureService(
        LibraryService library,
        SettingsService settings,
        AuthService auth,
        RecordingPoolClient pool,
        FingerprintService fingerprints,
        Governor governor,
        ILogger<CloudOnlyMeasureService> log)
    {
        _library = library;
        _settings = settings;
        _auth = auth;
        _pool = pool;
        _fingerprints = fingerprints;
        _log = log;
        _sweep = governor.Pass("cloud-only", Lane.Library, Shell.Wave.Last, notBefore: Phase.Ready);
        _fingerprinting = settings.FingerprintLibrary;

        _library.Changed += () => Background.Run(() => KickAsync());
        // The store answers only a signed-in account, so a sign-in is when the pass becomes possible.
        _auth.Changed += () => Background.Run(() => KickAsync());
        _settings.Changed += OnSettingsChanged;

        // The library may already be open when this object is built. See LoudnessService for the same call.
        Background.Run(() => KickAsync());

        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>Starts the pass, or lets a running one continue. Safe to call as often as needed.</summary>
    public async Task KickAsync()
    {
        if (!_library.IsOpen || !_auth.IsLoggedIn)
            return;
        // The demo library's rows are not the account's tracks.
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

    /// <summary>Stops after the track being asked about. The next <see cref="KickAsync"/> continues.</summary>
    public void Pause() => _cts?.Cancel();

    /// <summary>Turning fingerprinting on makes the fingerprints of these tracks wanted too.</summary>
    private void OnSettingsChanged()
    {
        if (_settings.FingerprintLibrary == _fingerprinting)
            return;
        _fingerprinting = _settings.FingerprintLibrary;
        if (_fingerprinting)
            Background.Run(() => KickAsync());
    }

    private static DateOnly StoreDay() => DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            await MeasureAsync(db, ct);
        }
        catch (OperationCanceledException)
        {
            // Paused, or the app is closing. Every answer already received is stored.
        }
        catch (ObjectDisposedException)
        {
            // The library was closed while the pass ran. The next kick continues with the next library.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[cloud-only] Pass stopped unexpectedly");
        }
    }

    /// <summary>Walks the tracks only in the cloud once. Returns when every one has been asked about, when the
    /// store stops answering, or when the pass is stopped.</summary>
    private async Task MeasureAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (_refusedOn == StoreDay())
            return;

        if (_noCopy is null || !ReferenceEquals(_noCopy.Value.Db, db))
            _noCopy = (db, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var noCopy = _noCopy.Value.Paths;

        int measured = 0, borrowed = 0, fingerprinted = 0, missing = 0;
        var started = Stopwatch.StartNew();
        string Tally() =>
            $"{measured} measured by the store, {borrowed} sounds from the pool, {fingerprinted} fingerprints, " +
            $"{missing} with no copy in the store, {started.Elapsed.TotalSeconds:F0}s";

        try
        {
            // A page starts after the last row of the page before, so a skipped track never stops the walk.
            CloudOnlyWork? after = null;

            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<CloudOnlyWork> page;
                using (var pick = await _sweep.Next(ct))
                {
                    if (!pick.Go)
                        return;
                    page = db.QueryCloudOnlyTracksToMeasure(after, _settings.FingerprintLibrary, BatchSize);
                }

                if (page.Count == 0)
                    break;
                after = page[^1];

                var work = page.Where(w => !noCopy.Contains(w.RelativePath)).ToList();
                if (work.Count == 0)
                    continue;

                // One pool request for the page's sounds. The duration is not used by the lookup.
                var known = await _pool.KnownSoundsAsync(db,
                    [.. work.Where(w => w.NeedsSound).Select(w => new SoundWork(w.RelativePath, w.SizeBytes, w.ModifiedUtcTicks, 0))],
                    ct);

                foreach (var item in work)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    bool askSound = item.NeedsSound;
                    if (askSound && known.TryGetValue(item.RelativePath, out var fromPool))
                    {
                        using (var slot = await _sweep.Next(ct))
                        {
                            if (!slot.Go)
                                return;
                            db.SaveTrackSound(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, fromPool);
                        }
                        borrowed++;
                        askSound = false;
                    }

                    if (!item.NeedsLevel && !item.NeedsMood && !askSound && !item.NeedsFingerprint)
                        continue;

                    var answer = await _pool.MeasureStoredAsync(item, askSound, ct);
                    switch (answer.Outcome)
                    {
                        case StoredAsk.RefusedForToday:
                            _refusedOn = StoreDay();
                            Said.Line("cloud-only", "the store's limit for measuring is used up for today (UTC); " +
                                                    $"asked again tomorrow. So far: {Tally()}.");
                            return;

                        case StoredAsk.NoAnswer:
                            // Offline, signed out meanwhile, a busy store, or a store without this address yet.
                            // Nothing is written, and the next round asks again.
                            Said.Line("cloud-only", $"the store did not answer; stopped after {Tally()}.");
                            return;
                    }

                    var reading = answer.Reading!;
                    if (!reading.HasCopy)
                    {
                        noCopy.Add(item.RelativePath);
                        missing++;
                        continue;
                    }

                    // Every reading that was asked for must be in the answer, in a form this build can store.
                    // Otherwise the answer says nothing about the record: the store could not read it this
                    // time, or it measured with another generation of a model or recipe. The round stops, as
                    // for a store that does not answer, and nothing is written.
                    var level = item.NeedsLevel ? reading.LevelFor() : null;
                    var mood = item.NeedsMood ? reading.MoodFor() : null;
                    SoundResult? sound = askSound ? reading.SoundFor(SoundFact.Generation) : null;
                    var fingerprint = item.NeedsFingerprint ? reading.FingerprintFor(LibraryDatabase.FingerprintVersion) : null;

                    if (!reading.IsUsable
                        || (item.NeedsLevel && level is null)
                        || (item.NeedsMood && mood is null)
                        || sound is { Outcome: SoundOutcome.ModelDidNotAnswer }
                        || (item.NeedsFingerprint && fingerprint is null))
                    {
                        Said.Line("cloud-only",
                            $"the store's answer for {item.RpmId} could not be used ({reading.Error ?? "sound generation " +
                            reading.Generation + ", fingerprint version " + reading.FingerprintVersion}); stopped after {Tally()}.");
                        return;
                    }

                    // Each write is stamped with the row's size and modified time as the page read them. A row
                    // that changed while the store worked is not written, or is written stale and asked about
                    // again (the fingerprint), and comes back in a later round.
                    using (var slot = await _sweep.Next(ct))
                    {
                        if (!slot.Go)
                            return;
                        if (level is { } l)
                            db.SaveTrackLoudness(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, l);
                        if (mood is { } m)
                            db.SaveTrackMood(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, m);
                        if (sound is { } s)
                            db.SaveTrackSound(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, s.Embedding);
                        if (fingerprint is { } f)
                            db.SaveTrackFingerprint(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, f.Seconds, f.Blob);
                    }

                    measured++;
                    if (fingerprint is not null)
                        fingerprinted++;
                }

                Progressed?.Invoke();
            }

            if (measured + borrowed + missing > 0)
                Said.Line("cloud-only", $"done: {Tally()}.");
        }
        finally
        {
            // New fingerprints are offered to the recognition service by the fingerprint pass, which may be
            // idle. It is started so they do not wait for the next library change.
            if (fingerprinted > 0)
                Background.Run(() => _fingerprints.KickAsync());
        }
    }

    public void Dispose()
    {
        Pause();
        _cts?.Dispose();
        _gate.Dispose();
    }
}

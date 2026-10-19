using System.Diagnostics;
using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Measures the library's tracks in the background, so that one track can be played at the same level
/// as the track before it.
///
/// <para>Most tracks never reach this code. A file that already has a ReplayGain tag from another tool
/// is read by the tag reader and arrives already measured. Such tags are written by foobar2000, beets,
/// Picard and rsgain. The remaining tracks are each decoded once. The result is stored in the database,
/// keyed on the file's size and modification time. So a library is measured once, across any number of
/// launches. Only a re-encoded file is measured again.</para>
///
/// <para>The pass runs slowly on purpose. The same is true of the fingerprint pass. It runs on a
/// machine where the user may be listening to music. It decodes one track at a time, waits between
/// tracks, and gives way completely to playback. The track on the deck already has a gain, or does not
/// need one. Each track measured here is never measured again.</para>
/// </summary>
public sealed partial class LoudnessService : IDisposable
{
    /// <summary>How many tracks are taken from the database at a time. The number is small. The loop
    /// asks again for each batch, and a large batch would delay the first track that gets a
    /// level.</summary>
    private const int BatchSize = 16;

    /// <summary>The pause between tracks. A decode is seconds of work, and the pause keeps the pass
    /// from using the machine all the time.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The longest part of a recording that is measured. A gated loudness reading settles within a few
    /// minutes of audio. So a one hour recording is measured using its first ten minutes. The rest is
    /// not decoded, because it would not change the result.
    /// </summary>
    private const double MaxSeconds = 10 * 60;

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly RecordingPoolClient _pool;
    private readonly ILogger<LoudnessService> _log;

    /// <summary>The governor slot that this pass waits for before each track. It runs in the last wave.
    /// Results that arrive late are not noticed by the listener, and every track measured here is never
    /// measured again.
    /// </summary>
    private readonly Shell.Sweep _sweep;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>Raised on a background thread when a batch has been measured. A page that shows the
    /// progress of the pass can use it to refresh.</summary>
    public event Action? Progressed;

    public LoudnessService(
        LibraryService library,
        SettingsService settings,
        RecordingPoolClient pool,
        Shell.Governor governor,
        ILogger<LoudnessService> log)
    {
        _library = library;
        _settings = settings;
        _pool = pool;
        _log = log;
        _sweep = governor.Pass(
            "loudness", Shell.Lane.Processor, Shell.Wave.Last,
            notBefore: Shell.Phase.Ready);

        _library.Changed += () => Shell.Background.Run(() => KickAsync());
        _settings.Changed += OnSettingsChanged;

        // Normally the shell opens the library after this object is built, and the event above starts
        // the pass. This call covers the other order, where the library is already open when this
        // object is built. Waiting for an event that has already happened would mean the pass never
        // starts.
        Shell.Background.Run(() => KickAsync());
    }

    /// <summary>How many tracks are still waiting for a measurement. The value is zero when every track
    /// is measured, and when no library is open. The result is unanswered when the database refuses the
    /// read, which shows the problem screen (<see cref="LibraryService.Read{T}"/>).</summary>
    public Asked<int> Pending => _library.IsOpen
        ? _library.Read(_library.Db, "how many tracks wait for loudness", db => db.CountTracksNeedingLoudness())
        : Asked.Answer(0);

    /// <summary>
    /// Starts the pass, or lets a running one continue. It is safe to call this as often as needed.
    ///
    /// <para>It does nothing while normalisation is switched off. Measuring a whole library takes real
    /// work, and there is no reason to do it when the setting is off. Turning the setting on later
    /// starts the pass. See <see cref="OnSettingsChanged"/>.</para>
    /// </summary>
    public async Task KickAsync()
    {
        // The pass is not gated on volume levelling. One decode answers two questions: how loud a track
        // is, and what it sounds like (see LoudnessScanner.Scan). Only the first question relates to
        // the levelling setting. If the pass stopped with levelling off, a listener who turns off
        // ReplayGain would also lose the mood data, which is a separate feature.
        //
        // The pass still stops when there is nothing to do. If levelling is off and no track needs a
        // mood, the loop below finds no work and ends.
        if (!Wanted)
            return;
        if (!_library.IsOpen)
            return;
        // The demo library's rows have no files behind them: every one would be decoded, fail, and be
        // remembered as unmeasurable.
        if (GalleryMode.Active)
            return;

        var db = _library.Db;

        await _gate.WaitAsync();
        try
        {
            if (_worker is { IsCompleted: false })
                return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _worker = Task.Run(() => RunAsync(db, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops after the track being measured. Tracks not yet measured stay unmeasured in the
    /// database. The next call to <see cref="KickAsync"/> continues with them.</summary>
    public void Pause() => _cts?.Cancel();

    /// <summary>Whether the pass has work to do. Levelling needs a measurement. The mood reading needs
    /// the same decode, whatever the levelling setting is. So this is false only when both are switched
    /// off.</summary>
    private bool Wanted => _settings.NormalizeMode is not NormalizeMode.Off || MoodWanted;

    /// <summary>Whether the mood measurement is taken. It is always taken for now, because it has no
    /// setting. This property exists so that a future setting is checked in one place, not in several
    /// places in the loop.</summary>
    private static bool MoodWanted => true;

    private void OnSettingsChanged()
    {
        if (!Wanted)
            Pause();
        else
            Shell.Background.Run(() => KickAsync());
    }

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            // Tracks this run cannot reach, which is almost always a row whose file is gone. Without
            // this list the same track would come back in every batch and the loop would never end.
            // With it, the track waits for the next kick. By then a sync may have removed the row, or
            // the file may have been restored.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int measured = 0;
            var started = Stopwatch.StartNew();

            // ── no database read happens before the governor allows it ──
            //
            // This pass waits for the governor with `notBefore: Phase.Ready`. Every database read must
            // run after the first `Next` call returns. Reads before it would hold the library gate
            // while the first screen is drawn. The pass only holds back decoding, and the drawing
            // thread does not wait for that.
            //
            // The count below walks the whole table, and it is needed only for a log line in DEBUG
            // builds. Other queries of this kind also hold the read gate for a long time, so a log line
            // should not trigger one.
            using (var start = await _sweep.Next(ct))
            {
                if (!start.Go)
                    return;

#if DEBUG
                var (needLevel, needMood) = db.CountTracksNeedingMeasurement();
                Said.Line("vollevelling",
                    $"pass starting: {needLevel} to level, {needMood} to describe");
#endif
            }

            while (!ct.IsCancellationRequested && Wanted)
            {
                // The batch query is a unit of work, like a decode, and it takes a governor slot. The
                // slot is held only for the query, and released before the network call below. A slot
                // held across `KnownLevelsAsync` would block the lane for the whole network round trip.
                List<LoudnessWork> work;
                using (var pick = await _sweep.Next(ct))
                {
                    if (!pick.Go)
                        return;

                    work = db.QueryTracksNeedingLoudness(BatchSize)
                        .Where(w => !skipped.Contains(w.RelativePath))
                        .ToList();
                }

                if (work.Count == 0)
                    break;

                // One request for the whole batch, before any file is opened: has anyone already
                // measured these tracks? The key is a hash of the file with its tags removed. The key
                // needs no decode, so a match saves the whole measurement.
                var known = await _pool.KnownLevelsAsync(db, work, ct);

                foreach (var item in work)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    // One track at a time, and only when the interface can afford it. The slot is taken
                    // per track, not per batch, because a batch is sixteen decodes and can take a
                    // minute. If the slot were taken once per batch, the pass would keep working during
                    // a scroll that started before it.
                    using var slot = await _sweep.Next(ct);
                    if (!slot.Go)
                        return;

                    // The pool only answers about levels. A track whose level comes back from the pool
                    // still needs a mood reading. So this skips the decode only when the mood is
                    // already known, which happens when the library is run again after a full pass.
                    if (known.TryGetValue(item.RelativePath, out var borrowed) && !item.NeedsMood)
                    {
                        db.SaveTrackLoudness(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, borrowed);
                        Said.Line("vollevelling",
                            $"{item.RelativePath}: {borrowed.Lufs:F2} LUFS from the pool, not read");
                        measured++;
                        continue;
                    }

                    if (MeasureOne(db, item, ct))
                        measured++;
                    else
                        skipped.Add(item.RelativePath);

                    if (!await Cancellable.DelayAsync(Breath, ct))
                        return;
                }

                Progressed?.Invoke();
            }

#if DEBUG
            // A count of the whole table, taken under the read gate. This is for debug builds only. The
            // reason is given where the pass starts. A release build would run the count just for one
            // log line.
            var left = $", {db.CountTracksNeedingLoudness()} still to do";
#else
            const string left = "";
#endif
            Said.Line("vollevelling",
                $"pass done: {measured} measured, {skipped.Count} skipped{left}, " +
                $"{started.Elapsed.TotalSeconds:F1}s");

        }
        catch (OperationCanceledException)
        {
            // The pass was paused, or the app is closing. The database holds every track already
            // measured.
        }
        catch (ObjectDisposedException)
        {
            // The library was closed while the pass ran, because the window closed or another
            // collection was opened. The pass keeps the database it started with for its whole run. A
            // decode takes seconds of that time. See LibraryDatabase.Dispose. The next kick continues
            // the remaining work.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[vollevelling] Pass stopped unexpectedly");
        }
    }

    /// <summary>Measures one track and stores what it found. Returns false only when the file was not
    /// there to read, the one outcome worth trying again later rather than writing down.</summary>
    private bool MeasureOne(LibraryDatabase db, LoudnessWork item, CancellationToken ct)
    {
        var root = _library.Root;
        if (root is null)
            return false;

        var path = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return false;

        try
        {
            var started = Stopwatch.StartNew();

            // The mood meter is not built for a track that needs only a level, such as an interlude.
            // The mood filters cost a few percent of a whole decode, which is not nothing. This pass
            // covers an entire library, so the cost adds up.
            var scan = item.NeedsMood
                ? LoudnessScanner.Scan(path, MaxSeconds, ct)
                : new Scanned(LoudnessScanner.Measure(path, MaxSeconds, ct), MoodReading.Unknown);
            if (ct.IsCancellationRequested)
                return true;

            var reading = scan.Loudness;

            // Only what was requested is written. A track decoded only for its mood already has a
            // level. Writing this level over it could lose a good reading. If a decode fails, it
            // returns Unknown, which is stored as "cannot be measured" and replaces the good reading.
            // When the decode works, the reading is the same, so nothing is gained by writing it.
            if (item.NeedsLevel)
            {
                // Stored even when nothing was found. "This file cannot be measured" is a result, and
                // storing it stops a corrupt track from being decoded in full on every pass.
                db.SaveTrackLoudness(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, reading);

                // The level is also published to the shared pool, so other users with the same encoding
                // do not decode it. Nothing waits for this call, and nothing fails if it does not reach
                // the pool.
                //
                // It is inside the same check as the write, for a stronger reason. Without the check,
                // the mood backfill would republish the level of every track in the library. That would
                // send a whole collection of requests to the pool, telling it what it already knows.
                _pool.PublishLevel(db, item.RelativePath, reading);
            }

            // The mood reading is the other result of the same decode. It is checked the same way as
            // the level, for the same reason. A track that only needs a level, such as an interlude, is
            // not asked about its mood. So no mood is stored for it. Its mood field stays null, which
            // is correct because it was not measured. Nothing requests it again.
            //
            // The mood is stored even when the meter found nothing. A track that was measured and had
            // nothing to describe must be stored as such. Otherwise it would be decoded again on every
            // pass.
            if (item.NeedsMood)
                db.SaveTrackMood(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, scan.Mood);

            // The gain is logged next to the loudness reading, because the gain is what is actually
            // heard. The two can differ. A loud master that needs -9 dB gets that gain. A quiet track
            // that needs +5 dB may be limited by its own peaks. If only the loudness were logged, it
            // would be unclear why a track is still louder than the one before. Both values go on one
            // line, because one decode produced both.
            var level = !item.NeedsLevel
                ? "levelled already"
                : reading.Known
                    ? $"{reading.Lufs:F2} LUFS, peak {reading.TruePeak:F3} " +
                      $"→ {reading.GainDb():+0.0;-0.0;0.0} dB"
                    : "no level";

            var mood = scan.Mood.Known
                ? $"bright {scan.Mood.Brightness:F2}, {scan.Mood.Onsets:F1}/s, {scan.Mood.Dynamics:F0} dB"
                : "nothing to describe";

            Said.Line("vollevelling",
                $"{Path.GetFileName(path)}: {level}; {mood} " +
                $"in {started.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ObjectDisposedException))
        {
            _log.LogWarning(ex, "[vollevelling] Could not measure {Path}", item.RelativePath);
            try { db.SaveTrackLoudness(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, LoudnessReading.Unknown); }
            catch { return false; }
        }

        return true;
    }

    public void Dispose()
    {
        Pause();
        _cts?.Dispose();
        _gate.Dispose();
    }
}

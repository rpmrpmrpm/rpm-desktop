using RPM.Core.Identify;
using RPM.Core.Library;
using RPM.Fingerprint.Chroma;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Fingerprints the whole library quietly in the background (Chromaprint, through fpcalc). The results are
/// offered to the recognition service, where each one counts as a report. This is the crowd's half of the
/// identification ladder's own database.
///
/// <para>There is no rush, and the pass should never be felt. It runs one or two fpcalc lanes, depending on
/// the machine, each at below-normal CPU priority, with a short pause between files. Progress is stored in
/// the mirror (track_fingerprint, stamped with size and mtime). So a library is fingerprinted once, across
/// however many launches it takes. See the renditions_utc_ticks note on
/// <see cref="CloudSyncService"/>.</para>
/// </summary>
public sealed partial class FingerprintService : IDisposable
{
    /// <summary>Background work on a machine someone is using: at most two fpcalc lanes, and only on a
    /// machine with cores to spare.</summary>
    private static readonly int Lanes = Math.Clamp(Environment.ProcessorCount / 4, 1, 2);

    /// <summary>The pause between files in one lane. fpcalc takes a second or two per track. The pause
    /// stops the pass from using the CPU in constant bursts, and it barely slows the pass down.</summary>
    private const int PaceMilliseconds = 250;

    /// <summary>
    /// Reports sent per POST. Small enough that a flaky connection loses little. Big enough that a large
    /// library does not make a flood of requests.
    ///
    /// <para>The value is set by what the service can answer inside its timeout. Each report costs one
    /// lookup against the whole fingerprint store, and that lookup gets slower as the store grows. At 5,351
    /// songs, a batch of twenty-five took about forty-five seconds on the function. The limit is sixty, and
    /// it cannot be raised, because the edge in front of the function also cuts at sixty. A batch that goes
    /// over the limit is lost whole. Nothing is marked as submitted, so the same rows are offered again on
    /// the next pass, and the library stops making progress.</para>
    ///
    /// <para>Each report costs about the same time, so a smaller batch takes less time to finish.</para>
    /// </summary>
    private const int SubmitBatchSize = 8;

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly IdentificationProvider _identification;
    private readonly ILogger<FingerprintService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What this pass asks before each batch. See Governor. Nothing in the background starts by
    /// itself.</summary>
    private readonly Shell.Sweep _sweep;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    /// <summary>The toggle as last acted on. <see cref="SettingsService.Changed"/> fires for every setting,
    /// so this is how a real change is told apart from someone dragging the cover-size slider.</summary>
    private bool _enabled;

    public FingerprintService(
        LibraryService library,
        SettingsService settings,
        AuthService auth,
        IdentificationProvider identification,
        Shell.Governor governor,
        ILogger<FingerprintService> log)
    {
        _library = library;
        _settings = settings;
        _auth = auth;
        _identification = identification;
        _log = log;

        // The LIBRARY lane is used, not the processor lane. The reason is the reading: fpcalc runs in its
        // own process at low priority, so it costs this process almost nothing. The queries that feed it
        // are whole-table reads through the same gate the window uses. In one measurement,
        // QueryTracksNeedingFingerprint held the read gate for 27.9 seconds, and
        // QueryFingerprintsNeedingSubmission for 48.8 seconds. The interface's playlist tree was queued
        // behind the first. The limit here is the database, not the machine.
        _sweep = governor.Pass(
            "fingerprint", Shell.Lane.Library, Shell.Wave.Last, notBefore: Shell.Phase.Ready);

        _enabled = settings.FingerprintLibrary;
        _settings.Changed += OnSettingsChanged;
        _library.Changed += () => Shell.Background.Run(() => KickAsync());
        // A sign-in makes submission possible; fingerprints made while signed out are waiting.
        _auth.Changed += () => Shell.Background.Run(() => KickAsync());

        // The pass holds the library for as long as a library takes. It is stopped and waited for before the
        // library is closed (see Shell.PassLife).
        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>The pass's workers, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    /// <summary>Starts, or lets run, the background pass. Safe to call as often as needed. A run that is
    /// already under way keeps going.</summary>
    public async Task KickAsync()
    {
        if (!_settings.FingerprintLibrary) return;
        if (!_library.IsOpen) return;
        // The demo library's rows have no files behind them. Fingerprinting would log a failure for each
        // one, and remember them all as unreadable.
        if (GalleryMode.Active) return;

        var db = _library.Db;

        // Naming a track is a request to the service. With no line out, the pass would run fpcalc and have
        // nowhere to send the answers. It is started again when the service is reachable.
        if (!await RPM.Core.Net.NetworkReach.Shared.AllowAsync(CancellationToken.None)) return;

        await _gate.WaitAsync();
        try
        {
            if (_worker is { IsCompleted: false }) return;   // Already running, so there is nothing to do.

            // Refused once the library is about to close; the token is cancelled by that close as well.
            _cts = _life.Linked();
            var ct = _cts.Token;
            _worker = _life.Run(() => RunAsync(db, ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops after the files in flight finish. Anything not yet done stays not done in the mirror.
    /// The next kick picks it up.</summary>
    public void Pause() => _cts?.Cancel();

    private void OnSettingsChanged()
    {
        if (_settings.FingerprintLibrary == _enabled)
            return;

        _enabled = _settings.FingerprintLibrary;
        if (_enabled)
            Shell.Background.Run(() => KickAsync());
        else
            Pause();
    }

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            using var slots = new SemaphoreSlim(Lanes, Lanes);

            // Files that could not be handled in this run (usually because the row outlived its file) are
            // skipped. Without this, the same missing file would come back in every batch and the loop
            // would never end. With it, such a file waits for the next kick. By then a sync may have
            // removed its row, or a restore may have brought its bytes back.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!ct.IsCancellationRequested && _settings.FingerprintLibrary)
            {
                // The gate is asked before the query, not after. For the window, the query is the expensive
                // part of this pass. Admitting the batch and then asking would be too late.
                using (var slot = await _sweep.Next(ct))
                {
                    if (!slot.Go)
                        return;
                }

                var work = db.QueryTracksNeedingFingerprint(Lanes * 8)
                    .Where(w => !skipped.Contains(w.RelativePath))
                    .ToList();
                if (work.Count == 0)
                    break;

                var running = work.Select(async item =>
                {
                    if (!await Cancellable.WaitAsync(slots, ct))
                        return;
                    try
                    {
                        if (!FingerprintOne(db, item))
                            lock (skipped)
                                skipped.Add(item.RelativePath);
                        await Cancellable.DelayAsync(PaceMilliseconds, ct);
                    }
                    finally
                    {
                        slots.Release();
                    }
                });
                await Task.WhenAll(running);
            }

            await SubmitPendingAsync(db, ct);
        }
        catch (OperationCanceledException)
        {
            // Paused, because the toggle was turned off or the app is closing. The mirror keeps the
            // progress.
        }
        catch (ObjectDisposedException)
        {
            // The library closed under the pass. This is not a fault. The window closed, or another
            // collection was opened. A pass holds the database it was started with for its whole run. A
            // submission can take a minute of that time (see LibraryDatabase.Dispose). Everything already
            // written stays written, and the next kick picks up the rest.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[fingerprint] Pass stopped unexpectedly");
        }
    }

    /// <summary>Fingerprints one track and stores the result. Returns false when the file was not there to
    /// read. That is the one outcome worth retrying later, rather than remembering.</summary>
    private bool FingerprintOne(LibraryDatabase db, FingerprintWork item)
    {
        var root = _library.Root;
        if (root is null)
            return false;

        var path = Path.Combine(root, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return false;

        try
        {
            var result = FpcalcRunner.Run(path, lowPriority: true);
            var blob = new ChromaFingerprint(result.SubFingerprints).Serialize();
            db.SaveTrackFingerprint(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks,
                result.DurationSeconds, blob);
        }
        catch (FpcalcException ex) when (ex.IsVerdictOnFile)
        {
            // "fpcalc read this file and found no fingerprint in it" is a finding. It is stored as empty,
            // so the file is not retried on every pass. The size and mtime stamp ages it out if the file
            // changes.
            _log.LogWarning(ex, "[fingerprint] Failed for {Path}", item.RelativePath);
            try { db.SaveTrackFingerprint(item.RelativePath, item.SizeBytes, item.ModifiedUtcTicks, 0, []); }
            catch { return false; }
        }
        catch (ObjectDisposedException)
        {
            // The library closed while this file was being read. This says nothing about the file. So it is
            // not logged against the file, and not remembered as unreadable. The pass stops at this file
            // instead.
            throw;
        }
        catch (Exception ex)
        {
            // Every other failure is not about this file, so it must not be written down as if it were.
            // Examples are no fpcalc on the machine, arguments it does not accept, or a drive that
            // disappeared during the pass. Recording those as "this track cannot be fingerprinted" would
            // condemn a whole library because of one broken install, and nothing would ever ask again. So
            // nothing is stored. The file waits for the next kick, and the failure is logged at a visible
            // level.
            _log.LogError(ex, "[fingerprint] Could not run for {Path}", item.RelativePath);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Fingerprints one track now, and gets it a name in the recognition database, ahead of the background
    /// pass.
    ///
    /// <para>The background pass works through a library over however many launches it takes. That is the
    /// right pace for something nobody is waiting on. Here somebody is waiting: a set has started, and this
    /// record is about to be analysed. The fingerprint's name is the only key under which another
    /// computer's reading of the same CD can be found. Without it, this machine spends about ten seconds of
    /// analysis on a record that has already been read. With it, the answer arrives for the cost of one
    /// fpcalc run and one request.</para>
    ///
    /// <para>Returns zero when there is nothing to be had: no fpcalc, signed out, a file that will not
    /// read, or a service that does not answer. Each of these is ordinary, and none is an error. The caller
    /// then reads the record itself, as it would without this method.</para>
    /// </summary>
    public async Task<long> NameNowAsync(string relativePath, CancellationToken ct = default)
    {
        if (!_settings.FingerprintLibrary)
            return 0;                       // The listener turned this off. It is not turned back on here.
        if (!_library.IsOpen || _library.Root is not { Length: > 0 } root)
            return 0;
        if (!_auth.IsLoggedIn || _identification.Backend is not LambdaIdentificationBackend lambda)
            return 0;

        var db = _library.Db;

        // Already named, by the background pass or by an earlier set.
        try
        {
            if (db.RecordingKeysFor(relativePath).SongId is > 0 and var already)
                return already;
        }
        catch { return 0; }

        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            return 0;

        try
        {
            // Fingerprint it now if none exists yet. fpcalc takes a second or two, which is far less than
            // the ten or twenty seconds this is trying to save.
            if (db.FingerprintReportFor(relativePath) is null)
            {
                var file = new FileInfo(path);
                try
                {
                    var made = await Task.Run(() => FpcalcRunner.Run(path, lowPriority: true));
                    db.SaveTrackFingerprint(relativePath, file.Length, file.LastWriteTimeUtc.Ticks,
                        made.DurationSeconds, new ChromaFingerprint(made.SubFingerprints).Serialize());
                }
                catch (FpcalcException fx) when (fx.IsVerdictOnFile)
                {
                    // This is the same finding the background pass writes down. It is written here for the
                    // same reason: without it, every set that reaches this record pays for another fpcalc
                    // run to be told the same thing again.
                    _log.LogWarning(fx, "[fingerprint] Nothing to be had from {Path}", relativePath);
                    try
                    {
                        db.SaveTrackFingerprint(relativePath, file.Length, file.LastWriteTimeUtc.Ticks, 0, []);
                    }
                    catch { /* the mirror can refuse; the set is not held up over it */ }
                    return 0;
                }
            }

            // Still nothing to report. This is a file fpcalc will not read, or a track with no title and
            // artist. The service files recordings by name as well as by sound.
            if (db.FingerprintReportFor(relativePath) is not { } report)
                return 0;

            var ids = await lambda.SubmitFingerprintsAsync(
                [new LambdaIdentificationBackend.LibraryFingerprintReport(
                    report.Title, report.Artist, report.Album, report.Year, report.DurationSeconds,
                    ChromaFingerprint.Deserialize(report.Fingerprint).SubFingerprints)],
                ct);

            long songId = ids.Count > 0 ? ids[0] : 0;

            db.MarkFingerprintSubmitted(relativePath, DateTime.UtcNow.Ticks, songId);
            return songId;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (FpcalcException fx)
        {
            // This is not a verdict on this record, so nothing is remembered against it. It is also not the
            // quiet kind of miss that the rest of this method absorbs. An fpcalc that cannot run fails the
            // same way for every record in the collection. So it is reported at a level that somebody
            // reads, rather than hidden among the ordinary misses.
            _log.LogWarning(fx, "[fingerprint] fpcalc could not run for {Path}", relativePath);
            return 0;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[fingerprint] Could not name {Path} in time for the set", relativePath);
            return 0;
        }
    }

    /// <summary>Offers everything that is fingerprinted but not yet submitted to the service, one batch at
    /// a time. Does nothing when submission is not possible right now (signed out, or the backend is not
    /// the rpm service). The rows keep waiting, and a sign-in starts the pass again.</summary>
    private async Task SubmitPendingAsync(LibraryDatabase db, CancellationToken ct)
    {
        if (_identification.Backend is not LambdaIdentificationBackend lambda)
            return;
        if (!_auth.IsLoggedIn)
            return;

        while (!ct.IsCancellationRequested && _settings.FingerprintLibrary)
        {
            var batch = db.QueryFingerprintsNeedingSubmission(SubmitBatchSize);
            if (batch.Count == 0)
                break;

            var reports = batch
                .Select(r => new LambdaIdentificationBackend.LibraryFingerprintReport(
                    r.Title, r.Artist, r.Album, r.Year, r.DurationSeconds,
                    ChromaFingerprint.Deserialize(r.Fingerprint).SubFingerprints))
                .ToList();

            IReadOnlyList<long> songIds;
            try
            {
                songIds = await lambda.SubmitFingerprintsAsync(reports, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is LambdaIdentificationBackend.IdentificationUnavailableException
                    || RPM.Core.Net.TransientHttp.LooksLikeTheLine(ex))
            {
                // The service was unreachable, or briefly unwell. The client has already tried again twice
                // when this is thrown. Nothing was marked, so the whole batch is offered on the next pass.
                // That happens often enough that a warning with a stack trace would fill the log with
                // noise. It is not a fault in the library, and nobody needs to act on it.
                _log.LogDebug("[fingerprint] Submission deferred to a later pass: {Reason}", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Either the server decided something, or there is a bug on this side. Nothing was marked,
                // so the whole batch is offered again on the next pass.
                _log.LogWarning(ex, "[fingerprint] Submission failed; will retry on a later pass");
                break;
            }

            var now = DateTime.UtcNow.Ticks;
            for (int i = 0; i < batch.Count; i++)
            {
                // The name the service gives this recording, kept on the row. Two independent rips of one
                // CD can share it, so it is the only way a second computer can find what the first one
                // already worked out about the record. See RecordingKey.Song. Zero means "it did not say",
                // and whatever is already stored is left as it is.
                long songId = i < songIds.Count ? songIds[i] : 0;
                db.MarkFingerprintSubmitted(batch[i].RelativePath, now, songId);
            }
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _cts?.Cancel();
    }
}

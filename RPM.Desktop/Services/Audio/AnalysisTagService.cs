using System.Diagnostics;
using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>How far a backfill has got: tracks looked at, and files actually written.</summary>
public readonly record struct AnalysisTagProgress(int Done, int Total, int Written);

/// <summary>
/// Puts what the analyser measured into the files it measured.
///
/// <para>The analyser measures the tempo and key of every track the DJ plays, because beat matching
/// needs them. The results are kept in a cache keyed on the file, not written into the file. Other
/// programs (the app's own metadata table, file browsers, DJ software) then see those tracks as
/// untagged. This class writes the results into the files as tags.</para>
///
/// <para>There are two ways in, and one rule. Each track is offered for tagging as soon as its analysis
/// finishes. The whole library can also be offered from the settings page. Both paths fill blank tags
/// only, unless the backfill's overwrite option is set. A value already in a file was put there on
/// purpose, so a measurement does not replace it.</para>
///
/// <para>Writing a tag changes the file's size and modification time. The analysis cache and the
/// library sync both use those values as keys. This class stores the new values right after each
/// write, in that order. Without this, the analysis would be discarded and the next sync would read
/// the whole collection again.</para>
/// </summary>
public sealed partial class AnalysisTagService : IDisposable
{
    /// <summary>How many tracks the backfill takes from the mirror at a time. The number is small. The
    /// loop asks the database again for each batch, so a track analysed while the backfill runs is
    /// picked up as well.</summary>
    private const int BatchSize = 32;

    /// <summary>The pause between files. On some formats a tag write rewrites the whole file, so this
    /// gives the disk, and anything playing from it, time to keep up.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromMilliseconds(120);

    /// <summary>How long a newly analysed track waits for its turn to write. If another write is still
    /// running after this, the track is left for the backfill. The wait is long enough for one file. It
    /// is short enough that a whole collection does not hold a task open.</summary>
    private static readonly TimeSpan WaitForTurn = TimeSpan.FromSeconds(30);

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly TrackAnalysisService _analysis;
    private readonly ILogger<AnalysisTagService> _log;

    /// <summary>Only one writer runs at a time, whichever way in was used. Two writers on the same
    /// files would clash, and one of them would fail with a file sharing error.</summary>
    private readonly SemaphoreSlim _writing = new(1, 1);

    private CancellationTokenSource? _backfill;

    /// <summary>Raised on a background thread after a file is tagged. A page that shows the progress of
    /// the pass can use it to refresh.</summary>
    public event Action? Progressed;

    public AnalysisTagService(
        LibraryService library, SettingsService settings, TrackAnalysisService analysis,
        ILogger<AnalysisTagService> log)
    {
        _library = library;
        _settings = settings;
        _analysis = analysis;
        _log = log;

        _analysis.Analyzed += OnAnalyzed;
    }

    /// <summary>How many analysed tracks still have measurements that are not in their files. The
    /// result is zero when the library is fully tagged, when nothing has been analysed yet, and when no
    /// library is open. The result is unanswered when the database refuses the read, which shows the
    /// problem screen (<see cref="LibraryService.Read{T}"/>).</summary>
    public Asked<int> Pending(bool overwrite)
    {
        if (!_library.IsOpen)
            return Asked.Answer(0);

        return _library.Read(_library.Db, "how many tracks wait for their tags",
            db => db.CountTracksNeedingAnalysisTags(overwrite));
    }

    /// <summary>True while a backfill is running. The settings page uses this to offer a stop button
    /// instead of a second start.</summary>
    public bool IsBackfilling => _backfill is { IsCancellationRequested: false };

    // ── as tracks are analysed ────────────────────────────────────────────────

    /// <summary>
    /// A track has just been analysed: write its measurements to the file now.
    ///
    /// <para>The write runs on a worker task and nobody waits for it. This handler runs on the thread
    /// that finished the analysis, which is often a sweep thread. A file write must not block that
    /// thread. It also must not run before the analysis itself is stored.</para>
    /// </summary>
    private void OnAnalyzed(TrackAnalysis analysis)
    {
        if (!_settings.WriteAnalysisTags || GalleryMode.Active || !_library.IsOpen)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // The wait has a time limit. A backfill can hold the lock for as long as it takes to
                // walk a collection. Without a limit, each track analysed behind it would keep a
                // thread pool task blocked for that whole time. A track that gives up here is not
                // lost. Its row still says the tags are missing, so the running backfill or the next
                // one writes them.
                if (!await _writing.WaitAsync(WaitForTurn).ConfigureAwait(false))
                    return;

                try { WriteOne(analysis.FilePath, overwrite: false); }
                finally { _writing.Release(); }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[analysis-tags] Could not tag {Path} after analysis", analysis.FilePath);
            }
        });
    }

    // ── the whole library, on demand ──────────────────────────────────────────

    /// <summary>
    /// Writes the measurements of every analysed track that does not have them in its file yet.
    ///
    /// <para>This is the manual pass. It is used for libraries analysed before tagging existed, and for
    /// any library played through the DJ. It is also the retry: a track whose file was open, missing,
    /// or read-only during analysis stays on the list.</para>
    /// </summary>
    /// <param name="overwrite">Replace the values already in the files with the measured values. When
    /// false, which is the normal case, only blank tags are filled.</param>
    /// <returns>How many files were written.</returns>
    public async Task<int> BackfillAsync(
        bool overwrite = false, IProgress<AnalysisTagProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_library.IsOpen || GalleryMode.Active)
            return 0;

        var fresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var previous = Interlocked.Exchange(ref _backfill, fresh);
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
            previous.Dispose();
        }

        var token = fresh.Token;
        var db = _library.Db;
        int written = 0, done = 0;

        if (!await Cancellable.WaitAsync(_writing, token).ConfigureAwait(false))
            return 0;

        try
        {
            int total = db.CountTracksNeedingAnalysisTags(overwrite);
            var started = Stopwatch.StartNew();
            _log.LogInformation("[analysis-tags] backfill starting: {Total} tracks, overwrite={Overwrite}",
                total, overwrite);

            // Tracks this run cannot reach, such as a row whose file is gone or a file the player has
            // open. Each batch is read from the database again, so without this list the same track
            // would come back every time and the loop would never end.
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    var batch = db.TracksNeedingAnalysisTags(BatchSize + skipped.Count, overwrite)
                        .Where(w => !skipped.Contains(w.RelativePath))
                        .Take(BatchSize)
                        .ToList();
                    if (batch.Count == 0)
                        break;

                    foreach (var item in batch)
                    {
                        if (token.IsCancellationRequested)
                            return;

                        var path = Path.Combine(
                            _library.Root ?? "", item.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                        switch (WriteOne(path, overwrite))
                        {
                            case TagOutcome.Written: written++; break;
                            case TagOutcome.Skipped: skipped.Add(item.RelativePath); break;
                        }

                        done++;
                        progress?.Report(new AnalysisTagProgress(done, total, written));
                        await Cancellable.DelayAsync(Breath, token).ConfigureAwait(false);
                    }

                    Progressed?.Invoke();
                }
            }).ConfigureAwait(false);

            _log.LogInformation(
                "[analysis-tags] backfill done: {Written} written, {Looked} looked at, {Skipped} left for later, {Seconds:F1}s",
                written, done, skipped.Count, started.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("[analysis-tags] backfill stopped: {Written} written of {Done} looked at",
                written, done);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[analysis-tags] backfill stopped unexpectedly");
        }
        finally
        {
            _writing.Release();
            if (Interlocked.CompareExchange(ref _backfill, null, fresh) == fresh)
                fresh.Dispose();
            Progressed?.Invoke();
        }

        return written;
    }

    /// <summary>Stops a running backfill after the file being written. Tracks not yet written stay on
    /// the list, so the next run picks them up.</summary>
    public void StopBackfill()
    {
        try { _backfill?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ── one file ──────────────────────────────────────────────────────────────

    /// <summary>What happened to one track.</summary>
    private enum TagOutcome
    {
        /// <summary>The file now carries what was measured.</summary>
        Written,

        /// <summary>Nothing needed writing, and the track is not retried. The file already had the
        /// values, or the analyser had no value it was sure of. The row is marked done.</summary>
        Settled,

        /// <summary>Could not be written now, because the file is missing, held by the player, or
        /// refused the write. The track stays on the list for the next run.</summary>
        Skipped,
    }

    private TagOutcome WriteOne(string absolutePath, bool overwrite)
    {
        if (_library.Root is not { } root || !File.Exists(absolutePath))
            return TagOutcome.Skipped;

        var relative = Path.GetRelativePath(root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return TagOutcome.Skipped;   // outside the library, so there is no row to mark

        // The track on the deck is the one most likely to have just been analysed, and it is open.
        // It is skipped, not forced: the backfill and the next launch will write it.
        if (App.Services.GetService<PlaybackService>() is { } playback && playback.HoldsOpen(absolutePath))
            return TagOutcome.Skipped;

        var db = _library.Db;
        var file = new FileInfo(absolutePath);

        // Read the row back through the cache's own check instead of trusting the caller. The row must
        // still match this file, or the timestamps stored below would be wrong. A row that does not
        // match is analysed again anyway.
        if (db.GetTrackAnalysis(relative, file.Length, file.LastWriteTimeUtc.Ticks, absolutePath)
            is not { } analysis)
            return TagOutcome.Skipped;

        AnalysisTagWrite written;
        try
        {
            written = TagReader.WriteAnalysisTags(absolutePath, analysis, overwrite);
        }
        catch (Exception ex)
        {
            // A locked or read-only file is not marked done. The track is retried later.
            _log.LogWarning(ex, "[analysis-tags] {Path} refused the write", relative);
            return TagOutcome.Skipped;
        }

        // The stored timestamps are always updated, in this order. This includes files that were not
        // changed. Without that, a track that already has both values would be opened on every pass.
        file.Refresh();
        db.NoteAnalysisTagsWritten(relative, file.Length, file.LastWriteTimeUtc.Ticks);
        if (written.Any)
        {
            // The mirror does not store the tempo or key, so its data is still correct. Its row is
            // keyed on the size and modification time that this write changed. Without the new stamp,
            // sync would read the whole collection again to find that nothing had changed.
            db.NoteFileStamp(relative, file.Length, file.LastWriteTimeUtc.Ticks);
            Said.Line("analysis-tags",
                $"{Path.GetFileName(absolutePath)}: " +
                $"{(written.Bpm > 0 ? $"{written.Bpm} BPM" : "")}" +
                $"{(written.Bpm > 0 && written.Key is { Length: > 0 } ? ", " : "")}" +
                $"{written.Key}");
        }

        return written.Any ? TagOutcome.Written : TagOutcome.Settled;
    }

    public void Dispose()
    {
        _analysis.Analyzed -= OnAnalyzed;
        StopBackfill();
        _writing.Dispose();
    }
}

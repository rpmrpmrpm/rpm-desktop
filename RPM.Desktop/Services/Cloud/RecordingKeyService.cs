using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Services.Audio;
using RPM.Fingerprint;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Cloud;

/// <summary>
/// Joins several names that belong to one record.
///
/// <para>
/// A machine that holds a file is the only thing that can tell that the hash of its bytes, the hash of
/// its decoded audio, and the id in its tags are three names for one piece of music. Every other part of
/// the system sees just one of them. The blob store sees a hash, the catalogue sees an id, and the
/// analyser sees a decode. The join is made here, once. After that it is shared, so the next machine that
/// meets the same encoding can use what is already known about the record, instead of working it out again.
/// </para>
///
/// <para>It runs alongside the analyser, because the sample hash exists at the moment a track is analysed.
/// Each track is analysed once per machine and answered from the cache after that, so this work is a
/// trickle, not a sweep. It is also optional. When the user is signed out, offline, or the server refuses,
/// the bundles are dropped. Nothing waits on this, and playing music does not depend on it.</para>
/// </summary>
public sealed partial class RecordingKeyService : IDisposable
{
    private readonly LibraryService _library;
    private readonly TrackAnalysisService _analysis;
    private readonly RpmCloudClient _cloud;
    private readonly AuthService _auth;
    private readonly ILogger<RecordingKeyService> _log;

    /// <summary>The most names that the endpoint accepts in one request.</summary>
    private const int BatchSize = 50;

    /// <summary>How long a bundle waits for others to join it. Analyses finish one at a time over several
    /// minutes. Without this wait, each track would need its own request.</summary>
    private static readonly TimeSpan Gather = TimeSpan.FromSeconds(20);

    /// <summary>The pause between one batch and the next. The one-off pass can run for a whole library, and
    /// it does not need to finish quickly.</summary>
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(2);

    /// <summary>Bundles waiting to be sent. The queue is limited by how many tracks one run can
    /// analyse.</summary>
    private readonly ConcurrentQueue<IReadOnlyList<string>> _pending = new();

    /// <summary>Paths already published in this run, so each is sent at most once. The store counts people,
    /// not requests, so a repeat sends nothing new but still costs a request.</summary>
    private readonly HashSet<string> _published = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _draining;
    private readonly object _gate = new();

    public RecordingKeyService(
        LibraryService library,
        TrackAnalysisService analysis,
        RpmCloudClient cloud,
        AuthService auth,
        ILogger<RecordingKeyService> log)
    {
        _library = library;
        _analysis = analysis;
        _cloud = cloud;
        _auth = auth;
        _log = log;

        _analysis.Analyzed += OnAnalyzed;
        _library.Changed += OnLibraryChanged;
        _auth.Changed += OnAuthChanged;

        // The backfill pages through the library. It is stopped and waited for before the library is closed
        // (see Shell.PassLife). The drain loop sends over the network and does not touch the library.
        _library.StopBeforeClose(_life.CloseAsync);

        // The library may already be open, and the user may already be signed in, when this object is
        // built.
        Consider();
    }

    /// <summary>The backfill's worker, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    // ── the one-off pass ──────────────────────────────────────────────────────

    /// <summary>The version of the backfill. Raising it sends every library again. Do this when the bundle
    /// gains a new name that is worth publishing. Without it, the rows of an older library would never be
    /// visited again, because everything else here is triggered by events that happen only once.
    /// </summary>
    private const int BackfillGeneration = 1;

    /// <summary>Rows read from the mirror at a time. The pass is small and low priority, and it runs behind
    /// whatever the user is doing.</summary>
    private const int PageSize = 200;

    private void OnLibraryChanged() => Consider();

    private void OnAuthChanged() => Consider();

    /// <summary>Starts the one-off pass if this library has not had it yet and a user is signed in. Cheap
    /// to call, and safe to call often. Both conditions are checked again inside the pass.</summary>
    private void Consider()
    {
        if (!_auth.IsLoggedIn || !_library.IsOpen)
            return;

        lock (_gate)
        {
            if (_backfilling is { IsCompleted: false })
                return;

            // Refused once the library is about to close; the pass stops for that close or for Dispose.
            var stop = _life.Linked(_stopping.Token);
            _backfilling = _life.Run(async () =>
            {
                using (stop)
                    await BackfillAsync(stop.Token).ConfigureAwait(false);
            });
            if (_backfilling is null)
                stop.Dispose();
        }
    }

    private Task? _backfilling;

    /// <summary>
    /// Publishes what this machine already knows, once per library.
    ///
    /// <para>Everything else here depends on an analysis, and an analysis happens once per track per
    /// machine. A collection that was analysed before this feature existed would therefore never publish
    /// anything. This pass can publish a lot without reading a single file. Every uploaded track already
    /// has the hash of its own bytes, with the tags removed, and the id in its tags. Those are two names
    /// for one record, which is the join that the pool lacks.</para>
    /// </summary>
    private async Task BackfillAsync(CancellationToken ct)
    {
        LibraryDatabase db;
        try
        {
            if (!_library.IsOpen)
                return;
            db = _library.Db;
            if (db.GetMetaVersion(LibraryDatabase.RecordingKeysPublishedKey) >= BackfillGeneration)
                return;
        }
        catch { return; }

        _log.LogInformation("[pool] Publishing what this library already knows about which names are one record");

        string? after = null;
        int sent = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_auth.IsLoggedIn)
                    return;

                IReadOnlyList<(string RelativePath, string? RpmId, string? CanonicalSha, string? SampleHash, long SongId)> page;
                try { page = db.TracksWithSeveralNames(after, PageSize); }
                catch { return; }

                if (page.Count == 0)
                    break;

                after = page[^1].RelativePath;

                foreach (var row in page)
                {
                    if (Bundle(row.RpmId, row.CanonicalSha, row.SampleHash, row.SongId) is { } bundle)
                    {
                        _pending.Enqueue(bundle);
                        // Marks the track as sent, so the analysis path does not offer it again in this
                        // run.
                        lock (_gate) _published.Add(row.RelativePath);
                        sent++;
                    }
                }

                await DrainOnceAsync(ct).ConfigureAwait(false);
            }

            // Marked only when the pass reaches the end. A pass that is cut short, by a sign-out or by
            // closing the app, starts again from the beginning next time. The store counts people, not
            // requests, so repeating the pass costs only extra requests.
            db.SetMeta(LibraryDatabase.RecordingKeysPublishedKey, BackfillGeneration.ToString());
            _log.LogInformation("[pool] Published {Count} key bundles this library already had", sent);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[pool] The one-off key pass stopped early; it will run again");
        }
    }

    /// <summary>
    /// The keys for one track, cheapest first, or null when fewer than two of them are really there.
    /// One name is not a bundle: nothing was observed together, so nothing was learnt.
    ///
    /// <para>The song id is the fourth, and it is the one that is shared between machines. The other three
    /// name this copy: its bytes, its samples, and the id in its tags. Two people who ripped the same CD
    /// share none of them, so a second machine may analyse a record that another machine has already read.
    /// The song id is the name they do share. Putting it beside the others is what joins the two halves of
    /// the pool.</para>
    /// </summary>
    private static IReadOnlyList<string>? Bundle(
        string? rpmId, string? canonicalSha, string? sampleHash, long songId)
    {
        var bundle = new List<string>(4);
        if (RecordingKey.Canonical(canonicalSha) is { } canon) bundle.Add(canon.ToString());
        if (RecordingKey.Samples(sampleHash) is { } sample) bundle.Add(sample.ToString());
        if (RecordingKey.Rpm(rpmId) is { } id) bundle.Add(id.ToString());
        if (RecordingKey.SongRow(songId) is { } song) bundle.Add(song.ToString());
        return bundle.Count >= 2 ? bundle : null;
    }

    /// <summary>A track has just been analysed. The names this machine has for it are collected now, while
    /// the sample hash from the decode is still available.</summary>
    private void OnAnalyzed(TrackAnalysis analysis)
    {
        if (!_auth.IsLoggedIn || !_library.IsOpen)
            return;

        var relative = Relative(analysis.FilePath);
        if (relative is null)
            return;

        lock (_gate)
        {
            if (!_published.Add(relative))
                return;
        }

        (string? rpmId, string? canonical, string? samples, long songId) keys;
        try { keys = _library.Db.RecordingKeysFor(relative); }
        catch { return; }

        // Nothing to publish for a track with only one name. This is usually a track that has never been
        // uploaded, so it has no hash of its own bytes yet.
        if (Bundle(keys.rpmId, keys.canonical, keys.samples, keys.songId) is not { } bundle)
            return;

        _pending.Enqueue(bundle);
        Start();
    }

    /// <summary>Starts the drain loop if it is not already running. Only one loop runs at a time, because
    /// two loops would send the same bundles twice.</summary>
    private void Start()
    {
        lock (_gate)
        {
            if (_draining is { IsCompleted: false })
                return;
            _draining = Task.Run(() => DrainAsync(_stopping.Token));
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            // Wait before the first send as well as between sends. Analyses for one set of records arrive
            // over the next minute, and they should be sent together.
            if (!await Cancellable.DelayAsync(Gather, ct).ConfigureAwait(false))
                return;
            await DrainOnceAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Nothing is thrown out of this fire-and-forget loop. DrainOnceAsync has already logged the
            // failure, and the next analysis starts a new loop.
        }
    }

    /// <summary>
    /// Sends whatever is waiting, a batch at a time, and returns when there is nothing left.
    ///
    /// <para>A failure ends the run. It is not retried. The bundles describe audio and are not the user's
    /// data. The next analysed record starts another batch. Anything dropped here can be derived again by
    /// any machine that meets the same file, including this machine on its next launch, if the one-off pass
    /// never reached its end.</para>
    /// </summary>
    private async Task DrainOnceAsync(CancellationToken ct)
    {
        // The one-off pass and the analysis path fill and drain the same queue. The queue itself stops a
        // bundle from being sent twice. Two requests at once from the low-priority pass would use too much
        // of the user's connection, so requests are sent one at a time.
        if (!await Cancellable.WaitAsync(_sending, ct).ConfigureAwait(false))
            return;
        try
        {
            await SendPendingAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    private readonly SemaphoreSlim _sending = new(1, 1);

    private async Task SendPendingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_pending.IsEmpty)
        {
            if (!_auth.IsLoggedIn)
                return;

            var batch = new List<IReadOnlyList<string>>(BatchSize);
            while (batch.Count < BatchSize && _pending.TryDequeue(out var bundle))
                batch.Add(bundle);

            if (batch.Count == 0)
                return;

            try
            {
                var records = await _cloud.SubmitRecordingKeysAsync(batch, ct).ConfigureAwait(false);
                _log.LogDebug(
                    "[pool] Published {Count} key bundles; {Placed} placed on a record",
                    batch.Count, records.Count(r => r is not null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug(ex, "[pool] Could not publish key bundles");
                throw;
            }

            if (!_pending.IsEmpty && !await Cancellable.DelayAsync(Pace, ct).ConfigureAwait(false))
                return;
        }
    }

    /// <summary>The path as the library stores it, or null for a file outside the root.</summary>
    private string? Relative(string absolutePath)
    {
        var root = _library.Root;
        if (string.IsNullOrEmpty(root))
            return null;

        try
        {
            var full = Path.GetFullPath(absolutePath);
            var rooted = Path.GetFullPath(root);
            if (!full.StartsWith(rooted, StringComparison.OrdinalIgnoreCase))
                return null;

            return Path.GetRelativePath(rooted, full).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _analysis.Analyzed -= OnAnalyzed;
        _library.Changed -= OnLibraryChanged;
        _auth.Changed -= OnAuthChanged;
        _stopping.Cancel();
        _stopping.Dispose();
        _sending.Dispose();
    }
}

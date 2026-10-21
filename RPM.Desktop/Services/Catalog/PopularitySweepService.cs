using RPM.Core.Library;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Asks the world how much it plays each song on the shelf, one record at a time, continuously.
///
/// <para><b>Why it is a pass and not a lookup.</b> The decade lists are ranked by
/// <c>tracks.world_plays</c>. That figure comes from three requests per RECORD, to two services that are
/// free, shared, and quick to refuse a caller who hurries. A collection can have a few thousand records.
/// Asking for them all at once hits a rate limit within a minute. So this walks slowly, and the lists fill
/// in over the following days. It follows the same pattern as the loudness and mood passes, for the same
/// reason.</para>
///
/// <para><b>It never asks the same record twice.</b> Once a record has been asked about, none of its tracks
/// has a null <c>world_plays</c> left (see <c>LibraryDatabase.SaveWorldPlays</c>). This includes songs the
/// catalogue had nothing to say about, and records no catalogue knows. Without this, the albums that cannot
/// be found would be offered again on every run, and the rest of the collection would never be
/// reached.</para>
///
/// <para><b>Nothing is written when nobody was reached.</b> A service that is down, or a rate limit still
/// in force, leaves the record untouched, so the next run asks again. Writing a failure down as "the world
/// does not play this" is the one mistake this pass could make that nothing would correct.
/// <c>PopularTrackAnswer</c> keeps these two cases apart.</para>
/// </summary>
public sealed partial class PopularitySweepService : IDisposable
{
    private readonly PopularityService _catalogue;
    private readonly Services.Library.LibraryService _library;

    private CancellationTokenSource? _stop;
    private Task? _worker;

    public PopularitySweepService(
        PopularityService catalogue,
        Services.Library.LibraryService library,
        Shell.Governor governor)
    {
        _catalogue = catalogue;
        _library = library;
        _sweep = governor.Pass(
            "world plays", Shell.Lane.Network, Shell.Wave.Last, notBefore: Shell.Phase.Ready);
        _library.Opened += _ => Start();

        // The sweep holds the library between its requests. It is stopped and waited for before the library
        // is closed (see Shell.PassLife).
        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>What this pass asks before each unit of work. See Governor. Nothing in the background
    /// starts by itself.</summary>
    private readonly Shell.Sweep _sweep;


    /// <summary>
    /// How long to wait between records.
    ///
    /// <para>Six seconds. That is three requests spread over the wait. MusicBrainz allows no more than one
    /// request a second from a named client, and it often returns 503 when it is busy. ListenBrainz is less
    /// strict, but it is the same kind of shared service. Nothing waits for this pass. The lists it fills
    /// are not on screen while it runs, so there is no reason to hurry, and many reasons not to.</para>
    /// </summary>
    private static readonly TimeSpan Breath = TimeSpan.FromSeconds(6);

    /// <summary>How long to wait after a record that could not be reached. One minute, not six seconds. The
    /// cause is not this record. Hitting a service that is already refusing earns a longer
    /// refusal.</summary>
    private static readonly TimeSpan AfterASilence = TimeSpan.FromMinutes(1);

    /// <summary>How many records are read at a time. This is a page, not the whole list. The shelf can
    /// change during a pass that runs for days. Asking the database again is cheaper than holding a few
    /// thousand rows in memory to walk one an hour.</summary>
    private const int Page = 40;

    /// <summary>
    /// Starts the pass, or leaves the one already running alone.
    ///
    /// <para>Calling it again does nothing. The library raises <c>Opened</c> for every open, and a second
    /// worker would double the request rate. Avoiding that is the main job of this class.</para>
    /// </summary>
    public void Start()
    {
        // Under a lock, so two Opened events cannot both find no worker and start two.
        lock (_starting)
        {
            if (_worker is { IsCompleted: false })
                return;

            _stop?.Dispose();
            _stop = _life.Linked();
            var ct = _stop.Token;

            if (!_library.IsOpen)
                return;

            // Refused once the library is about to close; the token is cancelled by that close as well.
            var db = _library.Db;
            _worker = _life.Run(() => RunAsync(db, ct));
        }
    }

    private readonly object _starting = new();

    /// <summary>The pass's worker, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            // Nothing here touches the database until the governor says yes. The write below and each page
            // query are units of work, and they ask first, like the ones in LoudnessService. They must
            // not run while startup is still loading, because they would take the locks that the first
            // screen reads.
            using (var start = await _sweep.Next(ct))
            {
                if (!start.Go)
                    return;

                try
                {
                    // Records with a second disc that were stamped while the counts were matched by
                    // position alone are asked again, once. See
                    // LibraryDatabase.AskAgainAboutDiscsMatchedByPosition.
                    db.AskAgainAboutDiscsMatchedByPosition();
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    return;   // The library closed during the pass. This is not an error.
                }
            }

            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<RecordToAsk> records;
                using (var page = await _sweep.Next(ct))
                {
                    if (!page.Go)
                        return;

                    try
                    {
                        records = db.NextRecordsWithoutWorldPlays(Page);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                    {
                        return;   // The library closed during the pass. This is not an error.
                    }
                }

                // Every record has been asked about. Nothing is scheduled to ask again. Song play counts
                // change slowly, and a finished shelf should cost the services nothing.
                if (records.Count == 0)
                    return;

                foreach (var record in records)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    using var slot = await _sweep.Next(ct);
                    if (!slot.Go)
                        return;

                    var reached = await AskAsync(db, record, ct);

                    if (!await Cancellable.DelayAsync(reached ? Breath : AfterASilence, ct))
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The library closed, or the app is shutting down. Nothing is left half-written, because each
            // record is one transaction.
        }
    }

    /// <summary>
    /// Asks about one record and writes what came back.
    /// </summary>
    /// <returns>Whether anybody was reached. This decides how long to wait before the next record. It is
    /// not the same as whether anything was found.</returns>
    private async Task<bool> AskAsync(LibraryDatabase db, RecordToAsk record, CancellationToken ct)
    {
        try
        {
            // A linked album gives its own release id, which turns the catalogue's fuzzy search into an
            // exact lookup. Across a whole library this saves one rate-limited second per record. It also
            // means the search cannot confidently return somebody else's pressing.
            var plays = await _catalogue.AlbumPlaysAsync(
                record.Artist, record.Album, record.Tracks, record.ReleaseMbid, ct);

            if (plays is null)
                return false;   // Nobody answered. The record keeps its nulls and is asked again.

            if (plays.Count == 0)
            {
                // Asked, and no catalogue has this record. That is the answer, and it is written down.
                // Otherwise this album would be offered again on every run.
                db.NoteWorldPlaysUnknown(record.Folder);
                return true;
            }

            db.SaveWorldPlays(
                record.Folder,
                plays.ToDictionary(p => p.Key, p => p.Value.Listens));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;   // The library closed in the middle of the record.
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }
}

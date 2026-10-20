using RPM.Core.Analysis;
using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>
/// Filling in what queued records lack from the catalogue, on their own, once music has been added. See
/// <see cref="CatalogueFill"/> for what is filled and when a release is trusted. This is the part that decides
/// when to ask, and puts the answers on the rows.
///
/// <para><b>Started by the user's own action, governed like any other pass.</b> Adding music starts it: a picked
/// folder, a finished rip, the setup wizard, or music dropped into the inbox while the app runs. The inbox
/// sweep that follows the library opening does not, because nothing in the background starts itself off the
/// library opening; the records it queued are picked up by the next add. Each record is one unit of work that
/// asks the <see cref="Services.Shell.Governor"/> first, in the network lane, because a record is one or two
/// requests to the rpm API and nothing else.</para>
///
/// <para><b>Quiet.</b> Signed out, offline, a refusal: the pass stops without a word, and the records it did not
/// reach are asked about after the next add. A record the catalogue answered is not asked about again this
/// session, whether or not a sure release was found, so a second add does not repeat the first one's
/// requests. The only thing the user is told is how many records were filled, on the queue's status line.</para>
///
/// <para><b>The rows are re-read at the moment of writing.</b> The answer arrives seconds after the record was
/// read, and the user may have typed a genre in the meantime. The fill is applied to what the store holds then,
/// on the interface thread, so a value typed while the request was in flight is a value already there, and a
/// blank-only fill leaves it alone.</para>
/// </summary>
public sealed partial class MainModel
{
    /// <summary>What the fill asks before each record. Made on first use, because the shell builds the governor
    /// before this model and a test host may build neither.</summary>
    private Services.Shell.Sweep? _fillSweep;

    private Task? _fillWorker;

    /// <summary>Set by every add, cleared by the worker when it starts a round. A round that ends with this set
    /// goes round again, so music added during a fill is read too.</summary>
    private int _fillAgain;

    /// <summary>The records the catalogue has answered about this session, by the queue's own group keys, and
    /// the library they were asked in. A different library is a different queue.</summary>
    private readonly HashSet<string> _fillAsked = new(StringComparer.OrdinalIgnoreCase);
    private LibraryDatabase? _fillAskedIn;

    /// <summary>A second between records. The API passes these to a catalogue that allows one request a
    /// second, and the lookup window someone may open meanwhile should not queue behind a whole import.</summary>
    private static readonly TimeSpan FillBreath = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the catalogue about the queued records that lack a genre, a style or a year, unless the setting is
    /// off. Called on the interface thread after music is added. A fill already running takes the new records
    /// on at the end of its round, rather than a second worker doubling the requests.
    /// </summary>
    private void StartCatalogueFill()
    {
        if (!_settings.ImportFillFromCatalogue || !_library.IsOpen)
            return;

        Interlocked.Exchange(ref _fillAgain, 1);
        if (_fillWorker is { IsCompleted: false })
            return;

        var db = _library.Db;
        _fillSweep ??= App.Services.GetRequiredService<Services.Shell.Governor>().Pass(
            "catalogue fill", Services.Shell.Lane.Network, Services.Shell.Wave.Second,
            notBefore: Services.Shell.Phase.Ready);
        var sweep = _fillSweep;
        _fillWorker = Task.Run(() => CatalogueFillAsync(db, sweep));
    }

    private async Task CatalogueFillAsync(LibraryDatabase db, Services.Shell.Sweep sweep)
    {
        // The pass ends when the library it was started on is no longer the open one. That is a swap of the
        // music folder, and the rows it would write to have gone with it.
        using var stop = new CancellationTokenSource();
        var ct = stop.Token;
        var filled = 0;

        lock (_fillAsked)
        {
            if (!ReferenceEquals(_fillAskedIn, db))
            {
                _fillAsked.Clear();
                _fillAskedIn = db;
            }
        }

        try
        {
            while (Interlocked.Exchange(ref _fillAgain, 0) == 1)
            {
                // The rpm catalogue is MusicBrainz behind the API. It needs a signed-in session, and without one
                // there is nothing to ask. Discogs is not asked: it needs the user's own account, and its genres
                // are filled by the style sweep where a record is linked.
                if (Catalog.Search.Providers.FirstOrDefault(p => p.Id == RpmMetadataProvider.ProviderId)
                        is not { IsAvailable: true } catalogue)
                    return;

                IReadOnlyList<QueueGroup> groups;
                using (var page = await sweep.Next(ct))
                {
                    if (!page.Go || !Still())
                        return;
                    groups = db.WholeQueue.Groups();
                }

                foreach (var group in groups)
                {
                    // Unknown artist and unknown album are buckets, not records. There is nothing to search by.
                    if (group.Artist.Length == 0 || group.Album.Length == 0)
                        continue;

                    var key = $"{group.Artist}\u001f{group.Album}";
                    lock (_fillAsked)
                        if (_fillAsked.Contains(key))
                            continue;

                    using var slot = await sweep.Next(ct);
                    if (!slot.Go || !Still() || !_settings.ImportFillFromCatalogue)
                        return;

                    var rows = db.WholeQueue.RowsInGroup(group.Artist, group.Album);
                    if (CatalogueFill.AlbumOf([.. rows.Select(r => r.Wanted)]) is not { } album)
                        continue;   // Nothing to fill. Not remembered: an edit can blank a field again.

                    // Not answering is not an answer about this record. The rest of the records would get the
                    // same silence, so the pass stops, and the next add asks again.
                    if (!(await CatalogueFill.FindAsync(catalogue, album, ct)).Answers(out var release, out _))
                        return;

                    lock (_fillAsked)
                        _fillAsked.Add(key);

                    if (release is not null
                        && await OnInterfaceAsync(() => ApplyCatalogueFill([.. rows.Select(r => r.Id)], release)))
                        filled++;

                    if (!await Cancellable.DelayAsync(FillBreath, ct))
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The library closed during the pass. Not an error, and nothing is half-written: one record is one
            // write on the interface thread.
        }
        catch (Exception ex)
        {
            // Quiet on the screen, but not lost. A record that cannot be read does not deserve a dialog, and the
            // diagnostic ring is where a fill that never happens can be explained.
            Faults.Note("import", "filling records from the catalogue", ex);
        }
        finally
        {
            if (filled > 0)
                SetStatus(Loc.Format("Queue_CatalogueFilled", filled));
        }

        bool Still()
        {
            if (_library.IsOpen && ReferenceEquals(_library.Db, db))
                return true;
            stop.Cancel();
            return false;
        }
    }

    /// <summary>Runs <paramref name="work"/> on the interface thread and hands back its answer. False when the
    /// window is gone and the queue will not take it.</summary>
    private Task<bool> OnInterfaceAsync(Func<bool> work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }))
            done.SetResult(false);
        return done.Task;
    }

    /// <summary>
    /// Puts a release's answers on one record's rows, blanks only, and shows them. On the interface thread,
    /// because the rows on screen are bound to it.
    /// </summary>
    /// <returns>Whether any row took a value.</returns>
    private bool ApplyCatalogueFill(IReadOnlyList<long> ids, ReleaseDetail release)
    {
        // An import is filing these rows right now. A write that lands under it would describe a row that is
        // about to leave the queue, so the record is left for the next add instead.
        if (!_library.IsOpen || _importing)
            return false;

        var changed = new List<TrackRow>();
        foreach (var id in ids)
        {
            if (_library.Db.QueueRowById(id) is not { } stored)
                continue;
            var filled = CatalogueFill.Fill(stored.Wanted, release);
            if (!ReferenceEquals(filled, stored.Wanted))
                changed.Add(QueueTreeSource.MaterializeRow(stored with { Wanted = filled }));
        }

        if (changed.Count == 0)
            return false;

        // The same ending as a recognition run (see QueueSink): a genre or a year moves nothing, so the rows on
        // screen are given their new values in place and the reader keeps their place in the tree.
        if (_queueSource.WriteBackMany(changed))
            _ = _queueSource.RebuildAsync(_queueFilter);
        else
            _queueSource.ApplyEditsInPlace(changed);
        return true;
    }
}

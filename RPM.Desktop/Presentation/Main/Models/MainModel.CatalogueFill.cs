using RPM.Core.Analysis;
using RPM.Core.Diagnostics;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>
/// Filling in what queued records lack from the catalogue, on their own, once music has been added. See
/// <see cref="CatalogueFill"/> for what is filled and when a release is trusted, and <see cref="AdvisoryFill"/>
/// for the explicit flag. This is the part that decides when to ask, and puts the answers on the rows.
///
/// <para><b>The disc picture.</b> A record that came with no picture of its disc (see <see cref="DiscArtFinder"/>)
/// is given the one the rpm API knows, if any (<see cref="Services.Art.ArtworkService.DiscArtBytesAsync"/>). It is held
/// against the record like a disc picked in the tag panel, and written into the album folder at import.</para>
///
/// <para><b>Started by the user's own action, governed like any other pass.</b> Adding music starts it: a picked
/// folder, a finished rip, the setup wizard, or music dropped into the inbox while the app runs. The inbox
/// sweep that follows the library opening does not, because nothing in the background starts itself off the
/// library opening; the records it queued are picked up by the next add. Each record is one unit of work that
/// asks the <see cref="Services.Shell.Governor"/> first, in the network lane, because a record is only requests
/// to the rpm API: one or two for the release, one for the explicit flags of its songs, and a few for single
/// songs still unknown, a few seconds apart (see <see cref="AdvisoryFill.AskStoreAsync"/>).</para>
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

    /// <summary>The queued records whose disc picture this pass found online, by the store's intent id. The tag
    /// panel says "found online" for these rather than "picked by you". Only for this session: after a restart the
    /// picture is still there, and is shown as picked.</summary>
    private readonly HashSet<long> _discFoundOnline = [];

    /// <summary>Whether this session's fill has answered for a queued record. An import follow-up asks only about
    /// the records it has not answered for. See MainModel.ImportFollowUp.</summary>
    private bool FillAnswered(string artist, string album)
    {
        lock (_fillAsked)
            return ReferenceEquals(_fillAskedIn, _library.Db) && _fillAsked.Contains($"{artist}\u001f{album}");
    }

    /// <summary>Whether the disc picture held against this intent was found online by the fill.</summary>
    public bool QueueDiscFoundOnline(long intentId)
    {
        lock (_discFoundOnline)
            return intentId != 0 && _discFoundOnline.Contains(intentId);
    }

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
                var bySongLater = new List<(QueueGroup Group, CatalogueFill.Album Album)>();
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
                    var album = CatalogueFill.AlbumOf([.. rows.Select(r => r.Wanted)]);

                    // A record that came with a picture of its disc, or already has one held against it, is not
                    // asked about it. This runs off the interface thread, because it looks in folders and files.
                    var wantsDisc = WantsDiscOnline(db, rows);
                    if (album is null && !wantsDisc)
                        continue;   // Nothing to fill. Not remembered: an edit can blank a field again.

                    if (album is not null)
                    {
                        // Not answering is not an answer about this record. The rest of the records would get the
                        // same silence, so the pass stops, and the next add asks again.
                        if (!(await CatalogueFill.FindAsync(catalogue, album, ct)).Answers(out var release, out _))
                            return;

                        var took = release is not null
                                   && await OnInterfaceAsync(() => ApplyCatalogueFill([.. rows.Select(r => r.Id)], release));
                        if (took)
                            filled++;

                        // The songs the release gave no explicit flag are asked about song by song, but only after
                        // every record in the queue has had its release fill. The song requests are seconds apart,
                        // and genre, style and year must not wait behind them.
                        bySongLater.Add((group, album));
                    }

                    if (wantsDisc)
                        await FetchQueueDiscAsync(db, rows, ct);

                    lock (_fillAsked)
                        _fillAsked.Add(key);

                    if (!await Cancellable.DelayAsync(FillBreath, ct))
                        return;
                }

                // Then the explicit flags, song by song, for the records the release left without them. A record
                // that has left the queue since has nothing to ask about (the import follow-up takes it). When the
                // API does not answer, only this part stops: the release fill above is already done.
                foreach (var (group, album) in bySongLater)
                {
                    using var slot = await sweep.Next(ct);
                    if (!slot.Go || !Still() || !_settings.ImportFillFromCatalogue)
                        return;

                    var (reached, bySong) = await FillAdvisoryBySongAsync(db, group, album, Still, ct);
                    if (bySong)
                        filled++;
                    if (!reached)
                        break;
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
            // The explicit flag comes from the same listing. The record's size is the number of rows asked about,
            // which is the whole record (see CatalogueFill.AlbumOf).
            var filled = AdvisoryFill.Fill(
                CatalogueFill.Fill(stored.Wanted, release), stored.Current, release, ids.Count);
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
    /// <summary>
    /// Asks the rpm API, song by song, about the songs of one queued record that still have no explicit flag (or
    /// Clean) after the release fill, and puts what it says on the rows (see <see cref="AdvisoryFill.AskStoreAsync"/>).
    /// A flag set in the queue is not asked about. One unit of work, run after the release fill of every queued
    /// record: the requests for single songs are a few seconds apart, and stop when the pass may no longer run.
    /// </summary>
    /// <returns>Whether every request was answered (false ends the song-by-song part of the pass, never the release
    /// fill, which has run by then), and whether any row took a flag.</returns>
    private async Task<(bool Reached, bool Changed)> FillAdvisoryBySongAsync(
        LibraryDatabase db, QueueGroup group, CatalogueFill.Album album, Func<bool> still, CancellationToken ct)
    {
        var open = db.WholeQueue.RowsInGroup(group.Artist, group.Album)
            .Where(r => r.Wanted.Advisory == r.Current.Advisory && AdvisoryFill.Wants(r.Wanted.Advisory))
            .ToList();
        var api = App.Services.GetRequiredService<IRpmMetadataApi>();
        if (open.Count == 0 || !api.IsAvailable)
            return (true, false);

        var asked = await AdvisoryFill.AskStoreAsync(
            api, album.Artist, album.Title, [.. open.Select(r => AdvisoryFill.SongOf(r.Wanted))],
            async (wait, token) => still() && _settings.ImportFillFromCatalogue && await Cancellable.DelayAsync(wait, token),
            ct);
        if (!asked.Answers(out var store, out _))
            return (false, false);

        var answers = new Dictionary<long, bool?>();
        for (var i = 0; i < open.Count; i++)
            if (store.Explicit[i] == true)
                answers[open[i].Id] = true;

        var changed = answers.Count > 0 && await OnInterfaceAsync(() => ApplyAdvisoryBySong(answers));
        return (store.Whole, changed);
    }

    /// <summary>Puts the API's song-by-song explicit flags on queued rows, read again at the moment of writing. On
    /// the interface thread, like <see cref="ApplyCatalogueFill"/>.</summary>
    /// <returns>Whether any row took a flag.</returns>
    private bool ApplyAdvisoryBySong(IReadOnlyDictionary<long, bool?> answers)
    {
        if (!_library.IsOpen || _importing)
            return false;

        var changed = new List<TrackRow>();
        foreach (var (id, answer) in answers)
        {
            if (_library.Db.QueueRowById(id) is not { } stored)
                continue;
            var filled = AdvisoryFill.FillBySong(stored.Wanted, stored.Current, answer);
            if (!ReferenceEquals(filled, stored.Wanted))
                changed.Add(QueueTreeSource.MaterializeRow(stored with { Wanted = filled }));
        }

        if (changed.Count == 0)
            return false;

        if (_queueSource.WriteBackMany(changed))
            _ = _queueSource.RebuildAsync(_queueFilter);
        else
            _queueSource.ApplyEditsInPlace(changed);
        return true;
    }

    /// <summary>
    /// Whether a queued record should be given a disc picture from the rpm API: it has no picture of its disc
    /// held against it, the person has not taken one away, and none came with its files.
    /// </summary>
    private static bool WantsDiscOnline(LibraryDatabase db, IReadOnlyList<QueueTrack> rows)
    {
        if (rows.Count == 0)
            return false;
        if (db.QueueAlbumIntentSharedBy([.. rows.Select(r => r.Id)]) is { } said
            && (said.DiscArt is { Length: > 0 } || said.RemoveDiscArt))
            return false;

        // A picture inside the files is on every track if it is on any, so a few files are enough to read.
        return DiscArtFinder.ForRecord(
            rows.Select(r => Path.GetDirectoryName(r.Wanted.FilePath)).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!),
            rows.Take(4).Select(r => r.Wanted.FilePath)) is null;
    }

    /// <summary>Asks the rpm API for the record's disc picture and holds it against the record. Quiet: no answer
    /// leaves the record as it was, and the drawn disc stands in for it.</summary>
    private async Task FetchQueueDiscAsync(LibraryDatabase db, IReadOnlyList<QueueTrack> rows, CancellationToken ct)
    {
        var first = rows[0].Wanted;
        var artist = string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.Artist : first.AlbumArtist;
        var title = first.Album is { } a ? AlbumResolver.NormalizeAlbum(a) : null;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            return;

        var bytes = await App.Services.GetRequiredService<Services.Art.ArtworkService>().DiscArtBytesAsync(artist, title, ct);
        if (bytes is not { Length: > 0 })
            return;

        await OnInterfaceAsync(() =>
        {
            if (!_library.IsOpen || !ReferenceEquals(_library.Db, db) || _importing)
                return false;

            // Asked again at the moment of writing. A disc picked in the tag panel while the request was out is
            // the person's answer, and it stays.
            var ids = rows.Select(r => r.Id).ToList();
            if (db.QueueAlbumIntentSharedBy(ids) is { } said && (said.DiscArt is { Length: > 0 } || said.RemoveDiscArt))
                return false;

            var intentId = db.QueueSetAlbumIntentForRows(ids, new QueueAlbumIntent(DiscArt: bytes));
            if (intentId != 0)
                lock (_discFoundOnline)
                    _discFoundOnline.Add(intentId);
            QueueDiscChanged?.Invoke();
            return true;
        });
    }

    /// <summary>Raised on the interface thread when the fill gave a queued record a disc picture, so a tag panel
    /// that shows that record can show the picture.</summary>
    public event Action? QueueDiscChanged;
}

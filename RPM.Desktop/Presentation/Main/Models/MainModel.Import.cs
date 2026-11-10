using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Identify;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>Music coming in, second half: filing what is queued into the library. Tags are written, each
/// file is placed by the naming template, and the row leaves the queue.
///
/// <para>This is not editing. Correcting fields across many tracks is the job of the mass edit (see <see
/// cref="Dialogs.MassEdit"/>). It has nothing to do with importing. One moves files in. The other changes
/// files that are already where they belong.</para>
/// </summary>
public sealed partial class MainModel
{
    // ── import (queue → library) ──

    /// <summary>The ticked rows: the set that both Recognize and Import act on. They are materialised from
    /// the store.</summary>
    private List<TrackRow> CheckedRows() => _library.IsOpen
        ? Queue.CheckedRows().Select(QueueTreeSource.MaterializeRow).ToList()
        : [];

    public bool CanImport => !_importing && !_scanning && _library.IsOpen && _queueSource.ImportableCount > 0;

    /// <summary>The import, awaitable.
    ///
    /// <para>This does the same work that <see cref="ImportCommand"/> starts. It exists because a command
    /// cannot be awaited. The setup wizard's last step prints how many records landed. It has to run after
    /// the filing, not beside it. A step that moved on while the files were still moving would print the
    /// count of an import that had not finished.</para>
    /// </summary>
    public Task RunImportAsync() => ImportAsync();

    /// <summary>Where the queue's files WOULD land, under a template, without filing anything.
    /// </summary>
    /// <remarks>
    /// <b>It runs the same preparation that the press runs. That is the whole point of it.</b> The setup
    /// wizard shows this list beside the template choice, so a wrong layout is caught before it is applied.
    /// A preview that plans differently from the press is worse than no preview. It is a confident wrong
    /// answer.
    ///
    /// <para>Multi-disc filing must be included here. Without it, a two-disc record is shown as "The Wall
    /// (1)" and "The Wall (2)". Disc two's track one collides with disc one's, and the planner makes a
    /// second folder to resolve the collision. The press merges both discs into one album with continuing
    /// numbers. So this preview would show a result that the app never produces.</para>
    ///
    /// <para>This runs off the UI thread. It renders a template over every importable row and resolves
    /// their collisions.</para>
    /// </remarks>
    public Task<IReadOnlyList<TrackSuggestion>> PlanImportAsync(NamingTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (!_library.IsOpen || _library.Root is not { } root)
            return Task.FromResult<IReadOnlyList<TrackSuggestion>>([]);

        bool merged = _settings.MultiDiscMode == MultiDiscMode.MergedAlbum;

        // The queue is read on the UI thread, and planned off it. The plan is LibraryFiler's, the same one
        // the press files by.
        var importable = Queue.ImportableRows();
        var tracks = importable.Select(q => q.Wanted).ToList();
        var db = _library.Db;

        return Task.Run(() => LibraryFiler.Plan(db, tracks, template, merged, root));
    }

    // ── arranging a record by hand ──────────────────────────────────────────────────────────────

    /// <summary>Puts rows somewhere else inside their record, and shows the result.
    ///
    /// <para>Nothing is read again. An arrange changes the order inside one record and nothing else. The
    /// same groups hold the same rows, and the rows are the very objects that the grid is drawing. So the
    /// store is written, and the tree is told which slots those objects now sit in. The reader keeps their
    /// place, their open records, and the wash on the rows they were carrying. None of it was ever thrown
    /// away.</para>
    /// </summary>
    /// <param name="settled">The record's rows on screen, in the order that they now read, starting
    /// <paramref name="firstOffset"/> places into the record. The tree is told THAT, rather than asked to
    /// read itself again. The rows are the same objects in new places, and nothing else moved.</param>
    public void ReorderQueueRows(
        ImportNode record, IReadOnlyList<long> ids, long? afterId, long? beforeId,
        int firstOffset, IReadOnlyList<ImportNode> settled)
    {
        if (!_library.IsOpen || ids.Count == 0
            || record.GroupArtist is not { } artist || record.GroupAlbum is not { } album)
            return;

        _library.Db.QueueReorder(artist, album, ids, afterId, beforeId);
        _queueSource.ReorderLeavesInPlace(record, firstOffset, settled);
    }

    /// <summary>The queue rows that the grid is drawing now. The pane paints its wash onto these. A ticked
    /// row that nobody has scrolled to has nothing to paint.</summary>
    public IEnumerable<Main.LeftPane.Models.ImportNode> QueueRealizedRows => _queueSource.RealizedFileNodes;

    // ── what the pane shows while an import runs ──

    /// <summary>The import animation, in the grid's place. The grid itself goes away for the duration (<see
    /// cref="QueueTreeVisibility"/>), so this is never drawn over rows.</summary>
    public Visibility ImportingVisibility => _importing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What is being done to the files, in the words that the setting uses: moving them into the
    /// collection, or copying them there and leaving the source alone.</summary>
    public string ImportCaption => _settings.ImportAction == ImportAction.Copy
        ? Loc.Get("Queue_ImportingCopy")
        : Loc.Get("Queue_ImportingMove");

    /// <summary>The running tally under the animation. It is the same "n / total files" line that a scan
    /// shows.</summary>
    public string ImportCountText => Loc.Format("Analyze_Progress", _importDone, _importTotal);

    /// <summary>Never 0, so the bar has a valid value before the first file is filed.</summary>
    public double ImportProgressMax => Math.Max(1, _importTotal);

    public double ImportProgressValue => _importDone;

    /// <summary>The bar sweeps while the plan is still being built (there is no total yet). This is only
    /// WHILE importing. An indeterminate ProgressBar runs a Forever storyboard, and a running one keeps the
    /// Skia compositor busy even when it is collapsed. The scan animation describes the same
    /// trap.</summary>
    public bool ImportIsIndeterminate => _importing && _importTotal <= 0;

    private void RaiseImportProgress()
    {
        OnChanged(nameof(ImportCaption));
        OnChanged(nameof(ImportCountText));
        OnChanged(nameof(ImportProgressMax));
        OnChanged(nameof(ImportProgressValue));
        OnChanged(nameof(ImportIsIndeterminate));
    }

    /// <summary>Files the queue into the collection. Every importable row is planned, written, and moved
    /// (or copied) on a background thread. The pane shows the animation, with a file count, while it runs.
    ///
    /// <para>Nothing here touches the disk on the UI thread: not the plan, not the queries, and not the row
    /// removal. A few thousand files is minutes of work, and the window has to stay responsive through it.
    /// The only things left on the UI thread are the two that must be there: reading the settings that the
    /// import runs under, and the progress reports. The reports come back through the <see
    /// cref="Progress{T}"/> that is captured here.</para>
    /// </summary>
    ///
    /// <remarks><see cref="RunImportAsync"/> does the same work, and it can be awaited. This command
    /// cannot be awaited, and the setup wizard has to await it. The wizard shows what was filed on the step
    /// after this one. A step that moved on while the files were still moving would print the count of an
    /// import that had not happened.
    /// </remarks>
    private async Task ImportAsync()
    {
        if (!CanImport)
            return;

        var template = _settings.DefaultTemplate;
        var mode = _settings.ImportAction == ImportAction.Copy ? OrganizeMode.Copy : OrganizeMode.Move;
        bool merged = _settings.MultiDiscMode == MultiDiscMode.MergedAlbum;
        var root = _library.Root!;

        // A library on a NAS: the NAS cannot refuse a write that comes over SMB, so its space is asked
        // here, before anything is copied.
        var (spaceLine, refuse) = await RPM.Desktop.Services.Nas.NasImportSpace.CheckAsync(
            _settings.LibraryNas, root, App.Services.GetRequiredService<RPM.Desktop.Services.Nas.NasDiscovery>(),
            QueueChipCounts.Bytes);
        if (refuse)
        {
            SetStatus(spaceLine!);
            return;
        }

        // An import files music into the user's own collection, so that is the collection to be standing in
        // when it lands. Somebody else's shelf is a place to look at records. A record that lands somewhere
        // the user cannot see would look as if it had disappeared.
        _library.ViewOwn();

        _importing = true;
        _importDone = 0;
        _importTotal = 0;
        RaiseImportState();
        RaiseQueueChrome();     // grid out, animation in
        RaiseImportProgress();

        try
        {
            // The Progress is created on the UI thread, so every report lands back there. This is the same
            // hand-off that the folder scan uses (see AddSourceAsync).
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                _importDone = p.Done;
                _importTotal = p.Total;
                RaiseImportProgress();
            });

            var results = await Task.Run(() => RunImport(template, mode, merged, root, progress));

            int ok = results.Count(r => r.Success);
            SetStatus(spaceLine is null
                ? Loc.Format("Queue_ImportDone", ok, results.Count)
                : Loc.Format("Queue_ImportDone", ok, results.Count) + " " + spaceLine);
        }
        catch (Exception ex)
        {
            // Whatever was filed before the error is already recorded. RunImport commits the mirror and the
            // queue removal in a finally block. So the queue that comes back holds exactly the rows that
            // did not make it. Nothing needs undoing. Only the user needs to be told.
            SetStatus(Loc.Format("Queue_ImportFailed", Faults.Note("import", "the import", ex).Say()));
        }
        finally
        {
            _importing = false;
            _importDone = 0;
            _importTotal = 0;
            await _queueSource.RebuildAsync(_queueFilter); // rows and folders changed
            RaiseQueueChrome();
            RaiseImportState();
            RaiseImportProgress();
            RaiseRecognizeState();
            _library.NotifyChanged();
        }
    }

    /// <summary>The whole import, off the UI thread. It reads what is importable, plans it, applies it
    /// file by file, mirrors what landed into the library db, and takes the filed rows out of the queue. It
    /// reports (done, total) as it goes. The db is lock-guarded, so all of this is safe here.
    /// </summary>
    private IReadOnlyList<ApplyResult> RunImport(
        NamingTemplate template,
        OrganizeMode mode,
        bool merged,
        string root,
        IProgress<(int Done, int Total)> progress)
    {
        // Rows that joined a record after somebody answered for it take on the record's answers before
        // anything is planned. Examples are a second disc that was scanned later, or a cloud placeholder
        // that was just fetched.
        _library.Db.QueueAdoptAlbumIntents();

        // Read BEFORE anything is filed. The rows leave the queue as they land, and the last row of a
        // record takes its intent with it. The orphan sweep depends on that. So the answers must be in hand
        // before that happens.
        var albumIntents = _library.Db.QueueAlbumIntents();

        var importable = Queue.ImportableRows();
        var byPath = importable.ToDictionary(q => q.Path, StringComparer.OrdinalIgnoreCase);
        var tracks = importable.Select(q => q.Wanted).ToList();

        // An import files the QUEUE's files. The music that is playing is almost always a record from the
        // library, and this does not touch it. Stopping it was collateral damage. Filing a few tracks in
        // the background is no reason for the room to go quiet.
        //
        // The one file that cannot be left alone is a queued file that is being previewed while it is moved
        // out from under the decoder. The path it is reading would stop existing. On Windows, the move
        // would fail outright against the open handle. A copy leaves the original in place. So only a move
        // is worth stopping for.
        if (_playback.PlayingPath is { } playing && byPath.ContainsKey(playing)
            && (mode == OrganizeMode.Move || LibraryInbox.IsInside(root, playing)))
            _dispatcher.TryEnqueue(_playback.Stop);

        // Successfully imported rows leave the queue. Failures stay for another look. The queue is cleared
        // in a finally block, after LibraryFiler has recorded what landed (in its own finally block), so the
        // mirror and the queue agree with the disk whichever way the run ends.
        var importedIds = new List<long>();
        // (intent, where its file landed) for this run only. This is the scope in which the album answers
        // are applied.
        var landed = new List<(long IntentId, string DestinationPath)>();
        IReadOnlyList<Filed> filed;
        try
        {
            filed = LibraryFiler.File(
                _library.Db, tracks, new FilingRules(template, mode, merged, root),
                [.. importable.Select(q => q.Source).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
                new FilingStatus(this, progress),
                (input, done) =>
                {
                    if (!byPath.TryGetValue(input.FilePath, out var queued))
                        return;
                    importedIds.Add(queued.Id);
                    // The record's answers are applied to the folders that these files landed in, and to
                    // no others (see AlbumIntentApplier). A run can only touch what the run filed.
                    if (queued.AlbumIntentId != 0)
                        landed.Add((queued.AlbumIntentId, done.Result.DestinationPath));
                },
                CancellationToken.None);
        }
        finally
        {
            _library.Db.QueueRemove(importedIds);
        }

        var list = filed.Select(f => f.Result).ToList();

        // What the queue was told about each RECORD (whether it is classical, whether it is levelled, and
        // what the disc label looks like) is written onto the folders that this run filed them into. It is
        // read after the filing, because the queue rows are gone by now, and the ids were kept above.
        ApplyAlbumIntents(landed, albumIntents, root);

        // The import already has in hand what the CATALOGUE said about each record. It is kept here, and it
        // is not thrown away once the tags are written.
        RememberPressings(list, root);

        return list;
    }

    /// <summary>Writes down which pressing each imported album was identified as.
    ///
    /// <para>The identification has already happened. Whatever the user accepted in the queue is in the
    /// tags of the files that just landed. That includes the catalogue number, the barcode and the
    /// MusicBrainz release. What did not happen was any of that reaching <c>album_meta</c>. So an album
    /// imported from files was unlinked, however well it had been identified. The only route to a pressing
    /// was to pick a Discogs release by hand afterwards. This method makes that route automatic.</para>
    ///
    /// <para>Nothing already written is overwritten. An album that the user linked themselves keeps their
    /// link. A tag is what a file says about itself, and the user's own answer outranks it.</para>
    /// </summary>
    private void RememberPressings(IReadOnlyList<ApplyResult> results, string root)
    {
        var db = _library.Db;

        // The first writer wins within the run too. An album's tracks all carry the same release. The
        // tracks that carry nothing must not blank what a sibling supplied.
        foreach (var group in results
            .Where(r => r.Success)
            .GroupBy(r => AlbumIntentApplier.RelativeFolder(r.DestinationPath, root),
                StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(group.Key))
                continue;

            var tags = group
                .Select(r => TryRead(r.DestinationPath))
                .Where(t => t is not null)
                .ToList();

            if (tags.Count == 0)
                continue;

            var meta = db.GetAlbumMeta(group.Key);

            if (!meta.IsLinkedToPressing &&
                First(tags, t => t!.MusicBrainzReleaseId) is { } release)
                db.SetAlbumPressing(group.Key, RpmMetadataProvider.ProviderId, release);

            var catno = meta.Catno ?? First(tags, t => t!.CatalogNumber);
            var barcode = meta.Barcode ?? First(tags, t => t!.Barcode);

            if (catno != meta.Catno || barcode != meta.Barcode)
                db.SetAlbumRelease(group.Key, catno, meta.Released, barcode);

            // The disc this came off, when it came off one. Nothing else identifies a pressing as well,
            // unless somebody has read the sleeve correctly first.
            if (meta.DiscId is null && First(tags, t => t!.MusicBrainzDiscId) is { } discId)
                db.SetAlbumDiscId(group.Key, discId);

            // And which album these pressings are of. Every library that Picard has touched carries it. It
            // keeps a remaster and the original cut on one page, instead of two.
            if (meta.ReleaseGroupId is null && First(tags, t => t!.MusicBrainzReleaseGroupId) is { } releaseGroup)
                db.SetAlbumReleaseGroup(group.Key, releaseGroup);
        }

        static TrackInfo? TryRead(string path)
        {
            try
            {
                return TagReader.Read(path);
            }
            catch
            {
                // A file that cannot be read again says nothing about the pressing. The import has already
                // succeeded. This is the part that is allowed to come up empty.
                return null;
            }
        }

        static string? First(List<TrackInfo?> tags, Func<TrackInfo?, string?> field)
            => tags.Select(field).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    /// <summary>Applies each record's album-level answers to the folders that THIS run put its files in.
    ///
    /// <para>It is done per folder and per run, on purpose. The folders come from the results. So an intent
    /// cannot reach music that the run did not file. A record filed one disc at a time is answered again on
    /// the second import. That answer applies to that disc's folder, and to that one only.</para>
    /// </summary>
    private void ApplyAlbumIntents(
        IReadOnlyList<(long IntentId, string DestinationPath)> landed,
        IReadOnlyDictionary<long, QueueAlbumIntent> intents, string root)
    {
        if (landed.Count == 0 || intents.Count == 0)
            return;

        foreach (var (intentId, folders) in AlbumIntentApplier.FoldersPerIntent(landed, root))
        {
            if (!intents.TryGetValue(intentId, out var intent))
                continue;
            foreach (var folder in folders)
                AlbumIntentApplier.Apply(_library.Db, root, folder, intent);
        }
    }

    /// <summary>The filer's progress as the import's: the status line names the file, and the bar moves.
    /// The status line is written at most as often as the filer reports (about sixteen times a
    /// second).</summary>
    private sealed class FilingStatus(MainModel model, IProgress<(int Done, int Total)> bar) : IProgress<FilingProgress>
    {
        public void Report(FilingProgress value)
        {
            if (value.FileName.Length > 0)
                model.SetStatus(Loc.Format("Queue_Importing", value.Done, value.Total, value.FileName));
            bar.Report((value.Done, value.Total));
        }
    }

    private void RaiseImportState()
    {
        OnChanged(nameof(CanImport));
        // The subtitle counts the ticked rows now that the button does not. So it changes whenever they do.
        OnChanged(nameof(QueueFolderPath));
        OnChanged(nameof(CanAddMusic));
    }
}

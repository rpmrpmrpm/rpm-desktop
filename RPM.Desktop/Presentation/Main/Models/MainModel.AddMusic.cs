using RPM.Desktop.Presentation.Import;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Identify;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>Music coming IN: scanning a source folder into the queue. This is the first half of the journey
/// that a file makes. It ends at <see cref="MainModel.ImportAsync"/>, which files what is queued into the
/// library. Nothing here writes to the library, and nothing here edits tags.
/// </summary>
public sealed partial class MainModel
{
    // ── add music (scan a source folder into the queue) ──

    public bool CanAddMusic => !_scanning && !_importing;

    /// <summary>Empties the import queue.
    ///
    /// <para>It goes through the tree source, not straight to the store. The store is only half of the
    /// queue. The source holds the realised leaves. Rows that were deleted behind its back stay on the
    /// tree, as ids that point at nothing. The setup wizard's Change asks for this. A new source then
    /// replaces the last one, instead of being added to it.</para>
    /// </summary>
    public Task ClearQueueAsync() => _queueSource.ClearAsync();

    /// <summary>Scans a source folder into the import queue. The folder is whatever AddMusicDialog hands
    /// back. It can be a folder that the user picked, or the staging folder that a CD was ripped into. Both
    /// arrive here the same way. A rip is just another source folder.
    ///
    /// <para>It answers with how many audio files the scan FOUND. That is not how many were queued, because
    /// a file that is already on the list is not added twice. Zero needs saying out loud. A folder with no
    /// music in it looks exactly like a scan that silently failed. See <see
    /// cref="MainPage.ScanSourcesAsync"/>.</para>
    ///
    /// <para>The records it adds have their missing genre, style and year looked up afterwards (see
    /// <see cref="StartCatalogueFill"/>), because everything that calls this is the user adding music.</para>
    /// </summary>
    public Task<int> AddSourceAsync(string sourcePath) => AddSourceAsync(sourcePath, fillFromCatalogue: true);

    /// <inheritdoc cref="AddSourceAsync(string)"/>
    /// <param name="fillFromCatalogue">Whether to look up what the added records lack. False only for the inbox
    /// sweep that follows the library opening, because nothing in the background starts itself off the library
    /// opening.</param>
    /// <remarks>Private, and with no default value, on purpose: the reactive view-model generator wraps every
    /// public method of this model, and it writes a <c>bool</c> default as <c>True</c>, which does not
    /// compile.</remarks>
    private async Task<int> AddSourceAsync(string sourcePath, bool fillFromCatalogue)
    {
        if (_scanning)
            return 0;
        _scanning = true;
        _scanProcessed = 0;
        _scanTotal = 0;
        RaiseQueueChrome(); // show the scanning overlay + disable Add, without rebuilding the tree
        RaiseScanProgress();
        try
        {
            var analyzer = new FolderAnalyzer(identifier: null); // offline: tags + filename only
            var progress = new Progress<AnalysisProgress>(p =>
            {
                _scanProcessed = p.Processed;
                _scanTotal = p.Total;
                RaiseScanProgress();
            });

            IReadOnlyList<TrackInfo> tracks;
            try
            {
                // Folders that an older build named "_originals" get the name that the tree shows now,
                // before the scan reads them. One folder gets one name, whichever build made it.
                await Task.Run(() => RPM.Core.Splitting.AlbumFileSplitter.MigrateLegacyOriginals(sourcePath));
                tracks = await Task.Run(() => analyzer.AnalyzeAsync(sourcePath, progress));
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: a folder that cannot be read leaves the queue as it was. But never
                // silently. This is the one place that knows why nothing turned up.
                RPM.Core.Diagnostics.Faults.Note("scan", $"scanning '{sourcePath}'", ex);
                tracks = []; // a failed scan just leaves the queue as it was
            }

            // Turn the scanned tracks into queue rows (each with its album-file probe, batched per folder)
            // off the UI thread. Then hand them to the store, which removes duplicates by path and rebuilds
            // the virtualised skeleton. The 20k+ rows are never all held in memory at once, so nothing
            // freezes.
            var queueRows = await Task.Run(() => BuildQueueTracks(tracks, sourcePath));
            await _queueSource.InsertAsync(queueRows);

            // The rows are in the store and on screen. What they lack is asked about now, in the background,
            // so the user sees the filled genre and year in the queue before they import. See
            // MainModel.CatalogueFill.
            if (fillFromCatalogue && queueRows.Count > 0)
                StartCatalogueFill();
            return tracks.Count;
        }
        finally
        {
            _scanning = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            RaiseQueueChrome();
            RaiseScanProgress();
        }
    }

    /// <summary>Turns scanned tracks into queue rows. This is the CPU and disk part, and it runs OFF the UI
    /// thread. Each row gets its album-file probe (batched per folder via <see
    /// cref="RPM.Core.Splitting.FolderScanIndex"/>), its size on disk, and the folder it came out of. The
    /// wanted values start equal to the current ones, as in a fresh scan. The tree does not group rows by
    /// that folder. The tags decide where a row shows. But the folder is still what says that a file was
    /// kept in <c>Originals/</c>, and such a file must never be imported.
    /// </summary>
    private static IReadOnlyList<QueueTrack> BuildQueueTracks(IReadOnlyList<TrackInfo> tracks, string source)
    {
        var cues = RPM.Core.Splitting.FolderScanIndex.Build(tracks.Select(t => t.FilePath));
        var sourceName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(sourceName))
            sourceName = source;

        // The two fields that a rip most often leaves blank, and that the library most needs: the album
        // artist the record is filed under, and which disc of the set each file is. Both are set on the
        // WANTED side only. Nothing was read off the disk, so CURRENT says nothing. The grid then shows the
        // proposal for exactly what it is: a field that this app filled in. Album artist comes first,
        // because the disc pass groups by the artist that a track files under, which is the one just worked
        // out.
        var wanted = DiscInference.Fill(AlbumArtistFill.Fill(tracks));

        var rows = new List<QueueTrack>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var rel = Path.GetRelativePath(source, track.FilePath).Replace(Path.DirectorySeparatorChar, '/');
            var relDir = Path.GetDirectoryName(rel)?.Replace(Path.DirectorySeparatorChar, '/') ?? "";
            var folder = relDir.Length > 0 ? $"{sourceName}/{relDir}" : sourceName;

            var candidate = RPM.Core.Splitting.AlbumFileDetector.Detect(track, cues);
            rows.Add(new QueueTrack
            {
                Source = source,
                Folder = folder,
                FileName = Path.GetFileName(track.FilePath),
                SplitHint = candidate is null ? 0 : (int)candidate.Hint,
                SplitCue = candidate?.CueSheetPath,
                SplitDuration = candidate?.DurationSeconds ?? 0,
                SizeBytes = FileSize(track.FilePath),
                Current = track,
                Wanted = wanted[i],
            });
        }
        return rows;
    }

    /// <summary>Fetches the cloud placeholders among <paramref name="rows"/>, which are the ticked rows or
    /// the right-clicked row, and re-reads them.</summary>
    public Task DownloadCloudRowsAsync(IReadOnlyList<TrackRow> rows)
    {
        if (_scanning || _importing || !_library.IsOpen)
            return Task.CompletedTask;

        // The store knows which source each row came in under. That is what re-queueing needs.
        var queued = rows
            .Where(r => r.Availability is FileAvailability.Dataless && r.QueueId != 0)
            .Select(r => _library.Db.QueueRowById(r.QueueId))
            .OfType<QueueTrack>()
            .ToList();
        return DownloadQueueTracksAsync(queued);
    }

    /// <summary>Fetches every placeholder under one tree group: a record, or a whole artist. The set comes
    /// out of the store, not off the grid. The group's leaves are paged, so the rows that nobody has
    /// scrolled to are not in memory to be ticked. Those rows are most of what "download this album" means.
    /// </summary>
    public Task DownloadCloudGroupAsync(string artist, string? album)
        => _scanning || _importing || !_library.IsOpen
            ? Task.CompletedTask
            : DownloadQueueTracksAsync(Queue.DatalessRowsInGroup(artist, album));

    /// <summary>How many placeholders a group holds. This is the number that the menu offers to
    /// fetch.</summary>
    public int QueueDatalessCount(string artist, string? album)
        => _library.IsOpen ? _library.Db.QueueDatalessCountInGroup(artist, album) : 0;

    /// <summary>A record's rows, as the actions use them. They are read out of the store in play order, and
    /// they are narrowed by whatever the queue is narrowed to. A right-click on a record is about the
    /// record that the user can SEE. The group's leaves are paged, so the tree holds only the few rows that
    /// have been scrolled past.
    /// </summary>
    public IReadOnlyList<TrackRow> QueueRowsInGroup(string artist, string? album)
        => _library.IsOpen
            ? Queue.RowsInGroup(artist, album).Select(QueueTreeSource.MaterializeRow).ToList()
            : [];

    /// <summary>How many rows a group holds. This is the number that the menu shows before an action has
    /// fetched a single one of them.</summary>
    public int QueueGroupCount(string artist, string? album)
        => _library.IsOpen ? Queue.GroupCheckState(artist, album).Total : 0;

    /// <summary>What a queued record has been told about itself, or null when nobody has said anything. The
    /// album editor opens with this.</summary>
    public QueueAlbumIntent? QueueAlbumIntentFor(string artist, string? album)
        => _library.IsOpen ? _library.Db.QueueAlbumIntentFor(artist, album) : null;

    /// <summary>Holds an album-level answer against a queued record until its files reach a folder. This
    /// rebuilds the tree, because the same visit to the editor usually moves fields that the tree groups by
    /// as well.
    /// </summary>
    public void SetQueueAlbumIntent(IReadOnlyList<TrackRow> rows, QueueAlbumIntent intent)
    {
        if (!_library.IsOpen || rows.Count == 0)
            return;
        // By ROW, never by name. The window that collects these answers is the same one that renames the
        // record. A release lookup fills in the album artist and the release's own title. So by the time
        // the answers come back, the group they were given for may not exist. A stamp keyed on its name
        // would land on nothing.
        _library.Db.QueueSetAlbumIntentForRows([.. rows.Select(r => r.QueueId)], intent);
        _ = _queueSource.RebuildAsync(_queueFilter);
    }

    /// <summary>The release that these rows' record was settled against, if one was. Naming its tracks
    /// afterwards then uses the pressing that was already chosen, instead of asking again.</summary>
    public QueueAlbumIntent? QueueAlbumIntentForRows(IReadOnlyList<TrackRow> rows)
        => _library.IsOpen && rows.Count > 0
            ? _library.Db.QueueAlbumIntentForRows([.. rows.Select(r => r.QueueId)])
            : null;

    /// <summary>The series names to offer: the ones the library already files records under, and the ones
    /// named against records still in the import queue, once each, in alphabetical order. Empty while no
    /// library is open.</summary>
    public IReadOnlyList<string> AlbumSeriesNames()
    {
        if (!_library.IsOpen)
            return [];
        var names = new List<string>();
        foreach (var name in _library.Db.AlbumSeriesNames().Concat(_library.Db.QueueAlbumSeriesNames()))
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>The record that a selection is the WHOLE of. It is null when the selection is not one
    /// record's worth: some of a record's tracks, or tracks from several records. A window asks this before
    /// it offers an answer that belongs to the record rather than to any one file. See <see
    /// cref="QueueRecordSelection"/>.
    ///
    /// <para>Both halves are asked of the queue AS IT IS BEING LOOKED AT. The ticks are read through the
    /// filter, and so is the record's count. So a filtered queue compares like with like.</para>
    /// </summary>
    public QueueRecordSelection? WholeQueueRecord(IReadOnlyList<TrackRow> rows)
    {
        if (!_library.IsOpen || QueueRecordSelection.RecordOf(rows) is not { } record)
            return null;

        // Every row of the record is needed, not merely rows from it. Ticking six of a twelve-track album
        // would leave six tracks unseen, and the answer would be written over them.
        if (QueueGroupCount(record.Artist, record.Album) != rows.Count)
            return null;

        var said = QueueAlbumIntentSharedByRows(rows);
        return new QueueRecordSelection(said?.DiscArt, said is { SeriesSet: true } ? said.Series : null);
    }

    /// <summary>What these rows' record says about itself, when all the rows agree. A window asks this
    /// before it SHOWS one of these answers, rather than writing one. See <see
    /// cref="LibraryDatabase.QueueAlbumIntentSharedBy"/>.</summary>
    public QueueAlbumIntent? QueueAlbumIntentSharedByRows(IReadOnlyList<TrackRow> rows)
        => _library.IsOpen && rows.Count > 0
            ? _library.Db.QueueAlbumIntentSharedBy([.. rows.Select(r => r.QueueId)])
            : null;

    /// <summary>Reads the disc numbers of a record off the folders that its files came out of, and writes
    /// them onto the rows. The queue cannot do this repair for itself. A rip that never wrote DISCNUMBER
    /// looks exactly like a single-disc album, until somebody looks at the paths. Everything downstream is
    /// built on that field: the shelf's disc sections, the file names, and what counts as a duplicate.
    ///
    /// <para>Returns how many rows were given a disc. Zero is a real answer. If the folders say nothing,
    /// the discs must not be invented from repeated track numbers. Doing that would turn a record imported
    /// twice into a box set. See <see cref="DiscInference"/>.</para>
    /// </summary>
    public int NumberDiscsFromFolders(string artist, string? album)
    {
        if (!_library.IsOpen)
            return 0;

        var rows = Queue.RowsInGroup(artist, album);
        if (rows.Count == 0)
            return 0;

        // Inference works on the file's own path, which is the evidence. The wanted side is what it writes
        // to, because the file on disk still says nothing.
        var before = rows.Select(r => r.Wanted with { FilePath = r.Path }).ToList();
        var after = DiscInference.Fill(before);

        var numbered = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (after[i].DiscNumber == before[i].DiscNumber && after[i].DiscCount == before[i].DiscCount)
                continue;
            _library.Db.QueueUpdateWanted(rows[i].Id, rows[i].IsChecked, after[i]);
            if (after[i].DiscNumber != before[i].DiscNumber)
                numbered++;
        }

        if (numbered > 0)
            _ = _queueSource.RebuildAsync(_queueFilter);
        return numbered;
    }

    /// <summary>The download itself, over a set of store rows, however they were chosen.
    ///
    /// <para>This is the only path in the app that downloads anything on the user's behalf. Everything else
    /// refuses to. A scan will not open a placeholder. So a queued placeholder has nothing but its name
    /// until somebody asks for it. Downloads are serial. The file being fetched is named in the status
    /// line. A hundred records is a long wait, and a silent wait looks the same as a hang.</para>
    ///
    /// <para>Each file that lands is re-queued through the ordinary scan path. It is not patched in place.
    /// It now has tags, a length, a size, a split probe and an availability. Reading it as if it had just
    /// been found is simpler. It is also the only version that cannot drift from what a real scan would
    /// have produced. The tick is kept. Nothing else is lost, because nothing was ever read off the
    /// placeholder to edit.</para>
    /// </summary>
    private async Task DownloadQueueTracksAsync(IReadOnlyList<QueueTrack> queued)
    {
        if (queued.Count == 0)
            return;

        _scanning = true;
        _scanProcessed = 0;
        _scanTotal = queued.Count;
        RaiseQueueChrome();
        RaiseScanProgress();
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                _scanProcessed = p.Done;
                _scanTotal = p.Total;
                RaiseScanProgress();
                if (p.File.Length > 0)
                    SetStatus(Loc.Format("Queue_Downloading", p.Done + 1, p.Total, p.File));
            });

            var paths = queued.Select(q => q.Path).ToList();
            var results = await Task.Run(() => RPM.Core.Files.CloudHydrator.Download(paths, progress));

            var landed = results.Where(r => r.Downloaded).Select(r => r.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            SetStatus(Loc.Format("Queue_DownloadDone", landed.Count, results.Count));

            if (landed.Count > 0)
                await Task.Run(() => ReQueueDownloaded(queued.Where(q => landed.Contains(q.Path)).ToList()));
        }
        finally
        {
            _scanning = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            await _queueSource.RebuildAsync(_queueFilter);
            RaiseQueueChrome();
            RaiseScanProgress();
            RaiseRecognizeState();
        }
    }

    /// <summary>Swaps the placeholder rows for freshly read ones, and keeps their ticks. It works one
    /// source at a time, because the tree folder that a row gets is relative to the source it came in
    /// under.</summary>
    private void ReQueueDownloaded(IReadOnlyList<QueueTrack> fetched)
    {
        _library.Db.QueueRemove(fetched.Select(q => q.Id).ToList());

        foreach (var group in fetched.GroupBy(q => q.Source, StringComparer.OrdinalIgnoreCase))
        {
            var ticked = group.Where(q => q.IsChecked)
                .Select(q => q.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tracks = group.Select(q => TagReader.Read(q.Path)).ToList();
            var rebuilt = BuildQueueTracks(tracks, group.Key)
                .Select(q => q with { IsChecked = ticked.Contains(q.Path) })
                .ToList();
            _library.Db.QueueInsertMany(rebuilt);
        }
    }

    /// <summary>The file's size, or 0 when the file cannot be found. The gallery seeds rows whose paths
    /// never existed. A missing file is no reason for a scan to stop.</summary>
    private static long FileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Puts ready-made tracks straight into the queue, with no disk scan, and ticks them all. The
    /// gallery uses it to show the recognize dialog with a real count. <paramref name="source"/> is only
    /// the label of the tree root. The tracks' paths need not exist, as long as nothing imports or plays
    /// them.
    /// </summary>
    public async Task SeedAndCheckAllAsync(IEnumerable<TrackInfo> tracks, string source)
    {
        var list = tracks as IReadOnlyList<TrackInfo> ?? tracks.ToList();
        await _queueSource.InsertAsync(BuildQueueTracks(list, source));
        if (_library.IsOpen)
            Queue.CheckAll(true);
        await _queueSource.RebuildAsync(_queueFilter);
        RaiseQueueChrome();
        RaiseRecognizeState();
    }
}

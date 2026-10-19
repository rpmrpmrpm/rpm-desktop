using RPM.Core.Diagnostics;
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

/// <summary>How queued files land in the library on import.</summary>
public enum ImportMode
{
    /// <summary>Move out of the source. This is the default, and the source folder empties out.</summary>
    Move,
    /// <summary>Copy, leaving the source untouched. Used for read-only media and borrowed sticks.</summary>
    Copy,
}
/// <summary>What one track's recognition did, and separately, what it cost.</summary>
/// <param name="Source">The layer that answered: "own-db", "acoustid" or "audd". Null on a miss.</param>
/// <param name="Error">Why this track failed, as the dialog says it. Null when it did not fail. The
/// exception itself is kept in the fault log under "recognize".</param>
/// <param name="AudD">What AudD did for this track. The report's AudD line counts these. There is no
/// default. Whether a track reached AudD is decided where the lookup is made, at every place where an
/// outcome is built.</param>
public sealed record RecognitionOutcome(
    string FileName,
    bool Matched,
    string? Title,
    string? Artist,
    string? Source,
    double Score,
    Fault? Error,
    RPM.Core.Identify.AudDOutcome AudD)
{
    /// <summary>The queue row that this answer is about. The report is not a receipt to be read and closed.
    /// It is where a wrong answer gets corrected. So every line must be able to hand its row to the
    /// ordinary editor.</summary>
    public TrackRow? Row { get; init; }

    /// <summary>What the row said before the run. A name that changed is the main thing the user came to
    /// see. "was: nothing" is as much an answer as "was: Track 04".</summary>
    public string? PreviousArtist { get; init; }
    public string? PreviousTitle { get; init; }

    /// <summary>The record that the answer put the track on, when it named one.</summary>
    public string? Album { get; init; }
}

/// <summary>Where a run has got to. The dialog uses this for its progress line.</summary>
public sealed record RecognitionProgress(int Done, int Total, string CurrentFile);

/// <summary>The whole run, as the dialog reports it back. Fatal is set when the run stopped instead of
/// finishing. That happens when the credits run out, when the service is unavailable, or when the run is
/// cancelled. A count of zero with no explanation beside it makes the feature look broken.
/// </summary>
public sealed record RecognitionReport(
    IReadOnlyList<RecognitionOutcome> Outcomes,
    int Attempted,
    string? Fatal)
{
    public static readonly RecognitionReport Empty = new([], 0, null);

    /// <summary>What the run did that the counts do not say. An example is the advanced layer being taken
    /// away halfway. This is not Fatal. The run finished, but it finished shallower than it
    /// started.</summary>
    public string? Note { get; init; }

    /// <summary>How many tracks AudD was asked about. The report's AudD line is shown only when this is
    /// more than zero. A supporter's run that the free layers answered in full sends nothing to
    /// AudD.</summary>
    public int SentToAudD => Outcomes.Count(o => o.AudD is not RPM.Core.Identify.AudDOutcome.NotAsked);

    /// <summary>How many of those AudD named. The server keeps these, so the rpm database answers them next
    /// time. The rest go to AudD again.</summary>
    public int IdentifiedByAudD => Outcomes.Count(o => o.AudD is RPM.Core.Identify.AudDOutcome.Identified);

    public int Recognized => Outcomes.Count(o => o.Matched);
    public int Failed => Outcomes.Count(o => o.Error is not null);
    public string Summary => Fatal
        ?? Strings.Loc.Format("RecogResult_Recognized", Recognized, Attempted);
}


/// <summary>The app's main page. It shows the user's central music folder on the right. That folder is
/// backed by the SQLite mirror, so search stays fast with thousands of tracks. The import queue is on the
/// left, and it is visible only while there is queued work. "Add music" scans any source folder into the
/// queue. Queued tracks are recognised, split and edited in the same way as in the review workbench. Then
/// "Import" files the recognised ones into the library by the arrangement template, and updates the mirror
/// in the same pass.
/// </summary>
[RpmBindable]
public sealed partial class MainModel : INotifyPropertyChanged, IDisposable
{
    private readonly IdentificationProvider _identification;

    /// <summary>The account's supporter standing, for what the split window offers. See <see
    /// cref="CreateSplitAnalyzer"/>.</summary>
    private readonly RPM.Desktop.Services.Account.SupporterService _supporter;
    private readonly PlaybackService _playback;
    private readonly RPaMpService _rpamp;
    private readonly DispatcherQueue _dispatcher;
    private readonly LibraryService _library;
    private readonly InboxWatchService _inbox;

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer? _queueSearchDebounce;

    private readonly QueueTreeSource _queueSource;
    private readonly LibraryAlbumIndex _libraryAlbums;
    private readonly LibraryTableSource _libraryTable;

    private bool _recognizing;
    private bool _scanning;
    private int _scanProcessed;
    private int _scanTotal;
    private bool _importing;
    private int _importDone;
    private int _importTotal;
    private string _status = "";
    private IReadOnlyList<LibraryTableRow> _selectedLibraryRows = Array.Empty<LibraryTableRow>();

    public MainModel(
        IdentificationProvider identification,
        PlaybackService playback,
        RPaMpService rpamp,
        DispatcherQueue dispatcher,
        LibraryService library,
        InboxWatchService inbox,
        CatalogService catalog,
        SettingsService settings,
        RPM.Desktop.Services.Account.SupporterService supporter)
    {
        _identification = identification;
        _supporter = supporter;
        _playback = playback;
        _rpamp = rpamp;
        _dispatcher = dispatcher;
        _library = library;
        _inbox = inbox;
        _settings = settings;
        Catalog = catalog;

        _libraryAlbums = new LibraryAlbumIndex(library);
        _libraryTable = new LibraryTableSource(library, playback);
        _queueSource = new QueueTreeSource(library, playback, dispatcher,
            unknown: (Loc.Get("Queue_UnknownArtist"), Loc.Get("Queue_UnknownAlbum")));
        // Any store change (scan insert, edit write-back, tick, remove) refreshes the SQL-computed counts.
        // A write raises Changed, and so does every replacement of the rows on display.
        _queueSource.Changed += RaiseQueueChromeSoon;

        // Search coalescing. Each keystroke re-runs the queue's SQL skeleton off the UI thread. The timer
        // debounces this, so a fast typist rebuilds once, and not once per letter.
        _queueSearchDebounce = dispatcher.CreateTimer();
        _queueSearchDebounce.Interval = TimeSpan.FromMilliseconds(200);
        _queueSearchDebounce.IsRepeating = false;
        // Searching changes what is SHOWN, never what anything is. So the duplicate pass is skipped. It is
        // the expensive half of a rebuild, and it answers a question about the data, not about the view.
        _queueSearchDebounce.Tick += (_, _) =>
        {
            DropHiddenTicks();
            _ = _queueSource.RebuildAsync(_queueFilter, refreshDuplicates: false);
        };

        _playback.Changed += OnPlaybackChanged;
        _identification.AvailabilityChanged += OnIdentificationAvailabilityChanged;
        _library.Changed += OnLibraryChanged;
        _library.Opened += OnLibraryOpened;
        // Music dropped into the library's inbox while the app is running goes straight into the queue.
        _inbox.Arrived += OnInboxArrived;

        ImportCommand = new RelayCommand(async () => await ImportAsync());
        // Grid play routes into RPaMp. It becomes the now-playing track there, replacing the current one.
        // Clicking the same playing row again stops it. RPaMp shares this PlaybackService singleton, so the
        // row's play or stop glyph still reflects the true state.
        PlayCommand = new RelayCommand(p =>
        {
            // A queue node and a library table row are both "a track in a grid" to this command. What they
            // share is the editable row underneath, which is what playback speaks in.
            var row = p switch
            {
                ImportNode { Row: { } node } => node,
                LibraryTableRow table => table.Row,
                _ => null,
            };
            if (row is null)
                return;
            if (_playback.IsPlaying(row.FilePath))
                _playback.Stop();
            else
                _rpamp.PlayAlbumFrom(row.ToWantedTrackInfo());
        });

        RebuildLibraryTree(Rebuilt.LibraryChanged);

        // Reconcile the mirror with whatever changed on disk since the last run. Then pick up whatever is
        // waiting in the inbox. This runs in the background. The grid refreshes through Changed when the
        // pass changed anything.
        _ = StartupLibraryPassAsync();

#if DEBUG
        // Dev shortcut: `dotnet run -- --import <path>` queues a source folder right away. This lets the
        // whole queue, recognize and import flow be run without manual clicking.
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--import");
        if (idx >= 0 && idx + 1 < args.Length && Directory.Exists(args[idx + 1]))
        {
            var path = args[idx + 1];
            Services.Shell.Ticker.Once(dispatcher, TimeSpan.FromMilliseconds(1200),
                () => _ = AddSourceAsync(path));
        }

        // Gallery: show the import queue filled with demo rows. This runs after the page has settled and
        // wired its listeners. A queue raised before that leaves the column collapsed. No disk and no scan
        // are used.
        if (GalleryMode.Active)
        {
            Services.Shell.Ticker.Once(dispatcher, TimeSpan.FromMilliseconds(400),
                () => _ = SeedAndCheckAllAsync(Gallery.GalleryDemo.QueueTracks(), "/Demo/Inbox"));
        }
#endif
    }

    // ── queue state ──

    /// <summary>The queue's virtualised, DB-backed tree. The folder skeleton comes from a GROUP BY. The
    /// editable rows are paged from the temp queue table. So this scales to the 1M-track design target, as
    /// the library does. The grid reads it directly. There is no items source between them.</summary>
    public RPM.DataGrid.Tree.VirtualTreeModel QueueTree => _queueSource.Tree;

    /// <summary>Puts the queue rows that the caller is holding back in step with the store's ticks.
    ///
    /// <para>The view calls this with the rows that the GRID is showing. A tick written to a whole group is
    /// one statement, and it touches no row in memory. The tree's page cache is not a reliable stand-in for
    /// what is on screen. With a queue of thousands of small records, a page is often evicted while the
    /// rows built from it are still on display.</para>
    /// </summary>
    public void RereadQueueChecks(IEnumerable<object> rows) => _queueSource.RereadChecks(rows);

    /// <summary>The library DATA TABLE's source: one flat row per track, ordered and filtered by the
    /// database. The table is the view that a collection is checked in. The wall and the album list are the
    /// views that it is browsed in. Both are built off the same skeleton.</summary>
    public LibraryTableSource LibraryTable => _libraryTable;

    /// <summary>The library's albums, in grid order. This is what the album wall lays out. A folder is an
    /// album's identity. What it is CALLED is <see cref="LibraryAlbumHeadings"/>.</summary>
    public IReadOnlyList<string> LibraryAlbums => _libraryAlbums.Albums;

    /// <summary>What the wall knows about each album, by folder: its name, its artist, when it arrived, its
    /// year, and its plays. All of it comes from the mirror. See <see
    /// cref="LibraryAlbumIndex.AlbumFacts"/>.</summary>
    public IReadOnlyDictionary<string, RPM.Core.Cloud.AlbumFacts> LibraryAlbumFacts => _libraryAlbums.AlbumFacts;

    /// <summary>How many tracks each album folder holds. This is known from the skeleton. So a view that
    /// lists them can reserve their room before reading a single track off the disk.</summary>
    public IReadOnlyDictionary<string, int> LibraryAlbumTrackCounts => _libraryAlbums.TrackCounts;

    /// <summary>The lines that an album's tracklist holds beside its tracks: disc dividers and work
    /// headers, counted as track lines. This is part of the same reservation as the track counts. See <see
    /// cref="LibraryAlbumIndex.HeaderLines"/>.</summary>
    public IReadOnlyDictionary<string, int> LibraryAlbumHeaderLines => _libraryAlbums.HeaderLines;

    /// <summary>Each album's genre, where the record agrees with itself. The album list groups by this when
    /// it is asked to group by genre. It is absent for a record with nothing tagged.</summary>
    public IReadOnlyDictionary<string, string> LibraryAlbumGenres => _libraryAlbums.AlbumGenres;

    /// <summary>The album folders that hold at least one explicit track. These get the E beside a record's
    /// title. See <see cref="LibraryAlbumIndex.ExplicitAlbums"/>.</summary>
    public IReadOnlySet<string> LibraryExplicitAlbums => _libraryAlbums.ExplicitAlbums;

    /// <summary>How long each album folder plays for. This lets the subtitle say what a narrowed shelf adds
    /// up to, not what the whole collection adds up to.</summary>
    public IReadOnlyDictionary<string, double> LibraryAlbumSeconds => _libraryAlbums.FolderSeconds;

    /// <summary>The same collection, read by who made it: one entry per act. Each entry has the records
    /// that are theirs, the records that they only appear on, and what an artist's page captions each one
    /// with. The artists view is laid out from this. See <see cref="LibraryAlbumIndex.ArtistShelf"/>.
    ///
    /// <para>This is the whole collection, at every search. <see cref="SearchText"/> is the album views'
    /// field, and the artists wall has one of its own.</para></summary>
    public LibraryAlbumIndex.ArtistShelf ArtistShelf => _libraryAlbums.Artists;

    /// <summary>What the collection adds up to under the current search: albums, songs, and running time.
    /// This is the line under the library panel's heading.</summary>
    public string LibrarySubheading => Presentation.Common.PaneStats.Library(
        _libraryAlbums.Albums.Count, _libraryAlbums.TotalTracks, _libraryAlbums.TotalSeconds);

    /// <summary>Raised when the skeleton has been rebuilt, and the album order with it.</summary>
    public event Action? LibraryAlbumsChanged;

    /// <summary>Whether the album views' shelf was read from the collection that is open now. It is false
    /// before the first read, and after the collection is reopened or switched, until the rebuild
    /// lands.</summary>
    public bool AlbumsRead => _libraryAlbums.AlbumsRead;

    /// <summary>Whether <see cref="ArtistShelf"/> was read from the collection that is open now. See <see
    /// cref="AlbumsRead"/>.</summary>
    public bool ArtistsRead => _libraryAlbums.ArtistsRead;

    /// <summary>Raised on the UI thread when <see cref="AlbumsRead"/> or <see cref="ArtistsRead"/> may
    /// have changed. That happens when the library changes (which can be a different collection), and after
    /// every rebuild. It carries nothing. A listener asks the two properties.
    /// </summary>
    public event Action? ShelfReadingChanged;

    /// <summary>The queue panel exists only while there is queued work, or a scan that is filling it, or an
    /// import that is emptying it. Rows leave the queue only once their files have landed. So the pane must
    /// not flip to the empty invitation while an import is still running.</summary>
    public bool QueueVisible => _queueSource.TotalCount > 0 || _scanning || _importing;

    /// <summary>Whether the queue actually HOLDS anything. <see cref="QueueVisible"/> does not answer this.
    /// A scan that finds nothing keeps the panel up for its whole run, without ever putting a row in it.
    /// The left pane needs the difference to tell an import that is finishing from a scan that came back
    /// empty-handed. See PaneConductor.SyncQueue.</summary>
    public bool QueueHasRows => _queueSource.TotalCount > 0;

    public Visibility QueueVisibility => QueueVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The scan-in-progress overlay over the queue tree. It shows the magnifier
    /// animation.</summary>
    public Visibility ScanningVisibility => _scanning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the queue's ticks can be worked with at all. A scan is still writing the rows, and
    /// an import is taking them away. In both cases the grid is off screen. Ticking every row of a list
    /// that is half-built answers a question that nobody could have read.</summary>
    public bool CanTickQueue => !_scanning && !_importing && _library.IsOpen;

    /// <summary>The tree's own visibility, which is the exact opposite. Neither a scan nor an import is
    /// shown next to the grid. The animation has no background of its own. The rows it would sit on are
    /// half-written (for a scan), or are being filed away one by one (for an import). So the grid goes away
    /// for the duration, and is not covered.</summary>
    public Visibility QueueTreeVisibility =>
        _scanning || _importing ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The queue's legend and its action chin. These are hidden while an import runs. Both
    /// describe a grid that is not on screen, and every button on the chin is dead until the import is
    /// done.</summary>
    public Visibility QueueActionsVisibility => _importing ? Visibility.Collapsed : Visibility.Visible;

    // ── live scan progress, shown under the magnifier animation ──

    /// <summary>The running file count under the scan animation: "n / total files" once the enumeration has
    /// a total. Until then it shows the "scanning for audio files" line, because the walk reports no count
    /// until it is done.</summary>
    public string ScanCountText => _scanTotal > 0
        ? Loc.Format("Analyze_Progress", _scanProcessed, _scanTotal)
        : Loc.Get("Analyze_Scanning");

    /// <summary>The bar's maximum. It is never 0, so a determinate bar has a valid value before the first
    /// count.</summary>
    public double ScanProgressMax => Math.Max(1, _scanTotal);

    public double ScanProgressValue => _scanProcessed;

    /// <summary>No total yet (still enumerating), so the bar sweeps indeterminately instead of sitting
    /// empty. This is only WHILE scanning. An indeterminate ProgressBar runs a Forever storyboard. A
    /// running one keeps the Skia compositor at full frame rate, even when the whole overlay is collapsed.
    /// The same trap is described on <see cref="FolderProgressAnimation"/>, for its own magnifier
    /// storyboard.</summary>
    public bool ScanIsIndeterminate => _scanning && _scanTotal <= 0;

    private void RaiseScanProgress()
    {
        OnChanged(nameof(ScanCountText));
        OnChanged(nameof(ScanProgressMax));
        OnChanged(nameof(ScanProgressValue));
        OnChanged(nameof(ScanIsIndeterminate));
    }

    public RelayCommand ImportCommand { get; }
    public RelayCommand PlayCommand { get; }

    // ── search (indexed query on the library, in-memory filter on the queue) ──

    private string _searchText = "";

    /// <summary>The deck's search box. It searches the LIBRARY only. The import queue has its own field in
    /// its header (<see cref="QueueSearchText"/>).</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var v = value ?? "";
            if (v == _searchText) return;
            _searchText = v;
            // The library source cancels its own earlier rebuild. Only the albums are re-read, because a
            // search does not change who is in the collection.
            RebuildLibraryTree(Rebuilt.SearchMoved);
            OnChanged();
        }
    }

    /// <summary>The queue as the user is looking at it right now. This is the one door to every narrowable
    /// read and every tick (see <see cref="LibraryDatabase.QueueView"/>). It is built on each call from the
    /// live filter. So an action can never act through a narrowing that has since moved.
    /// </summary>
    private LibraryDatabase.QueueView Queue => _library.Db.Queue(_queueFilter);

    private string _queueSearchText = "";
    private QueueFilter _queueFilter = QueueFilter.None;

    /// <summary>What the import queue is narrowed to right now: the header's search box and every chip in
    /// the action bar, as one value.
    ///
    /// <para>It is held on the model, not in the bar. A rebuild is triggered from many places: a scan
    /// finishing, an edit re-grouping a row, or a switch of library. Each of those must put back what the
    /// user was looking at, and not the whole queue.</para>
    /// </summary>
    public QueueFilter QueueFilter
    {
        get => _queueFilter;
        set
        {
            var v = value ?? QueueFilter.None;
            if (v == _queueFilter) return;
            _queueFilter = v;
            _queueSearchText = v.Search ?? "";
            DropHiddenTicks();
            _ = _queueSource.RebuildAsync(_queueFilter);
            OnChanged();
        }
    }

    /// <summary>The import grid's own header search. It filters ONLY the queue. It is debounced through the
    /// same off-thread timer that the queue rebuild uses. So a fast typist does not re-run the SQL skeleton
    /// on every key.</summary>
    public string QueueSearchText
    {
        get => _queueSearchText;
        set
        {
            var v = value ?? "";
            if (v == _queueSearchText) return;
            _queueSearchText = v;
            // The search is one part of the narrowing, not a second one beside it. The chips stay on.
            _queueFilter = _queueFilter with { Search = v.Length == 0 ? null : v.Trim() };
            _queueSearchDebounce?.Start();
            OnChanged();
        }
    }

    // ── queue side (left) header: which folder(s) the queued rows came from ──

    /// <summary>The distinct source folders behind the rows that are queued now. Rows removed from the
    /// queue take their source with them. So this never names a folder that has left the queue.</summary>
    private IReadOnlyList<string> QueueSources() =>
        _library.IsOpen ? _library.Db.QueueDistinctSources() : [];

    /// <summary>The line under the import pane's heading.
    ///
    /// <para>Once anything is ticked, it says how much. That is the fact the user is acting on. It is the
    /// number that the Import button does not carry, and it changes as they work. Until then, it says
    /// where the queued rows came from. With one source, it shows the whole path. A folder's NAME is not
    /// enough to know which folder it is, and a ripped disc's name is a machine id. With more than one
    /// source, it shows a count. Three paths do not fit on one line, and no single one of them is the
    /// answer.</para>
    /// </summary>
    public string QueueFolderPath
    {
        get
        {
            var ticked = _queueSource.ImportableCount;
            if (ticked > 0)
                return ticked == 1
                    ? Loc.Get("Queue_SelectedForImportOne")
                    : Loc.Format("Queue_SelectedForImport", ticked);

            return QueueSources() switch
            {
                { Count: 0 } => "",
                { Count: 1 } one => one[0],
                var many => Loc.Format("Queue_SourceCount", many.Count),
            };
        }
    }

    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    // ── library side (right) ──

    public string LibraryFolderName => _library.Root is { } root ? FolderName(root) : "";

    public Visibility LibraryEmptyVisibility =>
        _library.IsOpen && _library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What a shelf rebuild was asked BY. This says how much of the collection has to be re-read.
    /// A search moves only what the album views are narrowed to. A library change moves the collection
    /// itself. The artists' side is read from the collection, and not from the search (see <see
    /// cref="LibraryAlbumIndex.ArtistShelf"/>).
    /// </summary>
    private enum Rebuilt { SearchMoved, LibraryChanged }

    /// <summary>The collection changed, so both indexes are read again.
    /// </summary>
    /// <remarks>
    /// <b>Not in front of the first screen.</b> This also fires when <c>LibraryService.Open</c> runs. That
    /// is the library opening at startup. What it starts is two whole reads of the collection: one for the
    /// album views and one for the artists view. Neither is on screen at that point, because the app opens
    /// on home, and home covers both panes. The library's reads are serialised. So those two reads would
    /// take the gate that home's own reading needs. Measured, home's listening profile waited about a
    /// second behind the artist credits query, and the reveal waits on home.
    ///
    /// <para>Every later change (an import, a sync, or a folder swap) arrives with the screen long up. <see
    /// cref="Services.Shell.AfterFirstScreen"/> runs the work then. The only change this makes is to the
    /// first one.</para>
    /// </remarks>
    private void OnLibraryChanged() =>
        _dispatcher.TryEnqueue(() =>
        {
            // Said now, not when the rebuild lands. A switched collection is unread from this moment, and
            // the screens put their loader up on it.
            ShelfReadingChanged?.Invoke();
            Services.Shell.AfterFirstScreen.Run(() => RebuildLibraryTree(Rebuilt.LibraryChanged));
        });

    private void RebuildLibraryTree(Rebuilt what)
    {
        // Nothing awaits this method. A throw inside it would leave a shelf that quietly keeps showing the
        // last collection, because the views are only told by the event at the end. So the failure is
        // reported here instead.
        _ = RebuildLibraryTreeAsync(what).ContinueWith(
            t => RPM.Core.Diagnostics.Faults.Note("library", "rebuild", t.Exception?.GetBaseException()),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task RebuildLibraryTreeAsync(Rebuilt what)
    {
        // Both passes start first and are awaited last. So they share the wait, and they do not queue
        // behind each other. They are awaited before the event is raised either way. The artists view and
        // the album views are told by that one event, and they must not be told about two different
        // collections.
        var artists = what is Rebuilt.LibraryChanged
            ? _libraryAlbums.RebuildArtistsAsync()
            : Task.CompletedTask;

        // The skeleton query (a GROUP BY over the mirror) runs off the UI thread inside. A newer rebuild
        // cancels this one. That happens on the next keystroke, or on the next library change.
        await _libraryAlbums.RebuildAsync(_searchText);
        await artists;

        // The table asks the database itself. Only the search needs to be passed. A rebuild with the same
        // search (a library change) still asks again, because the rows behind it may be different ones.
        _libraryTable.Refresh(_searchText);
        OnChanged(nameof(LibraryEmptyVisibility));
        // The album wall is titled with the library folder, so it follows a library open or switch.
        OnChanged(nameof(LibraryFolderName));
        LibraryAlbumsChanged?.Invoke();
        ShelfReadingChanged?.Invoke();
    }

    /// <summary>A different collection was opened while the app was running. This happens when the music
    /// folder is changed in settings.
    ///
    /// <para>It gets the same pass that a launch gets, because the situation is the same: a mirror that
    /// nobody has reconciled this session, with whatever is in its inbox waiting. This is not raised at
    /// startup for this model. The shell opens the library before it navigates here. So the event has
    /// already fired before the constructor subscribes. The constructor runs the pass itself.</para>
    ///
    /// <para>It arrives on whichever thread called Open. That is the interface thread for the settings
    /// card, and a worker thread in other cases. It is marshalled anyway, because the pass writes a status
    /// line.</para>
    /// </summary>
    private void OnLibraryOpened(string root) =>
        _dispatcher.TryEnqueue(() => _ = StartupLibraryPassAsync());

    /// <summary>The pass that the app makes over the collection when it comes up: it reconciles the mirror
    /// with the folder, and then it queues whatever is sitting in the inbox.
    ///
    /// <para>The order matters. The sync is what sweeps music that somebody copied straight into the
    /// collection into the inbox (see <see cref="RPM.Core.Library.LibrarySynchronizer"/>). If the inbox
    /// were scanned first, those files would be missed, and this sweep exists to catch them. Afterwards,
    /// one scan covers both those files and anything dropped in while the app was shut.</para>
    /// </summary>
    private async Task StartupLibraryPassAsync()
    {
        await SyncLibraryAsync();
        if (_inbox.InboxPath is { } path && _inbox.HasMusic())
            await AddSourceAsync(path);
    }

    /// <summary>Music has landed in the inbox while the app is running. It goes into the queue as an
    /// ordinary source folder. It takes the same path as a picked folder or a finished rip, because it is
    /// the same thing: files that are not in the collection yet.
    ///
    /// <para>A drop that lands during a scan or an import waits rather than being dropped. The queue is
    /// busy for seconds, not hours, and the files are not going anywhere. If the queue is still busy after
    /// half a minute, the next drop or the next launch picks them up.</para>
    /// </summary>
    private async void OnInboxArrived(string inboxPath)
    {
        for (var attempt = 0; (_scanning || _importing) && attempt < 60; attempt++)
            await Task.Delay(500);
        if (_scanning || _importing)
            return;
        await AddSourceAsync(inboxPath);
    }

    private async Task SyncLibraryAsync()
    {
        if (!_library.IsOpen)
            return;
        SyncResult? result = null;
        try
        {
            SetStatus(Loc.Get("Library_Syncing"));
            result = await _library.SyncAsync();
        }
        catch
        {
            // The mirror stays as it was. The next open retries.
        }
        finally
        {
            // Files that were swept into the inbox have moved out from under the user. They put an album
            // somewhere, and it is not there any more. Saying so is not optional. Saying WHERE is the main
            // part of the answer. The queue that is about to fill up is the rest of it.
            SetStatus(result is { Quarantined: > 0 } swept
                ? swept.Quarantined == 1
                    ? Loc.Format("Library_QuarantinedOne", LibraryInbox.FolderName)
                    : Loc.Format("Library_Quarantined", swept.Quarantined, LibraryInbox.FolderName)
                : "");
        }
    }

    // ── selection (pushed from the page: TableView.SelectedItems isn't bindable) ──

    /// <summary>A queued row has moved, and the grid should go to it. The value is the flat row index. This
    /// is raised after recognition re-files a track under the artist it turned out to be.</summary>
    public event Action<int>? RevealQueueRow;

    /// <summary>Library-table selection. It feeds the RPaMp context actions.</summary>
    public void SetLibrarySelection(IReadOnlyList<LibraryTableRow> rows)
        => _selectedLibraryRows = rows;

    /// <summary>The queue has no row selection. Its TICKS are the selection. So everything that acts on
    /// "selected queue rows" acts on the checked set.</summary>
    private List<TrackRow> SelectedQueueRows() => CheckedRows();

    public IReadOnlyList<TrackRow> SelectedQueueRowsSnapshot() => SelectedQueueRows();

    /// <summary>The selected library rows, as the editable rows that every action speaks in.</summary>
    public IReadOnlyList<TrackRow> SelectedLibraryRowsSnapshot()
        => LibraryRowsFor(_selectedLibraryRows);

    /// <summary>Turns an explicit set of table rows into editable track rows. This is used by a drag from
    /// the grid, where the dragged items are whatever the pointer picked up, and not the selection. The
    /// table has one row per file. So this only removes duplicates. There are no folders to expand any
    /// more.</summary>
    public IReadOnlyList<TrackRow> LibraryRowsFor(IEnumerable<LibraryTableRow> tableRows)
    {
        var rows = new List<TrackRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableRow in tableRows)
        {
            if (seen.Add(tableRow.FilePath))
                rows.Add(tableRow.Row);
        }
        return rows;
    }

    // ── the chin's batch actions ───────────────────────────────────────────────────────────────────

    /// <summary>Gives up the ticks that the narrowing has just hidden. So the ticked set is never anything
    /// but a subset of what is on screen.
    ///
    /// <para>This is the whole rule that the queue's selection runs on. "Select all" and "Select none" mean
    /// the rows in front of you. A tick that survived out of sight would make "none" wrong. It would also
    /// leave Import acting on rows that nobody can see. The cost is deliberate: when the queue is narrowed,
    /// the ticks outside the narrowing are removed. They are not just hidden.</para>
    /// </summary>
    private void DropHiddenTicks()
    {
        if (!_library.IsOpen)
            return;
        if (Queue.UncheckHidden() > 0)
            RaiseQueueChrome();
    }

    /// <summary>Ticks or unticks the whole queue. The store does it in one statement. The leaves are paged,
    /// so there is no in-memory list of rows to walk.</summary>
    public void SetAllQueueChecked(bool isChecked)
    {
        if (!_library.IsOpen)
            return;
        // What is on screen, not what is in the store. A press that ticked ten thousand rows behind a
        // search would make a selection that nobody can read, and Import would act on it.
        Queue.CheckAll(isChecked);
        // Updated in place, not re-read. The pages are the same pages holding the same rows. The only thing
        // that changed is a tick, and every row on the page can be told where it now stands.
        _queueSource.SetAllChecksInPlace(isChecked);
        RaiseQueueChrome();
        RaiseRecognizeState();
    }

    /// <summary>How many rows the clean-ups would take. This is read off the marks that the grid is
    /// showing. So the menu never offers to remove something that has no visible mark.</summary>
    public int QueueDuplicateCount => _library.IsOpen ? _library.Db.QueueDuplicateCount() : 0;
    public int QueueUnreadableCount => _library.IsOpen ? _library.Db.QueueUnreadableCount() : 0;

    /// <summary>Drops every lesser copy. One best version of each track survives, and so does every
    /// upgrade. Only queue rows are changed. Nothing on disk is touched.</summary>
    public async Task RemoveQueueDuplicatesAsync()
    {
        if (!_library.IsOpen)
            return;
        var removed = await Task.Run(() => _library.Db.QueueRemoveDuplicates());
        // There is no second duplicate pass. Only LESSER copies were removed, so every row that is still
        // here keeps the verdict it already had. Removing unreadable rows is different. One of those may
        // have been the best copy of something, and removing it would promote whatever it was beating.
        await _queueSource.RebuildAsync(_queueFilter, refreshDuplicates: false);
        SetStatus(removed == 1
            ? Loc.Get("Queue_RemovedDuplicateOne")
            : Loc.Format("Queue_RemovedDuplicates", removed));
        RaiseQueueChrome();
        RaiseRecognizeState();
    }

    /// <summary>Drops the rows that nothing could read. Cloud placeholders stay. They are good files that
    /// are only away, and there is an action for fetching them.</summary>
    public async Task RemoveQueueUnreadableAsync()
    {
        if (!_library.IsOpen)
            return;
        var removed = await Task.Run(() => _library.Db.QueueRemoveUnreadable());
        await _queueSource.RebuildAsync(_queueFilter);
        SetStatus(removed == 1
            ? Loc.Get("Queue_RemovedUnreadableOne")
            : Loc.Format("Queue_RemovedUnreadable", removed));
        RaiseQueueChrome();
        RaiseRecognizeState();
    }

    /// <summary>Takes rows out of the queue without importing them. This is a context action.</summary>
    public void RemoveFromQueue(IReadOnlyList<TrackRow> rows)
    {
        _ = _queueSource.RemoveAsync(rows.Select(r => r.QueueId).Where(id => id != 0).ToList());
    }

    /// <summary>Writes edited rows back, and shows the result.
    ///
    /// <para>The tree is rebuilt only when something actually MOVED. That means a row was filed under a
    /// different artist or record, or it is now a copy of something else. That is the store's judgement,
    /// and not a guess from the field names (see <see cref="LibraryDatabase.QueueUpdateWanted"/>).
    /// Everything else, such as a year, a genre, or a track number written across forty rows, re-reads the
    /// pages that are on screen and leaves the tree standing. So the reader keeps their place, their open
    /// folders, and the row they were looking at.</para>
    ///
    /// <para>The ticked set is materialised from the store. So those rows have no live write-back behind
    /// them. Recognition already does this for each row. The context-menu editors go through here.</para>
    /// </summary>
    public void PersistQueueRows(IEnumerable<TrackRow> rows)
    {
        var edited = rows as IReadOnlyList<TrackRow> ?? rows.ToList();

        // The one question worth asking after an edit is whether anything MOVED. A row whose group, disc or
        // title changed belongs somewhere else, and the skeleton has to be built again. Everything else (a
        // genre, a comment, a year, a composer, four hundred of them at once) changes what a cell reads,
        // and nothing about where it is. The reader keeps their place.
        if (_queueSource.WriteBackMany(edited))
            _ = _queueSource.RebuildAsync(_queueFilter);
        else
            _queueSource.ApplyEditsInPlace(edited);
    }

    // ── playback ──
    // RPaMp is the player. The per-row play and stop buttons route into it, so the rows show the true
    // playing state.

    /// <summary>The playing track moved, so the mark has to move with it.
    ///
    /// <para>Only the rows that the grid is DRAWING are told. They are told through <see
    /// cref="PlayingMoved"/>, which asks the panel for its own children. The sources' page caches are the
    /// wrong set, for two reasons. First, they miss the rows on screen. A page that is evicted and fetched
    /// again builds new row objects, while the container keeps showing the old ones. So a change raised on
    /// the cache lands on an object that no binding is attached to. Second, the cache is very large. The
    /// grid answers EVERY raise with a walk of the whole viewport (see
    /// <c>TableView.OnItemPropertyChanged</c>). So raising on the cache costs cached rows times rows on
    /// screen. On the WinUI head, with 9,947 cached library rows and one of them playing, the UI thread was
    /// held for 3.4 seconds for every track started.</para>
    ///
    /// <para>A row that is not drawn needs no telling. <c>IsPlaying</c> is read off the player when the row
    /// is bound to a container. So a row that is scrolled back into view arrives correct, whether or not it
    /// heard about the change while it was away.</para>
    /// </summary>
    private void OnPlaybackChanged() => _dispatcher.TryEnqueue(() => PlayingMoved?.Invoke());

    /// <summary>Raised on the UI thread after the playing track moved. It is for a view that has to tell
    /// the rows it is drawing, and not the rows that the source happens to be holding.</summary>
    public event Action? PlayingMoved;

    // ── trees ──

    /// <summary>Whether a chrome pass is already waiting its turn. This is an int, not a bool, because
    /// Changed is not guaranteed to arrive on the UI thread.
    /// </summary>
    private int _queueChromePending;

    /// <summary>Set when a pass woke up, found the chrome held, and went away without reading the
    /// counts.</summary>
    private bool _queueChromeMissed;

    /// <summary>Asked when a chrome pass wakes up: is the user still in the middle of a gesture that is
    /// going to ask again the moment it ends? The view that owns the queue's grid sets this. Null means
    /// nothing is holding it.
    ///
    /// <para>This is a predicate, not a flag that somebody sets and clears. The answer is wanted at the
    /// moment the pass runs, and not at the moment the change arrived. A token taken at the start of a drag
    /// would also leak if the drag ended in a way its owner did not expect.</para>
    /// </summary>
    public Func<bool>? QueueChromeHeld { get; set; }

    /// <summary>Asks for ONE chrome pass on a later dispatcher turn, however many changes ask for it.
    ///
    /// <para>The counts behind the chrome are SQL over the whole queue, and there are several of them. <see
    /// cref="QueueChipCounts"/> alone is nine aggregates over every row. CanImport and QueueFolderPath each
    /// count the importable rows, and both are raised twice per pass. So one pass over a queue of twenty
    /// thousand rows costs a handful of full-table scans.</para>
    ///
    /// <para><c>Changed</c> is raised once for each row written. So a drag asked for one of these for each
    /// row it crossed. Coalescing to one per dispatcher turn was not enough on its own. A turn is a frame,
    /// so a drag still paid for a pass sixty times a second. Hence the hold. During a drag, the answer that
    /// nobody can read is not read at all. <see cref="RaiseHeldQueueChrome"/> pays for exactly one pass
    /// when the pointer comes up.</para>
    ///
    /// <para>The latch is cleared BEFORE the pass runs, not after. A change that lands while the counts are
    /// being read happened after they were read, so it must be able to ask for another pass. Clearing
    /// afterwards would drop it.</para>
    /// </summary>
    private void RaiseQueueChromeSoon()
    {
        if (Interlocked.Exchange(ref _queueChromePending, 1) == 1)
            return;

        _dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queueChromePending, 0);

            if (QueueChromeHeld?.Invoke() == true)
            {
                _queueChromeMissed = true;
                return;
            }

            _queueChromeMissed = false;
            RaiseQueueChrome();
            RaiseRecognizeState();
        });
    }

    /// <summary>Runs the pass that a gesture held back, if one was held back. This is called when the
    /// gesture ends.
    ///
    /// <para>It does nothing when nothing was missed. So calling it after a gesture that changed no ticks
    /// costs nothing. Nothing gets stuck if it is never called. The next change to the queue asks for a
    /// pass of its own, and by then the hold has been released.</para>
    /// </summary>
    public void RaiseHeldQueueChrome()
    {
        if (!_queueChromeMissed)
            return;

        _queueChromeMissed = false;
        RaiseQueueChrome();
        RaiseRecognizeState();
    }

    /// <summary>Raises the queue's header, button and visibility state. The tree itself is the DB-backed
    /// <see cref="QueueSource"/>, which refreshes on its own. This only re-reads the SQL-computed
    /// chrome.</summary>
    private void RaiseQueueChrome()
    {
        OnChanged(nameof(QueueVisible));
        OnChanged(nameof(QueueVisibility));
        OnChanged(nameof(ScanningVisibility));
        OnChanged(nameof(ImportingVisibility));
        OnChanged(nameof(QueueTreeVisibility));
        OnChanged(nameof(QueueActionsVisibility));
        OnChanged(nameof(QueueFolderPath));
        OnChanged(nameof(CanAddMusic));
        OnChanged(nameof(CanTickQueue));
        OnChanged(nameof(NeedsRecognitionText));
        OnChanged(nameof(CanImport));
        OnChanged(nameof(QueueChipCounts));
    }

    /// <summary>How much of the queue each of the bar's chips is about, in one read. The count is of the
    /// WHOLE queue, and not of what the chips have already narrowed it to.</summary>
    public QueueChipCounts QueueChipCounts =>
        _library.IsOpen ? _library.Db.QueueChipCounts() : default;

    /// <summary>What the ticked rows are made of, for the toolbar's keys: how many there are, and how many
    /// of them are in the cloud rather than on this disk.</summary>
    public (int Ticked, int Dataless) QueueTickedFacts =>
        _library.IsOpen ? _library.Db.QueueTickedFacts() : (0, 0);

    /// <summary>The sources behind the queued rows, for the bar's source list.</summary>
    public IReadOnlyList<string> QueueSourceNames =>
        _library.IsOpen ? _library.Db.QueueDistinctSources() : [];

    /// <summary>The file kinds in the queue, for the bar's kind list.</summary>
    public IReadOnlyList<string> QueueKinds =>
        _library.IsOpen ? _library.Db.QueueDistinctExtensions() : [];


}

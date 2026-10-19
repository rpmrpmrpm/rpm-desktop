using RPM.Core.Threading;
using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The window where a playlist gets a picture. The choices are four of its own records, a file from the
/// user's disk, or one of eight generated pictures.
///
/// <para>It returns <see cref="Chosen"/> and <see cref="Rendered"/>. The picture is composed once, here,
/// from the previews that are already loaded. The caller writes the master and the note. This window
/// never reads the disk. An existing custom picture arrives as <c>currentPicture</c>. It is not fetched
/// here.</para>
///
/// <para><b>What runs on its own timing here, and why each part is safe.</b> All the mutable state in
/// this window (<c>_allRecords</c>, <c>_figures</c>, <c>_picked</c>, <c>_recordSleeves</c>,
/// <c>_figure</c>, <c>_picture</c>, <c>_previewDrawn</c> and the rest) is read and written only on the UI
/// thread. Background work computes a pure result. It hands that result back either through
/// <c>_dispatcher.TryEnqueue</c> (the lane fills may not resume on the UI thread), or directly, for a
/// chain of plain <c>await</c>s that started in a UI event handler. Those continuations resume on the UI
/// thread. This one rule keeps the list below short.</para>
/// <list type="number">
/// <item><description><b>The records gather</b> (<c>FillRecordsAsync</c>). It flattens and groups the
/// tracks off the UI thread. It runs once, from <c>OnOpened</c>. Nothing else fills
/// <c>_allRecords</c>, so no older gather can race a newer one. <c>_allRecords</c> is an
/// <c>ObservableCollection</c> that is bound to the grid once. It must not be a plain <c>List</c> that is
/// assigned to <c>ItemsSource</c> again after it is filled. That assignment does nothing, because the
/// dependency property ignores a reference it already has, so the grid never updates.
/// Safe.</description></item>
/// <item><description><b>The record sleeves</b> (<c>FillRecordThumbsAsync</c>). Each lane reads the disk
/// for its own rows. It writes only to that row's <c>Thumb</c> or <c>Unavailable</c>, and to
/// <c>_recordSleeves</c> under the row's folder. All of these writes go through the dispatcher. This is
/// safe because the records tab is filled exactly once, so no older fill can write to a row that a newer
/// fill has replaced. A row with no artwork is marked <c>Unavailable</c>. It is not left loading. A tap
/// cannot add an unavailable or still-loading row to the pick (see
/// <c>RecordsGrid_ItemClick</c>). So <c>_recordSleeves</c> holds a sleeve for every ticked folder at the
/// moment it is ticked. One case is the exception: a folder picked in advance from a stored
/// <see cref="PlaylistCover.Records"/> choice, ticked before any lane has run. Save handles that case
/// (see below).</description></item>
/// <item><description><b>The generated tiles</b> (<c>FillFigureThumbsAsync</c>), lane-parallel Skia
/// composites. Unlike the records tab, this fill can run twice. The colours button calls
/// <c>FillFigures</c> again while an earlier fill may still be drawing. Two guards apply. The earlier
/// fill's <c>CancellationTokenSource</c> is cancelled before the new fill is built. A lane that gets past
/// that check still writes to a <see cref="CoverChoiceRow"/> that <c>FillFigures</c> has already removed
/// from <c>_figures</c>, so nobody can see the write. Safe on both counts.</description></item>
/// <item><description><b>The live preview</b> (<c>RepaintPreview</c>/<c>RepaintPreviewAsync</c>). It runs
/// on a tap on either grid, on a tab switch, and when a record sleeve arrives late for a ticked folder.
/// All of these use one generation counter. The counter is checked against <c>_closing</c> and against
/// itself at two points: before the decode and after it. So only the newest repaint is shown when its
/// result arrives. A finished repaint also caches its <see cref="PlaylistArt.Drawn"/> under the exact
/// <see cref="PlaylistCover"/> it was made for (by wire value, via <c>ToWire()</c>), and under the
/// generation that made it. <c>Save</c> reuses that cache instead of composing the same picture again,
/// but only while the cache's generation is still the newest. A tap does not wait for an earlier repaint.
/// The newer request simply makes the older result stale. Safe.</description></item>
/// <item><description><b>Choosing a file</b> (<c>ChooseFile_Click</c>). The read, the crop and the decode
/// all run off the UI thread. The generation is taken BEFORE the read starts, not after it finishes. A
/// tap on either grid during the read must win. So a tap must get a newer generation than the file pick.
/// If the file took its generation late, the file would always win, whatever the user did during the
/// read. It is checked against <c>_closing</c> and against itself at two points, the same way the live
/// preview is checked.</description></item>
/// <item><description><b>Save</b> (<c>Save_Click</c>/<c>RenderedForAsync</c>). Composing the Skia picture
/// can take time, so Save is async. A cache hit returns at once. A miss is composed off the UI thread.
/// For a Records choice, any picked folder whose sleeve is not yet in <c>_recordSleeves</c> (the
/// prefilled case above) is fetched first, also off the thread. This can overlap with a running
/// <c>FillRecordThumbsAsync</c> without conflict. Both only write to <c>_recordSleeves</c> from
/// continuations on the UI thread, so the writes never run at the same moment, even though the two jobs
/// overlap in time. <see cref="Chosen"/> and <see cref="Rendered"/> are set together, only AFTER the
/// await, and only when <c>_closing</c> is still clear. Close, Automatic and the window's own Escape key
/// all stay usable during the wait. The footer is disabled, but Escape does not go through a button. If
/// the window is dismissed during the wait, no choice is made. Otherwise a new
/// <c>Chosen</c> could be paired with the old <c>Rendered</c>.</description></item>
/// <item><description><b>The user, acting between any two of the above</b>, and <b>the window closing
/// while something is still in flight.</b> Every background loop uses a token linked to
/// <c>_closing</c>. The figures use <c>_figuresCts</c>, which is linked to <c>_closing</c>. Every
/// dispatcher callback and every plain <c>await</c> chain checks its token before it touches anything.
/// <c>RenderedForAsync</c> also catches whatever a cancelled read throws. It runs from
/// <c>async void Save_Click</c>, and an exception that escapes an <c>async void</c> method crashes the
/// app. It is not returned as an error.</description></item>
/// </list>
/// <para>Not verified: a second click on <c>ChooseFileButton</c> while the first pick is still waiting in
/// <c>PickSingleFileAsync</c>. The button is not disabled during that wait. This relies on the native
/// picker being exclusive to its window. Nothing in this file checks that.</para>
/// </summary>
public sealed partial class PlaylistCoverDialog : ContentDialog
{
    private const int PreviewEdge = 220;
    private const int PicturePreviewEdge = 120;

    /// <summary>The size a mood tile aims for, the gap between two tiles, and the smallest size a tile
    /// may shrink to. The column count is chosen against the target (see <see cref="LayOutTiles"/>). The
    /// gap is the item container's own margin. It is part of the spacing whether or not the layout
    /// arithmetic includes it.</summary>
    private const double TileTarget = 150;
    private const double TileGap = 10;
    private const double TileFloor = 56;

    /// <summary>The size a record sleeve aims for on the records tab. It is smaller than a mood tile.
    /// Sleeves are for recognising among many records, not for a close look.</summary>
    private const double SleeveTarget = 104;

    /// <summary>How many folder reads or tile composites run at once. Six. <c>CoverArtSearchDialog</c>
    /// uses the same number for the same reason: enough to fill what is on screen quickly, without
    /// starting more disk or CPU work than one window should use.</summary>
    private const int Lanes = 6;

    private readonly long _nodeId;
    private readonly string _name;
    private readonly bool _isFolder;
    private readonly PlaylistCover _current;

    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _closing = new();

    private readonly RailTabStrip _tabs;
    private readonly PictureWell _preview = new(PreviewEdge);
    private readonly PictureWell _picturePreview = new(PicturePreviewEdge);

    private readonly ObservableCollection<CoverChoiceRow> _allRecords = [];
    private readonly ObservableCollection<CoverChoiceRow> _figures = [];
    private readonly List<string> _picked = [];
    private readonly Dictionary<string, byte[]> _recordSleeves = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _figuresCts;
    private long _previewGen;
    private bool _saving;

    /// <summary>Whether the advisory mark is on. It applies to every tile on the moods grid and to
    /// whatever is saved from it. There is one state for the whole tab, because the mark is a fact about
    /// the playlist's music. A grid where only some tiles carried the mark would ask the same question
    /// several times over.</summary>
    private CoverMark _mark = CoverMark.None;

    /// <summary>The size the mood tiles are laid out at right now, from the panel's width. It is kept
    /// here as well as on each row. A fill that runs between two resizes then builds its rows at the size
    /// the grid is showing, not at the row's default size and then again a frame later.</summary>
    private double _tileEdge = TileTarget;

    /// <summary>The same for the records grid. See <see cref="LayOutRecords"/>.</summary>
    private double _sleeveEdge = SleeveTarget;

    private byte[]? _picture;
    private ImageSource? _pictureShown;
    private CoverChoiceRow? _figure;

    /// <summary>The last finished live preview. It holds the wire-form key of the
    /// <see cref="PlaylistCover"/> it was made for, and the generation that made it. <c>Save</c> reuses
    /// it instead of asking Skia to compose the same picture again. See
    /// <see cref="RenderedForAsync"/>.</summary>
    private string? _previewDrawnKey;
    private long _previewDrawnGen = -1;
    private PlaylistArt.Drawn _previewDrawn = PlaylistArt.Drawn.None;

    /// <summary>Which colourway the eight tiles use, and the arrangement inside each. It is one number,
    /// because the seed decides both.
    ///
    /// <para>The seed comes from the NAME. It is set in the constructor, not at the declaration. A field
    /// initializer runs before the constructor body sets <c>_name</c>, so reading the name there would
    /// read a value that is not set yet. The seed comes from the name so that the same playlist always shows the same
    /// eight. A set that changed colours on every open could never be found again.</para>
    ///
    /// <para>If the playlist already has a generated cover, the constructor replaces this with that
    /// cover's seed (see below). The grid then opens in the colours the playlist is wearing. That is the
    /// colourway its owner most needs to see first.</para></summary>
    private uint _roll;

    /// <summary>What the user chose, or null when the window closed without a choice.</summary>
    public PlaylistCover? Chosen { get; private set; }

    /// <summary>The picture for <see cref="Chosen"/>. It is composed once, here, from the previews that
    /// are already loaded. For an unchanged <see cref="PlaylistCover.Picture"/>, it is the bytes the
    /// caller passed in when the window was made. <see cref="PlaylistArt.Drawn.None"/> means only one
    /// thing: there is nothing to write. Either <see cref="Chosen"/> is
    /// <see cref="PlaylistCover.Automatic"/> and the caller should delete the master, or a Records or
    /// Figure picture could not be made.</summary>
    public PlaylistArt.Drawn Rendered { get; private set; } = PlaylistArt.Drawn.None;

    /// <param name="currentPicture">The bytes of the playlist's existing custom picture. The caller reads
    /// them from wherever it keeps its masters. This is null unless <paramref name="current"/> is a
    /// <see cref="PlaylistCover.Picture"/>. It is passed in, not defaulted. The
    /// <see cref="PlaylistCover.Picture"/> itself does not carry its bytes, because the file is the
    /// choice. Without this parameter the Picture tab would open on a picture nobody can see, and Save
    /// without touching that tab would have nothing to return. This window does not read the disk. It
    /// shows only the bytes it is given.</param>
    public PlaylistCoverDialog(
        long nodeId, string name, bool isFolder, PlaylistCover current, byte[]? currentPicture)
    {
        InitializeComponent();

        _nodeId = nodeId;
        _name = name;
        _isFolder = isFolder;
        _current = current;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _roll = SoftFigure.Seed(_name);

        Shell.AttachResize(this, minWidth: 760, minHeight: 480, maxWidth: 1200, defaultHeight: 600,
            defaultWidth: 940);

        PreviewHost.Child = _preview.Root;
        PicturePreviewHost.Child = _picturePreview.Root;

        _tabs = new RailTabStrip(
            new RailTabStrip.Tab("records", "Playlists_CoverRecords", ""),
            new RailTabStrip.Tab("picture", "Playlists_CoverPicture", ""),
            new RailTabStrip.Tab("figure", "Playlists_CoverMoods", ""));
        TabStripHost.Child = _tabs;

        _tabs.Register("records", RecordsPanel);
        _tabs.Register("picture", PicturePanel);
        _tabs.Register("figure", FigurePanel);

        // A folder holds lists, not records, so there are no records to take four from. The tab is hidden
        // rather than shown empty, because an empty grid looks like a search that failed.
        if (isFolder)
            _tabs.Hide("records");

        // The master list. FillRecordsAsync fills it with Clear and Add. It is never assigned to
        // ItemsSource again. An ObservableCollection tells the grid about its changes directly. That is
        // why it is used here, and not a plain List that is set again after filling.
        // SearchBox_TextChanged does set ItemsSource, but only to a new filtered List or back to this
        // collection. Each time, it is a different reference from the one already bound. A dependency
        // property ignores the same reference set twice in a row.
        RecordsGrid.ItemsSource = _allRecords;
        FiguresGrid.ItemsSource = _figures;

        // The stored figure is kept, so that Save without touching this tab keeps the cover the playlist
        // already has, and does not fall back to Automatic. The grid opens in the figure's own colourway,
        // not the name's. So the cover the playlist is wearing is lit where its owner can see it. If the
        // grid opened on the name's colourway, the one tile that matters would be missing.
        if (current is PlaylistCover.Figure storedFigure)
        {
            _figure = new CoverChoiceRow(storedFigure.Style, storedFigure.Seed);
            _roll = storedFigure.Seed;

            // The mark the figure has, set before the first fill. The grid is built from it. A playlist
            // that is already marked opens on tiles that show the mark. They do not drop it as soon as
            // one is clicked.
            _mark = storedFigure.Mark;
            ExplicitToggle.IsChecked = _mark is CoverMark.Explicit;
        }

        ShowMark();

        // The stored picture, shown rather than fetched: see the constructor's own parameter doc.
        if (current is PlaylistCover.Picture && currentPicture is { Length: > 0 })
        {
            _picture = currentPicture;
            _ = ShowCurrentPictureAsync(currentPicture);
        }

        FillFigures(Held.OnlyWhereItIsDrawn);

        _tabs.SelectionChanged += OnTabSelected;
        _tabs.Select(current switch
        {
            PlaylistCover.Records when !isFolder => "records",
            PlaylistCover.Picture => "picture",
            PlaylistCover.Figure => "figure",
            _ => isFolder ? "figure" : "records",
        });

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (!_isFolder)
            await FillRecordsAsync();
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _closing.Cancel();
        _figuresCts?.Cancel();
        _closing.Dispose();
        _figuresCts?.Dispose();
    }

    /// <summary>Decodes and shows the playlist's existing custom picture, which was passed in at
    /// construction. The decode runs off the UI thread. Like every other preview write here, it is
    /// guarded: a tap on either grid before this finishes wins, and the picture the window opened with
    /// does not overwrite it.</summary>
    private async Task ShowCurrentPictureAsync(byte[] bytes)
    {
        var gen = Interlocked.Increment(ref _previewGen);

        ImageSource? shown;
        try { shown = await RemoteImage.DecodeAsync(bytes); }
        catch { shown = null; }

        if (_closing.IsCancellationRequested || gen != Interlocked.Read(ref _previewGen))
            return;

        _pictureShown = shown;
        _picturePreview.Show(shown);
        if (_tabs.Current == "picture")
            _preview.Show(shown);
    }

    // ── records ──────────────────────────────────────────────────────────────

    private async Task FillRecordsAsync()
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return;

        // The records in the playlist, each once, in the list's own order. This is the same question
        // PlaylistCovers asks, with no limit. PlaylistCovers stops at sixty because it is guessing. This
        // is the full list the user chooses from.
        var db = library.Db;
        if (await Cancellable.RunAsync(
                () => library.Read(db, "a playlist's records for the cover picker", mirror => mirror.FlattenPlaylistTracks(_nodeId)),
                _closing.Token) is not { Ran: true, Value: { } asked })
            return;

        // If the library could not answer, no records are shown. A refusal has already put the problem
        // screen up behind this dialog.
        List<(string Folder, string? Album, string? Artist)> folders = asked.Match(
            tracks => tracks
                .Where(t => t.Folder is { Length: > 0 })
                .GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Folder: g.Key, Album: g.First().Album, Artist: g.First().Artist))
                .ToList(),
            _ => []);

        if (_closing.IsCancellationRequested)
            return;

        _allRecords.Clear();
        foreach (var f in folders)
            _allRecords.Add(new CoverChoiceRow
            {
                Folder = f.Folder,
                Album = f.Album ?? "",
                Artist = f.Artist ?? "",
                Edge = _sleeveEdge,
            });

        // If there are no folders at all, the answer is known at once. Whether SOME of these folders have
        // no artwork is worked out by the fill below.
        NoRecordsText.Visibility = _allRecords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The stored pick is kept only for folders still in the list, and it is capped the same way a tap
        // is. A record removed from the playlist after it was picked cannot stay one of the four.
        if (_current is PlaylistCover.Records stored)
        {
            var known = new HashSet<string>(folders.Select(f => f.Folder), StringComparer.OrdinalIgnoreCase);
            _picked.AddRange(stored.Folders.Where(known.Contains).Take(PlaylistArt.Sleeves));
            RenumberRecords();
        }

        if (_picked.Count > 0 && _tabs.Current == "records")
            RepaintPreview(new PlaylistCover.Records([.. _picked]));

        var settled = _allRecords.ToList();
        _ = FillRecordThumbsAsync(settled, _closing.Token);
    }

    /// <summary>
    /// The records tab's thumbnails. They are fetched after the tiles are shown. The shape is the same as
    /// <c>CoverArtSearchDialog.FillThumbsAsync</c>. The lanes share one cursor, so a long playlist does
    /// not open a hundred file reads as soon as its tab is filled.
    ///
    /// <para>A row with no artwork at all is marked <see cref="CoverChoiceRow.Unavailable"/>. It is not
    /// left <see cref="CoverChoiceRow.Pending"/> forever. When every lane is done, <c>NoRecordsText</c>
    /// is shown if none of them found artwork. The message says "no records have artwork", so the message
    /// depends on that question. It does not depend on whether the playlist had any folders at
    /// all.</para>
    /// </summary>
    private async Task FillRecordThumbsAsync(IReadOnlyList<CoverChoiceRow> rows, CancellationToken ct)
    {
        // The sleeves are read and decoded at the size they are drawn. This is the same rule the mood
        // tiles and a station's logo follow. The size is read once here, on the UI thread, before the
        // lanes start. XamlRoot must not be used from the lanes.
        var sleeve = ArtScaler.DecodeEdgeFor(_sleeveEdge, XamlRoot?.RasterizationScale ?? 1);

        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(Lanes, rows.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= rows.Count || ct.IsCancellationRequested)
                    return;

                var row = rows[index];
                byte[]? bytes;
                try { bytes = await AlbumCovers.LoadAsync(row.Folder, ct, sleeve, ownCollection: true); }
                catch (OperationCanceledException) { return; }
                catch { bytes = null; }

                if (ct.IsCancellationRequested)
                    return;

                if (bytes is not { Length: > 0 })
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            MarkUnavailable(row);
                    });
                    continue;
                }

                ImageSource? shown;
                try { shown = await RemoteImage.DecodeSmoothAsync(bytes, sleeve, RemoteImage.Cut.Photograph); }
                catch { shown = null; }

                if (shown is null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            MarkUnavailable(row);
                    });
                    continue;
                }

                _dispatcher.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    row.Thumb = shown;
                    _recordSleeves[row.Folder] = bytes;

                    // A folder can be ticked before its own sleeve arrives. That happens only with a
                    // prefilled pick from a stored choice, because a tap cannot add such a row. The
                    // preview was drawn with a piece missing. Now that the sleeve is here, the preview is
                    // drawn again.
                    if (_tabs.Current == "records"
                        && _picked.Contains(row.Folder, StringComparer.OrdinalIgnoreCase))
                        RepaintPreview(new PlaylistCover.Records([.. _picked]));
                });
            }
        });

        await Task.WhenAll(lanes);

        _dispatcher.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested)
                return;
            // Zero rows counts as the same answer as every row being unavailable. An empty lane list
            // finishes WhenAll straight away. Without this check, the message that FillRecordsAsync just
            // made visible for an empty playlist would be hidden again at once.
            NoRecordsText.Visibility = rows.Count == 0 || rows.All(r => r.Unavailable)
                ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    /// <summary>
    /// Marks a row as having no artwork, and removes it from the pick if it was picked.
    ///
    /// <para>A tap cannot tick a row before its sleeve arrives. So an UNAVAILABLE row can be TICKED only
    /// through a prefilled pick from a stored <see cref="PlaylistCover.Records"/> choice. When that row's
    /// read finally returns, it may have no artwork at all. If it stayed ticked, the saved recipe would
    /// list a folder with no sleeve behind it, and the preview and the saved picture would disagree. The
    /// tap check exists to prevent that. This path bypasses the tap check, so it is handled here.</para>
    /// </summary>
    private void MarkUnavailable(CoverChoiceRow row)
    {
        row.Unavailable = true;

        var at = _picked.FindIndex(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return;

        _picked.RemoveAt(at);
        RenumberRecords();
        if (_tabs.Current == "records")
            RepaintPreview(new PlaylistCover.Records([.. _picked]));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var terms = SearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        RecordsGrid.ItemsSource = terms.Length == 0
            ? _allRecords
            : _allRecords.Where(r => terms.All(t =>
                r.Album.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                || r.Artist.Contains(t, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    /// <summary>
    /// Tapping a row that is already picked removes it. Tapping one that is not picked adds it. It is
    /// added only when its sleeve has arrived (<see cref="CoverChoiceRow.Thumb"/> is not null) and the
    /// pick is under <see cref="PlaylistArt.Sleeves"/>. A row still loading, or one marked
    /// <see cref="CoverChoiceRow.Unavailable"/>, cannot be added. <c>Save</c> builds the picture from
    /// <c>_recordSleeves</c>. A picked folder with no sleeve would make the saved recipe and the saved
    /// picture disagree. Removing a folder is never blocked this way. It needs only the folder's own
    /// name.
    /// </summary>
    private void RecordsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CoverChoiceRow row || row.Folder.Length == 0)
            return;

        var at = _picked.FindIndex(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _picked.RemoveAt(at);
        }
        else
        {
            if (row.Thumb is null || _picked.Count >= PlaylistArt.Sleeves)
                return;
            _picked.Add(row.Folder);
        }

        RenumberRecords();
        RepaintPreview(new PlaylistCover.Records([.. _picked]));
    }

    /// <summary>Sets every row's number from the pick list, not only the row that changed. Removing the
    /// second pick moves the third into its place, so one tap can change the number on more than one
    /// row.</summary>
    private void RenumberRecords()
    {
        foreach (var row in _allRecords)
        {
            var at = _picked.FindIndex(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
            row.Number = at >= 0 ? at + 1 : null;
        }
    }

    // ── picture ──────────────────────────────────────────────────────────────

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            if (await picker.PickSingleFileAsync() is not { } file)
                return;

            // The generation is taken here, before the read starts. A tap on either grid during the read
            // must win, so the tap must get a newer generation than this pick. If it were taken after
            // the read, the read would always win, whatever the user did while it ran.
            var gen = Interlocked.Increment(ref _previewGen);

            // The bytes are cropped square around the centre, and scaled down only when they are bigger
            // than the largest edge the app ever draws a cover at. This runs off the UI thread, because
            // reading and cropping the file is real work. This handler runs on the button's own click.
            var bytes = await Task.Run(
                () => PlaylistArt.Bleed(File.ReadAllBytes(file.Path), ArtScaler.DisplayEdge));

            if (_closing.IsCancellationRequested || gen != Interlocked.Read(ref _previewGen))
                return;

            ImageSource? shown;
            try { shown = await RemoteImage.DecodeAsync(bytes); }
            catch { shown = null; }

            if (_closing.IsCancellationRequested || gen != Interlocked.Read(ref _previewGen))
                return;

            _picture = bytes;
            _pictureShown = shown;
            _picturePreview.Show(shown);
            CopiedText.Visibility = Visibility.Visible;
            if (_tabs.Current == "picture")
                _preview.Show(shown);
        }
        catch
        {
            // A file that cannot be opened is treated as no choice. The tab keeps what it had.
        }
    }

    // ── generated ────────────────────────────────────────────────────────────

    /// <summary>
    /// The eight, once each, all drawn from one roll of <see cref="_roll"/>. So all eight use one
    /// colourway. The seed picks the colours as well as the arrangement (see
    /// <c>PlaylistFigure.Colourway</c>).
    ///
    /// <para>There are eight tiles, not forty. Each style appears once, in one colourway. The eight
    /// tiles are eight kinds of light, and the colours change across all of them. Forty tiles (every
    /// style in several colourways) would ask the same question five times. So the grid shows one tile
    /// for each style, and the button below changes the colours.</para>
    ///
    /// <para>A fill cancels and replaces any fill already in progress. The records grid is filled only
    /// once. This tab needs the extra care, because the colours button calls this method again while an
    /// earlier fill may still be drawing its tiles. A late tile from the old fill would then show the
    /// wrong colours for the new fill.</para>
    /// </summary>
    /// <param name="pick">What happens to a pick that is already made. It is given at both call sites and
    /// has no default, because the two answers are opposite. The fill when the window opens must not
    /// change the pick. The fill from the colours button must move it.</param>
    private void FillFigures(Held pick)
    {
        _figuresCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _figuresCts = cts;

        _figures.Clear();
        for (var style = 0; style < PlaylistArt.Styles; style++)
            _figures.Add(new CoverChoiceRow(style, _roll) { Edge = _tileEdge, Mark = _mark });

        if (_figure is { } current)
        {
            foreach (var row in _figures)
            {
                var same = row.Style == current.Style
                           && (pick is Held.FollowsItsFamily || row.Seed == current.Seed);
                row.Selected = same;

                // The pick moves onto the lit tile. This is why the choice is stated and not a default. A
                // user who chose a sphere and then asked for other colours wants the sphere in the
                // colours they can now see. A user who only OPENED this window on a playlist that already
                // has a cover wants the cover they have. Moving the pick there would silently change it
                // on a Save that the user clicked only to keep the cover.
                if (same && pick is Held.FollowsItsFamily)
                    _figure = row;
            }
        }

        var settled = _figures.ToList();
        _ = FillFigureThumbsAsync(settled, cts.Token);
    }

    /// <summary>What a fill of the tile grid does with a pick that was already made.</summary>
    private enum Held
    {
        /// <summary>Lit only where this fill draws the same picture: the same style AND the same seed.
        /// This is what the window opens with. The stored cover is on the grid because the grid opens in
        /// its colourway. If it is not on the grid, nothing is lit and nothing changes.</summary>
        OnlyWhereItIsDrawn,

        /// <summary>Lit by style, whatever colourway this fill uses, and the pick itself moves to it.
        /// This is what the colours button asks for.</summary>
        FollowsItsFamily,
    }

    /// <summary>
    /// Shares the panel's width out among whole tiles, so the eight fill it edge to edge.
    ///
    /// <para><b>The album wall's arithmetic, called and not copied.</b> <see cref="AlbumGridLayout.Fit"/>
    /// decides how many cards fit across a shelf, and how wide they become once the row is full. It is a
    /// pure function of two numbers, so a second grid can call it. It uses the same rule, the same
    /// rounding and the same behaviour during a drag. A window between two whole column counts grows the
    /// tiles, so no gap is left. This method adds no rounding rule of its own.</para>
    ///
    /// <para>The count is capped at four columns. This grid needs that cap, and the wall does not. There
    /// are exactly eight tiles. At four columns they make two full rows. At five they make one row of
    /// five and one of three, which looks like a failed fit. The minimum is two, for the same reason.
    /// Eight tiles, one per row, is a list and not a grid.</para>
    /// </summary>
    private void LayOutTiles()
    {
        var available = FiguresGrid.ActualWidth
                        - FiguresGrid.Padding.Left - FiguresGrid.Padding.Right;

        var (asked, _) = AlbumGridLayout.Fit(available, TileTarget + TileGap, maxColumns: 4, minColumns: 2);

        // Four columns or two, never three. Eight tiles divide evenly by both. Three columns would leave
        // rows of three, three and two, which looks like a failed fit. So the wall's answer is treated as
        // a vote. It is snapped to four or to two, and the spacing is worked out from that count.
        var columns = asked >= 3 ? 4 : 2;
        var stride = AlbumGridLayout.StrideFor(available, columns, Math.Max(1, available));
        var edge = Math.Max(TileFloor, stride - TileGap);

        // A half point of tolerance. A resize that gives the same tile size must not walk the whole list
        // again. ActualWidth comes in fractions, and a floor to whole pixels gives the same answer
        // anyway.
        if (Math.Abs(edge - _tileEdge) < 0.5)
            return;

        _tileEdge = edge;
        foreach (var row in _figures)
            row.Edge = edge;
    }

    /// <summary>
    /// The same share-out for the records grid. Only the numbers differ.
    ///
    /// <para>There is no column cap here, and no snapping to an even count. This grid holds however many
    /// records the playlist has, forty as often as eight. So a last row that is not full is normal for a
    /// long list. It is not a sign of a failed fit. This grid uses a SMALLER target instead, because a
    /// sleeve is for recognising, not for a close look. Forty sleeves at the mood tile size would be a
    /// long page to scroll.</para>
    /// </summary>
    private void LayOutRecords()
    {
        var available = RecordsGrid.ActualWidth
                        - RecordsGrid.Padding.Left - RecordsGrid.Padding.Right;

        var columns = AlbumGridLayout.ColumnsForTarget(available, SleeveTarget + TileGap);
        var stride = AlbumGridLayout.StrideFor(available, columns, Math.Max(1, available));
        var edge = Math.Max(TileFloor, stride - TileGap);

        if (Math.Abs(edge - _sleeveEdge) < 0.5)
            return;

        _sleeveEdge = edge;
        foreach (var row in _allRecords)
            row.Edge = edge;
    }

    private void FiguresGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayOutTiles();

    private void RecordsGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayOutRecords();

    /// <summary>Turns the advisory toggle on or off. It is set in code, not in the template, because
    /// <c>ToolToggleStyle</c> leaves its Checked state empty on purpose. Those keys are painted by a rail
    /// that draws their faces itself. This window is one more owner that does the same.</summary>
    private void ShowMark()
    {
        var on = _mark is CoverMark.Explicit;
        ExplicitToggle.Background = (Brush)Resources[on ? "MarkLitBrush" : "MarkDarkBrush"];
        ExplicitToggle.Opacity = on ? 1 : 0.7;
    }

    private async Task FillFigureThumbsAsync(IReadOnlyList<CoverChoiceRow> rows, CancellationToken ct)
    {
        // Read once, on the UI thread, before the lanes start. XamlRoot must not be used from the lanes.
        // A scale that changed in the middle of a fill would decode half the grid at one size and half at
        // another.
        var edge = ArtScaler.DecodeEdgeFor(_tileEdge, XamlRoot?.RasterizationScale ?? 1);

        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(Lanes, rows.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= rows.Count || ct.IsCancellationRequested)
                    return;

                var row = rows[index];
                PlaylistArt.Drawn drawn;
                try
                {
                    if (await Cancellable.RunAsync(
                            // No mood is passed, for the reason PlaylistCovers.Render gives. This is the
                            // picker for somebody's own playlist. The tile must MATCH what Render draws,
                            // or the preview would show a cover that the saved one does not have.
                            () => PlaylistArt.Figure(row.Style, row.Seed, row.Mark, mood: null), ct)
                        is not { Ran: true, Value: { } made })
                        return;

                    drawn = made;
                }
                catch { continue; }

                if (drawn.Cover is not { Length: > 0 } bytes || ct.IsCancellationRequested)
                    continue;

                // The picture is requested at the size it is drawn at. A station's logo is fetched the
                // same way. A picture decoded at one size and shown at another looks soft. A mark with
                // hard black and white edges shows this first. These tiles share the panel's width (see
                // LayOutTiles), so the size is worked out for each fill, from the size the tiles are laid
                // out at and the scale the screen uses.
                ImageSource? shown;
                try { shown = await RemoteImage.DecodeSmoothAsync(bytes, edge, RemoteImage.Cut.Drawing); }
                catch { shown = null; }

                _dispatcher.TryEnqueue(() =>
                {
                    if (!ct.IsCancellationRequested)
                        row.Thumb = shown;
                });
            }
        });

        await Task.WhenAll(lanes);
    }

    // A new set of colours for all eight tiles at once. The whole grid changes, not only the lit tile.
    // The user asked for other colours, so the other seven tiles must change too.
    private void Colours_Click(object sender, RoutedEventArgs e)
    {
        _roll = (_roll * 1664525u) + 1013904223u;
        FillFigures(Held.FollowsItsFamily);

        // The pick was moved onto the tile with the new colours by FillFigures. The preview must follow
        // it, or the window would show one cover and save another.
        if (_figure is { } picked)
            RepaintPreview(new PlaylistCover.Figure(picked.Style, picked.Seed, _mark));
    }

    /// <summary>
    /// The advisory mark, on or off, for the whole tab.
    ///
    /// <para>Every tile is redrawn, not only restamped. The mark is drawn INTO the cover, under its
    /// rounded corner and over its picture. So a marked tile cannot be made from an unmarked one. The
    /// cost is the same eight composites as the colours button, on a click that the user makes
    /// once.</para>
    /// </summary>
    private void Explicit_Click(object sender, RoutedEventArgs e)
    {
        _mark = ExplicitToggle.IsChecked is true ? CoverMark.Explicit : CoverMark.None;
        ShowMark();

        FillFigures(Held.FollowsItsFamily);

        if (_figure is { } picked)
            RepaintPreview(new PlaylistCover.Figure(picked.Style, picked.Seed, _mark));
    }

    private void FiguresGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CoverChoiceRow row)
            return;

        foreach (var r in _figures)
            r.Selected = ReferenceEquals(r, row);

        _figure = row;
        RepaintPreview(new PlaylistCover.Figure(row.Style, row.Seed, _mark));
    }

    // ── tabs & the live preview ──────────────────────────────────────────────

    /// <summary>The left-hand preview follows the tab that is showing, not the tab it was last drawn for.
    /// Switching to a tab that already has a pick shows that pick at once, without waiting for a
    /// tap.</summary>
    private void OnTabSelected(string key)
    {
        switch (key)
        {
            case "records":
                if (_picked.Count > 0)
                    RepaintPreview(new PlaylistCover.Records([.. _picked]));
                else
                    _preview.Show(null);
                break;

            case "figure":
                if (_figure is { } f)
                    RepaintPreview(new PlaylistCover.Figure(f.Style, f.Seed, _mark));
                else
                    _preview.Show(null);
                break;

            case "picture":
                _preview.Show(_pictureShown);
                break;
        }
    }

    /// <summary>
    /// Redraws the left-hand preview from a choice. The composite runs off the UI thread.
    ///
    /// <para>It uses a generation counter, not a cancellation token. <c>PlaylistCovers.Render</c> is a
    /// synchronous Skia composite, and nothing in it is worth interrupting halfway. So the safety here is
    /// not stopping the redraw. It is throwing the answer away when a newer one has been asked for by the
    /// time this one returns. The generation is checked once before the decode and once before the
    /// picture is shown, and <c>_closing</c> is checked both times. A tap does not wait for the
    /// repaint before it. The newer request simply makes this one stale.</para>
    ///
    /// <para>A finished repaint that is still current is cached under the wire form of its choice, and
    /// the generation that made it. See <c>Save</c>, which reuses it instead of composing the same
    /// picture again.</para>
    /// </summary>
    private void RepaintPreview(PlaylistCover cover)
    {
        var gen = Interlocked.Increment(ref _previewGen);
        var sleeves = cover is PlaylistCover.Records ? BuildPickedSleeves() : [];
        _ = RepaintPreviewAsync(gen, cover, sleeves);
    }

    private async Task RepaintPreviewAsync(long gen, PlaylistCover cover, IReadOnlyList<byte[]> sleeves)
    {
        PlaylistArt.Drawn drawn;
        try { drawn = await Task.Run(() => PlaylistCovers.Render(cover, sleeves, _name)); }
        catch { return; }

        if (_closing.IsCancellationRequested || gen != Interlocked.Read(ref _previewGen))
            return;

        _previewDrawnKey = cover.ToWire();
        _previewDrawnGen = gen;
        _previewDrawn = drawn;

        if (drawn.Cover is not { Length: > 0 } bytes)
        {
            _preview.Show(null);
            return;
        }

        ImageSource? shown;
        try { shown = await RemoteImage.DecodeAsync(bytes); }
        catch { shown = null; }

        if (!_closing.IsCancellationRequested && gen == Interlocked.Read(ref _previewGen))
            _preview.Show(shown);
    }

    /// <summary>The sleeves for the folders picked on the records tab, in pick order. This is used for
    /// the LIVE preview only. The live preview may use whatever sleeves have loaded so far, because a
    /// sleeve that arrives later triggers another repaint. <c>Save</c> does not use this list. See
    /// <see cref="ResolvePickedSleevesAsync"/>, which waits for the sleeves this list would leave
    /// out.</summary>
    private List<byte[]> BuildPickedSleeves()
        => _picked.Select(f => _recordSleeves.GetValueOrDefault(f))
                  .Where(b => b is { Length: > 0 })
                  .Select(b => b!)
                  .ToList();

    /// <summary>The sleeves for every picked folder. It waits, off the UI thread, for any sleeve that has
    /// not arrived yet, rather than composing around the gap. A picked folder can be missing from
    /// <c>_recordSleeves</c> in one case only: a prefilled pick from a stored
    /// <see cref="PlaylistCover.Records"/> choice, whose disk read has not finished. An ordinary tap
    /// cannot add a folder before its sleeve arrives (see <c>RecordsGrid_ItemClick</c>). So this rarely
    /// has real work to do. It is still called before every Save. A missing folder would silently drop
    /// out of the picture, and this method exists to stop that.</summary>
    private async Task<List<byte[]>> ResolvePickedSleevesAsync(CancellationToken ct)
    {
        var sleeves = new List<byte[]>(_picked.Count);
        foreach (var folder in _picked)
        {
            if (_recordSleeves.TryGetValue(folder, out var known))
            {
                sleeves.Add(known);
                continue;
            }

            byte[]? bytes;
            try { bytes = await AlbumCovers.LoadAsync(folder, ct, ArtScaler.RowEdge, ownCollection: true); }
            catch (Exception ex) when (ex is not OperationCanceledException) { bytes = null; }

            if (bytes is { Length: > 0 })
            {
                _recordSleeves[folder] = bytes;
                sleeves.Add(bytes);
            }
        }
        return sleeves;
    }

    // ── footer ───────────────────────────────────────────────────────────────

    private void Automatic_Click(object sender, RoutedEventArgs e)
    {
        Chosen = new PlaylistCover.Automatic();
        Rendered = PlaylistArt.Drawn.None;
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>
    /// Neither <see cref="Chosen"/> nor <see cref="Rendered"/> is set until AFTER the render finishes. A
    /// Records or Figure Save that misses the cache waits for real work. Close, Automatic and the
    /// window's own Escape key all stay usable during that wait. <see cref="_saving"/> only stops a
    /// second Save. It does not stop a window that looks live and still answers Escape while it composes.
    /// So the footer is disabled for the whole wait, so that it does not look live. <c>_closing</c> is
    /// checked again once the wait ends. Escape skips the disabled buttons, so a dismissal can still
    /// happen during the wait. In that case nothing is written. Setting <see cref="Chosen"/> to a new
    /// cover while <see cref="Rendered"/> stays
    /// <see cref="PlaylistArt.Drawn.None"/> would make the two disagree.
    /// </summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
            return;
        _saving = true;
        SetFooterEnabled(false);

        PlaylistCover chosen = _tabs.Current switch
        {
            "records" when _picked.Count > 0 => new PlaylistCover.Records([.. _picked]),
            "picture" when _picture is { Length: > 0 } => new PlaylistCover.Picture(),
            "figure" when _figure is { } f => new PlaylistCover.Figure(f.Style, f.Seed, _mark),
            _ => new PlaylistCover.Automatic(),
        };

        // The picture for the choice. A Picture choice uses its own bytes, with nothing composed over
        // them. Those bytes are either the caller's master or the file just picked. Automatic renders
        // nothing, and the caller deletes the master. Everything else is composed off the UI thread (see
        // RenderedForAsync).
        PlaylistArt.Drawn rendered = chosen switch
        {
            PlaylistCover.Picture when _picture is { Length: > 0 } bytes => new PlaylistArt.Drawn(bytes, null),
            PlaylistCover.Automatic => PlaylistArt.Drawn.None,
            var c => await RenderedForAsync(c),
        };

        if (_closing.IsCancellationRequested)
            return;   // Dismissed while composing. Chosen stays null, and nothing is written

        Chosen = chosen;
        Rendered = rendered;
        Hide();
    }

    /// <summary>Disables every button that can end this window while a Save runs. The buttons are not
    /// enabled again afterwards. Every way out of <see cref="Save_Click"/> either hides the window, or
    /// the window has already been dismissed. There is no way back to an active footer from
    /// either.</summary>
    private void SetFooterEnabled(bool enabled)
    {
        AutomaticButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
    }

    /// <summary>
    /// The picture for a Records or Figure choice. It is composed off the UI thread in every case.
    ///
    /// <para>The usual case is a cache hit. The live preview has already composed this exact choice (the
    /// same wire value), and no newer repaint has been asked for since. So this returns that result, and
    /// Skia does not do the same work twice. This is the reason this dialog composes its own
    /// <see cref="Rendered"/>, instead of leaving it to the caller.</para>
    ///
    /// <para>A miss is normal, though less common. A Save can happen between a tap and the end of its
    /// repaint. Or a Records pick can hold a folder from the stored choice whose disk read has not
    /// finished. In both cases the picture is composed here, off the UI thread, after any missing sleeve
    /// is fetched (see
    /// <see cref="ResolvePickedSleevesAsync"/>). It is never composed on the click itself.</para>
    ///
    /// <para>This runs from <c>async void Save_Click</c>. So any failure, including the window closing
    /// during the call, is caught here and not thrown. An exception that escapes an <c>async void</c>
    /// method crashes the app. It is not returned as an error.</para>
    /// </summary>
    private async Task<PlaylistArt.Drawn> RenderedForAsync(PlaylistCover cover)
    {
        var key = cover.ToWire();
        if (key == _previewDrawnKey && _previewDrawnGen == Interlocked.Read(ref _previewGen))
            return _previewDrawn;

        try
        {
            var sleeves = cover is PlaylistCover.Records
                ? (IReadOnlyList<byte[]>)await ResolvePickedSleevesAsync(_closing.Token)
                : [];

            return await Task.Run(() => PlaylistCovers.Render(cover, sleeves, _name));
        }
        catch
        {
            return PlaylistArt.Drawn.None;
        }
    }
}

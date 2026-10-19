using System.Collections.ObjectModel;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The library as a wall of album covers, with an inline flip-down track list. It is similar to iTunes'
/// browse view, and an alternative to the folder tree-grid on the review workbench. The grid is a
/// vertical stack of rows, and each row is a fixed run of cards. Opening a card flips a full-width band
/// down under its row (tint, big cover, tracks in two columns), and pushes the rest of the wall down.
/// Rows are the unit that the ItemsRepeater virtualizes, so the wall scales with the collection.
///
/// The control knows nothing of the library. The page gives it the album folders, and the delegates that
/// turn a folder into a cover, its tracks, and playback. Re-chunking rows on resize, and the two-column
/// split, are pure arithmetic in <see cref="AlbumGridLayout"/>.
/// </summary>
public sealed partial class AlbumWallView : UserControl
{
    /// <summary>The wall's own vertical scroller, so the shell can pin it in step with the art
    /// drawer.</summary>
    public ScrollViewer ScrollHost => Scroller;

    /// <summary>The chrome a card wears around its art square: the button's 8 px padding on each side.
    /// That is the gap between covers. Card stride = art + this, so the art fills the padding box
    /// exactly, with no dead slack. The cover block then lines up with the drawer, which is inset by the
    /// same 8 px. Art is the size the user sets.</summary>
    private const double CardChrome = 16;

    /// <summary>The art-square sizes the header slider spans, and the size it starts at. 150 is the
    /// default edge. <see cref="MaxArt"/> also caps a lone card in a wide panel.</summary>
    public const double MinArt = 96;
    public const double MaxArt = 260;
    private const double DefaultArt = 150;

    /// <summary>ScrollViewer left and right padding. It is subtracted from the panel width before the
    /// columns are counted, so the last card in a row is not pushed under the scrollbar.</summary>
    private const double ScrollerPadding = 28;

    private readonly List<AlbumCard> _cards = new();
    private readonly ObservableCollection<AlbumRow> _rows = new();
    private readonly Dictionary<string, AlbumCard> _cardByFolder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The library the wall is showing now. A feed uses this to tell "the same albums again"
    /// from "a different library". Only a different library may not reuse cards.</summary>
    private string? _libraryName;

    /// <summary>The one live card and art size that the whole wall binds to. The control writes it, and
    /// the cards follow. It starts at the shipped size, so the wall looks the same before the first
    /// measure.</summary>
    private readonly AlbumTileMetrics _metrics = new();

    private int _columns = 1;
    private double _availableWidth;
    private double _targetArt = DefaultArt;
    private bool _suppressPersist;
    private DispatcherQueueTimer? _persistTimer;
    private string? _playingPath;
    private CancellationTokenSource? _bandCts;

    /// <summary>Owns the whole drawer state machine: which album is expanded, its live band, and the
    /// open, close, re-seat and same-row switch transitions. This keeps that state out of a set of loose
    /// fields and three near-duplicate code paths. The visual (unfold, snap, fold) is driven by flags on
    /// the band model, so a recycled element always re-applies the right state.</summary>
    private readonly BandConductor _band;


    public AlbumWallView()
    {
        InitializeComponent();
        _band = new BandConductor(this);
        _lineBreath = new Common.CloudLineBreath(() => _band.Current?.Lines ?? []);
        RowsRepeater.ItemsSource = _rows;
        RowsRepeater.ElementPrepared += Row_ElementPrepared;
        RowsRepeater.ElementClearing += Row_ElementClearing;
#if DEBUG
        // Shows whether the repeater really virtualizes this wall. A 1000-album library should realize
        // only the viewport plus a buffer of rows, and this number shows it in the debug output. The
        // INDEX is written with it. The count alone cannot tell a wall being scrolled past from a wall
        // that hands the same row back and forth. The second is a loop, not a scroll.
        //
        // This is behind the diagnostic's own switch, and that is not just tidiness. A line for each
        // realize is a line for each row on each scroll. Under a debugger, Debug.WriteLine is a round
        // trip down the debug channel. At the rate a fling realizes rows, that is enough to cause a
        // stutter of its own. A tool for measuring jitter must not cause it.
        if (WallDiag.On)
        {
            RowsRepeater.ElementPrepared += (_, a) =>
            {
                WallDiag.Realized(a.Index);
                RPM.Core.Diagnostics.Said.Line("wall", $"realized rows: {++_realizedRows} (row {a.Index} in)");
            };
            RowsRepeater.ElementClearing += (r, a) =>
            {
                var index = r.GetElementIndex(a.Element);
                WallDiag.Cleared(index);
                RPM.Core.Diagnostics.Said.Line("wall", $"realized rows: {--_realizedRows} (row {index} out)");
            };
        }
#endif
        // The cost of every cover decode, wherever it is requested. This is behind the diagnostic's own
        // switch. The clock is a Stopwatch per decode. That is small next to the decode itself, but a
        // tool for measuring jitter should add no cost.
        if (WallDiag.On)
            CoverDecoder.Timed = WallDiag.Decode;

        // How often the tree is laid out again. This is counted, not timed. The pass itself belongs to
        // Uno, so there is nothing of ours to put a clock inside. But a wall at rest that lays out sixty
        // times a second is a loop nobody intended. The count shows it.
        if (WallDiag.On)
            LayoutUpdated += (_, _) => WallDiag.Laid();

        Unloaded += (_, _) =>
        {
            _bandCts?.Cancel();
            StopBadgePulse();   // no rendering handler is left attached to a wall nobody is looking at
            _lineBreath.Stop(); // the same for an open band's own marks
            _stream?.Forget();  // and the pictures held for rows nobody is looking at
        };
    }

#if DEBUG
    private int _realizedRows;
#endif

    /// <summary>A row's element comes (back) on screen, so its cards' covers and badges are fetched. The
    /// card template's own DataContextChanged does this too. But that only fires when the DataContext
    /// actually CHANGES. A row element recycled back onto the SAME row rebinds nothing, so nobody asks
    /// again for the art. The top row is the usual case. It is scrolled away, its covers are dropped by
    /// <see cref="Row_ElementClearing"/>, and then it is scrolled back. The repeater hands row 0 its old
    /// element. This handler runs on every realize, whatever the case.</summary>
    private void Row_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => WallDiag.Time("realize", () => RowPrepared(args));

    private void RowPrepared(ItemsRepeaterElementPreparedEventArgs args)
    {
        _everRealized = true;
        if ((args.Element as FrameworkElement)?.DataContext is not AlbumRow row)
            return;

        // Realizing a row does NO work. It shows the picture the card already holds, which is the
        // resident 64 px one, in memory since the shelf was fed. Nothing is read from disk or the mirror,
        // and nothing is decoded. Nothing can arrive late. The loader is told what is on screen. It works
        // out for itself what to fetch, and in what order. See AlbumWallView.Loading.
        _onScreen.Add(row);
        foreach (var card in row.Cards)
            // The card was given back before the sweep reached it, so it still has its big picture.
            // Nothing needs doing for it. This is what makes a row that bounces on the edge of the
            // realization window free. See CoverHold.
            Stream.Realized(card);

        Load();
    }

    /// <summary>A row has scrolled far enough out that the repeater reclaims its element. It stops being
    /// the first thing the loader works on. Its records are released for collection, but they keep their
    /// big picture for a moment. Giving the picture back in the same frame the row is reclaimed causes a
    /// stutter, so it is kept. See <see cref="CoverHold"/>.</summary>
    private void Row_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
        => WallDiag.Time("clear", () => RowClearing(args));

    private void RowClearing(ItemsRepeaterElementClearingEventArgs args)
    {
        if ((args.Element as FrameworkElement)?.DataContext is not AlbumRow row)
            return;
        _onScreen.Remove(row);
        foreach (var card in row.Cards)
            Stream.Reclaimed(card);
    }

    /// <summary>Raised, debounced, when the user settles the cover-size slider, so the host can persist
    /// the chosen size. The value is the target art edge in pixels.</summary>
    public event Action<double>? TileSizeChanged;

    /// <summary>Seeds the size slider from the persisted setting, before the wall is first shown. It does
    /// not echo back through <see cref="TileSizeChanged"/>. This is a restore, not a user
    /// change.</summary>
    public void SetTileSize(double art)
    {
        _targetArt = Math.Clamp(art, MinArt, MaxArt);
        _suppressPersist = true;
        if (_sizeSlider is { } slider)
            slider.Value = _targetArt;   // no-op if unchanged; ValueChanged recomputes when it moves
        _suppressPersist = false;
        RecomputeLayout();
    }

    // ── the cover-size slider, which lives in the host's header ──
    // The wall shares one header with the tracklist (MainPage), so the slider is the page's element and
    // the wall is handed it. The wall still owns the value and the relayout. Where that header sits,
    // including keeping it clear of the spinning record, is the page's business, since the header
    // outlives the wall.

    private Slider? _sizeSlider;

    /// <summary>Hands the wall the header's cover-size slider. The wall drives its layout from it, and
    /// seeds it with the remembered size.</summary>
    public void AttachSizeSlider(Slider slider)
    {
        if (ReferenceEquals(_sizeSlider, slider))
            return;
        _sizeSlider = slider;
        slider.ValueChanged += SizeSlider_ValueChanged;

        _suppressPersist = true;
        slider.Value = _targetArt;   // the seed that SetTileSize could not write before the cluster arrived
        _suppressPersist = false;
    }

    // ── wiring the page supplies (the control stays library-agnostic) ──

    /// <summary>Folder and the pixel edge wanted → cover bytes (folder image, else first track's embedded
    /// art), or null. The bytes come back no larger than that edge, so what is handed over IS the thumb.
    /// The wall decodes it and does not shrink it again.</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? CoverLoader { get; set; }

    /// <summary>
    /// Where the collection on screen is rooted. This decides what its resident pack is named after.
    ///
    /// <para>This is a hook, not a value, because the shelf is swapped under this control. A borrowed
    /// collection has a different root, and it must not be packed over the user's own. Naming the pack
    /// after the collection, not after the wall, also lets this wall and the visualizer's wall share one
    /// pack. They usually draw the same records. A pack for each would make each one pay the whole
    /// first-run cost. See <see cref="ThumbPack.ScopeFor"/>.</para>
    /// </summary>
    public Func<string?>? CollectionRoot { get; set; }

    private string PackScope => ThumbPack.ScopeFor(CollectionRoot?.Invoke());

    /// <summary>Folder → its tracks in album order.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>>? TracksLoader { get; set; }

    /// <summary>Folder → the record's own answer to whether it is classical, or null where it has not
    /// given one. A record that is not classical must not be drawn as works with composers, even when its
    /// files carry a WORK tag. The answer belongs to the user, so the drawer asks for it in the same way
    /// the album page does.</summary>
    public Func<string, bool>? ClassicalLoader { get; set; }

    /// <summary>Play one track (clicked in an open band).</summary>
    public Action<TrackInfo>? PlayTrack { get; set; }

    /// <summary>Play a whole album. The bool is shuffle.</summary>
    public Action<IReadOnlyList<TrackInfo>, bool>? PlayTracks { get; set; }

    /// <summary>Hand a whole album to the DJ: the queue becomes the record, and the mix runs it.</summary>
    public Action<IReadOnlyList<TrackInfo>>? StartDj { get; set; }

    /// <summary>Folder → how much of the album is in the cloud, for the corner badge. The page reads it
    /// from the library. The wall only shows the answer.</summary>
    public Func<string, AlbumCloudBadge>? CloudBadgeLoader { get; set; }

    /// <summary>The same answer for a whole shelf at once. A re-read of every mark on screen uses this.
    /// It is one query and one take of the library's reading gate, instead of one per record. See
    /// <see cref="RefreshBadges"/>.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyDictionary<string, AlbumCloudBadge>>? CloudBadgesLoader { get; set; }

    /// <summary>Whether the cloud engine is sending nothing at all. A queued track line has to be told
    /// this about itself. Waiting for room is not the same as being on its way, and only one of the two
    /// breathes. It is asked when the marks are applied. Null reads as "it is running", which is what a
    /// view with no page behind it should assume.</summary>
    public Func<bool>? CloudUploadsStopped { get; set; }

    /// <summary>Whose collection is on the wall. This decides what a track line's mark means. A borrowed
    /// record's lines say where the song comes from, not whose disk it is on. It is asked when the marks
    /// are applied, because a lender can go off the network with their record open. Null reads as the
    /// user's own, which is the only shelf a view with no page behind it can be showing.</summary>
    public Func<RPM.Core.Cloud.ShelfReading>? ShelfReading { get; set; }

    /// <summary>Folder → the live facts that an album's right-click menu is built from: cloud mode,
    /// favourite, and the Discogs id. It is asked afresh each time a menu opens, so the menu never shows
    /// a stale state.</summary>
    public Func<string, AlbumMenuState>? MenuStateProvider { get; set; }

    /// <summary>An album menu item was chosen. The page does the work. It carries the album
    /// folder.</summary>
    public Action<AlbumMenuCommand, string>? MenuInvoked { get; set; }

    /// <summary>The saved playlists to list in the "Add to playlist" submenu: id, name, and depth for
    /// indenting. It is asked afresh each time the menu opens, since the tree changes.</summary>
    public Func<IReadOnlyList<(long Id, string Name, int Depth)>>? PlaylistEntriesProvider { get; set; }

    /// <summary>Adds an album's tracks to a saved playlist. It carries the album folder and the playlist
    /// id.</summary>
    public Action<string, long>? AddFolderToPlaylist { get; set; }

    /// <summary>Creates a new playlist from an album's tracks. It carries the album folder. The page
    /// prompts for the name.</summary>
    public Action<string>? NewPlaylistFromFolder { get; set; }

    /// <summary>A track menu item was chosen in an opened band. The page does the work. This is kept
    /// apart from <see cref="MenuInvoked"/>, because the two menus are for different things: one song,
    /// and one record.
    /// </summary>
    public Action<TrackMenuCommand, TrackInfo>? TrackMenuInvoked { get; set; }

    /// <summary>Adds one track to a saved playlist. It carries the track and the playlist id.</summary>
    public Action<TrackInfo, long>? AddTrackToPlaylist { get; set; }

    /// <summary>Creates a new playlist from one track. It carries the track. The page prompts for the
    /// name. This is the song's own counterpart of <see cref="NewPlaylistFromFolder"/>.</summary>
    public Action<TrackInfo>? NewPlaylistFromTrack { get; set; }

    /// <summary>Track → the live cloud facts that its right-click menu is built from. It is asked afresh
    /// each time a menu opens, so a song that has just gone up is never offered to be sent
    /// again.</summary>
    public Func<TrackInfo, TrackMenuState>? TrackMenuStateProvider { get; set; }

    /// <summary>Marks a song as a favourite, or clears it. This is the heart at the end of a line in an
    /// open drawer. It answers whether the library took the change. A file that is not under the library
    /// root has no row to carry the mark. So the heart must not fill in over a mark that nothing
    /// stored.</summary>
    public Func<TrackInfo, bool, bool>? SetTrackFavorite { get; set; }

    // ── album feed ──

    /// <summary>What the mirror says about each album on the wall: its name, its artist, when it arrived,
    /// its year and its plays. The wall's headings and its order both come from here, and from nothing
    /// else.</summary>
    private IReadOnlyDictionary<string, AlbumFacts> _facts =
        new Dictionary<string, AlbumFacts>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders that hold an explicit track. These get the E on a card's heading.</summary>
    private IReadOnlySet<string> _explicitAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders the listener has favourited. These get the sticker on a sleeve.</summary>
    private IReadOnlySet<string> _favoriteAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private AlbumFacts FactsFor(string folder) => _facts.TryGetValue(folder, out var f) ? f : default;

    private AlbumOrdering _ordering = AlbumOrdering.Artist;

    /// <summary>
    /// The order the wall is in. Changing it re-seats the cards that are already there, rather than
    /// rebuilding them. So nothing reloads and nothing blanks. A sort is a rearrangement of what somebody
    /// is looking at, not a new wall.
    /// </summary>
    public AlbumOrdering Ordering
    {
        get => _ordering;
        set
        {
            if (_ordering == value)
                return;
            _ordering = value;
            SortCards();
            _band.OnAlbumsChanged();
            Regroup();
        }
    }

    /// <summary>Puts <see cref="_cards"/> into <see cref="Ordering"/>. The rules are
    /// <see cref="AlbumSort"/>'s. They are in Core and tested there, and they are the same rules the
    /// phone's wall uses. Each rule has a tie to break and a missing value to place.</summary>
    private void SortCards()
    {
        if (_cards.Count < 2)
            return;

        var sorted = AlbumSort.By(_cards, _ordering, c => FactsFor(c.Folder));
        _cards.Clear();
        _cards.AddRange(sorted);
    }

    /// <summary>
    /// Replaces the wall with these album folders, in grid order. The open drawer is kept if its folder
    /// is still present. Otherwise it is dropped.
    ///
    /// <para>
    /// An album that is still here keeps the card it already had. A feed is not only a new library.
    /// Adding an album to the cloud, removing it, or favouriting it all come back through here with the
    /// same albums in the same order. A fresh <see cref="AlbumCard"/> has no cover. Rebuilding every card
    /// would drop the whole wall to placeholders, and decode every sleeve again. That is the flash. A
    /// reused card keeps its cover, tint and ring. <see cref="SyncRow"/> then sees the same references,
    /// and changes nothing.
    /// </para>
    /// </summary>
    public void SetAlbums(
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, AlbumFacts> facts,
        IReadOnlySet<string> explicitAlbums,
        IReadOnlySet<string> favoriteAlbums,
        string libraryName,
        ShelfEmpty empty)
    {
        var fedClock = WallDiag.On ? System.Diagnostics.Stopwatch.StartNew() : null;
        WallDiag.Start(DiagState);

        _explicitAlbums = explicitAlbums;
        _favoriteAlbums = favoriteAlbums;
        // The library's name only titles the one card for loose tracks at the root, so it can go stale on
        // a reused card. A different library is a different wall anyway, so it is rebuilt then.
        var reusable = libraryName == _libraryName;
        _libraryName = libraryName;
        _facts = facts;

        var previous = reusable
            ? new Dictionary<string, AlbumCard>(_cardByFolder, StringComparer.OrdinalIgnoreCase)
            : null;

        _cards.Clear();
        _cardByFolder.Clear();
        foreach (var folder in folders)
        {
            if (previous is null || !previous.TryGetValue(folder, out var card))
                card = new AlbumCard(folder, libraryName, _metrics);
            // The name is set on every rebuild, whether the card is reused or new. An album retagged in
            // place keeps its folder. So the card that survives the rebuild is exactly the one whose
            // heading has gone stale.
            var f = FactsFor(folder);
            card.Name(f.Album, f.Artist);
            // Also read on every rebuild, for the same reason as the name. A reused card outlives the tag
            // edit that made its record explicit or stopped it being so, and the click that made it a
            // favourite.
            card.IsExplicit = _explicitAlbums.Contains(folder);
            card.IsFavorite = _favoriteAlbums.Contains(folder);
            _cards.Add(card);
            _cardByFolder[folder] = card;
        }

        SortCards();

        // Records that are no longer on the wall cannot be given back by the sweep. The sweep only looks
        // at what is waiting. So the stream is told to drop what it holds for records that have left.
        Stream.ForgetGone(card => ReferenceEquals(
            _cardByFolder.TryGetValue(card.Folder, out var still) ? still : null, card));

        // The card set has been replaced, so the busy count is worked out again here. This is the one
        // place it is counted from scratch. See RecountBusyCards.
        RecountBusyCards();

        // The packed collection is read once, in one go. The loader is woken to stream in whatever is not
        // yet in it. Reused cards already hold their pictures. So a re-feed of the same shelf, such as a
        // favourite being marked or a cover arriving, finds nothing to do and stops.
        Stream.TierMoved(dropHeld: false);

        // The collection is read in one sweep, not a record at a time. The sweep has no cap. What is
        // capped is the number of PIXELS it decodes ahead of the screen (see CoverHold). The bytes it
        // gathers are what the pack is written from anyway.
        Stream.Warm();
        Load();

        _band.OnAlbumsChanged(sameLibrary: reusable);

        // The open drawer wears the sticker. So a feed that carries a new answer for the record it shows
        // must reach it. A mark made at another desk arrives this way.
        if (_band.Current is { } open && _cardByFolder.TryGetValue(open.Card.Folder, out var openCard))
            open.IsFavorite = openCard.IsFavorite;

        var saysEmpty = _cards.Count == 0 && empty is not ShelfEmpty.Reading;
        if (saysEmpty)
            ShelfEmptyHint.Dress(empty, EmptyIcon, EmptyText);
        EmptyHint.Visibility = saysEmpty ? Visibility.Visible : Visibility.Collapsed;
        Regroup();

        if (fedClock is not null)
            WallDiag.Feed(_cards.Count, fedClock.Elapsed.TotalMilliseconds);
    }

    /// <summary>The now-playing file, so the matching line in any open band turns green.</summary>
    public void SetPlaying(string? filePath)
    {
        _playingPath = filePath;
        foreach (var row in _rows)
            if (row.Band is { } band)
                ApplyPlaying(band.AllTracks);
    }

    // ── right-click menu (the wall builds it; the page does the work) ──

    private void Card_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumCard card)
            return;

        var menu = BuildAlbumMenu(card);
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement fe)
            menu.ShowAt(fe);
        args.Handled = true;
    }

    /// <summary>The card's menu, built by the shared factory. The wall and the album list show the same
    /// menu on the same record, so it is made in one place.</summary>
    private MenuFlyout BuildAlbumMenu(AlbumCard card) => AlbumMenuFactory.Build(
        card.Folder,
        new AlbumMenuHooks(
            MenuStateProvider, MenuInvoked, PlaylistEntriesProvider,
            AddFolderToPlaylist, NewPlaylistFromFolder),
        (FontFamily)Resources["Fa"],
        (FontFamily)Resources["FaRegular"],
        AlbumMenuPlace.Collection);

    private void ApplyPlaying(IReadOnlyList<AlbumTrackItem> items)
    {
        foreach (var item in items)
            item.IsPlaying = item.IsTrack && _playingPath is { } p
                && string.Equals(item.FilePath, p, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The open card's ring before its drawer has read its sleeve. The ring uses the record's own tint,
    /// muted the way <see cref="SleeveGround"/> mutes a mean. So the ring is in the same family as the
    /// ground it is about to cross into. The DRAWER opens on the card plate instead. A ring in a thin
    /// white would not be a mark on the card at all, and a ring has to be a mark.
    /// </summary>
    private static Brush SeedGround(Color tint) => new SolidColorBrush(CoverColor.Ground(tint));

    // ══ the loader ══
    //
    // The wall says what it is showing and what size it wants. CoverStream decides what to read next, and
    // when. The pack, the workers, the frame budget, the job order, the off-screen sweep and the
    // first-run pass are all in that class. The visualizer's wall shares it. What is left in this file is
    // only what belongs to THIS wall: which rows are up, how big its tiles are, and what it paints with a
    // record's colour.

    /// <summary>
    /// The ROWS the repeater is holding an element for. They are maintained by realize and recycle.
    ///
    /// <para>Rows, not cards. A change of tile size re-chunks the wall. <see cref="SyncRow"/> does that
    /// by changing each row's card collection IN PLACE. The row keeps its element, so nothing is realized
    /// and nothing is cleared. A set of cards captured at realize would go stale as soon as the zoom
    /// changes. It would list the records that used to stand in those rows, not the ones that stand there
    /// now. The loader would then look past everything on screen, and never fetch the sharper pictures
    /// that the new size asks for. A row stays stable across that change. What is IN it is read when the
    /// question is asked.</para>
    /// </summary>
    private readonly HashSet<AlbumRow> _onScreen = new();

    /// <summary>The records standing in the rows on screen, read now rather than remembered. This is a
    /// screenful of rows and their cards. It is small, and it is correct whatever the wall has just done
    /// to itself.</summary>
    private IEnumerable<ICoverCard> CardsOnScreen()
    {
        foreach (var row in _onScreen)
            foreach (var card in row.Cards)
                yield return card;
    }

    /// <summary>The edge the resident tier is cut at on THIS display. It is the size that the SMALLEST
    /// tile the slider can ask for needs. At the bottom of the slider, a realized row then has nothing to
    /// fetch. It is 128 on an ordinary screen, and 192 on a doubled one, because the ladder is in
    /// physical pixels. It is a floor, not a size that follows the tile. That is why this wall keeps what
    /// it holds when the figure changes. The visualizer's wall throws it away instead.</summary>
    private int ResidentEdge() => ArtScaler.ResidentEdgeFor(MinArt, XamlRoot?.RasterizationScale ?? 0);

    private CoverStream? _stream;

    /// <summary>The stream. It is built the first time it is wanted, not in the constructor. The
    /// constructor would be too early: the stream closes over hooks that the host fills in afterwards,
    /// and over a XamlRoot that the control does not have until it is in a window.</summary>
    private CoverStream Stream => _stream ??= new CoverStream(DispatcherQueue, new CoverStream.Wall(
        Collection: () => (IReadOnlyList<AlbumCard>)_cards,
        OnScreen: CardsOnScreen,
        WantedEdge: CoverDecodeEdge,
        ResidentEdge: ResidentEdge,
        PackScope: () => PackScope,
        CollectionRoot: () => CollectionRoot?.Invoke(),
        Read: (folder, edge, ct) => CoverLoader is { } load ? load(folder, edge, ct) : Task.FromResult<byte[]?>(null),
        Tints: TintReading.OnFirstRead,
        Hold: CoverHold.SharpOverResident)
    {
        // The corner cloud is read behind every cover on screen. A mark that arrives a moment late costs
        // nothing. A picture that arrives late means the wall is not working.
        Errand = card => ReadBadgeAsync((AlbumCard)card),
        ErrandWanted = card => card is AlbumCard { BadgeRead: false },

        // The open card's ring is its drawer's ground: the colour the drawer opens on. It is replaced by
        // the sleeve's own ground when the drawer reads it, so the ring and the drawer look like one.
        Tinted = (card, colour) =>
        {
            if (card is AlbumCard album)
                album.Ring = SeedGround(colour);
        },
        Spent = WallDiag.Spent,
    });

    /// <summary>The pixel edge a cover is decoded at for the tile size the wall is in now. It is the
    /// shared ladder, so this wall and the visualizer's wall answer the same question in the same way.
    /// See <see cref="ArtScaler.DecodeEdgeFor"/>.</summary>
    private int CoverDecodeEdge() => ArtScaler.DecodeEdgeFor(_metrics.Art, XamlRoot?.RasterizationScale ?? 1);

    /// <summary>Settles the tier against the display the window is on. This is asked on every layout,
    /// because that is the first moment there IS an answer. The scale comes from the XamlRoot, which a
    /// control does not have until it is in a window. The wall is fed before that happens.</summary>
    private void RefreshResidentEdge() => Stream.TierMoved(dropHeld: false);

    /// <summary>Wakes the loader. It is cheap, and it is safe to repeat. Call it for anything that could
    /// change what is worth doing: a realize, a feed, a resize, or a cover being replaced.</summary>
    private void Load() => Stream.Wake();

    /// <summary>The bucket the wall was last seen decoding at. This lets a resize tell a real change of
    /// density from the slider moving inside the bucket it is already in.</summary>
    private int _coverEdge;

    /// <summary>The tile size crossed a decode bucket. Nothing is queued and nothing is cancelled. The
    /// cards on screen stop matching what the stream asks for, and the stream is woken to notice. Each
    /// card keeps the picture it has until its replacement arrives. So the wall sharpens in place instead
    /// of blinking.</summary>
    private void RefreshCoverDensity()
    {
        var edge = CoverDecodeEdge();
        if (edge == _coverEdge)
            return;
        _coverEdge = edge;
        Load();
    }

    /// <summary>A cover's bytes, as pixels at the size a square needs. The drawer and the album list use
    /// this too, so all three answer the question in the same way. See
    /// <see cref="CoverDecoder"/>.</summary>
    internal static Task<WriteableBitmap> DecodeAsync(byte[] bytes, int edge) => CoverDecoder.Async(bytes, edge);

    /// <summary>
    /// Hangs the drawer under <paramref name="owner"/>. It is taken off whatever row had it, and the
    /// layout is told which row is now the tall one.
    ///
    /// <para>That is why this exists, rather than a plain <c>row.Band = …</c> write in several places.
    /// The wall does not estimate its own length (see <see cref="AlbumWallLayout"/>). It works the length
    /// out from the row height and the drawer's height. Once the row that carries the drawer is scrolled
    /// out of the realized range, the wall cannot see for itself which row that is. A row with a drawer
    /// attached, without the layout being told, reports a length several hundred points short. A scroller
    /// is then clamped back up from the end that length claims. That is the fault this design
    /// prevents.</para>
    ///
    /// <para><paramref name="owner"/> null detaches everything and opens nothing.</para>
    /// </summary>
    private void HangBand(AlbumRow? owner, AlbumBand? band) => ReshapeHoldingReader(() =>
    {
        foreach (var row in _rows)
            if (!ReferenceEquals(row, owner))
                row.Band = null;

        if (owner is not null)
            owner.Band = band;

        RowsLayout.BandRow = owner is null || band is null ? -1 : _rows.IndexOf(owner);
    });

    /// <summary>
    /// Every change to which row carries the drawer goes through here. The reader stays on the record they
    /// were looking at.
    ///
    /// <para>The drawer is the one row on the wall that is not a row tall. It can be thousands of points
    /// tall. For example, a compilation measures 2745 against rows of 246. If it is taken off a row ABOVE
    /// the reader, every row below it moves up by its whole height. The end of the wall moves up with
    /// them. A reader standing past that new end is CLAMPED to it. This is the fault the wall guards
    /// against. Open a big record, scroll down until it and its drawer are off the top, then click
    /// another record. The wall would fall out from under the click. This is the same clamp that
    /// <see cref="AlbumWallLayout"/> is written to avoid, arrived at from the other side. There the
    /// wall's length was wrong. Here it is right, and it changes underfoot.</para>
    ///
    /// <para>So the offset walks by exactly as far as the row at the top of the viewport walks. This is
    /// measured with <see cref="AlbumWallLayout.TopOf"/> on both sides, not with the elements. The layout
    /// pass that would place them has not run yet. So the arithmetic is the same sum that pass will use.
    /// Taking the drawer away only ever LOWERS the offset, which no extent can refuse. So the wall
    /// arrives shorter, under a reader already standing where it leaves them.</para>
    ///
    /// <para>Not yet verified. A re-seat that moves the drawer to a row above the reader asks the offset
    /// to go DOWN the wall before the pass that lengthens it has run. <c>ChangeView</c> then clamps
    /// against the length the wall still has. That is a reflow, where the wall moves under the reader
    /// anyway. It is no worse than doing nothing.</para>
    /// </summary>
    private void ReshapeHoldingReader(Action reshape)
    {
        var anchor = FirstVisibleRow();
        var was = anchor < 0 ? 0 : RowsLayout.TopOf(anchor);

        reshape();

        if (anchor < 0)
            return;
        var to = AlbumWallRows.OffsetHolding(Scroller.VerticalOffset, was, RowsLayout.TopOf(anchor));
        if (Math.Abs(to - Scroller.VerticalOffset) < 0.5)
            return;
        if (WallDiag.On)
            WallDiag.Open($"held row {anchor} — offset {Scroller.VerticalOffset:0.#} → {to:0.#}"
                + $" | extent {Scroller.ExtentHeight:0.#}, viewport {Scroller.ViewportHeight:0.#}");
        Scroller.ChangeView(null, to, null, disableAnimation: true);
    }

    /// <summary>Takes <paramref name="closing"/> off whatever row is holding it, and nothing else. The
    /// match is by identity. A newer open may have taken the wall over while this one was folding shut.
    /// Clearing the row it landed on would leave that drawer showing, with the wall sized for no drawer
    /// at all. The layout is told only when a row actually gave one up.</summary>
    private void DropBand(AlbumBand closing) => ReshapeHoldingReader(() =>
    {
        var dropped = false;
        foreach (var row in _rows)
        {
            if (!ReferenceEquals(row.Band, closing))
                continue;
            row.Band = null;
            dropped = true;
        }
        if (dropped)
            RowsLayout.BandRow = -1;
    });

    /// <summary>
    /// The album drawer's state machine. It is the one place that knows which album is expanded, holds
    /// its live band, and performs every transition: open, close, same-row switch, and re-seat after a
    /// reflow. The look of the drawer (unfold, snap, fold) is a property of the band model
    /// (<see cref="AlbumBand.AnimateOpen"/> and <see cref="AlbumBand.IsClosing"/>). The element applies
    /// it on realize. So nothing has to chase a recycled element to force it open.
    /// </summary>
    private sealed class BandConductor(AlbumWallView b)
    {
        private string? _folder;      // the expanded album, or null when closed (the one source of truth)
        private AlbumBand? _current;  // the live band while open

        /// <summary>The expanded album's folder, for the host's cover-refresh check.</summary>
        public string? ExpandedFolder => _folder;

        /// <summary>The open drawer itself. Whoever re-reads its lines in place uses this. The cloud
        /// marks are given to the tracklist that is on screen, rather than to a rebuilt one.</summary>
        public AlbumBand? Current => _current;

        /// <summary>Click on a card: toggle its drawer.</summary>
        public void Toggle(AlbumCard card)
        {
            if (string.Equals(_folder, card.Folder, StringComparison.OrdinalIgnoreCase))
                Close();
            else
                Open(card);
        }

        /// <summary>
        /// Called when the album feed is replaced. The drawer shuts, unless the record it shows is still on
        /// the wall.
        ///
        /// <para>Dropping the conductor's own state is not enough. The band stays ATTACHED to its row
        /// (<see cref="AlbumRow.Band"/>) until somebody detaches it. The row it hangs under is about to
        /// be filled with whatever cards the new feed puts there. If the shelf no longer holds the open
        /// record, its drawer would stay open under a stranger's sleeve, with the pointer on it.
        /// Switching from a friend's shelf back to your own does this. The record you had open is then
        /// read as an album by whoever stands in that column now. Search, the favourites filter, an
        /// offload and a delete all narrow the wall in the same way.</para>
        ///
        /// <para><paramref name="sameLibrary"/> false means a different collection entirely. Every card
        /// is a fresh instance. So even a folder that appears in both is not the card the open band was
        /// built from. The drawer then goes with the wall it belonged to.</para>
        /// </summary>
        public void OnAlbumsChanged(bool sameLibrary = true)
        {
            // A different collection is a different set of records. A folder that appears in both is not
            // the same album, because a borrowed shelf and your own can name the same one. The remembered
            // grounds are keyed by folder, so they are dropped with the wall they were read from.
            if (!sameLibrary)
                b.ForgetGrounds();

            if (_folder is not { } f)
                return;
            if (AlbumDrawerSurvival.Survives(f, sameLibrary, b._cardByFolder.ContainsKey))
                return;
            // The drawer snaps away rather than folding. The record is already off the wall, and a fold
            // would play over rows that are being re-filled underneath it.
            Close(animate: false);
        }

        /// <summary>The wall reflowed and the column count changed. The open band is re-seated into its
        /// card's new row and column. It snaps straight to open, with no re-animation on a
        /// resize.</summary>
        public void Reseat()
        {
            if (_folder is not { } f || !b._cardByFolder.TryGetValue(f, out var card) || _current is not { } band)
                return;
            var owner = b._rows.FirstOrDefault(r => r.Cards.Contains(card));
            if (owner is null)
                return;

            Ring(card);
            band.Reseat(ColumnOf(owner, card));
            band.AnimateOpen = false;   // applied on rebind, so the drawer snaps open with no gap
            b.HangBand(owner, band);
        }

        /// <summary>Slides the pointers after a card-size change, with no reflow. This keeps each
        /// triangle over its card.</summary>
        public void RefreshPointers()
        {
            foreach (var row in b._rows)
                row.Band?.RefreshPointer();
        }

        /// <summary>Reloads the open band's content in place. Its cover has been replaced.</summary>
        public void Reopen(string folder)
        {
            if (!string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase)
                || _current is not { } band
                || !b._cardByFolder.TryGetValue(folder, out var card))
                return;
            Seed(band, card);
            _ = b.LoadBandAsync(band);
        }

        private async void Open(AlbumCard card)
        {
            var owner = b._rows.FirstOrDefault(r => r.Cards.Contains(card));
            if (owner is null)
                return;

            // Same-row switch: a drawer is already open in THIS row. Morph it to the neighbour in place.
            // The element is kept, with its reserved space, so there is no collapse-then-expand flicker.
            if (_current is { } live && ReferenceEquals(owner.Band, live))
            {
                Ring(card);
                _folder = card.Folder;

                // Pin the drawer at its current height, so the content swap and the async track load do
                // not make it jump. When the neighbour's tracks are in, the height tweens from the old
                // value to the new one (MorphHeight).
                var root = b.FindBandRoot(live);
                double from = root?.ActualHeight ?? 0;
                if (root is not null && from > 0)
                {
                    root.Height = from;
                    root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, root.ActualWidth, from) };
                }

                live.Retarget(card, ColumnOf(owner, card));
                Seed(live, card);
                await b.LoadBandAsync(live);

                // Release the pin. Then, unless a newer open or close replaced this while the tracks
                // loaded, measure the new length and morph to it.
                if (root is not null)
                {
                    root.Height = double.NaN;
                    root.Clip = null;
                    if (from > 0 && ReferenceEquals(_current, live) && ReferenceEquals(owner.Band, live))
                    {
                        // Measured, not laid out. A synchronous UpdateLayout here would run the same
                        // layout cycle that PlayOpen must avoid. That is a full pass over the wall,
                        // entered from inside the drawer it is about to resize. See LengthWanted.
                        b.MorphHeight(root, from, b.LengthWanted(root));
                    }
                }
                return;
            }

            // Fresh open: clear any other drawer, set the final look, load the tracks OFF-SCREEN, then
            // attach the finished drawer with a one-shot unfold. So it opens straight to its final size
            // and colour.
            Ring(card);
            b.HangBand(null, null);

            var band = new AlbumBand(card, ColumnOf(owner, card));
            _current = band;
            _folder = card.Folder;
            Seed(band, card);

            await b.LoadBandAsync(band);

            // A newer open, or a close, may have replaced this while the tracks loaded.
            if (!ReferenceEquals(_current, band)
                || !string.Equals(_folder, card.Folder, StringComparison.OrdinalIgnoreCase))
                return;
            var owner2 = b._rows.FirstOrDefault(r => r.Cards.Contains(card));
            if (owner2 is null)
                return;

            // The wall moves BEFORE the drawer exists. Below the middle of the viewport, the row glides
            // to the top with nothing yet open under it. So no reserved gap glides up the screen. The
            // drawer unfolds once the row has landed. At or above the middle, the wall stays put.
            if (b.WantsLift(owner2))
            {
                await b.GlideRowToTop(owner2);

                // A newer open, or a close, may have replaced this while the wall moved.
                if (!ReferenceEquals(_current, band)
                    || !string.Equals(_folder, card.Folder, StringComparison.OrdinalIgnoreCase))
                    return;
                owner2 = b._rows.FirstOrDefault(r => r.Cards.Contains(card));
                if (owner2 is null)
                    return;
            }

            band.Reseat(ColumnOf(owner2, card));
            band.AnimateOpen = true;   // the element unfolds once on realize
            if (WallDiag.On)
                WallDiag.Open($"hang row {b._rows.IndexOf(owner2)} — offset {b.Scroller.VerticalOffset:0.#},"
                    + $" extent {b.Scroller.ExtentHeight:0.#}");
            b.HangBand(owner2, band);
        }

        /// <param name="animate">Fold the drawer shut (a click on its own card), or take it away in the
        /// frame (the record has left the wall, see <see cref="OnAlbumsChanged"/>). The snap path never
        /// awaits. So a feed that calls it has the row detached before it regroups.</param>
        private async void Close(bool animate = true)
        {
            _folder = null;
            foreach (var c in b._cards)
                c.IsExpanded = false;
            b._bandCts?.Cancel();   // stop any content load in flight for the band being closed

            var closing = _current;
            _current = null;

            // The lines are going away, so the breath must not outlive them. This is an unconditional
            // stop. The timer stops with the last mark that wanted it.
            b._lineBreath.Stop();

            if (closing is null)
                return;

            // Found while the drawer is still seated. The element is located by the band it shows.
            // Detaching first would leave nothing to find, and nothing to reset.
            var root = b.FindBandRoot(closing);
            if (animate)
            {
                closing.IsClosing = true;   // keep the realize-time sync off the element while it folds
                if (root is not null)
                    await b.PlayClose(root);
            }
            else
            {
                // An unfold still in flight owns the element. It would keep writing its height after the
                // reset below, and the recycled drawer would then come back invisible.
                b._bandAnim.Stop();
            }

            // Detach by identity, because a new open may have taken over meanwhile. See DropBand.
            b.DropBand(closing);
            closing.IsClosing = false;

            // Never leave the drawer folded. The fold leaves the element at Height 0 and Opacity 0, with
            // a clip cut to nothing. Uno recycles the element for the row's NEXT drawer. Any later path
            // that snaps rather than unfolds, such as a re-seat or a rebind, would inherit the folded
            // state, and the row would then show nothing. So the element is reset once it is detached,
            // unless a newer tween has already taken it over.
            if (root is not null && !b._bandAnim.Owns(root))
            {
                if (root.RenderTransform is ScaleTransform s)
                    s.ScaleY = 1;
                root.Height = double.NaN;
                root.Clip = null;
                root.Opacity = 1;
            }
        }

        /// <summary>The expand ring. Only the open card wears it.</summary>
        private void Ring(AlbumCard card)
        {
            foreach (var c in b._cards)
                c.IsExpanded = false;
            card.IsExpanded = true;
        }

        /// <summary>Seeds the drawer's look from the card: its cover (which may be null), and the ground
        /// it opens on. That is the record's own ground, whole, where the wall has read it before.
        /// Otherwise it is the tint-derived colour that the sleeve's ground will cross over. If the album
        /// has no tint at all, the default flat ground is used. The real cover reloads over this in
        /// <see cref="LoadBandAsync"/>.</summary>
        private void Seed(AlbumBand band, AlbumCard card)
        {
            // The best picture the card has: its sharp layer when one is up, and the resident one
            // otherwise. It is a placeholder either way, because it is cut for a tile and the drawer is
            // bigger than a tile. But it fills the sleeve on the frame the drawer opens. The read at the
            // drawer's own size lands over it. See LoadBandAsync.
            band.Cover = card.Sharp ?? card.Cover;

            // The sleeve's ground belongs to the sleeve being read, not to the drawer. A drawer is
            // re-seated and re-pointed at neighbours without ever being rebuilt. A ground left up here
            // would show the last record's sleeve under this one's tracks until the read lands. So it is
            // set to zero, and the new record's own ground crosses in over the colour set below.
            band.GroundOn = 0;
            band.Ground = null;
            band.Wash = null;
            band.Veil = 0;

            // Opened before: the ground is already in hand, so the drawer opens WEARING it. There is
            // nothing to read and nothing to cross. See AlbumWallView._grounds.
            if (b._grounds.TryGetValue(card.Folder, out var known))
            {
                b.ShowBandGround(band, known.Ground, known.Wash, cross: false);
                return;
            }

            // Otherwise the drawer opens on the plate (the template's own brush). The triangle stands in
            // the same thin white, so the two agree on the first frame as well as on every frame after
            // it.
            band.PointerBrush = BandPointer.Ramp(BandPointer.Plate);
        }
    }
}

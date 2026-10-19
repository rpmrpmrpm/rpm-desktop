using System.ComponentModel;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One record on the wall: what it is called, who it is by, where it lives, and how big its tile is
/// drawn. The picture is fetched when the row it is in is realized (see
/// <see cref="PegasusChrome.LibraryWall_ElementPrepared"/>), a collection is thousands of records and
/// the wall shows a few dozen.
/// </summary>
[RpmBindable]
public sealed partial class PegasusAlbumCard : INotifyPropertyChanged, Services.Art.ICoverCard
{
    // Init-only and defaulted rather than required: Uno's bindable metadata generator stands one of these
    // up with no arguments to describe the type to the binder, and a required member is a type it cannot
    // build.
    public string Name { get; init; } = "";
    public string? Under { get; init; }

    /// <summary>Where the record is, as the collection stores it: relative, forward slashes.</summary>
    public string Folder { get; init; } = "";

    /// <summary>Whether the record holds an explicit track, the E beside its title. ANY track, not most
    /// of them: one explicit song is a thing a listener would want to know about a record, and it is
    /// exactly what a majority rule would hide.</summary>
    public bool IsExplicit { get; set; }

    /// <summary>When the record joined the collection, and how much of it has been listened to. Neither
    /// is drawn on the tile: they are here because the wall can be put in their order, and the sort is
    /// one pass over the cards rather than a second query. See <see cref="AlbumSort"/>.</summary>
    public long AddedAt { get; set; }

    public int Plays { get; set; }

    /// <summary>Whether the listener has loved this record, what the heart in the tools narrows the wall
    /// to. Re-read on every fill rather than carried, because a mark made on the record screen does not
    /// rebuild the collection.</summary>
    public bool IsFavorite { get; set; }

    // There is no cancellation token here, and no "has it been asked" flag. The wall does not start a
    // read for each tile as it appears. The loader pulls instead: it asks what is worth doing now and
    // does one of those, so a record scrolled away simply stops being the answer. See
    // PegasusChrome.LibraryLoad.

    /// <summary>The pixel edge this one's picture was actually DECODED at, which is neither the edge it
    /// is drawn at nor the edge of the thumb it came from. Held so a window resize can tell the tiles
    /// whose pictures are now the wrong density from the ones already right.</summary>
    int Services.Art.ICoverCard.CoverEdge { get => CoverEdge; set => CoverEdge = value; }

    internal int CoverEdge { get; set; }

    /// <summary>
    /// The record's cover at the resident edge, held for as long as the wall is, the one picture a tile
    /// never gives back.
    ///
    /// <para>What makes a fling instant. Every record in the collection has one shortly after the wall is
    /// filled, so a tile realized mid-scroll has something to paint on the frame it appears rather than
    /// after a disk read, and a tile reclaimed falls back to it rather than to nothing.</para>
    /// </summary>
    ImageSource? Services.Art.ICoverCard.Tiny { get => Tiny; set => Tiny = value; }

    internal ImageSource? Tiny { get; set; }

    private ImageSource? _sharp;

    /// <summary>
    /// The full-size picture, drawn on its own layer OVER <see cref="Cover"/> and faded in.
    ///
    /// <para>Separate from the resident one rather than replacing it, because a tile that swaps a small
    /// picture for a large one in a single assignment shows a sudden jump. With two layers the large
    /// picture fades in over the small one. It is also the layer that costs memory, so it is the one an
    /// off-screen record gives back.</para>
    /// </summary>
    public ImageSource? Sharp
    {
        get => _sharp;
        set { _sharp = value; Changed(nameof(Sharp)); }
    }

    /// <summary>The encoded bytes behind <see cref="Tiny"/>, held so the resident pack can be written from
    /// what the wall actually read rather than by reading the collection a second time.</summary>
    byte[]? Services.Art.ICoverCard.ResidentBytes { get => ResidentBytes; set => ResidentBytes = value; }

    internal byte[]? ResidentBytes { get; set; }

    /// <summary>No art anywhere for this record. Said once so the loader stops offering it as work, a
    /// collection of coverless records must not cost a disk read apiece on every pass.</summary>
    bool Services.Art.ICoverCard.CoverMissing { get => CoverMissing; set => CoverMissing = value; }

    internal bool CoverMissing { get; set; }

    /// <summary>The edge a read came back empty at, so the loader does not ask again at that size and does
    /// ask again at any other, a read that failed is not a record without art. See
    /// <see cref="Services.Art.CoverTier.WorthAsking"/>.</summary>
    int Services.Art.ICoverCard.MissedEdge { get => MissedEdge; set => MissedEdge = value; }

    internal int MissedEdge { get; set; }

    /// <summary>The colour read off the cover, kept with it in the pack. Reading it is a full decode, so
    /// it is read once per record and never again.</summary>
    Windows.UI.Color? Services.Art.ICoverCard.Tint { get => Tint; set => Tint = value; }

    internal Windows.UI.Color? Tint { get; set; }

    /// <summary>The three colours the ROOM wears while this record is open, worked out once off the
    /// cover's own bytes and kept, a listener stepping in and out of four records must not decode four
    /// covers twice. Not the same thing as <see cref="Tint"/>, which is one colour for a ring and a
    /// pack; see PegasusChrome.PaintRoomFromRecord.</summary>
    internal PegasusChrome.RoomColours? Room { get; set; }

    /// <summary>
    /// The tile's side, held once for the whole wall rather than once per record.
    ///
    /// <para>Every tile is the same size (the row holds eight of them whatever the window is doing) so
    /// there is one figure and every card points at it. If the size were a property ON each card, a window
    /// resize would write the same number onto every record and raise a change for each: a few thousand
    /// notifications on the UI thread per step of a drag, for the forty tiles the listener can actually
    /// see. One shared object is one write and one notification, and the bindings that care are the
    /// realized ones. The library window's wall holds its card size the
    /// same way (see AlbumTileMetrics).</para>
    /// </summary>
    public PegasusTileMetrics Metrics { get; init; } = new();

    private ImageSource? _cover;
    public ImageSource? Cover
    {
        get => _cover;
        set { _cover = value; Changed(nameof(Cover)); }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The one live tile size the whole wall binds to: the wall writes it, every card follows.
/// See <see cref="PegasusAlbumCard.Metrics"/>. Starts at the shipped side so a tile drawn before the
/// first measure is not a point wide.
///
/// <para>The writing under a tile is sized here too, off <see cref="Scale"/>: the figure the mini player
/// above the wall is sized by (PegasusChrome.QueueScale), so the wall and the player read at one size,
/// as home does. One write for the whole wall, as the edge is.</para></summary>
[RpmBindable]
public sealed partial class PegasusTileMetrics : INotifyPropertyChanged
{
    private double _edge = 168;

    public double Edge
    {
        get => _edge;
        set
        {
            if (Math.Abs(_edge - value) < 0.5)
                return;   // a drag that moved less than half a point is not a new layout
            _edge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Edge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleRoom)));
        }
    }

    private double _scale = 1;

    /// <summary>What every size of the writing under a tile is multiplied by.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (value <= 0 || Math.Abs(_scale - value) < 0.001)
                return;
            _scale = value;
            foreach (var name in Sizes)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Every property that changes with <see cref="Scale"/>, raised by name.</summary>
    private static readonly string[] Sizes =
    [
        nameof(Scale), nameof(Title), nameof(TitleBox), nameof(TitleRoom),
        nameof(Line), nameof(LineBox), nameof(Badge),
    ];

    // The sizes of home's record cards (HomeTextSizes.CardTitle, CardTitleBox, CardLine, CardLineBox,
    // Badge), on the same figure, so a record reads the same on the wall as in home's rows: the album
    // name in bold, the spacing of the text under it and the spacing between covers.

    /// <summary>The record's name, in SemiBold as home writes it.</summary>
    public double Title => 12 * _scale;

    /// <summary>The name's line, held whether or not there is a name: see the card template on why every
    /// tile's two lines are the same height.</summary>
    public double TitleBox => 16 * _scale;

    /// <summary>How wide the name may be before it trims: the sleeve, less the E and the gap before it,
    /// which stand right after the name as on home's cards. A StackPanel measures its children with
    /// unlimited width, so without this cap nothing would trim.</summary>
    public double TitleRoom => Math.Max(0, _edge - Badge - BadgeGap);

    /// <summary>The gap between the name and its E: the StackPanel's Spacing in the card
    /// template.</summary>
    private const double BadgeGap = 4;

    /// <summary>The line under the name: the act.</summary>
    public double Line => 11 * _scale;

    public double LineBox => 15 * _scale;

    /// <summary>The E, square.</summary>
    public double Badge => 12 * _scale;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One row of the wall: its eight records.
///
/// <para>The wall is made of rows, not a flow of tiles, because a row is the unit the repeater
/// realizes and recycles. A press on a record opens it on its own screen (see PegasusChrome.Record).
/// Eight covers to a row is eight covers' worth of work per realize rather than one.</para>
/// </summary>
[RpmBindable]
public sealed partial class PegasusAlbumRow
{
    /// <summary>The row's records. Its own list type because it is bound to an ItemsSource, which under
    /// Native AOT needs a type CsWinRT generated WinRT interfaces for; see
    /// <see cref="Common.PegasusAlbumCardList"/> (RPM007).</summary>
    public Common.PegasusAlbumCardList Cards { get; init; } = [];
}

/// <summary>
/// The collection, full screen, in the room.
///
/// <para>Pegasus is mainly a picture of the record playing. This screen serves the two moments when a
/// listener wants something else from it: when they are looking for the next record, and when
/// nothing is playing. It shows the whole library, over the room, with the transport at the top.</para>
///
/// <para>The room behind is not a background drawn here. The layer is over the GL stage and the host is
/// asked to draw the bare room while it is up (see <see cref="LibraryShown"/>), so what is behind the
/// covers is the visualizer itself, still running on whatever is playing.</para>
///
/// <para>Pressing a record opens it on its own screen (see PegasusChrome.Record), with its tracks
/// beside its cover.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How many records stand in a row at a scale of 1. Fixed rather than fitted to the width:
    /// the wall is read across as much as down, and a grid that reflowed from eight to seven and back on
    /// a drag of the window's side is a collection that never looks the same way twice. On a tall screen
    /// the covers grow with the rest of Pegasus and fewer stand in a row (see LibraryColumnsAt).</summary>
    private const int LibraryColumns = 8;

    /// <summary>
    /// How many records stand in a row at this scale: eight over the scale, to the nearest whole row.
    /// </summary>
    /// <remarks>
    /// The covers grow by the same figure as the text and home's sleeves (QueueScale, the mini
    /// player's). A row always fills the wall exactly, so the growth is whatever whole number of
    /// columns comes nearest: five at 1.5 (each cover 1.6 times the eighth),
    /// seven at 1.2, eight at 1. The scale follows the window's height, so a drag of its top or bottom
    /// edge across 900 to 1350 points can change the count; a drag of its side cannot.
    /// </remarks>
    private static int LibraryColumnsAt(double scale) =>
        Math.Max(1, (int)Math.Round(LibraryColumns / Math.Max(1, scale)));

    /// <summary>How many records the rows on the wall were cut at. Compared on every size, and the rows
    /// are cut again when the scale asks for another count (see SizeLibrary).</summary>
    private int _wallColumns = LibraryColumns;

    /// <summary>The records the wall's rows were cut from, in the order the tools put them: what a fill
    /// handed over, kept so a change of scale can cut the rows again without asking the
    /// collection.</summary>
    private List<PegasusAlbumCard> _wallCards = [];

    /// <summary>Cuts <see cref="_wallCards"/> into rows of <paramref name="columns"/>. The cards are the
    /// same objects, so nothing decoded is thrown away; only which row each stands in changes.</summary>
    private void CutWall(int columns)
    {
        _wallColumns = columns;
        _onScreenRows.Clear();
        _libraryRows.Clear();
        for (var at = 0; at < _wallCards.Count; at += columns)
            _libraryRows.Add(new PegasusAlbumRow
            {
                Cards = [.. _wallCards.GetRange(at, Math.Min(columns, _wallCards.Count - at))],
            });
    }

    /// <summary>
    /// Cuts the rows again at a new count, keeping the record the listener was looking at at the top.
    /// </summary>
    /// <remarks>
    /// The record at the left of the top row is found by its place in the list (the old row times the old
    /// count), and the wall is put back with the row now holding it at the top. Through HealWall where the
    /// wall is seated and up, which is the one way to make the repeater forget its rows and land on a given
    /// one; otherwise it is seated afresh when it next can be.
    /// </remarks>
    private void RecutWall(int columns)
    {
        var first = _topRow * _wallColumns;
        CutWall(columns);
        if (_libraryRows.Count == 0)
            return;

        _topRow = Math.Clamp(first / columns, 0, _libraryRows.Count - 1);
        if (_seated && WallUp)
        {
            HealWall(_topRow);
            return;
        }

        _seated = false;
        SeatWall();
    }

    /// <summary>The gap between covers, and the wall's inset to the first cover. Held here because the
    /// tile's side is worked out from both. The gap is home's between two sleeves in a row: each card's
    /// room of 8 (CardRoomAround, the Padding in PegasusCardTemplate) on both sides of the row's spacing
    /// of 6. So the row's StackLayout Spacing in the chrome's XAML is this less twice the room, and the
    /// padding written on LibraryScroll is the inset less the room.</summary>
    private const double LibraryGap = 22;
    private const double LibraryEdgeInset = 34;

    /// <summary>The strip the scrollbar takes out of the viewport. The wall is always longer than the
    /// screen, so it is always taken; the tile's side is worked out from what is left, and the tools row
    /// ends on the same line so the search field and the last cover in a row share a right edge.</summary>
    private const double LibraryScrollbarRoom = 16;

    /// <summary>Raised as the collection is shown and hidden. The HOST acts on it: the room behind has to
    /// be the bare room while this is up, and the keys that cycle compositions have nothing to say on a
    /// screen with no record on it, see PegasusPlayer.EffectiveView.</summary>
    public event Action<bool>? LibraryShown;

    /// <summary>Raised when the wall has its records, what the view's own opening cover waits on when it
    /// opens straight onto the collection (see PegasusPlayer.TryEndLoading).</summary>
    public event Action? LibraryFilled;

    /// <summary>Whether the collection is on screen.</summary>
    public bool LibraryUp { get; private set; }

    /// <summary>Whether the wall has been read at least once. A second visit finds it already filled and
    /// has nothing to wait for.</summary>
    public bool LibraryIsFilled => _libraryRows.Count > 0;

    /// <summary>The read that filled the wall. A collection read off the disk that comes back to find the
    /// screen closed (or already filled again) drops what it found.</summary>
    private int _libraryRead;

    private readonly List<PegasusAlbumCard> _libraryCards = [];
    private readonly List<PegasusAlbumRow> _libraryRows = [];

    /// <summary>The tile size every card on the wall points at. See <see
    /// cref="PegasusTileMetrics"/>.</summary>
    private readonly PegasusTileMetrics _tiles = new();

    /// <summary>
    /// Puts the collection up, and fills it if it is not already filled.
    ///
    /// <para>Kept between visits, unlike the drawer's shelf next door: this is a wall of a few thousand
    /// tiles read out of one GROUP BY, and a listener stepping into a record and back out of it should
    /// find the wall where they left it rather than at the top of a fresh one.</para>
    ///
    /// <para><paramref name="immediate"/> for the collection being the screen the VIEW opens on, rather
    /// than one crossed to from a record. There is nothing to cross from then, the composition was never
    /// stood up (see PegasusPlayer.Show) and the screen was black a moment ago, and a wall that dissolves
    /// up from nothing is a wall arriving late over an empty room.</para>
    ///
    /// <para><paramref name="screen"/> is which of the two the collection shows: the wall, or home (see
    /// <see cref="CollectionScreen"/>). Required, because the caller is the only one who knows why the
    /// collection is coming up. A collection that is already up keeps the screen it has.</para>
    /// </summary>
    public void ShowLibrary(CollectionScreen screen, bool immediate)
    {
        if (LibraryUp)
            return;

        LibraryUp = true;
        _collection = screen;

        // Placed, not crossed: the layer itself is arriving. The crosses between the three are the wall's
        // back key, Back and the side menu (see CrossToHome). Set rather than faded also drops a fade still
        // landing from one of those.
        var wall = screen == CollectionScreen.Wall;
        var home = screen == CollectionScreen.Home;
        var radio = screen == CollectionScreen.Radio;
        WallScreen.Visibility = wall ? Visibility.Visible : Visibility.Collapsed;
        _wallScreenFade.Set(wall ? 1 : 0);
        ResetArtistsGenres();   // the collection opens on home, the wall or radio, never on these
        HomeHost.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        _homeScreenFade.Set(home ? 1 : 0);
        _radioScreenFade.Set(radio ? 1 : 0);
        if (home)
            EnsureHome();
        PaintLibraryBack();

        SizeLibrary(QueueScale);   // it may never have been laid out at this window's size
        LayOutLibraryTop();        // …nor stood clear of the keys, if nothing has played this visit
        ResetPlayerAway();         // …and the mini player up, whatever it did on the last visit
        PaintLibraryFavorites();   // …nor had its heart drawn in the state the filter is actually in
        SetLibraryLayerShown(true);
        LibraryLayer.IsHitTestVisible = true;

        if (immediate)
            _libraryFade.Set(1);
        else
            _libraryFade.To(1, ScreenCrossMs, null);
        LibraryShown?.Invoke(true);
        FocusScreenLater(null);   // with the remote in use: the screen's first card

        if (radio)
            ReadRadioIfDue();

        if (screen != CollectionScreen.Wall)
            return;

        UpdateLibraryFade();   // the foot of the wall dissolves from the first frame it is up

        // A screen that has been collapsed takes its next pass with no viewport to speak of, which is
        // exactly the pass that loses the wall's place, see CheckAnchor.
        SeatWall();
        CheckAnchorLater();

        if (_libraryRows.Count == 0)
            FillLibraryAsync();
    }

    /// <summary>
    /// Puts the collection layer in the tree or takes it out, and home with it.
    ///
    /// <para>Home is told by its own Visibility and not left to the layer's. A collapsed ancestor tells its
    /// children nothing, and home steps its banner on a timer and drifts its rooms for as long as its own
    /// Visibility says it is up (see HomeView.Rewind). Every write of the layer's Visibility goes through
    /// here, so home is up exactly while the layer is and home is the screen it shows.</para>
    /// </summary>
    private void SetLibraryLayerShown(bool shown)
    {
        LibraryLayer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ApplyHomeShown();
        ApplyRadioShown();   // radio's rooms drift on the same terms as home's (see PegasusChrome.Radio)
        ApplyArtistsGenresShown();   // …and the genre cards' rooms (see PegasusChrome.ArtistsGenres)
    }

    /// <summary>…and takes it down again, leaving the record turning.</summary>
    public void HideLibrary()
    {
        if (!LibraryUp)
            return;

        LibraryUp = false;

        // The menu goes with the screen it was opened over, and so do radio's wheel and search.
        ShowLibraryMenu(false);
        CloseRadioOverlays();

        // A record open over the collection goes down WITH it, and without crossing back to a wall that
        // is itself leaving, see DropRecord. It is also what puts the room back on whatever is playing.
        // The tuner is a page over radio and goes down on the same terms (see DropTuner). An artist's or a
        // genre's page in front had the room in its colours, which go back to whatever is playing too.
        if (PageRoomInFront is not null && !RecordUp)
            RoomPalette?.Invoke(null);
        DropRecord();
        DropTuner();

        LibraryLayer.IsHitTestVisible = false;
        _libraryFade.To(0, ScreenCrossMs, () =>
        {
            if (!LibraryUp)
            {
                SetLibraryLayerShown(false);
                DropArtistsGenresPages();   // the collection comes back on home; the pages are built again
            }
        });
        LibraryShown?.Invoke(false);
        FocusScreenLater(null);   // with the remote in use: the record turning's play key
    }

    /// <summary>The back key in the top left corner. On the wall it crosses to home; on home it goes to
    /// the record turning, as a press on the mini player does.</summary>
    private void LibraryBack_Click(object sender, RoutedEventArgs e)
    {
        if (_collection == CollectionScreen.Wall)
            CrossToHome();
        else if (IsArtistsGenres(_collection))
            StepBackFromArtistsGenres();
        else
            ShowLibraryMenu(!LibraryMenuOpen);
    }

    /// <summary>
    /// The cover or the words in the mini player were pressed: the collection goes down and the record
    /// turning is shown, on the wall and on home alike. The player is only up while something
    /// is playing, so there is always a record or a station to show.
    /// </summary>
    private void LibraryPlayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        HideLibrary();
    }

    /// <summary>
    /// Reads every record in the collection and lays it out eight to a row.
    ///
    /// <para>Off the skeleton's own GROUP BY on a thread pool thread, like the drawer's shelf: what a
    /// record is CALLED and who it is by are in the mirror, and not one track is read to fill this. No
    /// cap either, which the drawer's shelf has and this must not, a wall the listener was told is their
    /// collection has to be their collection, and the repeater only ever realizes the rows on
    /// screen.</para>
    /// </summary>
    private async void FillLibraryAsync()
    {
        var read = ++_libraryRead;

        var library = Library;
        if (!library.IsOpen)
        {
            ShowLibraryEmpty("Viz_BrowseNoLibrary");
            LibraryFilled?.Invoke();   // nothing to wait for: this IS the answer
            return;
        }

        // A different collection entirely. Everything held is about the last one, the cards and their
        // pictures, what is on screen, what is in flight, and a folder that happens to appear in both
        // is not the same record. This is checked here rather than assumed, because a search and a sort
        // also run through a fill, and those must NOT throw the pictures away.
        var root = library.Root ?? "";
        if (!string.Equals(root, _libraryRootFor, StringComparison.OrdinalIgnoreCase))
        {
            _libraryRootFor = root;
            _libraryByFolder.Clear();
            ForgetLibraryArt();
        }

        // The ring, if this turns out to be slow. It costs nothing when it is not, see ShowLibraryBusy.
        ShowLibraryBusyLater();

        var db = library.Db;
        var search = string.IsNullOrWhiteSpace(_librarySearch) ? null : _librarySearch;

        // The three questions in one hop off the thread that is driving the shader. All three take
        // the SEARCH, the mirror does the narrowing, so a question about a collection of thousands
        // is still one indexed query and the wall never sees the records it is not about.
        var answer = (await Task.Run(() => library.Read(db, "Pegasus's wall", mirror => new LibraryRead(
                mirror.QueryAlbumFacts(search),
                new HashSet<string>(mirror.QueryExplicitAlbums(search), StringComparer.OrdinalIgnoreCase),
                // The same mirror the two questions above it are asked of. This wall lists the
                // listener's OWN records whatever the window is browsing, and reading the hearts off
                // the shelf instead put a friend's marks on this collection's sleeves.
                mirror.FavoriteAlbumFolders()))))
            // Unanswered is an empty wall. A refusal has put the problem screen up over the app and
            // taken Pegasus down; a closed mirror is followed by a Changed that fills the wall again.
            .Match(found => found,
                _ => new LibraryRead([], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        if (read != _libraryRead)
            return;

        HideLibraryBusy();

        // A record open over a collection that has just been read again is a record off the old one.
        HideRecord();

        var (all, cards) = BuildLibraryCards(answer);

        // ── the collection, and the wall, are not the same list ──
        //
        // _libraryCards is THE COLLECTION: it is what the sweep dresses, what the pack is written from,
        // and what the loader falls back to when nothing on screen wants anything. The wall is the rows,
        // and the rows are whatever the three tools currently say.
        //
        // Do not write the filtered set here. The pack is written from this list and ThumbPack.Save
        // REPLACES the file, so a pack written from the favourites alone would overwrite the one
        // holding the whole collection. Clearing the heart would then find nothing packed for any other
        // record, and every record would go back to the disk to be read and cut again.
        //
        // Only a read that asked about ALL of it may replace this. A search narrows the QUESTION, not the
        // library, and the favourites filter is applied after the read rather than in it, so the answer
        // to an unfiltered search is the whole collection whatever the heart says.
        if (search is null)
        {
            _libraryCards.Clear();
            _libraryCards.AddRange(all);
        }

        // The rows are rebuilt on every fill, a search or a sort re-chunks which record stands where,
        // but the CARDS behind them are the ones the wall already had, so nothing decoded is thrown away
        // and a search cleared costs no reads at all. See BuildLibraryCards.
        // At the count this screen's scale asks for (see LibraryColumnsAt).
        _wallCards = cards;
        CutWall(LibraryColumnsAt(QueueScale));

        if (cards.Count == 0)
        {
            ShowLibraryEmpty(search is null && !_libraryFavoritesOnly
                ? "Viz_BrowseNoAlbums"
                : "Viz_LibraryNoMatch");
            LibraryFilled?.Invoke();
            return;
        }

        // The scroller is put back BEFORE the rows go into it. Handing them to a repeater whose viewport
        // is collapsed is the wholesale realize, see SeatWall.
        LibraryEmpty.Visibility = Visibility.Collapsed;
        LibraryScroll.Visibility = Visibility.Visible;

        SizeLibrary(QueueScale);   // the tiles have their side written before the wall is handed over
        _seated = false;
        SeatWall();

        // A different set of records is a different wall, and it starts at the top. Keeping the offset
        // would drop somebody who asked a question halfway down the answer.
        StopWheel();
        LibraryScroll.ChangeView(null, 0, null, disableAnimation: true);
        _topRow = 0;

        // The packed collection is read once, in one go, and then the whole of it is dressed in one sweep
        // the room standing still for the length of it. See WarmResidentAsync.
        //
        // Only when something is actually undressed, though. A search, a sort or the heart hands back
        // records the wall is ALREADY holding pictures for. Raising the warming flag for those would put
        // the host's "reading your collection" cover (a full-screen wait with a percentage) over a
        // collection that is complete, for a filter that has nothing to fetch.
        ReadPackAsync();
        if (AnyUndressed())
        {
            SetWarming(true);
            WarmResidentLater();
        }
        LoadLibraryArt();

        UpdateLibraryFade();
        LibraryFilled?.Invoke();
    }

    /// <summary>Whether any record in the collection still has no picture in hand. What decides that a
    /// fill is a first reading of a library rather than the wall being asked another question about one
    /// it has already read. A walk of the collection, once per fill, against the seconds the alternative
    /// costs.</summary>
    private bool AnyUndressed()
    {
        foreach (var card in _libraryCards)
            if (card.ResidentBytes is not { Length: > 0 } && !card.CoverMissing)
                return true;
        return false;
    }

    /// <summary>What one pass over the mirror comes back with: every record the question matched, which
    /// of them hold an explicit track, and which the listener has loved.</summary>
    private readonly record struct LibraryRead(
        IReadOnlyList<LibraryDatabase.AlbumFactsRow> Facts,
        HashSet<string> Explicit,
        IReadOnlySet<string> Favorites);

    /// <summary>
    /// The records a read matched, as cards, in the order the listener asked for.
    ///
    /// <para>A record still on the wall keeps the CARD it already had, and that is what makes searching
    /// this wall free. A card is where the cover lives, the decoded picture, the bytes behind it, the
    /// colour read off it, the room palette, so building fresh ones would drop every one of those and
    /// send the loader back to the disk for a collection it was holding a moment ago. Type a letter,
    /// delete it, and the wall comes back instantly because it is the same cards in a different order.
    /// The library window's own wall reuses its cards for the same reason (see AlbumWallView.SetAlbums).
    /// </para>
    /// </summary>
    /// <returns>Every record the read matched, and (separately) the ones the wall should actually show.
    /// The first is the collection and is what the sweep and the pack are about; the second is the first
    /// narrowed to the favourites (when the heart is on) and put in the listener's order. A card appears
    /// in both, so the two lists share their pictures rather than copying them.</returns>
    private (List<PegasusAlbumCard> All, List<PegasusAlbumCard> Wall) BuildLibraryCards(LibraryRead answer)
    {
        var all = new List<PegasusAlbumCard>(answer.Facts.Count);
        var cards = new List<PegasusAlbumCard>(answer.Facts.Count);

        foreach (var row in answer.Facts)
        {
            // The library root's own loose tracks group under the empty folder. They are not a record and
            // there is nothing to put on a tile.
            if (string.IsNullOrEmpty(row.Folder))
                continue;

            var favorite = answer.Favorites.Contains(row.Folder);

            var parts = row.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!_libraryByFolder.TryGetValue(row.Folder, out var card))
            {
                card = new PegasusAlbumCard
                {
                    Name = row.Album ?? (parts.Length > 0 ? parts[^1] : row.Folder),
                    Under = row.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : null),
                    Folder = row.Folder,
                    Metrics = _tiles,
                };
                _libraryByFolder[row.Folder] = card;
            }

            // Re-read on every fill, reused card or fresh one: a record retagged in place keeps its
            // folder, so the card that survives is exactly the one whose facts went stale.
            card.IsExplicit = answer.Explicit.Contains(row.Folder);
            card.IsFavorite = favorite;
            card.AddedAt = row.AddedAt;
            card.Plays = row.Plays;

            all.Add(card);
            if (!_libraryFavoritesOnly || favorite)
                cards.Add(card);
        }

        // The rules are AlbumSort's, in Core and tested there, and shared with the window's wall and the
        // phone's: every one of these orders has a tie to break and a missing value to place, and those
        // are the parts that are silently wrong. Only the WALL is put in order, the collection is read
        // in folder order and nothing downstream of it cares.
        var wall = AlbumSort.By(
            cards, _libraryOrdering,
            c => new AlbumFacts(c.Name, c.Under ?? "", c.AddedAt, 0, c.Plays)).ToList();

        return (all, wall);
    }

    /// <summary>Every record this wall has ever built a card for, so a search that narrows it and one
    /// that widens it again hand back the same cards, pictures and all. See
    /// <see cref="BuildLibraryCards"/>.</summary>
    private readonly Dictionary<string, PegasusAlbumCard> _libraryByFolder =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which collection those cards are about. A fill that finds a different root throws them
    /// all away; a fill that finds the same one is a search, a sort or a heart, and keeps them.</summary>
    private string? _libraryRootFor;

    // ── the wheel ──
    //
    // One notch moves the wall a fixed distance, and the wall TRAVELS it over the next few frames rather
    // than arriving. The scroller's own handling sets the offset in a single write. On a list of 34pt
    // lines that looks like a scroll, but on a wall whose rows are a cover tall it looks like a jump from
    // one row to the next. Moving the same distance over several frames is the difference between
    // scrolling and jumping.
    //
    // Caught UNDER the scroller rather than on it. A control's own handling of an event runs before the
    // handlers hung on it, so a handler on the ScrollViewer would be told about a notch the ScrollViewer
    // had already acted on; the ground under the rows is below it in the route, and a notch marked handled
    // there never reaches it. That ground is a transparent Grid rather than the repeater itself, because
    // neither the repeater nor its panels have a fill, and a pointer in the gap between two covers would
    // fall straight through them.
    //
    // The tween is the radio dial's, down to the curve (see RadioDial), and a tween for the same reason it
    // is one there: an animation holds the property it lands on, and everything else that moves this wall,
    // a heal, the scrollbar, the screen opening, writes the offset directly.

    /// <summary>
    /// How far one notch carries the wall, before the drawer's scale.
    ///
    /// <para>A third of a row rather than a half. The covers are the only text on the wall, so the
    /// listener must be able to follow it with their eyes: a notch that moves too far leaves them
    /// working out where they are. The travel underneath has no duration of its own (see TickWheel), so
    /// this is the only thing that sets the pace, and a whole spin of the wheel still covers ground: the
    /// notches add up rather than each restarting the travel.</para>
    /// </summary>
    private const double WheelNotch = 88;

    /// <summary>Roughly how long the wall takes to arrive once the wheel stops. Not a duration the travel
    /// is fitted into (see <see cref="TickWheel"/>, which has no duration) but the time constant of the
    /// approach: the wall covers all but a thousandth of the remaining distance in this.</summary>
    private const double WheelGlideSeconds = 0.22;

    /// <summary>Where the wall is headed, and where this tween believes it currently is.
    ///
    /// <para>Its own position rather than the scroller's, because a <c>ChangeView</c> is not necessarily
    /// answered by the time the next frame reads the offset back; driving the travel off what the scroller
    /// last admitted to would stall it a frame at a time.</para></summary>
    private double _wheelTo, _wheelAt;

    /// <summary>
    /// The offset this tween last handed to the scroller, or NaN before it has written one.
    ///
    /// <para>It tells this tween's own writes apart from anyone else's. It is needed because
    /// <c>ChangeView</c> does not land on the frame it is called. The travel covers forty per cent of
    /// what is left every frame, so a fast spin moves a hundred points in one frame. If the scroller's
    /// offset were compared with where the TWEEN has got to, a write still in flight would look like
    /// someone else moving the wall. The tween would be pulled back to the old offset, advance, and be
    /// pulled back again, and the rows would move a line at a time instead of in one smooth movement.
    /// Compared with the last offset WRITTEN, the scroller either has that offset or the difference is
    /// not ours.</para>
    /// </summary>
    private double _wheelWrote = double.NaN;

    private bool _wheeling;

    private void LibraryWallHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var wheel = e.GetCurrentPoint(LibraryScroll).Properties;
        if (wheel.IsHorizontalMouseWheel || wheel.MouseWheelDelta == 0)
            return;

        // A notch mid-travel moves the TARGET and nothing else. Where the wall is, and how fast it is
        // going, are left exactly as they are, which is the whole point: the travel is never restarted,
        // so a spun wheel is one continuous movement rather than a series of them.
        if (!_wheeling)
        {
            _wheelAt = LibraryScroll.VerticalOffset;
            _wheelTo = _wheelAt;
            _wheelWrote = double.NaN;
            _wheeling = true;
        }

        _wheelTo -= wheel.MouseWheelDelta / 120.0 * WheelNotch * QueueScale;
        e.Handled = true;
    }

    /// <summary>
    /// The travel itself, off the chrome's own tick with everything else that moves a frame at a time.
    ///
    /// <para>An exponential approach, not a curve fitted between two points. A fitted curve has a
    /// beginning, so every notch of a spun wheel would restart it: the wall would speed up, slow down and
    /// speed up again, four times in one flick, and the rows would snap past instead of sliding. An
    /// approach has no beginning to go back to. It moves toward wherever the wheel last pointed it, at a
    /// rate proportional to how far it still has to go, and a notch that arrives mid-travel simply moves
    /// the target further away.</para>
    /// </summary>
    private void TickWheel(double dt)
    {
        if (!_wheeling)
            return;

        if (!WallUp)
        {
            StopWheel();   // the screen went while the wall was still moving
            return;
        }

        // Clamped every frame rather than once at the notch: the extent grows under the wall as rows
        // realize, so a target measured against the estimate of a moment ago aims past the end of it.
        _wheelTo = Math.Clamp(_wheelTo, 0, LibraryScroll.ScrollableHeight);

        // Somebody else has taken the wall, the scrollbar, a heal, the screen opening. Their offset is
        // the true one; this tween catches up to it rather than dragging the wall back.
        //
        // Measured against the last offset WRITTEN, never against where the tween has got to: see
        // _wheelWrote. A write still in flight is the ordinary case at speed, and reading one as a foreign
        // move would make the spin jerk.
        var actual = LibraryScroll.VerticalOffset;
        if (!double.IsNaN(_wheelWrote) && Math.Abs(actual - _wheelWrote) > 24)
        {
            _wheelAt = actual;
            _wheelTo = Math.Clamp(_wheelTo, 0, LibraryScroll.ScrollableHeight);
        }

        // The fraction of what is left to cover in this frame. Expressed against dt so the travel is the
        // same speed whatever the frame rate, and capped so one long frame cannot jump the whole way.
        var k = 1 - Math.Pow(0.001, Math.Min(dt, 0.1) / WheelGlideSeconds);
        _wheelAt += (_wheelTo - _wheelAt) * k;

        var landed = Math.Abs(_wheelTo - _wheelAt) < 0.5;
        if (landed)
            _wheelAt = _wheelTo;

        // Clamped on the way out as well as on the way in. The scroller refuses an offset past its own
        // extent and reports the one it took, and a write we know was refused must not then be read back
        // as somebody else's doing.
        _wheelWrote = Math.Clamp(_wheelAt, 0, LibraryScroll.ScrollableHeight);
        LibraryScroll.ChangeView(null, _wheelWrote, null, disableAnimation: true);

        if (landed)
            StopWheel();
    }

    /// <summary>Ends the travel and leaves the wall exactly where it stands. Called by everything else
    /// that writes the offset, which must not have this putting the wall back a frame later.</summary>
    private void StopWheel()
    {
        if (!_wheeling)
            return;
        _wheeling = false;
        _wheelWrote = double.NaN;
        CheckAnchorLater();   // the scroll that just settled is one the check never got to see
    }

    // ── keeping the wall's place ──
    //
    // A virtualizing repeater places the rows it has NOT realized off an estimate, and only ever corrects
    // that estimate against rows it HAS realized. A layout pass taken with no viewport to speak of realizes
    // nothing at all, and the estimate it comes back with can put every row outside the viewport, nothing
    // realized, so nothing to correct it against, and the wall cannot find its way back on its own. It sits
    // blank, or shows some row halfway down the collection with the scrollbar already hard against the top,
    // and rows shift under the listener as it argues with itself about where they are.
    //
    // The library window's wall guards against this in two ways: it withholds the rows until it has a
    // viewport, and it checks its place after every pass that could lose it. This screen does the same,
    // and needs it more: it is COLLAPSED every time the listener goes back to the record, and it is
    // filled from a read that can answer before the screen it is going onto has been measured once.
    //
    // See AlbumGridLayout.AnchorLost for the three invariants that catch it without knowing anything about
    // the estimate.

    /// <summary>Whether the wall has a viewport worth laying rows out in.</summary>
    private bool _viewportReady;

    /// <summary>Whether the rows have been handed over, so a later size does not seat a wall that is
    /// already up: re-seating re-realizes every row on screen, which is every cover flashing.</summary>
    private bool _seated;

    /// <summary>Whether the repeater has ever realized a row. Before it has, an empty wall is one nobody
    /// has laid out yet rather than one that has lost its place.</summary>
    private bool _everRealized;

    /// <summary>
    /// The topmost row the wall was last seen SHOWING, so a heal can put the listener back where they
    /// were rather than at the top of the collection.
    ///
    /// <para>The first VISIBLE row, not the first realized one. The repeater keeps a cache either side of
    /// the viewport (half a screen of it here, VerticalCacheLength on LibraryWall), so the lowest index
    /// it holds an element for is above what the listener is looking at. Healing to that row would put
    /// them half a screen too high, and inside the first half-screen of the wall it would clamp to row 0
    /// and look like a jump to the top of the collection.</para>
    /// </summary>
    private int _topRow;

    private bool _healing;
    private bool _checkQueued;

    /// <summary>
    /// Hands the rows to the repeater, once there is somewhere to put them.
    ///
    /// <para>The rows are withheld until then, so that the screen arrives instead of hanging: rows given
    /// to a repeater under an unmeasured scroller are realized WHOLESALE, every row in the collection
    /// inflated in one pass, eight covers and sixteen lines at a time, thousands of them, in front of a
    /// shader. Given a viewport, the same repeater realizes a screenful.</para>
    /// </summary>
    private void SeatWall()
    {
        if (_seated || !_viewportReady || _libraryRows.Count == 0)
            return;

        _seated = true;
        LibraryWall.ItemsSource = null;
        LibraryWall.ItemsSource = _libraryRows;
    }

    /// <summary>The wall has been measured: the first honest size is what the rows were waiting for, and
    /// every size after it is a pass that could have cost the repeater its place.</summary>
    private void LibraryScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_viewportReady && e.NewSize.Width > 1 && e.NewSize.Height > 1)
        {
            _viewportReady = true;
            SeatWall();
        }
        CheckAnchorLater();

        // The bar is a template part, there only once the scroller has been laid out: LayOutLibraryTop may
        // have asked before it was (see ScrollerBar).
        ScrollerBar.StartAt(LibraryScroll, WallTop);
    }

    /// <summary>Queues the check for once the pending layout has run: the rows are not placed until that
    /// pass, and checking inline would read the state the wall is on its way out of.</summary>
    private void CheckAnchorLater()
    {
        if (_checkQueued)
            return;
        _checkQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _checkQueued = false;
            CheckAnchor();
        });
    }

    private void CheckAnchor()
    {
        if (_healing || !WallUp || !_viewportReady || _libraryRows.Count == 0)
            return;
        if (LibraryScroll.ActualWidth <= 1 || LibraryScroll.ActualHeight <= 1)
            return;   // no viewport for the place to be right or wrong about

        var first = FirstRealizedRow();
        if (!AlbumGridLayout.AnchorLost(_libraryRows.Count, first, LibraryScroll.VerticalOffset,
                                        _everRealized, FirstRowTop()))
        {
            _lostSeen = 0;
            // Remembered off the VISIBLE row: see _topRow. A blank wall has none to read, which is exactly
            // when the remembered one is needed, so it is only ever written from a healthy pass.
            if (FirstVisibleRow() is var visible && visible >= 0)
                _topRow = visible;
            return;
        }

        // Said twice before it is believed.
        //
        // A wall in the middle of a relayout holds no elements for a moment, and one sample of that reads
        // exactly like a wall that has lost its place for good. The cure makes it worse: healing re-seats
        // the rows, which empties the repeater, which reads as lost again, so the wall does not heal
        // once, it heals in a run, each one snapping to the top and climbing back. That is the skipping.
        //
        // A place that is really lost is still lost on the next pass; a blink is not. Costs one dispatcher
        // turn before a real fault is answered, and nothing at all otherwise.
        if (++_lostSeen < 2)
        {
            CheckAnchorLater();
            return;
        }
        _lostSeen = 0;

        // Blank wall: back to the row they were on. False top: they are already asking for the first row.
        HealWall(first < 0 ? _topRow : 0);
    }

    /// <summary>How many checks in a row have said the place is lost. See <see
    /// cref="CheckAnchor"/>.</summary>
    private int _lostSeen;

    /// <summary>Where the first row's top edge sits relative to the top of the CONTENT, or NaN when the
    /// repeater holds no element for it. The scroller's own top padding comes back off, the wall is
    /// supposed to start that far down, and counting it as displacement would call a wall resting at its
    /// top a lost one, on every rest.</summary>
    private double FirstRowTop()
    {
        if (LibraryWall.TryGetElement(0) is not FrameworkElement row)
            return double.NaN;
        try
        {
            return row.TransformToVisual(LibraryScroll).TransformPoint(default).Y - LibraryScroll.Padding.Top;
        }
        catch
        {
            // An element mid-recycle has no place in the tree to transform from. Not evidence either way,
            // and the next settled scroll asks again.
            return double.NaN;
        }
    }

    /// <summary>The lowest row the repeater currently holds an element for, or -1 for none. Walked over
    /// the realized children (a screenful) rather than asked of every row in the collection.</summary>
    private int FirstRealizedRow()
    {
        var first = -1;
        var count = VisualTreeHelper.GetChildrenCount(LibraryWall);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(LibraryWall, i) is not UIElement child)
                continue;
            var index = LibraryWall.GetElementIndex(child);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }
        return first;
    }

    /// <summary>
    /// The lowest row the listener can actually SEE, the topmost one whose bottom edge is still below the
    /// top of the viewport, or -1 when nothing is placed.
    ///
    /// <para>Not <see cref="FirstRealizedRow"/>. That one answers "what does the repeater hold", which
    /// takes in its cache above the viewport and is the right question for whether the place is lost. This
    /// one answers "where is the listener", which is the right question for where to put them back.</para>
    /// </summary>
    private int FirstVisibleRow()
    {
        var best = -1;
        var bestTop = double.PositiveInfinity;
        var count = VisualTreeHelper.GetChildrenCount(LibraryWall);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(LibraryWall, i) is not FrameworkElement child)
                continue;
            var index = LibraryWall.GetElementIndex(child);
            if (index < 0)
                continue;
            try
            {
                // Against the VIEWPORT this time, padding and all: the question is what the listener has
                // in front of them, and the scroller's top padding is as much out of sight as anything
                // else. That padding also MOVES here, the wall starts lower with the mini player down
                // (see LayOutLibraryTop), so it is not a fixed offset to reason around.
                var top = child.TransformToVisual(LibraryScroll).TransformPoint(default).Y;
                if (top + child.ActualHeight > 0 && top < bestTop)
                {
                    bestTop = top;
                    best = index;
                }
            }
            catch
            {
                // Mid-recycle: no place in the tree to transform from. Not evidence either way.
            }
        }
        return best;
    }

    /// <summary>Lays the wall out again and puts <paramref name="row"/> back at the top. Re-seating the
    /// rows is what drops the repeater's stale estimate (there is no other way to make it forget) and
    /// the covers come back from the thumb cache as the rows realize again, which is quick and needs no
    /// rescan.</summary>
    private void HealWall(int row)
    {
        _healing = true;
        StopWheel();   // nothing travelling may land on top of where the heal puts the wall
        row = Math.Clamp(row, 0, _libraryRows.Count - 1);

        LibraryWall.ItemsSource = null;
        LibraryWall.ItemsSource = _libraryRows;
        LibraryScroll.ChangeView(null, 0, null, disableAnimation: true);
        LibraryScroll.UpdateLayout();

        if (row > 0 && LibraryWall.GetOrCreateElement(row) is FrameworkElement element)
        {
            element.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions
            {
                VerticalAlignmentRatio = 0,
                AnimationDesired = false,
            });
        }

        // Checks are ignored until the heal has actually landed, and one dispatcher turn is not that.
        // The rows realize on the pass after the source is re-seated and the bring-into-view lands on
        // the pass after that, so a check run in between reads the heal's own half-finished state as a
        // fresh lost anchor and heals again. A short clock rather than a turn, and it clears itself
        // whatever the layout does.
        _healTimer ??= CreateHealTimer();
        _healTimer.Stop();
        _healTimer.Start();
    }

    private DispatcherQueueTimer? _healTimer;

    private DispatcherQueueTimer CreateHealTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _healing = false;
            _lostSeen = 0;   // whatever was seen mid-heal was the heal, not the wall
        };
        return timer;
    }

    private void ShowLibraryEmpty(string key)
    {
        LibraryEmpty.Text = Strings.Loc.Get(key);
        LibraryEmpty.Visibility = Visibility.Visible;
        LibraryScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// A row has come into view and been given an element.
    ///
    /// <para>It does NO work. The tiles show the pictures their records are already holding, the resident
    /// ones, in memory since the collection was read, and that is the whole of it: no disk, no decode,
    /// nothing that can arrive late. A fling realizes eight of these a row and every one paints on the
    /// frame it appears. What the row needs beyond that, the loader works out for itself; it is told only
    /// that this row is on screen. See PegasusChrome.LibraryLoad.</para>
    /// </summary>
    private void LibraryWall_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        _everRealized = true;

        if (args.Element is not FrameworkElement { DataContext: PegasusAlbumRow row })
            return;

        _onScreenRows.Add(row);
        foreach (var card in row.Cards)
            // Back before the sweep reached it: it still has its picture and there is nothing to do. This
            // is what makes a row bouncing on the edge of the realization window free.
            LibraryArt.Realized(card);

        LoadLibraryArt();
    }

    /// <summary>
    /// A row has scrolled far enough out that the repeater has taken its element back: it stops being what
    /// the loader works on first, and its records are put down for collection, they keep their big
    /// picture a moment yet.
    ///
    /// <para>The pictures are not given back in the frame the row is reclaimed. The repeater often
    /// reclaims and re-realizes the row at the edge of its window while the estimate settles, and giving
    /// the pictures back each time would read and decode a row's worth of covers again for a row that
    /// has not gone anywhere. See CoverHold.</para>
    /// </summary>
    private void LibraryWall_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is not FrameworkElement { DataContext: PegasusAlbumRow row })
            return;

        _onScreenRows.Remove(row);

        // Anything holding decoded pixels is put down for collection. With one tier at the tile's own size
        // the picture a record holds IS the expensive one, which is why this wall's hold gives back the
        // resident layer too, see CoverHold.OnePicture.
        foreach (var card in row.Cards)
            LibraryArt.Reclaimed(card);
    }


    /// <summary>
    /// A record was pressed: the wall gives way to that one record, on its own screen.
    ///
    /// <para>The library window unfolds the record under its row instead. See the note on RecordLayer
    /// in the XAML for why this screen does not.</para>
    ///
    /// <para>A Click, from the card's plain button: a press of the mouse, or OK on the remote.</para>
    /// </summary>
    private void LibraryCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PegasusAlbumCard card)
            ShowRecordAsync(card);
    }

    /// <summary>The mini player's cover: a press, or OK on the remote, goes to the record turning, as a
    /// press on its words does (see LibraryPlayer_Tapped).</summary>
    private void LibraryPlayerCover_Click(object sender, RoutedEventArgs e) => HideLibrary();

    /// <summary>What a track line spends on everything that is not the name: the number and the two gaps
    /// beside it, the line's own padding, the length at the end, and the E after the title. Taken off the
    /// column's width to leave what the name may have. Before the drawer's scale.</summary>
    private const double RecordTitleFurniture = 138;

    /// <summary>One line of the opened record. Pressing it plays the record FROM there, which is what a
    /// press on the fourth track of a sleeve means everywhere else in this app.</summary>
    /// <param name="titles">Where this line's name is added, so the screen that owns it can cap them all
    /// once the column they stand in has a width. See BuildRecord.</param>
    /// <param name="inList">The screen holds a list rather than a record (see BuildListRecord). A list
    /// numbers its POSITIONS rather than the numbers its songs wear on their own records, and says under
    /// each title who it is by and which record it came off, as the drawer's lines do. Required, because
    /// the two screens read differently and each caller has to say which it is.</param>
    private FrameworkElement RecordTrack(LibraryTrack track, List<TrackInfo> songs, int index, double scale,
                                         List<TextBlock> titles, bool inList)
    {
        var grid = new Grid { ColumnSpacing = 12 * scale };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28 * scale) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var number = new TextBlock
        {
            Text = (!inList && track.TrackNumber > 0 ? track.TrackNumber : (uint)(index + 1)).ToString(),
            FontSize = 13 * scale,
            Opacity = 0.45,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(number, 0);

        var title = new TextBlock
        {
            Text = track.Title ?? track.FileName,
            FontSize = 15 * scale,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titles.Add(title);

        // The name and its E on one line, so the mark falls in after the last letter of the title rather
        // than at the far end of the line beside the length, where it would read as another field about
        // the song instead of a note on its name. The same reading the record screen, the queue's rows
        // and the library window's own open record all give it.
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6 * scale,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(title);
        Grid.SetColumn(titleRow, 1);

        // Built only for the songs that wear one rather than built everywhere and hidden: most lines do
        // not, and a collapsed control is still a control the layout has to walk past.
        if (track.Advisory == ContentAdvisory.Explicit)
        {
            var badge = new ExplicitBadge
            {
                // The badge starts COLLAPSED and un-hides itself off this; a hand-built line has no
                // binding to do it. See ExplicitBadge.
                IsExplicit = true,
                Width = 13 * scale,
                Height = 13 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                // The letter is knocked out of the fill, so it has to match the surface BEHIND the badge
                // the record screen's own near-black here, not the library dark it defaults to.
                LetterBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08)),
            };
            ToolTipService.SetToolTip(badge, Strings.Loc.Get("Library_Explicit"));
            titleRow.Children.Add(badge);
        }

        // A list's line says who each song is by and which record it came off, under its name: the
        // records in a list are a dozen different ones, and the name at the top of the screen says none of
        // that. In the column under the title row, which gives it the column's width to be cut short in.
        FrameworkElement words = titleRow;
        var under = inList
            ? string.Join(" · ", new[] { track.Artist, track.Album }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : null;
        if (!string.IsNullOrWhiteSpace(under))
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 * scale };
            stack.Children.Add(titleRow);
            stack.Children.Add(new TextBlock
            {
                Text = under,
                FontSize = 12.5 * scale,
                Opacity = 0.5,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(stack, 1);
            words = stack;
        }

        var time = new TextBlock
        {
            Text = Duration(track.DurationSeconds),
            FontSize = 13 * scale,
            Opacity = 0.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(time, 2);

        grid.Children.Add(number);
        grid.Children.Add(words);
        grid.Children.Add(time);

        var row = new Border
        {
            Tag = index,
            Child = grid,
            Padding = new Thickness(12 * scale, 10 * scale, 12 * scale, 10 * scale),
            CornerRadius = new CornerRadius(6 * scale),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.PointerEntered += DrawerRow_PointerEntered;
        row.PointerExited += DrawerRow_PointerExited;

        // In a plain button (see PegasusPlainButton in the XAML), for the mouse. The line keeps its own
        // look and its hover wash; the button draws nothing.
        //
        // Click is the left mouse button and nothing else. Answering PointerReleased would also start
        // the song on a right-click, which is meant to open its menu.
        //
        // Not a tab stop: the RemoteLine around it takes the focus, so OK, a held OK and the Menu key all
        // arrive on one element (see RemoteLine).
        var play = () => PlayFromDrawer(songs, index, new SleeveInHand.None());
        var press = new Button
        {
            Style = (Style)Resources["PegasusPlainButton"],
            Content = row,
            CornerRadius = row.CornerRadius,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false,
        };
        press.Click += (_, _) => play();

        // …and the right button asks what else could be done with it: the same menu the library window
        // builds on the same song, out of the same factory and the same hooks. A held OK or the Menu key
        // opens the same menu through the line below.
        press.ContextRequested += (sender, e) => ShowRecordTrackMenu(sender, e, songs, index);

        return new RemoteLine(press, play, () => index < songs.Count ? TrackMenu(songs[index]) : null, turn: null)
        {
            CornerRadius = row.CornerRadius,   // the focus ring follows it
        };
    }

    /// <summary>
    /// What a view hands over for a track's right-click menu to work: what a menu entry should do, the
    /// playlists to list under "add to playlist", and the live facts each entry is drawn from.
    ///
    /// <para>Supplied by the page rather than built here, exactly as the library window's own wall has
    /// them supplied, the entries do things this control has no business knowing how to do (editing a
    /// file, sending it to the cloud, opening the record's page), and a second implementation of them
    /// would be a second set of answers about one song.</para>
    /// </summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    private void ShowRecordTrackMenu(
        UIElement sender, ContextRequestedEventArgs args, List<TrackInfo> songs, int index)
    {
        if (index < 0 || index >= songs.Count || TrackMenu(songs[index]) is not { } menu)
            return;

        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement element)
            menu.ShowAt(element);
        args.Handled = true;
    }

    /// <summary>The record in an order nobody chose, shuffled here and played straight, so the player's
    /// own shuffle switch is left alone. The same bargain the drawer's key strikes.</summary>
    private void PlayShuffledList(List<TrackInfo> songs, SleeveInHand sleeve)
    {
        if (songs.Count == 0)
            return;

        var order = songs.ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = System.Random.Shared.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        PlayFromDrawer(order, 0, sleeve);
    }

    /// <summary>What the mini player is up for. Read off the tick (see OnRendering) and stated as one of
    /// three, because a station is not a record without a length: it has no playhead and no queue to step,
    /// so the player draws differently for it, not just with a different title.</summary>
    private enum LibraryPlayerFor
    {
        /// <summary>Nothing on the deck and no station tuned. The player is down.</summary>
        Nothing,

        /// <summary>A record on the deck: the title, the playhead and three keys.</summary>
        Record,

        /// <summary>A tuned station, playing or paused: the title and the play/pause key, with
        /// no playhead.</summary>
        Station,
    }

    /// <summary>What the player was last put up for. Held so the fade is started once on the change
    /// rather than asked for on every frame of the tick that reads the deck.</summary>
    private LibraryPlayerFor _libraryPlayerFor;

    /// <summary>Whether the mini player is up, for anything.</summary>
    private bool LibraryPlayerOn => _libraryPlayerFor != LibraryPlayerFor.Nothing;

    /// <summary>
    /// The mini player comes up with the first record or a tuned station, and goes down when neither is
    /// left.
    ///
    /// <para>Read off the tick, like the heart and the volume beside it: what is on the deck can change
    /// from anywhere (a record ending, the queue running out, something started in the window behind),
    /// and a value read on the frame is right whoever wrote it. The guard is the whole of the cost.</para>
    ///
    /// <para>For a station the playhead is taken out of the layout rather than left at nothing (the bar
    /// hides itself by opacity, which would leave a gap), and previous and next go with it: they step the
    /// record queue, and pressing one would switch the station off to play a record. The play/pause key
    /// is the deck's own command, so it pauses the station the way the main window's key does.</para>
    /// </summary>
    private void ApplyLibraryPlayer(LibraryPlayerFor shows)
    {
        if (_libraryPlayerFor == shows)
            return;

        var wasOn = LibraryPlayerOn;
        _libraryPlayerFor = shows;
        ApplyPlayerKey(LibraryPlayerOn, QueueScale);   // the remote's stop, only while there is something to show

        // Not on the way down: the player is fading out with whatever it was showing, and a playhead
        // appearing in it as it went would be the one thing on the screen that moved.
        if (shows != LibraryPlayerFor.Nothing)
        {
            var keys = shows == LibraryPlayerFor.Record ? Visibility.Visible : Visibility.Collapsed;
            LibrarySeek.Visibility = keys;
            LibraryPrevButton.Visibility = keys;
            LibraryNextButton.Visibility = keys;

            // The album's column follows the playhead (see FitLibraryPlayerLines), and a player coming up
            // measures its lines again, now that they are in a layout that is drawn.
            FitLibraryPlayerLines(remeasure: !wasOn);

            // The notch follows the pill: cut when the pill comes up, and cut again to its width when a
            // record gives way to a station or back.
            ShapeLibraryPlayerGlass();
        }

        // A record giving way to a station, or the other way: the player stays up and only its contents
        // change.
        if (wasOn == LibraryPlayerOn)
            return;

        // The card stays where it is; what it holds crosses. What is playing and its keys come up over
        // the app's mark, or go down to leave it, on one clock. Out of reach of the pointer as soon as it
        // starts going, so a press on a fading cover cannot open the record view on an empty deck.
        var on = LibraryPlayerOn;
        LibraryPlayerContent.IsHitTestVisible = on;
        LibraryTransportPill.IsHitTestVisible = on;
        _libraryPlayerFade.To(on ? 1 : 0, ScreenCrossMs, null);
        _playerLogoFade.To(on ? 0 : 1, ScreenCrossMs, null);

        // The notch closes once the pill has gone, not as it starts going: a pill fading out over a
        // closed card would be the card's glass showing through it again. Asked of the state when the
        // fade lands, because something may have come on the deck in the meantime.
        _playerPillFade.To(on ? 1 : 0, ScreenCrossMs, on ? null : () =>
        {
            if (!LibraryPlayerOn)
                ShapeLibraryPlayerGlass();
        });
    }

    /// <summary>
    /// Where the tools stand, and how far down the wall starts under them.
    ///
    /// <para>Both written here because they are one measurement. The tools are pinned over the wall
    /// rather than stacked above it, so the only thing keeping the first row of covers out from behind
    /// the search field at rest is the scroller's own top padding, and it has to be the tools' strip plus
    /// the air under it or the two overlap.</para>
    ///
    /// <para>The wall runs the whole height of the layer behind the mini player, as home does, so the tools
    /// stand in the top right corner, centred on the line of the back key in the top left, and the
    /// wall starts under them.</para>
    ///
    /// <para>The mini player's own place and home's are written here too, because both are answers about
    /// the same strip at the top of the screen.</para>
    /// </summary>
    private void LayOutLibraryTop()
    {
        // The tools on the back key's line: the key's middle, less half their height.
        var toolsTop = CornerKeyTop + ((CornerKeySize - (LibrarySearchHeight * QueueScale)) / 2);

        // What stands between the top of the screen and the first row of covers at rest.
        var wallTop = WallTop;

        // The player is centred at the top, inside the wall's inset, which keeps it clear of the back key
        // on the left and the tools on the right. Nothing stands under it, so no air is kept below it.
        LibraryPlayer.Margin = new Thickness(LibraryEdgeInset, LibraryEdgeInset, LibraryEdgeInset, 0);

        // The tools end where the COVERS end, not where the scroller does. The wall gives the scrollbar a
        // strip of its own out of the viewport (see SizeLibrary), so the last cover in a row stops that
        // much short of the scroller's own padding. A search field lined up with the scroller would stick
        // out past the grid by exactly that strip. These tools are outside the scroller, so
        // the strip has to be added by hand here.
        LibraryTools.Margin =
            new Thickness(0, toolsTop, LibraryEdgeInset + LibraryScrollbarRoom, 0);

        // Less the room each card carries (see PegasusCardTemplate), so the covers stand at the inset
        // and at the wall's top while the cards reach out over them.
        var room = CardRoomAround.Points;
        LibraryScroll.Padding = new Thickness(LibraryEdgeInset - room, wallTop - room, LibraryEdgeInset - room, 28);

        // …and the wall's bar starts level with its first row, as home's does. Asked again when the
        // scroller changes size, because the bar is a template part (see ScrollerBar).
        ScrollerBar.StartAt(LibraryScroll, wallTop);

        // Home wears no heading here (see EnsureHome) and keeps 16 at its sides itself, so the host adds
        // the rest of the wall's inset at the sides. At the top: nothing on the host, which runs to the
        // top of the layer; the space above home's first row is inside its scroller (HomeTopPad).
        HomeHost.Margin = new Thickness(HomeSideInset, 0, HomeSideInset, 0);

        // The artists list stands where the wall does, with its tools on the same line. Its inset is the
        // host's padding rather than the scroller's (see ArtistsWallHost in the XAML).
        // The artist cards carry the same room (see PegasusArtistTemplate), so the host's padding is the
        // wall's scroller's.
        ArtistsTools.Margin = LibraryTools.Margin;
        ArtistsWallHost.Padding = LibraryScroll.Padding;
        ScrollerBar.StartAt(ArtistsScroll, wallTop);
    }

    /// <summary>
    /// The space above home's first row, inside its scroller. Home runs to the top of the layer and the
    /// mini player stands over it (see HomeHost in the XAML), so no room is kept for the player: the
    /// banner starts under it and the rows scroll under it. This only keeps the banner clear of the keys
    /// in the top corners: the back key is 14 down and 36 tall, and the banner starts 14 under it.
    /// </summary>
    private const double HomeTopPad = CornerKeyTop + CornerKeySize + CornerKeyTop;

    /// <summary>Where the wall's first row of covers stands at rest: under the tools, which stand on the
    /// back key's line (see LayOutLibraryTop). The cards' room reaches above it.</summary>
    private double WallTop =>
        CornerKeyTop + ((CornerKeySize - (LibrarySearchHeight * QueueScale)) / 2) + LibraryToolsRoom;

    /// <summary>The back key in the top left corner: how far down it stands and how tall it is (its
    /// margin in the XAML, and PegasusTransportButton's size). Home and the wall start under its line, and
    /// the wall's tools stand on it.</summary>
    private const double CornerKeyTop = 14;
    private const double CornerKeySize = 36;

    /// <summary>The mini player's cover, before the drawer's scale. 144 at the scale a 1440 high screen
    /// gets.</summary>
    private const double PlayerArtEdge = 96;

    // The mini player's card, before the drawer's scale. At the scale a 1440 high screen gets (1.5) it
    // is: a card 728 by 184 with the cover 20 in from its edge, the
    // words 26 right of the cover, and a column 518 wide for them; the keys in a pill 133 by 37.

    /// <summary>The air between the card's edge and the cover, all round.</summary>
    private const double PlayerPad = 13;

    /// <summary>The air between the cover and the words.</summary>
    private const double PlayerGap = 17;

    /// <summary>The words' column: the three lines are clipped to it and scroll within it, and the
    /// playhead spans it. Fixed, so the card is one size whatever is playing.</summary>
    private const double PlayerWords = 345;

    /// <summary>The app's mark in the card while nothing plays: its height, before the scale. About a
    /// third of the card's.</summary>
    private const double PlayerLogoHeight = 40;

    /// <summary>The card's corner, and the pill's.</summary>
    private const double PlayerCorner = 7;
    private const double PillCorner = 5;

    /// <summary>One key in the pill, and the glyphs: previous and next, and the larger play/pause between
    /// them. The keys fill the pill to its rim, with no gap at either end: three of them make
    /// the pill's 133 across.</summary>
    private const double PillKeyWidth = 29;
    private const double PillKeyHeight = 23;
    private const double PillGlyph = 11;
    private const double PillPlayGlyph = 13;

    /// <summary>How much of the words' column the album's line gives up at its right end while there is a
    /// playhead, before the scale: the countdown rides up there, on the album's line (see SeekBar), and a
    /// long album name would run under it.</summary>
    private const double PlayerCountdownRoom = 52;

    /// <summary>The words' column as the last size worked it out, for re-fitting the lines when the
    /// playhead comes or goes without the size changing (see FitLibraryPlayerLines).</summary>
    private double _playerWords;

    /// <summary>
    /// The mini player at this window's scale: a card of one size, whatever its words.
    /// </summary>
    /// <remarks>
    /// Its width is the cover, the words' column and the air round them, and its height is the cover and
    /// its air, so nothing about the card is measured from what it says. A line longer than the column
    /// is clipped to it and scrolls (see _playerLines).
    /// </remarks>
    private void SizeLibraryPlayer(double scale)
    {
        var pad = PlayerPad * scale;
        var art = PlayerArtEdge * scale;
        var gap = PlayerGap * scale;
        var words = PlayerWords * scale;
        _playerWords = words;

        LibraryPlayerArtFrame.Width = LibraryPlayerArtFrame.Height = art;
        LibraryPlayerArtPlaceholder.FontSize = 28 * scale;
        ApplyPlayerKey(LibraryPlayerOn, scale);
        LibraryWords.Margin = new Thickness(gap, 0, 0, 0);
        LibraryWords.Width = words;

        // The glass's rim is a pixel inside the card's edge (see ShapeLibraryPlayerGlass), so the card's
        // own padding is a pixel more and the inside is exactly the figures above.
        _playerScale = scale;
        LibraryPlayerCard.Padding = new Thickness(pad + 1);
        LibraryPlayerCard.Width = (pad * 2) + art + gap + words + 2;
        LibraryPlayerCard.Height = (pad * 2) + art + 2;

        // The app's mark, centred in the card while nothing plays: a third of the card's height, in the
        // header's own proportions (46.5 by 18).
        LibraryPlayerLogo.Height = PlayerLogoHeight * scale;
        LibraryPlayerLogo.Width = PlayerLogoHeight * scale * (46.5 / 18);

        LibraryTitle.FontSize = 19 * scale;
        LibraryArtist.FontSize = LibraryAlbum.FontSize = 13.5 * scale;
        LibrarySeekScale.Factor = scale;
        FitLibraryPlayerLines(remeasure: true);

        // The pill: three keys of one size, on the card's bottom edge, half over it and half under it.
        var keyWidth = PillKeyWidth * scale;
        var keyHeight = PillKeyHeight * scale;
        foreach (var key in new[] { LibraryPrevButton, LibraryPlayButton, LibraryNextButton })
        {
            key.Width = keyWidth;
            key.Height = keyHeight;
            key.CornerRadius = new CornerRadius(PillCorner * scale);
            key.FontSize = (ReferenceEquals(key, LibraryPlayButton) ? PillPlayGlyph : PillGlyph) * scale;
        }

        LibraryTransportPill.CornerRadius = new CornerRadius(PillCorner * scale);

        // The frost sits inside the pill's one-pixel rim, so its corner is a pixel tighter.
        LibraryPillFrost.RadiusX = LibraryPillFrost.RadiusY = Math.Max(0, (PillCorner * scale) - 1);
        LibraryPlayerCard.Margin = new Thickness(0, 0, 0, (keyHeight + 2) / 2);

        ShapeLibraryPlayerGlass();
    }

    /// <summary>The scale the card was last sized at, for drawing its glass again when only the pill has
    /// changed (see ShapeLibraryPlayerGlass).</summary>
    private double _playerScale = 1;

    /// <summary>The air between the pill and the notch cut for it in the card, before the scale.</summary>
    private const double PillNotchGap = 3;

    /// <summary>
    /// Draws the card's glass: its rounded rectangle, with a notch around the pill's upper half while there
    /// is a pill.
    /// </summary>
    /// <remarks>
    /// <para>One outline, filled and stroked: along the top, down the right, back along the bottom to
    /// the notch, up round the notch's two rounded corners, down again, and on round to the start. So the
    /// card's fill stops short of the pill and its rim turns up round it, rather than running on under the
    /// pill's own glass, which would show through. The notch is the pill's size and a gap all round,
    /// and open at the foot.</para>
    ///
    /// <para>Asked when the card is sized, when a record gives way to a station or back (the pill holds
    /// three keys or one, and the notch follows its width), and when the pill comes or goes: at the start
    /// of its fade in, and at the end of its fade out, so the notch is never cut where there is no pill or
    /// missing where there is one. Every figure comes off the scale and what the player is showing, worked
    /// out here rather than read back from the pill's layout, so the outline is right on the frame it is
    /// asked for.</para>
    ///
    /// <para>The outline runs half a pixel inside the card's edge, so its one-pixel rim lands on whole
    /// pixels at a scale of 1.</para>
    /// </remarks>
    private void ShapeLibraryPlayerGlass()
    {
        var w = LibraryPlayerCard.Width;
        var h = LibraryPlayerCard.Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0)
            return;

        // The frost under the glass takes the same outline, written in the same call so the two cannot
        // disagree. One geometry per path rather than one shared, so neither depends on whether a head
        // allows a Geometry on two elements.
        LibraryPlayerFrost.Data = PlayerGlassOutline(w, h);
        LibraryPlayerGlass.Data = PlayerGlassOutline(w, h);
    }

    /// <summary>The card's outline at a width and height, with the notch for the pill the player shows
    /// now. See <see cref="ShapeLibraryPlayerGlass"/>.</summary>
    private PathGeometry PlayerGlassOutline(double w, double h)
    {
        var scale = _playerScale;
        var r = PlayerCorner * scale;
        const double o = 0.5;

        var figure = new PathFigure { StartPoint = new Point(r + o, o), IsClosed = true, IsFilled = true };

        void Line(double x, double y) => figure.Segments.Add(new LineSegment { Point = new Point(x, y) });
        void Arc(double x, double y, double radius, SweepDirection sweep) =>
            figure.Segments.Add(new ArcSegment
            {
                Point = new Point(x, y),
                Size = new Size(radius, radius),
                SweepDirection = sweep,
            });

        Line(w - o - r, o);
        Arc(w - o, o + r, r, SweepDirection.Clockwise);
        Line(w - o, h - o - r);
        Arc(w - o - r, h - o, r, SweepDirection.Clockwise);

        // The pill as the player shows it now: three keys for a record, one for a station, none with
        // nothing on the deck (the card holds the app's mark, and the pill is down).
        var keys = _libraryPlayerFor switch
        {
            LibraryPlayerFor.Record => 3,
            LibraryPlayerFor.Station => 1,
            _ => 0,
        };

        if (keys > 0)
        {
            var pillWidth = (keys * PillKeyWidth * scale) + 2;
            var pillHeight = (PillKeyHeight * scale) + 2;
            var g = PillNotchGap * scale;
            var rn = (PillCorner * scale) + g;

            // The pill is centred on the card and stands half over its bottom edge (see the margin above).
            var right = ((w + pillWidth) / 2) + g;
            var left = ((w - pillWidth) / 2) - g;
            var top = h - (pillHeight / 2) - g;

            Line(right, h - o);
            Line(right, top + rn);
            Arc(right - rn, top, rn, SweepDirection.Counterclockwise);
            Line(left + rn, top);
            Arc(left, top + rn, rn, SweepDirection.Counterclockwise);
            Line(left, h - o);
        }

        Line(o + r, h - o);
        Arc(o, h - o - r, r, SweepDirection.Clockwise);
        Line(o, o + r);
        Arc(o + r, o, r, SweepDirection.Clockwise);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>
    /// Gives each of the player's lines its column, and measures it again where the words or the type
    /// size may have changed under it.
    /// </summary>
    /// <remarks>
    /// The album's column stops short of the countdown while a playhead is up (a record), and takes the
    /// whole width while none is (a station). So this is asked again when the player changes between the
    /// two (ApplyLibraryPlayer), not only when the window does. And again when the player comes up: a line
    /// whose words arrived while the player was collapsed may have been measured at nothing, and a line
    /// measured at nothing never scrolls.
    /// </remarks>
    private void FitLibraryPlayerLines(bool remeasure)
    {
        if (_playerWords <= 0 || _playerLines.Length < 3)
            return;

        // The countdown is drawn at the card's scale (LibrarySeekScale), so the room it takes is too.
        var withPlayhead = _libraryPlayerFor != LibraryPlayerFor.Station;
        var countdown = PlayerCountdownRoom * _playerScale;
        _playerLines[0].Fit(_playerWords);
        _playerLines[1].Fit(_playerWords);
        _playerLines[2].Fit(withPlayhead ? Math.Max(0, _playerWords - countdown) : _playerWords);

        if (!remeasure)
            return;

        foreach (var line in _playerLines)
        {
            line.Remeasure();
            line.Rewind();
        }
    }

    /// <summary>What puts home's content on the wall's inset: the wall's 34, less the 16 home keeps at
    /// each side itself.</summary>
    private const double HomeSideInset = LibraryEdgeInset - 16;

    /// <summary>The tools' own strip and the air under it: the search field's height at the wall's text
    /// scale, and 16 so the first row of covers is clear of it and not under its bottom
    /// edge. The same scale <see cref="SizeLibraryTools"/> draws the field at.</summary>
    private double LibraryToolsRoom => LibrarySearchHeight * QueueScale + 16;

    /// <summary>The search field, before the scale. The sort and favourites keys are drawn to fit its
    /// height.</summary>
    private const double LibrarySearchWidth = 270;
    private const double LibrarySearchHeight = 30;

    /// <summary>
    /// The mini player and the tiles at the size this window wants them.
    ///
    /// <para>The tile's side is the wall over the count for this scale (eight at a scale of 1, five at
    /// 1.5; see LibraryColumnsAt), less the gaps between them and the inset at either end, so a row
    /// always fits exactly, on any window, and neither wraps nor leaves a column of air down the
    /// right.</para>
    /// </summary>
    private void SizeLibrary(double scale)
    {
        SizeLibraryPlayer(scale);
        SizeSideMenu(scale);
        SizeRadio(scale);   // radio, its wheel, its search and the tuner (see PegasusChrome.Radio)
        SizeArtistsGenres(scale);   // the artists and genres screens (see PegasusChrome.ArtistsGenres)

        // Home's text, on the same figure as the player above it (see EnsureHome).
        if (_home is { } home)
            home.TextScale = scale;

        // The wall's own writing on the same figure: the two lines under every tile in one write (see
        // PegasusTileMetrics.Scale), and the tools over it. The room under the tools follows their
        // height, so the top is laid out again.
        _tiles.Scale = scale;
        SizeLibraryTools(scale);
        LayOutLibraryTop();

        var wall = LibraryScroll.ActualWidth > 0 ? LibraryScroll.ActualWidth : Root.ActualWidth;
        if (wall <= 0)
            return;

        // The scrollbar takes its own strip out of the viewport once the wall is longer than the screen,
        // which it always is. It is counted here, so that no row is found too wide later, and
        // named, because the tools above the wall have to end on the same line the covers do.
        // Fewer and larger covers on a tall screen. A scale that asks for another count than the rows
        // were cut at cuts them again first, so the side below is the side of the rows on the wall.
        var columns = LibraryColumnsAt(scale);
        if (columns != _wallColumns && _wallCards.Count > 0)
            RecutWall(columns);
        _wallColumns = columns;

        var room = wall - (LibraryEdgeInset * 2) - LibraryScrollbarRoom
                 - (LibraryGap * (columns - 1));
        var edge = Math.Max(72, room / columns);

        // One write for the whole wall, not one per record: see PegasusTileMetrics.
        _tiles.Edge = edge;
        _tileEdge = edge;

        // The resident tier is settled against the display here, because this is the first moment there is
        // an answer: the scale comes off the XamlRoot, which a control does not have until it is in a
        // window, and the wall is filled before that happens.
        RefreshResidentEdge();

        // Nothing is queued and nothing cancelled. The tiles on screen simply stop matching what the
        // loader asks for, and it is woken to notice. Each keeps the picture it has until its replacement
        // lands, so the wall sharpens in place without blinking.
        LoadLibraryArt();
    }

    /// <summary>The search field, the sort and favourites keys, and the line that stands in for an empty
    /// wall, at the wall's text scale. Their sizes are written only here; the markup states none.</summary>
    private void SizeLibraryTools(double scale)
    {
        LibrarySearchBox.Width = LibrarySearchWidth * scale;
        LibrarySearchBox.Height = LibrarySearchHeight * scale;
        LibrarySearchBox.Padding = new Thickness(9 * scale, 0, 9 * scale, 0);
        LibrarySearchMark.FontSize = 12 * scale;
        LibrarySearch.FontSize = 13 * scale;

        LibrarySortButton.Width = LibraryFavoritesButton.Width = 34 * scale;
        LibrarySortButton.Height = LibraryFavoritesButton.Height = 28 * scale;
        LibrarySortButton.FontSize = 12 * scale;
        LibraryFavoritesIcon.FontSize = 12 * scale;

        LibraryEmpty.FontSize = 14 * scale;
    }

    /// <summary>The tile's side as the last layout worked it out, so the decode ladder has something to
    /// bucket. Eight to a row whatever the window is, so one figure answers for the whole wall.</summary>
    private double _tileEdge;

    /// <summary>How the tile's own size is rounded before it is asked for. See
    /// <see cref="CardDecodeEdge"/>.</summary>
    private const int EdgeQuantum = 32;

    /// <summary>
    /// The pixel edge a tile's cover is decoded at: the size it is actually DRAWN, rounded up to the next
    /// <see cref="EdgeQuantum"/>.
    ///
    /// <para>The larger of two needs. The tile is one; the opened record's sleeve is the other, and it
    /// is drawn from the very same bitmap (<see cref="BuildBand"/> hands <c>card.Cover</c> straight to
    /// its <c>Image</c>), so decoding for the tile alone would leave every opened record with a
    /// blurred sleeve. On any ordinary window the tile is the larger of the two anyway; on a narrow one
    /// the sleeve takes over, which is exactly when it should.</para>
    ///
    /// <para>This does NOT use <see cref="ArtScaler.DecodeEdgeFor"/>'s ladder, for a reason of memory
    /// rather than sharpness. That ladder doubles: its rungs are 128, 192, 256, 384, 512, 640, so a tile
    /// drawn at 292 pixels is answered with 384. That is a third more across each side, which is seventy
    /// per cent more pixels held for a tile that cannot show them. Over a collection that is the
    /// difference between 247MB and 339MB, and the wall's off-screen budget is 320MB. The ladder's
    /// rounding alone would put a library of six hundred records over the limit, so covers would be given
    /// back and decoded again while the listener watches rows fill in.</para>
    ///
    /// <para>The ladder is coarse on purpose for the library window's wall, whose tile is dragged
    /// continuously by a size slider: a rung per pixel would re-decode the collection all along the
    /// drag. This wall has no slider. Its tile is an eighth of the window and moves only when the window
    /// does, so it can use a much finer step. There is a step at all, and not the exact pixel, because a
    /// window being DRAGGED wider is still a continuous change, and because a thumb is cut and cached per
    /// edge. Thirty-two keeps the waste under a rung's worth and the re-reads to a handful across a drag.
    /// See also RefreshResidentEdge, which does not act on any of them until the drag stops.</para>
    /// </summary>
    private int CardDecodeEdge()
    {
        const double bandSleeve = 176;   // BuildBand's sleeve, before the drawer's scale
        var wanted = Math.Max(_tileEdge, bandSleeve * QueueScale)
                   * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1);

        // Never above the thumb's own edge: decoding larger than the source buys nothing but the memory.
        var stepped = (int)(Math.Ceiling(wanted / EdgeQuantum) * EdgeQuantum);
        return Math.Clamp(stepped, ArtScaler.TinyEdge, ArtScaler.TileEdge);
    }

}

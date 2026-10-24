using RPM.Core.Models;

using RPM.Core.Library;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Presentation.Main.Library.Models;
using RemoteLine = RPM.Desktop.Presentation.Pegasus.RemoteLine;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// Opening a record on the home screen: the drawer that unfolds under the row it was pressed in, what
/// fills it, and what a press inside it means.
///
/// <para>The control is the album wall's own (<see cref="AlbumBandView"/>) and it is filled by the
/// shared loader (<see cref="AlbumBandLoader"/>), so a record opened here is the same as the record
/// opened on the wall and on an artist's page: the same tracklist, the same disc dividers, the same
/// three ways to play it, the same heart.</para>
///
/// <para>A press on a card opens the drawer. The way to the record's own page is a key on the cover,
/// shown while the pointer is on it, as on the album wall.</para>
///
/// <para>The open record is marked twice, as it is on the wall: a ring around its cover, and a triangle
/// under it pointing up at the card. The ring is a property of the card
/// (<see cref="SimilarRow.IsExpanded"/>). The triangle's position is calculated, because the row scrolls
/// sideways while the drawer does not. See <see cref="PointAtOpenCard"/>.</para>
/// </summary>
public sealed partial class HomeView
{
    // ── what the drawer is filled from and what a press in it means: the host's, every one ──

    /// <summary>Where an opened record's cover, tracklist and tint come from. These are the album
    /// views' own loaders, passed in the same way as for the artist page.</summary>
    public AlbumBandSources? BandSources { get; set; }

    /// <summary>Play one song off an open record.</summary>
    public Action<TrackInfo>? PlayTrack { get; set; }

    /// <summary>Play the whole record, in order or shuffled.</summary>
    public Action<IReadOnlyList<TrackInfo>, bool>? PlayTracks { get; set; }

    /// <summary>Give the whole record to the DJ.</summary>
    public Action<IReadOnlyList<TrackInfo>>? StartDj { get; set; }

    /// <summary>Mark one song as a favourite, or clear it. Returns whether the library accepted it.</summary>
    public Func<TrackInfo, bool, bool>? SetTrackFavorite { get; set; }

    /// <summary>The album menu's hooks. Two are used here: the state, for the heart the drawer opens
    /// with, and the invoke, for when the heart is pressed. It is the same object the wall and the
    /// artist page get, so a mark made here shows there.</summary>
    public AlbumMenuHooks? MenuHooks { get; set; }

    /// <summary>Everything a right-clicked song inside an open record needs.</summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    /// <summary>What is playing at the moment a tracklist is built. It is read when needed, not pushed:
    /// a folder read can take seconds on a cold disk and the song can change during it.</summary>
    public Func<string?>? PlayingPath { get; set; }

    /// <summary>The cloud mark under an open record's cover.</summary>
    public Func<string, RPM.Core.Library.AlbumCloudBadge>? CloudBadgeLoader { get; set; }

    /// <summary>What the collection is called, for the one record that is a library's loose tracks at
    /// its root. It is a function and not a stored value: the shelf on screen can be swapped for a
    /// peer's, and a stored name would label that record with a collection it is not in.</summary>
    public Func<string>? LibraryName { get; set; }

    // ── the presses ──

    /// <summary>
    /// A record pressed in one of the rows: its tracklist unfolds under that row.
    /// </summary>
    /// <remarks>
    /// <para>Exactly one record is open across the whole screen, so the drawer that is already open is
    /// folded here, not by each row. It folds on its own and is not waited for: the record being opened
    /// is the one the user pressed, and holding it back while another row closes would make the press
    /// seem unanswered.</para>
    ///
    /// <para>Pressing the record that is already open closes it.</para>
    ///
    /// <para>Every press takes a number (<see cref="_bandGesture"/>) and every await below checks that
    /// it is still the newest. A press has two waits (the folder is read, then the drawer unfolds), and
    /// a user who presses three cards during that time would otherwise get three openings landing on top
    /// of one another.</para>
    /// </remarks>
    private async void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SimilarRow { Folder: { Length: > 0 } } row)
            return;

        // When the host asks for it, a press from the keyboard opens the record's own page, as the key
        // on the cover does, and not the drawer under the row. A remote cannot reach the key on the
        // cover, which shows only under a hovering pointer. The two are told apart by how the card got
        // the focus: a card pressed with the mouse has it from the pointer; a card pressed with OK has
        // it from the keys, which Pegasus gives as Programmatic so the framework draws no focus
        // rectangle of its own (see PegasusPlayer.Remote).
        if (DrivenByRemote && sender is Control { FocusState: FocusState.Keyboard or FocusState.Programmatic })
        {
            AlbumRequested?.Invoke(this, row.Folder);
            return;
        }
        if (DrawerIn(sender as DependencyObject) is not { } mine)
            return;

        var gesture = ++_bandGesture;

        if (_open is { } other && !ReferenceEquals(other, mine))
            _ = FoldAsync(other, gesture);

        if (mine.Root.Visibility == Visibility.Visible
            && mine.View.Band is { } open
            && string.Equals(open.Card.Folder, row.Folder, StringComparison.OrdinalIgnoreCase))
        {
            // Stop the read: the record being closed may still be loading.
            _bandLoading?.Cancel();
            await FoldAsync(mine, gesture);
            return;
        }

        await OpenAsync(mine, row, gesture, Scroller(sender as DependencyObject));
    }

    /// <summary>
    /// A record card's button is loaded. Where a remote drives this screen and the host gives a record's
    /// menu (<see cref="RecordMenu"/>), the button is put in a remote line the first time (see
    /// RemoteLine.Around): OK on the line opens the record's own page, as a press from the keys does in
    /// <see cref="Card_Click"/>, and a held OK opens the record's menu. That is Pegasus, as on the TV. The
    /// mouse still clicks the button, which opens the drawer under the row. In the main window nothing
    /// changes.
    ///
    /// <para>The line reads the record from the button when it runs: the rows' repeater pools its cards
    /// and hands a card to another record. A pooled card is loaded again inside its line, has no panel of
    /// its own to be put in, and keeps the line it has.</para>
    /// </summary>
    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_drivenByRemote || RecordMenu is null || sender is not Button { Parent: Grid } key)
            return;

        RemoteLine.Around(
            key,
            press: () =>
            {
                if (key.Tag is SimilarRow { Folder: { Length: > 0 } folder })
                    AlbumRequested?.Invoke(this, folder);
            },
            menu: () => key.Tag is SimilarRow { Folder: { Length: > 0 } folder } row
                ? RecordMenu?.Invoke(folder, row.Title)
                : null);
    }

    /// <summary>
    /// Builds a record's menu, unshown, from its folder and its name, for a held OK on a record card
    /// where a remote drives this screen (see <see cref="Card_Loaded"/>). Pegasus gives the record's menu
    /// away from its page. Null in the main window, where a card's menu is the right-click one.
    /// </summary>
    public Func<string, string, MenuFlyout?>? RecordMenu { get; set; }

    /// <summary>
    /// The drawer belonging to the row an element is in. It is made on first use and kept for its
    /// element, so each row has exactly one drawer however many times it is opened.
    /// </summary>
    /// <remarks>
    /// Found by walking up the tree from the pressed card, because the rows live in a DataTemplate and
    /// there is nothing else to find it by. The drawer is not an ANCESTOR of the card. It is a sibling
    /// of the cards' own scroller, one level inside the row, so the walk goes up to a panel that has a
    /// drawer among its children, not up to the drawer itself.
    /// </remarks>
    private RowDrawer? DrawerIn(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Panel row)
                continue;

            foreach (var root in row.Children.OfType<Panel>())
            {
                // One level further in than the triangle: the drawer is inside the ScaleBox that draws
                // it at the text scale.
                if (root.Children.OfType<ScaleBox>().SelectMany(box => box.Children)
                        .OfType<AlbumBandView>().FirstOrDefault() is not { } view)
                    continue;

                if (_drawers.TryGetValue(root, out var known))
                    return known;

                if (root.Children.OfType<Polygon>().FirstOrDefault() is not { } point)
                    return null;   // the markup has no triangle, so there is no drawer here to open

                return _drawers[root] = new RowDrawer(root, view, point);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this home is driven by a TV remote. Pegasus sets it; it is false in the main window. It
    /// changes what the keys do:
    /// <list type="bullet">
    /// <item>A record card pressed from the keys opens the record's own page and not the drawer under
    /// its row. In the main window the drawer opens for a press of any kind.</item>
    /// <item>The banner has one stop, the card in front: Left and Right step the cards and OK opens the
    /// one in front. Its arrow keys and dots are for the pointer only (see
    /// <see cref="ApplyBannerStops"/>).</item>
    /// <item>Where the host also gives the menus, a record card and a song in the quick selection stand
    /// in a remote line, so a held OK opens their menu (see <see cref="Card_Loaded"/> and
    /// <see cref="Quick_Loaded"/>).</item>
    /// </list>
    /// </summary>
    public bool DrivenByRemote
    {
        get => _drivenByRemote;
        set
        {
            _drivenByRemote = value;
            ApplyBannerStops();
        }
    }

    private bool _drivenByRemote;

    /// <summary>
    /// Whether a record card shows its key to the record's own page while the pointer is on its cover.
    /// True in the main window, where a press on the card opens the drawer and the key is the way to the
    /// page. Pegasus turns it off: OK on the card opens the page there, and the key, which is built on
    /// pointer hover, would be one more stop for the remote on a card that already does the same.
    /// </summary>
    public bool AlbumPageKeyShown { get; set; } = true;

    /// <summary>The way into the record's own page, from the key on the cover. It raises
    /// <c>AlbumRequested</c>, so the page that opens knows it was opened from home and its back button
    /// returns to home.</summary>
    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SimilarRow { Folder: { Length: > 0 } folder })
            AlbumRequested?.Invoke(this, folder);
    }

    /// <summary>The press, marked handled so the card under the key does not open its drawer as well.
    /// The key and the card it sits on are two different ways in, and one press uses only one.</summary>
    private void OpenPage_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => e.Handled = true;

    // ── the key on the cover ──

    /// <summary>The pointer entering a card's artwork, which is the only time the way into the
    /// record's own page is drawn. If it were always shown, every card in five rows would have a key.</summary>
    private void Art_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ShowOpenKey(sender, true);

    private void Art_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        => ShowOpenKey(sender, false);

    /// <summary>
    /// The key on the cover, shown or hidden.
    /// </summary>
    /// <remarks>
    /// The two directions are different. The key is deferred in the template (<c>x:Load</c>) and
    /// looking it up by name builds it, so a hide that looked it up would build a key on every card it
    /// hides. Only the show path looks it up by name. The hide path uses <see cref="_openKey"/>, the
    /// one key currently shown (at most one, because the pointer is in one place). A card whose key was
    /// never built has nothing to hide and does not look anything up.
    /// </remarks>
    private void ShowOpenKey(object sender, bool show)
    {
        if (!show || !AlbumPageKeyShown)
        {
            DropOpenKey();
            return;
        }

        if ((sender as FrameworkElement)?.FindName("OpenSlot") is not FrameworkElement key)
            return;

        // Hide whichever key was shown before this one, wherever it was. A pointer leaving one card
        // and entering another usually reports the leaving first, but a row rebuilt under the pointer
        // does not report it at all, and a key left visible on a card the pointer has left would never
        // go away.
        if (!ReferenceEquals(key, _openKey))
            DropOpenKey();

        key.Opacity = 1;
        key.IsHitTestVisible = true;
        _openKey = key;
    }

    private void DropOpenKey()
    {
        if (_openKey is not { } key)
            return;

        _openKey = null;
        key.Opacity = 0;
        key.IsHitTestVisible = false;
    }

    /// <summary>The one key on the screen that is currently shown, or null. See
    /// <see cref="ShowOpenKey"/> for why this is stored and not looked up.</summary>
    private FrameworkElement? _openKey;

    // ── opening, folding, and the animations that do it ──

    /// <summary>Which press this is. It is increased by every open, every fold and every reading of the
    /// shelf, and checked after every await: an answer that returns with an older number belongs to a
    /// record the user has already moved on from.</summary>
    private int _bandGesture;

    /// <summary>
    /// One row's drawer: the element an animation owns, the two things drawn inside it, and the
    /// animation itself.
    /// </summary>
    /// <remarks>
    /// <para>The ROOT is what opens, not the control inside it. The triangle and the tracklist must
    /// appear together: a triangle shown on the row before the drawer it points at would look like the
    /// drawer opening twice. So the height, the clip and the opacity are set on the root and the control
    /// just sits inside.</para>
    ///
    /// <para>The animation is per row, not per screen. Two drawers can move at once, one folding while
    /// another opens, and a single animation would let the fold cancel the unfold.</para>
    ///
    /// <para><see cref="Opened"/>, <see cref="Row"/> and <see cref="Rail"/> are what the triangle is
    /// aimed with. They are read when the triangle is placed and not remembered from the press, because
    /// the row moves under the drawer afterwards.</para>
    /// </remarks>
    private sealed class RowDrawer(Panel root, AlbumBandView view, Polygon point)
    {
        public Panel Root { get; } = root;

        public AlbumBandView View { get; } = view;

        public Polygon Point { get; } = point;

        public BandUnfold Unfold { get; } = new();

        /// <summary>The card this drawer was opened for, or null while it is shut.</summary>
        public SimilarRow? Opened { get; set; }

        /// <summary>The row that card is in, for the card's position in it.</summary>
        public HomeRowModel? Row { get; set; }

        /// <summary>The scroller that holds that row's cards, for how far it has scrolled.</summary>
        public ScrollViewer? Rail { get; set; }
    }

    /// <summary>The one drawer that is open, or null. It is stored because the rows live in a
    /// DataTemplate, and searching for it would mean finding every row to fold one.</summary>
    private RowDrawer? _open;

    /// <summary>Cancels the read of a record the user has already moved on from. It is its own token,
    /// not the screen's: opening a second record must not stop the covers the rows are still loading.</summary>
    private CancellationTokenSource? _bandLoading;

    /// <summary>Each row's drawer, keyed by the panel it is drawn in. The key is the element because a
    /// press arrives through it. The whole dictionary is cleared when the rows are replaced.</summary>
    private readonly Dictionary<Panel, RowDrawer> _drawers = [];

    /// <summary>How long the drawer takes to open, to fold shut, and to grow or shrink between two
    /// records in the same row, in milliseconds. These are the album wall's own figures, because it is
    /// the album wall's own drawer: the same record opened here and there must not move at two
    /// speeds.</summary>
    private const double UnfoldMs = 200;
    private const double FoldMs = 160;
    private const double MorphMs = 200;

    /// <summary>How long, in milliseconds, the cover's background takes to cross-fade with what was
    /// behind the drawer. This is the album page's and the wall's own figure.</summary>
    private const double GroundCrossMs = 260;

    /// <summary>The measurements an opened record's card is built with. The drawer sizes its own cover,
    /// so nothing on screen reads these; the card only needs a set of measurements.</summary>
    private readonly AlbumTileMetrics BandMetrics = new();

    /// <summary>The pulse on an open record's cloud marks: a mark that is working pulses and stops when
    /// the work stops. There is one for the screen, because at most one record is open. It is created on
    /// first use, not in the constructor, which is in the other half of this class.</summary>
    private CloudLineBreath? _lineBreathMade;

    private CloudLineBreath LineBreath => _lineBreathMade ??= new(BreathingMarks);

    /// <summary>The marks on the open record: its lines, and the record's own under the cover. It is
    /// read again on every call, because the set changes whenever a record is opened, loaded or given a
    /// new badge. It is empty while no record is open, which stops the pulse.</summary>
    private IEnumerable<ICloudBadged> BreathingMarks()
    {
        if (_open?.View.Band is not { } band)
            yield break;

        foreach (var line in band.Lines)
            yield return line;

        yield return band.Card;
    }

    /// <summary>The one running background cross-fade. At most one drawer is open, so there is at most
    /// one of these. A record opened while one is running stops it at once.</summary>
    private ChromeFade? _groundFade;

    /// <summary>Folds a drawer shut and then releases the record it held. A band left on a hidden
    /// drawer would keep a tracklist, a decoded cover and a colour alive for a record nobody can
    /// see.</summary>
    private async Task FoldAsync(RowDrawer drawer, int gesture)
    {
        if (drawer.Root.Visibility != Visibility.Visible)
            return;

        // The ring goes at the press, not at the end of the fold: the record stops being the open one
        // as soon as another is pressed, and a ring that waited for the animation would leave two rings
        // for a sixth of a second. The triangle folds WITH the drawer, because it is part of it.
        Unring(drawer);

        await drawer.Unfold.To(drawer.Root, toHeight: 0, toOpacity: 0, FoldMs, easeOut: false, releaseToAuto: false);

        // A newer press may have taken this very drawer over while it folded. It owns the element now,
        // and removing the band from under it would make the drawer open onto nothing.
        if (gesture != _bandGesture || drawer.Unfold.Owns(drawer.Root))
            return;

        Shut(drawer);
    }

    /// <summary>Removes a drawer at once, with no fold. It is used when the rows are being replaced and
    /// nobody is watching this drawer.</summary>
    private void Shut(RowDrawer drawer)
    {
        drawer.Unfold.Stop();
        Unring(drawer);
        drawer.Root.Visibility = Visibility.Collapsed;
        drawer.Point.Visibility = Visibility.Collapsed;
        drawer.View.Band = null;

        // Never leave it folded: the element belongs to the row and is used again by the next record
        // opened in it, and a height of zero left behind would open onto nothing.
        drawer.Root.Height = double.NaN;
        drawer.Root.Clip = null;
        drawer.Root.Opacity = 1;

        if (ReferenceEquals(_open, drawer))
            _open = null;

        // The lines are going away, so the pulse must stop with them.
        LineBreath.Sync();
    }

    /// <summary>Removes the ring from the card this drawer was opened for and forgets the card. Safe to
    /// call twice: a fold and the shut that follows it both call it.</summary>
    private static void Unring(RowDrawer drawer)
    {
        if (drawer.Opened is { } card)
            card.IsExpanded = false;

        drawer.Opened = null;
        drawer.Row = null;
        drawer.Rail = null;
    }

    /// <summary>Closes the open record and cancels any press still in progress. Called before the rows
    /// are replaced, because the elements that a new reading of the shelf drops include the open
    /// drawer.</summary>
    private void ShutEvery()
    {
        _bandGesture++;
        _bandLoading?.Cancel();
        _groundFade?.Stop();

        if (_open is { } drawer)
            Shut(drawer);

        // The animations belong to elements that no longer exist once the rows are replaced.
        foreach (var known in _drawers.Values)
            known.Unfold.Stop();
        _drawers.Clear();
    }

    /// <summary>The screen is going away: stop the timers and cancel the read in progress.</summary>
    private void RecordsUnloaded()
    {
        _bandGesture++;
        _bandLoading?.Cancel();
        _groundFade?.Stop();
        _lineBreathMade?.Stop();
    }

    /// <summary>
    /// Opens one record in one row's drawer.
    /// </summary>
    /// <remarks>
    /// <para>The record is read INTO the drawer before the drawer is shown. It is put in the tree at
    /// its own width, folded to zero height and invisible, and the folder is read into it there. So
    /// what unfolds a moment later is complete: the tracklist, the cover at the size it is drawn, and
    /// the record's own background, all arriving together.</para>
    ///
    /// <para>A second record in the SAME row is a morph, not a new opening. The element and the room it
    /// has taken are kept and what is inside them is swapped, then the height is animated from the old
    /// record's length to the new one's. Folding one record and unfolding the next would be two
    /// animations for one user action.</para>
    /// </remarks>
    private async Task OpenAsync(RowDrawer drawer, SimilarRow row, int gesture, ScrollViewer? rail)
    {
        if (BandSources is not { } sources)
            return;

        _bandLoading?.Cancel();
        var cts = _bandLoading = new CancellationTokenSource();

        Wire(drawer.View);

        // Column 0 always: the triangle is placed by this screen's own calculation, not by the band's
        // column index, which is a step on a wall and this row is not a wall (see PointAtOpenCard).
        var card = new AlbumCard(row.Folder, LibraryName?.Invoke() ?? "", BandMetrics)
        {
            IsExplicit = row.IsExplicit,
            IsFavorite = MenuHooks?.StateProvider?.Invoke(row.Folder).IsFavorite == true,
        };

        var unfold = drawer.Unfold;
        var morph = drawer.Root.Visibility == Visibility.Visible && drawer.View.Band is not null;

        // Where the drawer is right now: the fixed height while an animation owns it, and the laid-out
        // height otherwise. Read before the animation is stopped; stopping it leaves the element
        // exactly where it was.
        var from = morph ? (double.IsNaN(drawer.Root.Height) ? drawer.Root.ActualHeight : drawer.Root.Height) : 0;
        unfold.Stop();

        AlbumBand band;
        if (morph && drawer.View.Band is { } showing)
        {
            // The same band, pointed at the new card. A new band would be a new DataContext, and the
            // drawer would be rebuilt in front of the user. This branch exists to avoid that collapse
            // and expand.
            band = showing;
            band.Retarget(card, 0);

            // The background belongs to the cover being read. If it stayed, the last record's colour
            // would show under this record's tracks until the read finishes. Cleared, the drawer shows
            // the app's own plate meanwhile and the new record's background fades in over it.
            band.GroundOn = 0;
            band.Ground = null;
            band.Wash = null;
            band.Veil = 0;
            band.Loading = true;

            // Fixed during the read, so the old record's length cannot be laid out again underneath.
            drawer.Root.Height = from;
        }
        else
        {
            band = new AlbumBand(card, 0) { Title = row.Title, Subtitle = row.Artist };
            drawer.Root.Height = 0;
            drawer.Root.Clip = null;
            drawer.Root.Opacity = 0;
            drawer.Root.Visibility = Visibility.Visible;
            drawer.View.Band = band;
        }

        // The drawer opens on the app's own plate, and so do the two marks it draws outside itself
        // (the triangle and the ring). All three agree on the first frame as well as on every later
        // frame, and the cover's own background fades in on all three together once it has been read
        // (see CrossGround).
        drawer.Point.Fill = BandPointer.Ramp(BandPointer.Plate);
        band.PointerBrush = drawer.Point.Fill;

        _open = drawer;
        Ring(drawer, row, rail?.Tag as HomeRowModel, rail);
        PointAtOpenCard();

        // What the row has already loaded, so the cover is not blank while the folder is queried. It is
        // cast, not converted: the card holds an ImageSource because that is all a cover in a row needs
        // to be, and the drawer wants the bitmap. If it is not a bitmap there is no seed, and the read
        // at the drawer's own size arrives a moment later either way.
        band.Cover = row.Cover as BitmapImage;

        // The cover is decoded for the size it is drawn at. The ScaleBox around the drawer makes that
        // larger than the drawer's layout size by the text scale.
        var drawn = sources with { RasterScale = sources.RasterScale * _textSizes.Scale };
        var items = await AlbumBandLoader.FillAsync(band, drawn, loadCover: true, BandTracks.Whole, cts.Token);

        if (gesture != _bandGesture || cts.IsCancellationRequested)
            return;

        // Which line is playing is read here, not pushed in when the deck changed: the read above can
        // take seconds on a cold folder, and the song can change during it.
        var playing = PlayingPath?.Invoke();
        foreach (var item in items)
            item.IsPlaying = item.IsTrack && Same(item.FilePath, playing);

        // Then what the cloud holds of the record as a whole, which is the mark under its cover.
        if (CloudBadgeLoader is { } badge)
            band.Card.CloudBadge = badge(band.Card.Folder);

        // A record opened while some of it is uploading starts pulsing straight away.
        LineBreath.Sync();

        drawer.Root.Height = double.NaN;
        drawer.Root.Clip = null;

        // The row's width stands in for the drawer's width, and this fallback decides the whole unfold
        // on a FRESH open. The element is collapsed until the press, and a collapsed element is never
        // measured, so it has no width until a layout pass has run with it visible. The read above can
        // finish before that pass. Measured against zero width, the drawer would want no height, and
        // "nothing to unfold" is treated as "show it where it is", so the drawer would just appear. The
        // parent is always laid out, whatever the drawer has been through, so the parent is asked and
        // not the drawer.
        var full = BandUnfold.Wanted(
            drawer.Root, fallbackWidth: (drawer.Root.Parent as FrameworkElement)?.ActualWidth ?? 0);
        if (full <= 1)
        {
            // Nothing to measure: let it lay out and show, instead of animating to a length that
            // cannot be worked out.
            drawer.Root.Opacity = 1;
            return;
        }

        drawer.Root.Height = morph ? from : 0;
        await unfold.To(drawer.Root, full, toOpacity: 1, morph ? MorphMs : UnfoldMs,
                        easeOut: true, releaseToAuto: true);
    }

    /// <summary>
    /// The ring: only the open card wears one, and it is this drawer's own record that wears it.
    /// </summary>
    /// <remarks>
    /// Called while the drawer's previous record is still set, which is what a morph needs: a press on
    /// the neighbour of an open record moves the mark across with no fold in between, so the record
    /// being replaced loses its ring here, because no fold will do it.
    ///
    /// <para>The ring's COLOUR is not set here. Each card keeps its own (<see cref="SimilarRow.Ring"/>):
    /// the drawer's default background until its cover has been read, and that record's own background
    /// afterwards. So a record opened a second time keeps the colour it had the first time and does not
    /// start again from the default. The wall rings from its own cards for the same reason.</para>
    /// </remarks>
    private static void Ring(RowDrawer drawer, SimilarRow row, HomeRowModel? inRow, ScrollViewer? rail)
    {
        if (drawer.Opened is { } was && !ReferenceEquals(was, row))
            was.IsExpanded = false;

        drawer.Opened = row;
        drawer.Row = inRow;
        drawer.Rail = rail;
        row.IsExpanded = true;
    }

    /// <summary>
    /// Puts the open drawer's triangle over the card it belongs to, or hides it when that card is not
    /// on screen.
    /// </summary>
    /// <remarks>
    /// <para>Called on open, on every move of the row, and whenever the cards are re-measured. It always
    /// uses the row's CURRENT offset and is not moved by whatever moved the row. A key press, a trackpad
    /// swipe and the size slider all lead to the same call, and a path that skipped it would leave the
    /// mark pointing at a record that has gone.</para>
    ///
    /// <para>It reads the drawer's own record and the drawer's own row, so a call that comes from
    /// another row's scroller is harmless: there is one open drawer and this places that one.</para>
    ///
    /// <para>The row is drawn one card padding to the LEFT of the drawer (the cards' own grid has a
    /// negative margin, so the first COVER starts on the drawer's left edge). That is the one
    /// conversion between the middle measured across the row and the margin set here.</para>
    /// </remarks>
    private void PointAtOpenCard()
    {
        if (_open is not { Opened: { } card, Row: { } row, Rail: { } rail } drawer)
            return;

        var middle = AlbumGridLayout.CardMiddleOnScreen(
            row.Cards.IndexOf(card), card.Metrics.Stride, card.Metrics.Edge, SimilarTileMetrics.Padding,
            rail.HorizontalOffset, rail.ViewportWidth, BandPointer.TriangleHalf);

        if (middle is not { } x)
        {
            drawer.Point.Visibility = Visibility.Collapsed;
            return;
        }

        // Never to the left of the drawer's own edge, as on the wall: a card whose middle is within one
        // padding of the row's left end would otherwise leave the mark hanging off the drawer. This
        // costs a few points of aim on that one card and nothing elsewhere.
        drawer.Point.Margin = new Thickness(
            Math.Max(0, x - SimilarTileMetrics.Padding - BandPointer.TriangleHalf), 0, 0, 0);
        drawer.Point.Visibility = Visibility.Visible;
    }

    /// <summary>Connects what a press inside the drawer does. It runs on every open, not once: the hooks
    /// are given to this view after it is built, and a drawer wired at construction would have a heart
    /// and menus that do nothing.</summary>
    private void Wire(AlbumBandView drawer)
    {
        drawer.PlayTrack = PlayTrack;
        drawer.PlayTracks = PlayTracks;
        drawer.StartDj = StartDj;
        drawer.MenuInvoked = MenuHooks?.Invoked;
        drawer.SetTrackFavorite = SetTrackFavorite;
        drawer.TrackMenuHooks = TrackMenuHooks;
    }

    /// <summary>
    /// The record's own background, applied to the drawer once its cover has been read: the mean colour
    /// underneath, the blurred cover over it, and the veil inside the blur.
    /// </summary>
    /// <remarks>
    /// The fade runs only when the drawer is visible on screen, and that is checked on the drawer itself
    /// and not remembered from the press. A record opened fresh is read while its drawer is still folded
    /// to zero height, and a quarter-second fade behind a folded drawer would be over before the drawer
    /// shows, leaving the record without its background. A drawer that is being pointed at a second
    /// record IS visible, and there the fade is the whole point.
    ///
    /// <para>Given to the loader as this screen's <see cref="AlbumBandSources.Sleeve"/>.</para>
    /// </remarks>
    public async Task CrossGround(AlbumBand band, byte[] sleeve, CancellationToken ct)
    {
        if (await AlbumBandLoader.ReadGroundAsync(sleeve, ct) is not { } read
            || ct.IsCancellationRequested)
            return;

        // The drawer showing THIS band, or none: a record that finishes reading after the user has
        // moved on writes its colours onto its own band and nowhere else.
        var drawer = _open is { } open && ReferenceEquals(open.View.Band, band) ? open : null;
        var watched = drawer?.Root is { Opacity: > 0, ActualHeight: > 1 };

        band.Ground = new SolidColorBrush(read.Ground.Ground);
        band.Veil = read.Ground.Veil;
        band.Wash = read.Wash;

        // The triangle and the ring cannot show the drawer's three layers, so they use the single
        // colour the three layers combine to. They do not fade by a second copy fading over the first:
        // two stacked ramps are one alpha over another and not the ramp itself, so the mark would come
        // out darker and a different hue from the drawer it points at. Both are REPAINTED instead, their
        // colour moving from what the drawer opened with to what its cover turned out to be, on the
        // same value that drives the layers.
        var wasPoint = BandPointer.ColourOf(band.PointerBrush) ?? read.Ground.Flat;
        var wasRing = read.Ground.Flat;
        SolidColorBrush? ring = null;
        if (drawer?.Opened is { } card)
        {
            // The ring's brush is made once and its colour set on each frame. The triangle's brush is
            // rebuilt each time, because a gradient is its stops, and changing them in place does not
            // make the element repaint.
            wasRing = (card.Ring as SolidColorBrush)?.Color ?? read.Ground.Flat;
            card.Ring = ring = new SolidColorBrush(wasRing);
        }

        _groundFade?.Stop();
        _groundFade = new ChromeFade(
            v =>
            {
                band.GroundOn = v;
                band.PointerBrush = BandPointer.Ramp(BandPointer.Blend(wasPoint, read.Ground.Flat, v));
                if (drawer is not null)
                    drawer.Point.Fill = band.PointerBrush;
                if (ring is not null)
                    ring.Color = BandPointer.Blend(wasRing, read.Ground.Flat, v);
            },
            band.GroundOn);
        _groundFade.To(1, watched ? GroundCrossMs : 0);
    }

    // ── marks made somewhere else, landing on the open record ──

    /// <summary>A song marked as a favourite elsewhere (the menu, the full-screen player) is applied to
    /// the matching line inside the open record.</summary>
    public void ApplyTrackFavorite(string filePath, bool favorite)
    {
        if (_open?.View.Band is not { } band)
            return;

        foreach (var line in band.AllTracks)
            if (line.IsTrack && Same(line.FilePath, filePath))
                line.IsFavorite = favorite;
    }

    /// <summary>A record favourited or unfavourited elsewhere is applied to the heart under the open
    /// drawer's cover.</summary>
    public void ApplyAlbumFavorite(string folder, bool favorite)
    {
        if (_open?.View.Band is { } band
            && string.Equals(band.Card.Folder, folder, StringComparison.OrdinalIgnoreCase))
            band.IsFavorite = favorite;
    }

    /// <summary>Whether two paths name the same file. Two nulls are NOT the same: with no song playing,
    /// a line whose path the library has lost must not be marked as playing.</summary>
    private static bool Same(string? a, string? b)
        => a is { Length: > 0 } && b is { Length: > 0 }
           && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

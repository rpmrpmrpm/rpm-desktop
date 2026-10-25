using RPM.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>What Left or Right did to the focused control (see PegasusChrome.Adjust).</summary>
public enum KeyTurn
{
    /// <summary>The control is not turned with Left and Right; the arrow moves the focus off it.</summary>
    NotTurnable,

    /// <summary>The control moved.</summary>
    Turned,

    /// <summary>The control is already at that end. The arrow bounces.</summary>
    AtEnd,
}

/// <summary>
/// Pegasus under a TV remote: where the focus goes when a screen comes up or goes away, and the steps the
/// host's Back key takes through the chrome.
///
/// <para>The view is made for use with a TV remote control: no clicking and no swiping, only the arrow
/// keys, OK, Back and a few other keys. The arrows move the focus (XY navigation, switched on at the
/// host's root), OK presses what has the focus, and Back goes up one level (see
/// PegasusPlayer.TakeBack). The mouse still works.</para>
///
/// <para><b>Only while the remote is in use.</b> The focus is moved by a screen change only while
/// <see cref="KeyboardDriven"/> holds. The host sets it on a key and clears it on a pointer move. A
/// focus moved for a user with the mouse would scroll home to wherever the focus went. Where the focus
/// was not moved, the next key finds the focus lost and the host puts it on the screen's own default
/// (see ShellPage.OnPreviewKeyDown), so a screen reached with the mouse is still reachable by keys.</para>
///
/// <para><b>After layout, on the frame tick.</b> A screen that has just been made visible has not been
/// measured, and an element in it cannot take the focus or be brought into view until it has. So a screen
/// change only records where the focus should go (<see cref="FocusScreenLater"/>), and the chrome's frame
/// tick places it on the first frame in which the element is loaded, laid out and shown
/// (<see cref="TickFocus"/>). A low-priority dispatcher item is not used for this, because while the
/// visualizer draws every frame such an item can run many seconds late.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>Whether the user is driving the view with keys. The host sets it: true on a key it
    /// handles, false on a pointer move. A pointer move also puts back a queue row that the keys had
    /// picked up (see PegasusChrome.QueueRemote), so nothing is moved that the user did not place.</summary>
    public bool KeyboardDriven
    {
        get => _keyboardDriven;
        set
        {
            _keyboardDriven = value;
            if (!value)
                EndQueueMove(place: false);
        }
    }

    private bool _keyboardDriven;

    /// <summary>Whether a line's menu is open. It asks the host (PegasusPlayer.LineMenuOpen), which opens
    /// and closes those menus. It is read each time, so it is never out of step with the menu.</summary>
    public Func<bool>? LineMenuOpen { get; set; }

    /// <summary>The play key of the album or list page. The focus starts there on a list's page, and it is
    /// where Left from the tracks goes.</summary>
    private Control? _recordPlayKey;

    /// <summary>The first track line of the album page. The focus starts there on an album's page, as on
    /// the TV's: somebody who opens a record has usually come to choose a song on it.</summary>
    private Control? _recordFirstTrack;

    /// <summary>The track line that last had the focus on the album or list page, so that Right from the
    /// keys goes back to it. Null until a line has had the focus on the page now open.</summary>
    private Control? _recordLastTrack;

    /// <summary>The element that had the focus when the album or list page was opened, so that Back can
    /// put the focus back there. It is kept only if it was in the collection.</summary>
    private UIElement? _recordOpener;

    /// <summary>Whether the wall is the screen in front: up, and no page over it.</summary>
    public bool WallInFront => WallUp && !RecordUp;

    /// <summary>
    /// Where the arrow keys look for the next element to move to: the screen in front and nothing behind
    /// it. This is the menu while it is open (or the panel open over it, see <see cref="MenuScope"/>),
    /// which keeps the focus inside it; the drawer while the focus is in it; a page over the collection;
    /// the collection; and otherwise the host's whole view (the turning record), without the transport
    /// that its view does not draw (see <see cref="TransportNotDrawn"/>). Without this, an arrow could
    /// land on a key that is faded out behind the screen in front.
    /// </summary>
    /// <param name="wholeView">The host's whole view, which is the screen of the turning record.</param>
    private RemoteScreen FocusScreen(UIElement wholeView)
    {
        if (LibraryMenuOpen)
            return new RemoteScreen(MenuScope(), notDrawn: []);
        if (FocusWithin(QueuePanel))
            return new RemoteScreen(QueuePanel, notDrawn: []);
        if (RecordUp)
            return new RemoteScreen(RecordLayer, notDrawn: []);
        if (TunerUp)
            return new RemoteScreen(TunerLayer, notDrawn: []);
        if (RadioSearchOpen)
            return new RemoteScreen(RadioSearchLayer, notDrawn: []);

        // Radio's search is in the collection layer and fades out over radio when it closes, so its field
        // and its tiles are left out while they fade. The wall and home are collapsed under radio. The
        // artists and genres screens that stay in the tree under the screen in front are left out too
        // (see ApplyArtistsGenresShown).
        if (LibraryUp && _collection == CollectionScreen.Radio)
            return new RemoteScreen(LibraryLayer, notDrawn: [RadioSearchLayer, .. ArtistsGenresNotDrawn()]);
        if (LibraryUp)
            return new RemoteScreen(LibraryLayer, notDrawn: [RadioScreen, RadioSearchLayer, .. ArtistsGenresNotDrawn()]);
        return new RemoteScreen(wholeView, notDrawn: TransportNotDrawn() is { } part ? [part] : []);
    }

    /// <summary>
    /// The part of the turning record that its view does not draw: one of its two transports. The lyric
    /// view alone and the bare room draw the seek bar and keys along the foot (LyricFoot, see FootDrawn)
    /// and fade the column that holds the words, the seek bar and the keys (Words). Every other view draws
    /// the column and sets the foot to opacity 0. Both stay in the tree, laid out and focusable. It is read from the
    /// view being moved to (<c>_viewTo</c>), which <see cref="DefaultFocusTarget"/> also reads, so the two
    /// cannot name different transports. Null while the collection is up, because the turning record is
    /// behind it.
    /// </summary>
    private UIElement? TransportNotDrawn() =>
        LibraryUp ? null : FootDrawn(_viewTo) ? Words : LyricFoot;

    /// <summary>
    /// Whether the focus is on a part of the screen that its view does not draw: on the column's lyric
    /// key after that key turned the lyric view on, or on the lyric view's own keys after they turned it
    /// off. The host counts that as lost (PegasusPlayer.FocusedInView), so no glow shows over nothing and
    /// the next key places the focus on the play key of the drawn transport. It is evaluated each time, so
    /// nobody has to move the focus when the view changes.
    ///
    /// <para>It is also true on one of the artists and genres screens that is not in front: a list or page
    /// left on the trail under the page in front stays in the tree at opacity 0 (see
    /// ApplyArtistsGenresShown). A page opened with the pointer leaves the focus on the card that was
    /// pressed, and without this the first key would move the focus along that invisible list. Counted as
    /// lost, the focus goes to the page's own default instead.</para>
    /// </summary>
    public bool FocusOnNotDrawn() =>
        (TransportNotDrawn() is { } part && FocusWithin(part))
        || (CollectionInFront && ArtistsGenresNotDrawn().Any(FocusWithin));

    /// <summary>
    /// Where an arrow key moves the focus from <paramref name="from"/>: the screen in front is searched
    /// by layout (see <see cref="RemoteSearch"/>), then that screen's own rule is applied where it has
    /// one: the album page's rule for crossing between its two columns (see
    /// <see cref="AcrossRecordPage"/>), and home's rule for not passing over its banner
    /// (HomeView.OverBanner). Null where there is nothing in that direction.
    /// </summary>
    /// <param name="wholeView">The host's whole view, which is the screen of the turning record.</param>
    public Control? NextFocus(FrameworkElement? from, FocusNavigationDirection direction, UIElement wholeView)
    {
        var screen = FocusScreen(wholeView);
        var next = RemoteSearch.Next(from, direction, screen);
        if (from is null)
            return next;

        // The settings over the menu: their two cards' own rule (see PegasusChrome.SideMenu).
        if (ReferenceEquals(screen.Area, SettingsPanel))
            return AcrossSettings(from, direction, next);

        if (RecordUp)
            return AcrossRecordPage(from, direction, next);

        // Home in front: the collection is the screen searched (not its menu, not the drawer).
        if (ReferenceEquals(screen.Area, LibraryLayer) && _collection == CollectionScreen.Home && _home is { } home)
            return home.OverBanner(from, direction, next);

        // Radio in front, its search and the tuner each have a rule of their own (see
        // PegasusChrome.Radio).
        if (ReferenceEquals(screen.Area, LibraryLayer) && _collection == CollectionScreen.Radio)
            return RadioOverBanner(from, direction, next);
        if (ReferenceEquals(screen.Area, RadioSearchLayer))
            return IntoSearchResults(from, direction, next);
        if (ReferenceEquals(screen.Area, TunerLayer))
            return AcrossTuner(from, direction, next);

        // The album wall as a carousel: Up opens its letters, Down the front record's menu, and Down from the
        // tools comes back to it (see PegasusChrome.AlbumCarousel).
        if (ReferenceEquals(screen.Area, LibraryLayer) && _collection == CollectionScreen.Wall && _carouselOn)
            return AcrossCarousel(from, direction, next);

        // The album wall and the artists list: Up from the first row reaches the sort key, and Down from
        // the tools comes back (see PegasusChrome.LibraryTools).
        if (ReferenceEquals(screen.Area, LibraryLayer) && _collection is CollectionScreen.Wall or CollectionScreen.Artists)
            return ToSortKey(from, direction, next);

        // An artist's or a genre's page: Up and Down can reach a row of a different length (see
        // PegasusChrome.ArtistsGenres).
        if (ReferenceEquals(screen.Area, LibraryLayer) && IsArtistsGenres(_collection))
            return AcrossShelves(from, direction, next, screen);

        return next;
    }

    /// <summary>
    /// Left and Right between the two columns of an album or list page: the album with its keys on the
    /// left and the track list on the right. A move that stays in its column is kept (Right from the play
    /// key to the menu key). A move that leaves the column goes to a fixed stop in the other column: Right
    /// to the track line that last had the focus on this page (track 1 the first time), Left to the play
    /// key. The track list must be reachable from the left side even when no line is level with the
    /// focused key, and Right must not go to the track that happens to be level. The search by layout only
    /// crosses to something level with the focused element, so without this rule a move from the keys
    /// under a short album would bounce, and a move from beside a long album would land on whichever line
    /// is level.
    /// </summary>
    /// <param name="next">Where the search by layout would move the focus, or null.</param>
    private Control? AcrossRecordPage(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        switch (direction)
        {
            case FocusNavigationDirection.Right when Inside(from, RecordInfoPanel)
                                                     && (next is null || !Inside(next, RecordInfoPanel)):
                // The track left last, while it is still a line on this page. Giving it the focus scrolls
                // the running order to it if it has scrolled away.
                if (_recordLastTrack is { } last && Inside(last, RecordBody) && CanTakeFocus(last))
                    return last;
                return FocusManager.FindFirstFocusableElement(RecordBody) as Control ?? next;

            case FocusNavigationDirection.Left when Inside(from, RecordBody)
                                                    && (next is null || !Inside(next, RecordBody)):
                return _recordPlayKey is { } play && CanTakeFocus(play) ? play : next;

            default:
                return next;
        }
    }

    /// <summary>Whether <paramref name="node"/> is <paramref name="scope"/> or stands inside it.</summary>
    private static bool Inside(DependencyObject node, DependencyObject scope)
    {
        for (DependencyObject? at = node; at is not null; at = VisualTreeHelper.GetParent(at))
            if (ReferenceEquals(at, scope))
                return true;
        return false;
    }

    /// <summary>Back from the wall: home, as the wall's own back key goes.</summary>
    public void BackToHome() => CrossToHome();

    /// <summary>Back from the turning record: the collection, on home. Does nothing if the collection is
    /// already up.</summary>
    public void ShowHome()
    {
        if (!LibraryUp)
            ShowLibrary(CollectionScreen.Home, immediate: false);
    }

    /// <summary>A key was pressed: the transport and its playhead appear again, as they do on a pointer
    /// move, so that the key the focus moves to is always visible.</summary>
    public void WakeForKeys() => WakeTransport();

    /// <summary>
    /// Whether the player's controls are hidden: the turning record is in front (not the collection, not the
    /// drawer), a record is on the deck, and the transport has faded after its quiet seconds. With the
    /// controls hidden, the remote's keys do not move the focus over keys nobody can see. Left and Right
    /// seek, and OK, Up and Down only bring the controls up (see PegasusPlayer.TakeHiddenControlsKey).
    /// </summary>
    public bool ControlsHidden => !LibraryUp && !_queueOpen && !_transportUp && _amp?.Deck is not null;

    /// <summary>
    /// Left or Right with the controls hidden: the playhead goes 10 seconds back or on, without bringing the
    /// controls up. It is the column's own seek bar that moves, by the same rule as a seek bar with the
    /// focus (see <see cref="Adjust"/>). So a held key runs at the seek bar's held rate, and the seek is made
    /// once, when the key comes up (see <see cref="AdjustDone"/>). A seek on every repeat would open the
    /// file again thirty times a second.
    /// </summary>
    /// <param name="way">-1 for Left, 1 for Right.</param>
    public void SeekHidden(int way, bool repeat) => Adjust(Seek, way, repeat);

    /// <summary>
    /// Where the focus belongs on the screen in front: the side menu's place while it is open over home,
    /// the drawer's own place while the drawer is open (see <see cref="DrawerFocusTarget"/>), the play key
    /// of the turning record (the lyric view's own foot holds it there), the play key of a page, home's
    /// first card, or the wall's first album. Null where there is nothing yet (a page whose keys are not
    /// built).
    /// </summary>
    public Control? DefaultFocusTarget()
    {
        // The side menu, while it is open: the panel open over it, else the entry of the screen in front
        // (see MenuEntryInFront). Without this, a focus lost inside the menu (for example when a dialog
        // closes) would go to home's first card, behind the menu.
        if (LibraryMenuOpen)
            return _menuPanel == MenuPanel.None
                ? MenuEntryInFront()
                : FocusManager.FindFirstFocusableElement(MenuScope()) as Control;

        if (_queueOpen && DrawerFocusTarget() is { } inDrawer)
            return inDrawer;

        if (!LibraryUp)
            return FootDrawn(_viewTo) ? LyricPlayButton : TransportPlayButton;

        if (RecordUp)
            return _recordPlayKey;

        if (TunerUp)
            return TunerFocusTarget();
        if (RadioSearchOpen)
            return RadioSearchBox;

        if (_collection == CollectionScreen.Home)
            return _home?.DefaultFocusTarget();
        if (_collection == CollectionScreen.Radio)
            return RadioFocusTarget();
        if (IsArtistsGenres(_collection))
            return ArtistsGenresFocusTarget();

        // The wall as a carousel: its stop over the front cover (see PegasusChrome.AlbumCarousel).
        if (_carouselOn)
            return CarouselFocusTarget()
                   ?? (_wallCards.Count == 0 && LibraryEmpty.Visibility == Visibility.Visible ? LibrarySearch : null);

        // The wall: its first card in the rows that are realized. The search field is the target only
        // when the wall has been filled and holds no albums (none exist, or none match), and shows its
        // line saying so. While the first read is running, or the rows are not realized yet, there is no
        // target and the focus waits for the first card. Falling back to the field would put the focus in
        // it on every visit that arrives before the rows, and then the field would take the arrow keys.
        return FocusManager.FindFirstFocusableElement(LibraryWall) as Control
               ?? (_wallCards.Count == 0 && LibraryEmpty.Visibility == Visibility.Visible ? LibrarySearch : null);
    }

    /// <summary>A placement of the focus that a screen change asked for and the frame tick has not made
    /// yet. It holds the element to put the focus on if that element can take it, whether to move the
    /// focus only when it was left on nothing, and whether the placement is also made while the pointer
    /// is in use (a field that the user asked to type into). There is one object per request, and null
    /// when there is no request.</summary>
    private sealed class FocusWanted(UIElement? prefer, bool onlyIfLost, bool withoutKeys)
    {
        public UIElement? Prefer { get; } = prefer;
        public bool OnlyIfLost { get; } = onlyIfLost;
        public bool WithoutKeys { get; } = withoutKeys;
        public int Frames { get; set; }
    }

    private FocusWanted? _focusWanted;

    /// <summary>How many frames to wait for a preferred element before the screen's default is used
    /// instead. The opener of a page is already laid out and takes the focus on the first frame. An
    /// opener that cannot take it by now has been recycled away.</summary>
    private const int FocusPreferFrames = 10;

    /// <summary>How many frames a request is kept, about three seconds. A screen that is not built by
    /// then is left alone, and the next key finds the focus lost and places it (see
    /// ShellPage.TakePegasusKey).</summary>
    private const int FocusGiveUpFrames = 180;

    /// <summary>
    /// Asks for the focus to go to the screen in front once it can take it: to <paramref name="prefer"/>
    /// if that is loaded, laid out and shown within a few frames, and otherwise to the screen's default.
    /// Does nothing while the remote is not in use. Replaces any request that is not yet placed.
    /// </summary>
    private void FocusScreenLater(UIElement? prefer)
    {
        if (KeyboardDriven)
            _focusWanted = new FocusWanted(prefer, onlyIfLost: false, withoutKeys: false);
    }

    /// <summary>
    /// Asks for the focus to go to a field that the user asked to type into (the wall's search field,
    /// from the menu's Search) once it can take it. It also works while the pointer is in use, because a
    /// field opened by a click is also waiting for the keyboard. Only that field gets the focus: if it
    /// cannot take the focus in time, nothing else is focused instead. Replaces any request that is not
    /// yet placed.
    /// </summary>
    private void FocusFieldLater(Control field) =>
        _focusWanted = new FocusWanted(field, onlyIfLost: false, withoutKeys: true);

    /// <summary>
    /// Back took a step (see PegasusPlayer.TakeBack): if the focus is on nothing, or on something that is
    /// now collapsed, it goes to the screen's default once that can take it. A request that is already
    /// made is not replaced, because it says more exactly where the focus belongs.
    /// </summary>
    public void RefocusIfLost()
    {
        if (KeyboardDriven && _focusWanted is null)
            _focusWanted = new FocusWanted(null, onlyIfLost: true, withoutKeys: false);
    }

    /// <summary>An arrow key was pressed: a request that is not yet placed is dropped, so that the focus
    /// the arrow moved is not taken back by a placement that was requested earlier.</summary>
    public void DropFocusRequest() => _focusWanted = null;

    /// <summary>
    /// One frame: places the focus that a screen change asked for, as soon as the element can take it.
    /// It depends only on facts at the time of the frame (is the element loaded, does it have a size, is
    /// it shown, does the remote still have the view), so it does not matter which came first, the
    /// request or the layout.
    /// </summary>
    private void TickFocus()
    {
        if (_focusWanted is not { } want)
            return;

        if ((!KeyboardDriven && !want.WithoutKeys) || ++want.Frames > FocusGiveUpFrames)
        {
            _focusWanted = null;
            return;
        }

        if (want.OnlyIfLost && !FocusLeftOnNothing())
            return;   // keep watching until the limit: something that is closing may still fade with the focus on it

        // A field requested while the pointer is in use: that field or nothing, for as long as the
        // request is kept, because the screen's default only applies to the remote.
        if (want.WithoutKeys)
        {
            if (want.Prefer is Control field && CanTakeFocus(field) && RemoteSearch.Give(field))
                _focusWanted = null;
            return;
        }

        // A preferred element is used only if it is in the tree, laid out and visible all the way up.
        // The opener of a page can be recycled away (for example a wall row scrolled out of view) while
        // the page is shown.
        if (want.Prefer is Control prefer && want.Frames <= FocusPreferFrames)
        {
            if (CanTakeFocus(prefer) && RemoteSearch.Give(prefer))
                Placed();
            return;
        }

        if (DefaultFocusTarget() is { } target && CanTakeFocus(target) && RemoteSearch.Give(target))
            Placed();

        // On the turning record the focus lands on the transport, which a key would have woken. A
        // placement is not a key, so the transport is woken here. With nothing on the deck yet it stays
        // hidden, and so does the ring (see FocusOnFadedKeys).
        void Placed()
        {
            _focusWanted = null;
            if (!LibraryUp)
                WakeTransport();
        }
    }

    /// <summary>
    /// Whether the focus is on the transport (its keys or its seek bar, under the record or under the
    /// lyric) while the transport has faded after a few quiet seconds. The host draws no ring there, and
    /// the next arrow or OK key only brings the transport back (see ShellPage.TakePegasusKey), so nothing
    /// is moved or pressed that cannot be seen.
    /// </summary>
    public bool FocusOnFadedKeys() =>
        !_transportUp
        && (FocusWithin(TransportRow) || FocusWithin(Seek) || FocusWithin(LyricKeys) || FocusWithin(LyricSeek));

    /// <summary>Whether an element can be given the focus now: it is loaded, laid out, and visible all the
    /// way up.</summary>
    private static bool CanTakeFocus(Control control) =>
        control is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } && ShownAllTheWayUp(control);

    /// <summary>Whether the focus is on nothing, or on something collapsed or with no size.</summary>
    private bool FocusLeftOnNothing() =>
        XamlRoot is null
        || FocusManager.GetFocusedElement(XamlRoot) is not FrameworkElement focused
        || focused.ActualWidth <= 0
        || !ShownAllTheWayUp(focused);

    /// <summary>Remembers what has the focus in the collection when a page is opened over it.</summary>
    private void NoteRecordOpener() => _recordOpener = FocusedWithin(LibraryLayer);

    /// <summary>Whether the focus is on <paramref name="scope"/> or anything inside it. It is read from
    /// the focus manager each time.</summary>
    private bool FocusWithin(DependencyObject scope) => FocusedWithin(scope) is not null;

    /// <summary>The focused element, if it is <paramref name="scope"/> or inside it.</summary>
    private UIElement? FocusedWithin(DependencyObject scope)
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not UIElement focused)
            return null;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, scope))
                return focused;

        return null;
    }

    /// <summary>Whether an element and every parent above it is visible, so the focus can land on it.</summary>
    private static bool ShownAllTheWayUp(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed })
                return false;
        return true;
    }

    // ── the drawer ──

    /// <summary>The play key of the drawer's album screen. The focus starts there on that screen.</summary>
    private Control? _detailPlayKey;

    /// <summary>The shelf row that opened the album or list on the drawer's album screen, so that Back
    /// puts the focus back on it. It is kept only when the focus was on the shelf.</summary>
    private Control? _detailOpener;

    /// <summary>
    /// Where the focus belongs in the drawer: the album screen's play key; the shelf's first row, else
    /// its highlighted tab; the queue's playing row, else its first row, else its tab.
    /// </summary>
    private Control? DrawerFocusTarget() => _screen switch
    {
        DrawerScreen.Album => _detailPlayKey,
        DrawerScreen.Browse => FocusManager.FindFirstFocusableElement(BrowseRows) as Control
                               ?? (_browseAlbums ? BrowseAlbumsTab : BrowsePlaylistsTab),
        _ => QueueLineFor(_queueCurrent)
             ?? FocusManager.FindFirstFocusableElement(QueueRows) as Control
             ?? BrowseQueueTab,
    };

    /// <summary>The queue row of one track, as the remote focuses it: the line inside the row's Border
    /// (see BuildQueue). Null when the track has no row.</summary>
    private RemoteLine? QueueLineFor(PlaylistTrack? track)
    {
        if (track is null)
            return null;
        foreach (var child in QueueRows.Children)
            if (child is Border { Child: RemoteLine line } row && ReferenceEquals(row.Tag, track))
                return line;
        return null;
    }

    /// <summary>
    /// Back inside the drawer: from an album or list on the album screen to the shelf it was opened
    /// from, with the focus back on its row. Returns false where the drawer has no level above the
    /// current screen, and then Back closes the drawer instead.
    /// </summary>
    public bool DrawerBack()
    {
        if (!_queueOpen || _screen != DrawerScreen.Album)
            return false;

        var opener = _detailOpener;
        _detailOpener = null;
        ShowScreen(DrawerScreen.Browse);
        FocusScreenLater(opener);
        return true;
    }

    // ── Left and Right on a control that is turned ──

    /// <summary>How far one press of Left or Right moves the playhead, in seconds.</summary>
    private const double SeekStepSeconds = 10;

    /// <summary>While the key is held, the playhead moves at least this many seconds per second.</summary>
    private const double SeekHeldLeast = 30;

    /// <summary>A held key also moves the playhead fast enough to cross the whole track in this many
    /// seconds, if that is faster than <see cref="SeekHeldLeast"/>.</summary>
    private const double SeekHeldCross = 6;

    /// <summary>How far one press moves the volume, and how far one repeat of a held key moves it.</summary>
    private const int VolumeStep = 5;
    private const int VolumeHeldStep = 2;

    /// <summary>When the last Left or Right was turned. It sets the rate of a held seek.</summary>
    private long _turnedAt;

    /// <summary>
    /// Left or Right on the focused control, where that control is turned and the focus does not move
    /// away. A seek bar steps its preview (the seek is made when the key comes up, see
    /// <see cref="AdjustDone"/>), the volume steps its level, the stars of the album page step one whole
    /// star, and a line that turns (home's banner card, see RemoteLine) steps itself. Anything else is
    /// not turnable, and the arrow moves the focus as usual.
    /// </summary>
    /// <param name="way">-1 for Left, 1 for Right.</param>
    /// <param name="repeat">The key is held down and this is the keyboard's repeat.</param>
    public KeyTurn Adjust(object? focused, int way, bool repeat)
    {
        var now = Environment.TickCount64;
        var since = Math.Min(0.25, (now - _turnedAt) / 1000.0);
        _turnedAt = now;

        // The album carousel's covers and letter wheel turn on their own terms (see PegasusChrome.AlbumCarousel).
        if (CarouselTurn(focused, way, repeat) is { } carousel)
            return carousel;

        switch (focused)
        {
            case SeekBar bar:
            {
                var length = _amp?.Duration ?? 0;
                var seconds = repeat
                    ? Math.Max(SeekHeldLeast, length / SeekHeldCross) * since
                    : SeekStepSeconds;
                return bar.StepBy(way * seconds) ? KeyTurn.Turned : KeyTurn.AtEnd;
            }

            case ContentControl volume when ReferenceEquals(volume, VolumeGroup):
            {
                if (_amp is not { } amp)
                    return KeyTurn.AtEnd;
                var to = Math.Clamp(amp.Volume + (way * (repeat ? VolumeHeldStep : VolumeStep)), 0, 100);
                if (to == amp.Volume)
                    return KeyTurn.AtEnd;
                amp.Volume = to;
                LayoutVolume();
                return KeyTurn.Turned;
            }

            case StarRating { IsInteractive: true } stars:
                // Not on a repeat: a held key would go from one star to five faster than it can be
                // read, and would write the rating at every step.
                if (repeat)
                    return KeyTurn.Turned;
                return stars.Step(way) ? KeyTurn.Turned : KeyTurn.AtEnd;

            case RemoteLine { Turns: true } line:
                // Not on a repeat either: each step of home's banner is a slide, and a held key would
                // start a new slide about thirty times a second. On home and radio a held key never
                // reaches here: the shell first counts it towards the menu on that side (see
                // PegasusPlayer.SideKey). The tuner's dial is the exception: a held key there moves along
                // the band, as a held key moves a seek bar.
                if (repeat && !ReferenceEquals(line, _tunerDialKey))
                    return KeyTurn.Turned;
                return line.Turn(way) ? KeyTurn.Turned : KeyTurn.AtEnd;

            default:
                return KeyTurn.NotTurnable;
        }
    }

    /// <summary>Left or Right came up: every seek bar in this view commits the preview that the keys
    /// made, if it has one, and so does the tuner's dial (see CommitDialStep). All of them are committed,
    /// not only the focused one, because the focus can move while the key is down.</summary>
    public void AdjustDone()
    {
        Seek.CommitStep();
        LyricSeek.CommitStep();
        LibrarySeek.CommitStep();
        CommitDialStep();
        CarouselKeyUp();   // …and a held turn of the album carousel glides to rest
    }

    // ── a track's menu ──

    /// <summary>
    /// A song's menu, built but not shown: the same menu that the library window builds for the same
    /// song, from the same factory and the same hooks. Null when no hooks were given. It is used by a
    /// right-click on a line of an album page, and by a held OK or the Menu key on any track line (see
    /// RemoteLine).
    /// </summary>
    /// <remarks>
    /// Two changes are made to the factory's menu here, in Pegasus only, to follow the TV's song menu. The
    /// main window's menu is left as it is.
    /// <list type="bullet">
    /// <item>On somebody else's collection (see <see cref="RecordBorrowed"/>) the favourite is taken out.
    /// The factory already leaves out Add to playlist there.</item>
    /// <item>Add to playlist starts with New playlist, so somebody with many playlists does not have to go
    /// past all of them to make one, and a tick shows on each playlist that already holds the song (see
    /// <see cref="PegasusPlaylistMenu"/>).</item>
    /// </list>
    /// </remarks>
    private MenuFlyout? TrackMenu(TrackInfo song)
    {
        if (TrackMenuHooks is not { } hooks)
            return null;

        var menu = TrackMenuFactory.Build(
            song, hooks, (FontFamily)Resources["PegasusFa"], (FontFamily)Resources["PegasusFaRegular"]);

        // Before ForThisView, which takes the icons off: the favourite is found by its heart.
        if (RecordBorrowed)
            DropFavoriteEntry(menu.Items);
        else
            PegasusPlaylistMenu(menu.Items, song, hooks);

        return ForThisView(menu);
    }

    /// <summary>The heart the factory draws on its favourite entry (TrackMenuFactory.Build). The entry is
    /// found by it rather than by its words, which change with the language and with the mark.</summary>
    private const string FavoriteGlyph = "";

    /// <summary>Takes a song's favourite entry out of its menu, with the rule the factory puts above it,
    /// so no two rules are left next to each other.</summary>
    private static void DropFavoriteEntry(IList<MenuFlyoutItemBase> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not MenuFlyoutItem { Icon: FontIcon { Glyph: FavoriteGlyph } })
                continue;

            items.RemoveAt(i);
            if (i > 0 && items[i - 1] is MenuFlyoutSeparator)
                items.RemoveAt(i - 1);
            return;
        }
    }

    /// <summary>
    /// Fills the factory's Add to playlist again, the TV's way: New playlist first, a rule, then the
    /// playlists, each with a tick where it already holds the song.
    ///
    /// <para>The entries do what the factory's do, through the same hooks (NewPlaylistFromTrack,
    /// AddTrackToPlaylist), on the same list of playlists (PlaylistEntries). Choosing a ticked playlist
    /// adds the song as the main window does, which asks first about a song that is already there.</para>
    ///
    /// <para>The ticks are read off the thread that draws the view and land when the read does, usually
    /// before the submenu is opened. Until then no playlist is ticked. A menu closed before the read lands
    /// is not touched again.</para>
    /// </summary>
    private void PegasusPlaylistMenu(IList<MenuFlyoutItemBase> items, TrackInfo song, TrackMenuHooks hooks)
    {
        var heading = Strings.Loc.Get("Playlists_AddToPlaylist");
        if (items.OfType<MenuFlyoutSubItem>().FirstOrDefault(s => s.Text == heading) is not { } playlists)
            return;

        var entries = hooks.PlaylistEntries?.Invoke() ?? Array.Empty<(long, string, int)>();
        playlists.Items.Clear();

        if (hooks.NewPlaylistFromTrack is { } create)
        {
            playlists.Items.Add(MenuEntry(Strings.Loc.Get("Playlists_NewPlaylistFrom"), () => create(song)));
            if (entries.Count > 0)
                playlists.Items.Add(new MenuFlyoutSeparator());
        }

        var byId = new Dictionary<long, ToggleMenuFlyoutItem>();
        foreach (var (id, name, depth) in entries)
        {
            var playlistId = id;
            var entry = new ToggleMenuFlyoutItem
            {
                Text = new string(' ', depth * 2) + name,
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
            };
            entry.Click += (_, _) => hooks.AddTrackToPlaylist?.Invoke(song, playlistId);
            playlists.Items.Add(entry);
            byId[playlistId] = entry;
        }

        if (byId.Count > 0)
            TickPlaylistsHoldingAsync(song, byId);
    }

    /// <summary>Ticks the playlists that already hold the song, from one read of the listener's own
    /// playlists (LibraryDatabase.PlaylistsHolding), by its RPM id and by its path, the two keys a playlist
    /// entry is kept by. Nothing is ticked if the read does not answer.</summary>
    private static async void TickPlaylistsHoldingAsync(TrackInfo song, Dictionary<long, ToggleMenuFlyoutItem> byId)
    {
        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
            return;

        var db = library.Db;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (song.RpmId is { Length: > 0 } rpmId)
            ids.Add(rpmId);

        // Forward-slashed and relative to the root, which is how a playlist entry stores a path.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetRelativePath(root, song.FilePath).Replace(Path.DirectorySeparatorChar, '/'),
        };

        try
        {
            var asked = await Task.Run(() => library.Read(db, "the playlists holding a song, for Pegasus's menu",
                                                           mirror => mirror.PlaylistsHolding(ids, paths)));
            if (!asked.Answers(out var holding, out _))
                return;

            foreach (var list in holding)
                if (byId.TryGetValue(list.NodeId, out var entry))
                    entry.IsChecked = true;
        }
        catch
        {
            // A collection that closed under the read. The menu is left without ticks.
        }
    }

    // ── menus at this view's size ──

    /// <summary>The size of a menu's words in this view: the same as a track line, so a menu opened from
    /// across a room is as readable as the list it was opened on.</summary>
    private double MenuFontSize => 15 * QueueScale;

    /// <summary>The maximum width of a menu at the desktop's size, in points: the framework's own
    /// FlyoutThemeMaxWidth. It is scaled together with the words, so a long playlist name is not cut
    /// short.</summary>
    private const double MenuDesktopMaxWidth = 456;

    /// <summary>
    /// A menu made for the desktop, adapted for this view: its words at <see cref="MenuFontSize"/>, its
    /// rows and their padding scaled by the same factor, and without its icons. A desktop-sized menu is
    /// too small to read from across a room.
    ///
    /// <para>The icons are removed because the item template gives an icon a fixed 16 points and places
    /// the words at a fixed distance after it, from its own visual states. Words scaled to this size
    /// would run into icons that cannot scale with them. Scaling the icons too would need a copy of the
    /// item templates, for both heads. The entries, their order and what they do are the same as in the
    /// desktop menu.</para>
    /// </summary>
    private MenuFlyout ForThisView(MenuFlyout menu)
    {
        var factor = MenuFontSize / 12;   // the desktop menus are written at 12 points
        menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            Setters = { new Setter(FrameworkElement.MaxWidthProperty, MenuDesktopMaxWidth * factor) },
        };
        Grow(menu.Items, factor);
        return menu;

        static void Grow(IList<MenuFlyoutItemBase> items, double by)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case MenuFlyoutSubItem sub:
                        sub.Icon = null;
                        Size(sub, by);
                        Grow(sub.Items, by);
                        break;
                    case MenuFlyoutItem one:
                        one.Icon = null;
                        Size(one, by);
                        break;
                }
            }
        }

        static void Size(Control item, double by)
        {
            item.FontSize *= by;
            item.MinHeight *= by;
            var p = item.Padding;
            item.Padding = new Thickness(p.Left * by, p.Top * by, p.Right * by, p.Bottom * by);
        }
    }

    /// <summary>One entry of a menu that this view builds itself, at the desktop menus' own size, so
    /// that <see cref="ForThisView"/> scales it with the rest.</summary>
    private static MenuFlyoutItem MenuEntry(string text, Action chosen)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            FontSize = 12,
            MinHeight = 26,
            Padding = new Thickness(11, 3, 11, 4),
        };
        item.Click += (_, _) => chosen();
        return item;
    }

    // ── tooltips ──

    /// <summary>
    /// Closes every tooltip that is open over this view. A key that the remote gives the focus opens its
    /// tooltip, and without this the tooltip would stay on screen after the key itself has faded away.
    /// Called when the transport fades and when the host's corner keys fade.
    /// </summary>
    internal static void CloseOpenToolTips(XamlRoot? root)
    {
        if (root is null)
            return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
            if (popup.Child is ToolTip tip)
                tip.IsOpen = false;
    }

    // ── the mini player as one key ──

    /// <summary>
    /// What the remote focuses for the whole mini player: one key over the card and the pill together.
    /// OK on it shows the turning record, as a press on the card's cover or words does.
    ///
    /// <para>The key is laid over the player, not around it, and it takes no pointer input, so the mouse
    /// still reaches the cover, the words, the seek bar and the three keys under it. Those are not stops
    /// for the remote. The remote's media keys play, pause and step the queue wherever the focus is.</para>
    ///
    /// <para>It is a stop only while the card holds something: with nothing on the deck the card shows
    /// the app's mark, and there is no turning record to go to (see ApplyLibraryPlayer).</para>
    /// </summary>
    private RemoteLine? _playerKey;

    /// <summary>Lays the mini player's key over it. Called once, from the constructor.</summary>
    private void BuildPlayerKey()
    {
        _playerKey = new RemoteLine(new Grid(), () =>
        {
            if (LibraryPlayerOn)
                HideLibrary();
        }, menu: null, turn: null)
        {
            IsHitTestVisible = false,
            IsTabStop = false,   // not a stop until something is on the deck
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        LibraryPlayer.Children.Add(_playerKey);
    }

    /// <summary>Makes the key a stop or not, depending on what the card holds, and gives it the card's
    /// corner radius so that the ring follows it.</summary>
    private void ApplyPlayerKey(bool on, double scale)
    {
        if (_playerKey is not { } key)
            return;
        key.IsTabStop = on;
        key.CornerRadius = new CornerRadius(PlayerCorner * scale);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Analysis;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The side menu on home: who the collection on screen belongs to and a key to switch collections, the
/// collection's screens, the way out of Pegasus, and Pegasus's own settings. It overlays the screen in
/// the same way as the mini player and uses the same smoked glass effect. The panels are in the
/// chrome's XAML under LibraryMenu.
///
/// <para><b>How it opens</b>, as on the TV, where every remote must have a way in (the Shield passes no
/// Menu key, and nobody finds a held Left): Back on home (see PegasusPlayer.TakeBack); the Menu key on
/// home, the wall and the artists and genres lists when the focused element has no menu of its own (see
/// <see cref="OpenMenuByMenuKey"/>); Left with nothing further left on those screens and on radio, and
/// Left on the mini player (see <see cref="TakeEdgeKey"/> and <see cref="OpenMenuFromMiniPlayer"/>);
/// Left held for two seconds on home or on radio (see <see cref="HoldOpens"/>); and the corner
/// key.</para>
///
/// <para><b>Leaving from it.</b> Over home, Back in the menu does not close it: the first press says at
/// the foot of the screen that a second leaves, and a second within two seconds leaves Pegasus (see
/// PegasusPlayer.TakeBack and <see cref="MenuOverHome"/>). Over any other screen Back closes it.</para>
///
/// <para><b>One panel at a time over the menu.</b> Choose library and the settings are panels the
/// menu opens (<see cref="MenuPanel"/>); while one is open the arrows search it alone (see
/// <see cref="FocusScreen"/>), and Back closes it before it closes the menu (see
/// PegasusPlayer.TakeBack).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The access to the host's settings, used by the settings panel. The host sets it when it
    /// builds the chrome.</summary>
    public IPegasusSettings? Settings { get; set; }

    /// <summary>Raised when the menu's Exit is pressed. The host leaves Pegasus, as a second Back on home
    /// does.</summary>
    public event Action? ExitRequested;

    /// <summary>Whether the menu is open or opening. Back checks this first (see PegasusPlayer).</summary>
    public bool LibraryMenuOpen { get; private set; }

    /// <summary>The panels the menu can open over itself.</summary>
    private enum MenuPanel
    {
        None,
        Collections,
        Settings,
    }

    private MenuPanel _menuPanel;

    /// <summary>The element that had the focus on home when the menu opened. The focus returns there
    /// when the menu closes. Null when the menu was opened with the pointer.</summary>
    private UIElement? _menuOpener;

    /// <summary>How long the menu takes to appear and to disappear, in milliseconds. Short, because it
    /// answers a press.</summary>
    private const int MenuInMs = 140;
    private const int MenuOutMs = 110;

    /// <summary>
    /// Opens or closes the menu. It fades in or out. It stops receiving pointer input as soon as it
    /// starts to close. It is collapsed when the fade out ends, unless it was opened again meanwhile.
    /// Closing the menu also closes the panel that is open over it.
    /// </summary>
    public void ShowLibraryMenu(bool open)
    {
        if (LibraryMenuOpen == open)
            return;

        LibraryMenuOpen = open;
        LibraryMenu.IsHitTestVisible = open;
        if (open)
        {
            _menuOpener = FocusedWithin(LibraryLayer);
            FillMenuHeader();
            SideMenuNowPlaying.Visibility = LibraryPlayerOn ? Visibility.Visible : Visibility.Collapsed;
            LibraryMenu.Visibility = Visibility.Visible;
            _libraryMenuFade.To(1, MenuInMs, null);
            FocusScreenLater(MenuEntryInFront());   // with the remote: focus goes into the menu
            return;
        }

        ShowMenuPanel(MenuPanel.None);
        _libraryMenuFade.To(0, MenuOutMs, () =>
        {
            if (!LibraryMenuOpen)
                LibraryMenu.Visibility = Visibility.Collapsed;
        });

        // With the remote: focus goes back to where it was on home, else to the screen's own place. Not to
        // the corner key: it is the mouse's, and collapsed while the remote is in use (see MouseKeys). This
        // request comes after the panel's own request above, and replaces it.
        FocusScreenLater(_menuOpener);
        _menuOpener = null;
    }

    /// <summary>
    /// An arrow key that found nothing to move to (see PegasusPlayer.NextFocus): Right in the menu closes
    /// it, and Left with nothing further left opens it on the screens where the edge does (see
    /// <see cref="EdgeOpensMenu"/>). Returns true when the key did one of those. Returns false when the
    /// host should show its bounce.
    /// </summary>
    /// <remarks>
    /// Left at the edge is the TV's way in that every remote has (FOBO 92b589fbb). The first Left still
    /// moves where there is something to move to: the banner still steps, a row still scrolls back to its
    /// first card. Only the Left that finds nothing opens the menu.
    /// </remarks>
    public bool TakeEdgeKey(FocusNavigationDirection direction)
    {
        if (direction == FocusNavigationDirection.Right && LibraryMenuOpen && _menuPanel == MenuPanel.None)
        {
            ShowLibraryMenu(false);
            return true;
        }
        if (direction == FocusNavigationDirection.Left && EdgeOpensMenu)
        {
            ShowLibraryMenu(true);
            return true;
        }
        return false;
    }

    /// <summary>
    /// The screens where the Menu key opens the side menu: home, the wall and the artists and genres
    /// lists, with no page, tuner or menu over them, and not while the focus is in the drawer. Not radio,
    /// where the Menu key is a station's menu, nor an artist's or a genre's page, where it is a track's.
    /// One line here for each further screen that should (the TV lists its playlists screen too).
    /// </summary>
    private bool MenuKeyOpensMenu =>
        CollectionInFront && !LibraryMenuOpen && !FocusWithin(QueuePanel)
        && _collection is CollectionScreen.Home or CollectionScreen.Wall
                       or CollectionScreen.Artists or CollectionScreen.Genres;

    /// <summary>The screens where Left with nothing further left opens the side menu: those where the Menu
    /// key does, and radio while its stations are in front (the TV's edgeOpensSideMenu).</summary>
    private bool EdgeOpensMenu =>
        MenuKeyOpensMenu || (RadioHomeInFront && !FocusWithin(QueuePanel));

    /// <summary>The Menu key (see PegasusPlayer.MenuKey): opens the side menu on the screens where it opens
    /// from, whatever the focus is on, as on the TV. A row there that has a menu of its own opens it on a
    /// held OK. Returns true if it did. Where it did not, the host opens the focused row's menu, if the row
    /// has one.</summary>
    public bool OpenMenuByMenuKey()
    {
        if (!MenuKeyOpensMenu)
            return false;
        ShowLibraryMenu(true);
        return true;
    }

    /// <summary>Left while the remote stands on the mini player: the side menu opens, wherever the card
    /// stands and whatever is to its left, on the screens where the edge opens it (the TV's MiniPlayer
    /// onLeft). Returns true if it did; otherwise Left moves as any arrow does.</summary>
    public bool OpenMenuFromMiniPlayer()
    {
        if (!KeyboardDriven || !EdgeOpensMenu || !FocusWithin(LibraryPlayer))
            return false;
        ShowLibraryMenu(true);
        return true;
    }

    /// <summary>Back on home with nothing over it: the side menu opens (the TV's Back on home). Returns
    /// true if it did; false where home is not the screen in front.</summary>
    public bool OpenMenuByBack()
    {
        if (!HomeInFront || TunerUp || LibraryMenuOpen)
            return false;
        ShowLibraryMenu(true);
        return true;
    }

    /// <summary>Whether the menu is open over home, with no panel over it, where Back leaves Pegasus on a
    /// second press instead of closing the menu (see PegasusPlayer.TakeBack). Over any other screen Back
    /// closes it.</summary>
    public bool MenuOverHome => LibraryMenuOpen && !MenuPanelOpen && HomeInFront;

    /// <summary>
    /// Whether Left or Right held for two seconds opens a menu on the screen in front: Left opens the
    /// side menu, on home and on radio; Right opens radio's wheel, on radio. The host asks on the press
    /// and again when the two seconds are up (see PegasusPlayer.SideKey), so a screen that changed in
    /// the meantime opens nothing. Not while the focus is in the drawer, where Left and Right move along
    /// its rows.
    /// </summary>
    /// <remarks>
    /// Both sides count a hold of two seconds. Left at the edge of a screen also opens the side menu, at
    /// once (see <see cref="TakeEdgeKey"/>); the hold stays for a Left that has somewhere to go.
    /// </remarks>
    public bool HoldOpens(FocusNavigationDirection way) => way switch
    {
        FocusNavigationDirection.Left =>
            (HomeInFront || RadioHomeInFront) && !LibraryMenuOpen && !FocusWithin(QueuePanel),
        FocusNavigationDirection.Right => RadioHomeInFront && !FocusWithin(QueuePanel),
        _ => false,
    };

    /// <summary>The two seconds are up: opens the menu on that side, if a hold in that direction still
    /// opens one. Returns true if it did.</summary>
    public bool OpenHeldMenu(FocusNavigationDirection way)
    {
        if (!HoldOpens(way))
            return false;

        if (way == FocusNavigationDirection.Left)
            ShowLibraryMenu(true);
        else
            ShowRadioWheel(true);
        return true;
    }

    /// <summary>
    /// The entry of the screen in front, where the remote's focus starts when the menu opens, as an Apple
    /// TV sidebar's does: Home over home, Albums over the wall, Artists over the artists list or an
    /// artist's page, Genres likewise, Playlists over the playlists, Search over the search, Internet Radio over
    /// radio. Else the first
    /// entry shown, which is Now playing while something is loaded.
    /// </summary>
    private Button MenuEntryInFront() => _collection switch
    {
        CollectionScreen.Home => SideMenuHome,
        CollectionScreen.Wall => SideMenuAlbums,
        CollectionScreen.Artists or CollectionScreen.Artist => SideMenuArtists,
        CollectionScreen.Genres or CollectionScreen.Genre => SideMenuGenres,
        CollectionScreen.Playlists => SideMenuPlaylists,
        CollectionScreen.Search => SideMenuSearch,
        CollectionScreen.Radio => SideMenuRadio,
        _ => SideMenuNowPlaying.Visibility == Visibility.Visible ? SideMenuNowPlaying : SideMenuHome,
    };

    /// <summary>Whether a panel is open over the menu.</summary>
    public bool MenuPanelOpen => _menuPanel != MenuPanel.None;

    /// <summary>Closes the panel that is open over the menu, if there is one. Returns true if there was.
    /// This is Back's step before it closes the menu itself. In the settings, Back from a row goes back
    /// out to its section first, as on the TV; Back on the sections closes the settings.</summary>
    public bool CloseMenuPanel()
    {
        if (!MenuPanelOpen)
            return false;
        if (_menuPanel == MenuPanel.Settings && BackToSettingsSection())
            return true;
        ShowMenuPanel(MenuPanel.None);
        return true;
    }

    /// <summary>The element that the arrow keys search while the menu is open: the panel open over it,
    /// or the menu's own panel. The ground around them has nothing to move to.</summary>
    private UIElement MenuScope() => _menuPanel switch
    {
        MenuPanel.Collections => CollectionsPanel,
        MenuPanel.Settings => SettingsPanel,
        _ => SideMenuPanel,
    };

    /// <summary>
    /// Shows one panel over the menu, or none. Each panel is built when it opens, from the state at that
    /// time. With the remote, the focus goes into the panel, and back to the key that opened it when the
    /// panel closes.
    /// </summary>
    private void ShowMenuPanel(MenuPanel panel)
    {
        var was = _menuPanel;
        _menuPanel = panel;

        ShowPanelLayer(CollectionsLayer, ref _collectionsFade, panel == MenuPanel.Collections);
        ShowPanelLayer(SettingsLayer, ref _settingsFade, panel == MenuPanel.Settings);

        switch (panel)
        {
            case MenuPanel.Collections:
                FocusScreenLater(BuildCollectionRows());
                break;
            case MenuPanel.Settings:
                FocusScreenLater(BuildSettings());
                break;
            default:
                if (was == MenuPanel.Collections)
                    FocusScreenLater(SideMenuSwitch);
                else if (was == MenuPanel.Settings)
                    FocusScreenLater(SideMenuSettings);
                break;
        }
    }

    /// <summary>The fade of each layer a panel stands in. Made the first time the panel opens, and handed
    /// to the chrome's tick with the other fades (see Fader).</summary>
    private Fader? _settingsFade, _collectionsFade;

    /// <summary>
    /// Fades a panel's layer in over <see cref="MenuInMs"/> or out over <see cref="MenuOutMs"/>, as the
    /// menu itself fades and as the TV's settings do. It takes no pointer input from the moment it starts
    /// to go, and is collapsed when the fade lands unless it was opened again meanwhile. The layer starts
    /// at opacity 0 in the markup.
    /// </summary>
    private void ShowPanelLayer(FrameworkElement layer, ref Fader? fade, bool shown)
    {
        if (fade is null)
        {
            fade = new Fader(layer);
            _faders = [.. _faders, fade];
        }

        layer.IsHitTestVisible = shown;
        if (shown)
        {
            layer.Visibility = Visibility.Visible;
            fade.To(1, MenuInMs, null);
        }
        else if (layer.Visibility == Visibility.Visible)
        {
            fade.To(0, MenuOutMs, () =>
            {
                if (!layer.IsHitTestVisible)
                    layer.Visibility = Visibility.Collapsed;
            });
        }
    }

    /// <summary>A press outside the panels: the menu closes, and the press is not passed on.</summary>
    private void LibraryMenuGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowLibraryMenu(false);
    }

    /// <summary>A press on the blurred ground around Choose library's tiles: it closes and the menu stays
    /// open.</summary>
    private void CollectionsGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowMenuPanel(MenuPanel.None);
    }

    /// <summary>A press on the darkened ground around the settings: the settings close and the menu
    /// stays open.</summary>
    private void SettingsGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowMenuPanel(MenuPanel.None);
    }

    // ── the entries ──

    /// <summary>Now playing, shown only while something is loaded: the menu closes and the collection
    /// goes down to the turning record, as a press on the mini player does. The collection's own request
    /// for the record's play key replaces the menu's request for its opener.</summary>
    private void SideMenuNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        HideLibrary();
    }

    /// <summary>Home: the menu closes and the collection crosses to home from whichever screen it is on.
    /// Over home itself it only closes, and the focus goes back to where it was.</summary>
    private void SideMenuHome_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        CrossToHome();
    }

    /// <summary>Search: the menu closes and the collection crosses to the search screen, whose field takes
    /// the focus, for both the remote and the pointer (see PegasusChrome.SearchScreen). The wall keeps its
    /// own search field, which Cmd or Ctrl+F still opens.</summary>
    private void SideMenuSearch_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        OpenSearchScreen();
    }

    /// <summary>
    /// Cmd or Ctrl+F while Pegasus is up: opens the wall's search field, from wherever the view is. The
    /// menu closes, the queue drawer closes, an album page goes back into its cover, and the collection
    /// comes up on the wall or crosses to it from home. Then the field takes the keyboard as soon as it
    /// can, as it does from the menu's Search.
    /// </summary>
    public void SearchCollection()
    {
        ShowLibraryMenu(false);
        SetQueueOpen(false);
        HideRecord();

        if (LibraryUp)
            CrossToWall();
        else
            ShowLibrary(CollectionScreen.Wall, immediate: false);

        FocusFieldLater(LibrarySearch);
    }

    /// <summary>Albums: the menu closes and the collection crosses from home to the wall.</summary>
    private void SideMenuAlbums_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        CrossToWall();
    }

    /// <summary>Artists: the menu closes and the collection crosses to the artists list (see
    /// PegasusChrome.ArtistsGenres).</summary>
    private void SideMenuArtists_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        OpenArtistsList();
    }

    /// <summary>Genres: the menu closes and the collection crosses to the genres list.</summary>
    private void SideMenuGenres_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        OpenGenresList();
    }

    /// <summary>Internet Radio: the menu closes and the collection crosses to radio (see
    /// PegasusChrome.Radio). Asked after the menu's own request for its opener, which the cross's request
    /// replaces.</summary>
    private void SideMenuRadio_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        CrossToRadio();
    }

    /// <summary>Exit: the menu closes and the host leaves Pegasus.</summary>
    private void SideMenuExit_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        ExitRequested?.Invoke();
    }

    private void SideMenuSettings_Click(object sender, RoutedEventArgs e) => ShowMenuPanel(MenuPanel.Settings);

    private void SideMenuSwitch_Click(object sender, RoutedEventArgs e) => ShowMenuPanel(MenuPanel.Collections);

    // ── whose collection is on screen ──

    /// <summary>Whether the header listens for new faces. It is hooked once, on the first fill.</summary>
    private bool _menuFacesHooked;

    /// <summary>
    /// The header: the picture, the name and the collection line of the owner of the collection on
    /// screen, and the switch key when there is another collection to switch to. It is filled each time
    /// the menu opens and after a switch, and again when a face arrives while the menu is open.
    ///
    /// <para>The desktop app has no user handle for anybody, so the line under the name says which
    /// collection it is: "My collection", or "Shared collection" for somebody else's.</para>
    ///
    /// <para>Under the line, the supporter mark when the collection's owner supports rpm: for somebody
    /// else's collection, what their profile says (PeerFace.IsSupporter); for the user's own, this
    /// account's standing, as the badge on the settings pages reads it.</para>
    /// </summary>
    private void FillMenuHeader()
    {
        var faces = App.Services.GetService<PeerFaces>();
        if (faces is not null && !_menuFacesHooked)
        {
            _menuFacesHooked = true;
            faces.Changed += () => DispatcherQueue.TryEnqueue(() =>
            {
                if (LibraryMenuOpen)
                    FillMenuHeader();
            });
        }
        faces?.LearnSelf(SelfLookup.IfDue);

        var library = Library;
        string name;
        string line;
        PeerFace face;
        bool supporter;
        if (library is { IsPeerView: true, ViewingPeer: { } peer })
        {
            face = faces?.Face(peer) ?? PeerFace.Nobody;
            name = PeerNames.Lender();
            line = Loc.Get("Viz_MenuShared");
            supporter = face.IsSupporter;
        }
        else
        {
            face = faces?.Self ?? PeerFace.Nobody;
            name = face.IsKnown ? face.DisplayName : Loc.Get("Share_MyLibrary");
            line = face.IsKnown ? Loc.Get("Share_MyLibrary") : "";
            supporter = SupporterReach.For(App.Services.GetService<SupporterService>()?.Standing).MayWearTheBadge;
        }

        SideMenuName.Text = name;
        SideMenuCollection.Text = line;
        SideMenuCollection.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SideMenuSupporter.Visibility = supporter ? Visibility.Visible : Visibility.Collapsed;

        // The picture, else the first letter of the name, else the collection's mark: a house for the
        // user's own collection, a computer for somebody else's, as the rows of the share button draw them.
        var initial = face.IsKnown && name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "";
        SideMenuAvatarPicture.Source = face.Avatar;
        SideMenuAvatarPicture.Visibility = face.Avatar is not null ? Visibility.Visible : Visibility.Collapsed;
        SideMenuAvatarInitial.Text = initial;
        SideMenuAvatarInitial.Visibility =
            face.Avatar is null && initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SideMenuAvatarGlyph.Text = library.IsPeerView ? "" : "";
        SideMenuAvatarGlyph.Visibility =
            face.Avatar is null && initial.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        SideMenuSwitch.Visibility = CollectionRows().Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── switching collections ──

    /// <summary>
    /// The collections on offer, as the main window's share button lists them (SharedLibraryRow.Build).
    /// Empty when there is nothing to switch to: nobody signed in, or no shared collection on this
    /// computer and none open.
    /// </summary>
    private static List<SharedLibraryRow> CollectionRows()
    {
        var sharing = App.Services.GetService<SharingService>();
        if (sharing is null)
            return [];

        var faces = App.Services.GetService<PeerFaces>();
        faces?.Learn(sharing.Peers);
        Func<string, PeerFace> faceOf = faces is not null ? faces.Face : static _ => PeerFace.Nobody;
        var gate = App.Services.GetService<Services.Account.AccountGate>();

        return SharedLibraryRow.Build(
            sharing.Peers,
            gate is { IsOpen: true } ? ShelfAccess.Open : ShelfAccess.NeedsAccount,
            PeerLibraryStore.Exists,
            sharing.IsReachable,
            faceOf,
            Library.ViewingPeer,
            faces?.Self ?? PeerFace.Nobody,
            Loc.Get("Share_MyLibrary"),
            Loc.Get("Settings_ShareOnline"),
            Loc.Get("Settings_ShareOffline"));
    }

    /// <summary>
    /// Fills Choose library with one tile per collection, as the TV's profile picker draws its accounts
    /// (ComputersScreen, LibraryTile). Returns the tile where the focus starts: the open collection.
    ///
    /// <para>The tiles are 220 points wide before the scale with 24 between them. A row too wide for the
    /// window is drawn smaller, down to six tenths, rather than cut off at its ends: one person's
    /// computers and friends are a handful, never a list to scroll.</para>
    ///
    /// <para>The TV also lists the computers on the network under the row; Pegasus has no such list
    /// (another of the user's own computers adds its music to this collection, see
    /// SharedLibraryRow.Build), so there is nothing to put there.</para>
    /// </summary>
    private Control? BuildCollectionRows()
    {
        var rows = CollectionRows();
        var scale = QueueScale;

        var room = Math.Max(0, Root.ActualWidth - (2 * PickerEdge * scale));
        var wanted = ((rows.Count * (PickerTileWide + PickerTileGap)) - PickerTileGap) * scale;
        var s = wanted > room && wanted > 0 ? scale * Math.Max(0.6, room / wanted) : scale;

        CollectionsRows.Children.Clear();
        CollectionsRows.Spacing = PickerTileGap * s;
        Control? open = null;
        foreach (var row in rows)
        {
            var tile = LibraryTile(row, s);
            var deviceId = row.DeviceId;
            tile.Click += (_, _) => PickCollection(deviceId);
            CollectionsRows.Children.Add(tile);
            if (row.IsOpen)
                open = tile;
        }

        return open ?? CollectionsRows.Children.OfType<Control>().FirstOrDefault();
    }

    /// <summary>The picker's tile width and the gap between tiles, and the least room left at either side
    /// of the row, before the scale.</summary>
    private const double PickerTileWide = 220;
    private const double PickerTileGap = 24;
    private const double PickerEdge = 40;

    /// <summary>The size of a face on the picker before the scale (the TV's PICKER_FACE).</summary>
    private const double PickerFace = 160;

    /// <summary>
    /// One collection on the picker: its owner's round face (their picture, else the first letter of
    /// their name, else the collection's mark), their name under it, then how it is shared with a tick
    /// before it on the open collection, and the supporter mark under that when a friend whose
    /// collection it is supports rpm. The user's own collection carries their own name, with "My
    /// collection" under it; a friend's says whether their computer is on this network.
    /// </summary>
    private Button LibraryTile(SharedLibraryRow row, double scale)
    {
        var face = row.Face;
        var name = row.IsOwn && face.IsKnown ? face.DisplayName : row.Name;
        var line = row.IsOwn ? Loc.Get("Share_MyLibrary") : row.Status;

        // The own row has no letter in the share button's list (its name there is the collection's),
        // but here it carries the person's name, so it has the person's letter.
        var initial = row.Initial.Length > 0 ? row.Initial
            : row.IsOwn && face.IsKnown ? face.DisplayName[..System.Globalization.StringInfo.GetNextTextElementLength(face.DisplayName, 0)].ToUpperInvariant()
            : "";

        var size = PickerFace * scale;
        var round = new CornerRadius(size / 2);
        var picture = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        picture.Children.Add(new Border
        {
            CornerRadius = round,
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        });
        if (row.Avatar is { } avatar)
        {
            picture.Children.Add(new AvatarPicture { Source = avatar, CornerRadius = round });
        }
        else
        {
            var mark = new TextBlock
            {
                Text = initial.Length > 0 ? initial : row.Glyph,
                FontSize = (initial.Length > 0 ? 64 : 48) * scale,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (initial.Length == 0)
                mark.FontFamily = (FontFamily)Resources["PegasusFa"];   // the house or the laptop
            picture.Children.Add(mark);
        }

        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        words.Children.Add(picture);
        words.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 22 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = (PickerTileWide - 20) * scale,
            Margin = new Thickness(0, 14 * scale, 0, 0),
        });

        var how = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6 * scale,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4 * scale, 0, 0),
        };
        if (row.IsOpen)
            how.Children.Add(new TextBlock
            {
                Text = "",   // the tick
                FontFamily = (FontFamily)Resources["PegasusFa"],
                FontSize = 13 * scale,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                VerticalAlignment = VerticalAlignment.Center,
            });
        if (line.Length > 0)
            how.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 15 * scale,
                Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = (PickerTileWide - 40) * scale,
                VerticalAlignment = VerticalAlignment.Center,
            });
        words.Children.Add(how);

        if (row.IsSupporter)
            words.Children.Add(new Viewbox
            {
                Height = SupporterHigh(12, scale),   // the TV's 12 point mark
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8 * scale, 0, 0),
                Child = new SupporterBadge { ReadsAccount = false, IsSupporter = true },
            });

        var key = new Button
        {
            Style = (Style)Resources["PegasusSideButton"],
            Content = words,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalAlignment = VerticalAlignment.Top,
            Width = PickerTileWide * scale,
            Padding = new Thickness(0, 14 * scale, 0, 14 * scale),
            CornerRadius = new CornerRadius(12 * scale),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, name);
        LightOnKeys(key);
        return key;
    }

    /// <summary>
    /// Switches to another collection, or back to the user's own, as the main window's share button
    /// does. Somebody else's collection needs the sign-in; the user's own never does. The collection opens
    /// on its home, as on the TV: home reads it again by itself (it listens for the change), and the other
    /// screens follow it (see PegasusChrome.Follow). The header is filled again here.
    /// </summary>
    private async void PickCollection(string deviceId)
    {
        ShowMenuPanel(MenuPanel.None);

        var sharing = App.Services.GetService<SharingService>();
        if (sharing is null
            || string.Equals(deviceId, Library.ViewingPeer ?? "", StringComparison.OrdinalIgnoreCase))
            return;

        // Whose collection it is, for the screen shown while it is read: the row as the list drew it.
        var row = CollectionRows().FirstOrDefault(
            r => string.Equals(r.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        if (deviceId.Length > 0 && !await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForSharing")))
            return;

        var switching = ++_switch;
        ShowSwitching(row);

        if (deviceId.Length == 0)
        {
            sharing.CloseLibrary();
        }
        else if (!sharing.OpenLibrary(deviceId))
        {
            HideSwitching(switching);
            return;
        }

        // Asked after the switch, in the same dispatcher turn. Home hears of the change through the
        // library's event and reads again on a later turn, so that reading is the next one to start.
        var read = _home?.NextReadingDone() ?? Task.CompletedTask;

        // Then home is put in front, so that it reads: a home that is not shown reads nothing, and the
        // switching screen would stay up to its longest over any other screen.
        HideRecord();
        CrossToHome();

        if (LibraryMenuOpen)
            FillMenuHeader();

        // The switching screen stays until home has read and shown the collection. It stays at least
        // long enough to be read and not just flash, and never for ever: a home that reads nothing (it
        // is not shown) never answers.
        await Task.WhenAll(
            Task.WhenAny(read, Task.Delay(SwitchMostMs)),
            Task.Delay(SwitchLeastMs));
        HideSwitching(switching);
    }

    // ── the screen while a collection is switched to ──

    /// <summary>The shortest and longest time the switching screen stays, in milliseconds. The longest
    /// is only an upper limit for a reading that never ends, not a normal wait: home's own longest wait
    /// is six seconds for pictures after the shelf is read.</summary>
    private const int SwitchLeastMs = 800;
    private const int SwitchMostMs = 20000;

    /// <summary>Which switch the screen is shown for. There is one number per pick, so the end of an
    /// earlier switch cannot hide the screen of a later one.</summary>
    private int _switch;

    /// <summary>Whether the switching screen is shown or appearing. While it is, the shell does not pass
    /// it any of the remote's keys (see ShellPage.TakePegasusKey).</summary>
    public bool SwitchingCollection { get; private set; }

    /// <summary>Shows the switching screen with the face of the collection's owner: the picture, else
    /// the first letter of their name, else the collection's mark.</summary>
    private void ShowSwitching(SharedLibraryRow? row)
    {
        var picture = row?.Avatar;
        var initial = row?.Initial ?? "";
        SwitchFacePicture.Source = picture;
        SwitchFacePicture.Visibility = picture is not null ? Visibility.Visible : Visibility.Collapsed;
        SwitchFaceInitial.Text = initial;
        SwitchFaceInitial.Visibility =
            picture is null && initial.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SwitchFaceGlyph.Text = row?.Glyph ?? "";
        SwitchFaceGlyph.Visibility =
            picture is null && initial.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        SizeSwitching(QueueScale);
        SwitchingCollection = true;
        SwitchLayer.Visibility = Visibility.Visible;
        SwitchSpinner.Spinning = true;
        _switchFade.To(1, MenuInMs, null);
    }

    /// <summary>Hides the switching screen, if it is still shown for <paramref name="switching"/>. It is
    /// collapsed when the fade ends, unless a later switch has shown it again.</summary>
    private void HideSwitching(int switching)
    {
        if (switching != _switch)
            return;

        SwitchingCollection = false;
        _switchFade.To(0, ScreenCrossMs, () =>
        {
            if (SwitchingCollection)
                return;
            SwitchSpinner.Spinning = false;
            SwitchLayer.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Sizes the switching screen for this window's scale: the face, the words (the same size
    /// as the menu's own) and the spinner under them.</summary>
    private void SizeSwitching(double scale)
    {
        var face = 120 * scale;
        SwitchFace.Width = SwitchFace.Height = face;
        SwitchFaceGround.CornerRadius = new CornerRadius(face / 2);
        SwitchFacePicture.CornerRadius = new CornerRadius(face / 2);
        SwitchFaceRing.CornerRadius = new CornerRadius(face / 2);
        SwitchFaceRing.BorderThickness = new Thickness(3 * scale);
        SwitchFaceGlyph.FontSize = 44 * scale;
        SwitchFaceInitial.FontSize = 48 * scale;
        SwitchStack.Spacing = 26 * scale;
        SwitchWords.FontSize = 24 * scale;
        SwitchSpinner.Width = SwitchSpinner.Height = 64 * scale;
    }

    // ── the settings ──

    /// <summary>One section of the settings: the key of its name, its glyph, and its rows.</summary>
    private sealed record SettingsSection(string Title, string Glyph, SettingLine[] Lines);

    /// <summary>
    /// One row of a section: the key of its name, what its value says now, what OK on it does, and,
    /// for a row that only means something while another setting is on, when it is shown.
    /// </summary>
    private sealed record SettingLine(
        string Label,
        Func<IPegasusSettings, string> Value,
        Action<IPegasusSettings> Change,
        Func<IPegasusSettings, bool>? Shown = null);

    /// <summary>
    /// The settings, as the TV's settings panel lists them (SettingsPage): its six sections in its order,
    /// each with the rows Pegasus has for it. A section with no row shown is left out of the left card,
    /// so Profile and Display stay hidden until they have a row.
    ///
    /// <para><b>A further row is one line</b> in its section: its name, its value from
    /// <see cref="IPegasusSettings"/>, and the call OK makes. The panel builds, lights, moves through and
    /// repaints it with no other change. The setting itself goes on IPegasusSettings and its host,
    /// PegasusPlayer.Settings.</para>
    /// </summary>
    private static readonly SettingsSection[] SettingsSections =
    [
        new("Tv_SettingsGeneral", "",
        [
            new("Tv_SettingsAlbumView", s => Loc.Get(s.AlbumCarousel ? "Tv_AlbumViewCarousel" : "Tv_AlbumViewGrid"),
                s => s.ToggleAlbumCarousel()),
            new("Tv_SettingsAlbumSort", s => Loc.Get(OrderingName(s.AlbumOrdering)), s => s.NextAlbumOrdering()),
            new("Tv_SettingsScreensaver", s => s.ScreensaverMinutes switch
            {
                0 => Loc.Get("Viz_SettingOff"),
                1 => Loc.Get("Tv_SettingsAfterMinute"),
                var minutes => Loc.Format("Tv_SettingsAfterMinutes", minutes),
            }, s => s.NextScreensaver()),
        ]),
        new("Settings_Profile", "", []),
        new("Settings_Display", "", []),
        new("Settings_Sound", "",
        [
            new("Settings_Normalize", s => Loc.Get(s.Levelling switch
            {
                NormalizeMode.Album => "Settings_NormalizeAlbum",
                NormalizeMode.Track => "Settings_NormalizeTrack",
                _ => "Settings_NormalizeOff",
            }), s => s.NextLevelling()),
            new("Settings_NormalizeClassical", s => OnOff(s.LevelsClassical), s => s.ToggleLevelsClassical(),
                Shown: s => s.Levelling != NormalizeMode.Off),
        ]),
        new("Tv_SettingsPlayer", "",
        [
            new("Viz_SettingsCensor", s => OnOff(s.MaskProfanity), s => s.ToggleMaskProfanity()),
            new("Viz_SettingsAlign", s => OnOff(s.AlignWords), s => s.ToggleAlignWords()),
        ]),
        new("Tv_SettingsVisualisation", "",
        [
            new("Viz_SettingsLayout", s => Loc.Get(s.Layout switch
            {
                PegasusView.Centred => "Viz_LayoutCompact",
                PegasusView.Room => "Viz_LayoutVisuals",
                _ => "Viz_LayoutFull",
            }), s => s.NextLayout()),
            new("Tv_SettingsRoomStyle", s => Loc.Get(s.RoomStyle switch
            {
                RPM.Core.Viz.RoomStyle.Yang => "Tv_RoomStyleYang",
                RPM.Core.Viz.RoomStyle.Lava => "Tv_RoomStyleLava",
                RPM.Core.Viz.RoomStyle.Alchemy => "Tv_RoomStyleAlchemy",
                _ => "Tv_RoomStyleOriginal",
            }), s => s.NextRoomStyle()),
        ]),
    ];

    /// <summary>On or Off, as a row's value says it.</summary>
    private static string OnOff(bool on) => Loc.Get(on ? "Viz_SettingOn" : "Viz_SettingOff");

    /// <summary>The sections shown in the left card this time, and their keys, in the same order.</summary>
    private readonly List<SettingsSection> _shownSections = [];
    private readonly List<Button> _sectionKeys = [];

    /// <summary>Which of <see cref="_shownSections"/> the right card shows.</summary>
    private int _settingsSection;

    /// <summary>The right card's rows as built: each row's setting, its value field and its key.</summary>
    private readonly List<(SettingLine Line, TextBlock Value, Button Key)> _settingRows = [];

    /// <summary>
    /// Fills the settings: the sections that have a row to show in the left card, and the first of them
    /// in the right card. Returns the first section, where the remote starts, as on the TV. Each is built
    /// when the panel opens, from the settings at that time.
    /// </summary>
    private Control? BuildSettings()
    {
        var scale = QueueScale;
        SettingsPages.Children.Clear();
        _shownSections.Clear();
        _sectionKeys.Clear();

        if (Settings is { } settings)
            foreach (var section in SettingsSections)
            {
                if (!section.Lines.Any(line => line.Shown?.Invoke(settings) ?? true))
                    continue;

                var index = _shownSections.Count;
                var key = SectionRow(section, scale);

                // The remote standing on a section shows it on the right, as the TV's does. OK on it (or a
                // press) goes into its rows.
                key.GotFocus += (_, _) =>
                {
                    if (index != _settingsSection)
                        ShowSettingsSection(index);
                    PaintSections();
                };
                key.LostFocus += (_, _) => PaintSections();
                key.Click += (_, _) =>
                {
                    ShowSettingsSection(index);
                    PaintSections();
                    FocusScreenLater(_settingRows.Count > 0 ? _settingRows[0].Key : null);
                };

                _shownSections.Add(section);
                _sectionKeys.Add(key);
                SettingsPages.Children.Add(key);
            }

        _settingsSection = -1;
        ShowSettingsSection(0);
        PaintSections();
        return _sectionKeys.FirstOrDefault();
    }

    /// <summary>Shows section <paramref name="index"/> on the right: its glyph and its name over its
    /// rows.</summary>
    private void ShowSettingsSection(int index)
    {
        if (index < 0 || index >= _shownSections.Count)
        {
            SettingsGlyph.Text = SettingsTitle.Text = "";
            SettingsRows.Children.Clear();
            _settingRows.Clear();
            return;
        }

        _settingsSection = index;
        var section = _shownSections[index];
        SettingsGlyph.Text = section.Glyph;
        SettingsTitle.Text = Loc.Get(section.Title);
        BuildSettingRows();
    }

    /// <summary>Builds the right card's rows for the section shown, those shown at this moment, and writes
    /// their values.</summary>
    private void BuildSettingRows()
    {
        var scale = QueueScale;
        SettingsRows.Children.Clear();
        _settingRows.Clear();
        if (Settings is not { } settings || _settingsSection < 0)
            return;

        foreach (var line in _shownSections[_settingsSection].Lines)
        {
            if (!(line.Shown?.Invoke(settings) ?? true))
                continue;
            var key = SettingRow(Loc.Get(line.Label), scale, out var value);
            key.Click += (_, _) => ChangeSetting(line);
            _settingRows.Add((line, value, key));
            SettingsRows.Children.Add(key);
        }
        PaintSettings();
    }

    /// <summary>
    /// OK on a row: its setting moves to its next value. When that shows or hides another row of the
    /// section (levelling switched off hides its classical row), the rows are built again and the remote
    /// stays on the row it pressed; otherwise only the values are written again.
    /// </summary>
    private void ChangeSetting(SettingLine line)
    {
        if (Settings is not { } settings || _settingsSection < 0)
            return;

        line.Change(settings);

        // The album wall's two rows: a wall up behind the menu takes the new view and order now, not on its
        // next visit.
        if (WallUp)
            ApplyAlbumView();

        var shown = _shownSections[_settingsSection].Lines.Where(l => l.Shown?.Invoke(settings) ?? true).ToList();
        if (shown.SequenceEqual(_settingRows.Select(row => row.Line)))
        {
            PaintSettings();
            return;
        }

        BuildSettingRows();
        var again = _settingRows.FindIndex(row => ReferenceEquals(row.Line, line));
        if (again >= 0)
            FocusScreenLater(_settingRows[again].Key);
    }

    /// <summary>Writes the value of each row from the host's current settings.</summary>
    private void PaintSettings()
    {
        if (Settings is not { } settings)
            return;
        foreach (var (line, value, _) in _settingRows)
            value.Text = line.Value(settings);
    }

    /// <summary>The fill of the section shown on the right while the remote is not on it (it is in the
    /// section's rows, or the pointer is in use): the TV's CHOSEN.</summary>
    private static readonly SolidColorBrush SectionChosen = new(Microsoft.UI.ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));

    /// <summary>Lights the section keys: the one the remote stands on as a menu entry is lit, the one
    /// shown on the right with the fainter fill, the others not at all.</summary>
    private void PaintSections()
    {
        for (var i = 0; i < _sectionKeys.Count; i++)
        {
            var key = _sectionKeys[i];
            key.Background = key.FocusState is FocusState.Keyboard or FocusState.Programmatic
                ? MenuKeyLit
                : i == _settingsSection ? SectionChosen : TransparentFill;
        }
    }

    private static readonly SolidColorBrush TransparentFill = new(Microsoft.UI.Colors.Transparent);

    /// <summary>Back while the remote is in a section's rows: it goes back out to the section, as on the
    /// TV. Returns false where it was not in the rows.</summary>
    private bool BackToSettingsSection()
    {
        if (!KeyboardDriven || !FocusWithin(SettingsRows) || _settingsSection < 0
            || _settingsSection >= _sectionKeys.Count)
            return false;
        FocusScreenLater(_sectionKeys[_settingsSection]);
        return true;
    }

    /// <summary>
    /// Where an arrow moves the focus in the settings, as on the TV: Up and Down move through the
    /// sections, or through the rows; Right from a section goes into its first row, Left from a row back
    /// to its section. Nothing else leaves either card. Null where that way goes nowhere, so the host
    /// bounces. A search by layout would cross between the cards to whichever row or section stood level,
    /// not to the first row or the section shown.
    /// </summary>
    private Control? AcrossSettings(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        var section = _sectionKeys.FindIndex(key => Inside(from, key));
        if (section >= 0)
            return direction switch
            {
                FocusNavigationDirection.Up => section > 0 ? _sectionKeys[section - 1] : null,
                FocusNavigationDirection.Down => section < _sectionKeys.Count - 1 ? _sectionKeys[section + 1] : null,
                FocusNavigationDirection.Right => _settingRows.Count > 0 ? _settingRows[0].Key : null,
                _ => null,
            };

        var row = _settingRows.FindIndex(r => Inside(from, r.Key));
        if (row >= 0)
            return direction switch
            {
                FocusNavigationDirection.Up => row > 0 ? _settingRows[row - 1].Key : null,
                FocusNavigationDirection.Down => row < _settingRows.Count - 1 ? _settingRows[row + 1].Key : null,
                FocusNavigationDirection.Left when _settingsSection >= 0 && _settingsSection < _sectionKeys.Count
                    => _sectionKeys[_settingsSection],
                _ => null,
            };

        return next;
    }

    /// <summary>A section in the left card: its glyph in a column of its own and its name, 60 points high
    /// before the scale, as the side menu's entries are. Its fill is set by <see cref="PaintSections"/>.</summary>
    private Button SectionRow(SettingsSection section, double scale)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 * scale };
        line.Children.Add(new TextBlock
        {
            Text = section.Glyph,
            FontFamily = (FontFamily)Resources["PegasusFa"],
            FontSize = 22 * scale,
            Width = 32 * scale,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.Children.Add(new TextBlock
        {
            Text = Loc.Get(section.Title),
            FontSize = 24 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        return new Button
        {
            Style = (Style)Resources["PegasusSideButton"],
            Content = line,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Height = 60 * scale,
            Padding = new Thickness(14 * scale, 0, 14 * scale, 0),
            CornerRadius = new CornerRadius(PlayerCorner * scale),
        };
    }

    /// <summary>One settings row: its name on the left, its value on the right, 60 points high before the
    /// scale.</summary>
    private Button SettingRow(string label, double scale, out TextBlock value)
    {
        var name = new TextBlock
        {
            Text = label,
            FontSize = 22 * scale,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        value = new TextBlock
        {
            FontSize = 22 * scale,
            Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = new Grid { ColumnSpacing = 40 * scale };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(value, 1);
        line.Children.Add(name);
        line.Children.Add(value);

        var key = MenuRow(line, scale);
        key.Height = 60 * scale;
        key.Padding = new Thickness(14 * scale, 0, 14 * scale, 0);
        return key;
    }

    // ── the menu's look ──

    /// <summary>A row in one of the menu's panels: a button with the look of the side menu's entries
    /// around <paramref name="content"/>. It is lit while the remote has the focus on it.</summary>
    private Button MenuRow(UIElement content, double scale)
    {
        var key = new Button
        {
            Style = (Style)Resources["PegasusSideButton"],
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14 * scale, 10 * scale, 14 * scale, 10 * scale),
            CornerRadius = new CornerRadius(PlayerCorner * scale),
        };
        LightOnKeys(key);
        return key;
    }

    /// <summary>The fill of a menu button while the remote has the focus on it. The remote's glow is
    /// drawn under the screens. Under this glass it is dimmed and blurred together with the room, so the
    /// entry itself is lit as well.</summary>
    private static readonly SolidColorBrush MenuKeyLit = new(Microsoft.UI.ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    /// <summary>
    /// Lights a menu button while it has the focus from the keys, and restores its own fill when the
    /// focus leaves. A focus from the pointer (a click) is not lit, because the pointer's own hover
    /// already shows where it is.
    /// </summary>
    private static void LightOnKeys(Button key)
    {
        var rest = key.Background;
        key.GotFocus += (_, _) =>
        {
            if (key.FocusState is FocusState.Keyboard or FocusState.Programmatic)
                key.Background = MenuKeyLit;
        };
        key.LostFocus += (_, _) => key.Background = rest;
    }

    /// <summary>The side menu's width before the scale.</summary>
    private const double SideMenuWide = 495;

    /// <summary>
    /// The height of a box that draws the supporter badge (drawn at 10 points, about 19 high) as if its
    /// word were <paramref name="points"/> points: the badge's pill, padding and heart all grow with it,
    /// as the TV's SupporterMark does with its size.
    /// </summary>
    private static double SupporterHigh(double points, double scale) => 19 * (points / 10) * scale;

    /// <summary>Whether the side menu's own buttons have been given their lit state. This happens once.</summary>
    private bool _sideMenuLit;

    /// <summary>Sizes the side menu for the collection's scale. Its names are the same size as the
    /// banner titles on home (24 points before the scale), in the app's own type. It is 495 points wide
    /// before the scale, as the TV's is: it was 330, and half as wide again a long name (a first name and
    /// a family name with a particle) is not cut short beside the switch key. Nothing else is placed from
    /// its width; the screen under it does not move.</summary>
    private void SizeSideMenu(double scale)
    {
        if (!_sideMenuLit)
        {
            _sideMenuLit = true;
            foreach (var key in SideMenuItems.Children.OfType<Button>()
                         .Concat([SideMenuSwitch, SideMenuExit, SideMenuSettings]))
                LightOnKeys(key);
        }

        var corner = new CornerRadius(PlayerCorner * scale);
        var edge = 10 * scale;

        SideMenuPanel.Width = SideMenuWide * scale;
        SideMenuPanel.Margin = new Thickness(edge, edge, 0, edge);
        SideMenuFrost.CornerRadius = corner;
        SideMenuGlass.CornerRadius = corner;
        SideMenuBody.Padding = new Thickness(16 * scale, 20 * scale, 16 * scale, 16 * scale);

        // The header: the picture, the two lines, the switch key.
        var face = 64 * scale;
        SideMenuAvatar.Width = SideMenuAvatar.Height = face;
        SideMenuAvatarGround.CornerRadius = new CornerRadius(face / 2);
        SideMenuAvatarPicture.CornerRadius = new CornerRadius(face / 2);
        SideMenuAvatarGlyph.FontSize = 24 * scale;
        SideMenuAvatarInitial.FontSize = 26 * scale;
        SideMenuWho.Margin = new Thickness(14 * scale, 0, 10 * scale, 0);
        SideMenuName.FontSize = 24 * scale;
        SideMenuCollection.FontSize = 15 * scale;
        SideMenuSupporter.Height = SupporterHigh(13, scale);   // the TV's 13 point mark
        SideMenuSupporter.Margin = new Thickness(0, 6 * scale, 0, 0);
        SideMenuSwitch.Width = SideMenuSwitch.Height = 52 * scale;
        SideMenuSwitch.FontSize = 20 * scale;
        SideMenuSwitch.CornerRadius = corner;

        // The entries: the icon in a column of its own so that the names line up, then the name.
        SideMenuItems.Margin = new Thickness(0, 28 * scale, 0, 0);
        SideMenuItems.Spacing = 4 * scale;
        foreach (var key in SideMenuItems.Children.OfType<Button>())
        {
            key.Height = 60 * scale;
            key.Padding = new Thickness(12 * scale, 0, 12 * scale, 0);
            key.CornerRadius = corner;
            if (key.Content is not StackPanel { Children: [TextBlock icon, TextBlock name] } row)
                continue;
            row.Spacing = 16 * scale;
            icon.FontSize = 22 * scale;
            icon.Width = 32 * scale;
            name.FontSize = 24 * scale;
        }

        // The foot: Exit Pegasus is as wide as the space left by the settings key beside it.
        SideMenuFoot.ColumnSpacing = 10 * scale;
        SideMenuExit.Height = 60 * scale;
        SideMenuExit.FontSize = 22 * scale;
        SideMenuExit.CornerRadius = corner;
        SideMenuSettings.Width = SideMenuSettings.Height = 60 * scale;
        SideMenuSettings.FontSize = 22 * scale;
        SideMenuSettings.CornerRadius = corner;

        // The two panels over the menu. Choose library: its title at the TV's largest size (44), the row of
        // tiles 40 under it. The tiles are sized as they are built (see LibraryTile).
        CollectionsTitle.FontSize = 44 * scale;
        CollectionsPanel.Spacing = 40 * scale;

        // The settings: the TV's two cards, 340 and 760 wide and 640 high before the scale, 16 apart. They
        // are no higher than the window leaves room for, with the menu's edge above and below.
        var settingsHigh = Math.Min(SettingsHigh * scale, Math.Max(0, Root.ActualHeight - (4 * edge)));
        SettingsPanel.Spacing = 16 * scale;
        SettingsPagesFrost.CornerRadius = SettingsPagesGlass.CornerRadius = corner;
        SettingsPagesCard.Width = SettingsPagesWide * scale;
        SettingsPagesCard.Height = settingsHigh;
        SettingsPages.Padding = new Thickness(10 * scale);
        SettingsPages.Spacing = 4 * scale;

        SettingsFrost.CornerRadius = SettingsGlass.CornerRadius = corner;
        SettingsRowsCard.Width = SettingsRowsWide * scale;
        SettingsRowsCard.Height = settingsHigh;
        SettingsBody.Padding = new Thickness(16 * scale, 20 * scale, 16 * scale, 20 * scale);
        SettingsHeading.Spacing = 14 * scale;
        SettingsHeading.Margin = new Thickness(14 * scale, 0, 0, 14 * scale);
        SettingsGlyph.FontSize = 22 * scale;
        SettingsTitle.FontSize = 26 * scale;
    }

    /// <summary>The settings' two cards before the scale: the sections' width, the rows' width and the
    /// height of both (the TV's PAGES_WIDE, ROWS_WIDE and PANEL_HIGH).</summary>
    private const double SettingsPagesWide = 340;
    private const double SettingsRowsWide = 760;
    private const double SettingsHigh = 640;
}

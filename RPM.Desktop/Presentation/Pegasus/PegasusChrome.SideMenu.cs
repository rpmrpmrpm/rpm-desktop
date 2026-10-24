using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
/// <para><b>One panel at a time over the menu.</b> The collections list and the settings are panels the
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

        // With the remote: focus goes back to where it was on home, else to the corner key. This request
        // comes after the panel's own request above, and replaces it.
        FocusScreenLater(_menuOpener ?? LibraryBackButton);
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

    /// <summary>The Menu key, when the focused element has no menu of its own (the host asks that first;
    /// see PegasusPlayer.MenuKey): opens the side menu on the screens where it opens from. Returns true
    /// if it did.</summary>
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
    /// artist's page, Genres likewise, Internet Radio over radio. Else the first entry shown, which is Now
    /// playing while something is loaded.
    /// </summary>
    private Button MenuEntryInFront() => _collection switch
    {
        CollectionScreen.Home => SideMenuHome,
        CollectionScreen.Wall => SideMenuAlbums,
        CollectionScreen.Artists or CollectionScreen.Artist => SideMenuArtists,
        CollectionScreen.Genres or CollectionScreen.Genre => SideMenuGenres,
        CollectionScreen.Radio => SideMenuRadio,
        _ => SideMenuNowPlaying.Visibility == Visibility.Visible ? SideMenuNowPlaying : SideMenuHome,
    };

    /// <summary>Whether a panel is open over the menu.</summary>
    public bool MenuPanelOpen => _menuPanel != MenuPanel.None;

    /// <summary>Closes the panel that is open over the menu, if there is one. Returns true if there was.
    /// This is Back's step before it closes the menu itself.</summary>
    public bool CloseMenuPanel()
    {
        if (!MenuPanelOpen)
            return false;
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

        CollectionsPanel.Visibility = panel == MenuPanel.Collections ? Visibility.Visible : Visibility.Collapsed;
        SettingsLayer.Visibility = panel == MenuPanel.Settings ? Visibility.Visible : Visibility.Collapsed;

        switch (panel)
        {
            case MenuPanel.Collections:
                FocusScreenLater(BuildCollectionRows());
                break;
            case MenuPanel.Settings:
                FocusScreenLater(BuildSettingsRows());
                break;
            default:
                if (was == MenuPanel.Collections)
                    FocusScreenLater(SideMenuSwitch);
                else if (was == MenuPanel.Settings)
                    FocusScreenLater(SideMenuSettings);
                break;
        }
    }

    /// <summary>A press outside the panels: the menu closes, and the press is not passed on.</summary>
    private void LibraryMenuGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowLibraryMenu(false);
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

    /// <summary>Search: the menu closes, the collection crosses to the wall, and the wall's search field
    /// takes the focus, for both the remote and the pointer.</summary>
    private void SideMenuSearch_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        CrossToWall();
        FocusFieldLater(LibrarySearch);   // replaces the wall's own request for its first album
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

    /// <summary>Fills the collections panel with one row per collection. Returns the row where the focus
    /// starts: the open collection.</summary>
    private Control? BuildCollectionRows()
    {
        var scale = QueueScale;
        CollectionsRows.Children.Clear();
        Control? open = null;

        foreach (var row in CollectionRows())
        {
            var avatar = new Grid { Width = 44 * scale, Height = 44 * scale };
            avatar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(22 * scale),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            });
            if (row.Avatar is { } picture)
            {
                avatar.Children.Add(new AvatarPicture { Source = picture, CornerRadius = new CornerRadius(22 * scale) });
            }
            else
            {
                var mark = new TextBlock
                {
                    Text = row.Initial.Length > 0 ? row.Initial : row.Glyph,
                    FontSize = 18 * scale,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (row.Initial.Length == 0)
                    mark.FontFamily = (FontFamily)Resources["PegasusFa"];   // the house or the computer
                avatar.Children.Add(mark);
            }

            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock
            {
                Text = row.Name,
                FontSize = 20 * scale,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (row.Status.Length > 0)
                words.Children.Add(new TextBlock
                {
                    Text = row.Status,
                    FontSize = 14 * scale,
                    Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

            // The open collection has a tick at the right end.
            var tick = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)Resources["PegasusFa"],
                FontSize = 16 * scale,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = row.IsOpen ? Visibility.Visible : Visibility.Collapsed,
            };

            var line = new Grid { ColumnSpacing = 14 * scale };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(words, 1);
            Grid.SetColumn(tick, 2);
            line.Children.Add(avatar);
            line.Children.Add(words);
            line.Children.Add(tick);

            var key = MenuRow(line, scale);
            var deviceId = row.DeviceId;
            key.Click += (_, _) => PickCollection(deviceId);
            CollectionsRows.Children.Add(key);
            if (row.IsOpen)
                open = key;
        }

        // Beside the switch key, at the top of the screen.
        CollectionsPanel.Margin = new Thickness(
            SideMenuPanel.Margin.Left + SideMenuPanel.Width + (10 * scale),
            SideMenuPanel.Margin.Top,
            0,
            0);
        return open ?? CollectionsRows.Children.OfType<Control>().FirstOrDefault();
    }

    /// <summary>
    /// Switches to another collection, or back to the user's own, as the main window's share button
    /// does. Somebody else's collection needs the sign-in; the user's own never does. Home reads the
    /// collection again by itself (it listens for the change); the header is filled again here. The wall
    /// always stays on the user's own collection (see FillLibraryAsync).
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

    /// <summary>The value fields of the settings rows. They are written again after every change.</summary>
    private TextBlock? _layoutValue, _censorValue, _alignValue;

    /// <summary>Fills the settings panel with a row for each of Pegasus's settings. Returns the first
    /// row, where the focus starts. OK on a row, or a press, changes its setting.</summary>
    private Control? BuildSettingsRows()
    {
        var scale = QueueScale;
        SettingsRows.Children.Clear();

        var layout = SettingRow(Loc.Get("Viz_SettingsLayout"), scale, out _layoutValue, () => Settings?.NextLayout());
        SettingsRows.Children.Add(layout);
        SettingsRows.Children.Add(SettingRow(Loc.Get("Viz_SettingsCensor"), scale, out _censorValue,
            () => Settings?.ToggleMaskProfanity()));
        SettingsRows.Children.Add(SettingRow(Loc.Get("Viz_SettingsAlign"), scale, out _alignValue,
            () => Settings?.ToggleAlignWords()));

        PaintSettings();
        return layout;
    }

    /// <summary>One settings row: its name on the left, its value on the right.</summary>
    private Button SettingRow(string label, double scale, out TextBlock value, Action change)
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
        key.Click += (_, _) =>
        {
            change();
            PaintSettings();
        };
        return key;
    }

    /// <summary>Writes the value of each settings row from the host's current settings.</summary>
    private void PaintSettings()
    {
        if (Settings is not { } settings)
            return;

        if (_layoutValue is not null)
            _layoutValue.Text = Loc.Get(settings.Layout switch
            {
                PegasusView.Centred => "Viz_LayoutCompact",
                PegasusView.Room => "Viz_LayoutVisuals",
                _ => "Viz_LayoutFull",
            });
        if (_censorValue is not null)
            _censorValue.Text = Loc.Get(settings.MaskProfanity ? "Viz_SettingOn" : "Viz_SettingOff");
        if (_alignValue is not null)
            _alignValue.Text = Loc.Get(settings.AlignWords ? "Viz_SettingOn" : "Viz_SettingOff");
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

        // The two panels over the menu.
        CollectionsFrost.CornerRadius = CollectionsGlass.CornerRadius = corner;
        CollectionsPanel.Width = 380 * scale;
        CollectionsRows.Padding = new Thickness(8 * scale);
        CollectionsRows.Spacing = 4 * scale;

        SettingsFrost.CornerRadius = SettingsGlass.CornerRadius = corner;
        SettingsPanel.Width = 620 * scale;
        SettingsBody.Padding = new Thickness(20 * scale, 22 * scale, 20 * scale, 20 * scale);
        SettingsBody.Spacing = 16 * scale;
        SettingsTitle.FontSize = 28 * scale;
        SettingsTitle.Margin = new Thickness(14 * scale, 0, 0, 0);
        SettingsRows.Spacing = 6 * scale;
    }
}

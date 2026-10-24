using RPM.Core.Library;
using RPM.Core.Radio;
using RPM.Desktop.Presentation.Main.Home;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What the page that owns the library hands Pegasus so its home screen reads, plays and answers its
/// menus the way the main window's home does.
/// </summary>
/// <param name="Wire">Sets every hook the main window's home is given on another <see cref="HomeView"/>:
/// the cover and track loaders, the play doors, the album and track menus, and the heart pushes. It is the
/// same method MainPage wires its own home with, so the two homes are given one list of hooks.</param>
/// <param name="AddMusic">The locked panel's button. Leaves Pegasus for the library and opens Add music
/// there, because the import that follows runs in the main window.</param>
public sealed record PegasusHomeHooks(Action<HomeView> Wire, Action AddMusic);

/// <summary>Which screen the collection shows. Chosen when the collection comes up (see
/// <see cref="PegasusChrome.ShowLibrary"/>), and changed while it is up by the wall's back key and Back
/// (both go to home) and by the side menu's entries.</summary>
public enum CollectionScreen
{
    /// <summary>Every record, eight to a row, with search, sort and the favourites filter. What the key
    /// under the words opens.</summary>
    Wall,

    /// <summary>The main window's home screen. What Pegasus opens on when nothing is on the deck, and
    /// where the wall's back key goes.</summary>
    Home,

    /// <summary>Internet radio: a banner of stations, the essential stations of the listener's country and
    /// the favourite stations. Opened by the side menu's Internet Radio; Back goes to home. See
    /// PegasusChrome.Radio.</summary>
    Radio,

    /// <summary>Every act the collection files a record under, as round photographs. Opened by the side
    /// menu's Artists; Back goes to home. See PegasusChrome.Artists.</summary>
    Artists,

    /// <summary>The collection's genres, as cards in their colours. Opened by the side menu's Genres; Back
    /// goes to home. See PegasusChrome.Genres.</summary>
    Genres,

    /// <summary>One artist's page. Back goes to the screen it was opened from. See
    /// PegasusChrome.ArtistPage.</summary>
    Artist,

    /// <summary>One genre's page. Back goes to the screen it was opened from. See
    /// PegasusChrome.GenrePage.</summary>
    Genre,

    /// <summary>The generated lists, then the listener's own, as cards. Opened by the side menu's
    /// Playlists; Back goes to home. See PegasusChrome.PlaylistsScreen.</summary>
    Playlists,

    /// <summary>The search over the collection's artists, albums and tracks and the radio directory's
    /// stations. Opened by the side menu's Search; Back goes to home. See PegasusChrome.SearchScreen.</summary>
    Search,
}

/// <summary>
/// Home in Pegasus: the main window's home screen, over the room, when Pegasus is opened with nothing on
/// the deck.
///
/// <para><b>The same control, not a copy of it.</b> A second <see cref="HomeView"/> stands in the
/// collection layer where the wall stands, wired by the method MainPage wires its own with (see
/// <see cref="PegasusHomeHooks"/>), so it reads the same rows, draws the same banner and cards, opens the
/// same drawer under a row and answers a right-click with the same menus. It is a second instance rather
/// than the main window's moved over: that one is shown, hidden and resized by MainPage's pane conductor,
/// and two owners writing one control's Visibility would let one of them hide the other's screen.</para>
///
/// <para><b>What differs is where its requests go</b>, because Pegasus has its own screens for them. A
/// record's own page is Pegasus's record screen, and so is a list's (see PegasusChrome.RecordList). A
/// station is tuned and said so along the foot. Add music goes back to the library, where the import
/// runs. The two menu entries that open a record's page are turned to the record screen the same
/// way.</para>
///
/// <para><b>How it is reached.</b> Pegasus opens on it when nothing is on the deck, and the wall's back
/// key goes to it from the wall. Its own back key, in the same corner, goes to the record turning, and so
/// does a press on the mini player's cover or words. It has no heading of its own.</para>
///
/// <para><b>A press on home that starts music stays on home.</b> The mini player at the top of it shows
/// what came on, and a press on its cover or words is the way to the record turning. A record's or a
/// list's own screen, opened from home, does go to the record when it plays, as a record's does from
/// the wall.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>Which screen the collection is showing, or last showed. Written only by
    /// <see cref="ShowLibrary"/> and <see cref="CrossToHome"/>.</summary>
    private CollectionScreen _collection = CollectionScreen.Wall;

    /// <summary>The home screen, built the first time the collection opens on it.</summary>
    private HomeView? _home;

    private PegasusHomeHooks? _homeHooks;

    /// <summary>
    /// The hooks the page hands over. They arrive when MainPage wires its album views, which is usually
    /// long before Pegasus is first opened, but nothing depends on that: home is wired by whichever of the
    /// two lands second, the hooks or the home (see <see cref="WireHome"/>).
    /// </summary>
    public PegasusHomeHooks? HomeHooks
    {
        get => _homeHooks;
        set
        {
            _homeHooks = value;
            WireHome();
        }
    }

    /// <summary>Whether the wall is the screen up. What every wall-only path asks: with home up, the
    /// collection is up and the wall is not.</summary>
    private bool WallUp => LibraryUp && _collection == CollectionScreen.Wall;

    /// <summary>Whether home is the screen in front: the collection is up on home and no record screen is
    /// over it.</summary>
    private bool HomeInFront => LibraryUp && _collection == CollectionScreen.Home && !RecordUp;

    /// <summary>Whether the screen the collection is on is ready to be met: the wall read, or home up with
    /// its pictures above the fold in (see HomeView.IsReady). What the view's opening cover waits on (see
    /// PegasusPlayer.Show).</summary>
    public bool CollectionIsReady => _collection switch
    {
        CollectionScreen.Home => _home?.IsReady == true,
        CollectionScreen.Radio => RadioIsReady,
        CollectionScreen.Artists or CollectionScreen.Genres or CollectionScreen.Artist or CollectionScreen.Genre
            => _shelf is not null,
        CollectionScreen.Playlists => PlaylistsAreRead,
        CollectionScreen.Search => true,   // a field, with nothing to wait for
        _ => LibraryIsFilled,
    };

    /// <summary>Builds home and puts it in its host, once.</summary>
    private void EnsureHome()
    {
        if (_home is not null)
            return;

        // No heading: home starts just under the corner keys, with the mini player over it (see
        // HomeTopPad).
        //
        // Its text is scaled by the figure the mini player is sized by, so both read at one size.
        // SizeLibrary sets it again on every resize.
        var home = new HomeView
        {
            Visibility = Visibility.Collapsed,
            HeadingShown = false,
            TextScale = QueueScale,
            ContentTop = HomeTopPad,

            // OK on a record in the rows opens its page, as a press on the wall does (the mouse still
            // opens the drawer under the row), and the banner's card in front is its one stop, stepped
            // with Left and Right.
            DrivenByRemote = true,

            // With the remote, OK on a record opens its page, so the key to the page on a sleeve would be
            // one stop too many.
            AlbumPageKeyShown = false,

            // A held OK on a quick song opens the song's menu, the one a record's tracks open, and on a
            // record in the rows the record's menu away from its page, as on the TV (see
            // HomeView.Quick_Loaded and HomeView.Card_Loaded). The Menu key stays the side menu's on home.
            QuickSongMenu = TrackMenu,
            RecordMenu = RecordMenuOf,
        };
        home.AlbumRequested += Home_AlbumRequested;
        home.PlaylistRequested += Home_PlaylistRequested;
        home.StoredPlaylistRequested += Home_StoredPlaylistRequested;
        home.StationRequested += Home_StationRequested;
        home.AddMusicRequested += Home_AddMusicRequested;
        home.Scrolled += Collection_Scrolled;   // the mini player going on a scroll down (see PlayerAway)

        // The view's opening cover waits on LibraryFilled for the wall, and home being ready is the same
        // answer for the same wait: the screen up with the banner's and the lists' pictures in, which is
        // what the main window's splash comes off on. Only while home is the screen the collection is on:
        // a reading that lands after the listener has left has nobody waiting for it.
        home.Ready += () =>
        {
            if (LibraryUp && _collection == CollectionScreen.Home)
                LibraryFilled?.Invoke();
        };

        HomeHost.Children.Add(home);
        _home = home;
        WireHome();
    }

    /// <summary>
    /// Gives home its hooks, and turns the two menu entries that open a record's page to the record
    /// screen. Called when the hooks arrive and when home is built, and does the work only once both are
    /// there, so either order ends wired. Running it again wires from scratch, which is harmless.
    /// </summary>
    private void WireHome()
    {
        if (_home is not { } home || _homeHooks is not { } hooks)
            return;

        hooks.Wire(home);

        // Home's play doors (the drawer under a row, the quick selection, its menus) are the main
        // window's own, untouched: music started from home stays on home (see the class's notes).

        // "Album page" on a record's menu and "Show album" on a song's. In the main window both go to the
        // album page, which is behind Pegasus. Here they go to the record screen, which is what that page
        // is in Pegasus. Every other entry does what it does in the main window.
        if (home.MenuHooks is { Invoked: { } album } albumHooks)
            home.MenuHooks = albumHooks with
            {
                Invoked = (command, folder) =>
                {
                    if (command == AlbumMenuCommand.AlbumPage)
                    {
                        ShowRecordAsync(folder);
                        return;
                    }

                    album(command, folder);
                },
            };

        if (home.TrackMenuHooks is { Invoked: { } track } trackHooks)
            home.TrackMenuHooks = trackHooks with
            {
                Invoked = (command, song) =>
                {
                    if (command == TrackMenuCommand.ShowAlbum)
                    {
                        if (MainPage.LibraryFolderOf(song) is { } folder)
                            ShowRecordAsync(folder);
                        return;
                    }

                    track(command, song);
                },
            };

        LoadHomeIfDue();
    }

    /// <summary>
    /// Reads home's rows if home is on screen and what it shows is no longer what the shelf would give.
    ///
    /// <para>Not before it has its cover loader: a reading without one dresses every card with a made
    /// sleeve and reports it only on the ring (see HomeView.Load). Asked when home comes on screen and
    /// when the hooks arrive, so whichever happens last does the read.</para>
    /// </summary>
    private void LoadHomeIfDue()
    {
        if (_home is { Stale: true, CoverLoader: not null, Visibility: Visibility.Visible } home)
            home.Load();
    }

    /// <summary>
    /// Home's own Visibility, from the two facts it depends on: the collection layer is in the tree, and
    /// home is the screen it shows. Called wherever either changes (see SetLibraryLayerShown and
    /// <see cref="CrossToHome"/>), and reads home's rows if they are due when it comes up.
    /// </summary>
    private void ApplyHomeShown()
    {
        if (_home is not { } home)
            return;

        var up = LibraryLayer.Visibility == Visibility.Visible && _collection == CollectionScreen.Home;
        home.Visibility = up ? Visibility.Visible : Visibility.Collapsed;
        if (up)
            LoadHomeIfDue();
    }

    /// <summary>
    /// The wall's back key, and Back on the wall or on radio: the collection stays up and crosses to home,
    /// over the same length the record screen crosses with the wall. The mini player and the back key
    /// belong to the layer and stay where they are.
    /// </summary>
    private void CrossToHome()
    {
        if (!LibraryUp || RecordUp || _collection == CollectionScreen.Home)
            return;

        var from = _collection;
        _collection = CollectionScreen.Home;
        if (from == CollectionScreen.Wall)
            StopWheel();   // nothing may still be travelling under a wall that is on its way out
        EnsureHome();
        LayOutLibraryTop();
        PaintLibraryBack();

        HomeHost.Visibility = Visibility.Visible;
        _homeScreenFade.To(1, ScreenCrossMs, null);
        LeaveScreen(from);

        ApplyHomeShown();
        FocusScreenLater(null);   // with the remote in use: home's first card
    }

    /// <summary>
    /// The menu's Albums and Search: the collection stays up and crosses from home or radio to the wall,
    /// over the length <see cref="CrossToHome"/> takes the other way. The mini player and the key belong
    /// to the layer and stay where they are; the key turns back into the way back to home.
    /// </summary>
    /// <remarks>
    /// The wall is seated and read here if it never has been, as <see cref="ShowLibrary"/> does when the
    /// collection opens on it. Home is collapsed once its fade lands, through <see cref="ApplyHomeShown"/>,
    /// which is what stops its banner and its rooms; collapsed at once, it would vanish instead of fading.
    /// </remarks>
    private void CrossToWall()
    {
        if (!LibraryUp || RecordUp || _collection == CollectionScreen.Wall)
            return;

        var from = _collection;
        _collection = CollectionScreen.Wall;
        LayOutLibraryTop();
        PaintLibraryBack();

        WallScreen.Visibility = Visibility.Visible;
        _wallScreenFade.To(1, ScreenCrossMs, null);
        LeaveScreen(from);

        UpdateLibraryFade();
        SeatWall();
        CheckAnchorLater();
        if (_libraryRows.Count == 0)
            FillLibraryAsync();
        FocusScreenLater(null);   // with the remote in use: the wall's first record
    }

    /// <summary>
    /// Fades out the collection screen being crossed away from, and collapses it when the fade lands if it
    /// has not been crossed back to meanwhile. Collapsed rather than left at nothing, for the reason
    /// CrossToRecord gives: a screen at zero opacity is still laid out. Home and radio are collapsed
    /// through their own Apply methods, which are what stop their banners and rooms.
    /// </summary>
    private void LeaveScreen(CollectionScreen from)
    {
        switch (from)
        {
            case CollectionScreen.Wall:
                _wallScreenFade.To(0, ScreenCrossMs, () =>
                {
                    if (_collection != CollectionScreen.Wall)
                        WallScreen.Visibility = Visibility.Collapsed;
                });
                break;

            case CollectionScreen.Home:
                _homeScreenFade.To(0, ScreenCrossMs, () =>
                {
                    if (_collection != CollectionScreen.Home)
                    {
                        HomeHost.Visibility = Visibility.Collapsed;
                        ApplyHomeShown();
                    }
                });
                break;

            case CollectionScreen.Radio:
                CloseRadioOverlays();
                _radioScreenFade.To(0, ScreenCrossMs, () =>
                {
                    if (_collection != CollectionScreen.Radio)
                        ApplyRadioShown();
                });
                break;

            case CollectionScreen.Artists:
            case CollectionScreen.Genres:
            case CollectionScreen.Artist:
            case CollectionScreen.Genre:
            case CollectionScreen.Playlists:
            case CollectionScreen.Search:
                LeaveArtistsGenresScreen(from);
                break;
        }
    }

    /// <summary>What the key in the top left corner is: the way back over the wall and the artists and
    /// genres screens (to home, or one step along their trail), the menu key over home and radio (see
    /// ShowLibraryMenu).</summary>
    private void PaintLibraryBack()
    {
        var back = _collection == CollectionScreen.Wall || IsArtistsGenres(_collection);
        var page = _collection is CollectionScreen.Artist or CollectionScreen.Genre;
        LibraryBackButton.Content = back ? "\uf053" : "\uf078";   // chevron left; chevron down
        IconName.Set(LibraryBackButton, Strings.Loc.Get(
            page ? "Album_Back" : back ? "Viz_LibraryBackHome" : "Viz_Menu"));
        if (back)
            ShowLibraryMenu(false);
    }


    // ── what home asks for ──

    /// <summary>A record's own page: the key on a sleeve, a record on the banner, a record in the quick
    /// picks. Pegasus's record screen, whose back key comes back to home.</summary>
    private void Home_AlbumRequested(object? sender, string folder) => ShowRecordAsync(folder);

    /// <summary>A generated list, opened on the record screen as a record is (see
    /// PegasusChrome.RecordList). Read off the collection on screen, which is the one home read the list
    /// from.</summary>
    private void Home_PlaylistRequested(object? sender, SmartPlaylistId id)
    {
        if (Library is { IsOpen: true } library)
            ShowListRecordAsync(GeneratedListEntry(library.Shelf, id));
    }

    /// <summary>One of the collection's playlists, by its node in the tree. The tree is a query, so the
    /// node is read on the pool and the record screen opens on the list when it is back.</summary>
    private async void Home_StoredPlaylistRequested(object? sender, long nodeId)
    {
        if (Library is not { IsOpen: true } library)
            return;

        var shelf = library.Shelf;

        // Whose tree the node is read from, decided with the read: the shelf on screen is a borrowed one
        // exactly while another computer's collection is being viewed. The cover is looked up by node id
        // in that tree, and both trees number their lists from one (see PlaylistShelf).
        var whose = library.IsPeerView ? PlaylistShelf.Borrowed : PlaylistShelf.Own;

        var asked = await Task.Run(() => library.Read(shelf, "a playlist opened from home", mirror =>
        {
            var nodes = mirror.GetPlaylistNodes();
            var byId = nodes.ToDictionary(n => n.Id);
            return byId.TryGetValue(nodeId, out var node) && node.Kind == PlaylistNodeKind.Playlist
                ? StoredListEntry(mirror, node, byId, whose)
                : null;
        }));

        // Unanswered opens nothing: a refusal has put the problem screen up over the app, and a closed
        // mirror is a collection that moved under the read.
        if (!asked.Answers(out var entry, out _))
            return;

        // Pressed on home, so opened from home. If home has gone while the tree was read, the listener
        // has moved on and nothing is opened over whatever is there now.
        if (entry is not null && HomeInFront)
            ShowListRecordAsync(entry);
    }

    /// <summary>A station: tuned, and said so along the foot, as the main window does. A station puts
    /// nothing on the deck, so home stays up.</summary>
    private void Home_StationRequested(object? sender, RadioStation station)
    {
        App.Services.GetRequiredService<Services.Audio.RadioService>().Tune(station);
        Announced?.Invoke(Strings.Loc.Format("Radio_TunedTo", station.Display));
    }

    /// <summary>The locked panel's button. The page leaves Pegasus and opens Add music.</summary>
    private void Home_AddMusicRequested(object? sender, EventArgs e) => _homeHooks?.AddMusic();
}

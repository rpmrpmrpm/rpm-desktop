using RPM.Core.Import;
using RPM.Core.Cloud;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Presentation.Main.Home;
using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The album grid, with the loaders that feed the covers, their tracks, and playback. The grid shows the
/// same collection as the tree, as a wall of covers with an inline flip-down. The switch between the list
/// and the grid is in the shell's header. It changes the setting, and this class reacts to it.
/// </summary>
public sealed partial class MainPage
{
    private bool _albumGridHooked;

    /// <summary>Every home screen wired by <c>WireHome</c> in <see cref="AttachAlbumGrid"/>: this page's
    /// own, and the one Pegasus stands up over the room. A heart set anywhere is pushed to each of them,
    /// so a home added later gets the pushes by being wired, not by somebody remembering a second
    /// line.</summary>
    private readonly List<HomeView> _homes = [];

    /// <summary>
    /// The locked home panel's one button, pressed on Pegasus's home screen.
    ///
    /// <para>The app goes back to the library first. Add music is a dialog over this page, and the import
    /// that follows it runs in this page's panes. Opened over Pegasus, the listener would choose a source
    /// and then see nothing happen. The switch is queued by the view service, and the dialog is queued
    /// behind it. So the dialog opens over the library, not over Pegasus.</para>
    /// </summary>
    private void AddMusicFromPegasus()
    {
        App.Services.GetRequiredService<ViewModeService>().Switch(AppView.Library);
        DispatcherQueue.TryEnqueue(() => AddMusic_Click(this, new RoutedEventArgs()));
    }

    private void AttachAlbumGrid()
    {
        if (_albumGridHooked || Model is not { } model)
            return;
        _albumGridHooked = true;

        var settings = App.Services.GetRequiredService<SettingsService>();
        var playback = App.Services.GetRequiredService<PlaybackService>();

        AlbumWall.CoverLoader = LoadAlbumCoverAsync;
        // The wall's resident pack is named after the collection, not the wall. So this wall and the
        // visualizer's wall share one file when they draw the same records. The name is read through a
        // hook, because the shelf is swapped under the control. A borrowed collection gets its own pack.
        AlbumWall.CollectionRoot = () => App.Services.GetRequiredService<LibraryService>().ShelfRoot;
        AlbumWall.TracksLoader = LoadAlbumTracksAsync;
        AlbumWall.PlayTrack = PlayAlbumTrack;
        AlbumWall.PlayTracks = PlayAlbumTracks;
        AlbumWall.StartDj = StartDjAlbum;

        // The home screen's rows show the same records. They use the same cover loader as the wall, so a
        // borrowed shelf and every offloaded record get their real sleeve, not a made one. A record is a
        // record, whichever screen it is shown on.
        Home.CoverLoader = LoadAlbumCoverAsync;

        // The album list reads the same records from the same loaders. It is a second view of one
        // collection, not a second collection.
        AlbumList.CoverLoader = LoadAlbumCoverAsync;
        AlbumList.TracksLoader = LoadAlbumTracksAsync;
        AlbumList.PlayTrack = PlayAlbumTrack;
        AlbumList.PlayTracks = PlayAlbumTracks;
        AlbumList.StartDj = StartDjAlbum;

        // The right-click menu: the wall builds the menu, the page fills in the live per-album state and
        // does the work. The corner cloud badge follows the same state, refreshed when the cloud changes.
        var library = App.Services.GetRequiredService<LibraryService>();
        var cloud = App.Services.GetRequiredService<CloudSyncService>();

        // Asked only about a borrowed shelf, and only for the one thing a mark on it turns on: whether
        // the computer that lent it is answering right now.
        var sharing = App.Services.GetRequiredService<Services.Sharing.SharingService>();

        // A record's own answer to whether it is classical. This decides whether its drawer is a list of
        // works with their composers, or a plain tracklist. Both views ask, because the answer belongs to
        // the record, not to the view it is opened in. Only an answer that somebody gave counts. An album
        // that nobody has marked is a plain tracklist.
        bool ClassicalOf(string folder) => library.IsOpen && library.Shelf.GetAlbumMeta(folder).Classical == true;

        AlbumWall.ClassicalLoader = ClassicalOf;
        AlbumList.ClassicalLoader = ClassicalOf;

        // Removal is the one cloud state that the mirror cannot tell us about, because the mirror writes
        // nothing while it runs. So the service is asked first. The rollup answers for everything else.
        //
        // The engine is also asked whether anything is going up at all. When the store is full, a queued
        // track is not on its way. It is waiting for room that may not come for days. So the record shows
        // the resting outline cloud, not a pulsing one. See AlbumCloudRollup.BadgeWhen.
        //
        // A borrowed shelf is read differently, because the question is different there. Nothing on it is
        // on this disk, so "using none of your disk" would describe every record and single out none. The
        // mark shows how the music arrives instead (see BadgeOnBorrowedShelf). That depends on whether the
        // computer lending the shelf is answering, which only the sharing service knows. Neither of the
        // cloud engine's two states can happen there. Somebody else's record cannot be uploaded or withdrawn
        // from here.
        //
        // Many records or one, the reading is the same, so there is one way to read it. A view that wants
        // every mark on screen asks for every folder at once. That is one query and one take of the reading
        // gate. Asking record by record would take the one lock that every read in the app uses many times
        // over, and the wall's own query would wait behind them. See LibraryDatabase.GetAlbumCloudRollups.
        IReadOnlyDictionary<string, RPM.Core.Library.AlbumCloudBadge> CloudBadgesOf(
            IReadOnlyList<string> folders)
        {
            var marks = new Dictionary<string, RPM.Core.Library.AlbumCloudBadge>(
                StringComparer.OrdinalIgnoreCase);
            if (!library.IsOpen || folders.Count == 0)
                return marks;

            var lender = library.ViewingPeer;
            var reachable = lender is not null && sharing.IsReachable(lender);
            var rollups = library.Shelf.GetAlbumCloudRollups(folders);

            foreach (var folder in folders)
            {
                var roll = rollups.TryGetValue(folder, out var found)
                    ? found
                    : RPM.Core.Library.AlbumCloudRollup.Empty;

                marks[folder] =
                    lender is not null ? roll.BadgeOnBorrowedShelf(reachable)
                    : cloud.IsAlbumLeaving(folder) ? RPM.Core.Library.AlbumCloudBadge.Leaving
                    : roll.BadgeWhen(cloud.UploadsStopped);
            }

            return marks;
        }

        RPM.Core.Library.AlbumCloudBadge CloudBadgeOf(string folder) =>
            CloudBadgesOf([folder]).TryGetValue(folder, out var badge)
                ? badge
                : RPM.Core.Library.AlbumCloudBadge.None;

        AlbumWall.CloudBadgeLoader = CloudBadgeOf;
        AlbumWall.CloudBadgesLoader = CloudBadgesOf;
        // The list wears the same mark on the same record: it is the other view of one shelf, and the
        // cloud must not be a thing you can only see in one of them.
        AlbumList.CloudBadgeLoader = CloudBadgeOf;
        AlbumList.CloudBadgesLoader = CloudBadgesOf;

        // Both controls are told whether the engine is running. A queued TRACK line needs that answer.
        // It is read through a hook, not passed as a value, because the marks are applied long after
        // this line runs, and the answer changes.
        AlbumWall.CloudUploadsStopped = () => cloud.UploadsStopped;
        AlbumList.CloudUploadsStopped = () => cloud.UploadsStopped;

        // The shelf they are drawing decides what the marks inside a borrowed record mean. It is read
        // through a hook for the same reason: the collection on screen can be swapped, and the computer
        // lending it can go off the network, long after this line runs.
        RPM.Core.Cloud.ShelfReading ShelfOf() =>
            library.ViewingPeer is not { } lender ? RPM.Core.Cloud.ShelfReading.Own
            : sharing.IsReachable(lender) ? RPM.Core.Cloud.ShelfReading.BorrowedFromOnline
            : RPM.Core.Cloud.ShelfReading.BorrowedFromOffline;

        AlbumWall.ShelfReading = ShelfOf;
        AlbumList.ShelfReading = ShelfOf;
        AlbumWall.MenuStateProvider = folder => BuildAlbumMenuState(library, settings, folder);
        AlbumWall.MenuInvoked = (command, folder) => _ = RunAlbumMenuAsync(command, folder);

        // The "Add to playlist" submenu lists the saved playlists (built each open) and can add the
        // album's tracks to one, or make a new playlist from them.
        AlbumWall.PlaylistEntriesProvider = () =>
        {
            EnsurePlaylistPanel();
            return _playlistModel?.PlaylistPickerEntries() ?? Array.Empty<(long, string, int)>();
        };
        AlbumWall.AddFolderToPlaylist = (folder, id) => _ = AddFolderTracksToPlaylistAsync(folder, id);
        AlbumWall.NewPlaylistFromFolder = folder => _ = NewPlaylistFromFolderAsync(folder);

        // The album list shows the same menu on the same record, from the same hooks.
        AlbumList.MenuHooks = new AlbumMenuHooks(
            StateProvider: folder => BuildAlbumMenuState(library, settings, folder),
            Invoked: (command, folder) => _ = RunAlbumMenuAsync(command, folder),
            PlaylistEntries: () =>
            {
                EnsurePlaylistPanel();
                return _playlistModel?.PlaylistPickerEntries() ?? Array.Empty<(long, string, int)>();
            },
            AddFolderToPlaylist: (folder, id) => _ = AddFolderTracksToPlaylistAsync(folder, id),
            NewPlaylistFromFolder: folder => _ = NewPlaylistFromFolderAsync(folder));

        // An artist's page holds the same records. It uses the same hooks object. A record right-clicked
        // under an artist and the same record right-clicked on the wall are one record. Two sets of answers
        // about it would make the two disagree.
        ArtistDetail.MenuHooks = AlbumList.MenuHooks;

        // The record's own page, where the menu hangs off the sleeve, uses the same object for the same
        // reason. It is the same record being asked about.
        AlbumDetail.MenuHooks = AlbumList.MenuHooks;

        // The artist named under the sleeve opens their own page, if the collection has one for them. Both
        // questions are asked of the wall: whether to draw the door, and where it goes. So a name that is a
        // door cannot turn out not to be one between the drawing and the press.
        AlbumDetail.ArtistHooks = new ArtistPageHooks(
            Exists: credit => ArtistPageFor(credit) is not null,
            Open: credit =>
            {
                if (ArtistPageFor(credit) is { } artist)
                {
                    // The record the artist is being read out of, taken before the page goes. The page knows
                    // this, and closing it is what makes the page stop knowing.
                    var record = AlbumDetail.Folder is { Length: > 0 } open ? open : null;

                    // The artist's page opens first, and the record is taken down after it. This keeps the
                    // window dressed the whole way across. Opening the artist puts them on the backdrop as
                    // open and undressed (their peel lends nothing, see ArtistDetailView.Lend). That is the
                    // one state the window can hold its current ground through. The other order would flash
                    // the app's own wallpaper between two pages that both have one.
                    //
                    // This is still a move ACROSS the card, not a page stacked on top of one. The record
                    // does close, so the reader is never two backs from the wall they started on. Back from
                    // the artist puts the record up again (see ArtistDetail_BackRequested). So the two are
                    // one step apart in both directions.
                    //
                    // Whether the record was opened from the home screen is taken before the page is
                    // closed, for the same reason its folder is. Closing it drops the claim. The artist's
                    // page carries the claim until the record is put back.
                    var fromHome = _albumOpenedFrom == AlbumFrom.HomeScreen;

                    NavigateToArtistPage(artist, ArtistFrom.AlbumPage(record, fromHome));
                    ShowAlbumDetail(false);
                }
            });

        // The drawer a record opens into on that page is the wall's own control, fed from the wall's own
        // sources. There is one reading of a record for the whole app. The same sleeve is read at the same
        // size, with the same disc dividers and the same format line under the name.
        ArtistDetail.BandSources = new AlbumBandSources(
            Cover: LoadAlbumCoverAsync,
            // The page's own ground, not the wall's. The reading is shared, but WHEN the drawer crosses into
            // what it has read is the page's decision. It opens a record fully dressed, and crosses only when
            // it is pointed at a second one. See ArtistDetailView.CrossGround.
            Sleeve: ArtistDetail.CrossGround,
            Tracks: LoadAlbumTracksAsync,
            Classical: ClassicalOf,
            UploadsStopped: () => cloud.UploadsStopped,
            Shelf: ShelfOf,
            RasterScale: XamlRoot?.RasterizationScale ?? 1);
        ArtistDetail.PlayTracks = PlayAlbumTracks;
        ArtistDetail.StartDj = StartDjAlbum;
        ArtistDetail.SetTrackFavorite = SetTrackFavorite;
        ArtistDetail.LibraryName = () => Model?.LibraryFolderName ?? "";

        // This is pulled, not pushed. The page builds its listing a folder at a time. It opens records into
        // a drawer seconds after they are pressed. Either can happen between two songs. Asking what is
        // playing at the moment the line is built gives an answer that is true whenever it is asked. Being
        // told when the song changed misses every line built after that.
        ArtistDetail.PlayingPath = () => playback.PlayingPath;

        // The collection it reads decides what a cloud mark on one of its songs means. A borrowed collection
        // shows how the music arrives, not whose disk it is on. This is the same answer the album views
        // read, so a record shows one badge wherever it is opened.
        ArtistDetail.Shelf = ShelfOf;
        ArtistDetail.CloudBadgeLoader = CloudBadgeOf;

        // The track menu, for the songs listed inside a record in either view and on the album page. It
        // has its own hooks, on purpose. A menu on a song and a menu on a record are menus on different
        // things. The album's entries must not appear under a right-clicked track.
        var trackHooks = new TrackMenuHooks(
            Invoked: (command, track) => _ = RunTrackMenuAsync(command, track),
            PlaylistEntries: () =>
            {
                EnsurePlaylistPanel();
                return _playlistModel?.PlaylistPickerEntries() ?? Array.Empty<(long, string, int)>();
            },
            AddTrackToPlaylist: (track, id) => _ = AddTrackToPlaylistAsync(track, id),
            NewPlaylistFromTrack: track => _ = NewPlaylistFromTrackAsync(track),
            StateProvider: BuildTrackMenuState);
        AlbumWall.TrackMenuInvoked = trackHooks.Invoked;
        AlbumWall.AddTrackToPlaylist = trackHooks.AddTrackToPlaylist;
        AlbumWall.NewPlaylistFromTrack = trackHooks.NewPlaylistFromTrack;
        AlbumWall.TrackMenuStateProvider = trackHooks.StateProvider;
        AlbumList.TrackMenuHooks = trackHooks;
        AlbumDetail.TrackMenuHooks = trackHooks;
        ArtistDetail.TrackMenuHooks = trackHooks;

        // Everything a home screen reads, plays and answers a menu with is set in one place. Pegasus has a
        // home screen of its own (see PegasusChrome.Home), and it is given this same method. So the two are
        // wired by one list of assignments, and a hook added here reaches both.
        //
        // CoverLoader is set again here, although the reading path above set it first. The two are the same
        // method group. This list is where a reader checks what a home is given.
        void WireHome(HomeView home)
        {
            home.CoverLoader = LoadAlbumCoverAsync;

            // The rows open the same drawer under the row a record was pressed in. The same control is fed
            // from the same loaders. There is one reading of a record for the whole app.
            home.BandSources = new AlbumBandSources(
                Cover: LoadAlbumCoverAsync,
                // The screen's own ground, not the wall's, for the same reason as the artist page. The
                // reading is shared, but WHEN the drawer crosses into what it read belongs to the surface.
                // See HomeView.CrossGround.
                Sleeve: home.CrossGround,
                Tracks: LoadAlbumTracksAsync,
                Classical: ClassicalOf,
                UploadsStopped: () => cloud.UploadsStopped,
                Shelf: ShelfOf,
                RasterScale: XamlRoot?.RasterizationScale ?? 1);
            home.PlayTrack = PlayAlbumTrack;
            home.PlayTracks = PlayAlbumTracks;
            home.StartDj = StartDjAlbum;
            home.SetTrackFavorite = SetTrackFavorite;
            home.MenuHooks = AlbumList.MenuHooks;
            home.TrackMenuHooks = trackHooks;
            home.LibraryName = () => Model?.LibraryFolderName ?? "";
            home.PlayingPath = () => playback.PlayingPath;
            home.CloudBadgeLoader = CloudBadgeOf;

            // It is also told about hearts set anywhere, like every other view on this page.
            if (!_homes.Contains(home))
                _homes.Add(home);
        }

        WireHome(Home);

        // The collection over the visualizer. Its record screen lists songs the same way, and it must answer
        // a right-click the same way. It uses the same hooks object, on purpose. A song right-clicked full
        // screen and the same song right-clicked in this window are one song. Two sets of answers would make
        // them disagree. Its home screen is wired by WireHome above.
        if (ShellPage.Current?.Pegasus is { } pegasus)
        {
            pegasus.TrackMenuHooks = trackHooks;
            pegasus.HomeHooks = new PegasusHomeHooks(Wire: WireHome, AddMusic: AddMusicFromPegasus);
        }

        // The heart on a song's line, in both album views. It writes through the same path as the menu
        // entry that does the same thing (see MainPage.TrackMenu). So the two cannot disagree about what
        // was stored or which lines were told.
        AlbumWall.SetTrackFavorite = SetTrackFavorite;
        AlbumList.SetTrackFavorite = SetTrackFavorite;

        // A mark made anywhere else lands here too. The full-screen player is shown over this page, not
        // instead of it. A song loved from the key under the words is loved on the shelf behind that
        // screen. The heart on its line must be filled by the time the listener comes back to it.
        var libraryForHearts = App.Services.GetRequiredService<LibraryService>();
        libraryForHearts.TrackFavoriteChanged += OnTrackFavoriteForGrid;
        libraryForHearts.AlbumFavoriteChanged += OnAlbumFavoriteForGrid;
        Unloaded += (_, _) =>
        {
            libraryForHearts.TrackFavoriteChanged -= OnTrackFavoriteForGrid;
            libraryForHearts.AlbumFavoriteChanged -= OnAlbumFavoriteForGrid;
        };
        void OnTrackFavoriteForGrid(string path, bool favorite) =>
            DispatcherQueue.TryEnqueue(() => ApplyTrackFavoriteMark(path, favorite));
        // A whole record marked from somewhere else (the player's album screen) moves the sticker on the
        // shelf. It also moves the record itself on or off the shelf, while the shelf is narrowed to
        // favourites.
        void OnAlbumFavoriteForGrid(string folder, bool favorite) =>
            DispatcherQueue.TryEnqueue(() => ApplyAlbumFavorite(folder, favorite));

        SetFavoritesToggle();

        cloud.Changed += OnCloudChangedForGrid;
        Unloaded += (_, _) => cloud.Changed -= OnCloudChangedForGrid;
        void OnCloudChangedForGrid() => DispatcherQueue.TryEnqueue(RefreshCloudMarks);

        // A record favourited at another of the user's desks. The shelf is fed again rather than re-badged.
        // The stickers move. And while the heart in the header is on, which records are on the shelf at all
        // changes too.
        cloud.AlbumMarksChanged += OnAlbumMarksForGrid;
        Unloaded += (_, _) => cloud.AlbumMarksChanged -= OnAlbumMarksForGrid;
        void OnAlbumMarksForGrid() => DispatcherQueue.TryEnqueue(() =>
        {
            if (_view is LibraryViewMode.Wall or LibraryViewMode.List)
                FeedAlbumViews(_view.Value);
        });

        // A sleeve or a disc label chosen for this record at another desk has just been downloaded. One
        // card re-reads its picture in place. The wall itself is not rebuilt, and nothing else on it moves.
        cloud.AlbumArtChanged += OnCloudArtForGrid;
        Unloaded += (_, _) => cloud.AlbumArtChanged -= OnCloudArtForGrid;
        void OnCloudArtForGrid(string folder) => DispatcherQueue.TryEnqueue(() =>
        {
            AlbumWall.RefreshAlbum(folder);
            AlbumList.RefreshAlbum(folder);
        });

        // The cover-size slider sits in the panel's shared header, which the tracklist and the wall share.
        // It is given to the wall, which still owns the value and the relayout.
        AlbumWall.AttachSizeSlider(SizeSlider);

        // Cover size is a remembered panel choice: seed the header slider from the setting, and write it
        // back (debounced by the control) when the user drags it.
        AlbumWall.SetTileSize(settings.AlbumTileSize);
        AlbumWall.TileSizeChanged += size => settings.AlbumTileSize = size;

        // The order is remembered too. It is seeded from the setting before the first feed, so the wall is
        // never shown in one order and then reshuffled into the remembered one.
        AlbumWall.Ordering = settings.AlbumOrdering;

        // The album list's grouping is remembered in the same way. It is set before the first feed. Set
        // after it, the grouping would regroup a list that is already on screen.
        AlbumList.Grouping = settings.AlbumGrouping;
        PaintGroupButton();

        // Slides the header chrome clear of the spinning disc. The disc overlaps the panel's top-right
        // corner while a track is playing. _recordReach is the disc's leftmost window X at a given window Y
        // (positive infinity when the disc is off the deck). This is the PAGE's job, not the wall's. The
        // switch and the slider form one set, and they must dodge together. They are on screen in both
        // views, and the wall is only shown sometimes.
        var stage = Art;   // the ArtDisplay stage owns the record geometry (_art is the controller)
        _recordReach = y =>
        {
            if (stage.IsIdle)
                return double.PositiveInfinity;
            var w = stage.ActualWidth;
            var bulge = stage.RecordBulgeAt(y);
            return w <= 0 || bulge <= 0 ? double.PositiveInfinity : w - bulge;
        };
        stage.GeometryChanged += OnArtGeometryForGrid;
        stage.IdleChanged += OnArtIdleForGrid;
        Unloaded += (_, _) =>
        {
            stage.GeometryChanged -= OnArtGeometryForGrid;
            stage.IdleChanged -= OnArtIdleForGrid;
        };

        // The library rail dodges the same disc, and cannot do it sideways, see MainPage.RightPane.
        WatchRailForRecord();
        // The panel resizing moves the chrome in the window, so re-check the overlap then too.
        LibraryChrome.SizeChanged += (_, _) =>
        {
            SettleSizeSlot();
            UpdateChromeAvoidance(animate: false);
        };
        // And the heading around it. The chrome is Auto-sized, so a window resize changes the star column
        // beside it, and never the chrome itself. The chrome's own SizeChanged would not fire at all.
        LibraryHeading.SizeChanged += (_, _) =>
        {
            // The panel's width decides which controls the header can hold (ApplyHeaderDensity). It is
            // asked first, because the settle below places whatever is left.
            ApplyHeaderDensity();
            SettleSizeSlot();
            UpdateChromeAvoidance(animate: false);
        };

        void OnArtGeometryForGrid() => UpdateChromeAvoidance();
        void OnArtIdleForGrid(bool _) => UpdateChromeAvoidance();

        // Keeps the wall current while it is the shown view. While it is hidden, the rebuild is skipped.
        // A switch to the grid feeds it on the way in.
        model.LibraryAlbumsChanged += OnAlbumsChangedForGrid;
        Unloaded += (_, _) => model.LibraryAlbumsChanged -= OnAlbumsChangedForGrid;

        // The playing line in an open band tracks the shared player, same source the art display reads.
        playback.Changed += OnPlaybackChangedForGrid;
        Unloaded += (_, _) => playback.Changed -= OnPlaybackChangedForGrid;

        // The switch is in the shell header. It changes this setting, and the page swaps its view to match.
        settings.Changed += OnSettingsChangedForGrid;
        Unloaded += (_, _) => settings.Changed -= OnSettingsChangedForGrid;

        ApplyViewMode(settings.LibraryView);

        void OnAlbumsChangedForGrid() => DispatcherQueue.TryEnqueue(() =>
        {
            // Only the view on screen is re-fed; the other one is fed on its way in.
            if (_view is LibraryViewMode.Wall or LibraryViewMode.List)
                FeedAlbumViews(_view.Value);

            // The subtitle states what is being SHOWN, so it follows the search and the favourites
            // filter as well as the library: it is re-read on the same signal the shelf itself is
            // rebuilt on, whichever view is up.
            UpdateLibrarySubheading();
        });

        UpdateLibrarySubheading();

        void OnPlaybackChangedForGrid() => DispatcherQueue.TryEnqueue(() =>
        {
            AlbumWall.SetPlaying(playback.PlayingPath);
            AlbumList.SetPlaying(playback.PlayingPath);

            // The open record too. It is the third view of the same tracklist, and the listener looks at it
            // while pressing play on a song in it.
            AlbumDetail.ApplyPlaying();

            // An artist's page lists their songs, and it can have one of their records open under it. It
            // shows the same bars on the same song, in whichever of the four views it is read.
            ArtistDetail.SetPlaying(playback.PlayingPath);

            // An open playlist is the fifth view. The element is always in the tree. Only its model is built
            // on demand. With no list open it holds no rows, so this is safe to call before anybody has
            // visited the playlists pane. It needs no path, because its rows read the player themselves.
            // See PlaylistView.SetPlaying.
            PlaylistPanel.SetPlaying();
        });

        void OnSettingsChangedForGrid() =>
            DispatcherQueue.TryEnqueue(() => ApplyViewMode(settings.LibraryView));
    }

    /// <summary>
    /// The order the wall is in: artist, album, when it was added, year, or how much it has been played.
    /// Each one is a question about the library, not about the disk. Every answer comes from the mirror.
    ///
    /// <para>This is a menu, not a row of chips, as on the phone. It is a choice made rarely. Five chips
    /// across the top of a wall of album art would stay in the way for good. The rules are
    /// <see cref="AlbumSort"/>'s, shared with the phone, so the two walls cannot disagree.</para>
    /// </summary>
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        // Each choice has a glyph, and the one in force shows a tick. The icons make the list readable at
        // a glance. Five words in a column all look alike.
        Add(AlbumOrdering.Artist, "AlbumSort_Artist", "\uf007");   // a person
        Add(AlbumOrdering.Album, "AlbumSort_Album", "\uf51f");     // the record itself
        Add(AlbumOrdering.Added, "AlbumSort_Added", "\uf271");     // a date, with something new on it
        Add(AlbumOrdering.Year, "AlbumSort_Year", "\uf073");       // a calendar
        Add(AlbumOrdering.Plays, "AlbumSort_Plays", "\uf025");     // headphones

        menu.ShowAt(SortButton);

        void Add(AlbumOrdering ordering, string key, string glyph)
        {
            var chosen = ordering == AlbumWall.Ordering;
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(key),
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
                // The one in force shows a tick instead of its own glyph. A checkable item would make the
                // whole menu reserve a check gutter to the left of every icon.
                Icon = new FontIcon
                {
                    Style = (Style)Application.Current.Resources["FaMenuIcon"],
                    Glyph = chosen ? "\uf00c" : glyph,
                },
            };
            item.Click += (_, _) =>
            {
                AlbumWall.Ordering = ordering;
                settings.AlbumOrdering = ordering;   // a remembered panel choice, like the cover size
            };
            menu.Items.Add(item);
        }
    }

    // The view the page is in. It is kept here, not read off Visibility, because the views cross-fade.
    // Their visibility lags the decision by the length of the fade.
    private LibraryViewMode? _view;

    /// <summary>True while the wall is up. The cover-size slider and the sort picker belong to the wall
    /// alone. The record avoidance measures its box either way.</summary>
    private bool ViewIsWall => _view == LibraryViewMode.Wall;

    /// <summary>The header's switch. It is the same setting the page reacts to, so all three buttons and
    /// any other writer use one path.</summary>
    private void ViewWall_Click(object sender, RoutedEventArgs e) => SwitchView(LibraryViewMode.Wall);

    private void ViewAlbums_Click(object sender, RoutedEventArgs e) => SwitchView(LibraryViewMode.List);

    private void ViewTable_Click(object sender, RoutedEventArgs e) => SwitchView(LibraryViewMode.Table);

    /// <summary>Reaching for any of the three closes an open album first. They share one slot. The switch
    /// means "show me the collection", whichever record is up.</summary>
    private void SwitchView(LibraryViewMode mode)
    {
        // The user has used the switch, so the hint about it has nothing left to say. This is set at the
        // one door a person can come through. It is not set in ApplyViewMode, which also runs for a view
        // restored at startup and for a view that another window changed. Neither of those is someone
        // learning anything.
        Coachmarks.CoachmarkState.Seen(RPM.Core.Library.CoachMark.Views);

        RPM.Desktop.Diagnostics.SwitchDiag.Begin($"to {mode}");
        ShowAlbumDetail(false);
        App.Services.GetRequiredService<SettingsService>().LibraryView = mode;
    }

    /// <summary>Swaps the three views, feeding the one coming in. They cross-fade and the header chrome
    /// slides: leaving the wall, the cover-size slider and the sort picker leave to the right and the
    /// switch takes their place; back to the wall, they come in behind it.</summary>
    private void ApplyViewMode(LibraryViewMode mode)
    {
        if (_view == mode)
            return;   // settings.Changed fires for every setting; only act on a real view flip
        bool first = _view is null;
        var wasWall = ViewIsWall;
        _view = mode;

        // A view flip from anywhere else (the setting written by another window, or a keyboard shortcut)
        // also means the collection, not the record that happens to be open over it.
        ShowAlbumDetail(false);

        SetViewToggle(mode);

        // The switch itself is not drawn on every view (see WantsViewToggle), so the hint about it comes
        // and goes with the view. ApplyViews does the same for the pane's own views.
        Shell.ShellPage.Current?.RefreshCoachmarks();

        CrossFadeViews(mode, animate: !first);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("crossfade");
        // Grouping belongs to the album list alone. The wall is a wall of covers in one order, and the table
        // groups by nothing. The favourites heart belongs to both album views, and to neither the table nor
        // an open record. Which of the two the header has ROOM for is decided by the density ladder. So both
        // are set from one place, from the view and the rung together.
        RefreshHeaderChromeVisibility(mode);
        // The view decides what the header wants at all, so the rung is re-walked whichever way it flips.
        ApplyHeaderDensity(force: true);
        if (first || wasWall != ViewIsWall)
            SlideSizeSlot(ViewIsWall, animate: !first);
        RefreshLibraryEmptyHint();   // the wall and the album list carry their own empty state
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("header");

        // Both album views are fed on the way in. The table asks the database itself. The art strip
        // belongs to playback alone, and ignores view flips entirely.
        if (mode is LibraryViewMode.Wall or LibraryViewMode.List)
            FeedAlbumViews(mode);

        // Re-place the chrome for the current disc state at once, with no glide on a view flip. Later moves
        // animate off the art's idle and geometry changes.
        DispatcherQueue.TryEnqueue(() => UpdateChromeAvoidance(animate: false));

        // A view shown for the first time is the only moment its stored scroll position means anything.
        // Until then it is collapsed, and nothing is laid out to scroll. From then on it reports its own
        // scrolling, so the next run opens where this one was reading.
        RestoreScrollFor(mode);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("applied");
    }

    // ── record avoidance: keep the header chrome clear of the spinning disc ──
    // The switch and the cover-size slider are one set. The set's box is the same in both views, because
    // the slider's column stays measured while it is away. So the dodge is measured from the group, and
    // both members move together.

    /// <summary>The record's leftmost reach in WINDOW-X at a given window-Y, or +∞ when the disc is off the
    /// deck and laps over nothing.</summary>
    private Func<double, double>? _recordReach;

    /// <summary>How far left the chrome is currently held, in px.</summary>
    private double _recordReserve;

    private void UpdateChromeAvoidance(bool animate = true)
    {
        if (_recordReach is not { } reach || XamlRoot?.Content is not { } root || LibraryChrome.ActualWidth <= 0)
            return;

        // The group's resting right edge and its TOP, in the window's coordinates (the space the record reach
        // is measured in). TransformToVisual already includes the current shift, so the shift is taken back
        // out, to get the RESTING edge. Otherwise the avoidance would keep correcting for its own shift.
        var transform = LibraryChrome.TransformToVisual(root);
        var p = transform.TransformPoint(new Windows.Foundation.Point(LibraryChrome.ActualWidth, 0));
        var restRight = p.X - LibraryChromeShift.X;
        var height = LibraryChrome.ActualHeight;

        // Clear space kept between the chrome and the disc. It is generous, because the reach reports the sharp
        // arc, and the disc also casts a soft shadow past that edge. 12 px would leave the chrome sitting in
        // it. These are BUTTONS, so they must be clickable, not only visible. The disc takes clicks wherever
        // it overlaps, so it is kept a full thumb's width away.
        const double gap = 56;

        // The disc's arc runs across the chrome's whole height. Down here, off the low shoulder of the
        // circle, it swings hard over a few px of Y. So take the WORST (leftmost) reach over the band the
        // chrome occupies, not the reach at one line through it.
        var recordLeft = double.PositiveInfinity;
        for (var i = 0; i <= 4; i++)
            recordLeft = Math.Min(recordLeft, reach(p.Y + height * i / 4));

        var reserve = double.IsInfinity(recordLeft) ? 0 : Math.Max(0, restRight + gap - recordLeft);

        if (Math.Abs(reserve - _recordReserve) < 0.5)
            return;
        _recordReserve = reserve;

        if (!animate)
        {
            LibraryChromeShift.X = -reserve;
            return;
        }
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = -reserve,
            Duration = new Duration(TimeSpan.FromSeconds(ArtDisplay.TransitionSeconds)),
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, LibraryChromeShift);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    /// <summary>The chosen third of the switch wears the rail's azure wash; the others stay bare
    /// glass.</summary>
    private void SetViewToggle(LibraryViewMode mode)
    {
        SelectedSegment.Paint(ViewWallButton, mode == LibraryViewMode.Wall);
        SelectedSegment.Paint(ViewAlbumsButton, mode == LibraryViewMode.List);
        SelectedSegment.Paint(ViewTableButton, mode == LibraryViewMode.Table);
    }

    private const double ViewFadeMs = 180;

    /// <summary>Fades the incoming view in and the other two out (they collapse at the end, so a hidden
    /// view costs no layout).</summary>
    private void CrossFadeViews(LibraryViewMode mode, bool animate)
    {
        UIElement incoming = ViewFor(mode);

        incoming.Visibility = Visibility.Visible;
        if (mode == LibraryViewMode.Wall)
            AlbumWall.EnsureAnchored();   // First pass back from collapsed. See AlbumWallView.EnsureAnchored

        foreach (var other in new[] { LibraryTableView, (UIElement)AlbumList, AlbumWall })
        {
            if (ReferenceEquals(other, incoming))
                continue;

            if (!animate)
            {
                // Set, not assigned: a fade left in flight from a navigation a moment ago would go on
                // writing over a plain assignment. See FadeView.
                Dissolving(other).Set(0);
                other.Visibility = Visibility.Collapsed;
                continue;
            }

            var leaving = other;
            FadeView(leaving, 0, then: () =>
            {
                // Guard a fast double flip: only hide it if it is still not the view we are on.
                if (!ReferenceEquals(ViewFor(_view ?? mode), leaving))
                    leaving.Visibility = Visibility.Collapsed;
            });
        }

        if (animate)
            FadeView(incoming, 1);
        else
            Dissolving(incoming).Set(1);
    }

    private UIElement ViewFor(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Wall => AlbumWall,
        LibraryViewMode.List => AlbumList,
        _ => LibraryTableView,
    };

    /// <summary>The cover-size slider belongs only to the wall. On the way to the list it slides out to the
    /// right and fades. It carries the switch into the space it leaves, so the switch ends up against the
    /// header's right edge, where it is still there to switch back with. On the way back, it returns.
    ///
    /// The slot slides on a translate, and it is COLLAPSED at the end of that slide. It is not left
    /// measured. Its Auto column is width that the heading's own column would never get back. Without the
    /// collapse, "music library" would be clipped in the list view, to hold a slider that view does not have.
    /// Collapsing during the slide would jump the switch, because the layout shift lands on top of its
    /// translate. So the collapse waits for the slide to finish, and the shifts are zeroed in the same step.
    /// The switch is already where the layout puts it. The travel is cached while the slot can be measured,
    /// because a collapsed slot has no width to read.</summary>
    private double _sizeSlotTravel;

    /// <summary>
    /// The storyboards currently animating the chrome, the two translates and the slot's fade.
    ///
    /// <para>They are held so they can be STOPPED. A running animation, or one that has finished and is
    /// holding its value, outranks a local value. While one of these is alive, <c>ViewToggleShift.X = 0</c>
    /// is accepted by the property system and then ignored in favour of the animated value. Stopping the
    /// storyboard hands the property back. Without a stop, the held animation keeps winning, and the chrome
    /// stays translated past the header's right edge.</para>
    /// </summary>
    private readonly List<Microsoft.UI.Xaml.Media.Animation.Storyboard> _sizeSlotAnims = new();

    /// <summary>Gives the chrome's translates and the slot's opacity back to their local values.</summary>
    private void ReleaseSizeSlotAnimations()
    {
        foreach (var sb in _sizeSlotAnims)
        {
            try { sb.Stop(); }
            catch { /* Already finished or detached. The property is ours either way. */ }
        }
        _sizeSlotAnims.Clear();
    }

    /// <summary>When the chrome may be settled again. A slide is in flight until then, and the settle must
    /// not fight it.
    ///
    /// <para>This is a DEADLINE, not a flag. A flag cleared in an animation's completion handler stays set
    /// forever if that handler never runs. But the settle it guards is exactly what has to survive that
    /// case. A deadline clears itself as time passes, whatever the animation does.</para></summary>
    private long _settleBlockedUntilTick;

    /// <summary>
    /// Puts the chrome where the current view says it belongs, with no animation and no memory of how it
    /// got there. It is called on every layout of the header, which means every window resize.
    ///
    /// <para>The slide's resting state is reached in the animation's COMPLETION handler. If that completion
    /// does not fire, the switch stays translated a slot-width to the right, with the slot still holding its
    /// column. The chrome then ends up past the header's right edge and stays there. Nothing should depend on
    /// an animation landing to be correct. The slide is how the chrome moves, not what it is.</para>
    /// </summary>
    private void SettleSizeSlot()
    {
        if (_view is null || Environment.TickCount64 < _settleBlockedUntilTick)
            return;

        ReleaseSizeSlotAnimations();

        if (ViewIsWall)
        {
            ShowSizeSlot();
            SizeSlot.Opacity = 1;
        }
        else if (_sizeSlotTravel > 0)
        {
            // Before the slot has ever been measured its travel is unknown, and collapsing it now would
            // lose the one layout pass that can be read. SlideSizeSlot's own first-run path handles that.
            SizeSlot.Visibility = Visibility.Collapsed;
            LibraryChrome.ColumnSpacing = 0;
            SizeSlot.Opacity = 0;
        }
        else
        {
            return;
        }

        SizeSlotShift.X = 0;
        ViewToggleShift.X = 0;
    }

    private void SlideSizeSlot(bool grid, bool animate)
    {
        // The travel is the slot's own width plus the gap to the switch. That is how far the switch has to go
        // to land on the header's right edge. It is read while the slot is still measured. Before the first
        // layout it is zero, and the no-animate path below runs again once layout has been done.
        if (SizeSlot.Visibility == Visibility.Visible && SizeSlot.ActualWidth > 0)
            _sizeSlotTravel = SizeSlot.ActualWidth + 12;
        double travel = _sizeSlotTravel;

        SizeSlot.IsHitTestVisible = grid;   // Faded out, it must not take clicks meant for the wall

        if (!animate)
        {
            ReleaseSizeSlotAnimations();
            SizeSlot.Opacity = grid ? 1 : 0;
            if (grid)
            {
                ShowSizeSlot();
                SizeSlotShift.X = 0;
                ViewToggleShift.X = 0;
                return;
            }

            // Applied straight into the list, before the header has been measured. The slot stays measured
            // for one layout pass, so its travel can be read. It is collapsed on the next tick.
            if (travel <= 0)
            {
                ShowSizeSlot();
                SizeSlotShift.X = 0;
                ViewToggleShift.X = 0;
                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!ViewIsWall)
                        SlideSizeSlot(false, animate: false);
                });
                return;
            }

            CollapseSizeSlot();
            return;
        }

        if (grid)
        {
            // Coming back. The slot takes its column again, so the switch starts where the layout has just
            // put it, a slot-width to the right. It moves back in with the slot.
            ReleaseSizeSlotAnimations();
            ShowSizeSlot();
            SizeSlotShift.X = travel;
            ViewToggleShift.X = travel;
            BlockSettleForOneSlide();
            _sizeSlotAnims.Add(Slide(SizeSlotShift, 0));
            _sizeSlotAnims.Add(Slide(ViewToggleShift, 0));
            _sizeSlotAnims.Add(Fade(SizeSlot, 1));
            return;
        }

        ReleaseSizeSlotAnimations();
        BlockSettleForOneSlide();
        _sizeSlotAnims.Add(Slide(SizeSlotShift, travel));
        _sizeSlotAnims.Add(Slide(ViewToggleShift, travel, then: () =>
        {
            // Guard a fast double flip: only give the width away if we are still off the wall.
            if (!ViewIsWall)
                CollapseSizeSlot();
        }));
        _sizeSlotAnims.Add(Fade(SizeSlot, 0));
    }

    /// <summary>
    /// Holds the settle off for the length of one slide, and then settles. This happens whether or not the
    /// animation ever reports that it finished. The slide is how the chrome moves. Where it ends up must
    /// not depend on an event.
    /// </summary>
    private void BlockSettleForOneSlide()
    {
        const long grace = 40;   // a frame or two past the fade, so the settle lands after the motion
        _settleBlockedUntilTick = Environment.TickCount64 + (long)ViewFadeMs + grace;
        var deadline = _settleBlockedUntilTick;

        _ = SettleAfterSlideAsync(deadline);

        async Task SettleAfterSlideAsync(long at)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(ViewFadeMs + grace));
            // A newer slide started in the meantime owns the chrome now; it will settle itself.
            if (_settleBlockedUntilTick == at)
                SettleSizeSlot();
        }
    }

    /// <summary>Gives the slot its column back, with its gap. On a narrow panel the density ladder has
    /// already emptied the slot (no sort picker and no cover-size slider). An empty column with a 12 pt gap
    /// in front of it takes width that the heading needs.</summary>
    private void ShowSizeSlot()
    {
        var wanted = HeaderWallSlotWanted;
        SizeSlot.Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
        LibraryChrome.ColumnSpacing = wanted ? 12 : 0;
    }

    /// <summary>Hands the slot's column back to the heading. The slot is collapsed, and both translates are
    /// zeroed. The switch keeps the place the slide left it in, and the header's type gets the
    /// room.</summary>
    private void CollapseSizeSlot()
    {
        ReleaseSizeSlotAnimations();
        SizeSlot.Visibility = Visibility.Collapsed;
        // The gap to the slot goes with the slot. A Grid keeps its ColumnSpacing between the columns, whether
        // or not one of them still has anything in it. On the list, 12 points of that gap would sit between the
        // switch and the header's right edge.
        LibraryChrome.ColumnSpacing = 0;
        SizeSlotShift.X = 0;
        ViewToggleShift.X = 0;
        // The chrome is one slot narrower now, so the record's dodge is measured from a different right edge.
        UpdateChromeAvoidance(animate: false);
    }

    /// <summary>
    /// A view crossing to another view, faded off the render tick.
    /// </summary>
    /// <remarks>
    /// <b>Not a Storyboard.</b> A <c>DoubleAnimation</c> HOLDS the property it animated. When it lands, the
    /// value it arrived at outranks the base value. So a later <c>Opacity = 0</c> or <c>= 1</c> is accepted
    /// by the property system and then ignored. The view swaps ran once per navigation and never stopped the
    /// one before. So every record opened left another held animation on a view. The reader then saw the
    /// page they were leaving flash back up part-way through the fade. The fade also did not land where it
    /// was aimed.
    ///
    /// <para>This is the same fault as the note on <c>_sizeSlotAnims</c>. That note says a held animation
    /// outranks a local value, and that its storyboards must be kept so they can be STOPPED. It is also the
    /// fault that <see cref="Dissolve"/> was written to fix, after the genre page's header stayed on screen
    /// carrying the previous genre's photograph. <c>Dissolve</c> writes the plain property. So a fade always
    /// starts from where the layer is, and always ends with the layer really there.</para>
    ///
    /// <para>One per element, kept, so a second navigation retargets the fade already in flight rather
    /// than racing it.</para>
    /// </remarks>
    private void FadeView(UIElement element, double to, Action? then = null)
        => Dissolving(element).To(to, TimeSpan.FromMilliseconds(ViewFadeMs), then);

    private Dissolve Dissolving(UIElement layer)
    {
        if (!_viewFades.TryGetValue(layer, out var tween))
            _viewFades[layer] = tween = new Dissolve(layer, "view-swap");
        return tween;
    }

    private readonly Dictionary<UIElement, Dissolve> _viewFades = [];

    /// <summary>The chrome's own fade. It stays a Storyboard, because it is coordinated with two translates
    /// and it IS stopped. See <see cref="_sizeSlotAnims"/>.</summary>
    private static Microsoft.UI.Xaml.Media.Animation.Storyboard Fade(
        UIElement element, double to, Action? then = null)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ViewFadeMs)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        if (then is not null)
            sb.Completed += (_, _) => then();
        sb.Begin();
        return sb;
    }

    private static Microsoft.UI.Xaml.Media.Animation.Storyboard Slide(
        Microsoft.UI.Xaml.Media.TranslateTransform transform, double toX, Action? then = null)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = toX,
            Duration = new Duration(TimeSpan.FromMilliseconds(ViewFadeMs)),
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, transform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        if (then is not null)
            sb.Completed += (_, _) => then();
        sb.Begin();
        return sb;
    }

    /// <summary>Hands the album views their records. Only the view being shown is fed. Both views hold a
    /// cover and a tracklist for each record. Filling the one nobody is looking at would cost a disk read
    /// per album, for nothing.</summary>
    private void FeedAlbumViews(LibraryViewMode mode)
    {
        if (Model is not { } model)
            return;

        var playing = App.Services.GetRequiredService<PlaybackService>().PlayingPath;
        var name = model.LibraryFolderName ?? "";
        var favorites = FavoriteAlbumFolders();
        var folders = _favoritesOnly
            ? model.LibraryAlbums.Where(favorites.Contains).ToList()
            : model.LibraryAlbums;
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("favourites");

        // The filter is applied here, so an empty shelf only means something here. With a search in the box,
        // nothing on the wall is an answer to the search. That is not an empty collection.
        var empty = !model.AlbumsRead ? ShelfEmpty.Reading
            : model.SearchText is { Length: > 0 } ? ShelfEmpty.Search
            : ShelfEmpty.Collection;

        if (mode == LibraryViewMode.Wall)
        {
            AlbumWall.SetAlbums(folders, model.LibraryAlbumFacts,
                                model.LibraryExplicitAlbums, favorites, name, empty);
            AlbumWall.SetPlaying(playing);
        }
        else
        {
            AlbumList.SetAlbums(folders, model.LibraryAlbumFacts, model.LibraryAlbumGenres,
                                model.LibraryAlbumTrackCounts, model.LibraryAlbumHeaderLines,
                                model.LibraryExplicitAlbums, favorites, name, empty);
            AlbumList.SetPlaying(playing);
        }
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("feed");

        UpdateLibrarySubheading();
    }

    // ── the heart in the header: only the records the listener has loved ──

    /// <summary>Whether both album views are narrowed to the favourites.
    ///
    /// <para>Remembered between sessions, with the rest of the screen (see <see cref="RestoreSession"/>).
    /// Coming back to the shelf you were reading is the point of restoring anything at all. A narrowed
    /// shelf is only alarming when nothing says why. Two things say why. The heart in the header is lit,
    /// and the subtitle counts what is actually on screen, not what the library holds.</para>
    /// </summary>
    private bool _favoritesOnly;

    /// <summary>
    /// Which records are favourites, read at the moment the shelf is fed rather than carried on the
    /// library skeleton.
    ///
    /// <para>One small query over <c>album_meta</c>, which holds a row only for records that somebody has
    /// said something about. Taken from the skeleton, the set would be as old as the last rebuild. A mark
    /// made from a record's own menu does not rebuild the library. So the sticker would appear on the next
    /// import, not on the click.</para>
    /// </summary>
    private static IReadOnlySet<string> FavoriteAlbumFolders()
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        return library.IsOpen
            ? library.Shelf.FavoriteAlbumFolders()
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A record was favourited or unfavourited. Its sticker is put on, or taken off, where it stands in
    /// both views. While the shelf is narrowed to the favourites, the shelf is fed again, because the record
    /// has just joined or left the shelf being shown.
    /// </summary>
    private void ApplyAlbumFavorite(string folder, bool favorite)
    {
        AlbumWall.ApplyAlbumFavorite(folder, favorite);
        AlbumList.ApplyAlbumFavorite(folder, favorite);
        // The record opened on a home screen shows the same heart under its sleeve. Every home this page
        // has wired gets the change: its own, and Pegasus's.
        foreach (var home in _homes)
            home.ApplyAlbumFavorite(folder, favorite);

        if (_favoritesOnly && _view is LibraryViewMode.Wall or LibraryViewMode.List)
            FeedAlbumViews(_view.Value);
    }

    private void FavoritesOnly_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = !_favoritesOnly;
        RememberFavoritesOnly();
        SetFavoritesToggle();
        if (_view is LibraryViewMode.Wall or LibraryViewMode.List)
            FeedAlbumViews(_view.Value);
    }

    /// <summary>The heart is filled and lit while the filter is on, and an outline while it is off. This is
    /// the same pair that the sticker and a song's own line use.</summary>
    private void SetFavoritesToggle()
    {
        FavoritesIcon.FontFamily = (FontFamily)Resources[_favoritesOnly ? "Fa" : "FaRegular"];
        FavoritesIcon.Foreground = (Brush)Application.Current.Resources[
            _favoritesOnly ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
        SelectedSegment.Paint(FavoritesButton, _favoritesOnly);
    }

    /// <summary>What the panel's subtitle states: the whole collection, or, while the heart is on, the
    /// favourites alone.
    ///
    /// <para>It is counted from the shelf actually on screen, not from the library's own totals. A subtitle
    /// that says two thousand records over a wall that shows eleven would describe something nobody is
    /// looking at.</para>
    /// </summary>
    private void UpdateLibrarySubheading()
    {
        if (Model is not { } model)
            return;

        // Nothing is counted before the shelf has been read. "0 albums" would claim something about a
        // collection nobody has read yet. The loader is shown instead (MainPage.Loading).
        if (!model.AlbumsRead)
        {
            LibrarySubheading.Text = "";
            return;
        }

        if (!_favoritesOnly)
        {
            LibrarySubheading.Text = model.LibrarySubheading;
            return;
        }

        var favorites = FavoriteAlbumFolders();
        var folders = model.LibraryAlbums.Where(favorites.Contains).ToList();
        var songs = folders.Sum(f => model.LibraryAlbumTrackCounts.TryGetValue(f, out var n) ? n : 0);
        var seconds = folders.Sum(f => model.LibraryAlbumSeconds.TryGetValue(f, out var s) ? s : 0);
        LibrarySubheading.Text = Presentation.Common.PaneStats.Library(folders.Count, songs, seconds);
    }

    /// <summary>What the album list gathers its records under: their artist, their genre or their year.
    /// The grouping is the list's ORDER as well as its headings. Records only look gathered when they are
    /// next to each other.
    ///
    /// <para>This is a menu on a button, like the wall's sort picker. It is remembered in settings in the
    /// same quiet way. It is a choice made rarely, and three chips across the header would stay in the way
    /// of the collection for good.</para>
    /// </summary>
    private void Group_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };

        Add(AlbumGrouping.None, "AlbumGroup_None", "\uf05e");       // struck through: gathered by nothing
        Add(AlbumGrouping.Artist, "AlbumGroup_Artist", "\uf007");   // a person
        Add(AlbumGrouping.Genre, "AlbumGroup_Genre", "\uf001");     // a note
        Add(AlbumGrouping.Year, "AlbumGroup_Year", "\uf073");       // a calendar

        menu.ShowAt(GroupButton);

        void Add(AlbumGrouping grouping, string key, string glyph)
        {
            var chosen = grouping == AlbumList.Grouping;
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(key),
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
                // The one in force shows a tick instead of its own glyph. This is the same rule as the sort
                // menu, for the same reason. A checkable item makes the whole menu reserve a check gutter to
                // the left of every icon.
                Icon = new FontIcon
                {
                    Style = (Style)Application.Current.Resources["FaMenuIcon"],
                    Glyph = chosen ? "\uf00c" : glyph,
                },
            };
            item.Click += (_, _) =>
            {
                AlbumList.Grouping = grouping;
                settings.AlbumGrouping = grouping;   // a remembered panel choice, like the wall's order
                PaintGroupButton();
            };
            menu.Items.Add(item);
        }
    }

    /// <summary>The grouping key wears the azure wash while the shelf is gathered by something. This is the
    /// same wash that the favourites heart and the view switch use, because it answers the same question: is
    /// this filter on? Gathered by nothing is the resting state, and it looks like one.</summary>
    private void PaintGroupButton()
        => SelectedSegment.Paint(GroupButton, AlbumList.Grouping != AlbumGrouping.None);

    /// <summary>
    /// Every cloud mark on screen, re-read: the wall's corner clouds, and the small cloud in front of
    /// each song in an open record, on the wall, in the list and on the album page.
    ///
    /// <para>One call covers all of them, because they show one fact at three sizes. A listener who puts a
    /// song up sees the tile and the line change together. Each view only looks at what it is showing. A
    /// shut drawer and an unread shelf cost nothing.</para>
    /// </summary>
    private void RefreshCloudMarks()
    {
        AlbumWall.RefreshBadges();
        AlbumWall.RefreshTrackBadges();
        AlbumList.RefreshBadges();
        AlbumList.RefreshTrackBadges();
        AlbumDetail.RefreshTrackBadges();

        // An artist's page lists their songs, and it can have one of their records open under it. It gets
        // the same re-read as the album views. The marks on the open record, and whose shelf they are read
        // on, decide what those marks mean.
        ArtistDetail.RefreshTrackBadges();
    }

    /// <summary>An album's tracks, in album order, straight from the mirror. An album folder is small, so
    /// one page covers a record. This runs off the UI thread. A band open that is superseded cancels
    /// it.</summary>
    private async Task<IReadOnlyList<TrackInfo>> LoadAlbumTracksAsync(string folder, CancellationToken ct)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return Array.Empty<TrackInfo>();

        var shelf = library.Shelf;
        return await Task.Run(() =>
            library.Read(shelf, "an album band's tracks", mirror => mirror.QueryFolderPage(folder, 0, 500))
                // Disc order, not the mirror's file-name order. Otherwise a multi-disc album plays and lists
                // both track 1s first, then both track 2s. No answer means an empty band. A refusal has
                // already put the problem screen up over the app.
                .Match<IReadOnlyList<TrackInfo>>(
                    rows => RPM.Core.Library.AlbumTracklist.InDiscOrder(rows.Select(t => t.ToTrackInfo(root)).ToList()),
                    _ => Array.Empty<TrackInfo>()));
    }

    // Playback routes into RPaMp, the same way the tree's play glyph does. The track becomes the
    // now-playing one there, and clicking the playing track again stops it. RPaMp shares the
    // PlaybackService singleton, so the band's green line stays true. Playing by hand rebuilds the
    // queue from this track to the end of its record (see RPaMpService.PlayAlbumFrom).
    private void PlayAlbumTrack(TrackInfo track)
    {
        var playback = App.Services.GetRequiredService<PlaybackService>();
        if (playback.IsPlaying(track.FilePath))
            playback.Stop();
        else
            App.Services.GetRequiredService<RPaMpService>().PlayAlbumFrom(track);
    }

    private void PlayAlbumTracks(IReadOnlyList<TrackInfo> tracks, bool shuffle)
    {
        if (tracks.Count == 0)
            return;
        var ordered = shuffle
            ? tracks.OrderBy(_ => Random.Shared.Next()).ToList()
            : tracks;
        App.Services.GetRequiredService<RPaMpService>().AddTracks(ordered, play: true);
    }

    /// <summary>The record becomes the set. This is the same work as the album menu's Start DJ, reached from
    /// the headphones beside the record's name in either view.</summary>
    private void StartDjAlbum(IReadOnlyList<TrackInfo> tracks)
    {
        if (tracks.Count == 0)
            return;
        App.Services.GetRequiredService<RPaMpService>().StartDjSet(tracks);
    }

    /// <summary>
    /// An album's cover at the edge the caller is going to draw it at. Every wall in the app reads covers
    /// from this one place (see <see cref="AlbumCovers"/>).
    ///
    /// <para>The caller names the edge. Naming it lets <see cref="ThumbCache"/> answer BY PATH, so the scan
    /// is not opened at all. That is one small file read per record per size, cached on disk across sessions.
    /// Without a named edge, every tile read the whole picture off the disk and hashed it in full, only to
    /// arrive at a thumbnail already in the cache.</para>
    ///
    /// <para>The SHORT side is the one named, because every surface that reads through here draws its cover
    /// in a square. These are the wall's tiles, the album list, the drawer's sleeve, and the artist page's
    /// shelves. See <see cref="AlbumCovers.LoadForSquareAsync"/>.</para>
    /// </summary>
    private Task<byte[]?> LoadAlbumCoverAsync(string folder, int edge, CancellationToken ct)
        => AlbumCovers.LoadForSquareAsync(folder, ct, edge);
}

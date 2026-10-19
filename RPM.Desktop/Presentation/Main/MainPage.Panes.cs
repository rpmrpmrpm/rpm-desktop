using RPM.Core.Layout;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// Both panes, and the seam between them.
///
/// <para>The left pane is a WMP7-style vertical function rail (Track info, Import, Visualizations,
/// Internet Radio), with the selected function's view beside it. Track info follows playback (everything
/// known about the record now playing); Import auto-opens on a fresh import; a foot button collapses the
/// column and a deck pill opens it again. Visualizations lists what the app can draw and launches one
/// full screen (the pane itself renders nothing). Internet Radio is the tuning dial over the station
/// directory.</para>
///
/// <para>The library pane on the right wears the same rail, mirrored: Albums, Artists, Genres,
/// Playlists: four ways of reading the collection rather than four things to do to a record, which is
/// the difference between the two sides of the window.</para>
/// </summary>
public sealed partial class MainPage
{
    private PlaylistModel? _playlistModel;
    private RadioModel? _radioModel;
    private bool _trackInfoWired;

    // Both panes' whole presentation (which function each shows, open or folded, and the column widths
    // that follow) lives in ONE state machine. Every change runs through it, and it is the only writer of
    // LeftCol and RightCol widths. So the transitions cannot race each other, because two owners of one
    // width would fight over it.
    private PaneConductor? _paneConductor;

    private PaneConductor Panes
    {
        get
        {
            if (_paneConductor is { } made)
                return made;
            _paneConductor = new PaneConductor(this);
            _paneConductor.WatchWidth();   // the width ceiling is re-decided on every window resize
            return _paneConductor;
        }
    }

    private void EnsurePlaylistPanel()
    {
        if (_playlistModel is not null || DispatcherQueue is null)
            return;
        var library = App.Services.GetRequiredService<LibraryService>();
        _playlistModel = new PlaylistModel(
            library,
            App.Services.GetRequiredService<RPaMpService>(),
            DispatcherQueue);
        PlaylistPanel.DataContext = _playlistModel;
        PlaylistPanel.DetailModeChanged += on => Panes.SetDrilled(on);   // a tracklist wants its width

        // Back out of a list that was pressed on the home screen: home again. The view says the press
        // happened and this puts home up, because home covers both panes and only this page can.
        PlaylistPanel.HomeRequested += () => ShowHome(true);
        _playlistModel.DetailInvalidated += PlaylistPanel.RefreshDetailIfOpen;        // add/remove ⇒ re-read
        _playlistModel.QueueInvalidated += PlaylistPanel.RefreshQueueDetailIfOpen;    // and the queue, itself only
        _playlistModel.MarksInvalidated += PlaylistPanel.RefreshMarkedDetailIfOpen;   // and a heart, the loved lists only

        // …and so does the artist page's shelf of playlists, which is a picture of this same tree.
        // It answers for itself whether a page is open and on screen, see RefreshPlaylists, because
        // this report fires on every library change for the life of the window.
        _playlistModel.DetailInvalidated += ArtistDetail.RefreshPlaylists;

        // And the drawn covers are about the tree too: a song added to a list changes what that list
        // looks like. Dropped whole rather than by node, see PlaylistCovers.Forget.
        _playlistModel.DetailInvalidated += PlaylistCovers.Forget;

        // And a chosen Records cover can go stale the same way: one of its four folders was retagged
        // out from under it. The database is captured HERE, synchronously, before anything is handed
        // to Task.Run, the same race PlaylistCoverSweepService's own doc names, where reading
        // library.Db late would let a queued background read enumerate whichever tree happens to be
        // open by the time the thread pool gets to it rather than the one that actually just changed.
        // Off the dispatcher because it is a directory probe per chosen folder, and DetailInvalidated
        // fires with a UI thread waiting on the rest of this handler list.
        _playlistModel.DetailInvalidated += () =>
        {
            if (!library.IsOpen)
                return;
            var db = library.Db;
            _ = Task.Run(() => DropStaleRecordCovers(db));
        };

        // The masters on disk are swept on LibraryService.Opened instead, not here: this method is a
        // lazy first-touch (whichever playlist-shaped UI the user reaches first), fired at most once a
        // session, where Opened fires on every open including a switch, the moment that actually means
        // a different tree is now in front of the app. See PlaylistCoverSweepService.
    }

    /// <summary>A chosen record that has been retagged out of existence, told apart from one whose
    /// disk is simply not plugged in right now (see <see cref="StaleRecordCovers.Find"/>, which is the
    /// whole rule). The master is bytes, so nothing else notices; dropping it sends that one playlist
    /// back through the gather, which falls the cover back to the quilt.
    /// </summary>
    private static void DropStaleRecordCovers(LibraryDatabase db)
    {
        try
        {
            var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, db.Root, null);
            foreach (var id in StaleRecordCovers.Find(db.GetPlaylistNodes(), Directory.Exists))
                PlaylistCoverStore.Forget(id, segment);
        }
        catch
        {
            // The captured database was disposed, the library it belonged to closed, or was replaced,
            // before this task ran, or some other read failed. Nothing downstream depends on this
            // having run: a dropped Records master is always recoverable, because the folders it named
            // are still in the database and the next gather rebuilds the same quilt from them (or a
            // smaller one, if fewer than four still resolve), and the next tree change tries the whole
            // check again regardless. This never touches a Picture master, which has no such way back;
            // StaleRecordCovers.Find only ever looks at Records.
        }
    }

    /// <summary>Keep the Track info pane following playback: on every playback change, if that view is
    /// open, point it at the file now playing. Wired once.</summary>
    private void EnsureTrackInfoWiring()
    {
        if (_trackInfoWired)
            return;
        _trackInfoWired = true;
        // "open the whole record" leaves the pane, onto whichever page the reader is on.
        TrackInfoPanel.AlbumRequested +=
            folder => NavigateToAlbumPage(folder, WhereTheReaderIs);
        TrackInfoPanel.Announced += ShowFlash;                  // …and a press with no visible answer says so
        App.Services.GetRequiredService<PlaybackService>().Changed += () =>
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (Panes.Open && Panes.Tab == LeftTab.TrackInfo)
                    UpdateTrackInfo();
            });
    }

    /// <summary>Feed the Track info pane the record now playing, or the empty state when there is
    /// none. The pane resolves its own library row and folder from the path, and ignores a path it is
    /// already showing, which is what keeps it still while playback ticks its position twice a second.
    ///
    /// <para>The RECORD and not the playing path: a source that plays a succession of tracks is
    /// playing under its own label until it has one, and that label is not something a sheet can be
    /// written about. Shown one, the pane put "dj-set:f33270…" in the header for the first seconds of
    /// a set, and a station's stream address for the whole of one.</para></summary>
    private void UpdateTrackInfo() =>
        TrackInfoPanel?.Show(App.Services.GetRequiredService<PlaybackService>().PlayingRecord);

    /// <summary>Builds the radio pane's model, once. Nothing is fetched here: the view asks the
    /// directory for itself the first time it is handed the model, so an app that never opens the radio
    /// never touches the network for it. Called from <see cref="PaneConductor.ApplyViews"/>, the one
    /// place a left view is put up, so every way into the pane builds it, the rail and a restored
    /// session alike.</summary>
    private void EnsureRadioPanel()
    {
        if (_radioModel is not null)
            return;

        // The queue the directory's answers come back on. The page's own, or the calling thread's when
        // the page has none yet: this runs while the session is being put back, which is before the page
        // has been through a layout, and a model built without a queue is a pane that never fills.
        var dispatcher = DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _radioModel = new RadioModel(App.Services.GetRequiredService<RadioService>(), dispatcher);
        RadioBody.DataContext = _radioModel;

        // Bad news from the radio goes to the pane's band, not over the station list: the directory going
        // quiet leaves the saved stations listed and playable, so the message has rows underneath it.
        _radioModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RadioModel.Error) or nameof(RadioModel.HasError))
                SyncLeftPaneBand();
        };
    }

    /// <summary>What the Import view has to say for itself: a scan that turned nothing up, a drop that
    /// held no folder. Held here rather than in the model because it is news about one visit to the
    /// view, not a state the queue is in.</summary>
    private string? _importBand;

    /// <summary>Shows the foot band with whatever the open view has to say, or hides it. One band for the
    /// whole column: only the view actually on screen gets to speak, so switching away takes its message
    /// with it (the radio's error survives the switch and comes back with the view).
    ///
    /// <para>Two voices, told apart by colour: the radio's is a failure and wears the palette's Red, the
    /// import's is "that did nothing" and wears its Gold. A folder with no music in it is not a fault
    /// to be fixed, and dressing it as one says the app broke.</para></summary>
    private void SyncLeftPaneBand()
    {
        var (message, warning) = Panes.Tab switch
        {
            LeftTab.Radio => (_radioModel?.Error, false),
            LeftTab.Import => (_importBand, true),
            _ => (null, false),
        };

        LeftPaneBandText.Text = message ?? "";
        LeftPaneBand.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (string.IsNullOrEmpty(message))
            return;

        var voice = warning ? "BandWarning" : "BandError";
        LeftPaneBand.Background = BandBrush(voice + "Fill");
        LeftPaneBand.BorderBrush = BandBrush(voice + "Edge");
        LeftPaneBandMark.Foreground = BandBrush(voice + "Mark");
        LeftPaneBandText.Foreground = BandBrush(voice + "Ink");
    }

    /// <summary>The band's colours by name. They live in Themes/MessageBand.xaml, which App.xaml merges
    /// into the application's own dictionary, so the page's <c>Resources</c> does not hold them: asking
    /// it hands back nothing and the band paints bare. StaticResource in markup walks up to the app on
    /// its own; a lookup from code has to go there itself.</summary>
    private static Brush BandBrush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Raises the Import view's warning band. The pane is opened on Import by whatever is
    /// scanning, so this only has to say the words.</summary>
    /// <summary>
    /// A press of one of the library rail's tabs, and Cmd or Ctrl+F asking for the collection
    /// (<see cref="FocusSearch"/>): each tab asks for its view's own wall or tree, not for the one page
    /// that happens to be open in it, and leaves home.
    /// </summary>
    private void AskForTab(RightTab tab)
    {
        // The press, for the switch diagnostic. The library's three views each have their own
        // stopwatch. The rail's tabs do not, and their switch is the bigger move: a whole view stands
        // down and another comes up.
        RPM.Desktop.Diagnostics.SwitchDiag.Begin($"library {tab}");

        // Playlists is every list, so pressing it is asking for the tree, not for the one list that
        // happens to be open in it. Said first because the panel has to exist to be asked; the
        // three views below say the same thing as ShowXDetail(false), and this view holds its own
        // drilled state, so it is asked directly.
        //
        // The tree, and not home, even where the list was pressed on the home screen. The view's
        // own back key (DetailBack_Click) goes back the way the reader came; this one does not go
        // back at all, it asks for a place.
        if (tab == RightTab.Playlists)
        {
            EnsurePlaylistPanel();
            PlaylistPanel.ExitDetail();
        }

        // Albums is the collection, so pressing it asks for the collection, not for the one record open
        // in that view. Without this, the tab would be lit while an album page is up, and pressing it
        // would do nothing.
        if (tab == RightTab.Albums)
            ShowAlbumDetail(false);

        // Artists is everybody, so the same holds for the wall. The tab stays lit while one artist's
        // page is open, and pressing it brings back the wall.
        if (tab == RightTab.Artists)
            ShowArtistDetail(false);

        // Genres is every kind of music, so the same holds: the button asks for the wall, not for the one
        // shelf open in that view.
        if (tab == RightTab.Genres)
            ShowGenreDetail(false);

        RPM.Desktop.Diagnostics.SwitchDiag.Mark("detail");
        Panes.OpenRightTab(tab);
    }

    private void ShowPaneWarning([Shown] string message)
    {
        _importBand = message;
        SyncLeftPaneBand();
    }

    /// <summary>Drops the Import view's message. Called as a scan starts: the last one's news is stale
    /// the moment a new folder is being read.</summary>
    private void ClearPaneBand()
    {
        _importBand = null;
        SyncLeftPaneBand();
    }

    /// <summary>The × on the band. The message has been read; the view that raised it drops it.</summary>
    private void LeftPaneBand_Dismiss(object sender, RoutedEventArgs e)
    {
        if (Panes.Tab == LeftTab.Radio)
            _radioModel?.Dismiss();
        else if (Panes.Tab == LeftTab.Import)
            _importBand = null;
        SyncLeftPaneBand();
    }

    /// <summary>The one entry point the rest of the page calls (queue changes, attach). Feeds the current
    /// queue state to the conductor, which re-applies the whole left pane.</summary>
    private void SyncLeftPanel()
    {
        if (Model is not { } m)
            return;
        EnsurePlaylistPanel();
        EnsureTrackInfoWiring();
        Panes.SyncQueue(m.QueueVisible, m.QueueHasRows);
    }

    /// <summary>Wires both rails' two reports to the conductor. A tab press is only ever a tab press
    /// here: the radio has to be built before it is shown, and that is done where views are shown (see
    /// <see cref="PaneConductor.ApplyViews"/>), not at one of the ways of asking for one.</summary>
    private void WirePaneViews()
    {
        Rail.TabRequested += tab =>
        {
            // Opening the import view is the answer to the hint that points at its button.
            if (tab == LeftTab.Import)
                Coachmarks.CoachmarkState.Seen(RPM.Core.Library.CoachMark.AddMusic);

            RPM.Desktop.Diagnostics.SwitchDiag.Begin($"pane {tab}");
            Panes.OpenTab(tab);   // the conductor is made on first use, not here
        };
        Rail.CloseRequested += () => Panes.ToggleView();
        VizBody.LaunchRequested += id => ShellPage.Current?.ShowVisualizer(id);

        // A play written to the log does not mark the home screen stale. Every row on home is a
        // question about what has been played, so a play could change a row. But a re-read costs more
        // than it gains. Two plays in two minutes would mean two full re-reads (a shelf read of half a
        // second to two and a half seconds, and every picture on screen fetched again) to move one
        // record to the front of one row. So home re-reads only when the shelf changes
        // (HomeView.OnShelfChanged). The cost is that "Recently listened" can be one record behind
        // until something else forces a read. That is better than a screen that rebuilds itself under
        // the reader.

        // And the library's own rail, the same two reports. Its playlist model is built here rather than
        // on the way in, because the library row menu's "Add to playlist" needs the tree whether or not
        // that view has ever been opened.
        LibraryRail.TabRequested += AskForTab;
        LibraryRail.CloseRequested += () => Panes.ToggleRightView();

        // The deck's own reports: its pill drives the same pane the rail's foot button closes, its
        // hamburger opens the shell's menu at the button, and its view menu asks for one of the app's
        // three views. None of the three is decided here, the deck asks, the service moves, and the
        // deck is told what happened so its menu can grey the line the app is on.
        Deck.PaneToggleRequested += () => Panes.Toggle();
        Deck.HomeRequested += () =>
        {
            RPM.Desktop.Diagnostics.SwitchDiag.Begin("home");
            ShowHome(true);
        };
        Deck.MenuRequested += fe => ShellPage.Current?.ShowMenuAt(fe);

        var views = App.Services.GetRequiredService<ViewModeService>();
        Deck.ViewRequested += views.Switch;
        views.Changed += Deck.SetView;
        views.Changed += RememberAppView;   // which view the window was left in
        Deck.SetView(views.Current);
    }

    /// <summary>The user dragged the splitter: hand the new star split to the conductor (it writes the
    /// columns and remembers the split as the open width).</summary>
    private void ResizeLeftPane(double leftStars, double rightStars) => Panes.Resize(leftStars, rightStars);

    /// <summary>Both panes' single source of truth: which function the left pane shows
    /// (<see cref="Tab"/>) and whether it is <see cref="Open"/> or <see cref="Shown"/> at all, which
    /// way the library pane is <see cref="Reading"/> the collection and whether it is
    /// <see cref="RightOpen"/>, plus the remembered star split. Every state change funnels through
    /// <see cref="Apply"/>, the ONLY writer of the column widths, so open/close, tab switches, folds
    /// and splitter drags can't fight over the width.
    /// </summary>
    private sealed class PaneConductor(MainPage p)
    {
        // The open split the user prefers (updated by splitter drags). Close zeroes the left column so
        // the library takes the whole row.
        private double _leftStars = 1.0;
        private double _rightStars = 1.4;
        private bool _queueWasVisible;
        private bool _queueHadRows;      // rows, not chrome: a running scan makes the queue visible with none
        private bool _applying;

        // Live column values (what the columns currently read), the start point for the next width
        // glide.
        private double _curLeft = 1.0;
        private double _curRight = 1.4;
        private bool _primed;                 // first apply snaps; later ones glide
        private EventHandler<object>? _widthTick;

        /// <summary>Which glide is the current one. A deadline from an older glide must not land on a
        /// newer one, repeated clicks on the pane button redirect the tween mid-flight, and each
        /// redirect starts a fresh generation.</summary>
        private int _glideGeneration;

        /// <summary>How long the width takes. `RPM_PANE_MS` overrides it for measuring; 240 is what
        /// ships.</summary>
        private static readonly double GlideMs =
            double.TryParse(
                RPM.Core.Diagnostics.DevSwitch.Read("RPM_PANE_MS"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var askedMs)
            && askedMs >= 80 && askedMs <= 1000
                ? askedMs
                : 240;

        /// <summary>Each half of the content's crossfade. Short on purpose: it is a short accent around the
        /// width's move, not an animation of its own, and the two of them plus the glide are what the user
        /// sees as one movement.</summary>
        private const double FadeMs = 110;

        /// <summary>
        /// The shortest gap between two column writes on a live resize, for a card that cannot be laid
        /// out inside a display frame: 30 a second. The WinUI head's floor; the Skia head's is
        /// <see cref="LiveFloorMs"/>.
        /// </summary>
        /// <remarks>
        /// Every write is a real measure and arrange of both columns and everything standing in them, so
        /// a frame skipped here is layout work the move does not have to do. Where the layout is what
        /// stops the next frame arriving, asking for half as many frames gives each one time to finish.
        /// It is a floor and not a schedule: a tick that arrives late is taken as it comes, and the
        /// landing frame is never skipped.
        ///
        /// <para><b>Measured on the album wall with the window maximised.</b> With the cap, a move
        /// takes 6 to 7 frames of a 240 ms move, and the worst gap between frames is 60 to 75 ms.
        /// Without it, a move takes 5 to 8 frames and the worst gap is 54 to 168 ms. So for the wall
        /// the cap does not cost frames. It gives smoother steps, because asking for the display's rate
        /// gives no more frames and a worse tail.</para>
        /// </remarks>
        private const double HeavyStepMs = 33;

        /// <summary>
        /// The floor each head ships with. The Skia head writes on every frame the display offers; WinUI
        /// keeps <see cref="HeavyStepMs"/> until it has been measured on Uno 6.8.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured on macOS with Uno 6.8.0-dev.60</b> (60 Hz, a large library, full screen,
        /// the left pane closing and opening). At 33 ms: 6 to 8 writes per 240 ms move, worst gap 49 to
        /// 58 ms, on Artists, Genres, Home and the album wall alike. At 0: 25 to 32 writes, worst gap
        /// 16 to 22 ms. Under a heavy trace the album wall at 0 fell to 15 to 17 writes with one 154 ms
        /// gap, against a worst gap of 55 to 94 ms at 33 ms in the same run. A trace of what reached
        /// the display shows 13 to 14 presented frames per move at both floors, 16.7 ms apart with at
        /// most one 33 ms gap. So at 33 ms the pane moves on about half the frames the screen shows,
        /// and at 0 it moves on every one. The main thread is about 18% busy through a move at 0. A 16
        /// ms floor was no better than 0, and sometimes worse, because the render tick's spacing
        /// jitters around a frame.</para>
        ///
        /// <para>The 33 ms value was set from readings on Uno 6.7. That version did a Skia path
        /// operation for each visual on every frame's damage region. Uno 6.8 does not, which changes
        /// the answer for this head. WinUI's layout cost is its own and has not been measured
        /// since.</para>
        /// </remarks>
#if RPM_WINUI
        private const double LiveFloorMs = HeavyStepMs;
#else
        private const double LiveFloorMs = 0;
#endif

        // Home uses the same floor as every other card. `RPM_PANE_STEP_MS` still overrides it for
        // measuring.

        /// <summary>
        /// The floor in force for this move: the head's own (<see cref="LiveFloorMs"/>), unless
        /// `RPM_PANE_STEP_MS` asks for another one to measure it. The constants above carry the readings.
        /// </summary>
        private double LiveStepMs => _stepAsked ?? LiveFloorMs;

        /// <summary>The floor as the environment sets it, for measuring what it is worth:
        /// `RPM_PANE_STEP_MS` takes a number of milliseconds and 0 is every frame the display offers. It
        /// overrides BOTH cards, which is what makes a sweep a sweep. Null where nothing was
        /// asked.</summary>
        private static readonly double? _stepAsked = ReadStepMs();

        private static double? ReadStepMs()
            => double.TryParse(
                   RPM.Core.Diagnostics.DevSwitch.Read("RPM_PANE_STEP_MS"),
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var asked)
               && asked >= 0 && asked <= 200
                   ? asked
                   : null;

        /// <summary>What <see cref="ApplyViews"/> last put up, per pane, null until it first has. What a
        /// move fades is the difference between these and the state as it now stands, so a re-apply that
        /// changes nothing (a queue count ticking over, a window resize) costs no show.</summary>
        private (LeftTab Tab, bool Open, bool Shown, bool Queue)? _leftShowing;
        private (RightTab Reading, bool Open, bool Home)? _rightShowing;

        private (LeftTab, bool, bool, bool) LeftSignature =>
            (Tab, Open, Shown, p.Model?.QueueVisible ?? false);

        // Home uses the RIGHT signature, since that is the host it stands in: going home swaps the
        // library card's content, and the left pane's own change is already told by Open.
        private (RightTab, bool, bool) RightSignature => (Reading, RightOpen, AtHome);

        /// <summary>
        /// How wide the pane is allowed to get, in px, closed over the star split.
        ///
        /// <para>The split is a ratio, so on its own the pane keeps its share of whatever it is given:
        /// 42% of the window. Right while the window is small, everything in there is cramped and every
        /// pixel is worth having, and wrong on a large one, where it becomes a half-metre column beside
        /// the library it is only an index INTO. This is the width a 1280-wide window already hands it,
        /// which is about where the ratio stops paying for itself; past that the library gets the
        /// extra.</para>
        ///
        /// <para>A number rather than one measured off the content. Measuring is what the pane WANTS to
        /// know (a tuning dial and a grid of tracks in columns are not the same shape), but the answer
        /// moves every time anything in there is laid out, and a ceiling that moves is a width that
        /// dances. A number that is a little wrong and completely still beats one that is right and
        /// twitching.</para>
        /// </summary>
        private const double MaxLeftWidthPx = 520;

        /// <summary>
        /// The narrowest the library is left while it has a playlist's tracks open.
        ///
        /// <para>The drill-in is a track list in columns (number, title, artist, album, length). The
        /// tree it replaced was a column of names. It is the one view in either pane that asks for room
        /// on its own account, rather than being given whatever its pane has. On this side of the
        /// window the request reads as a floor under the library, which the pane opposite gives way
        /// to.</para>
        ///
        /// <para>Only ever a floor. A library already wider than this is not touched, which is the
        /// common case (it is the wide half of the window), so on most windows opening a playlist moves
        /// nothing at all, and the pane is squeezed only where it was genuinely taking the room.</para>
        /// </summary>
        private const double DrillMinRightWidthPx = 720;

        /// <summary>The ceiling in force right now: Import has none at all, and a playlist open in the
        /// library lowers whichever one applies.
        ///
        /// <para>Import is the one view that is worked IN rather than read from: a queue of files being
        /// ticked, retitled and matched, in as many columns as the grid has. The reasoning behind the
        /// ceiling (a pane that is only an index INTO the library should not take half the window) does
        /// not hold for a view that IS the work, so it is let out to wherever the user drags it (the
        /// library's own minimum is the only stop) and stands at its own remembered width.</para>
        /// </summary>
        public double MaxLeftWidth
        {
            get
            {
                var ceiling = ImportSized ? double.PositiveInfinity : MaxLeftWidthPx;
                return Drilled ? Math.Min(ceiling, DrilledCeiling) : ceiling;
            }
        }

        /// <summary>What the pane may take while the library is showing a playlist: everything the
        /// library does not need for it, and never less than a panel's own minimum. A row too narrow
        /// for both is a row where something has to be too small, and it is not the pane being worked
        /// in.</summary>
        private double DrilledCeiling =>
            Math.Max(MinPanelWidth, p.PanelsGrid.ActualWidth - Seam() - DrillMinRightWidthPx);

        /// <summary>Whether the library has a playlist's tracks open rather than the tree.</summary>
        public bool Drilled { get; private set; }

        /// <summary>Whether the pane's width is Import's own remembered pixel width rather than a share
        /// of the row under a ceiling. Import only, while the pane is open, and only while the library
        /// is standing beside it: with the library folded away Import has the whole row and there is no
        /// remembered width to hold it to.</summary>
        private bool ImportSized => Open && RightOpen && Tab == LeftTab.Import;

        /// <summary>Import's remembered width in px: negative until read from the settings, zero when the
        /// settings hold none (never dragged) and the default is standing in for it.</summary>
        private double _importWidth = -1;

        private static SettingsService Settings => App.Services.GetRequiredService<SettingsService>();

        /// <summary>How wide Import stands: what the user last dragged it to, or the width every other
        /// view is capped at when they never have, the ceiling is what Import is being let out of, so
        /// that is where being let out starts from.
        ///
        /// <para>Clamped to what the row can actually give: the library keeps its minimum, so a width
        /// dragged on a wide window does not swallow the library on a narrow one. The clamp is applied to
        /// the answer only, never to what is remembered, widen the window again and the pane comes back
        /// to the width it was given.</para>
        /// </summary>
        private double ImportWidth
        {
            get
            {
                if (_importWidth < 0)
                    _importWidth = Settings.ImportPaneWidth;
                return LeftPaneWidth.ForImport(
                    _importWidth, MaxLeftWidthPx, p.PanelsGrid.ActualWidth - Seam(), MinPanelWidth);
            }
        }

        private static SessionService Session => App.Services.GetRequiredService<SessionService>();

        public LeftTab Tab { get; private set; } = LeftTab.TrackInfo;   // Track info is the first function

        /// <summary>Whether the selected function's view is up beside the rail, the chevron's own
        /// answer, held in <see cref="_open"/>. Home does not enter into it: it stands over the library
        /// card and this column is not in it.</summary>
        public bool Open => Stage.LeftViewUp;

        private bool _open;                                              // closed by default

        /// <summary>
        /// Whether the home screen is the view up.
        ///
        /// <para>It is a view in its own right rather than one more way of reading the collection, so it
        /// is on neither rail: while it is up the library's rail is lit for nothing and home has that
        /// card. The left pane goes on as it was, its rail lit, its view up or folded as its chevron
        /// says. The tab the library will come back to is still held here, untouched, which is what
        /// makes leaving home "the view that was up before" rather than whichever view the app would
        /// otherwise fall back on.</para>
        /// </summary>
        public bool AtHome { get; private set; }

        /// <summary>What is on screen, as opposed to what has been asked for: the one place the home
        /// screen's effect on the library card and its rail is written down. See <see
        /// cref="PaneStage"/>.</summary>
        private PaneStage Stage => new(AtHome, _open, Tab, RightOpen, Reading);

        /// <summary>The same, for a reader outside the conductor: which search field is on screen is
        /// read off it (see <see cref="SearchTarget"/>).</summary>
        public PaneStage OnScreen => Stage;

        /// <summary>Whether the column is there at all, the rail included.
        ///
        /// <para>The two are separate because the two controls that move them mean different things.
        /// The chevron at the foot of the rail folds the VIEW away and leaves the tabs. The reader is
        /// done reading, not done with the pane. Taking the four functions off the screen as well would
        /// leave the only way back on the other side of the window. The deck's pane pill folds the
        /// whole column, and that is the control for showing or hiding the side pane.</para>
        /// </summary>
        public bool Shown { get; private set; } = true;

        /// <summary>The rail on its own, with no view beside it.</summary>
        private bool RailOnly => Shown && !Open;

        /// <summary>What each rail measures. Fixed in the rails' own markup; read from the controls where
        /// they have been laid out, since a number in two files is a number that will disagree with
        /// itself.</summary>
        private double RailWidth => p.Rail.ActualWidth > 0 ? p.Rail.ActualWidth : RailWidthFallback;

        private double RightRailWidth =>
            p.LibraryRail.ActualWidth > 0 ? p.LibraryRail.ActualWidth : RailWidthFallback;

        private const double RailWidthFallback = 82;

        /// <summary>Which way the library pane is reading the collection.</summary>
        public RightTab Reading { get; private set; } = RightTab.Albums;

        /// <summary>Which of the library card's four views is actually on screen, see
        /// <see cref="PaneStage.RightShowing"/>, which is where that is decided.</summary>
        private RightTab? ShownRight => Stage.RightShowing;

        /// <summary>Whether the library pane's view stands beside its rail, or is folded away to leave
        /// the rail alone. Open, always, except while the import workbench has asked for the
        /// window.</summary>
        public bool RightOpen { get; private set; } = true;

        /// <summary>
        /// Whether the library is allowed to step aside at all.
        ///
        /// <para>It steps aside for one thing: the import workbench wanting the width, which is the one
        /// view in the app that is worked IN. Anywhere else there is nothing on the other side of the
        /// window that wants the room, and a library folded down to a rail would be the app hiding the
        /// only thing it is for.</para>
        /// </summary>
        public bool CanFoldRight => Shown && Open && Tab == LeftTab.Import;

        /// <summary>
        /// The panes as the last session left them: which function the left pane showed, open or shut,
        /// which way the library was being read, and the split they stood at.
        /// </summary>
        /// <param name="split">Null when nothing usable was stored, and then the built-in split
        /// stands.</param>
        public void Restore(bool open, LeftTab tab, RightTab reading, (double Left, double Right)? split)
        {
            if (split is { } stars)
            {
                _leftStars = stars.Left;
                _rightStars = stars.Right;
            }

            Tab = tab;
            _open = open;
            Reading = reading;
            Apply();
        }

        /// <summary>Writes the panes' state down for the next run. Called from <see cref="ApplyCore"/>,
        /// so every path that changes them, either rail, the deck pill, a fresh import, is covered by
        /// the one line rather than by a note at each of them.
        ///
        /// <para>The library's fold is not written down: it is only ever offered while the import
        /// workbench is up, and an import queue does not survive the process (see RestoreSession). A
        /// library that came back folded would be folded for a reason that had gone.</para></summary>
        private void Remember()
        {
            var session = Session;
            session.LeftPaneOpen = _open;   // the chevron's own state, which is now the only one there is
            session.LeftTab = Tab.ToString();
            session.RightTab = Reading.ToString();

            // Whether home was over the two of them is NOT written. The app opens on home whatever was
            // up when it closed, so nothing reads it, see MainPage.RestoreHome.
        }

        /// <summary>
        /// The rail's four tabs: which view stands beside it.
        ///
        /// <para><b>Home is not touched.</b> This column stands beside the library card rather than in
        /// it, so asking for the track pane or the stations does not leave the screen that covers the
        /// card. The ways out are the controls that name the CARD: its own four tabs, and a record or a
        /// list pressed on home itself.</para>
        /// </summary>
        public void OpenTab(LeftTab tab)
        {
            Tab = tab;
            _open = true;
            Shown = true;   // a tab pressed on a hidden column is a column being asked for
            Apply();
        }

        /// <summary>The library rail's tabs: which way the collection is read. There is no closed
        /// state to come back from here, pressing a tab is asking for that view, and the fold is the
        /// chevron's own business.
        /// </summary>
        /// <remarks>
        /// <b>This is a way out of home</b>, because it asks for the card home is standing over: its
        /// four tabs, and a record or a list pressed on home itself, which arrives here too.
        ///
        /// <para>It does not fold the left view on the way out. The left pane keeps the state it had: a
        /// pane the reader folded stays folded, and a pane they had open stays open. Nothing appears,
        /// and nothing is taken away.</para>
        /// </remarks>
        public void OpenRightTab(RightTab tab)
        {
            Reading = tab;
            RightOpen = true;
            AtHome = false;
            Apply();
        }

        /// <summary>The chevron at the foot of the rail: the view folds away and the tabs stay, and
        /// pressing it again brings back the view that was folded, which is the whole reason the
        /// chevron turns round rather than disappearing. The tab is never changed here: what comes back
        /// is what went away.
        /// </summary>
        public void ToggleView()
        {
            // Home is not a special case here. The pane opens beside home, and home stays up, as the
            // chevron says it will. A reader who pressed the chevron to see the track pane keeps the
            // screen they were on.
            _open = !_open;
            Apply();
        }

        /// <summary>
        /// The deck's home key: home up, or home away and the two panes back on the views they were on.
        ///
        /// <para>Nothing about either pane is changed on the way in: not the tab, not the chevron, not
        /// the split. So there is nothing to restore on the way out. Leaving home stops covering what
        /// was already there. The left pane is not covered at all. It stands beside home, exactly as it
        /// stands beside the library card.</para>
        /// </summary>
        public void GoHome(bool on)
        {
            if (AtHome == on)
                return;
            AtHome = on;
            Apply();
        }

        /// <summary>The Playlists view drilled into a playlist (true) or returned to the tree (false).
        /// Only counts while that view is actually the one up: the drill is a request for width made by
        /// something on screen, and a view that has been switched away from is not making it.</summary>
        public void SetDrilled(bool on)
        {
            if (!(RightOpen && Reading == RightTab.Playlists))
                on = false;
            if (Drilled == on)
                return;
            Drilled = on;
            Apply();
        }

        /// <summary>The same gesture on the library's rail: its view folds away and leaves the tabs, so
        /// the import workbench beside it has the window. Ignored where the fold is not offered, the
        /// button is not there to press then, and this is the same answer from the other
        /// direction.</summary>
        public void ToggleRightView()
        {
            if (!CanFoldRight)
                return;
            RightOpen = !RightOpen;
            _libraryAskedBack = RightOpen;   // brought back by hand: no later scan folds it away again
            Apply();
        }

        /// <summary>Whether the user has taken the library back out while an import was running. Set by
        /// the chevron, cleared when a fresh import starts, because the next import is a new piece of work and
        /// makes its own decision.</summary>
        private bool _libraryAskedBack;

        /// <summary>
        /// The same fold, asked for by a scan that has just landed rows rather than by the chevron: the
        /// queue is there to be worked through now, so the workbench takes the window and the library
        /// steps back to its rail.
        ///
        /// <para>Only ever a fold, never an unfold: a library the user brought back mid-import stays
        /// back until the import is over, and then it returns on its own (see ApplyCore, where a pane
        /// that has left Import takes the library out with it).</para>
        /// </summary>
        public void FoldLibraryForImport()
        {
            if (!CanFoldRight || !RightOpen || _libraryAskedBack)
                return;
            RightOpen = false;
            Apply();
        }

        /// <summary>
        /// The deck's pane pill: the whole column, rail and all.
        ///
        /// <para>Coming back it opens on the view it had, because a column that returned as a bare rail
        /// would answer a press with almost nothing, and the press was for the pane.</para>
        /// </summary>
        public void Toggle()
        {
            Shown = !Shown;
            _open = Shown;
            Apply();
        }

        /// <summary>Queue-driven transitions: a fresh import opens the pane on Import; when a queue
        /// that WAS there empties (the import finished), Import hands back to Track info. Only on that
        /// transition: the user can sit on Import with an empty queue, and that view is the add-music
        /// invitation now.
        ///
        /// <para>The hand-back depends on ROWS having been there, not on the view having been up. The
        /// queue counts as visible while a scan is only running. So a scan that turned nothing up would
        /// look like an import that had just finished, and it would take the pane to Track info a
        /// second after a drop had opened it on Import. Nothing was imported, and the news about that
        /// is on the view the pane has just left.</para>
        /// </summary>
        public void SyncQueue(bool queueVisible, bool queueHasRows)
        {
            if (queueVisible && !_queueWasVisible)
            {
                _open = true;
                Tab = LeftTab.Import;
                _libraryAskedBack = false;
                AtHome = false;   // the workbench is asking for the window, so home is not what is up
            }
            else if (!queueVisible && _queueHadRows && Tab == LeftTab.Import)
            {
                Tab = LeftTab.TrackInfo;
            }
            _queueWasVisible = queueVisible;
            _queueHadRows = queueHasRows;
            Apply();
        }

        /// <summary>Splitter drag: write the columns now, and remember the split as the open width. On
        /// Import the drag sets that view's own width instead, and the star split the other views share
        /// is left exactly as it was, which is what they come back to.</summary>
        public void Resize(double leftStars, double rightStars)
        {
            leftStars = Math.Max(1, leftStars);
            rightStars = Math.Max(1, rightStars);

            // A drag owns the width directly, with no animation fighting the pointer. A move cut short
            // this way still owes its views the change it was making room for, and there is no landing
            // left to come and make it, so it is made here.
            var interrupted = Moving;
            StopWidthGlide();
            if (interrupted)
            {
                ApplyViews();
                SetViewOpacity(left: true, right: true, opacity: 1);
            }

            _curLeft = leftStars;
            _curRight = rightStars;

            // The drag hands its columns over in pixels (used as stars, which is the same layout). Import
            // reads them as the pixels they are, and keeps the number.
            if (ImportSized)
            {
                _importWidth = leftStars;
                Settle();
                return;
            }

            Settle();

            // Not while a playlist is open: the drag is happening inside a ceiling the tracklist put
            // there, so what it lands on is not the split the user wants back once the tracklist has
            // gone. It nudges the live width and the base is restored on the way out.
            if (!Drilled)
            {
                _leftStars = leftStars;
                _rightStars = rightStars;
            }
        }

        /// <summary>The drag ended: write Import's width to the settings, once. During the drag it is
        /// only held in the conductor, a settings save is a file write, and doing one per pointer move
        /// would rewrite settings.json a hundred times crossing the window.</summary>
        public void CommitWidth()
        {
            if (ImportSized && _importWidth > 0)
                Settings.ImportPaneWidth = _importWidth;
            else if (!Drilled)
                Session.Split = (_leftStars, _rightStars);   // where the seam was left, for the next run
        }

        public void Apply()
        {
            if (_applying)
                return;   // re-entrancy guard: a view told to stand down can echo back through here
            _applying = true;
            try { ApplyCore(); }
            finally { _applying = false; }
        }

        private void ApplyCore()
        {
            if (p.Model is not { } m)
                return;

            // ── the library pane ── The fold is the import workbench's to ask for, so a left pane that
            // has left Import takes the library back out with it: a fold nobody can undo is a view held
            // down by a button that is no longer on the screen.
            if (!CanFoldRight)
                RightOpen = true;

            // The drill only lives on the Playlists view while it is up. Dropped HERE rather than beside
            // the ExitDetail that follows from it, so the width decision below is made without it and the
            // pane gets its room back on the same move, and so the view's own report of having left the
            // tracklist finds the state already changed and does not come back through here.
            //
            // Home is not such a switch: it stands OVER the card for as long as it is up and the
            // Playlists view is still the one underneath, so a tracklist left open is still open when
            // the reader comes back out of home.
            if (!AtHome && !(RightOpen && Reading == RightTab.Playlists))
                Drilled = false;

            ApplyChrome();
            RPM.Desktop.Diagnostics.SwitchDiag.Mark("chrome");

            // The single width decision: hidden ⇒ zero the left column (library fills the row); rail
            // only ⇒ the rail's own width, written in pixels by ResolvedLeftWidth; open ⇒ the user's
            // remembered split, or the whole row less the library's rail while the library is folded
            // away.
            //
            // The VIEWS follow it rather than lead it (see ApplyViews): the room is made first and what
            // goes in it changes once the room is the right size.
            (double left, double right) = !Shown || !Open ? (0.0, _rightStars) : (_leftStars, _rightStars);
            GlideColumns(left, right);
            RPM.Desktop.Diagnostics.SwitchDiag.Mark("glide");

            Remember();   // the panes' own state, for the next run
        }

        /// <summary>The furniture: the rails, their chevrons, the seam and the deck's pill. It answers
        /// the press straight away: a tab that lit a quarter of a second after it was clicked would
        /// read as the app having missed the click, whatever the pane behind it was doing.
        /// </summary>
        private void ApplyChrome()
        {
            var stage = Stage;

            p.Rail.SetActive(stage.LeftLit, importActive: stage.LeftLit == LeftTab.Import);   // always lit: see PaneStage.LeftLit
            p.Rail.SetFolded(!Open);   // the chevron faces the way it works; see LeftRail.SetFolded

            p.LibraryRail.SetActive(stage.RightLit);
            p.LibraryRail.SetFoldable(CanFoldRight);
            p.LibraryRail.SetFolded(!RightOpen);

            // The seam stands between whatever the two columns hold (two views, a view and a bare rail,
            // or two rails). It is the gutter down the middle of the window and it is always the same
            // width, so folding a pane never changes how far its neighbour is from what is beside it. It
            // goes only when there is no left column at all and the library has the whole row.
            p.Splitter.Visibility = Vis(Shown);
            // Dragged only where there are two VIEWS to size against each other: a bare rail has no width
            // of its own to give, and a folded library was folded on purpose.
            p.Splitter.IsHitTestVisible = Shown && Open && RightOpen;
            if (Open && RightOpen)
                p.DebutSplitterGrip();   // the grip introduces itself the first time there is a seam to drag

            // The card itself is the box being resized, not something standing in it, so it comes and
            // goes with the column. Its view comes with it when the pane opens on the tab it had (see
            // GlideColumns and ApplyLeftViews). The view is put in during the move, not at the end of
            // it. If it waited for the end, the pane would grow as an empty card.
            p.LeftPanel.Visibility = Vis(Shown);

            // The views never lay out narrower than the pane's narrowest open width. A move passes
            // through every width between the rail's and the pane's, and without a floor the text in a
            // view wrapped one character a line for the first and last frames of it. With the floor the
            // column clips the view instead, as the visualization gallery's own 240-point floor does.
            // No settled width is below it: the splitter stops at MinPanelWidth.
            p.LeftViews.MinWidth = Math.Max(0, MinPanelWidth - RailWidth);

            p.Deck.SetPaneOpen(Shown);   // the deck pill's drawer rides with the column it hides

            // Home's tab, since it has none on either rail: the deck's key wears the same mark the rails'
            // tabs wear while home is the view up.
            p.Deck.SetHomeActive(stage.HomeLit);
        }

        /// <summary>What stands in each pane, run when the columns have finished moving, never before.
        ///
        /// <para>The views are the last thing to change, and that order is the whole point of it: a
        /// pane resizes with what it was already showing still in it, and the swap happens once the
        /// room is the size the new view was asked for. The other order would put the incoming view in
        /// at the old width and then move the walls around it, so every switch would be watched
        /// reflowing. The wall's reflow is held for the length of the glide, so nothing relaid itself
        /// out in the middle of the move.</para>
        ///
        /// <para>Written from the state as it stands rather than from what the glide was for, so it is
        /// safe to run at every landing, and a glide overtaken by a newer one simply lands on the newer
        /// answer.</para>
        /// </summary>
        private void ApplyViews()
        {
            if (p.Model is null)
                return;

            ApplyLeftViews();
            ApplyRightViews();
        }

        /// <summary>The left pane's half of <see cref="ApplyViews"/>: which tab's body is up, and
        /// whether the views are shown at all. Written from the state as it stands, so it is safe to
        /// run twice.
        ///
        /// <para>Also run at the START of a move that opens the pane without switching its tab (see
        /// <see cref="GlideColumns"/>), so the view is up and laid out live as the column grows.
        /// Running it only at the landing would make the pane grow as an empty card, with its content
        /// appearing in one step at the end. Content is not hidden on resize.</para>
        /// </summary>
        private void ApplyLeftViews()
        {
            if (p.Model is not { } m)
                return;

            bool showImport = Tab == LeftTab.Import;
            p.TrackInfoPanel.Visibility = Vis(Tab == LeftTab.TrackInfo);
            p.ImportWorkbench.Visibility = Vis(showImport);

            // Inside Import: the queue's grid + chin while something is queued, the add-music invitation
            // while nothing is.
            p.ImportWorkbench.ShowQueue(m.QueueVisible);
            p.VizBody.Visibility = Vis(Tab == LeftTab.Visualization);

            // The radio is the one left view with a model to be given before it can show anything. It
            // is built HERE, because this is the only place a view is put up. A session restored onto
            // the radio would otherwise show the view with no model behind it, and an unbound pane is a
            // spinner over an empty list that never ends.
            if (Tab == LeftTab.Radio)
                p.EnsureRadioPanel();
            p.RadioBody.Visibility = Vis(Tab == LeftTab.Radio);
            p.SyncLeftPaneBand();   // the band belongs to the view on screen, so it changes with it

            if (Tab == LeftTab.Visualization)
                p.VizBody.EnsureFilled();

            if (Open && Tab == LeftTab.TrackInfo)
                p.UpdateTrackInfo();

            p.LeftViews.Visibility = Vis(Shown && Open);

            // Whether the track info pane is really on screen, which is this class's answer and not the
            // view's: the view stays marked visible inside a left column that has been folded away. The
            // album page paints its facing corner in the pane's colour while it is (see PaneCorner).
            Presentation.Common.PaneCorner.Showing(Shown && Open && Tab == LeftTab.TrackInfo);
            p.LeftPaneBand.Visibility = Shown && Open ? p.LeftPaneBand.Visibility : Visibility.Collapsed;
        }

        /// <summary>The library card's half of <see cref="ApplyViews"/>. Only ever run at the landing: a
        /// library that is switching views makes that swap behind an opacity of nought, between the
        /// fade out and the fade in.</summary>
        private void ApplyRightViews()
        {
            // Which of the card's views is the one showing, home is a fifth, off the rail, and it is
            // the ONE answer the four below and the backdrop are all read from, so there is no way for
            // home to be up with one of them still standing behind it.
            var showing = ShownRight;

            p.LibraryBody.Visibility = Vis(showing == RightTab.Albums);
            p.ArtistsBody.Visibility = Vis(showing == RightTab.Artists);
            p.GenresBody.Visibility = Vis(showing == RightTab.Genres);
            p.PlaylistPanel.Visibility = Vis(showing == RightTab.Playlists);
            p.Home.Visibility = Vis(Stage.HomeLit);

            // And the same answer is given to the playlists view, because one of the things it holds is
            // a clock. The room at the head of an open list draws thirty-two frames a second, and an
            // element is told nothing when an ancestor is collapsed. So a pane hidden behind home would
            // keep drawing for the life of the window. Setting a Visibility raises no event that
            // anything can hook into, so this is a separate line from the one above.
            p.PlaylistPanel.PaneShown(showing == RightTab.Playlists);

            // A playlist left open in the tracklist stays open only while the Playlists view is up;
            // anywhere else the view goes back to its tree, so it comes back the way a view should.
            // Home is not "anywhere else": it covers the card rather than replacing what the pane is
            // reading, and the tracklist is still there to come back to (see ApplyCore's Drilled).
            if (!AtHome && !(RightOpen && Reading == RightTab.Playlists))
                p.PlaylistPanel.ExitDetail();

            // Which detail page the reader is really looking at, which is this class's answer and not
            // the view's, for the same reason as PaneCorner.Showing above: a record's page and an
            // artist's page both stay marked visible inside a library pane that has been switched to
            // another tab or folded away. The window stands in whichever of the two is on screen and
            // dressed (see PageBackdrop); either page says for itself whether it is dressed.
            //
            // This is the answer for a move of the TAB, and it is late by design: it runs at the end of
            // the pane's own move, which is where every view change belongs. Opening and closing a page
            // says the same thing on the press (ShowAlbumDetail, ShowArtistDetail), because a quarter of
            // a second is long enough to watch the window go back to its wallpaper and come off it again.
            // The two agree by the time this runs: it is read off the tab, and the tab is current.
            Presentation.Common.PageBackdrop.Reading(
                showing is null ? null
                : showing == RightTab.Albums ? p.AlbumDetail
                : showing == RightTab.Artists ? (object)p.ArtistDetail
                // A genre's page stands on the colour of its own head photograph and lends it the same
                // way, see GenreDetailView.Lend. Named whether or not it is open: a page that is not
                // says so itself, by not being in PageBackdrop at all.
                : showing == RightTab.Genres ? p.GenreDetail
                // The playlists view is a tab rather than a page, and it is only a page half the
                // time: drilled into a list it is dressed and lends its ground like the other two,
                // and back on its tree it is not open at all. It says which of the two it is for
                // itself (see PlaylistView.Lend and ExitDetail); naming it here only says the
                // reader is looking at it.
                : showing == RightTab.Playlists ? p.PlaylistPanel
                : null);

            // What is now up, for the next move to measure its fade against.
            _leftShowing = LeftSignature;
            _rightShowing = RightSignature;

            // A screen that has just come up may not have been read yet.
            p.ReflectShelfReading();

            // A first-run hint points at a control on one of these views, so which hint can be shown
            // has just changed. Said here because this is the one place a view is put up, and a hint
            // whose target came or went with a view nobody told the shell about is an arrow pointing
            // at the screen before last.
            Shell.ShellPage.Current?.RefreshCoachmarks();
        }


        /// <summary>
        /// The whole move, in three phases on one clock: the old content fades out, the columns take
        /// their new widths, and the new content fades in. The first apply snaps, so the panes do not
        /// animate their own start-up state.
        ///
        /// <para><b>TWO KINDS OF MOVE, and the LIBRARY decides which.</b> A move that switches the
        /// library's view (a tab, a fold, home) keeps the compositor move and the crossfade: that
        /// swap has to happen behind an opacity of zero, and a view nobody can see is not worth laying
        /// out once a frame. Every other width move is a LIVE RESIZE: no clip, no overhang, real column
        /// widths written as often as <see cref="LiveFloorMs"/> allows, so the library re-proportions
        /// its columns and re-chunks its wall as the pane slides. That is what a splitter drag already
        /// does, and it is the same layout write both reach (see <see cref="GlideOnTick"/> and
        /// <see cref="Resize"/>).</para>
        ///
        /// <para>The left pane opening and closing is the second kind, and it does not fade at all: it
        /// is the thing being revealed, and what changed about it is the move itself. A left pane that
        /// is switching TABS still crossfades, on the tick, over a library resizing live beneath it.
        /// </para>
        ///
        /// <para>The order is the point. A view that changed at the old width was watched reflowing into
        /// a box that was still moving; a box that changed size with the wrong thing already in it was
        /// watched doing the same. So the content goes, then the walls move, then the content comes
        /// back, and the swap itself (<see cref="Land"/>) happens at the fold between the two, behind an
        /// opacity of zero, where nothing about it can be seen.</para>
        ///
        /// <para>ONE clock, not three animations chained end to end. A chain has a seam between two
        /// animations and a seam is a frame the move can fall into. On the WinUI head the whole gesture
        /// is one set of composition animations started in one turn and timed off one driver (see <see
        /// cref="PaneGlide"/>), and the columns are written twice for the whole move instead of once a
        /// frame. On the Skia head it is one render tick reading the wall clock against each phase
        /// boundary, so a slow frame shortens a phase instead of pushing the next one back.</para>
        ///
        /// <para>The widths move in PIXELS, on the resolved width: the split's share of the row, or the
        /// content's ceiling, whichever is smaller. Tweening the star split and resolving it each frame
        /// would make the target depend on the ceiling at that moment. A measurement that lands after
        /// the animation has finished (which is when it usually lands, because it waits for the layout
        /// to settle) would move the answer, and the pane would jump to it. Everything that changes the
        /// answer goes through here, so a late measurement is a second, shorter ramp rather than a
        /// snap.</para>
        ///
        /// <para>ONE column moves and the other holds the star, so the two always add up to the row and
        /// nothing is left standing in the middle of it. Which of them moves is <see
        /// cref="FoldingLibrary"/>: normally the pane, with the library keeping the star; while the
        /// library is folding away (or coming back) it is the library, with the pane keeping it. Moving
        /// the pane through a fold is what made the rail snap to the middle of the window on the first
        /// frame and slide back out to the edge. Its column had been pinned to the rail's width while
        /// the pane was still narrow, so for the length of the animation the row was not full.</para>
        /// </summary>
        private void GlideColumns(double toLeft, double toRight)
        {
            _curLeft = toLeft;
            _curRight = toRight;

            var fold = FoldingLibrary;
            var target = fold ? ResolvedRightWidth() : ResolvedLeftWidth();

            if (!_primed)
            {
                _primed = true;
                Land();
                return;
            }

            // Which side is about to show something else, and so has something to fade. A switch that
            // costs no width fades without moving anything.
            //
            // THE LEFT PANE'S VIEW, not its signature. The signature also carries Open and Shown, and
            // those changing IS this move: a pane sliding in or out is not switching what it shows, so
            // it has nothing to cross-fade and slides with its column instead. Only its tab, or the
            // queue appearing under Import, is a switch.
            //
            // Only a pane that had its views UP can switch: a folded or hidden pane shows nothing, so
            // there is nothing to fade out, and a pane opened onto another tab puts the new view up at
            // the start of the move like any other opening (see below).
            var fadeLeft = _leftShowing is { Open: true, Shown: true } left
                && (left.Tab != Tab || left.Queue != (p.Model?.QueueVisible ?? false));
            var fadeRight = _rightShowing is { } right && !right.Equals(RightSignature);
            var fades = fadeLeft || fadeRight;

            // Where this move starts, and the end of whatever was already moving.
            //
            // The tween is detached WITHOUT releasing the wall, and a compositor move is stopped the
            // same way: a move redirected mid-flight (open, then closed again before it lands) must
            // cancel and restart, not stop to spend a full wall reflow between the two animations. With
            // a big wall that reflow is exactly the stall the redirect was dodging. The hold carries
            // over and is spent once, when a move actually lands.
            //
            // A compositor move lays its column out at the WIDER of its two ends for the whole move, so
            // the column cannot say how wide the pane currently reads; only the compositor can, and only
            // once the driver is stopped. See PaneGlide.Stop.
            var wasMoving = Moving;
            DetachWidthTick();
            var from = _glide.Stop() ?? (fold ? p.RightCol.ActualWidth : p.LeftCol.ActualWidth);

            var moves = Math.Abs(from - target) >= 0.5;

            // A pane opening on the tab it already had puts its view up now, so the view is laid out
            // live as the column grows, the same as it is when the pane closes. Left to the landing, the
            // pane grew as an empty card and its content appeared in one step at the end. A pane that is
            // switching tabs still waits: that swap happens behind its fade, at the landing.
            if (moves && !fadeLeft && Shown && Open)
                ApplyLeftViews();

            // A width move that does not switch the library's view is a LIVE RESIZE, not a compositor
            // move behind a clip. The columns are written on every tick, and both sides lay out at the
            // width they actually are. This is what a splitter drag does: the library re-proportions
            // its columns and re-chunks its wall as the pane slides, instead of holding still and
            // settling in one jump at the end. The left pane slides in and out at 30 fps while the
            // right panel resizes its contents.
            //
            // The wall re-flows against a layout that is the real one on every tick, which is the layout
            // a drag gives it too. What IS held for a live resize is the work that rides on that reflow:
            // the cover density, the anchor check, the repeater's cache. See HoldScene.
            //
            // Only the right side is read for this, because that is the side being resized in front of
            // the reader. A library that is switching views (a tab, a fold, home) has to make that swap
            // behind an opacity of zero whatever its width does, and a view nobody can see is not worth
            // laying out once a frame, so that keeps the compositor move. The LEFT pane's own switch
            // does not: its fade runs on the tick as well (see GlideOnTick), over a library that is
            // resizing live underneath it.
            var live = moves && !fadeRight;

            // Home is resized live, like every other card in this column. Taking home off this path was
            // tried twice, and both attempts were rejected. The compositor lays nothing out per frame.
            // That measures well but looks wrong: the screen stands still through the slide, and the
            // content arrives in one step at the end. The reader follows the CONTENT re-proportioning
            // as the pane moves, not the seam travelling. A card that holds still while its edge moves
            // reads as a jump, whatever the frame rate.

            if (!moves && !fades)
            {
                // Nothing to move and nothing to swap, but the star/pixel choice may still need
                // re-writing, and a state re-applied is not made to wait for a show it does not need. A
                // move in flight that this one supersedes has been stopped above; its content is put
                // back here, or it would be left standing at whatever opacity its fade had reached with
                // no clock left to carry it home. (The ordinary case, a queue count ticking over, writes
                // an opacity of 1 over an opacity of 1 and costs nothing.)
                if (wasMoving)
                    ReleaseScene();
                SetViewOpacity(left: true, right: true, opacity: 1);
                Land();
                return;
            }

            // A live resize takes the OTHER hold: the wall's reflow is what it exists to show, so that
            // runs, and what is held is the work that goes with it: the cover density, the
            // anchor check and the repeater's off-screen cache. A move hidden behind a fade holds the
            // reflow itself, as it always did.
            HoldScene(live);

            if (live)
            {
                // A compositor move that this one is taking over from left both cards laid out for a
                // clip that is no longer there: the mover at the wider of its two ends, the card beside
                // it overhanging the row. Undo that, and put the column at the width the move has
                // actually reached, so the first tick carries on from there instead of jumping.
                Undress();
                WritePixels(from, fold);
            }

            // Where the fade-out starts FROM, rather than from 1: a move redirected while an earlier one
            // was still fading would otherwise brighten the content back up before dimming it again.
            // Reading stops the fade (see PaneGlide.OpacityOf), which is why it is read here and not
            // before the exit above.
            var fromLeft = PaneGlide.OpacityOf(p.LeftViews);
            var fromRight = PaneGlide.OpacityOf(p.RightViews);

            // A side this gesture does not fade is at full opacity, whatever a superseded move had left
            // it part-way through: nothing later in this gesture will touch it again.
            if (!fadeLeft)
                PaneGlide.SetOpacity(p.LeftViews, 1);
            if (!fadeRight)
                PaneGlide.SetOpacity(p.RightViews, 1);

            // The phases, as milliseconds from the start. A phase nobody needs is zero long, so the
            // boundaries collapse onto each other and the clock walks straight past it.
            double outEnds = fades ? FadeMs : 0;
            double moveEnds = outEnds + (moves ? GlideMs : 0);
            double inEnds = moveEnds + (fades ? FadeMs : 0);

            var gesture = new Gesture(
                fold, moves, from, target,
                fadeLeft, fadeRight, fromLeft, fromRight,
                new PanePhases(outEnds, moveEnds, inEnds));

            var generation = ++_glideGeneration;
            _landed = -1;
            _due = moveEnds;
            _clock = System.Diagnostics.Stopwatch.StartNew();

            // Which sides are holding at zero waiting for their content. Only the compositor path
            // holds: the tween writes its own fade-in from its own clock, as it always did.
            //
            // A live resize is the tween by choice rather than because the compositor refused it: the
            // whole point of it is that the columns are real on every frame, and a compositor move lays
            // them out once and clips.
            _onCompositor = !live && GlideOnCompositor(generation, gesture);
            _liftLeft = _onCompositor && fadeLeft;
            _liftRight = _onCompositor && fadeRight;
            if (!_onCompositor)
                GlideOnTick(generation, gesture, live ? LiveStepMs : 0, live);

            LandMoveEvenWithoutFrames(generation, inEnds);
        }

        /// <summary>
        /// One move, as decided: which column moves and between which widths, which sides are swapping
        /// content and what they are fading from, and where the three phases end. Both heads read the
        /// same answer, so the two implementations below cannot disagree about what was asked for.
        /// </summary>
        private readonly record struct Gesture(
            bool Fold,
            bool Moves,
            double From,
            double Target,
            bool FadeLeft,
            bool FadeRight,
            double FromLeft,
            double FromRight,
            PanePhases Phases);

        /// <summary>
        /// Hands the move to the system compositor, and lays both columns out once for the whole of it.
        ///
        /// <para>The moving column takes the WIDER of the move's two ends, and the card in the other
        /// column is given a negative margin on its far edge so it is laid out that much wider than its
        /// own column and overhangs the row. Between them the two cards then always have at least as
        /// many pixels as the move needs to show, whichever way it is going, and <see cref="PaneGlide"/>
        /// clips each of them back to the part that is showing. Both are undone at the landing, by <see
        /// cref="Settle"/>.</para>
        ///
        /// <para>The columns are written AFTER the animations are started, and that order is deliberate:
        /// the clips are sized off the cards' live composition size, so they take the new layout as it
        /// arrives rather than a number read here, before the layout pass has run. Nothing renders
        /// between the two, so the cards are never seen overhanging.</para>
        ///
        /// <para>Answers false where the platform will not run a move off the drawing thread, which is
        /// the Skia head. Nothing has been written by then, and the caller keeps its tween.</para>
        /// </summary>
        private bool GlideOnCompositor(int generation, in Gesture g)
        {
            var mover = g.Fold ? p.LibraryPanel : (FrameworkElement)p.LeftPanel;
            var other = g.Fold ? p.LeftPanel : (FrameworkElement)p.LibraryPanel;

            PaneMove? move = g.Moves
                ? new PaneMove(
                    mover, other, p.Splitter,
                    g.Fold ? null : p.RightViews,
                    g.Fold, g.From, g.Target)
                : (PaneMove?)null;
            PaneFade? left = g.FadeLeft ? new PaneFade(p.LeftViews, g.FromLeft) : (PaneFade?)null;
            PaneFade? right = g.FadeRight ? new PaneFade(p.RightViews, g.FromRight) : (PaneFade?)null;

            var started = _glide.Start(
                move, left, right, g.Phases,
                () => LandGlide(generation, onDeadline: false));

            if (!started || !g.Moves)
                return started;

            // The moving column takes the wider of the two ends, and the other card overhangs its own
            // column by the whole of the difference for the whole move. Both are then laid out at their
            // widest, and the one that gives way overhangs on the side the SEAM is on rather than away
            // from it. That is what lets it stay where layout put it for the whole move: PaneGlide cuts
            // it back at the seam, its far edge never moves, and neither does anything standing on that
            // edge. Nothing on this thread touches either card again until the landing.
            var slack = Math.Abs(g.Target - g.From);
            WritePixels(Math.Max(g.From, g.Target), g.Fold);
            other.Margin = g.Fold ? new Thickness(0, 0, -slack, 0) : new Thickness(-slack, 0, 0, 0);

            // ...and the heading's type is held where the overhang is about to put it. Only when the card
            // that gives way is the LIBRARY, which is every move except the fold: through a fold the
            // library is the mover and its whole content is crossfading anyway.
            PinHeading(g.Fold ? 0 : slack);
            return true;
        }

        /// <summary>
        /// The move on a render tick. It writes a column width per frame, and every write is a full
        /// measure and arrange of both columns and everything in them, on the thread that then has to
        /// draw the result.
        ///
        /// <para>Two callers want that, for opposite reasons. The Skia head has nothing else, it is
        /// what both heads had before the compositor took the move over. And a LIVE RESIZE asks for it:
        /// the cost is the re-flow the reader is meant to be watching, which is the same thing a
        /// splitter drag pays on every pointer move.</para>
        /// </summary>
        /// <param name="stepMs">The shortest gap between two width writes, or zero for every frame the
        /// display offers. See <see cref="LiveStepMs"/>.</param>
        private void GlideOnTick(int generation, in Gesture g, double stepMs, bool live)
        {
            var fold = g.Fold;
            var from = g.From;
            var target = g.Target;
            var fadeLeft = g.FadeLeft;
            var fadeRight = g.FadeRight;
            var fromLeft = g.FromLeft;
            var fromRight = g.FromRight;
            var outEnds = g.Phases.OutEnds;
            var moveEnds = g.Phases.MoveEnds;
            var inEnds = g.Phases.InEnds;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double startMs = -1;   // clock starts on the first real frame (a cold frame won't skip a phase)
            var landed = false;

            // How the move actually got delivered, which is the only thing "smooth" means here. A glide
            // is a promise of N milliseconds; what the user sees is the gaps BETWEEN the frames that
            // carried it, and a 240ms move drawn in nine frames with one 70ms hole in the middle is a
            // stutter however close its total is to 240. Counted always, because a diagnostic that has to
            // be switched on is off when the fault happens, and said only when a gap was worth saying.
            var frames = 0;
            double worstGap = 0;
            double lastFrameMs = -1;
            var late = 0;

            // What counts as a frame this move did not get. A rate-capped move asks for one write every
            // stepMs, so its own floor is not a stutter and the bar is that floor plus the same slack a
            // free-running move gets. A move with no cap is barred at the slack alone, which on a 60Hz
            // display is a frame and a bit.
            var bar = stepMs > 0 ? stepMs + FrameGapBarMs : FrameGapBarMs;

            _widthTick = (_, _) =>
            {
                double now = sw.Elapsed.TotalMilliseconds;
                if (startMs < 0)
                    startMs = now;
                double at = now - startMs;

                // Under the floor, and not yet at the landing: this frame is skipped, which is the whole
                // of what the rate cap does. The landing and everything after it always run, or the
                // columns would be left a step short of their target with no tick left to finish them.
                if (stepMs > 0 && lastFrameMs >= 0 && now - lastFrameMs < stepMs && at < moveEnds)
                    return;

                frames++;
                if (lastFrameMs >= 0)
                {
                    var gap = now - lastFrameMs;
                    if (gap > worstGap)
                        worstGap = gap;
                    if (gap > bar)
                        late++;
                }
                lastFrameMs = now;

                if (at < outEnds)
                {
                    var dim = 1 - Ease(at / outEnds);
                    if (fadeLeft)
                        SetViewOpacity(left: true, right: false, fromLeft * dim);
                    if (fadeRight)
                        SetViewOpacity(left: false, right: true, fromRight * dim);
                    return;
                }

                if (at < moveEnds)
                {
                    SetViewOpacity(fadeLeft, fadeRight, 0);
                    WritePixels(from + (target - from) * Move((at - outEnds) / GlideMs), fold);
                    return;
                }

                if (!landed)
                {
                    landed = true;
                    // Flat zero before the swap, in case the frame clock stepped straight over the
                    // fade-out's own last frame and left it part-way down.
                    SetViewOpacity(fadeLeft, fadeRight, 0);
                    LandGlide(generation, onDeadline: false);   // the swap, behind zero opacity
                }

                if (at < inEnds)
                {
                    SetViewOpacity(fadeLeft, fadeRight, Ease((at - moveEnds) / FadeMs));
                    return;
                }

                SetViewOpacity(fadeLeft, fadeRight, 1);
                SayGlide(inEnds, now - startMs, frames, worstGap, late, bar, live, stepMs);
                DetachWidthTick();
            };
            CompositionTarget.Rendering += _widthTick;
        }

        /// <summary>
        /// The width move is over, so the content swap is due. Every landing goes through here: the
        /// compositor's own callback, the tween's landing frame, and the deadline that catches a clock
        /// which stopped delivering. It runs once per move.
        /// </summary>
        /// <param name="onDeadline">True when the deadline had to do it, which means neither clock came
        /// back. It is said on the panes channel, because it is the fault the deadline exists for rather
        /// than an ordinary landing.</param>
        private void LandGlide(int generation, bool onDeadline)
        {
            if (generation != _glideGeneration || generation == _landed)
                return;   // a newer move owns the panes, or this one has already landed
            _landed = generation;

            // Only a compositor move reports its landing. A tween measures the frames it was given
            // instead (see SayGlide), and its landing is a frame of its own animation rather than a
            // callback that can queue behind something.
            if (_onCompositor)
                SayLanding(_due, _clock?.Elapsed.TotalMilliseconds ?? 0, onDeadline);
            Land();

            // The reflow lands on the NEXT turn, not this one: this one still owes the settled layout a
            // measure, and stacking the wall's relayout on top of it is a visible hitch. It gets the
        /// fade-in to happen behind, which is what it is for. It runs at normal priority, not low. At
        /// low priority it waited behind the background work a big wall queues (cover decodes landing),
        /// so the settled layout came late. By enqueue time a newer move may have taken the hold back;
        /// then this
            // release is not ours to spend.
            p.DispatcherQueue.TryEnqueue(() =>
            {
                if (generation == _glideGeneration)
                    ReleaseScene();
            });
        }

        /// <summary>Which move has already landed, so the deadline and the clock that beat it do not
        /// both swap the content.</summary>
        private int _landed = -1;

        /// <summary>The move's own clock and the moment its content swap was due, for <see
        /// cref="SayLanding"/>. Only the compositor path reads them; the tween measures its own
        /// frames.</summary>
        private System.Diagnostics.Stopwatch? _clock;
        private double _due;

        /// <summary>Which head is running the move in flight. It decides what gets measured, and it is
        /// a property of the MOVE rather than of the build: the compositor path answers false for a
        /// window it will not animate, and the tween takes over for that move only.</summary>
        private bool _onCompositor;

        /// <summary>
        /// A gap between two frames of a tween glide longer than this is a frame the move did not get.
        ///
        /// <para>Twenty milliseconds, not sixteen: a display at sixty asks for a frame every 16.7 and
        /// the reading is taken on the UI thread rather than off the presentation clock, so a few
        /// milliseconds of slack keeps an on-time frame from being counted as a missed one. Anything
        /// past this is a frame's worth of nothing on screen while the pane was supposed to be moving.
        /// </para>
        /// </summary>
        private const double FrameGapBarMs = 20;

        /// <summary>
        /// What a tween move cost, said once when it is over.
        /// </summary>
        /// <remarks>The total is carried as well as the worst gap, because the two fail differently. A
        /// move that took 260 ms in fifteen even frames is fine, and one that took 245 ms in six is
        /// not.
        ///
        /// <para><b>A LIVE RESIZE ALWAYS REPORTS ITS LINE</b>, whether or not a frame was missed. The
        /// others only report when one was. A compositor move produces no per-frame reading at all, so
        /// this line is the only way to see whether a live move is smooth. A live resize is made of
        /// frames this thread delivers, so it can be counted. One line per pane press on a ring is
        /// worth having. The freeze mode is in the line, so two runs cannot be mixed up.</para>
        ///
        /// <para><b>It counts WRITES, not frames the reader saw.</b> The tick writes a width and
        /// returns. The layout and the paint it causes happen after that. When they overrun the
        /// display's budget, two writes are presented as one. So a high count here is necessary but not
        /// sufficient. The word in the line is "writes", so that a reader does not take it for what was
        /// seen.</para>
        /// </remarks>
        private static void SayGlide(
            double askedMs, double tookMs, int frames, double worstGapMs, int late, double barMs,
            bool live, double stepMs)
        {
            if (late == 0 && !live)
                return;

            var freeze = RPM.Desktop.Presentation.Main.Library.AlbumWallView.Freeze;

            RPM.Core.Diagnostics.Scope.Said(
                "panes",
                $"{(live ? $"live@{(stepMs > 0 ? $"{stepMs:F0}ms" : "display")}" : "glide")} " +
                $"{askedMs:F0}ms asked, {tookMs:F0}ms taken, {frames} writes, " +
                $"{late} over {barMs:F0}ms, worst gap {worstGapMs:F0}ms" +
                (live && freeze != RPM.Desktop.Presentation.Main.Library.AlbumWallView.WallFreeze.None
                    ? $", wall frozen: {freeze}" : ""));
        }

        /// <summary>
        /// What a compositor move cost, said once when it lands.
        /// </summary>
        /// <remarks>
        /// The frame-gap reading above does not apply to a compositor move and cannot be taken: the move
        /// is not made of frames this thread delivers, and there is no per-frame work here for a slow
        /// frame to miss. What can still go wrong is the LANDING. The content swap is a callback on this
        /// thread, and a thread that is wedged runs it late, which leaves both panes faded out past the
        /// point where the new content should have been up. So that is what is measured, and a landing
        /// that only the deadline made is always said, because it means neither clock came back at all.
        /// </remarks>
        private static void SayLanding(double dueMs, double atMs, bool onDeadline)
        {
            var lateBy = atMs - dueMs;
            if (!onDeadline && lateBy <= FrameGapBarMs)
                return;

            RPM.Core.Diagnostics.Scope.Said(
                "panes",
                $"land {dueMs:F0}ms due, {atMs:F0}ms taken, {lateBy:F0}ms late" +
                (onDeadline ? ", on the deadline" : ""));
        }

        /// <summary>
        /// The curve the WIDTH moves on.
        /// </summary>
        /// <remarks><b>Cubic ease-out is the wrong curve for a wide panel, and the frames show
        /// why.</b> Ease-out starts at three times the average speed. The pane travels about 450 points
        /// in 240 ms, so its first frames each carry 80 points, and its last twenty carry one. Read off
        /// the screen, the steps ran 32, 27, 22, 17, 14, 14, 6, 4, 2 and 1 points, at a steady 15 ms
        /// apart. The rate was not the fault. A frame that moves a hard-edged panel 80 points is read
        /// as a jump however punctually it arrives, and the crawl at the end is read as the move
        /// already being over.
        ///
        /// <para>Cubic ease-IN-OUT peaks at twice the average instead of three times, and starts from
        /// nothing, so there is no first jump to read. The same distance is spent over the whole move
        /// rather than in its first fifth.</para>
        ///
        /// <para>The fades keep the ease-out: they are opacity over 110ms, where there is no distance to
        /// jump and a fade that starts slowly reads as a fade that has not started.</para>
        /// </remarks>
        private static double Move(double t)
        {
            var x = Math.Clamp(t, 0, 1);
            return x < 0.5
                ? 4 * x * x * x
                : 1 - (Math.Pow((-2 * x) + 2, 3) / 2);
        }

        /// <summary>The curve the FADES move on: cubic ease-out, matching the drawers. The compositor is
        /// given the same shape as a bezier; see <see cref="PaneGlide"/>.</summary>
        private static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        /// <summary>Writes the panes' content opacity now. Only the sides that are actually changing are
        /// touched: a library that is staying exactly as it is must not dim because the pane on the
        /// other side of the window switched views.</summary>
        private void SetViewOpacity(bool left, bool right, double opacity)
        {
            if (left)
                PaneGlide.SetOpacity(p.LeftViews, opacity);
            if (right)
                PaneGlide.SetOpacity(p.RightViews, opacity);
        }

        /// <summary>
        /// The end of a move, however it got there: the columns take their resting widths and the views
        /// change to match. EVERY exit goes through here: <see cref="LandGlide"/>, a move that turned out
        /// to cost no width, and the first apply. A landing that forgot to call this would leave the
        /// panes at the right size showing the wrong thing, with nothing left to come and put it right.
        /// </summary>
        /// <summary>Whether a pane is holding at zero opacity waiting for its new content to be laid
        /// out. See <see cref="Land"/>.</summary>
        private bool _liftLeft;

        private bool _liftRight;


        /// <summary>The swap, and then the content coming back up, in that order, with the layout paid
        /// for in between.
        /// </summary>
        /// <remarks><b>The fade-in is started HERE, not timed from the start of the gesture.</b> If
        /// one animation ran down, flat and up, its fade-in would begin before anybody knew how long
        /// the incoming view takes to lay out. On the album page that layout takes about 286 ms, so the
        /// fade-in would be well under way before the page had been measured. A half-built page would
        /// then settle under a rising opacity.
        ///
        /// <para><c>UpdateLayout</c> here is what makes the holding worth anything: it forces the
        /// measure and arrange of the view that has just been shown, while it is still behind an opacity
        /// of zero, so the fade-in reveals a page that is already finished. It is the expensive line in
        /// this method and it is expensive on purpose, that cost is being spent either way, and this is
        /// the only place to spend it where nobody is watching.</para>
        ///
        /// <para>Every route that lands a move comes through here, the deadline included. A pane left
        /// holding at zero with no fade-in is an app with no content in it, so there is no path that may
        /// skip this.</para>
        /// </remarks>
        private void Land()
        {
            Settle();
            ApplyViews();
            RPM.Desktop.Diagnostics.SwitchDiag.Mark("views");

            if (_liftLeft || _liftRight)
                LiftWhenLaidOut();
        }

        /// <summary>
        /// Brings the panes back up on the first layout pass after the swap.
        /// </summary>
        /// <remarks><b>Waiting for a layout rather than forcing one.</b> Forcing a layout with
        /// <c>UpdateLayout()</c> is the right idea with the wrong tool. It forces the whole page, not
        /// only the view that changed. On the first record opened in a run there is a great deal of
        /// pending work, and the app stops completely for a moment before the page appears.
        ///
        /// <para><c>LayoutUpdated</c> is the same wait without the shove: it fires when a layout pass
        /// has finished, so the pane comes up on the first frame where its content is measured, and a
        /// view that had nothing to do comes up on the very next one. The work is the same work; this
        /// simply does not insist on all of it happening in one turn.</para>
        ///
        /// <para>One shot, and it unhooks itself. A handler left on <c>LayoutUpdated</c> runs on every
        /// pass for the life of the page, which is the sort of thing that is never noticed and is never
        /// free.</para>
        /// </remarks>
        private void LiftWhenLaidOut()
        {
            EventHandler<object>? hook = null;
            hook = (_, _) =>
            {
                p.LayoutUpdated -= hook;
                Lift();
            };
            p.LayoutUpdated += hook;
        }

        /// <summary>The panes back up, whichever of them was holding at nought. Safe to call twice: the
        /// flags are cleared here and a pane already up animates from where it is.</summary>
        private void Lift()
        {
            if (!_liftLeft && !_liftRight)
                return;

            RPM.Desktop.Diagnostics.SwitchDiag.Mark("laid out");

            if (_liftLeft)
                PaneGlide.FadeIn(p.LeftViews, FadeMs);
            if (_liftRight)
                PaneGlide.FadeIn(p.RightViews, FadeMs);

            _liftLeft = false;
            _liftRight = false;
        }

        /// <summary>
        /// Puts the panes at their resting state when neither clock comes back.
        ///
        /// <para>A move ends on its own clock: the compositor calls back when its driver finishes, and
        /// the tween's last frame detaches, settles and hands the wall back. If neither happens (the
        /// window occluded, the frame clock paused, a compositor that stopped producing frames) none of
        /// that runs. The columns are left at the widths the move was laid out against, which belong to
        /// nothing; and because <see cref="WatchWidth"/> stands down while a move is in flight, even
        /// resizing the window can no longer put them right. Repeated clicks on the pane's own button
        /// can leave it stuck open at an arbitrary width, with the library squeezed into the
        /// corner.</para>
        ///
        /// <para>So the resting state does not depend on any clock arriving. It is where the panes ARE
        /// after the move's time is up, however it was drawn getting there, and that includes the
        /// content, which a stalled clock would otherwise have left faded out and never swapped.</para>
        /// </summary>
        private async void LandMoveEvenWithoutFrames(int generation, double totalMs)
        {
            // Comfortably past the last phase: a slow frame or two must not trip this, only a stalled
            // clock.
            await Task.Delay(TimeSpan.FromMilliseconds(totalMs + 150));

            // A newer move owns the panes now, and carries its own deadline.
            if (generation != _glideGeneration)
                return;

            // By here the gesture is over however it was run, so everything it was still holding comes
            // back. All of it is idempotent: the ordinary run has already landed and this only writes
            // what is already true.
            StopWidthGlide();
            LandGlide(generation, onDeadline: true);
            SetViewOpacity(left: true, right: true, opacity: 1);
        }

        /// <summary>Whether a move is in flight on either head. It is what <see cref="WatchWidth"/>
        /// stands down for and what tells a redirect there was something to redirect.</summary>
        private bool Moving => _widthTick is not null || _glide.Running;

        /// <summary>The move on the compositor. One per conductor, since one conductor runs one move at a
        /// time and a redirect replaces what it is carrying.</summary>
        private readonly PaneGlide _glide = new();

        /// <summary>Unhooks the tween only. <see cref="StopWidthGlide"/> is the outside world's stop, a
        /// splitter drag taking over, and releases the wall with it; the glide's own paths manage the
        /// hold themselves.</summary>
        private void DetachWidthTick()
        {
            if (_widthTick is null)
                return;
            CompositionTarget.Rendering -= _widthTick;
            _widthTick = null;
        }

        /// <summary>
        /// Holds the library heading's left-anchored type where it will SETTLE, for the length of a move.
        /// </summary>
        /// <remarks>The card that gives way is laid out overhanging its column ON THE SEAM SIDE and
        /// clipped back there, which is what keeps its far edge, and the rail standing on it, perfectly
        /// still. Whatever is anchored at the OTHER end pays for that: the panel's name and its counts
        /// sit at the card's left, so they are laid out a whole move's width left of where they will
        /// end up and the clip eats them as the pane opens. Without this they would vanish for the
        /// length of every move and come back in one frame at the end. That is the pop this whole area
        /// exists to remove.
        ///
        /// <para>A CONSTANT translation, not an animated one, and on the compositor rather than in
        /// layout. The overhang is one number for the whole move, so this is a write at each end and no
        /// per-frame work; a margin instead would re-arrange the card, which is the expense the move
        /// exists to avoid. Two constants composing is also not the trap that two animated translations
        /// cancelling each other was: a fixed offset rasterises to the same pixel every frame.</para>
        ///
        /// <para>Only the two left-anchored elements, never the whole heading. The chrome beside them is
        /// right-aligned against an edge that does not move, so it is already where it settles; carrying
        /// it along would push it past the card's right edge and under the clip.</para>
        /// </remarks>
        private void PinHeading(double x)
        {
#if RPM_WINUI
            Pin(p.LibraryTitleBox, x);
            Pin(p.LibrarySubheading, x);

            static void Pin(FrameworkElement el, double x)
            {
                // Before the visual is taken, or the property is not there to write and nothing says why.
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(el, true);
                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el).Properties
                    .InsertVector3("Translation", new System.Numerics.Vector3((float)x, 0, 0));
            }
#else
            _ = x;
#endif
        }

        /// <summary>Stops a move on either head and hands the wall back. The composition animations come
        /// off here; the layout they were run against is undone by <see cref="Settle"/>, which every
        /// caller of this runs in the same turn.</summary>
        private void StopWidthGlide()
        {
            DetachWidthTick();
            _glide.Rest();
            ReleaseScene();
        }

        /// <summary>
        /// Freezes what must not act while a move is in flight, and hands it back at the landing. The
        /// frost plate always; the album wall in one of two ways, depending on which kind of move this
        /// is.
        ///
        /// <para><b>A switch</b> holds the wall's reflow outright. The layout in force during that move
        /// is not the one the panes settle at (the moving column is at the wider of its two ends and
        /// the card beside it deliberately hangs off the window), so anything that reads that layout and
        /// keeps the answer is reading a shape that exists for 240ms. The wall would re-chunk its rows
        /// against it; the frost would cut a plate against it, clamped at the window's edge and
        /// stretched back across the card (see <see cref="FrostPlate.Hold"/>).</para>
        ///
        /// <para><b>A live resize</b> is the opposite: the layout in force IS the settled one on every
        /// frame, and the wall re-flowing against it is the whole point. What is held there is the work
        /// that goes with the reflow and does not have to happen until the end, the cover density, the
        /// anchor check, the repeater's off-screen cache. See
        /// <see cref="Library.AlbumWallView.BeginLiveResize"/>, which carries what each of them costs.
        /// The frost is still held: a plate captured mid-move is a photograph of a layout that lasts a
        /// quarter of a second either way.</para>
        ///
        /// <para>Held by this conductor rather than by either of their own counters, so that a move
        /// redirected mid-flight passes the hold to the move that replaces it instead of spending a full
        /// wall reflow and a frost capture between the two. Only a landing, or an outside stop, spends
        /// it.</para>
        /// </summary>
        /// <param name="live">True for a live resize, where the wall is MEANT to re-flow and only its
        /// passengers are held. See <see cref="Library.AlbumWallView.BeginLiveResize"/>.</param>
        private void HoldScene(bool live)
        {
            // The two holds are not the same hold, so one cannot be left standing while the other is
            // taken: a redirect from a switch into a live resize has to hand the reflow back before it
            // asks the wall to start re-flowing again.
            if (_holdsScene && _heldLive != live)
                ReleaseScene();

            if (_holdsScene)
                return;

            _holdsScene = true;
            _heldLive = live;

            if (live)
                p.AlbumWall.BeginLiveResize();
            else
                p.AlbumWall.HoldReflow();

            // Home is the other card that can be on screen for a live move, and it has its own
            // passengers. It is told either way: what it does with a switch is nothing.
            if (live && AtHome)
                p.Home.BeginLiveResize();

            FrostPlate.Current.Hold(FrostHold.PaneMove);
        }

        private void ReleaseScene()
        {
            if (!_holdsScene)
                return;
            _holdsScene = false;

            if (_heldLive)
                p.AlbumWall.EndLiveResize();
            else
                p.AlbumWall.ReleaseReflow();

            p.Home.EndLiveResize();   // a no-op unless a live move was held over home

            FrostPlate.Current.Release(FrostHold.PaneMove);
        }

        private bool _holdsScene;

        /// <summary>Which of the two holds <see cref="_holdsScene"/> is, so the release gives back what
        /// was taken.</summary>
        private bool _heldLive;

        /// <summary>
        /// The pane's share of the row under the current split, in px. Zero before the row has been laid
        /// out.
        ///
        /// <para>Measured from the splitter's COLUMN, not from the splitter: the grip is 8 px wide
        /// inside a 4,0 margin, so its column is 16 px. If only the grip were subtracted, the answer
        /// would be 8 px too wide. The tween would then end a few px off where the star layout puts the
        /// pane, and <see cref="Settle"/> would correct it with a visible pinch.</para>
        /// </summary>
        private double Share()
        {
            var room = p.PanelsGrid.ActualWidth - Seam();
            if (room <= 0 || _curLeft + _curRight <= 0)
                return 0;
            return room * _curLeft / (_curLeft + _curRight);
        }

        /// <summary>What the seam takes out of the row: its column, or nothing at all when there is no
        /// left column for it to be a seam to. The same gutter whatever the two columns hold, see the
        /// splitter's visibility in <see cref="ApplyCore"/>, which is what this reads.
        ///
        /// <para>Falls back to the grip's own declared width and margin when the column has not been
        /// measured, which is exactly the case that matters: an open beginning while the splitter is
        /// still collapsed from being closed. Its ActualWidth is zero there, so the target would be
        /// computed against a row this much too wide and the animation would end on a pinch.</para>
        /// </summary>
        private double Seam()
        {
            if (p.Splitter.Visibility == Visibility.Collapsed)
                return 0;

            var measured = p.SplitterCol.ActualWidth;
            if (measured > 0)
                return measured;

            var grip = p.Splitter.Width;
            return (double.IsNaN(grip) ? 0 : grip) + p.Splitter.Margin.Left + p.Splitter.Margin.Right;
        }

        /// <summary>Where the pane is headed: its share of the row, or the ceiling its content asked for,
        /// whichever is smaller, or, on Import, that view's own remembered width; or, with the library
        /// folded away, everything the library's rail does not want. Closed is zero.</summary>
        private double ResolvedLeftWidth() =>
            RailOnly ? RailWidth
          : _curLeft <= 0 ? 0
          : !RightOpen ? Math.Max(0, p.PanelsGrid.ActualWidth - Seam() - RightRailWidth)
          : ImportSized ? Math.Min(ImportWidth, MaxLeftWidth)
          : Math.Min(Share(), MaxLeftWidth);

        /// <summary>Where the LIBRARY is headed: its rail's width while it is folded away, else whatever
        /// the pane beside it is not taking. The two are one answer read from either end, so the fold's
        /// tween and the pane's cannot disagree about where the seam lands.</summary>
        private double ResolvedRightWidth() =>
            !RightOpen
                ? RightRailWidth
                : Math.Max(0, p.PanelsGrid.ActualWidth - Seam() - ResolvedLeftWidth());

        /// <summary>
        /// Whether the tween in flight is the library folding away or coming back, rather than the pane
        /// opening or closing.
        ///
        /// <para>True while the library is folded, and true again on the way back out of it. It is read off
        /// the column, which is only ever a fixed width while the library is standing at its rail's. The
        /// return trip has to be recognised for what it is or the rail would slide out of the window's
        /// edge for the length of it.</para>
        /// </summary>
        private bool FoldingLibrary => !RightOpen || p.RightCol.Width.IsAbsolute;

        /// <summary>Mid-tween: one column is a fixed width for the frame and the other keeps the star, so
        /// the two still fill the row. <paramref name="fold"/> says which is which (see
        /// <see cref="FoldingLibrary"/>).</summary>
        private void WritePixels(double px, bool fold)
        {
            px = Math.Max(0, px);
            if (fold)
            {
                p.LeftCol.Width = new GridLength(_curLeft <= 0 ? 1 : _curLeft, GridUnitType.Star);
                p.RightCol.Width = new GridLength(px);
                return;
            }

            p.LeftCol.Width = new GridLength(px);
            p.RightCol.Width = new GridLength(_curRight, GridUnitType.Star);
        }

        /// <summary>At rest. Stars while the pane's share is under the ceiling, so the pair keeps
        /// filling the row and a window resize stays proportional, and a fixed width once it is over,
        /// with the library (still a star) taking everything the pane no longer wants. Which of the two
        /// is in force depends on the window, so this is re-run whenever it changes size (see
        /// WatchWidth).
        ///
        /// <para>It is also where a compositor move's layout is undone, because that layout is the one
        /// thing standing between the panes and their resting state: the moving column was written at
        /// the wider of the move's two ends and the card beside it was given a margin that overhangs the
        /// row (see <see cref="GlideOnCompositor"/>). The clips and slides come off in the same turn, so
        /// nothing renders between the two states.</para>
        /// </summary>
        private void Settle()
        {
            _glide.Rest();
            Undress();

            // The library folded away is a width and nothing else (its rail's), and the pane beside it
            // takes the row: it asked for the room, and the star is how it keeps getting it as the window
            // changes size.
            if (!RightOpen)
            {
                p.LeftCol.Width = new GridLength(_curLeft <= 0 ? 1 : _curLeft, GridUnitType.Star);
                p.RightCol.Width = new GridLength(RightRailWidth);
                return;
            }

            // The rail alone is a width and nothing else: it has no share to keep, and a star here would
            // hand the folded pane a slice of the row to stand in the middle of.
            if (RailOnly)
            {
                p.LeftCol.Width = new GridLength(RailWidth);
                p.RightCol.Width = new GridLength(_curRight, GridUnitType.Star);
                return;
            }

            // Import is a width, not a share: it stays where it was put while the window changes size
            // around it, and the library (a star) takes whatever is left.
            if (ImportSized && _curLeft > 0)
            {
                p.LeftCol.Width = new GridLength(Math.Min(ImportWidth, MaxLeftWidth));
                p.RightCol.Width = new GridLength(_curRight, GridUnitType.Star);
                return;
            }

            var share = Share();

            p.LeftCol.Width = _curLeft <= 0 || share <= 0 || share <= MaxLeftWidth
                ? new GridLength(_curLeft, GridUnitType.Star)
                : new GridLength(MaxLeftWidth);
            p.RightCol.Width = new GridLength(_curRight, GridUnitType.Star);
        }

        /// <summary>
        /// Takes off the layout a compositor move is run against, and nothing else: the overhanging
        /// margins, the heading's pin, and the inset the view inside the card was given.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="Settle"/> because a live resize needs this half without the other:
        /// it takes over from a compositor move that was stopped part-way, and that move's card is still
        /// laid out at the wider of its two ends with the card beside it hanging off the row. Settling
        /// as well would write the resting columns and draw one frame at the move's target.
        ///
        /// <para>The view's own inset is the one margin a move writes per frame rather than once (see
        /// PaneGlide.Widen). Left standing it is a permanent band of bare card between the wall and the
        /// rail, because the last thing the tick wrote was a move that had not finished.</para>
        /// </remarks>
        private void Undress()
        {
            p.LeftPanel.Margin = default;
            p.LibraryPanel.Margin = default;
            PinHeading(0);
            p.RightViews.Margin = default;
        }

        /// <summary>Re-decides stars-or-ceiling as the window changes size. Without it the pane would
        /// keep whichever of the two it was given when it opened: pinned at its ceiling on a window
        /// since shrunk (leaving it a much bigger share than the split says), or star-sized on one
        /// since widened, which is the growth the ceiling exists to stop.
        /// </summary>
        public void WatchWidth()
        {
            p.PanelsGrid.SizeChanged += (_, _) =>
            {
                // A move in flight owns the columns: the tween writes them every frame, and a compositor
                // move has laid them out at the widths its clips are cut against. Settling under either
                // would put the panes somewhere the animation is not.
                if (!Moving)
                    Settle();   // the width follows the pointer dragging the window
            };
        }

        private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── "Add to playlist" from the library grid ──

    /// <summary>Fills the library row's "Add to playlist" submenu with the current playlists (indented by
    /// depth) plus a "New playlist…" entry, rebuilt each open, since the tree changes.</summary>
    private void BuildAddToPlaylistSubmenu(MenuFlyoutSubItem sub)
    {
        sub.Items.Clear();
        EnsurePlaylistPanel();

        // Now playing is a playlist like the others as far as this menu is concerned. It just happens
        // to be the live queue, so the tracks land at the bottom of it and nothing is interrupted.
        var queue = new MenuFlyoutItem { Text = Strings.Loc.Get("Playlists_NowPlaying"), FontSize = 12 };
        queue.Click += (_, _) => AddLibrarySelectionToQueue();
        sub.Items.Add(queue);
        sub.Items.Add(new MenuFlyoutSeparator());

        if (_playlistModel is { } pm)
            foreach (var (id, name, depth) in pm.PlaylistPickerEntries())
            {
                var entry = new MenuFlyoutItem { Text = new string(' ', depth * 2) + name, FontSize = 12 };
                var playlistId = id;
                entry.Click += async (_, _) => await AddLibrarySelectionToPlaylistAsync(playlistId);
                sub.Items.Add(entry);
            }

        if (sub.Items.Count > 0)
            sub.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = Strings.Loc.Get("Playlists_NewPlaylistFrom"), FontSize = 12 };
        create.Click += async (_, _) => await NewPlaylistFromLibrarySelectionAsync();
        sub.Items.Add(create);
    }

    /// <summary>"Add to playlist ▸ Now playing": appends to the bottom of the live queue, playing or
    /// not. Distinct from playing a track, which rebuilds the queue from that track's record.</summary>
    private void AddLibrarySelectionToQueue()
    {
        var rows = LibraryActionRows();
        if (rows.Count == 0 || _playlistModel is null)
            return;
        _playlistModel.AddToQueue(rows.Select(r => r.ToWantedTrackInfo()));
    }

    private async Task AddLibrarySelectionToPlaylistAsync(long playlistId)
    {
        var rows = LibraryActionRows();
        if (rows.Count == 0 || _playlistModel is null)
            return;

        // Read before the question: the selection can change while the dialog is up.
        var tracks = rows.Select(r => r.ToWantedTrackInfo()).ToList();
        var answer = await AlreadyInPlaylistPrompt.AskAsync(XamlRoot, PlaylistNameOf(playlistId),
            _playlistModel.AlreadyIn(playlistId, tracks), tracks.Count);
        if (answer is not { } whenAlreadyThere)
            return;

        _playlistModel.AddTracksToPlaylist(playlistId, tracks, whenAlreadyThere);
    }

    private async Task NewPlaylistFromLibrarySelectionAsync()
    {
        var rows = LibraryActionRows();
        if (rows.Count == 0 || _playlistModel is null || XamlRoot is null)
            return;

        var name = await PromptPlaylistNameAsync();
        if (name is null)
            return;

        var db = App.Services.GetRequiredService<LibraryService>().Db;
        var id = db.CreatePlaylist(null, name);
        // A playlist made on the line above holds nothing, so there is nothing to ask about.
        _playlistModel.AddTracksToPlaylist(id, rows.Select(r => r.ToWantedTrackInfo()), AlreadyInPlaylist.AddAnother);
    }

    // ── "Add to playlist" from an album wall card ──

    private async Task AddFolderTracksToPlaylistAsync(string folder, long playlistId)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || _playlistModel is null)
            return;

        var tracks = FolderTracksInDiscOrder(library, folder);
        var answer = await AlreadyInPlaylistPrompt.AskAsync(XamlRoot, PlaylistNameOf(playlistId),
            _playlistModel.AlreadyIn(playlistId, tracks), tracks.Count);
        if (answer is not { } whenAlreadyThere)
            return;

        _playlistModel.AddLibraryTracks(playlistId, tracks, whenAlreadyThere);
    }

    private async Task NewPlaylistFromFolderAsync(string folder)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || _playlistModel is null || XamlRoot is null)
            return;

        var name = await PromptPlaylistNameAsync();
        if (name is null)
            return;

        var id = library.Db.CreatePlaylist(null, name);
        // A playlist made on the line above holds nothing, so there is nothing to ask about.
        _playlistModel.AddLibraryTracks(id, FolderTracksInDiscOrder(library, folder), AlreadyInPlaylist.AddAnother);
    }

    /// <summary>A playlist's name as the add-to-playlist menus list it, for the title of the question
    /// asked when it already holds a song being added.</summary>
    private string PlaylistNameOf(long playlistId) =>
        _playlistModel?.PlaylistPickerEntries().FirstOrDefault(e => e.Id == playlistId).Name ?? "";

    /// <summary>An album's rows the way the album reads: disc 1 whole, then disc 2. The mirror hands
    /// them back in file-name order, which interleaves a multi-disc set's two track 1s.</summary>
    private static IReadOnlyList<RPM.Core.Library.LibraryTrack> FolderTracksInDiscOrder(LibraryService library, string folder)
        => RPM.Core.Library.AlbumTracklist.InDiscOrder(
            library.Db.FolderTracks(folder), t => t.DiscNumber, t => t.TrackNumber);

    private async Task<string?> PromptPlaylistNameAsync()
    {
        if (XamlRoot is null)
            return null;
        var initial = Strings.Loc.Get("Playlists_DefaultPlaylist");
        var box = new TextBox { Text = initial, SelectionStart = 0, SelectionLength = initial.Length };
        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            Strings.Loc.Get("Playlists_NewPlaylist"),
            box,
            primaryText: Strings.Loc.Get("Common_OK"),
            closeText: Strings.Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);
        if (answer != ContentDialogResult.Primary)
            return null;
        var name = box.Text?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

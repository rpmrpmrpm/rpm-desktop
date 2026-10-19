using RPM.Core.Cloud;
using RPM.DataGrid.Model;
using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace RPM.Desktop.Presentation.Main;

public sealed partial class MainPage : Microsoft.UI.Xaml.Controls.Page, Presentation.Import.IImportWorkbenchHost
{
    /// <summary>Tells the home screen whether Pegasus covers it (see <c>HomeView.Covered</c>).</summary>
    internal void CoverHome(bool covered) => Home.Covered = covered;

    public MainPage()
    {
        InitializeComponent();

        // The two rails and the Visualizations gallery are separate controls. They report what was clicked,
        // and the panes' conductor decides what that means.
        WirePaneViews();

        // `TableView` does not accept `ItemsSource` through `{Binding}` on Uno. Assign it from the code
        // behind, and only after the control is `Loaded`: an earlier assignment is ignored.
        Loaded += (_, _) => AttachSources();

        // The first-run hints point at two controls on this page. The shell that draws them cannot know when
        // these controls exist, because the navigation to this page happens before its rail and header are
        // laid out.
        Loaded += (_, _) => Shell.ShellPage.Current?.RefreshCoachmarks();




        DataContextChanged += (_, _) => AttachSources();

        Loaded += (_, _) =>
        {
            // The deck drives the one shared RPaMp model, whether or not the skin is the view that is up. The
            // page also keeps the transport, because the art strip must follow the same player, and the
            // deck's `DeckChanged` tells it that a queue has moved on.
            var transport = App.Services.GetRequiredService<RPaMpService>().Transport;
            Deck.AttachTransport(transport);

            // A rip can finish at any time: with the popup closed, or with the app on another page. So the
            // folder it staged is collected here and is not returned through the dialog's result. Taking it
            // clears it, so it is imported only once.
            var rips = App.Services.GetRequiredService<RipJobService>();
            rips.Finished += OnRipFinished;
            Unloaded += (_, _) => rips.Finished -= OnRipFinished;
            ImportFinishedRip();

            // A folder dragged onto the window from Finder. On Windows this arrives as the XAML `Drop` event
            // that the root grid handles. On macOS Uno has no drag destination, so the paths come from our
            // own AppKit one (see `MacFolderDrop`).
            if (OperatingSystem.IsMacOS())
            {
                MacFolderDrop.Dropped += OnNativeFolderDrop;
                Unloaded += (_, _) =>
                {
                    // Checked again here. The handler runs later on its own, and the platform check above
                    // does not apply inside it. The analyzer checks each body separately.
                    if (OperatingSystem.IsMacOS())
                        MacFolderDrop.Dropped -= OnNativeFolderDrop;
                };
            }

            void OnRipFinished() => DispatcherQueue.TryEnqueue(ImportFinishedRip);

            // Taken only when there is a queue to take it into. A rip that finishes before the page has its
            // model is picked up by the next `Loaded` and is not dropped.
            async void ImportFinishedRip()
            {
                if (Model is not null && rips.TakeCompleted() is { } folder)
                    await ScanSourcesAsync([folder]);   // a rip that produced nothing says so, like any other scan
            }

            // The artwork strip is on the shell, as a band across the top of the window above the player, but
            // this page drives it. The shell's display lives longer than the page, so the manager is built
            // once and reused. A new one on each navigation would leave the old one without an owner.
            if (ShellPage.Current?.Art is not { } art)
                return;

            if (art.Controller is ArtworkManager existing)
            {
                _art = existing;
            }
            else
            {
                // The stage keeps the crop models. The manager owns everything else (fetch, rank, stored
                // order) and is the one path to the art.
                art.AttachModels(App.Services.GetRequiredService<SaliencyService>(),
                                 App.Services.GetRequiredService<FaceService>());
                _art = new ArtworkManager(
                    art,
                    App.Services.GetRequiredService<ArtworkService>(),
                    App.Services.GetRequiredService<LibraryService>(),
                    App.Services.GetRequiredService<BackdropPreselector>(),
                    () => App.Services.GetRequiredService<Services.Shell.SettingsService>().HeaderArtistPhoto);
                art.Controller = _art;
            }

            var playback = App.Services.GetRequiredService<PlaybackService>();
            playback.Changed += OnPlaybackChanged;
            // The deck is the other half of the answer, and it changes on its own: when the last track of a
            // queue plays out, the engine has already said "nothing playing" and says nothing more.
            transport.DeckChanged += OnPlaybackChanged;
            Unloaded += (_, _) =>
            {
                playback.Changed -= OnPlaybackChanged;
                transport.DeckChanged -= OnPlaybackChanged;
            };
            OnPlaybackChanged();

            // The engine raises `Changed` off the UI thread, and the display is a UI object. The record turns
            // only while a track plays (not while paused). The art stays on the track loaded in the player
            // until it stops: pausing must not remove the cover, and a playing track always wins over the
            // grid selection.
            void OnPlaybackChanged() => DispatcherQueue.TryEnqueue(() =>
            {
                // What the player has loaded is not the same question as what the sound device is doing at
                // this moment. A queue advances on the same event that says the last track ended, so between
                // two records the engine has nothing loaded for a few milliseconds. If the art treated that
                // as a stop, it would discard the whole strip and build it again one song later: the
                // backdrop, the cover, the record and the text of the mini player. The deck covers that gap
                // (see `RPaMpModel.Deck`), so a track change looks like a track change: the text changes and
                // the playhead returns to the start.
                var path = playback.PlayingPath ?? transport.Deck?.FilePath;

                art.SetPlaying(path is not null && !playback.IsPaused);

                // A source that plays a succession of things (a mix, a radio station) says what it is on and,
                // where there is no file to read art from, where its picture is. Anything else is one file,
                // and the pipeline reads its tags itself: from the disk when the file is there, and from the
                // library row when it is not. See `ArtworkManager.RunShowAsync`. Where it is one file, the
                // file is used, and never the label of a source that has no track yet, because the pipeline
                // would look for tags in that label.
                _art?.SetPlaying(playback.NowPlaying is { } now
                    ? new TrackRef(now.Title, now.Artist, now.Album, now.Path, now.ArtUrl)
                    : (playback.PlayingRecord ?? transport.Deck?.FilePath) is { } p
                        ? new TrackRef(null, null, null, p)
                        : null);
            });
        };

#if DEBUG
        // Developer shortcut: `--queue-folder <path>` scans a folder into the import queue at startup. A
        // queue can then be inspected without the native folder panel, which is not in the visual tree and
        // cannot be clicked by a test harness. The queue is a TEMP table that is rebuilt on every run, and
        // nothing is written to disk until Import is asked for, so this changes no files.
        if (Environment.GetCommandLineArgs() is { } cli
            && Array.IndexOf(cli, "--queue-folder") is var at and >= 0
            && at + 1 < cli.Length
            && Directory.Exists(cli[at + 1]))
        {
            var folder = cli[at + 1];
            Loaded += async (_, _) =>
            {
                await Task.Delay(1500);
                await ScanSourcesAsync([folder]);
            };
        }

        // Developer shortcut: `MD_DEBUG_DETAILS=1` opens the Track details dialog for the first queued file,
        // so the dialog can be checked in a screenshot without manual clicks.
        if (RPM.Core.Diagnostics.DevSwitch.Read("MD_DEBUG_DETAILS") == "1")
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(1200);
                if (Model?.DebugFirstQueueRow() is { } row && XamlRoot is not null)
                    await Dialogs.TrackDetailsDialog.ShowAsync(XamlRoot, row);
            };
        }
#endif
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // `DataContext` is inherited from the shell (which holds the `ShellModel`), so it is never null here.
        // Check the model type and not null, or the page keeps the shell's context and every binding on it
        // silently resolves to nothing.
        if (DataContext is not MainModel)
            DataContext = Microsoft.Extensions.DependencyInjection.ActivatorUtilities
                .CreateInstance<MainModel>(App.Services);

        // The gallery can open this page already looking at one record. The album page is a view inside the
        // library card, so it can be opened only after this page is built.
        if (e.Parameter is Gallery.GalleryAlbumRequest request)
        {
            if (IsLoaded)
                NavigateToAlbumPage(request.Folder, AlbumFrom.Collection);
            else
                Loaded += OpenRequestedAlbum;

            void OpenRequestedAlbum(object sender, RoutedEventArgs args)
            {
                Loaded -= OpenRequestedAlbum;
                NavigateToAlbumPage(request.Folder, AlbumFrom.Collection);
            }
        }
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (Model is { } m)
            m.PropertyChanged -= Model_PropertyChanged;
        Model?.Dispose();   // only our own model, never the inherited `ShellModel`
    }

    /// <summary>
    /// Redraws the library table and the import queue so that they show the playing track.
    ///
    /// <para>Raising a change on the rows is not enough for the library table. The source's page cache holds
    /// the rows it has read, but the bindings on screen are attached to the panel's containers. When a page
    /// is evicted and fetched again, new row objects go into the cache while the container on screen keeps
    /// the old one, so a change raised on the cache reaches an object that nothing listens to. A redraw does
    /// not depend on which row object a container holds.</para>
    /// </summary>
    private void MarkPlayingOnScreen()
    {
        // Both grids read the playing mark from the row when they draw it, so one repaint each is enough.
        LibraryGridView.Invalidate();
        ImportWorkbench.MarkPlayingChanged();
    }

    private void AttachSources()
    {
        if (!IsLoaded || Model is not { } m)
            return;

        WatchShelfReading(m);

        // The time taken by each stage of the attach. The reveal waits for home's reading, and home's reading
        // cannot start until this method releases the thread, so any slow stage here delays the end of the
        // splash screen. Read it with: `rpmscope log --channel boot`
        var attach = System.Diagnostics.Stopwatch.StartNew();
        var stage = System.Diagnostics.Stopwatch.StartNew();
        double Took() { var ms = stage.Elapsed.TotalMilliseconds; stage.Restart(); return ms; }
        var spent = new System.Text.StringBuilder();
        void Spent(string what) => spent.Append(spent.Length > 0 ? ", " : "").Append(
            FormattableString.Invariant($"{what} {Took():F0}"));

        // Home's reading comes first.
        //
        // The splash screen goes away when the first screen has content (`RevealGate`, `FirstScreen`), and
        // the first screen is home. Nothing else in this method is on screen while home is shown, because
        // home covers both panes. So every stage below only delays the one thing that the reveal waits for.
        //
        // The reading is started here and is not left to `RestoreHome` one turn later. The reading moves to a
        // worker thread at its first await, so the stages below overlap it instead of coming before it.
        //
        // `RestoreHome` still runs and still shows home. It finds `Stale` already false and does not read a
        // second time. Starting the read and showing the screen are two separate steps.
        //
        // The one loader that home's reading needs.
        //
        // Home reads its cards and then asks `CoverLoader` for each sleeve. If the loader is still null,
        // `FillCoverAsync` returns before it tells the card anything, and the card keeps the generated sleeve
        // it was built with. Nothing throws, so the failure is silent: every album and song on the home
        // screen shows a placeholder. The radio rows still look right, because a station logo comes down a
        // different path.
        //
        // The loader is set here and not by moving `AttachAlbumGrid` above this line. That method is where
        // home's loaders are set, but it also touches `AlbumWall`, `AlbumList`, `AlbumDetail` and
        // `ArtistDetail`, and reaching those controls realises them. That costs about 1,350ms on a cold start
        // and cancels the gain of this ordering. Here the line is a delegate assignment to a plain private
        // method and costs nothing.
        //
        // `AttachAlbumGrid` sets it again below. It is the same method group, so the value is the same. The
        // second assignment stays because that is where a reader looks for home's wiring, and a loader that
        // is set in one place and read in another is easy to break.
        //
        // Not set here, on purpose: `BandSources`, `PlayTrack`, `TrackMenuHooks` and `CloudBadgeLoader`. The
        // reading does not use any of them. They answer a press or draw a corner mark, and each needs a
        // control or a local variable of `AttachAlbumGrid`. They are set with the rest of the wiring, which
        // finishes seconds before the splash screen goes away.
        Home.CoverLoader = LoadAlbumCoverAsync;

        if (Home.Stale)
            Home.Load();
        Spent("home asked");
        if (!ReferenceEquals(LibraryGridView.Source, m.LibraryTable.Source))
        {
            // The filter handler is set before the source: assigning `ItemsSource` clears the filters, and
            // the default handler would build its value lists by walking a source that is virtual on purpose,
            // which means reading a million rows from disk to fill one dropdown.
            AttachLibraryGrid(m.LibraryTable);
        }

        Spent("grid");

        m.PropertyChanged -= Model_PropertyChanged;
        m.PropertyChanged += Model_PropertyChanged;
        m.RevealQueueRow -= RevealQueueRow;
        m.RevealQueueRow += RevealQueueRow;
        m.PlayingMoved -= MarkPlayingOnScreen;
        m.PlayingMoved += MarkPlayingOnScreen;
        ImportWorkbench.Host = this;
        ImportWorkbench.Attach();

        // Restore where the interface was when the app was last closed: the pane, the search, the favourites
        // and the sort of the table. This is done before the shelf is fed below, so the shelf is built once
        // and is already narrowed.
        RestoreSession();
        Spent("session");

        // The views that nobody is looking at, run on a Low turn.
        //
        // These are the left panel's tree, the album grid, the artists, the genres and the empty hint. None
        // of them is on screen while home is, because home covers both panes and the app opens on home.
        //
        // The album grid is in this group, but the one loader that home's reading needs is set above. When
        // you move anything into this group, ask whether the screen that is on screen reads anything that it
        // sets. Asking only whether the view itself is on screen is not enough.
        //
        // All of these read the collection, and the library's reads are serialised: one gate, one reader at a
        // time. So they do not only occupy the drawing thread ahead of home. They take the gate that home's
        // own reading needs. On the boot channel, with these first and home asked afterwards, the attach took
        // 1,567ms (the tree took 1,168ms). With home asked first and these left in place, the attach fell to
        // 662ms, but the album grid rose from 194ms to 1,884ms and home's reading from 977ms to 4,222ms,
        // because the two queued on each other. Starting the reading earlier gains nothing while the thread
        // that applies it spends the same gate on views behind the splash screen.
        //
        // They must still run automatically: the library row menu's "Add to playlist" needs the playlist tree
        // whether or not that view has ever been opened (see `MainPage.Panes`). A Low turn is a frame or two
        // on an idle thread. It is not a lazy build on first use.
        //
        // They wait for the first screen and not only for a Low turn, because a Low turn during the opening
        // is not an idle thread: it is the next gap in home's own work, and these would use the read gate in
        // it. Run on a Low turn during the read, the tree took 247ms instead of 1,168ms, but home's reading
        // rose from about 1,000ms to 2,470ms and the screen came up no sooner. Moving the work does not help,
        // because the gate allows one reader at a time.
        Services.Shell.AfterFirstScreen.Run(() =>
        {
            var rest = System.Diagnostics.Stopwatch.StartNew();
            SyncQueueColumns();
            var treeMs = rest.Elapsed.TotalMilliseconds;
            AttachAlbumGrid();
            AttachArtists();
            AttachGenres();
            RefreshLibraryEmptyHint();

            if (RPM.Core.Diagnostics.Scope.On)
                RPM.Core.Diagnostics.Scope.Said(
                    "boot",
                    FormattableString.Invariant(
                        $"views attached {rest.Elapsed.TotalMilliseconds:F0}ms, tree {treeMs:F0}"));
        });
        Spent("views asked");

        // Whose shelf is on screen, and whether that computer is here. Both can change without any action by
        // the user (a laptop is closed, or comes back), so they are read again on the library's change and on
        // the sharing service's change.
        if (App.Services.GetService<Services.Library.LibraryService>() is { } sharedLibrary)
        {
            sharedLibrary.Changed += OnSharedShelfChanged;
            Unloaded += (_, _) => sharedLibrary.Changed -= OnSharedShelfChanged;
        }

        if (App.Services.GetService<Services.Sharing.SharingService>() is { } sharingForBand)
        {
            sharingForBand.Changed += OnSharedShelfChanged;
            Unloaded += (_, _) => sharingForBand.Changed -= OnSharedShelfChanged;
        }

        // The player's own change: the music stopped for lack of a connection, or started again. That is the
        // band's loudest message, and the player knows it, not the library.
        if (App.Services.GetService<Services.Audio.PlaybackService>() is { } playerForBand)
        {
            playerForBand.SourceLostChanged += OnSourceLostChanged;
            Unloaded += (_, _) => playerForBand.SourceLostChanged -= OnSourceLostChanged;
        }

        // Who the lender is arrives on its own schedule. The band is drawn when the shelf opens, and the
        // profile behind the account that the shelf came from is fetched over the internet and arrives later,
        // often after that. Without this, the band keeps the name it could work out at the time, which is the
        // machine's name, while the share button next to it has learned the person's name. The event is
        // raised off the thread pool, so it goes through the queue like the others.
        if (App.Services.GetService<Services.Sharing.PeerFaces>() is { } facesForBand)
        {
            facesForBand.Changed += OnFacesChanged;
            Unloaded += (_, _) => facesForBand.Changed -= OnFacesChanged;
        }

        // This machine's own reach: the connection going down or coming back is the band's other subject. It
        // is measured on a worker (see `NetworkReach`), so it arrives through the queue like the others.
        var reachForBand = RPM.Core.Net.NetworkReach.Shared;
        reachForBand.Changed += OnReachChanged;
        Unloaded += (_, _) => reachForBand.Changed -= OnReachChanged;

        void OnReachChanged(RPM.Core.Net.Reach _) => DispatcherQueue.TryEnqueue(SyncLibraryBand);

        void OnFacesChanged() => DispatcherQueue.TryEnqueue(SyncLibraryBand);

        void OnSourceLostChanged() => DispatcherQueue.TryEnqueue(SyncLibraryBand);

        // The cloud marks follow the band. On a borrowed shelf a cloud mark says how the music arrives, and
        // one of the two things that decide that is whether the lender's computer is answering. When it stops
        // or starts answering, every mark on screen is out of date.
        void OnSharedShelfChanged() => DispatcherQueue.TryEnqueue(() =>
        {
            SyncLibraryBand();
            RefreshCloudMarks();
        });

        SyncLibraryBand();
        Spent("band");

        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said(
                "boot",
                FormattableString.Invariant(
                    $"attach {attach.Elapsed.TotalMilliseconds:F0}ms: ") + spent);

        DumpLayout("attach");
        SizeChanged += (_, _) => DumpLayout("page-size-changed");
        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => DumpLayout("after-layout"));
    }

    /// <summary>
    /// Scrolls the grid to a row that moved. Recognition re-files a track under the artist it turned out to
    /// be, and in a large queue that makes the row leave the screen without any sign.
    ///
    /// <para>This uses the grid's own row scroll and not the list's `ScrollIntoView`. `ScrollIntoView`
    /// reaches its target by filling the panel towards it one row at a time, building a container and its
    /// template for every row on the way, and a re-filed track in a large queue moves far. On a queue of
    /// 27,000 rows the interface stopped for as long as it ran, and garbage collection took half of every
    /// second, which emptied the audio buffer and stopped the music. The grid moves the offset instead and
    /// builds one viewport where it lands.</para>
    /// </summary>
    private void RevealQueueRow(int index)
    {
        if (Model is not { } m || index < 0 || index >= m.QueueTree.Count)
            return;

        ImportWorkbench.RevealQueueRow(index);
    }

    private void Model_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainModel.QueueVisible) or nameof(MainModel.QueueVisibility))
            SyncQueueColumns();
        else if (e.PropertyName is nameof(MainModel.LibraryEmptyVisibility))
            RefreshLibraryEmptyHint();
        else if (e.PropertyName is nameof(MainModel.SearchText))
            RememberSearch();   // the box comes back with what was in it
        // The counts, and whether the queue can be worked on at all. The second is not implied by the first:
        // a scan ending changes no count, and the chrome of the workbench (above all the tag panel) must come
        // back when it does.
        else if (e.PropertyName is nameof(MainModel.QueueChipCounts) or nameof(MainModel.CanTickQueue))
            RefreshQueueBar();
    }

    /// <summary>The queue changed, so the workbench reads its own chrome again: the chip counts, the lists,
    /// the enabled state of the bar and what the tag panel is editing. The page only knows that something
    /// changed.</summary>
    private void RefreshQueueBar() => ImportWorkbench.Refresh();

    /// <summary>The empty-library hint of the data table. The wall and the album list each have their own, so
    /// this one is shown only on the table. Otherwise two hints would stack as a double icon.</summary>
    private void RefreshLibraryEmptyHint()
    {
        var empty = Model?.LibraryEmptyVisibility == Visibility.Visible;
        LibraryEmptyHint.Visibility = empty && _view == LibraryViewMode.Table
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // The left column hosts the whole rail (Track info, Import and so on). `PaneConductor` in
    // `MainPage.Panes.cs` owns its opening and closing and the widths of both columns. This method keeps the
    // existing call sites working.
    private void SyncQueueColumns() => SyncLeftPanel();


    /// <summary>
    /// Heals a page that was arranged before the navigation region had a height. Every `Grid` row then
    /// arranges to zero and the content piles up at the top (the grid arrives collapsed, and only a window
    /// resize puts it right). This is a race, so it does not always happen. When it does, the page measures
    /// itself again against the real constraint.
    /// </summary>
    private void DumpLayout(string when)
    {
        var collapsed = ActualHeight <= 1 || PanelsGrid.ActualHeight <= 1;

        if (collapsed)
        {
            Console.WriteLine(
                $"[grid-diag {when}] COLLAPSED — page={ActualWidth:F0}x{ActualHeight:F0} " +
                $"panels={PanelsGrid.ActualWidth:F0}x{PanelsGrid.ActualHeight:F0} " +
                $"host={(Parent as FrameworkElement)?.ActualWidth:F0}x{(Parent as FrameworkElement)?.ActualHeight:F0} " +
                "→ healing");

            try
            {
                (Parent as FrameworkElement)?.InvalidateMeasure();
                InvalidateMeasure();
                UpdateLayout();
            }
            catch { /* best-effort */ }
        }
    }

    private MainModel? Model => DataContext as MainModel;

    /// <summary>The artwork strip. It is owned by the shell, as the top band of the window. The shell is
    /// built before any page is navigated into it, so this is never null while the page is alive.</summary>
    private ArtDisplay Art => ShellPage.Current!.Art;

    /// <summary>The shell calls this on each frame while the art drawer slides (between stopped and playing). The
    /// content below the drawer moves by <paramref name="delta"/>, so each grid is scrolled by the same
    /// amount to keep the row of a given item on the same screen line. The scroll is clamped at the ends of
    /// the range: near the top there is nothing above to reveal, so the pin cannot be exact there.</summary>
    public void CompensateScroll(double delta)
    {
        ImportWorkbench.CompensateScroll(delta);
        LibraryGridView.ScrollBy(delta);
        Pin(AlbumWall?.ScrollHost, delta);
        Pin(AlbumList?.ScrollHost, delta);

        static void Pin(ScrollViewer? sv, double delta)
        {
            if (sv is null || sv.ScrollableHeight <= 0)
                return;
            var target = Math.Clamp(sv.VerticalOffset + delta, 0, sv.ScrollableHeight);
            if (Math.Abs(target - sv.VerticalOffset) > 0.01)
                sv.ChangeView(null, target, null, disableAnimation: true);
        }
    }

    /// <summary>The first `ScrollViewer` inside a control's template (a `TableView` keeps its own private
    /// one). It is found by walking the visual tree and not by template part name.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null)
            return null;
        if (root is ScrollViewer sv)
            return sv;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindScrollViewer(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }


    // `TableView.SelectedItems` is not bindable, so selection changes are pushed into the model. (The queue
    // table has none of this: its ticks are its selection.) Selection is deliberately not an artwork cue. The
    // strip, the record and the track lines show what is playing and nothing else, so the art moves only when
    // playback does.
    /// <summary>
    /// The chosen rows, handed to the model.
    ///
    /// <para>Only the rows whose pages are held are included: a select-all over a million tracks is one run
    /// in the grid and must not become a million objects here. What the model does with a selection that it
    /// cannot see completely is its own concern.</para>
    /// </summary>
    private void LibrarySelectionChanged()
    {
        if (Model is not { } m || LibraryGridView.Source is not SqlGridSource source)
            return;

        var rows = new List<LibraryTableRow>();
        foreach (var i in LibraryGridView.Selection.Rows())
        {
            if (source.Peek(i) is LibraryTableRow row)
                rows.Add(row);
        }

        m.SetLibrarySelection(rows);
    }

    /// <summary>The one thing in charge of the art strip: every change of what it shows goes through it.
    /// Resizing the strip by dragging is done by the shell (the strip is the window's header band, above the
    /// player).</summary>
    private ArtworkManager? _art;

    // Recognition needs a rpm account. Asking for the account is the gate, and the entry is not disabled with
    // an explanation in a banner: when signed out, the login dialog opens with the reason, and the
    // recognition runs as soon as sign-in succeeds.
    private Task<bool> EnsureSignedInAsync() =>
        Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ActionRequired"));

    // Panel splitter: the column resize is driven by the pointer. (`Thumb.DragDelta` is unreliable in
    // Uno-Skia, so this follows the `DragResizer` approach.)

    private const double MinPanelWidth = 220;
    private bool _dragging;
    private bool _splitterHovered;
    private double _dragStartX, _startLeftWidth, _startRightWidth;

    private void Splitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStartX = e.GetCurrentPoint(PanelsGrid).Position.X;
        _startLeftWidth = LeftCol.ActualWidth;
        _startRightWidth = RightCol.ActualWidth;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    // The grip's edge, the same as the art splitter's: lit under the pointer, and kept lit for the whole
    // drag. A pointer that leaves the 8px bar during a drag is still dragging it, and a handle that went dim
    // while being pulled would look as if it was released. As with the art splitter, the pill is shown only
    // while the pointer is on it: unlit means gone, not faint.
    /// <summary>Whether the startup show has already run. It is static because it belongs to the app's
    /// launch, and the page is built again on every navigation back to the library.</summary>
    private static bool _gripDebuted;

    /// <summary>The grip's debut: it is shown for the same five seconds as the art splitter's (`ShellPage`
    /// owns the timing), counted from the first time the left pane opens and not from startup. There is no
    /// splitter when there is one panel: with the pane closed the bar is collapsed, and a debut then would
    /// show a handle to nobody.</summary>
    internal void DebutSplitterGrip()
    {
        if (_gripDebuted)
            return;
        _gripDebuted = true;
        ShowSplitterGrip(shown: true, lit: false);
        Services.Shell.Ticker.Once(DispatcherQueue, TimeSpan.FromSeconds(ShellPage.GripDebutSeconds), () =>
        {
            if (!_splitterHovered && !_dragging)
                ShowSplitterGrip(shown: false, lit: false);
        });
    }

    /// <summary>Shown and lit are two different things, the same as for the art splitter. The pointer on the
    /// bar (or a drag in progress) gives the bright edge, and the startup debut shows the handle at rest: the
    /// barely visible white it has when nothing points at it.</summary>
    private void ShowSplitterGrip(bool shown, bool lit)
    {
        SplitterGrip.Stroke = GripEdge.Brush(lit);
        SplitterGrip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Splitter_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _splitterHovered = true;
        ShowSplitterGrip(shown: true, lit: true);
    }

    private void Splitter_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _splitterHovered = false;
        if (!_dragging)
            ShowSplitterGrip(shown: false, lit: false);
    }

    private void Splitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        var dx = e.GetCurrentPoint(PanelsGrid).Position.X - _dragStartX;
        // Clamp so that neither panel is smaller than the minimum. The total of the pair stays constant. The
        // pane's own maximum is the far end of the same clamp: beyond it the split would be written and then
        // ignored, so the grip stops there and does not slide on with nothing following it.
        var widest = Math.Min(_startRightWidth - MinPanelWidth, Panes.MaxLeftWidth - _startLeftWidth);
        dx = Math.Clamp(dx, MinPanelWidth - _startLeftWidth, Math.Max(MinPanelWidth - _startLeftWidth, widest));
        // Keep both columns proportional (star) so that the pair keeps filling the row when the window is
        // resized. The conductor is the only writer of widths: it follows the drag without animation and
        // remembers this split.
        ResizeLeftPane(_startLeftWidth + dx, _startRightWidth - dx);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        ShowSplitterGrip(_splitterHovered, _splitterHovered);
        Panes.CommitWidth();   // the width the drag ended on is the one that is remembered
        e.Handled = true;
    }

    // RPaMp: queue or play the selected (or right-clicked) library tracks in the player.

    private TrackRow? _contextLibraryRow;

    /// <summary>Fills the row menu for whichever row it was opened on.</summary>
    private void PrepareLibraryContextMenu(LibraryTableRow? row)
    {
        _contextLibraryRow = row?.Row;

        // Someone else's collection is on screen. The three editors rewrite files and the playlist submenu
        // writes rows, and neither the files nor the rows belong to this machine. So only the play actions
        // remain: what to play, and where.
        var borrowed = App.Services.GetRequiredService<Services.Library.LibraryService>().IsPeerView;

        // What the selection is decides which editors are on the menu. One track is a file. Several tracks
        // are a change to apply across files. Every track of one record is a record.
        var rows = LibraryActionRows();
        CtxLibEditTrack.Visibility = !borrowed && rows.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        CtxLibEdit.Visibility = !borrowed && rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CtxLibEditAlbum.Visibility = !borrowed && WholeAlbumSelected(rows) is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        CtxLibAddPlaylist.Visibility = borrowed ? Visibility.Collapsed : Visibility.Visible;

        // The three repair actions follow the same rule as the editors: never on a borrowed shelf, because
        // all three rewrite files that do not belong to this machine.
        //
        // Recognition takes any number of rows from anywhere. It asks each file what it is, one at a time,
        // and a scatter of badly named tracks from nine records is exactly what it is meant for. The cut
        // takes exactly the row under the cursor: it turns one file into many, and "split these nine files"
        // is not an action to offer.
        var repair = borrowed ? Visibility.Collapsed : Visibility.Visible;
        CtxLibRepairSeparator.Visibility = repair;
        CtxLibRecognize.Visibility = repair;

        // The release lookup is the one action that does not work across folders. It matches a listing to
        // rows by disc and track number, and those number a track within one record. Track 3 of one album and
        // track 3 of another are not two candidates for one row of a pressing's listing. They are two
        // records, and only one of them was looked up. The action is removed from the menu and not greyed
        // out, because a selection that spans folders is a different question and not a partly usable case.
        CtxLibNameTracks.Visibility = !borrowed && AlbumFolderOf(rows) is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Offered on a single row, and enabled only if that row looks like a record in one file. This is the
        // same behaviour as the queue's own entry, where an enabled entry for the wrong row would open a
        // window that could only say no.
        CtxLibSplitAlbum.Visibility = !borrowed && rows.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        CtxLibSplitAlbum.IsEnabled = _contextLibraryRow is { } one && LooksLikeAlbumFile(one);

        // Delete acts on the same rows as every other entry here: the selection, or the right-clicked row
        // when it is not part of one. The wording says which it will be: a selection is "Delete selected",
        // and a single row is the plain "Delete" that the record's menu and the song's menu have.
        //
        // The three entries under it are filled here and not in markup, because which of them is enabled
        // depends on the rows in hand: where their copies are, and whether this machine is set to send a
        // deleted cloud copy straight back up. One builder serves all three surfaces, so a listener who
        // learns the submenu on the wall finds the same one in the table.
        CtxLibDeleteSeparator.Visibility = repair;
        CtxLibDelete.Visibility = repair;
        DeleteMenu.Fill(
            CtxLibDelete,
            DeleteOfferFor(rows),
            Loc.Get(rows.Count > 1 ? "Delete_MenuSelected" : "Delete_Menu"),
            MenuFa,
            () => DeleteTracks(DeleteReach.Local, LibraryActionRows().Select(r => r.FilePath).ToList()),
            () => DeleteTracks(DeleteReach.Cloud, LibraryActionRows().Select(r => r.FilePath).ToList()),
            () => DeleteTracks(DeleteReach.Everywhere, LibraryActionRows().Select(r => r.FilePath).ToList()));

        // Folders act on everything under them, so both actions stay enabled either way.
        if (!borrowed)
            BuildAddToPlaylistSubmenu(CtxLibAddPlaylist);
    }

    private void AddToNowPlaying_Click(object sender, RoutedEventArgs e) => SendLibraryToPlayer(play: false);
    private void PlayNow_Click(object sender, RoutedEventArgs e) => SendLibraryToPlayer(play: true);

    // Library rows that the context actions act on: the selection, or the right-clicked row when it is not
    // part of the selection.
    private List<TrackRow> LibraryActionRows()
    {
        if (Model is not { } m)
            return new List<TrackRow>();
        var rows = m.SelectedLibraryRowsSnapshot().ToList();
        if (_contextLibraryRow is { } cr && !rows.Contains(cr))
            rows = new List<TrackRow> { cr };
        return rows;
    }

    /// <summary>The Font Awesome solid face used for the table's own menu icons. The markup gets it from the
    /// `FaMenuIcon` style. The submenu built in code must name it.</summary>
    private static readonly FontFamily MenuFa =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    /// <summary>
    /// Which of the three Delete entries the table offers for the rows in hand.
    ///
    /// <para>The answer is read from the mirror and not from the rows: a <see cref="TrackRow"/> is the review
    /// workbench's idea of a file and has no cloud state, and the answer must be the one that is true at the
    /// moment of the right-click.</para>
    /// </summary>
    private static DeleteOffer DeleteOfferFor(IReadOnlyList<TrackRow> rows)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (rows.Count == 0 || !library.IsOpen || library.IsPeerView || library.Root is not { } root)
            return DeleteOffer.None;

        var relative = new List<string>(rows.Count);
        foreach (var row in rows)
        {
            var path = Path.GetRelativePath(root, row.FilePath).Replace(Path.DirectorySeparatorChar, '/');
            if (!path.StartsWith("..", StringComparison.Ordinal))
                relative.Add(path);
        }

        var (anyLocal, anyCloud) = library.Db.CloudSpread(relative);
        return new(anyLocal, anyCloud,
            App.Services.GetRequiredService<SettingsService>().CloudMode == CloudSyncMode.All);
    }

    /// <summary>
    /// Delete from the keyboard: the same act on the selection, which is where a listener goes first when a
    /// row is highlighted.
    ///
    /// <para>It uses the selection only, never a right-clicked row, because there was no right-click. It does
    /// nothing while a cell is being edited or the table is otherwise using the key. The grid is read-only
    /// here, so Delete reaches this method unchanged (see `TableView.OnKeyDown`, which handles F2, Escape,
    /// Space and the navigation keys and leaves Delete alone).</para>
    /// </summary>
    private void DeleteLibrarySelection()
    {
        if (Model is not { } m)
            return;

        var paths = m.SelectedLibraryRowsSnapshot().Select(r => r.FilePath).ToList();
        if (paths.Count == 0)
            return;

        // This is the recoverable reach. Delete on the keyboard is a reflex, and a reflex must not be able to
        // reach the cloud copy. The other two reaches have their own wording, a click and a confirmation.
        DeleteTracks(DeleteReach.Local, paths);
    }

    /// <summary>
    /// The album folder that these rows are all of (every track that the mirror lists under it, none
    /// missing), or null when they are not a whole record.
    ///
    /// <para>"All in one folder" is not enough alone: eleven of a record's twelve tracks are a selection.
    /// Opening the album editor on them would write the record's fields to eleven files and leave the twelfth
    /// behind, which is the split that this arrangement exists to prevent. The count must match.</para>
    /// </summary>
    private static string? WholeAlbumSelected(IReadOnlyList<TrackRow> rows)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (rows.Count == 0 || !library.IsOpen || library.Root is not { } root)
            return null;

        string? folder = null;
        foreach (var row in rows)
        {
            if (Path.GetDirectoryName(row.FilePath) is not { Length: > 0 } dir)
                return null;
            var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
                return null;   // outside the library: not a folder the mirror knows
            if (folder is null)
                folder = relative;
            else if (!string.Equals(folder, relative, StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return folder is not null && library.Db.FolderTracks(folder).Count == rows.Count ? folder : null;
    }

    /// <summary>The record the selection turned out to be, in its own editor.</summary>
    private async void CtxLibEditAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;
        var rows = LibraryActionRows();
        if (WholeAlbumSelected(rows) is not { } folder)
            return;

        // The editor's lookup can go on to name the tracks. Applying it writes and re-files at once, which is
        // a second write in the same session as this window's own OK. This is safe only because the write
        // gives each row the path its file actually landed at (`MassEdit.WriteToLibrary` to
        // `TrackRow.FiledAt`). Without that, the rows that this window still holds would name files that had
        // moved, and its OK would skip every one of them without saying so.
        var touched = await AlbumEdit.EditAsync(
            XamlRoot, LibraryFolderPath(folder) ?? folder, rows,
            wantArt => LookUpReleaseForRowsAsync(rows, wantArt, LibraryTrackStep(rows, ticked: false)));
        foreach (var f in touched.Append(folder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AlbumWall.RefreshAlbum(f);
            AlbumList.RefreshAlbum(f);
        }
    }

    /// <summary>One track: the full per-file editor, the same window that the import queue opens for a single
    /// row. It is written straight into the file and re-filed by the naming template, which is what the mass
    /// edit does for many.</summary>
    private async void CtxLibEditTrack_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;
        if (LibraryActionRows() is not [var row])
            return;

        // The library knows which record this file is on, so the editor gets the release lookup. One track is
        // the most common thing to correct, and this editor would otherwise be the only one that could not
        // ask a catalogue.
        if (await Dialogs.TrackDetailsDialog.ShowAsync(
                XamlRoot, row,
                // No track step: one file is what this window edits, field by field.
                wantArt => LookUpReleaseForRowsAsync([row], wantArt, Dialogs.Models.TrackMetadataStep.None),
                albumFolder: Path.GetDirectoryName(row.FilePath),
                // The second source: a stray file usually belongs to a record already on the shelf.
                pickAlbum: wantArt => PickLibraryAlbumAsync(SeedFor(row), wantArt)) is not { } picked)
            return;

        // The details window writes nothing to disk. It fills in the row it was given, which is all the
        // import queue needs because those files move at import. A library file is already where it belongs,
        // so the wanted values of the row must be carried down the same path as a mass edit: tags written,
        // the file re-filed by the naming template, the mirror row re-pointed.
        var info = row.ToWantedTrackInfo() with { TagsEdited = true };

        // Art that the window set or cleared is carried in the `TrackInfo` for the file's own tags. It is
        // named here as well, so that the folder's cover follows, as in a mass edit. The two pictures that a
        // tag cannot carry (the full-size scan and the disc's own label) come back on the result, because
        // only the caller knows which folder they belong beside.
        var edit = new Dialogs.MassEditResult(
            new Dictionary<Dialogs.TagFieldKind, string>(), info.PendingArt, info.PendingArtMime,
            picked.FolderArtBytes);

        var touched = (await MassEdit.WriteToLibraryAsync([row], edit, _ => info)).ToList();

        // Anything that the user switched the album marker on for is also applied to the rest of the record.
        // This is its own pass over the sibling tracks, because this window edited one file: the marker means
        // the listener says "the others are wrong about this too", which is a different edit on different
        // rows.
        touched.AddRange(await ApplyToAlbumAsync(row, info, picked.ApplyToAlbum));

        foreach (var folder in touched.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AlbumWall.RefreshAlbum(folder);
            AlbumList.RefreshAlbum(folder);
        }
    }

    private async void CtxLibEdit_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
            return;
        var rows = LibraryActionRows();
        if (rows.Count == 0)
            return;

        // One row can still reach here from a keyboard or an out-of-date menu. It gets the window that fits.
        if (rows.Count == 1)
        {
            CtxLibEditTrack_Click(sender, e);
            return;
        }

        // The wall reuses its cards by folder, so the albums that these rows belong to must be told even
        // though the edit came from the tree (see `AlbumWallView.RefreshAlbum`).
        //
        // Tracks edited from the tree get the same release lookup that the album menu offers: they are the
        // same files and the same tags, and a selection that happens to be a whole record is the usual way to
        // correct one. The lookup searches on what the rows already say.
        foreach (var folder in await MassEdit.EditLibraryRowsAsync(
                     XamlRoot, rows,
                     // No track step, for the same reason as in the album editor: the sweep writes on OK by
                     // reading each file again, and a fill written before that would move the files away from
                     // the rows it still holds.
                     wantArt => LookUpReleaseForRowsAsync(
                         rows, wantArt, Dialogs.Models.TrackMetadataStep.None)))
        {
            AlbumWall.RefreshAlbum(folder);
            AlbumList.RefreshAlbum(folder);
        }
    }

    private void SendLibraryToPlayer(bool play)
    {
        if (LibraryMenuTracks() is { Count: > 0 } tracks)
            App.Services.GetRequiredService<RPaMpService>().AddTracks(tracks, play);
    }

    /// <summary>"Play next": the selection is inserted directly under the playing track, so it comes up next
    /// without cutting anything short.</summary>
    private void CtxPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (LibraryMenuTracks() is { Count: > 0 } tracks)
            App.Services.GetRequiredService<RPaMpService>().InsertNext(tracks);
    }

    /// <summary>
    /// "Start DJ here" on library rows: the queue becomes the set and the DJ mixes it.
    ///
    /// <para>One row is a record: the DJ gets that track and the rest of the album after it, the same as
    /// double-clicking the row queues the album from there. A selection is the selection, with the
    /// right-clicked row moved to the front, so the set opens on what was clicked, because the opening record
    /// is the first one handed over.</para>
    /// </summary>
    private void CtxStartDj_Click(object sender, RoutedEventArgs e)
    {
        var amp = App.Services.GetRequiredService<RPaMpService>();
        if (LibraryMenuTracks() is not { Count: > 0 } tracks)
            return;

        if (tracks.Count == 1)
        {
            amp.StartDjHere(tracks[0]);
            return;
        }

        if (_contextLibraryRow?.FilePath is { Length: > 0 } clicked)
        {
            int at = tracks.FindIndex(t => string.Equals(t.FilePath, clicked, StringComparison.Ordinal));
            if (at > 0)
            {
                var first = tracks[at];
                tracks.RemoveAt(at);
                tracks.Insert(0, first);
            }
        }

        amp.StartDjSet(tracks);
    }

    /// <summary>"Start radio" on a library row: the right-clicked row, or the first selected one when the menu was
    /// opened without a row under it.</summary>
    private void CtxStartRadio_Click(object sender, RoutedEventArgs e)
    {
        var seed = _contextLibraryRow?.ToWantedTrackInfo()
            ?? (LibraryMenuTracks() is { Count: > 0 } tracks ? tracks[0] : null);
        if (seed is not null)
            App.Services.GetRequiredService<RPaMpService>().StartRadio(seed);
    }

    /// <summary>What a library row menu acts on: the selection, or the right-clicked row alone when it
    /// is not part of it.</summary>
    private List<RPM.Core.Models.TrackInfo> LibraryMenuTracks()
    {
        if (Model is not { } m)
            return [];
        var rows = m.SelectedLibraryRowsSnapshot().ToList();
        if (_contextLibraryRow is { } cr && !rows.Contains(cr))
            rows = [cr];
        return [.. rows.Select(r => r.ToWantedTrackInfo())];
    }

    // What just happened.
    //
    // This follows the full-screen player, including the timings: a press answered one way in the window and
    // another way on the visualizer would look like two apps. See `PegasusPlayer.ShowFlash`.

    /// <summary>How long a message stays up before it starts to leave. The two fade times are
    /// below.</summary>
    private static readonly TimeSpan FlashHold = TimeSpan.FromSeconds(2.6);
    private const int FlashInMs = 140;
    private const int FlashOutMs = 320;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _flashTimer;   // one-shot: restarted by every message
    private ChromeFade? _flashFade;

    /// <summary>
    /// Shows one sentence along the foot of the window.
    ///
    /// <para>A second message that arrives while the first is shown replaces it and restarts the clock. It
    /// does not queue behind the first: these are answers to presses, and the answer to the latest press is
    /// the only one anybody waits for.</para>
    /// </summary>
    private void ShowFlash([Shown] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _flashFade ??= new ChromeFade(Flash);
        FlashText.Text = message;
        Flash.Visibility = Visibility.Visible;
        _flashFade.To(1, FlashInMs);

        _flashTimer ??= CreateFlashTimer();
        _flashTimer?.Stop();
        _flashTimer?.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? CreateFlashTimer()
    {
        if (DispatcherQueue is null)
            return null;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = FlashHold;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideFlash();
        return timer;
    }

    /// <summary>Hides the message.</summary>
    private void HideFlash()
    {
        _flashTimer?.Stop();
        _flashFade?.To(0, FlashOutMs);

        // Collapsed shortly after the fade ends, so that the layout stops measuring it. The opacity is what
        // the user sees. This only removes the element from the layout.
        Services.Shell.Ticker.Once(DispatcherQueue, TimeSpan.FromMilliseconds(FlashOutMs + 40), () =>
        {
            if (_flashFade?.Target == 0)
                Flash.Visibility = Visibility.Collapsed;
        });
    }

}

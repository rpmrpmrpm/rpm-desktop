using RPM.Core.Cloud;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Diagnostics;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Shell;

public sealed partial class ShellPage : Microsoft.UI.Xaml.Controls.Page
{
    /// <summary>The live shell, so the review page can reach the artwork header (which lives here,
    /// above the player, and not inside the page). There is one window and one shell.</summary>
    public static ShellPage? Current { get; private set; }

    /// <summary>The artwork header. The review page drives it (selection and playback).</summary>
    public ArtDisplay Art => ShellArt;

    /// <summary>Runs a gallery visualizer over the whole window. The Visualizations pane calls this with
    /// the id of the entry whose launch button was pressed. The overlay has its own now-playing chrome
    /// and closes itself on Escape.
    ///
    /// <para>This is not the way to Pegasus. Pegasus is a VIEW and not a visualizer (see
    /// <see cref="ViewModeService"/>), and it is reached through the deck's view menu.</para></summary>
    public void ShowVisualizer(string vizId) => VizOverlay.Show(vizId);

    /// <summary>Takes the full-screen visualizer down, if it is up.</summary>
    public void HideVisualizer() => VizOverlay.Hide();

    /// <summary>The full-screen player, for the view-mode service to show and hide. Nothing else
    /// drives it directly, because one service decides which view is on screen.</summary>
    public PegasusPlayer Pegasus => PegasusOverlay;

    /// <summary>
    /// Puts the RPaMp skin over the shell, or takes it away.
    ///
    /// <para>The skin is mounted and unmounted, not just hidden. It is one control with one parent at a
    /// time: the same instance carries the queue, the EQ and the record playing. If it stayed in a
    /// collapsed grid, a live view would keep laying itself out behind a view that nobody sees. Its own
    /// visual state survives the move, because it is the same object.</para>
    ///
    /// <para>Only <see cref="ViewModeService"/> calls this. Nothing else decides what is on screen.</para>
    /// </summary>
    public void ShowAmp(bool on)
    {
        var amp = App.Services.GetRequiredService<RPaMpService>().View;

        // Everything else in the shell is collapsed, not covered. The window itself becomes
        // see-through for this view, so the gaps between the casing's panels show the desktop, and
        // anything left behind them would show through those gaps instead of the desktop. Collapsed
        // and not just hidden, so that a library nobody sees does not lay itself out.
        var rest = on ? Visibility.Collapsed : Visibility.Visible;
        BackdropLayer.Visibility = rest;
        ShellBody.Visibility = rest;
        TopSeek.Visibility = rest;
        ParkSeek.Visibility = rest;
        _ampUp = on;
        SyncRecordShown();

        // The PAGE's own fill must go too, and nothing above reaches it. Collapsing every layer in
        // the shell still leaves this fill painting #0e1520 across the whole window, and a
        // see-through window under an opaque page is a window with a dark rectangle in it. Three
        // fills must go for the desktop to show through the casing's gaps: this one, the window root
        // that Uno paints (SkinWindow.ClearRootBackground) and the NSWindow's own. If one is left,
        // the desktop does not show.
        Background = new SolidColorBrush(on ? Microsoft.UI.Colors.Transparent : ShellFill);

        if (on)
        {
            if (!AmpHost.Children.Contains(amp))
                AmpHost.Children.Add(amp);
            AmpHost.Visibility = Visibility.Visible;

            // The window becomes the skin's: no chrome, see-through, and the size of the casing. It is
            // fitted again on every settle, because the playlist grows with the queue and the window
            // follows it.
            amp.SizeChanged -= Amp_SizeChanged;
            amp.SizeChanged += Amp_SizeChanged;

            // The casing's own settle is needed as well as the window's. The control stretches to the
            // window, so its SizeChanged reports the window's size and says nothing when the casing
            // changes shape inside it, which is what opening the library does.
            amp.CasingSettled -= Amp_CasingSettled;
            amp.CasingSettled += Amp_CasingSettled;

            SkinWindow.Set(true, amp);
            return;
        }

        amp.SizeChanged -= Amp_SizeChanged;
        amp.CasingSettled -= Amp_CasingSettled;
        SkinWindow.Set(false);
        AmpHost.Visibility = Visibility.Collapsed;
        AmpHost.Children.Remove(amp);
    }

    private void Amp_SizeChanged(object sender, SizeChangedEventArgs e)
        => SkinWindow.Set(true, sender as RPaMpView);

    /// <summary>The casing settled at a new size (the library was opened or shut). Fits the window to
    /// it again, with the same call that the size change makes.</summary>
    private void Amp_CasingSettled()
        => SkinWindow.Set(true, App.Services.GetRequiredService<RPaMpService>().View);

    /// <summary>The shell's own dark background, as the page declares it. It is restored when the skin
    /// leaves.</summary>
    private static readonly Windows.UI.Color ShellFill = Windows.UI.Color.FromArgb(0xFF, 0x0E, 0x15, 0x20);

    /// <summary>The saved OPEN height of the band: where dragging and the artwork ceiling leave it, and
    /// where the drawer reopens when a track comes back. It is not the height on screen: while stopped
    /// the drawer parks lower (see <see cref="_displayBand"/>) and this value is kept.</summary>
    private double _artBandHeightValue = 200;

    /// <summary>Always set through this property and never the field, because the artwork must know the
    /// height too. When parked, the band on screen is the stopped park and the picture is not shown, so
    /// the picture must be framed for THIS height, the one it will have when the drawer opens. Otherwise
    /// a session that starts parked (the listener's setting, restored at launch) would frame its first
    /// artwork for a strip eighty pixels tall.</summary>
    private double _artBandHeight
    {
        get => _artBandHeightValue;
        set
        {
            _artBandHeightValue = value;
            ShellArt.OpenBandHeight = value;
        }
    }

    /// <summary>The height on screen right now: the saved open height while a track is up, the parked
    /// height while nothing plays, or a value between them during the animation.</summary>
    private double _displayBand = 200;

    /// <summary>True while the art is idle and the drawer is parked at its stopped height.</summary>
    private bool _drawerClosed;

    // ── borderless window drag (Linux) ── the title strip moves the AppWindow by the pointer delta, the
    // same managed drag that the RPaMp player uses. The latest delta wins, and the Move is deferred by one
    // tick. Windows does not use this: it is given the strip as a caption region, so the system's own
    // move loop does the drag and the snap gestures work. See RefreshCaptionRegions.
    private bool _dragging;
    private Windows.Foundation.Point _dragOrigin;
    private int _pendingDx;
    private int _pendingDy;
    private bool _movePending;

    public ShellPage()
    {
        InitializeComponent();
        Current = this;

        // Windows and Linux show our own frosted close, minimize and maximize buttons. The native
        // title bar is removed on both (WindowPlacement removes it, through the presenter on Windows
        // and the X11 Motif hint on Linux). macOS keeps its real traffic lights, so the cluster stays
        // hidden there.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            WinCaptionButtons.Visibility = Visibility.Visible;
            SizeChanged += (_, _) => UpdateMaxGlyph();
            Loaded += (_, _) => UpdateMaxGlyph();
            // Place the cluster at the card edge once loaded. After that, the record's geometry signal
            // (ShellArt.GeometryChanged) moves it out of the way of the disc.
            Loaded += (_, _) => PositionWinCaptionButtons();
        }

        if (OperatingSystem.IsWindows())
        {
            // The system is told where the title bar is on every signal that can move its edges: the
            // window's size, the disc and the cluster that the band stops short of, and which view
            // owns the window. It is also told when the scale changes, because the rectangles are in
            // physical pixels, and dragging the window to a monitor with another scale changes what a
            // DIP is worth without changing the layout.
            SizeChanged += (_, _) => RefreshCaptionRegions();
            ShellArt.GeometryChanged += RefreshCaptionRegions;
            App.Services.GetRequiredService<ViewModeService>().Changed += _ => RefreshCaptionRegions();
            Loaded += (_, _) =>
            {
                // Loaded fires again each time the page is entered, so the scale hook is added once.
                if (!_scaleHooked)
                {
                    _scaleHooked = true;
                    try { XamlRoot!.Changed += (_, _) => RefreshCaptionRegions(); } catch { }
                }
                RefreshCaptionRegions();
            };
        }

        // The spinning record is hosted here, above the content, so it overlaps the grids. ArtDisplay
        // still sizes and turns it.
        ShellArt.AttachRecord(TopRecord);

        // The now-playing seek bar lives in this top overlay (so it gets pointer input), but ArtDisplay
        // positions it under the album name. It uses the one shared player (the same instance that the
        // review deck and the amp popout drive), so its playhead and countdown always match what is
        // sounding. There are two seek bars, one for the open drawer and one for the parked drawer (see
        // the XAML). Both use the same transport, so whichever is on screen shows the same playhead.
        ShellArt.AttachSeek(TopSeek, ParkSeek);
        var transport = App.Services.GetRequiredService<RPaMpService>().Transport;
        TopSeek.DataContext = transport;
        ParkSeek.DataContext = transport;

        // ── the space bar ──
        // Pause what is sounding, or start it again when it is paused, from anywhere in the window. The
        // key is taken on the way DOWN: PreviewKeyDown tunnels from the root before any control acts on
        // the key, and Uno raises it on Skia, which is what this head is. Who owns the key must be
        // decided BEFORE the library grid toggles the current cell's selection. SpaceBar is the one
        // place that decides. When it says the key is not the player's, this handler leaves the key
        // alone and the press continues as normal.
        PreviewKeyDown += OnPreviewKeyDown;

        // Key releases are handled for Pegasus only: a held OK and Left or Right on a seek bar are
        // decided on the release (see OnPreviewKeyUp).
        PreviewKeyUp += OnPreviewKeyUp;

        // The view-mode service is told where the full-screen player is and what "closed" means for
        // each of the two views that can be closed. This is done here and not in the service's
        // constructor, because the service is built long before this tree, so it would have nothing to
        // point at.
        var views = App.Services.GetRequiredService<ViewModeService>();
        views.AttachPegasus(() => PegasusOverlay);
        views.HookCloseHandlers(PegasusOverlay);
        PegasusOverlay.CoversChanged += Cover;
        VizOverlay.CoversChanged += Cover;

        // The problem screen over the library, for when reading the open collection failed.
        WatchLibraryTrouble();

        // Stopped and playing: when the art goes idle the drawer slides up under the rpm mark and the
        // disc leaves the screen. When a track brings the art back, the drawer opens again. The grids
        // keep their position during the move.
        ShellArt.IdleChanged += OnArtIdleChanged;

        // A park that the listener chose in an earlier run still applies: the drawer opens parked and
        // the session's first track leaves it parked. This is set before anything can play.
        if (App.Services.GetRequiredService<SettingsService>().ArtDrawerParked)
            ShellArt.RestoreHeldPark();

        // The hamburger sits on the record's edge, so it is positioned again whenever the strip lays
        // out again (a window resize, or the band being dragged taller). The caption cluster moves out
        // of the disc's way on the same signal.
        ShellArt.OpenBandHeight = _artBandHeight;   // the height the artwork is framed for, from the start
        ShellArt.GeometryChanged += PositionHamburger;
        ShellArt.GeometryChanged += PositionWinCaptionButtons;

        // ── the cached frost ──
        // The frosted cards use ONE picture of the two layers below them (BackdropLayer), taken when
        // the scene is still, so that each card does not blur the window again on every frame.
        // Everything that can change those pixels tells the plate to take a new picture: the window's
        // size, the artwork's geometry (the drawer, the record, a resize) and the artwork itself. The
        // artwork dissolves over two seconds, so it asks for the longer settle.
        var frost = FrostPlate.Current;
        frost.Attach(BackdropLayer);
        SizeChanged += (_, _) => frost.Invalidate(FrostReason.Motion);

        // Every control a hint points at moves when the window does.
        SizeChanged += (_, _) => RefreshCoachmarks();

        // One of the hints is about signing in, so a change of account changes which hint is due.
        if (App.Services.GetService<AuthService>() is { } account)
            account.Changed += () => DispatcherQueue.TryEnqueue(RefreshCoachmarksForAccount);
        ShellArt.GeometryChanged += () => frost.Invalidate(FrostReason.Motion);
        ShellArt.BackdropChanged += () => frost.Invalidate(FrostReason.Artwork);
        ShellArt.IdleChanged += _ => frost.Invalidate(FrostReason.Artwork);
        RootFrame.Navigated += (_, _) => frost.Invalidate(FrostReason.Motion);

        // The background picture is the largest single thing in the plate's photograph. It is hooked
        // where it is made, on its own decode event. See ApplyBackground.

        // ── what the window is standing on ──
        // The app's own backdrop, a record's sleeve or an artist's photograph: ONE control shows one of
        // the three (see ShellGround). SyncPageBack decides which one, from the two facts that
        // PageBackdrop combines. The artwork is not one of them: it is drawn OVER the ground and covers
        // it by itself. If the ground were told about the artwork, it would switch back to the
        // wallpaper while hidden. Nothing is decided here.
        PageBackdrop.Changed += SyncPageBack;

        // The whole opening, in one place and in one order: the wordmark, then the library, then the
        // interface. See Startup.Opening, which holds the order. This code only gives it the parts.
        Loaded += (_, _) =>
        {
            // Once per run, like every other one-shot here. Loaded fires again whenever the page
            // enters the tree again, and a second opening would hide the interface behind a splash
            // that has already gone.
            if (_revealArmed)
                return;
            _revealArmed = true;
            RPM.Core.Diagnostics.Scope.Said("boot", "shell loaded, opening begins");

            // The body is laid out, but not DRAWN, for as long as the black rectangle is over it.
            //
            // Uno's renderer skips a subtree whose opacity is zero (Visual.IsRenderSuppressed, checked
            // before anything is recorded). Skipping this one skips the whole library page, which
            // during startup includes three near-fullscreen live backdrop blurs at radius 30. The
            // frosted panels use their live glass until FrostPlate has taken its picture, and it cannot
            // take one during a startup that keeps invalidating it. Without this, every frame of the
            // splash would pay for three blurs of a window that nobody can see. Layout is not affected,
            // so nothing is postponed until the reveal.
            _bodyHeldForReveal = true;
            ShowBody();

            Startup.Opening.Begin(
                DispatcherQueue,
                App.Services.GetRequiredService<Services.Shell.Governor>(),
                App.Services.GetRequiredService<Services.Shell.UiPulse>(),
                App.Services.GetRequiredService<Services.Shell.FirstScreen>(),
                floor: TimeSpan.FromMilliseconds(1500),
                // PATIENCE is counted from the moment the screen has something on it, not from launch.
                // A limit counted from launch can run out before the screen is ready: on a slow start
                // the shell may load so late that the gate is armed already past its deadline and
                // opens over an empty window.
                //
                // It is a limit and not a cost. The gate opens as soon as the screen says its pictures
                // have arrived, so this number only matters when they do not arrive. Raising it changes
                // nothing for a start where they do.
                //
                // The covers arrive about 2.3 s and 3.3 s after the rows have their shape on a large
                // library. Four seconds covers both with room to spare, because a start slower than
                // those is the one this limit is for. In the worst case it costs four seconds of
                // wordmark over a screen that was ready. In the normal case the app is already fully
                // drawn when it appears.
                patience: TimeSpan.FromSeconds(4),
                // BACKSTOP is for a screen that never reports AT ALL, which is a fault and not a slow
                // machine. It must be longer than any working startup. A cold start on a slow machine
                // reached "first screen up" at about 16 seconds, so a lower value would fire on a start
                // that was going to work. Twenty seconds leaves room above that.
                backstop: TimeSpan.FromSeconds(20),
                new Startup.Opening.Parts(
                    Wordmark: PlayWordmark,
                    Library: OpenLibraryAndLand,
                    Reveal: DismissSplash));   // told HOW the gate opened: a busy thread gets no fade
        };

        // The artwork header and the library-only chrome (its caption, the list/grid switch) belong
        // to the library page. The first-run setup page has no library, so the band collapses and they
        // are hidden. This is evaluated again on every navigation.
        RootFrame.Navigated += (_, _) => OnNavigated();

        // The cloud engine cannot tell the user by itself. It stops when the store is full and
        // raises an event. Showing a window is the shell's job, and the shell knows whether one is
        // already open.
        Loaded += (_, _) =>
        {
            if (_cloudFullHooked)
                return;
            _cloudFullHooked = true;
            App.Services.GetRequiredService<CloudSyncService>().StorageFull += OnCloudStorageFull;
        };

        // Whether there is a newer version, checked a short time after the app is up. See
        // ShellPage.Updates.cs.
        Loaded += (_, _) => HookUpdates();

        // What to try when a play is refused because the sound output is not working. See
        // ShellPage.AudioOutput.cs.
        Loaded += (_, _) => HookAudioOutput();

        // Crashes since the last run: package them, then ask the user or send them. See
        // ShellPage.Crashes.cs.
        Loaded += (_, _) => HookCrashes();

        // The user's own backdrop, if they set one. It is applied again each time they change it in
        // Settings.
        Loaded += (_, _) =>
        {
            if (_bgHooked)
                return;
            _bgHooked = true;
            var settings = App.Services.GetRequiredService<SettingsService>();
            ApplyBackground(settings);
            ApplyArtDisplay(settings);
            settings.Changed += () => DispatcherQueue.TryEnqueue(() =>
            {
                ApplyBackground(settings);
                ApplyArtDisplay(settings);
            });

            // The ACCOUNT can also change, which is not a settings change. One of the backdrops is
            // for supporters, so the supporter status is an input to what is on the screen, as the
            // stored choice is. When support lapses, the gold backdrop must come down while the app is
            // running and not at the next restart. When support starts, it must appear the same way.
            // ApplyBackground resolves the picture again and does nothing if the picture is the same.
            App.Services.GetRequiredService<Services.Account.SupporterService>().Changed +=
                () => DispatcherQueue.TryEnqueue(() => ApplyBackground(settings));
        };

        // The drag handles only appear on hover, so someone who has never pointed at the strip cannot
        // find them. At startup both grips are shown for five seconds, once per run, and then hidden.
        // See GripDebutSeconds.
        Loaded += (_, _) =>
        {
            if (_gripDebuted)
                return;
            _gripDebuted = true;
            ShowArtGrip(shown: true, lit: false);
            Services.Shell.Ticker.Once(DispatcherQueue, TimeSpan.FromSeconds(GripDebutSeconds), () =>
            {
                if (!_artHovered && !_artDragging)
                    ShowArtGrip(shown: false, lit: false);
            });
        };

    }

    /// <summary>
    /// The mark arriving: step one of the opening.
    ///
    /// <para>The mark grows from invisible and slightly small to the resting values that the markup
    /// carries. The direction matters (see the note on <c>SplashLogo</c> in the XAML): a head that does
    /// not run the storyboard leaves the mark where the markup put it, which is visible, and not at the
    /// animation's start value, which is invisible.</para>
    ///
    /// <para>The animation takes 400 ms (opacity) and 560 ms (scale). It is the ONLY thing asked of the
    /// drawing thread while it runs, which is why <see cref="Startup.Opening"/> delays the library for a
    /// short time.</para>
    /// </summary>
    /// <summary>The mark's entrance, kept so that its hold on the opacity can be released before the
    /// reveal uses the same property. See <see cref="DismissSplash"/>.</summary>
    private Storyboard? _wordmark;

    private void PlayWordmark()
    {
        if (SplashLogo is null)
            return;

        // Not while the splash is a window of its own. This window is cloaked and the mark on the
        // card is what the user sees. The entrance would be 560 ms of animation inside a window that
        // nobody can see, on the thread that opens the library.
        if (Startup.SplashWindow.IsUp)
            return;

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 0.5,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, SplashLogo);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(fade);

        // The scale is set on the transform and not on the Path, and both axes are animated. One axis
        // alone would look like a stretch.
        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation
            {
                From = 0.94,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(560)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, SplashLogoScale);
            Storyboard.SetTargetProperty(grow, axis);
            sb.Children.Add(grow);
        }

        _wordmark = sb;
        Services.Shell.Storyboards.Play(sb);
    }

    /// <summary>
    /// The collection, and the first screen built on it: step two of the opening.
    /// </summary>
    /// <remarks>
    /// <para>This runs on the drawing thread and is the expensive half of the startup. It opens the
    /// database and builds the whole main page, which reads the playlist tree, the queue count and the
    /// album index while it is built. The record turns over it if it runs long. The background passes
    /// do not compete with it for the database gate, because the governor starts them later.</para>
    ///
    /// <para><b>Returning does not mean the screen is filled.</b> The main page lands on home, and home
    /// reads the shelf on a worker afterwards. Home calls <see cref="Services.Shell.FirstScreen.Up"/>
    /// when its rows have something in them. The two landings that have no such read call it here
    /// instead, because a landing that nobody reports holds the splash until the gate's ceiling.</para>
    /// </remarks>
    private void OpenLibraryAndLand()
    {
        var screen = App.Services.GetRequiredService<Services.Shell.FirstScreen>();

        App.Services.GetRequiredService<NavigationService>().Attach(RootFrame);
        if (RootFrame.Content is not null)
        {
            // Something navigated before the opening got here, so this step has nothing to show and
            // nothing to wait for: what is in the frame is the first screen.
            screen.Up();
            return;
        }

#if DEBUG
        // `--gallery`: open the seeded demo library and land on the workbench. The gallery index is
        // in its own window (opened from App), so the main window shows a real screen.
        if (GalleryMode.Active && GalleryMode.LibraryPath is { } galleryLib)
        {
            App.Services.GetRequiredService<LibraryService>().Open(galleryLib);
            RootFrame.Navigate(typeof(MainPage));
            return;
        }
#endif

        // A configured central music folder skips setup: open its mirror and land on the library
        // page. There are four results that are not a collection: no folder at all, a folder with no
        // albums in it, a folder that does not exist, and a mirror that will not open. Each has its
        // own screen. LibraryService.OpenForLanding opens the library and names the outcome.
        // OpeningScreen.Choose decides which screen that means (it is pure and tested). Landing.Show
        // shows the screen, and the problem screen's Try again button also calls it, so the two
        // agree.
        var route = Landing.Show(Landing.Attempt());

        if (route is RPM.Core.Library.OpeningRoute.Library)
            return;

        // The wizard and the problem screen have no library read to wait for: they are finished as
        // soon as they are shown. FirstScreen is told here and the ceiling is not left to decide,
        // because the ceiling would hold a first run behind the splash for several seconds.
        screen.Up();
    }

    /// <summary>
    /// The space bar, wherever the listener is in the window.
    ///
    /// <para>When the key is held down, the repeats are swallowed and not played and paused at the
    /// keyboard's repeat rate, so a finger resting on the key pauses once. With a modifier held, the key
    /// is not a play/pause key: Ctrl (which is also Cmd on the Mac), Alt and Shift belong to whatever
    /// shortcut is being typed, and the key continues untouched.</para>
    ///
    /// <para>The command is the deck's own <see cref="RPaMpModel.PlayPauseCommand"/>, so the key always
    /// agrees with the player: it pauses a playing track, resumes a paused one, and starts the queue
    /// when there is one and nothing is sounding.</para>
    /// </summary>
    private void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Handled)
            return;

        // Every key while Pegasus is up starts its screensaver's wait again. The press that brings its
        // screens back does nothing else, down to its release, except the media keys and the shortcuts
        // below, which still do their own work: a pause pressed over the room pauses (see
        // PegasusPlayer.Screensaver).
        var waking = PegasusOverlay.IsShowing && PegasusOverlay.ScreensaverKeyDown(e.Key);

        // The media keys, wherever the listener is: play and pause, next and previous track, stop.
        // A TV remote sends these, and so does a keyboard with media keys. On the WinUI head the
        // Windows media controls also answer them when another app is in front (NowPlayingService).
        // With rpm in front, a press runs once and not twice.
        if (MediaCommand(e.Key) is { } media && !Modified())
        {
            e.Handled = true;
            if (!HeldKeys.IsRepeat(e) && media.CanExecute(null))
                media.Execute(null);
            return;
        }

        // The shortcuts: Cmd on a Mac and Ctrl elsewhere, with the arrows and F (see Shortcuts). They
        // come before Pegasus's keys, like the media keys, so Cmd+Right there is the next track and
        // not an arrow that moves the focus.
        if (TakeShortcut(e))
            return;

        if (waking)
        {
            e.Handled = true;
            return;
        }

        if (PegasusOverlay.IsShowing && TakePegasusKey(e))
            return;

        if (e.Key is not Windows.System.VirtualKey.Space || Modified())
            return;

        var focused = XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject
            : null;
        if (!SpaceBar.Under(RouteFrom(focused)).BelongsToTransport)
            return;

        // The press is handled completely, repeat or not, so that it does not reach the grid
        // underneath and toggle a cell's selection.
        e.Handled = true;
        if (HeldKeys.IsRepeat(e))
            return;

        App.Services.GetRequiredService<RPaMpService>().Transport.PlayPauseCommand.Execute(null);
    }

    /// <summary>
    /// A keyboard shortcut: previous and next track, the volume, and the search field of the screen in
    /// front. <see cref="Shortcuts"/> decides which key is which, and where a focused control keeps the
    /// key. Returns true when the key was a shortcut and has been handled.
    ///
    /// <para>The key is handled completely, repeat or not, so that the focused control does not act on it
    /// as well (the table would move its row on Cmd+Down on a Mac). The commands are the deck's own, so a
    /// shortcut always agrees with the player. A held volume key keeps stepping, while a held Cmd+Right
    /// skips one track.</para>
    /// </summary>
    private bool TakeShortcut(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!Shortcuts.IsShortcutKey(e.Key))
            return false;

        var focused = XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject
            : null;
        var shortcut = Shortcuts.For(e.Key, HeldKeys.Now, OperatingSystem.IsMacOS(), RouteFrom(focused));
        if (shortcut == Shortcut.None)
            return false;

        e.Handled = true;
        if (HeldKeys.IsRepeat(e) && !Shortcuts.ActsOnRepeat(shortcut))
            return true;

        var transport = App.Services.GetRequiredService<RPaMpService>().Transport;
        System.Windows.Input.ICommand? command = shortcut switch
        {
            Shortcut.Previous => transport.PrevCommand,
            Shortcut.Next => transport.NextCommand,
            Shortcut.VolumeUp => transport.VolumeUpCommand,
            Shortcut.VolumeDown => transport.VolumeDownCommand,
            _ => null,
        };

        if (command is not null)
        {
            if (command.CanExecute(null))
                command.Execute(null);
            return true;
        }

        SearchInFront();
        return true;
    }

    /// <summary>
    /// Cmd+F or Ctrl+F: the search field of the view in front. Pegasus goes to its wall and focuses its
    /// field. The mini player opens its collection if it is shut and focuses its field. The library
    /// window chooses among its own fields (see <see cref="MainPage.FocusSearch"/>). Nothing happens
    /// while the full-screen visualizer is up, which covers the library but is not a view of its own.
    /// </summary>
    private void SearchInFront()
    {
        if (VizOverlay.IsShowing)
            return;

        switch (App.Services.GetRequiredService<ViewModeService>().Current)
        {
            case AppView.FullScreen:
                PegasusOverlay.SearchCollection();
                break;
            case AppView.RPaMp:
                App.Services.GetRequiredService<RPaMpService>().View.SearchCollection();
                break;
            case AppView.Library:
                LibraryPage?.FocusSearch();
                break;
        }
    }

    /// <summary>
    /// A key while Pegasus is up. Pegasus is driven by a TV remote: no clicking and no swiping, only
    /// the arrow buttons, OK, Back and a few others. The key is handled here, on the way down from the
    /// window's root, because this is the one handler that every press passes through, whether the
    /// focus is in Pegasus or not. If Pegasus listened on its own root, it would miss every press made
    /// with the focus outside it, which after a change of screen is most of them. Returns true when the
    /// key has been handled and goes no further.
    ///
    /// <list type="bullet">
    /// <item>Back (Escape, the remote's Back, a gamepad's B, and Backspace outside a text field) goes one
    /// level up (see PegasusPlayer.TakeBack).</item>
    /// <item>On the player with its controls hidden, Left and Right seek 10 seconds, and OK, Up and Down
    /// only bring the controls up with the play key focused (see PegasusPlayer.TakeHiddenControlsKey).</item>
    /// <item>An arrow or OK with the focus lost (on nothing, outside the view, or on the view's root) puts
    /// the focus on the screen's own default instead of going nowhere. The first one after the pointer was
    /// in use only shows where the focus is, without moving it, and so does the first one after the
    /// transport faded with the focus on it.</item>
    /// <item>While a menu is open (a track's, a record's, the wall's sort menu: every Pegasus menu is the
    /// TV's glass card), the arrows and OK go to it (see PegasusPlayer.LineMenuKey). The press that opened
    /// it, and the OK that chose in it, are dropped until they come up (see PegasusPlayer.MenuPressHeld).
    /// While the focus is in any other open popup, every key but Back is left to it.</item>
    /// <item>While a queue row is carried by the keys, the arrows and OK move it and put it down (see
    /// PegasusPlayer.QueueMoveKey).</item>
    /// <item>While radio's wheel is open, the arrows turn it and OK opens its entry (see
    /// PegasusPlayer.RadioWheelKey). On home and radio, Left held for two seconds opens the side menu, and
    /// on radio Right held for two seconds opens the wheel; a held Left or Right moves nothing meanwhile
    /// (see PegasusPlayer.SideKey).</item>
    /// <item>The Menu key on a track line opens its menu (see PegasusPlayer.MenuKey).</item>
    /// <item>OK is handed to Pegasus first (PegasusPlayer.OkDown): a repeat presses nothing, and on a
    /// track line OK is held back to see whether it is a press or a hold for the menu. On anything else
    /// it goes on to the focused control, which presses itself.</item>
    /// <item>Left and Right on a control that is turned rather than moved off (a seek bar, the volume, a
    /// record page's stars) turn it, and bounce at its end (see PegasusChrome.Adjust).</item>
    /// <item>Otherwise an arrow moves the focus to the next thing in that direction on the screen in front
    /// (see PegasusChrome.NextFocus and RemoteSearch for how it is found). This is done here and not
    /// left to the framework: a scroller above the focused control takes the arrow keys to scroll with,
    /// before XY navigation sees them, so on home Down would scroll the page and the focus would stay
    /// where it was. Left and Right stay in the row and Up and Down in the column. Where there is
    /// nothing to go to, the focus bounces at the end and nothing scrolls.</item>
    /// </list>
    /// </summary>
    private bool TakePegasusKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var pegasus = PegasusOverlay;
        var focused = XamlRoot is { } root
            ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root)
            : null;

        // A collection is being switched to: the screen over the view says so, and the remote's keys
        // are ignored until it goes, so that they do not move or press something behind it. The media
        // keys were handled above.
        if (pegasus.SwitchingCollection
            && (IsBackKey(e.Key, typing: false) || IsNavigationKey(e.Key) || IsMenuKey(e.Key)))
        {
            e.Handled = true;
            return true;
        }

        // Any other key going down while Left or Right is held ends the hold that would open the menu
        // on that side (see PegasusPlayer.SideKey). The held key's own repeats do not end it.
        pegasus.SideHoldSees(e.Key);

        // The rest of a press that a menu owns: the press that opened it, or the OK that chose in it. Its
        // repeats go nowhere until it has come up (see PegasusPlayer.MenuPressHeld).
        if (pegasus.MenuPressHeld(e.Key))
        {
            e.Handled = true;
            return true;
        }

        if (IsBackKey(e.Key, typing: focused is TextBox))
        {
            e.Handled = true;
            pegasus.KeysUsed();
            pegasus.TakeBack(repeat: HeldKeys.IsRepeat(e));
            return true;
        }

        // A menu is open, as the TV's glass card: the arrows and OK move through it and choose, and
        // nothing behind it moves (see PegasusPlayer.LineMenuKey). Back was handled above, and closes it
        // or leaves its submenu.
        if (pegasus.LineMenuOpen)
        {
            if (!IsNavigationKey(e.Key) && !IsMenuKey(e.Key))
                return false;
            e.Handled = true;
            pegasus.LineMenuKey(e.Key, ArrowDirection(e.Key), IsOkKey(e.Key));
            return true;
        }

        // The focus is in another open popup (a dialog over the view): its keys belong to it. Keys pressed
        // in a popup still pass through here. If they were handled as Pegasus keys, the focus would count
        // as "lost" (it is in the popup, outside the view) and be moved back to the screen.
        if (pegasus.FocusInPopup)
            return false;

        // A queue row is being carried by the keys: the arrows and OK belong to the move (see
        // PegasusPlayer.QueueMoveKey), and the Menu key does nothing while it lasts.
        if (pegasus.QueueMoving && (IsNavigationKey(e.Key) || IsMenuKey(e.Key)))
        {
            e.Handled = true;
            pegasus.KeysUsed();
            pegasus.QueueMoveKey(ArrowDirection(e.Key), IsOkKey(e.Key), HeldKeys.IsRepeat(e));
            return true;
        }

        // Radio's wheel: the arrows turn it and OK opens what is on its marker, and nothing behind it
        // moves (see PegasusPlayer.RadioWheelKey). Back was handled above and closes it.
        if (pegasus.RadioWheelOpen && (IsNavigationKey(e.Key) || IsMenuKey(e.Key)))
        {
            e.Handled = true;
            pegasus.KeysUsed();
            pegasus.RadioWheelKey(ArrowDirection(e.Key), IsOkKey(e.Key), HeldKeys.IsRepeat(e));
            return true;
        }

        if (IsMenuKey(e.Key))
        {
            pegasus.KeysUsed();
            if (!pegasus.MenuKey(focused, e.Key, HeldKeys.IsRepeat(e)))
                return false;
            e.Handled = true;
            return true;
        }

        if (!IsNavigationKey(e.Key))
            return false;

        // The player with its controls hidden: Left and Right seek, and OK, Up and Down only bring the
        // controls up with the play key focused. Nothing is moved or pressed that cannot be seen (see
        // PegasusPlayer.TakeHiddenControlsKey).
        if (pegasus.TakeHiddenControlsKey(e.Key, ArrowDirection(e.Key), IsOkKey(e.Key), HeldKeys.IsRepeat(e)))
        {
            e.Handled = true;
            return true;
        }

        var wasKeyboard = pegasus.KeyboardMode;
        var keysAway = pegasus.FocusOnFadedKeys;
        pegasus.KeysUsed();

        if (pegasus.FocusIsLost())
        {
            e.Handled = true;
            pegasus.FocusDefault();
            return true;
        }

        // After use of the pointer: show the ring where the focus is, and do not move yet. The same
        // applies to the first key after the transport faded with the focus on it: KeysUsed brought it
        // back, and the key goes no further, so nothing is moved or pressed before it can be seen.
        if (!wasKeyboard || keysAway)
        {
            e.Handled = true;
            return true;
        }

        if (IsOkKey(e.Key))
        {
            if (!pegasus.OkDown(focused, e.Key, HeldKeys.IsRepeat(e)))
                return false;
            e.Handled = true;
            return true;
        }

        // The focus moves before anything scrolls, and an arrow is always handled: where there is
        // nowhere to go, the focus bounces and no scroller runs. Left and Right stay with a text field
        // while there is text in that direction for the caret to move through. At the end of the text
        // they move the focus, so the wall's search field does not trap the focus away from the sort
        // and heart keys beside it.
        if (ArrowDirection(e.Key) is not { } direction
            || (focused is TextBox field && CaretMoves(field, direction)))
            return false;

        e.Handled = true;

        var sideways = direction is Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left
                                 or Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right;

        // Left on home and radio, Right on radio: the press moves as any arrow does and starts timing
        // a hold. The repeats move nothing, and holding for two seconds opens the menu on that side
        // (see PegasusPlayer.SideKey).
        if (sideways && pegasus.SideKey(e.Key, direction, HeldKeys.IsRepeat(e)))
            return true;

        if (sideways)
        {
            var way = direction == Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left ? -1 : 1;
            switch (pegasus.Adjust(focused, way, HeldKeys.IsRepeat(e)))
            {
                case RPM.Desktop.Presentation.Pegasus.KeyTurn.Turned:
                    return true;
                case RPM.Desktop.Presentation.Pegasus.KeyTurn.AtEnd:
                    pegasus.Bounce(direction);
                    return true;
            }

        }

        var next = pegasus.NextFocus(focused as FrameworkElement, direction);

        // The focus is given through RemoteSearch.Give, which turns the framework's own focus rectangle
        // off first.
        if (next is not null && RPM.Desktop.Presentation.Pegasus.RemoteSearch.Give(next))
        {
            pegasus.FocusMovedByKey();
            return true;
        }

        // Nothing in that direction: Right in Pegasus's side menu closes it, and anything else
        // bounces. The side menu and radio's wheel open on a held key instead (see SideKey above).
        if (!pegasus.TakeEdgeKey(direction))
            pegasus.Bounce(direction);
        return true;
    }

    /// <summary>The direction an arrow, a gamepad's pad or a navigation remote's pad asks for, or null for
    /// any other key.</summary>
    private static Microsoft.UI.Xaml.Input.FocusNavigationDirection? ArrowDirection(Windows.System.VirtualKey key) => key switch
    {
        Windows.System.VirtualKey.Up or Windows.System.VirtualKey.NavigationUp
            or Windows.System.VirtualKey.GamepadDPadUp => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Up,
        Windows.System.VirtualKey.Down or Windows.System.VirtualKey.NavigationDown
            or Windows.System.VirtualKey.GamepadDPadDown => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Down,
        Windows.System.VirtualKey.Left or Windows.System.VirtualKey.NavigationLeft
            or Windows.System.VirtualKey.GamepadDPadLeft => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left,
        Windows.System.VirtualKey.Right or Windows.System.VirtualKey.NavigationRight
            or Windows.System.VirtualKey.GamepadDPadRight => Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right,
        _ => null,
    };

    /// <summary>The keys a remote, a keyboard or a gamepad sends for Back. Backspace counts only outside
    /// a text field, where it deletes a letter.</summary>
    private static bool IsBackKey(Windows.System.VirtualKey key, bool typing) => key switch
    {
        Windows.System.VirtualKey.Escape
            or Windows.System.VirtualKey.GoBack
            or Windows.System.VirtualKey.NavigationCancel
            or Windows.System.VirtualKey.GamepadB => true,
        Windows.System.VirtualKey.Back => !typing,
        _ => false,
    };

    /// <summary>
    /// A key coming up while Pegasus is up, passed to Pegasus (PegasusPlayer.KeyReleased): releasing a
    /// held OK on a track line presses the line, and releasing Left or Right commits a seek made with
    /// the keys.
    /// </summary>
    private void OnPreviewKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Handled || !PegasusOverlay.IsShowing)
            return;

        // The release of the press that brought the screensaver's screens back (see OnPreviewKeyDown).
        if (PegasusOverlay.ScreensaverKeyUp(e.Key))
        {
            e.Handled = true;
            return;
        }

        var sideways = ArrowDirection(e.Key) is Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left
                                             or Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right;
        if (PegasusOverlay.KeyReleased(e.Key, sideways))
            e.Handled = true;
    }

    /// <summary>Whether Left or Right moves the caret in a text field and not the focus. This is true
    /// only while there is text in that direction, or a selection to collapse.</summary>
    private static bool CaretMoves(TextBox field, Microsoft.UI.Xaml.Input.FocusNavigationDirection direction) => direction switch
    {
        Microsoft.UI.Xaml.Input.FocusNavigationDirection.Left => field.SelectionStart > 0 || field.SelectionLength > 0,
        Microsoft.UI.Xaml.Input.FocusNavigationDirection.Right =>
            field.SelectionLength > 0 || field.SelectionStart < field.Text.Length,
        _ => false,
    };

    /// <summary>OK: Enter, and the same from a gamepad or a navigation remote.</summary>
    private static bool IsOkKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Enter
        or Windows.System.VirtualKey.NavigationAccept
        or Windows.System.VirtualKey.GamepadA;

    /// <summary>The Menu key: the keyboard's context menu key (VK_APPS, not VirtualKey.Menu, which is
    /// Alt), and a gamepad's menu button.</summary>
    private static bool IsMenuKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Application
        or Windows.System.VirtualKey.GamepadMenu;

    /// <summary>The keys that move the focus or press what has it: the arrows, OK (Enter), and the same
    /// from a gamepad or a navigation remote.</summary>
    private static bool IsNavigationKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
        or Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
        or Windows.System.VirtualKey.Enter
        or Windows.System.VirtualKey.NavigationUp or Windows.System.VirtualKey.NavigationDown
        or Windows.System.VirtualKey.NavigationLeft or Windows.System.VirtualKey.NavigationRight
        or Windows.System.VirtualKey.NavigationAccept
        or Windows.System.VirtualKey.GamepadDPadUp or Windows.System.VirtualKey.GamepadDPadDown
        or Windows.System.VirtualKey.GamepadDPadLeft or Windows.System.VirtualKey.GamepadDPadRight
        or Windows.System.VirtualKey.GamepadA;

    /// <summary>The transport's command for a media key, or null. The media keys have no names in
    /// VirtualKey; these are their Windows codes (VK_MEDIA_NEXT_TRACK 0xB0, VK_MEDIA_PREV_TRACK 0xB1,
    /// VK_MEDIA_STOP 0xB2, VK_MEDIA_PLAY_PAUSE 0xB3).</summary>
    private static System.Windows.Input.ICommand? MediaCommand(Windows.System.VirtualKey key)
    {
        var transport = App.Services.GetRequiredService<RPaMpService>().Transport;
        return (int)key switch
        {
            0xB3 => transport.PlayPauseCommand,
            0xB0 => transport.NextCommand,
            0xB1 => transport.PrevCommand,
            0xB2 => transport.StopCommand,
            _ => null,
        };
    }

    /// <summary>The focused control's type and the types of its parents above it, which is what
    /// <see cref="SpaceBar"/> reads. Nothing focused gives an empty route, and an empty route belongs to
    /// the player.</summary>
    private static IEnumerable<Type> RouteFrom(DependencyObject? focused)
    {
        for (var node = focused; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
            yield return node.GetType();
    }

    /// <summary>Whether a modifier is being held, read from the keyboard. See <see cref="HeldKeys"/> for
    /// why it is not read from Uno's table: after a Cmd-Tab that table keeps Cmd down, and the space
    /// bar would stop playing.</summary>
    private static bool Modified() => HeldKeys.Now != Modifier.None;

    /// <summary>Double-tapping the disc switches to RPaMp. The switch goes through the one service that
    /// decides which view is up.</summary>
    private void Record_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => App.Services.GetRequiredService<ViewModeService>().Switch(AppView.RPaMp);

    /// <summary>Double-clicking the top edge of the window maximizes it, or restores it if it is already
    /// maximized, like a native title bar.</summary>
    private void TitleBar_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        App.ToggleMaximizeMainWindow();
        UpdateMaxGlyph();
        e.Handled = true;
    }

    // ── frosted window buttons (Windows and Linux) ──

    private void Caption_Close_Click(object sender, RoutedEventArgs e) => App.AppWindow?.Close();

    private void Caption_Minimize_Click(object sender, RoutedEventArgs e)
    {
        try { RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(App.AppWindow?.AppWindow)?.Minimize(); }
        catch { }
    }

    private void Caption_MaxRestore_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleMaximizeMainWindow();
        UpdateMaxGlyph();
    }

    // The disc keeps its frost on hover, and the glyph (invisible at rest) fades in, as the macOS
    // traffic lights show their symbols. Each button's Tag points at its glyph element.
    private void Caption_Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UIElement g) g.Opacity = 1;
    }

    private void Caption_Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UIElement g) g.Opacity = 0;
    }

    /// <summary>Switches the maximize button between the single-square glyph (maximize) and the
    /// offset-squares glyph (restore) to match the window's state, and updates its tooltip.</summary>
    private void UpdateMaxGlyph()
    {
        var maximized = false;
        try
        {
            if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(App.AppWindow?.AppWindow) is { } p)
                maximized = p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        }
        catch { }
        MaxIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        IconName.Set(MaxCaptionButton, Loc.Get(maximized ? "Window_Restore" : "Window_Maximize"));
    }

    // ── borderless drag: move the AppWindow by the pointer delta within the title strip (Linux) ──

    private void TitleBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Linux only. macOS keeps the native title-bar drag. Windows gives the strip to the system as
        // a caption region (RefreshCaptionRegions), which makes the snap gestures work: a press in the
        // band there is a non-client press and never reaches this handler.
        // A maximized window cannot be dragged (double-tap the strip or use the button to restore it
        // first), like a maximized native title bar.
        if (!OperatingSystem.IsLinux()) return;
        if (App.AppWindow?.AppWindow is not { } aw) return;
        if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(aw) is { } p
            && p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            return;
        _dragging = true;
        _dragOrigin = e.GetCurrentPoint(this).Position;   // cursor offset inside the window at grab
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TitleBar_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetCurrentPoint(this).Position;
        // The latest delta wins (it replaces the old one and is not added to it). The window catches
        // up between frames.
        _pendingDx = (int)(p.X - _dragOrigin.X);
        _pendingDy = (int)(p.Y - _dragOrigin.Y);
        if (_movePending) return;
        _movePending = true;
        DispatcherQueue.TryEnqueue(ApplyPendingMove);
    }

    private void ApplyPendingMove()
    {
        _movePending = false;
        // The grab can end between the move being queued and it running: a release, a lost capture,
        // or a maximize from the same burst of pointer events. Moving the window then would put a
        // window that is no longer being dragged somewhere the user did not drag it, and a maximized
        // window moved this way stays marked maximized while sitting off its screen. Whether the drag
        // is still on is a fact that can be checked at any time, so it is checked here and the order
        // of the queue is not trusted.
        if (!_dragging) return;
        if (App.AppWindow?.AppWindow is not { } aw) return;
        if (_pendingDx == 0 && _pendingDy == 0) return;
        try
        {
            var pos = aw.Position;
            aw.Move(new Windows.Graphics.PointInt32 { X = pos.X + _pendingDx, Y = pos.Y + _pendingDy });
        }
        catch { /* window gone mid-drag */ }
    }

    private void TitleBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Right-click on the artwork header opens its flip and refresh menu. The art is behind this
    /// content grid, so the catcher band passes the gesture on at the click point.</summary>
    private void ArtHeader_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var target = (FrameworkElement)sender;
        ShellArt.ShowHeaderMenu(target, e.GetPosition(target));
        e.Handled = true;
    }

    /// <summary>Whether the background and header-display settings subscription is in place. Loaded
    /// fires again each time the shell enters the tree.</summary>
    private bool _bgHooked;

    /// <summary>Whether the storage-full subscription is in place. Loaded fires again each time the
    /// shell gets a new parent, and a second subscription would open a second window.</summary>
    private bool _cloudFullHooked;

    /// <summary>Whether the storage-full window is up. The engine can reach the full state twice in a
    /// second (the pass that finds it, and the pass that the mode change starts), and the weekly stamp
    /// is not written until the window is dismissed.</summary>
    private bool _cloudFullShowing;

    /// <summary>
    /// The cloud has stopped because there is not enough room. Tells the user, once a week.
    ///
    /// <para>
    /// <see cref="CloudFullNotice"/> decides how often the notice appears. The stamp is written before
    /// the window opens and not after, because a modal that is dismissed and opened again would
    /// otherwise get past the check. There is no window to show the notice over until the shell is
    /// loaded, which is why this is subscribed on Loaded.
    /// </para>
    /// </summary>
    private void OnCloudStorageFull() => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_cloudFullShowing) return;

        var settings = App.Services.GetRequiredService<SettingsService>();
        if (!CloudFullNotice.Due(
                settings.CloudFullNoticeUtc, DateTime.UtcNow, settings.CloudFullNoticeSuppressed))
            return;

        var cloud = App.Services.GetRequiredService<CloudSyncService>();
        var shownBefore = settings.CloudFullNoticeCount;

        _cloudFullShowing = true;
        settings.CloudFullNoticeUtc = DateTime.UtcNow;
        settings.CloudFullNoticeCount = shownBefore + 1;
        try
        {
            var never = await Dialogs.SupporterDialog.ShowAsync(
                XamlRoot,
                Dialogs.SupporterDialog.Occasion.StorageFull(cloud.Status.Space, settings.SyncAllPausedByFull, CloudFullNotice.MayOptOut(shownBefore)));

            if (never)
                settings.CloudFullNoticeSuppressed = true;
        }
        finally
        {
            _cloudFullShowing = false;
        }
    });

    /// <summary>
    /// The two things on the header the listener can switch off: the turning record and the photograph of
    /// the artist behind it.
    ///
    /// <para>The shell pushes these values to the header, and the header does not read them itself, as
    /// for the backdrop. The shell owns the settings object and already hears it change, and the header
    /// is a control that is given what it shows. Both calls do nothing when nothing has changed, so
    /// this is safe to call on every settings change, which is when it is called.</para>
    /// </summary>
    private void ApplyArtDisplay(SettingsService settings)
    {
        ShellArt.ShowRecord(settings.HeaderRecord);
        SyncRecordShown();
        ShellArt.ShowArtistPhoto(settings.HeaderArtistPhoto);
    }

    /// <summary>The amp view is up, and everything else in the shell is collapsed behind it.</summary>
    private bool _ampUp;

    /// <summary>The library body is laid out but not drawn while the startup splash is over it. It is
    /// set on Loaded and cleared by the reveal.</summary>
    private bool _bodyHeldForReveal;

    /// <summary>Pegasus or the full-screen visualizer covers the window (<see cref="PegasusPlayer.Covers"/>,
    /// <c>VizFullScreen.Covers</c>), so nothing in the shell beneath it is drawn. <see cref="CardRoomBeat"/>
    /// reads it to stop the home banner's room.</summary>
    internal bool Covered => _covered;

    private bool _covered;

    /// <summary>
    /// The body's opacity, worked out from both reasons to hide it. The reveal and Pegasus each set
    /// their own flag and ask for the result again, so neither can draw the body while the other wants
    /// it hidden, in whatever order they arrive.
    /// </summary>
    private void ShowBody() => ShellBody.Opacity = _bodyHeldForReveal || _covered ? 0 : 1;

    /// <summary>
    /// Stops drawing the shell while Pegasus or the full-screen visualizer covers it, and draws it again
    /// before either one goes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> Uno's Skia renderer records and draws the whole visual tree every frame. Under
    /// Pegasus that would be the library, the art band with its live glass, the header record and the
    /// home banner's drifting room, all at full cost, behind an opaque black view. Stopping it reduces
    /// the work a lot: on an M4 Mac with a Release AOT build, full screen and a track playing, recorded
    /// frames fell from 70 to 96 a second to the display's 61 to 66, gaps over 25 ms fell from about 6
    /// a second to under 1, and process CPU fell from about 170% to about 100%.</para>
    ///
    /// <para><b>Opacity, not visibility</b>, as for the reveal: Uno skips a subtree at opacity zero
    /// before recording anything (<c>Visual.IsRenderSuppressed</c>), and layout is not affected, so the
    /// library is laid out at the window's size the moment Pegasus goes and nothing is rebuilt then. Only
    /// the two layers that nothing else fades are set here. The seek bars and the record belong to
    /// <see cref="ArtDisplay"/>'s fades, so the record is collapsed through <see cref="SyncRecordShown"/>
    /// and the seek bars are left drawn.</para>
    ///
    /// <para><b>The frost plate is held</b> for as long as this lasts. It photographs
    /// <c>BackdropLayer</c>, and Pegasus changes the window's size in both directions, which asks for a
    /// new photograph each time. A photograph taken while the layer is not drawn would be blank, and the
    /// cards would show it for a moment after Pegasus goes. While held, the cards stay on live glass
    /// (not drawn either), and the photograph is taken once Pegasus has gone and the window has
    /// settled.</para>
    ///
    /// <para><b>The main window's home rooms are stopped</b> too, by telling that home that it is
    /// covered (<c>HomeView.Covered</c>). Each of their ticks asks Uno for an extra frame recording,
    /// about 45 a second under Pegasus. Only that home is stopped: Pegasus's own home is a second
    /// <c>HomeView</c> on the same beat, and stopping the beat itself would freeze its rooms too.</para>
    /// </remarks>
    private void Cover()
    {
        var covered = PegasusOverlay.Covers || VizOverlay.Covers;
        if (covered == _covered)
            return;
        _covered = covered;

        BackdropLayer.Opacity = covered ? 0 : 1;
        ShowBody();
        SyncRecordShown();

        if (covered)
            FrostPlate.Current.Hold(FrostHold.Covered);
        else
            FrostPlate.Current.Release(FrostHold.Covered);

        (RootFrame.Content as MainPage)?.CoverHome(covered);

        RPM.Core.Diagnostics.Scope.Said("window", covered
            ? $"shell covered by {(PegasusOverlay.Covers ? "Pegasus" : "the visualizer")}"
            : "shell drawn again");
    }

    /// <summary>Whether the library page is the one in the frame. It is read from the frame each time
    /// and not saved, because a saved copy would have to be set before the first navigation by
    /// whichever code runs first.</summary>
    private bool OnLibrary => RootFrame.Content is MainPage;

    /// <summary>Whether a setup screen is the page in the frame: the wizard, or the screen for a
    /// collection that would not open. It is read each time and not saved, for the same reason as
    /// <see cref="OnLibrary"/>.</summary>
    private bool OnSetup => RootFrame.Content is Setup.WizardPage or Setup.LibraryProblemPage;

    /// <summary>
    /// Whether the disc is on screen: three facts combined with AND, in the one place that may set it.
    ///
    /// <para>The facts arrive at different times and in no fixed order: the settings hook on Loaded, the
    /// frame's first Navigated shortly after, and the amp whenever the user asks for it. Each one sets
    /// its own fact and then asks for the result to be worked out again. If each one set the disc
    /// directly, the last to arrive would overwrite the others, and a record switched off in Settings
    /// would come back when the library page arrived.</para>
    /// </summary>
    private void SyncRecordShown()
        => TopRecord.Visibility = OnLibrary && !_ampUp && !_covered && ShellArt.RecordShown
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>The URI of the picture the shell's backdrop uses now: the user's chosen picture, or the
    /// built-in one when there is none or the file is missing. A missing custom file reverts to the
    /// built-in picture and does not leave the window blank.</summary>
    private string? _appliedBackgroundUri;

    private void ApplyBackground(SettingsService settings)
    {
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("shell in");
        var custom = settings.CustomBackgroundPath;
        // Both the custom picture and the built-in default are raster files, so one BitmapImage path
        // covers both.
        //
        // The built-in default is RPaMp's ground (Backdrop.BuiltIn) unless a built-in was named in
        // Settings. A picture that the user chose from disk wins over both.
        // The supporter status is read here each time and not saved, so when support lapses the gold
        // backdrop goes away at the next restyle, and when support starts it appears the same way.
        var builtIn = Backdrop.Resolve(settings.BuiltInBackground,
            Services.Account.SupporterReach.For(
                App.Services.GetRequiredService<Services.Account.SupporterService>().Standing));
        var uri = !string.IsNullOrWhiteSpace(custom) && System.IO.File.Exists(custom)
            ? new Uri(custom)
            : new Uri(builtIn);

        // The settings windows are painted with a shared brush and not with this image, and the brush
        // always shows the built-in picture, whether or not the user has a picture of their own. It is
        // updated here so that those windows are lit the same way as the shell. The brush object is
        // shared, so setting its source is enough and nothing has to be rebuilt.
        if (Application.Current.Resources["AppBackgroundBrush"] is Microsoft.UI.Xaml.Media.ImageBrush brush)
            brush.ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(builtIn));
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("brush");

        // Changed fires for every setting, not just the backdrop. Rebuilding the BitmapImage reloads
        // the full-screen background and flashes the whole window, so it is only touched when the
        // backdrop actually changed. An unrelated setting (for example the album-wall size) must not
        // repaint the app.
        if (_appliedBackgroundUri == uri.AbsoluteUri)
        {
            RPM.Desktop.Diagnostics.SwitchDiag.Mark("backdrop");
            return;
        }
        _appliedBackgroundUri = uri.AbsoluteUri;

        // The backdrop as a GROUND: a picture at full strength with nothing over it. It has the same
        // shape as a record's or an artist's ground, because all three answer the same question (what
        // is behind the window content). With one shape, the one control (see ShellGround) can hold
        // all three and none of them is placed under another.
        var picture = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);

        // The decode's own event is exact and costs nothing. The plate that REPLACES the live glass
        // below must be taken once the picture is really there, and a settle timed from this line
        // cannot be sure to catch it: on a window that opens behind another one it can be a few
        // milliseconds too early, and the cards show a flat dark slab until the next resize.
        picture.ImageOpened += (_, _) => FrostPlate.Current.Invalidate(FrostReason.Motion);

        _appGround = (uri.AbsoluteUri, new PageBackdrop.Ground(
            Colour: null, picture, WashStrength: 1, Veil: 0, Weight: null));

        // The ground goes onto the screen only if no page's ground is in front of it. If the listener
        // changes the backdrop while a record is open, that changes what the window goes back to, not
        // what is shown now.
        SyncPageBack();

        // A new picture behind everything needs a new frost for the cards on top of it. This call only
        // puts them back on live glass while the change is under way.
        FrostPlate.Current.Invalidate(FrostReason.Artwork);
    }

    /// <summary>The app's own backdrop, as a ground, stored with the URI of its picture as the owner.
    /// Changing the backdrop in Settings gives a new owner, so the ground dissolves to the new one.
    /// Setting the same backdrop again is not a change.</summary>
    private (string Owner, PageBackdrop.Ground Ground)? _appGround;

    // ── which of the three grounds the window is standing on ──

    /// <summary>
    /// Gives the ground control the ground to show. This is the ONE place that decides. The app's own
    /// backdrop is chosen here by name, and it is not left to show through whatever is in front of it.
    ///
    /// <para>The artwork is not part of this. A track that is playing paints the window with its own
    /// blurred copy, and it does that OVER the ground and not instead of it. So the ground still matters
    /// while it is covered: the artwork dissolves in over it, and it is visible again as soon as the
    /// drawer is parked. If the ground were switched back to the backdrop when a track starts, a
    /// record's colours would turn into the wallpaper.</para>
    ///
    /// <para>There are three cases (see <see cref="PageBackdrop.Showing"/>), and the middle one is why
    /// the other two are not enough:</para>
    /// <list type="bullet">
    /// <item><b>No page is open</b>: the backdrop, by name. This is the only call that brings it
    /// back.</item>
    /// <item><b>A page is open but has no ground yet</b>: nothing happens. A page has no ground until
    /// its cover or photograph arrives. If the backdrop were shown then, a reader moving from a record
    /// to an artist would see the backdrop in the gap between two pages that both have colours of
    /// their own.</item>
    /// <item><b>A page with a ground</b>: that ground, stored with the page as its owner. If the same
    /// page fills in its ground, the ground is updated in place. If another page opens, the ground
    /// dissolves to the new one.</item>
    /// </list>
    /// </summary>
    private void SyncPageBack()
    {
        // In both cases, what is behind the frosted cards is about to change. The wait is the
        // artwork's length and not the ground's, because a page keeps filling in after its ground has
        // arrived. See FrostPlate.ArtworkSettle, which describes the ghost images that a shorter wait
        // leaves.
        FrostPlate.Current.Invalidate(FrostReason.Artwork);

        if (PageBackdrop.Showing is not { } worn)
        {
            if (_appGround is { } app)
                Ground.Show(app.Owner, app.Ground);
            return;
        }

        if (worn.Ground is { } ground)
            Ground.Show(worn.Page, ground);
    }

    /// <summary>Clears the splash in two steps: the rpm wordmark fades out first, then the black overlay
    /// fades away so the already-rendered interface fades in behind it, with no sudden change.</summary>
    /// <summary>Whether the reveal has been armed. There is one splash per run, and Loaded fires more
    /// than once.</summary>
    private bool _revealArmed;

    /// <summary>How long the wordmark takes to go, and then the black behind it. There are two steps and
    /// not one, so the wordmark clears while the window underneath is still covered.</summary>
    private const double SplashLogoMs = 280;

    private const double SplashBlackMs = 450;

    /// <summary>The wordmark's resting opacity. It is set here as well as in the markup, because a
    /// composition animation must be told where it starts from (see <see cref="SplashFade"/>): a
    /// property that the compositor owns cannot be read back.</summary>
    private const double SplashLogoRest = 0.5;

    private void DismissSplash(RevealWay way)
    {
        if (SplashOverlay is null)
            return;
        SplashOverlay.IsHitTestVisible = false;   // clicks pass through as soon as the reveal starts

        // ── the splash is a window of its own ───────────────────────────────────────────────────
        //
        // In that case there is nothing to reveal here. This window has been cloaked since before it
        // was activated, so the interface goes onto the canvas at once and the black over it is
        // removed in the same turn. Nobody can see this, because the window itself is not visible. The
        // user sees the card dissolve over the app once the app is up, which App.RestorePlacement
        // runs after the window has been brought back. Fading the inside of this window as well would
        // only keep the card on screen for 730 ms with the finished app behind it.
        if (Startup.SplashWindow.IsUp)
        {
            _bodyHeldForReveal = false;
            ShowBody();
            RPM.Core.Diagnostics.Scope.Said("boot", $"reveal begins: splash window ({way} gate)");
            FinishReveal();
            return;
        }

        // The interface is drawn again HERE and not when the black clears. The first step of the
        // reveal makes this possible: the wordmark's 280 ms runs with the overlay still fully opaque,
        // so the frame that records every visual under it for the first time is not seen. That frame is
        // expensive, and without this it would land on the first step of the fade.
        _bodyHeldForReveal = false;
        ShowBody();

        // ── the fade, on the compositor, whatever the drawing thread is doing ────────────────────
        //
        // A composition animation ticks on the compositor at the display's rate, and a drawing thread
        // that is busy laying out the home screen is not the thread that draws it. So the fade does not
        // depend on how the gate opened (`way`): a busy thread no longer makes the fade stutter.
        //
        // Trap: when the entrance storyboard has finished, a finished DoubleAnimation HOLDS its
        // property and outranks everything written to it afterwards. The hold is released here, before
        // the compositor is given the same property. Nothing jumps: the storyboard ends at the resting
        // opacity that the markup already carries, so releasing it leaves the mark where it is.
        _wordmark?.Stop();
        _wordmark = null;

        if (SplashFade.Start(
                SplashLogo, SplashOverlay, SplashLogoMs, SplashBlackMs, SplashLogoRest, FinishReveal))
        {
            // This line says WHAT drives the fade. How the gate opened is a separate fact, logged on
            // the same channel just before. A gate that opened on a deadline does not mean the reveal
            // has no fade. The time from here to "reveal done" shows whether the fade really ran: about
            // 1 ms means there was no fade.
            RPM.Core.Diagnostics.Scope.Said("boot", $"reveal begins: fade on the compositor ({way} gate)");
            return;
        }

        RPM.Core.Diagnostics.Scope.Said("boot", $"reveal begins: {way}, no compositor");

        // There is no system compositor, which is the case for every head except WinUI. The
        // storyboard below is the path for those heads. On Skia every DoubleAnimation is a
        // DispatcherDoubleAnimator on CompositionTarget.Rendering, so a thread that has just reported
        // that it is held cannot animate anything. In that case the interface is shown at once, which
        // takes one frame.
        if (way == RevealWay.Ceiling)
        {
            SplashLogo.Opacity = 0;
            SplashOverlay.Opacity = 0;
            FinishReveal();
            return;
        }

        var logoFade = new DoubleAnimation
        {
            From = SplashLogoRest,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(SplashLogoMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(logoFade, SplashLogo);
        Storyboard.SetTargetProperty(logoFade, "Opacity");

        var overlayFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(SplashLogoMs),   // starts after the wordmark has gone
            Duration = new Duration(TimeSpan.FromMilliseconds(SplashBlackMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(overlayFade, SplashOverlay);
        Storyboard.SetTargetProperty(overlayFade, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(logoFade);
        sb.Children.Add(overlayFade);
        sb.Completed += (_, _) => FinishReveal();
        Services.Shell.Storyboards.Play(sb);
    }

    /// <summary>
    /// The splash is off. Both reveals end here, so the window's saved size is restored at the same
    /// point whether the interface was faded in or shown at once.
    /// </summary>
    private void FinishReveal()
    {
        // Collapsed, not just transparent: an overlay left in the tree at zero opacity is still a
        // visual that the renderer walks past on every frame. Collapsing it before Revealed is raised
        // also removes the splash from the tree before RestorePlacement resizes the window, so an
        // incomplete repaint during that resize cannot show a wordmark.
        if (SplashOverlay is not null)
            SplashOverlay.Visibility = Visibility.Collapsed;
        RPM.Core.Diagnostics.Scope.Said("boot", "reveal done");

        // The first-run hints wait for this. If they were drawn earlier, they would be under the
        // splash, and their fade-in would finish behind the black sheet before it lifted.
        RefreshCoachmarks();

        // One-shot: each waiting handler is called once. A second splash (there is never one) must
        // not call them again.
        HasRevealed = true;
        var waiting = Revealed;
        Revealed = null;
        waiting?.Invoke();
    }

    /// <summary>
    /// Raised once, on the interface's first visible frame: the splash has faded and what is on screen
    /// is the app.
    ///
    /// <para>The window's saved size is restored on this event. If it were restored earlier, the window
    /// would resize itself behind a black rectangle, which looks like a slow start.</para>
    /// </summary>
    public static event Action? Revealed;

    /// <summary>True once <see cref="Revealed"/> has been raised. A page that arrives after it has nothing
    /// left to wait for.</summary>
    public static bool HasRevealed { get; private set; }

    /// <summary>Shows the artwork band and the library-only menu items on the library page. On the setup
    /// page it collapses them, and the setup page gets the whole window for its picker.</summary>
    private void OnNavigated()
    {
        // The hints are about the library page, so after any navigation they may be wrong until they
        // are evaluated again. The refresh is queued, so it runs after the new page has been laid out
        // and its controls have a position to point at.
        RefreshCoachmarks();

        var library = OnLibrary;
        // Use the right height from the start: parked (stopped) while the art is idle (a new library is
        // stopped, so it opens closed with the album wall under the rpm mark), and the saved open
        // height once a track is up. This avoids a flash of open and then collapsed.
        _drawerClosed = library && ShellArt.IsIdle;
        var band = library ? (_drawerClosed ? ShellArt.StoppedBandHeight : _artBandHeight) : 0;
        ApplyBand(band);
        ShellArt.Visibility = library ? Visibility.Visible : Visibility.Collapsed;

        // The drawer's handle and the artwork's right-click catcher go with the band.
        //
        // Collapsing the row is NOT enough. The splitter is bottom aligned in row 0 with a negative
        // bottom margin, so at a height of zero it does not disappear. Its 14 pt strip stays over the
        // top of whatever is in row 1, keeps its pointer handlers, and shows its grip on hover. On the
        // wizard that would be a drag handle for a drawer that is not there, over the first screen
        // that anybody sees.
        var bandParts = library ? Visibility.Visible : Visibility.Collapsed;
        ShellArtSplitter.Visibility = bandParts;
        ArtHeaderCatcher.Visibility = bandParts;
        // The record overlay is its own layer, so it does not hide with ArtDisplay. It follows the
        // same flag, but that flag is not the only input. See SyncRecordShown.
        SyncRecordShown();
        // On the library page the deck's own hamburger opens this menu, so the edge one is hidden.
        // On Windows and Linux the frosted window buttons take the top-right corner, so the edge
        // hamburger is hidden there too, on every page.
        //
        // It is also hidden on the wizard. During setup the menu only offers Settings, Help and
        // Sign out, which are three ways to leave a flow whose purpose is to get the user to a
        // collection. The screen for a collection that would not open follows the same rule, because
        // its two buttons are the way out of it.
        HamburgerButton.Visibility =
            library || OnSetup || OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                ? Visibility.Collapsed
                : Visibility.Visible;
        PositionHamburger();
    }

    /// <summary>Opens the shell's hamburger menu at a given element. The deck's own hamburger button
    /// calls this so that it shows the same flyout as the edge button.</summary>
    public void ShowMenuAt(FrameworkElement target) => HamburgerButton.Flyout?.ShowAt(target);

    /// <summary>Places the hamburger just clear of the record's left edge, at its own height in the
    /// band, so it sits on the artwork but never on the disc. When there is no band, it is not placed
    /// against the record at all (the right margin becomes the gutter).</summary>
    private void PositionHamburger()
    {
        const double top = 16, height = 28, gap = 12;
        var bottom = top + height;      // its lowest edge reaches furthest into the disc
        var inset = ShellArt.RecordBulgeAt(bottom);
        HamburgerButton.Margin = new Thickness(0, top, inset + gap, 0);
    }

    /// <summary>The frosted caption cluster (minimize, maximize and close, on Windows and Linux) sits in
    /// the top-right corner, which is the same corner that the record disc reaches into. So it avoids
    /// the disc like the hamburger does: it is placed just clear of the record's edge, at its own
    /// height. Without a record (no band, or the setup page) the right margin returns to the resting
    /// gutter. It has no visible effect on macOS, where the cluster is hidden.</summary>
    private void PositionWinCaptionButtons()
    {
        const double top = 11, gutter = 24, gap = 10;
        var height = WinCaptionButtons.ActualHeight > 0 ? WinCaptionButtons.ActualHeight : 15;
        // There is no disc when the art is idle (stopped, disc gone), so there is nothing to avoid:
        // sit at the card edge.
        var reach = ShellArt.IsIdle ? 0 : ShellArt.RecordShadowBulgeAt(top + height);
        // Resting position: level with the library card's right edge, 24 px, which is the app's
        // content inset (MainPage's `Grid Margin="24"`). The cluster and the card share the window's
        // content space, so they extend equally and no correction is needed. When a disc is up, the
        // cluster moves left to clear it.
        WinCaptionButtons.Margin = new Thickness(0, top, Math.Max(gutter, reach + gap), 0);
        RefreshCaptionRegions();
    }

    /// <summary>Whether the scale hook is in place. Loaded fires again each time the page is entered
    /// and the handler must not be added twice.</summary>
    private bool _scaleHooked;

    /// <summary>
    /// Gives the top strip to Windows as the window's title bar.
    ///
    /// <para>The system does the dragging from there: snap layouts, snap to an edge, drag to the top to
    /// maximize, and drag a maximized window to restore it. A managed drag cannot do any of these,
    /// because they only run inside the system's own move loop (see <see cref="WindowCaption"/>).</para>
    ///
    /// <para><b>Where the band stops.</b> The system hit-tests a region that was given to it before XAML
    /// sees the input, so z-order no longer protects what is drawn on top. Everything in this band that
    /// keeps its own gestures is on the right: the caption cluster, the hamburger, and the record disc
    /// that both of them avoid. So the band runs from the left edge to the leftmost of those three and
    /// no further. The maximize button is given to the system as its own region, which shows the
    /// Windows 11 snap-layout flyout on it. Uno routes the press back into the XAML button, so
    /// <see cref="Caption_MaxRestore_Click"/> still runs.</para>
    ///
    /// <para><b>When the strip is taken back.</b> Only the library view uses this chrome. RPaMp, Pegasus
    /// and the full-screen visualizer cover the strip completely, and a covered strip that still drags
    /// the window would turn a click into a drag that the user cannot see.</para>
    /// </summary>
    internal void RefreshCaptionRegions()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (App.AppWindow is not { } window)
            return;

        var scale = XamlRoot?.RasterizationScale ?? 0;
        var strip = EdgeOf(TitleBarStrip);
        var band = TitleBarStrip.ActualHeight;
        var width = ActualWidth;
        var shellOnScreen = App.Services.GetRequiredService<ViewModeService>().Current == AppView.Library
            && !FullScreenWindow.IsOn;

        if (!shellOnScreen || scale <= 0 || band <= 0 || width <= 0 || strip.X > width)
        {
            WindowCaption.Apply(window, [], []);
            return;
        }

        // The disc is measured at both edges of the band and not in the middle: it is a circle, so
        // the row that reaches furthest left is the one that the band has to clear.
        var edge = width;
        if (!ShellArt.IsIdle)
            edge = Math.Min(edge, width - Math.Max(ShellArt.RecordShadowBulgeAt(0), ShellArt.RecordShadowBulgeAt(band)));
        edge = Math.Min(edge, LeftEdgeOf(WinCaptionButtons));
        edge = Math.Min(edge, LeftEdgeOf(HamburgerButton));

        var caption = edge > strip.X && Physical(strip.X, strip.Y, edge - strip.X, band, scale) is { } band0
            ? new[] { band0 }
            : Array.Empty<Windows.Graphics.RectInt32>();

        var maximize = MaxCaptionButton.Visibility == Visibility.Visible && MaxCaptionButton.ActualWidth > 0
            && Physical(LeftEdgeOf(MaxCaptionButton), TopEdgeOf(MaxCaptionButton),
                MaxCaptionButton.ActualWidth, MaxCaptionButton.ActualHeight, scale) is { } max0
            ? new[] { max0 }
            : Array.Empty<Windows.Graphics.RectInt32>();

        WindowCaption.Apply(window, caption, maximize);
    }

    /// <summary>An element's left edge in the page's own coordinates. A hidden or unmeasured element
    /// takes no space, so it returns a value that <see cref="Math.Min(double, double)"/> never
    /// picks.</summary>
    private double LeftEdgeOf(FrameworkElement element) => EdgeOf(element).X;

    /// <summary>An element's top edge in the page's own coordinates. See <see cref="LeftEdgeOf"/>.</summary>
    private double TopEdgeOf(FrameworkElement element) => EdgeOf(element).Y;

    private Windows.Foundation.Point EdgeOf(FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return new Windows.Foundation.Point(double.MaxValue, double.MaxValue);
        try { return element.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)); }
        catch { return new Windows.Foundation.Point(double.MaxValue, double.MaxValue); }
    }

    /// <summary>
    /// A rectangle in the page's coordinates, converted to the physical pixels that the system
    /// hit-tests in. Returns null when any edge of it is not a number that a rectangle can be made of.
    ///
    /// <para><b>Null, and not a rectangle that is slightly wrong.</b> <see cref="EdgeOf"/> returns
    /// <c>double.MaxValue</c> for an element that is hidden, unmeasured, or briefly not in this tree.
    /// The last case happens during a resize, when TransformToVisual throws and the catch there returns
    /// that value. After scaling and casting, the value is not clamped: a cast of a double outside the
    /// range of int is unchecked, and on x64 it gives int.MinValue. The window would then give the
    /// system a non-client region at -2,147,483,648 through
    /// <see cref="Services.Shell.WindowCaption.Apply"/>, and the process would crash in ntdll with
    /// STATUS_HEAP_CORRUPTION during a drag. This only happens on the WinUI head. On the Skia head the
    /// call does nothing, because Uno's X11 and Win32 hosts have no non-client pointer source, so the
    /// numbers never leave managed code.</para>
    /// </summary>
    private static Windows.Graphics.RectInt32? Physical(double x, double y, double width, double height, double scale)
    {
        var left = x * scale;
        var top = y * scale;
        var w = width * scale;
        var h = height * scale;

        if (!Fits(left) || !Fits(top) || !Fits(w) || !Fits(h))
            return null;

        return new Windows.Graphics.RectInt32
        {
            X = (int)Math.Round(left),
            Y = (int)Math.Round(top),
            Width = (int)Math.Round(w),
            Height = (int)Math.Round(h),
        };

        // The conversion to int rounds, so the bound is checked against the range of the rounded
        // value and not the raw one.
        static bool Fits(double v) => double.IsFinite(v) && v >= int.MinValue && v <= int.MaxValue;
    }

    // ── artwork header: drag to resize ──

    // Under this much pointer travel a press is a click, not a drag.
    private const double GripDragThreshold = 5;
    // Two clicks closer together than this count as a double-click (park).
    private const long GripDoubleClickMs = 400;
    private bool _artDragging, _artPressed;
    private double _artDragStartY, _artStartHeight, _artPressY;
    private long _lastGripTapTick, _lastUnparkTick;
    private bool _artHovered;

    // Band-height tween, which slides the header between its parked and open heights.
    private double _bandFrom, _bandTo;
    private readonly System.Diagnostics.Stopwatch _bandClock = new();
    private bool _bandTweening;

    /// <summary>The shortest the header may be dragged: the band height at which the now-playing cover
    /// stops shrinking, which ArtDisplay publishes live. Below it the square keeps its size and moves up
    /// over the rpm mark, so the drag stops exactly there.</summary>
    private double MinArtBand => ShellArt.MinHeaderHeight();

    /// <summary>The tallest the header may be: the band height at which the record and the cover reach
    /// full size, published by ArtDisplay, but never below the minimum. It is a fixed number, because the
    /// artwork is framed to the header, so no photograph can lower it.</summary>
    private double BandCeiling => Math.Max(MinArtBand, ShellArt.MaxHeaderHeight());

    /// <summary>Shows a band height: the row the content sits under, and the artwork's own header zone.
    /// It does NOT change the saved open height. The caller does that.</summary>
    private void ApplyBand(double h)
    {
        _displayBand = h;
        ArtBandRow.Height = new GridLength(h);
        ShellArt.ChromeHeight = h;   // the artwork's header zone follows

        // The grip sits at the bottom of this row and the library page moves down with it, so both
        // hints that point at either are now out of date. The refresh is queued, so a whole tween
        // costs one redraw and not one per frame.
        RefreshCoachmarks();
    }

    /// <summary>A height set by dragging or by the ceiling: it is shown AND saved as the open
    /// height.</summary>
    private void SetBandFromDrag(double h)
    {
        _artBandHeight = h;
        ApplyBand(h);
    }

    /// <summary>The art went idle or a track came back: park the drawer under the rpm mark, or reopen it
    /// to the saved height. In both cases the grids are pinned so that the rows under the drawer keep
    /// their screen position while the band slides.</summary>
    private void OnArtIdleChanged(bool idle)
    {
        _drawerClosed = idle;

        // The band belongs to the library page, and this is the second place that must know it.
        // OnNavigated collapses the band on any other page. This handler fires on the artwork's own
        // timing and would reopen the band, so the setup wizard would draw a 165 pt strip of empty
        // header across its top. The page is read here each time, as OnNavigated reads it, so the
        // result does not depend on which of the two ran last.
        if (!OnLibrary)
            return;

        // A drag in progress owns the band, so the height is not animated against the pointer.
        if (_artDragging)
            return;
        if (idle)
        {
            AnimateBandTo(ShellArt.StoppedBandHeight, compensate: true, updateRemembered: false);
        }
        else
        {
            // Reopen to the saved height, but never taller than the artwork that frames it now.
            //
            // Do not use the artwork's own ceiling here. That is correct for the chevron, which is
            // the one gesture that asks for it, but this handler runs whenever the art comes back,
            // which is every track. Every track would then discard the height that the user dragged
            // to. An automatic open to the ceiling belongs on the chevron's own path.
            _artBandHeight = Math.Min(_artBandHeight, BandCeiling);
            AnimateBandTo(_artBandHeight, compensate: true, updateRemembered: false);
        }
    }

    // Per-tween flags: whether to pin the grids on each frame, and whether the height being animated
    // to is also the new saved open height.
    private bool _bandCompensate, _bandUpdateRemembered;

    private void AnimateBandTo(double target, bool compensate, bool updateRemembered)
    {
        if (_bandTweening && Math.Abs(_bandTo - target) < 0.5)
            return;   // already heading there: do not restart on every frame (Layout repeats the ceiling)
        _bandCompensate = compensate;
        _bandUpdateRemembered = updateRemembered;
        _bandFrom = _displayBand;
        _bandTo = target;
        _bandClock.Restart();
        if (!_bandTweening)
        {
            _bandTweening = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnBandTween;
        Services.Shell.FrameWatch.Began("band-tween");
        }
    }

    private void OnBandTween(object? sender, object e)
    {
        // The drawer uses the SHARED transition clock, so it moves exactly with the record, the wash
        // and the backdrop's fade to and from idle.
        const double dur = ArtDisplay.TransitionSeconds;
        var t = Math.Clamp(_bandClock.Elapsed.TotalSeconds / dur, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3);   // easeOutCubic
        var prev = _displayBand;
        var next = _bandFrom + (_bandTo - _bandFrom) * eased;
        ApplyBand(next);
        if (_bandUpdateRemembered)
            _artBandHeight = next;
        // Pin the grids: as the drawer's foot moves by (next - prev), scroll each grid by the same
        // amount, so the row that a given item is on does not move while the band slides.
        if (_bandCompensate && Math.Abs(next - prev) > 0.001 && RootFrame.Content is MainPage review)
            review.CompensateScroll(next - prev);
        if (t >= 1)
            StopBandTween();
    }

    private void StopBandTween()
    {
        if (!_bandTweening)
            return;
        _bandTweening = false;
        _bandClock.Stop();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnBandTween;
        Services.Shell.FrameWatch.Ended("band-tween");
    }

    private void ArtSplitter_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Whether this becomes a drag or a double click, the user has found the grip.
        Coachmarks.CoachmarkState.Seen(RPM.Core.Library.CoachMark.ArtHeader);

        _artPressed = true;
        _artDragging = false;   // it becomes a drag only after the pointer moves past the threshold
        _artPressY = e.GetCurrentPoint(this).Position.Y;
        _artDragStartY = _artPressY;
        _artStartHeight = ArtBandRow.ActualHeight;
        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ArtSplitter_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_artPressed)
            return;
        var y = e.GetCurrentPoint(this).Position.Y;
        if (!_artDragging)
        {
            if (Math.Abs(y - _artPressY) < GripDragThreshold)
                return;   // still within the click tolerance, so not a drag yet
            _artDragging = true;
            StopBandTween();            // the user is in control now
            if (ShellArt.IsIdle)
            {
                // Dragging the parked drawer open brings the disc and the art back. Use Unpark and not
                // EnterTrack: a held park (the saved double-click) blocks EnterTrack completely, and
                // pulling the drawer open by hand lifts that hold, just as a click on it does.
                ShellArt.UnparkByUser();
                App.Services.GetRequiredService<SettingsService>().ArtDrawerParked = false;
            }
        }
        // Dragging is manual control: the drawer is open and this becomes the saved height. The height
        // is limited to the artwork ceiling, so it cannot be dragged taller than the picture.
        SetBandFromDrag(Math.Clamp(_artStartHeight + (y - _artDragStartY), MinArtBand, BandCeiling));
        e.Handled = true;
    }

    private void ArtSplitter_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_artPressed)
            return;
        _artPressed = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        if (_artDragging)
        {
            _artDragging = false;
            ShowArtGrip(_artHovered, _artHovered);
            return;
        }

        // No travel: a click. A second click inside the time window parks the drawer. A single click on
        // the parked grip pulls the drawer open. The timing tells them apart, because the framework tap
        // gestures are unreliable in Uno Skia.
        var now = Environment.TickCount64;
        if (now - _lastGripTapTick <= GripDoubleClickMs)
        {
            _lastGripTapTick = 0;
            GripDoubleClicked();
        }
        else
        {
            _lastGripTapTick = now;
            GripClicked();
        }
    }

    /// <summary>A single click. On the parked grip it pulls the drawer (and the disc) back open with one
    /// coordinated animation. On the open grip it does nothing, because the drag owns the press.</summary>
    private void GripClicked()
    {
        if (!_drawerClosed)
            return;
        _lastUnparkTick = Environment.TickCount64;
        ShellArt.UnparkByUser();
        App.Services.GetRequiredService<SettingsService>().ArtDrawerParked = false;
    }

    /// <summary>A double click parks the drawer: the disc moves off to the corner and the band slides up
    /// under the rpm mark. It also works while a track is playing.</summary>
    private void GripDoubleClicked()
    {
        if (_drawerClosed)
            return;   // already parked
        if (Environment.TickCount64 - _lastUnparkTick < GripDoubleClickMs)
            return;   // the first of the two clicks just unparked, so do not park again at once
        ShellArt.ParkByUser();
        App.Services.GetRequiredService<SettingsService>().ArtDrawerParked = true;
    }

    private void ArtSplitter_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _artHovered = true;
        ShowArtGrip(shown: true, lit: true);
    }

    private void ArtSplitter_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _artHovered = false;
        if (!_artDragging)
            ShowArtGrip(shown: false, lit: false);
    }

    // The grip's fill is the glass. Only its thin edge lights up on hover or drag. The pair of brushes
    // is app-level (Generic.xaml) because the panel splitter uses the same pair: two handles that lit
    // differently would look like two different controls.
    /// <summary>How long both drag handles stay up at startup before the hover rule takes over. It is
    /// shared with the panel splitter's own startup display (MainPage), so the two appear and go
    /// together.</summary>
    internal const double GripDebutSeconds = 5;
    private bool _gripDebuted;

    // The pill only exists while the pointer is on the strip (or a drag is in progress). A handle that
    // is always drawn would be a permanent mark across the artwork for a gesture that nobody is making.
    // It is collapsed and not transparent, because the glass fill costs a backdrop read on every frame
    // while it is in the tree.
    /// <summary>Shown and lit are two different things. The pointer on the strip (or a drag in progress)
    /// gives the bright edge. The startup display shows the handle at REST, in the faint white it has
    /// when the pointer is not there. A grip that appeared lit at startup would look as if the pointer
    /// were on it when the pointer is somewhere else.</summary>
    /// <summary>Whether the first-run hint about the grip is keeping it on screen. This is the one
    /// thing that outranks the hover rule below; see ShellPage.Coachmarks.HoldArtGrip.</summary>
    private bool _coachHoldsGrip;

    private void ShowArtGrip(bool shown, bool lit)
    {
        // A hint that points at the grip keeps it visible, whatever the pointer is doing. The check
        // is here, in the one method all callers use, and not in each of the four callers: a caller
        // that forgot would hide the grip while an arrow on screen points at it.
        if (!shown && _coachHoldsGrip)
            return;

        ArtGrip.Stroke = GripEdge.Brush(lit);
        ArtGrip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    // Auth lives on the ShellModel built by dependency injection (the services are ready by the time
    // it is constructed) and is reached through DataContext, so the constructor does not use
    // App.Services for it.
    private AuthService? Auth => (DataContext as ShellModel)?.Auth;

    private void Menu_Opening(object? sender, object e)
    {
        // The account items are in this menu: sign in when signed out, sign out when signed in.
        var signedIn = Auth?.IsLoggedIn == true;
        SignInItem.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutItem.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutSeparator.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        // Send feedback is for closed beta testers only: their reports carry the log and the
        // screenshot, and nobody else's may (ReportScope).
        FeedbackItem.Visibility = App.Services.GetRequiredService<FeedbackService>().CurrentScope.IsFull
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Send feedback takes a picture of the app once the menu has gone. The wait is set up here so
        // it exists before any item in this menu can be clicked (ShellPage.Feedback.cs).
        _menuGone = new TaskCompletionSource();
    }

    private void SignOut_Click(object sender, RoutedEventArgs e) => Auth?.Logout();

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (Auth is { IsLoggedIn: false } auth)
            await Dialogs.LoginDialog.ShowAsync(auth, XamlRoot);
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is { } root)
            await Settings.SettingsDialog.ShowAsync(root, App.Services);
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is { } root)
            await About.AboutDialog.ShowAsync(root);
    }

    private void Help_Click(object sender, RoutedEventArgs e) => Open(RPM.Core.Cloud.RpmSite.Support);

    private void Privacy_Click(object sender, RoutedEventArgs e) => Open(RPM.Core.Cloud.RpmSite.Privacy);

    private void Terms_Click(object sender, RoutedEventArgs e) => Open(RPM.Core.Cloud.RpmSite.Terms);

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best effort */ }
    }
}

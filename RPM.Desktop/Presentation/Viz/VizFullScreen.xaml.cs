using RPM.Core.Diagnostics;
using RPM.Desktop.Presentation.RPaMp;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Viz;

/// <summary>
/// A gallery visualizer, full screen. The stage fills the whole window. The now-playing controls that the
/// header normally shows (cover, title, artist, album, countdown, seek bar and transport keys) run along the
/// foot of the screen instead. So the picture is the visualizer and nothing else.
///
/// <para>Everything here is opened from the Visualizations gallery and belongs to it. Pegasus is not one of
/// these. It is one of the app's three views and has its own host
/// (<see cref="Presentation.Pegasus.PegasusPlayer"/>). Its record and its words are the picture, not a strip laid along the foot of another screen.</para>
///
/// <para>The chrome is not built here. The header (<see cref="ArtDisplay"/>) resolves a track's identity and
/// decodes its cover. This control reads those values from the header. So the two always agree, and each tag
/// read and each decode happens once.</para>
///
/// <para>A stage is built the first time its visualizer is launched, and it is kept while it is the one being
/// shown. A stage owns a GL context. So launching the SAME visualizer again switches it back on
/// (<see cref="IVizStage.SetActive"/>), and no new context is created. Launching a different visualizer
/// swaps the stage. There is never more than one live GL context.</para>
/// </summary>
public sealed partial class VizFullScreen : UserControl
{
    /// <summary>How long the chrome stays up after the last pointer move.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long the visualizer takes to fade in over the app.</summary>
    private const int OpenFadeMs = 520;

    private RPaMpModel? _amp;                  // the shared transport, used by the space bar
    private FrameworkElement? _stageElement;   // the stage in the tree
    private IVizStage? _stage;                 // the same stage, as the interface the host drives it through
    private string? _stageId;                  // which catalogue entry it is
    private ArtDisplay? _art;

    private DispatcherQueueTimer? _idle;   // one-shot: restarted by every pointer move
    private bool _pinned;

    private readonly ChromeFade _rootFade;
    private readonly ChromeFade _chromeFade;

    public VizFullScreen()
    {
        InitializeComponent();
        // This is set up through a writer of its own. So the moment the fade-in finishes is also the moment
        // the overlay starts covering the window (see Covers).
        _rootFade = new ChromeFade(v =>
        {
            if (Root.Opacity != v)
                Root.Opacity = v;
            SetCovers(IsShowing && v >= 1);
        }, Root.Opacity);
        _chromeFade = new ChromeFade(Chrome);

        Unloaded += (_, _) => Hide();

        // Escape is also handled on the control itself. Root holds the focus. If something inside it, such as
        // the close button, takes the focus instead, the key still bubbles up to here.
        KeyDown += Root_KeyDown;

        // The GL failure reason arrives on the logging thread. Subscribe once, here, and not per stage. This is
        // a static event. A subscription per launch would keep this control alive for every stage ever built.
        GlDiagnostics.Changed += () => DispatcherQueue?.TryEnqueue(UpdateStatus);
    }

    /// <summary>True while the overlay is up.</summary>
    public bool IsShowing => Root.Visibility == Visibility.Visible;

    /// <summary>
    /// Whether the overlay is up and fully opaque, so nothing under it can be seen. This is true from the
    /// frame the fade-in finishes until <see cref="Hide"/> begins. It is the same fact as
    /// <c>PegasusPlayer.Covers</c>, which <c>ShellPage.Cover</c> reads.
    /// </summary>
    public bool Covers { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Covers"/> changes.</summary>
    public event Action? CoversChanged;

    private void SetCovers(bool on)
    {
        if (Covers == on)
            return;
        Covers = on;
        CoversChanged?.Invoke();
    }

    /// <summary>Raised when the user leaves the visualizer (the close button or Escape).</summary>
    public event Action? Closed;

    /// <summary>Shows the given visualizer. An unknown id falls back to the built-in ribbon sea. The gallery
    /// is the only caller, so an id it does not know means a catalogue entry that has no stage yet.</summary>
    public void Show(string vizId)
    {
        var entry = VizCatalog.Find(vizId);
        if (IsShowing && _stageId == entry.Id)
            return;

        // This is either an arrival, or a change of visualizer while the overlay is already up. Only an arrival
        // fades in. During a change the overlay already covers the app. Fading it out first would show a black
        // frame between the two pictures.
        var opening = !IsShowing;

        _amp = App.Services.GetRequiredService<RPaMpService>().Transport;

        // The try has to be out here, not inside EnsureStage. Every stage derives from GLCanvasElement, which
        // lives in another assembly. The runtime resolves a method's types when it compiles that method. So
        // where GL is missing, EnsureStage cannot even start, and a try written inside it is never reached.
        // Caught here, the overlay still opens. The reason is shown on the black area, and the now-playing
        // chrome stays along the foot.
        try
        {
            EnsureStage(entry);
        }
        catch (Exception ex)
        {
            Faults.Note("gl", "starting the visualizer", ex);
            StatusText.Text = StageTrouble.Line($"{ex.GetType().Name}: {ex.Message}");
            StatusText.Visibility = Visibility.Visible;
        }

        HookChrome();
        Seek.DataContext = _amp;
        Transport.DataContext = _amp;
        Seek.SetRemainInset(0);   // full width: the countdown sits on the bar's own right edge

        // Fade up from nothing. The overlay covers the app either way. Without the fade, the change would be a
        // single frame. The library would be replaced by a black rectangle, and the GL stage usually has nothing
        // ready to draw during the first frame or two.
        if (opening)
        {
            _rootFade.To(0, 0);
            Root.Visibility = Visibility.Visible;
            _rootFade.To(1, OpenFadeMs);
        }

        SizeStage(StageHost.ActualWidth, StageHost.ActualHeight);
        _stage?.SetActive(true);
        Root.Focus(FocusState.Programmatic);   // so Escape lands here

        ShowChrome(immediate: true);
        RestartIdle();

        // The window goes into true full screen where the platform allows it. The overlay covers the app either
        // way. This also removes the title bar and the desktop. It is best effort.
        FullScreenWindow.Set(true);

        // Going full screen re-parents or re-activates the native window. That can take the overlay's keyboard
        // focus away. So the focus is taken back on the next tick, after the resize has settled. Then Escape
        // still exits.
        //
        // The stage is also resized on that same tick, and this is not a precaution. The window changes size
        // twice in quick succession: it is un-maximized, then it covers the monitor. If it does not settle where
        // it was left, the stage keeps the size it had at the widest point. An oversized stage looks wrong, and
        // it is also wrong in another way. GLCanvasElement fills only the viewport. The shader then draws into
        // the bottom of a taller framebuffer, and the visualizer comes out solid black. Setting the size again to
        // the host's actual size costs one comparison. It also covers any change that the window made.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsShowing)
                return;
            Root.Focus(FocusState.Programmatic);
            SizeStage(StageHost.ActualWidth, StageHost.ActualHeight);
        });
    }

    /// <summary>Takes the visualizer down and stops it rendering.</summary>
    public void Hide()
    {
        if (!IsShowing)
            return;

        // This comes first, so the shell draws again before the root is collapsed at the end of this turn.
        SetCovers(false);

        _stage?.SetActive(false);
        _idle?.Stop();
        _rootFade.Stop();
        _chromeFade.Stop();
        FullScreenWindow.SetCursorHidden(false);   // always give the pointer back when leaving
        Root.Opacity = 1;                          // the fades have stopped, so the next Show starts from nothing
        Root.Visibility = Visibility.Collapsed;
        UnhookChrome();
        FullScreenWindow.Set(false);
    }

    // ── the stage ──

    /// <summary>Puts the entry's stage in the host. Whatever was there before is removed first, so only one GL
    /// context is ever live. A stage throws when it is built on a machine without hardware acceleration. This
    /// method is never inlined. So on a machine with no GL assembly at all, the failure happens here, at the call
    /// that the caller has wrapped in a try, and not somewhere up the stack where nothing catches it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void EnsureStage(VizEntry entry)
    {
        if (_stageId == entry.Id && _stage is not null)
            return;

        // The old stage is removed first. It is switched off, then taken out of the tree. If it were left
        // running, it would keep a second GL context alive behind an overlay that nobody is looking at.
        _stage?.SetActive(false);
        if (_stageElement is not null)
            StageHost.Children.Remove(_stageElement);
        _stageElement = null;
        _stage = null;
        _stageId = null;
        StatusText.Visibility = Visibility.Collapsed;

        {
            FrameworkElement element;
            if (entry.Id == VizCatalog.LowFrequencyFlow)
            {
                var blocks = new BlockFlowStage();
                _stage = blocks;
                element = blocks;
            }
            else if (entry.Id == VizCatalog.Bloom)
            {
                var bloom = new BloomStage();
                _stage = bloom;
                element = bloom;
            }
            else if (entry.Id == VizCatalog.Chladni)
            {
                var plate = new ChladniStage();
                _stage = plate;
                element = plate;
            }
            else if (entry.Id == VizCatalog.Butterfly)
            {
                var wings = new ButterflyStage();
                _stage = wings;
                element = wings;
            }
            else if (entry.Id == VizCatalog.Tunnel)
            {
                var tunnel = new TunnelStage();
                _stage = tunnel;
                element = tunnel;
            }
            else if (entry.Id == VizCatalog.Kaleidoscope)
            {
                var mirrors = new KaleidoscopeStage();
                _stage = mirrors;
                element = mirrors;
            }
            else if (entry.Id == VizCatalog.Lattice)
            {
                var lattice = new LatticeStage();
                _stage = lattice;
                element = lattice;
            }
            else
            {
                var ribbon = new RibbonStage();
                _stage = ribbon;
                element = ribbon;
            }

            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.VerticalAlignment = VerticalAlignment.Center;
            _stageElement = element;
            _stageId = entry.Id;
            StageHost.Children.Add(element);

            _stage.CompileStateChanged += UpdateStatus;
        }
    }

    /// <summary>The GL message that was last written to the log, so the same message is not written twice.</summary>
    private string? _glSaid;

    private void UpdateStatus()
    {
        if (_stage is null)
            return;
        if (_stage.LastError is { } err)
        {
            var reason = GlDiagnostics.Last;
            var detail = reason is null ? "GL: " + err : $"GL: {err}\n{reason}";

            // Written once per change. The stage raises this event on every compile-state change.
            if (detail != _glSaid)
                Said.Line("gl", $"visualizer: {detail}");
            _glSaid = detail;

            StatusText.Text = StageTrouble.Line(detail);
            StatusText.Visibility = Visibility.Visible;
        }
        else
        {
            _glSaid = null;
            StatusText.Visibility = Visibility.Collapsed;
        }
    }

    private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => SizeStage(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Sizes the GL stage to fill the host, on every platform.
    ///
    /// <para>On macOS the stage is sized to the full host. It is not cropped to a square. GLCanvasElement renders
    /// into a framebuffer that is the size of the element itself, and it sets the viewport to that size. A square
    /// crop would only add work. Each frame would be rendered, read back to the processor, and uploaded again.
    /// For a 16:10 window that is 1.6 times the pixels on every frame. Keep the stage at the full host size.</para>
    /// </summary>
    private void SizeStage(double w, double h)
    {
        if (_stageElement is not { } s || w <= 0 || h <= 0)
            return;

        s.Width = w;
        s.Height = h;
    }

    // ── the chrome, read off the header ──

    private void HookChrome()
    {
        // Subscribe once, but rebuild the chrome every time. Switching to another visualizer while this one is
        // up rebuilds the chrome without touching the subscription. An early return here would leave the new
        // chrome blank until the next track change.
        if (_art is null)
        {
            _art = ShellPage.Current?.Art;
            if (_art is not null)
                _art.ChromeChanged += OnChromeChanged;
        }
        OnChromeChanged();
    }

    private void UnhookChrome()
    {
        if (_art is not null)
            _art.ChromeChanged -= OnChromeChanged;
        _art = null;
    }

    private void OnChromeChanged()
    {
        if (_art is not { } art)
            return;

        // Writes are guarded. This also runs while a track plays. Setting the same text again still costs a text
        // measure, and that takes a frame away from the stage. The title and album are masked on the way in, the
        // same way as the words on the stage beside them (see TitleCensor).
        SetText(TitleText, TitleCensor.Shown(art.TrackTitle));
        SetText(ArtistText, art.TrackArtist);
        SetText(AlbumText, TitleCensor.Shown(art.TrackAlbum));

        // The small copy. This badge is 104 points across. The display-sized picture would be shrunk by the
        // renderer into it, and the result looks grainy. See LoadedCover.Square.
        var cover = art.TrackCoverSquare;
        if (!ReferenceEquals(CoverImage.Source, cover))
        {
            CoverImage.Source = cover;
            CoverImage.Opacity = cover is null ? 0 : 1;
            CoverPlaceholder.Opacity = cover is null ? 1 : 0;
        }

        static void SetText(TextBlock block, string? value)
        {
            var text = value ?? "";
            if (block.Text != text) block.Text = text;
        }
    }

    // ── the chrome fades away ──
    //
    // Five seconds after the last pointer move, everything that is not the visualizer fades out. So the stage
    // is alone on the screen. The next move brings it back. The pin keeps it visible. The fade changes the
    // opacity of ONE container. Opacity is a composition-only property, so an idle screen does no layout work.
    // Layout work is what the visualizer cannot afford.

    private void Root_PointerActivity(object sender, PointerRoutedEventArgs e)
    {
        ShowChrome();
        RestartIdle();
    }

    /// <summary>Runs after a transport key's own Command. The Command belongs to the button. This handler only
    /// does the rest. It gives the focus back to Root, because a button that keeps the focus swallows the space
    /// bar. It also holds the chrome up, because someone who has just pressed pause is still using the
    /// controls.</summary>
    private void Transport_Click(object sender, RoutedEventArgs e)
    {
        Root.Focus(FocusState.Programmatic);
        ShowChrome();
        RestartIdle();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;

        // A pinned button uses the RPM azure that the active rail buttons use. An unpinned one uses a plain
        // dark disc.
        PinButton.Background = new SolidColorBrush(_pinned
            ? Color.FromArgb(0xAA, 0x03, 0x71, 0xD0)
            : Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        PinButton.Foreground = new SolidColorBrush(Color.FromArgb(_pinned ? (byte)0xFF : (byte)0xE6, 0xFF, 0xFF, 0xFF));
        IconName.Set(PinButton, Strings.Loc.Get(_pinned ? "Viz_Unpin" : "Viz_Pin"));

        ShowChrome();
        RestartIdle();
        Root.Focus(FocusState.Programmatic);   // a focused button would swallow the space bar
    }

    /// <summary>Restarts the five-second countdown. While the chrome is pinned, it stops the countdown
    /// instead.</summary>
    private void RestartIdle()
    {
        if (_pinned)
        {
            _idle?.Stop();
            return;
        }
        _idle ??= CreateIdleTimer();
        _idle?.Stop();
        _idle?.Start();
    }

    private DispatcherQueueTimer? CreateIdleTimer()
    {
        if (DispatcherQueue is null)
            return null;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = IdleDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideChrome();
        return timer;
    }

    private void ShowChrome(bool immediate = false)
    {
        FullScreenWindow.SetCursorHidden(false);
        Chrome.IsHitTestVisible = true;
        _chromeFade.To(1, immediate ? 0 : 140);
    }

    private void HideChrome()
    {
        if (_pinned || !IsShowing)
            return;
        Chrome.IsHitTestVisible = false;   // faded chrome must not take pointer input
        _chromeFade.To(0, 420);

        // The pointer hides with the chrome. It is the last thing on the screen that is not the visualizer.
        // A pointer left over a full-screen picture is as much of an intrusion as the buttons that just faded.
        // The pin keeps it visible with everything else. Someone who asked for the controls to stay wants
        // something to point at them with.
        FullScreenWindow.SetCursorHidden(true);
    }

    // ── leaving ──

    private void Close_Click(object sender, RoutedEventArgs e) => Leave();

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Leave();
                break;

            // Space plays and pauses. It uses the same Command that the amp's own transport button runs, so the
            // full screen never has a separate view of what the player is doing. The chrome comes back up with
            // it. Pressing pause and seeing nothing change looks like a dropped keypress.
            case Windows.System.VirtualKey.Space:
                e.Handled = true;
                _amp?.PlayPauseCommand.Execute(null);
                ShowChrome();
                RestartIdle();
                break;
        }
    }

    private void Leave()
    {
        Hide();
        Closed?.Invoke();
    }
}

using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Transport;

/// <summary>
/// The deck: the bar along the foot of the main screen. The pane pill is on the left edge. The transport floats
/// in the exact centre. The state chrome sits on the right: the rip, the cloud, the cast pill and the menu.
///
/// <para>The deck drives everything through the one shared player model (<see cref="AttachTransport"/>). The
/// buttons are Commands on that model, so the bar plays whatever the player is playing. The deck does not
/// decide the pane. It reports the pill's click and paints whatever <see cref="SetPaneOpen"/> tells it.</para>
/// </summary>
internal sealed partial class TransportDeck : UserControl
{
    public TransportDeck()
    {
        InitializeComponent();

        // The picker only reports the choice, and the deck passes it on. As with the pane pill and the hamburger,
        // nothing here decides anything about the app.
        ViewPickerContent.Chosen += view =>
        {
            ViewFlyout.Hide();
            ViewRequested?.Invoke(view);
        };
    }

    /// <summary>The pane pill was clicked: open the left pane if it is closed, close it if it is open.</summary>
    public event Action? PaneToggleRequested;

    /// <summary>The home key was pressed. Carries nothing: there is one home and the page that shows
    /// it knows where it goes.</summary>
    public event Action? HomeRequested;

    /// <summary>The hamburger was clicked, carrying itself so the menu can open at the button.</summary>
    public event Action<FrameworkElement>? MenuRequested;

    /// <summary>The menu button. The first-run hint about signing in points at it. The shell's own edge hamburger is
    /// collapsed on the library page, and this is the menu people use there. A hint aimed at the hamburger would
    /// point at nothing.</summary>
    public FrameworkElement MenuTarget => MenuPill;

    // ── the view ─────────────────────────────────────────────────────────────

    /// <summary>A line of the view menu was pressed. The deck reports the choice, then paints whatever
    /// <see cref="SetView"/> reports. Which of the three views is up is not the deck's decision. It is the same
    /// as for the pane pill and the pane.</summary>
    public event Action<AppView>? ViewRequested;

    private AppView _view = AppView.Library;

    /// <summary>The view now on screen, so the picker can light the card the app is already on.</summary>
    public void SetView(AppView view) => _view = view;

    /// <summary>Called when the picker opens, rather than kept in step with every change. The picker is closed
    /// most of the time, and this is the only place the answer is shown.</summary>
    private void ViewFlyout_Opening(object? sender, object e) => ViewPickerContent.SetCurrent(_view);

    // ── the player ────────────────────────────────────────────────────────────

    private RPaMpModel? _transport;

    /// <summary>Gives the deck the one shared player model. The transport bar uses it as its DataContext, which binds
    /// every key's Command. The cast pill is given the model separately, because its siblings inherit the page's
    /// own model.</summary>
    public void AttachTransport(RPaMpModel transport)
    {
        if (_transport is { } old)
            old.PropertyChanged -= Transport_PropertyChanged;

        _transport = transport;
        TransportBar.DataContext = transport;
        DeckCastButton.DataContext = transport;
        transport.PropertyChanged += Transport_PropertyChanged;
        SyncTransportPulse();
    }

    /// <summary>While the DJ hands over, the next key pulses and does nothing. The player's own keys do the same,
    /// since both drive the one model.</summary>
    private void Transport_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RPaMpModel.IsTransportBusy))
            SyncTransportPulse();
    }

    private void SyncTransportPulse() =>
        Pulse.Set(TransportNext, _transport?.IsTransportBusy == true);

    /// <summary>The speaker mark is the mute: down to nothing, and back to the level it was left at.</summary>
    private void VolumeMark_Click(object sender, RoutedEventArgs e)
        => _transport?.ToggleMute();

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            MenuRequested?.Invoke(fe);
    }

    // Re-scan for speakers each time the deck's cast picker opens (same as the popout's).
    private void DeckCast_Opening(object? sender, object e) => DeckCastContent.Refresh();

    // The queue card reads the count and finds the playing row each time it opens.
    private void QueueFlyout_Opening(object? sender, object e) => QueueCardContent.Refresh();

    private void QueueFlyout_Closed(object? sender, object e) => QueueCardContent.Closed();

    // ── the pane pill ─────────────────────────────────────────────────────────
    // The glyph is a little window. Its drawer slides out of the left edge, the same move the pane makes. When
    // closed, the drawer is a sliver at the frame's edge, and the clip hides the rest. Hovering nudges it out.
    // Opening throws it fully out with a small overshoot, so the button has some spring.

    private const double PaneDrawerTucked = -7;    // a 3px sliver of the 10px drawer shows
    private const double PaneDrawerPeek = -5;      // hover: a touch more
    private const double PaneDrawerOut = 0;        // open: the whole drawer
    private const double PaneDrawerMs = 220;

    private double _paneDrawerX = PaneDrawerTucked;
    private bool _paneOpen;
    private bool _paneHover;
    private bool _paneClipped;
    private EventHandler<object>? _paneTick;

    private void PanePill_Click(object sender, RoutedEventArgs e) => PaneToggleRequested?.Invoke();

    /// <summary>The reader asked for home. This is a request, not an action. The deck does not know what is on the
    /// right-hand pane, and it cannot put that pane away. See MainPage, which owns the pane and every surface in
    /// it. It works like the pane pill above and the view menu beside it.</summary>
    private void HomePill_Click(object sender, RoutedEventArgs e) => HomeRequested?.Invoke();

    private static readonly SolidColorBrush HomeGlyphIdle =
        new(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));

    /// <summary>Lights the home key while the home screen is showing. The key uses the same mark style as the tabs on
    /// the rails. Home is a view like the four on either rail, and this key is its only tab. It is painted here,
    /// not through <see cref="Presentation.Common.SelectedSegment.Paint"/>, because the glyph has its own colour
    /// and would not take the button's.</summary>
    public void SetHomeActive(bool active)
    {
        HomePill.Background = Presentation.Common.SelectedSegment.Wash(active);
        HomeGlyph.Foreground = active
            ? Presentation.Common.SelectedSegment.Ink(true)
            : HomeGlyphIdle;   // the deck's own resting ink, not the rails': nothing else here dims
    }

    /// <summary>Called on every apply of the pane's own state: the drawer follows it.</summary>
    public void SetPaneOpen(bool open)
    {
        _paneOpen = open;
        EnsurePaneClip();
        GlidePaneDrawer();
    }

    private void PanePill_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _paneHover = true;
        GlidePaneDrawer();
    }

    private void PanePill_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _paneHover = false;
        GlidePaneDrawer();
    }

    /// <summary>The drawer has to vanish into the frame, not hang off it. A clip geometry needs a measured size, so
    /// it is cut again whenever the icon's size changes.</summary>
    private void EnsurePaneClip()
    {
        if (_paneClipped)
            return;
        _paneClipped = true;
        PaneIconClip.SizeChanged += (_, args) => Cut(args.NewSize.Width, args.NewSize.Height);
        Cut(PaneIconClip.ActualWidth, PaneIconClip.ActualHeight);   // already laid out, so cut it now

        void Cut(double w, double h)
        {
            if (w <= 0 || h <= 0)
                return;
            PaneIconClip.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, w, h),
            };
        }
    }

    private void GlidePaneDrawer()
    {
        double target = _paneOpen ? PaneDrawerOut : _paneHover ? PaneDrawerPeek : PaneDrawerTucked;
        if (Math.Abs(_paneDrawerX - target) < 0.01)
            return;

        StopPaneGlide();
        double from = _paneDrawerX;
        // Opening springs past the mark and settles back. Tucking away is a plain ease-out, because a drawer being
        // pushed shut does not bounce.
        bool spring = target > from;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double startMs = -1;
        _paneTick = (_, _) =>
        {
            double now = sw.Elapsed.TotalMilliseconds;
            if (startMs < 0)
                startMs = now;
            double t = Math.Min(1, (now - startMs) / PaneDrawerMs);
            double e = spring ? BackOut(t) : 1 - Math.Pow(1 - t, 3);
            _paneDrawerX = from + (target - from) * e;
            PaneDrawerShift.X = _paneDrawerX;
            if (t >= 1)
            {
                _paneDrawerX = target;
                PaneDrawerShift.X = target;
                StopPaneGlide();
            }
        };
        CompositionTarget.Rendering += _paneTick;

        // Overshoot ease, using the standard back-out constants. It goes past the target, then settles.
        static double BackOut(double t)
        {
            const double c1 = 1.70158, c3 = c1 + 1;
            return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
        }
    }

    private void StopPaneGlide()
    {
        if (_paneTick is null)
            return;
        CompositionTarget.Rendering -= _paneTick;
        _paneTick = null;
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Presentation.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace RPM.Desktop.Presentation.RPaMp;

/// <summary>
/// The RPaMp window body: main transport, 10-band EQ and reorderable playlist stacked
/// like classic Winamp sub-windows. Presets and the decorative spectrum bars live here;
/// everything with real audio behaviour is on <see cref="RPaMpModel"/>.
/// </summary>
public sealed partial class RPaMpView : UserControl
{
    private const int BarCount = 20;
    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _levels = new double[BarCount];
    private readonly SpectrumAnalyzer _spectrum = new();
    private DispatcherTimer? _visTimer;

    /// <summary>Raised when the faux title-bar close button is clicked; the host closes the window.</summary>
    public event Action? CloseRequested;

    /// <summary>The window the skin is dressed as. It is set by the service, so the title bars can drag,
    /// minimise and pin it. It is the app's own window: there is no second one.</summary>
    public Microsoft.UI.Windowing.AppWindow? HostWindow { get; set; }

    /// <summary>The skin's shape: fixed casing width, the see-through gap between the stacked
    /// sub-panels, their corner radius, and the playlist's fixed height.</summary>
    private const double SkinWidth = 440, PanelGap = 8, PanelRadius = 5;

    /// <summary>The library pane's width: twice the stack's, as it is drawn in the XAML.</summary>
    private const double LibraryWidth = 880;
    private const double PlaylistHeight = 188;

    /// <summary>
    /// The fixed measurements of the casing, applied once at construction.
    /// </summary>
    /// <remarks>
    /// A Winamp-style stack of separate sub-windows: a fixed casing width of 440, 8px of see-through gap
    /// between the panels, every panel rounded on all four corners, and everything sized to its content,
    /// because the window is measured from the casing (see <see cref="Shell.SkinWindow"/>) and not the other
    /// way round.
    /// </remarks>
    private void ApplySkinShape()
    {
        RootCasing.VerticalAlignment = VerticalAlignment.Top;
        RootCasing.HorizontalAlignment = HorizontalAlignment.Left;

        // The stack has the fixed width, not the casing. The casing is as wide as what is open: the stack
        // alone, or the stack and the library beside it. The window is measured from the casing, so opening
        // the library widens the window and does not crop it.
        PanelStack.Width = SkinWidth;

        // The dialog card's white glass tint, over the native frost behind the window.
        var panelFill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        MainPanel.Background = panelFill;
        EqPanel.Background = panelFill;
        PlaylistPanel.Background = panelFill;

        // The base fill of the panels is opaque (`RpmInkBrush`), not the translucent veil of the cards. This
        // is required and is not only cosmetic. This window is see-through, and it repaints only the region
        // that changed without clearing it first. A translucent fill would be painted over the previous
        // frame's copy of itself and build up: hovering over a button would turn a panel white. An opaque
        // fill is overwritten and not blended. The picture and its veils go on top, inside the panel. The gaps draw nothing at all, so nothing can build up there either.

        PanelStack.RowSpacing = PanelGap;
        MainPanel.CornerRadius = new CornerRadius(PanelRadius);
        EqPanel.CornerRadius = new CornerRadius(PanelRadius);
        PlaylistPanel.CornerRadius = new CornerRadius(PanelRadius);

        PlaylistRow.Height = GridLength.Auto;
        PlaylistList.Height = PlaylistHeight;
    }

    /// <summary>
    /// Puts the part of the picture that belongs to each panel on that panel.
    /// </summary>
    /// <remarks>
    /// There is one picture behind the player. Each panel draws the whole picture at the size of the whole
    /// casing, moved by that panel's own offset within the casing. So the light continues across the gaps
    /// between the panels, and each panel does not show the middle of the same photograph.
    ///
    /// <para>The offset is asked of the layout (<c>TransformToVisual</c>) and is not worked out from the
    /// panel heights, because the panels do not all have the same height, the height of the playlist changes
    /// with the queue, and the library appears beside them. This is called whenever one of them settles at a
    /// new size. A panel that is not on screen is skipped: it has no place in the casing to ask about, and it
    /// is asked again when it comes back.</para>
    /// </remarks>
    private void SyncGrounds()
    {
        var width = RootCasing.ActualWidth;
        var height = RootCasing.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        Place(MainGroundHost, MainGround, MainPanel);
        Place(EqGroundHost, EqGround, EqPanel);
        Place(PlaylistGroundHost, PlaylistGround, PlaylistPanel);
        Place(LibraryGroundHost, LibraryGround, LibraryPanel);

        void Place(Canvas host, Rectangle ground, FrameworkElement panel)
        {
            if (panel.Visibility != Visibility.Visible || panel.ActualWidth <= 0)
                return;

            var at = panel.TransformToVisual(RootCasing).TransformPoint(default);

            ground.Width = width;
            ground.Height = height;
            Canvas.SetLeft(ground, -at.X);
            Canvas.SetTop(ground, -at.Y);

            // The picture is bigger than the panel by design, and a `Canvas` does not clip its children. The
            // panel's `Border` does, but the clip is set here as well so that nothing depends on that. If it
            // went wrong, the picture would show in the gaps, which is the one place where nothing may be
            // drawn.
            host.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight),
            };
        }
    }

    /// <summary>
    /// Shows or hides the library beside the stack.
    /// </summary>
    /// <remarks>
    /// The library is collapsed and not only narrowed, so a library that nobody has opened costs no layout.
    /// The second column of the casing is Auto, so the casing measures the stack alone while the library is
    /// shut. The window follows on the next settle: see <see cref="CasingSettled"/>.
    /// </remarks>
    private void LibraryToggle_Click(object sender, RoutedEventArgs e)
        => ShowLibrary(LibraryPanel.Visibility != Visibility.Visible);

    /// <summary>Whether the library is open beside the stack.</summary>
    public bool IsLibraryOpen => LibraryPanel.Visibility == Visibility.Visible;

    /// <summary>Cmd or Ctrl+F while the mini player is the view: the collection opens beside the stack if it
    /// is shut, and its search field takes the keyboard once it is laid out. If the pane is closed again
    /// before then, the request is dropped.</summary>
    internal void SearchCollection()
    {
        if (!IsLibraryOpen)
            ShowLibrary(true);

        FieldFocus.Give(LibSearchBox, () => IsLibraryOpen);
    }

    private void ShowLibrary(bool on)
    {
        LibraryPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // The pane is as tall as the stack. The height is stated and not negotiated: see
        // `SizeLibraryToStack`.
        if (on)
            SizeLibraryToStack();

        // The grids are built and the collection is read on the way in, not at startup: the collection is
        // worth reading only for a pane that someone has opened, and the grids are worth building only for a
        // pane that is shown. Both are done once, and later calls do nothing.
        //
        // This is before the window is asked to change size, and that is the reason it is here and not at the
        // end. All of it runs on the interface thread (the grids are built and the whole shelf is read). If
        // the resize were asked for first, there would be no frame to draw at the new width until the reading
        // is done. The window server shows its last frame, stretched across the wider window, for as long as
        // that takes, about a second, and it looks like the player being pulled out of shape and snapping
        // back.
        if (on && Model is { } model)
        {
            AttachLibraryGrids(model);
            model.LoadLibrary();

            // The collection on screen can be changed from the deck's share button while this pane is shut.
            // It is read here, so the two switches agree without either telling the other.
            model.SyncLibraryToShelf();
            ShowCollectionName();
        }

        // Laid out before the window is resized, not after. The window server scales whatever was last drawn
        // to the new size of the window until the app draws again, so a resize that arrives before the casing
        // is measured shows the stack stretched across the new width for one frame. Laying out first makes
        // the tree right, so the first frame at the new size is the right picture.
        RootCasing.UpdateLayout();

        // The casing has a different shape now, so every panel's part of the picture has moved.
        SyncGrounds();

        // This is called here and is not waited for. The casing cannot change width until the window has room
        // for it, and the window is not given room until something says that the casing wants it. Waiting for
        // `RootCasing.SizeChanged` would wait for a size change that this call causes.
        CasingSettled?.Invoke();

        LibraryButton.Foreground = new SolidColorBrush(
            on ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(0xFF, 0x9A, 0xA4, 0xAC));
    }

    /// <summary>
    /// Raised when the casing has laid out at a new size.
    /// </summary>
    /// <remarks>
    /// This is the cue that the window must be fitted again. It is not the same as the `SizeChanged` event of
    /// this control: the `UserControl` stretches to the window, so it reports the size of the window and says
    /// nothing when the casing changes shape inside it, which is what opening the library does.
    /// </remarks>
    public event Action? CasingSettled;

    /// <summary>The rendered size of the skin casing, used to fit the chromeless window exactly. The
    /// UserControl itself stretches to the window, so it cannot be measured.</summary>
    /// <remarks>
    /// The width is stated, not measured. The window is fitted to this value and the casing is inside that
    /// window, so a measured width can never be larger than the window it is measured in: when the library
    /// opens, the casing would lay out clipped at 440, report 440, and the window would never grow. What the
    /// casing needs is known: the stack, and the library beside it when the library is open.
    ///
    /// <para>The height is measured too, but from the stack and against no ceiling, and not from the arranged
    /// casing. A measure is clamped to what it was offered, and the casing is offered the window whose height
    /// is about to be set. On Windows the window keeps a sizing frame after it is told to drop its border, so
    /// the content is shorter than asked, the casing reports that shorter height, and the next fit makes the
    /// window shorter again. It would shrink by one panel at a time until only the first title bar was left.
    /// Measured against an infinite ceiling, the answer is the same for any window size.</para>
    ///
    /// <para>The stack is used and not the casing, because the stack is bounded: every row in it has a fixed
    /// height. The library pane beside it holds four scrolling tables that would ask for the height of the
    /// whole collection, so that pane is told the height of the stack and does not ask for its own: see
    /// <see cref="SizeLibraryToStack"/>.</para>
    /// </remarks>
    public Windows.Foundation.Size ContentSize =>
        new(SkinWidth + (IsLibraryOpen ? PanelGap + LibraryWidth : 0), StackHeight());

    /// <summary>
    /// Where each panel of the casing is, in this control's own coordinates.
    /// </summary>
    /// <remarks>
    /// On Windows the window is cut to these rectangles (<see cref="Services.Platform.WindowShape"/>), which
    /// is how the gaps between the panels show the desktop on a head that has no transparent window. This
    /// control fills the client area of the window, so its coordinates are the client's.
    ///
    /// <para>Asked of the layout and not worked out from the panel heights, for the same reason as in
    /// <see cref="SyncGrounds"/>: the panels do not all have the same height, the height of the playlist
    /// changes with the queue, and the library appears beside them. A panel that is not shown is left out, so
    /// the shut library is not a hole in the window.</para>
    /// </remarks>
    public IReadOnlyList<Windows.Foundation.Rect> PanelRects()
    {
        var rects = new List<Windows.Foundation.Rect>(4);
        foreach (var panel in new FrameworkElement[] { MainPanel, EqPanel, PlaylistPanel, LibraryPanel })
        {
            if (panel.Visibility != Visibility.Visible || panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
                continue;

            var at = panel.TransformToVisual(this).TransformPoint(default);
            rects.Add(new Windows.Foundation.Rect(at.X, at.Y, panel.ActualWidth, panel.ActualHeight));
        }
        return rects;
    }

    /// <summary>The panels' corner radius, so the cut round their corners matches the drawing.</summary>
    public double PanelCornerRadius => PanelRadius;

    /// <summary>The height the stack of panels wants, measured against no ceiling.</summary>
    /// <remarks>
    /// Falls back to the casing as it is arranged when no measure has run yet, which is only before
    /// the first layout: a height that is short by the frame is better than a window of no height.
    /// </remarks>
    private double StackHeight() => _stackWanted > 1 ? _stackWanted : RootCasing.ActualHeight;

    /// <summary>What the last measure pass found that the stack wants. It is written in
    /// <see cref="MeasureOverride"/> and read everywhere else.</summary>
    private double _stackWanted;

    /// <summary>
    /// Takes the height that the stack wants, which is what the window is sized to.
    /// </summary>
    /// <remarks>
    /// <para>The measure is here because this is the one place where measuring is allowed. A property getter
    /// such as <see cref="StackHeight"/> is reached from <c>RootCasing.SizeChanged</c>, from the handler of
    /// each panel, and from <see cref="ShowLibrary"/>, so measuring there would measure the tree against a
    /// ceiling that no pass offered, from a size-changed handler, once per settle. A measure taken outside a
    /// measure pass leaves the desired size of the element in disagreement with the arrangement around it,
    /// and a frame drawn on that disagreement puts the panels in the wrong place. On the mac head the window
    /// is see-through and its surface is never cleared before a frame, so whatever such a frame drew in the
    /// gaps between the panels stays there until something forces a full repaint: tabbing away and back, or
    /// opening the library.</para>
    ///
    /// <para>The measure is against an infinite ceiling and at the stated width. This is the number that the
    /// WinUI head needs, and the reason the measure exists: a measure is clamped to what it was offered, and
    /// the casing is offered the window whose height is about to be set. See <see cref="ContentSize"/>.
    /// Measuring the same child twice in one pass is ordinary: the second measure, from
    /// <c>base.MeasureOverride</c>, is at the real constraint and is the one that the layout keeps.</para>
    /// </remarks>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        PanelStack.Measure(new Windows.Foundation.Size(SkinWidth, double.PositiveInfinity));
        if (PanelStack.DesiredSize.Height > 1)
            _stackWanted = PanelStack.DesiredSize.Height;

        return base.MeasureOverride(availableSize);
    }

    /// <summary>
    /// Gives the library pane the height of the stack.
    /// </summary>
    /// <remarks>
    /// The four tables of the pane are scrolling grids under a star row, so the pane would want the height of
    /// the whole collection, and only the window it is measured in limits that. The casing is as tall as the
    /// stack whether the pane is open or not, so that height is given to the pane, and the height of the
    /// casing then depends on nothing outside it.
    /// </remarks>
    private void SizeLibraryToStack()
    {
        var wanted = StackHeight();
        if (wanted <= 1)
            return;
        if (double.IsNaN(LibraryPanel.Height) || Math.Abs(LibraryPanel.Height - wanted) > 0.5)
            LibraryPanel.Height = wanted;
    }



    private bool _dragging;
    private Windows.Foundation.Point _dragOrigin;
    private int _pendingDx, _pendingDy;
    private bool _movePending;

    // 10-band presets (dB), in the order of `PlaybackService.EqBands`.
    /// <summary>The named curves. They belong to the equaliser, so they live with it, and the settings page
    /// offers the same list.</summary>
    private static EqPreset[] Presets => EqService.Presets;

    public RPaMpView()
    {
        InitializeComponent();
        ApplySkinShape();

        // In the constructor, not on Loaded: the view exists, bound to the player, whether or not it is on screen.
        WireScrubbing();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private RPaMpModel? Model => DataContext as RPaMpModel;

    /// <summary>The one-time wiring is kept apart from the per-load wiring: when the view is left and entered
    /// again, this same view is re-parented, so `Loaded` runs again every time.</summary>
    private bool _wired;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_wired)
        {
            _wired = true;


            foreach (var p in Presets)
                PresetBox.Items.Add(p.Label);

            VisualizerCanvas.SizeChanged += (_, _) => BuildBars();

            // Any of the sub-window title bars drags the borderless window.
            foreach (var bar in new[] { MainTitleBar, EqTitleBar, PlaylistTitleBar, LibraryTitleBar })
            {
                bar.PointerPressed += TitleBar_PointerPressed;
                bar.PointerMoved += TitleBar_PointerMoved;
                bar.PointerReleased += TitleBar_PointerReleased;
                bar.PointerCaptureLost += TitleBar_PointerReleased;
            }

            RootCasing.SizeChanged += (_, _) =>
            {
                SyncGrounds();
                CasingSettled?.Invoke();
            };

            // The panels do not all have the same height and the height of the playlist changes with the
            // queue, so each panel says when it has moved. The casing is not asked to notice.
            foreach (var panel in new FrameworkElement[] { MainPanel, EqPanel, PlaylistPanel })
                panel.SizeChanged += (_, _) =>
                {
                    // The stack has a different height, so the pane beside it has too.
                    if (IsLibraryOpen)
                        SizeLibraryToStack();
                    SyncGrounds();
                };
            LibraryPanel.SizeChanged += (_, _) => SyncGrounds();
        }

        BuildBars();

        // `Loaded` runs again every time the view is re-parented, and the casing may already be at its final
        // size by then. In that case no size change is coming to place the pictures.
        SyncGrounds();

        _visTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _visTimer.Tick += OnVisualizerTick;

        StartTicker();

        if (Model is { } m)
        {
            m.PropertyChanged += OnModelChanged;
            SyncVisualizer();
            SyncNextPulse();
            AttachDeckRows(m);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (Model is { } m) m.PropertyChanged -= OnModelChanged;
        DetachDeckRows();
        Pulse.Set(NextButton, false);
        if (_visTimer is { } t) { t.Stop(); t.Tick -= OnVisualizerTick; _visTimer = null; }
        if (_tickerTimer is { } tt) { tt.Stop(); tt.Tick -= OnTickerTick; _tickerTimer = null; }
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RPaMpModel.IsPlaying))
            SyncVisualizer();
        else if (e.PropertyName == nameof(RPaMpModel.NowPlayingText))
            UpdateTickerText();
        else if (e.PropertyName == nameof(RPaMpModel.IsTransportBusy))
            SyncNextPulse();
        else if (e.PropertyName == nameof(RPaMpModel.SelectedTrack))
            ShowSelection();
    }

    // The playlist panel's own rows.
    //
    // The panel does not show the queue directly. It shows one row for each place in the queue, and a drag
    // moves faces along those rows, so that the index of the carried track follows the pointer. See `DeckRow`
    // for why the queue itself must not be reordered while a record plays out of it, and `CarriedReorder` for
    // why an index that does not follow the pointer makes a row vanish when the list scrolls at an edge.

    private readonly ObservableCollection<DeckRow> _deckRows = new();
    private CarriedReorder<DeckRow>? _deckReorder;
    private RPaMpModel? _deckModel;
    private bool _showingSelection;

    private void AttachDeckRows(RPaMpModel model)
    {
        if (ReferenceEquals(_deckModel, model))
            return;

        DetachDeckRows();

        _deckModel = model;
        _deckModel.Playlist.CollectionChanged += OnQueueChanged;

        PlaylistList.ItemsSource = _deckRows;
        BuildDeckRows();

        _deckReorder ??= new CarriedReorder<DeckRow>(
            PlaylistList, _deckRows,
            exchange: (a, b) =>
            {
                var carried = _deckRows[a].Track;
                _deckRows[a].Show(_deckRows[b].Track);
                _deckRows[b].Show(carried);
            },
            commit: (from, to) => _deckModel?.Playlist.Move(from, to));
    }

    private void DetachDeckRows()
    {
        if (_deckModel is not null)
            _deckModel.Playlist.CollectionChanged -= OnQueueChanged;
        _deckModel = null;
    }

    /// <summary>
    /// The queue changed. The rows are rebuilt only if they do not already show what the queue says.
    ///
    /// <para>Usually they do: a reorder that this panel made itself has already put every face on its final
    /// row before the queue is told, so the `Move` that comes back through here changes nothing. Rebuilding
    /// anyway makes the list blink each time a row is moved in it.</para>
    /// </summary>
    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A row is being carried. Whatever this change was, it did not come from the drag, and rebuilding the
        // rows under a carried row would leave it holding a place that no longer exists.
        if (_deckReorder is { IsBusy: true })
        {
            _deckReorder.Cancel();
            BuildDeckRows();
            return;
        }

        if (!RowsInStep())
            BuildDeckRows();
    }

    private bool RowsInStep()
    {
        if (_deckModel is null || _deckRows.Count != _deckModel.Playlist.Count)
            return false;

        for (var i = 0; i < _deckRows.Count; i++)
            if (!ReferenceEquals(_deckRows[i].Track, _deckModel.Playlist[i]))
                return false;

        return true;
    }

    private void BuildDeckRows()
    {
        foreach (var row in _deckRows)
            row.Release();
        _deckRows.Clear();

        if (_deckModel is null)
            return;

        foreach (var track in _deckModel.Playlist)
            _deckRows.Add(new DeckRow(track));

        ShowSelection();
    }

    /// <summary>Puts the list's selection on the row that shows the selected track. The model owns the
    /// selection (everything else in the app asks it, not the list), so the selection travels one way, and
    /// the flag stops the answer from coming straight back as a new selection.</summary>
    private void ShowSelection()
    {
        if (_showingSelection)
            return;

        _showingSelection = true;
        PlaylistList.SelectedItem = _deckModel?.SelectedTrack is { } track
            ? _deckRows.FirstOrDefault(r => ReferenceEquals(r.Track, track))
            : null;
        _showingSelection = false;
    }

    private void Playlist_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showingSelection || _deckModel is null)
            return;

        _deckModel.SelectedTrack = (PlaylistList.SelectedItem as DeckRow)?.Track;
    }

    /// <summary>The mix is handing over: the next key pulses until the hand-over finishes, and does nothing
    /// if pressed. A press asks for something that takes eight bars, and the button has to show
    /// that.</summary>
    private void SyncNextPulse() =>
        Pulse.Set(NextButton, Model?.IsTransportBusy == true);

    // Start the timer when playback starts. It keeps running through brief `IsPlaying` blips (for example the
    // ffplay restart on an EQ or volume change), so the bars decay smoothly and do not snap to zero. The tick
    // stops itself once everything is idle, because idle timers pin the CPU on Uno-Skia.
    private void SyncVisualizer()
    {
        if (_visTimer is { IsEnabled: false } t && Model?.IsPlaying == true)
            t.Start();
    }

    // Transport and playlist.

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        try { RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(HostWindow)?.Minimize(); }
        catch { }
    }

    // Casting.

    /// <summary>Speakers come and go, so the list is rebuilt every time the menu is opened and is not kept
    /// ready. The shared picker does the scan; this only starts it again on re-open.</summary>
    private void CastFlyout_Opening(object? sender, object e) => CastContent.Refresh();

    private void Pin_Toggle(object sender, RoutedEventArgs e)
    {
        var on = (sender as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)?.IsChecked == true;
        try
        {
            if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(HostWindow) is { } op)
                op.IsAlwaysOnTop = on;
        }
        catch { }
    }

    // The window buttons keep their colour on hover. The minus and cross glyph fades in instead.
    /// <summary>
    /// The glyphs of the window buttons: invisible at rest, shown under the pointer.
    /// </summary>
    /// <remarks>
    /// The glyph is named by the `Tag` of the button and is not its `Content`, because the content is the
    /// frosted disc as well as the glyph, and fading the content would fade the glass too. The caption
    /// buttons of the shell work in the same way (`ShellPage.Caption_Enter`).
    /// </remarks>
    private void ChromeBtn_Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UIElement glyph) glyph.Opacity = 1;
    }

    private void ChromeBtn_Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is UIElement glyph) glyph.Opacity = 0;
    }

    // Borderless drag: move the `AppWindow` by the pointer delta within the title bar.

    private void TitleBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // macOS: hand over to the native drag loop of AppKit. `AppWindow.Move` aborts the process on Uno-mac,
        // so the window must never be moved from managed code here.
        if (OperatingSystem.IsMacOS())
        {
            try { MacWindowChrome.BeginWindowDrag(); } catch { }
            return;
        }

        if (HostWindow is null) return;
        _dragging = true;
        _dragOrigin = e.GetCurrentPoint(this).Position;   // cursor offset inside the window at grab
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void TitleBar_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging || HostWindow is null) return;
        var p = e.GetCurrentPoint(this).Position;
        // Latest delta wins (overwrite, do not accumulate). The window catches up between frames.
        _pendingDx = (int)(p.X - _dragOrigin.X);
        _pendingDy = (int)(p.Y - _dragOrigin.Y);
        if (_movePending) return;
        _movePending = true;
        // CRITICAL: never move the window synchronously inside a pointer-moved event. On Uno-mac that
        // re-enters `NSWindow.setFrame` from within `sendEvent:` and aborts the process. Defer it.
        DispatcherQueue.TryEnqueue(ApplyPendingMove);
    }

    private void ApplyPendingMove()
    {
        _movePending = false;
        if (HostWindow is null || (_pendingDx == 0 && _pendingDy == 0)) return;
        try
        {
            var pos = HostWindow.Position;
            HostWindow.Move(new Windows.Graphics.PointInt32 { X = pos.X + _pendingDx, Y = pos.Y + _pendingDy });
        }
        catch { /* window gone mid-drag */ }
    }

    private void TitleBar_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void Playlist_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DeckRow row)
            Model?.PlayTrack(row.Track);
        else if (PlaylistList.SelectedItem is DeckRow sel)
            Model?.PlayTrack(sel.Track);
    }

    // Seeking from the position slider.
    //
    // The slider seeks when a person lets go of it or lifts a key on it, and never because its value changed.
    // Its value changes all the time without anyone touching it: the binding follows the playhead, and during
    // a track change the maximum (the length of the new row) and the value (the position of the old track)
    // arrive from two places a few milliseconds apart, so the slider clamps the old position to the new
    // length. Reading that change as a seek would jump to the end of the new track: it would play its last
    // quarter second and the queue would go on to the next song (pressing play would skip the song). Reading
    // only which event came first is not enough, because the order differs by path. This view is built with
    // the player whether or not the skin is on screen, so this applies to every view. The seek bar of the
    // shell works in the same way (`SeekBar`).

    /// <summary>Set by a press on the slider and spent by the release or a lost capture.</summary>
    private bool _scrubbing;

    /// <summary>Set while a key is down on the slider, so the playhead does not overwrite the value the key moved
    /// before the key comes up and the seek is made.</summary>
    private bool _keying;

    private void WireScrubbing()
    {
        // With handledEventsToo: the slider and its thumb handle their own pointer and key events.
        PositionSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(Scrub_Pressed), true);
        PositionSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(Scrub_Released), true);
        PositionSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(Scrub_Released), true);
        PositionSlider.AddHandler(KeyDownEvent, new KeyEventHandler(Scrub_KeyDown), true);
        PositionSlider.AddHandler(KeyUpEvent, new KeyEventHandler(Scrub_KeyUp), true);

        DataContextChanged += (_, _) => Follow(Model);
    }

    // The slider following the playhead.
    //
    // The value is set here on every `Position` or `Duration` change and is not bound. A one-way binding is
    // dropped the first time the slider writes its own value (a press on the thumb, a clamp to a shorter
    // track), and the thumb then stays still while the music plays.

    /// <summary>The model whose changes the slider follows, so it can be let go of when the view's model changes.</summary>
    private RPaMpModel? _following;

    private void Follow(RPaMpModel? model)
    {
        if (ReferenceEquals(_following, model)) return;
        if (_following is not null) _following.PropertyChanged -= OnModelChangedForSlider;
        _following = model;
        if (model is not null) model.PropertyChanged += OnModelChangedForSlider;
        FollowPlayhead();
    }

    private void OnModelChangedForSlider(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RPaMpModel.Position) or nameof(RPaMpModel.Duration) or null or "")
            FollowPlayhead();
    }

    /// <summary>Puts the playhead on the slider, unless a person is holding it or has a key down on it.</summary>
    private void FollowPlayhead()
    {
        if (_scrubbing || _keying || _following is not { } m) return;
        PositionSlider.Value = m.Position;
    }

    private void Scrub_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (IsSeekKey(e.Key))
            _keying = true;
    }

    private void Scrub_Pressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

    private void Scrub_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        SeekToSlider("the skin's position slider, released", margin: 1.5);
    }

    private void Scrub_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsSeekKey(e.Key)) return;

        // Half a second and not the pointer's 1.5: an arrow moves the thumb by one second, and the value is
        // not bound, so there is no playhead noise for a wider margin to absorb. With 1.5, an arrow
        // press was ignored.
        SeekToSlider("the skin's position slider, keys", margin: 0.5);
        _keying = false;
    }

    /// <summary>The keys that the slider moves itself on. PageUp and PageDown are not among them: they do not
    /// move the thumb.</summary>
    private static bool IsSeekKey(Windows.System.VirtualKey key) =>
        key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
            or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
            or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End;

    /// <summary>Seeks to where the slider was left, when that is more than <paramref name="margin"/> seconds from the
    /// playhead. A press and release in place is not a seek.</summary>
    private void SeekToSlider(string why, double margin)
    {
        if (Model is { } m && Math.Abs(PositionSlider.Value - m.Position) > margin)
            m.SeekTo(PositionSlider.Value, why);
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Model is not { } m || PresetBox.SelectedIndex < 0) return;
        m.EqOn = true;
        m.ApplyPreset(Presets[PresetBox.SelectedIndex].Gains);
    }

    // Scrolling ticker: the full tag line, looping like a Winamp title bar.

    private const double TickerGap = 60;      // blank run between the end and the repeat
    private const double TickerSpeed = 0.55;  // px per tick

    private DispatcherTimer? _tickerTimer;
    private double _tickerOffset;
    private double _tickerWidth;
    private string _tickerText = "";

    private void StartTicker()
    {
        _tickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _tickerTimer.Tick += OnTickerTick;
        _tickerTimer.Start();
        UpdateTickerText();
    }

    private void Ticker_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Uno's Canvas has no ClipToBounds. Clip with a geometry so that the text cannot spill out.
        TickerCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
        LayoutTicker();
    }

    /// <summary>Picks up a new track's tag line and restarts the scroll.</summary>
    private void UpdateTickerText()
    {
        // Uppercased: the Doto dot-matrix face is a caps design and reads best all-caps.
        var text = (Model?.NowPlayingText ?? "").ToUpperInvariant();
        if (text == _tickerText) return;
        _tickerText = text;
        Ticker1.Text = text;
        Ticker2.Text = text;
        _tickerOffset = 0;
        LayoutTicker();
    }

    private void LayoutTicker()
    {
        Ticker1.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _tickerWidth = Ticker1.DesiredSize.Width;

        // Only a line that is too long to fit scrolls; short ones sit still. The timer runs only while
        // something scrolls: a sitting line would tick 33 times a second for nothing (idle timers pin the CPU
        // on Uno-Skia). The text of a new track arrives by model event and not by polling: `UpdateTickerText`
        // lands here and restarts the timer if the new line is long.
        bool scrolls = _tickerWidth > TickerCanvas.ActualWidth && TickerCanvas.ActualWidth > 0;
        Ticker2.Visibility = scrolls ? Visibility.Visible : Visibility.Collapsed;
        if (!scrolls) _tickerOffset = 0;

        if (scrolls) _tickerTimer?.Start();
        else _tickerTimer?.Stop();

        Canvas.SetLeft(Ticker1, _tickerOffset);
        Canvas.SetLeft(Ticker2, _tickerOffset + _tickerWidth + TickerGap);
    }

    private void OnTickerTick(object? sender, object e)
    {
        if (Ticker2.Visibility != Visibility.Visible) { UpdateTickerText(); return; }

        _tickerOffset -= TickerSpeed;
        var span = _tickerWidth + TickerGap;
        if (_tickerOffset <= -span)
        {
            _tickerOffset += span;   // seamless wrap: the second copy has already taken over
            UpdateTickerText();      // a new track's text swaps in at the loop point
        }

        Canvas.SetLeft(Ticker1, _tickerOffset);
        Canvas.SetLeft(Ticker2, _tickerOffset + span);
    }

    // ── visualizations (click the canvas to cycle, like the real thing) ──

    private enum VisMode { Bars, Scope, Mirror, Off }

    private VisMode _mode = VisMode.Bars;
    private readonly Rectangle[] _peaks = new Rectangle[BarCount];
    private readonly double[] _peakLevels = new double[BarCount];
    private readonly float[] _wave = new float[SpectrumAnalyzer.FrameSize];
    private Polyline? _scope;

    private static LinearGradientBrush BarBrush() => new()
    {
        StartPoint = new Windows.Foundation.Point(0, 1),
        EndPoint = new Windows.Foundation.Point(0, 0),
        GradientStops =
        {
            // Bottom to top: a cool grey base rising to a white glass tip (neutral, matches the frosted
            // chrome).
            new GradientStop { Offset = 0, Color = Color.FromArgb(0xFF, 0x9F, 0xAD, 0xB5) },
            new GradientStop { Offset = 1, Color = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) },
        },
    };

    private void Visualizer_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _mode = (VisMode)(((int)_mode + 1) % 4);
        Array.Clear(_levels);
        Array.Clear(_peakLevels);
        BuildBars();
        if (_visTimer is { IsEnabled: false } t && Model?.IsPlaying == true) t.Start();
    }

    /// <summary>Rebuilds the canvas for the current mode (bars + peak caps, a scope polyline, or
    /// nothing at all).</summary>
    private void BuildBars()
    {
        VisualizerCanvas.Children.Clear();
        Array.Clear(_bars);
        Array.Clear(_peaks);
        _scope = null;

        var w = VisualizerCanvas.ActualWidth;
        var h = VisualizerCanvas.ActualHeight;
        if (w <= 0 || h <= 0 || _mode == VisMode.Off) return;

        if (_mode == VisMode.Scope)
        {
            _scope = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xEA, 0xF0, 0xF3)),
                StrokeThickness = 1.4,
            };
            VisualizerCanvas.Children.Add(_scope);
            return;
        }

        var gap = 2.0;
        var barW = Math.Max(1.0, (w - gap * (BarCount - 1)) / BarCount);
        for (int i = 0; i < BarCount; i++)
        {
            var r = new Rectangle
            {
                Width = barW,
                Height = 1,
                RadiusX = 1,
                RadiusY = 1,
                Fill = BarBrush(),
            };
            Canvas.SetLeft(r, i * (barW + gap));
            Canvas.SetTop(r, h - 1);
            VisualizerCanvas.Children.Add(r);
            _bars[i] = r;

            if (_mode == VisMode.Bars)
            {
                // classic falling peak cap
                var cap = new Rectangle
                {
                    Width = barW,
                    Height = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                };
                Canvas.SetLeft(cap, i * (barW + gap));
                Canvas.SetTop(cap, h - 2);
                VisualizerCanvas.Children.Add(cap);
                _peaks[i] = cap;
            }
        }
    }

    private void OnVisualizerTick(object? sender, object e)
    {
        long tAmp = PipelineProbe.Begin(PipelineProbe.Stage.Amp);
        try { VisualizerTick(); }
        finally { PipelineProbe.Work(PipelineProbe.Stage.Amp, tAmp); }
    }

    private void VisualizerTick()
    {
        var h = VisualizerCanvas.ActualHeight;
        var w = VisualizerCanvas.ActualWidth;
        if (h <= 0) return;

        bool playing = Model?.IsPlaying == true;

        // One copy of the samples the sound device is playing right now: the bars FFT it, the
        // scope draws it directly. Null = nothing queued yet, so hold the last frame.
        bool haveAudio = playing && Model is { } m && m.CopyPlayFrame(_wave);
        float[]? bands = haveAudio && _mode != VisMode.Scope && _mode != VisMode.Off
            ? _spectrum.Compute(_wave, BarCount)
            : null;

        if (_mode == VisMode.Off)
        {
            if (!playing) _visTimer?.Stop();
            return;
        }

        if (_mode == VisMode.Scope)
        {
            RenderScope(haveAudio, playing, w, h);
            if (!playing) _visTimer?.Stop();
            return;
        }

        double maxLevel = 0;
        double maxPeak = 0;
        for (int i = 0; i < BarCount; i++)
        {
            // No bands = nothing queued: HOLD the level exactly. Any decay here reads as a flicker.
            double target = playing ? (bands is not null ? bands[i] : _levels[i]) : 0;
            var rate = target > _levels[i] ? 0.55 : 0.16;   // fast attack, gentle release
            _levels[i] += (target - _levels[i]) * rate;
            if (_levels[i] > maxLevel) maxLevel = _levels[i];

            var bh = Math.Max(1, _levels[i] * (h - 2));
            if (_bars[i] is { } r)
            {
                r.Height = bh;
                Canvas.SetTop(r, _mode == VisMode.Mirror ? (h - bh) / 2 : h - bh);
            }

            // peak cap: snaps up with the bar, then drifts down on its own
            if (_mode == VisMode.Bars && _peaks[i] is { } cap)
            {
                _peakLevels[i] = Math.Max(_peakLevels[i] - 0.012, _levels[i]);
                Canvas.SetTop(cap, Math.Clamp(h - 2 - _peakLevels[i] * (h - 2), 0, h - 2));
            }
            if (_peakLevels[i] > maxPeak) maxPeak = _peakLevels[i];
        }

        // Stop only once the caps have come down too. The bars settle in about a second, but a cap drifts
        // down at 0.012 a tick. Stopping on the bars alone would leave the caps stuck above the bars.
        if (!playing && maxLevel < 0.01 && maxPeak < 0.01)
            _visTimer?.Stop();
    }

    /// <summary>Winamp's oscilloscope: the raw waveform, decimated to the canvas width.</summary>
    private void RenderScope(bool haveAudio, bool playing, double w, double h)
    {
        if (_scope is null || w <= 0) return;

        var pts = new Microsoft.UI.Xaml.Media.PointCollection();
        int steps = Math.Max(2, Math.Min(220, (int)w));
        int per = _wave.Length / steps;
        double mid = h / 2;

        for (int i = 0; i < steps; i++)
        {
            double v = 0;
            if (haveAudio)
            {
                // peak of the slice, so fast transients stay visible after decimation
                for (int j = 0; j < per; j++)
                {
                    var s = _wave[i * per + j];
                    if (Math.Abs(s) > Math.Abs(v)) v = s;
                }
            }
            var x = i * (w / (steps - 1));
            var y = Math.Clamp(mid - v * (mid - 1) * 0.95, 0, h);
            pts.Add(new Windows.Foundation.Point(x, y));
        }
        _scope.Points = pts;
    }
}

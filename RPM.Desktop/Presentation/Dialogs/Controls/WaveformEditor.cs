using RPM.Core.Splitting;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SkiaSharp;
using Windows.Foundation;
using Windows.System;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>One track as the waveform draws it: where it runs, what it is called, and the colour of the
/// boundary it was found by.</summary>
public sealed record WaveTrack(double Start, double End, uint Number, string? Title, uint EdgeColor);

/// <summary>The waveform of an album-length recording, with the track boundaries drawn on it. The
/// boundaries can be dragged.
///
/// <para>Splitting a record by typing times into a grid is guesswork. The numbers do not show where the
/// music actually stops. This control shows the recording, and every tool works on what is on screen.
/// Drag a marker to move a cut. Zoom in until a second is wide enough to place a cut exactly. Drag
/// across the picture to select a stretch and hear it. Split a track in two at the cursor, or join two
/// tracks back together.</para>
///
/// <para>It is drawn with Skia, not with XAML shapes. A twenty-minute side is a few hundred thousand
/// peak buckets, and a redraw touches every column on screen. As elements, that would be a layout pass
/// for every frame of a drag, which is the stutter this control must avoid. The canvas comes from <see
/// cref="SkiaSurface"/>, which each head supplies. The Uno head caches its drawing until it is
/// invalidated, so a waveform that is standing still costs nothing.</para>
///
/// <para>The control has no editing rules of its own. It reports what the pointer did: this edge moved
/// to this second, or this stretch was selected. The dialog applies that through <see
/// cref="TrackRegions"/> and hands the tracks back. There is one source of truth, so what is drawn is
/// always what will be cut.</para>
/// </summary>
public sealed partial class WaveformEditor : Grid
{
    // ── geometry ──
    private const float RulerHeight = 18;
    private const float MinimapHeight = 28;
    private const float MinimapGap = 4;
    private const float EdgeGrabPixels = 6;
    private const float HandleWidth = 9;
    private const float HandleHeight = 13;

    /// <summary>A press and release that never travelled this far is a click, not a drag.</summary>
    private const double ClickSlopPixels = 3;

    /// <summary>Two presses this close in time, on the same spot, are a double-click. The time is
    /// measured here, by hand, because the framework's tap gestures are unreliable under Uno Skia. The
    /// shell's drag grip also counts its own taps for the same reason.</summary>
    private const long DoubleClickMs = 400;

    private readonly SkiaSurface _surface;
    private readonly InputLayer _input = new();

    private WavePeaks? _peaks;
    private float[] _envelope = [];
    private double _window = 0.05;
    private double _total;

    /// <summary>The scale for a full-scale sample. It is based on the loud parts of this recording, not
    /// on an imaginary signal that peaks at 0 dBFS. A quiet transfer would otherwise draw as a flat
    /// line.</summary>
    private float _gain = 1;

    private IReadOnlyList<WaveTrack> _tracks = [];

    /// <summary>The track starts from an adopted release, drawn over the recording as marks (see <see
    /// cref="SetGuides"/>). Nothing can drag them, and nothing is cut at them.</summary>
    private IReadOnlyList<double> _guides = [];

    private int _current = -1;
    private WaveView _view;
    private double _cursor;
    private double? _playhead;
    private (double From, double To)? _selection;

    private enum Drag { None, Edge, Select, Scrub, Minimap }

    private Drag _drag;
    private int _dragIndex = -1;
    private bool _dragIsEnd;
    private double _selectAnchor;
    private Point _pressPoint;
    private bool _moved;
    private long _lastPressTick;
    private Point _lastPressPoint;

    private int _hoverIndex = -1;
    private bool _hoverIsEnd;

    public WaveformEditor()
    {
        // The canvas is created on every head. Gating it on a platform check would leave WinUI with no
        // canvas, and the dialog would show a black panel with a working toolbar over it. SkiaSurface
        // is whichever element the head being compiled provides.
        _surface = new SkiaSurface { Draw = Render };
        Children.Add(_surface);

        // Pointer input goes through a transparent layer over the canvas. A canvas is not a hit-test
        // target on either head. The handlers go on a layer that reliably is one, so every gesture is
        // handled in one place.
        Children.Add(_input);
        _input.PointerPressed += OnPointerPressed;
        _input.PointerMoved += OnPointerMoved;
        _input.PointerReleased += OnPointerReleased;
        _input.PointerCaptureLost += OnPointerLost;
        _input.PointerExited += OnPointerExited;
        _input.PointerWheelChanged += OnPointerWheel;

        SizeChanged += (_, _) => Invalidate();
        Unloaded += (_, _) => DisposePaints();
    }

    // ── what is being shown ──

    /// <summary>The audio: the peaks to draw, and the detector's envelope. The envelope is the
    /// fallback, and it is what a snapped drag searches for quiet points in.</summary>
    public void SetAudio(WavePeaks? peaks, float[] envelopeDb, double windowSeconds, double totalSeconds)
    {
        _peaks = peaks;
        _envelope = envelopeDb ?? [];
        _window = windowSeconds > 0 ? windowSeconds : 0.05;
        _total = Math.Max(0.001, totalSeconds);
        _gain = MeasureGain();
        _miniPath?.Dispose();   // A different recording draws a different overview
        _miniPath = null;
        _view = WaveTimeline.Fit(_total);
        _viewHeld = false;   // This is a different recording, so nobody is looking at anything in it yet
        _cursor = Math.Clamp(_cursor, 0, _total);
        Invalidate();
    }

    /// <summary>The tracks as they now stand. This is called after every edit. The control draws what
    /// it is given. It does not keep its own copy of the truth.</summary>
    public void SetTracks(IReadOnlyList<WaveTrack> tracks, int current)
    {
        _tracks = tracks ?? [];
        _current = current;
        Invalidate();
    }

    /// <summary>Times to mark but not to cut at: where an adopted release says its tracks begin. They
    /// are drawn as red guides, the same red as the album dot in the grid. So the sleeve's version of
    /// the record can be compared with the record, without moving any boundary. Pass an empty list to
    /// clear them.
    /// </summary>
    public void SetGuides(IReadOnlyList<double> seconds)
    {
        _guides = seconds ?? [];
        Invalidate();
    }

    public double TotalSeconds => _total;

    public WaveView View => _view;

    /// <summary>Where the next split would go, and where playback starts from.</summary>
    public double CursorSeconds
    {
        get => _cursor;
        set
        {
            double at = Math.Clamp(value, 0, _total);
            if (Math.Abs(at - _cursor) < 1e-6)
                return;
            _cursor = at;
            Invalidate();
        }
    }

    /// <summary>The stretch under the rubber band, or null when nothing is selected.</summary>
    public (double From, double To)? Selection
    {
        get => _selection;
        set
        {
            _selection = Normalize(value);
            Invalidate();
        }
    }

    public bool HasSelection => _selection is { } s && s.To - s.From > 0.05;

    /// <summary>Where the sound has got to, or null when nothing is playing.</summary>
    public double? PlayheadSeconds
    {
        get => _playhead;
        set
        {
            if (_playhead is { } a && value is { } b && Math.Abs(a - b) < 0.01)
                return;
            _playhead = value;
            if (value is { } at)
            {
                // Playback has reached the part the user is looking at. Following the playhead starts
                // again.
                if (_viewHeld && _view.Contains(at))
                    _viewHeld = false;

                if (FollowPlayhead && !_viewHeld)
                {
                    var next = WaveTimeline.Follow(_view, at, _total);
                    if (next != _view)
                    {
                        _view = next;
                        ViewChanged?.Invoke();
                    }
                }
            }
            Invalidate();
        }
    }

    /// <summary>Keep a playing position on screen by scrolling the view when it reaches the
    /// edge.</summary>
    public bool FollowPlayhead { get; set; } = true;

    /// <summary>The user moved the view while something was playing, so the playhead stops moving it.
    /// Two things must not fight over one viewport. Looking somewhere else during playback is a
    /// deliberate act. A view that snaps back sixty times a second cannot be read at all.
    ///
    /// <para>This ends by itself when the sound reaches what the user is looking at (see <see
    /// cref="PlayheadSeconds"/>). For example, if the user scrolls ahead to the next track, following
    /// resumes when the playhead gets there. It also ends when a new preview starts (<see
    /// cref="ResumeFollow"/>).</para>
    /// </summary>
    private bool _viewHeld;

    /// <summary>Gives the view back to the playhead. Starting a preview does this.</summary>
    public void ResumeFollow() => _viewHeld = false;

    /// <summary>Pull a dragged cut onto the quietest point within a few pixels of where it was dropped.
    /// That is the gap between two tracks, not a point inside the second one. Hold Shift while dragging
    /// to place it exactly where the pointer is.</summary>
    public bool SnapToSilence { get; set; } = true;

    // ── what the pointer did ──

    /// <summary>A boundary is being moved. <c>committed</c> is false while the drag is in progress, and
    /// true when the pointer is released. The dialog decides where the boundary is allowed to
    /// land.</summary>
    public event Action<int, bool, double, bool>? EdgeDragged;

    /// <summary>The cursor moved. This happens on a click on the waveform, or on a scrub along the
    /// ruler.</summary>
    public event Action<double>? CursorChanged;

    public event Action<(double From, double To)?>? SelectionChanged;

    /// <summary>A track was clicked (the row to highlight), or -1 for the gap between two
    /// tracks.</summary>
    public event Action<int>? CurrentChanged;

    /// <summary>A track was double-clicked: play it.</summary>
    public event Action<int>? TrackActivated;

    /// <summary>A boundary handle was double-clicked: join the two tracks it separates.</summary>
    public event Action<int>? BoundaryActivated;

    public event Action? ViewChanged;

    // ── zoom ──

    public void ZoomIn() => SetView(WaveTimeline.ZoomAt(_view, _view.Start + _view.Span / 2, WaveTimeline.ZoomStep, _total));

    public void ZoomOut() => SetView(WaveTimeline.ZoomAt(_view, _view.Start + _view.Span / 2, 1 / WaveTimeline.ZoomStep, _total));

    public void ZoomFit() => SetView(WaveTimeline.Fit(_total));

    public void ZoomTo(double from, double to) => SetView(WaveTimeline.Around(from, to, _total));

    /// <summary>Frames the selection, or the current track when there is none.</summary>
    public void ZoomToSelection()
    {
        if (_selection is { } s && s.To - s.From > 0.05)
            ZoomTo(s.From, s.To);
        else if (_current >= 0 && _current < _tracks.Count)
            ZoomTo(_tracks[_current].Start, _tracks[_current].End);
    }

    public void ScrollTo(double seconds) => SetView(WaveTimeline.CenterOn(_view, seconds, _total));

    /// <summary>Brings a time on screen without moving the view if it is already comfortably in it.
    /// This is what selecting a row in the grid does to the waveform.</summary>
    public void EnsureVisible(double from, double to)
    {
        if (_view.Contains(from) && _view.Contains(to))
            return;
        if (to - from > _view.Span)
            ZoomTo(from, to);
        else
            SetView(WaveTimeline.CenterOn(_view, (from + to) / 2, _total));
    }

    /// <summary>Every way the view moves, except the playhead's own following: the wheel, the overview
    /// strip, the zoom keys, a drag that reaches the edge, and a row that brings its track on screen.
    /// All of these are the user's doing. So each one takes the view off the playhead until the sound
    /// catches up.
    /// </summary>
    private void SetView(WaveView view)
    {
        if (view == _view)
            return;
        _viewHeld = true;
        _view = view;
        ViewChanged?.Invoke();
        Invalidate();
    }

    private void Invalidate() => _surface.Redraw();

    private static (double From, double To)? Normalize((double From, double To)? span) =>
        span is not { } s ? null : s.To < s.From ? (s.To, s.From) : s;

    // ── pointer ──

    private double BodyTop => RulerHeight;

    private double BodyBottom => Math.Max(BodyTop + 1, ActualHeight - MinimapHeight - MinimapGap);

    private double BodyWidth => Math.Max(1, ActualWidth);

    private double TimeAt(double x) => Math.Clamp(WaveTimeline.XToTime(_view, x, BodyWidth), 0, _total);

    private double XAt(double seconds) => WaveTimeline.TimeToX(_view, seconds, BodyWidth);

    /// <summary>The boundary handle under a point, if any: (track index, is it the track's
    /// end).</summary>
    private (int Index, bool IsEnd)? EdgeAt(Point point)
    {
        if (point.Y < BodyTop || point.Y > BodyBottom)
            return null;

        // The nearest edge wins. When two edges are at the same place, for example a gapless rip's
        // shared boundary, either one moves both. So which one is picked up does not matter.
        (int Index, bool IsEnd)? best = null;
        double bestDistance = EdgeGrabPixels;
        for (int i = 0; i < _tracks.Count; i++)
        {
            for (int e = 0; e < 2; e++)
            {
                bool isEnd = e == 1;
                double distance = Math.Abs(XAt(isEnd ? _tracks[i].End : _tracks[i].Start) - point.X);
                if (distance > bestDistance)
                    continue;
                bestDistance = distance;
                best = (i, isEnd);
            }
        }
        return best;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_input).Position;
        _input.CapturePointer(e.Pointer);
        _pressPoint = point;
        _moved = false;
        e.Handled = true;

        bool doubleClick = Environment.TickCount64 - _lastPressTick <= DoubleClickMs
            && Math.Abs(point.X - _lastPressPoint.X) < 6 && Math.Abs(point.Y - _lastPressPoint.Y) < 6;
        _lastPressTick = Environment.TickCount64;
        _lastPressPoint = point;

        // The overview strip: press anywhere in it to go there.
        if (point.Y > BodyBottom)
        {
            _drag = Drag.Minimap;
            MinimapTo(point.X);
            return;
        }

        // The ruler is the scrub bar.
        if (point.Y < BodyTop)
        {
            _drag = Drag.Scrub;
            _cursor = TimeAt(point.X);
            CursorChanged?.Invoke(_cursor);
            Invalidate();
            return;
        }

        if (EdgeAt(point) is { } edge)
        {
            if (doubleClick)
            {
                // The handle between two tracks, double-clicked: join them.
                int boundary = edge.IsEnd ? edge.Index : edge.Index - 1;
                if (boundary >= 0)
                    BoundaryActivated?.Invoke(boundary);
                _drag = Drag.None;
                return;
            }
            _drag = Drag.Edge;
            _dragIndex = edge.Index;
            _dragIsEnd = edge.IsEnd;
            Invalidate();
            return;
        }

        int track = TrackRegions.IndexAt(_tracks.Select(t => new TrackSpan(t.Start, t.End)).ToList(), TimeAt(point.X));
        if (doubleClick && track >= 0)
        {
            TrackActivated?.Invoke(track);
            _drag = Drag.None;
            return;
        }

        _drag = Drag.Select;
        _selectAnchor = TimeAt(point.X);
        if (track != _current)
        {
            _current = track;
            CurrentChanged?.Invoke(track);
        }
        Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_input).Position;

        if (_drag == Drag.None)
        {
            UpdateHover(point);
            return;
        }

        if (Math.Abs(point.X - _pressPoint.X) > ClickSlopPixels || Math.Abs(point.Y - _pressPoint.Y) > ClickSlopPixels)
            _moved = true;

        switch (_drag)
        {
            case Drag.Edge:
            {
                double at = DragTime(point, e.KeyModifiers);
                EdgeDragged?.Invoke(_dragIndex, _dragIsEnd, at, false);
                break;
            }
            case Drag.Select:
            {
                double at = TimeAt(point.X);
                AutoScroll(point.X);
                _selection = Normalize((_selectAnchor, at));
                SelectionChanged?.Invoke(_selection);
                Invalidate();
                break;
            }
            case Drag.Scrub:
            {
                _cursor = TimeAt(point.X);
                CursorChanged?.Invoke(_cursor);
                Invalidate();
                break;
            }
            case Drag.Minimap:
                MinimapTo(point.X);
                break;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_input).Position;
        var drag = _drag;
        _drag = Drag.None;
        _input.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        switch (drag)
        {
            case Drag.Edge:
                EdgeDragged?.Invoke(_dragIndex, _dragIsEnd, DragTime(point, e.KeyModifiers), true);
                _dragIndex = -1;
                break;

            case Drag.Select when !_moved:
                // A click, not a band: drop the selection and put the cursor where the click was.
                _selection = null;
                SelectionChanged?.Invoke(null);
                _cursor = TimeAt(point.X);
                CursorChanged?.Invoke(_cursor);
                break;

            case Drag.Select:
                _selection = Normalize((_selectAnchor, TimeAt(point.X)));
                // A selection is where the next play starts from, so the cursor goes to its head.
                if (_selection is { } s)
                {
                    _cursor = s.From;
                    CursorChanged?.Invoke(_cursor);
                }
                SelectionChanged?.Invoke(_selection);
                break;
        }
        UpdateHover(point);
        Invalidate();
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == Drag.Edge && _dragIndex >= 0)
            EdgeDragged?.Invoke(_dragIndex, _dragIsEnd, _dragIsEnd ? _tracks[_dragIndex].End : _tracks[_dragIndex].Start, true);
        _drag = Drag.None;
        _dragIndex = -1;
        Invalidate();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_drag != Drag.None)
            return;
        _hoverIndex = -1;
        _input.SetCursor(InputSystemCursorShape.Arrow);
        Invalidate();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_input);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        e.Handled = true;

        // Shift scrolls sideways. Without Shift, the wheel zooms around the pointer, which is the
        // gesture that every waveform editor shares.
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            SetView(WaveTimeline.Pan(_view, -delta / 120.0 * _view.Span * 0.15, _total));
            return;
        }
        double factor = delta > 0 ? WaveTimeline.ZoomStep : 1 / WaveTimeline.ZoomStep;
        SetView(WaveTimeline.ZoomAt(_view, TimeAt(point.Position.X), factor, _total));
    }

    /// <summary>Where a dragged edge wants to go: the pointer position, pulled onto the quietest nearby
    /// point unless snapping is off or Shift is held.</summary>
    private double DragTime(Point point, VirtualKeyModifiers modifiers)
    {
        double at = TimeAt(point.X);
        AutoScroll(point.X);
        if (!SnapToSilence || modifiers.HasFlag(VirtualKeyModifiers.Shift) || _envelope.Length == 0)
            return at;

        // The search is only a few pixels wide. It is never wide enough for a zoomed-out drag to snap
        // to a different place. At one minute per pixel, the cut stays where it was dropped.
        double radius = Math.Min(1.5, WaveTimeline.SecondsPerPixel(_view, BodyWidth) * EdgeGrabPixels);
        return SilenceDetector.SnapToLocalMinimum(_envelope, _window, at, radius);
    }

    /// <summary>Dragging against the edge of the control scrolls the view under the pointer. So a cut
    /// can be dragged to a place off screen without letting go.</summary>
    private void AutoScroll(double x)
    {
        const double margin = 26;
        double step = _view.Span * 0.02;
        if (x < margin)
            SetView(WaveTimeline.Pan(_view, -step, _total));
        else if (x > BodyWidth - margin)
            SetView(WaveTimeline.Pan(_view, step, _total));
    }

    private void MinimapTo(double x)
    {
        double at = Math.Clamp(x / BodyWidth, 0, 1) * _total;
        SetView(WaveTimeline.CenterOn(_view, at, _total));
    }

    private void UpdateHover(Point point)
    {
        var edge = EdgeAt(point);
        int index = edge?.Index ?? -1;
        bool isEnd = edge?.IsEnd ?? false;
        if (index == _hoverIndex && isEnd == _hoverIsEnd)
            return;

        _hoverIndex = index;
        _hoverIsEnd = isEnd;
        _input.SetCursor(index >= 0 ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
        Invalidate();
    }

    // ── drawing ──

    private SKPaint? _fill;
    private SKPaint? _stroke;
    private SKFont? _labelFont;
    private SKFont? _tinyFont;
    private SKPath? _miniPath;
    private float _miniWidth, _miniHeight, _miniTop;

    private void DisposePaints()
    {
        _fill?.Dispose();
        _stroke?.Dispose();
        _labelFont?.Dispose();
        _tinyFont?.Dispose();
        _miniPath?.Dispose();
        _fill = null;
        _stroke = null;
        _labelFont = null;
        _tinyFont = null;
        _miniPath = null;
    }

    private SKPaint Fill => _fill ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

    private SKPaint Stroke => _stroke ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

    private SKFont Label => _labelFont ??= new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Normal) ?? SKTypeface.Default, 11.5f);

    private SKFont Tiny => _tinyFont ??= new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Normal) ?? SKTypeface.Default, 9.5f);

    // ── the palette ──
    //
    // No colour here is picked by eye. Every accent comes from Assets/RpmPalette.xaml. The recording is
    // drawn in RPM azure, the deep blue. The brighter colours sit on top of it, and each one means one
    // thing. The markers use the same four colours as the dot legend under the grid: dark green for a
    // cue sheet, gold for a detected silence, vermilion for an album time, and white for a hand edit.
    // So the waveform and the legend use the same colours for the same things.

    /// <summary>The colour of the recording itself: the dialog header's blue (the lit grooves in the
    /// vinyl photograph across the top of the card), taken down to a grey-blue. A waveform this size is
    /// a solid field of colour. A saturated colour would shout down every mark on it. The markers, the
    /// guides and the playhead are what must be read here, not the sound behind them.</summary>
    private static readonly SKColor WaveBlue = new(0x4F, 0x6A, 0x85);

    /// <summary>RPM azure, the palette blue, for the overview strip's ticks.</summary>
    private static readonly SKColor Azure = new(0x03, 0x71, 0xD0);

    /// <summary>rpm cyan: not the sound, but where you are in it. It marks the track being worked
    /// on.</summary>
    private static readonly SKColor Cyan = new(0x04, 0xC8, 0xFF);

    /// <summary>RPM vermilion: the album's own times, which are guides and never cuts.</summary>
    private static readonly SKColor Vermilion = new(0xD2, 0x31, 0x0E);

    /// <summary>RPM magenta: a selection. A selection is something you are holding, not something the
    /// record has. So it has a colour that nothing else here uses.</summary>
    private static readonly SKColor Magenta = new(0xAC, 0x52, 0x8D);

    private static readonly SKColor Backdrop = new(0x06, 0x0F, 0x16);
    private static readonly SKColor RulerBack = new(0x0A, 0x18, 0x22);
    private static readonly SKColor Hairline = new(0x16, 0x30, 0x3F);
    private static readonly SKColor WaveInk = WaveBlue;

    /// <summary>Audio inside no track. It is the same blue, taken further down. It reads as the
    /// recording still, but it is clearly not part of anything that will be written out.</summary>
    private static readonly SKColor WaveOrphan = new(0x2A, 0x39, 0x47);

    private static readonly SKColor BandInk = new(0xFF, 0xFF, 0xFF, 0x0C);
    private static readonly SKColor BandCurrent = Cyan.WithAlpha(0x24);
    private static readonly SKColor SelectionInk = Magenta.WithAlpha(0x40);
    private static readonly SKColor SelectionEdge = new(0xDD, 0x9E, 0xC6);
    private static readonly SKColor GuideInk = Vermilion.WithAlpha(0xC8);
    private static readonly SKColor TextInk = new(0xCC, 0xE6, 0xFF);
    private static readonly SKColor MutedInk = new(0xFF, 0xFF, 0xFF, 0x99);
    private static readonly SKColor PlayInk = new(0xFF, 0xFF, 0xFF);

    private void Render(SKCanvas canvas, Size area)
    {
        float w = (float)area.Width;
        float h = (float)area.Height;
        if (w < 8 || h < 8 || _total <= 0 || _view.Span <= 0)
            return;   // Nothing is analysed yet. Draw an empty well, and avoid dividing by zero

        float bodyTop = RulerHeight;
        float bodyBottom = Math.Max(bodyTop + 8, h - MinimapHeight - MinimapGap);

        Fill.Color = Backdrop;
        canvas.DrawRect(0, 0, w, h, Fill);

        DrawBands(canvas, w, bodyTop, bodyBottom);
        DrawWave(canvas, w, bodyTop, bodyBottom);
        DrawSelection(canvas, w, bodyTop, bodyBottom);
        DrawGuides(canvas, w, bodyTop, bodyBottom);
        DrawRuler(canvas, w);
        DrawEdges(canvas, w, bodyTop, bodyBottom);
        DrawLabels(canvas, w, bodyTop);
        DrawCursor(canvas, w, bodyTop, bodyBottom);
        DrawMinimap(canvas, w, h - MinimapHeight, MinimapHeight);
    }

    /// <summary>The track bands behind the wave. A dimmed wash covers the stretches that belong to no
    /// track: the lead-in, the run-out, and the gap between two songs. Dark here means the audio will
    /// not be in any file afterwards. That is the hardest thing to see in a list of times.</summary>
    private void DrawBands(SKCanvas canvas, float w, float top, float bottom)
    {
        Fill.Color = new SKColor(0, 0, 0, 0x66);
        canvas.DrawRect(0, top, w, bottom - top, Fill);

        for (int i = 0; i < _tracks.Count; i++)
        {
            float x0 = (float)XAt(_tracks[i].Start);
            float x1 = (float)XAt(_tracks[i].End);
            if (x1 < 0 || x0 > w)
                continue;
            Fill.Color = i == _current ? BandCurrent : BandInk;
            canvas.DrawRect(x0, top, Math.Max(1, x1 - x0), bottom - top, Fill);
        }
    }

    /// <summary>The recording itself, one column of pixels at a time. Each column shows the loudest and
    /// quietest sample that it covers. That is what makes a waveform look like the sound and not like a
    /// graph. Columns inside a track are drawn in the ink colour. Columns outside a track are greyed.
    /// It is the same audio, but nobody is cutting it.
    /// </summary>
    private void DrawWave(SKCanvas canvas, float w, float top, float bottom)
    {
        int columns = (int)Math.Ceiling(w);
        if (columns <= 0)
            return;

        float mid = (top + bottom) / 2;
        float half = (bottom - top) / 2 - 2;

        // Two paths, one draw call each. A thousand separate DrawLine calls on every frame of a drag
        // would make a zoomed-in waveform slow.
        using var inside = new SKPath();
        using var orphan = new SKPath();
        int spanAt = 0;

        for (int x = 0; x < columns; x++)
        {
            double t0 = WaveTimeline.XToTime(_view, x, w);
            double t1 = WaveTimeline.XToTime(_view, x + 1, w);
            if (!Amplitude(t0, t1, out float low, out float high))
                continue;

            float yTop = mid - Math.Clamp(high * _gain, -1, 1) * half;
            float yBottom = mid - Math.Clamp(low * _gain, -1, 1) * half;
            if (yBottom - yTop < 1)
            {
                yTop = mid - 0.5f;
                yBottom = mid + 0.5f;
            }

            // Walk the tracks along with the columns instead of searching for each track. Both run from
            // left to right, and a side can have many columns.
            while (spanAt < _tracks.Count && _tracks[spanAt].End < t0)
                spanAt++;
            bool covered = spanAt < _tracks.Count && _tracks[spanAt].Start <= t1 && _tracks[spanAt].End >= t0;

            var path = covered ? inside : orphan;
            path.MoveTo(x + 0.5f, yTop);
            path.LineTo(x + 0.5f, yBottom);
        }

        Stroke.StrokeWidth = 1;
        Stroke.IsAntialias = false;
        Stroke.Color = WaveOrphan;
        canvas.DrawPath(orphan, Stroke);
        Stroke.Color = WaveInk;
        canvas.DrawPath(inside, Stroke);
        Stroke.IsAntialias = true;
    }

    /// <summary>The extremes over one column. They come from the peaks when there are any. Otherwise
    /// they come from the detector's RMS envelope. That is the case for a demo side, or for an analysis
    /// made before the peaks existed.</summary>
    private bool Amplitude(double from, double to, out float low, out float high)
    {
        if (_peaks is { Count: > 0 } peaks)
            return peaks.Range(from, to, out low, out high);

        low = 0;
        high = 0;
        if (_envelope.Length == 0)
            return false;

        int a = Math.Clamp((int)(from / _window), 0, _envelope.Length - 1);
        int b = Math.Clamp((int)Math.Ceiling(to / _window), a + 1, _envelope.Length);
        float loudest = -100f;
        for (int i = a; i < b; i++)
            if (_envelope[i] > loudest) loudest = _envelope[i];

        // Convert the RMS value from dB into a height to draw. A crest factor is added, because music
        // has peaks above its RMS level.
        high = (float)Math.Min(1.0, Math.Pow(10, loudest / 20.0) * 3.2);
        low = -high;
        return true;
    }

    /// <summary>Full scale for this recording: a high percentile of its own peaks. So a quiet transfer
    /// fills the strip, and does not hug the centre line. It is measured once per analysis.</summary>
    private float MeasureGain()
    {
        if (_peaks is not { Count: > 0 } peaks)
            return 1;

        // Every hundredth bucket is enough to find the loud end of a side.
        var sample = new List<float>(Math.Max(16, peaks.Count / 100));
        double step = peaks.BucketSeconds * 100;
        for (double at = 0; at + step <= peaks.TotalSeconds; at += step)
            if (peaks.Range(at, at + step, out var lo, out var hi))
                sample.Add(Math.Max(Math.Abs(lo), Math.Abs(hi)));
        if (sample.Count < 8)
            return 1;

        sample.Sort();
        float loud = sample[(int)(sample.Count * 0.98)];
        return loud <= 0.001f ? 1 : Math.Clamp(0.92f / loud, 1f, 24f);
    }

    private void DrawSelection(SKCanvas canvas, float w, float top, float bottom)
    {
        if (_selection is not { } selection)
            return;

        float x0 = (float)XAt(selection.From);
        float x1 = (float)XAt(selection.To);
        if (x1 < 0 || x0 > w)
            return;

        Fill.Color = SelectionInk;
        canvas.DrawRect(x0, top, Math.Max(1, x1 - x0), bottom - top, Fill);

        Stroke.Color = SelectionEdge;
        Stroke.StrokeWidth = 1;
        canvas.DrawLine(x0, top, x0, bottom, Stroke);
        canvas.DrawLine(x1, top, x1, bottom, Stroke);
    }

    /// <summary>The track starts from an adopted release: a dashed red line, with a pip at its head and
    /// the album's track number beside it. It is deliberately unlike a boundary. It is dashed and thin,
    /// and it has no grab handle, because it is not one. It shows where the sleeve puts track 4. Where
    /// track 4 actually is stays what detection found or the user placed.
    /// </summary>
    private void DrawGuides(SKCanvas canvas, float w, float top, float bottom)
    {
        if (_guides.Count == 0)
            return;

        Stroke.Color = GuideInk;
        Stroke.StrokeWidth = 1;
        Stroke.PathEffect?.Dispose();
        Stroke.PathEffect = SKPathEffect.CreateDash([2, 5], 0);

        for (int i = 0; i < _guides.Count; i++)
        {
            float x = (float)XAt(_guides[i]);
            if (x < -20 || x > w + 20)
                continue;
            canvas.DrawLine(x, top + 6, x, bottom, Stroke);
        }

        Stroke.PathEffect?.Dispose();
        Stroke.PathEffect = null;

        // The pips and their numbers are drawn afterwards, so the dash pattern does not touch them.
        Fill.Color = GuideInk;
        for (int i = 0; i < _guides.Count; i++)
        {
            float x = (float)XAt(_guides[i]);
            if (x < -20 || x > w + 20)
                continue;

            using var pip = new SKPath();
            pip.MoveTo(x - 4, top);
            pip.LineTo(x + 4, top);
            pip.LineTo(x, top + 6);
            pip.Close();
            canvas.DrawPath(pip, Fill);

            // The number of the album track that this mark starts. The last mark only closes the last
            // track and starts nothing, so it has no number.
            if (i < _guides.Count - 1)
                canvas.DrawText((i + 1).ToString("00"), x + 6, top + 8, SKTextAlign.Left, Tiny, Fill);
        }
    }

    private void DrawRuler(SKCanvas canvas, float w)
    {
        Fill.Color = RulerBack;
        canvas.DrawRect(0, 0, w, RulerHeight, Fill);
        Stroke.Color = Hairline;
        Stroke.StrokeWidth = 1;
        canvas.DrawLine(0, RulerHeight - 0.5f, w, RulerHeight - 0.5f, Stroke);

        double step = WaveTimeline.TickSeconds(_view, w);
        double first = Math.Floor(_view.Start / step) * step;
        Fill.Color = MutedInk;
        for (double at = first; at <= _view.End + step; at += step)
        {
            if (at < 0)
                continue;
            float x = (float)XAt(at);
            if (x < -40 || x > w + 40)
                continue;
            Stroke.Color = Hairline;
            canvas.DrawLine(x, RulerHeight - 5, x, RulerHeight - 1, Stroke);
            canvas.DrawText(Clock(at, step), x + 3, RulerHeight - 6, SKTextAlign.Left, Tiny, Fill);
        }
    }

    /// <summary>Every boundary, as a line with a grab handle at its head. The handle is what makes a
    /// cut something you can take hold of. A two-pixel line is a target that nobody can hit.</summary>
    private void DrawEdges(SKCanvas canvas, float w, float top, float bottom)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            DrawEdge(canvas, w, top, bottom, i, isEnd: false);
            DrawEdge(canvas, w, top, bottom, i, isEnd: true);
        }
    }

    private void DrawEdge(SKCanvas canvas, float w, float top, float bottom, int index, bool isEnd)
    {
        var track = _tracks[index];
        double seconds = isEnd ? track.End : track.Start;
        float x = (float)XAt(seconds);
        if (x < -20 || x > w + 20)
            return;

        bool hot = (_drag == Drag.Edge && _dragIndex == index && _dragIsEnd == isEnd)
            || (_drag == Drag.None && _hoverIndex == index && _hoverIsEnd == isEnd);
        var color = new SKColor(track.EdgeColor);

        Stroke.Color = hot ? PlayInk : color;
        Stroke.StrokeWidth = hot ? 2 : 1.5f;
        canvas.DrawLine(x, top, x, bottom, Stroke);

        // A start handle sits under the ruler, and an end handle sits at the foot. So the two edges of
        // a gap can be told apart at a glance. A shared boundary shows both handles, one above the
        // other.
        float handleTop = isEnd ? bottom - HandleHeight : top;
        Fill.Color = hot ? PlayInk : color;
        var handle = new SKRect(x - HandleWidth / 2, handleTop, x + HandleWidth / 2, handleTop + HandleHeight);
        canvas.DrawRoundRect(handle, 2, 2, Fill);

        if (!hot)
            return;

        // While it is being dragged, say exactly where it is.
        string text = Clock(seconds, 0.1);
        float textWidth = Tiny.MeasureText(text);
        float boxX = Math.Clamp(x - textWidth / 2 - 4, 2, w - textWidth - 10);
        Fill.Color = new SKColor(0, 0, 0, 0xCC);
        canvas.DrawRoundRect(new SKRect(boxX, top + 3, boxX + textWidth + 8, top + 17), 3, 3, Fill);
        Fill.Color = PlayInk;
        canvas.DrawText(text, boxX + 4, top + 13.5f, SKTextAlign.Left, Tiny, Fill);
    }

    /// <summary>The track number and title inside each band, clipped to the band. This is the
    /// waveform's own key to which stretch is which row in the grid.</summary>
    private void DrawLabels(SKCanvas canvas, float w, float top)
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            var track = _tracks[i];
            float x0 = (float)XAt(track.Start);
            float x1 = (float)XAt(track.End);
            if (x1 < 0 || x0 > w || x1 - x0 < 26)
                continue;

            string text = string.IsNullOrWhiteSpace(track.Title)
                ? track.Number.ToString("00")
                : $"{track.Number:00}  {track.Title}";
            float available = x1 - x0 - 16;
            while (text.Length > 2 && Label.MeasureText(text) > available)
                text = text[..^2];

            Fill.Color = i == _current ? PlayInk : TextInk;
            canvas.DrawText(text, x0 + 8, top + 14, SKTextAlign.Left, Label, Fill);
        }
    }

    private void DrawCursor(SKCanvas canvas, float w, float top, float bottom)
    {
        float x = (float)XAt(_cursor);
        if (x >= 0 && x <= w)
        {
            Stroke.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x99);
            Stroke.StrokeWidth = 1;
            Stroke.PathEffect?.Dispose();
            Stroke.PathEffect = SKPathEffect.CreateDash([4, 3], 0);
            canvas.DrawLine(x, top, x, bottom, Stroke);
            Stroke.PathEffect?.Dispose();
            Stroke.PathEffect = null;
        }

        if (_playhead is not { } at)
            return;
        float px = (float)XAt(at);
        if (px < 0 || px > w)
            return;

        Stroke.Color = PlayInk;
        Stroke.StrokeWidth = 1.5f;
        canvas.DrawLine(px, top, px, bottom, Stroke);

        using var head = new SKPath();
        head.MoveTo(px - 5, top);
        head.LineTo(px + 5, top);
        head.LineTo(px, top + 6);
        head.Close();
        Fill.Color = PlayInk;
        canvas.DrawPath(head, Fill);
    }

    /// <summary>The whole side at a glance, with the view window drawn on it. It shows where the zoom
    /// is, and it holds the bar that moves the zoom.</summary>
    private void DrawMinimap(SKCanvas canvas, float w, float top, float height)
    {
        Fill.Color = RulerBack;
        canvas.DrawRect(0, top, w, height, Fill);

        // The overview does not change with the zoom, so it is built once and kept. Rebuilding a whole
        // side's worth of columns on every frame of a drag would be wasted work.
        if (_miniPath is null || Math.Abs(_miniWidth - w) > 0.5f || Math.Abs(_miniHeight - height) > 0.5f)
        {
            _miniPath?.Dispose();
            _miniPath = BuildMinimapPath(w, top, height);
            _miniWidth = w;
            _miniHeight = height;
            _miniTop = top;
        }
        canvas.Save();
        canvas.Translate(0, top - _miniTop);
        Stroke.StrokeWidth = 1;
        Stroke.IsAntialias = false;
        Stroke.Color = WaveBlue;
        canvas.DrawPath(_miniPath, Stroke);
        Stroke.IsAntialias = true;
        canvas.Restore();

        // Where the tracks are, as ticks along the foot. The track being worked on is cyan, so the
        // overview shows where you are in the side as well as where the zoom is.
        for (int i = 0; i < _tracks.Count; i++)
        {
            float x0 = (float)(_tracks[i].Start / _total * w);
            float x1 = (float)(_tracks[i].End / _total * w);
            Fill.Color = i == _current ? Cyan : Azure.WithAlpha(0xC8);
            canvas.DrawRect(x0, top + height - 3, Math.Max(1, x1 - x0), 2, Fill);
        }

        float viewX0 = (float)(_view.Start / _total * w);
        float viewX1 = (float)(_view.End / _total * w);
        Fill.Color = new SKColor(0, 0, 0, 0x88);
        canvas.DrawRect(0, top, Math.Max(0, viewX0), height, Fill);
        canvas.DrawRect(viewX1, top, Math.Max(0, w - viewX1), height, Fill);

        Stroke.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x77);
        Stroke.StrokeWidth = 1;
        canvas.DrawRect(new SKRect(viewX0 + 0.5f, top + 0.5f, Math.Max(viewX0 + 2, viewX1 - 0.5f), top + height - 0.5f), Stroke);

        if (_playhead is { } at)
        {
            Stroke.Color = PlayInk;
            float px = (float)(at / _total * w);
            canvas.DrawLine(px, top, px, top + height, Stroke);
        }
    }

    private SKPath BuildMinimapPath(float w, float top, float height)
    {
        var path = new SKPath();
        float mid = top + height / 2;
        float half = height / 2 - 2;
        int columns = (int)Math.Ceiling(w);
        for (int x = 0; x < columns; x++)
        {
            double t0 = (double)x / columns * _total;
            double t1 = (double)(x + 1) / columns * _total;
            if (!Amplitude(t0, t1, out float low, out float high))
                continue;
            path.MoveTo(x + 0.5f, mid - Math.Clamp(high * _gain, -1, 1) * half);
            path.LineTo(x + 0.5f, mid - Math.Clamp(low * _gain, -1, 1) * half);
        }
        return path;
    }

    /// <summary>m:ss, with tenths once the zoom is close enough for them to mean something.</summary>
    private static string Clock(double seconds, double step)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string tail = step < 1 ? $"{time.Seconds:00}.{time.Milliseconds / 100}" : $"{time.Seconds:00}";
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{tail}"
            : $"{time.Minutes}:{tail}";
    }

    /// <summary>The transparent sheet that takes the pointer. It is a Border subclass only so that the
    /// cursor can be set. <c>ProtectedCursor</c> is protected, so it can only be set from a subclass.
    /// See <see cref="RowSplitter"/>, which does the same.</summary>
    private sealed partial class InputLayer : BorderBase
    {
        private InputSystemCursorShape _shape = InputSystemCursorShape.Arrow;

        public InputLayer()
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            IsTabStop = false;
        }

        public void SetCursor(InputSystemCursorShape shape)
        {
            if (shape == _shape)
                return;
            _shape = shape;
            ProtectedCursor = InputSystemCursor.Create(shape);
        }
    }
}

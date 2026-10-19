#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Analysis;
using RPM.Core.Mixing;
using RPM.Core.Splitting;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>Which set of marks the pointer is allowed to move. Everything is drawn whatever this says;
/// only one set at a time answers to a drag.</summary>
public enum MixLabEdit
{
    /// <summary>No mark moves. A click puts the needle down. This is the mode for when the DJ is
    /// listening and not changing anything, which is most of the time. It is shown as
    /// "Playhead".</summary>
    None,

    /// <summary>The beat grid. Dragging slides the whole grid so that its bar line lands on the real
    /// one.</summary>
    Grid,

    /// <summary>Where the record is mixed in.</summary>
    MixIn,

    /// <summary>Where the record is mixed out of.</summary>
    MixOut,

    /// <summary>The changeover itself: where it starts, where it ends, and where the needle goes down
    /// on the record coming in.</summary>
    Mix,
}

/// <summary>What the pointer took hold of.</summary>
public enum MixLabHandle
{
    None,
    Downbeat,
    MixIn,
    MixOut,

    /// <summary>The start of the changeover, on the record playing.</summary>
    MixStart,

    /// <summary>The end of the changeover, on the record playing. Dragging it asks for a longer or
    /// shorter fade, in whole bars.</summary>
    MixEnd,

    /// <summary>The plan's cue point, on the record coming in.</summary>
    CueIn,
}

/// <summary>
/// One record on one clock: the waveform, everything the analyser found drawn over it, and grab handles
/// for the one set of marks that can be edited at the moment.
///
/// <para>It follows the same rules as <see cref="Dialogs.Controls.WaveformEditor"/>. That control is
/// also exact, for the same reason. Both let a person put a mark on a sample by eye. Both are useless
/// if the picture and the pointer disagree about where a second is. So the viewport maths comes from
/// <see cref="WaveTimeline"/>, not from this file. The paints are built once, not for every frame. The
/// width used for drawing is also the width the pointer is measured against.</para>
///
/// <para>Everything that does not move with the needle (the waveform, the grid, the zone and the ruler)
/// is recorded once into an <see cref="SKPicture"/>. That picture is replayed for each frame. Redrawing
/// every pixel of the waveform sixty times a second is too slow. A frame now costs one blit, a line and
/// a few marks.</para>
/// </summary>
public sealed partial class MixLabTimeline : Grid
{
    // ── geometry ──
    private const float RulerHeight = 15;
    /// <summary>The strip above the waveform and the strip below it. The ruler's marks are in the
    /// first, and the voice bands share the second. The height is seventeen, not thirteen, because the
    /// voice band holds three readings. Three lanes under four pixels each would be too thin to tell
    /// apart.</summary>
    private const float LaneHeight = 17;

    /// <summary>The three voice readings for the record on this deck: what the lyric sheet says, what
    /// the record's own middle band says, and the stretch that both of them claim. They are stored, not
    /// worked out for every frame. See <see cref="Show"/>.</summary>
    private VocalMap _told = VocalMap.Unknown, _measured = VocalMap.Unknown, _agreed = VocalMap.Unknown;
    private const float TabWidth = 11;
    private const float GrabPixels = 8;

    /// <summary>The gap between two tabs that have been moved apart so that both can be read.</summary>
    private const float TabGap = 2;

    private readonly MixLabSurface? _surface;
    private readonly MixLabInputLayer _input = new();

    // Built once. Creating a paint for each draw call, sixty times a second, makes garbage that the
    // collector must clean up. This happens while the user is placing a bar line by hand.
    private readonly SKPaint _fill = new() { IsAntialias = true };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _smooth = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKFont _tiny = new() { Size = 10 };

    /// <summary>The measured phrase, drawn dashed. It has its own paint, because the paint uses a path
    /// effect. The path effect is kept too, because building one for every frame allocates a native
    /// object.
    ///
    /// <para>The line is dashed, not only a different colour. The picture already has six kinds of
    /// vertical line. One more solid line in one more colour would need explaining to the reader. A
    /// dashed line is a different kind of line at a glance. It stays different for readers who cannot
    /// tell rose from amber.</para></summary>
    private readonly SKPaint _dashed = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        PathEffect = SKPathEffect.CreateDash([4, 4], 0),
    };

    private MixTrack? _track;
    private TransitionPlan? _plan;
    private WavePeaks? _peaks;
    private double? _cueIn;
    private double _playhead = -1;
    private float _gain = 1;

    private WaveView _view;
    private double _total = 1;

    /// <summary>The width the last frame was drawn at. Input is measured against this value, not
    /// against <see cref="FrameworkElement.ActualWidth"/>. So a click lands on the same second that is
    /// drawn under it. The two values are the same here, but they must not be assumed to stay the
    /// same.</summary>
    private float _drawnWidth = 1;

    // ── the still layer ──
    //
    // Everything that does not move with the playhead is replayed for each frame. The key says what the
    // layer was drawn from. Identity is checked, not equality, for the reading and the peaks, because
    // an edit always arrives as a new instance. The key is a struct and not a formatted string. This
    // check runs for each frame, so it must not allocate.
    private SKPicture? _still;
    private StillKey _stillFor;

    private readonly record struct StillKey(
        int Track, int Analysis, int Peaks, int Plan, double CueIn,
        double ViewStart, double ViewSpan, float Width, float Height, MixLabEdit Editing, float Gain);

    private MixLabHandle _drag = MixLabHandle.None;
    private MixLabHandle _hover = MixLabHandle.None;

    /// <summary>Which beat the right button would name, drawn under the pointer so the choice is
    /// visible before it is made. Null whenever beats are not being picked.</summary>
    private double? _hoverBeat;

    /// <summary>Which marks answer to the pointer.</summary>
    public MixLabEdit Editing
    {
        get => _editing;
        set
        {
            if (_editing == value)
                return;
            _editing = value;
            Invalidate();
        }
    }

    private MixLabEdit _editing = MixLabEdit.None;

    /// <summary>Raised as a handle is dragged, with what and where to.</summary>
    public event Action<MixLabHandle, double>? Moved;

    /// <summary>Raised when a drag finishes, so a page can write down one edit rather than
    /// forty.</summary>
    public event Action<MixLabHandle>? Dropped;

    /// <summary>Raised on a click that was not a handle: the needle goes there.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>
    /// Whether this deck's needle may be moved at all. See <see cref="BoothView.CanScrubTheLiveDeck"/>.
    ///
    /// <para>This is asked here, not in the handler. So a needle that the booth will refuse to move
    /// does not move on the picture either. Otherwise the playhead would jump to the new place during a
    /// changeover, stay there, and then snap back on the next frame.</para>
    /// </summary>
    public bool CanScrub { get; set; } = true;

    /// <summary>Raised when a beat was right-clicked and named the one, with that beat's
    /// time.</summary>
    public event Action<double>? DownbeatPicked;

    /// <summary>The minimum distance on screen between beats before one can be named. Six pixels.
    /// Closer than that, the grab radius covers several beats. The one chosen is then the one the
    /// arithmetic picks, not the one under the pointer.</summary>
    private const double PickBeatPixels = 6;

    public MixLabTimeline()
    {
        if (SKCanvasElement.IsSupportedOnCurrentPlatform())
        {
            _surface = new MixLabSurface { Draw = Render };
            Children.Add(_surface);
        }

        Children.Add(_input);
        _input.PointerPressed += OnPressed;
        _input.PointerMoved += OnMoved;
        _input.PointerReleased += OnReleased;
        _input.PointerCaptureLost += (_, _) => Release();
        _input.PointerExited += (_, _) => { _hover = MixLabHandle.None; Invalidate(); };
        _input.PointerWheelChanged += OnWheel;

        SizeChanged += (_, _) => Invalidate();
        Unloaded += (_, _) =>
        {
            _fill.Dispose();
            _stroke.Dispose();
            _smooth.Dispose();
            _dashed.PathEffect?.Dispose();
            _dashed.Dispose();
            _tiny.Dispose();
            _still?.Dispose();
            _still = null;
            _stillFor = default;
        };
    }

    /// <summary>
    /// The record and everything about it that does not move sixty times a second.
    /// </summary>
    public void Show(MixTrack? track, TransitionPlan? plan, WavePeaks? peaks, double? cueInSeconds)
    {
        // A new RECORD refits the view. A new READING of the same record must not refit it. Every edit
        // arrives as a new instance, and a drag can make a hundred of them. Refitting on every new
        // instance would make the markers impossible to drag, because the zoom would jump on the first
        // pixel.
        bool fresh = !string.Equals(track?.Path, _track?.Path, StringComparison.Ordinal);

        // Both voice readings are worked out here, not in each frame. MixTrack.Voices is a PROPERTY
        // that rebuilds its map on every read. The measured reading sorts the whole vocal curve twice
        // to find the record's two levels. At sixty frames a second across two decks, that is thousands
        // of allocations and two sorts per frame, for a band thirteen pixels tall. They are rebuilt
        // whenever the record OR its reading changes. A hand-edited grid is one such change.
        if (!ReferenceEquals(track, _track))
        {
            // Each one is asked for by name, not taken from MixTrack.Voices. That property now gives
            // the AGREED reading (see VocalMap.For). Three lanes drawn from one answer would show one
            // fact three times. The point of the picture is to show where the readings disagree.
            _told = VocalMap.FromSheet(track?.Sheet);
            _measured = VocalMap.FromBands(track?.Analysis);
            _agreed = VocalMap.Both(_told, _measured);
        }

        _track = track;
        _plan = plan;
        _cueIn = cueInSeconds;

        if (!ReferenceEquals(peaks, _peaks))
        {
            _peaks = peaks;
            _gain = MeasureGain();
        }

        if (fresh)
        {
            // The view fits the part of the record that PLAYS, not the whole file. A file can hold
            // minutes of silence after the music, and the grid runs to both ends of the file on
            // purpose. If the view fitted the file, the music would fill a third of the strip and bar
            // lines would cover the silence. That looks like a record that is still playing.
            _total = Math.Max(WaveTimeline.MinSpanSeconds, track?.AudibleEndSeconds ?? 1);
            _view = WaveTimeline.Fit(_total);
        }

        Invalidate();
    }

    /// <summary>
    /// Sets where the needle is. This has its own entry point, called from the render loop and not from
    /// the panel timer. A playhead that moves only eight times a second looks laggy.
    /// </summary>
    public void SetPlayhead(double seconds)
    {
        if (Math.Abs(seconds - _playhead) < 0.001)
            return;
        _playhead = seconds;

        if (_follow)
            KeepInView();

        Invalidate();
    }

    /// <summary>
    /// Whether the picture follows the needle. When the playhead nears the right edge, the view moves
    /// forward one page, so the playhead comes back in from the left. The view moves by pages, not by a
    /// smooth scroll. A view that slides every frame would redraw the still layer every frame. Bar
    /// lines that never stand still cannot be read against. The first click in the waveform stops the
    /// following, because a click means the user is choosing where to look.
    /// </summary>
    public bool Follow
    {
        get => _follow;
        set
        {
            if (_follow == value)
                return;
            _follow = value;
            FollowChanged?.Invoke(value);
            if (value)
                KeepInView();
            Invalidate();
        }
    }

    /// <summary>Raised when following starts or stops. This includes a click in the waveform that stops
    /// it, so that a toggle button elsewhere can show the right state.</summary>
    public event Action<bool>? FollowChanged;

    private bool _follow;

    /// <summary>Pages the view so the needle sits a little way in from the left edge.</summary>
    private void KeepInView()
    {
        if (_playhead < 0)
            return;

        double lead = _view.Span * 0.15;
        if (_playhead > _view.End - lead || _playhead < _view.Start)
            _view = WaveTimeline.Clamp(new WaveView(_playhead - lead, _view.Span), _total);
    }

    /// <summary>One step of the zoom buttons: in and out around the needle when it is on screen, around
    /// the middle when it is not. The wheel still zooms about the pointer.</summary>
    public void ZoomBy(double factor)
    {
        double centre = _playhead >= _view.Start && _playhead <= _view.End
            ? _playhead
            : _view.Start + _view.Span / 2;
        _view = WaveTimeline.ZoomAt(_view, centre, factor, _total);
        Invalidate();
    }

    public void ZoomIn() => ZoomBy(WaveTimeline.ZoomStep);

    public void ZoomStepOut() => ZoomBy(1 / WaveTimeline.ZoomStep);

    /// <summary>The whole record across the width.</summary>
    public void ZoomOut()
    {
        _view = WaveTimeline.Fit(_total);
        Invalidate();
    }

    /// <summary>Frames the changeover. This is the stretch that people most often open this view to
    /// see. On the deck coming in, the plan is on the other record's clock. So this frames the cue
    /// point instead, which is that deck's part of the same changeover.</summary>
    public void ZoomToTransition()
    {
        if (_plan is { } plan)
        {
            double span = Math.Max(WaveTimeline.MinSpanSeconds, plan.FadeSeconds * 2.5);
            _view = WaveTimeline.Clamp(new WaveView(plan.StartAtSeconds - span / 4, span), _total);
            Invalidate();
            return;
        }

        if (_cueIn is { } cue)
            ZoomAround(cue, 40);
    }

    /// <summary>Frames a stretch around <paramref name="seconds"/>. This is the cue under the
    /// pointer.</summary>
    public void ZoomAround(double seconds, double spanSeconds)
    {
        double span = Math.Max(WaveTimeline.MinSpanSeconds, spanSeconds);
        _view = WaveTimeline.Clamp(new WaveView(seconds - span / 2, span), _total);
        Invalidate();
    }

    private void Invalidate() => _surface?.Invalidate();

    // ── the marks ─────────────────────────────────────────────────────────────

    private double InAt => _track?.Analysis?.IntroEndSeconds ?? 0;

    private double OutAt => _track?.Analysis?.OutroStartSeconds ?? 0;

    private double DownbeatAt =>
        _track?.Analysis is { } a ? GridEdit.FirstDownbeatSeconds(a) : 0;

    private double MixStartAt => _plan?.StartAtSeconds ?? 0;

    private double MixEndAt => _plan is { } p ? p.StartAtSeconds + p.FadeSeconds : 0;

    /// <summary>Whether a given handle is the one currently on offer.</summary>
    private bool Live(MixLabHandle handle) => handle switch
    {
        MixLabHandle.Downbeat => Editing == MixLabEdit.Grid,
        MixLabHandle.MixIn => Editing == MixLabEdit.MixIn,
        MixLabHandle.MixOut => Editing == MixLabEdit.MixOut,
        MixLabHandle.MixStart => Editing == MixLabEdit.Mix && _plan is not null,
        MixLabHandle.MixEnd => Editing == MixLabEdit.Mix && _plan is not null,
        MixLabHandle.CueIn => Editing == MixLabEdit.Mix && _cueIn.HasValue,
        _ => false,
    };

    // ── drawing ───────────────────────────────────────────────────────────────

    private static readonly SKColor Cyan = new(0x5A, 0xC8, 0xFA);
    private static readonly SKColor Amber = new(0xFF, 0x9F, 0x0A);
    private static readonly SKColor Yellow = new(0xF2, 0xC0, 0x4C);
    private static readonly SKColor Green = new(0x4C, 0xD9, 0x8A);

    /// <summary>
    /// The colour that this deck draws the marks of one pointer mode in. Anything outside the picture
    /// that must match it asks here, for example the radio that arms the mode.
    ///
    /// <para>The colour is read from the timeline, not copied into the control beside it. Two copies of
    /// the same list of colours in two files would drift apart.</para>
    /// </summary>
    public static Color InkFor(MixLabEdit mode)
    {
        var ink = mode switch
        {
            MixLabEdit.Grid => Yellow,          // the bar lines and the "1"
            MixLabEdit.MixIn => Cyan,           // where the record starts
            MixLabEdit.MixOut => Amber,         // where it is left
            MixLabEdit.Mix => Green,            // the changeover: its start, its end and the cue
            _ => new SKColor(0xC8, 0xCE, 0xD6), // Playhead moves no mark, so it is no mark's colour
        };

        return Color.FromArgb(0xFF, ink.Red, ink.Green, ink.Blue);
    }

    private void Render(SKCanvas canvas, Size area)
    {
        float w = (float)area.Width, h = (float)area.Height;
        _drawnWidth = Math.Max(1, w);

        canvas.Clear(new SKColor(0x14, 0x16, 0x1A));
        if (_track is null || w < 8 || h < 8 || _view.Span <= 0)
            return;

        float top = RulerHeight + LaneHeight;
        float bottom = Math.Max(top + 8, h - LaneHeight);
        float height = bottom - top;

        // The still layer is replayed unless something it was drawn from has changed. Identity is the
        // test for the reading, the peaks and the plan. An edit always arrives as a new instance.
        var key = new StillKey(
            RuntimeId(_track), RuntimeId(_track.Analysis), RuntimeId(_peaks), RuntimeId(_plan),
            _cueIn ?? -1, _view.Start, _view.Span, w, h, Editing, _gain);
        if (key != _stillFor || _still is null)
        {
            _stillFor = key;
            _still?.Dispose();

            using var recorder = new SKPictureRecorder();
            var page = recorder.BeginRecording(new SKRect(0, 0, w, h));
            DrawWave(page, w, top, height);
            DrawGrid(page, w, top, height);
            DrawPhrases(page, w, top, height);
            DrawTransition(page, top, height);
            DrawDrops(page, top, height);
            DrawVoices(page, w, top);
            DrawRuler(page, w);
            DrawCueNote(page, w, top);
            _still = recorder.EndRecording();
        }

        canvas.DrawPicture(_still);
        DrawMarks(canvas, w, top, bottom);
        DrawPickedBeat(canvas, top, height);
        DrawPlayhead(canvas, top, height);
    }

    private static int RuntimeId(object? o) =>
        o is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);

    /// <summary>The record itself: the decoded waveform where there is one, and the analyser's curves
    /// until it arrives.</summary>
    private void DrawWave(SKCanvas canvas, float w, float top, float height)
    {
        if (_peaks is { Count: > 0 } peaks)
        {
            _fill.Color = new SKColor(0x6F, 0xA8, 0xD8);
            _fill.IsAntialias = false;

            float middle = top + height / 2, half = height / 2;
            for (int x = 0; x < (int)w; x++)
            {
                double from = TimeAt(x), to = TimeAt(x + 1);
                if (!peaks.Range(from, to, out float lo, out float hi))
                    continue;

                float y0 = middle - Math.Clamp(hi * _gain, -1, 1) * half;
                float y1 = middle - Math.Clamp(lo * _gain, -1, 1) * half;
                canvas.DrawRect(x, y0, 1, Math.Max(1, y1 - y0), _fill);
            }

            _fill.IsAntialias = true;
            return;
        }

        if (_track?.Analysis is not { EnergyCurve.Count: > 0 } analysis)
            return;

        _fill.Color = new SKColor(0x2E, 0x6B, 0xA8);
        _fill.IsAntialias = false;
        var bass = analysis.BassCurve;

        for (int x = 0; x < (int)w; x++)
        {
            // Floor: reading s of a curve is the level over [s*step, (s+1)*step).
            int i = (int)Math.Floor(TimeAt(x) / EnergyProfile.StepSeconds);
            if (bass.Count == 0 || i < 0 || i >= bass.Count)
                continue;

            float v = Math.Clamp(bass[i], 0, 1);
            canvas.DrawRect(x, top + height * (1 - v), 1, height * v, _fill);
        }

        _fill.IsAntialias = true;
    }

    /// <summary>Beats, bar lines and four-bar lines, with their count. It also marks any stretch where
    /// the grid is a line that nobody hears.</summary>
    private void DrawGrid(SKCanvas canvas, float w, float top, float height)
    {
        if (_track?.Analysis is not { BeatTimes.Count: > 0 } analysis)
            return;

        // Only where the grid is counted over music that is still playing. Past the end of the music,
        // every record's grid is extrapolated. Marking that red would say "fault" for the normal shape
        // of a fade-out. See TrackAnalysis.IsExtrapolatedOverMusicAt.
        double unheardTo = Math.Min(analysis.AudibleEndSeconds, _view.End);
        if (analysis.BeatLastSeconds > 0 && analysis.BeatLastSeconds < unheardTo)
        {
            _fill.Color = new SKColor(0xFF, 0x6B, 0x6B, 0x28);
            float x = XAt(Math.Max(analysis.BeatLastSeconds, _view.Start));
            canvas.DrawRect(x, top, XAt(unheardTo) - x, height, _fill);
        }

        int perBar = Math.Max(1, analysis.BeatsPerBar);
        int anchor = analysis.FirstWholeBar;
        bool grid = Editing == MixLabEdit.Grid;

        // How far apart the lines land on screen. The picture thins out the lines as the view zooms
        // out, so it does not turn into a solid fence.
        double spacing = analysis.BeatTimes.Count > 1
            ? (analysis.BeatTimes[^1] - analysis.BeatTimes[0]) / (analysis.BeatTimes.Count - 1)
            : 0;
        double pixels = spacing > 0 ? WaveTimeline.TimeToX(_view, spacing, w) - WaveTimeline.TimeToX(_view, 0, w) : 0;

        bool drawBeats = pixels > 4;
        bool drawBars = pixels * perBar > 16;
        bool countBars = pixels * perBar > 34;

        // The grid stops where the music stops. The grid is a straight line carried to both ends of the
        // file. On a file with padding, it would keep naming bar lines in silence for minutes. A bar
        // line in silence cannot be used for timing. See TrackAnalysis.Audible.
        double playsTo = _track?.AudibleEndSeconds ?? double.MaxValue;

        for (int i = 0; i < analysis.BeatTimes.Count; i++)
        {
            double at = analysis.BeatTimes[i];
            if (at < _view.Start) continue;
            if (at > _view.End || at > playsTo) break;

            float x = XAt(at);
            int fromDownbeat = i - analysis.FirstDownbeat;
            bool isBar = fromDownbeat >= 0 && fromDownbeat % perBar == 0;

            if (!isBar)
            {
                if (!drawBeats)
                    continue;
                _stroke.Color = new SKColor(0xFF, 0xFF, 0xFF, grid ? (byte)0x3A : (byte)0x1E);
                _stroke.StrokeWidth = 1;
                canvas.DrawLine(x, top + height * 0.78f, x, top + height, _stroke);
                continue;
            }

            int bar = fromDownbeat / perBar;
            int off = ((bar - anchor) % TrackAnalysis.PhraseBars + TrackAnalysis.PhraseBars)
                      % TrackAnalysis.PhraseBars;

            // The fours are always drawn. They are the ruler that a changeover is read against. The
            // plain bars in between are drawn only when there is room to tell them apart.
            if (off != 0 && !drawBars)
                continue;

            _stroke.Color = off == 0
                ? Yellow.WithAlpha(grid ? (byte)0xFF : (byte)0x88)
                : new SKColor(0xFF, 0xFF, 0xFF, grid ? (byte)0x55 : (byte)0x2A);
            _stroke.StrokeWidth = off == 0 ? 2 : 1;
            canvas.DrawLine(x, top, x, top + height, _stroke);

            // The count itself is drawn on the phrase heads, when there is room. A number such as "17"
            // next to a mark is how an edit is said out loud ("the mix should start at 33") and written
            // down.
            if (off == 0 && countBars)
            {
                _fill.Color = Yellow.WithAlpha(0x77);
                canvas.DrawText((bar - anchor + 1).ToString(), x + 3, top + height - 3, SKTextAlign.Left, _tiny, _fill);
            }
        }
    }

    /// <summary>The measured phrase this record repeats over. See
    /// <see cref="TrackShape.Phrases"/>.</summary>
    private static readonly SKColor Rose = new(0xFF, 0x77, 0xC8);

    /// <summary>
    /// Where the record's own phrases fall: eight, sixteen or thirty-two bars, based on the reading and
    /// not on a constant.
    ///
    /// <para>These are only shown. No changeover is planned against them. A mix lands on one of the
    /// fours, counted from the bar the drums came in on. That is
    /// <see cref="TrackAnalysis.PhraseBars"/>, which is a different anchor. When the two agree, that is
    /// the normal case. When they disagree, that is the interesting case. So both are drawn, and
    /// neither is moved onto the other.</para>
    ///
    /// <para>It has its own loop, not another branch inside <see cref="DrawGrid"/>. The phrase line
    /// does not follow that loop's rules. It is drawn even when the bar it lands on is not drawn as a
    /// bar line. It is also thinned out by its own spacing, not by the spacing of a bar.</para>
    /// </summary>
    private void DrawPhrases(SKCanvas canvas, float w, float top, float height)
    {
        if (_track?.Analysis is not { BeatTimes.Count: > 0, Phrases.IsKnown: true } analysis)
            return;

        var phrases = analysis.Phrases;
        var beats = analysis.BeatTimes;
        int perBar = Math.Max(1, analysis.BeatsPerBar);

        // This draws a phrase. It does not draw the span the record was measured to repeat over. The
        // measurement gives the PHASE, which is where the record was heard to change. The length is
        // TrackAnalysis.PhraseBeats, which is thirty-two beats. A DJ counts a phrase this way. Drawing
        // the repeat would put a line every thirty-two BARS, which is four times too sparse. It would
        // also not be the line that the phrase blend lands on. See
        // TrackAnalysis.MeasuredPhraseAtOrBefore. It uses the same arithmetic, so this line must agree
        // with it.
        int step = analysis.BarsPerPhrase * perBar;
        if (step <= 0)
            return;

        // Thinned by the phrase's own spacing. Thirty-two bars is eight times a four. So when the view
        // is zoomed out far enough that the fours form a fence, the phrase lines still have room.
        double spacing = beats.Count > 1 ? (beats[^1] - beats[0]) / (beats.Count - 1) : 0;
        double pixels = spacing > 0
            ? WaveTimeline.TimeToX(_view, spacing * step, w) - WaveTimeline.TimeToX(_view, 0, w)
            : 0;
        if (pixels < 10)
            return;

        double playsTo = _track?.AudibleEndSeconds ?? double.MaxValue;

        // The phase is counted in bars from FirstDownbeat, the same way TrackShape measured it. See
        // TrackShape.BarLevels, where bar zero is the bar that starts at that beat. This is not
        // FirstWholeBar. That is the anchor of the fours, and the two are only sometimes the same bar.
        // Showing that difference is the purpose of this line.
        //
        // The phase is taken modulo the phrase length, because the measured phase can be a bar count of
        // a longer span. For example, "the record changes every 32 bars, starting at bar 19" gives a
        // phase of 3 in eight-bar phrases.
        int phase = ((phrases.FirstBar % analysis.BarsPerPhrase) + analysis.BarsPerPhrase)
                    % analysis.BarsPerPhrase;
        int first = analysis.FirstDownbeat + phase * perBar;

        _dashed.Color = Rose.WithAlpha(0xCC);
        _dashed.StrokeWidth = 1.5f;

        for (int i = Math.Max(0, first); i < beats.Count; i += step)
        {
            double at = beats[i];
            if (at > _view.End || at > playsTo)
                break;
            if (at < _view.Start)
                continue;

            float x = XAt(at);
            canvas.DrawLine(x, top, x, top + height, _dashed);

            // Which phrase this is, counted from the first phrase the reading found. It is drawn above
            // the yellow bar number, not beside it. The two numbers mean different things for the same
            // line, so the reader must be able to tell which is which without measuring the gap.
            if (pixels > 46)
            {
                _fill.Color = Rose.WithAlpha(0x99);
                canvas.DrawText(
                    $"P{(i - first) / step + 1}", x + 3, top + height - 14, SKTextAlign.Left, _tiny, _fill);
            }
        }
    }

    private void DrawTransition(SKCanvas canvas, float top, float height)
    {
        if (_plan is not { FadeSeconds: > 0 } plan)
            return;

        double start = plan.StartAtSeconds, end = start + plan.FadeSeconds;
        if (end < _view.Start || start > _view.End)
            return;

        _fill.Color = Green.WithAlpha(0x22);
        float x0 = XAt(Math.Max(start, _view.Start)), x1 = XAt(Math.Min(end, _view.End));
        canvas.DrawRect(x0, top, Math.Max(1, x1 - x0), height, _fill);
    }


    /// <summary>
    /// Where somebody is singing on this record, shown twice: what the lyric sheet says on top, and
    /// what the record's own middle band says underneath.
    ///
    /// <para>It is drawn because the rule it feeds would otherwise be invisible. A changeover that
    /// moved back two phrases "because of the voices" would show only as a sentence in the trace, with
    /// nothing on the screen. The first question anyone asks is whether the singing is really
    /// there.</para>
    ///
    /// <para><b>Why three lanes.</b> A lyric sheet is a transcription made by someone else. Its timings
    /// are often a second or two out, and some are out by a whole phrase. That is enough to place a
    /// changeover over the very thing it was moved to avoid. The band is the same record, measured by
    /// <see cref="VocalMap.FromBands"/>. It is thresholded between the record's own two levels. It
    /// counts a lead line as a voice. Neither reading is the truth on its own. So the changeover is
    /// placed by the third lane, which is the stretch that both readings claim. See
    /// <see cref="VocalMap.For"/>.</para>
    ///
    /// <para>The bottom lane is the agreed answer. The two lanes above it show the workings. That is
    /// why the bottom lane is drawn brightest. If a sheet lane sticks out past the agreed lane below
    /// it, the sheet's timestamps have drifted. Seeing that at a glance is the main reason all three
    /// lanes are shown.</para>
    ///
    /// <para>The lanes form a thin band under the ruler. They stay out of the waveform's way. They give
    /// context for the marks. They are not another thing to read the grid against.</para>
    /// </summary>
    private void DrawVoices(SKCanvas canvas, float w, float top)
    {
        if (_track is null)
            return;

        // The reading the planner places the changeover by, and the record's own measurement. These are
        // the same calls the planner makes. Both are taken from the planner, not worked out again here.
        // A picture that thresholds a curve in its own way would show a rule that nobody follows. Both
        // are stored for each record. See Show.
        var told = _told;
        var measured = _measured;
        var agreed = _agreed;

        // How many readings to show. Without a lyric sheet, the first two readings are the same map.
        // The third is that map again, so one fact would be drawn three times. In that case only one
        // band is drawn, as it has always been.
        bool sheet = told.IsKnown && told.Source == VocalMap.Told.Sheet && agreed.IsKnown;
        var lanes = new List<(VocalMap Map, SKColor Ink)>(3);
        if (sheet)
        {
            lanes.Add((told, new SKColor(0xE8, 0xC0, 0x6A, 0xCC)));        // what the sheet says
            lanes.Add((measured, new SKColor(0x6A, 0xC8, 0xE8, 0x99)));    // what the record says

            // This lane shows what both readings agree on. The changeover is placed by this lane. See
            // VocalMap.For. It is drawn brightest for that reason. The other two lanes are the
            // workings. This lane is the answer.
            lanes.Add((agreed, new SKColor(0xF2, 0x8A, 0xB0, 0xFF)));
        }
        else
        {
            lanes.Add((measured, new SKColor(0xE8, 0xC0, 0x6A, 0x55)));    // a guess, and all there is
        }

        float whole = LaneHeight - 5;
        float y = top - LaneHeight + 1;
        float each = (whole - (lanes.Count - 1)) / lanes.Count;
        if (each <= 0)
            return;

        for (var i = 0; i < lanes.Count; i++)
        {
            var (map, ink) = lanes[i];
            if (!map.IsKnown || map.Sung.Count == 0)
                continue;                       // an empty lane, left as a gap: nothing was found

            _fill.Color = ink;
            float lineTop = y + i * (each + 1);

            foreach (var (from, to) in map.Sung)
            {
                if (to < _view.Start || from > _view.End)
                    continue;

                float x = XAt(Math.Max(from, _view.Start));
                float right = XAt(Math.Min(to, _view.End));
                canvas.DrawRect(x, lineTop, Math.Max(1, right - x), each, _fill);
            }
        }
    }

    private void DrawDrops(SKCanvas canvas, float top, float height)
    {
        if (_track?.Analysis is not { DropSeconds.Count: > 0 } analysis)
            return;

        _stroke.Color = new SKColor(0xB0, 0x8A, 0xF2, 0xBB);
        _stroke.StrokeWidth = 2;
        foreach (double at in analysis.DropSeconds)
        {
            if (at < _view.Start || at > _view.End) continue;
            float x = XAt(at);
            canvas.DrawLine(x, top, x, top + height * 0.2f, _stroke);
        }
    }

    /// <summary>One mark waiting to be drawn. Where its tab can go depends on the other marks beside
    /// it. So all the marks are collected before any of them is drawn.</summary>
    private readonly record struct MarkSpec(
        double At, SKColor Color, string Label, MixLabHandle Handle, bool Above);

    /// <summary>Reused for each frame. There are six marks at most. A list built sixty times a second
    /// would create garbage, which the paints above are also built to avoid.</summary>
    private readonly MarkSpec[] _marks = new MarkSpec[6];

    /// <summary>Cues are above and the grid is below. A mark and its tab are always in the same place,
    /// so the hand learns where to go without reading the screen. The changeover's own marks are drawn
    /// above with the cues, in green.</summary>
    private void DrawMarks(SKCanvas canvas, float w, float top, float bottom)
    {
        int n = 0;
        if (Shown(InAt)) _marks[n++] = new(InAt, Cyan, "in", MixLabHandle.MixIn, true);
        if (Shown(OutAt)) _marks[n++] = new(OutAt, Amber, "out", MixLabHandle.MixOut, true);
        if (Shown(DownbeatAt)) _marks[n++] = new(DownbeatAt, Yellow, "1", MixLabHandle.Downbeat, false);

        if (_plan is not null)
        {
            if (Shown(MixStartAt)) _marks[n++] = new(MixStartAt, Green, "mix", MixLabHandle.MixStart, true);
            if (Shown(MixEndAt)) _marks[n++] = new(MixEndAt, Green, "end", MixLabHandle.MixEnd, true);
        }

        if (_cueIn is { } cue && Shown(cue))
            _marks[n++] = new(cue, Green, "cue", MixLabHandle.CueIn, true);

        Lane(canvas, w, top, bottom, n, above: true);
        Lane(canvas, w, top, bottom, n, above: false);
    }

    /// <summary>Whether a mark is worth drawing at all. It must be measured and inside the view. A cue
    /// at zero is one that was never found. It is not a cue at the top of the record. See
    /// <see cref="DrawCueNote"/>, which says so where this method can only leave a gap.</summary>
    private bool Shown(double at) => at > 0 && at >= _view.Start - 1 && at <= _view.End + 1;

    /// <summary>
    /// One lane of tabs, laid out so that two marks close together can both be read.
    ///
    /// <para>The lines stay where they are. Only the tabs move. Going from left to right, each tab sits
    /// under its own line. If the tab before it is already there, the tab is pushed along, and a leader
    /// line is drawn back to the line it names.</para>
    ///
    /// <para>Without this, two marks a second apart would draw one tab on top of the other, and only
    /// the last one drawn could be read. This case is common. On the deck coming in, the cue is worked
    /// out FROM the intro end (see <see cref="TransitionPlanner.MixInPoint"/>). So on any record whose
    /// intro is shorter than the fade, the two marks land within a bar of each other.</para>
    /// </summary>
    private void Lane(SKCanvas canvas, float w, float top, float bottom, int count, bool above)
    {
        float placed = float.NegativeInfinity;

        // The next mark is picked each time, instead of sorting. There are six at most, and a sort for
        // each lane in each frame would allocate. A bitmask is used instead of a "past the last time"
        // check. So two marks on the very same second are still two marks.
        int done = 0;
        while (true)
        {
            int next = -1;
            for (int i = 0; i < count; i++)
                if ((done & (1 << i)) == 0 && _marks[i].Above == above &&
                    (next < 0 || _marks[i].At < _marks[next].At))
                    next = i;

            if (next < 0)
                return;

            done |= 1 << next;

            var mark = _marks[next];
            float width = Math.Max(TabWidth, _tiny.MeasureText(mark.Label) + 4);
            float left = TabLeft(XAt(mark.At), width, placed, w);
            placed = left + width;

            Mark(canvas, w, top, bottom, mark, left, width);
        }
    }

    /// <summary>
    /// Where a tab goes: under its own line, unless the tab to its left is already there.
    /// </summary>
    /// <param name="x">The line the tab names.</param>
    /// <param name="width">How wide this tab is.</param>
    /// <param name="placed">The right edge of the tab before it in the lane, or
    /// <see cref="float.NegativeInfinity"/> for the first.</param>
    /// <param name="w">The width of the picture.</param>
    /// <remarks>
    /// The push is applied after the clamp. So a crowd at the right-hand edge runs off the picture, and
    /// does not pile up inside it. This is deliberate. A tab half off the edge can still be read for
    /// what it is. Two tabs in the same place cannot be read at all. That is the fault this code exists
    /// to fix, so it must not come back at the end of the record.
    /// </remarks>
    internal static float TabLeft(float x, float width, float placed, float w)
    {
        float left = Math.Clamp(x - TabWidth / 2, 0, Math.Max(0, w - width));
        return left < placed + TabGap ? placed + TabGap : left;
    }

    /// <summary>
    /// One mark: a line through the body and, when the set is being edited, a tab to take hold of. The
    /// tab is above the waveform or below it. <see cref="Lane"/> decides where the tab goes.
    /// </summary>
    private void Mark(
        SKCanvas canvas, float w, float top, float bottom, MarkSpec mark, float left, float width)
    {
        bool live = Live(mark.Handle);
        bool hot = _drag == mark.Handle || (_drag == MixLabHandle.None && _hover == mark.Handle);
        float x = XAt(mark.At);

        // Always brighter and thicker than the grid behind it. A thin, dim cue line would be lost among
        // sixty bar lines, which is the same as not drawing it.
        _smooth.Color = live ? mark.Color : mark.Color.WithAlpha(0xBB);
        _smooth.StrokeWidth = hot ? 3 : live ? 2.5f : 2;
        canvas.DrawLine(x, top, x, bottom, _smooth);

        // Every mark keeps its label, whether or not it is the one being held. Being editable changes
        // only the weight of the tab. The tab is filled, with a dark letter, when it responds to the
        // pointer. It is only an outline when it just shows where the mark is.
        float tabTop = mark.Above ? top - LaneHeight : bottom;
        var box = new SKRect(left, tabTop, left + width, tabTop + LaneHeight);

        // When a tab is pushed aside to clear the tab next to it, a thin leader line is drawn back to
        // its mark. The leader runs along the edge of the tab that meets the waveform. A tab that
        // stands away from its own mark must still show which mark it belongs to. Otherwise two marks
        // that could not be read on top of each other would just become two marks that cannot be told
        // apart.
        if (Math.Abs(box.MidX - x) > 1)
        {
            _smooth.StrokeWidth = 1;
            canvas.DrawLine(
                x, mark.Above ? box.Bottom - 0.5f : box.Top + 0.5f,
                box.MidX < x ? box.Right : box.Left, mark.Above ? box.Bottom - 0.5f : box.Top + 0.5f,
                _smooth);
        }

        if (live)
        {
            _fill.Color = hot ? SKColors.White : mark.Color;
            canvas.DrawRoundRect(box, 3, 3, _fill);
            _fill.Color = new SKColor(0x10, 0x12, 0x16);
        }
        else
        {
            _fill.Color = new SKColor(0x14, 0x16, 0x1A, 0xDD);
            canvas.DrawRoundRect(box, 3, 3, _fill);
            _smooth.StrokeWidth = 1;
            canvas.DrawRoundRect(box, 3, 3, _smooth);
            _fill.Color = mark.Color;
        }

        canvas.DrawText(mark.Label, box.MidX, tabTop + LaneHeight - 3.5f, SKTextAlign.Center, _tiny, _fill);

        if (!hot)
            return;

        // While a mark is held, show its exact position. If a user places a mark by eye and reads its
        // time from somewhere else, the mark can end up a frame out.
        string text = MixInputs.Sec(mark.At);
        float readout = _tiny.MeasureText(text);
        float boxX = Math.Clamp(x - readout / 2 - 4, 2, Math.Max(2, w - readout - 10));
        _fill.Color = new SKColor(0, 0, 0, 0xCC);
        canvas.DrawRoundRect(new SKRect(boxX, top + 2, boxX + readout + 8, top + 16), 3, 3, _fill);
        _fill.Color = SKColors.White;
        canvas.DrawText(text, boxX + 4, top + 12.5f, SKTextAlign.Left, _tiny, _fill);
    }

    /// <summary>
    /// Shown as text when the record has no cue points, because nothing else on the picture says so.
    ///
    /// <para>A record whose intro end was never measured is drawn the same as a record whose intro end
    /// is at zero. Neither has a line. But they are different. The first has not been analysed yet. The
    /// second starts on its downbeat. If the marks alone are left to show this, an unread record looks
    /// as if the analyser checked it and found nothing to say. That is the more costly mistake. It
    /// looks like a judgement, but it is really a missing reading.</para>
    /// </summary>
    private void DrawCueNote(SKCanvas canvas, float w, float top)
    {
        string? note = _track?.Analysis switch
        {
            null => "Not analysed",
            { IntroEndSeconds: <= 0, OutroStartSeconds: <= 0 } => "No cue points",
            { IntroEndSeconds: <= 0 } => "No intro end",
            { OutroStartSeconds: <= 0 } => "No outro start",
            _ => null,
        };

        if (note is null)
            return;

        float width = _tiny.MeasureText(note);
        float right = Math.Max(width + 12, w - 4);
        _fill.Color = new SKColor(0x14, 0x16, 0x1A, 0xDD);
        canvas.DrawRoundRect(new SKRect(right - width - 8, top + 2, right, top + 16), 3, 3, _fill);
        _fill.Color = Amber;
        canvas.DrawText(note, right - 4, top + 12.5f, SKTextAlign.Right, _tiny, _fill);
    }

    private void DrawRuler(SKCanvas canvas, float w)
    {
        _fill.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x55);
        _stroke.Color = new SKColor(0xFF, 0xFF, 0xFF, 0x33);
        _stroke.StrokeWidth = 1;

        double step = WaveTimeline.TickSeconds(_view, w);
        if (step <= 0)
            return;

        for (double at = Math.Ceiling(_view.Start / step) * step; at <= _view.End; at += step)
        {
            float x = XAt(at);
            canvas.DrawLine(x, 0, x, 4, _stroke);
            canvas.DrawText(MixInputs.Sec(at), x + 2, 11, SKTextAlign.Left, _tiny, _fill);
        }
    }

    /// <summary>
    /// The beat that a click would name, drawn in the yellow of the bar lines, with the number it would
    /// take. It is drawn live, not in the still layer, because it follows the pointer.
    /// </summary>
    private void DrawPickedBeat(SKCanvas canvas, float top, float height)
    {
        if (_hoverBeat is not { } at || !PickingBeats || at < _view.Start || at > _view.End)
            return;

        float x = XAt(at);
        _smooth.Color = Yellow;
        _smooth.StrokeWidth = 2;
        canvas.DrawLine(x, top, x, top + height, _smooth);

        var box = new SKRect(x - 7, top + height - 15, x + 7, top + height - 1);
        _fill.Color = Yellow;
        canvas.DrawRoundRect(box, 3, 3, _fill);
        _fill.Color = new SKColor(0x10, 0x12, 0x16);
        canvas.DrawText("1", box.MidX, box.Bottom - 3.5f, SKTextAlign.Center, _tiny, _fill);
    }

    private void DrawPlayhead(SKCanvas canvas, float top, float height)
    {
        if (_playhead < _view.Start || _playhead > _view.End)
            return;

        _smooth.Color = SKColors.White;
        _smooth.StrokeWidth = 1.5f;
        float x = XAt(_playhead);
        canvas.DrawLine(x, top - 3, x, top + height + 3, _smooth);
    }

    private float MeasureGain() =>
        _peaks is { Count: > 0 } peaks && peaks.Range(0, peaks.TotalSeconds, out float lo, out float hi)
            ? Math.Max(Math.Abs(lo), Math.Abs(hi)) is > 0.001f and var loudest
                ? Math.Min(8f, 0.98f / loudest)
                : 1
            : 1;

    // ── the pointer ───────────────────────────────────────────────────────────

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_track is null)
            return;

        // A click means the user is choosing where to look. The first click stops the following,
        // whatever else the click does next, such as scrubbing or grabbing a handle. Otherwise the view
        // would move away from the place that was just pointed at.
        Follow = false;

        var point = e.GetCurrentPoint(_input);
        double x = point.Position.X;
        double seconds = TimeAt(x);

        // The right button names the beat under the pointer, and does nothing else. It has its own
        // button, because the left button already has two jobs. It scrubs where there is no handle, and
        // it drags where there is one. A gesture that must be told apart from those two by how far the
        // hand moved would fight the needle. Both readings of the same press are checked.
        // IsRightButtonPressed gives the button state. PointerUpdateKind gives what this event was.
        // Which of the two a platform fills in varies, so both are checked.
        if (point.Properties.IsRightButtonPressed ||
            point.Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed)
        {
            if (PickingBeats && NearestBeat(seconds) is { } named &&
                Math.Abs(x - XAt(named)) <= GrabPixels)
                DownbeatPicked?.Invoke(named);

            e.Handled = true;
            return;
        }

        if (HandleAt(x) is var handle and not MixLabHandle.None)
        {
            _drag = handle;
            _input.CapturePointer(e.Pointer);
            Invalidate();
            e.Handled = true;
            return;
        }

        // Not a handle, so it is a scrub. It is refused where the booth would refuse it. It is not
        // moved on the picture and then put back a frame later.
        if (!CanScrub)
        {
            e.Handled = true;
            return;
        }

        // The needle goes here, and it goes here NOW. The deck takes a moment to reopen at the new
        // position, but the picture must not wait for it. A playhead that arrives a fifth of a second
        // after the click makes the display feel like a readout, not an instrument. The deck's own
        // position takes over again as soon as the deck reports one.
        _playhead = seconds;
        Invalidate();
        Scrubbed?.Invoke(seconds);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_track is null)
            return;

        double x = e.GetCurrentPoint(_input).Position.X;

        if (_drag != MixLabHandle.None)
        {
            Moved?.Invoke(_drag, TimeAt(x));
            Invalidate();
            e.Handled = true;
            return;
        }

        // Which beat the right button would name. The picture shows this before the button is pressed.
        double? beat = PickingBeats ? NearestBeat(TimeAt(x)) : null;
        if (beat is { } at && Math.Abs(x - XAt(at)) > GrabPixels)
            beat = null;
        if (beat != _hoverBeat)
        {
            _hoverBeat = beat;
            Invalidate();
        }

        var over = HandleAt(x);
        if (over == _hover)
            return;

        _hover = over;
        _input.SetCursor(over == MixLabHandle.None
            ? InputSystemCursorShape.Arrow
            : InputSystemCursorShape.SizeWestEast);
        Invalidate();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == MixLabHandle.None)
            return;

        var was = _drag;
        _input.ReleasePointerCapture(e.Pointer);
        Release();
        Dropped?.Invoke(was);
        e.Handled = true;
    }

    private void Release()
    {
        if (_drag == MixLabHandle.None)
            return;
        _drag = MixLabHandle.None;
        Invalidate();
    }

    /// <summary>How far one notch of the wheel moves, as a share of the visible span. A share, not a
    /// number of seconds, so the gesture moves the same proportion when zoomed in on a bar as when
    /// zoomed out on the record.</summary>
    private const double PanPerNotch = 0.15;

    /// <summary>
    /// The wheel moves along the record. Ctrl with the wheel zooms about the pointer.
    ///
    /// <para>The wheel moves along the record because of the other controls beside it. Zoom already has
    /// two buttons, fit and follow. Neither of those can be done with the wheel. Moving along a
    /// five-minute record at bar resolution needs the wheel for moving, so plain wheel movement is the
    /// main gesture. A timeline is also expected to work this way.</para>
    ///
    /// <para>Ctrl is the zoom modifier, not Shift. <see cref="Dialogs.Controls.WaveformEditor"/> uses
    /// Shift for sideways movement. Both controls follow the same rules, so Shift must mean the same
    /// thing in both. Ctrl also arrives on its own from a trackpad pinch.</para>
    /// </summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_track is null)
            return;

        var point = e.GetCurrentPoint(_input);
        int delta = point.Properties.MouseWheelDelta;

        // No delta, so there is nothing to do. An event without a delta is not a wheel turn. Reading
        // "not greater than zero" as "downwards" would make every such event a step out.
        if (delta == 0)
            return;

        _view = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)
            ? WaveTimeline.ZoomAt(
                _view, TimeAt(point.Position.X),
                delta > 0 ? WaveTimeline.ZoomStep : 1 / WaveTimeline.ZoomStep, _total)
            : WaveTimeline.Pan(_view, -delta / 120.0 * _view.Span * PanPerNotch, _total);

        Invalidate();
        e.Handled = true;
    }

    /// <summary>Which handle is under the pointer, and only ever one that is being offered.</summary>
    private MixLabHandle HandleAt(double x)
    {
        if (_track?.Analysis is null)
            return MixLabHandle.None;

        if (Live(MixLabHandle.MixEnd) && Math.Abs(x - XAt(MixEndAt)) <= GrabPixels)
            return MixLabHandle.MixEnd;
        if (Live(MixLabHandle.MixStart) && Math.Abs(x - XAt(MixStartAt)) <= GrabPixels)
            return MixLabHandle.MixStart;
        if (Live(MixLabHandle.CueIn) && _cueIn is { } cue && Math.Abs(x - XAt(cue)) <= GrabPixels)
            return MixLabHandle.CueIn;
        if (Live(MixLabHandle.MixOut) && Math.Abs(x - XAt(OutAt)) <= GrabPixels)
            return MixLabHandle.MixOut;
        if (Live(MixLabHandle.MixIn) && Math.Abs(x - XAt(InAt)) <= GrabPixels)
            return MixLabHandle.MixIn;
        // In Grid mode, EVERY bar line responds, not only the "1" tab at the head of the record. The
        // grid is judged at the changeover, sixty bars from that tab. Every bar line is the same handle
        // on the same ruler. Dragging any of them slides the phase, so the line you grabbed lands where
        // you let go.
        if (Live(MixLabHandle.Downbeat))
        {
            if (Math.Abs(x - XAt(DownbeatAt)) <= GrabPixels)
                return MixLabHandle.Downbeat;
            if (NearestBarLine(TimeAt(x)) is { } bar && Math.Abs(x - XAt(bar)) <= GrabPixels)
                return MixLabHandle.Downbeat;
        }

        return MixLabHandle.None;
    }

    /// <summary>
    /// The bar line nearest <paramref name="seconds"/>. Null when the record has no usable grid.
    ///
    /// <para>This reads the grid array. It does not calculate the answer from the tempo, as <c>first +
    /// n * (60/Bpm) * beatsPerBar</c>. Every line on screen is drawn at <c>BeatTimes[i]</c>. The two
    /// methods agree only while the stored tempo and the stored grid agree. They agree today, but
    /// nothing would notice if they stopped agreeing. The calculation would then give a line that is
    /// right at the top of the record, but further out with every bar after it. The line you can grab
    /// would drift away from the line you can see. Zoomed out, nothing looks wrong. Zoomed in far
    /// enough to place a mark by eye, a handle would miss what it is under. Reading the array cannot
    /// drift from the drawing, because it uses the drawing's own numbers.</para>
    ///
    /// <para>The beat is found by searching the array, not by dividing the record's length by its beat
    /// count. The average spacing is the same calculation one step removed, and it goes wrong in the
    /// same way. <see cref="ModelBeatGrid"/> bends the grid on a record that does not hold one tempo.
    /// An average across a bend is a beat or two out near the ends. That is close enough to land on a
    /// bar line, but it may be the wrong one.</para>
    /// </summary>
    private double? NearestBarLine(double seconds)
    {
        if (_track?.Analysis is not { BeatTimes.Count: > 1 } analysis)
            return null;

        var beats = analysis.BeatTimes;
        int perBar = Math.Max(1, analysis.BeatsPerBar);
        int first = Math.Clamp(analysis.FirstDownbeat, 0, beats.Count - 1);

        // First find the beat under the pointer, then the bar line nearest to it. This keeps the answer
        // a bar line, and keeps it on the grid at both ends of the record.
        int at = NearestBeat(beats, seconds);
        int bar = (int)Math.Round((at - first) / (double)perBar);
        bar = Math.Clamp(bar, -(first / perBar), (beats.Count - 1 - first) / perBar);
        return beats[first + bar * perBar];
    }

    /// <summary>Which beat of the grid is closest to a time. The grid is in order, and that order is
    /// the only thing that can be relied on.</summary>
    private static int NearestBeat(IReadOnlyList<double> beats, double seconds)
    {
        int low = 0, high = beats.Count - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (beats[mid] <= seconds)
                low = mid;
            else
                high = mid - 1;
        }

        return low + 1 < beats.Count && seconds - beats[low] > beats[low + 1] - seconds ? low + 1 : low;
    }

    /// <summary>
    /// Whether a beat can be named right now. This needs three things: the grid is what the pointer is
    /// armed for, there is a grid to name the beat on, and the beats are far enough apart on screen to
    /// point at one.
    ///
    /// <para>This is armed by the pointer mode, not always on. The right click has its own button, and
    /// it names nothing else. But the preview it draws is a yellow line and a yellow tab that follow
    /// the pointer everywhere. That includes looking, scrubbing and dragging a cue. The colour belongs
    /// to a mode that is not selected. The radio buttons show what the pointer is for. The yellow
    /// preview is only for the grid mode.</para>
    ///
    /// <para>This does not depend on the deck standing still. The grid does not move when the record
    /// moves. Only the needle moves. So the beat under the pointer is the same beat either way.</para>
    /// </summary>
    private bool PickingBeats =>
        Editing == MixLabEdit.Grid &&
        _track?.Analysis is { BeatTimes.Count: > 1 } && BeatPixels >= PickBeatPixels;

    /// <summary>How far apart this record's beats fall on screen, in pixels.</summary>
    private double BeatPixels
    {
        get
        {
            if (_track?.Analysis is not { BeatTimes.Count: > 1 } analysis || _view.Span <= 0)
                return 0;

            double spacing =
                (analysis.BeatTimes[^1] - analysis.BeatTimes[0]) / (analysis.BeatTimes.Count - 1);
            return spacing / _view.Span * _drawnWidth;
        }
    }

    /// <summary>The tracked beat nearest <paramref name="seconds"/>. This uses the list of beats
    /// itself, not a straight line through them. The beat being named is one that the user is looking
    /// at.</summary>
    private double? NearestBeat(double seconds)
    {
        if (_track?.Analysis is not { BeatTimes.Count: > 0 } analysis)
            return null;

        double best = double.NaN, bestGap = double.MaxValue;
        foreach (double at in analysis.BeatTimes)
        {
            if (at < _view.Start - 1) continue;
            if (at > _view.End + 1) break;

            double gap = Math.Abs(at - seconds);
            if (gap >= bestGap)
                break;                          // the list is sorted: past the nearest one
            bestGap = gap;
            best = at;
        }

        return double.IsNaN(best) ? null : best;
    }

    private float XAt(double seconds) => (float)WaveTimeline.TimeToX(_view, seconds, _drawnWidth);

    private double TimeAt(double x) =>
        Math.Clamp(WaveTimeline.XToTime(_view, x, _drawnWidth), 0, _total);

}
#endif
#endif

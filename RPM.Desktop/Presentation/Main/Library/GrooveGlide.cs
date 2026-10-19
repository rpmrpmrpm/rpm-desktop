using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// One slide of the album page's groove: the elements that move, and how far.
/// </summary>
/// <param name="Groove">The column being folded away or brought back.</param>
/// <param name="GrooveWidth">How wide the groove is when it is open, in px.</param>
/// <param name="Divider">The hairline at the join. It is not part of the groove's panel. It hangs off the
/// right of the two columns before it, so it has to be told to go with the groove.</param>
/// <param name="FoldKey">The key that folds. It stands on that same hairline, and is a child of the
/// groove's CELL rather than of the groove. The cell does not move during the slide.
///
/// <para>It is not called Key. A record member with that name turns on Uno's implicit IKeyEquatable
/// generation, and that then requires the record to be partial (KE0001).</para></param>
/// <param name="Album">The record's panel, which takes some of the room that the groove gives up.</param>
/// <param name="AlbumGrow">How much wider the panel is with the groove folded, in px. It is never
/// negative. The same figure serves both directions, because the page is laid out at the wider of the two
/// widths whichever way the fold is going.</param>
/// <param name="Papers">The encyclopedias' panel. It is the one thing on the page that a fold really does
/// RESIZE (see AlbumDetailView.CapPanels).</param>
/// <param name="PapersOpen">How wide that panel is with the groove beside it, in px.</param>
/// <param name="PapersFolded">How wide it is with the groove folded away, in px.</param>
/// <param name="WidenPapers">Puts a width on that panel. This is a callback, not a write onto
/// <paramref name="Papers"/>. Widening the outer panel is not enough on its own. What has to re-wrap is
/// inside a ScrollViewer, and only the page knows what to write to reach it (see
/// AlbumDetailView.WidenPapers).</param>
/// <param name="PapersTravel">How far right the panel stands with the groove folded, in px. It is
/// measured from the two layouts, not worked out. It is zero while the record's column is the one held to
/// a width. That zero is a stated value, not an assumed one.</param>
/// <param name="Head">The record's head: the sleeve, the name, the stars and the buttons. It is CENTRED
/// in the album panel. So it is the one thing in that panel which a wider panel moves, rather than merely
/// uncovers.</param>
/// <param name="HeadShift">How far that centre moves. This is half of <paramref name="AlbumGrow"/>. It is
/// never negative.</param>
/// <param name="Similar">The row of suggestions. It spans both panels and grows with them.</param>
/// <param name="SimilarGrow">How much wider the row is with the groove folded, in px. It is never
/// negative.</param>
/// <param name="Folding">True when the groove is going away, and false when it is coming back. It decides
/// which end of the one expression the move starts at, and nothing else.</param>
internal readonly record struct GrooveMove(
    FrameworkElement Groove,
    double GrooveWidth,
    FrameworkElement Divider,
    FrameworkElement FoldKey,
    FrameworkElement Album,
    double AlbumGrow,
    FrameworkElement Papers,
    double PapersOpen,
    double PapersFolded,
    Action<double> WidenPapers,
    double PapersTravel,
    FrameworkElement Head,
    double HeadShift,
    FrameworkElement Similar,
    double SimilarGrow,
    bool Folding);

/// <summary>
/// Where every part of the page stands at one point of a slide: what is carried, and by how much each
/// thing is cut back.
/// </summary>
/// <remarks>
/// <b>Arithmetic only, so it can be checked without watching the page.</b> The move is a few numbers over
/// one <c>p</c>. If one of them has the wrong sign, a hairline floats in the middle of the page, or a
/// paragraph arrives before the column that holds it. The arithmetic is kept apart from the page, so the
/// ends and the direction can be tested. The tests of this type check them.
///
/// <para><b>One p, and the same p as the compositor's.</b> Zero is the OPEN page and one is the folded
/// page, whichever way the fold is going. Folding runs p from 0 to 1, and unfolding runs it from 1 to 0.
/// The WinUI head states the same numbers as expressions over its driver (see <c>GrooveGlide.Dress</c>).
/// This is the number form of them. The two must not drift apart.</para>
/// </remarks>
/// <param name="Groove">How far right the groove stands, and how much of its right edge is cut. The two
/// values are the same, which keeps the groove against the card's edge while it leaves.</param>
/// <param name="Album">How much is cut off the record panel's right edge. It is laid out at its wider
/// width, and uncovered as the groove makes room.</param>
/// <param name="Similar">The same, for the row of suggestions.</param>
/// <param name="Head">How far LEFT the record's head is held. It is centred in a panel laid out at the
/// wider width, so it has to be pulled back until the fold has happened.</param>
/// <param name="Papers">How far right the encyclopedias' panel stands.</param>
/// <param name="PapersWidth">How wide it is. This is the one number here that is a real width, and it
/// causes a layout pass. Everything else is drawn, not measured.</param>
internal readonly record struct GrooveFrame(
    double Groove, double Album, double Similar, double Head, double Papers, double PapersWidth)
{
    /// <summary>The page at <paramref name="p"/>, read off the move it belongs to.</summary>
    public static GrooveFrame At(in GrooveMove move, double p)
        => At(move.GrooveWidth, move.AlbumGrow, move.SimilarGrow, move.HeadShift,
              move.PapersTravel, move.PapersOpen, move.PapersFolded, p);

    /// <summary>The same, stated in numbers alone. This is the form the geometry is tested in. A
    /// <see cref="GrooveMove"/> holds elements from a live page, so it cannot be built at a keyboard.
    /// These numbers are the whole of what the move depends on.</summary>
    public static GrooveFrame At(
        double grooveWidth, double albumGrow, double similarGrow, double headShift,
        double papersTravel, double papersOpen, double papersFolded, double p)
    {
        var through = Math.Clamp(p, 0, 1);
        var left = 1 - through;

        return new GrooveFrame(
            Groove: grooveWidth * through,
            Album: albumGrow * left,
            Similar: similarGrow * left,
            Head: -headShift * left,
            Papers: papersTravel * through,
            PapersWidth: PapersAt(papersOpen, papersFolded, through));
    }

    /// <summary>How wide the encyclopedias' panel is at <paramref name="p"/>. It has its own method,
    /// because the WinUI head writes this value from a second clock (see <c>GrooveGlide.Widen</c>). That
    /// head must write the same number as the rest of the move.</summary>
    public static double PapersAt(double open, double folded, double p)
        => open + (folded - open) * Math.Clamp(p, 0, 1);
}

/// <summary>
/// The album page's groove sliding open and shut. The system compositor runs the slide, not the thread that
/// draws.
///
/// <para><b>The same argument as <see cref="PaneGlide"/>, and the same geometry.</b> Writing
/// <c>ColumnDefinition.Width</c> once a frame invalidates the layout once a frame. On this page, that
/// means a full measure and arrange of a tracklist, two encyclopedia panels and a row of suggestions.
/// That work runs on the thread that then has to draw the result. So nothing here writes a width while
/// the move is in flight. The page is laid out ONCE, at the widest width each column gets during the
/// move. What animates is a clip on each of the three, and a translation on the groove.</para>
///
/// <para><b>How the row stays full.</b> Let <c>p</c> be zero with the groove open, and one with it
/// folded. The caller lays the page out ONCE. The columns are in their open arrangement, and each of the
/// two panels and the row of suggestions is at its WIDER width. A negative right margin makes it overhang
/// the cell beside it. Each one is then cut on its right edge by <c>grow * (1 - p)</c>. That is the whole
/// overhang while the groove is still there, and nothing once it has gone. The groove is translated right
/// by <c>GrooveWidth * p</c>, and cut on its right edge by the same amount. So what is left of it stays
/// pinned against the card's edge, and it is eaten from the left as the panels grow into it. At every
/// value of <c>p</c>, the three widths add up to the row.</para>
///
/// <para><b>A clip cannot resize anything, and one thing here is resized.</b> The papers column is the
/// one panel that a fold widens. Its content is text. A clip that uncovered it would show text wrapped
/// for the far width from the first frame. That is a page that pops and then slides. So that column, and
/// only that column, is laid out once a frame. See <see cref="Widen"/>. This runs on the drawing thread,
/// because a width that re-wraps text is a LAYOUT, and there is no other place to do one. Everything else
/// in the move stays on the compositor. So the one subtree measured each frame is four cards and their
/// paragraphs.</para>
///
/// <para><b>The record's head needs a carry, not a clip.</b> Where the record's column is the one that
/// grows, the head is CENTRED in its panel. A panel 240 points wider draws it 120 points further right.
/// So it is carried from <c>-HeadShift</c> to zero. On today's layout, the record's column is the one
/// held to a width and does not grow. So the shift is zero.</para>
///
/// <para>Nothing else is re-cut for the move. The sleeve asks AlbumDetailView.SleeveRoom, and the
/// suggestion tiles ask SimilarJustifyWidth. So both are the size they are with the groove open, and the
/// row of suggestions shows more of itself.</para>
///
/// <para><b>What it costs.</b> The record's panel is wrapped for its new width in the frame the key is
/// pressed. It is the one panel that really grows. So its tracklist's durations move out to the new right
/// edge, under a clip that has not reached them yet. This is the near end of the move, while everything
/// is sliding. The alternative is to hold the panel still and re-wrap when the slide lands. That puts the
/// same change at the moment the reader is looking, on a page that has stopped.</para>
///
/// <para><b>One expression for both directions.</b> Folding runs <c>p</c> from 0 to 1. Unfolding runs it
/// from 1 to 0. Both use the same expressions and the same layout, because the page is laid out at the
/// widest width each column gets either way. So there is one geometry to get right, not two to keep in
/// step.</para>
///
/// <para><b>The right edge is cut, not the left.</b> The panes' glide clips the edge AWAY from the seam,
/// because its cards anchor their content at the seam. Everything on this page anchors LEFT: the sleeve,
/// the tracklist, and both panels of text. So the right edge is the one that can be cut without a single
/// thing inside shifting. The settled layout at the end is exactly what the clip was already
/// showing.</para>
///
/// <para><b>The Skia head slides too, on the drawing thread.</b> Uno's composition is its own
/// implementation, and it is not what the geometry above is written against. So that head runs the SAME
/// geometry off a <see cref="DispatcherTimer"/>. It uses the same <c>p</c>, the same curve and the same
/// duration. It writes a <c>TranslateTransform</c> and a <c>RectangleGeometry</c> once a frame. Neither
/// of those lays anything out. So a frame costs the drawing of the page, not its measuring.
/// <c>PaneGlide</c> keeps its tick path for the same reason. The caller cannot tell the two apart.</para>
///
/// <para><b>The heads differ in who ticks.</b> On WinUI, the drawing thread can be busy and the slide
/// still runs. On Skia, the slide runs on that same thread, so it cannot. In both cases, a slide that
/// lands late lands at <c>p = 1</c> rather than short. That is because <c>p</c> is read off a wall clock,
/// not counted in frames.</para>
///
/// <para><b>Clip and transform, in that order.</b> Both heads cut the element in its OWN coordinates,
/// then move it. A composition <c>InsetClip</c> is in the visual's local space, and so is
/// <c>UIElement.Clip</c>. <c>RenderTransform</c> carries the clipped result. So the groove is cut at the
/// card's edge while it slides out from under it, rather than shrinking from both ends.</para>
/// </summary>
internal sealed class GrooveGlide
{
    /// <summary>How long the groove takes to slide. The page's view fade is 180 ms, and a pane moves in
    /// 240 ms. This is between them, because a column is moving rather than a picture changing. And it is
    /// the whole page that moves.</summary>
    public const double Ms = 220;

    /// <summary>Whether a slide is in flight.</summary>
    public bool Running { get; private set; }

    /// <summary>What to run when the slide is over. It is cleared before it is called, so a landing
    /// cannot be delivered twice.</summary>
    private Action? _onLanded;

    /// <summary>The one element whose real width is written once a frame, and the two ends of it. See
    /// <see cref="Widen"/> for why this one is a width, not a clip.</summary>
    private FrameworkElement? _widening;
    private Action<double>? _wideApply;
    private double _wideOpen;
    private double _wideFolded;
    private bool _wideFolding;
    private System.Diagnostics.Stopwatch? _wideClock;

#if RPM_WINUI
    /// <summary>Carries <c>p</c>. It is the one animated value. All three clips and the translation are
    /// expressions over it. So no part of one slide can drift out of step with another, however busy the
    /// drawing thread gets.</summary>
    private CompositionPropertySet? _driver;

    private Visual? _groove;
    private readonly List<Visual> _moved = [];
    private readonly List<Visual> _clipped = [];

    /// <summary>
    /// What pumps the one width this class writes by hand.
    /// </summary>
    /// <remarks>
    /// <b>A timer, not <c>CompositionTarget.Rendering</c>.</b> That event is raised by the XAML
    /// framework's own tick. The framework does not tick when it has nothing to do. Every other part of
    /// this move is on the compositor. So for the length of a slide the XAML tree is clean, and the
    /// drawing thread is idle. The handler would fire once, for the frame the first width dirtied, and
    /// never again. Measured on a fold, the papers' width stayed at its old value for the whole slide.
    /// The settled width then arrived in one frame, at the end.
    ///
    /// <para>A DispatcherTimer is pumped by the dispatcher, not by the renderer. So it fires whether or
    /// not anything is being drawn. Fifteen milliseconds is about one frame at sixty. A tick that lands
    /// twice inside one frame writes a width that is nearly the same, and it costs a measure of four
    /// cards.</para>
    ///
    /// <para>A XAML <c>DoubleAnimation</c> with <c>EnableDependentAnimation</c> is the framework's own
    /// answer to this. It was not used, for the reason written in Common/Dissolve.cs. A landed animation
    /// HOLDS the property it animated, and it outranks every later plain write. So the <c>Width = NaN</c>
    /// that gives the column back to its cell would be accepted and then ignored.</para>
    ///
    /// <para>The Skia head has no second clock to keep in step. Everything there is already on a tick. So
    /// the width is written from the same tick as the rest of the move. See <see cref="Slide"/>.</para>
    /// </remarks>
    private DispatcherTimer? _wideTimer;
#else
    /// <summary>The move in flight, or null. This is the whole of this head's state. <c>p</c> is worked
    /// out from the clock on every tick, not stepped. So there is nothing else to keep.</summary>
    private GrooveMove? _move;

    /// <summary>What pumps the slide. A DispatcherTimer is used, not <c>CompositionTarget.Rendering</c>,
    /// for the reason given on <see cref="_wideTimer"/>. There is a second reason for this head. On
    /// Uno-Skia, a handler attached to that event demands a frame. So every finished frame asks for the
    /// next one, for as long as the handler stays attached (see <c>Services.Shell.FrameTick</c>). A timer
    /// asks for a frame only when it has written something.</summary>
    private DispatcherTimer? _tick;

    private System.Diagnostics.Stopwatch? _clock;

    /// <summary>The transforms and clips this head put on the page. <see cref="Stop"/> takes the same
    /// ones off. They are held per element, and are not made per frame. A slide writes each of them many
    /// times. A fresh <c>TranslateTransform</c> every frame would be a property change on the element, as
    /// well as a write on the transform.</summary>
    private readonly List<(FrameworkElement Element, TranslateTransform? Move, RectangleGeometry? Cut)>
        _dressed = [];
#endif

    /// <summary>
    /// Starts the slide.
    /// </summary>
    /// <param name="onLanded">Called on the UI thread when the slide is over, to put the page in its
    /// settled state. It is NOT the only way the move can land. A window that stops producing frames
    /// never runs it. That is what the caller's own deadline is for.</param>
    /// <returns>False where there is nothing to move. The caller then settles at once, with no slide.
    /// Both heads slide. Which thread they slide on is covered in the class note.</returns>
    public bool Start(GrooveMove move, Action onLanded)
    {
        ArgumentNullException.ThrowIfNull(onLanded);

#if RPM_WINUI
        if (move.GrooveWidth <= 1)
            return false;

        try
        {
            Stop();

            var compositor = ElementCompositionPreview.GetElementVisual(move.Groove).Compositor;
            var driver = compositor.CreatePropertySet();
            driver.InsertScalar("p", move.Folding ? 0f : 1f);
            _driver = driver;

            Dress(compositor, driver, move);

            // The app's own move curve, taken from the class that holds it rather than written again
            // here. A much sharper ease-out moves nine tenths of the way in three
            // frames, and then crawls. Nothing stutters, but it looks choppy. What matters is the
            // size of the step from frame to frame, not the frame rate.
            var ease = PaneGlide.Ease(compositor);

            var run = compositor.CreateScalarKeyFrameAnimation();
            run.InsertKeyFrame(1f, move.Folding ? 1f : 0f, ease);
            run.Duration = TimeSpan.FromMilliseconds(Ms);

            // A scoped batch. A composition animation's completion does not come back on the UI thread
            // without one. The page is settled in there.
            _onLanded = onLanded;
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) =>
            {
                var landed = _onLanded;
                _onLanded = null;
                Stop();
                landed?.Invoke();
            };

            driver.StartAnimation("p", run);
            batch.End();

            Running = true;
            return true;
        }
        catch
        {
            // A composition call that will not run leaves the page folding without a slide. Every other
            // head is in that state anyway.
            Stop();
            return false;
        }
#else
        if (move.GrooveWidth <= 1)
            return false;

        Stop();

        _move = move;
        _onLanded = onLanded;

        // The papers' real width is set up here, but it is not given a clock. Slide writes it from this
        // head's own tick, so the width and the geometry cannot be a frame apart.
        Widen(move.Papers, move.WidenPapers, move.PapersOpen, move.PapersFolded, move.Folding);

        // The near end is written before the first frame. Without it, the page is drawn once at the
        // move's layout, which is the OPEN arrangement whichever way the fold goes. On the way back, that
        // is a frame of the finished page before the slide that is meant to produce it.
        Slide(move.Folding ? 0 : 1);

        // Both counters are reset after the near end, not before it. That write is the move's starting
        // position, not a frame of it. Counting it would let a slide that produced nothing else clear the
        // bar that Stop and StopWidening check against.
        _clock = System.Diagnostics.Stopwatch.StartNew();
        _ticks = 0;
        _wideTicks = 0;

        _tick ??= new DispatcherTimer();
        _tick.Stop();
        _tick.Interval = TimeSpan.FromMilliseconds(WideTickMs);
        _tick.Tick -= Slide_Tick;
        _tick.Tick += Slide_Tick;
        _tick.Start();

        Running = true;
        return true;
#endif
    }

#if !RPM_WINUI
    /// <summary>How many frames this slide has actually been given. <see cref="Stop"/> reads it, and says
    /// so when a move that should have animated stepped instead.</summary>
    private int _ticks;

    private void Slide_Tick(object? sender, object e)
    {
        if (_clock is not { } clock)
            return;

        _ticks++;

        var through = Math.Clamp(clock.Elapsed.TotalMilliseconds / Ms, 0, 1);
        var eased = PaneGlide.EaseAt(through);

        // Folding runs p from 0 to 1, and unfolding from 1 to 0, over the ONE geometry described in the
        // class note. This is the same statement that the compositor's key frame makes, read from a wall
        // clock.
        Slide(_move is { Folding: true } ? eased : 1 - eased);

        // The end is read off the clock, not off the frame count. So a slide that the thread was too busy
        // to draw still lands finished, not short. This is the difference between the two heads, and it
        // is the whole of it. On WinUI the animation does not depend on this thread.
        if (through < 1)
            return;

        var landed = _onLanded;
        _onLanded = null;
        Stop();
        landed?.Invoke();
    }

    /// <summary>
    /// Draws the whole move at <paramref name="p"/>: every element cut and carried where the compositor's
    /// expressions would put it, at the same point of the same curve.
    /// </summary>
    /// <remarks>
    /// One method and one number, for the reason the compositor path uses one driver. Several elements
    /// written from separate clocks can drift apart. A groove half a frame ahead of the divider it is
    /// joined to is a hairline floating in the middle of the page.
    ///
    /// <para><b>Nothing here lays anything out.</b> <c>RenderTransform</c> and <c>UIElement.Clip</c> are
    /// both applied after arrange. So a frame is a redraw of the page, not a measure of it. The one
    /// exception is the papers' width. It has to re-wrap, and it is the last line of this method. See
    /// <see cref="Widen"/>.</para>
    /// </remarks>
    private void Slide(double p)
    {
        if (_move is not { } move)
            return;

        var at = GrooveFrame.At(move, p);

        // The groove is pushed right by however much of it has gone. It is cut off by the same amount, so
        // what is left stays against the card's edge instead of running past it.
        Carry(move.Groove, at.Groove);
        CutRight(move.Groove, at.Groove);

        // The join goes with it. The divider hangs off the columns before the groove. The fold key is a
        // child of the groove's CELL. Neither moves during the slide, so both are carried here.
        Carry(move.Divider, at.Groove);
        Carry(move.FoldKey, at.Groove);

        // The record's panel and the row of suggestions are laid out at their wider width, using the
        // caller's negative margins. They are cut back until the groove has made room.
        CutRight(move.Album, at.Album);
        CutRight(move.Similar, at.Similar);

        // The head is centred in the record's panel, so it is laid out at the WIDER panel's centre. It is
        // held back at the narrower centre until the fold has happened.
        Carry(move.Head, at.Head);

        // The papers are laid out where the open page puts them, and carried to where the folded page
        // does. They are also the one thing here that is re-measured.
        Carry(move.Papers, at.Papers);
        WidenTo(at.PapersWidth);
    }

    /// <summary>Draws <paramref name="element"/> <paramref name="x"/> points right of where it was laid
    /// out. Zero is written rather than skipped. An element that was carried on the last frame, and is
    /// not on this one, must be put back.</summary>
    private void Carry(FrameworkElement element, double x)
    {
        var slot = Dress(element);

        if (slot.Move is not { } move)
        {
            move = new TranslateTransform();
            element.RenderTransform = move;
            Replace(element, move, slot.Cut);
        }

        move.X = x;
    }

    /// <summary>Cuts <paramref name="inset"/> points off <paramref name="element"/>'s right edge. The cut
    /// is in the element's own coordinates, so it happens before whatever <see cref="Carry"/> has done to
    /// the element.</summary>
    /// <remarks>
    /// The height is read again on every frame, not taken once. The groove's own content does not change
    /// height during a slide. The papers' content does change, because that column is being re-wrapped
    /// underneath. A clip cut against a stale height would leave a paragraph with its last line missing.
    /// </remarks>
    private void CutRight(FrameworkElement element, double inset)
    {
        var slot = Dress(element);

        if (slot.Cut is not { } cut)
        {
            cut = new RectangleGeometry();
            element.Clip = cut;
            Replace(element, slot.Move, cut);
        }

        cut.Rect = new Rect(0, 0, Math.Max(0, element.ActualWidth - inset), element.ActualHeight);
    }

    private (TranslateTransform? Move, RectangleGeometry? Cut) Dress(FrameworkElement element)
    {
        foreach (var (candidate, move, cut) in _dressed)
        {
            if (ReferenceEquals(candidate, element))
                return (move, cut);
        }

        _dressed.Add((element, null, null));
        return (null, null);
    }

    private void Replace(FrameworkElement element, TranslateTransform? move, RectangleGeometry? cut)
    {
        for (var i = 0; i < _dressed.Count; i++)
        {
            if (!ReferenceEquals(_dressed[i].Element, element))
                continue;

            _dressed[i] = (element, move, cut);
            return;
        }
    }
#endif

    /// <summary>
    /// Takes everything this put on the page back off, leaving every element in its own layout slot. It
    /// is safe to call when nothing is in flight, and it is called before every start.
    /// </summary>
    /// <remarks>
    /// The animations are stopped before the properties are cleared. Reading or writing an animated
    /// property answers with the last value SET, not the live one. So a clear that came first would be
    /// accepted, and then covered by the animation that is still holding the property.
    /// </remarks>
    public void Stop()
    {
        Running = false;
        _onLanded = null;

#if RPM_WINUI
        StopWidening();

        if (_groove is { } groove)
        {
            groove.StopAnimation("Translation");
            groove.Properties.InsertVector3("Translation", Vector3.Zero);
            groove.Clip = null;
            _groove = null;
        }

        foreach (var moved in _moved)
        {
            moved.StopAnimation("Translation");
            moved.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        _moved.Clear();

        foreach (var visual in _clipped)
            visual.Clip = null;
        _clipped.Clear();

        if (_driver is { } driver)
        {
            driver.StopAnimation("p");
            _driver = null;
        }
#else
        if (_tick is { } tick)
        {
            tick.Stop();
            tick.Tick -= Slide_Tick;
        }

        // A move that was given a handful of frames did not animate. It stepped. This is said because the
        // fault is invisible in the code and obvious on screen. On this head it is the thread's own load
        // that causes it. It is the same line that StopWidening writes for the width.
        if (_move is not null && _ticks < 5)
        {
            RPM.Core.Diagnostics.Scope.Said(
                "panes",
                $"groove slide ran {_ticks} frames in " +
                $"{_clock?.Elapsed.TotalMilliseconds ?? 0:F0}ms, WHICH IS NOT AN ANIMATION");
        }

        _clock = null;
        _move = null;

        // The transforms and the clips come off. The papers' WIDTH does not, for the reason StopWidening
        // gives. They are cleared rather than left at their landed values, because the settled page is
        // the layout itself. An element that still carries a translation is one the next layout pass
        // cannot put right.
        foreach (var (element, move, cut) in _dressed)
        {
            if (move is not null)
                element.RenderTransform = null;
            if (cut is not null)
                element.Clip = null;
        }

        _dressed.Clear();
        StopWidening();
#endif
    }

#if RPM_WINUI
    private void Dress(Compositor compositor, CompositionPropertySet driver, GrooveMove move)
    {
        // Before the visual is taken, as Spin.Wobble does it. Without this the "Translation" property is
        // not on the visual. Animating it then does nothing, and nothing says why.
        ElementCompositionPreview.SetIsTranslationEnabled(move.Groove, true);

        var groove = ElementCompositionPreview.GetElementVisual(move.Groove);
        _groove = groove;

        // The groove is pushed right by however much of it has gone, and cut at the card's edge by the
        // same amount. So what is left stays against that edge.
        var slide = compositor.CreateExpressionAnimation(
            $"Vector3({Fx(move.GrooveWidth)} * d.p, 0, 0)");
        slide.SetReferenceParameter("d", driver);
        groove.StartAnimation("Translation", slide);
        ClipRight(compositor, driver, groove, $"{Fx(move.GrooveWidth)} * d.p");

        // The join goes with it. The divider hangs off the right of the two columns before the groove.
        // The key that folds is a child of the groove's CELL. Neither the columns nor the cell move
        // during the slide. So without this, both would stay at the old join while the groove left. That
        // leaves a line and a key floating in the middle of the page. They ride on the groove's own
        // expression, which anchors them to its left edge. The scroller cuts them off at the card's edge
        // the same way it cuts the groove. The settle takes both down.
        Carry(compositor, driver, move.Divider, 0, move.GrooveWidth);
        Carry(compositor, driver, move.FoldKey, 0, move.GrooveWidth);

        // The record's panel and the row of suggestions under it are laid out at their wider width, using
        // the caller's negative margins. They are clipped back until the groove has made room.
        Uncover(compositor, driver, move.Album, move.AlbumGrow);
        Uncover(compositor, driver, move.Similar, move.SimilarGrow);

        // The papers are measured, not uncovered.
        Widen(move.Papers, move.WidenPapers, move.PapersOpen, move.PapersFolded, move.Folding);

        // The head is centred in the record's panel. It is laid out at the wider panel's centre, and held
        // back at the narrower one until the fold has happened. The papers are laid out where the open
        // page puts them, and carried to where the folded page does.
        Carry(compositor, driver, move.Head, -move.HeadShift, 0);
        Carry(compositor, driver, move.Papers, 0, move.PapersTravel);
    }

    /// <summary>
    /// Draws an element <paramref name="atOpen"/> points from where it was laid out while the groove is
    /// open, and <paramref name="atFolded"/> points from it once the fold has finished. It slides between
    /// the two on the driver.
    /// </summary>
    /// <remarks>
    /// Two offsets rather than one distance, because the two things carried here start at opposite ends.
    /// The head is laid out at the WIDER panel's centre. It has to be held back at the narrower one while
    /// the groove is still there, so it runs from <c>-shift</c> to zero. The papers are laid out where
    /// the open page puts them. They have to be pushed to where the folded page does, so they run from
    /// zero to <c>travel</c>. One expression covers both. Both directions of the fold read it. Unfolding
    /// simply runs <c>p</c> the other way.
    /// </remarks>
    private void Carry(
        Compositor compositor, CompositionPropertySet driver, FrameworkElement element,
        double atOpen, double atFolded)
    {
        if (Math.Abs(atOpen) <= 0.5 && Math.Abs(atFolded) <= 0.5)
            return;

        // Before the visual is taken, as the groove's own translation needs it.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        var visual = ElementCompositionPreview.GetElementVisual(element);
        var slide = compositor.CreateExpressionAnimation(
            $"Vector3({Fx(atOpen)} + {Fx(atFolded - atOpen)} * d.p, 0, 0)");
        slide.SetReferenceParameter("d", driver);
        visual.StartAnimation("Translation", slide);
        _moved.Add(visual);
    }

    private void Uncover(
        Compositor compositor, CompositionPropertySet driver, FrameworkElement element, double by)
    {
        if (by <= 0.5)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        ClipRight(compositor, driver, visual, $"{Fx(by)} * (1.0 - d.p)");
        _clipped.Add(visual);
    }

#endif

    /// <summary>
    /// Writes a real width on one element once a frame. So what is in it is laid out at every size
    /// between the two, not at the far one from the start.
    /// </summary>
    /// <remarks>
    /// <b>This is the one part of the move on the drawing thread, and it has to be.</b> The papers column
    /// holds paragraphs. A paragraph that is 450 points wider breaks its lines in different places. A
    /// clip can only uncover the far layout. So the text and the cards would arrive at their new size in
    /// the frame the key goes down, and then slide out from under the clip. The text would pop to a fixed
    /// size instead of moving smoothly.
    ///
    /// <para>What keeps it cheap is that it is the ONLY thing being laid out. The caller holds the
    /// record's column to one width, and gives the row of suggestions an overhang, so neither is dirty.
    /// The element written here gets an explicit width and is left-aligned. So it overhangs its own cell,
    /// rather than moving the grid's columns. The measure each frame is four cards and their paragraphs,
    /// not the page.</para>
    ///
    /// <para><b>Which clock writes it differs between the two heads.</b> On WinUI it is this class's own
    /// timer, because the compositor's <c>p</c> cannot be read from here. The timer runs the same curve
    /// over the same duration (see PaneGlide.EaseAt, which is the number form of the bezier the rest of
    /// the move is given). The groove is drawn OVER this column while it slides, so any small difference
    /// between the two clocks is hidden behind the groove. On Skia the whole move is already on a tick.
    /// So the width is written from that tick, and there is no difference to have.</para>
    /// </remarks>
    private void Widen(
        FrameworkElement element, Action<double> apply, double open, double folded, bool folding)
    {
        if (Math.Abs(folded - open) <= 0.5)
            return;

        _widening = element;
        _wideApply = apply;
        _wideOpen = open;
        _wideFolded = folded;
        _wideFolding = folding;
        _wideClock = System.Diagnostics.Stopwatch.StartNew();
        _wideTicks = 0;

        // The near end is written before the first frame. The element starts at the width that the page
        // it is leaving gives it, whichever direction this is.
        apply(folding ? open : folded);

#if RPM_WINUI
        _wideTimer ??= new DispatcherTimer();
        _wideTimer.Stop();
        _wideTimer.Interval = TimeSpan.FromMilliseconds(WideTickMs);
        _wideTimer.Tick -= Widen_Tick;
        _wideTimer.Tick += Widen_Tick;
        _wideTimer.Start();
#endif
        // On the Skia head there is no second clock. Slide hands the width over with the rest of the
        // frame. So the column cannot reach its settled width while the groove is still sliding, and open
        // a hole between them.
    }

    /// <summary>About one frame at sixty. See <see cref="_wideTimer"/>.</summary>
    private const double WideTickMs = 15;

    private int _wideTicks;

#if RPM_WINUI
    private void Widen_Tick(object? sender, object e) => WidenTick();

    /// <summary>The width, on this class's own clock, because the compositor's <c>p</c> cannot be read
    /// from here. It uses the same curve and the same duration. The groove is drawn OVER this column
    /// while it slides, so any small difference between the two is hidden behind the groove.</summary>
    private void WidenTick()
    {
        if (_wideClock is not { } clock)
            return;

        var through = Math.Clamp(clock.Elapsed.TotalMilliseconds / Ms, 0, 1);
        var eased = PaneGlide.EaseAt(through);

        WidenTo(GrooveFrame.PapersAt(_wideOpen, _wideFolded, _wideFolding ? eased : 1 - eased));

        if (through >= 1)
            StopWidening();
    }
#endif

    /// <summary>Writes the papers' width. This is the one place it is written on either head. So the
    /// column cannot be given one number by the tick and a different one by the landing.</summary>
    private void WidenTo(double width)
    {
        if (_wideApply is not { } apply)
            return;

        _wideTicks++;
        apply(width);
    }

    /// <summary>Takes the tick off. The width itself is left standing. The caller clears it when it
    /// settles the page. Clearing it here would draw one frame of the open layout before the settled one
    /// arrives.</summary>
    private void StopWidening()
    {
#if RPM_WINUI
        if (_wideTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= Widen_Tick;
        }
#endif

        // A move that wrote only a handful of widths did not animate. It stepped. This is said because
        // the fault is invisible in the code and obvious on screen. The column reaches its settled width
        // while the groove is still sliding, and a hole opens between them. This has happened twice: once
        // when the pump was CompositionTarget.Rendering, and once when the width was written somewhere
        // the framework would not re-measure from.
        if (_widening is not null && _wideTicks < 5)
        {
            RPM.Core.Diagnostics.Scope.Said(
                "panes",
                $"groove widen ran {_wideTicks} ticks in " +
                $"{_wideClock?.Elapsed.TotalMilliseconds ?? 0:F0}ms, WHICH IS NOT AN ANIMATION");
        }

        _wideClock = null;
        _wideApply = null;
        _widening = null;
    }

#if RPM_WINUI
    /// <summary>
    /// Cuts a visual's right edge back, by an expression over the driver.
    /// </summary>
    /// <remarks>
    /// An <c>InsetClip</c> is used, not the rounded geometry that <see cref="PaneGlide"/> uses. Nothing
    /// cut here has a corner on the edge being cut. The page's own rounded corner belongs to
    /// <c>PageCorner</c>. That spans all three columns, and none of this touches it.
    /// </remarks>
    private static void ClipRight(
        Compositor compositor, CompositionPropertySet driver, Visual visual, string inset)
    {
        var clip = compositor.CreateInsetClip();
        var animation = compositor.CreateExpressionAnimation(inset);
        animation.SetReferenceParameter("d", driver);
        clip.StartAnimation("RightInset", animation);
        visual.Clip = clip;
    }

    /// <summary>A number as the compositor's expression language wants to read it: always a decimal
    /// point, never the machine's own comma.</summary>
    private static string Fx(double value)
        => ((float)value).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
#endif
}

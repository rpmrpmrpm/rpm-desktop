using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The column whose width changes during a pane move, and the two things beside it that move with
/// it.
/// </summary>
/// <param name="Mover">The card in the column whose width changes. It is laid out at the WIDER of the
/// move's two ends for the whole move. The glide clips it to the width it should show.</param>
/// <param name="Other">The card that gives way. It is laid out <c>|From - Target|</c> px wider than its
/// column, so it overhangs the far edge of the row. The glide clips the overhang off.</param>
/// <param name="Seam">The splitter between the two. It has no width of its own to change, so it only
/// slides.</param>
/// <param name="Inner">The view inside <paramref name="Other"/> that stands BESIDE the frozen card, or
/// null. The move is really resizing this view. It is cut back to the frozen card's near edge, so it cannot
/// pass underneath that card while the two are briefly out of step.</param>
/// <param name="Fold">True when the moving column is the RIGHT one (the library folding away or coming
/// back). False when it is the left one (the pane opening or closing). It decides which edge of each card
/// is clipped and which way the seam slides.</param>
/// <param name="From">The moving column's width now, in px.</param>
/// <param name="Target">The moving column's width when the move is over, in px.</param>
internal readonly record struct PaneMove(
    FrameworkElement Mover,
    FrameworkElement Other,
    FrameworkElement Seam,
    FrameworkElement? Inner,
    bool Fold,
    double From,
    double Target);

/// <summary>One pane's content crossfade: what fades, and the opacity it starts from.</summary>
internal readonly record struct PaneFade(FrameworkElement Views, double From);

/// <summary>The gesture's boundaries, in milliseconds from its start: where the fade-out ends, where the
/// width move ends, and where the fade-in ends. A phase that is not needed has zero length. Its boundary
/// then sits on the one before it.
/// </summary>
internal readonly record struct PanePhases(double OutEnds, double MoveEnds, double InEnds);

/// <summary>A pane move, run by the system compositor rather than by the thread that draws.
///
/// <para><b>Why this exists.</b> Setting <c>ColumnDefinition.Width</c> once per frame, from a
/// <c>CompositionTarget.Rendering</c> handler, invalidates layout on every write. So each frame is a full
/// measure and arrange of both columns and everything in them, on the thread that then had to draw the
/// result. A frame where that work ran over was a dropped frame, which users see as a stutter. A cheaper
/// tick cannot fix this. The cost comes from doing the work on that thread at all. See <see
/// cref="Common.Spin"/>, which does the same move on the now-playing record.</para>
///
/// <para><b>What this does instead.</b> It lays out both columns ONCE, at the widest each of them gets
/// during the move. Then it animates only properties the compositor owns: a clip on each card, and a
/// translation on the seam alone. Layout runs twice for a whole move, once here and once at
/// <c>PaneConductor.Settle</c>, instead of once per frame. Nothing runs on the drawing thread while the
/// move is in flight.</para>
///
/// <para><b>The geometry.</b> Let <c>W</c> be the moving column's apparent width, easing from <c>From</c>
/// to <c>Target</c>. Let <c>max</c> and <c>min</c> be the wider and the narrower of those two values. The
/// mover's column is laid out at <c>max</c>. The other card is laid out <c>max - min</c> px wider than its
/// own column, overhanging ON THE SEAM SIDE. So at every point in the move, both cards have at least as
/// many pixels as they need to show. Then:</para>
/// <list type="bullet">
/// <item>the mover is clipped by <c>max - W</c>, so it reads as <c>W</c> wide;</item>
/// <item>the other is clipped at the SEAM by <c>W - min</c>, which is exactly its overhang;</item>
/// <item>the seam, and only the seam, slides by <c>W - max</c>, negated when folding.</item>
/// </list>
/// <para>The two clips always add up to <c>max - min</c>, so the row is full at every value of
/// <c>W</c>.</para>
///
/// <para><b>Neither card is translated, and that is the point.</b> A card that is never translated cannot
/// move, so nothing at its far edge can move either. Both rails stay still, because their cards are only
/// ever clipped. Translating the card that gives way, and holding its rail still with an equal and opposite
/// translation, does not work. The arithmetic is right, but the edge still shimmers. Two floating-point
/// translations that sum to a constant do not rasterise to the same pixel every frame. Cancelling a motion
/// is not the same as not having one.</para>
///
/// <para><b>The Skia head keeps the per-frame tween</b>, on purpose. Uno's composition is its own
/// implementation on its own renderer. The redirect, the fold and the resting state of the panes all have
/// to stay correct on the head that still ships. <see cref="Start"/> returns false there, and the caller
/// keeps the tick it always had.</para>
/// </summary>
internal sealed class PaneGlide
{
    /// <summary>Whether a move is in flight on the compositor.</summary>
    public bool Running { get; private set; }

#if RPM_WINUI
    /// <summary>Carries <c>W</c>, the moving column's apparent width. It is the one animated value. Every
    /// clip and every slide is an expression over it, so the parts of one move cannot drift out of step
    /// with each other, however busy the drawing thread is.</summary>
    private CompositionPropertySet? _driver;

    private CompositionScopedBatch? _batch;
    private Action? _onMoved;

    private Visual? _mover;
    private Visual? _other;
    private Visual? _seam;

    /// <summary>The view whose inset is written once per frame, and the numbers that decide it. See <see
    /// cref="Widen"/> for why this one is not an expression.</summary>
    private FrameworkElement? _widening;
    private FrameworkElement? _wideSeam;
    private FrameworkElement? _wideRoom;
    private bool _wideFold;
    private double _wideMin;
    private double _wideSlack;
    private double _widePrev;
    private double _wideEnds;
    private System.Diagnostics.Stopwatch? _wideClock;
    private DispatcherTimer? _wideTimer;
    private CompositionRoundedRectangleGeometry? _moverShape;
    private CompositionRoundedRectangleGeometry? _otherShape;

    /// <summary>Whether the gesture in flight has a width move at all. A gesture that only swaps content
    /// still runs a driver, because the driver is also the clock the swap is called back on. Its <c>W</c>
    /// is a placeholder, so it must not be read back by <see cref="Stop"/>.</summary>
    private bool _hasMove;
#endif

    /// <summary>Starts the whole gesture: the fades, the width move, and the callback that finishes it. All
    /// of them begin in one turn and are timed off one clock.
    /// </summary>
    /// <param name="move">The width move, or null when this gesture only swaps content.</param>
    /// <param name="left">The left pane's crossfade, or null when its content is not changing.</param>
    /// <param name="right">The library's crossfade, or null when its content is not changing.</param>
    /// <param name="phases">Where the three phases end.</param>
    /// <param name="onMoved">Called on the UI thread when the width move is over and the content swap is
    /// due. At that point the content is behind an opacity of zero. This is NOT the only way a move can
    /// land. A window that stops producing frames never runs it, which is what the caller's own deadline is
    /// for.</param>
    /// <returns>False where the platform will not run this off the drawing thread. The caller then keeps
    /// its per-frame tween.</returns>
    public bool Start(PaneMove? move, PaneFade? left, PaneFade? right, PanePhases phases, Action onMoved)
    {
        ArgumentNullException.ThrowIfNull(onMoved);

#if RPM_WINUI
        var host = move?.Mover ?? left?.Views ?? right?.Views;
        if (host is null)
            return false;

        try
        {
            Rest();   // clears whatever an earlier gesture left on. The caller has already read what it
                      // needed.

            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;

            var driver = compositor.CreatePropertySet();
            driver.InsertScalar("W", (float)(move?.From ?? 0));

            // How far past its column the other card is laid out. Read by Freeze and by nothing else.
            driver.InsertScalar("over", (float)Math.Abs((move?.Target ?? 0) - (move?.From ?? 0)));
            _driver = driver;
            _hasMove = move is not null;

            // The clips and slides are set up first, as expressions over a driver that already holds From.
            // The cards are dressed for the start of the move before the layout written after this call is
            // arranged. Expression animations never end, so they must stay out of the batch below. The
            // batch completes only when everything in it has finished.
            if (move is { } dressed)
                Dress(compositor, driver, dressed, phases);

            // The batch holds only the width's own ramp, so its completion is the signal that the move is
            // over. The ramp ends at MoveEnds, which is when the content swap is due.
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _batch = batch;
            driver.StartAnimation("W", Ramp(compositor, move, phases));
            batch.End();
            _onMoved = onMoved;
            batch.Completed += OnBatchCompleted;
            SayCommitDelay(compositor);

            // The fades run past MoveEnds, so they are started outside the batch. Each pane gets one
            // animation that covers the fade-out, the hold and the fade-in. Three chained animations would
            // have a seam between each pair, and a seam is a frame where the opacity can jump.
            if (left is { } fadeLeft)
                Crossfade(compositor, fadeLeft, phases);
            if (right is { } fadeRight)
                Crossfade(compositor, fadeRight, phases);

            Running = true;
            return true;
        }
        catch
        {
            // If the compositor refuses any of this, the move goes back to the caller's tween. The panes
            // never stay still.
            Rest();
            return false;
        }
#else
        _ = move;
        _ = left;
        _ = right;
        _ = phases;
        return false;
#endif
    }

    /// <summary>Stops a move in flight, and reports how wide the moving column reads right now. A redirect
    /// can then start from that width.
    /// </summary>
    /// <returns>The apparent width in px, or null when no move was running. The column cannot give this
    /// value itself, because it is laid out at the wider of the move's two ends for the whole
    /// move.</returns>
    public double? Stop()
    {
#if RPM_WINUI
        if (_driver is not { } driver || !_hasMove)
        {
            Rest();
            return null;
        }

        // STOP FIRST, THEN READ. The order decides whether this works. A read of an animated composition
        // property returns the last value that was SET, not the live one. While the animation runs, that
        // value is where the move began. Stopping hands back the live value. If the read came first, every
        // redirect would start from the width the interrupted move began at, and the panes would jump
        // backwards to meet it.
        driver.StopAnimation("W");
        double? width = driver.TryGetScalar("W", out var w) == CompositionGetValueStatus.Succeeded
            ? w
            : null;

        Rest();
        return width;
#else
        return null;
#endif
    }

    /// <summary>Takes every animation off, and puts the clips and slides back to nothing. The cards stay
    /// where layout wants them. Safe to call when nothing is running.
    ///
    /// <para>It does NOT touch the panes' opacity, and must not. The width move lands while the fade-in is
    /// still running. The caller settles the layout at that landing. The fades are stopped where they
    /// actually end, by whoever writes the next opacity: <see cref="OpacityOf"/> for a redirect, <see
    /// cref="SetOpacity"/> for a stop.</para>
    ///
    /// <para>The layout this move ran against (the mover's column at the wider end, and the other card's
    /// overhanging margin) is the caller's to undo, in the same turn as this call. See
    /// <c>PaneConductor.Settle</c>.</para>
    /// </summary>
    public void Rest()
    {
        Running = false;

#if RPM_WINUI
        try
        {
            if (_batch is { } batch)
            {
                batch.Completed -= OnBatchCompleted;
                _batch = null;
            }
            _onMoved = null;
            StopWidening();

            _driver?.StopAnimation("W");

            _moverShape?.StopAnimation("Size");
            _moverShape?.StopAnimation("Offset");
            _otherShape?.StopAnimation("Size");
            _otherShape?.StopAnimation("Offset");

            if (_mover is { } mover)
                mover.Clip = null;

            // The card that gives way is only clipped and never translated, so there is no translation to
            // reset. Calling StopAnimation on a property the visual does not have throws an exception. So
            // Steady must not be called on it here.
            if (_other is { } other)
                other.Clip = null;

            // The seam is the one thing with a Translation on it, so it is the one thing to put back.
            if (_seam is { } seam)
                Steady(seam);
        }
        catch
        {
            // A visual whose element has left the tree cannot be undressed, and does not need to be.
        }
        finally
        {
            // Cleared in a finally block, so an exception above cannot leave stale state behind. If the
            // seam were not reset, the splitter would stay displaced by the move's whole distance until the
            // next move. A cleanup that can throw part-way through has to run in a finally.
            _driver = null;
            _hasMove = false;
            _moverShape = null;
            _otherShape = null;
            _mover = null;
            _other = null;
            _seam = null;
        }
#endif
    }

    /// <summary>Writes a pane's content opacity now, with no animation.
    /// </summary>
    /// <remarks>On the WinUI head the compositor owns this value. A fade is a composition animation on the
    /// visual's own Opacity. <c>UIElement.Opacity</c> is left at 1, so XAML never pushes a stale value back
    /// over the animated one.
    /// </remarks>
    public static void SetOpacity(FrameworkElement views, double opacity)
    {
        ArgumentNullException.ThrowIfNull(views);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(views);
            visual.StopAnimation("Opacity");
            visual.Opacity = (float)opacity;
        }
        catch
        {
            views.Opacity = opacity;
        }
#else
        views.Opacity = opacity;
#endif
    }

    /// <summary>What a pane's content opacity actually is right now. A redirected fade has to start from
    /// this value.
    /// </summary>
    /// <remarks>Reading STOPS the fade, for the reason given on <see cref="Stop"/>. An animated composition
    /// property answers a read with the last value that was set, not the live one. Every caller is about to
    /// write the opacity again, either as a fresh fade or as a flat 1.
    /// </remarks>
    public static double OpacityOf(FrameworkElement views)
    {
        ArgumentNullException.ThrowIfNull(views);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(views);
            visual.StopAnimation("Opacity");
            return visual.Opacity;
        }
        catch
        {
            return 1;
        }
#else
        return views.Opacity;
#endif
    }

    /// <summary>The app's move curve as a number. For a move, it gives the share of the distance covered
    /// when a share <paramref name="t"/> of the time has passed.
    /// </summary>
    /// <remarks>This is the same curve as <see cref="Ease"/>, in the other of its two forms. The compositor
    /// is given control points and works the curve out itself. Anything that has to move a LAYOUT rather
    /// than a visual must work the curve out on this thread. The album page's papers column does this (see
    /// GrooveGlide.Widen). A column whose text re-wraps as it grows cannot be a clip.
    ///
    /// <para>Cubic ease-out both ways. 1 - (1 - t) cubed is exactly the curve drawn by the bezier (0.215,
    /// 0.61) (0.355, 1). So the two can be written apart without drifting. Change one and change the
    /// other.</para>
    /// </remarks>
    internal static double EaseAt(double t)
    {
        var left = 1 - Math.Clamp(t, 0, 1);
        return 1 - left * left * left;
    }

#if RPM_WINUI
    /// <summary>Cubic ease-out, the curve that everything in the app that MOVES uses. It has the same shape
    /// as the tween's 1 - (1 - t) cubed, written as the bezier that the compositor uses.
    ///
    /// <para>It is internal because it is the app's curve, not this class's. The album page's groove uses
    /// it too (see GrooveGlide). A second set of control points would be a second answer to a question that
    /// already has one. A sharper ease-out is not a matter of taste. It puts nine tenths of the distance
    /// into the first three frames and leaves the rest to crawl, which users see as choppy.
    /// </para></summary>
    internal static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));

    /// <summary>The moving column's width over time. It is held at its start for the length of the
    /// fade-out, then eased to its target. It is one animation, so the wait is on the same clock. It is not
    /// a second animation that has to be started later.</summary>
    private static ScalarKeyFrameAnimation Ramp(Compositor compositor, PaneMove? move, PanePhases phases)
    {
        var from = (float)(move?.From ?? 0);
        var ramp = compositor.CreateScalarKeyFrameAnimation();
        ramp.InsertKeyFrame(0f, from, compositor.CreateLinearEasingFunction());
        ramp.InsertKeyFrame(1f, (float)(move?.Target ?? from), Ease(compositor));

        // At least one millisecond, even when nothing moves. This animation is also the clock the content
        // swap is called back on, and a zero-length animation never completes.
        ramp.Duration = TimeSpan.FromMilliseconds(Math.Max(phases.MoveEnds - phases.OutEnds, 1));
        ramp.DelayTime = TimeSpan.FromMilliseconds(phases.OutEnds);
        ramp.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        return ramp;
    }

    /// <summary>Puts the clip and the slides on the three elements, as expressions over the
    /// driver.</summary>
    private void Dress(
        Compositor compositor, CompositionPropertySet driver, PaneMove move, PanePhases phases)
    {
        var wMax = (float)Math.Max(move.From, move.Target);
        var wMin = (float)Math.Min(move.From, move.Target);

        // This must be done before the visuals are taken, in the same order as Spin.Wobble. Otherwise the
        // Translation property is not on the visual. Animating it then does nothing, and gives no error.
        ElementCompositionPreview.SetIsTranslationEnabled(move.Seam, true);

        var mover = ElementCompositionPreview.GetElementVisual(move.Mover);
        var other = ElementCompositionPreview.GetElementVisual(move.Other);
        var seam = ElementCompositionPreview.GetElementVisual(move.Seam);
        _mover = mover;
        _other = other;
        _seam = seam;

        // The clip hides part of the MOVER. Its content is crossfading, so a clip that cannot re-wrap the
        // text does not matter. Behind an opacity that is going to zero, a re-wrap is not visible.
        _moverShape = Clip(compositor, driver, move.Mover, mover, move.Fold, Fx(wMax) + " - d.W");

        // The other card gets the same shape, and that is why this approach is right. Both cards are laid
        // out at their widest and cut back on the side the SEAM is on. Neither is translated. A card that
        // is never translated cannot move, so nothing at its far edge can move either. The pane's rail is
        // still because its card is only clipped. The library's rail is still for the same reason.
        //
        // Translating this card, and holding its rail against that translation with an equal and opposite
        // one, does not work. The arithmetic is right, but the edge still shimmers. Two floating-point
        // translations that sum to a constant do not rasterise to the same pixel every frame. The rail must
        // stay still. Cancelling a motion is not the same as not having one.
        _otherShape = Clip(compositor, driver, move.Other, other, !move.Fold, "d.W - " + Fx(wMin));

        // Only the seam slides. Use Translation, not Offset. An element's composition Offset IS where
        // layout put it, so writing it moves the element out of its own layout slot.
        var slide = compositor.CreateExpressionAnimation(
            "Vector3(" + (move.Fold ? "-1.0" : "1.0") + " * (d.W - " + Fx(wMax) + "), 0, 0)");
        slide.SetReferenceParameter("d", driver);
        seam.StartAnimation("Translation", slide);

        // THE PAGE IS NOT LAID OUT DURING THE MOVE. Widen below can do it, once a frame. It is kept because
        // it is the only way a page re-flows as it goes, but it is not called. The timer that drives it is
        // starved by the layout it causes, so the page arrives in a few large jumps rather than gliding. In
        // one measurement of a close, the wall's steps between frames were +106, +238, 0, 0, 0, 0, 0, -106,
        // -84. Each 0 is a frame that no tick reached. Per-frame layout on this thread still jitters.
        //
        // Smooth motion and per-frame layout cannot both be had on this thread. The compositor is the only
        // thing here that moves something every frame. So the move belongs to the compositor, and the page
        // re-flows once, at the landing, as it always did.
    }

    /// <summary>Gives the view inside the card that gives way its real width, once per frame, for the
    /// length of the move.
    /// </summary>
    /// <remarks>
    /// <b>The one part of a pane move that is a layout, and it has to be.</b> The card is laid out at the
    /// widest it gets, and then clipped back. That is right for anything anchored away from the seam, but
    /// wrong for a page. The library card holds a grid of star columns. A card 400 points narrower
    /// re-proportions every column, re-wraps the record's name and folds the groove. If the card is only
    /// clipped, none of that happens until the settled layout arrives. Then all of it happens in one frame,
    /// and users see the right-hand panel and page jitter and pop in place.
    ///
    /// <para>So the view inside is inset from the card's seam edge by however much of the card the clip
    /// hides right now. That lays it out at the width the card is SHOWING. It is the only thing on this
    /// side that is written per frame. It is written on the SEAM side. The card's far edge, its rail and
    /// its header are pure layout, and they never change during the move.</para>
    ///
    /// <para>The cost is the same work, moved. The landing already paid for that layout in one lump, so the
    /// total work does not change.</para>
    ///
    /// <para>The inset is read off the SEAM, not off a clock of our own. The seam is the one thing still
    /// translated, so reading where it stands asks the compositor what <c>W</c> is, on this thread. A clock
    /// of our own could be starved by the layout it causes, and that has happened.</para>
    ///
    /// <para>This uses a <c>DispatcherTimer</c>, not <c>CompositionTarget.Rendering</c>. The reason is
    /// given on GrooveGlide.Widen. The framework does not raise that event when it has nothing to do, and
    /// during a move on the compositor it has nothing to do. The timer's only other job is as a deadline
    /// for stopping the tick.</para>
    /// </remarks>
    private void Widen(PaneMove move, double wMin, double slack)
    {
        if (move.Inner is not { } inner)
            return;

        _widening = inner;
        _wideSeam = move.Seam;
        _wideRoom = move.Other.Parent as FrameworkElement;
        _wideFold = move.Fold;
        _wideMin = wMin;
        _wideSlack = slack;
        _widePrev = 0;
        _wideEnds = MoveMs + DeadlineMs;
        _wideClock = System.Diagnostics.Stopwatch.StartNew();

        _wideTimer ??= new DispatcherTimer();
        _wideTimer.Stop();
        _wideTimer.Interval = TimeSpan.FromMilliseconds(WideTickMs);
        _wideTimer.Tick -= Widen_Tick;
        _wideTimer.Tick += Widen_Tick;
        _wideTimer.Start();
    }

    /// <summary>About one frame at sixty per second.</summary>
    private const double WideTickMs = 15;

    /// <summary>How long past the end of the width move the tick keeps running. This makes the last write
    /// land on a compositor that has finished, not on one that is a frame short of it.</summary>
    private const double DeadlineMs = 80;

    /// <summary>Long enough to outlast any move this class is given. The tick stops on this deadline, not
    /// on a phase, because the width is not read off a clock.</summary>
    private const double MoveMs = 600;

    private void Widen_Tick(object? sender, object e) => WidenTick();

    private void WidenTick()
    {
        if (_widening is not { } view || _wideSeam is not { } seam
            || _wideClock is not { } clock || _wideRoom is not { } room)
            return;

        // Where the seam stands IS the moving column's width. Its own column begins where that one ends.
        // TransformToVisual reads the live composition translation.
        double at;
        try { at = seam.TransformToVisual(room).TransformPoint(new Windows.Foundation.Point(0, 0)).X; }
        catch { return; }

        var hidden = Math.Clamp(at - _wideMin, 0, _wideSlack);

        // A tick's write is rendered a frame later, so this inset is always a little behind where the seam
        // has got to. The two directions pay for that in different ways, and only one of them can be
        // allowed to.
        //
        // Opening, the inset has to GROW. If it is behind, it is smaller than the clip the card is cut
        // with. The view then reaches under that clip, and its far column is sliced. That is what the genre
        // and artist walls showed, a strip of part-tiles down the seam. So opening leads by however far the
        // seam travelled since the last tick. That keeps the inset at or ahead of the clip, and costs at
        // worst a hairline of bare card.
        //
        // Closing, the inset SHRINKS. If it is behind, it is larger than the clip. That leaves the same
        // hairline and nothing else, and the wall stays whole. Closing has nothing to lead for. Leading
        // there made the move look like it stepped, because every tick jumped ahead and then waited.
        var opening = hidden > _widePrev;
        var lead = opening ? Math.Abs(hidden - _widePrev) : 0;
        _widePrev = hidden;

        var inset = Math.Min(_wideSlack, hidden + lead);
        view.Margin = _wideFold
            ? new Thickness(0, 0, inset, 0)
            : new Thickness(inset, 0, 0, 0);

        if (clock.Elapsed.TotalMilliseconds >= _wideEnds)
            StopWidening();
    }

    /// <summary>Takes the tick off. The margin is left in place. The caller clears it when it settles the
    /// panes. Clearing it here would draw one frame of the settled layout too early.</summary>
    private void StopWidening()
    {
        if (_wideTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= Widen_Tick;
        }

        _wideClock = null;
        _widening = null;
        _wideSeam = null;
        _wideRoom = null;
    }

    /// <summary>Clips one card to the part of it that is showing, and keeps its rounded corners.
    /// </summary>
    /// <remarks>This is a rounded rectangle, not an <c>InsetClip</c>. An inset clip is cheaper, but it cuts
    /// square edges. A card being revealed would lose the two corners on the moving edge for the length of
    /// the move, and get them back in one frame at the end. The cards have an 8px radius, and a popping
    /// corner is visible.
    /// </remarks>
    /// <param name="inset">The expression for how many px to take off the card's FAR edge. That is the
    /// right edge normally, and the left edge while folding.</param>
    private static CompositionRoundedRectangleGeometry Clip(
        Compositor compositor, CompositionPropertySet driver,
        FrameworkElement element, Visual visual, bool fold, string inset)
    {
        var radius = element is Border border ? (float)border.CornerRadius.TopLeft : 0f;

        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.CornerRadius = new Vector2(radius, radius);

        // Sized off the VISUAL, not off a number taken here. The layout for this move is written in the
        // same turn but has not been arranged yet, so the element's measured width is still the old one.
        // The expression reads whatever size the card ends up with.
        var size = compositor.CreateExpressionAnimation("Vector2(v.Size.X - (" + inset + "), v.Size.Y)");
        size.SetReferenceParameter("d", driver);
        size.SetReferenceParameter("v", visual);
        shape.StartAnimation("Size", size);

        // When folding, the far edge is the LEFT one. The visible rectangle then starts at the inset, not
        // at zero. When not folding, it starts at zero and stays there, so it needs no animation.
        if (fold)
        {
            var offset = compositor.CreateExpressionAnimation("Vector2(" + inset + ", 0)");
            offset.SetReferenceParameter("d", driver);
            shape.StartAnimation("Offset", offset);
        }

        visual.Clip = compositor.CreateGeometricClip(shape);
        return shape;
    }

    /// <summary>One pane's fade OUT, and the flat stretch at zero across the move. The fade back in is not
    /// part of it. See <see cref="FadeIn"/>.
    /// </summary>
    /// <remarks>
    /// <b>One animation for the whole fade</b>, down, flat, then up, would be simpler, because one
    /// animation cannot drift out of step with itself. But that shape is wrong. The fade-in started BEFORE
    /// anybody knew how long the incoming view takes to lay out. On the album page, that layout takes about
    /// 286 ms with the thread running and nothing of ours inside it. So the fade-in was already a third of
    /// the way up before the view had been measured. A half-built page then settled under a rising opacity.
    ///
    /// <para>So the pane holds at zero until the content is actually there. The caller starts the fade-in
    /// on the other side of the swap. The flat stretch is as long as it needs to be, not as long as a
    /// guess.</para>
    ///
    /// <para>Nothing can drift, because there is nothing left to drift against. The two halves are
    /// separated by an event, not by two clocks running side by side.</para>
    /// </remarks>
    private static void Crossfade(Compositor compositor, PaneFade fade, PanePhases phases)
    {
        var visual = ElementCompositionPreview.GetElementVisual(fade.Views);
        var linear = compositor.CreateLinearEasingFunction();

        // How long this half lasts: the fade-out, and the move after it where there is one.
        var ends = Math.Max(phases.MoveEnds, phases.OutEnds);
        if (ends <= 0)
            return;

        var down = compositor.CreateScalarKeyFrameAnimation();
        down.InsertKeyFrame(0f, (float)fade.From, linear);
        down.InsertKeyFrame((float)(phases.OutEnds / ends), 0f, Ease(compositor));

        // The flat stretch across the move. It is added only when there is one, because two key frames at
        // the same progress is not a curve the compositor accepts.
        if (phases.MoveEnds > phases.OutEnds)
            down.InsertKeyFrame(1f, 0f, linear);

        down.Duration = TimeSpan.FromMilliseconds(ends);
        visual.StartAnimation("Opacity", down);
    }

    /// <summary>How long after the ramp was started the compositor first committed it.
    /// </summary>
    /// <remarks>
    /// <b>Why the landing always reports late.</b> SayLanding measures against a stopwatch that is started
    /// on THIS thread, in the turn that sets the move up. The ramp's own clock does not start then. It
    /// starts at the compositor's next commit, which is at best the end of this dispatcher turn. This turn
    /// still owes the move a layout pass. So the ramp ends that much after the stopwatch says it should,
    /// and the completion costs again on the way back. Every landing is late by both together, whatever the
    /// UI thread is doing. A late-landing threshold of 20 ms is below that floor.
    ///
    /// <para>This method measures only the FIRST half of that floor. The second half is the time spent
    /// getting the completion back. That half is not measured here. A landing that is reported as n ms late
    /// on an idle thread is n minus both halves, so both are needed before the threshold means
    /// anything.</para>
    ///
    /// <para>This is diagnostic only, and it runs only while diagnostics are on. Nothing waits on it. The
    /// task is dropped, and the move does not read the value.</para>
    /// </remarks>
    private static void SayCommitDelay(Compositor compositor)
    {
        if (!RPM.Core.Diagnostics.Scope.On)
            return;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        _ = Committed();

        async System.Threading.Tasks.Task Committed()
        {
            try
            {
                await compositor.RequestCommitAsync();
            }
            catch
            {
                return;   // the window went away between the start and the commit; nothing is owed here
            }

            RPM.Core.Diagnostics.Scope.Said(
                "panes", $"commit {clock.Elapsed.TotalMilliseconds:F1}ms after the ramp was started");
        }
    }

    /// <summary>Puts an element back in its own layout slot.</summary>
    private static void Steady(Visual visual)
    {
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    /// <summary>The width ramp has finished, so the content swap is due.
    /// </summary>
    /// <remarks>A composition animation's completion does not come back on the UI thread by itself. A
    /// scoped batch is what asks for it. That is why the ramp is the only animation in the batch.
    /// </remarks>
    private void OnBatchCompleted(object sender, CompositionBatchCompletedEventArgs args)
    {
        if (_batch is not { } batch || !ReferenceEquals(sender, batch))
            return;   // a batch belonging to a gesture that has already been replaced

        batch.Completed -= OnBatchCompleted;
        _batch = null;

        var moved = _onMoved;
        _onMoved = null;
        moved?.Invoke();
    }

    /// <summary>A float as the compositor's expression language reads it. It always has a decimal point,
    /// never a culture's comma, and never an exponent. It is written into the expression, not passed as a
    /// parameter, because these are constants of one move and do not change while it runs.</summary>
    private static string Fx(float value) =>
        value.ToString("0.0####", System.Globalization.CultureInfo.InvariantCulture);
#endif

    // Outside the WinUI guard, because both heads call it.

    /// <summary>Brings a pane's content back up, once it is there to be brought up.
    /// </summary>
    /// <remarks>The conductor calls this on the other side of the swap, after the incoming view has been
    /// measured and arranged. The reasons it is called here, and not in <see cref="Crossfade"/>, are
    /// explained on that method.
    ///
    /// <para>It is safe to call on a pane that is already up. It animates from wherever the visual
    /// is.</para>
    ///
    /// <para><b>It sits outside the WinUI-only block, although it has its own <c>#else</c>.</b> The Skia
    /// head needs this method, because <c>MainPage.Panes.cs</c> calls it without a guard. The guard belongs
    /// around the compositor calls inside the method, and not around the whole method.</para>
    /// </remarks>
    public static void FadeIn(FrameworkElement views, double ms)
    {
        ArgumentNullException.ThrowIfNull(views);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(views);
            var compositor = visual.Compositor;

            var up = compositor.CreateScalarKeyFrameAnimation();
            up.InsertKeyFrame(1f, 1f, Ease(compositor));
            up.Duration = TimeSpan.FromMilliseconds(Math.Max(1, ms));
            visual.StartAnimation("Opacity", up);
        }
        catch
        {
            // A pane that will not animate must still be visible. Everything else here can be given up.
            // This cannot.
            SetOpacity(views, 1);
        }
#else
        SetOpacity(views, 1);
        _ = ms;
#endif
    }
}

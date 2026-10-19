using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
#if RPM_WINUI
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
#endif

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The album page's row of similar albums sliding up as it opens and down as it closes.
/// </summary>
/// <remarks>
/// <b>What moves, and what is cut.</b> Two elements, which may be the same one. The CARRIED element is
/// translated down by <c>y</c> points. The CUT element, the block itself (rule, heading and sleeves), is
/// cut along its bottom edge by the same <c>y</c>. Zero is the open row at rest. The caller gives the
/// other end, which sets how far the heading travels on screen between the open and the shut page.
///
/// <para>On a page that does not scroll, both are the block. The rest of the page is anchored at the
/// top and does not move. The cut keeps the block's lowest visible edge where layout put it, so the
/// sleeves rise out of the card's foot instead of being drawn over the padding below it.</para>
///
/// <para>On a page that scrolls, the carried element is the page's whole grid. When the scroll offset
/// changes, EVERYTHING on screen moves, not only the block. If only the block were carried, a blank band
/// would show above it during a close. The whole page would also jump by the row's height when the slide
/// ended. The block is a child of the grid, so the same cut on it still pins its lowest visible edge.</para>
///
/// <para><b>Nothing here lays anything out.</b> The caller lays the page out OPEN for the whole move in
/// both directions. An opening page is laid out before the slide starts. A closing page is laid out
/// shut only once the slide has landed.</para>
///
/// <para><b>Two implementations, one curve.</b> On WinUI the compositor runs the animation off the
/// drawing thread, as GrooveGlide does. On Skia a DispatcherTimer writes a TranslateTransform and a
/// RectangleGeometry. Neither path lays anything out. Both use PaneGlide's curve and GrooveGlide's
/// duration, so the move matches the groove beside it.</para>
///
/// <para><b>Stale landings are dropped by generation.</b> Stopping a composition animation completes its
/// scoped batch. So a slide that was stopped and then replaced still sends a Completed for the old one.
/// Every start and every stop moves the generation on, and a landing from an older generation does
/// nothing. The deadline timer covers a window that stops producing frames during a slide (for example,
/// when it is minimised). In that case the batch may never complete.</para>
/// </remarks>
internal sealed class SimilarSlide
{
    /// <summary>The groove's own duration, so the two moves on this page take the same time.</summary>
    public const double Ms = GrooveGlide.Ms;

    /// <summary>Whether a slide is in flight.</summary>
    public bool Running { get; private set; }

    private Action? _onLanded;
    private int _generation;
    private DispatcherTimer? _deadline;

#if RPM_WINUI
    private Visual? _carriedVisual;
    private Visual? _cutVisual;
    private CompositionPropertySet? _driver;
#else
    private FrameworkElement? _carried;
    private FrameworkElement? _cut;
    private TranslateTransform? _move;
    private RectangleGeometry? _cutGeometry;
    private double _from;
    private double _to;
    private System.Diagnostics.Stopwatch? _clock;
    private DispatcherTimer? _tick;

    /// <summary>About a frame at sixty, as GrooveGlide's tick.</summary>
    private const double TickMs = 15;
#endif

    /// <summary>Carries <paramref name="carried"/> from <paramref name="from"/> points down to
    /// <paramref name="to"/> points down, cutting <paramref name="cut"/> along its bottom edge by the same
    /// distance at every frame.
    /// </summary>
    /// <param name="carried">What is translated: the block on a page that does not scroll, the page's grid
    /// on one that does.</param>
    /// <param name="cut">What is cut: always the block. Must be <paramref name="carried"/> or inside it, so
    /// the cut moves with the translation.</param>
    /// <param name="onLanded">Called on the UI thread when the slide reaches its end, after the translation
    /// and the cut have been taken off. Not called for a slide that was stopped.</param>
    /// <returns>False where there is nothing to move. The caller then applies the end state at
    /// once.</returns>
    public bool Start(FrameworkElement carried, FrameworkElement cut, double from, double to, Action onLanded)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(cut);
        ArgumentNullException.ThrowIfNull(onLanded);

        Stop();

        if (Math.Abs(to - from) <= 0.5)
            return false;

#if RPM_WINUI
        var generation = _generation;

        try
        {
            // Before the visual is taken: without it the "Translation" property is not on the visual,
            // and animating it does nothing.
            ElementCompositionPreview.SetIsTranslationEnabled(carried, true);

            var carriedVisual = ElementCompositionPreview.GetElementVisual(carried);
            var cutVisual = ElementCompositionPreview.GetElementVisual(cut);
            var compositor = carriedVisual.Compositor;

            var driver = compositor.CreatePropertySet();
            driver.InsertScalar("y", (float)from);

            var carry = compositor.CreateExpressionAnimation("Vector3(0, d.y, 0)");
            carry.SetReferenceParameter("d", driver);
            carriedVisual.StartAnimation("Translation", carry);

            var clip = compositor.CreateInsetClip();
            var inset = compositor.CreateExpressionAnimation("Max(0, d.y)");
            inset.SetReferenceParameter("d", driver);
            clip.StartAnimation("BottomInset", inset);
            cutVisual.Clip = clip;

            _carriedVisual = carriedVisual;
            _cutVisual = cutVisual;
            _driver = driver;
            _onLanded = onLanded;

            var run = compositor.CreateScalarKeyFrameAnimation();
            run.InsertKeyFrame(1f, (float)to, PaneGlide.Ease(compositor));
            run.Duration = TimeSpan.FromMilliseconds(Ms);

            // A scoped batch, because a composition animation's completion does not come back on the
            // UI thread without one.
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) => Land(generation);
            driver.StartAnimation("y", run);
            batch.End();
        }
        catch
        {
            // A composition call that will not run is a row that opens or closes without sliding.
            Stop();
            return false;
        }
#else
        _carried = carried;
        _cut = cut;
        _from = from;
        _to = to;
        _onLanded = onLanded;

        // The near end, written before the first frame, so the page is never drawn once with the block
        // at its layout position before the slide begins.
        Draw(from);

        _clock = System.Diagnostics.Stopwatch.StartNew();
        _tick ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _tick.Tick -= Slide_Tick;
        _tick.Tick += Slide_Tick;
        _tick.Start();
#endif

        _deadline ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Ms + 120) };
        _deadline.Tick -= Deadline_Tick;
        _deadline.Tick += Deadline_Tick;
        _deadline.Start();

        Running = true;
        return true;
    }

    /// <summary>
    /// Sends a slide that is in flight to a new end, from wherever it is drawn right now, without
    /// stopping it. What a second press mid-slide does.
    /// </summary>
    /// <remarks>
    /// <b>Not Stop and Start.</b> Stop reads where the slide has got to, and Start begins a new slide
    /// from there. On WinUI that read is wrong. Reading the driver's <c>y</c> in the same turn as
    /// stopping it returns the value last set from this thread (the slide's start), not the compositor's
    /// live value. So a close reversed 170 points down would read as zero, and the page would jump back
    /// to open in one frame. Here the new animation starts on the same driver, with
    /// <c>this.StartingValue</c> as its first key frame. The compositor fills that in with the live value,
    /// so this thread does not need to know it.
    ///
    /// <para>The animation it replaces completes its scoped batch when it is replaced. That batch's
    /// landing arrives late. The generation moves on here, so <see cref="Land"/> drops it.</para>
    /// </remarks>
    /// <param name="to">The new far end, in points down from the carried element's layout position.</param>
    /// <param name="onLanded">Replaces the landing the slide had.</param>
    /// <returns>False where nothing is in flight. The caller then starts a slide of its own.</returns>
    public bool Retarget(double to, Action onLanded)
    {
        ArgumentNullException.ThrowIfNull(onLanded);

        if (!Running)
            return false;

        _generation++;
        _onLanded = onLanded;

#if RPM_WINUI
        if (_driver is not { } driver)
            return false;

        var generation = _generation;

        try
        {
            var compositor = driver.Compositor;
            var run = compositor.CreateScalarKeyFrameAnimation();
            run.InsertExpressionKeyFrame(0f, "this.StartingValue");
            run.InsertKeyFrame(1f, (float)to, PaneGlide.Ease(compositor));
            run.Duration = TimeSpan.FromMilliseconds(Ms);

            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) => Land(generation);
            driver.StartAnimation("y", run);
            batch.End();
        }
        catch
        {
            Stop();
            return false;
        }
#else
        // This head does know where it is drawn: the transform holds the last value the tick wrote.
        _from = _move?.Y ?? _from;
        _to = to;
        _clock = System.Diagnostics.Stopwatch.StartNew();
#endif

        _deadline?.Stop();
        _deadline?.Start();
        return true;
    }

    /// <summary>
    /// Stops the slide and takes the translation and the cut off, leaving the carried element at its
    /// layout position.
    /// </summary>
    /// <remarks>
    /// Returns nothing on purpose. A reversal starts from <see cref="Retarget"/>, not from a position
    /// read back here. On WinUI a position read back is the slide's start, not the live position.
    /// </remarks>
    public void Stop()
    {
        _generation++;
        Running = false;
        _onLanded = null;
        _deadline?.Stop();

#if RPM_WINUI
        if (_driver is { } driver)
        {
            driver.StopAnimation("y");
            _driver = null;
        }

        if (_carriedVisual is { } carried)
        {
            carried.StopAnimation("Translation");
            carried.Properties.InsertVector3("Translation", Vector3.Zero);
            _carriedVisual = null;
        }

        if (_cutVisual is { } cut)
        {
            cut.Clip = null;
            _cutVisual = null;
        }
#else
        _tick?.Stop();
        _clock = null;

        if (_carried is { } carried && _move is not null)
            carried.RenderTransform = null;

        if (_cut is { } cut && _cutGeometry is not null)
            cut.Clip = null;

        _carried = null;
        _cut = null;
        _move = null;
        _cutGeometry = null;
#endif
    }

    private void Deadline_Tick(object? sender, object e) => Land(_generation);

    /// <summary>Ends the slide at its far end and hands over to the caller, once. A landing from a slide
    /// that has been stopped or replaced has an old generation, so it does nothing.</summary>
    private void Land(int generation)
    {
        if (generation != _generation || !Running)
            return;

        var landed = _onLanded;
        Stop();
        landed?.Invoke();
    }

#if !RPM_WINUI
    private void Slide_Tick(object? sender, object e)
    {
        if (_clock is not { } clock)
            return;

        // Read off the wall clock, not counted in frames, so a busy thread lands on time rather than
        // short.
        var through = Math.Clamp(clock.Elapsed.TotalMilliseconds / Ms, 0, 1);
        Draw(_from + (_to - _from) * PaneGlide.EaseAt(through));

        if (through >= 1)
            Land(_generation);
    }

    /// <summary>Draws the carried element <paramref name="y"/> points below its layout position, and cuts
    /// the cut element on its bottom edge by the same amount. The cut is in that element's own
    /// coordinates, before the carried element's transform, so its visible bottom stays where layout put
    /// it.</summary>
    private void Draw(double y)
    {
        if (_carried is not { } carried || _cut is not { } cut)
            return;

        if (_move is null)
        {
            _move = new TranslateTransform();
            carried.RenderTransform = _move;
        }

        if (_cutGeometry is null)
        {
            _cutGeometry = new RectangleGeometry();
            cut.Clip = _cutGeometry;
        }

        _move.Y = y;
        _cutGeometry.Rect = new Windows.Foundation.Rect(
            0, 0, cut.ActualWidth, Math.Max(0, cut.ActualHeight - Math.Max(0, y)));
    }
#endif
}

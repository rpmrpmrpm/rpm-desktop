#if RPM_WINUI
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
#else
using Microsoft.UI.Xaml;
#endif

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The splash coming off, on the system compositor.
///
/// <para><b>Why the compositor.</b> A XAML storyboard is driven by the drawing thread. That thread is
/// busy during startup, so a storyboard fade would run as a few frames spread over several seconds
/// (a 730 ms fade took about 6.7 s). A composition animation is handed to the compositor and ticked on
/// its own thread at the display's rate. A drawing thread that is laying out the whole home screen does
/// not make it stutter. So the fade does not need to wait for the drawing thread to be free.</para>
///
/// <para><b>Skia keeps the storyboard.</b> Uno's composition is its own implementation, and every
/// <c>DoubleAnimation</c> there is a <c>DispatcherDoubleAnimator</c> on <c>CompositionTarget.Rendering</c>
/// anyway, so there is nothing to move it onto. <see cref="Start"/> returns false there and the caller
/// uses its own storyboard path. <c>Spin</c> and <c>PaneGlide</c> work the same way.</para>
/// </summary>
internal static class SplashFade
{
    /// <summary>
    /// Fades the wordmark out, then the black over it, and calls <paramref name="done"/> on the drawing
    /// thread when both have finished.
    /// </summary>
    /// <param name="logo">The wordmark. Faded from its resting opacity to nothing first.</param>
    /// <param name="overlay">The black covering the window. Starts once the wordmark has gone.</param>
    /// <param name="logoMs">How long the wordmark takes to go.</param>
    /// <param name="overlayMs">How long the black takes after it.</param>
    /// <param name="logoFrom">The wordmark's resting opacity. It is not 1, so the animation is told
    /// where to start and does not read a property that the compositor now owns.</param>
    /// <param name="done">Run on the drawing thread when the second half ends. It is required: the
    /// window's saved size is restored here, so a reveal that never reports its end leaves the window at
    /// the wrong size.</param>
    /// <returns>False where there is no system compositor to hand this to, which is every head except
    /// WinUI. Nothing has been started in that case and the caller runs its own reveal.</returns>
    public static bool Start(
        UIElement logo,
        UIElement overlay,
        double logoMs,
        double overlayMs,
        double logoFrom,
        Action done)
    {
        ArgumentNullException.ThrowIfNull(logo);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(done);

#if RPM_WINUI
        try
        {
            var logoVisual = ElementCompositionPreview.GetElementVisual(logo);
            var overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);
            var compositor = logoVisual.Compositor;

            // A cubic ease-out, the same curve the panes move on, written as the bezier that the
            // compositor uses.
            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));

            var logoOut = compositor.CreateScalarKeyFrameAnimation();
            logoOut.InsertKeyFrame(0f, (float)logoFrom);
            logoOut.InsertKeyFrame(1f, 0f, ease);
            logoOut.Duration = TimeSpan.FromMilliseconds(Math.Max(1, logoMs));

            var blackOut = compositor.CreateScalarKeyFrameAnimation();
            blackOut.InsertKeyFrame(0f, 1f);
            blackOut.InsertKeyFrame(1f, 0f, ease);
            blackOut.Duration = TimeSpan.FromMilliseconds(Math.Max(1, overlayMs));

            // The wait between the two halves is the second animation's own delay, not a timer that
            // starts it. With one clock, the black cannot begin before the wordmark has finished,
            // however busy the drawing thread is.
            blackOut.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, logoMs));
            blackOut.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

            // Trap: the completion of a composition animation does not come back on the UI thread by
            // itself. A scoped batch asks for it. Only the LONGER half goes in the batch, because a batch
            // over both would complete when the first of them did.
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            overlayVisual.StartAnimation("Opacity", blackOut);
            batch.End();
            batch.Completed += (_, _) => done();

            logoVisual.StartAnimation("Opacity", logoOut);
            return true;
        }
        catch
        {
            // The window may have no compositor yet, or the visual may not animate. The splash must
            // come off in either case, so the caller is told this did not start and shows the interface
            // its own way.
            return false;
        }
#else
        _ = logoMs;
        _ = overlayMs;
        _ = logoFrom;
        _ = done;
        return false;
#endif
    }
}

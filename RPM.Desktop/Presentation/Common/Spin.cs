using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A record that turns, driven by the compositor rather than by the thread that draws.
///
/// <para><b>Why this exists.</b> Turning the record by writing a <c>RotateTransform.Angle</c> from a
/// per-frame callback on the drawing thread is not smooth. Every write invalidates the render tree, so
/// the turn is only as smooth as that thread is free. The same thread builds pages, reads the library,
/// and lays out the album wall. The record then turns at the display's rate while nothing else happens,
/// and it visibly stutters whenever something else runs. Making the tick cheaper does not help, because
/// the cost is not the arithmetic. The cost is running on that thread at all.</para>
///
/// <para><b>What this does instead.</b> It gives the rotation to the system compositor. The compositor
/// runs it on its own thread at the display's refresh rate, and keeps running it while the drawing
/// thread is busy, collecting, or laying out ten thousand rows. The app writes nothing per frame. It
/// says "turn at this rate" once, and stops it once. The frame tick in <see cref="Shell.FrameTick"/>
/// makes the same point.</para>
///
/// <para><b>The Skia head keeps the per-frame way.</b> This is a deliberate choice. Uno's composition
/// is its own implementation, on its own renderer. The ramp, the wobble and the stop-where-it-is must
/// all be right on the head that ships. <see cref="Start"/> returns false there, and the caller keeps
/// its per-frame tick.</para>
/// </summary>
internal static class Spin
{
    /// <summary>Starts <paramref name="element"/> turning about its own centre, and keeps it turning
    /// until
    /// <see cref="Stop"/>.
    /// </summary>
    /// <param name="element">What turns. Its centre is tracked as it resizes, so a record that grows
    /// with the artwork band keeps turning about its own centre.</param>
    /// <param name="secondsPerRevolution">How long one turn takes at full speed. 1.8 is 33⅓ revolutions
    /// a minute, which is what an LP does.</param>
    /// <param name="rampSeconds">How long it takes to get there from a standstill. A record does not
    /// reach speed instantly and neither does this.</param>
    /// <param name="fromDegrees">Where to start turning from: the angle at which the last spin was
    /// stopped. So a record that is put back on carries on, and does not jump to the top of the
    /// groove.</param>
    /// <returns>False where the platform will not do this off the drawing thread. The caller then keeps
    /// whatever it had, which is a per-frame tick.</returns>
    public static bool Start(
        FrameworkElement element, double secondsPerRevolution, double rampSeconds, double fromDegrees)
    {
        ArgumentNullException.ThrowIfNull(element);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            // The centre is an expression, not a fixed number. The record sits in a band that the user
            // can drag taller, so its size changes. A fixed centre would stop being the middle of the
            // disc after the first resize. The record would then look badly centred, which is noticed
            // even when the cause is not clear.
            var centre = compositor.CreateExpressionAnimation(
                "Vector3(this.Target.Size.X * 0.5, this.Target.Size.Y * 0.5, 0)");
            visual.StartAnimation("CenterPoint", centre);

            // One turn, linear, repeating forever. A record turns at a constant rate, so the easing is
            // linear. Any other easing would make the turn speed up and slow down, like a poor
            // turntable.
            var spin = compositor.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(0f, (float)fromDegrees, compositor.CreateLinearEasingFunction());
            spin.InsertKeyFrame(1f, (float)fromDegrees + 360f, compositor.CreateLinearEasingFunction());
            spin.Duration = TimeSpan.FromSeconds(secondsPerRevolution);
            spin.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngleInDegrees", spin);

            // The ramp to full speed uses the animation's own playback rate. A separate ramp animation
            // would hand over to the forever animation, and that handover leaves a seam where the angle
            // can jump. With one rotation running for the whole spin, there is no seam.
            if (rampSeconds > 0 && visual.TryGetAnimationController("RotationAngleInDegrees")
                    is { } controller)
            {
                controller.PlaybackRate = 0;

                var up = compositor.CreateScalarKeyFrameAnimation();
                up.InsertKeyFrame(0f, 0f);
                // Smoothstep's shape, as the cubic bezier the compositor speaks: no jerk at either end.
                up.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 1f)));
                up.Duration = TimeSpan.FromSeconds(rampSeconds);
                controller.StartAnimation("PlaybackRate", up);
            }

            return true;
        }
        catch
        {
            // If the compositor cannot do this, return false so the caller keeps its own path. A record
            // that does not turn at all is worse.
            return false;
        }
#else
        _ = secondsPerRevolution;
        _ = rampSeconds;
        _ = fromDegrees;
        return false;
#endif
    }

    /// <summary>Stops the turn and says where it stopped.
    /// </summary>
    /// <returns>The angle at which the record stopped. Pass it to <see cref="Start"/> when the needle
    /// goes down again. A record that is lifted off does not jump back to the start of the
    /// groove.</returns>
    public static double Stop(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);

            // STOP FIRST, THEN READ. The order decides whether this works.
            //
            // An animated composition property is not read from this thread. While the animation runs,
            // reading it returns the last value that was set, which is where the spin began. It does
            // not return where the spin has got to. Stopping hands the live value back, because the
            // property keeps whatever the compositor last gave it. So the reading is only true after
            // the stop. In the other order the record would jump back to the top of the groove each
            // time the needle lifted.
            visual.StopAnimation("RotationAngleInDegrees");
            return visual.RotationAngleInDegrees % 360.0;
        }
        catch
        {
            return 0;
        }
#else
        return 0;
#endif
    }

    /// <summary>Makes <paramref name="element"/> orbit a little, once per turn of <paramref
    /// name="record"/>. This is an eccentric pressing, which stops a spinning disc from looking like a
    /// spinning picture.</summary>
    /// <remarks>An expression over the record's own rotation, so it runs on the compositor beside the
    /// turn it follows. It cannot drift out of step with the turn. Read per frame on the drawing
    /// thread, the angle would be a second piece of arithmetic on that thread, which is what this class
    /// avoids.
    ///
    /// The amplitude is in pixels, not a fraction. The thing it would be a fraction of is not visible
    /// to the compositor. The disc is drawn at a fixed 200 inside a Viewbox, so its layout size says
    /// nothing about how big it appears on screen. The caller works out the real radius for the halo
    /// and the gradients (see
    /// <c>ArtDisplay.Layout</c>), and sets this again when the band is dragged to a new height.
    /// </remarks>
    /// <param name="turning">The layer whose rotation the orbit follows. The compositor reads its
    /// angle, so the two cannot drift out of step.</param>
    /// <param name="amplitudePx">How far off centre, in pixels.</param>
    /// <returns>False where the platform will not do it; the caller keeps its own.</returns>
    public static bool Wobble(FrameworkElement element, FrameworkElement turning, double amplitudePx)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(turning);

#if RPM_WINUI
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            var visual = ElementCompositionPreview.GetElementVisual(element);
            var spinning = ElementCompositionPreview.GetElementVisual(turning);
            var compositor = visual.Compositor;

            // Translation, not Offset. An element's composition Offset is where layout put it. Writing
            // it moves the element away from its own layout slot. Translation is added on top of it.
            var wobble = compositor.CreateExpressionAnimation(
                "Vector3(" +
                "  amp * Cos(r.RotationAngleInDegrees * rad)," +
                "  amp * Sin(r.RotationAngleInDegrees * rad)," +
                "  0)");
            wobble.SetReferenceParameter("r", spinning);
            wobble.SetScalarParameter("amp", (float)amplitudePx);
            wobble.SetScalarParameter("rad", (float)(Math.PI / 180.0));

            visual.StartAnimation("Translation", wobble);
            return true;
        }
        catch
        {
            return false;
        }
#else
        _ = turning;
        _ = amplitudePx;
        return false;
#endif
    }

    /// <summary>Takes the wobble off and puts the element back where layout wants it.</summary>
    public static void Steady(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

#if RPM_WINUI
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch
        {
            // A wobble that cannot be stopped must not make the caller fail.
        }
#endif
    }
}

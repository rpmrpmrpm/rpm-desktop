using System.Diagnostics;
using RPM.Core.Art;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;


namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The turning record and the moving waves: the two things on this stage that move on their own. Both are
/// driven by the render tick, not by a Storyboard. A dependency animation that repeats forever does not
/// stop cleanly, and both must stop as soon as playback stops.
/// </summary>
public sealed partial class ArtDisplay
{
// Motion: the record and the waves
//
// One clock drives both, so they spin up together. A per-frame tick is used instead of storyboards. A
// storyboard can loop a rotation, but it cannot ramp into the loop and then hold it. The waves must ramp on
// exactly the same curve as the record.

    private readonly Stopwatch _clock = new();
    private double _lastTick;
    private double _rampElapsed;
    private bool _running;

    private double _angle;
    private readonly double[] _waveOffsets = new double[3];

    /// <summary>Playback started or stopped. The record and the waves follow it, and nothing else does. The
    /// art itself is driven by the manager, not by the audio output.</summary>
    public void SetPlaying(bool playing)
    {
        _playing = playing;
        SyncParked();   // This also syncs the motion. See SyncMotion
    }

    /// <summary>
    /// Whether the per-frame tick should be running at all: something is playing and the drawer is open.
    ///
    /// <para>When parked, the record has moved into the corner (see RecordDisc.Turntable) and faded out,
    /// and the waves are collapsed. Running the tick then would update a disc that nobody can see, on every
    /// frame, which repaints the whole app for no reason.</para>
    ///
    /// <para>Stopping is deferred by the fly-out, because the disc is still visible during that second.
    /// Cutting the tick at once would freeze the disc mid-turn and then shrink the still frame into the
    /// corner. Stopping because playback stopped is the exception: the record halts where it stands, the
    /// way a record lifted off a turntable stops.</para>
    /// </summary>
    private void SyncMotion()
    {
        // A record that is not on the header (see ShowRecord) has nothing for the tick to turn, and the
        // waves are collapsed, so the render clock is not needed.
        if (_playing && !_idle && RecordShown)
        {
            CancelMotionStop();
            if (!_running)
                StartMotion();
            return;
        }

        if (!_running)
            return;

        if (!_playing || !RecordShown)
        {
            CancelMotionStop();
            StopMotion();
            return;
        }

        ScheduleMotionStop();
    }

    private DispatcherTimer? _motionStop;

    private void CancelMotionStop()
    {
        _motionStop?.Stop();
        _motionStop = null;
    }

    private void ScheduleMotionStop()
    {
        if (_motionStop is not null)
            return;

        var timer = new DispatcherTimer { Interval = RecordFlyDuration };
        timer.Tick += (_, _) =>
        {
            CancelMotionStop();
            if (!(_playing && !_idle))
                StopMotion();
        };
        _motionStop = timer;
        timer.Start();
    }

    /// <summary>
    /// Whether the compositor turns the record, rather than this thread.
    ///
    /// <para>True on the WinUI head. The turn is given to the system compositor, which runs it on its own
    /// thread at the display's refresh rate. Nothing in this file runs per frame, so the record keeps
    /// turning smoothly while the UI thread is busy with layout or garbage collection. False on the Skia
    /// head, and wherever the compositor refuses the turn. Then the per-frame tick below does the work. See
    /// <see cref="Common.Spin"/>.</para>
    /// </summary>
    private bool _composited;

    private void StartMotion()
    {
        _running = true;

        // Start the compositor turn first, and pass the last angle. A needle put back down carries on from
        // where it was lifted, not from the top of the groove.
        if (_record is { } onDeck
            && Common.Spin.Start(onDeck.Turning, RevolutionSeconds, SpinUpSeconds, _angle))
        {
            _composited = true;
            ApplyWobble();

            // Log which thread turns the record, once per spin-up. The difference cannot be seen in a
            // screenshot, and it decides whether the turn can stutter. So it is logged, not left to be
            // guessed from the missing FrameWatch lines.
            RPM.Core.Diagnostics.Scope.Said("window", "record turns on the compositor");
            return;
        }

        _composited = false;
        RPM.Core.Diagnostics.Scope.Said("window", "record turns on the drawing thread");
        _rampElapsed = 0;
        _clock.Restart();
        _lastTick = 0;
        Services.Shell.FrameTick.Add("art-motion", DispatcherQueue, OnRendering);
    }

    /// <summary>
    /// The eccentric-pressing wobble: a small orbit of the disc, sized as a share of the record on screen.
    ///
    /// <para>It is applied again whenever it is needed, not set once. The amplitude is in pixels, and the
    /// disc changes size when the artwork band is dragged. It is called from the layout pass that works out
    /// the radius (see <c>ArtDisplay.Layout</c>), and from the start of the turn.</para>
    ///
    /// <para>It does nothing unless the compositor has the turn. On the per-frame path the wobble is
    /// written with the angle in <see cref="OnRendering"/>.</para>
    /// </summary>
    private void ApplyWobble()
    {
        if (!_composited || _deck is not { } deck || _recordRadius <= 0)
            return;

        // Go through the deck, which knows whether the disc is a CD. A CD does not wobble.
        deck.Orbit(_recordRadius * 2 * WobbleFraction);
    }

    /// <summary>Stops feeding the tick. The record and the waves stay where they stopped. A record that is
    /// lifted off does not snap back to the start of the groove.</summary>
    private void StopMotion()
    {
        if (!_running)
            return;

        _running = false;

        if (_composited)
        {
            _composited = false;

            // The stop angle is kept. The next Start continues from it.
            if (_record is { } disc)
                _angle = Common.Spin.Stop(disc.Turning);

            // Go through the deck, so that a face change after this does not restart the orbit.
            _deck?.Orbit(0);

            return;
        }

        _clock.Stop();
        Services.Shell.FrameTick.Remove("art-motion", OnRendering);
    }

    private void OnRendering(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastTick;
        _lastTick = now;

        // A long frame (the app was busy, or the window was hidden) must not move the record by a big jump.
        // The step is capped below.
        if (dt <= 0)
            return;
        dt = Math.Min(dt, 0.1);

        _rampElapsed += dt;
        var speed = Ramp(_rampElapsed / SpinUpSeconds);

        _angle = (_angle + 360.0 / RevolutionSeconds * speed * dt) % 360.0;
        if (_record is { } record)
        {
            record.Rotate.Angle = _angle;

            // Eccentric-pressing wobble: the whole disc and its shadow orbit a little, once per turn. The
            // amplitude scales with the disc, so the wobble stays small at any size.
            if (_deck is { } deck)
            {
                var rad = _angle * Math.PI / 180.0;
                var amp = _recordRadius * 2 * WobbleFraction;
                deck.Wobble(amp * Math.Cos(rad), amp * Math.Sin(rad));
            }
        }

        // The waves are collapsed. The record carries the motion now (see WaveHost), and moving a hidden
        // polyline would be three transform writes per frame for nothing. The waves stay driven, not
        // removed, so that a host shown again resumes a moving wave.
        //
        // Note for anyone who shows the host again: on the WinUI head this method does not run at all,
        // because the record's turn belongs to the compositor. A wave shown there stays still until it gets
        // its own composition animation. See Common.Spin for the shape such an animation would take.
        if (WaveHost.Visibility == Visibility.Visible)
        {
            TranslateTransform[] shifts = [Wave1Shift, Wave2Shift, Wave3Shift];
            for (var i = 0; i < shifts.Length; i++)
            {
                var (wavelength, _, seconds) = WaveLayers[i];
                _waveOffsets[i] = (_waveOffsets[i] + wavelength / seconds * speed * dt) % wavelength;
                shifts[i].X = -_waveOffsets[i];
            }
        }
    }

    /// <summary>Smoothstep from a standstill to full speed: no jerk at either end of the second.</summary>
    private static double Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Whether a track is actually playing. This is not the same as _running, which only says
    /// whether the tick is being fed. The two agree today, but they are read at different moments. This one
    /// is read before the motion state has settled.</summary>
    private bool _playing;

    /// <summary>The target last sent to the stage fades: the scrim up or down, and the waves the other way.
    /// It starts at zero, which is where the XAML sets them. See <see cref="SyncStage"/> for why it is
    /// stored here and not read back from the elements.</summary>
    private double _stageTarget;

    /// <summary>
    /// The waves and the scrim, from the two things that own them.
    ///
    /// The waves fill the strip when nothing else is on it, and step aside for a photo. The scrim keeps the
    /// white text readable over a photo, so it fades in and out with the photo.
    /// </summary>
    private void SyncStage(TimeSpan? duration = null)
    {
        var photo = _shownUrl is not null;
        var target = photo ? 1 : 0;

        // Only act when the value actually changes.
        //
        // Starting a fade that is already at its target is not a no-op in Uno. A new dependent animation
        // over one that is holding its end value makes the property revert to its base for a frame, and the
        // base here is zero. The scrim would then vanish and fade back in, so the picture flashes bright
        // and then dims again. This happened on every call with the artwork unchanged. It also happens on
        // every reshape now, because a re-bake for a new band goes through the whole swap (see
        // ShowBackdrop).
        //
        // The value is stored here rather than read from the element. While a dependent animation is
        // running, the property still reads its old value, so the element cannot say where it is going.
        // This is the same reason as for the rpm mark's scale (_rpmScaleTarget).
        if (Math.Abs(_stageTarget - target) < 0.001)
            return;
        _stageTarget = target;

        Animate(WaveHost, photo ? 0 : 1, duration: duration);
        // The scrim uses the same clock as the picture it darkens. When the art changes from idle, the
        // composite fades in fast (WashDuration). With the default 2 s duration the scrim would keep
        // darkening the picture for about 1.6 s after it appeared, so the picture would load bright and
        // then dim. Passing the same duration keeps the scrim in step with the picture. The scrim and its
        // tail are one element drawn in two pieces. So the host fades, never the pieces, which would come
        // up at different rates and show the join between them.
        Animate(ScrimHost, photo ? 1 : 0, duration: duration);

        // The 20% dim is for the same text over the same picture, so it moves with the scrim. It uses the
        // same target, the same clock and the same guard above. The guard stops a repeated call from
        // dropping it to its base of zero for a frame.
        Animate(HeaderDim, photo ? 1 : 0, duration: duration);
    }
}

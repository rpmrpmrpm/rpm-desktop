using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// One layer's opacity, driven from the render tick.
///
/// <para><b>Why this and not a Storyboard.</b> A <c>DoubleAnimation</c> keeps hold of the property it
/// animates. When it finishes, the value it reached is an animated value. A later
/// <c>layer.Opacity = 0</c> then writes to the base value, which the animation keeps covering. Nothing
/// throws, and nothing looks wrong at the time. The layer just stays where the last fade left it. The
/// genre page hit this. A layer shown on each navigation kept the previous genre's photograph, and the new
/// one appeared in a single frame on top of it.</para>
///
/// <para>A tick writes the plain property. So <see cref="Set"/> takes effect, and a fade always starts
/// from where the layer really is, not from where the last fade was aimed.</para>
///
/// <para>The curve is smoothstep, so neither end of a dissolve has a hard edge.</para>
/// </summary>
/// <param name="layer">What is being faded.</param>
/// <param name="watch">The name that frames are reported under. See <see cref="Services.Shell.FrameWatch"/>.
/// The caller states it, because the question that watch answers is which surface is asking for frames.
/// A shared helper cannot know that.</param>
public sealed class Dissolve(UIElement layer, string watch)
{
    private readonly Stopwatch _clock = new();
    private double _from, _to;
    private TimeSpan _over;
    private bool _running;
    private Action? _landed;

    /// <summary>Fades the layer to an opacity over a duration, starting from wherever it is now.</summary>
    public void To(double to, TimeSpan over, Action? landed = null)
    {
        // Already at the target, with no fade running: there is nothing to animate. The completion must
        // still run. If it were skipped, the layer it was covering would stay covered for good.
        if (!_running && Math.Abs(layer.Opacity - to) < 0.001)
        {
            _to = to;
            landed?.Invoke();
            return;
        }

        _from = layer.Opacity;   // wherever it actually is. An interrupted fade does not jump.
        _to = to;
        _over = over;
        _landed = landed;        // a new fade replaces the completion of the old one
        _clock.Restart();

        if (_running)
            return;
        _running = true;
        CompositionTarget.Rendering += Tick;
        Services.Shell.FrameWatch.Began(watch);
    }

    /// <summary>Sets the opacity straight to a value, now. There is no fade, and no fade is left in
    /// flight to arrive later.</summary>
    public void Set(double value)
    {
        _landed = null;
        Stop();
        _to = value;
        layer.Opacity = value;
    }

    /// <summary>Runs a fade in flight straight to its end, including its completion. Whatever starts next
    /// then starts from a known value.</summary>
    public void Land()
    {
        if (_running)
            Finish();
    }

    private void Tick(object? sender, object e)
    {
        var seconds = _over.TotalSeconds;
        var t = seconds <= 0 ? 1 : Math.Clamp(_clock.Elapsed.TotalSeconds / seconds, 0, 1);
        var eased = t * t * (3 - (2 * t));   // smoothstep, so neither end of the dissolve has a hard edge

        layer.Opacity = _from + ((_to - _from) * eased);

        if (t >= 1)
            Finish();
    }

    private void Finish()
    {
        layer.Opacity = _to;
        Stop();
        var landed = _landed;
        _landed = null;
        landed?.Invoke();
    }

    private void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _clock.Stop();
        CompositionTarget.Rendering -= Tick;
        Services.Shell.FrameWatch.Ended(watch);
    }
}

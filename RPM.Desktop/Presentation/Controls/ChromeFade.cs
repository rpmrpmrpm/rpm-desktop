using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>One value, moved towards a target one frame at a time. The value can be an element's opacity,
/// or any other number that a callback applies.
/// </summary>
/// <remarks>
/// <para><b>It uses numbers, not a Storyboard.</b> This is needed for a fade that can change direction part
/// way through. A <c>DoubleAnimation</c> with no <c>From</c> starts at the property's value as Uno reads
/// it. While an animation is already on the property, Uno returns the base value, not the live one. So a
/// container whose base opacity is 1 snapped back to full when a pointer move stopped its fade-out. A row
/// whose base is 0 never faded out at all. It just disappeared.</para>
///
/// <para>Stopping the old storyboard has the same problem from the other end. The animated value is kept in
/// one slot per property. Stopping the old animation clears the slot that the new one is already writing
/// to, so the base value shows for one frame. Starting the new one first does not help, because it uses the
/// same slot.</para>
///
/// <para>The fade is driven from the composition tick, so the live value is always known. A new target
/// during a fade continues from the current value. Nothing is stopped, held or reverted. The tick is
/// subscribed only while something is moving. An idle screen pays nothing for it.</para>
///
/// <para>The cost per frame is the same as a Storyboard's. A dependent animation also has the framework
/// write Opacity once per frame on this thread.</para>
/// </remarks>
internal sealed class ChromeFade
{
    private readonly Action<double> _write;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private double _value, _from, _to;
    private double _elapsed, _seconds, _last;
    private bool _ticking;
    private EventHandler<object>? _tick;

    /// <summary>An element's own opacity. Nearly every fade in the app is this.</summary>
    public ChromeFade(FrameworkElement element)
        : this(v => { if (element.Opacity != v) element.Opacity = v; }, element.Opacity) { }

    /// <summary>Any other number, handed to <paramref name="write"/> one frame at a time, starting from
    /// <paramref name="from"/>.
    ///
    /// <para>For fades that are not one element's opacity. For example, the wall's drawer fades a colour, a
    /// blurred cover and a pointer triangle. It fades from the value it opened with to the value its cover
    /// needs. These are three elements inside a recycled template. Writing to the model they all bind to
    /// moves them together. A drawer that is re-seated during a fade then picks up the right value. Writing
    /// the opacity of three elements by hand cannot promise that.</para>
    /// </summary>
    public ChromeFade(Action<double> write, double from)
    {
        _write = write;
        _value = _from = _to = from;
    }

    /// <summary>Where the fade is headed. Use it as the guard. A pointer move over chrome that is already
    /// visible must not restart the fade on every move.</summary>
    public double Target => _to;

    /// <summary>Eases to <paramref name="to"/> over <paramref name="ms"/> milliseconds. A zero
    /// <paramref name="ms"/> writes the value at once and stops the tick. Calling it with the target it is
    /// already moving to does nothing.</summary>
    public void To(double to, double ms)
    {
        if (_to == to)
            return;
        _to = to;

        if (ms <= 0)
        {
            Stop();
            Write(to);
            return;
        }

        _from = _value;
        _elapsed = 0;
        _seconds = ms / 1000.0;

        if (_ticking)
            return;
        _ticking = true;
        _clock.Restart();
        _last = 0;
        _tick ??= OnTick;
        CompositionTarget.Rendering += _tick;
        Services.Shell.FrameWatch.Began("chrome-fade");
    }

    /// <summary>Stops the tick and leaves the value where it is. Use this for a surface that is going away,
    /// because nobody is left to finish the move.</summary>
    public void Stop()
    {
        if (!_ticking)
            return;
        _ticking = false;
        _seconds = 0;
        _clock.Stop();
        if (_tick is not null)
            CompositionTarget.Rendering -= _tick;
        Services.Shell.FrameWatch.Ended("chrome-fade");
    }

    private void OnTick(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _last;
        _last = now;
        if (dt <= 0)
            return;
        dt = Math.Min(dt, 0.1);   // Cap the step, so one slow frame cannot finish the fade in one jump.

        _elapsed += dt;
        var t = Math.Clamp(_seconds > 0 ? _elapsed / _seconds : 1, 0, 1);
        Write(_from + (_to - _from) * Ease(t));

        if (t >= 1)
            Stop();
    }

    private void Write(double v)
    {
        _value = v;
        _write(v);
    }

    /// <summary>Quadratic ease-in-out. The same curve is used for every fade, whatever
    /// drives it.</summary>
    private static double Ease(double t) => t < 0.5 ? 2 * t * t : 1 - 2 * (1 - t) * (1 - t);
}

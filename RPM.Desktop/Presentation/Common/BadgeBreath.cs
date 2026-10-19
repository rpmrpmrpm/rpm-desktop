using System.Diagnostics;
using Microsoft.UI.Xaml;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The one clock that every working cloud badge uses to pulse. This covers the album wall's tiles and
/// the playlist rows above them.
///
/// <para>The clock is shared for two reasons. First, a tile and the row for the same album must not pulse
/// out of step. That happens when each view restarts its own stopwatch when its own work starts. Second,
/// the main cost of a pulse is the repaint, not the arithmetic. On Skia, each write marks the whole window
/// as changed. So two views that tick 50 ms apart repaint the window twice for each pulse.</para>
///
/// <para>This uses a timer, not <c>CompositionTarget.Rendering</c>. Subscribing to Rendering does more than
/// observe frames. While any handler is attached, each finished frame requests the next one. So the app
/// records and rasterises the whole window at the display's rate for as long as the handler stays attached,
/// even if nothing changed. That is 60 full repaints a second to dim a glyph along a curve that takes 1.4 s
/// to complete. Throttling inside the handler does not help, because the loop comes from the subscription,
/// not from the writes. Stopping the timer halts the loop at once. Under Uno, a looping storyboard does not
/// do that for an element that stays in the tree.</para>
/// </summary>
internal static class BadgeBreath
{
    /// <summary>How often the opacity is written. This is not the display's rate. Every write repaints
    /// the whole window, and the 1.4 second curve looks smooth with one update every 50 ms.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    private static readonly List<Action<double>> Breathers = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    /// <param name="breathe">Called with the badge opacity for the moment, every <see cref="Beat"/>,
    /// until the same delegate is passed to <see cref="Leave"/>. UI thread only.</param>
    public static void Join(Action<double> breathe)
    {
        if (Breathers.Contains(breathe))
            return;

        Breathers.Add(breathe);
        if (_timer is not null)
            return;

        // The first caller starts the clock. Later callers join the pulse that is already running, and do
        // not restart it under the badges that are already on screen.
        Clock.Restart();
        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>Stops calling <paramref name="breathe"/>. The caller sets its own badges back to
    /// <see cref="CloudBadgePulse.Rest"/>. This class does not know what they are.</summary>
    public static void Leave(Action<double> breathe)
    {
        if (!Breathers.Remove(breathe) || Breathers.Count > 0 || _timer is not { } timer)
            return;

        _timer = null;
        timer.Stop();
        timer.Tick -= OnTick;
        Clock.Stop();
    }

    private static void OnTick(object? sender, object e)
    {
        var opacity = CloudBadgePulse.At(Clock.Elapsed);

        // Iterates backwards. A view can remove itself from the list while its own call is running.
        // Going backwards means that this does not cause the next view in the list to be skipped.
        for (var i = Breathers.Count - 1; i >= 0; i--)
            Breathers[i](opacity);
    }
}

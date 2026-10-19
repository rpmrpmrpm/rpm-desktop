using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The per-frame tick the app's animations ride, and where it comes from on each head.
///
/// <para><b>The two heads mean different things by CompositionTarget.Rendering.</b> On Uno's Skia host
/// a subscriber does not just observe frames. It demands them. While any handler is attached, each
/// finished frame asks for the next one (see <see cref="FrameWatch"/>). On WinUI the event only reports
/// frames that the system compositor is already producing. A window with nothing moving in it produces
/// no frames. So an animation that is the first thing to move never gets a tick, and never starts.</para>
///
/// <para>On the WinUI head the record does not turn. The visualizers do turn. The difference is that
/// every GL stage has a watchdog timer of its own. Those timers were added for another reason, but they
/// keep the visualizers running on that head.</para>
///
/// <para><b>What this does.</b> On Skia, it subscribes to the event, as each call site did. On WinUI,
/// a timer for each subscription drives the handler instead, so an animation starts whether or not
/// anything else is already moving. It also keeps the <see cref="FrameWatch"/> count. Call sites would
/// otherwise each have to keep that count by hand, and any one of them could forget.</para>
///
/// <para>This is a repair, not the destination. The reason to be on WinUI at all is that the system
/// compositor can run an animation without the UI thread; a timer on the UI thread does not do that.
/// Moving these to composition animations is the real answer and is not done here.</para>
/// </summary>
internal static class FrameTick
{
    /// <summary>About sixty a second. This matches the frame rate of a compositor and the watchdog
    /// timers of the GL stages.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(16);

#if RPM_WINUI
    private static readonly Dictionary<EventHandler<object>, DispatcherQueueTimer> Pumps = [];
#endif

    /// <summary>Ticks delivered per subscription, so the pair of lines below can say whether an
    /// animation that started actually got any frames. Keyed by handler, like the pumps.</summary>
    private static readonly Dictionary<EventHandler<object>, int> Ticks = [];

#if !RPM_WINUI
    /// <summary>The wrapper actually subscribed on the Skia head, so it can be unsubscribed again.</summary>
    private static readonly Dictionary<EventHandler<object>, EventHandler<object>> Counted = [];
#endif

    private static void Count(EventHandler<object> handler)
    {
        if (Ticks.TryGetValue(handler, out var n))
            Ticks[handler] = n + 1;
    }

    /// <summary>Starts feeding <paramref name="handler"/> a tick per frame.</summary>
    /// <param name="name">What to call this animation on a stall line. See
    /// <see cref="FrameWatch"/>.</param>
    /// <param name="queue">The dispatcher the animation runs on. Null does no harm on the Skia head,
    /// because it is not used there.</param>
    public static void Add(string name, DispatcherQueue? queue, EventHandler<object> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        FrameWatch.Began(name);

        // Two lines per animation, start and stop. The second line says how many frames the animation
        // actually got. An animation that starts and stops with no frames is the WinUI fault this type
        // exists to fix. That fault is not visible any other way: nothing throws, and the handler is
        // simply never called. The lines are always written, because a diagnostic that has to be
        // switched on is off when the fault happens.
        Ticks[handler] = 0;
        RPM.Core.Diagnostics.Scope.Said("window", $"frames wanted by {name}");

#if RPM_WINUI
        if (queue is null || Pumps.ContainsKey(handler))
            return;

        var pump = queue.CreateTimer();
        pump.Interval = Beat;
        pump.IsRepeating = true;
        pump.Tick += (_, _) =>
        {
            Count(handler);
            handler(null, EventArgs.Empty);
        };
        Pumps[handler] = pump;      // Keeps the timer alive. A started timer needs a reference. See Ticker
        pump.Start();
#else
        Counted[handler] = (_, e) => { Count(handler); handler(null, e); };
        CompositionTarget.Rendering += Counted[handler];
#endif
    }

    /// <summary>Stops. Safe to call for a handler that is not subscribed.</summary>
    public static void Remove(string name, EventHandler<object> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        FrameWatch.Ended(name);

        Ticks.Remove(handler, out var got);
        RPM.Core.Diagnostics.Scope.Said("window", $"frames done for {name}: {got}");

#if RPM_WINUI
        if (Pumps.Remove(handler, out var pump))
            pump.Stop();
#else
        if (Counted.Remove(handler, out var wrapper))
            CompositionTarget.Rendering -= wrapper;
#endif
    }
}

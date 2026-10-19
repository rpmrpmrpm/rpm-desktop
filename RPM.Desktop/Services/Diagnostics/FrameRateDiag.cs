using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// Frames per second on the UI thread, written to the diagnostic ring once a second on the
/// <c>fps</c> channel. The watch is off unless <c>RPM_FPS_DIAG=1</c> is set. A Release build ignores
/// that setting (see <see cref="DevSwitch"/>).
///
/// <para>The app animates in two ways, so there are two counts. <c>frames</c> counts
/// <c>CompositionTarget.Rendering</c>: the UI-thread frames that were actually drawn. On WinUI that
/// event does not request frames, so the count is what the app produces by itself. <c>timer</c> counts
/// a 16 ms <see cref="DispatcherQueueTimer"/>, the same tick that <see cref="Shell.FrameTick"/> uses for
/// the app's timer animations, so it shows the rate those animations get. <c>ui cpu</c> is the UI
/// thread's processor time over the second, measured in 15 ms steps on Windows (see
/// <see cref="ScopeClock.ThreadCpuNanos"/>).
/// </para>
///
/// <para>A second with no frames and only timer ticks is not written, so an idle window adds
/// nothing.</para>
/// </summary>
internal static class FrameRateDiag
{
    private static int _frames;
    private static int _ticks;
    private static long _lastFrame;
    private static long _lastTick;
    private static double _worstFrameMs;
    private static double _worstTickMs;
    private static long _windowStart;
    private static long _cpuAtStart;

    // Keep a reference here. A DispatcherQueueTimer that nothing references is collected and stops ticking.
    private static DispatcherQueueTimer? _timer;

    public static void Install()
    {
        if (!DevSwitch.IsOn("RPM_FPS_DIAG"))
            return;

        _windowStart = Stopwatch.GetTimestamp();
        _cpuAtStart = ScopeClock.ThreadCpuNanos();

        CompositionTarget.Rendering += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (_lastFrame != 0)
                _worstFrameMs = Math.Max(_worstFrameMs, Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds);
            _lastFrame = now;
            _frames++;
        };

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Scope.Said("fps", "frame rate watch on");
    }

    private static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastTick != 0)
            _worstTickMs = Math.Max(_worstTickMs, Stopwatch.GetElapsedTime(_lastTick, now).TotalMilliseconds);
        _lastTick = now;
        _ticks++;

        var elapsed = Stopwatch.GetElapsedTime(_windowStart, now);
        if (elapsed < TimeSpan.FromSeconds(1))
            return;

        var cpu = ScopeClock.ThreadCpuNanos();
        var seconds = elapsed.TotalSeconds;
        Scope.Said("fps",
            $"frames {_frames / seconds:0}/s worst {_worstFrameMs:0}ms | timer {_ticks / seconds:0}/s worst {_worstTickMs:0}ms | ui cpu {(cpu - _cpuAtStart) / 1e6 / seconds:0}ms/s | {Shell.FrameWatch.Describe()}");

        _frames = 0;
        _ticks = 0;
        _worstFrameMs = 0;
        _worstTickMs = 0;
        _lastFrame = 0;
        _windowStart = now;
        _cpuAtStart = cpu;
    }
}

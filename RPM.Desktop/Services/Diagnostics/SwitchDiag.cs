using System.Diagnostics;
using Microsoft.UI.Dispatching;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Diagnostics;

/// <summary>A stopwatch that runs across one flip of the library's view switch: the wall, the album list,
/// or the data table. It runs until the flip has settled, so the cause of a slow flip can be found. It is
/// off unless <c>RPM_SWITCH_DIAG</c> is set in the environment, so an ordinary run performs only a boolean
/// test.
///
/// <para>The flip is not one piece of work. It is a chain of work across three different files, and each
/// link could be the cause on its own. So each link is timed where it happens, and the result is read as a
/// chain, not as one total:</para>
///
/// <list type="number">
/// <item><c>write</c>: the click writes the setting, and the setting is written to disk on the UI
/// thread, before anything else happens.</item>
/// <item><c>backdrop</c>: the setting raises the shell's global <c>Changed</c> event, which every
/// subscriber handles. The shell handles it by reading the window's background again.</item>
/// <item><c>crossfade</c>, <c>header</c>: the page's own response. The views swap, and the chrome above
/// them is laid out again.</item>
/// <item><c>favourites</c>, <c>feed</c>: the view that comes in is given the collection. First one query
/// for the stickers, then the records themselves.</item>
/// <item><c>settle</c>: the total that the other marks are measured against. It is how long the UI
/// thread stayed busy after the last mark returned. Work recorded here is the layout of the view being
/// shown. No clock in our own code can measure inside that layout, so see the worst frame next to
/// it.</item>
/// </list>
///
/// <para>The watch is a timer that is asked to tick every 8 ms. A tick that arrives 150 ms late did not
/// take 150 ms. It waited that long for a thread that somebody else was holding. When the ticks arrive on
/// time for a few in a row, the flip is over and the line is written.</para>
///
/// <para><b>The variable takes a threshold in milliseconds, not a switch.</b> That is what makes it safe to
/// leave on. The fault it is for is intermittent. A flip that stalls once in an evening cannot be caught by
/// a diagnostic that somebody must turn on beforehand, and a diagnostic that writes a line for every press
/// is turned off again quickly. So a number is a floor: nothing is reported for a flip faster than the
/// floor, and only a flip slow enough to notice leaves a line. The value <c>1</c> still means every flip,
/// for when the question is what a good flip costs.</para>
/// </summary>
internal static class SwitchDiag
{
    /// <summary>Whether the watch runs at all. It is on when the variable is 1, true, or a positive
    /// number.</summary>
    public static readonly bool On;

    /// <summary>How busy a flip has to have been to be reported. Zero reports every flip, which is what
    /// <c>1</c> and <c>true</c> give. A positive number is the floor. See the class summary.</summary>
    private static readonly double FloorMs;

    static SwitchDiag()
    {
        var said = RPM.Core.Diagnostics.DevSwitch.Read("RPM_SWITCH_DIAG");

        if (said is "1" or "true")
        {
            On = true;
            FloorMs = 0;
        }
        else if (double.TryParse(said, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var floor) && floor > 0)
        {
            On = true;
            FloorMs = floor;
        }
    }

    /// <summary>How often the thread is asked for a turn while a flip is in flight.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(8);

    /// <summary>A tick this far past its due time means the thread was held, not that the timer was late.
    /// Two frames at 60 frames per second.</summary>
    private const double HeldMs = 32;

    /// <summary>The number of on-time ticks needed before the flip counts as over. Waiting for half a
    /// second of frames would be too patient. One tick would be too trusting, because a layout pass often
    /// takes two.</summary>
    private const int QuietTicks = 6;

    /// <summary>The watch never waits forever. A flip that never goes quiet is itself a finding.
    ///
    /// <para>The limit is 15 seconds. A flip can hold the thread for several seconds, so a shorter limit
    /// would stop the measurement before the flip settled. The limit must be longer than the fault, or the
    /// measurement stops at its own limit and reports that instead.</para></summary>
    private const double GiveUpMs = 15000;

    private static readonly Stopwatch Clock = new();
    private static readonly List<(string Name, double At)> Marks = new();

    private static DispatcherQueueTimer? _watch;
    private static string _what = "";
    private static double _lastTick, _lastBusy, _worst;
    private static int _quiet;

    /// <summary>Called on the click. Starts the clock and the watch. A flip already in progress is
    /// abandoned, because the flip the user just asked for is the one being measured.</summary>
    public static void Begin(string what)
    {
        if (!On)
            return;

        _what = what;
        Marks.Clear();
        _worst = 0;
        _quiet = 0;
        Clock.Restart();
        _lastTick = _lastBusy = 0;

        _watch ??= DispatcherQueue.GetForCurrentThread()?.CreateTimer();
        if (_watch is null)
            return;
        _watch.Interval = Beat;
        _watch.IsRepeating = true;
        _watch.Tick -= OnTick;
        _watch.Tick += OnTick;
        _watch.Start();
    }

    /// <summary>One link of the chain has just finished.</summary>
    public static void Mark(string name)
    {
        if (On && Clock.IsRunning)
            Marks.Add((name, Clock.Elapsed.TotalMilliseconds));
    }

    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        var now = Clock.Elapsed.TotalMilliseconds;
        var gap = now - _lastTick;
        _lastTick = now;

        if (gap > HeldMs)
        {
            _worst = Math.Max(_worst, gap);
            _lastBusy = now;
            _quiet = 0;
        }
        else
        {
            _quiet++;
        }

        if (_quiet < QuietTicks && now < GiveUpMs)
            return;

        sender.Stop();
        Clock.Stop();
        Report(now);
    }

    private static void Report(double now)
    {
        var chain = new List<string>();
        var previous = 0d;
        foreach (var (name, at) in Marks)
        {
            chain.Add($"{name} {at - previous:0.#}");
            previous = at;
        }

        // The time left after the last mark: the layout and drawing of the view that was just shown. This
        // is the number that the diagnostic is meant to separate from the marks above it.
        var after = Math.Max(0, _lastBusy - previous);

        // A flip below the floor went normally, so there is nothing to report. A flip that never settled is
        // always reported, whatever the floor is. That is the finding, and it is not a measurement to
        // compare with the floor.
        if (_lastBusy < FloorMs && now < GiveUpMs)
            return;

        Say($"{_what}: busy {_lastBusy:0}ms (worst frame {_worst:0}ms) | "
            + string.Join(" | ", chain)
            + $" | layout+draw {after:0}"
            + (now >= GiveUpMs ? " | NEVER SETTLED" : ""));
    }

    private static void Say(string line) => Said.Line("switchdiag", line);
}

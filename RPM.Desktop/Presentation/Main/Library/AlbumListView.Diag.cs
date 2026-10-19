using System.Diagnostics;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The wall's stopwatch (see <see cref="WallDiag"/>), applied to the album LIST instead. The list stalls
/// where the wall does not, for a reason the wall does not have: a record on the wall is a picture, but a
/// record here is a picture and also a couple of hundred track lines that have to be built. It is off
/// unless <c>RPM_LIST_DIAG=1</c> is set in the environment.
///
/// <para>There are three questions. The third is the one this exists for:</para>
///
/// <list type="number">
/// <item>Is the UI thread being held, and for how long? The timer's own lateness is the size of the
/// longest stall the scroll had to sit through. It is reported the moment it happens, not averaged into
/// a summary. A stall of a fifth of a second disappears in an average over a whole second.</item>
/// <item>Is it our code? Every phase of reading a record is timed by name: the cover from the disk, the
/// colour from the cover, the bitmap decode, the tracks from the mirror, and the lines built from them. A
/// stall larger than the sum of these is not from our code. That fact is the finding.</item>
/// <item>Is it the tracklist arriving? This is the list's own suspect. It does not show up under any of the
/// above. <c>SetLines</c> hands two columns to two ItemsControls and returns. The several hundred elements
/// that answer for those lines are built in the layout pass afterwards. So the time from publishing a
/// record's lines to the tree being laid out again is measured directly, as <c>inflate</c>. The number of
/// lines published to earn it is recorded beside it.</item>
/// </list>
/// </summary>
internal static class ListDiag
{
    /// <summary>
    /// Which list is being watched. It is a name rather than a switch.
    ///
    /// <para>The counters below are static, so this watch can only be about one list at a time. With a plain
    /// on/off switch, whichever list loaded first would take it. Every reading would then describe that list
    /// while the user scrolled another. Without a name, a reading can describe a list nobody is scrolling,
    /// for example one that shows <c>offset=0 rows=0</c>. Naming the list makes this impossible. A list that is not
    /// named does not count, does not time, and does not start the beat.</para>
    ///
    /// <para><c>RPM_LIST_DIAG=1</c> still means the album list, so the measurements made before are made the
    /// same way now. <c>RPM_LIST_DIAG=tree</c> watches the playlists tree.</para>
    /// </summary>
    private static readonly string Which =
        (RPM.Core.Diagnostics.DevSwitch.Read("RPM_LIST_DIAG") ?? "").Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "albums" => "albums",
            "tree" => "tree",
            _ => "",
        };

    /// <summary>The album list is the one being watched. Every gate and every clock in that view reads this
    /// value, so watching another list costs it nothing.</summary>
    public static readonly bool On = Which == "albums";

    /// <summary>The playlists tree is the one being watched.</summary>
    public static readonly bool OnTree = Which == "tree";

    /// <summary>How often the list is sampled, and the interval a stall is measured against.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>The summary is written every this many beats. That is one second, so the counters read as
    /// "per second" without dividing.</summary>
    private const int BeatsPerReport = 20;

    /// <summary>A gap this far beyond the requested interval means the UI thread was held, not that the timer
    /// was late. It is three frames.</summary>
    private static readonly TimeSpan Spike = TimeSpan.FromMilliseconds(50);

    // ── what the list did ──

    private static int _realized, _cleared, _records, _layouts, _views;
    private static int _lines, _lineElements;

    /// <summary>One line element built or taken back out of the repeater's pool. Compared with the number of
    /// <c>lines published</c>, this answers the whole virtualization question. A much smaller number means the
    /// tracklists are realized only to the viewport. The same number means they are not.</summary>
    public static void LineRealized() => _lineElements++;
    private static double _inflateMs, _inflateWorst;
    private static int _inflates;

    /// <summary>A block was given a record. On this list, a read may also be queued at this moment, so the
    /// churn here includes the queue's churn.</summary>
    public static void Realized() => _realized++;

    public static void Cleared() => _cleared++;

    /// <summary>One record read from end to end.</summary>
    public static void Record() => _records++;

    public static void Laid()
    {
        _layouts++;

        // The inflate: the time from when a record's lines were published to the first layout pass after it.
        // Nothing of ours runs in between, because the reader is awaiting. So this measures the tree being
        // built and measured for those lines.
        if (_publishedAt <= 0)
            return;

        var ms = Clock.Elapsed.TotalMilliseconds - _publishedAt;
        _publishedAt = 0;
        _inflates++;
        _inflateMs += ms;
        if (ms > _inflateWorst)
            _inflateWorst = ms;
    }

    private static double _publishedAt;

    /// <summary>A record's tracklist was handed to its two columns. The count is what the layout pass after
    /// this has to answer for.</summary>
    public static void LinesPublished(int lines)
    {
        _lines += lines;
        _publishedAt = Clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>The scroller's view moved. The two spans answer the estimate question. An extent that changes
    /// while the offset stands still means the virtualizing panel is changing its mind about how long the shelf
    /// is. A fling cannot carry through that.</summary>
    public static void View(double offset, double extent)
    {
        _views++;
        _offLo = Math.Min(_offLo, offset);
        _offHi = Math.Max(_offHi, offset);
        _extLo = Math.Min(_extLo, extent);
        _extHi = Math.Max(_extHi, extent);
    }

    private static double _offLo = double.MaxValue, _offHi = double.MinValue;
    private static double _extLo = double.MaxValue, _extHi = double.MinValue;

    private static string Views() =>
        _views == 0
            ? "view still"
            : $"view {_views}×, offset {_offLo:0.#}..{_offHi:0.#}, extent {_extLo:0.#}..{_extHi:0.#}";

    // ── our own entry points ──

    private static readonly Dictionary<string, (int Calls, double Ms)> _spent = new();

    public static void Spent(string what, double ms)
    {
        var (calls, total) = _spent.TryGetValue(what, out var s) ? s : default;
        _spent[what] = (calls + 1, total + ms);
    }

    /// <summary>Runs <paramref name="work"/> with a clock on it, under the given name. It costs nothing when
    /// the diagnostic is off.</summary>
    public static T Time<T>(string what, Func<T> work)
    {
        if (!On)
            return work();

        var clock = Stopwatch.StartNew();
        try
        {
            return work();
        }
        finally
        {
            Spent(what, clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The awaited form. The clock runs across the await, so a phase that waits on a disk shows as
    /// that wait. That is the right number for a reader that is occupied by it. It is told apart from UI-thread
    /// time by whether it falls inside a stall.</summary>
    public static async Task<T> TimeAsync<T>(string what, Func<Task<T>> work)
    {
        if (!On)
            return await work();

        var clock = Stopwatch.StartNew();
        try
        {
            return await work();
        }
        finally
        {
            Spent(what, clock.Elapsed.TotalMilliseconds);
        }
    }

    private static string Spend()
    {
        if (_spent.Count == 0)
            return "nothing of ours ran";
        return string.Join(", ", _spent
            .OrderByDescending(p => p.Value.Ms)
            .Take(6)
            .Select(p => $"{p.Key} {p.Value.Ms:0.#}ms×{p.Value.Calls}"));
    }

    // ── one wheel notch, and which scroller took it ──

    /// <summary>One notch over a record's tracklist. It is reported the moment it happens, not counted into the
    /// summary. What matters is the order of a few notches, and an average over a second cannot show that.
    ///
    /// <para><paramref name="handledOnEntry"/> is the telling field. Uno's ScrollContentPresenter marks a notch
    /// as handled only when the offset it asked for is the offset it got. So <c>true</c> means it scrolled the
    /// tracklist and ran before this handler. <c>false</c> means either that it was clamped at an end, or that
    /// it has not run yet. The difference between those is the difference between a working tracklist and a
    /// frozen one. <paramref name="shelf"/>, across consecutive notches, shows whether the list behind it moved
    /// when it should not have.</para></summary>
    public static void Wheel(
        int delta, double room, double offset, bool handledOnEntry, double shelf, string took)
    {
        if (!On)
            return;
        Say($"wheel {delta:+#;-#;0} | tracklist room {room:0.#}, offset {offset:0.#}" +
            $" | handled on entry {handledOnEntry} | shelf at {shelf:0.#} | {took}");
    }

    /// <summary>One notch as the SHELF saw it. It is reported on the turn after the event, so the offset is
    /// where the shelf actually ended up. Read the two fields together. "handled yes" with a moved offset is the
    /// finding. It means that marking a notch as handled does not stop this scroller. So the fix must take the
    /// shelf's movement, not the notch's route.</summary>
    public static void ShelfWheel(bool handled, double before, double after)
    {
        if (!On)
            return;
        var moved = Math.Abs(after - before) > 0.5;
        Say($"  shelf saw the notch | handled {(handled ? "yes" : "NO")}" +
            $" | offset {before:0.#} -> {after:0.#} ({(moved ? "MOVED" : "still")})");
    }

    // ── the collector, asked rather than inferred ──

    private static TimeSpan _pauseAt, _pauseBeatAt;
    private static int _gen2At, _gen1At, _gen0At;

    private static string Collector()
    {
        var pause = (GC.GetTotalPauseDuration() - _pauseAt).TotalMilliseconds;
        var gen0 = GC.CollectionCount(0) - _gen0At;
        var gen1 = GC.CollectionCount(1) - _gen1At;
        var gen2 = GC.CollectionCount(2) - _gen2At;
        return $"gc {pause:0.#}ms in {gen0}/{gen1}/{gen2} (gen0/1/2), heap {GC.GetTotalMemory(false) / (1024 * 1024)}MB";
    }

    private static void MarkCollector()
    {
        _pauseAt = GC.GetTotalPauseDuration();
        _gen0At = GC.CollectionCount(0);
        _gen1At = GC.CollectionCount(1);
        _gen2At = GC.CollectionCount(2);
    }

    // ── the report ──

    private static string Since() =>
        $"records {_records}, lines {_lines} published, {_lineElements} realized" +
        $" | inflate {_inflates}×, worst {_inflateWorst:0.#}ms, {_inflateMs:0.#}ms total" +
        $" | churn {_realized} in, {_cleared} out" +
        $" | layouts {_layouts} | {Views()}" +
        $" | ours: {Spend()}";

    private static void Reset()
    {
        _worstGap = 0;
        _realized = _cleared = _records = _layouts = _views = _lines = _lineElements = 0;
        _inflates = 0;
        _inflateMs = _inflateWorst = 0;
        _offLo = _extLo = double.MaxValue;
        _offHi = _extHi = double.MinValue;
        _spent.Clear();
        MarkCollector();
    }

    /// <summary>Which list each line is about. It is printed on every line. A reading that does not say which
    /// list it describes is worthless.</summary>
    private static string _watching = "";

    private static void Say(string line) => RPM.Core.Diagnostics.Said.Line($"listdiag:{_watching}", line);

    private static DispatcherTimer? _timer;
    private static readonly Stopwatch Clock = new();
    private static double _lastTick, _worstGap;
    private static int _beats;
    private static Func<string>? _list;

    /// <summary>
    /// Starts the report for one named list. It takes the delegate that reads whatever that list can say about
    /// itself at the moment a line is written.
    /// </summary>
    /// <param name="name">Which list this is. It is matched against <see cref="Which"/>. A list that is not the
    /// one being watched returns without starting anything, so two lists on screen cannot report each other's
    /// numbers.</param>
    public static void Start(string name, Func<string> list)
    {
        if (name != Which || _timer is not null)
            return;

        _watching = name;
        _list = list;
        Clock.Restart();
        _lastTick = 0;
        MarkCollector();
        _pauseBeatAt = GC.GetTotalPauseDuration();
        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnBeat;
        _timer.Start();
        Say("on");
    }

    private static void OnBeat(object? sender, object e)
    {
        var now = Clock.Elapsed.TotalMilliseconds;
        var gap = now - _lastTick;
        _lastTick = now;
        if (gap > _worstGap)
            _worstGap = gap;

        var held = (GC.GetTotalPauseDuration() - _pauseBeatAt).TotalMilliseconds;
        _pauseBeatAt = GC.GetTotalPauseDuration();

        if (gap > Beat.TotalMilliseconds + Spike.TotalMilliseconds)
            Say($"STALL {gap:0}ms — gc held {held:0.#}ms of it | {Collector()} | {Since()} | {_list?.Invoke()}");

        if (++_beats < BeatsPerReport)
            return;
        _beats = 0;

        Say($"worst gap {_worstGap:0}ms | {Collector()} | {Since()} | {_list?.Invoke()}");
        Reset();
    }
}

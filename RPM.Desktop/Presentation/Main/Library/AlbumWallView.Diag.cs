using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// A stopwatch that runs against the wall while somebody scrolls it. It is used to find out what causes a
/// stutter. It is off unless <c>RPM_WALL_DIAG=1</c> is set in the environment, so a normal run pays only
/// for a boolean test.
///
/// <para>Three questions are asked. Each answer would explain a jittery wall. The numbers tell them
/// apart:</para>
///
/// <list type="number">
/// <item>Is the UI thread being held? A timer that is asked to tick every 100 ms and arrives at 340 ms was
/// not late. Something else was running. The worst arrival in each report is the size of the longest stall
/// the scroll had to sit through.</item>
/// <item>Is the cover fade the cause? Every pass of each tile's tween is counted, per second. A tween that
/// writes an opacity about twenty times over its 200 ms is a fade. One that writes it two thousand times is
/// a spin.</item>
/// <item>Do the rows agree on how tall they are? A virtualizing stack places rows that have not been
/// realized by using the ones that have. So two heights in the same wall mean an estimate that is wrong by
/// the difference, times the rows below. That is the wall shifting under a scroll. The distinct heights are
/// listed, and the extent is printed beside them. An extent that changes while nothing is added means the
/// estimate is being corrected under the reader.</item>
/// </list>
/// </summary>
internal static class WallDiag
{
    public static readonly bool On =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_WALL_DIAG") is "1" or "true";

    /// <summary>How often the wall is sampled. A stall is measured against this interval. Half a frame of
    /// slack on top of it is normal. Anything beyond that is something holding the thread.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>The summary is written every this many beats, which is one second. So the counters read
    /// as "per second" without any division.</summary>
    private const int BeatsPerReport = 20;

    /// <summary>A gap this far past the ask is not lateness. It means the thread was held, for about
    /// three frames. It is reported the moment it happens, not at the summary. A stall of a fifth of a
    /// second is invisible in a number averaged over a whole second. Averaged, it would look as if the
    /// wall was fine while it was visibly stuttering.</summary>
    private static readonly TimeSpan Spike = TimeSpan.FromMilliseconds(50);

    /// <summary>The fade clock's write count when the period opened. The report then shows how many opacity
    /// writes the last second cost, not the total for the run.</summary>
    private static long _fadeWritesAt;
    private static int _fadesPeak;

    private static int _realized;
    private static int _cleared;

    /// <summary>How many times each row index was realized in the period. This tells a row that is taken
    /// and given back again and again from a wall that is only being scrolled. A scroll realizes each row
    /// ONCE on its way past. The same index realized six times while nothing moves is a loop.</summary>
    private static readonly Dictionary<int, int> _flips = new();

    public static void Realized(int index)
    {
        _realized++;
        _flips[index] = _flips.TryGetValue(index, out var n) ? n + 1 : 1;
    }

    public static void Cleared(int index) => _cleared++;

    private static int _layouts;

    /// <summary>The tree was laid out again.</summary>
    public static void Laid() => _layouts++;

    // ── the collector ──
    //
    // This was checked because of what a stall looked like once the wall's own faults were removed. The
    // thread was gone for 565 ms, with no layout pass, no view change, no decode, and none of our
    // handlers running. Nothing was running. A thread that is not running anything and is not available
    // has been stopped. The runtime stops threads for one ordinary reason. The pause is taken from the
    // runtime, not guessed. If the stall and the pause are the same number, that is the answer. If they
    // are not, the collector is ruled out and the search goes on elsewhere.

    private static TimeSpan _pauseAt;
    private static TimeSpan _pauseBeatAt;
    private static int _gen2At, _gen1At, _gen0At;

    private static TimeSpan PauseSince() => GC.GetTotalPauseDuration() - _pauseAt;

    private static string Collector()
    {
        var pause = PauseSince().TotalMilliseconds;
        var gen0 = GC.CollectionCount(0) - _gen0At;
        var gen1 = GC.CollectionCount(1) - _gen1At;
        var gen2 = GC.CollectionCount(2) - _gen2At;
        return $"gc {pause:0.#}ms in {gen0}/{gen1}/{gen2} (gen0/1/2), heap {GC.GetTotalMemory(false) / (1024 * 1024)}MB";
    }

    /// <summary>
    /// What the app allocated over the stall, and what the last collection had to move.
    ///
    /// <para><b>The number that the pause on its own cannot give.</b> Fifteen collections that hold the
    /// thread for ten seconds can come from two very different faults. One is an app allocating faster
    /// than the collector can keep up. Then the fix is in the allocating code, not in the collector. The
    /// other is an app that allocates almost nothing, whose collections are slow anyway. That means a
    /// graph that must be walked and promoted on every pass, and the fix is somewhere else. Both print as
    /// "gc held ten seconds of it", and that number alone cannot decide between them. A rate and a
    /// promotion figure decide it in one line.</para>
    ///
    /// <para>Read <b>promoted</b> first. A gen0 or gen1 collection should be cheap, because almost
    /// nothing survives it. A gen1 that promoted two hundred megabytes did not do the work of a young
    /// collection, whatever its name. That means a graph is being built and kept, rather than garbage
    /// being made and dropped.</para>
    ///
    /// <para>This is asked only when a stall is reported, so its cost is not paid on ordinary beats.</para>
    /// </summary>
    private static string Churned(double made, double gap)
    {
        var mb = made / (1024.0 * 1024.0);
        var rate = gap <= 1 ? 0 : mb / (gap / 1000.0);

        var info = GC.GetGCMemoryInfo();
        var sizes = new StringBuilder();
        var gens = info.GenerationInfo;
        for (var i = 0; i < gens.Length && i < 4; i++)
        {
            if (sizes.Length > 0)
                sizes.Append('/');
            sizes.Append((gens[i].SizeAfterBytes / (1024 * 1024)).ToString());
        }

        // The last collection's own pause. This shows whether the total is spread over every collection,
        // or is one of them. Seventeen collections holding fifteen seconds average 900 ms each, which may
        // be true of none of them. A blocking gen2 taking fourteen seconds, with sixteen ordinary gen0s
        // either side, gives the same total. It needs a different fix.
        var last = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0;

        // Two counts name a collection that is slow for a reason other than its size. PINNED objects
        // cannot be moved. A heap with many of them is compacted around them, so the work no longer grows
        // only with what survives. FINALIZERS pending are objects the collector has found, cannot free
        // yet, and must keep alive for another pass. A queue that does not drain means a collection that
        // repeats its work every time.
        return $"alloc {mb:0}MB ({rate:0}MB/s), last gc gen{info.Generation} {last:0.#}ms"
             + $"{(info.Concurrent ? " background" : "")}{(info.Compacted ? " compacting" : "")}"
             + $", promoted {info.PromotedBytes / (1024.0 * 1024.0):0}MB"
             + $", pinned {info.PinnedObjectsCount}, finalizers {info.FinalizationPendingCount}"
             + $", frag {info.FragmentedBytes / (1024 * 1024)}MB, left {sizes}MB (gen0/1/2/loh)";
    }

    private static void MarkCollector()
    {
        _pauseAt = GC.GetTotalPauseDuration();
        _gen0At = GC.CollectionCount(0);
        _gen1At = GC.CollectionCount(1);
        _gen2At = GC.CollectionCount(2);
    }

    private static int _views;
    private static double _offLo = double.MaxValue, _offHi = double.MinValue;
    private static double _extLo = double.MaxValue, _extHi = double.MinValue;

    /// <summary>
    /// The scroller reports that its view changed. The two spans kept here answer the main question about
    /// a row that is realized and cleared over and over at a standstill.
    ///
    /// <para>If the OFFSET span is a hair wide, the wall is being nudged. Momentum, a pin, or something
    /// writing a position can do that. If the EXTENT span is wide while the offset stands still, nothing
    /// moved the wall. The repeater changed its estimate of how long the collection is. That moves every
    /// unrealized row, which changes what is in the realization window, which realizes a row. Its arrival
    /// then corrects the estimate again. This is a loop that feeds itself. It does not need a finger on
    /// the wheel to keep going.</para>
    /// </summary>
    public static void View(double offset, double extent)
    {
        _views++;
        _offLo = Math.Min(_offLo, offset);
        _offHi = Math.Max(_offHi, offset);
        _extLo = Math.Min(_extLo, extent);
        _extHi = Math.Max(_extHi, extent);
    }

    private static string Views() =>
        _views == 0
            ? "view still"
            : $"view {_views}×, offset {_offLo:0.#}..{_offHi:0.#}, extent {_extLo:0.#}..{_extHi:0.#}";

    private static int _decodes;
    private static double _decodeMs;
    private static double _decodeWorst;

    /// <summary>One cover decoded, and what it cost. A BitmapImage can only be decoded on the UI thread,
    /// so this is time the scroll does not have. It is the largest piece of work left that a newly
    /// realized row can cause. Six columns at 384 px means six of these per row.</summary>
    public static void Decode(double ms)
    {
        _decodes++;
        _decodeMs += ms;
        if (ms > _decodeWorst)
            _decodeWorst = ms;
    }

    /// <summary>What each named piece of the wall's own code cost since the period opened. A stall is
    /// worth more than the sum of these, or the time is not ours. For example, a 113 ms stall with 22 ms
    /// of decode under it means the time is going somewhere nobody has measured. The only way to find
    /// which of our own entry points it is, or that it is none of them, is to time them all.</summary>
    private static readonly Dictionary<string, (int Calls, double Ms)> _spent = new();

    public static void Spent(string what, double ms)
    {
        var (calls, total) = _spent.TryGetValue(what, out var s) ? s : default;
        _spent[what] = (calls + 1, total + ms);
    }

    /// <summary>Runs <paramref name="work"/> with a clock on it, under the given name. It costs nothing
    /// when the diagnostic is off.</summary>
    public static void Time(string what, Action work)
    {
        if (!On)
        {
            work();
            return;
        }
        var clock = Stopwatch.StartNew();
        try
        {
            work();
        }
        finally
        {
            Spent(what, clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The costliest of our own entry points in the period, worst first.</summary>
    private static string Spend()
    {
        if (_spent.Count == 0)
            return "nothing of ours ran";
        return string.Join(", ", _spent
            .OrderByDescending(p => p.Value.Ms)
            .Take(5)
            .Select(p => $"{p.Key} {p.Value.Ms:0.#}ms×{p.Value.Calls}"));
    }

    /// <summary>Everything the wall has done since the period opened, in one clause. The same words
    /// appear on a stall line and on a summary, so the two can be read against each other.</summary>
    private static string Since() =>
        $"fades {_fadesPeak} at once, {Common.SharpFade.Writes - _fadeWritesAt} writes" +
        $" | decodes {_decodes}, worst {_decodeWorst:0.#}ms, {_decodeMs:0.#}ms total" +
        $" | churn {_realized} realized, {_cleared} cleared {Churn()}" +
        $" | layouts {_layouts} | {Views()}" +
        $" | ours: {Spend()}";

    private static void Reset()
    {
        _worstGap = 0;
        _fadeWritesAt = Common.SharpFade.Writes;
        _fadesPeak = Common.SharpFade.Count;
        _realized = 0;
        _cleared = 0;
        _decodes = 0;
        _decodeMs = 0;
        _decodeWorst = 0;
        _layouts = 0;
        _views = 0;
        _offLo = _extLo = double.MaxValue;
        _offHi = _extHi = double.MinValue;
        _flips.Clear();
        _spent.Clear();
        MarkCollector();
    }

    /// <summary>The indices realized more than once in the period, worst first. The list is empty on an
    /// honest scroll.</summary>
    private static string Churn()
    {
        var repeats = _flips.Where(p => p.Value > 1).OrderByDescending(p => p.Value).Take(4).ToList();
        return repeats.Count == 0
            ? ""
            : $"(row {string.Join(", ", repeats.Select(p => $"{p.Key}×{p.Value}"))})";
    }

    /// <summary>A feed of the whole shelf, and what it cost. It is reported separately, because it is the
    /// one piece of work here that is O(the collection) and can land in the middle of a scroll.</summary>
    public static void Feed(int albums, double ms) =>
        Say($"fed {albums} albums in {ms:0.#}ms");

    /// <summary>One step of opening a record, reported as it happens. The glide and the heal are two
    /// different movers of the same wall. They land within a few frames of each other. So the only way to
    /// tell whose move the reader saw is to have both on the same clock.</summary>
    public static void Open(string step) => Say($"open: {step}");

    /// <summary>Writes to the diagnostic ring, and to the debug channel only where there is no ring. See
    /// <see cref="RPM.Core.Diagnostics.Said"/>. This writes a line per stall, and under the row counters
    /// a great many more. Each line that reaches an attached debugger is a round trip that stops the
    /// process, which is the very thing being measured. Read them with <c>rpmscope log --channel
    /// walldiag</c>.</summary>
    private static void Say(string line) => RPM.Core.Diagnostics.Said.Line("walldiag", line);

    // ── the watchdog, on a thread of its own ──
    //
    // Everything above is measured BY the UI thread, which can only report what it did. The stall worth
    // chasing is the one where it did nothing at all. A stall of about 1.5 seconds, with no collection,
    // no layout of ours, no realize and nothing timed, means a thread that is not running anything and is
    // not available. Such a thread is waiting on something. Nothing it could report afterwards would say
    // what.
    //
    // So something that is not blocked must look while it is happening. This samples, every few
    // milliseconds, whether the library's two connections are busy. See LibraryDatabase.ReadGateBusy.
    // Reads serialize against each other on one connection. So a long query on a sweep or a sync holds
    // every other reader behind it, including the UI thread. That is a stall with no frames of its own to
    // show. The count says whether that is what happened, or whether the cause is somewhere else. Both
    // answers are worth knowing.

    /// <summary>
    /// Whether the library's connections are busy this instant. The shell sets this, because the
    /// diagnostic should not resolve services. It is null before a library is open.
    /// </summary>
    /// <remarks>
    /// <b>Both databases are sampled.</b> The wall's collection may be a borrowed shelf. The drawn
    /// collection is <c>Shelf</c>, and on a borrowed shelf that is the peer's file. The user's own
    /// database is where every write in the app goes, and where the generated lists are cut. Sampling
    /// only <c>Shelf</c> would report the peer's connections and stay silent about the ones under
    /// suspicion. A diagnostic that answers a question nobody asked is worse than none, because it gets
    /// believed.
    /// </remarks>
    public static Func<GateSample>? Gates { get; set; }

    /// <summary>What the two databases' connections were doing at one instant.</summary>
    /// <param name="OwnRead">The user's own mirror, <c>LibraryService.Db</c>. Everything that writes uses
    /// it. The mood cut is taken from it, and the catalogue served to a peer is built from it.</param>
    /// <param name="ShelfRead">The collection on screen, <c>LibraryService.Shelf</c>. It is the same
    /// object as the own one until something is borrowed.</param>
    /// <param name="Same">Whether the two are one database. The line then says it only once.</param>
    /// <remarks>
    /// <b>A <see cref="RPM.Core.Library.Gate.Look"/> is used, not a boolean.</b> A boolean can only say
    /// that a connection was occupied at a sample. It cannot say by whom, for how long, or what will
    /// happen next. A Look records who took the connection and how long ago. So a hold that has not ENDED
    /// yet still has a reading. During a freeze, that is the only kind of reading there is.
    /// </remarks>
    public readonly record struct GateSample(
        RPM.Core.Library.Gate.Look OwnRead,
        RPM.Core.Library.Gate.Look OwnWrite,
        RPM.Core.Library.Gate.Look ShelfRead,
        RPM.Core.Library.Gate.Look ShelfWrite,
        bool Same);

    // ── the second witness ──
    //
    // The stall above is a gap between two ticks of a DispatcherTimer. That can mean one of two very
    // different things, and a timer alone cannot tell them apart. Either the UI thread was not AVAILABLE,
    // or the timer was not DELIVERED. A sample taken during a stall of about 700 ms showed the main
    // thread parked in the run loop's mach port wait. It was asleep, waiting for a message, not busy. So
    // the difference matters, and no clock running ON that thread can settle it.
    //
    // So the watchdog, which is not that thread, posts a plain action to the queue and times how long it
    // takes to run. A stall with a long reach beside it means the thread was unavailable. A stall with a
    // short reach means the thread was free the whole time and was not woken. That is a different fault
    // in a different place.

    /// <summary>The UI queue, used for the reach. It is set by whichever wall started the watch.</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? Ui { get; set; }

    /// <summary>The worst time a plain action took to reach the UI thread in this period, and whether one
    /// is outstanding now.</summary>
    private static double _reachWorst;
    private static double _reachSentAt = double.NaN;

    /// <summary>Sends one ping, unless the last is still outstanding. Only one is in flight at a time. A
    /// queue of pings would measure the pings queueing, not the thread.</summary>
    private static void Reach()
    {
        if (Ui is not { } ui || !double.IsNaN(_reachSentAt))
            return;

        var sent = Clock.Elapsed.TotalMilliseconds;
        _reachSentAt = sent;
        if (!ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                var took = Clock.Elapsed.TotalMilliseconds - sent;
                if (took > _reachWorst)
                    _reachWorst = took;
                _reachSentAt = double.NaN;
            }))
        {
            _reachSentAt = double.NaN;
        }
    }

    /// <summary>How long the queue took to reach the thread, at worst in the period. Where a ping is
    /// still outstanding, this also gives how long it has been out. That is the only honest reading
    /// during a stall.</summary>
    private static string ReachLine()
    {
        var outstanding = double.IsNaN(_reachSentAt) ? 0 : Clock.Elapsed.TotalMilliseconds - _reachSentAt;
        var worst = Math.Max(_reachWorst, outstanding);
        _reachWorst = 0;
        return Ui is null ? "reach unwatched" : $"reach {worst:0}ms{(outstanding > 0 ? " (still out)" : "")}";
    }

    private static readonly TimeSpan Sniff = TimeSpan.FromMilliseconds(10);
    private static int _sniffs, _split;
    private static Thread? _watchdog;

    /// <summary>
    /// What one connection was doing over a period: for how many of the samples it was occupied, and the
    /// longest hold seen, with the name of whoever had it.
    ///
    /// <para>The share says whether a stall was caused by the database. The name says what to fix. The
    /// WORST reading is kept, not the last one. A scroll makes hundreds of short holds, and only one of
    /// them may matter. Averaged in with the rest, it would disappear.</para>
    /// </summary>
    private sealed class Occupancy
    {
        private readonly object _own = new();
        private int _busy;
        private double _worstMs;
        private string? _worstBy;
        private int _queued;

        public void Saw(RPM.Core.Library.Gate.Look look)
        {
            if (!look.Busy)
                return;

            lock (_own)
            {
                _busy++;
                if (look.Waiting > _queued)
                    _queued = look.Waiting;
                if (look.ForMs <= _worstMs)
                    return;
                _worstMs = look.ForMs;
                _worstBy = look.By;
            }
        }

        public (int Busy, double WorstMs, string? By, int Queued) TakeAndClear()
        {
            lock (_own)
            {
                var was = (_busy, _worstMs, _worstBy, _queued);
                _busy = 0;
                _worstMs = 0;
                _worstBy = null;
                _queued = 0;
                return was;
            }
        }
    }

    private static readonly Occupancy _ownRead = new(), _ownWrite = new();
    private static readonly Occupancy _shelfRead = new(), _shelfWrite = new();

    private static void Watch()
    {
        _watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(Sniff);
                Reach();
                if (Gates is not { } ask)
                    continue;

                try
                {
                    var seen = ask();
                    Interlocked.Increment(ref _sniffs);
                    _ownRead.Saw(seen.OwnRead);
                    _ownWrite.Saw(seen.OwnWrite);

                    // Counted apart only when the two databases ARE different. When nothing is borrowed,
                    // the report shows one pair of numbers. Two identical pairs would look like a bug in
                    // the diagnostic, not the same database twice.
                    if (!seen.Same)
                    {
                        Interlocked.Increment(ref _split);
                        _shelfRead.Saw(seen.ShelfRead);
                        _shelfWrite.Saw(seen.ShelfWrite);
                    }
                }
                catch
                {
                    // A library closing under the sampler is not something a diagnostic reports on.
                }
            }
        })
        {
            IsBackground = true,
            Name = "walldiag-watchdog",
            // Below everything it is watching. A sampler that competed for a core with the thread it is
            // timing would be part of what it measured.
            Priority = ThreadPriority.BelowNormal,
        };
        _watchdog.Start();
    }

    /// <summary>What the connections were doing over the period, as a share of the samples taken. The
    /// read number is the one to compare with a stall. A UI thread held for a second and a half, while
    /// the reading connection was occupied for a second and a half of it, was not slow. It was
    /// queued.</summary>
    private static string GateLine()
    {
        var sniffs = Interlocked.Exchange(ref _sniffs, 0);
        var split = Interlocked.Exchange(ref _split, 0);
        var read = _ownRead.TakeAndClear();
        var write = _ownWrite.TakeAndClear();
        var shelfRead = _shelfRead.TakeAndClear();
        var shelfWrite = _shelfWrite.TakeAndClear();

        if (sniffs == 0)
            return Gates is null ? "gates unwatched" : "gates not sampled";

        var own = $"read {Share(read, sniffs)} write {Share(write, sniffs)}";

        if (split == 0)
            return $"gates {own} of {sniffs}";

        // A borrowed shelf is on screen. Which of the two is busy is the whole question. The drawn
        // collection being busy means the page is reading what it shows. The OWN database being busy
        // while a borrowed shelf is on screen means something else in the app holds the connection that
        // the page also needs for marks, favourites and play counts.
        return $"gates own {own} | shelf read {Share(shelfRead, split)} write {Share(shelfWrite, split)}"
             + $" of {sniffs}";
    }

    /// <summary>One connection's share of the samples, with the name of its longest hold. The name is the
    /// useful part. A percentage says the database caused the stall. A name says which piece of work it
    /// was.</summary>
    private static string Share((int Busy, double WorstMs, string? By, int Queued) seen, int of)
    {
        if (of == 0 || seen.Busy == 0)
            return "0%";

        var line = $"{seen.Busy * 100 / of}% ({seen.Busy * Sniff.TotalMilliseconds:0}ms";
        if (seen.By is { } by)
            line += $", worst {seen.WorstMs:0}ms in {by}";
        if (seen.Queued > 0)
            line += $", {seen.Queued} waiting";
        return line + ")";
    }

    /// <summary>
    /// How hard the process worked over the period, compared with how long the period was.
    ///
    /// <para>This settles the question for a stall with nothing in it. A UI thread held for a second,
    /// with no collection, no lock of ours, and nothing of ours running, is doing one of two things. The
    /// fixes for the two are opposite. It is either WAITING on something, such as a handle, a native lock
    /// or a device. Or it is BUSY in code with no managed frames to show. On this renderer, that means
    /// Skia. Waiting costs no processor time. Busy costs a core's worth. So the two are far apart in the
    /// numbers, and no argument is needed.</para>
    ///
    /// <para>The measure covers the whole process, not one thread, because .NET does not give a
    /// per-thread reading on macOS. So read it against the rest of the line. A stall at four cores'
    /// worth, with the models running, is background work. A stall at about exactly one core, with the
    /// gates idle and nothing of ours timed, is one thread spinning. There is only one thread this could
    /// be.</para>
    ///
    /// <para><b>Read over the stall itself and nothing else.</b> The processor is sampled on every beat,
    /// the same way the collector's pause is. So what a stall reports is the work done between the beat
    /// before it and the beat that arrived late. Those two numbers are the gap, so the division gives the
    /// answer.</para>
    /// </summary>
    private static string Effort(double gap, double burned)
        => double.IsNaN(burned) ? "cpu unreadable"
         : gap <= 1 ? "cpu ?"
         : $"cpu {burned:0}ms over {gap:0}ms = {burned / gap:0.00} cores";

    /// <summary>The processor time this process has used, in milliseconds, or NaN where it cannot be
    /// read. It is asked once per beat.</summary>
    private static double Cpu()
    {
        try
        {
            Me.Refresh();
            return Me.TotalProcessorTime.TotalMilliseconds;
        }
        catch
        {
            return double.NaN;
        }
    }

    private static readonly Process Me = Process.GetCurrentProcess();
    private static double _cpuAt = double.NaN;
    private static long _allocBeatAt;

    private static DispatcherTimer? _timer;
    private static readonly Stopwatch Clock = new();
    private static double _lastTick;
    private static double _worstGap;
    private static int _beats;
    private static Func<string>? _wall;

    // ── who moved the wall ──
    //
    // Three things can carry the reader off the row they were on. They are told apart by numbers, not by
    // argument. It helps to have all three on the same line.
    //
    //   1. The HEAL. CheckAnchor decides the repeater has lost its place and re-seats it. That is a
    //      ChangeView to zero, by design. This is the only one that jumps on purpose.
    //   2. A CLAMP. The estimate shrank, so the extent shrank. A scroller cannot stand at an offset past
    //      its own end, so it is pulled back to the new end. Nothing here asked for that. No anchor can
    //      hold against it, because the anchor's own correction is a ChangeView, and that also clamps.
    //   3. An ORIGIN SHIFT. The row height changed, so every row moved while the offset stood still. This
    //      is answered by AlbumWallLayout, which places rows from two remembered heights rather than a
    //      mean. It is also answered by ReshapeHoldingReader, which walks the offset with the reader's
    //      own row when a drawer is attached or detached.
    //
    // A backwards move is reported with the extent on either side of it, and with what the heal was doing
    // at the time. So the line says which of the three it was.

    private static double _lastOffset = -1, _lastExtent = -1;
    private static double _healAt = double.NegativeInfinity;

    /// <summary>The wall healed. The time is stamped, not counted. What matters is whether a heal landed
    /// in the same moment as a jump.</summary>
    public static void Healed(string detail)
    {
        _healAt = Clock.Elapsed.TotalMilliseconds;
        if (On)
            Say($"HEAL {detail}");
        else
            Scope.Said("wall", $"HEAL {detail}");   // the ring always, the terminal only when asked
    }

    /// <summary>Checks every settled view, and watches for the wall going backwards by more than half a
    /// screen. The reader never asks for that by scrolling down.</summary>
    public static void Move(double offset, double extent, double viewport)
    {
        if (!On)
            return;
        var wasOffset = _lastOffset;
        var wasExtent = _lastExtent;
        _lastOffset = offset;
        _lastExtent = extent;
        if (wasOffset < 0 || viewport <= 1)
            return;
        var fell = wasOffset - offset;
        if (fell < viewport / 2)
            return;

        var sinceHeal = Clock.Elapsed.TotalMilliseconds - _healAt;
        var end = Math.Max(0, extent - viewport);
        var clamped = offset >= end - 1 && wasOffset > end + 1;
        Say($"JUMP UP {wasOffset:0} → {offset:0} (fell {fell:0})"
            + $" | extent {wasExtent:0} → {extent:0}, end now {end:0}{(clamped ? " — AT THE END, this is a clamp" : "")}"
            + $" | heal {(double.IsInfinity(sinceHeal) ? "never" : $"{sinceHeal:0}ms ago")}");
    }

    /// <summary>Starts the report. It takes a delegate that reads what the wall can say about itself when
    /// the report is written: its extent and the heights of the rows it holds.</summary>
    public static void Start(Func<string> wall)
    {
        if (!On)
            return;

        // Whichever wall asked last is the one reported on. Two walls install this, the covers and the
        // genres. Only one timer runs, so a second caller must still replace the reporting delegate.
        // Otherwise the lines would describe the wrong wall. The counters are shared and always correct.
        // Only the part of the line that names the wall depends on this delegate.
        _wall = wall;

        if (_timer is not null)
            return;
        Clock.Restart();
        _lastTick = 0;
        MarkCollector();
        _pauseBeatAt = GC.GetTotalPauseDuration();
        _cpuAt = Cpu();
        _allocBeatAt = GC.GetTotalAllocatedBytes(precise: false);
        Watch();
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

        // Sampled on every beat, not only when the report is written. A burst of fades between two
        // reports is still seen. The peak is the number that matters, not whatever was live at the end.
        if (Common.SharpFade.Count > _fadesPeak)
            _fadesPeak = Common.SharpFade.Count;

        // How much of THIS beat's gap the collector accounts for. This is kept per beat, not per report,
        // because the comparison is made per beat. A 565 ms gap with 560 ms of pause under it is a
        // collection. The same gap with no pause under it is something else.
        var held = (GC.GetTotalPauseDuration() - _pauseBeatAt).TotalMilliseconds;
        _pauseBeatAt = GC.GetTotalPauseDuration();

        // The processor time the same beat cost. It is kept beside the pause, for the same reason. A
        // stall is worth reading only over its own length. See Effort.
        var cpu = Cpu();
        var burned = cpu - _cpuAt;
        _cpuAt = cpu;

        // What was allocated over the beat. It is sampled on the beat, for the same reason as the other
        // two. A rate is only meaningful over a period that was chosen. The period that means something
        // is the stall itself. See Churned.
        var alloc = GC.GetTotalAllocatedBytes(precise: false);
        var made = alloc - _allocBeatAt;
        _allocBeatAt = alloc;

        // The thread was held. It is reported now, while everything that was going on is still in hand.
        if (gap > Beat.TotalMilliseconds + Spike.TotalMilliseconds)
            Say($"STALL {gap:0}ms — gc held {held:0.#}ms of it | {ReachLine()} | {Services.Shell.NetDiag.Line()} | {Services.Shell.ExceptionTrace.Line()} | {Effort(gap, burned)} | {GateLine()} | {RPM.Core.Library.Gate.Line()} | {Collector()} | {Churned(made, gap)} | {Since()} | {_wall?.Invoke()}");

        if (++_beats < BeatsPerReport)
            return;
        _beats = 0;

        // A period in which the wall did nothing is not worth a line. The summary is meant to be read
        // beside a scroll. A console full of idle reports is how the scroll gets missed.
        if (_realized > 0 || _cleared > 0 || _decodes > 0 || _fadesPeak > 0 || _layouts > 0
            || PauseSince() > TimeSpan.Zero
            || _worstGap > Beat.TotalMilliseconds + Spike.TotalMilliseconds)
            Say($"stall {_worstGap:0}ms (asked {Beat.TotalMilliseconds:0}) | {Collector()} | {Since()} | {_wall?.Invoke()}");

        Reset();
    }
}

public sealed partial class AlbumWallView
{
    /// <summary>What the wall can say about its own placement: how many rows it holds, what heights they
    /// have, and the extent the scroller believes in. It is read when a report is written, and never
    /// otherwise.</summary>
    private string DiagState()
    {
        var heights = new SortedSet<double>();
        var held = 0;
        var count = VisualTreeHelper.GetChildrenCount(RowsRepeater);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(RowsRepeater, i) is not FrameworkElement child)
                continue;
            if (RowsRepeater.GetElementIndex(child) < 0)
                continue;
            held++;
            heights.Add(Math.Round(child.ActualHeight, 2));
        }

        return $"rows {held}/{_rows.Count} held, heights [{string.Join(", ", heights)}]" +
               $" | extent {Scroller.ExtentHeight:0.#} at {Scroller.VerticalOffset:0.#}" +
               $" | art {_metrics.Art:0.#} × {_columns} cols, decode {CoverDecodeEdge()}";
    }
}

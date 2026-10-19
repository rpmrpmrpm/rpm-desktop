using Microsoft.UI.Dispatching;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// One turn of the drawing thread, as it arrived.
/// </summary>
/// <param name="LateMs">How long the turn took to arrive. This is the drawing thread's unavailability,
/// whatever caused it.</param>
/// <param name="CpuNanos">The drawing thread's own processor counter when the turn arrived, or
/// <see cref="ScopeClock.Unknown"/>. It is passed on raw, not subtracted here. The subtraction belongs to
/// whoever keeps a baseline, and more than one part of the app does.</param>
public readonly record struct Turn(double LateMs, long CpuNanos);

/// <summary>
/// How available the thread that draws is. It is measured, not assumed, and it can be read from any thread
/// at any moment.
///
/// <para><b>What it is.</b> A background thread posts a stamped item to the dispatcher, waits for it to
/// arrive, and then posts another. When the item runs, it says how long it took to get there. That number
/// is the drawing thread's unavailability, whatever caused it: a long layout, a native mutex, a collection,
/// or a database gate that somebody else is holding. It does not need to be attributed to anything to be
/// true. A thread that is busy or parked cannot report how long it was busy or parked, because the report
/// is more work for the thread that is not doing any work.</para>
///
/// <para><b>Why this is one object and not three measurements.</b> Three parts of the app need this
/// reading: the ring (<see cref="Services.Diagnostics.ScopeShell"/>), the splash (<see
/// cref="Presentation.Shell.RevealGate"/>) and the governor (<see cref="Governor"/>). Each of them used to
/// measure it for itself, with two dispatcher timers and a watch thread. All of them asked the same thread
/// the same question at about the same rate, and all of them competed with the thread they were measuring.
/// A diagnostic has twice made the app unusable. So there is one watcher and three
/// readers.</para>
///
/// <para><b>Not <c>CompositionTarget.Rendering</c>.</b> Subscribing to it does not observe frames. It
/// DEMANDS them. See <see cref="FrameWatch"/>. An instrument that makes the app draw in order to watch it
/// draw is measuring itself.</para>
///
/// <para><b>What arrives out of order here.</b> Four things happen, each on its own clock: the watch thread
/// posts an item (on its own sleep), the item lands (on the drawing thread, which is the whole point), a
/// reader asks for a value (on any thread, at any moment, including during a stall), and <see cref="Stop"/>
/// runs at shutdown. The readings are plain fields, written with <see cref="Volatile"/>, and they are never
/// read as a set. So a reader that catches the moment between two of the writes sees one number from this
/// turn and one from the last. That reading is half a beat old, but it is not torn. This is deliberate. A
/// lock here would let the writer make a reader wait, and the readers include a background pass that asks
/// whether it may run. That check must never block.</para>
/// </summary>
public sealed class UiPulse
{
    /// <summary>How often the thread is asked for a turn. This is about one frame at sixty per
    /// second.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// A turn that arrives later than this means the thread was held, not that the timer was late.
    ///
    /// <para>This is two frames at sixty per second. It is the one definition, and everything that asks
    /// whether the interface is free uses it. The splash, the governor and the view-switch diagnostic each
    /// had their own copy of this number, and none of the three said why it was thirty-two.</para>
    /// </summary>
    public const double HeldMs = 32;

    /// <summary>
    /// How many turns must arrive on time before the thread is called free.
    ///
    /// <para>Six turns is about a tenth of a second of calm. That is long enough that the gap BETWEEN two
    /// layout passes does not look like the end of them, and short enough that waiting for it is not a
    /// delay itself.</para>
    /// </summary>
    public const int CalmTurns = 6;

    /// <summary>The thread being watched, or null in a host that has none, such as a test or a headless
    /// run. See
    /// <see cref="Governor.Unattended"/>. A pulse with no thread to protect reports the interface as free,
    /// because there is no interface that could stutter.</summary>
    private readonly DispatcherQueue? _ui;

    private Thread? _watch;
    private volatile bool _stopped;

    /// <summary>When the outstanding post was sent, or 0 when none is outstanding. Both threads write this
    /// field. A reader uses it to see a stall WHILE it is happening, not only after it has ended. During a
    /// hang, the stalls that have already ended are none of them.</summary>
    private long _outstanding;

    private double _lateMs;
    private int _calm;
    private long _turns;

    public UiPulse(DispatcherQueue? ui) => _ui = ui;

    /// <summary>A turn that arrived. It is raised ON the drawing thread at the moment the turn
    /// arrived.</summary>
    public event Action<Turn>? Landed;

    /// <summary>The thread has not answered yet. It is raised on the watch thread with how long it has been
    /// so far. It repeats every beat for as long as the outage lasts.</summary>
    public event Action<double>? StillOut;

    /// <summary>How late the last turn to arrive was. It can be stale by up to one beat, and it says
    /// nothing about an outage in progress. Read <see cref="HeldForMs"/> for that.</summary>
    public double LateMs => Volatile.Read(ref _lateMs);

    /// <summary>How many turns in a row have arrived on time. One late turn resets this count to zero
    /// rather than subtracting from it. A thread that was held for two hundred milliseconds and then free
    /// for two turns has not gone quiet. It has taken a breath.</summary>
    public int Calm => _ui is null ? CalmTurns : Volatile.Read(ref _calm);

    /// <summary>How long the drawing thread has been unavailable right now, or 0 when it is answering. This
    /// is the reading that matters during a hang.</summary>
    public double HeldForMs
    {
        get
        {
            var sent = Volatile.Read(ref _outstanding);
            return sent == 0 ? 0 : (ScopeClock.Nanos() - sent) / 1_000_000.0;
        }
    }

    /// <summary>The number of turns that have arrived since the watch started. It never goes down. This is
    /// what tells a reader that "this pulse has never been answered" apart from "this pulse says the thread
    /// is free". Both cases show zero for the other readings, but they need opposite treatment.</summary>
    public long Turns => Interlocked.Read(ref _turns);

    /// <summary>Whether the interface is free enough to give away background work. This is the one check
    /// that everything that governs background work uses, so that there is one definition of it.</summary>
    public bool Free => _ui is null || (HeldForMs <= HeldMs && Calm >= CalmTurns);

    /// <summary>
    /// Starts the watch. It is called once, after the window has content.
    /// </summary>
    /// <remarks>
    /// The thread runs at below-normal priority. A watchdog that competes for the processor with the thread
    /// it is watching reports its own delay as the app's delay.
    /// </remarks>
    public void Start()
    {
        if (_watch is not null || _ui is null)
            return;

        _watch = new Thread(Watch)
        {
            IsBackground = true,
            Name = "ui-pulse",
            Priority = ThreadPriority.BelowNormal,
        };
        _watch.Start();
    }

    /// <summary>Stops the watch. The thread ends after at most one beat.</summary>
    public void Stop() => _stopped = true;

    /// <summary>
    /// Posts a stamped item, waits, and posts another. Only one item is outstanding at a time.
    /// </summary>
    /// <remarks>
    /// Only one item at a time, on purpose. A watchdog that keeps posting during a stall reaches the
    /// drawing thread with a queue of its own items, and every one of them is late. It would then report
    /// one stall as fifty. Because of this rule, the time between turns IS the stall. That is what makes
    /// <c>_outstanding</c> readable as "still out", and not as a gap in the data.
    /// </remarks>
    private void Watch()
    {
        while (!_stopped)
        {
            Thread.Sleep(Beat);

            var ui = _ui;
            if (ui is null)
                return;

            var posted = Volatile.Read(ref _outstanding);
            if (posted != 0)
            {
                // This is said every beat, not only once, so that a reader watching live sees the outage
                // while it is happening. The calm count is also dropped here, not only when the item lands.
                // A pass that asks "may I?" in the middle of a two-second hang must be told no by the
                // reading it can see. It cannot wait for the reading that exists only after the hang is
                // over.
                Volatile.Write(ref _calm, 0);
                StillOut?.Invoke((ScopeClock.Nanos() - posted) / 1_000_000.0);
                continue;
            }

            var sent = ScopeClock.Nanos();
            Volatile.Write(ref _outstanding, sent);

            if (!ui.TryEnqueue(DispatcherQueuePriority.Normal, () => Land(sent)))
            {
                Volatile.Write(ref _outstanding, 0);
                return;   // The queue is shutting down, so the watch stops too.
            }
        }
    }

    /// <summary>Runs on the drawing thread. It records how long the item took to get there.</summary>
    private void Land(long sent)
    {
        var late = (ScopeClock.Nanos() - sent) / 1_000_000.0;

        Volatile.Write(ref _lateMs, late);
        Volatile.Write(ref _calm, late > HeldMs ? 0 : Volatile.Read(ref _calm) + 1);

        // This is cleared LAST of the three writes. A reader that catches the moment between the writes
        // above and this one still sees the thread as held. If it were cleared first, a pass could be
        // admitted on the strength of a calm count that the very next line is about to reset.
        Volatile.Write(ref _outstanding, 0);
        Interlocked.Increment(ref _turns);

        Landed?.Invoke(new Turn(late, ScopeClock.ThreadCpuNanos()));
    }
}

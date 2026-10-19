using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace RPM.Core.Library;

/// <summary>A lock that records who holds it, and for how long.
///
/// <para><b>Why this exists.</b> The watchdog could see that a connection was busy, but not who was using
/// it. In one stall the writing connection was held for 24 seconds. A sample taken during the stall found
/// the holder inside a SQLite call. Knowing that a lock is taken, but not by whom, is not enough to fix a
/// stall.</para>
///
/// <para><b>What it is instead.</b> Callers are not left to remember to report, and no logger is added to
/// every <c>lock</c> statement. The lock object is replaced by this type. The only way in is
/// <see cref="Take"/>. So the compiler records the caller's name at every site, and no site can forget to
/// record it. Both holds and waits are timed. A waiter records who it queued behind. That is the detail
/// that was missing. Not "the write gate was busy", but "the genre wall waited 24,102 ms behind
/// MirrorPeerCatalog".</para>
///
/// <para><b>What it costs.</b> Two timestamps and two field writes per take. The bookkeeping always runs,
/// because a diagnostic that has to be switched on before a fault is off when the fault happens. Only the
/// printing is conditional: a note is written at once only when <c>RPM_DB_DIAG</c> is set. Notes are
/// written after the lock is released, so timing a long hold never makes the hold longer.</para>
///
/// <para><b>Reentrancy.</b> <c>lock</c> is reentrant, and the code this replaced depended on that. A public
/// method takes the gate and calls another method that takes it again. The depth is counted. The OUTERMOST
/// name is kept, because the question is who started the work, not which inner call was running at the
/// time.</para>
/// </summary>
public sealed class Gate
{
    /// <summary>Set by the <c>RPM_DB_DIAG</c> environment variable. Unset is the quiet default. Any value
    /// other than <c>0</c> or <c>false</c> writes every note as it happens. A number above 1 also sets the
    /// threshold, in milliseconds.
    /// </summary>
    private static readonly string? Asked = RPM.Core.Diagnostics.DevSwitch.Read("RPM_DB_DIAG");

    /// <summary>Whether each note is written out the moment it is taken, as well as kept.</summary>
    private static readonly bool Loud = Asked is not (null or "" or "0" or "false");

    /// <summary>How long a hold or a wait must last before it is written down. The default is 100 ms,
    /// which is about six frames. Nothing ordinary reaches it, and everything worth naming does.
    ///
    /// <para>The default applies even when nothing is switched on. So the next stall names itself, and
    /// nobody has to start sampling it by hand.</para>
    /// </summary>
    private static readonly double NoteAboveMs =
        double.TryParse(Asked, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 1
            ? ms
            : 100;

    /// <summary>Past this, a note carries the stack it happened on as well as the name.
    ///
    /// <para>A second is not a slow query. It is a freeze, and at that length the name of one member is not
    /// enough. <c>SaveTrackMood</c> says what was running, but not what asked for it. The stack is read by
    /// the thread itself, at the moment it releases the gate or gets through it. Those frames exist only at
    /// that moment, and nothing outside the thread can read them.</para>
    /// </summary>
    private const double TraceAboveMs = 1000;

    private readonly object _lock = new();

    private string? _holder;
    private long _since;
    private long _sinceCpu;
    private int _depth;
    private int _waiting;
    private long _turns;
    private bool _closed;

    public Gate(string name) => Name = name;

    /// <summary>Which connection this guards. A note then says <c>write</c> or <c>read</c>, and the reader
    /// does not have to work it out from the caller.
    /// </summary>
    public string Name { get; }

    /// <summary>How many holds of this gate have finished. It never goes down, and it can be read without
    /// the lock.
    ///
    /// <para><b>What it is for.</b> On the writing gate this is a change count for the whole database, and
    /// no call site can forget to update it. <see cref="Take"/> is the one door, and every write goes
    /// through it. So a row cannot be written without a hold, and a hold cannot finish without updating
    /// this count. Anything that needs to know whether the collection has changed reads two longs, instead
    /// of reading the whole collection again. See <c>LibraryDatabase.WriteTurns</c>.</para>
    ///
    /// <para><b>Changed when the hold ENDS, not when it starts.</b> This ordering is the whole of the
    /// correctness. If the count changed on the way in, it would change before the row did. A reader could
    /// then take the new count, read rows the write had not yet landed, and file that stale answer under a
    /// count that says it is current. When the count changes on the way out, the write is already done. So
    /// a change in the count is always the second of the two events.</para>
    ///
    /// <para>It counts holds, not rows. A hold that wrote nothing changes it, and so does a hold that wrote
    /// forty thousand rows. So a reader is told the collection MAY have changed, and sometimes does its
    /// work again for nothing. Being told that it has not changed when it has is the one answer this count
    /// can never give.</para>
    /// </summary>
    public long Turns => Interlocked.Read(ref _turns);

    /// <summary>Enters the gate and returns the value that leaves it again. This is the one door.
    /// </summary>
    /// <param name="by">Filled in by the compiler with the name of the calling member. <b>Never pass this
    /// yourself.</b> The value of it is that no call site can get it wrong.</param>
    public Hold Take([CallerMemberName] string? by = null)
    {
        var asked = Stopwatch.GetTimestamp();

        string? waitedBehind = null;
        var waitedMs = 0d;

        if (!Monitor.TryEnter(_lock))
        {
            // Read who is inside BEFORE queueing. By the time this thread gets in, they will have left, and
            // the answer would be nothing. This is the whole point of the class.
            var behind = Volatile.Read(ref _holder);

            Interlocked.Increment(ref _waiting);
            try
            {
                Monitor.Enter(_lock);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            // Notes are gathered here and written later. Writing one on the spot would make a contended
            // take call into the debug channel while it holds the gate that every read in the app goes
            // through. Under a debugger each of those calls stops the runtime. So a wait over the threshold
            // would lengthen the hold. The longer hold would push the next waiter over the threshold, and
            // that waiter would pay the same cost. The diagnostic would feed itself, and the app would stop
            // responding. The class summary says notes are written after the lock is released. That rule
            // held for holds but not for waits, and that was enough to cause the loop.
            var waited = SinceMs(asked);
            if (waited >= NoteAboveMs)
            {
                waitedBehind = behind ?? "?";
                waitedMs = waited;
            }
        }

        // The lock is held here, so the depth and the closed flag need no extra synchronisation.
        //
        // The check is made here, before the caller reaches the connection. After Close, the connection is
        // gone or about to be. A depth above zero means this same thread is re-entering. It got in before
        // the close, so it is allowed to finish what it started.
        if (Volatile.Read(ref _closed) && _depth == 0)
        {
            Monitor.Exit(_lock);
            throw new ObjectDisposedException(Name, $"The {Name} connection is closed.");
        }

        if (++_depth == 1)
        {
            Volatile.Write(ref _holder, by ?? "?");
            Volatile.Write(ref _since, Stopwatch.GetTimestamp());
            Volatile.Write(ref _sinceCpu, Diagnostics.ScopeClock.ThreadCpuNanos());
        }

        return new Hold(this, waitedMs > 0 ? by ?? "?" : null, waitedBehind, waitedMs);
    }

    /// <summary>Shuts the gate for good. Whoever is inside finishes, and nobody else gets in.
    ///
    /// <para>Every take after this throws <see cref="ObjectDisposedException"/>. A caller that outlived the
    /// database needs that answer. Without this check, the refusal would come from the SQLite driver instead.
    /// That happens one frame further on, and only sometimes. Usually the error is "ExecuteNonQuery can
    /// only be called when the connection is open". On a bad run, a native handle is closed under a
    /// running statement.</para>
    /// </summary>
    /// <param name="wait">How long to wait for a hold that is already running.</param>
    /// <returns>True when the gate was idle by the time it shut, so what it guards can be taken apart.
    /// False when a hold was still running past the wait. The gate is shut to everyone else, but the thread
    /// inside is still using what is behind it. Taking that apart now would pull it out from under a
    /// running statement.</returns>
    public bool Close(TimeSpan wait)
    {
        var idle = Monitor.TryEnter(_lock, wait);

        // This is set whether or not the gate came free. A hold that runs past the wait keeps its
        // connection. Nothing new gets one.
        Volatile.Write(ref _closed, true);

        if (idle)
            Monitor.Exit(_lock);

        return idle;
    }

    /// <summary>Whether <see cref="Close"/> has been called. It takes no lock.</summary>
    public bool IsClosed => Volatile.Read(ref _closed);

    /// <summary>What this gate is doing at the instant it is asked, for a watchdog on another thread.
    /// </summary>
    /// <remarks><b>Answered from the holder field, not by trying the lock.</b> The earlier approach took
    /// the lock and released it. That was accurate, but it competed with the very thing being measured, and
    /// it could only return true or false. This reads two fields that nobody has to wait for. It also
    /// returns the name and the age of the hold. So a stall reported while it is still happening can say
    /// who is inside and for how long. That is the only useful reading during a freeze.
    /// </remarks>
    public Look Now
    {
        get
        {
            var by = Volatile.Read(ref _holder);
            return by is null
                ? default
                : new Look(true, by, SinceMs(Volatile.Read(ref _since)), Volatile.Read(ref _waiting));
        }
    }

    /// <param name="waiter">Who waited to get in, when they waited long enough to be worth recording. It is
    /// carried from <see cref="Take"/> and not written there, because writing it there would mean writing
    /// under the lock. See the note in Take.</param>
    /// <param name="behind">Who they waited behind.</param>
    /// <param name="waitedMs">How long they waited.</param>
    private void Release(string? waiter, string? behind, double waitedMs)
    {
        string? who = null;
        double held = 0;
        double cpu = 0;

        if (--_depth == 0)
        {
            who = _holder;
            held = SinceMs(Volatile.Read(ref _since));
            cpu = CpuMsSince(Volatile.Read(ref _sinceCpu));
            Volatile.Write(ref _holder, null);

            // The outermost hold only. A reentrant call is one piece of work, not two. See Turns for why
            // the count is here and not in Take.
            Interlocked.Increment(ref _turns);
        }

        Monitor.Exit(_lock);

        // Everything below runs outside the lock. Describing a hold must never be part of holding it.
        if (waiter is not null)
            Write(new Note(Name, "waited", waiter, behind, waitedMs, Where(waitedMs), Cpu: 0));

        if (who is not null && held >= NoteAboveMs)
            Write(new Note(Name, "held", who, null, held, Where(held), cpu));
    }

    private static double SinceMs(long from) =>
        (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary>The processor time the holding thread used while it was inside, set against how long it
    /// was inside. This is the one number that splits a long hold in two. The two halves need opposite
    /// fixes.
    ///
    /// <para>Sixteen seconds of wall time with sixteen seconds of processor time is work: a query with no
    /// index, a scan, something to make faster. Sixteen seconds of wall time with twelve milliseconds of
    /// processor time means the thread was not running. It was parked on a native lock, waiting on the
    /// disk, suspended for a collection, stopped by the debugger, or descheduled by a machine that is
    /// swapping. Making the query faster would do nothing. From outside, the two cases look the same, so
    /// only this measurement tells them apart.</para>
    /// </summary>
    /// <param name="from">The reading taken when the hold began, or
    /// <see cref="Diagnostics.ScopeClock.Unknown"/> if there was none to take.</param>
    /// <returns>Milliseconds of processor time, or <see cref="Unmeasured"/> where the platform does not
    /// report it. <b>Zero is never returned for a reading that was not taken.</b> Zero means the thread did
    /// not run, which is the answer this number exists to give. Printing zero on a platform with no such
    /// counter would be a false answer.</returns>
    private static double CpuMsSince(long from)
    {
        if (from < 0)
            return Unmeasured;

        var now = Diagnostics.ScopeClock.ThreadCpuNanos();
        return now < 0 ? Unmeasured : (now - from) / 1_000_000.0;
    }

    /// <summary>What <see cref="CpuMsSince"/> returns when there is no counter to read. It is negative, so
    /// a reader can tell it apart from zero without knowing which platforms have a counter.</summary>
    internal const double Unmeasured = -1;

    /// <summary>The chain of calls that led here, innermost first. It is recorded for a hold or a wait
    /// long enough to count as a hang.
    ///
    /// <para>Only this application's frames are included. A stack through the framework is forty frames of
    /// scheduling and two that say something useful. This returns null below <see cref="TraceAboveMs"/>.
    /// Walking a stack costs more than everything else this class does together, so it is not worth paying
    /// for a hold that lasted less than one second.</para>
    /// </summary>
    private static string? Where(double ms)
    {
        if (ms < TraceAboveMs)
            return null;

        try
        {
            // This uses DiagnosticMethodInfo rather than StackFrame.GetMethod(). In the published Native
            // AOT build, GetMethod() returns null for every frame, whatever IlcGenerateStackTraceData is
            // set to. DiagnosticMethodInfo reads the same name table as the printed stack trace. So the
            // names are the same in a published build and under a debugger.
            var gate = typeof(Gate).FullName!;
            var ours = new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames()
                .Select(DiagnosticMethodInfo.Create)
                .Where(m => m?.DeclaringTypeName is { } type
                         && type.StartsWith("RPM.", StringComparison.Ordinal)
                         && type != gate
                         && !type.StartsWith(gate + "+", StringComparison.Ordinal))
                .Select(m => $"{Short(m!.DeclaringTypeName!)}.{m.Name}")
                .Take(12)
                .ToList();

            return ours.Count == 0 ? null : string.Join(" <- ", ours);
        }
        catch
        {
            // A stack that cannot be walked is not worth failing a write over.
            return null;
        }
    }

    /// <summary>The declaring type as written in the source. The namespace and any enclosing type are
    /// dropped, and so is the suffix a compiler-made name carries. So a trace shows method names, not names
    /// such as <c>&lt;&gt;c__DisplayClass12_0</c>.
    /// </summary>
    private static string Short(string fullName)
    {
        var leaf = fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
        var angle = leaf.IndexOf('<');
        return angle > 0 ? leaf[..angle] : leaf;
    }

    /// <summary>The value a <c>using</c> block holds. It is a struct, because there are many of these call
    /// sites, and some of them run once for every row.
    /// </summary>
    public readonly struct Hold : IDisposable
    {
        private readonly Gate _gate;
        private readonly string? _waiter;
        private readonly string? _behind;
        private readonly double _waitedMs;

        internal Hold(Gate gate, string? waiter, string? behind, double waitedMs)
        {
            _gate = gate;
            _waiter = waiter;
            _behind = behind;
            _waitedMs = waitedMs;
        }

        public void Dispose() => _gate?.Release(_waiter, _behind, _waitedMs);
    }

    /// <summary>What a gate was doing at one instant.</summary>
    /// <param name="Busy">Whether anybody is inside.</param>
    /// <param name="By">The member that took the gate, outermost first.</param>
    /// <param name="ForMs">How long they have been inside so far.</param>
    /// <param name="Waiting">How many others are queued behind them.</param>
    public readonly record struct Look(bool Busy, string? By, double ForMs, int Waiting)
    {
        public override string ToString() =>
            !Busy ? "free" : $"{By} {ForMs:0}ms{(Waiting > 0 ? $" +{Waiting} waiting" : "")}";
    }

    /// <summary>One thing that took too long: a hold, or a wait behind one.</summary>
    /// <param name="Gate">Which connection.</param>
    /// <param name="What"><c>held</c> or <c>waited</c>.</param>
    /// <param name="Who">The member it happened to.</param>
    /// <param name="Behind">For a wait, the member it queued behind.</param>
    /// <param name="Ms">How long it lasted.</param>
    /// <param name="Where">The call chain, for a hold long enough to count as a hang. Otherwise null, which
    /// is the usual case.</param>
    /// <param name="Cpu">Processor time the holding thread used while it was inside. It is zero for a wait,
    /// which is parked by definition. It is <see cref="Unmeasured"/> where the platform keeps no such
    /// counter. That is a different fact from zero, so it is not printed as zero.</param>
    public readonly record struct Note(
        string Gate, string What, string Who, string? Behind, double Ms, string? Where, double Cpu)
    {
        /// <remarks><b>One line.</b> A note must stay on one line. If it ran over two lines, the part with
        /// the stack could be lost when the note is pasted into a bug report. That would leave a long hold
        /// with a name and no call chain, and another round trip to find the chain.
        /// </remarks>
        public override string ToString()
        {
            var line = $"{Gate} {What} {Ms:0}ms";
            if (Cpu < 0)
                line += " (cpu not measured on this platform)";
            else if (Cpu > 0 || What == "held")
                line += $" (cpu {Cpu:0}ms, {(Cpu * 100 / Math.Max(Ms, 1)):0}%)";
            line += $" in {Who}";
            if (Behind is { } behind)
                line += $", behind {behind}";
            if (Where is { } via)
                line += $" | via {via}";
            return line;
        }
    }

    /// <summary>How many notes are kept before the oldest is dropped. A stall worth investigating produces
    /// a few. A run that produces five hundred has a different problem, and the newest notes describe it
    /// best.</summary>
    private const int Keep = 512;

    private static readonly ConcurrentQueue<Note> Notes = new();

    private static void Write(Note note)
    {
        // Scope.Gate records the note first, and always. The queue below holds at most 512 notes and is
        // emptied when a line is requested. The scope's ring is not emptied by a request for a line, and
        // it is the one of the two that survives when the process cannot print anything.
        Diagnostics.Scope.Gate(note.Gate, note.What, note.Who, note.Behind, note.Ms, note.Cpu);

        Notes.Enqueue(note);
        while (Notes.Count > Keep && Notes.TryDequeue(out _)) { }

        // A hold longer than a second is always written, whether or not anyone asked for it. That is not a
        // slow query. It is a freeze with a name on it. Such a hold is written even when diagnostics are
        // off, because freezes often happen on runs where nothing was switched on. When RPM_DB_DIAG is set,
        // every note over the threshold is written. That is for watching live, not for finding out later.
        if (!Loud && note.Ms < TraceAboveMs)
            return;

        Diagnostics.Said.Line("db", note.ToString());
    }

    /// <summary>Takes the notes recorded since the last call, and empties the list.</summary>
    public static List<Note> Drain()
    {
        var taken = new List<Note>();
        while (Notes.TryDequeue(out var note))
            taken.Add(note);
        return taken;
    }

    /// <summary>The notes since the last request, as one line for a stall report. The longest come first.
    /// Notes for the same hold are grouped, so a hold that happened forty times reads as forty, not as
    /// forty lines.
    /// </summary>
    public static string Line(int most = 4)
    {
        var notes = Drain();
        if (notes.Count == 0)
            return "db quiet";

        var grouped = notes
            .GroupBy(n => (n.Gate, n.What, n.Who, n.Behind))
            .Select(g => (
                g.Key,
                Count: g.Count(),
                Worst: g.Max(n => n.Ms),
                Total: g.Sum(n => n.Ms),
                Where: g.OrderByDescending(n => n.Ms).First().Where))
            .OrderByDescending(g => g.Worst)
            .ToList();

        var said = grouped.Take(most).Select(g =>
        {
            var one = $"{g.Key.Gate} {g.Key.What} {g.Worst:0}ms";
            if (g.Count > 1)
                one += $"×{g.Count} ({g.Total:0}ms)";
            one += $" in {g.Key.Who}";
            if (g.Key.Behind is { } behind)
                one += $" behind {behind}";
            if (g.Where is { } via)
                one += $" [via {via}]";
            return one;
        });

        var line = "db: " + string.Join("; ", said);
        if (grouped.Count > most)
            line += $"; +{grouped.Count - most} more";
        return line;
    }
}

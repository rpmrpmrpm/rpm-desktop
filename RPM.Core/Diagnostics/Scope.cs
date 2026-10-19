namespace RPM.Core.Diagnostics;

/// <summary>
/// The door everything in the app writes a fact through.
///
/// <para>One ring per process, opened once, never closed, and written to whether or not anybody is
/// reading. An environment variable may only turn it OFF (<c>RPM_SCOPE=0</c>). There is deliberately
/// nothing to turn ON: a fault that shows up only when nothing was switched on beforehand would never
/// be seen, so the instrument is always on unless it is turned off. <c>Gate</c> makes the same choice
/// for the same reason.</para>
///
/// <para><b>Parentage is carried, not passed.</b> <see cref="Under"/> pushes an
/// <see cref="AsyncLocal{T}"/>, so work begun inside other work records whose it is at no call site's
/// expense, and it survives an <c>await</c>, a thread hop and a continuation on the pool. That one
/// field is what turns a list of things that happened into a tree, and the tree is what
/// <c>rpmscope why</c> walks. Timings alone say what was slow, not what asked for it.</para>
/// </summary>
public static class Scope
{
    /// <summary>
    /// Where a thread is: the job it is inside, and the job THAT one was started by.
    ///
    /// <para>Both, because a record naming its own job still has to name a parent, and the parent of
    /// the current job is not the current job. Keeping only one of the two loses the edge at exactly
    /// the record that matters: the one that says a piece of work started.</para>
    /// </summary>
    internal readonly record struct Inside(long Job, long Parent);

    private static readonly AsyncLocal<Inside> Here = new();

    private static ScopeRing? _ring;
    private static Timer? _heartbeat;
    private static long _nextJobId;

    /// <summary>
    /// Where the rings live. <c>RPM_SCOPE_DIR</c> moves them; otherwise the platform's cache folder:
    /// <c>~/Library/Caches/rpm</c> on macOS, <c>%LOCALAPPDATA%\RPM\scope</c> on Windows (next to the
    /// app's other local data), and <c>$XDG_CACHE_HOME/rpm</c> or <c>~/.cache/rpm</c> elsewhere. The
    /// collector and <c>tools/rpmscope</c> (<c>ring.default_dir</c>) read the same place.
    /// </summary>
    public static string Dir
    {
        get
        {
            if (RPM.Core.Diagnostics.DevSwitch.Read("RPM_SCOPE_DIR") is { Length: > 0 } asked)
                return asked;

            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM", "scope");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Caches", "rpm");

            return Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(home, ".cache"),
                "rpm");
        }
    }

    /// <summary>The names, for anything that has to turn an id back into text.</summary>
    public static ScopeNames? Names { get; private set; }

    /// <summary>Whether there is a ring to write into.</summary>
    public static bool On => _ring is not null;

    /// <summary>
    /// Opens the ring. Called once, from the app's start-up, before anything worth recording runs.
    /// </summary>
    public static void Start()
    {
        if (_ring is not null || RPM.Core.Diagnostics.DevSwitch.Read("RPM_SCOPE") is "0" or "false")
            return;

        try
        {
            // Named after the process, so that two processes can never share a file. Two writers on
            // one file corrupt each other's records: a stale collector can keep appending to a table
            // the app has just truncated, and then every reading taken through it is wrong without
            // looking wrong. A file that no other process holds needs no coordination, and a second
            // copy of the app running at the same time is harmless as well.

            // The table is made fresh with the ring, not carried over. A fresh ring holds no record
            // that refers to an old id, and an id only means something relative to the table that
            // issued it. Keeping the previous run's table could only give wrong names.
            Names = ScopeNames.Fresh(Path.Combine(Dir, $"scope.{Environment.ProcessId}.names"));
            _ring = ScopeRing.Create(Path.Combine(Dir, $"scope.{Environment.ProcessId}.ring"));

            // Off the start-up path: listing the processes and deleting files is nothing the first
            // frame should wait for. Safe to run beside this run's writes because it never touches
            // this process's own files (see SweepFinishedRuns).
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (LivePids() is { } live)
                    SweepFinishedRuns(Dir, Environment.ProcessId, live);
            });

            // Once a second, both clocks in one record, so a viewer can put a wall time on an axis and
            // the collector's stream can be laid over this one.
            _heartbeat = new Timer(
                _ => Say(ScopeKind.ClockSync, null, a: ScopeClock.UnixNanos(), b: Environment.ProcessId),
                null, 0, 1000);
        }
        catch (Exception e)
        {
            // A diagnostic that stops the app from starting is worse than no diagnostic. Say so once,
            // on the channel that exists before this one does, and carry on without it. The ring is
            // dropped first, so Said sees no ring and writes to the terminal or the debugger.
            _ring = null;
            global::RPM.Core.Diagnostics.Said.Line("scope", $"not started: {e.Message}");
        }
    }

    /// <summary>How many runs' rings are kept, counting from the newest. The last finished run's is
    /// the one somebody reads after a crash or a hang, and three covers a launch that failed twice in
    /// a row.</summary>
    internal const int KeptRuns = 3;

    /// <summary>
    /// Deletes the rings and name tables of runs that have finished, except the newest
    /// <see cref="KeptRuns"/>.
    ///
    /// <para>Each launch writes a ring of about 16 MB named after its process. Without this sweep the
    /// rings pile up: one machine once held over 200 of them, about 1.1 GB. The collector sweeps its
    /// own files the same way (<c>tools/RpmScope.Collector</c>, <c>Sweep</c>).</para>
    ///
    /// <para>Never touches the files of <paramref name="ownPid"/> or of any process in
    /// <paramref name="live"/>: this run, a second copy of the app, or the collector. Files not named
    /// <c>scope.&lt;pid&gt;.*</c> are left alone. A file that will not delete (another process has it
    /// open on Windows) is left for the next launch.</para>
    /// </summary>
    internal static void SweepFinishedRuns(string directory, int ownPid, IReadOnlySet<int> live)
    {
        try
        {
            var finished = Directory.GetFiles(directory, "scope.*")
                .Select(f => (File: f, Pid: Path.GetFileName(f).Split('.') is [_, var id, _] && int.TryParse(id, out var pid) ? pid : 0))
                .Where(f => f.Pid > 0)
                .GroupBy(f => f.Pid, f => f.File)
                .OrderByDescending(run => run.Max(File.GetLastWriteTimeUtc))
                .Skip(KeptRuns)
                .Where(run => run.Key != ownPid && !live.Contains(run.Key));

            foreach (var run in finished)
                foreach (var file in run)
                {
                    try { File.Delete(file); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Housekeeping. The next launch tries again.
        }
    }

    /// <summary>The ids of the processes running now, or null when they cannot be listed. The list is
    /// read in one call rather than checked pid by pid. Asking .NET about a pid that has already exited
    /// throws, and a sweep over many old rings would then throw once per ring. Public because the crash
    /// harvest needs the same list.
    ///
    /// <para>The operating system's own list is used, not <c>Process.GetProcesses</c>. On macOS that call
    /// reads the path of every process and throws a caught Win32Exception for each one this user may not
    /// read. Under a debugger each of those throws stops the app. The lists here throw nothing:
    /// <c>proc_listallpids</c> on macOS, <c>K32EnumProcesses</c> on Windows, and the numbered folders of
    /// /proc on Linux.</para></summary>
    public static HashSet<int>? LivePids()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return MacPids();
            if (OperatingSystem.IsWindows())
                return WindowsPids();
            if (OperatingSystem.IsLinux())
                return LinuxPids();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException
                                     or IOException or UnauthorizedAccessException)
        {
            // Without the list no run can be shown to have finished, so the sweep does not run.
        }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib", EntryPoint = "proc_listallpids")]
    private static extern int ProcListAllPids(int[]? buffer, int bufferBytes);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "K32EnumProcesses")]
    private static extern bool EnumProcesses(uint[] pids, uint bufferBytes, out uint usedBytes);

    private static HashSet<int>? MacPids()
    {
        // Asked for the count first, then given room for that many and some that start meanwhile.
        var count = ProcListAllPids(null, 0);
        if (count <= 0)
            return null;

        var buffer = new int[count + 256];
        var listed = ProcListAllPids(buffer, buffer.Length * sizeof(int));
        return listed <= 0 ? null : [.. buffer.Take(Math.Min(listed, buffer.Length)).Where(pid => pid > 0)];
    }

    private static HashSet<int>? WindowsPids()
    {
        // The call says how many bytes it used, not how many it needed: a full buffer may have been
        // too small, so it is asked again with twice the room.
        for (var room = 1024; room <= 1 << 20; room *= 2)
        {
            var buffer = new uint[room];
            if (!EnumProcesses(buffer, (uint)(room * sizeof(uint)), out var used))
                return null;
            var listed = (int)(used / sizeof(uint));
            if (listed < room)
                return [.. buffer.Take(listed).Select(pid => (int)pid)];
        }
        return null;
    }

    private static HashSet<int> LinuxPids() =>
        [.. Directory.EnumerateDirectories("/proc")
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var pid) ? pid : 0)
            .Where(pid => pid > 0)];

    /// <summary>A number for a piece of work. Never zero, which means "no job".</summary>
    public static long NewJobId() => Interlocked.Increment(ref _nextJobId);

    /// <summary>The job this thread is inside, or zero.</summary>
    public static long Current => Here.Value.Job;

    /// <summary>
    /// Names a piece of work and puts everything that follows inside it, until it is disposed.
    ///
    /// <para><b>What this is for.</b> A stack says who threw; it does not always say who ASKED. Work
    /// handed to the pool by a library, such as a picture the renderer decodes on our behalf, can fail
    /// with no frame of our code on its stack. Walking that stack cannot find the caller, because the
    /// caller is on the other side of an await. Parentage is carried in an <c>AsyncLocal</c>, which
    /// flows through <c>Task.Run</c> and through every continuation, so a record written in there names
    /// the job it belongs to even though nothing on its stack does.</para>
    ///
    /// <para>Pass names the compiler already has. A <c>[CallerMemberName]</c> is a literal, so this
    /// costs an increment and a record and allocates nothing.</para>
    /// </summary>
    public static Held Job(string? name, string? detail = null)
    {
        var id = NewJobId();
        Say(ScopeKind.JobEnqueue, name, detail: detail, jobId: id);
        return Under(id);
    }

    /// <summary>Says that what follows happens inside <paramref name="jobId"/>, until it is disposed.</summary>
    public static Held Under(long jobId)
    {
        var was = Here.Value;
        Here.Value = new Inside(jobId, was.Job);
        return new Held(was);
    }

    /// <summary>Puts the enclosing job back. A struct, because these are per-job and there are many.</summary>
    public readonly struct Held : IDisposable
    {
        private readonly Inside _was;
        internal Held(Inside was) => _was = was;
        public void Dispose() => Here.Value = _was;
    }

    /// <summary>
    /// The general door. Never throws.
    /// </summary>
    /// <param name="jobId">The job this record is ABOUT. Zero means the record is about whatever the
    /// thread is currently inside, which is the ordinary case.</param>
    public static void Say(
        ScopeKind kind, string? name, long a = 0, long b = 0, string? detail = null, long jobId = 0)
    {
        var ring = _ring;
        if (ring is null)
            return;

        var here = Here.Value;
        var names = Names;

        ring.Write(new ScopeRecord
        {
            Nanos = ScopeClock.Nanos(),
            Kind = (ushort)kind,
            Thread = (ushort)(Environment.CurrentManagedThreadId & 0xFFFF),
            JobId = jobId,

            // A record about the job we are inside is parented to the job THAT was started by;
            // anything else is parented to the job we are inside.
            ParentId = jobId != 0 && jobId == here.Job ? here.Parent : here.Job,

            NameId = names?.Id(name) ?? 0,
            Detail = names?.Id(detail) ?? 0,
            A = a,
            B = b,
        });
    }

    /// <summary>
    /// What <c>Gate</c> writes here instead of only to a console.
    ///
    /// <para>Milliseconds in, nanoseconds down. A record has one unit, and it is the finest one
    /// anything reports in, so nothing downstream has to know where a number came from.</para>
    /// </summary>
    public static void Gate(string gate, string what, string who, string? behind, double ms, double cpuMs) =>
        Say(
            what == "held" ? ScopeKind.GateHold : ScopeKind.GateWait,
            who,
            a: (long)(ms * 1_000_000),
            b: (long)(cpuMs * 1_000_000),
            detail: behind ?? gate,
            jobId: Current);

    /// <summary>The user did something. The tick every other track is read against.</summary>
    public static void Input(string what) => Say(ScopeKind.Input, what);

    /// <summary>The user did something, and to what: <paramref name="where"/> names the element it
    /// landed on and whether something had already handled it.</summary>
    public static void Input(string what, string where) => Say(ScopeKind.Input, what, detail: where);

    /// <summary>A frame, and what it cost.</summary>
    public static void Frame(ScopeKind kind, double ms) => Say(kind, null, a: (long)(ms * 1_000_000));

    /// <summary>A first-chance throw, from <c>ExceptionTrace</c>.</summary>
    public static void Throw(string type, string? site) => Say(ScopeKind.Throw, type, detail: site);

    /// <summary>A line the app wants to say, put where it costs a record instead of a round trip to a
    /// debugger. <paramref name="channel"/> is the tag the line is filed under, such as "walldiag" or
    /// "db", and is what <c>rpmscope log</c> filters on.</summary>
    public static void Said(string channel, string line) =>
        Say(ScopeKind.Said, channel, detail: line);

    /// <summary>Says that this run has begun ending normally. Called first in the main window's
    /// Closed handler and from ProcessExit, on both heads.</summary>
    public static void Closing() => Say(ScopeKind.Closing, null);

    /// <summary>
    /// When this run's ring was made, in Unix nanoseconds, from the ring header. Zero when there is
    /// no ring. With the process id it names the run: a crash file carries it so the next launch can
    /// find the same run's ring.
    /// </summary>
    public static long StartedUnixNanos => _ring?.StartUnixNanos ?? 0;

    // --- for the tests, and for nothing else -------------------------------------------------

    internal static void StartForTest(string dir)
    {
        StopForTest();
        Names = ScopeNames.Open(Path.Combine(dir, "scope.names"));
        _ring = ScopeRing.Create(Path.Combine(dir, "scope.ring"), capacity: 4096);
    }

    internal static void StopForTest()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
        _ring?.Dispose();
        _ring = null;
        Names?.Dispose();
        Names = null;
    }
}

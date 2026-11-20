namespace RPM.Core.Threading;

/// <summary>
/// Background work that gives way to everything else on the computer, and goes back to its normal priority
/// for as long as it asks for or holds a library gate.
/// </summary>
/// <remarks>
/// <para><b>Each system has its own lever.</b> On Windows it is <see cref="Thread.Priority"/>. On macOS it is
/// the thread's scheduling priority, set to the utility class's level (see <see cref="DarwinThread"/>, which
/// says why not the QoS class). Linux has no lever an unprivileged process can pull; the slot count and the
/// pause between tracks of each pass are its only throttle.</para>
///
/// <para><b>Why a gate lifts it.</b> The passes that use this also write to the library, and the library's
/// gates are shared with the interface. A lowered thread that holds a gate makes everybody behind it wait at
/// the lowered thread's speed: the interface waits for the slowest thread in the app. So <see cref="Gate"/>
/// tells this class when a thread asks for a gate and when it lets go, and a lowered thread runs at its
/// normal priority from the ask to the release. The rule is in the gate, so no pass has to remember it.</para>
///
/// <para><b>Restoring matters.</b> The passes run on pool threads. A thread left lowered would go back to the
/// pool that way and slow down whatever it was given next. A scope puts back what the thread had when the
/// outermost scope began.</para>
///
/// <para>The state is per thread. A scope must end on the thread it began on, so it must not span an
/// <c>await</c>.</para>
/// </remarks>
public static class GiveWay
{
    /// <summary>How many scopes this thread is inside.</summary>
    [ThreadStatic] private static int t_scopes;

    /// <summary>How many gates this thread is asking for or holding, counting a reentrant take again.</summary>
    [ThreadStatic] private static int t_gates;

    /// <summary>What the thread ran at before the outermost scope began.</summary>
    [ThreadStatic] private static ThreadPriority t_priorityBefore;

    [ThreadStatic] private static int? t_darwinBefore;

    /// <summary>Lowers the calling thread until the returned scope ends.</summary>
    public static Scope Begin()
    {
        if (t_scopes++ == 0)
        {
            t_priorityBefore = Thread.CurrentThread.Priority;
            t_darwinBefore = DarwinThread.Priority;
            if (t_gates == 0)
                Lower();
        }
        return new Scope(Environment.CurrentManagedThreadId);
    }

    /// <summary>Whether the calling thread is lowered now. For tests and diagnostics.</summary>
    public static bool IsLowered => t_scopes > 0 && t_gates == 0;

    /// <summary>The calling thread's scheduling priority on macOS, and null elsewhere. For tests.</summary>
    public static int? DarwinPriority => DarwinThread.Priority;

    /// <summary>The level a lowered thread runs at on macOS. For tests.</summary>
    public const int DarwinLevel = DarwinThread.Utility;

    /// <summary>Called by <see cref="Gate.Take"/> before it waits. The thread runs at its normal priority
    /// while it waits for the gate and while it holds it.</summary>
    internal static void GateAsked()
    {
        if (t_gates++ == 0 && t_scopes > 0)
            Restore();
    }

    /// <summary>Called by the gate after a hold ends, and by a take that was refused.</summary>
    internal static void GateLetGo()
    {
        if (--t_gates == 0 && t_scopes > 0)
            Lower();
    }

    private static void End()
    {
        if (--t_scopes == 0 && t_gates == 0)
            Restore();
    }

    private static void Lower()
    {
        if (DarwinThread.Applies)
        {
            DarwinThread.SetPriority(DarwinThread.Utility);
            return;
        }

        try { Thread.CurrentThread.Priority = ThreadPriority.Lowest; }
        catch { /* not every host lets a thread say so */ }
    }

    private static void Restore()
    {
        if (DarwinThread.Applies)
        {
            if (t_darwinBefore is { } before)
                DarwinThread.SetPriority(before);
            return;
        }

        try { Thread.CurrentThread.Priority = t_priorityBefore; }
        catch { }
    }

    /// <summary>The value a <c>using</c> holds. It ends on the thread it began on.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly int _thread;

        internal Scope(int thread) => _thread = thread;

        public void Dispose()
        {
            // A scope that ended on another thread would put back the wrong thread. That is a programming
            // error; the thread it began on stays lowered until that thread ends a scope of its own.
            if (_thread != 0 && _thread == Environment.CurrentManagedThreadId)
                End();
        }
    }
}

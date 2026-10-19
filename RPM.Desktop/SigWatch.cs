using System.Runtime.InteropServices;

namespace RPM.Desktop;

/// <summary>
/// Writes a line to the diagnostic ring each time the SIGUSR1 handler changes, and once at startup.
/// macOS only.
///
/// <para>Why it exists: some crashes on macOS happen when the garbage collector sends SIGUSR1 to the
/// main thread and the signal reaches a handler that the runtime did not install. The cause is not
/// known. When a crash happens, the change of handler is a line in the ring, and the last 300 ring
/// lines are attached to the crash report, so the report shows when the handler changed and to what.</para>
///
/// <para>The watch reads the handler once a second on a timer and writes any change through
/// <see cref="RPM.Core.Diagnostics.Said"/>, which writes the ring. Output to stderr is discarded when
/// the app is started from Finder, so the ring is used instead. One <c>sigaction</c> query a second
/// costs nothing measurable.</para>
/// </summary>
internal static class SigWatch
{
    private const int SIGUSR1 = 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct SigActionRaw { public nint Handler; public uint Mask; public int Flags; }

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "sigaction")]
    private static extern int SigAction(int sig, nint act, out SigActionRaw old);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dladdr")]
    private static extern int DlAddr(nint addr, out DlInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo { public nint FileName; public nint FileBase; public nint SymName; public nint SymAddr; }

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    /// <summary>Held in a field: an unreferenced timer can be collected before it fires.</summary>
    private static Timer? _timer;

    /// <summary>The handler last written, or -1 before the first reading. Read and written only from
    /// the timer callback. At this period the timer never runs the callback twice at once.</summary>
    private static nint _last = -1;

    /// <summary>Starts the watch. Call after the ring is open, so the first reading lands on it.</summary>
    public static void Start()
    {
        if (!OperatingSystem.IsMacOS() || _timer is not null)
            return;

        _timer = new Timer(_ => Look(), null, TimeSpan.Zero, Every);
    }

    private static void Look()
    {
        try
        {
            if (SigAction(SIGUSR1, 0, out var now) != 0 || now.Handler == _last)
                return;

            var change = _last == -1 ? "" : $"0x{_last:x} -> ";
            RPM.Core.Diagnostics.Said.Line("signals",
                $"SIGUSR1 handler {change}0x{now.Handler:x} flags=0x{now.Flags:x} {Where(now.Handler)}");
            _last = now.Handler;
        }
        catch
        {
            // A diagnostic must not take the app down; the next tick tries again.
        }
    }

    private static string Where(nint handler)
    {
        if (handler is 0) return "(SIG_DFL, nothing will catch it)";
        if (handler is 1) return "(SIG_IGN)";
        if (DlAddr(handler, out var info) == 0 || info.FileName == 0)
            return "(not inside any loaded image)";
        return Path.GetFileName(Marshal.PtrToStringAnsi(info.FileName) ?? "?")
            + " + " + (handler - info.FileBase);
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>Keeps the machine awake while the music is playing.
///
/// <para>Local playback keeps the machine awake by itself. An open audio device counts as activity for both
/// macOS and Windows, so the idle timer never runs out. Casting has no such device. The sound is encoded
/// and sent over a socket, the sound card is closed, and to the power manager the app looks like a program
/// that nobody has used for ten minutes. On mains power this rarely shows, because the machine is usually
/// set to stay awake. On battery the idle timer is short, so the laptop sleeps during a record and the
/// stream stops with it.</para>
///
/// <para><b>macOS.</b> The assertion is <c>PreventUserIdleSystemSleep</c>. It is used on purpose
/// instead of <c>PreventSystemSleep</c>. The latter is what <c>caffeinate -s</c> takes, and Apple
/// documents it as honoured only on AC power. On battery it does nothing, and battery is the case this
/// fixes. The idle assertion is respected on battery. Neither assertion keeps the machine awake when
/// the lid is closed. Clamshell sleep is not idle sleep, so no assertion holds it off. Casting from a
/// closed laptop still stops.</para>
///
/// <para><b>Windows.</b> <c>SetThreadExecutionState</c> sets state for the calling thread only, and
/// that state lasts only as long as the thread lives. It cannot be called from a pool thread, because
/// transport events arrive on whichever thread the engine uses. One dedicated thread owns the state for
/// the life of the process instead.</para>
///
/// <para>What is held is visible: <c>pmset -g assertions</c> on macOS,
/// <c>powercfg /requests</c> on Windows.</para>
/// </summary>
internal static class SleepBlocker
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint Utf8 = 0x08000100;

    /// <summary>kIOPMAssertionLevelOn.</summary>
    private const uint LevelOn = 255;

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(nint assertionType, uint assertionLevel, nint assertionName, out uint assertionId);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint assertionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);

    private const uint ContinuousState = 0x80000000;
    private const uint SystemRequired = 0x00000001;

    private static readonly object Gate = new();
    private static bool _held;

    /// <summary>
    /// Holds the machine awake, or lets it go. Idempotent and cheap to call on every transport change;
    /// silently does nothing on a platform with no such notion.
    /// </summary>
    internal static void Hold(bool on)
    {
        lock (Gate)
        {
            if (_held == on) return;
            _held = on;

            try
            {
                if (OperatingSystem.IsMacOS()) MacHold(on);
                else if (OperatingSystem.IsWindows()) WindowsHold(on);
            }
            catch
            {
                // Keeping the machine awake is optional. If the power manager does not respond, the music
                // must keep playing.
            }
        }
    }

    // ── macOS ──

    /// <summary>0 is kIOPMNullAssertionID, which is also "nothing held".</summary>
    private static uint _assertion;

    [SupportedOSPlatform("macos")]
    private static void MacHold(bool on)
    {
        if (on)
        {
            if (_assertion != 0) return;

            var type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", Utf8);
            // Shown verbatim by `pmset -g assertions`, so it says who is holding the machine up.
            var name = CFStringCreateWithCString(0, "RPM is playing to a speaker", Utf8);
            try
            {
                if (type != 0 && name != 0 && IOPMAssertionCreateWithName(type, LevelOn, name, out var id) == 0)
                    _assertion = id;
            }
            finally
            {
                if (type != 0) CFRelease(type);
                if (name != 0) CFRelease(name);
            }
        }
        else if (_assertion != 0)
        {
            IOPMAssertionRelease(_assertion);
            _assertion = 0;
        }
    }

    // ── Windows ──

    private static Thread? _winThread;
    private static readonly AutoResetEvent WinSignal = new(false);
    private static volatile bool _winWanted;

    [SupportedOSPlatform("windows")]
    private static void WindowsHold(bool on)
    {
        _winWanted = on;

        if (_winThread is null)
        {
            _winThread = new Thread(WinLoop) { IsBackground = true, Name = "rpm-sleep-blocker" };
            _winThread.Start();
        }

        WinSignal.Set();
    }

    /// <summary>Owns the execution state for the life of the process. The state ends with the thread that
    /// set it, so this thread never exits.</summary>
    [SupportedOSPlatform("windows")]
    private static void WinLoop()
    {
        bool applied = false;

        while (true)
        {
            WinSignal.WaitOne();

            var want = _winWanted;
            if (want == applied) continue;
            applied = want;

            // ES_CONTINUOUS on its own is the release: it replaces the standing request with nothing.
            SetThreadExecutionState(want ? ContinuousState | SystemRequired : ContinuousState);
        }
    }
}

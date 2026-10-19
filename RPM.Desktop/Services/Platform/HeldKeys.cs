using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Diagnostics;
using Windows.System;
using Windows.UI.Core;

namespace RPM.Desktop.Services.Platform;

/// <summary>The modifier keys, as one set.</summary>
[Flags]
internal enum Modifier
{
    None = 0,
    Shift = 1,
    Control = 2,

    /// <summary>Option on a Mac, Alt on Windows.</summary>
    Option = 4,

    /// <summary>Cmd on a Mac, the Windows key on Windows.</summary>
    Command = 8,
}

/// <summary>Which modifier keys are down, read from the keyboard at the moment of asking.
///
/// <para>Uno's own answer, <see cref="InputKeyboardSource.GetKeyStateForCurrentThread"/>, comes from a
/// table built from the key events this app has received. Uno clears the table when the window's
/// activation state changes. On macOS that state never changes, because the Mac host registers no focus
/// callback. So after Cmd-Tab, Cmd stays down in the table, because its key-up went to the next app. It
/// stays down until Cmd is pressed and released again inside rpm. Every playlist row tapped in the
/// meantime is read as a Cmd-click and does not play, and the space bar does nothing. Pressing Cmd-Tab
/// back delivers the missing key-up, which is why switching away and back clears it.</para>
///
/// <para>On macOS this asks AppKit (<c>[NSEvent modifierFlags]</c>), which reads the keyboard rather than
/// the event stream. Elsewhere it asks WinUI, which reads the keyboard on Windows. Analyzer rule RPM005
/// requires this class to be the only place that reads key state.</para>
///
/// <para>Whether a key press is a repeat of a key that is held is also read here (<see cref="IsRepeat"/>),
/// for the same reason: on macOS the event's own flag is never set. Analyzer rule RPM015 requires this
/// class to be the only place that reads it.</para>
/// </summary>
internal static class HeldKeys
{
    /// <summary>The modifiers down right now.</summary>
    public static Modifier Now
    {
        get
        {
            var tracked = FromTracker();
            if (!OperatingSystem.IsMacOS())
                return tracked;

            var keyboard = FromAppKit();

            // Kept as a line so the fault stays visible on the day it matters: a disagreement is Uno's
            // table holding a key the keyboard has let go of (or the reverse), and what the app did with
            // the answer is in the lines around it.
            if (keyboard != tracked)
                Said.Line("input", $"key state: the keyboard has {keyboard}, Uno's table has {tracked}");

            return keyboard;
        }
    }

    /// <summary>
    /// Whether one key is down now, as WinUI reports it. On Windows this reads the keyboard. Elsewhere it
    /// reads Uno's table of the key events this app has received. It is used for a hold that the app times
    /// itself, for example Right held in Pegasus to scroll the radio wheel. A key whose release went to
    /// another window then does not count as still held.
    /// </summary>
    public static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>
    /// Whether this key going down is the keyboard repeating a key that is held, rather than a new press.
    ///
    /// <para>On macOS <c>KeyStatus.WasKeyDown</c> is always false. Uno's mac host builds the key status
    /// from the scan code only (<c>MacOSWindowHost.CreateArgs</c>, Uno 6.7.65). Its native host also passes
    /// AppKit's repeats on as ordinary key-downs. So this asks AppKit for the event being handled. Uno
    /// raises the key events synchronously during AppKit's dispatch of the key-down, because the native
    /// side waits for the handled flag. So <c>[NSApp currentEvent]</c> is that key-down. If it is not a
    /// key-down, the answer is false.</para>
    /// </summary>
    public static bool IsRepeat(KeyRoutedEventArgs e)
    {
        if (!OperatingSystem.IsMacOS())
            return e.KeyStatus.WasKeyDown;

        var current = SendPointer(SendPointer(NsApplication.Class, NsApplication.Shared), NsApplication.CurrentEvent);
        if (current == 0)
            return false;

        // -isARepeat raises for anything but a key event, and an Objective-C exception through this call
        // ends the process. The type is checked first.
        if (SendNUInt(current, NsEvent.Type) != KeyDownType)
            return false;

        return SendByte(current, NsEvent.IsARepeat) != 0;
    }

    /// <summary>NSEventTypeKeyDown, from AppKit's NSEvent.h.</summary>
    private const nuint KeyDownType = 10;

    private static Modifier FromTracker()
    {
        var held = Modifier.None;
        if (Down(VirtualKey.Shift) || Down(VirtualKey.LeftShift) || Down(VirtualKey.RightShift))
            held |= Modifier.Shift;
        if (Down(VirtualKey.Control) || Down(VirtualKey.LeftControl) || Down(VirtualKey.RightControl))
            held |= Modifier.Control;
        if (Down(VirtualKey.Menu) || Down(VirtualKey.LeftMenu) || Down(VirtualKey.RightMenu))
            held |= Modifier.Option;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
            held |= Modifier.Command;
        return held;

        // A flag, not an equality: Locked is set alongside Down while caps or num lock is on.
        static bool Down(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    // NSEventModifierFlags, from AppKit's NSEvent.h.
    private const ulong ShiftFlag = 1UL << 17;
    private const ulong ControlFlag = 1UL << 18;
    private const ulong OptionFlag = 1UL << 19;
    private const ulong CommandFlag = 1UL << 20;

    private static Modifier FromAppKit()
    {
        var flags = (ulong)SendNUInt(NsEvent.Class, NsEvent.ModifierFlags);

        var held = Modifier.None;
        if ((flags & ShiftFlag) != 0) held |= Modifier.Shift;
        if ((flags & ControlFlag) != 0) held |= Modifier.Control;
        if ((flags & OptionFlag) != 0) held |= Modifier.Option;
        if ((flags & CommandFlag) != 0) held |= Modifier.Command;
        return held;
    }

    /// <summary>Looked up on first use, which happens on macOS only. AppKit is normally loaded by the Uno
    /// host. It is also loaded here, so a test process without it still gets the class and not
    /// nil.</summary>
    private static class NsEvent
    {
        public static readonly nint Class = Load();
        public static readonly nint ModifierFlags = sel_registerName("modifierFlags");
        public static readonly nint Type = sel_registerName("type");
        public static readonly nint IsARepeat = sel_registerName("isARepeat");

        private static nint Load()
        {
            NativeLibrary.TryLoad("/System/Library/Frameworks/AppKit.framework/AppKit", out _);
            return objc_getClass("NSEvent");
        }
    }

    /// <summary>The application object, and the event it is handling. Looked up on first use, on macOS
    /// only. AppKit is loaded by <see cref="NsEvent"/> first.</summary>
    private static class NsApplication
    {
        public static readonly nint Class = NsEvent.Class != 0 ? objc_getClass("NSApplication") : 0;
        public static readonly nint Shared = sel_registerName("sharedApplication");
        public static readonly nint CurrentEvent = sel_registerName("currentEvent");
    }

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint SendNUInt(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint receiver, nint selector);

    /// <summary>A BOOL return: a signed char on x86_64 and a bool on arm64, one byte either way.</summary>
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(nint receiver, nint selector);
}

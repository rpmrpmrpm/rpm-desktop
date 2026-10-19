using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using RPM.Desktop.Services.Platform;
using Windows.System;
using Windows.UI.Core;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="HeldKeys"/> reads the keyboard, not Uno's table of key events.
///
/// <para>On macOS, a Cmd-Tab can leave the Uno key table wrong. The Cmd key-down reaches rpm, but its
/// key-up goes to the next app. Uno clears the table only when the window's activation changes, and the
/// macOS host never reports that. These tests put the table into that state and compare the answer with a
/// second read of the keyboard through CoreGraphics. So they work whatever keys are held down while they
/// run.</para>
/// </summary>
public class HeldKeysTests
{
    private static readonly Type Tracker =
        typeof(VirtualKey).Assembly.GetType("Uno.UI.Core.KeyboardStateTracker", throwOnError: true)!;

    private static void TableSays(VirtualKey key) =>
        Tracker.GetMethod("OnKeyDown", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [key]);

    private static void ClearTable() =>
        Tracker.GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);

    /// <summary>The keyboard, read through CoreGraphics rather than AppKit. It is the same hardware state
    /// read by a different route, so an error in <see cref="HeldKeys"/> shows up as a difference.</summary>
    private static Modifier Keyboard()
    {
        var flags = CGEventSourceFlagsState(CombinedSessionState);
        var held = Modifier.None;
        if ((flags & (1UL << 17)) != 0) held |= Modifier.Shift;
        if ((flags & (1UL << 18)) != 0) held |= Modifier.Control;
        if ((flags & (1UL << 19)) != 0) held |= Modifier.Option;
        if ((flags & (1UL << 20)) != 0) held |= Modifier.Command;
        return held;
    }

    private const int CombinedSessionState = 0;

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern ulong CGEventSourceFlagsState(int stateId);

    [Fact]
    public void Cmd_left_down_in_Uno_table_by_a_Cmd_Tab_is_read_from_the_keyboard_instead()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            TableSays(VirtualKey.LeftWindows);

            // The table says Cmd is down, whatever the keyboard really holds. This is the wrong answer that
            // HeldKeys must not return.
            Assert.True(InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows)
                .HasFlag(CoreVirtualKeyStates.Down));

            Assert.Equal(Keyboard(), HeldKeys.Now);
        }
        finally
        {
            ClearTable();
        }
    }

    [Fact]
    public void Every_modifier_left_down_in_Uno_table_is_read_from_the_keyboard_instead()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            TableSays(VirtualKey.LeftShift);
            TableSays(VirtualKey.LeftControl);
            TableSays(VirtualKey.LeftMenu);
            TableSays(VirtualKey.RightWindows);

            Assert.Equal(Keyboard(), HeldKeys.Now);
        }
        finally
        {
            ClearTable();
        }
    }
}

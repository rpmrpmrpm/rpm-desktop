using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Services.Platform;

/// <summary>Takes a window off the screen without taking it out of the app.
///
/// <para><b>What this is for.</b> At startup a splash window is shown, and the main window must not appear
/// behind it. See <see cref="Presentation.Startup.SplashWindow"/>. Hiding the window outright
/// (<c>ShowWindow(SW_HIDE)</c>) would also stop the desktop compositor from drawing it. The interface is
/// built during those seconds, so the first frame after the window returns would show a layout that was
/// never rendered. Cloaking is the compositor's own answer. The window keeps rendering, and the user cannot
/// see it. The Windows shell uses cloaking for a suspended app whose live thumbnail it still needs.</para>
///
/// <para><b>On macOS, the window keeps a small alpha that the compositor still counts as visible.</b>
/// AppKit has no cloak. Both obvious methods hide the window, which is what cloaking must avoid.
/// <c>orderOut:</c> removes the window from the window list. <c>alphaValue = 0</c> makes
/// <c>occlusionState</c> report notVisible, and AppKit uses that report to stop sending display updates to
/// the window. So the window is set to <see cref="MacHidden"/> instead. This is one step of alpha, the
/// smallest that the compositor can represent. It looks invisible, and it is not zero, so the window is
/// still treated as visible and drawn.</para>
///
/// <para>On other platforms this returns false, and the startup does not use the new path. The splash
/// stays the black overlay inside the window.</para>
/// </summary>
internal static class WindowCloak
{
    /// <summary>What "hidden" is on macOS: 1/255, the smallest alpha the compositor can put on screen.
    /// </summary>
    /// <remarks>This is not zero, and that difference is the point of cloaking rather than
    /// hiding. AppKit counts a window as visible when its alpha is nonzero. A window that AppKit counts as
    /// not visible can stop being drawn. The interface is built in that state, so the first frame that
    /// anyone saw would be a layout that was never rendered. At 1/255 the window is drawn like any other.
    /// What reaches the screen is 0.4% of it, over the desktop, for about two seconds.
    /// </remarks>
    private const double MacHidden = 1.0 / 255.0;

    /// <summary>The title the app's own window wears, which is how it is found on macOS. The splash
    /// card deliberately wears a different one: <c>FindWindowHandle</c> matches on the title and
    /// answers with the first window that has it.</summary>
    private const string MacWindowTitle = "rpm";

    /// <summary>Cloaks the window, or brings it back.
    /// </summary>
    /// <returns>True when the compositor took
    /// the instruction. False means this machine cannot do it. The caller must not then show a splash over
    /// a window it cannot hide.</returns>
    public static bool Apply(Window window, bool cloaked)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (OperatingSystem.IsMacOS())
            return ApplyMac(cloaked);

        if (!OperatingSystem.IsWindows())
            return false;

        if (NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
            return false;

        return Apply(hwnd, cloaked);
    }

    /// <summary>The same thing on macOS, where the window is found by title rather than handed in.
    /// </summary>
    /// <remarks>
    /// <para><b>There is nothing to find until the window has been activated.</b> Uno
    /// creates the NSWindow when the window is first shown, not when it is constructed. A cloak requested
    /// before that returns false. So on this head the startup cloaks the window just AFTER <c>Activate</c>,
    /// not before it. The two calls must be in the same turn. The window server composites on the next pass
    /// through the run loop. An alpha set synchronously is applied before the first frame reaches the
    /// screen.</para>
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool ApplyMac(bool cloaked)
    {
        try
        {
            var window = MacWindowChrome.FindWindowHandle(MacWindowTitle);
            if (window == 0)
                return false;

            MacWindowCard.SetAlpha(window, cloaked ? MacHidden : 1.0);

            // Invisible is not the same as out of the way. AppKit hit-tests a window at 1/255 like any
            // other window. A click over it during the splash would land on an interface that nobody can
            // see. A click over it could start a record that the user cannot see. While cloaked, clicks go
            // to whatever the user can see behind the window. Every uncloak goes through this same call, so
            // the mouse setting and the alpha always change together.
            MacWindowCard.IgnoreMouse(window, cloaked);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc cref="Apply(Window, bool)"/>
    public static bool Apply(IntPtr hwnd, bool cloaked)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return false;

        try
        {
            var value = cloaked ? 1 : 0;
            var hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));
            if (hr != 0)
                RPM.Core.Diagnostics.Said.Line("splash", $"dwm refused the cloak — 0x{hr:X8}");
            return hr == 0;
        }
        catch
        {
            // No dwmapi to talk to. The window stays where it is, which is visible.
            return false;
        }
    }

    /// <summary>Puts the window at the top of the stack and gives it the focus.
    /// </summary>
    /// <remarks>
    /// <para>Uncloaking does not do this by itself. The window was activated at launch and has been
    /// invisible ever since. Nothing has raised it, so whatever the user was looking at is still in front
    /// of it. <c>Window.Activate</c> does not help. The window already counts as active in its own
    /// bookkeeping, so calling it again changes nothing. Without this call, the app can come up behind
    /// other windows.</para>
    ///
    /// <para>This is not foreground stealing. The splash window belongs to this process and has the
    /// foreground at the moment it is called, which is the condition Windows sets for handing it on.</para>
    /// </remarks>
    public static void Raise(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (OperatingSystem.IsMacOS())
        {
            try { MacWindowCard.Raise(MacWindowChrome.FindWindowHandle(MacWindowTitle)); }
            catch { /* the window stays where it is in the stack */ }
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

        if (NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
            return;

        try
        {
            SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        catch { /* the window stays where it is in the stack */ }
    }

    private const int DWMWA_CLOAK = 13;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

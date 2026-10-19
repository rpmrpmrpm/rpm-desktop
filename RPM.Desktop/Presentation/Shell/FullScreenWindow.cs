using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// Makes the app's window full screen, and returns it to normal.
///
/// <para>This is shared, because two surfaces need it and there is only one window: the Pegasus player
/// (<see cref="Presentation.Pegasus.PegasusPlayer"/>) and the gallery's visualizer host
/// (<see cref="Presentation.Viz.VizFullScreen"/>). Both cover the whole window with their own content
/// either way. This class adds the TITLE BAR and the desktop around it. That step depends on the platform
/// and the window manager, so it is kept in one place rather than in two copies.</para>
///
/// <para>The state it saves is static, for the same reason: there is one window, so there is one state to
/// put back. The first surface that asks captures how the window was. The last one out restores it. A
/// second call to <c>Set(true)</c> while the window is already full screen must not save "full screen"
/// as the state to return to. If it did, the player would never get back to its own size.</para>
/// </summary>
internal static class FullScreenWindow
{
    /// <summary>Real full screen where the platform offers it. On macOS this is AppKit's own
    /// <c>toggleFullScreen:</c>. That is the same path the green button takes, and it avoids the
    /// managed <c>NSWindow.setFrame</c> abort that <c>AppWindow.MoveAndResize</c> hits on Uno-mac.
    ///
    /// On Windows, Uno's full screen presenter only maximizes the window to the work area. That leaves the
    /// taskbar showing over the picture. So this code removes the borders and covers the whole monitor
    /// rectangle, taskbar strip included. Windows treats an edge-to-edge foreground window as full screen
    /// and hides the taskbar. The app's borderless chrome and its maximized or size state are restored on
    /// the way back.
    /// </summary>
    public static void Set(bool on)
    {
        if (OperatingSystem.IsMacOS())
        {
            try { MacWindowChrome.SetFullScreen("rpm", on); }
            catch { /* best-effort: the surface covers the window regardless */ }
            return;
        }

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (App.AppWindow is not { } window
                || window.AppWindow is not { } aw
                || RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(aw) is not { } op)
                return;

            if (Services.Platform.NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
            {
                // Logged, because the failure is otherwise invisible. Without a handle none of the code
                // below runs. The result is the app's ordinary window with a picture in it: full screen
                // was asked for, but nothing was done and nothing was logged.
                RPM.Core.Diagnostics.Said.Line("fullscreen", "no native handle — window left as it was");
                return;
            }

            if (on)
                Enter(hwnd, aw, op);
            else
                Leave(hwnd, op);

            // The title bar is removed with the chrome. While the shell is on screen, Windows is given
            // the caption strip as a caption region (ShellPage.RefreshCaptionRegions). A full-screen
            // window that can still be dragged by its top edge would be pulled off the monitor it
            // fills, so the regions are refreshed here.
            ShellPage.Current?.RefreshCaptionRegions();
        }
        catch { /* best-effort: the surface covers the window regardless */ }
    }

    /// <summary>Whether a surface has put the window in full screen. The shell reads this, and it does not
    /// use its caption region while this is true.</summary>
    internal static bool IsOn => _savedStyle != IntPtr.Zero;

    private static void Enter(IntPtr hwnd, Microsoft.UI.Windowing.AppWindow aw,
        Microsoft.UI.Windowing.OverlappedPresenter op)
    {
        // What the window was, captured ONCE. Switching from one full-screen surface to the other comes
        // back through here, and by then the window is borderless and covers the monitor. A second
        // capture would record the full-screen state as the thing to return to, and the player would
        // never find its way back to its own size or its own frame. Zero means nothing is saved yet.
        // Leaving clears it again.
        if (_savedStyle == IntPtr.Zero)
        {
            _savedStyle = GetWindowLongPtrW(hwnd, GWL_STYLE);

            _wasMaximized = op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
            _restoreBounds = _wasMaximized
                ? null
                : new Windows.Graphics.RectInt32
                {
                    X = aw.Position.X,
                    Y = aw.Position.Y,
                    Width = aw.Size.Width,
                    Height = aw.Size.Height,
                };
        }

        // The taskbar (Shell_TrayWnd) is a TOPMOST window, so a normal window that only covers the
        // monitor still shows it. Windows' auto-hide rule for full-screen windows is not reliable. So
        // this code first removes the chrome. Then it un-maximizes the window SYNCHRONOUSLY
        // (ShowWindow, not the presenter's async Restore, which would race the resize and shrink the
        // window). Then it covers the monitor's whole rectangle AS A TOPMOST window (HWND_TOPMOST, no
        // SWP_NOZORDER), so it sits above the taskbar. Topmost is cleared on the way out.
        op.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);

        // Then remove the sizing frame by hand, because dropping the border does not remove it.
        // WS_THICKFRAME is what makes an edge draggable, and SetBorderAndTitleBar leaves it in place.
        // Without this step the picture could cover the monitor while its edges could still be grabbed
        // and pulled out of full screen. The caption and the min and max boxes are removed too. There
        // is no title bar to carry them, but Win+Up or a double-click on a corner would still act on
        // them.
        //
        // Put back verbatim on the way out, from _savedStyle above.
        SetWindowLongPtrW(hwnd, GWL_STYLE, new IntPtr(_savedStyle.ToInt64()
            & ~(WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX | WS_CAPTION)));

        // The presenter's own flag as well, where Uno implements it. On its own it is not enough. It
        // throws on some backends, so it gets its own guard and does not take the transition down with
        // it.
        try { op.IsResizable = false; } catch { }

        // DisplayArea.OuterBounds throws NotImplementedException on Uno Skia, where only WorkArea is
        // implemented. So the full monitor rectangle, including the taskbar strip, is read from Win32.
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
        var r = mi.rcMonitor;

        // Clears WS_MAXIMIZE so the resize sticks. A maximized window snaps to the work area.
        ShowWindow(hwnd, SW_RESTORE);
        // Do not use SWP_NOACTIVATE here. The surface must keep the keyboard so that Escape still exits.
        SetWindowPos(hwnd, HWND_TOPMOST, r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_FRAMECHANGED);
        SetForegroundWindow(hwnd);

        // The two edges that no style word controls are handled by Edges.
        Edges(hwnd, square: true);
    }

    /// <summary>Squares off the window's corners and removes the thin line around the window, or restores
    /// both. The skin does the same on its way in, so both live in one place. See
    /// <see cref="Services.Platform.WindowEdges"/> for the reason.</summary>
    private static void Edges(IntPtr hwnd, bool square)
        => Services.Platform.WindowEdges.Square(hwnd, square, "fullscreen");

    private static void Leave(IntPtr hwnd, Microsoft.UI.Windowing.OverlappedPresenter op)
    {
        Edges(hwnd, square: false);   // An ordinary window gets the corners the desktop gives it

        // Clear topmost. Then restore the app's borderless chrome (title bar removed, sizing border
        // kept; see App.OnLaunched) and the earlier maximized or size state.
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // The style goes back BEFORE anything restores a size. The order matters. Windows maximizes a
        // window with no sizing frame and no caption to the FULL MONITOR, not to the work area. So
        // op.Maximize() below would put the player edge to edge over the taskbar if the style were
        // still stripped. Restoring the style first makes it maximize to the work area, like any other
        // window.
        if (_savedStyle != IntPtr.Zero)
        {
            SetWindowLongPtrW(hwnd, GWL_STYLE, _savedStyle);
            _savedStyle = IntPtr.Zero;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        try { op.IsResizable = true; } catch { }
        op.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        if (_wasMaximized)
            op.Maximize();
        else if (_restoreBounds is { } bounds)
            // Win32 is used because Uno does not implement AppWindow.MoveAndResize. Without this call
            // the window would stay the size the surface left it.
            SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        _restoreBounds = null;
    }

    /// <summary>Whether the window was maximized before it went full screen, so the borderless state is
    /// restored faithfully on the way back (Windows).</summary>
    private static bool _wasMaximized;

    /// <summary>The window's bounds before it went full screen, restored on the way back when it was not
    /// maximized (Windows).</summary>
    private static Windows.Graphics.RectInt32? _restoreBounds;

    /// <summary>The window's style word as it was before any full-screen surface touched it, put back
    /// verbatim on the way out (Windows).</summary>
    private static IntPtr _savedStyle;

    // The mouse pointer

    /// <summary>
    /// Shows or hides the mouse pointer.
    /// </summary>
    /// <remarks>Win32 is used rather than <c>ProtectedCursor</c>, which is how the rest of the app
    /// sets cursors. WinUI can choose a SHAPE but has no way to ask for "no cursor at all", so it
    /// cannot hide the pointer.
    /// <para>ShowCursor keeps an internal counter, not a flag. The pointer is hidden while the count is
    /// below zero, so every hide must be balanced by a show. Two hides in a row take the count to -2,
    /// and one show does not bring it back. A listener would then have no pointer anywhere until the
    /// app restarted. This flag keeps the calls balanced. It is static, like the state above, because
    /// there is one pointer and two surfaces that hide it.</para>
    /// </remarks>
    public static void SetCursorHidden(bool hidden)
    {
        if (hidden == _cursorHidden || !OperatingSystem.IsWindows())
            return;
        try
        {
            ShowCursor(!hidden);
            _cursorHidden = hidden;
        }
        catch { /* best effort: a visible pointer is not a failure */ }
    }

    private static bool _cursorHidden;

    // Win32 declarations

    private const int SW_RESTORE = 9;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;   // The draggable sizing edge
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    private const uint MONITOR_DEFAULTTONEAREST = 2;


    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // The 64-bit forms are used because the app ships as win-x64. GetWindowLong would truncate a style
    // word on that platform.
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}

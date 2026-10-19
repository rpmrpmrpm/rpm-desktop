using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Platform;

/// <summary>Removes the native title bar from the main window on Linux. Uno cannot do this by itself.
///
/// <para>The presenter calls <c>SetBorderAndTitleBar(true, false)</c>. That goes through Uno's X11 backend,
/// which clears only the TITLE bit of <c>_MOTIF_WM_HINTS</c>. The other decoration bits (resize handle,
/// menu, minimise, maximise) stay set. KWin, and mutter too, treats the decoration mask as all or nothing:
/// any non-zero mask means "decorated", and only a full zero removes the bar. So this class writes the
/// property itself. Decorations are 0, and all functions are allowed, so resize, move, minimise, maximise
/// and close stay available through the window menu and the taskbar. The property is typed as
/// <c>_MOTIF_WM_HINTS</c>. A property written with the type CARDINAL is ignored by the window
/// manager.</para>
///
/// <para>Either order works when this races Uno's own write. Uno's SetBorderAndTitleBar reads the property
/// back, clears the Title bit and rewrites the rest. If this class writes first, the decorations stay at
/// zero. If Uno writes first, this class overwrites a mask that KWin was ignoring anyway. KWin applies the
/// change live (checked on 6.x under XWayland). So catching the window before the window manager maps it
/// is for appearance only, not correctness. The scan starts early so that it usually wins, and no native
/// bar flashes between the map and the reveal.</para>
///
/// <para>Only the window titled "rpm" with the app's own WM_CLASS is changed. The same process also owns
/// the gallery and MixLab windows (DEBUG) and the player popout, and those keep the chrome they asked for.
/// This is best effort. With no X11 session, a missing libX11, or a window manager that ignores Motif
/// hints, the native bar stays where it was. It never throws into the caller.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxWindowChrome
{
    private const string WindowTitle = "rpm";
    private const string WmClassToken = "RPM.Desktop";   // what Uno's X11 backend sets for this app

    // _MOTIF_WM_HINTS: flags = FUNCTIONS | DECORATIONS. Functions = all but ALL (resize, move, minimise,
    // maximise, close). Decorations = 0, the all-or-nothing zero that KWin requires.
    private static readonly nint[] BorderlessHints = { 0x3, 0x3e, 0x0, 0x0, 0x0 };

    // The window appears within a frame or two of Activate. The long limit covers a slow cold start.
    private static readonly TimeSpan WatchDuration = TimeSpan.FromSeconds(12);
    private const int ScanMs = 15;

    private static readonly nint XA_WM_CLASS = 67;
    private const int PropModeReplace = 0;
    private const int AnyPropertyType = 0;
    private const int InputOutput = 1;

    // Keep the delegate alive for as long as it may be installed (GC must not collect it).
    private static readonly XErrorHandler NoopErrorHandler = (_, _) => 0;

    /// <summary>Fire-and-forget: watches for the main window (ideally before the WM maps it) and
    /// strips its native decorations. Call once, right after the window is activated.</summary>
    public static void DropTitleBar()
    {
        _ = Task.Run(async () =>
        {
            try { await ApplyAsync(); }
            catch { /* best-effort cosmetic; never break launch */ }
        });
    }

    private static async Task ApplyAsync()
    {
        var display = XOpenDisplay(null);
        if (display == IntPtr.Zero) return; // not an X11 / XWayland session

        // A window can close between being found and being changed. The resulting BadWindow error must not
        // reach Xlib's default handler, which exits the process.
        var previousHandler = XSetErrorHandler(NoopErrorHandler);
        try
        {
            var root = XDefaultRootWindow(display);
            var motif = XInternAtom(display, "_MOTIF_WM_HINTS", false);
            var netWmName = XInternAtom(display, "_NET_WM_NAME", false);
            var netClientList = XInternAtom(display, "_NET_CLIENT_LIST", false);

            var deadline = Environment.TickCount64 + (long)WatchDuration.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                // Before mapping, the window is a direct child of the root. Once KWin manages it, the
                // window is moved into a frame, and only _NET_CLIENT_LIST still names it. Both lists are
                // scanned on every pass. Whichever one finds the window first wins.
                foreach (var w in RootChildren(display, root).Concat(ClientList(display, root, netClientList)))
                {
                    if (!IsOurMainWindow(display, w, netWmName))
                        continue;

                    var bytes = new byte[BorderlessHints.Length * 8];
                    Buffer.BlockCopy(BorderlessHints.Select(h => (long)h).ToArray(), 0, bytes, 0, bytes.Length);
                    _ = XChangeProperty(display, w, motif, motif, 32, PropModeReplace, bytes, BorderlessHints.Length);
                    _ = XFlush(display);
                    return;
                }

                await Task.Delay(ScanMs);
            }
        }
        finally
        {
            XSetErrorHandler(previousHandler);
            _ = XCloseDisplay(display);
        }
    }

    /// <summary>The main window and nothing else: the title says which of this process's windows it
    /// is, the class keeps another app that happens to be titled "rpm" out of reach.</summary>
    private static bool IsOurMainWindow(IntPtr display, IntPtr w, nint netWmName)
    {
        var title = ReadString(display, w, netWmName) ?? ReadString(display, w, (nint)39 /* XA_WM_NAME */);
        if (!string.Equals(title, WindowTitle, StringComparison.Ordinal))
            return false;
        var wmClass = ReadString(display, w, XA_WM_CLASS);
        return wmClass is not null && wmClass.Contains(WmClassToken, StringComparison.Ordinal);
    }

    /// <summary>Direct children of the root: InputOutput, not override-redirect (menus,
    /// tooltips).</summary>
    private static List<IntPtr> RootChildren(IntPtr display, IntPtr root)
    {
        var result = new List<IntPtr>();
        if (XQueryTree(display, root, out _, out _, out var children, out var count) == 0 || children == IntPtr.Zero)
            return result;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var w = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                if (XGetWindowAttributes(display, w, out var attrs) == 0) continue;
                if (attrs.c_class != InputOutput || attrs.override_redirect != 0) continue;
                result.Add(w);
            }
        }
        finally { _ = XFree(children); }
        return result;
    }

    /// <summary>The windows the WM already manages, read off the root's <c>_NET_CLIENT_LIST</c>.</summary>
    private static List<IntPtr> ClientList(IntPtr display, IntPtr root, nint netClientList)
    {
        var result = new List<IntPtr>();
        if (XGetWindowProperty(display, root, netClientList, IntPtr.Zero, (IntPtr)1024, false, (IntPtr)AnyPropertyType,
                out _, out var format, out var nitems, out _, out var data) != 0)
            return result;
        try
        {
            if (data != IntPtr.Zero && format == 32)
                for (long i = 0; i < (long)nitems; i++)
                    result.Add(Marshal.ReadIntPtr(data, (int)(i * IntPtr.Size)));
        }
        finally { if (data != IntPtr.Zero) _ = XFree(data); }
        return result;
    }

    private static string? ReadString(IntPtr display, IntPtr window, nint prop)
    {
        if (XGetWindowProperty(display, window, prop, IntPtr.Zero, (IntPtr)1024, false, (IntPtr)AnyPropertyType,
                out _, out var format, out var nitems, out _, out var data) != 0)
            return null;
        try
        {
            return data != IntPtr.Zero && format == 8 && (long)nitems > 0
                ? Marshal.PtrToStringUTF8(data, (int)nitems)
                : null;
        }
        finally { if (data != IntPtr.Zero) _ = XFree(data); }
    }

    // ── libX11 ──────────────────────────────────────────────────────────────
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern nint XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6")] private static extern XErrorHandler? XSetErrorHandler(XErrorHandler? handler);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(
        IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent,
        out IntPtr children, out uint nChildren);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowAttributes(IntPtr display, IntPtr window, out XWindowAttributes attributes);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(
        IntPtr display, IntPtr window, nint property, nint type, int format, int mode,
        byte[] data, int nelements);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(
        IntPtr display, IntPtr window, nint property, IntPtr longOffset, IntPtr longLength,
        bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr nitems, out IntPtr bytesAfter, out IntPtr prop);

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int x, y, width, height, border_width, depth;
        public IntPtr visual;
        public IntPtr root;
        public int c_class, bit_gravity, win_gravity, backing_store;
        public IntPtr backing_planes, backing_pixel;
        public int save_under;
        public IntPtr colormap;
        public int map_installed, map_state;
        public IntPtr all_event_masks, your_event_mask, do_not_propagate_mask;
        public int override_redirect;
        public IntPtr screen;
    }
}

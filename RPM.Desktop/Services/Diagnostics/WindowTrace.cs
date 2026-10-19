using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Services.Diagnostics;

/// <summary>
/// Three sizes that should be the same, written down each time any of them changes.
///
/// <para><b>The question this settles.</b> After a minimize, and sometimes after an ordinary resize, the
/// window can come back with the interface drawn small in the bottom-left corner of a black window. The
/// mechanism is believed to be this, but no run has checked it. The Win32 Skia host passes every WM_SIZE to
/// the XAML bounds. A minimized window has a client area of 0x0. Nothing pauses the render thread while
/// that is true. This trace is the measurement that settles it.</para>
///
/// <para><b>How to read it.</b> Three numbers per line:</para>
/// <list type="bullet">
/// <item><c>client</c> is the client area that Win32 reports for the window. The renderer must present into
/// this size.</item>
/// <item><c>bounds</c> is the size that XAML has for the window. This is what the host passed on.</item>
/// <item><c>root</c> is the size at which the content was actually laid out.</item>
/// </list>
///
/// <para>There are two possible answers, and they need different fixes:</para>
/// <list type="bullet">
/// <item><b><c>client</c> differs from <c>bounds</c> or <c>root</c></b>: the new size never reached the
/// tree, or it reached the tree and the tree did not re-run. This is a layout fault in the app. The fix is
/// to measure again when the window is restored.</item>
/// <item><b>All three are equal, but the screen is still wrong</b>: everything above the renderer agrees,
/// and the renderer is still showing a surface that it made at an earlier size. The backend keeps this
/// surface, and nothing in our layout changes it. The fix is in the framework, upstream of the app.</item>
/// </list>
///
/// <para><b>Always on, like everything else in this folder.</b> A diagnostic that has to be switched on
/// before a fault occurs is off when the fault happens. This writes one ring record per size change. A size
/// change happens tens of times in a drag, not thousands of times.</para>
/// </summary>
internal static class WindowTrace
{
    private static Window? _window;

    /// <summary>Starts watching. Called once, after the window has content.</summary>
    public static void Install(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows() || _window is not null)
            return;

        _window = window;

        // The window's own report, raised by the host from WM_SIZE.
        window.SizeChanged += (_, e) => Say("window", e.Size.Width, e.Size.Height);

        // The content reports its size separately from the window. Each event fires only when its own part
        // of the tree changes, so comparing the two shows where a size stopped.
        if (window.Content is FrameworkElement root)
            root.SizeChanged += (_, e) => Say("root", e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>One line: who reported, what they said, and what the operating system says at the same
    /// moment.</summary>
    private static void Say(string who, double reportedWidth, double reportedHeight)
    {
        if (_window is not { } window)
            return;

        var (clientWidth, clientHeight) = ClientSize(window);
        var bounds = window.Bounds;
        var root = window.Content as FrameworkElement;

        // Where the window is on the screen, and whether the shell is moving it rather than the user. The
        // position is recorded too, because the fault has been seen more often in the lower half of the
        // screen. A snap to an edge or to a half resizes the window from the shell, not through the drag's
        // own loop.
        var (left, top, zoomed) = Placement(window);

        // The verdict is written on the line, so the reader gets the answer without working it out.
        var agree = Same(clientWidth, bounds.Width) && Same(clientHeight, bounds.Height)
                 && (root is null || (Same(clientWidth, root.ActualWidth) && Same(clientHeight, root.ActualHeight)));

        RPM.Core.Diagnostics.Scope.Said(
            "window",
            $"{who} {reportedWidth:F0}x{reportedHeight:F0} | " +
            $"client {clientWidth:F0}x{clientHeight:F0} | " +
            $"bounds {bounds.Width:F0}x{bounds.Height:F0} | " +
            $"root {root?.ActualWidth ?? 0:F0}x{root?.ActualHeight ?? 0:F0} | " +
            $"at {left},{top}{(zoomed ? " ZOOMED" : "")} | " +
            $"scale {Scale(window):F2} | " +
            (agree ? "agree" : "DISAGREE"));
    }

    // The scale is on the line because the app sizes every decoded picture by it. ArtScaler.DecodeEdgeFor
    // multiplies the surface size in points by RasterizationScale and picks a size bucket. A display that
    // reports a different scale decodes every cover at a different size. If covers look softer on one
    // display than another, this value shows whether the scale is the cause.
    /// <summary>Within 1.5 pixels. The three values are computed in different places, so a small rounding
    /// difference is not a disagreement.</summary>
    private static bool Same(double a, double b) => Math.Abs(a - b) < 1.5;

    /// <summary>What the operating system says the client area is. Returns zero when there is no handle to
    /// ask, and a zero on the line shows that.</summary>
    private static (double Width, double Height) ClientSize(Window window)
    {
        try
        {
            if (Platform.NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
                return (0, 0);
            if (!GetClientRect(hwnd, out var r))
                return (0, 0);

            // Client pixels are physical, but Bounds and ActualWidth use the window's own scale. Divide
            // here so that the three numbers on the line can be compared.
            var scale = Scale(window);
            return ((r.right - r.left) / scale, (r.bottom - r.top) / scale);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Where the window is on the screen, and whether Windows has it zoomed. A snap to an edge or
    /// to a half sets this as well as a maximize.</summary>
    private static (int Left, int Top, bool Zoomed) Placement(Window window)
    {
        try
        {
            if (Platform.NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
                return (0, 0, false);
            if (!GetWindowRect(hwnd, out var r))
                return (0, 0, false);
            return (r.left, r.top, IsZoomed(hwnd));
        }
        catch
        {
            return (0, 0, false);
        }
    }

    private static double Scale(Window window)
    {
        try
        {
            if (window.Content?.XamlRoot?.RasterizationScale is > 0 and var s)
                return s;
        }
        catch { }
        return 1;
    }


    // ── Win32 ──

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);
}

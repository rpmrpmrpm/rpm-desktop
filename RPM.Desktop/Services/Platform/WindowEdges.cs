using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Platform;

/// <summary>The two edges of a window that belong to the desktop compositor rather than to the window.
///
/// <para>Windows 11 rounds the corners of every top-level window and draws a one-pixel line around it. This
/// happens whatever the window asks for, and removing the frame styles does not change it. On a window that
/// is dressed as something else, such as the skin or a picture that covers the monitor, these edges are the
/// last part of the app's own window that still shows. They look like a pale hairline around the outside,
/// and four corners cut out of the content.</para>
///
/// <para>Windows 10 has neither attribute. It reports this in an HRESULT that is not read. It draws neither
/// edge either, so asking costs nothing.</para>
/// </summary>
internal static class WindowEdges
{
    /// <summary>Squares the window's corners off and takes the hairline away, or gives both back.
    /// </summary>
    /// <param name="tag">Who is asking. It is written to the log when the compositor refuses the
    /// request.</param>
    public static void Square(IntPtr hwnd, bool square, string tag)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            var corners = square ? DWMWCP_DONOTROUND : DWMWCP_DEFAULT;
            var cut = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

            var border = square ? DWMWA_COLOR_NONE : DWMWA_COLOR_DEFAULT;
            var line = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

            if (cut != 0 || line != 0)
                RPM.Core.Diagnostics.Scope.Said(
                    "boot", $"[{tag}] dwm kept its edges — corners 0x{cut:X8}, border 0x{line:X8}");
        }
        catch { /* no dwmapi to talk to. The window keeps the edges it has. */ }
    }

    /// <summary>
    /// Rounds the window's corners and takes the system hairline off it, for a window that draws its
    /// own edge.
    ///
    /// <para>Windows rounds the corners of top-level windows that have a frame. A window whose border and
    /// title bar were removed through the presenter has no frame. So it keeps four square corners until
    /// rounding is requested by name. The splash card does this.</para>
    ///
    /// <para>The hairline is removed with the corners, because the two belong together. The line the compositor
    /// draws is a solid pale edge at the window's own brightness. A card that should look like the app's
    /// other cards needs the edge they have. That edge is one pixel, mostly transparent, and drawn in the
    /// content. Two edges a pixel apart look like a border that was drawn twice.</para>
    /// </summary>
    /// <param name="tag">Who is asking. It is written to the log when the compositor refuses the
    /// request.</param>
    public static void Round(IntPtr hwnd, string tag)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            var corners = DWMWCP_ROUND;
            var cut = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));

            var border = DWMWA_COLOR_NONE;
            var line = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

            // The other way to ask for the same result is to draw no non-client area for this window at
            // all. The border attribute above is ignored on a layered window. This older call works there.
            var policy = DWMNCRP_DISABLED;
            var nc = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));

            // This is written to the ring, not through Debug.WriteLine. Debug.WriteLine is
            // [Conditional("DEBUG")], and this head runs in Release, so a message written that way is never
            // seen. Without the ring line, a refusal of both the corners and the border leaves no evidence
            // except what is on screen.
            RPM.Core.Diagnostics.Scope.Said(
                "boot", $"[{tag}] dwm edges — corners 0x{cut:X8}, border 0x{line:X8}, nc 0x{nc:X8}");
        }
        catch { /* no dwmapi to talk to. The window keeps the edges it has. */ }
    }

    // How the corners are cut, and what colour the hairline round the window is drawn in.
    // DWMWA_COLOR_NONE is "do not draw one".
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DEFAULT = 0;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

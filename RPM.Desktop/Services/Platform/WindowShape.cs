using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Platform;

/// <summary>Cuts a window down to the shape of what it is showing.
///
/// <para><b>Why this exists.</b> The skin is a stack of separate panels with see-through gaps between them.
/// On the Mac head the gaps are real transparency. The NSWindow's backing colour is cleared, and the
/// compositor shows the desktop behind whatever the app does not paint. WinUI has no equivalent. A WinUI
/// window's content is drawn on its own surface, and that surface is opaque. None of the other methods
/// reach it: the extended frame, the blur-behind region and the accent policy were each tried, and the gaps
/// stayed painted.</para>
///
/// <para><b>What this does instead.</b> It cuts the gaps out of the window. A window region says which
/// pixels the window has. Everything else is not part of the window at all, so the desktop is not behind
/// it. It is simply not there. This does not depend on a compositor feature. The same call works whether or
/// not the desktop compositor is running.</para>
///
/// <para>Compared with real transparency, the cut costs two things. The cut edges are hard, so the rounded
/// corners of the panels are not smoothed against the background. And a click in a gap does not reach the
/// window. It lands on whatever is behind it. On the Mac head both stay as they were.</para>
/// </summary>
internal static class WindowShape
{
    /// <summary>Cuts the window to <paramref name="panels"/>, or gives the whole window back when the list
    /// is empty.
    /// </summary>
    /// <param name="panels">Where the panels are, in the client area's own
    /// coordinates, before scaling. The client area holds the app's content. A region is measured from the
    /// window's outer corner. The difference between the two is read from the window, not assumed.</param>
    /// <param name="radius">The panels' corner radius, in the same coordinates.</param>
    /// <param name="scale">Device pixels per coordinate, from the XamlRoot.</param>
    public static void Cut(IntPtr hwnd, IReadOnlyList<Windows.Foundation.Rect> panels, double radius, double scale)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;

        try
        {
            if (panels.Count == 0)
            {
                SetWindowRgn(hwnd, IntPtr.Zero, bRedraw: true);
                return;
            }

            if (!GetWindowRect(hwnd, out var window))
                return;

            var origin = default(Point);
            if (!ClientToScreen(hwnd, ref origin))
                return;

            var dx = origin.X - window.Left;
            var dy = origin.Y - window.Top;
            var ellipse = (int)Math.Round(radius * 2 * scale);

            var whole = IntPtr.Zero;
            foreach (var panel in panels)
            {
                if (panel.Width <= 0 || panel.Height <= 0)
                    continue;

                // The right and bottom edges of a region are exclusive. A panel 440 pixels wide must end at
                // 441, or it is one pixel too narrow.
                var left = dx + (int)Math.Round(panel.X * scale);
                var top = dy + (int)Math.Round(panel.Y * scale);
                var right = dx + (int)Math.Round((panel.X + panel.Width) * scale) + 1;
                var bottom = dy + (int)Math.Round((panel.Y + panel.Height) * scale) + 1;

                var one = ellipse > 0
                    ? CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse)
                    : CreateRectRgn(left, top, right, bottom);
                if (one == IntPtr.Zero)
                    continue;

                if (whole == IntPtr.Zero)
                {
                    whole = one;
                    continue;
                }

                CombineRgn(whole, whole, one, RGN_OR);
                DeleteObject(one);
            }

            if (whole == IntPtr.Zero)
                return;

            // After this call the window owns the region, so it must NOT be deleted. Deleting it would
            // remove the window's own shape. The documented way to restore the normal shape is to set a
            // null region.
            if (SetWindowRgn(hwnd, whole, bRedraw: true) == 0)
                DeleteObject(whole);
        }
        catch { /* no gdi to talk to: the window keeps its own rectangle, gaps and all */ }
    }

    /// <summary>Cuts the window to ONE rounded rectangle, its own, pulled in from its edge by
    /// <paramref name="inset"/> device pixels.
    /// </summary>
    /// <remarks>
    /// <para>The inset is the purpose of this method.
    /// Windows draws a one-pixel line around the outside of a window. On a LAYERED window that line cannot
    /// be turned off. Setting <c>DWMWA_BORDER_COLOR</c> to <c>DWMWA_COLOR_NONE</c> returns S_OK, and the
    /// line stays. A region is not a request. A pixel outside the region is not part of the window. So the
    /// line is cut off instead of being turned off. What is left at the edge is whatever the content draws
    /// there.</para>
    ///
    /// <para>Window coordinates, not client: the line being cut belongs to the window's own edge.</para>
    /// </remarks>
    public static void CutInset(IntPtr hwnd, int inset, double radius, double scale)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;

        try
        {
            if (!GetWindowRect(hwnd, out var window))
                return;

            var width = window.Right - window.Left;
            var height = window.Bottom - window.Top;
            if (width <= inset * 2 || height <= inset * 2)
                return;

            // The +1 is the same as in <see cref="Cut"/>, for the same reason. The right and bottom edges
            // of a region are exclusive. Without the +1, the window is one pixel narrow on those two sides.
            var ellipse = (int)Math.Round(radius * 2 * scale);
            var right = width - inset + 1;
            var bottom = height - inset + 1;
            var region = ellipse > 0
                ? CreateRoundRectRgn(inset, inset, right, bottom, ellipse, ellipse)
                : CreateRectRgn(inset, inset, right, bottom);
            if (region == IntPtr.Zero)
                return;

            // The window owns the region from here. See Cut.
            if (SetWindowRgn(hwnd, region, bRedraw: true) == 0)
                DeleteObject(region);
        }
        catch { /* no gdi to talk to: the window keeps its rectangle and its line */ }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    private const int RGN_OR = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

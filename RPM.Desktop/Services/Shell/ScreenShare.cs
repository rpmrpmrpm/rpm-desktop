using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// How much of the screen the main window is taking.
///
/// <para>The album page asks this question. It opens its groove by itself when its window has the whole
/// screen. In a window that shares the screen, the groove stays folded. The rule is in
/// <c>AlbumPageDensity.GrooveOpensItself</c>. Nothing here decides anything. It only reads two
/// numbers.</para>
///
/// <para><b>Win32 rather than <c>DisplayArea</c>.</b> Uno implements neither
/// <c>DisplayArea.GetFromWindowId</c> nor <c>WorkArea</c>. It compiles and returns nothing. So the managed
/// route would work on the WinUI head and silently fold every groove on the Skia head. See
/// <c>App.ToggleMaximizeMainWindow</c>, which says the same thing about the same API.</para>
///
/// <para><b>The PRIMARY monitor, not the one the window is on.</b> A window dragged to a second screen of a
/// different width is measured against the first, and the answer can be wrong there. Choosing the right
/// monitor needs the window handle and <c>MonitorFromWindow</c>. That is another platform path, for a fold
/// that the reader can undo with one press. Revisit this if anyone works with two screens of very different
/// widths.</para>
/// </summary>
internal static class ScreenShare
{
    /// <summary>The width of the screen and of the window, in physical pixels. Null where either cannot be
    /// read. Null does not mean narrow. The caller opens the groove, so a reading that failed does not
    /// decide for the reader.</summary>
    public static (double Window, double Screen)? Widths()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return WindowsWidths();
    }

    [SupportedOSPlatform("windows")]
    private static (double Window, double Screen)? WindowsWidths()
    {
        try
        {
            const int cxScreen = 0;
            var screen = GetSystemMetrics(cxScreen);
            if (screen <= 0)
                return null;

            if (App.AppWindow?.AppWindow is not { } window)
                return null;

            var width = window.Size.Width;
            return width > 0 ? (width, screen) : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

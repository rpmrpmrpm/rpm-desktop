using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// What the window itself paints under its content, which is a different thing from what the content
/// paints.
///
/// <para><b>Why it is asked for.</b> The skin window stacks panels with gaps between them, and the desktop
/// should show through those gaps. Clearing the content's backgrounds is not enough on its own. Something
/// below the content fills the window, and on both heads it is opaque until it is told otherwise.</para>
///
/// <para><b>The Skia head</b> has one call for it, <c>Uno.UI.Xaml.WindowHelper.SetBackground</c>, which
/// sets the brush Uno's window root paints.</para>
///
/// <para><b>The WinUI head has no way to do this at all.</b> A WinUI window's content is drawn on its own
/// opaque surface. Removing the system backdrop, using a transparent root, or using
/// <c>DwmExtendFrameIntoClientArea</c> with -1 margins (the documented per-pixel alpha route for a desktop
/// window) all leave it opaque. So do <c>DwmEnableBlurBehindWindow</c> with an empty region and the accent
/// policy's TRANSPARENTGRADIENT.</para>
///
/// <para>So on that head the answer is not a background. The gaps are cut out of the window (see
/// <see cref="WindowShape"/>). What remains here is two calls that cost nothing and are correct on heads that
/// compose the window themselves. The system backdrop is dropped so that Mica cannot paint over the casing,
/// and the frame is extended. Neither call makes this window see-through.</para>
/// </summary>
internal static class WindowBackground
{
    /// <summary>Paints the window under its content with <paramref name="brush"/>. A transparent brush
    /// means the desktop shows through wherever the content does not paint.</summary>
    public static void Apply(Window window, Brush brush)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(brush);

#if RPM_WINUI
        var transparent = brush is SolidColorBrush { Color.A: 0 };

        try { window.SystemBackdrop = null; } catch { }

        if (window.Content is Panel panel)
        {
            try { panel.Background = brush; } catch { }
        }

        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (NativeWindow.Handle(window) is not { } hwnd || hwnd == IntPtr.Zero)
                return;

            // -1 on every side means the frame covers the whole client area. Zeros put it back. This does
            // not make the gaps see-through on this head. See the class remark and WindowShape.
            var margins = transparent
                ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
                : new Margins();
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch { /* a machine with no desktop composition: the window stays opaque */ }
#else
        try { Uno.UI.Xaml.WindowHelper.SetBackground(window, brush); }
        catch { }
#endif
    }

#if RPM_WINUI
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);
#endif
}

using Microsoft.UI.Xaml;

namespace RPM.Desktop.Services.Platform;

/// <summary>The operating system's handle for a window, read the way the head in use provides it.
///
/// <para>The two heads reach the handle differently, and neither method works on the other head. On the
/// WinUI head a Window is a real HWND, and <c>WinRT.Interop.WindowNative.GetWindowHandle</c> returns it. On
/// the Skia head the window belongs to Uno's Win32 host, and only that host's own helper knows the handle.
/// <c>Microsoft.UI.Win32Interop.GetWindowFromWindowId</c> throws NotImplementedException there, so it is
/// not used.</para>
///
/// <para>On both heads the process's own main window is the fallback. Without a handle, the callers' work
/// does not run at all. A full-screen picture stays in an ordinary window, and the size trace records
/// zeroes. This app has one window, so the process's main window is that window.</para>
///
/// <para>The logic lives in one place so that every caller gets the same answer.</para>
/// </summary>
internal static class NativeWindow
{
    /// <summary>The window's handle, or null when nothing can name it.</summary>
    public static IntPtr? Handle(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
#if RPM_WINUI
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
                return hwnd;
#else
            if (Uno.UI.Xaml.WindowHelper.GetNativeWindow(window)
                is Uno.UI.NativeElementHosting.Win32NativeWindow native && native.Hwnd != IntPtr.Zero)
                return native.Hwnd;
#endif
        }
        catch { /* The host has no such window type. The process's main window is used instead. */ }

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;
        }
        catch { }

        return null;
    }
}

using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Platform;

/// <summary>Reads clipboard text straight from Win32, going around Uno's own clipboard on this host.
///
/// <para>Uno 6.6's <c>Win32ClipboardExtension.GetContentPackage()</c> reads every format on the clipboard.
/// For <c>CF_OEMTEXT</c> it builds an <see cref="System.Text.Encoding"/> through a <c>Lazy</c> that asks
/// for code page 1. Code page 1 is not a real code page, so this throws <c>ArgumentException: 1 is not a
/// supported code page</c>. The throw happens while the package is built, so it fails the WHOLE read, not
/// just the OEM part. <c>Clipboard.GetContent()</c> never returns, Paste disappears from the TextBox
/// context menu, and Ctrl+V does nothing. Copy still works, because writing never runs that code. That is
/// why the fault looks like a dead paste and not a broken clipboard.</para>
///
/// <para>Windows creates CF_OEMTEXT from CF_UNICODETEXT automatically, so the bad format is present for
/// anything anyone copies. No clipboard content avoids it. A faulted <c>Lazy</c> also caches its exception,
/// so the first failure breaks the clipboard for the rest of the process. For that reason this code reads
/// CF_UNICODETEXT directly, rather than trying to make Uno's path work. macOS uses a different extension
/// and is not affected, so this is used only on Windows.</para>
/// </summary>
public static class Win32Clipboard
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    /// <summary>Whether this host needs the workaround at all.</summary>
    public static bool IsNeeded => OperatingSystem.IsWindows();

    /// <summary>The clipboard's Unicode text, or null when it holds none (or another process is
    /// holding the clipboard open, which is transient and not worth surfacing).</summary>
    public static string? GetText()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            return null;

        // The clipboard is a shared, single-owner resource: a paste while another app is mid-copy
        // simply fails, and retrying briefly is what every Windows app does here.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var handle = GetClipboardData(CF_UNICODETEXT);
                    if (handle == IntPtr.Zero)
                        return null;

                    var pointer = GlobalLock(handle);
                    if (pointer == IntPtr.Zero)
                        return null;

                    try { return Marshal.PtrToStringUni(pointer); }
                    finally { GlobalUnlock(handle); }
                }
                finally { CloseClipboard(); }
            }

            Thread.Sleep(10);
        }

        return null;
    }

    /// <summary>Whether there is text to paste, without materialising it.</summary>
    public static bool HasText() =>
        OperatingSystem.IsWindows() && IsClipboardFormatAvailable(CF_UNICODETEXT);
}

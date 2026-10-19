using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RPM.Core.Files;

/// <summary>
/// Moves a file or folder to the OS trash / recycle bin: a recoverable delete, cross-platform.
/// Returns whether the item <b>actually moved</b>. A delete the OS refused reports false, so the
/// caller must not pretend the item is gone (drop its library row, free its space) unless this
/// returned true.
///
/// <para>Each platform path avoids starting a subprocess for every item. macOS uses
/// <c>NSFileManager trashItemAtURL:</c> (no Finder, no trash sound, no automation entitlement).
/// Windows uses <c>SHFileOperation</c> with <c>FOF_ALLOWUNDO</c> (the same mechanism Explorer's Delete
/// uses). Linux uses <c>gio trash</c>. All three take the path as a value, so there is nothing to escape.</para>
/// </summary>
public static class OsTrash
{
    /// <summary>Moves <paramref name="path"/> to the trash. Returns false if it did not move.</summary>
    public static bool Trash(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            if (OperatingSystem.IsMacOS())
                return MacTrash(path);
            if (OperatingSystem.IsWindows())
                return WindowsTrash(path);
            return RunToCompletion("gio", "trash", path);
        }
        catch
        {
            return false;
        }
    }

    // ── macOS: -[NSFileManager trashItemAtURL:resultingItemURL:error:] ──

    private const string Libobjc = "/usr/lib/libobjc.dylib";

    [DllImport(Libobjc, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    // trashItemAtURL: returns a BOOL, which is a signed char on arm64. Marshalling it as a 4-byte
    // bool would read three bytes of whatever follows in the register.
    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool MsgSendTrash(IntPtr receiver, IntPtr selector,
        IntPtr url, IntPtr resultingUrl, IntPtr error);

    [SupportedOSPlatform("macos")]
    private static bool MacTrash(string path)
    {
        var utf8 = IntPtr.Zero;
        try
        {
            utf8 = Marshal.StringToCoTaskMemUTF8(path);

            var nsString = MsgSend(GetClass("NSString"), Sel("stringWithUTF8String:"), utf8);
            if (nsString == IntPtr.Zero) return false;

            var url = MsgSend(GetClass("NSURL"), Sel("fileURLWithPath:"), nsString);
            if (url == IntPtr.Zero) return false;

            var manager = MsgSend(GetClass("NSFileManager"), Sel("defaultManager"));
            if (manager == IntPtr.Zero) return false;

            // Both out-parameters are optional. Passing null asks for neither the resulting URL nor
            // the NSError. The BOOL is the whole answer we need.
            return MsgSendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"),
                url, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            if (utf8 != IntPtr.Zero) Marshal.FreeCoTaskMem(utf8);
        }
    }

    // ── Windows: SHFileOperation with FOF_ALLOWUNDO (recycle bin) ──

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;   // the app already confirmed
    private const ushort FOF_ALLOWUNDO = 0x0040;        // route to the Recycle Bin (recoverable)
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [SupportedOSPlatform("windows")]
    private static bool WindowsTrash(string path)
    {
        // pFrom is a double-null-terminated list of paths. The LPWStr marshaller copies the whole
        // managed string and appends its own terminator, so one trailing NUL here yields the
        // "item\0\0" the API wants for a single entry.
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        var rc = SHFileOperation(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    // ── Linux: gio trash ──

    private static bool RunToCompletion(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

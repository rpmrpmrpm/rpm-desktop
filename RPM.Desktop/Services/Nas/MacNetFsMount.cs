using System.Runtime.InteropServices;
using System.Text;
using RPM.Core.Nas;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// macOS: <c>NetFSMountURLSync</c> at <c>&lt;local data&gt;/mounts/&lt;NAS device id&gt;</c>, with the
/// "do not browse" flag, so the share appears neither on the desktop nor in Finder's sidebar, and with no
/// login window of the system's own.
/// </summary>
internal sealed unsafe class MacNetFsMount(string mountsFolder) : NasMount
{
    /// <summary>The NetFS error for a refused login.</summary>
    private const int EAUTH = 80;
    private const int EEXIST = 17;

    /// <summary><c>MNT_DONTBROWSE</c> from <c>sys/mount.h</c>.</summary>
    private const int MntDontBrowse = 0x00100000;

    protected override NasMountResult MountCore(string nasDeviceId, NasSmbAddress address, NasCredentials login)
    {
        var mountPoint = Path.Combine(mountsFolder, nasDeviceId);
        if (NasMountPoint.IsLiveNetworkMount(mountPoint))
            return new NasMountResult.Mounted(mountPoint);
        Directory.CreateDirectory(mountPoint);
        // The kernel refuses a mount point reached through a symbolic link (ELOOP), and /var and /tmp are
        // links to /private on macOS. So the folder is mounted at its real path.
        mountPoint = RealPath(mountPoint);

        var url = CFURLCreateWithString(0, CF(address.ShareUrl), 0);
        var pathBytes = Encoding.UTF8.GetBytes(mountPoint);
        nint path;
        fixed (byte* p = pathBytes)
            path = CFURLCreateFromFileSystemRepresentation(0, p, pathBytes.Length, 1);
        var user = CF(login.User);
        var password = CF(login.Password);

        var open = CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);
        var uiKey = CF("UIOption");
        var noUi = CF("NoUI");
        CFDictionarySetValue(open, uiKey, noUi);

        var mount = CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);
        var atDirKey = CF("MountAtMountDir");
        CFDictionarySetValue(mount, atDirKey, BooleanTrue);
        var flagsKey = CF("MountFlags");
        var flags = MntDontBrowse;
        var flagsNumber = CFNumberCreate(0, 3 /* kCFNumberSInt32Type */, &flags);
        CFDictionarySetValue(mount, flagsKey, flagsNumber);

        nint mountPoints = 0;
        int status;
        try
        {
            status = NetFSMountURLSync(url, path, user, password, open, mount, &mountPoints);
        }
        finally
        {
            foreach (var cf in new[] { url, path, user, password, open, mount, uiKey, noUi, atDirKey, flagsKey, flagsNumber, mountPoints })
            {
                if (cf != 0)
                    CFRelease(cf);
            }
        }

        if (status == 0 || (status == EEXIST && NasMountPoint.IsLiveNetworkMount(mountPoint)))
            return new NasMountResult.Mounted(mountPoint);
        if (status == EAUTH)
            return new NasMountResult.WrongLogin();
        return new NasMountResult.Unreachable($"NetFS answered {status}");
    }

    private static string RealPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        var resolved = stackalloc byte[1024]; // PATH_MAX on macOS
        fixed (byte* p = bytes)
            return realpath(p, resolved) is null ? path : Marshal.PtrToStringUTF8((nint)resolved) ?? path;
    }

    private static nint CF(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            return CFStringCreateWithBytes(0, p, bytes.Length, 0x08000100 /* UTF-8 */, 0);
    }

    // Every signature is blittable, so Native AOT needs no marshalling for any of them.
    private const string NetFS = "/System/Library/Frameworks/NetFS.framework/NetFS";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport("libc")]
    private static extern byte* realpath(byte* path, byte* resolved);

    [DllImport(NetFS)]
    private static extern int NetFSMountURLSync(nint url, nint mountPath, nint user, nint password,
        nint openOptions, nint mountOptions, nint* mountPoints);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, byte isExternal);

    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateWithString(nint allocator, nint text, nint baseUrl);

    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte* buffer, nint length, byte isDirectory);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallBacks, nint valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberCreate(nint allocator, nint type, void* value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    // kCFBooleanTrue is a pointer-typed global: the export is the variable's address. The two callback
    // tables are struct globals: the export is the table, and CoreFoundation wants its address.
    private static readonly nint BooleanTrue =
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFBooleanTrue"));
    private static readonly nint KeyCallBacks =
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryKeyCallBacks");
    private static readonly nint ValueCallBacks =
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryValueCallBacks");
}

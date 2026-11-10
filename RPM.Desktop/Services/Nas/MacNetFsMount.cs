using System.Runtime.InteropServices;
using System.Text;
using RPM.Core.Nas;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// macOS: <c>NetFSMountURLSync</c> with the "do not browse" flag, so the share appears neither on the
/// desktop nor in Finder's sidebar, and with no login window of the system's own. NetFS chooses the mount
/// point in <c>/Volumes</c>, as it does for Finder. rpm does not choose a folder of its own: macOS refuses
/// a mount point inside <c>~/Library</c> (EPERM).
/// </summary>
internal sealed unsafe class MacNetFsMount : NasMount
{
    /// <summary>The NetFS error for a refused login.</summary>
    private const int EAUTH = 80;
    private const int EEXIST = 17;

    /// <summary><c>MNT_DONTBROWSE</c> from <c>sys/mount.h</c>.</summary>
    private const int MntDontBrowse = 0x00100000;

    protected override NasMountResult MountCore(NasSmbAddress address, NasCredentials login)
    {
        // A share that is already mounted, by rpm earlier or by the user in Finder, is used where it is.
        // NetFS answers EEXIST for it and does not say where it is mounted. The login is not checked again:
        // the mount belongs to this user of the Mac, who can already read the share.
        if (MountedAt(address) is { } existing)
            return new NasMountResult.Mounted(existing);

        var url = CFURLCreateWithString(0, CF(address.ShareUrl), 0);
        var user = CF(login.User);
        var password = CF(login.Password);

        var open = CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);
        var uiKey = CF("UIOption");
        var noUi = CF("NoUI");
        CFDictionarySetValue(open, uiKey, noUi);

        var mount = CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);
        var flagsKey = CF("MountFlags");
        var flags = MntDontBrowse;
        var flagsNumber = CFNumberCreate(0, 3 /* kCFNumberSInt32Type */, &flags);
        CFDictionarySetValue(mount, flagsKey, flagsNumber);

        nint mountPoints = 0;
        int status;
        string? mountedAt = null;
        try
        {
            status = NetFSMountURLSync(url, 0, user, password, open, mount, &mountPoints);
            if (status == 0)
                mountedAt = FirstString(mountPoints);
        }
        finally
        {
            foreach (var cf in new[] { url, user, password, open, mount, uiKey, noUi, flagsKey, flagsNumber, mountPoints })
            {
                if (cf != 0)
                    CFRelease(cf);
            }
        }

        if (status == 0 && mountedAt is not null)
            return new NasMountResult.Mounted(mountedAt);
        // Mounted by something else between the look in the mount table and the mount call.
        if (status == EEXIST && MountedAt(address) is { } mountedMeanwhile)
            return new NasMountResult.Mounted(mountedMeanwhile);
        if (status == EAUTH)
            return new NasMountResult.WrongLogin();
        return new NasMountResult.Unreachable($"NetFS answered {status}");
    }

    /// <summary>Where this Mac has the share of <paramref name="address"/> mounted, from the mount table.
    /// Null when it is not mounted.</summary>
    internal static string? MountedAt(NasSmbAddress address)
    {
        foreach (var (type, on, from) in MountTable())
        {
            if (type == "smbfs" && address.IsMountedFrom(from) && NasMountPoint.IsLiveNetworkMount(on))
                return on;
        }

        return null;
    }

    // struct statfs with 64-bit inodes, from sys/mount.h: the offsets of f_fstypename (16 bytes),
    // f_mntonname and f_mntfromname (1024 bytes each), and the size of one entry.
    private const int TypeOffset = 72;
    private const int OnOffset = 88;
    private const int FromOffset = 1112;
    private const int StatfsSize = 2168;
    private const int MntNoWait = 2;

    private static List<(string Type, string On, string From)> MountTable()
    {
        var table = new List<(string, string, string)>();
        var count = GetFsStat(null, 0);
        if (count <= 0)
            return table;

        // Room for mounts made between the two calls.
        var buffer = new byte[(count + 8) * StatfsSize];
        fixed (byte* p = buffer)
            count = GetFsStat(p, buffer.Length);
        for (var i = 0; i < count; i++)
        {
            var entry = buffer.AsSpan(i * StatfsSize, StatfsSize);
            table.Add((CString(entry.Slice(TypeOffset, 16)), CString(entry.Slice(OnOffset, 1024)),
                CString(entry.Slice(FromOffset, 1024))));
        }

        return table;
    }

    private static string CString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>On Apple silicon the plain symbol has the 64-bit inode layout. On Intel Macs the plain
    /// symbol has the older layout, and the 64-bit one is <c>getfsstat$INODE64</c>.</summary>
    private static int GetFsStat(byte* buffer, int size) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? getfsstat(buffer, size, MntNoWait)
            : getfsstat_inode64(buffer, size, MntNoWait);

    private static string? FirstString(nint array)
    {
        if (array == 0 || CFArrayGetCount(array) < 1)
            return null;
        const int size = 4096;
        var buffer = stackalloc byte[size];
        return CFStringGetCString(CFArrayGetValueAtIndex(array, 0), buffer, size, Utf8) != 0
            ? Marshal.PtrToStringUTF8((nint)buffer)
            : null;
    }

    private const uint Utf8 = 0x08000100;

    private static nint CF(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            return CFStringCreateWithBytes(0, p, bytes.Length, Utf8, 0);
    }

    // Every signature is blittable, so Native AOT needs no marshalling for any of them.
    private const string NetFS = "/System/Library/Frameworks/NetFS.framework/NetFS";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport("libc", EntryPoint = "getfsstat")]
    private static extern int getfsstat(byte* buffer, int size, int mode);

    [DllImport("libc", EntryPoint = "getfsstat$INODE64")]
    private static extern int getfsstat_inode64(byte* buffer, int size, int mode);

    [DllImport(NetFS)]
    private static extern int NetFSMountURLSync(nint url, nint mountPath, nint user, nint password,
        nint openOptions, nint mountOptions, nint* mountPoints);

    [DllImport(CoreFoundation)]
    private static extern nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, byte isExternal);

    [DllImport(CoreFoundation)]
    private static extern byte CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern nint CFURLCreateWithString(nint allocator, nint text, nint baseUrl);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallBacks, nint valueCallBacks);

    [DllImport(CoreFoundation)]
    private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberCreate(nint allocator, nint type, void* value);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);

    // The two callback tables are struct globals: the export is the table, and CoreFoundation wants its
    // address.
    private static readonly nint KeyCallBacks =
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryKeyCallBacks");
    private static readonly nint ValueCallBacks =
        NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryValueCallBacks");
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RPM.Core.Ripping;

/// <summary>
/// Descrambles a CSS-protected DVD by driving libdvdcss on the raw optical device.
///
/// RPM does NOT ship libdvdcss. It is GPL, and shipping a CSS descrambler is a legal question in its
/// own right (copyleft plus anti-circumvention). Instead, RPM loads a copy the user installed
/// themselves: <c>brew install libdvdcss</c> on macOS, or a <c>libdvdcss-2.dll</c> placed next to the
/// app on Windows. This is the same arrangement HandBrake uses. With none installed,
/// <see cref="LibraryAvailable"/> is false and the caller tells the user the disc is copy-protected.
///
/// Windows note: do not point users at VLC. VLC 3.x ships libdvdread and libdvdnav, which load
/// libdvdcss by name. They export no <c>dvdcss_*</c> symbol and carry no libdvdcss DLL, so a VLC install
/// gives us nothing to load. VideoLAN publishes libdvdcss only as source, so there is no official
/// Windows build. The hint therefore asks for the DLL next to the app: the one place the user fully
/// controls, and the first place this class probes.
///
/// The library's model is the same on both systems. It addresses the disc by ABSOLUTE device sector,
/// on the RAW device, because the mounted filesystem cannot be used to authenticate CSS. So a VOB's
/// sectors must first be located on the physical disc. Each OS offers a way to do that translation
/// without a UDF parser of our own:
/// <list type="bullet">
/// <item>macOS: <c>F_LOG2PHYS_EXT</c> maps a byte offset within a mounted file straight to its physical
/// device offset.</item>
/// <item>Windows: <c>FSCTL_GET_RETRIEVAL_POINTERS</c> returns a file's extent map (VCN to LCN). A
/// DVD-Video volume is a 2048-byte-cluster UDF laid over the start of the disc, so an LCN <em>is</em> the
/// disc sector. The only arithmetic needed is the cluster the byte falls in.</item>
/// </list>
/// </summary>
public sealed class CssDecryptor : IDisposable
{
    /// <summary>A DVD sector. On a DVD-Video volume, one UDF cluster is also one sector.</summary>
    public const int SectorSize = 2048;

    // ── macOS: fcntl(F_LOG2PHYS_EXT) ────────────────────────────────────────────────────────────

    // The fcntl command, and the struct it fills. On input, l2p_devoffset is the file offset and
    // l2p_contigbytes is how many bytes to map. On output, l2p_devoffset is the device offset and
    // l2p_contigbytes is how many bytes are contiguous.
    private const int F_LOG2PHYS_EXT = 65;

    /// <summary>How much F_LOG2PHYS_EXT and FSCTL_GET_RETRIEVAL_POINTERS resolve per call. Both report
    /// contiguity only up to the size asked for. A small query needs one call per sector, while a large
    /// one resolves a whole cell in a single call.</summary>
    private const long MapQueryBytes = 4 * 1024 * 1024;

    // libdvdcss flags.
    private const int DVDCSS_SEEK_KEY = 1 << 1;
    private const int DVDCSS_READ_DECRYPT = 1 << 0;

    private static readonly string[] MacLibraryCandidates =
    [
        "/opt/homebrew/lib/libdvdcss.2.dylib",
        "/usr/local/lib/libdvdcss.2.dylib",
        "/opt/homebrew/lib/libdvdcss.dylib",
        "/usr/local/lib/libdvdcss.dylib",
    ];

    // macOS declares struct log2phys as packed: 20 bytes, with the off_t fields unaligned (contigbytes
    // at offset 4, devoffset at offset 12). A naturally aligned 24-byte struct reads back garbage.
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct Log2Phys
    {
        [FieldOffset(0)] public uint Flags;
        [FieldOffset(4)] public long ContigBytes;
        [FieldOffset(12)] public long DevOffset;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenDelegate([MarshalAs(UnmanagedType.LPStr)] string target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseDelegate(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SeekDelegate(IntPtr ctx, int blocks, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadDelegate(IntPtr ctx, IntPtr buffer, int blocks, int flags);

    // fcntl is variadic (int fcntl(int, int, ...)). On Apple arm64 every variadic argument is passed
    // on the stack. A plain P/Invoke would pass the third argument in a register, so fcntl would read
    // a stack slot holding garbage and crash. Padding the call to eight register arguments puts the
    // real pointer on the stack, where fcntl looks for it. On x64 the fixed and variadic ABIs share
    // the first registers, so the plain three-argument form is correct.
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_arm64(int fd, int cmd,
        long r2, long r3, long r4, long r5, long r6, long r7, ref Log2Phys arg);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_x64(int fd, int cmd, ref Log2Phys arg);

    private static int Fcntl(int fd, int cmd, ref Log2Phys arg) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? fcntl_arm64(fd, cmd, 0, 0, 0, 0, 0, 0, ref arg)
            : fcntl_x64(fd, cmd, ref arg);

    // ── Windows: DeviceIoControl(FSCTL_GET_RETRIEVAL_POINTERS) ───────────────────────────────────

    private const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

    // STARTING_VCN_INPUT_BUFFER: a single LARGE_INTEGER.
    [StructLayout(LayoutKind.Sequential)]
    private struct StartingVcnInputBuffer
    {
        public long StartingVcn;
    }

    // The fixed head of RETRIEVAL_POINTERS_BUFFER: ExtentCount (DWORD), then a LARGE_INTEGER StartingVcn
    // that must be 8-byte aligned. So there are 4 bytes of padding between them. The layout is explicit
    // to show the padding. One EXTENT (NextVcn, Lcn) follows. The first extent is all we need.
    [StructLayout(LayoutKind.Explicit)]
    private struct RetrievalPointersHead
    {
        [FieldOffset(0)] public uint ExtentCount;
        [FieldOffset(8)] public long StartingVcn;
        [FieldOffset(16)] public long FirstNextVcn;
        [FieldOffset(24)] public long FirstLcn;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        ref StartingVcnInputBuffer inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    // ── instance ────────────────────────────────────────────────────────────────────────────────

    private readonly IntPtr _library;
    private readonly IntPtr _ctx;
    private readonly CloseDelegate _close;
    private readonly SeekDelegate _seek;
    private readonly ReadDelegate _read;
    private long _nextSector = -1; // device sector libdvdcss is positioned at, -1 = unknown
    private bool _disposed;

    private CssDecryptor(IntPtr library, IntPtr ctx, CloseDelegate close, SeekDelegate seek, ReadDelegate read)
    {
        _library = library;
        _ctx = ctx;
        _close = close;
        _seek = seek;
        _read = read;
    }

    /// <summary>True when a libdvdcss the user installed is present, so a CSS disc can be descrambled.</summary>
    public static bool LibraryAvailable => FindLibrary() is not null;

    /// <summary>Where libdvdcss lives, or null.</summary>
    public static string? FindLibrary()
    {
        // A copy the user chose through the app comes first, before anything found elsewhere. It is the
        // one location the user picked on purpose, and the only one whose architecture we have checked.
        if (File.Exists(CssLibraryInstaller.InstalledPath))
            return CssLibraryInstaller.InstalledPath;

        if (OperatingSystem.IsMacOS())
            return MacLibraryCandidates.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows())
            return WindowsLibraryCandidates().FirstOrDefault(File.Exists);
        return null;
    }

    /// <summary>The one-line, platform-specific instruction the UI shows when libdvdcss is missing.
    /// On Windows the app can install the file itself (see <see cref="CssLibraryInstaller"/>), so the
    /// hint points there instead of asking the user to find an install folder.</summary>
    public static string InstallHint => OperatingSystem.IsWindows()
        ? "choose a downloaded libdvdcss-2.dll and RPM will install it"
        : "install libdvdcss (brew install libdvdcss) and try again";

    /// <summary>Where a libdvdcss-2.dll that the user installed can be, most specific first: next to
    /// RPM, inside a VLC install, or anywhere on PATH. The VLC folder is checked because users often
    /// copy the DLL there. A stock VLC install does not contain one, so the VLC folder must never be the
    /// place we tell the user to install it from.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> WindowsLibraryCandidates()
    {
        static IEnumerable<string> NamesIn(string dir)
        {
            yield return Path.Combine(dir, "libdvdcss-2.dll");
            yield return Path.Combine(dir, "libdvdcss.dll");
        }

        foreach (var path in NamesIn(AppContext.BaseDirectory))
            yield return path;

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programFiles))
                continue;
            foreach (var path in NamesIn(Path.Combine(programFiles, "VideoLAN", "VLC")))
                yield return path;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var path in NamesIn(dir))
                yield return path;
        }
    }

    /// <summary>Opens the descrambler against a raw device: <c>/dev/rdiskN</c> on macOS, or a drive spec
    /// (<c>D:</c>) on Windows. Throws <see cref="DvdEncryptedException"/> when libdvdcss is missing or the
    /// drive will not authenticate. The message is shown to the user as it is.</summary>
    public static CssDecryptor Open(string rawDevice)
    {
        var path = FindLibrary()
            ?? throw new DvdEncryptedException($"this DVD is copy-protected; {InstallHint} to rip it");

        var library = NativeLibrary.Load(path);
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<OpenDelegate>(NativeLibrary.GetExport(library, "dvdcss_open"));
            var close = Marshal.GetDelegateForFunctionPointer<CloseDelegate>(NativeLibrary.GetExport(library, "dvdcss_close"));
            var seek = Marshal.GetDelegateForFunctionPointer<SeekDelegate>(NativeLibrary.GetExport(library, "dvdcss_seek"));
            var read = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(NativeLibrary.GetExport(library, "dvdcss_read"));

            var ctx = open(rawDevice);
            if (ctx == IntPtr.Zero)
                throw new DvdEncryptedException($"could not open {rawDevice} for descrambling");

            return new CssDecryptor(library, ctx, close, seek, read);
        }
        catch (DvdEncryptedException)
        {
            NativeLibrary.Free(library);
            throw;
        }
        catch (Exception ex)
        {
            NativeLibrary.Free(library);
            throw new DvdEncryptedException($"could not initialise CSS descrambling: {ex.Message}");
        }
    }

    /// <summary>Maps a byte offset within a mounted VOB file to its physical device sector, and returns
    /// how many sectors are contiguous from there. The caller must not read past that in one read.</summary>
    public static (long DeviceSector, int ContigSectors) Locate(SafeFileHandle fileHandle, long byteOffset)
    {
        if (OperatingSystem.IsWindows())
            return LocateWindows(fileHandle, byteOffset);
        if (OperatingSystem.IsMacOS())
            return LocateMac(fileHandle, byteOffset);
        throw new DvdEncryptedException("CSS descrambling is not supported on this platform");
    }

    [SupportedOSPlatform("macos")]
    private static (long DeviceSector, int ContigSectors) LocateMac(SafeFileHandle fileHandle, long byteOffset)
    {
        var l2p = new Log2Phys { ContigBytes = MapQueryBytes, DevOffset = byteOffset };
        var fd = (int)fileHandle.DangerousGetHandle();

        if (Fcntl(fd, F_LOG2PHYS_EXT, ref l2p) < 0)
            throw new DvdEncryptedException($"could not locate DVD data on the disc (errno {Marshal.GetLastWin32Error()})");

        var contig = (int)Math.Max(1, l2p.ContigBytes / SectorSize);
        return (l2p.DevOffset / SectorSize, contig);
    }

    /// <summary>
    /// The Windows translation. FSCTL_GET_RETRIEVAL_POINTERS reports a file's on-disc extents as
    /// VCN to LCN, in clusters. A DVD-Video volume is a 2048-byte-cluster UDF whose cluster 0 is the
    /// start of the disc. So one cluster is one DVD sector, and an LCN is already the device sector.
    /// The only arithmetic needed is finding which cluster of the returned extent holds the byte.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (long DeviceSector, int ContigSectors) LocateWindows(SafeFileHandle fileHandle, long byteOffset)
    {
        var vcn = byteOffset / SectorSize;
        var input = new StartingVcnInputBuffer { StartingVcn = vcn };

        var size = Marshal.SizeOf<RetrievalPointersHead>();
        var output = Marshal.AllocHGlobal(size);
        try
        {
            // ERROR_MORE_DATA (234) is expected. The file has more extents than our one-extent buffer
            // holds, but the head and the first extent (the one that covers vcn) always come back.
            if (!DeviceIoControl(fileHandle, FSCTL_GET_RETRIEVAL_POINTERS, ref input,
                    (uint)Marshal.SizeOf<StartingVcnInputBuffer>(), output, (uint)size, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                const int ErrorMoreData = 234;
                if (err != ErrorMoreData)
                    throw new DvdEncryptedException($"could not locate DVD data on the disc (error {err})");
            }

            var head = Marshal.PtrToStructure<RetrievalPointersHead>(output);
            if (head.ExtentCount == 0)
                throw new DvdEncryptedException("the DVD reported no on-disc location for its data");
            if (head.FirstLcn < 0) // LCN_ENTRY_IS_HOLE. An optical file with a hole means the mapping is wrong.
                throw new DvdEncryptedException("the DVD's data is not laid out contiguously on the disc");

            // head.StartingVcn is rounded down to the extent's first cluster. The extent runs to FirstNextVcn.
            var clustersIntoExtent = vcn - head.StartingVcn;
            var deviceSector = head.FirstLcn + clustersIntoExtent;

            var contig = head.FirstNextVcn - vcn;
            contig = Math.Clamp(contig, 1, MapQueryBytes / SectorSize);
            return (deviceSector, (int)contig);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    /// <summary>
    /// Reads and decrypts <paramref name="sectorCount"/> sectors starting at a device sector, and
    /// returns how many it got.
    ///
    /// It seeks ONLY when the read is not where libdvdcss is already positioned. A keyed seek makes
    /// libdvdcss check the title key again and moves the drive head. Seeking on every chunk cut the
    /// throughput from about 1.3 MB/s to about 0.3 MB/s. Reading in order instead lets libdvdcss advance
    /// its own position and keep the cached key, so a whole cell is read after one seek.
    /// </summary>
    public int ReadDecrypted(long deviceSector, int sectorCount, byte[] buffer, int byteOffset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (deviceSector != _nextSector)
        {
            if (_seek(_ctx, (int)deviceSector, DVDCSS_SEEK_KEY) < 0)
            {
                _nextSector = -1;
                throw new DvdEncryptedException($"CSS key negotiation failed at sector {deviceSector}");
            }
        }

        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var target = pin.AddrOfPinnedObject() + byteOffset;
            var read = _read(_ctx, target, sectorCount, DVDCSS_READ_DECRYPT);
            if (read < 0)
            {
                _nextSector = -1; // The position is now uncertain, so the next read must seek.
                throw new DvdEncryptedException($"CSS decryption failed at sector {deviceSector}");
            }

            _nextSector = deviceSector + read;
            return read;
        }
        finally
        {
            pin.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _close(_ctx); } catch { /* Closing is best effort. */ }
        NativeLibrary.Free(_library);
    }
}

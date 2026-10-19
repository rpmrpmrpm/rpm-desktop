using System.Runtime.InteropServices;

namespace RPM.Core.Files;

/// <summary>
/// Whether a file's bytes are actually on this disk, or whether it is a cloud placeholder: a OneDrive
/// "online-only" file, or an iCloud Drive file evicted by Optimise Mac Storage. Opening such a file
/// would download it straight away.
///
/// <para>This has to be answered WITHOUT opening the file, and that is the whole difficulty. A
/// placeholder looks like an ordinary file to every normal API: it has a name, a size and a modified
/// date. As soon as a reader touches its contents, the file provider starts to download it. A scan
/// that reads tags in parallel across a folder of placeholders would download someone's entire
/// collection. So both checks below read only metadata.</para>
///
/// <para>On macOS a dataless file has no blocks allocated while it still reports a logical length. On
/// Windows it carries the recall-on-access attribute. Every other platform answers "local", which is
/// true of every file it can see.</para>
/// </summary>
public static class CloudFile
{
    /// <summary>The suffix iCloud Drive puts on the stub it leaves behind for an evicted file:
    /// <c>Track.mp3</c> becomes <c>.Track.mp3.icloud</c>. The stub is a real, tiny, dot-prefixed file,
    /// and the track itself is simply absent from the folder. So a scanner that does not know this name
    /// loses the track silently instead of reporting it as not on this disk.</summary>
    public const string ICloudStubExtension = ".icloud";

    /// <summary>True when <paramref name="path"/> is an iCloud stub. The track it stands for is
    /// <see cref="TrackBehindStub"/>.</summary>
    public static bool IsICloudStub(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith('.')
            && name.EndsWith(ICloudStubExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path of the track an iCloud stub stands for: its leading dot and trailing
    /// <c>.icloud</c> removed. The file will not exist; that is the point of it.</summary>
    public static string TrackBehindStub(string stubPath)
    {
        var dir = Path.GetDirectoryName(stubPath) ?? "";
        var name = Path.GetFileName(stubPath);
        var real = name[1..^ICloudStubExtension.Length];
        return Path.Combine(dir, real);
    }

    /// <summary>The stub that would stand for this track if it were evicted. This is the inverse of
    /// <see cref="TrackBehindStub"/>.</summary>
    public static string StubFor(string trackPath)
        => Path.Combine(
            Path.GetDirectoryName(trackPath) ?? "",
            "." + Path.GetFileName(trackPath) + ICloudStubExtension);

    /// <summary>
    /// True when the file exists but its contents are not on this disk. False for ordinary files,
    /// for genuinely empty files, and on any platform that has no such notion. It never opens the file
    /// and never triggers a download.
    /// </summary>
    public static bool IsDataless(string path)
    {
        try
        {
            // The iCloud stub form: the track is not there at all, and a stub sits beside it under a
            // dotted name. There is nothing to measure, so the stub is the answer.
            if (!File.Exists(path))
                return File.Exists(StubFor(path));

            if (OperatingSystem.IsWindows())
                return IsWindowsPlaceholder(path);
            if (OperatingSystem.IsMacOS())
                return IsMacDataless(path);
            return false;
        }
        catch
        {
            // A file that cannot be checked is treated as local. A reader will hit the error when it
            // opens the file anyway. Calling a broken file "in the cloud" would send the user to a
            // download that never comes.
            return false;
        }
    }

    // ── Windows ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set on a dehydrated cloud placeholder: the data is not local and would be recalled on
    /// access. OFFLINE is the older HSM bit, and OneDrive sets it too.</summary>
    private const int FileAttributeOffline = 0x00001000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    private static bool IsWindowsPlaceholder(string path)
        => ((int)File.GetAttributes(path) & (FileAttributeOffline | FileAttributeRecallOnDataAccess)) != 0;

    // ── macOS ──────────────────────────────────────────────────────────────────────────────────────
    //
    // A dataless file reports its full logical length but has NO blocks allocated: the file provider
    // knows how big it is and does not have any of it. So: ask for the allocated size, and only when
    // that is zero (an empty file, or a placeholder) pay for a second call to find out which.

    private static bool IsMacDataless(string path)
        => AttributeOf(path, AttrFileAllocSize) == 0 && AttributeOf(path, AttrFileDataLength) > 0;

    /// <summary>One <c>off_t</c> file attribute, or 0 when it cannot be read. One attribute per call.
    /// getattrlist packs its answer in bitmap order with no padding. A single value at a known offset
    /// is hard to read wrongly.</summary>
    private static long AttributeOf(string path, uint fileAttr)
    {
        // Buffer: [u_int32 length][off_t value], packed tight, with the value at offset 4.
        // Note: getattrlist rejects FSOPT_PACK_INVAL_ATTRS (EINVAL), so no options are passed.
        var list = new AttrList { BitmapCount = AttrBitMapCount, FileAttr = fileAttr };
        const int bufSize = 16;
        var buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            if (GetAttrList(path, ref list, buf, bufSize, 0) != 0)
                return 0;
            var value = Marshal.ReadInt64(buf, sizeof(uint));
            return value < 0 ? 0 : value;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "getattrlist")]
    private static extern int GetAttrList(
        string path, ref AttrList list, IntPtr attrBuf, nuint attrBufSize, ulong options);

    [StructLayout(LayoutKind.Sequential)]
    private struct AttrList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttr;
        public uint VolAttr;
        public uint DirAttr;
        public uint FileAttr;
        public uint ForkAttr;
    }

    private const ushort AttrBitMapCount = 5;

    /// <summary>Physical bytes allocated on disk. Zero for an empty file AND for a placeholder.</summary>
    private const uint AttrFileAllocSize = 0x00000004;

    /// <summary>Logical data-fork length. This is what tells the two apart.</summary>
    private const uint AttrFileDataLength = 0x00000200;
}

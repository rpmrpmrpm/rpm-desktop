using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RPM.Core.Library;

/// <summary>
/// Whether a folder is on a spinning hard disk. Library settings use it to suggest keeping the database on
/// a faster disk (<see cref="DatabasePlaces.OffMusicDisk"/>). It only suggests; nothing is switched by it.
///
/// <para><b>How it asks.</b> On Windows, the storage driver is asked for the volume's seek penalty
/// (<c>IOCTL_STORAGE_QUERY_PROPERTY</c> with <c>StorageDeviceSeekPenaltyProperty</c>). A disk with a seek
/// penalty has a head that moves: a spinning disk. An SSD has none. The volume is opened with no access
/// rights, which needs no administrator rights, and nothing on the disk is read: the driver answers from
/// what it already knows. It is one call that takes a few milliseconds.</para>
///
/// <para>Other systems answer null, as do a network folder and a volume whose driver does not answer.
/// Null means "cannot tell", and nothing is suggested.</para>
/// </summary>
public static partial class SpinningDisk
{
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint QueryStorageProperty = 0x002D1400;
    private const int SeekPenaltyProperty = 7;
    private const int StandardQuery = 0;

    /// <summary>True when <paramref name="folder"/> is on a spinning disk, false when it is on a disk with
    /// no seek penalty (an SSD), and null when this cannot be told. The folder does not have to exist: only
    /// its drive is asked about.</summary>
    public static bool? Holds(string folder)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(folder))
            return null;

        try
        {
            var full = Path.GetFullPath(folder);
            if (MirrorLocation.IsWindowsShare(full) || MirrorLocation.WindowsDriveRoot(full) is not { } root)
                return null;
            if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
                return null;

            using var volume = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0, ShareReadWrite, 0, OpenExisting, 0, 0);
            if (volume.IsInvalid)
                return null;

            var query = new PropertyQuery { PropertyId = SeekPenaltyProperty, QueryType = StandardQuery };
            if (!DeviceIoControl(volume, QueryStorageProperty, ref query, Marshal.SizeOf<PropertyQuery>(),
                    out var answer, Marshal.SizeOf<SeekPenaltyAnswer>(), out var returned, 0)
                || returned < Marshal.SizeOf<SeekPenaltyAnswer>())
                return null;

            return answer.IncursSeekPenalty != 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>STORAGE_PROPERTY_QUERY.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyAnswer
    {
        public int Version;
        public int Size;
        public byte IncursSeekPenalty;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint code, ref PropertyQuery input, int inputSize,
        out SeekPenaltyAnswer output, int outputSize, out int returned, nint overlapped);
}

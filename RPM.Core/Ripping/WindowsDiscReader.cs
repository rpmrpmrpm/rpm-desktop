using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RPM.Core.Ripping;

/// <summary>
/// Reads audio CDs on Windows. Windows' cdfs driver mounts an audio CD, but the <c>.cda</c> files it shows
/// are 44-byte stubs with no audio in them, so the file system is no use for ripping. Instead we open the
/// drive as a device and use the two IOCTLs that matter: READ_TOC for the table of contents, and RAW_READ
/// for the CDDA sectors.
///
/// RAW_READ works from a plain volume handle without elevation. EAC and dBpoweramp use it too. SPTI would
/// also give C2 error pointers, but it needs Administrator rights, which a consumer app cannot assume.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiscReader : IDiscReader
{
    private const int SectorBytes = 2352;   // CDROM_RAW_SECTOR_SIZE: one CDDA frame
    private const int SectorsPerRead = 26;  // ~61 KB, under the usual 64 KB miniport transfer ceiling
    private const int DataTrackFlag = 0x04; // the TOC control nibble's data bit

    private const uint IoctlCdromReadToc = 0x00024000;
    private const uint IoctlCdromReadTocEx = 0x00024054; // multisession/full TOC, for CD-Extra lead-outs
    private const uint IoctlCdromRawRead = 0x0002403E;
    private const uint IoctlStorageCheckVerify = 0x002D4800;

    private const int LeadInFrames = 150; // the 2-second lead-in an MSF address carries, dropped to get the LBA

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int TrackModeCdda = 2;

    public bool PcmIsBigEndian => false; // raw CDDA off the wire is little-endian s16.

    public Task<IReadOnlyList<DiscDrive>> ListDrivesAsync(CancellationToken ct = default)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d =>
            {
                var letter = d.Name.TrimEnd('\\'); // "D:\" -> "D:"
                var label = SafeVolumeLabel(d);
                return new DiscDrive(letter, string.IsNullOrWhiteSpace(label) ? letter : $"{letter} {label}");
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DiscDrive>>(drives);
    }

    /// <summary>
    /// Whether cdfs has an audio CD mounted. This is read from the file system, not from the device. The stub
    /// <c>.cda</c> files are useless for ripping, but cdfs writes them for an audio disc and for nothing else.
    /// So their presence is the answer.
    ///
    /// It is deliberately not <see cref="HasMedia"/>. CHECK_VERIFY goes to the drive, and a drive that is
    /// asked every few seconds never gets to sleep.
    /// </summary>
    public bool AudioDiscPresent()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom))
        {
            try
            {
                if (drive.IsReady &&
                    Directory.EnumerateFiles(drive.RootDirectory.FullName, "*.cda").Any())
                    return true;
            }
            catch
            {
                // An empty or half-spun drive throws instead of answering. That counts as a no.
            }
        }

        return false;
    }

    public Task<DiscInfo?> ReadDiscAsync(DiscDrive drive, CancellationToken ct = default)
    {
        using var handle = OpenDrive(drive.Id);
        if (handle.IsInvalid || !HasMedia(handle))
            return Task.FromResult<DiscInfo?>(null);

        var toc = ReadToc(handle);
        if (toc is null || !toc.AudioTracks.Any())
            return Task.FromResult<DiscInfo?>(null);

        // The cdfs mount is useless for audio, so there is no useful mount path to return.
        var disc = new DiscInfo(drive, toc, MusicBrainzDiscId.Compute(toc), MountPath: null);
        return Task.FromResult<DiscInfo?>(disc);
    }

    public async Task ReadTrackPcmAsync(DiscInfo disc, DiscTrack track, Stream destination,
        IProgress<double>? progress = null, CancellationToken ct = default, RipReadLog? log = null)
    {
        var handle = OpenDrive(disc.Drive.Id);
        if (handle.IsInvalid)
            throw new DiscReadException($"could not open drive {disc.Drive.Id}");

        // No log means no tolerance. The first refused block fails the track (see MacDiscReader).
        log ??= new RipReadLog(new RipReadPolicy { Retries = 0, MaxBadBlocks = 0 });

        // Any exit other than a clean one can leave an abandoned RAW_READ still holding the drive handle.
        var abandoned = false;
        try
        {
            var startLba = track.Lba;
            var totalSectors = disc.Toc.SectorCountOf(track);
            var buffer = new byte[SectorsPerRead * SectorBytes];
            var done = 0;

            while (done < totalSectors)
            {
                ct.ThrowIfCancellationRequested();

                var count = Math.Min(SectorsPerRead, totalSectors - done);
                var at = startLba + done;

                // RAW_READ is a blocking IOCTL that takes no cancel token. So it runs on the thread pool, where it
                // can be abandoned. A drive re-seeking a scratch answers nothing else.
                var bytes = await DiscBlockReader.ReadAsync(
                    token => Task.Run(() => ReadRawSectors(handle, at, count, buffer), token),
                    log, $"sectors {at}..{at + count - 1}", ct);

                // Whatever the drive withheld (the whole block, or the tail of a short read) is written out as
                // silence, rather than shortening the track. These sectors have their place in the timeline whether
                // the drive filled it or not. Sliding the rest forward would put every later second of the track in
                // the wrong place.
                var wanted = count * SectorBytes;
                var got = bytes == DiscBlockReader.Unreadable ? 0 : bytes;
                if (got < wanted)
                    Array.Clear(buffer, got, wanted - got);

                await destination.WriteAsync(buffer.AsMemory(0, wanted), ct);
                done += count;
                progress?.Report((double)done / totalSectors);
            }

            progress?.Report(1);
        }
        catch
        {
            abandoned = true;
            throw;
        }
        finally
        {
            if (abandoned)
                _ = Task.Run(() => { try { handle.Dispose(); } catch { } });
            else
                handle.Dispose();
        }
    }

    public Task EjectAsync(DiscDrive drive, CancellationToken ct = default)
    {
        // IOCTL_STORAGE_EJECT_MEDIA, kept inline. It is the only other IOCTL we need. It may fail legitimately,
        // for a drive with no motorised tray. In that case we just move on.
        const uint ioctlStorageEjectMedia = 0x002D4808;
        try
        {
            using var handle = OpenDrive(drive.Id);
            if (!handle.IsInvalid)
                DeviceIoControl(handle, ioctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { /* Ejecting is a courtesy. Never fail a finished rip because of it. */ }

        return Task.CompletedTask;
    }

    private static SafeFileHandle OpenDrive(string driveId)
    {
        // The device path takes no trailing backslash: \\.\D:  (not \\.\D:\)
        var path = $@"\\.\{driveId.TrimEnd('\\', ':')}:";
        return CreateFile(path, GenericRead, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
    }

    private static bool HasMedia(SafeFileHandle handle) =>
        DeviceIoControl(handle, IoctlStorageCheckVerify, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private static DiscToc? ReadToc(SafeFileHandle handle)
    {
        // CDROM_TOC: Length[2], FirstTrack, LastTrack, then 100 * 8-byte TRACK_DATA.
        const int tocBytes = 4 + 100 * 8;
        var buffer = Marshal.AllocHGlobal(tocBytes);
        try
        {
            if (!DeviceIoControl(handle, IoctlCdromReadToc, IntPtr.Zero, 0, buffer, tocBytes, out _, IntPtr.Zero))
                return null;

            var raw = new byte[tocBytes];
            Marshal.Copy(buffer, raw, 0, tocBytes);

            int firstTrack = raw[2];
            int lastTrack = raw[3];

            var tracks = new List<DiscTrack>();
            var leadoutLba = 0;

            for (var i = 0; i < 100; i++)
            {
                var offset = 4 + i * 8;
                var control = raw[offset + 1] & 0x0F; // low nibble is Control, high is Adr
                int trackNumber = raw[offset + 2];

                // IOCTL_CDROM_READ_TOC returns addresses as MSF. MusicBrainz's offset is exactly
                // (M * 60 + S) * 75 + F. Subtracting the 150-frame lead-in gives the LBA that the rest of the code uses.
                var minutes = raw[offset + 5];
                var seconds = raw[offset + 6];
                var frames = raw[offset + 7];
                var lba = (minutes * 60 + seconds) * DiscToc.FramesPerSecond + frames - MusicBrainzDiscId.LeadIn;

                if (trackNumber == 0xAA) // the lead-out descriptor
                {
                    leadoutLba = lba;
                    break;
                }

                if (trackNumber is < 1 or > 99)
                    continue;

                tracks.Add(new DiscTrack(trackNumber, lba, (control & DataTrackFlag) != 0));
            }

            if (tracks.Count == 0)
                return null;

            tracks.Sort((a, b) => a.Number.CompareTo(b.Number));
            if (lastTrack == 0)
                lastTrack = tracks[^1].Number;

            // A CD-Extra disc has a data track after the audio, in its own session. The last audio track must stop
            // before the unreadable session gap, so it needs the AUDIO session's lead-out. The plain TOC carries
            // only the disc's final lead-out, so the multisession TOC is read for it. If that read fails, the last
            // audio track keeps the longer end, and the rip does not fail here.
            int? audioLeadout = tracks.Any(t => t.IsData) ? ReadAudioSessionLeadout(handle) : null;

            return new DiscToc(firstTrack, lastTrack, tracks, leadoutLba, audioLeadout);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The lead-out of the AUDIO session on a multisession (CD-Extra) disc. This is where the last
    /// audio track really ends, before the session gap and the data track. It reads the full TOC with
    /// IOCTL_CDROM_READ_TOC_EX and returns the lead-out of session 1 (POINT 0xA2). Null when the drive will not
    /// return the full TOC. The caller then keeps the disc's final lead-out.</summary>
    private static int? ReadAudioSessionLeadout(SafeFileHandle handle)
    {
        // CDROM_READ_TOC_EX input: the format is FULL_TOC (0x02) in the low nibble. The Msf bit (0x80) is set,
        // so addresses come back as M:S:F. The other three bytes are zero.
        var request = new byte[] { 0x02 | 0x80, 0, 0, 0 };

        const int outBytes = 4096;   // ample for a couple of sessions' worth of 11-byte descriptors
        var input = Marshal.AllocHGlobal(request.Length);
        var output = Marshal.AllocHGlobal(outBytes);
        try
        {
            Marshal.Copy(request, 0, input, request.Length);
            if (!DeviceIoControl(handle, IoctlCdromReadTocEx, input, (uint)request.Length,
                    output, outBytes, out var returned, IntPtr.Zero))
                return null;

            var raw = new byte[Math.Min((int)returned, outBytes)];
            Marshal.Copy(output, raw, 0, raw.Length);
            if (raw.Length < 4)
                return null;

            // CDROM_TOC_FULL_TOC_DATA: Length[2], FirstCompleteSession, LastCompleteSession, then a run of 11-byte
            // descriptors. The one we want is the audio session's lead-out: SessionNumber 1, POINT 0xA2. Its
            // address is in the last three bytes.
            var length = (raw[0] << 8) | raw[1];   // bytes following this 2-byte field
            var end = Math.Min(raw.Length, 2 + length);
            for (var o = 4; o + 11 <= end; o += 11)
            {
                int session = raw[o];
                int point = raw[o + 3];
                if (session == 1 && point == 0xA2)
                {
                    int m = raw[o + 8], s = raw[o + 9], f = raw[o + 10];
                    return (m * 60 + s) * DiscToc.FramesPerSecond + f - LeadInFrames;
                }
            }
            return null;
        }
        catch
        {
            return null;   // A drive that fails on READ_TOC_EX must not stop the rip.
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    private static int ReadRawSectors(SafeFileHandle handle, int startLba, int sectorCount, byte[] destination)
    {
        var wanted = sectorCount * SectorBytes;

        var request = new RawReadInfo
        {
            // The trap in this API: the offset is in 2048-byte units, even though a CDDA sector is 2352 bytes.
            // Microsoft's own docs spell it out. Mixing up the two constants is the usual bug here.
            DiskOffset = (long)startLba * 2048,
            SectorCount = (uint)sectorCount,
            TrackMode = TrackModeCdda,
        };

        var input = Marshal.AllocHGlobal(Marshal.SizeOf<RawReadInfo>());
        var output = Marshal.AllocHGlobal(wanted);
        try
        {
            Marshal.StructureToPtr(request, input, false);

            if (!DeviceIoControl(handle, IoctlCdromRawRead, input, (uint)Marshal.SizeOf<RawReadInfo>(),
                    output, (uint)wanted, out var returned, IntPtr.Zero))
            {
                throw new DiscReadException(
                    $"the drive could not read sectors {startLba}..{startLba + sectorCount - 1} " +
                    $"(error {Marshal.GetLastWin32Error()}) — the disc may be scratched or dirty");
            }

            var got = (int)Math.Min(returned, (uint)wanted);
            Marshal.Copy(output, destination, 0, got);
            return got;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    private static string SafeVolumeLabel(DriveInfo drive)
    {
        // Every property but Name throws on a drive with no disc in it.
        try { return drive.IsReady ? drive.VolumeLabel : ""; }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawReadInfo
    {
        public long DiskOffset;
        public uint SectorCount;
        public int TrackMode;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace RPM.Core.Ripping;

/// <summary>
/// Reads a video DVD: finds the mounted disc, parses each title set's chapter structure out of its
/// IFO, and asks ffprobe what audio the title carries.
///
/// The chapter structure and the audio layout both come from the IFO, which is never scrambled.
/// So the disc can be listed even when its VOBs are CSS-encrypted. The audio itself is in the VOBs.
/// On a clear disc ffmpeg reads them directly. On a CSS disc <see cref="DvdRipper"/> descrambles them
/// through libdvdcss. Either way, no GPL library is linked. <see cref="IsEncrypted"/> tells us which
/// case applies.
/// </summary>
public sealed class DvdReader
{
    private const string VideoTs = "VIDEO_TS";

    /// <summary>A title shorter than this is a menu, a logo or a copyright card, not content.</summary>
    private static readonly TimeSpan ShortestUsefulTitle = TimeSpan.FromMinutes(1);

    /// <summary>The mounted video DVDs: volumes that have a VIDEO_TS folder. On macOS that is a
    /// subdirectory of <c>/Volumes</c>. On Windows it is the root of a CD-ROM drive (<c>D:\</c>).</summary>
    public static IReadOnlyList<string> FindDvdVolumes()
    {
        if (OperatingSystem.IsWindows())
            return FindWindowsDvdVolumes();

        try
        {
            return Directory.EnumerateDirectories("/Volumes")
                .Where(volume => Directory.Exists(Path.Combine(volume, VideoTs)))
                .ToList();
        }
        catch { return []; }
    }

    /// <summary>The CD-ROM drives that hold a video DVD, by drive root (<c>D:\</c>). Reading a property
    /// of an empty drive can throw, so each drive is checked defensively.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> FindWindowsDvdVolumes()
    {
        var volumes = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom)
                    continue;
                try
                {
                    var root = drive.RootDirectory.FullName; // "D:\"
                    if (Directory.Exists(Path.Combine(root, VideoTs)))
                        volumes.Add(root);
                }
                catch { /* No disc, or a drive that disappeared during enumeration. */ }
            }
        }
        catch { return []; }

        return volumes;
    }

    /// <summary>Reads the DVD mounted at <paramref name="mount"/>. Titles come back longest first.
    /// On a concert disc the main feature is much longer than the menus and extras, so it comes first.</summary>
    public async Task<DvdInfo?> ReadDvdAsync(DiscDrive drive, string mount, CancellationToken ct = default)
    {
        var videoTs = Path.Combine(mount, VideoTs);
        if (!Directory.Exists(videoTs))
            return null;

        var titles = new List<DvdTitle>();

        // One IFO per title set: VTS_01_0.IFO, VTS_02_0.IFO, ...
        foreach (var ifoPath in Directory.EnumerateFiles(videoTs, "VTS_*_0.IFO").OrderBy(p => p))
        {
            ct.ThrowIfCancellationRequested();

            var titleSet = TitleSetNumberOf(ifoPath);
            if (titleSet is null)
                continue;

            byte[] ifo;
            try { ifo = await File.ReadAllBytesAsync(ifoPath, ct); }
            catch { continue; }

            var vobs = VobsOfTitleSet(videoTs, titleSet.Value);
            if (vobs.Count == 0)
                continue;

            var parsed = DvdIfo.ReadTitles(ifo);
            if (parsed.Count == 0)
                continue;

            // A title set's program chains all play from the same VOBs. The first one is the feature.
            var (duration, chapters) = parsed.MaxBy(t => t.Duration);
            if (duration < ShortestUsefulTitle)
                continue;

            var audio = DvdIfo.ReadAudioStreams(ifo);
            var scrambled = IsScrambledOnDisc(vobs[0]);

            titles.Add(new DvdTitle(titleSet.Value, duration, chapters, vobs, audio, scrambled));
        }

        if (titles.Count == 0)
            return null;

        titles.Sort((a, b) => b.Duration.CompareTo(a.Duration));

        return new DvdInfo(drive, mount, VolumeNameOf(mount), titles, ResolveRawDevice(mount));
    }

    /// <summary>The disc's own name: the mounted folder's name on macOS, or the volume label on Windows
    /// (<c>Path.GetFileName</c> of a drive root is empty). It fills the album box for the lookup.</summary>
    private static string? VolumeNameOf(string mount)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var label = new DriveInfo(mount).VolumeLabel;
                return string.IsNullOrWhiteSpace(label) ? null : label;
            }
            catch { return null; }
        }

        return Path.GetFileName(mount);
    }

    /// <summary>True when the title's audio is CSS-scrambled and needs descrambling to rip.</summary>
    public static bool IsEncrypted(DvdTitle title) => title.Scrambled;

    /// <summary>True when a libdvdcss the user installed is present, so a CSS disc can be descrambled.</summary>
    public static bool LibDvdCssAvailable() => CssDecryptor.LibraryAvailable;

    /// <summary>How many probe points to spread across the VOB, and how many sectors to read at each.</summary>
    private const int ProbePoints = 24;
    private const int ProbeSectors = 64;

    /// <summary>
    /// Detects CSS by reading the PES scrambling-control bits of the VOB's content packs.
    ///
    /// A simple check does not work: looking at whether the file "looks like" a program stream.
    /// CSS scrambles only the PES payload and leaves every pack and PES header in the clear. So a
    /// scrambled VOB still starts with <c>00 00 01 BA</c>, like any other. The real flag is two bits in
    /// each media PES header. This uses plain file reads, with no raw device and no libdvdcss, so it
    /// works as a detector on any machine.
    ///
    /// <para>
    /// A disc does not have to scramble every pack. The packs that are not scrambled are the ones that an
    /// early check would see. Reading a few sectors from the front of the VOB and trusting the first media
    /// pack found gives the wrong answer on a disc that scrambles only some packs. The title would look
    /// clear. The VOB would then be read straight from the mounted filesystem, and every scrambled pack
    /// would decode as full-scale white noise mixed into otherwise good music, with no error anywhere,
    /// because all the headers were readable. So this samples widely, and a single scrambled pack anywhere
    /// is proof that the title needs descrambling. That is what CSS means.
    /// </para>
    /// <para>
    /// There is one more case, which only happens on Windows. A VOB that cannot be read at all is
    /// protected, not clear. Windows refuses to return CSS-scrambled sectors through the mounted
    /// filesystem without drive authentication, and the read fails with ERROR_INVALID_PARAMETER. macOS
    /// returns the scrambled bytes instead. If a read error counted as "not scrambled", the check would
    /// look fine on macOS. On Windows the app would never offer libdvdcss. It would rip without a
    /// decryptor, fail every chapter's read, and report the disc as damaged. So a probe that cannot read
    /// a single media pack is treated as protected, which is almost always the real state.
    /// </para>
    /// </summary>
    private static bool IsScrambledOnDisc(string vob)
    {
        var mediaPacksSeen = 0;
        try
        {
            using var file = File.OpenRead(vob);
            var totalSectors = file.Length / VobSectorStream.SectorSize;
            if (totalSectors == 0)
                return false;

            var buffer = new byte[VobSectorStream.SectorSize];
            var stride = Math.Max(1, totalSectors / ProbePoints);

            for (long start = 0; start < totalSectors; start += stride)
            {
                file.Position = start * VobSectorStream.SectorSize;

                for (var i = 0; i < ProbeSectors; i++)
                {
                    if (file.Read(buffer) < buffer.Length)
                        break;

                    if (ProgramStream.IsMediaPack(buffer))
                        mediaPacksSeen++;

                    // One scrambled pack is enough. The title is protected, and reading it without
                    // libdvdcss would produce noise wherever the scrambling is.
                    if (ProgramStream.IsScrambled(buffer))
                        return true;
                }
            }

            // The probe read everything and never saw a media pack. Nothing here says the title is clear,
            // so treat it like the unreadable case below. Do not call a disc rippable without evidence.
            return mediaPacksSeen == 0 && ProtectionIsTheLikelyCause;
        }
        catch (IOException)
        {
            // The Windows case: the drive refuses scrambled sectors through the filesystem, so the read fails
            // before any pack is seen. Having read nothing, we cannot say the title is clear. On a disc that
            // is not protected, these reads succeed.
            return mediaPacksSeen == 0 && ProtectionIsTheLikelyCause;
        }
        catch (UnauthorizedAccessException)
        {
            return ProtectionIsTheLikelyCause;
        }
    }

    /// <summary>
    /// Whether "the probe could not read this VOB" should be taken to mean "the disc is protected".
    ///
    /// <para>True on Windows. The drive does not serve CSS sectors through the mounted filesystem at all,
    /// so a failed read means protection. False on macOS and Linux. Those hand the scrambled bytes over
    /// without complaint, so a failed read there means a damaged disc. Claiming it needs libdvdcss would
    /// send the user after a library that cannot help.</para>
    /// </summary>
    private static bool ProtectionIsTheLikelyCause => OperatingSystem.IsWindows();

    /// <summary>The raw optical device behind a mount, which is what libdvdcss needs. It cannot
    /// authenticate CSS through the mounted filesystem. On Windows that is the drive spec libdvdcss
    /// opens (<c>D:</c>, derived from the mount root). On macOS it is the <c>/dev/rdiskN</c> node found
    /// with <c>stat -f %Sd</c>. Null when it cannot be found, which leaves only clear titles rippable.</summary>
    private static string? ResolveRawDevice(string mount)
    {
        if (OperatingSystem.IsWindows())
        {
            // "D:\" -> "D:", the drive spec libdvdcss opens as \\.\D: for raw sector access.
            var letter = Path.GetPathRoot(mount)?.TrimEnd('\\', '/');
            return string.IsNullOrEmpty(letter) ? null : letter;
        }

        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            var psi = new ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("%Sd");
            psi.ArgumentList.Add(mount);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var node = process.StandardOutput.ReadToEnd().Trim(); // e.g. "disk5"
            process.WaitForExit();

            return process.ExitCode == 0 && node.StartsWith("disk", StringComparison.Ordinal)
                ? $"/dev/r{node}"
                : null;
        }
        catch { return null; }
    }

    /// <summary>The VOBs that hold a title set's content, in play order. VTS_nn_0.VOB is the title
    /// set's menu, so it is skipped. The content starts at _1.</summary>
    private static List<string> VobsOfTitleSet(string videoTs, int titleSet)
    {
        try
        {
            return Directory.EnumerateFiles(videoTs, $"VTS_{titleSet:00}_*.VOB")
                .Where(path => VobPartNumberOf(path) is > 0)
                .OrderBy(VobPartNumberOf)
                .ToList();
        }
        catch { return []; }
    }

    private static int? TitleSetNumberOf(string ifoPath)
    {
        var match = Regex.Match(Path.GetFileName(ifoPath), @"^VTS_(\d+)_0\.IFO$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    private static int VobPartNumberOf(string vobPath)
    {
        var match = Regex.Match(Path.GetFileName(vobPath), @"^VTS_\d+_(\d+)\.VOB$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : -1;
    }
}

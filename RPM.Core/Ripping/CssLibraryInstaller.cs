using System.Runtime.InteropServices;

namespace RPM.Core.Ripping;

/// <summary>
/// Takes a libdvdcss the user has downloaded, checks it can actually work, and files it where
/// <see cref="CssDecryptor"/> looks.
///
/// <para>Telling the user to copy the DLL next to the exe is a bad instruction. Most users do not know
/// where an installed app lives. On Windows it is usually under Program Files, where copying needs
/// administrator rights. So the library is placed in the user's own app-data folder. That needs no
/// rights, and unlike the build output, it survives a reinstall.</para>
///
/// <para>Only a few things can be checked, and they are narrow. The file must be a real library for
/// THIS process's architecture, and it must export the four entry points we call. These checks catch
/// the mistake that happens most often: most libdvdcss builds found on Windows are 32-bit, and a 64-bit
/// process cannot load them at all. Whether the binary is trustworthy cannot be checked. Nobody signs
/// these builds, VideoLAN publishes no current Windows build to compare against, and a changed library
/// with a correct export table passes every structural test. So this reports compatibility, and the UI
/// must not present it as safety.</para>
/// </summary>
public static class CssLibraryInstaller
{
    /// <summary>What the file turned out to be. <see cref="Compatible"/> means only that it can be
    /// loaded and called here. It does not mean the file is trustworthy.</summary>
    /// <param name="Architecture">The binary's machine type, for a message that names the mismatch.</param>
    /// <param name="MissingExports">Entry points we call that the file does not provide.</param>
    /// <param name="Problem">Why it cannot be used, ready to show; null when compatible.</param>
    public sealed record Inspection(
        bool Compatible,
        string Architecture,
        IReadOnlyList<string> MissingExports,
        string? Problem)
    {
        public static Inspection Fail(string problem, string arch = "unknown") =>
            new(false, arch, [], problem);
    }

    /// <summary>The entry points <see cref="CssDecryptor"/> binds. A file missing any of them would
    /// fail at the first descramble instead, which is much harder to debug.</summary>
    private static readonly string[] RequiredExports =
        ["dvdcss_open", "dvdcss_close", "dvdcss_seek", "dvdcss_read"];

    /// <summary>Where an installed copy lives. This mirrors the app's own local-data root. Core cannot
    /// reference the app's LocalData, so the path is built again here rather than shared.</summary>
    public static string InstalledDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "RPM", "lib");

    /// <summary>The file name the platform's loader expects.</summary>
    public static string LibraryFileName => OperatingSystem.IsWindows()
        ? "libdvdcss-2.dll"
        : OperatingSystem.IsMacOS() ? "libdvdcss.2.dylib" : "libdvdcss.so.2";

    /// <summary>Where a copy installed through here ends up.</summary>
    public static string InstalledPath => Path.Combine(InstalledDirectory, LibraryFileName);

    /// <summary>
    /// Reads the file's headers and reports whether it could serve as our libdvdcss. This is inspection
    /// only. Nothing is loaded and nothing is copied, so an unusable or hostile file never gets a chance
    /// to run code.
    /// </summary>
    public static Inspection Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            return Inspection.Fail("that file does not exist");

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (OperatingSystem.IsWindows())
                return InspectPe(reader);

            // Only Windows ships the library as a loose file that people download by hand. Elsewhere it
            // comes from a package manager and lands where CssDecryptor already looks. Rather than parse
            // Mach-O and ELF, defer to the loader on those platforms.
            return new Inspection(true, "not checked", [], null);
        }
        catch (Exception ex)
        {
            return Inspection.Fail($"that file could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks the file and, if it can work, copies it into <see cref="InstalledDirectory"/>.
    /// Returns the inspection either way. On success the library is in place, and
    /// <see cref="CssDecryptor.LibraryAvailable"/> returns true from the next call.
    /// </summary>
    public static Inspection Install(string path)
    {
        var inspection = Inspect(path);
        if (!inspection.Compatible)
            return inspection;

        try
        {
            Directory.CreateDirectory(InstalledDirectory);
            // Overwrites on purpose. Running this again is how a user replaces a build that is too old
            // for a disc.
            File.Copy(path, InstalledPath, overwrite: true);
            return inspection;
        }
        catch (Exception ex)
        {
            return Inspection.Fail($"could not be copied into place: {ex.Message}", inspection.Architecture);
        }
    }

    /// <summary>Removes an installed copy. Answers false when there was nothing to remove.</summary>
    public static bool Uninstall()
    {
        try
        {
            if (!File.Exists(InstalledPath))
                return false;
            File.Delete(InstalledPath);
            return true;
        }
        catch { return false; }
    }

    // ── PE reading ───────────────────────────────────────────────────────────────────────────────
    // Enough of the format to answer two questions. It is written by hand, because the alternative is a
    // reflection-metadata library, and that does not read native export tables anyway.

    private const ushort MachineI386 = 0x014c;
    private const ushort MachineAmd64 = 0x8664;
    private const ushort MachineArm64 = 0xaa64;

    private static Inspection InspectPe(BinaryReader r)
    {
        var stream = r.BaseStream;

        if (stream.Length < 0x40 || r.ReadUInt16() != 0x5a4d)   // "MZ"
            return Inspection.Fail("that is not a Windows library");

        stream.Position = 0x3c;
        var peOffset = r.ReadUInt32();
        if (peOffset + 24 > stream.Length)
            return Inspection.Fail("that is not a Windows library");

        stream.Position = peOffset;
        if (r.ReadUInt32() != 0x00004550)                        // "PE\0\0"
            return Inspection.Fail("that is not a Windows library");

        var machine = r.ReadUInt16();
        var sectionCount = r.ReadUInt16();
        var arch = machine switch
        {
            MachineI386 => "32-bit (x86)",
            MachineAmd64 => "64-bit (x64)",
            MachineArm64 => "64-bit (ARM64)",
            _ => $"unknown (0x{machine:x4})",
        };

        var expected = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => MachineI386,
            Architecture.X64 => MachineAmd64,
            Architecture.Arm64 => MachineArm64,
            _ => (ushort)0,
        };

        if (expected != 0 && machine != expected)
        {
            var need = expected == MachineAmd64 ? "64-bit (x64)"
                     : expected == MachineArm64 ? "64-bit (ARM64)" : "32-bit (x86)";
            // The most common failure, and the one to name exactly: a 32-bit build.
            return Inspection.Fail(
                $"that is the {arch} build of libdvdcss; RPM needs the {need} one", arch);
        }

        // The COFF header is 20 bytes, after the 4-byte signature. SizeOfOptionalHeader is its
        // second-to-last field. The optional header starts right after it, and its first word is the magic.
        stream.Position = peOffset + 20;
        var optionalSize = r.ReadUInt16();
        var optionalStart = peOffset + 24;

        stream.Position = optionalStart;
        var plus = r.ReadUInt16() == 0x20b;                       // PE32+ = 64-bit

        // Data directory 0 is the export table. It follows the fixed part of the optional header, which
        // is longer in PE32+ because several fields are 64 bits wide.
        stream.Position = optionalStart + (plus ? 112 : 96);
        var exportRva = r.ReadUInt32();
        if (exportRva == 0)
            return Inspection.Fail("that library exports nothing at all", arch);

        // The section table follows the optional header. Its real length is given by the COFF header,
        // so use that value. The usual 224 or 240 breaks with any linker that pads the header.
        var sections = ReadSections(r, optionalStart + optionalSize, sectionCount);
        var exportOffset = ToFileOffset(sections, exportRva);
        if (exportOffset is null)
            return Inspection.Fail("that library's export table could not be read", arch);

        var names = ReadExportNames(r, sections, exportOffset.Value);
        var missing = RequiredExports.Where(e => !names.Contains(e)).ToList();

        if (missing.Count == RequiredExports.Length)
            return Inspection.Fail("that is a Windows library, but not libdvdcss", arch);
        if (missing.Count > 0)
            return new Inspection(false, arch, missing,
                $"that libdvdcss is missing {string.Join(", ", missing)}");

        return new Inspection(true, arch, [], null);
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawAddress, uint RawSize);

    private static List<Section> ReadSections(BinaryReader r, long tableStart, int count)
    {
        var sections = new List<Section>(count);
        for (var i = 0; i < count; i++)
        {
            r.BaseStream.Position = tableStart + 40L * i + 8;     // Skip the 8-byte name.
            var virtualSize = r.ReadUInt32();
            var virtualAddress = r.ReadUInt32();
            var rawSize = r.ReadUInt32();
            var rawAddress = r.ReadUInt32();
            sections.Add(new Section(virtualAddress, virtualSize, rawAddress, rawSize));
        }
        return sections;
    }

    /// <summary>An RVA is an address once the file is loaded. On disk, the same bytes sit at a different offset.</summary>
    private static long? ToFileOffset(List<Section> sections, uint rva)
    {
        foreach (var s in sections)
        {
            var span = Math.Max(s.VirtualSize, s.RawSize);
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + span)
                return s.RawAddress + (rva - s.VirtualAddress);
        }
        return null;
    }

    private static HashSet<string> ReadExportNames(BinaryReader r, List<Section> sections, long exportOffset)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        r.BaseStream.Position = exportOffset + 24;                // NumberOfNames
        var nameCount = r.ReadUInt32();
        r.BaseStream.Position = exportOffset + 32;                // AddressOfNames
        var namesRva = r.ReadUInt32();

        var namesOffset = ToFileOffset(sections, namesRva);
        if (namesOffset is null)
            return names;

        // A corrupt table can claim an absurd count. Cap it rather than loop forever.
        for (var i = 0u; i < Math.Min(nameCount, 8192); i++)
        {
            r.BaseStream.Position = namesOffset.Value + 4L * i;
            var nameOffset = ToFileOffset(sections, r.ReadUInt32());
            if (nameOffset is null)
                continue;

            r.BaseStream.Position = nameOffset.Value;
            var bytes = new List<byte>(32);
            int b;
            while ((b = r.BaseStream.ReadByte()) > 0)
                bytes.Add((byte)b);
            names.Add(System.Text.Encoding.ASCII.GetString([.. bytes]));
        }

        return names;
    }
}

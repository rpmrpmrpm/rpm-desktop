using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The Windows half of DVD ripping. Its one piece of Windows-specific native code is
/// <see cref="CssDecryptor.Locate"/>, which maps a file offset to a physical disc sector through
/// FSCTL_GET_RETRIEVAL_POINTERS (the Windows equivalent of the mac F_LOG2PHYS_EXT). There is no
/// CSS-protected optical volume in CI, so these tests can only check that the interop is correct
/// (struct layout, IOCTL code, extent parsing) and returns sensible values, and that the
/// cross-platform discovery API behaves correctly with no disc present.
/// </summary>
public class DvdWindowsSupportTests
{
    /// <summary>Locate must resolve the first sector of a real, non-resident file to a concrete disc
    /// sector and a positive run of contiguous sectors. Run only on Windows: the mapping is a Windows
    /// filesystem control. A file must be large enough that NTFS stores it out-of-line (a resident
    /// file is stored in the MFT and has no retrieval pointers), so a megabyte of data forces real
    /// extents.</summary>
    [Fact]
    public void Locate_resolves_a_real_file_to_a_disc_sector_on_windows()
    {
        if (!OperatingSystem.IsWindows())
            return; // the mapping is Windows-only; nothing to exercise elsewhere

        var path = Path.Combine(Path.GetTempPath(), $"rpm-locate-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[1024 * 1024]);

            using var file = File.OpenRead(path);
            var (deviceSector, contigSectors) = CssDecryptor.Locate(file.SafeFileHandle, byteOffset: 0);

            Assert.True(deviceSector >= 0, $"expected a real disc sector, got {deviceSector}");
            Assert.True(contigSectors >= 1, $"expected at least one contiguous sector, got {contigSectors}");
            // The query is limited to 4 MB, i.e. 2048 sectors: a run may be shorter but never longer.
            Assert.True(contigSectors <= 2048, $"contiguous run {contigSectors} exceeds the 4 MB query cap");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>The library probe never throws on any platform, and when it reports a path that path
    /// really exists. The caller relies on <see cref="CssDecryptor.LibraryAvailable"/> to allow the
    /// rip.</summary>
    [Fact]
    public void FindLibrary_is_null_or_an_existing_file()
    {
        var path = CssDecryptor.FindLibrary();

        Assert.Equal(path is not null, CssDecryptor.LibraryAvailable);
        if (path is not null)
            Assert.True(File.Exists(path), $"FindLibrary returned a path that is not on disk: {path}");
    }

    /// <summary>
    /// The missing-library hint the user sees is the platform's actual fix (brew on macOS, the DLL
    /// next to the app on Windows), never the wrong OS's instruction.
    ///
    /// <para>Windows must NOT say "install VLC". A standard VLC 3.x has no libdvdcss DLL: its
    /// libdvdread/libdvdnav plugins dlopen one by name and export no <c>dvdcss_*</c> symbol themselves.
    /// So installing VLC changes nothing and leaves the user with no next step.</para>
    /// </summary>
    [Fact]
    public void InstallHint_names_the_right_fix_for_the_platform()
    {
        var hint = CssDecryptor.InstallHint;

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("libdvdcss-2.dll", hint);
            Assert.DoesNotContain("VLC", hint);
            Assert.DoesNotContain("brew", hint);
        }
        else
        {
            Assert.Contains("brew", hint);
        }
    }

    /// <summary>Discovery must never throw and must return an empty result when there is nothing, on
    /// every platform: an empty drive is a normal state, not an error. With no disc in a test agent
    /// it returns [].</summary>
    [Fact]
    public void FindDvdVolumes_returns_a_list_without_throwing()
    {
        var volumes = DvdReader.FindDvdVolumes();
        Assert.NotNull(volumes);
    }
}

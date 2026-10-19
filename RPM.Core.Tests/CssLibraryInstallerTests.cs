using System.Runtime.InteropServices;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// Checking a libdvdcss the user supplies. The goal is to fail with a sentence that says what to do
/// next, instead of letting a wrong file through to a BadImageFormatException in the middle of a rip.
/// </summary>
public class CssLibraryInstallerTests
{
    [Fact]
    public void A_file_that_is_not_there_says_so()
    {
        var result = CssLibraryInstaller.Inspect(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.dll"));

        Assert.False(result.Compatible);
        Assert.Contains("does not exist", result.Problem);
    }

    [Fact]
    public void A_blank_path_is_rejected_rather_than_probed()
    {
        Assert.Throws<ArgumentException>(() => CssLibraryInstaller.Inspect("  "));
    }

    [Fact]
    public void Something_that_is_not_a_library_at_all_is_refused()
    {
        // What a user picks when they take the .zip, or the README, instead of the DLL inside it.
        var path = Path.Combine(Path.GetTempPath(), $"notalib-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "PK this is a zip, not a library");
        try
        {
            var result = CssLibraryInstaller.Inspect(path);

            if (OperatingSystem.IsWindows())
            {
                Assert.False(result.Compatible);
                Assert.Contains("not a Windows library", result.Problem);
            }
        }
        finally { File.Delete(path); }
    }

    /// <summary>A real native library of the right architecture, but the wrong library. The important
    /// case is VLC's own libdvdread_plugin.dll, which loads without error and exports nothing rpm
    /// calls.</summary>
    [Fact]
    public void A_real_library_that_is_not_libdvdcss_is_refused()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (!File.Exists(kernel32))
            return;

        var result = CssLibraryInstaller.Inspect(kernel32);

        Assert.False(result.Compatible);
        Assert.Contains("not libdvdcss", result.Problem);
        // It parsed the file properly to reach that answer: the architecture is read, not guessed.
        Assert.Contains(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" : "ARM64",
                        result.Architecture);
    }

    /// <summary>The install target is the user's own app-data, which needs no elevation, and not the
    /// app folder, which on Windows is usually under Program Files.</summary>
    [Fact]
    public void The_install_path_is_per_user_and_correctly_named()
    {
        var dir = CssLibraryInstaller.InstalledDirectory;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.StartsWith(local, dir, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(CssLibraryInstaller.LibraryFileName, CssLibraryInstaller.InstalledPath);

        var expected = OperatingSystem.IsWindows() ? "libdvdcss-2.dll"
                     : OperatingSystem.IsMacOS() ? "libdvdcss.2.dylib" : "libdvdcss.so.2";
        Assert.Equal(expected, CssLibraryInstaller.LibraryFileName);
    }

    [Fact]
    public void Installing_something_incompatible_copies_nothing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var before = File.Exists(CssLibraryInstaller.InstalledPath);

        var path = Path.Combine(Path.GetTempPath(), $"notalib-{Guid.NewGuid():N}.dll");
        File.WriteAllText(path, "definitely not a PE file");
        try
        {
            var result = CssLibraryInstaller.Install(path);

            Assert.False(result.Compatible);
            // The critical part: a refused file must not have overwritten a working install.
            Assert.Equal(before, File.Exists(CssLibraryInstaller.InstalledPath));
        }
        finally { File.Delete(path); }
    }
}

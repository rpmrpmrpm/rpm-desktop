using System.Diagnostics;
using RPM.Core.Nas;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

/// <summary>Runs on macOS when <c>RPM_TEST_SMB</c> is <c>smb://host:port/share|user|password</c>, for
/// example a Samba container.</summary>
public sealed class SmbLiveFactAttribute : FactAttribute
{
    public static string[]? Parts =>
        Environment.GetEnvironmentVariable("RPM_TEST_SMB") is { Length: > 0 } value && value.Split('|') is { Length: 3 } parts
            ? parts
            : null;

    public SmbLiveFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Parts is null)
            Skip = "Set RPM_TEST_SMB=smb://host:port/share|user|password on macOS to run this.";
    }
}

public sealed class NasMountLiveTests : IDisposable
{
    private readonly string _mounts = Path.Combine(Path.GetTempPath(), "rpm-mounts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var folder in Directory.Exists(_mounts) ? Directory.GetDirectories(_mounts) : [])
        {
            using var umount = Process.Start(new ProcessStartInfo("umount", folder) { UseShellExecute = false });
            umount?.WaitForExit(10_000);
        }

        try { Directory.Delete(_mounts, recursive: true); } catch (IOException) { }
    }

    [SmbLiveFact]
    public void Wrong_login_is_refused_and_the_right_one_mounts_hidden()
    {
        var parts = SmbLiveFactAttribute.Parts!;
        var address = NasSmbAddress.Parse(parts[0])!;
        var mount = new MacNetFsMount(_mounts);

        Assert.IsType<NasMountResult.WrongLogin>(mount.Mount("WRONG", address, new NasCredentials(parts[1], "not-the-password")));

        var answer = mount.Mount("RIGHT", address, new NasCredentials(parts[1], parts[2]));
        var result = answer as NasMountResult.Mounted ?? throw new Xunit.Sdk.XunitException(answer.ToString());
        Assert.True(NasMountPoint.IsLiveNetworkMount(result.ShareRoot));

        // Hidden: mounted with nobrowse, so Finder shows no volume.
        var table = Run("mount");
        var line = table.Split('\n').First(l => l.Contains(result.ShareRoot, StringComparison.Ordinal));
        Assert.Contains("nobrowse", line, StringComparison.Ordinal);

        // A library folder made on it is on the network volume.
        var library = Path.Combine(result.ShareRoot, "rpm", "Chivan");
        Directory.CreateDirectory(library);
        Assert.True(NasMountPoint.IsLiveNetworkMount(library));
        Directory.Delete(Path.Combine(result.ShareRoot, "rpm"), recursive: true);

        // Asking again while it is mounted is answered from the mount.
        Assert.IsType<NasMountResult.Mounted>(mount.Mount("RIGHT", address, new NasCredentials(parts[1], parts[2])));
    }

    private static string Run(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

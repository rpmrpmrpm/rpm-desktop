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
    private string? _mounted;

    public void Dispose()
    {
        if (_mounted is null)
            return;
        using var umount = Process.Start(new ProcessStartInfo("umount", _mounted) { UseShellExecute = false });
        umount?.WaitForExit(10_000);
    }

    [SmbLiveFact]
    public void Wrong_login_is_refused_and_the_right_one_mounts_hidden()
    {
        var parts = SmbLiveFactAttribute.Parts!;
        var address = NasSmbAddress.Parse(parts[0])!;
        var mount = new MacNetFsMount();
        Assert.Null(MacNetFsMount.MountedAt(address));

        Assert.IsType<NasMountResult.WrongLogin>(mount.Mount(address, new NasCredentials(parts[1], "not-the-password")));

        var answer = mount.Mount(address, new NasCredentials(parts[1], parts[2]));
        var result = answer as NasMountResult.Mounted ?? throw new Xunit.Sdk.XunitException(answer.ToString());
        _mounted = result.ShareRoot;
        Assert.True(NasMountPoint.IsLiveNetworkMount(result.ShareRoot));

        // Mounted where NetFS chose, as Finder does, and found again in the mount table.
        Assert.StartsWith("/Volumes/", result.ShareRoot, StringComparison.Ordinal);
        Assert.Equal(result.ShareRoot, MacNetFsMount.MountedAt(address));

        // Hidden: mounted with nobrowse, so Finder shows no volume.
        var table = Run("mount");
        var line = table.Split('\n').First(l => l.Contains(result.ShareRoot, StringComparison.Ordinal));
        Assert.Contains("nobrowse", line, StringComparison.Ordinal);

        // A library folder made on it is on the network volume.
        var library = Path.Combine(result.ShareRoot, "rpm", "Chivan");
        Directory.CreateDirectory(library);
        Assert.True(NasMountPoint.IsLiveNetworkMount(library));
        Directory.Delete(Path.Combine(result.ShareRoot, "rpm"), recursive: true);

        // Asking again while it is mounted is answered from the mount, at the same place.
        Assert.Equal(answer, mount.Mount(address, new NasCredentials(parts[1], parts[2])));
    }

    private static string Run(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

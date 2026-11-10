using System.Diagnostics;
using RPM.Core.Nas;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// Linux: <c>gio mount</c>, which mounts through GVfs into <c>/run/user/&lt;uid&gt;/gvfs</c> without
/// root. The login is written to its standard input, never put on the command line.
/// </summary>
internal sealed class LinuxGioMount : NasMount
{
    protected override NasMountResult MountCore(string nasDeviceId, NasSmbAddress address, NasCredentials login)
    {
        var root = ShareRoot(address);
        if (Directory.Exists(root))
            return new NasMountResult.Mounted(root);

        var psi = new ProcessStartInfo("gio")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("mount");
        psi.ArgumentList.Add(address.ShareUrl);
        using var gio = Process.Start(psi)
            ?? throw new IOException("gio could not be started");
        // gio asks for the user, the domain and the password, in that order.
        gio.StandardInput.WriteLine(login.User);
        gio.StandardInput.WriteLine("WORKGROUP");
        gio.StandardInput.WriteLine(login.Password);
        gio.StandardInput.Close();
        var error = gio.StandardError.ReadToEnd();
        if (!gio.WaitForExit(30_000))
        {
            gio.Kill();
            return new NasMountResult.Unreachable("gio mount did not finish");
        }

        if (gio.ExitCode == 0 && Directory.Exists(root))
            return new NasMountResult.Mounted(root);
        return error.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               error.Contains("denied", StringComparison.OrdinalIgnoreCase)
            ? new NasMountResult.WrongLogin()
            : new NasMountResult.Unreachable(error.Trim());
    }

    private static string ShareRoot(NasSmbAddress address) =>
        Path.Combine("/run/user", Uid(), "gvfs",
            $"smb-share:server={address.HostWithoutPort.ToLowerInvariant()},share={address.Share.ToLowerInvariant()}");

    private static string Uid()
    {
        try
        {
            var psi = new ProcessStartInfo("id", "-u") { RedirectStandardOutput = true, UseShellExecute = false };
            using var id = Process.Start(psi)!;
            return id.StandardOutput.ReadToEnd().Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "1000";
        }
    }
}

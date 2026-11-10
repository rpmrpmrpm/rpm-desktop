using System.Net.Sockets;
using RPM.Core.Nas;

namespace RPM.Desktop.Services.Nas;

/// <summary>What came of mounting a NAS share.</summary>
public abstract record NasMountResult
{
    private NasMountResult() { }

    /// <summary>The share is mounted. <paramref name="ShareRoot"/> is its local path.</summary>
    public sealed record Mounted(string ShareRoot) : NasMountResult;

    /// <summary>The NAS refused the user name or password.</summary>
    public sealed record WrongLogin : NasMountResult;

    /// <summary>The NAS could not be reached, or the mount failed for another reason.</summary>
    public sealed record Unreachable(string Detail) : NasMountResult;
}

/// <summary>
/// Mounts a NAS share without a drive letter, a desktop icon or a Finder sidebar entry. The system keeps the login only for the mount; rpm keeps it in <see cref="NasLogin"/>.
/// </summary>
public abstract class NasMount
{
    /// <summary>How long the NAS has to answer on the SMB port before a mount is tried. Without this, a NAS
    /// that is switched off would hold the mount call, and the app's launch, for half a minute.</summary>
    public static readonly TimeSpan ReachWait = TimeSpan.FromSeconds(2);

    public static NasMount ForThisSystem() =>
        OperatingSystem.IsMacOS() ? new MacNetFsMount()
        : OperatingSystem.IsWindows() ? new WindowsUncMount()
        : new LinuxGioMount();

    /// <summary>Mounts the share in <paramref name="address"/>. Blocks; call it off the interface thread
    /// except at launch, where it is bounded by <see cref="ReachWait"/>.</summary>
    public NasMountResult Mount(NasSmbAddress address, NasCredentials login)
    {
        if (!Reachable(address))
            return new NasMountResult.Unreachable($"{address.HostWithoutPort} did not answer on the SMB port");
        try
        {
            return MountCore(address, login);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException
                                       or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            return new NasMountResult.Unreachable(ex.Message);
        }
    }

    protected abstract NasMountResult MountCore(NasSmbAddress address, NasCredentials login);

    private static bool Reachable(NasSmbAddress address)
    {
        var parts = address.Host.Split(':');
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 445;
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(parts[0], port).Wait(ReachWait) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }
}

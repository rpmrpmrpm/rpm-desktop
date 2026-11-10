using RPM.Core.Nas;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// The step before the library opens at launch, and on Try again, when the library is on a NAS: mount the
/// share with the stored login, and work out the library's local path from it.
/// </summary>
public static class NasStartup
{
    /// <summary>
    /// The library path to open, or null when the share could not be mounted. Null makes the open report
    /// the folder as missing, which shows the problem screen with Try again. It never returns a path that
    /// is not on a live network mount (see <see cref="NasMountPoint"/>).
    /// </summary>
    public static string? Prepare(SettingsService settings, AuthService auth, NasMount mount)
    {
        if (settings.LibraryNas is not { } nas)
            return settings.LibraryPath;
        if (Resolve(nas, new NasLogin(auth.Secrets).Read(nas.DeviceId), mount) is not { } path)
            return null;
        // The mount point is fixed on macOS, but the UNC path on Windows and the GVfs path on Linux follow
        // the address, so the saved path is brought up to date.
        if (!string.Equals(settings.LibraryPath, path, StringComparison.Ordinal))
            settings.LibraryPath = path;
        return path;
    }

    /// <summary>The library's local path after mounting, or null. Null when there is no login, the mount
    /// failed, or the folder it gives is not on a live network mount.</summary>
    public static string? Resolve(NasLocation nas, NasCredentials? login, NasMount mount)
    {
        ArgumentNullException.ThrowIfNull(nas);
        ArgumentNullException.ThrowIfNull(mount);
        if (login is null || NasSmbAddress.Parse(nas.Smb) is not { } address)
            return null;
        if (mount.Mount(nas.DeviceId, address, login) is not NasMountResult.Mounted mounted)
            return null;
        var path = address.LibraryPath(mounted.ShareRoot, nas.Folder);
        return NasMountPoint.IsLiveNetworkMount(path) ? path : null;
    }
}

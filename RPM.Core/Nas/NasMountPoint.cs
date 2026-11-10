using RPM.Core.Library;

namespace RPM.Core.Nas;

/// <summary>
/// Whether a folder is on a live network mount.
///
/// <para><b>This guards the most dangerous case in the NAS code.</b> When the share is not mounted, the
/// path of a library on it can be an ordinary empty folder on this computer's disk. Opened as the library,
/// it would read as an empty collection, a new database would be made in it, and the whole collection
/// would look deleted. So a NAS library is opened only when its folder is on a network volume.</para>
/// </summary>
public static class NasMountPoint
{
    public static bool IsLiveNetworkMount(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Directory.Exists(path) &&
        MirrorLocation.VolumeOf(path) == DriveType.Network;
}

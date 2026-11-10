namespace RPM.Desktop.Services.Nas;

/// <summary>
/// The NAS a library is on, as settings keep it. The library's local path is worked out from this at
/// launch, after the share is mounted, because the mount point and the address can change.
/// </summary>
/// <param name="DeviceId">The NAS's own device id, which names its stored login.</param>
/// <param name="Name">The NAS's name, for the screens.</param>
/// <param name="Smb">The SMB address of the folder that holds the libraries, from the NAS's hello.</param>
/// <param name="Folder">The library's folder under that folder.</param>
public sealed record NasLocation(string DeviceId, string Name, string Smb, string Folder);

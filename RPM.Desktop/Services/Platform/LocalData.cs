namespace RPM.Desktop.Services.Platform;

/// <summary>The app's local data folder. It holds settings.json, and the secrets folder where SessionStore
/// keeps the sign-in sealed.
/// </summary>
public static class LocalData
{
    /// <summary>Where this computer keeps the database of a library whose music folder is on a network
    /// volume, one folder per library id (see <see cref="RPM.Core.Library.MirrorLocation"/>).</summary>
    public static string Libraries => Path.Combine(Dir, RPM.Core.Library.MirrorLocation.LibrariesFolderName);

    public static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RPM");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}

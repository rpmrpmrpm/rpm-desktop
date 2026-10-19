namespace RPM.Core.Library;

/// <summary>
/// What happened when the app tried to open the configured central music folder.
///
/// <para>Each failure has its own case, so none of them is lost. A folder check alone cannot tell a
/// first run from a broken setup. If every exception from the open became "no folder", the setup
/// wizard would open with no message, and picking a folder there would overwrite the saved setting.
/// An unmounted disk, a database from a newer build, and a database that cannot be read are three
/// different facts. Each one must reach the screen.</para>
///
/// <para><see cref="Trouble"/> is a nested type, so the screen that explains a failure can only be
/// given a failure. <c>LibraryProblemPage</c> takes a <see cref="Trouble"/>, not a
/// <see cref="LibraryOpening"/> and not a string.</para>
/// </summary>
public abstract record LibraryOpening
{
    private LibraryOpening() { }

    /// <summary>No central music folder is configured. This is a first run.</summary>
    public sealed record NoFolder : LibraryOpening;

    /// <summary>
    /// The folder opened and the library database was read.
    /// </summary>
    /// <param name="Folder">The central music folder.</param>
    /// <param name="Albums">How many albums the database holds. This counts albums, not tracks. A folder
    /// with one loose file is not a collection. Counting tracks would send someone who imported a single
    /// song to the library page.</param>
    public sealed record Opened(string Folder, int Albums) : LibraryOpening;

    /// <summary>
    /// A folder is configured, but its collection cannot be used. It carries the reason, with enough
    /// detail to show on screen. Every case except <see cref="ReadFailed"/> comes from the open itself.
    /// <see cref="ReadFailed"/> comes from a read after a successful open (<c>LibraryService.Read</c>).
    /// </summary>
    /// <param name="Folder">The configured central music folder.</param>
    public abstract record Trouble(string Folder) : LibraryOpening
    {
        /// <summary>The configured folder is not a directory that is there now: an unmounted disk, a
        /// network share that is not connected, a folder that was renamed, moved or deleted.</summary>
        public sealed record FolderMissing(string Folder) : Trouble(Folder);

        /// <summary>The database was written by a newer build of rpm. It is left unchanged, so that newer
        /// build can still open it.</summary>
        /// <param name="Found">The schema version the file carries.</param>
        /// <param name="Supported">The newest schema version this build knows.</param>
        public sealed record TooNew(string Folder, long Found, long Supported) : Trouble(Folder);

        /// <summary>The folder exists, but the database could not be opened or read. Possible causes are a
        /// corrupt file, another copy of rpm holding it, or a volume that refuses writes.</summary>
        /// <param name="Detail">The exception text. It is for the log and the Details line of the page,
        /// never for the sentence that explains the problem.</param>
        public sealed record Unreadable(string Folder, string Detail) : Trouble(Folder);

        /// <summary>The collection opened, but a later read failed with the database's own error. Possible
        /// causes are a file damaged while the app ran, a disk that stopped responding, or another program
        /// that locked the file. The folder still exists. If it does not, the case is
        /// <see cref="FolderMissing"/> instead.</summary>
        /// <param name="Detail">The exception text. It is for the log and the Details line of the
        /// page.</param>
        public sealed record ReadFailed(string Folder, string Detail) : Trouble(Folder);
    }
}

/// <summary>
/// Which screen the app opens on.
/// </summary>
public enum OpeningRoute
{
    /// <summary>No folder is configured. The wizard opens at its first step.</summary>
    PickFolder,

    /// <summary>The folder exists, but the database holds no albums. The wizard opens at its second
    /// step, with the folder already set.</summary>
    ImportFirstMusic,

    /// <summary>The collection holds something. The library opens.</summary>
    Library,

    /// <summary>A folder is configured and could not be opened. The problem screen opens, with the
    /// <see cref="LibraryOpening.Trouble"/> that says which failure it was.</summary>
    CannotOpen,
}

/// <summary>
/// The opening decision.
///
/// <para>It is named after the screen, not the moment, because <c>Presentation.Startup.Opening</c>
/// already names the launch sequence. Two types called Opening would be confusing.</para>
/// </summary>
public static class OpeningScreen
{
    /// <summary>Where the app opens, given the result of the open attempt. This method is pure, so the
    /// decision can be tested without a window. Reaching the disk is the job of
    /// <c>LibraryService.OpenForLanding</c>.
    /// </summary>
    public static OpeningRoute Choose(LibraryOpening opening) => opening switch
    {
        LibraryOpening.NoFolder => OpeningRoute.PickFolder,
        LibraryOpening.Opened { Albums: > 0 } => OpeningRoute.Library,
        LibraryOpening.Opened => OpeningRoute.ImportFirstMusic,
        LibraryOpening.Trouble => OpeningRoute.CannotOpen,
        _ => throw new ArgumentOutOfRangeException(nameof(opening), opening, "Unknown opening."),
    };
}

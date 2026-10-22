using System.Text;
using System.Text.Json;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

/// <summary>Where a library's database file is kept. <see cref="LibraryDatabase"/> takes one of these and
/// nothing else, so every caller states where its file is.
///
/// <para><b>In the music folder</b> (<c>.rpm/library.db</c>) when the folder is on one of this computer's
/// own disks, so the database moves with the folder. <b>On this computer</b>
/// (<c>libraries/&lt;id&gt;/library.db</c> under the app's local data folder) when the folder is on a
/// network volume. The database always runs in WAL mode. WAL's shared-memory index does not work between
/// computers. Two computers with one database open in a network folder do not see each other's locks, so
/// both can write at once. Two computers upgrading it at the same moment can both migrate it. With a
/// database on each computer, nothing that SQLite opens is shared.</para>
///
/// <para><b>The library id.</b> A music folder on a network volume holds <c>.rpm/library.json</c> with a
/// random id, written by the first open. This computer's database for it is filed under that id, not under
/// the folder's path. The path is how this computer reaches the share today. The same share mounted again
/// as <c>/Volumes/Music-1</c>, or picked as <c>Z:\Music</c> instead of <c>\\nas\music\Music</c>, is the
/// same library and opens the same database.</para>
///
/// <para><b>Carrying a database across.</b> When a network folder is opened on a computer that has no
/// database for its id, the folder's own <c>.rpm/library.db</c> is copied to this computer, if there is
/// one. So a library moved to a NAS, or used there by an older build, keeps its playlists, plays and
/// favourites. It is copied as files, together with its <c>-wal</c> file, and SQLite opens only the copy.
/// The folder's file is not changed or deleted. In the other direction, a folder on this computer's own
/// disk that has an id and no <c>library.db</c> (a library moved off a network volume) gets this computer's
/// database for that id copied into it. A copy is made only when the destination has no file, so a database
/// is never replaced.</para>
/// </summary>
public sealed class MirrorLocation
{
    /// <summary>The id file's name in the music folder's <c>.rpm</c>.</summary>
    public const string IdFileName = "library.json";

    /// <summary>The folder under the app's local data folder that holds one folder per library
    /// id.</summary>
    public const string LibrariesFolderName = "libraries";

    private MirrorLocation(string root, string databasePath, Carried? carryFrom, bool onThisComputer)
    {
        Root = root;
        DatabasePath = databasePath;
        CarryFrom = carryFrom;
        OnThisComputer = onThisComputer;
    }

    /// <summary>The music folder.</summary>
    public string Root { get; }

    /// <summary>The database file.</summary>
    public string DatabasePath { get; }

    /// <summary>True when the database is kept on this computer because the music folder is on a
    /// network volume.</summary>
    public bool OnThisComputer { get; }

    /// <summary>A database to copy to <see cref="DatabasePath"/> if no file is there when it is opened.
    /// Null when there is none to copy.</summary>
    internal Carried? CarryFrom { get; }

    /// <summary>A database to copy in, and whether it is on the network volume. The two are one value
    /// because the second decides how the copy is read. A file on the share is copied as files, and it is
    /// never opened with SQLite there (see <c>LibraryDatabase.Carry</c>).
    /// </summary>
    internal sealed record Carried(string Path, bool OnNetworkVolume);

    /// <summary>The database inside the folder, whatever volume it is on. This is for a folder that is
    /// always on this computer: a borrowed shelf under the app's data folder, the gallery's demo libraries,
    /// or a test's temporary folder. The user's own library uses <see cref="For"/>.
    /// </summary>
    /// <remarks>Reaches the disk. A legacy <c>.grooves</c> folder is moved to <c>.rpm</c> first. Every open
    /// does this.</remarks>
    public static MirrorLocation InFolder(string libraryRoot)
    {
        LibraryDatabase.CarryLegacyFolderOver(libraryRoot);
        return new MirrorLocation(libraryRoot, InFolderPath(libraryRoot), carryFrom: null, onThisComputer: false);
    }

    /// <summary>Where the user's library at <paramref name="libraryRoot"/> keeps its database: on this
    /// computer when the folder is on a network volume, and in the folder otherwise.
    /// </summary>
    /// <param name="thisComputer">The folder this computer keeps library databases in
    /// (<see cref="LibrariesFolderName"/> under the app's local data folder).</param>
    /// <exception cref="DirectoryNotFoundException">The music folder is not there.</exception>
    /// <exception cref="InvalidDataException">The folder's <c>.rpm/library.json</c> does not hold a library
    /// id, and the id is needed to find the database.</exception>
    /// <exception cref="IOException">The id file is empty or locked, because another open is writing it.
    /// Opening again recovers.</exception>
    /// <remarks>Reaches the disk. It moves a <c>.grooves</c> folder to <c>.rpm</c>, asks for the folder's
    /// volume, and reads the id file. The first time a network folder is opened, it also writes the id
    /// file.</remarks>
    public static MirrorLocation For(string libraryRoot, string thisComputer)
    {
        // Asked here as well as through the volume. On Windows, a missing folder under C:\ is on a fixed
        // drive. Only macOS and Linux answer NoRootDirectory for it.
        if (!Directory.Exists(libraryRoot))
            throw new DirectoryNotFoundException($"The music folder {libraryRoot} is not there.");

        return For(libraryRoot, thisComputer, VolumeOf(libraryRoot));
    }

    /// <summary><see cref="For(string, string)"/> with the volume already asked, so a test can stand a
    /// network volume in.</summary>
    internal static MirrorLocation For(string libraryRoot, string thisComputer, DriveType volume)
    {
        // This runs before the id file is written. Writing the id file creates .rpm, and that would stop
        // the move.
        LibraryDatabase.CarryLegacyFolderOver(libraryRoot);
        var inFolder = InFolderPath(libraryRoot);

        switch (volume)
        {
            case DriveType.NoRootDirectory:
                // The folder has gone between the check above and this answer. Creating the data folder
                // here would also create the music folder, at a mount point that the share comes back to
                // under another name.
                throw new DirectoryNotFoundException($"The music folder {libraryRoot} is not there.");

            case DriveType.Network:
            {
                var id = LibraryId.ReadOrCreate(libraryRoot);
                return new MirrorLocation(
                    libraryRoot,
                    Path.Combine(thisComputer, id, LibraryDatabase.DataFileName),
                    carryFrom: File.Exists(inFolder) ? new Carried(inFolder, OnNetworkVolume: true) : null,
                    onThisComputer: true);
            }

            default:
            {
                // The id is read only when there is no database in the folder. It matters only for copying
                // one in. An unreadable id file must not stop a folder that has a database.
                Carried? carryBack = null;
                if (!File.Exists(inFolder) && LibraryId.Read(libraryRoot) is { } id)
                {
                    var onThisComputer = Path.Combine(thisComputer, id, LibraryDatabase.DataFileName);
                    if (File.Exists(onThisComputer))
                        carryBack = new Carried(onThisComputer, OnNetworkVolume: false);
                }

                return new MirrorLocation(libraryRoot, inFolder, carryBack, onThisComputer: false);
            }
        }
    }

    private static string InFolderPath(string libraryRoot) =>
        Path.Combine(libraryRoot, LibraryDatabase.DataFolderName, LibraryDatabase.DataFileName);

    /// <summary>What kind of volume the folder is on. This is <see cref="DriveType.Network"/> for SMB,
    /// AFP, NFS and WebDAV mounts on macOS and Linux (.NET reads the file system's type name with statfs),
    /// and on Windows for a mapped network drive or a UNC path. It is
    /// <see cref="DriveType.NoRootDirectory"/> when the path is not there.
    /// </summary>
    internal static DriveType VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return new DriveInfo(full).DriveType;

        if (IsWindowsShare(full))
            return DriveType.Network;

        // Any other \\?\ or \\.\ path (a volume GUID, or a device) is one that DriveInfo cannot ask about.
        // It is treated as this computer's own disk.
        return WindowsDriveRoot(full) is { } drive ? new DriveInfo(drive).DriveType : DriveType.Unknown;
    }

    /// <summary>Whether a full Windows path is on a network share: <c>\\server\share\…</c> or
    /// <c>\\?\UNC\server\share\…</c>. These are plain string checks, so the rule can be tested on any
    /// platform.</summary>
    internal static bool IsWindowsShare(string full)
    {
        var path = full.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return true;
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    /// <summary>The drive root (<c>C:\</c>) of <c>C:\…</c>, <c>\\?\C:\…</c> or <c>\\.\C:\…</c>. It is null
    /// for any other path.</summary>
    internal static string? WindowsDriveRoot(string full)
    {
        var path = full.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            path = path[4..];
        return IsDriveLetter(path) ? $"{char.ToUpperInvariant(path[0])}:\\" : null;
    }

    private static bool IsDriveLetter(string path) =>
        path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    /// <summary>The music folder's <c>.rpm/library.json</c>: <c>{"id":"&lt;32 hex digits&gt;"}</c>. The id
    /// names a folder on this computer. So anything other than 32 lowercase hex digits is refused, rather
    /// than being joined to a path.
    /// </summary>
    internal static class LibraryId
    {
        public static string PathIn(string libraryRoot) =>
            Path.Combine(libraryRoot, LibraryDatabase.DataFolderName, IdFileName);

        /// <summary>The folder's id, or null when it has no id file.</summary>
        /// <exception cref="InvalidDataException">The file is there and does not hold an id.</exception>
        /// <exception cref="IOException">The file is empty: another open may be writing it.</exception>
        public static string? Read(string libraryRoot)
        {
            var path = PathIn(libraryRoot);

            // This check runs before the read, so the first open of a folder throws nothing. A debugger set
            // to break on thrown exceptions stops on every one of them, handled or not. LibraryService.Open
            // runs on the interface thread. The catch below is for a file that is removed between the check
            // and the read.
            if (!File.Exists(path))
                return null;

            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            // **An I/O failure, not a broken file.** An empty file is one that another open has created
            // and not yet written, or one whose writer ended in between. The two cannot be told apart
            // without waiting, and a wait is a time window, so both refuse the open the way meeting the
            // winner's lock does, and Try again recovers from the first. On Linux the winner's lock is
            // taken after the create, so a racing reader can find the file empty. Read as unreadable,
            // a first open of a network folder would say the library's data could not be read.
            if (text.Length == 0)
                throw new IOException(
                    $"{path} is empty. Another open may be writing it; try again. If it stays empty, delete it.");

            return Parse(text)
                ?? throw new InvalidDataException($"{path} does not hold a library id.");
        }

        /// <summary>The folder's id, writing a new one when the folder has none.
        ///
        /// <para>The file is created with <see cref="FileMode.CreateNew"/>. On a local disk, and on an SMB,
        /// AFP or NFS server, that is one atomic step. When two opens race on the first write (another
        /// process, or another computer on the share), exactly one creates the file, and its id becomes the
        /// library's id. The other open reads that id. If it reads while the winner is still writing, it
        /// fails with the file named, and trying again recovers. No ordering gives two opens two ids. A
        /// move without overwriting is not used. On macOS, .NET does that as a check that the name is free,
        /// followed by a rename. Two racers can both pass the check.</para>
        ///
        /// <para>A write that fails deletes the file it created.</para>
        /// </summary>
        public static string ReadOrCreate(string libraryRoot)
        {
            if (Read(libraryRoot) is { } known)
                return known;

            var path = PathIn(libraryRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            FileStream created;
            try
            {
                created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another open created it first.
                return Read(libraryRoot)
                    ?? throw new IOException($"{path} was removed while it was being read.");
            }

            var id = Guid.NewGuid().ToString("N");
            try
            {
                using (created)
                {
                    created.Write(Encoding.UTF8.GetBytes(Format(id)));
                    created.Flush(flushToDisk: true);
                }
            }
            catch
            {
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }

            Said.Line("db", $"wrote library id {id} into a music folder on a network volume");
            return id;
        }

        public static string Format(string id) => $"{{\"id\":\"{id}\"}}\n";

        /// <summary>The id in the file's text, or null when the text does not hold a valid one.</summary>
        public static string? Parse(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("id", out var value)
                    || value.ValueKind != JsonValueKind.String)
                    return null;

                var id = value.GetString()!;
                return id.Length == 32 && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f') ? id : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

/// <summary>
/// Which database a music folder on this computer's own disk opens, and what is carried or set aside first.
///
/// <para><b>The places.</b> <c>F</c> is the music folder's own <c>.rpm/library.db</c>. <c>L</c> is
/// <c>&lt;database folder&gt;/&lt;id&gt;/library.db</c>, found through the id in <c>.rpm/library.json</c>.
/// <c>L'</c> is the same file in a database folder chosen earlier (<see cref="DatabasePlaces.Before"/>).
/// The place the setting asks for is the target: <c>L</c> when <see cref="DatabasePlaces.OffMusicDisk"/>
/// is on, <c>F</c> when it is off.</para>
///
/// <para><b>The rule.</b> Every database that exists for the library is found. With none, a new one is made
/// at the target. With one, it is opened at the target, and moved there first if it is somewhere else. With
/// two, the newer one is kept (see <see cref="Newest"/>) and moved to the target if needed, and the other
/// one is renamed aside as <c>library.db.older</c>. A database is never deleted, and two databases for one
/// library are never both left where an open could find them.</para>
///
/// <para><b>The states, with the switch on.</b></para>
/// <list type="bullet">
/// <item>No id file: the id is recovered from a claim in the database folder (see <see cref="RecoverId"/>),
/// or a new id is written. Then as below.</item>
/// <item>An id file that does not hold an id: <c>F</c> is opened where it is, nothing moves. With no
/// <c>F</c>, the open is refused, as for a network folder.</item>
/// <item>Only <c>F</c>: moved to <c>L</c>; <c>F</c> becomes <c>library.db.moved</c>.</item>
/// <item>Only <c>L</c>: opened. This is the usual state.</item>
/// <item>Only <c>L'</c>: moved to <c>L</c>.</item>
/// <item><c>F</c> and <c>L</c> (or <c>L'</c>): the newer one is kept at <c>L</c>.</item>
/// <item>Neither: a new database at <c>L</c>.</item>
/// </list>
///
/// <para><b>The states, with the switch off.</b></para>
/// <list type="bullet">
/// <item>Only <c>F</c>, or no id file and <c>F</c>: opened. This is the usual state.</item>
/// <item>Only <c>L</c> or <c>L'</c> (the switch was just turned off, or the folder came off a network
/// volume): moved back to <c>F</c>; the old one becomes <c>library.db.moved</c>.</item>
/// <item><c>F</c> and <c>L</c> (or <c>L'</c>): the newer one is kept at <c>F</c>.</item>
/// <item>No id file and no <c>F</c>: the id is recovered from a claim if there is one, then as above.</item>
/// <item>Neither: a new database at <c>F</c>.</item>
/// </list>
///
/// <para><b>A move that cannot be made</b> (another program holds the database, or the copy fails its check)
/// changes nothing: the database is opened where it is, and the move is tried again on the next open. See
/// <c>LibraryDatabase.Carry</c>.</para>
/// </summary>
public sealed partial class MirrorLocation
{
    /// <summary>The claim file in each library's folder in the database folder, which names the music folder
    /// the database belongs to. It is what finds the database again when the music folder has lost its id
    /// file.</summary>
    public const string ClaimFileName = "folder.json";

    internal static string OffDiskPath(string thisComputer, string id) =>
        Path.Combine(thisComputer, id, LibraryDatabase.DataFileName);

    /// <summary>A local music folder whose database is kept in the database folder.</summary>
    private static MirrorLocation OffTheMusicDisk(string libraryRoot, DatabasePlaces places, string inFolder)
    {
        string id;
        try
        {
            id = LibraryId.Read(libraryRoot) ?? RecoverId(libraryRoot, places) ?? LibraryId.ReadOrCreate(libraryRoot);
        }
        catch (InvalidDataException) when (File.Exists(inFolder))
        {
            // Without an id there is no way to find the database folder's copy, so moving the folder's own
            // database there would make one that a later open cannot find. It stays where it is.
            Said.Line("db", "the music folder's library.json holds no id, so the database stays in the music folder");
            return new MirrorLocation(libraryRoot, inFolder, carryFrom: null, onThisComputer: false);
        }

        var found = new List<string>();
        if (OffDiskDatabase(places, id) is { } off)
            found.Add(off);
        if (File.Exists(inFolder))
            found.Add(inFolder);

        var location = Settle(libraryRoot, OffDiskPath(places.ThisComputer, id), found, onThisComputer: true);
        Claim(places.ThisComputer, id, libraryRoot);
        return location;
    }

    /// <summary>A local music folder whose database is kept in the folder.</summary>
    private static MirrorLocation InTheMusicFolder(string libraryRoot, DatabasePlaces places, string inFolder)
    {
        if (File.Exists(inFolder))
        {
            // An unreadable id file must not stop a folder that has a database. It only hides a copy in the
            // database folder, which stays there untouched.
            string? id;
            try
            {
                id = LibraryId.Read(libraryRoot);
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                id = null;
            }

            var away = id is null ? null : OffDiskDatabase(places, id);
            return Settle(libraryRoot, inFolder, away is null ? [inFolder] : [inFolder, away], onThisComputer: false);
        }

        var known = LibraryId.Read(libraryRoot) ?? RecoverId(libraryRoot, places);
        var elsewhere = known is null ? null : OffDiskDatabase(places, known);
        return Settle(libraryRoot, inFolder, elsewhere is null ? [] : [elsewhere], onThisComputer: false);
    }

    /// <summary>The database for <paramref name="id"/> in the database folder, or in one chosen earlier.
    /// Null when there is none.</summary>
    private static string? OffDiskDatabase(DatabasePlaces places, string id)
    {
        var here = OffDiskPath(places.ThisComputer, id);
        return File.Exists(here) ? here : Former(places, id);
    }

    /// <summary>The newest database for <paramref name="id"/> in a database folder chosen earlier, or
    /// null.</summary>
    internal static string? Former(DatabasePlaces places, string id)
    {
        var found = places.Elsewhere.Select(place => OffDiskPath(place, id)).Where(File.Exists).ToList();
        return found.Count == 0 ? null : Newest(found);
    }

    /// <summary>The location for the databases <paramref name="found"/> for one library, the one the
    /// setting asks for listed first. See the rule in the summary above.</summary>
    private static MirrorLocation Settle(
        string libraryRoot, string target, IReadOnlyList<string> found, bool onThisComputer)
    {
        if (found.Count == 0)
            return new MirrorLocation(libraryRoot, target, carryFrom: null, onThisComputer);

        var keep = Newest(found);
        var older = found.Where(f => !SamePath(f, keep)).ToList();
        if (older.Count > 0)
            Said.Line("db", "carry: two places hold this library's database; the newer one is kept, the other is set aside");

        var carry = SamePath(keep, target) ? null : new Carried(keep, OnNetworkVolume: false);
        return new MirrorLocation(libraryRoot, target, carry, onThisComputer, older);
    }

    /// <summary>
    /// The newest of two or more databases for one library. The first one wins a tie.
    ///
    /// <para><b>Read from the databases themselves.</b> Every open writes a note into
    /// <c>library_meta</c> (the launch count of the generated lists), and each note carries the time it was
    /// written. So the database with the latest note is the one that was opened last. A database with no
    /// such note (an older build's, or one that cannot be read) cannot be compared that way, and then all
    /// of them are compared by the time their files were last written.</para>
    /// </summary>
    internal static string Newest(IReadOnlyList<string> found)
    {
        if (found.Count == 1)
            return found[0];

        var notes = found.Select(LastNote).ToList();
        var marks = notes.All(n => n is not null)
            ? notes.Select(n => n!.Value).ToList()
            : found.Select(LastWritten).ToList();

        var best = 0;
        for (var i = 1; i < found.Count; i++)
            if (marks[i] > marks[best])
                best = i;
        return found[best];
    }

    /// <summary>The time of the latest note in the database, in UTC ticks, or null when it has none or
    /// cannot be read. Opened read-only: nothing in the file is changed.</summary>
    private static long? LastNote(string database)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(noted_utc_ticks) FROM library_meta";
            return cmd.ExecuteScalar() is long ticks and > 0 ? ticks : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <summary>When the database or its <c>-wal</c> was last written, in UTC ticks.</summary>
    private static long LastWritten(string database)
    {
        var main = File.GetLastWriteTimeUtc(database).Ticks;
        var wal = database + "-wal";
        return File.Exists(wal) ? Math.Max(main, File.GetLastWriteTimeUtc(wal).Ticks) : main;
    }

    /// <summary>
    /// The id of a music folder that has lost its id file, found from the claims in the database folders,
    /// and written back into the folder. Null when no database claims this folder.
    ///
    /// <para>Without this, a folder whose database is kept in the database folder would get a new id when
    /// its id file was deleted, and open an empty database. The old one would still be there, under an id
    /// nothing names any more.</para>
    /// </summary>
    private static string? RecoverId(string libraryRoot, DatabasePlaces places)
    {
        var claimed = new List<(string Id, string Database)>();
        foreach (var place in (IEnumerable<string>)[places.ThisComputer, .. places.Elsewhere])
        {
            List<string> folders;
            try
            {
                if (!Directory.Exists(place))
                    continue;
                folders = Directory.EnumerateDirectories(place).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var folder in folders)
            {
                var id = Path.GetFileName(folder);
                var database = Path.Combine(folder, LibraryDatabase.DataFileName);
                if (LibraryId.IsId(id) && File.Exists(database)
                    && ClaimedRoot(Path.Combine(folder, ClaimFileName)) is { } root && SamePath(root, libraryRoot))
                    claimed.Add((id, database));
            }
        }

        if (claimed.Count == 0)
            return null;

        var newest = Newest(claimed.Select(c => c.Database).ToList());
        var found = claimed.First(c => c.Database == newest).Id;
        Said.Line("db", "the music folder had no library.json; its database was found by the claim in the database folder");
        return LibraryId.Restore(libraryRoot, found);
    }

    /// <summary>Writes the claim beside the library's database: <c>{"root":"&lt;music folder&gt;"}</c>. It
    /// is written only when it is missing or names another folder. A claim that cannot be written is
    /// logged: it is needed only if the id file is lost later.</summary>
    private static void Claim(string thisComputer, string id, string libraryRoot)
    {
        var file = Path.Combine(thisComputer, id, ClaimFileName);
        try
        {
            var root = Path.GetFullPath(libraryRoot);
            if (ClaimedRoot(file) is { } claimed && claimed == root)
                return;

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("root", root);
                json.WriteEndObject();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Encoding.UTF8.GetString(buffer.ToArray()) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Said.Line("db", $"could not write the database folder's claim ({e.GetType().Name})");
        }
    }

    /// <summary>The music folder a claim names, or null when the file is missing or does not hold
    /// one.</summary>
    private static string? ClaimedRoot(string file)
    {
        try
        {
            if (!File.Exists(file))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("root", out var root)
                   && root.ValueKind == JsonValueKind.String
                ? root.GetString()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A path in a form that two spellings of one folder share: full, with no separator at the end,
    /// and in one case except on Linux, whose file systems are case-sensitive.</summary>
    internal static string Comparable(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsLinux() ? full : full.ToUpperInvariant();
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Comparable(a), Comparable(b), StringComparison.Ordinal);
}

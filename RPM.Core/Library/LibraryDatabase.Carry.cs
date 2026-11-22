using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>The name an old database gets when it has been moved somewhere else:
    /// <c>library.db.moved</c>.</summary>
    internal const string MovedSuffix = ".moved";

    /// <summary>The name a database gets when a newer one for the same library was kept instead:
    /// <c>library.db.older</c>.</summary>
    internal const string OlderSuffix = ".older";

    /// <summary>The name of a <c>-wal</c> or <c>-shm</c> file found with no database beside it.</summary>
    internal const string StraySuffix = ".stray";

    /// <summary>
    /// Does what <paramref name="location"/> asks before the database is opened: sets the older copies
    /// aside, and carries a database to <see cref="MirrorLocation.DatabasePath"/>. Returns the file to
    /// open. That is <see cref="MirrorLocation.DatabasePath"/>, except when a move could not be made: then
    /// it is the database where it already is.
    /// </summary>
    private static string Prepare(MirrorLocation location)
    {
        var path = location.DatabasePath;

        foreach (var older in location.SetAside)
            SetAside(older, OlderSuffix);

        if (location.CarryFrom is { } from)
        {
            if (!File.Exists(path))
            {
                SetAsideStrays(path);
                return Carry(from, path);
            }

            // The older copy that stands at the target could not be renamed: another program has it open. The
            // newer one is opened where it is, so the older one is not opened in its place.
            if (location.SetAside.Any(older => MirrorLocation.SamePath(older, path)))
            {
                Said.Line("db", "carry: the older database is in use and was not set aside; opening the newer one where it is");
                return from.Path;
            }

            return path;
        }

        if (!File.Exists(path))
            SetAsideStrays(path);
        return path;
    }

    /// <summary>
    /// Carries the database that <paramref name="from"/> names to <paramref name="to"/>, which has no file
    /// yet, and returns the file to open.
    ///
    /// <para><b>From a share</b> it is copied as files (<see cref="CopyFromShare"/>), because SQLite opening a
    /// WAL file there needs a <c>-shm</c> beside it. The share's file is not changed or deleted, because it
    /// belongs to the folder and to every computer that opens it. A copy that fails throws, which refuses
    /// the open.</para>
    ///
    /// <para><b>From this computer</b> it is moved (<see cref="Move"/>).</para>
    ///
    /// <para>The copy is written under a <c>.part</c> name of its own and moved to <paramref name="to"/>
    /// without overwriting. After a move succeeds, every other <c>.part</c> file beside it is deleted. Each
    /// of those was left by a process that ended while copying, or belongs to another process on this
    /// computer that is copying at the same time.</para>
    ///
    /// <para>If anything goes wrong while <paramref name="to"/> exists, another process finished first: its
    /// move won, or its sweep deleted this copy's file. This copy is dropped and that file is opened. The
    /// check is on the file and not on which error arrived, so the order in which the two processes reach
    /// each step does not matter.</para>
    /// </summary>
    private static string Carry(MirrorLocation.Carried from, string to)
    {
        if (!from.OnNetworkVolume)
            return Move(from.Path, to);

        var part = PartName(to);
        var started = Stopwatch.GetTimestamp();
        try
        {
            CopyFromShare(from.Path, part);
            File.Move(part, to, overwrite: false);
        }
        catch (Exception) when (File.Exists(to))
        {
            TryDeleteCarry(part);
            Said.Line("db", "carry: another process copied the database here first; opening its copy");
            return to;
        }
        catch
        {
            TryDeleteCarry(part);
            throw;
        }

        // The ring line contains neither path.
        Said.Line("db",
            $"carry: copied the library database to this computer, " +
            $"{new FileInfo(to).Length / 1_048_576} MB in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");

        RemoveUnfinishedCarries(to);
        return to;
    }

    /// <summary>
    /// Moves a database on this computer to <paramref name="to"/>, and returns the file to open.
    ///
    /// <para><b>No other connection can write it meanwhile.</b> The source is opened in SQLite's exclusive
    /// locking mode first. That fails when another program has the database in a transaction, and then
    /// nothing is moved. Once it holds, nobody else can read or write the file until this connection
    /// closes, so the copy cannot miss a write.</para>
    ///
    /// <para><b>The copy is checked before anything else happens.</b> It is made with SQLite's backup API,
    /// then <c>PRAGMA integrity_check</c> must answer <c>ok</c> and its page count must match the source's.
    /// The copy is left as one file (<c>journal_mode=DELETE</c>) and moved into place.</para>
    ///
    /// <para><b>Only then is the source touched.</b> Its <c>-wal</c> is written into its main file, still
    /// under the exclusive lock, and it is renamed aside as <c>library.db.moved</c>. It is never deleted. If
    /// the rename fails, both places hold the same data, and the next open keeps the newer one, which is the
    /// one opened now.</para>
    ///
    /// <para>If the lock cannot be taken or the copy fails its check, the copy is deleted, the source is left
    /// exactly as it was, and it is opened where it is. The move is tried again on the next open.</para>
    /// </summary>
    private static string Move(string from, string to)
    {
        var part = PartName(to);
        var started = Stopwatch.GetTimestamp();

        if (OpenElsewhere(from))
        {
            Said.Line("db", "carry: another program has the database open; it stays where it is for now");
            return from;
        }

        SqliteConnection source;
        try
        {
            source = OpenAlone(from);
        }
        catch (SqliteException e)
        {
            Said.Line("db", $"carry: the database is in use (SQLite {e.SqliteErrorCode}); it stays where it is for now");
            return from;
        }

        using (source)
        {
            try
            {
                var pages = Pages(source);
                using (var target = Open(part, SqliteOpenMode.ReadWriteCreate))
                    source.BackupDatabase(target);
                Check(part, pages);
                File.Move(part, to, overwrite: false);
            }
            catch (Exception) when (File.Exists(to))
            {
                TryDeleteCarry(part);
                Said.Line("db", "carry: another process moved the database here first; opening its copy");
                return to;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException
                                          or InvalidDataException)
            {
                TryDeleteCarry(part);
                Said.Line("db", $"carry: the copy was not made or failed its check ({e.GetType().Name}); " +
                                "the database stays where it is");
                return from;
            }

            // The ring line contains neither path.
            Said.Line("db",
                $"carry: moved the library database and checked the copy, " +
                $"{new FileInfo(to).Length / 1_048_576} MB in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
            RemoveUnfinishedCarries(to);

            // The source becomes one file while the lock still holds, so it is renamed whole.
            TryFold(source);
        }

        SetAside(from, MovedSuffix);
        return to;
    }

    /// <summary>
    /// Whether another handle has the database or its <c>-wal</c> open. Windows only: there, opening the file
    /// with no sharing fails while any other handle is open. SQLite's exclusive lock alone does not show an
    /// idle connection, and a database that another rpm still has open must not be moved from under it.
    /// macOS and Linux have no such check, and rely on the lock.
    /// </summary>
    private static bool OpenElsewhere(string database)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        foreach (var file in (string[])[database, database + "-wal"])
        {
            if (!File.Exists(file))
                continue;
            try
            {
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A connection to <paramref name="database"/> that holds it alone: exclusive locking mode, with
    /// the lock taken now. Throws <see cref="SqliteException"/> (busy) when another connection is using
    /// it.</summary>
    private static SqliteConnection OpenAlone(string database)
    {
        var connection = Open(database, SqliteOpenMode.ReadWrite);
        try
        {
            using var cmd = connection.CreateCommand();
            // A short wait: another open of the same file may be rebuilding its index (BUSY_RECOVERY). A
            // program that holds the database for longer is not waited for; the move is tried next time.
            cmd.CommandText = "PRAGMA busy_timeout=1000; PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; COMMIT;";
            cmd.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection Open(string database, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Pages(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA page_count";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Refuses a copy that is damaged or not whole. Leaves a whole one as a single file.</summary>
    /// <exception cref="InvalidDataException">The copy failed the check.</exception>
    private static void Check(string copy, long sourcePages)
    {
        using var connection = Open(copy, SqliteOpenMode.ReadWrite);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        var verdict = Convert.ToString(cmd.ExecuteScalar());
        if (verdict != "ok")
            throw new InvalidDataException($"The copy of the library database is damaged ({verdict}).");

        var pages = Pages(connection);
        if (pages != sourcePages)
            throw new InvalidDataException($"The copy of the library database has {pages} pages, not {sourcePages}.");

        cmd.CommandText = "PRAGMA journal_mode=DELETE";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Writes the <c>-wal</c> into the main file and removes it, so the database is one file. Not
    /// required: a database that keeps its <c>-wal</c> is renamed together with it.</summary>
    private static void TryFold(SqliteConnection connection)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            Said.Line("db", $"carry: the old database keeps its -wal (SQLite {e.SqliteErrorCode})");
        }
    }

    /// <summary>
    /// Renames a database aside: <c>library.db</c> becomes <c>library.db&lt;suffix&gt;</c>, with its
    /// <c>-wal</c> and <c>-shm</c> renamed to match, so the renamed file still opens with everything in it.
    /// A name that is taken gets a number (<c>library.db.moved.2</c>). Never deletes. Returns the new name,
    /// or null when the database is not there or could not be renamed (another program has it open).
    /// </summary>
    internal static string? SetAside(string database, string suffix)
    {
        if (!File.Exists(database))
            return null;

        if (OpenElsewhere(database))
        {
            Said.Line("db", "carry: another program has the old database open; it is kept where it is");
            return null;
        }

        // One file is one rename. The fold is skipped when the database is in use, and then the rename
        // below fails as well and nothing is changed.
        try
        {
            using var alone = OpenAlone(database);
            TryFold(alone);
        }
        catch (SqliteException)
        {
        }

        var name = FreeName(database + suffix);
        try
        {
            File.Move(database, name, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Said.Line("db", $"carry: could not rename the old database aside ({e.GetType().Name}); it is kept where it is");
            return null;
        }

        foreach (var side in (string[])["-wal", "-shm"])
            TryRename(database + side, name + side);

        Said.Line("db", $"carry: the old database is kept as {Path.GetFileName(name)}");
        return name;
    }

    /// <summary>A <c>-wal</c> or <c>-shm</c> with no database beside it is renamed aside before a database is
    /// made or carried there. SQLite would read such a <c>-wal</c> into the new file.</summary>
    private static void SetAsideStrays(string database)
    {
        foreach (var side in (string[])["-wal", "-shm"])
            if (File.Exists(database + side))
                TryRename(database + side, FreeName(database + StraySuffix) + side);
    }

    private static void TryRename(string from, string to)
    {
        if (!File.Exists(from))
            return;
        try
        {
            File.Move(from, to, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Said.Line("db", $"carry: could not rename {Path.GetFileName(from)} ({e.GetType().Name})");
        }
    }

    /// <summary><paramref name="wanted"/>, or with <c>.2</c>, <c>.3</c> and so on after it, whichever is the
    /// first name that neither a file nor its <c>-wal</c> or <c>-shm</c> uses.</summary>
    private static string FreeName(string wanted)
    {
        static bool Taken(string name) =>
            File.Exists(name) || File.Exists(name + "-wal") || File.Exists(name + "-shm");

        if (!Taken(wanted))
            return wanted;
        for (var n = 2; ; n++)
            if (!Taken($"{wanted}.{n}"))
                return $"{wanted}.{n}";
    }

    private static string PartName(string to) => $"{to}.{Guid.NewGuid():N}.part";

    /// <summary>
    /// The share's <c>library.db</c> and its <c>-wal</c>, copied as files to <paramref name="part"/>,
    /// then opened on this computer.
    ///
    /// <para><b>Nothing that SQLite opens is on the share.</b> A WAL file opened there needs a
    /// <c>-shm</c> beside it, and a WebDAV mount refuses to create one (SQLITE_CANTOPEN). On SMB, the
    /// app keeps the shared-memory index off the share. The <c>-wal</c> is copied because an older
    /// build that ended without a checkpoint, or one still open on another computer, has rows only
    /// there. The <c>-shm</c> is not copied, because SQLite rebuilds it from the <c>-wal</c>.</para>
    ///
    /// <para><b>Then it is checked here.</b> SQLite reads the <c>-wal</c> into the copy.
    /// <c>quick_check</c> refuses a copy torn by another computer writing while it was read.
    /// <c>journal_mode=DELETE</c> writes the <c>-wal</c>'s pages into the main file and removes the
    /// <c>-wal</c>, so the move takes one whole file.</para>
    /// </summary>
    private static void CopyFromShare(string from, string part)
    {
        File.Copy(from, part);
        if (File.Exists(from + "-wal"))
            File.Copy(from + "-wal", part + "-wal");

        using var copy = Open(part, SqliteOpenMode.ReadWrite);
        using var cmd = copy.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        var verdict = Convert.ToString(cmd.ExecuteScalar());
        if (verdict != "ok")
            throw new InvalidDataException(
                $"The copy of {from} is damaged ({verdict}). Another computer may have been writing it; try again.");

        cmd.CommandText = "PRAGMA journal_mode=DELETE";
        cmd.ExecuteNonQuery();
    }

    private static void RemoveUnfinishedCarries(string to)
    {
        string[] parts;
        try
        {
            parts = Directory.GetFiles(Path.GetDirectoryName(to)!, Path.GetFileName(to) + ".*.part*");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in parts)
            TryDeleteFile(file);
    }

    private static void TryDeleteCarry(string part)
    {
        foreach (var suffix in (string[])["", "-journal", "-wal", "-shm"])
            TryDeleteFile(part + suffix);
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Said.Line("db", $"carry: could not delete an unfinished copy ({e.GetType().Name})");
        }
    }
}

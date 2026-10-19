using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Copies the database that <paramref name="from"/> names to <paramref name="to"/>, which has no
    /// file yet. This is either a network folder's own database copied onto this computer, or this
    /// computer's database copied into a folder moved off a network volume (<see cref="MirrorLocation"/>).
    /// Throws when the copy fails, which refuses the open.
    ///
    /// <para><b>How the source is read depends on where it is</b>, and the source says which
    /// (<see cref="MirrorLocation.Carried"/>). On the share it is copied as files
    /// (<see cref="CopyFromShare"/>), because SQLite opening a WAL file there needs a <c>-shm</c> beside
    /// it. On this computer it is read with SQLite's backup API through a connection of its own, so the
    /// copy is one consistent state even if another rpm process here is writing it.</para>
    ///
    /// <para>The copy is written under a <c>.part</c> name of its own and moved to
    /// <paramref name="to"/> without overwriting. After a move succeeds, every other <c>.part</c> file
    /// beside it is deleted. Each of those was left by a process that ended while copying, or belongs
    /// to another process on this computer that is copying at the same time.</para>
    ///
    /// <para>If anything goes wrong while <paramref name="to"/> exists, another process finished
    /// first: its move won, or its sweep deleted this copy's file. This copy is dropped and that file
    /// is opened. The check is on the file and not on which error arrived, so the order in which the
    /// two processes reach each step does not matter.</para>
    ///
    /// <para>The source is not changed or deleted.</para>
    /// </summary>
    private static void Carry(MirrorLocation.Carried from, string to)
    {
        var part = $"{to}.{Guid.NewGuid():N}.part";
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (from.OnNetworkVolume)
                CopyFromShare(from.Path, part);
            else
                BackUp(from.Path, part);

            File.Move(part, to, overwrite: false);
        }
        catch (Exception) when (File.Exists(to))
        {
            TryDeleteCarry(part);
            Said.Line("db", "carry: another process copied the database here first; opening its copy");
            return;
        }
        catch
        {
            TryDeleteCarry(part);
            throw;
        }

        // The ring line contains neither path.
        Said.Line("db",
            $"carry: copied the library database to {(from.OnNetworkVolume ? "this computer" : "the music folder")}, " +
            $"{new FileInfo(to).Length / 1_048_576} MB in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");

        RemoveUnfinishedCarries(to);
    }

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

        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = part,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        copy.Open();
        using var cmd = copy.CreateCommand();
        cmd.CommandText = "PRAGMA quick_check";
        var verdict = Convert.ToString(cmd.ExecuteScalar());
        if (verdict != "ok")
            throw new InvalidDataException(
                $"The copy of {from} is damaged ({verdict}). Another computer may have been writing it; try again.");

        cmd.CommandText = "PRAGMA journal_mode=DELETE";
        cmd.ExecuteNonQuery();
    }

    /// <summary>A database on this computer, copied with SQLite's backup API.</summary>
    private static void BackUp(string from, string part)
    {
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = from,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = part,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        source.Open();
        using (var cmd = source.CreateCommand())
        {
            // backup_step does not retry a lock it cannot take, so the wait is SQLite's own: another
            // open of the same WAL file may be rebuilding its index (BUSY_RECOVERY).
            cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA query_only=1";
            cmd.ExecuteNonQuery();
        }
        target.Open();
        source.BackupDatabase(target);
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

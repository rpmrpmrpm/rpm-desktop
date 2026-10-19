using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RPM.Core.Diagnostics;

namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The only way to run <see cref="Migrate"/>: every step runs inside one write transaction, and a
    /// copy of the file is taken first whenever a step is going to run.
    ///
    /// <para><b>The transaction</b> makes a failed migration leave the file as it was. Without it,
    /// each statement commits on its own, so a step that throws leaves the file part of the way
    /// between two versions, with nothing to go back to. <b>The copy</b> covers what a rollback
    /// cannot: a file damaged during the migration by the disk or the volume it is on.</para>
    ///
    /// <para><b>The copy's file.</b> <c>library.v61.backup.db</c> beside <c>library.db</c>, named for
    /// the version it holds. It is written under a <c>.part</c> name and renamed when complete, so a
    /// name without <c>.part</c> is always a whole copy. It is never overwritten: a copy already there
    /// at the file's own version was left by an attempt that failed, and it holds the file as it was
    /// before the first attempt.</para>
    ///
    /// <para><b>When it is deleted.</b> After the commit, and on every later open, every copy named
    /// for a version below <see cref="SchemaVersion"/> is deleted. The file can only be past a copy's
    /// version if the migration that took the copy committed, so this one rule deletes the copy on
    /// success and also removes a copy whose delete failed last time (a file held open by an indexer
    /// or a sync client).</para>
    ///
    /// <para><b>Under the write lock.</b> <c>BEGIN IMMEDIATE</c> runs before the version is read.
    /// Another process that opens the same file waits there, then reads the version this one committed,
    /// so two processes never migrate the same file at once and nothing commits between the copy
    /// and the first step. The copy is read through a second connection because SQLite's backup
    /// refuses a source connection that holds a write transaction. In WAL mode that connection reads
    /// the last commit, which is the state the steps start from. This holds between processes on one
    /// computer. Between two computers it is not needed for a library in a network folder, whose
    /// database is kept on each computer (<see cref="MirrorLocation"/>).</para>
    ///
    /// <para><see cref="Migrate"/> takes an instance of this class, and only <see cref="Run"/> can
    /// make one, so the compiler refuses a call to it from anywhere else.</para>
    /// </summary>
    private sealed class Upgrade
    {
        private const string CopyPrefix = "library.v";
        private const string CopySuffix = ".backup.db";

        private Upgrade() { }

        /// <summary>Brings the open file up to <see cref="SchemaVersion"/>, or leaves it as it was and
        /// throws. Called once, from the constructor, before the reading connection is opened.</summary>
        public static void Run(LibraryDatabase db)
        {
            var path = db.DatabasePath;

            // Taken on every open, not only when the file is behind. It adds no wait: the
            // constructor writes a launch note straight after this, which waits for the same lock.
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                long version = Convert.ToInt64(db.Scalar("PRAGMA user_version"));

                // A new file has no tables and nothing to keep. A v1 mirror reads as version 0 too,
                // so the version alone cannot tell them apart.
                if (version < SchemaVersion && HasTables(db))
                    KeepCopy(path, version);

                db.Migrate(new Upgrade());
                db.Execute("COMMIT");
            }
            catch
            {
                // SQLite ends the transaction itself on some errors (a full disk, an I/O error), and
                // a ROLLBACK then would throw over the error that matters.
                if (SQLitePCL.raw.sqlite3_get_autocommit(db._connection.Handle) == 0)
                    db.Execute("ROLLBACK");
                throw;
            }

            RemoveFinishedCopies(path);
        }

        private static bool HasTables(LibraryDatabase db) =>
            Convert.ToInt64(db.Scalar("SELECT count(*) FROM sqlite_master")) > 0;

        private static string CopyPath(string path, long version) =>
            Path.Combine(Path.GetDirectoryName(path)!, $"{CopyPrefix}{version}{CopySuffix}");

        /// <summary>Makes sure a whole copy of the file at <paramref name="version"/> is on disk, or
        /// throws, which refuses the migration.</summary>
        private static void KeepCopy(string path, long version)
        {
            var copy = CopyPath(path, version);
            if (File.Exists(copy))
                return;

            var part = copy + ".part";
            var started = Stopwatch.GetTimestamp();
            try
            {
                // Left by a process that ended while writing it.
                DeleteWithSidecars(part);

                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                       {
                           DataSource = path,
                           Mode = SqliteOpenMode.ReadWrite,
                           Pooling = false,
                       }.ToString()))
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                       {
                           DataSource = part,
                           Mode = SqliteOpenMode.ReadWriteCreate,
                           Pooling = false,
                       }.ToString()))
                {
                    source.Open();
                    using (var cmd = source.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA query_only=1";
                        cmd.ExecuteNonQuery();
                    }
                    target.Open();
                    source.BackupDatabase(target);
                }

                File.Move(part, copy);
            }
            catch
            {
                // The disk is the likely cause, and a partial copy left on it makes that worse.
                TryDeleteWithSidecars(part);
                throw;
            }

            Said.Line("db",
                $"upgrade: copied the library at v{version} before migrating to v{SchemaVersion}, " +
                $"{new FileInfo(copy).Length / 1_048_576} MB in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
        }

        /// <summary>Deletes every copy, and every unfinished copy, named for a version below
        /// <see cref="SchemaVersion"/>. Run only after a commit, when the file is at that version.</summary>
        private static void RemoveFinishedCopies(string path)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.GetFiles(Path.GetDirectoryName(path)!, CopyPrefix + "*");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The migration has committed; a folder that cannot be listed just now is not a reason
                // to refuse the library. The next open lists it again.
                Said.Line("db", $"upgrade: could not list the data folder for old copies ({e.GetType().Name})");
                return;
            }

            foreach (var file in files)
            {
                if (VersionOf(Path.GetFileName(file)) is long held && held < SchemaVersion)
                    TryDelete(file);
            }
        }

        /// <summary>The version in a copy's file name, including its <c>.part</c> and SQLite's
        /// <c>-journal</c>, <c>-wal</c> and <c>-shm</c> files; null for any other name.</summary>
        private static long? VersionOf(string name)
        {
            if (!name.StartsWith(CopyPrefix, StringComparison.Ordinal))
                return null;
            int end = name.IndexOf(CopySuffix, CopyPrefix.Length, StringComparison.Ordinal);
            if (end <= CopyPrefix.Length)
                return null;
            return long.TryParse(name.AsSpan(CopyPrefix.Length, end - CopyPrefix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? version
                : null;
        }

        private static readonly string[] Sidecars = ["", "-journal", "-wal", "-shm"];

        private static void DeleteWithSidecars(string file)
        {
            foreach (var suffix in Sidecars)
                File.Delete(file + suffix);
        }

        private static void TryDeleteWithSidecars(string file)
        {
            foreach (var suffix in Sidecars)
                TryDelete(file + suffix);
        }

        private static void TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open by another program. RemoveFinishedCopies tries again on the next open.
                Said.Line("db", $"upgrade: could not delete {Path.GetFileName(file)} ({e.GetType().Name})");
            }
        }
    }
}

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>A mirror's connection, whose commands report a refusal before it leaves them.
///
/// <para>Many call sites in the desktop app read or write the mirror directly, with no wrapper around the
/// call. <c>LibraryService.Read</c> covers only the screens that have been moved to it. Reporting at the
/// command means a refusal anywhere reaches <see cref="LibraryDatabase.Refused"/>, and from there the
/// problem screen. Each call site does not have to report it itself.</para>
///
/// <para><b>Covered:</b> the first step of a query, and every step of a command run to its end
/// (<see cref="SqliteCommand.ExecuteNonQuery"/>, <see cref="SqliteCommand.ExecuteScalar"/>). <b>Not
/// covered:</b> a later row of a query, read through <see cref="SqliteDataReader.Read"/>. The reader's
/// constructor is internal to Microsoft.Data.Sqlite, so it cannot be wrapped. A page damaged past a query's
/// first row fails there unreported, unless the read went through <c>LibraryService.Read</c>.</para>
/// </summary>
/// <param name="refused">Told of each refusal on the thread that ran the command, before the exception
/// leaves it. It is called from an exception filter, so the stack has not unwound yet. An exception thrown
/// inside it is discarded, so it cannot replace SQLite's exception.</param>
internal sealed class MirrorConnection(string connectionString, Action<SqliteCommand, SqliteException> refused)
    : SqliteConnection(connectionString)
{
    /// <summary>What <see cref="SqliteConnection.CreateCommand"/> does, with the command that
    /// reports.</summary>
    public override SqliteCommand CreateCommand() => new MirrorCommand(refused)
    {
        Connection = this,
        CommandTimeout = DefaultTimeout,
        Transaction = Transaction,
    };

    protected override DbCommand CreateDbCommand() => CreateCommand();

    private sealed class MirrorCommand(Action<SqliteCommand, SqliteException> refused) : SqliteCommand
    {
        // ExecuteNonQuery and ExecuteScalar both run through ExecuteReader, so one failure can pass through
        // two of these overrides. The refusal handler must count each exception only once.
        public override SqliteDataReader ExecuteReader(CommandBehavior behavior)
        {
            try { return base.ExecuteReader(behavior); }
            catch (SqliteException e) when (Report(e)) { throw; }
        }

        public override int ExecuteNonQuery()
        {
            try { return base.ExecuteNonQuery(); }
            catch (SqliteException e) when (Report(e)) { throw; }
        }

        public override object? ExecuteScalar()
        {
            try { return base.ExecuteScalar(); }
            catch (SqliteException e) when (Report(e)) { throw; }
        }

        /// <summary>Reports a refusal that the database is responsible for. It returns false, so the filter
        /// never catches: the exception goes on to the caller exactly as SQLite raised it.</summary>
        private bool Report(SqliteException error)
        {
            if (MirrorFaults.Of(error) is MirrorFault.Broken)
                refused(this, error);

            return false;
        }
    }
}

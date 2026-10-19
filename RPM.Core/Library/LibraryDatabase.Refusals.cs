using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Raised when SQLite refuses a command on this mirror with an error the database is responsible
    /// for (<see cref="MirrorFaults.Of"/>). It is raised on the thread that ran the command, before the
    /// exception reaches the caller. The event carries the command's SQL text and the exception. The
    /// caller still receives the exception. See <see cref="MirrorConnection"/> for which failures this
    /// covers.
    ///
    /// <para>Nothing is subscribed while the constructor runs, so the migration's own refusals, which
    /// it handles, are never raised.</para>
    /// </summary>
    public event Action<string, SqliteException>? Refused;

    private void ReportRefusal(SqliteCommand command, SqliteException error)
        => Refused?.Invoke(command.CommandText, error);
}

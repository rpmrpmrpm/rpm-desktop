using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// What an exception thrown by a read of a mirror says about the mirror.
///
/// <para>There are two answers, because a screen has to tell them apart. A mirror closed under the read
/// means the collection is being swapped for another one. The next <c>Changed</c> event then sends the
/// screen back to read the new collection. A mirror that refused means the collection cannot be used.
/// An empty screen in front of it would say "you have no music", which is wrong.</para>
/// </summary>
public enum MirrorFault
{
    /// <summary>The mirror was closed while the read was waiting for it or running: the library was
    /// replaced, or the app is quitting. <see cref="Gate.Close"/> throws
    /// <see cref="ObjectDisposedException"/> for every take after it.</summary>
    Closed,

    /// <summary>The database refused, with SQLite's own error: a damaged file, a disk that stopped
    /// answering, a lock another program holds. The collection cannot be read until it is opened
    /// again.</summary>
    Broken,
}

public static class MirrorFaults
{
    /// <summary>What <paramref name="error"/> says about the mirror, or null when it says nothing about
    /// the mirror. A null answer means a defect in the code around the read, which no caller catches.
    ///
    /// <para>Only <see cref="SqliteException"/> is the database. SQLite reports a disk error, a read-only
    /// volume and a file it cannot open as its own codes. <see cref="LibraryDatabase"/> touches no file but
    /// its own. So an <see cref="IOException"/> inside a read is about some other file that the caller
    /// opened. Reading it as a broken collection would put the problem screen up for a file another program
    /// has locked.</para>
    ///
    /// <para>Four of SQLite's codes come from the code, not from the file: a constraint the statement
    /// broke, a value of the wrong type, the API used wrongly, and a parameter index out of range. A
    /// collection that hits one of those is still readable, so these are not a refusal and are not
    /// caught.</para>
    /// </summary>
    public static MirrorFault? Of(Exception error) => error switch
    {
        ObjectDisposedException => MirrorFault.Closed,
        SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_CONSTRAINT or SQLitePCL.raw.SQLITE_MISMATCH
            or SQLitePCL.raw.SQLITE_MISUSE or SQLitePCL.raw.SQLITE_RANGE } => null,
        SqliteException => MirrorFault.Broken,
        _ => null,
    };
}

using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>How many lines the cloud log holds, and the id of the newest one.</summary>
/// <remarks>Two equal stamps of one database mean the same lines. A line is never changed after it is
/// written, and <c>id</c> is <c>AUTOINCREMENT</c>, so an id is never used again, also not after the log
/// is emptied. Any insert raises <see cref="NewestId"/>. A delete without an insert lowers
/// <see cref="Count"/>.</remarks>
public readonly record struct CloudActivityStamp(long Count, long NewestId);

/// <summary>
/// Reads the cloud log for a screen that shows it, and reads it again only when lines were added or
/// removed.
///
/// <para>The cloud engine reports a change many times per track (each stage, each count), and only
/// the end of a track writes a log line. Reading and showing all lines on every report makes the
/// settings page slow while an upload runs. A stamp read costs one small query.</para>
/// </summary>
public sealed class CloudActivityFollower
{
    private LibraryDatabase? _db;
    private CloudActivityStamp _stamp;

    /// <summary>The log, newest first, when it changed since the last read of the same database.
    /// Null when it did not change.</summary>
    public IReadOnlyList<CloudActivityEntry>? ReadIfChanged(LibraryDatabase db)
    {
        // The stamp is read BEFORE the lines. A line written between the two reads is then in the lines
        // but not in the stamp, so the next call reads again. In the other order, that line would be
        // missing from the lines while the stamp says it was read, and it would not show until the next
        // write.
        var stamp = db.StampCloudActivity();
        if (ReferenceEquals(db, _db) && stamp == _stamp)
            return null;

        var lines = db.QueryCloudActivity();
        _db = db;
        _stamp = stamp;
        return lines;
    }

    /// <summary>Forgets what was read, so the next <see cref="ReadIfChanged"/> reads the log. For a
    /// screen that opens again and must show the ages as they are now.</summary>
    public void Forget() => _db = null;
}

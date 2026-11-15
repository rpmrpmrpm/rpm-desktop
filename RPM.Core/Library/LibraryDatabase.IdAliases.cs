using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// The rpm ids that a song had before, and the id it has now.
///
/// <para>A song's id can change. When its audio is already in the cloud under another id, the upload
/// takes that id over (see <see cref="SetRpmId"/>), so the account does not hold the same recording
/// twice. Everything that named the song by its old id must follow. A playlist entry that does not
/// follow names a song that no longer exists. The library still plays it, because a playlist entry also
/// has the song's path. But the cloud copy of the playlist travels as ids only, so every other screen
/// counts that entry as a song it cannot find, and it never arrives.</para>
///
/// <para>Two things keep the ids in step. <see cref="SetRpmId"/> moves the playlist entries with the
/// song, and <see cref="RepairPlaylistIds"/> runs on every open and repairs entries that an older build
/// left behind. Both write the old id here, so that a playlist arriving from the cloud with an old id in
/// it (written by another device that still had the old id) can be read in today's ids
/// (<see cref="CurrentRpmId"/>).</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>How many steps an old id is followed at most. A song whose id changed twice has a chain
    /// of two. The limit only stops a loop, which no write here can make, from running forever.</summary>
    private const int AliasHopsAtMost = 8;

    /// <summary>
    /// Creates the alias table. Created on every open and not behind a version step, so an older build
    /// that shares the file keeps opening it (see Migrate). An older build does not read the table and
    /// does not write it.
    /// </summary>
    private void CreateIdAliasTable()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS rpm_id_alias (
                old_id TEXT PRIMARY KEY,
                new_id TEXT NOT NULL,
                at_utc_ticks INTEGER NOT NULL
            )
            """);
    }

    /// <summary>
    /// Gives every playlist entry that names an id no song has the id of the song at its path, and
    /// records the old id as an alias of the new one. Entries with no id at all get one the same way.
    ///
    /// <para>Run on every open. It is cheap: it looks only at playlist entries, and an entry it has
    /// repaired is not looked at again. A repaired playlist that is in the cloud is sent up once more
    /// on the next sync, so its cloud copy names today's ids too. A cloud copy that still holds an old
    /// id is also read through the alias when it comes back (<see cref="CurrentRpmId"/>).</para>
    /// </summary>
    /// <returns>How many entries were repaired.</returns>
    public int RepairPlaylistIds()
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using (var alias = _connection.CreateCommand())
            {
                alias.Transaction = tx;
                alias.CommandText =
                    """
                    INSERT OR REPLACE INTO rpm_id_alias (old_id, new_id, at_utc_ticks)
                    SELECT p.rpm_id, t.rpm_id, $now
                    FROM pl_item p
                    JOIN tracks t ON t.relative_path = p.rel_path
                    WHERE p.item_kind = 'song'
                      AND p.rpm_id IS NOT NULL AND p.rpm_id <> ''
                      AND t.rpm_id IS NOT NULL AND t.rpm_id <> p.rpm_id
                      AND NOT EXISTS (SELECT 1 FROM tracks o WHERE o.rpm_id = p.rpm_id)
                    """;
                alias.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                alias.ExecuteNonQuery();
            }

            // A repaired playlist that is in the cloud goes up once more, so its cloud copy names every song
            // by the id it has now, whichever ids that copy holds. The push writes only when what it would
            // send differs from what was last sent, and the ids it sends are already today's. So the record
            // of the name and folder last sent is cleared instead: the push then sees a change and writes.
            // The record of the ids last sent is kept, because a merge measures against it. A cleared name
            // record makes the pull take no rename (CloudPlaylistIntake.NameToTake), and the push writes it
            // again.
            using (var resend = _connection.CreateCommand())
            {
                resend.Transaction = tx;
                resend.CommandText =
                    """
                    UPDATE pl_node SET cloud_sent_meta = NULL
                    WHERE cloud_id IS NOT NULL
                      AND id IN (
                        SELECT p.playlist_id FROM pl_item p
                        WHERE p.item_kind = 'song'
                          AND p.rel_path IS NOT NULL
                          AND (p.rpm_id IS NULL OR p.rpm_id = ''
                               OR NOT EXISTS (SELECT 1 FROM tracks o WHERE o.rpm_id = p.rpm_id))
                          AND EXISTS (SELECT 1 FROM tracks t
                                      WHERE t.relative_path = p.rel_path AND t.rpm_id IS NOT NULL AND t.rpm_id <> ''))
                    """;
                resend.ExecuteNonQuery();
            }

            int repaired;
            using (var entries = _connection.CreateCommand())
            {
                entries.Transaction = tx;
                entries.CommandText =
                    """
                    UPDATE pl_item
                    SET rpm_id = (SELECT t.rpm_id FROM tracks t WHERE t.relative_path = pl_item.rel_path)
                    WHERE item_kind = 'song'
                      AND rel_path IS NOT NULL
                      AND (rpm_id IS NULL OR rpm_id = ''
                           OR NOT EXISTS (SELECT 1 FROM tracks o WHERE o.rpm_id = pl_item.rpm_id))
                      AND EXISTS (SELECT 1 FROM tracks t
                                  WHERE t.relative_path = pl_item.rel_path AND t.rpm_id IS NOT NULL AND t.rpm_id <> '')
                    """;
                repaired = entries.ExecuteNonQuery();
            }

            tx.Commit();
            return repaired;
        }
    }

    /// <summary>
    /// The id a song has now, for an id it may have had before. An id with no alias is its own answer,
    /// so this is safe to ask about any id.
    /// </summary>
    public string CurrentRpmId(string rpmId)
    {
        ArgumentNullException.ThrowIfNull(rpmId);

        using (_readGate.Take())
            return CurrentRpmIdLocked(rpmId);
    }

    /// <summary>
    /// <see cref="CurrentRpmId"/> for a whole list, in the same order. A list with no old ids in it
    /// comes back with the same ids.
    /// </summary>
    public IReadOnlyList<string> CurrentRpmIds(IReadOnlyList<string> rpmIds)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);

        using (_readGate.Take())
        {
            var now = new List<string>(rpmIds.Count);
            foreach (var id in rpmIds)
                now.Add(CurrentRpmIdLocked(id));
            return now;
        }
    }

    private string CurrentRpmIdLocked(string rpmId)
    {
        var id = rpmId;
        for (var hop = 0; hop < AliasHopsAtMost; hop++)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT new_id FROM rpm_id_alias WHERE old_id = $old";
            cmd.Parameters.AddWithValue("$old", id);
            if (cmd.ExecuteScalar() is not string next || next == id)
                break;
            id = next;
        }

        return id;
    }

    /// <summary>
    /// Records that a song once known as <paramref name="oldId"/> is now <paramref name="newId"/>, and
    /// moves the playlist entries that name the old id to the new one. Called inside the transaction of
    /// the write that changes the id.
    /// </summary>
    private void FollowIdChangeLocked(SqliteTransaction tx, string oldId, string newId)
    {
        using (var alias = _connection.CreateCommand())
        {
            alias.Transaction = tx;
            alias.CommandText =
                """
                INSERT OR REPLACE INTO rpm_id_alias (old_id, new_id, at_utc_ticks)
                VALUES ($old, $new, $now)
                """;
            alias.Parameters.AddWithValue("$old", oldId);
            alias.Parameters.AddWithValue("$new", newId);
            alias.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            alias.ExecuteNonQuery();
        }

        using var entries = _connection.CreateCommand();
        entries.Transaction = tx;
        entries.CommandText = "UPDATE pl_item SET rpm_id = $new WHERE rpm_id = $old";
        entries.Parameters.AddWithValue("$old", oldId);
        entries.Parameters.AddWithValue("$new", newId);
        entries.ExecuteNonQuery();
    }
}

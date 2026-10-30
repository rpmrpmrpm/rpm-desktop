using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// The import queue's album-level answers: what somebody said about a RECORD before its files reached
/// a folder. See <see cref="QueueAlbumIntent"/> for what is held and why it cannot be keyed by name.
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Merges <paramref name="update"/> into whatever the record already carries, and attaches the result
    /// to every row of the group. Returns the intent's id, or 0 when nothing is left to say.
    ///
    /// <para>An id already on the group is kept and updated. This is what makes the intent survive a
    /// second visit to the editor. Where two ids meet in one group, because two records were renamed
    /// into one, the newest wins. It is the one whose answers the user gave most recently.</para>
    /// </summary>
    public long QueueSetAlbumIntent(string artist, string? album, QueueAlbumIntent update)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            return SetIntentLocked(GroupRowIdsLocked(artist, album), update);
        }
    }

    /// <summary>
    /// The same, against rows named outright.
    ///
    /// <para>Use this from any caller that holds rows. A record's group keys are its ARTIST and ALBUM. The
    /// answers are collected in a window that can rename the record. For example, a release lookup fills
    /// in the album artist and the release's own title. By the time the answers come back, the group the
    /// user selected no longer exists under the old names. Stamping by name would then write the intent
    /// onto nothing. Stamping by row ids avoids this.</para>
    /// </summary>
    public long QueueSetAlbumIntentForRows(IReadOnlyList<long> rowIds, QueueAlbumIntent update)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            return SetIntentLocked([.. rowIds.Where(id => id != 0)], update);
        }
    }

    /// <summary>The shared body: merge onto what these rows already carry, store it, stamp them.</summary>
    private long SetIntentLocked(IReadOnlyList<long> rowIds, QueueAlbumIntent update)
    {
        var id = IntentIdOnRowsLocked(rowIds);
        var merged = (id == 0 ? new QueueAlbumIntent() : ReadIntentLocked(id) ?? new QueueAlbumIntent())
            .MergedWith(update);

        if (merged.IsEmpty)
        {
            if (id != 0)
                DeleteIntentLocked(id);
            StampRowsLocked(rowIds, 0);
            return 0;
        }

        var discHash = merged.RemoveDiscArt ? ArtRemovedHash : StoreArtLocked(merged.DiscArt);

        using var cmd = _connection.CreateCommand();
        if (id == 0)
        {
            cmd.CommandText =
                """
                INSERT INTO queue_album (classical_set, classical, normalize_set, normalize, disc_hash,
                                         release_provider, release_id, series_set, series, series_volume)
                VALUES ($cs, $c, $ns, $n, $disc, $provider, $release, $ss, $series, $volume)
                RETURNING id
                """;
        }
        else
        {
            cmd.CommandText =
                """
                UPDATE queue_album
                SET classical_set = $cs, classical = $c, normalize_set = $ns, normalize = $n,
                    disc_hash = $disc, release_provider = $provider, release_id = $release,
                    series_set = $ss, series = $series, series_volume = $volume
                WHERE id = $id
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$id", id);
        }
        cmd.Parameters.AddWithValue("$cs", merged.ClassicalSet ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", Tri(merged.Classical));
        cmd.Parameters.AddWithValue("$ns", merged.NormalizeSet ? 1 : 0);
        cmd.Parameters.AddWithValue("$n", Tri(merged.Normalize));
        cmd.Parameters.AddWithValue("$disc", (object?)discHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$provider", (object?)merged.ReleaseProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$release", (object?)merged.ReleaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ss", merged.SeriesSet ? 1 : 0);
        cmd.Parameters.AddWithValue("$series", (object?)merged.Series?.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$volume", (object?)merged.Series?.Volume ?? DBNull.Value);

        var newId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        StampRowsLocked(rowIds, newId);
        return newId;
    }

    /// <summary>What a record in the queue currently says about itself, or null when nobody has said
    /// anything. A null <paramref name="album"/> asks about a whole artist. That only answers when every
    /// record under it carries the same intent.</summary>
    public QueueAlbumIntent? QueueAlbumIntentFor(string artist, string? album)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            var id = GroupIntentIdLocked(artist, album);
            return id == 0 ? null : ReadIntentLocked(id);
        }
    }

    /// <summary>What these rows' record has been told about itself, or null when nobody has said
    /// anything. It is asked by row, so a record renamed since still answers.</summary>
    public QueueAlbumIntent? QueueAlbumIntentForRows(IReadOnlyList<long> rowIds)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            var id = IntentIdOnRowsLocked([.. rowIds.Where(i => i != 0)]);
            return id == 0 ? null : ReadIntentLocked(id);
        }
    }

    /// <summary>
    /// What these rows' record says about itself, when they are ALL rows of one record. Null if two of them
    /// carry different answers, or if any of them carries none.
    ///
    /// <para>This is the question a window asks before SHOWING one of these answers.
    /// <see cref="QueueAlbumIntentForRows"/> is the question a window asks before WRITING one. The two
    /// differ on a selection that spans records. Writing stamps every row named, which is what the caller
    /// asked for. Showing the newest of several records' answers over a mixed selection would show
    /// somebody else's record in this one's panel.</para>
    /// </summary>
    public QueueAlbumIntent? QueueAlbumIntentSharedBy(IReadOnlyList<long> rowIds)
    {
        ArgumentNullException.ThrowIfNull(rowIds);
        var ids = rowIds.Where(i => i != 0).ToList();
        if (ids.Count == 0)
            return null;

        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"SELECT MIN(album_intent_id), MAX(album_intent_id) FROM queue WHERE id IN ({Ids(ids)})";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
                return null;

            var lowest = reader.GetInt64(0);
            var highest = reader.GetInt64(1);
            return lowest == highest && highest != 0 ? ReadIntentLocked(highest) : null;
        }
    }

    /// <summary>Every series named against a record still in the queue. A box of ten volumes imported in
    /// one go is named on its first volume, and the other nine have to be offered that name before any of
    /// them has reached the library.</summary>
    public IReadOnlyList<string> QueueAlbumSeriesNames()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT series FROM queue_album
                WHERE series_set = 1 AND series IS NOT NULL AND TRIM(series) <> ''
                GROUP BY series COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
    }

    /// <summary>Every intent the queue holds, by id. An import needs this to apply them all in one pass,
    /// without a query per file.</summary>
    public IReadOnlyDictionary<long, QueueAlbumIntent> QueueAlbumIntents()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM queue_album";
            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<long, QueueAlbumIntent>();
            while (reader.Read())
            {
                var intent = ReadIntent(reader);
                map[intent.Id] = intent with { DiscArt = LoadArtLocked(Str(reader, "disc_hash")) };
            }
            return map;
        }
    }

    /// <summary>
    /// Hands a group's intent to rows that joined it later. Examples are the second disc of a set scanned
    /// after the record was already answered for, or a row re-queued by a cloud download. This is cheap
    /// enough to run before an import and after a scan. It does nothing when no group carries an intent.
    /// </summary>
    public void QueueAdoptAlbumIntents()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            Execute(
                """
                UPDATE queue SET album_intent_id = (
                    SELECT MAX(sibling.album_intent_id) FROM queue AS sibling
                    WHERE sibling.g_artist = queue.g_artist COLLATE NOCASE
                      AND sibling.g_album = queue.g_album COLLATE NOCASE
                      AND sibling.album_intent_id != 0)
                WHERE album_intent_id = 0
                  AND EXISTS (
                    SELECT 1 FROM queue AS sibling
                    WHERE sibling.g_artist = queue.g_artist COLLATE NOCASE
                      AND sibling.g_album = queue.g_album COLLATE NOCASE
                      AND sibling.album_intent_id != 0)
                """);
        }
    }

    /// <summary>Drops intents that no row belongs to any more. Every track of the record was imported or
    /// taken off the list, so the intent has nothing left to apply to.</summary>
    private void DropOrphanIntentsLocked()
        => Execute(
            """
            DELETE FROM queue_album
            WHERE id NOT IN (SELECT DISTINCT album_intent_id FROM queue WHERE album_intent_id != 0)
            """);

    /// <summary>The intent id on a group's rows: the newest where more than one has met, or 0 where none
    /// has.</summary>
    private long GroupIntentIdLocked(string artist, string? album)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT COALESCE(MAX(album_intent_id), 0) FROM queue WHERE {GroupWhere(cmd, artist, album)}";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Every row of one tree group, by id.</summary>
    private List<long> GroupRowIdsLocked(string artist, string? album)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT id FROM queue WHERE {GroupWhere(cmd, artist, album)}";
        using var reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>The intent on these rows: the newest where two have met, or 0 where none has.</summary>
    private long IntentIdOnRowsLocked(IReadOnlyList<long> rowIds)
    {
        if (rowIds.Count == 0)
            return 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT COALESCE(MAX(album_intent_id), 0) FROM queue WHERE id IN ({Ids(rowIds)})";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private void StampRowsLocked(IReadOnlyList<long> rowIds, long id)
    {
        if (rowIds.Count == 0)
            return;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"UPDATE queue SET album_intent_id = $id WHERE id IN ({Ids(rowIds)})";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Row ids as a literal list. They are the store's own integers, never user text.</summary>
    private static string Ids(IReadOnlyList<long> rowIds) => string.Join(",", rowIds);

    private QueueAlbumIntent? ReadIntentLocked(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM queue_album WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var intent = ReadIntent(reader);
        return intent with { DiscArt = LoadArtLocked(Str(reader, "disc_hash")) };
    }

    private void DeleteIntentLocked(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM queue_album WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The row without its picture. The caller loads the picture, because the bytes live in the
    /// shared art table, and the reader is still open on this table.</summary>
    private static QueueAlbumIntent ReadIntent(SqliteDataReader r) => new(
        Id: Int64(r, "id"),
        ClassicalSet: Int(r, "classical_set") != 0,
        Classical: TriBack(r, "classical"),
        NormalizeSet: Int(r, "normalize_set") != 0,
        Normalize: TriBack(r, "normalize"),
        RemoveDiscArt: Str(r, "disc_hash") == ArtRemovedHash,
        ReleaseProvider: Str(r, "release_provider"),
        ReleaseId: Str(r, "release_id"),
        SeriesSet: Int(r, "series_set") != 0,
        Series: AlbumSeries.Of(Str(r, "series"), Str(r, "series_volume")));

    /// <summary>A three-valued answer as SQLite holds it: NULL means leave it to the app, and 0 or 1 means
    /// a person has said so.</summary>
    private static object Tri(bool? value) => value is { } v ? v ? 1 : 0 : DBNull.Value;

    private static bool? TriBack(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt32(i) != 0;
    }
}

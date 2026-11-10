using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Migration;

namespace RPM.Core.Library;

/// <summary>
/// What an import from another music app (MusicBee, iTunes, Jellyfin, Plex, Navidrome, playlist
/// files) keeps in the library beside the tracks themselves: the date added it brings, and the record
/// of the import.
///
/// <para>The record is one row per migration (a source library), one row per track the source listed,
/// and one row per playlist imported. It is what lets an import of 200 GB continue after the app
/// closes, and a second import of the same library bring only what is new. Every read and write of it
/// goes through <see cref="MigrationRecord"/>, which <see cref="OpenMigration"/> hands out.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Moves the date a track joined the library earlier, never later. An import from another app
    /// knows when the track joined that library, which is the earlier and truer date. Zero is ignored.
    /// </summary>
    public void SetAddedEarlier(long trackId, long utcTicks)
    {
        if (utcTicks <= 0)
            return;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE tracks SET added_utc_ticks = $at WHERE id = $id AND (added_utc_ticks = 0 OR added_utc_ticks > $at)";
            cmd.Parameters.AddWithValue("$at", utcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Whether the upload workers have stopped trying this track: its upload failed and it has no attempts
    /// left, or the same refusal came twice and it waits for a date that is not a backoff (see
    /// <see cref="CloudRetrySchedule.AfterFailure"/>). The rule <see cref="QueryDueForUpload"/> and
    /// <see cref="NextCloudRetryTicks"/> use.
    /// </summary>
    public bool UploadGivenUp(long trackId, long nowUtcTicks)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT 1 FROM tracks
                WHERE id = $id AND cloud_state = {(int)CloudState.Error}
                  AND (cloud_attempts >= {CloudRetrySchedule.MaxAttempts} OR cloud_next_attempt_utc_ticks > $horizon)
                """;
            cmd.Parameters.AddWithValue("$id", trackId);
            // A date further out than a day is a never-again date, not a backoff.
            cmd.Parameters.AddWithValue("$horizon", nowUtcTicks + TimeSpan.FromDays(1).Ticks);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>One track by its row id, or null when there is no such row.</summary>
    public LibraryTrack? FindById(long id)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT * FROM tracks WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTrack(reader) : null;
        }
    }

    /// <summary>
    /// Opens the record of the import of <paramref name="library"/>, creating it the first time. The
    /// same source library (same kind and library id) always opens the same record, so a second import
    /// continues the first. Opening it again also marks it unfinished, and takes the new destination.
    /// </summary>
    public MigrationRecord OpenMigration(SourceLibrary library, MigrationDestination destination)
    {
        ArgumentNullException.ThrowIfNull(library);

        var kind = library.Kind.ToString().ToLowerInvariant();
        var key = LibraryKeyOf(library);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO migration (kind, library_id, name, destination, started_utc_ticks, finished_utc_ticks)
                VALUES ($kind, $key, $name, $dest, $now, 0)
                ON CONFLICT (kind, library_id) DO UPDATE SET
                    name = excluded.name, destination = excluded.destination, finished_utc_ticks = 0
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$name", library.Name);
            cmd.Parameters.AddWithValue("$dest", (int)destination);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            var id = Convert.ToInt64(cmd.ExecuteScalar());
            return new MigrationRecord(this, id, key, destination);
        }
    }

    /// <summary>
    /// The record of an unfinished import of <paramref name="library"/>, or null when there is none (never
    /// imported, or finished). Unlike <see cref="OpenMigration"/> it changes nothing, so the app can ask
    /// the person whether to continue it or start over before anything is written.
    /// </summary>
    public MigrationRecord? UnfinishedMigration(SourceLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var key = LibraryKeyOf(library);
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT id, destination FROM migration WHERE kind = $kind AND library_id = $key AND finished_utc_ticks = 0";
            cmd.Parameters.AddWithValue("$kind", library.Kind.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$key", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new MigrationRecord(this, reader.GetInt64(0), key, (MigrationDestination)reader.GetInt64(1)) : null;
        }
    }

    /// <summary>
    /// The names of the other source libraries that brought tracks into this library. Each source library
    /// counts its plays on a device of its own, so two that hold the same history (a MusicBee library built
    /// from an iTunes import) count those plays twice; the summary says so.
    /// </summary>
    public IReadOnlyList<string> OtherImports(SourceLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT m.name FROM migration m
                WHERE NOT (m.kind = $kind AND m.library_id = $key)
                  AND EXISTS (SELECT 1 FROM migration_track t WHERE t.migration_id = m.id AND t.state IN (2, 3))
                ORDER BY m.started_utc_ticks, m.id
                """;
            cmd.Parameters.AddWithValue("$kind", library.Kind.ToString().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$key", LibraryKeyOf(library));
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
    }

    /// <summary>
    /// Forgets an import: its record of tracks and playlists goes, so it is no longer offered and a later
    /// import of the same library starts fresh. What it brought into the library stays, and a fresh import
    /// finds those tracks as already in rpm.
    /// </summary>
    public void ForgetMigration(MigrationSummary import)
    {
        ArgumentNullException.ThrowIfNull(import);
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            foreach (var sql in new[]
            {
                "DELETE FROM migration_track WHERE migration_id IN (SELECT id FROM migration WHERE kind = $kind AND library_id = $key)",
                "DELETE FROM migration_playlist WHERE migration_id IN (SELECT id FROM migration WHERE kind = $kind AND library_id = $key)",
                "DELETE FROM migration WHERE kind = $kind AND library_id = $key",
            })
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$kind", import.Kind.ToString().ToLowerInvariant());
                cmd.Parameters.AddWithValue("$key", import.LibraryId);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>The imports that have not finished, newest first, so the app can offer to continue them.</summary>
    public IReadOnlyList<MigrationSummary> UnfinishedMigrations()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT kind, library_id, name, destination FROM migration WHERE finished_utc_ticks = 0 ORDER BY started_utc_ticks DESC, id DESC";
            using var reader = cmd.ExecuteReader();
            var open = new List<MigrationSummary>();
            while (reader.Read())
            {
                if (!Enum.TryParse<SourceKind>(reader.GetString(0), ignoreCase: true, out var kind))
                    continue;
                open.Add(new MigrationSummary(kind, reader.GetString(1), reader.GetString(2), (MigrationDestination)reader.GetInt64(3)));
            }
            return open;
        }
    }

    /// <summary>The key a library's record is filed under. Playlist files have no library id of their
    /// own, so the sorted paths of the playlists stand in: the same set of playlists is the same import.</summary>
    private static string LibraryKeyOf(SourceLibrary library)
    {
        if (library.LibraryId.Length > 0 || library.Kind != SourceKind.PlaylistFiles)
            return library.LibraryId;
        var paths = string.Join('\n', library.Playlists.Select(p => p.SourceId).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(paths)))[..16];
    }

    private void CreateMigrationTables()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS migration (
                id INTEGER PRIMARY KEY,
                kind TEXT NOT NULL,
                library_id TEXT NOT NULL,
                name TEXT NOT NULL,
                destination INTEGER NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                finished_utc_ticks INTEGER NOT NULL DEFAULT 0,
                UNIQUE (kind, library_id)
            )
            """);
        Execute(
            """
            CREATE TABLE IF NOT EXISTS migration_track (
                migration_id INTEGER NOT NULL,
                source_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                state INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_try_utc_ticks INTEGER NOT NULL DEFAULT 0,
                reason TEXT,
                detail TEXT,
                track_id INTEGER,
                PRIMARY KEY (migration_id, source_id)
            ) WITHOUT ROWID
            """);
        Execute("CREATE INDEX IF NOT EXISTS idx_migration_track_position ON migration_track(migration_id, position)");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS migration_playlist (
                migration_id INTEGER NOT NULL,
                source_id TEXT NOT NULL,
                playlist_id INTEGER NOT NULL,
                entries TEXT NOT NULL,
                PRIMARY KEY (migration_id, source_id)
            ) WITHOUT ROWID
            """);
    }

    /// <summary>
    /// The record of one import. Every state change of the import's tracks and playlists goes through
    /// here, under the library's own write lock, so the run, the upload workers and the person's
    /// actions cannot write past each other.
    /// </summary>
    public sealed class MigrationRecord
    {
        private readonly LibraryDatabase _db;

        internal MigrationRecord(LibraryDatabase db, long id, string libraryKey, MigrationDestination destination)
        {
            _db = db;
            Id = id;
            LibraryKey = libraryKey;
            Destination = destination;
        }

        public long Id { get; }

        /// <summary>The key the record is filed under with its kind: the library id, or for playlist files
        /// a hash of their paths. The same value <see cref="UnfinishedMigrations"/> reports.</summary>
        public string LibraryKey { get; }

        public MigrationDestination Destination { get; }

        /// <summary>Adds the source tracks the record does not have yet, after the ones it has, in the
        /// given order. Tracks it already has keep their state.</summary>
        public void AddFound(IEnumerable<string> sourceIds)
        {
            ArgumentNullException.ThrowIfNull(sourceIds);
            using (_db._gate.Take())
            {
                using var tx = _db._connection.BeginTransaction();
                long next;
                using (var max = _db._connection.CreateCommand())
                {
                    max.Transaction = tx;
                    max.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM migration_track WHERE migration_id = $m";
                    max.Parameters.AddWithValue("$m", Id);
                    next = Convert.ToInt64(max.ExecuteScalar());
                }

                using var cmd = _db._connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO migration_track (migration_id, source_id, position, state)
                    VALUES ($m, $s, $p, 0)
                    ON CONFLICT (migration_id, source_id) DO NOTHING
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                var source = cmd.Parameters.Add("$s", SqliteType.Text);
                var position = cmd.Parameters.Add("$p", SqliteType.Integer);
                foreach (var id in sourceIds)
                {
                    source.Value = id;
                    position.Value = next;
                    if (cmd.ExecuteNonQuery() > 0)
                        next++;
                }
                tx.Commit();
            }
        }

        /// <summary>The tracks to work on next, in source order: not handled yet, downloaded and waiting
        /// for the cloud, or not imported with attempts left and their wait over.</summary>
        public IReadOnlyList<MigrationTrackRow> Due(long nowUtcTicks, int limit) => Read(
            """
            WHERE migration_id = $m
              AND (state IN (0, 1) OR (state = 4 AND attempts < $max AND next_try_utc_ticks <= $now))
            ORDER BY position
            LIMIT $limit
            """,
            ("$now", nowUtcTicks), ("$limit", limit));

        /// <summary>One track's row, or null when the record does not list it.</summary>
        public MigrationTrackRow? Track(string sourceId) =>
            Read("WHERE migration_id = $m AND source_id = $s", ("$s", sourceId)).FirstOrDefault();

        /// <summary>Every row in one state, in source order.</summary>
        public IReadOnlyList<MigrationTrackRow> Rows(MigrationTrackState state) =>
            Read("WHERE migration_id = $m AND state = $state ORDER BY position", ("$state", (int)state));

        /// <summary>The library row of every source track that is in rpm (imported or matched), by source id.</summary>
        public IReadOnlyDictionary<string, long> TrackIds()
        {
            using (_db._readGate.Take())
            {
                using var cmd = _db._read.CreateCommand();
                cmd.CommandText = "SELECT source_id, track_id FROM migration_track WHERE migration_id = $m AND state IN (2, 3) AND track_id IS NOT NULL";
                cmd.Parameters.AddWithValue("$m", Id);
                using var reader = cmd.ExecuteReader();
                var ids = new Dictionary<string, long>(StringComparer.Ordinal);
                while (reader.Read())
                    ids[reader.GetString(0)] = reader.GetInt64(1);
                return ids;
            }
        }

        /// <summary>Sets one track's state. <see cref="MigrationTrackState.NotImported"/> written here is
        /// final (no attempts left); a failure to try again goes through <see cref="NoteFailure"/>.</summary>
        public void Note(string sourceId, MigrationTrackState state, long? trackId = null, string? reason = null, string? detail = null)
        {
            using (_db._gate.Take())
            {
                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE migration_track
                    SET state = $state, track_id = COALESCE($track, track_id), reason = $reason, detail = $detail,
                        attempts = CASE WHEN $state = 4 THEN $max ELSE attempts END
                    WHERE migration_id = $m AND source_id = $s
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$s", sourceId);
                cmd.Parameters.AddWithValue("$state", (int)state);
                cmd.Parameters.AddWithValue("$track", (object?)trackId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$max", CloudRetrySchedule.MaxAttempts);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>A failure to try again: one more attempt counted, and the next try waits by the same
        /// rule as a failed upload (<see cref="CloudRetrySchedule"/>). After the last attempt the track
        /// counts as not imported, with this reason.</summary>
        public void NoteFailure(string sourceId, string reason, long nowUtcTicks)
        {
            using (_db._gate.Take())
            {
                int attempts;
                using (var read = _db._connection.CreateCommand())
                {
                    read.CommandText = "SELECT attempts FROM migration_track WHERE migration_id = $m AND source_id = $s";
                    read.Parameters.AddWithValue("$m", Id);
                    read.Parameters.AddWithValue("$s", sourceId);
                    attempts = Convert.ToInt32(read.ExecuteScalar() ?? 0) + 1;
                }

                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE migration_track
                    SET state = 4, attempts = $attempts, next_try_utc_ticks = $next, reason = $reason
                    WHERE migration_id = $m AND source_id = $s
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$s", sourceId);
                cmd.Parameters.AddWithValue("$attempts", attempts);
                cmd.Parameters.AddWithValue("$next", nowUtcTicks + CloudRetrySchedule.Backoff(attempts - 1).Ticks);
                cmd.Parameters.AddWithValue("$reason", reason);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// At the start of each run, the tracks that were not imported go back to waiting, with no
        /// attempts counted, so a run after the server was asleep, the disk was full or a file was missing
        /// tries them again. A track the person removed from rpm during the import stays removed.
        /// </summary>
        public void ReopenFailed()
        {
            using (_db._gate.Take())
            {
                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE migration_track SET state = 0, attempts = 0, next_try_utc_ticks = 0, reason = NULL, detail = NULL
                    WHERE migration_id = $m AND state = 4 AND COALESCE(reason, '') <> $removed
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$removed", MigrationReason.Removed);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// "Start over": every track is checked again, as on a first run. The tracks that are in rpm
        /// already are found by the matching and not copied twice. The links to the playlists stay, so
        /// they are filled in and not made a second time. Downloaded tracks that wait for the cloud keep
        /// their state, because only the record knows to delete their files once uploaded.
        /// </summary>
        public void StartOver()
        {
            using (_db._gate.Take())
            {
                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE migration_track SET state = 0, attempts = 0, next_try_utc_ticks = 0, reason = NULL, detail = NULL, track_id = NULL
                    WHERE migration_id = $m AND state <> $downloaded
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$downloaded", (int)MigrationTrackState.Downloaded);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>"Try again" from the report: the tracks start over with no attempts counted.</summary>
        public void Retry(IEnumerable<string> sourceIds)
        {
            ArgumentNullException.ThrowIfNull(sourceIds);
            using (_db._gate.Take())
            {
                using var tx = _db._connection.BeginTransaction();
                using var cmd = _db._connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    UPDATE migration_track SET state = 0, attempts = 0, next_try_utc_ticks = 0, reason = NULL, detail = NULL
                    WHERE migration_id = $m AND source_id = $s AND state = 4
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                var source = cmd.Parameters.Add("$s", SqliteType.Text);
                foreach (var id in sourceIds)
                {
                    source.Value = id;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        public MigrationCounts Counts()
        {
            using (_db._readGate.Take())
            {
                using var cmd = _db._read.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT COUNT(*),
                           COALESCE(SUM(state = 2), 0),
                           COALESCE(SUM(state = 3), 0),
                           COALESCE(SUM(state = 4 AND attempts >= $max), 0)
                    FROM migration_track WHERE migration_id = $m
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$max", CloudRetrySchedule.MaxAttempts);
                using var reader = cmd.ExecuteReader();
                reader.Read();
                var found = reader.GetInt32(0);
                var imported = reader.GetInt32(1);
                var already = reader.GetInt32(2);
                var notImported = reader.GetInt32(3);
                return new MigrationCounts(found, imported, already, notImported, found - imported - already - notImported);
            }
        }

        /// <summary>The rpm playlist a source playlist became, and the source entries that were imported
        /// into it; null when the playlist was not imported yet.</summary>
        public (long PlaylistId, IReadOnlyList<string> Entries)? Playlist(string sourceId)
        {
            using (_db._readGate.Take())
            {
                using var cmd = _db._read.CreateCommand();
                cmd.CommandText = "SELECT playlist_id, entries FROM migration_playlist WHERE migration_id = $m AND source_id = $s";
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$s", sourceId);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;
                var entries = JsonSerializer.Deserialize(reader.GetString(1), MigrationJson.Default.StringArray) ?? [];
                return (reader.GetInt64(0), entries);
            }
        }

        public void NotePlaylist(string sourceId, long playlistId, IReadOnlyList<string> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            using (_db._gate.Take())
            {
                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO migration_playlist (migration_id, source_id, playlist_id, entries) VALUES ($m, $s, $p, $e)
                    ON CONFLICT (migration_id, source_id) DO UPDATE SET playlist_id = excluded.playlist_id, entries = excluded.entries
                    """;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$s", sourceId);
                cmd.Parameters.AddWithValue("$p", playlistId);
                cmd.Parameters.AddWithValue("$e", JsonSerializer.Serialize([.. entries], MigrationJson.Default.StringArray));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Marks the import finished, so the app stops offering to continue it.</summary>
        public void Finish(long nowUtcTicks)
        {
            using (_db._gate.Take())
            {
                using var cmd = _db._connection.CreateCommand();
                cmd.CommandText = "UPDATE migration SET finished_utc_ticks = $now WHERE id = $m";
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$now", Math.Max(1, nowUtcTicks));
                cmd.ExecuteNonQuery();
            }
        }

        private IReadOnlyList<MigrationTrackRow> Read(string where, params (string Name, object Value)[] parameters)
        {
            using (_db._readGate.Take())
            {
                using var cmd = _db._read.CreateCommand();
                cmd.CommandText =
                    "SELECT source_id, state, attempts, next_try_utc_ticks, reason, detail, track_id FROM migration_track " + where;
                cmd.Parameters.AddWithValue("$m", Id);
                cmd.Parameters.AddWithValue("$max", CloudRetrySchedule.MaxAttempts);
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value);
                using var reader = cmd.ExecuteReader();
                var rows = new List<MigrationTrackRow>();
                while (reader.Read())
                    rows.Add(new MigrationTrackRow(
                        reader.GetString(0),
                        (MigrationTrackState)reader.GetInt64(1),
                        reader.GetInt32(2),
                        reader.GetInt64(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6)));
                return rows;
            }
        }
    }
}

/// <summary>The JSON shapes the record stores, written at build time (the published app is Native AOT).</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class MigrationJson : System.Text.Json.Serialization.JsonSerializerContext;

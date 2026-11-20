using RPM.Core.Analysis;
using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>A track whose sound has not been measured yet: where it is, what to stamp the reading
/// against, and how long it is, which says where its clips are (see <see cref="SoundClips"/>).</summary>
public sealed record SoundWork(string RelativePath, long SizeBytes, long ModifiedUtcTicks, double DurationSeconds);

/// <summary>
/// Storage for each track's CLAP embedding (see <see cref="SoundEmbedding"/>).
///
/// <para><b>A table of its own, not columns on <c>tracks</c>.</b> An embedding is a kilobyte. On the track
/// row it would make every read of <c>tracks</c> touch several times more pages, and the library reads that
/// table all the time. It would also travel with the row into the cloud catalogue and to the phone, which
/// do not need it.</para>
///
/// <para><b>Stamped against the file</b>, like the loudness: the size and modified time it was taken from,
/// and the generation of the measurement (<see cref="SoundFact.Generation"/>). A row whose stamp no longer
/// matches the track is not read and is measured again. A NULL embedding means "measured, and nothing
/// usable came out", so a file that cannot be decoded is not decoded again on every pass.</para>
///
/// <para>Created on every open and not behind a version step, for the same reason as
/// <c>tracks.release_type</c> (see <see cref="LibraryDatabase.Migrate"/>): an older build that shares the
/// file does not know the table and does not need to.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    private void CreateSoundTable()
    {
        Execute(
            """
            CREATE TABLE IF NOT EXISTS track_sound (
                relative_path TEXT PRIMARY KEY COLLATE NOCASE,
                size_bytes INTEGER NOT NULL,
                modified_utc_ticks INTEGER NOT NULL,
                generation INTEGER NOT NULL,
                embedding BLOB
            )
            """);
    }

    /// <summary>
    /// The next tracks with no current sound reading, oldest additions first.
    ///
    /// <para>The same tracks the mood half of the loudness pass takes (see
    /// <see cref="QueryTracksNeedingLoudness"/>): bytes on this disk, not lent by another computer, and
    /// long enough to be a song (<see cref="Moods.LeastSeconds"/>).</para>
    /// </summary>
    public IReadOnlyList<SoundWork> QueryTracksNeedingSound(int limit)
    {
        using (ReadLock.Take())
        {
            using var cmd = Reader.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT t.relative_path, t.size_bytes, t.modified_utc_ticks, t.duration_seconds
                FROM tracks t
                WHERE t.duration_seconds >= {Moods.LeastSeconds}
                  AND t.cloud_state <> {(int)CloudState.CloudOnly}
                  AND t.peer_adopted = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM track_sound s
                      WHERE s.relative_path = t.relative_path
                        AND s.size_bytes = t.size_bytes
                        AND s.modified_utc_ticks = t.modified_utc_ticks
                        AND s.generation = $generation)
                ORDER BY t.added_utc_ticks
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$generation", SoundFact.Generation);
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var work = new List<SoundWork>();
            while (reader.Read())
                work.Add(new SoundWork(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDouble(3)));
            return work;
        }
    }

    /// <summary>How many tracks <see cref="QueryTracksNeedingSound"/> would still hand out.</summary>
    public int CountTracksNeedingSound()
    {
        using (ReadLock.Take())
        {
            using var cmd = Reader.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COUNT(*) FROM tracks t
                WHERE t.duration_seconds >= {Moods.LeastSeconds}
                  AND t.cloud_state <> {(int)CloudState.CloudOnly}
                  AND t.peer_adopted = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM track_sound s
                      WHERE s.relative_path = t.relative_path
                        AND s.size_bytes = t.size_bytes
                        AND s.modified_utc_ticks = t.modified_utc_ticks
                        AND s.generation = $generation)
                """;
            cmd.Parameters.AddWithValue("$generation", SoundFact.Generation);
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    /// <summary>
    /// Records a track's embedding, or that none could be made.
    ///
    /// <para>Written only while the track row still has the size and modified time the reading was taken
    /// from. The guard is in the statement, not in the caller: the file may have been replaced while the
    /// clips were decoded.</para>
    /// </summary>
    /// <param name="embedding">The embedding, or null when the file gave nothing usable. Null is stored as
    /// an answer, so the pass does not try the file again until it changes.</param>
    public void SaveTrackSound(string relativePath, long sizeBytes, long modifiedUtcTicks, float[]? embedding)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO track_sound
                    (relative_path, size_bytes, modified_utc_ticks, generation, embedding)
                SELECT $path, $size, $mtime, $generation, $embedding
                WHERE EXISTS (
                    SELECT 1 FROM tracks
                    WHERE relative_path = $path AND size_bytes = $size AND modified_utc_ticks = $mtime)
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$mtime", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$generation", SoundFact.Generation);
            cmd.Parameters.AddWithValue("$embedding",
                embedding is { Length: SoundEmbedding.Dimensions } ? SoundEmbedding.Pack(embedding) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every current embedding, by track path: the rows whose stamp still matches the track and whose
    /// generation is this build's. Tracks with no embedding are left out.
    ///
    /// <para>The whole library at once, as <see cref="QueryMeasuredMoods"/> reads it, because a feeling list
    /// ranks every track against every other. A kilobyte per track is about 20 MB for twenty thousand
    /// tracks.</para>
    /// </summary>
    public IReadOnlyDictionary<string, float[]> QuerySoundEmbeddings()
        => ReadSounds(embedding => embedding);

    /// <summary>
    /// Every heard track's score for each feeling (<see cref="SoundPrompts.ScoreAll"/>), by relative path,
    /// under the same rules as <see cref="QuerySoundEmbeddings"/>.
    ///
    /// <para>For the session's mood cut. The scores are worked out row by row as they are read, so the
    /// embeddings themselves (about 40 MB as numbers for twenty thousand tracks) are never all held at
    /// once. A score is six numbers.</para>
    /// </summary>
    public IReadOnlyDictionary<string, FeelingScores> QuerySoundFeelings()
        => ReadSounds(embedding => SoundPrompts.ScoreAll(embedding));

    /// <summary>The current rows, each turned into what the caller needs. The packed rows are read under
    /// the read gate and turned after it is let go: scoring twenty thousand rows takes a moment, and
    /// every other read in the app would wait out that moment behind the gate.</summary>
    private Dictionary<string, T> ReadSounds<T>(Func<float[], T> read)
    {
        var byPath = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, packed) in ReadPackedSounds())
            if (SoundEmbedding.Unpack(packed) is { } embedding)
                byPath[path] = read(embedding);
        return byPath;
    }

    private List<(string Path, byte[] Packed)> ReadPackedSounds()
    {
        using (ReadLock.Take())
        {
            using var cmd = Reader.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.relative_path, s.embedding
                FROM track_sound s
                JOIN tracks t ON t.relative_path = s.relative_path
                WHERE s.size_bytes = t.size_bytes
                  AND s.modified_utc_ticks = t.modified_utc_ticks
                  AND s.generation = $generation
                  AND s.embedding IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$generation", SoundFact.Generation);

            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, byte[])>();
            while (reader.Read())
                rows.Add((reader.GetString(0), (byte[])reader.GetValue(1)));
            return rows;
        }
    }

    /// <summary>Removes the readings of tracks that are no longer in the library. A moved or deleted file
    /// leaves its row behind, because nothing else in the library knows this table.</summary>
    /// <returns>How many rows were removed.</returns>
    public int ForgetSoundOfMissingTracks()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "DELETE FROM track_sound WHERE relative_path NOT IN (SELECT relative_path FROM tracks)";
            return cmd.ExecuteNonQuery();
        }
    }
}

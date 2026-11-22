using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>
/// A track that is only in the cloud and still lacks one or more of its readings. The store measures it
/// from its own copy (see <see cref="StoredReading"/>), asked for by the track's rpm id. The size and
/// modified time are what each answer is stamped against. The time it was added and its path place it in
/// the list, so that the next page can start after it.
/// </summary>
public sealed record CloudOnlyWork(
    string RelativePath, long SizeBytes, long ModifiedUtcTicks, long AddedUtcTicks, string RpmId,
    bool NeedsLevel, bool NeedsMood, bool NeedsSound, bool NeedsFingerprint);

/// <summary>
/// The tracks that no computer can measure, because their only copy is in the cloud.
///
/// <para>Every pass that measures a track (loudness and mood, sound, fingerprint) decodes the file on this
/// disk, so each of them leaves out a <see cref="CloudState.CloudOnly"/> row. This is the list of what those
/// passes would have done for such rows, read with the same rules, for the store to do instead.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The tracks only in the cloud that still lack a reading, oldest additions first, with which readings
    /// each one lacks.
    ///
    /// <para>Each reading is wanted under the rule of the pass that measures it on this disk: a level for
    /// every track (<see cref="QueryTracksNeedingLoudness"/>); the arousal numbers and the sound only for a
    /// track long enough to be a song (<see cref="Moods.LeastSeconds"/>, <see cref="QueryTracksNeedingSound"/>);
    /// a fingerprint for every track whose row is missing, stale or from an older recipe
    /// (<see cref="QueryTracksNeedingFingerprint"/>), when fingerprinting is on. A row lent by another computer
    /// is left out, as those passes leave it out, and so is a row without an rpm id, because the store is
    /// asked by that id.</para>
    ///
    /// <para><b>A page starts after the last row of the page before</b>, not at the top of the list. A caller
    /// that skips some rows (a track the store holds no copy of) would otherwise be handed the same skipped
    /// rows at the top of every page, and a full page of them would end its walk with the rest of the list
    /// never reached.</para>
    /// </summary>
    /// <param name="after">The last row of the previous page, or null for the first page.</param>
    /// <param name="fingerprints">Whether fingerprinting is on. The setting decides it for the files on this
    /// disk, and it decides it here too.</param>
    public IReadOnlyList<CloudOnlyWork> QueryCloudOnlyTracksToMeasure(CloudOnlyWork? after, bool fingerprints, int limit)
    {
        using (ReadLock.Take())
        {
            using var cmd = Reader.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT t.relative_path, t.size_bytes, t.modified_utc_ticks, t.added_utc_ticks, t.rpm_id,
                       {NeedsLevel}, {NeedsMood}, {NeedsSound}, {NeedsFingerprint}
                FROM tracks t
                WHERE {CloudOnlyToMeasure}
                  AND (t.added_utc_ticks > $afterTicks
                       OR (t.added_utc_ticks = $afterTicks AND t.relative_path > $afterPath))
                ORDER BY t.added_utc_ticks, t.relative_path
                LIMIT $limit
                """;
            BindCloudOnly(cmd, fingerprints);
            cmd.Parameters.AddWithValue("$afterTicks", after?.AddedUtcTicks ?? long.MinValue);
            cmd.Parameters.AddWithValue("$afterPath", after?.RelativePath ?? "");
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var work = new List<CloudOnlyWork>();
            while (reader.Read())
                work.Add(new CloudOnlyWork(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                    reader.GetInt64(5) != 0, reader.GetInt64(6) != 0, reader.GetInt64(7) != 0, reader.GetInt64(8) != 0));
            return work;
        }
    }

    /// <summary>How many tracks <see cref="QueryCloudOnlyTracksToMeasure"/> would still hand out.</summary>
    public int CountCloudOnlyTracksToMeasure(bool fingerprints)
    {
        using (ReadLock.Take())
        {
            using var cmd = Reader.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM tracks t WHERE {CloudOnlyToMeasure}";
            BindCloudOnly(cmd, fingerprints);
            return (int)(long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    private static void BindCloudOnly(Microsoft.Data.Sqlite.SqliteCommand cmd, bool fingerprints)
    {
        cmd.Parameters.AddWithValue("$generation", SoundFact.Generation);
        cmd.Parameters.AddWithValue("$fpVersion", FingerprintVersion);
        cmd.Parameters.AddWithValue("$fingerprints", fingerprints ? 1 : 0);
    }

    private const string NeedsLevel = "t.loudness_lufs IS NULL";

    private static readonly string NeedsMood =
        $"(t.mood_brightness IS NULL AND t.duration_seconds >= {Moods.LeastSeconds})";

    private static readonly string NeedsSound =
        $"""
        (t.duration_seconds >= {Moods.LeastSeconds} AND NOT EXISTS (
            SELECT 1 FROM track_sound s
            WHERE s.relative_path = t.relative_path
              AND s.size_bytes = t.size_bytes
              AND s.modified_utc_ticks = t.modified_utc_ticks
              AND s.generation = $generation))
        """;

    private const string NeedsFingerprint =
        """
        ($fingerprints = 1 AND NOT EXISTS (
            SELECT 1 FROM track_fingerprint f
            WHERE f.relative_path = t.relative_path
              AND f.size_bytes = t.size_bytes
              AND f.modified_utc_ticks = t.modified_utc_ticks
              AND f.fp_version >= $fpVersion))
        """;

    /// <summary>The rows of <see cref="QueryCloudOnlyTracksToMeasure"/>, in one place so that the count and
    /// the query cannot disagree.</summary>
    private static readonly string CloudOnlyToMeasure =
        $"""
        t.cloud_state = {(int)CloudState.CloudOnly}
          AND t.peer_adopted = 0
          AND t.rpm_id IS NOT NULL AND t.rpm_id <> ''
          AND ({NeedsLevel} OR {NeedsMood} OR {NeedsSound} OR {NeedsFingerprint})
        """;
}

using System.Globalization;
using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>A track still waiting for a fingerprint: what the worker needs to run fpcalc and
/// stamp the row, nothing more.</summary>
public sealed record FingerprintWork(
    string RelativePath,
    long SizeBytes,
    long ModifiedUtcTicks);

/// <summary>A stored fingerprint ready to be offered to the recognition service, joined with the
/// track's own metadata. The report says "this fingerprint is this song", so both halves are sent
/// together.</summary>
public sealed record FingerprintReport(
    string RelativePath,
    string Title,
    string Artist,
    string? Album,
    uint Year,
    double DurationSeconds,
    byte[] Fingerprint);

/// <summary>
/// Storage for each track's Chromaprint fingerprint. Fingerprinting needs a full decode, so it is
/// done once and kept. The row is stamped with the file's size and mtime, as <c>track_analysis</c>
/// is, so a re-tagged or re-encoded file no longer matches its row and is fingerprinted again.
///
/// <para>A row with an EMPTY fingerprint is a finding too: "fpcalc could not read this file".
/// Remembering it stops a corrupt file being retried on every pass. The stamp ages it out as soon
/// as the file changes.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The fingerprinting recipe's version. Bump it and every stored row reads as stale,
    /// which is how a changed recipe reaches tracks already done.</summary>
    public const int FingerprintVersion = 1;

    /// <summary>Stores (or replaces) one track's fingerprint. An empty blob records a file fpcalc
    /// could not read. A new fingerprint resets the submitted stamp, because what was sent before
    /// was a different fingerprint.</summary>
    public void SaveTrackFingerprint(string relativePath, long sizeBytes, long modifiedUtcTicks,
        double durationSeconds, byte[] fingerprint)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(fingerprint);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO track_fingerprint (
                    relative_path, size_bytes, modified_utc_ticks, fp_version, duration_seconds,
                    fingerprint, fingerprinted_utc_ticks, submitted_utc_ticks)
                VALUES ($path, $size, $modified, $version, $duration, $fp, $at, 0)
                ON CONFLICT(relative_path) DO UPDATE SET
                    size_bytes = excluded.size_bytes,
                    modified_utc_ticks = excluded.modified_utc_ticks,
                    fp_version = excluded.fp_version,
                    duration_seconds = excluded.duration_seconds,
                    fingerprint = excluded.fingerprint,
                    fingerprinted_utc_ticks = excluded.fingerprinted_utc_ticks,
                    submitted_utc_ticks = 0
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$modified", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$version", FingerprintVersion);
            cmd.Parameters.AddWithValue("$duration", durationSeconds);
            cmd.Parameters.AddWithValue("$fp", fingerprint);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The stored fingerprint blob for a track, or null when there is none, the file has
    /// changed since it was made, or it was made by an older recipe. Empty means "tried, unreadable".</summary>
    public byte[]? GetTrackFingerprint(string relativePath, long sizeBytes, long modifiedUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT size_bytes, modified_utc_ticks, fp_version, fingerprint
                FROM track_fingerprint WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            // Size AND timestamp, because a re-tag can leave one of the two unchanged.
            if (reader.GetInt64(0) != sizeBytes || reader.GetInt64(1) != modifiedUtcTicks)
                return null;
            if (reader.GetInt32(2) < FingerprintVersion)
                return null;

            return reader.IsDBNull(3) ? [] : (byte[])reader.GetValue(3);
        }
    }

    /// <summary>
    /// Tracks that still need a fingerprint: no row, a row for an older file, or a row from an
    /// older recipe. Offloaded tracks are skipped, because their bytes are not on this machine.
    /// </summary>
    public IReadOnlyList<FingerprintWork> QueryTracksNeedingFingerprint(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.relative_path, t.size_bytes, t.modified_utc_ticks
                FROM tracks t
                LEFT JOIN track_fingerprint f ON f.relative_path = t.relative_path
                WHERE t.cloud_state <> $cloudOnly
                  AND (f.relative_path IS NULL
                       OR f.size_bytes <> t.size_bytes
                       OR f.modified_utc_ticks <> t.modified_utc_ticks
                       OR f.fp_version < $version)
                ORDER BY t.id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$cloudOnly", (int)CloudState.CloudOnly);
            cmd.Parameters.AddWithValue("$version", FingerprintVersion);
            cmd.Parameters.AddWithValue("$limit", limit);

            var work = new List<FingerprintWork>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                work.Add(new FingerprintWork(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            return work;
        }
    }

    /// <summary>
    /// Fingerprints made but not yet offered to the recognition service. Only rows that still
    /// describe their file, have an actual fingerprint, and have a title and artist to report are
    /// returned. A fingerprint of an untitled file claims nothing that anyone can confirm.
    /// </summary>
    public IReadOnlyList<FingerprintReport> QueryFingerprintsNeedingSubmission(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT f.relative_path, t.title, t.artist, t.album, t.year, f.duration_seconds, f.fingerprint
                FROM track_fingerprint f
                JOIN tracks t ON t.relative_path = f.relative_path
                WHERE f.submitted_utc_ticks = 0
                  AND f.fp_version >= $version
                  AND f.size_bytes = t.size_bytes
                  AND f.modified_utc_ticks = t.modified_utc_ticks
                  AND f.fingerprint IS NOT NULL AND length(f.fingerprint) > 0
                  AND t.title IS NOT NULL AND t.title <> ''
                  AND t.artist IS NOT NULL AND t.artist <> ''
                ORDER BY f.fingerprinted_utc_ticks
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$version", FingerprintVersion);
            cmd.Parameters.AddWithValue("$limit", limit);

            var reports = new List<FingerprintReport>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                reports.Add(new FingerprintReport(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    (uint)reader.GetInt64(4),
                    reader.GetDouble(5),
                    (byte[])reader.GetValue(6)));
            return reports;
        }
    }

    /// <summary>
    /// One track's stored fingerprint and the metadata that goes with it, whether or not it has been
    /// offered before. It is for the record that a set is waiting on, where the background pass's own
    /// order is the wrong order.
    /// </summary>
    /// <returns>Null when there is no current fingerprint for it, or when the track has no name to
    /// report it under.</returns>
    public FingerprintReport? FingerprintReportFor(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT f.relative_path, t.title, t.artist, t.album, t.year, f.duration_seconds, f.fingerprint
                FROM track_fingerprint f
                JOIN tracks t ON t.relative_path = f.relative_path
                WHERE f.relative_path = $path
                  AND f.fp_version >= $version
                  AND f.size_bytes = t.size_bytes
                  AND f.modified_utc_ticks = t.modified_utc_ticks
                  AND f.fingerprint IS NOT NULL AND length(f.fingerprint) > 0
                  AND t.title IS NOT NULL AND t.title <> ''
                  AND t.artist IS NOT NULL AND t.artist <> ''
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$version", FingerprintVersion);

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            return new FingerprintReport(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (uint)reader.GetInt64(4),
                reader.GetDouble(5),
                (byte[])reader.GetValue(6));
        }
    }

    /// <summary>Stamps a fingerprint as offered to the recognition service.</summary>
    /// <param name="songId">What the service calls this recording, when it said. It is kept because it
    /// is the one name that two independent rips of a CD can share (see
    /// <see cref="RPM.Fingerprint.RecordingKey.Song"/>). Zero means "it did not say" and never
    /// overwrites an id already on the row.</param>
    public void MarkFingerprintSubmitted(string relativePath, long utcTicks, long songId = 0)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE track_fingerprint
                SET submitted_utc_ticks = $at,
                    song_id = CASE WHEN $song > 0 THEN $song ELSE song_id END
                WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$at", utcTicks);
            cmd.Parameters.AddWithValue("$song", songId);
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many tracks carry a current fingerprint, for a progress readout.</summary>
    public int CountTrackFingerprints()
    {
        using (_gate.Take())
            return Convert.ToInt32(Scalar(
                $"SELECT COUNT(*) FROM track_fingerprint WHERE fp_version >= {FingerprintVersion}"),
                CultureInfo.InvariantCulture);
    }

    /// <summary>How many tracks could be fingerprinted at all (their bytes are on this machine),
    /// for the denominator of the same readout.</summary>
    public int CountFingerprintableTracks()
    {
        using (_gate.Take())
            return Convert.ToInt32(Scalar(
                $"SELECT COUNT(*) FROM tracks WHERE cloud_state <> {(int)CloudState.CloudOnly}"),
                CultureInfo.InvariantCulture);
    }
}

using RPM.Core.Cloud;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// The cloud's activity log: one row per finished upload, removal or restore.
///
/// <para>It lives in the mirror and not in memory, because the question it answers is asked after
/// the app has been closed and opened again ("did that album go up last night?"). It is pruned on
/// every write, so the table cannot grow larger than the answer it exists to give. See
/// <see cref="CloudActivityRetention"/>.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>Records a finished operation and prunes anything past the retention window.</summary>
    /// <param name="detail">The sentence the user is shown, or null for a success.</param>
    /// <param name="fullError">Everything known about a failure (<see cref="CloudFailure.InFull(Exception)"/>),
    /// sent with a report from the Errors card. Null when there was no exception to keep, which is a
    /// success or a failure decided here and not thrown.</param>
    public void AddCloudActivity(
        CloudOp op, string title, string? subtitle, CloudOutcome outcome, string? detail,
        string? fullError, long bytes, long atUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO cloud_activity (op, title, subtitle, outcome, detail, full_error, bytes, at_utc_ticks)
                VALUES ($op, $title, $subtitle, $outcome, $detail, $full, $bytes, $at)
                """;
            cmd.Parameters.AddWithValue("$op", (int)op);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$subtitle", (object?)subtitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$outcome", (int)outcome);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$full", (object?)fullError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bytes", bytes);
            cmd.Parameters.AddWithValue("$at", atUtcTicks);
            cmd.ExecuteNonQuery();

            PruneCloudActivityLocked(CloudActivityRetention.CutoffTicks(atUtcTicks));
        }
    }

    /// <summary>The log, newest first.</summary>
    public IReadOnlyList<CloudActivityEntry> QueryCloudActivity(int limit = CloudActivityRetention.MaxRows)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT id, op, title, subtitle, outcome, detail, bytes, at_utc_ticks, full_error " +
                "FROM cloud_activity ORDER BY at_utc_ticks DESC, id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var rows = new List<CloudActivityEntry>();
            while (reader.Read())
                rows.Add(new CloudActivityEntry(
                    Id: reader.GetInt64(0),
                    Op: (CloudOp)reader.GetInt64(1),
                    Title: reader.GetString(2),
                    Subtitle: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Outcome: (CloudOutcome)reader.GetInt64(4),
                    Detail: reader.IsDBNull(5) ? null : reader.GetString(5),
                    FullError: reader.IsDBNull(8) ? null : reader.GetString(8),
                    Bytes: reader.GetInt64(6),
                    AtUtcTicks: reader.GetInt64(7)));
            return rows;
        }
    }

    /// <summary>Empties the log. This is the user's own "forget all this".</summary>
    public void ClearCloudActivity()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM cloud_activity";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops everything older than the cutoff, then everything past the row cap. Both are
    /// needed: a month of a quiet library is a handful of rows, and one afternoon of a big first
    /// sync is a hundred thousand.</summary>
    private void PruneCloudActivityLocked(long cutoffTicks)
    {
        using var byAge = _connection.CreateCommand();
        byAge.CommandText = "DELETE FROM cloud_activity WHERE at_utc_ticks < $cutoff";
        byAge.Parameters.AddWithValue("$cutoff", cutoffTicks);
        byAge.ExecuteNonQuery();

        using var byCount = _connection.CreateCommand();
        byCount.CommandText =
            """
            DELETE FROM cloud_activity WHERE id NOT IN (
                SELECT id FROM cloud_activity ORDER BY at_utc_ticks DESC, id DESC LIMIT $keep
            )
            """;
        byCount.Parameters.AddWithValue("$keep", CloudActivityRetention.MaxRows);
        byCount.ExecuteNonQuery();
    }
}

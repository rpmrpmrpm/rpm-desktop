using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>
/// The song's star rating: 0 to 10 (half stars), 0 meaning not rated. It belongs to the row, never to
/// the file, like a favourite.
///
/// <para>It travels to the cloud on a pass of its own and not with the catalogue row. The catalogue
/// pass compares the row with the store and pushes what differs; a rating that cannot be sent yet (a
/// server without the ratings route) would then differ on every pass and be pushed for ever.
/// <c>rating_sent</c> says whether the cloud has the value the row holds now.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Sets the rating an import from another app brought, when the row has none. A rating the row
    /// already has is the listener's and is kept. The new value is marked unsent.
    /// </summary>
    /// <returns>True when the rating was written.</returns>
    public bool SetImportedRating(long trackId, int rating)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rating, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rating, 10);
        if (rating == 0)
            return false;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET rating = $rating, rating_sent = 0 WHERE id = $id AND rating = 0";
            cmd.Parameters.AddWithValue("$rating", rating);
            cmd.Parameters.AddWithValue("$id", trackId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Ratings the cloud does not have yet, on tracks the cloud holds (synced, or only in the
    /// cloud). A track that is not uploaded yet keeps its rating unsent until it is.</summary>
    public IReadOnlyList<(long Id, string RpmId, int Rating)> RatingsToSend(int limit)
    {
        if (limit <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, rpm_id, rating FROM tracks
                WHERE rating_sent = 0 AND rpm_id IS NOT NULL AND cloud_state IN ($synced, $cloudOnly)
                ORDER BY id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$synced", (int)CloudState.Synced);
            cmd.Parameters.AddWithValue("$cloudOnly", (int)CloudState.CloudOnly);
            cmd.Parameters.AddWithValue("$limit", limit);
            using var reader = cmd.ExecuteReader();
            var rows = new List<(long, string, int)>();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), (int)reader.GetInt64(2)));
            return rows;
        }
    }

    /// <summary>
    /// Records that the cloud has taken these ratings. A row whose rating changed between the read and
    /// this call stays unsent: what is compared is the value that was sent, so the newer one goes in
    /// the next pass instead of being lost.
    /// </summary>
    public void NoteRatingsSent(IReadOnlyList<(long Id, int Rating)> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (sent.Count == 0)
            return;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE tracks SET rating_sent = 1 WHERE id = $id AND rating = $rating";
            var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var rating = cmd.Parameters.Add("$rating", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var row in sent)
            {
                id.Value = row.Id;
                rating.Value = row.Rating;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Takes ratings from the cloud catalogue. A row with a rating not sent yet keeps its own: that
    /// value is newer than what the cloud says, and it goes up on the next pass.
    /// </summary>
    /// <returns>How many rows changed.</returns>
    public int AcceptTrackRatings(IEnumerable<(string RpmId, int Rating)> ratings)
    {
        ArgumentNullException.ThrowIfNull(ratings);

        var changed = 0;
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE tracks SET rating = $rating WHERE rpm_id = $id AND rating_sent = 1 AND rating <> $rating";
            var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var rating = cmd.Parameters.Add("$rating", Microsoft.Data.Sqlite.SqliteType.Integer);
            foreach (var row in ratings)
            {
                if (string.IsNullOrEmpty(row.RpmId) || row.Rating is < 0 or > 10)
                    continue;
                id.Value = row.RpmId;
                rating.Value = row.Rating;
                changed += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return changed;
    }
}

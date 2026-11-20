using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// One listen as it travels between the listener's devices through the cloud (<c>cloud/listens</c>).
/// </summary>
/// <param name="ListenId">Made by the device that heard it. A listen sent twice is stored once.</param>
/// <param name="DeviceId">The install that heard it, the same id it files its play totals under.</param>
/// <param name="RpmId">The track, by the id every device shares.</param>
/// <param name="AtUtcTicks">When the listen ended.</param>
/// <param name="Counted">Whether it counted as a play. False is a skip.</param>
/// <param name="LocalHour">0 to 23, on the clock of the device that heard it. See <see cref="ListenEntry"/>.</param>
/// <param name="LocalDayOfWeek">0 = Sunday, on the same clock.</param>
public sealed record SharedListen(
    string ListenId, string DeviceId, string RpmId, long AtUtcTicks,
    double HeardSeconds, double LengthSeconds, bool Counted, int LocalHour, int LocalDayOfWeek);

/// <summary>
/// The listening log, shared with the listener's other devices.
///
/// <para><b>A listen taken in from another device goes into the log and nowhere else.</b> Its play is
/// already on its way here as that device's play total (<see cref="ApplyForeignPlays"/>), so moving the
/// track's counters here as well would count it twice. The log rows only feed the lists that read the log:
/// On repeat, Rediscover, Late night, First listens and the hourly lean of Your mix.</para>
///
/// <para>Rows are told apart by <c>listen.device</c>: null is this library's own listen, anything else is
/// the device that sent it. Only own rows are sent, and only own rows make up a session.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The server stamp this mirror has taken every listen up to.</summary>
    public const string ListensCursorKey = "listens_cursor";

    /// <summary>
    /// This library's own listens that the cloud has not taken yet, oldest first.
    /// </summary>
    /// <param name="mine">This install's id (<see cref="PlaysDevice"/>), which the listens are sent under.</param>
    /// <param name="limit">The most to return.</param>
    /// <remarks>
    /// The track's cloud id is read from the track row now, not from the log row. A track uploaded after it
    /// was played gets its id later, and its listens are sent then. A listen of a track that has no cloud id
    /// stays here unsent until it has one.
    /// </remarks>
    public IReadOnlyList<SharedListen> ListensToSend(string mine, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);

        if (limit <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // Driven from the partial index of unsent own rows, so a pass costs what is unsent, not the log.
            cmd.CommandText =
                """
                SELECT listen.uid, COALESCE(tracks.rpm_id, listen.rpm_id), listen.at_utc_ticks,
                       listen.heard_seconds, listen.length_seconds, listen.counted, listen.local_hour,
                       listen.local_dow
                FROM listen INDEXED BY idx_listen_unsent
                LEFT JOIN tracks ON tracks.relative_path = listen.relative_path
                WHERE listen.sent = 0 AND listen.device IS NULL AND listen.uid IS NOT NULL
                  AND COALESCE(tracks.rpm_id, listen.rpm_id) IS NOT NULL
                ORDER BY listen.id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var listens = new List<SharedListen>();
            while (reader.Read())
                listens.Add(new SharedListen(
                    reader.GetString(0), mine, reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3),
                    reader.GetDouble(4), reader.GetInt64(5) != 0, (int)reader.GetInt64(6), (int)reader.GetInt64(7)));
            return listens;
        }
    }

    /// <summary>Records that the cloud has taken these listens. They are not offered again.</summary>
    public void NoteListensSent(IReadOnlyList<SharedListen> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        if (sent.Count == 0)
            return;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE listen SET sent = 1 WHERE uid = $uid AND device IS NULL";
            var uid = cmd.Parameters.Add("$uid", SqliteType.Text);
            foreach (var listen in sent)
            {
                uid.Value = listen.ListenId;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Takes in listens from the listener's OTHER devices, and joins any listen held without a track to the
    /// track it names, if that track is here now.
    /// </summary>
    /// <param name="mine">This install's id. Listens carrying it are this library's own, come back from the
    /// cloud, and are already in the log.</param>
    /// <param name="listens">What the cloud handed back. A listen already held (by its id) is skipped.</param>
    /// <returns>How many listens were new.</returns>
    /// <remarks>
    /// A listen of a track this library does not hold is kept, with no path, and joined when the track
    /// arrives. The cursor moves past it, so a listen dropped here could not be asked for again.
    /// </remarks>
    public int AcceptListens(string mine, IReadOnlyList<SharedListen> listens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);
        ArgumentNullException.ThrowIfNull(listens);

        var taken = 0;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            if (listens.Count > 0)
            {
                using var insert = _connection.CreateCommand();
                insert.CommandText =
                    """
                    INSERT OR IGNORE INTO listen (relative_path, rpm_id, at_utc_ticks, heard_seconds, length_seconds,
                                                  counted, local_hour, local_dow, session_id, uid, device, sent)
                    VALUES (COALESCE((SELECT relative_path FROM tracks WHERE rpm_id = $rpm LIMIT 1), ''), $rpm, $at,
                            $heard, $length, $counted, $hour, $dow, 0, $uid, $device, 1)
                    """;
                var rpm = insert.Parameters.Add("$rpm", SqliteType.Text);
                var at = insert.Parameters.Add("$at", SqliteType.Integer);
                var heard = insert.Parameters.Add("$heard", SqliteType.Real);
                var length = insert.Parameters.Add("$length", SqliteType.Real);
                var counted = insert.Parameters.Add("$counted", SqliteType.Integer);
                var hour = insert.Parameters.Add("$hour", SqliteType.Integer);
                var dow = insert.Parameters.Add("$dow", SqliteType.Integer);
                var uid = insert.Parameters.Add("$uid", SqliteType.Text);
                var device = insert.Parameters.Add("$device", SqliteType.Text);

                foreach (var listen in listens)
                {
                    if (string.IsNullOrEmpty(listen.ListenId) || string.IsNullOrEmpty(listen.RpmId)
                        || string.IsNullOrEmpty(listen.DeviceId))
                        continue;
                    if (string.Equals(listen.DeviceId, mine, StringComparison.Ordinal))
                        continue;

                    rpm.Value = listen.RpmId;
                    at.Value = listen.AtUtcTicks;
                    heard.Value = listen.HeardSeconds;
                    length.Value = listen.LengthSeconds;
                    counted.Value = listen.Counted ? 1 : 0;
                    hour.Value = Math.Clamp(listen.LocalHour, 0, 23);
                    dow.Value = Math.Clamp(listen.LocalDayOfWeek, 0, 6);
                    uid.Value = listen.ListenId;
                    device.Value = listen.DeviceId;
                    taken += insert.ExecuteNonQuery();
                }
            }

            // Listens held without a track, whose track is here now.
            using (var join = _connection.CreateCommand())
            {
                join.CommandText =
                    """
                    UPDATE listen
                    SET relative_path = (SELECT relative_path FROM tracks WHERE tracks.rpm_id = listen.rpm_id LIMIT 1)
                    WHERE relative_path = '' AND device IS NOT NULL
                      AND EXISTS (SELECT 1 FROM tracks WHERE tracks.rpm_id = listen.rpm_id)
                    """;
                join.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return taken;
    }
}

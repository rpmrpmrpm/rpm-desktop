using System.Globalization;
using RPM.Core.Playback;

namespace RPM.Core.Library;

/// <summary>
/// The listening log: what was played, when, and whether it counted.
///
/// <para><see cref="NoteListen"/> is the only way any of it changes. The log row and the running totals
/// on the track are written in one transaction, from one call. They are two views of one fact, and a
/// mirror where they disagree cannot be repaired by looking at it. The totals are kept because
/// "most played" is read on the UI thread over a million rows. An indexed integer makes that a scan of
/// about two thousand rows, not a group-by over everything. The log is the source of truth, and the
/// counter is its index.</para>
///
/// <para>This is the only table in the mirror that grows with use rather than with the collection, so
/// it is the only one with a retention rule. See <see cref="PruneListensLocked"/> for what bounds it
/// and, more importantly, for what does not.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// How far back the log is kept: five years. A heavy listener plays about fifty tracks a day. At
    /// that rate the log is about ninety thousand rows, or a few megabytes. That is small next to the
    /// mirror of a collection. It is also long enough that a question like "this week two years ago"
    /// still has an answer.
    /// </summary>
    public const int ListenKeepDays = 5 * 365;

    /// <summary>
    /// The hard ceiling, well above what <see cref="ListenKeepDays"/> can produce. It is not the
    /// policy. It is a backstop for cases nobody planned for, such as a stuck player, a script, or a
    /// device that reports a listen every second. The age rule does the normal work.
    /// </summary>
    public const int ListenKeepRows = 1_000_000;

    /// <summary>How often the log checks whether it needs trimming: once a day. That is far more often
    /// than a five-year limit needs. The check runs in the one place that makes the table grow, so it
    /// happens without any caller having to remember it.</summary>
    private const string ListenPrunedKey = "listen_pruned_utc_ticks";

    /// <summary>
    /// Writes a finished listen: the row in the log and, when it counted, one more play and a new
    /// last-played time on the track.
    ///
    /// <para>One transaction, one call site (<c>PlayCounter.Settle</c>), and no other entry point. A
    /// second way to add a play could let the counter and the log drift apart. A mirror in that state
    /// does not say which of the two is right.</para>
    /// </summary>
    /// <returns>False when no track row has that path, for example a file played from outside the
    /// library. Nothing is written in that case, not even the log row. A listen with no track is a row
    /// that can never be joined.</returns>
    public bool NoteListen(ListenEntry listen)
    {
        ArgumentNullException.ThrowIfNull(listen);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            // The play first, because its result tells us whether this is a track we know about.
            if (listen.Counted)
            {
                using var play = _connection.CreateCommand();
                play.CommandText =
                    """
                    UPDATE tracks SET plays = plays + 1, last_played_utc_ticks = $at
                    WHERE relative_path = $path
                    """;
                play.Parameters.AddWithValue("$path", listen.RelativePath);
                play.Parameters.AddWithValue("$at", listen.AtUtcTicks);
                if (play.ExecuteNonQuery() == 0)
                    return false;

                // The same play, as this device's own row. It is in this transaction, not after it.
                // The track's counter and this breakdown are two views of one fact. A mirror where
                // they disagree cannot be repaired by looking at it. A track with no rpm_id has
                // nothing to key the row on, so it gets none. Its plays stay on this machine.
                // See LibraryDatabase.Plays.
                using var own = _connection.CreateCommand();
                own.CommandText =
                    """
                    INSERT INTO track_plays (rpm_id, device, plays, last_played_utc_ticks, sent_plays)
                    SELECT rpm_id, $device, 1, $at, 0 FROM tracks
                    WHERE relative_path = $path AND rpm_id IS NOT NULL
                    ON CONFLICT (rpm_id, device) DO UPDATE SET
                        plays = plays + 1,
                        last_played_utc_ticks =
                            MAX(last_played_utc_ticks, excluded.last_played_utc_ticks)
                    """;
                own.Parameters.AddWithValue("$device", PlaysDeviceLocked());
                own.Parameters.AddWithValue("$path", listen.RelativePath);
                own.Parameters.AddWithValue("$at", listen.AtUtcTicks);
                own.ExecuteNonQuery();
            }
            else if (!ExistsLocked(listen.RelativePath))
            {
                return false;
            }

            using var write = _connection.CreateCommand();
            write.CommandText =
                """
                INSERT INTO listen (relative_path, rpm_id, at_utc_ticks, heard_seconds, length_seconds,
                                    counted, local_hour, local_dow, session_id)
                VALUES ($path, (SELECT rpm_id FROM tracks WHERE relative_path = $path), $at, $heard,
                        $length, $counted, $hour, $dow, $session)
                """;
            write.Parameters.AddWithValue("$path", listen.RelativePath);
            write.Parameters.AddWithValue("$at", listen.AtUtcTicks);
            write.Parameters.AddWithValue("$heard", listen.HeardSeconds);
            write.Parameters.AddWithValue("$length", listen.LengthSeconds);
            write.Parameters.AddWithValue("$counted", listen.Counted ? 1 : 0);
            write.Parameters.AddWithValue("$hour", listen.LocalHour);
            write.Parameters.AddWithValue("$dow", listen.LocalDayOfWeek);
            write.Parameters.AddWithValue("$session", SessionForLocked(listen.AtUtcTicks));
            write.ExecuteNonQuery();

            tx.Commit();
        }

        // Outside the transaction, so the listen is already saved. Trimming is housekeeping. A listen
        // must never be lost because the tidying failed.
        PruneListensIfDue(listen.AtUtcTicks);
        return true;
    }

    /// <summary>Whether the mirror carries that path at all. Held under <see cref="_gate"/>.</summary>
    private bool ExistsLocked(string relativePath)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tracks WHERE relative_path = $path LIMIT 1";
        cmd.Parameters.AddWithValue("$path", relativePath);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Which listening session a listen at <paramref name="atUtcTicks"/> belongs to: the previous
    /// session when the two are close enough in time, otherwise a new one.
    ///
    /// <para>The answer is read from the table, not kept in a field. It is the same after a restart,
    /// after a crash, and on a second machine. A session is a fact about what is in the log, not about
    /// how long this process has been running.</para>
    /// </summary>
    private long SessionForLocked(long atUtcTicks)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT session_id, at_utc_ticks FROM listen ORDER BY at_utc_ticks DESC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return 1;

        var last = reader.GetInt64(0);
        var when = reader.GetInt64(1);
        var gap = TimeSpan.FromMinutes(Listen.SessionGapMinutes).Ticks;
        return atUtcTicks - when <= gap ? last : last + 1;
    }

    /// <summary>
    /// Trims the log, at most once a day.
    ///
    /// <para>The note is saved before the trim, not after it. A trim that throws should wait until the
    /// next day like any other run. It must not be retried on every listen while it keeps failing.</para>
    /// </summary>
    private void PruneListensIfDue(long nowUtcTicks)
    {
        var last = long.TryParse(GetMeta(ListenPrunedKey), out var noted) ? noted : 0;
        if (last != 0 && nowUtcTicks - last < TimeSpan.TicksPerDay)
            return;

        SetMeta(ListenPrunedKey, nowUtcTicks.ToString(CultureInfo.InvariantCulture));

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            PruneListensLocked(nowUtcTicks);
            tx.Commit();
        }
    }

    /// <summary>
    /// Trims the log to its limits: rows older than <see cref="ListenKeepDays"/>, and then any rows
    /// beyond <see cref="ListenKeepRows"/>.
    ///
    /// <para>It deliberately does NOT drop rows whose track is no longer in the mirror. A track can be
    /// missing for ordinary reasons. It may be offloaded, cloud-only and not yet adopted, on a peer's
    /// shelf that is offline, or in the middle of a rescan. Listening history is the one thing in the
    /// app that cannot be rebuilt from the files. So these rows stay. A row that nothing joins to costs
    /// a few dozen bytes and is hidden from every query. It is there again when the music comes
    /// back.</para>
    /// </summary>
    /// <returns>How many rows went.</returns>
    private int PruneListensLocked(long nowUtcTicks)
    {
        var gone = 0;

        using (var old = _connection.CreateCommand())
        {
            old.CommandText = "DELETE FROM listen WHERE at_utc_ticks < $cutoff";
            old.Parameters.AddWithValue("$cutoff", nowUtcTicks - (ListenKeepDays * TimeSpan.TicksPerDay));
            gone += old.ExecuteNonQuery();
        }

        using (var over = _connection.CreateCommand())
        {
            // Everything at or below the id of the row one past the cap, counting from the newest. It
            // uses the id rather than the time, so two listens with the same tick cannot both survive.
            over.CommandText =
                """
                DELETE FROM listen WHERE id <= COALESCE(
                    (SELECT id FROM listen ORDER BY id DESC LIMIT 1 OFFSET $cap), -1)
                """;
            over.Parameters.AddWithValue("$cap", ListenKeepRows);
            gone += over.ExecuteNonQuery();
        }

        return gone;
    }

    /// <summary>Trims the log now, whatever the last trim recorded. This is for tests and for callers
    /// with a specific reason. The log trims itself as it is written. Nothing needs to call this.</summary>
    public int PruneListens(long nowUtcTicks)
    {
        SetMeta(ListenPrunedKey, nowUtcTicks.ToString(CultureInfo.InvariantCulture));

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            var gone = PruneListensLocked(nowUtcTicks);
            tx.Commit();
            return gone;
        }
    }

    /// <summary>How many listens the log holds. The pane asks this before it offers a windowed list,
    /// and tests count with it.</summary>
    public int ListenCount()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM listen";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>How many listening sessions the log holds. A session is a run of listening with no gap
    /// longer than <see cref="Listen.SessionGapMinutes"/>.</summary>
    public int ListenSessions()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(DISTINCT session_id) FROM listen";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Every listen of one track, newest first. This is the history shown in the track info
    /// pane.</summary>
    public IReadOnlyList<(long AtUtcTicks, double HeardSeconds, bool Counted)> ListensOf(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT at_utc_ticks, heard_seconds, counted FROM listen
                WHERE relative_path = $path ORDER BY at_utc_ticks DESC
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);

            using var reader = cmd.ExecuteReader();
            var listens = new List<(long, double, bool)>();
            while (reader.Read())
                listens.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetInt64(2) != 0));
            return listens;
        }
    }
}

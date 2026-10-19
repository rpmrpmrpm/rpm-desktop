namespace RPM.Core.Library;

/// <summary>
/// The play counters, per device.
///
/// <para><b><c>tracks.plays</c> is the merged total.</b> It is everything this machine has played, plus
/// everything the listener's phone and their other computers have reported. Every read of that column
/// in the app means "how much has this been played", which is the answer it should give. So nothing
/// that reads it had to change, and the indexes that serve Most played still apply. What this table
/// adds is what a total cannot give back: which part of the total is this machine's, and so what it
/// may send.</para>
///
/// <para><b>A device writes only its own row, and writes totals.</b> The cloud's listing returns every
/// row a listener has, including this machine's. So the way in filters on the device. It does not rely
/// on the caller to do that. See <see cref="ApplyForeignPlays"/>. Totals are stored rather than counts
/// to add, so a row that arrives twice changes nothing the second time.</para>
///
/// <para>Keyed on <c>rpm_id</c>, because that is the identity both devices share. A track with none is a
/// file the cloud has never seen. Its plays still count here, but they travel nowhere.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The server stamp this mirror has taken every play row up to.</summary>
    public const string PlaysCursorKey = "track_plays_cursor";

    /// <summary>
    /// What this install files its own play rows under, kept with the LIBRARY rather than with the
    /// app's settings.
    /// </summary>
    /// <remarks>
    /// Stored here because the rows it names are here. Two libraries opened on one machine can hold
    /// different music under the same account. If both used one name, each would read the other's rows
    /// as its own and write its own totals over them. The stored name would then flip between the two,
    /// and every other device would apply the difference twice on each pass.
    /// </remarks>
    public const string PlaysDeviceKey = "track_plays_device";

    /// <summary>
    /// The name that the v67 migration files its seeded rows under, until this install says who it is.
    /// </summary>
    /// <remarks>
    /// The migration runs before anything can give the mirror the install's id. The rows it seeds belong
    /// to this install by definition. So they are written under a name that no cloud device has.
    /// <see cref="ClaimSeededPlays"/> renames them on the first sync pass. If the seed were left out, a
    /// computer with years of listening behind it would publish none of that listening.
    /// </remarks>
    public const string PlaysSeedDevice = "\u0001seed";

    /// <summary>
    /// What this install files its own play rows under.
    /// </summary>
    /// <remarks>
    /// Read from the mirror the first time a listen is counted. So a machine that has been named once
    /// counts under that name for the rest of every later session. Only a library that has never synced
    /// counts under the seed name. <see cref="ClaimSeededPlays"/> folds those rows in when it arrives.
    /// This is read and written under <see cref="_gate"/>, the same lock that
    /// <see cref="NoteListen"/> uses to write the counters.
    /// </remarks>
    private string? _playsDevice;

    /// <summary>What to file this install's play rows under. Held under <see cref="_gate"/>.</summary>
    private string PlaysDeviceLocked()
    {
        if (_playsDevice is { Length: > 0 } held)
            return held;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM library_meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", PlaysDeviceKey);
        var stored = cmd.ExecuteScalar();

        _playsDevice = stored is null or DBNull ? PlaysSeedDevice : Convert.ToString(stored);
        return _playsDevice is { Length: > 0 } name ? name : PlaysSeedDevice;
    }

    /// <summary>One device's count for one track.</summary>
    /// <param name="Plays">That device's own total, not the track's. The track's is
    /// <c>tracks.plays</c>, which is these added up.</param>
    /// <param name="SentPlays">What the cloud has accepted for this row. Meaningless on a row that is
    /// not this install's, which never sends.</param>
    public sealed record TrackPlayRow(
        string RpmId, string Device, int Plays, long LastPlayedUtcTicks, int SentPlays);

    /// <summary>
    /// What this library files its own play rows under, made once and kept with the library.
    /// </summary>
    /// <remarks>
    /// Made here rather than passed in, because the rows it names are here. Nothing outside this class
    /// can say what they were filed under before. See <see cref="PlaysDeviceKey"/> for why the name
    /// belongs to the library and not to the install.
    /// </remarks>
    public string PlaysDevice
    {
        get
        {
            using (_gate.Take())
            {
                var held = PlaysDeviceLocked();
                if (held != PlaysSeedDevice)
                    return held;

                var made = Guid.NewGuid().ToString("n");
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO library_meta (key, value) VALUES ($key, $made) " +
                    "ON CONFLICT (key) DO UPDATE SET value = excluded.value";
                cmd.Parameters.AddWithValue("$key", PlaysDeviceKey);
                cmd.Parameters.AddWithValue("$made", made);
                cmd.ExecuteNonQuery();

                _playsDevice = made;
                return made;
            }
        }
    }

    /// <summary>Every device's row for one track, in the devices' own order.</summary>
    public IReadOnlyList<TrackPlayRow> TrackPlaysOf(string rpmId)
    {
        ArgumentNullException.ThrowIfNull(rpmId);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT rpm_id, device, plays, last_played_utc_ticks, sent_plays
                FROM track_plays WHERE rpm_id = $id ORDER BY device
                """;
            cmd.Parameters.AddWithValue("$id", rpmId);
            return ReadPlayRows(cmd);
        }
    }

    /// <summary>
    /// Renames the rows the v67 migration seeded to this install's own id.
    /// </summary>
    /// <remarks>
    /// Safe to call on every pass. The name is written down the first time, and after that there are no
    /// seeded rows left to fold in.
    ///
    /// <para><b>Folded in, not written over.</b> A row can already stand under this install's name while
    /// a seeded one exists beside it. The claim runs on the sync pass. A listen counted before that pass,
    /// in a library that has not been named yet, lands on the seed. Renaming the seed row over the
    /// standing row would throw away everything this machine has counted. The push would then tell every
    /// other device to remove those plays too.</para>
    /// </remarks>
    public void ClaimSeededPlays(string mine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using (var fold = _connection.CreateCommand())
            {
                fold.CommandText =
                    """
                    INSERT INTO track_plays (rpm_id, device, plays, last_played_utc_ticks, sent_plays)
                    SELECT rpm_id, $mine, plays, last_played_utc_ticks, 0
                    FROM track_plays WHERE device = $seed
                    ON CONFLICT (rpm_id, device) DO UPDATE SET
                        plays = plays + excluded.plays,
                        last_played_utc_ticks =
                            MAX(last_played_utc_ticks, excluded.last_played_utc_ticks)
                    """;
                fold.Parameters.AddWithValue("$mine", mine);
                fold.Parameters.AddWithValue("$seed", PlaysSeedDevice);
                fold.ExecuteNonQuery();
            }

            using (var drop = _connection.CreateCommand())
            {
                drop.CommandText = "DELETE FROM track_plays WHERE device = $seed";
                drop.Parameters.AddWithValue("$seed", PlaysSeedDevice);
                drop.ExecuteNonQuery();
            }

            using (var name = _connection.CreateCommand())
            {
                name.CommandText =
                    "INSERT INTO library_meta (key, value) VALUES ($key, $mine) " +
                    "ON CONFLICT (key) DO UPDATE SET value = excluded.value";
                name.Parameters.AddWithValue("$key", PlaysDeviceKey);
                name.Parameters.AddWithValue("$mine", mine);
                name.ExecuteNonQuery();
            }

            tx.Commit();

            // From here on, listens count under this install's own name. This is inside the gate, so a
            // listen settling on another thread reads one name or the other, never half of one.
            _playsDevice = mine;
        }
    }

    /// <summary>This install's rows holding more plays than the cloud has accepted.</summary>
    public IReadOnlyList<TrackPlayRow> PlaysToSend(string mine, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);

        if (limit <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT rpm_id, device, plays, last_played_utc_ticks, sent_plays
                FROM track_plays
                WHERE device = $mine AND plays > sent_plays
                ORDER BY rpm_id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$mine", mine);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadPlayRows(cmd);
        }
    }

    /// <summary>
    /// Records that the cloud has taken these rows.
    /// </summary>
    /// <remarks>
    /// What is written down is the value that was SENT, taken from the rows the caller was given. It is
    /// not the row's plays as they stand now. A listen counted between the read and this call leaves the
    /// row higher than what was sent. Written this way, that play stays unsent and goes in the next pass.
    /// Written the other way, it would be lost.
    /// </remarks>
    public void NotePlaysSent(string mine, IReadOnlyList<TrackPlayRow> sent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);
        ArgumentNullException.ThrowIfNull(sent);

        if (sent.Count == 0)
            return;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE track_plays SET sent_plays = $plays
                WHERE rpm_id = $id AND device = $mine
                """;
            var id = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Text);
            var plays = cmd.Parameters.Add("$plays", Microsoft.Data.Sqlite.SqliteType.Integer);
            cmd.Parameters.AddWithValue("$mine", mine);

            foreach (var row in sent)
            {
                id.Value = row.RpmId;
                plays.Value = row.Plays;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Takes in rows from the listener's OTHER devices, and moves the tracks' own counters by the
    /// difference.
    /// </summary>
    /// <param name="mine">This install's id. Rows carrying it are skipped. They are this machine's own
    /// rows coming back from the store, and applying one would count its plays twice.</param>
    /// <param name="rows">What the cloud handed back. Rows for tracks this mirror does not carry are
    /// still stored, so their plays are not lost. This is the normal case for a listener whose phone
    /// plays music this machine has never held.</param>
    /// <returns>How many rows changed anything.</returns>
    public int ApplyForeignPlays(string mine, IReadOnlyList<TrackPlayRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mine);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
            return 0;

        var applied = 0;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.RpmId))
                    continue;

                // This machine's own row, come back from the store. It is already counted in
                // tracks.plays; applying it would count it twice.
                if (string.Equals(row.Device, mine, StringComparison.Ordinal))
                    continue;

                var (heldPlays, heldLastPlayed) = HeldPlaysLocked(row.RpmId, row.Device);
                if (heldPlays == row.Plays && row.LastPlayedUtcTicks <= heldLastPlayed)
                    continue;

                // The row is written whatever this mirror holds, including for a track it does not
                // carry. A second computer with half the library pulls rows for music it does not
                // have, and the cursor moves past them. If they were dropped here, those plays could
                // never be asked for again. They would be missing for good once the album was downloaded.
                using (var keep = _connection.CreateCommand())
                {
                    keep.CommandText =
                        """
                        INSERT INTO track_plays (rpm_id, device, plays, last_played_utc_ticks, sent_plays)
                        VALUES ($id, $device, $plays, $at, 0)
                        ON CONFLICT (rpm_id, device) DO UPDATE SET
                            plays = excluded.plays,
                            last_played_utc_ticks =
                                MAX(last_played_utc_ticks, excluded.last_played_utc_ticks)
                        """;
                    keep.Parameters.AddWithValue("$id", row.RpmId);
                    keep.Parameters.AddWithValue("$device", row.Device);
                    keep.Parameters.AddWithValue("$plays", row.Plays);
                    keep.Parameters.AddWithValue("$at", row.LastPlayedUtcTicks);
                    keep.ExecuteNonQuery();
                }

                // The track's own counter is worked out again from the rows, not moved by the
                // difference. Both give the same answer when everything arrives in order and exactly
                // once. A recount also gives the right answer after a row that arrived twice, a row
                // that went below zero, and a row that was stored before its track existed. It is one
                // indexed read of a few rows.
                //
                // A track this mirror does not carry updates nothing. There is no counter to put the
                // plays on yet.
                using (var track = _connection.CreateCommand())
                {
                    track.CommandText =
                        """
                        UPDATE tracks
                        SET plays = (SELECT COALESCE(SUM(plays), 0) FROM track_plays
                                     WHERE track_plays.rpm_id = tracks.rpm_id),
                            last_played_utc_ticks = MAX(last_played_utc_ticks, $at)
                        WHERE rpm_id = $id
                        """;
                    track.Parameters.AddWithValue("$at", row.LastPlayedUtcTicks);
                    track.Parameters.AddWithValue("$id", row.RpmId);
                    track.ExecuteNonQuery();
                }

                applied++;
            }

            tx.Commit();
        }

        return applied;
    }

    /// <summary>What this mirror already holds for one device's row. Zero where there is none, which
    /// gives the same result as a row at zero. Held under <see cref="_gate"/>.</summary>
    private (int Plays, long LastPlayedUtcTicks) HeldPlaysLocked(string rpmId, string device)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT plays, last_played_utc_ticks FROM track_plays WHERE rpm_id = $id AND device = $device";
        cmd.Parameters.AddWithValue("$id", rpmId);
        cmd.Parameters.AddWithValue("$device", device);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0), reader.GetInt64(1)) : (0, 0);
    }

    private static IReadOnlyList<TrackPlayRow> ReadPlayRows(Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        var rows = new List<TrackPlayRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new TrackPlayRow(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetInt64(3), reader.GetInt32(4)));
        return rows;
    }
}

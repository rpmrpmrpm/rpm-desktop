using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// The roll-ups over the listening log: which kinds of music are played at which times, and which
/// artists and albums are played most.
///
/// <para>The log holds the three columns these need: <c>local_hour</c>, <c>local_dow</c>, and the path
/// that joins to the track row. This reads all three. <c>local_dow</c> is written on every listen and
/// this is its only reader.</para>
///
/// <para><b>Three grouped reads, and none of them scans a million track rows.</b> The log is bounded
/// by how much someone has listened, not by how much they own (<see cref="ListenKeepDays"/>,
/// <see cref="ListenKeepRows"/>). The range is taken on the indexed <c>at_utc_ticks</c> column. The two
/// group-bys cannot return more rows than the listens they read. The third read is the genre table
/// that the mood bands already build, which is grouped per folder.</para>
///
/// <para><b>Off the UI thread.</b> Nothing draws this while someone waits on a pane. Three grouped
/// reads over years of listening are too much work for the dispatcher. The result describes the
/// listener, so it is read when something needs to decide with it, not when a row is shown.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Rolls the log up into <see cref="ListeningProfile"/>. This is the purpose of this file.
    /// </summary>
    /// <param name="window">How far back to read. <see cref="ListeningWindow.Everything"/> reads all of
    /// the log. There is no default, so the caller must always state the window.</param>
    /// <param name="nowUtcTicks">The current time, for windows that depend on the clock. It is passed
    /// in and not read here, the same rule <see cref="QuerySmartPlaylist"/> follows. A query that
    /// gives different answers when called twice in one second cannot be tested.</param>
    public ListeningProfile ReadListeningProfile(ListeningWindow window, long nowUtcTicks)
    {
        var since = window.SinceUtcTicks(nowUtcTicks);

        // Read before the build. All three reads use the same connection and the same gate, one after
        // another. They are three views of one collection. A write that lands between two of the reads
        // can leave a record in the album read that the genre read does not know about. The build
        // handles this safely: a folder with no genre is left out of the genre roll-ups. So the reads
        // are not locked together.
        var records = ReadListenedRecords(since);
        var credits = ReadListenedCredits(since);

        // The same genre table that the mood bands use, built the same way. A record is one kind of
        // music everywhere in the app. See MoodBands.GenreByFolder for why the folder is the unit and
        // not the track.
        var genreByFolder = MoodBands.GenreByFolder(QueryAlbumGenreCounts(), CatalogueFactsByFolder());

        // How this collection spells its people. Without it, a band tagged four ways counts as four
        // acts. Each act has a quarter of the plays, and none of them reaches the top ten.
        return ListeningProfile.Build(
            window, records, credits, genreByFolder, ArtistCanonicalizer.FromLibrary(this));
    }

    /// <summary>
    /// Every album folder's listening, split by the hour and the weekday it happened on and by whether
    /// it counted.
    ///
    /// <para>The hour is returned as it is, not grouped into a part of the day here. The cut points
    /// for the parts of the day belong to <see cref="DayParts"/>. A CASE in this query would repeat
    /// that rule, and the two would drift apart when one of the cut points changed.</para>
    ///
    /// <para>The album's name and artist come from the same GROUP BY, not from a second query. See
    /// <see cref="QueryAlbumFacts"/>, which gives the same reason. A second walk over the track rows
    /// for one more value per folder is extra work for nothing.</para>
    /// </summary>
    /// <param name="sinceUtcTicks">The earliest tick to take in, or null for all of them. With null,
    /// the WHERE clause is left out entirely and is not compared against a floor value.</param>
    private IReadOnlyList<ListenedRecord> ReadListenedRecords(long? sinceUtcTicks)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT tracks.folder,
                       MIN(NULLIF(tracks.album, '')),
                       -- Album artist falling back to the track credit: the same rule QueryAlbumFacts
                       -- applies, and the same one the naming template used to build the folder this is
                       -- grouped by.
                       MIN(NULLIF(COALESCE(NULLIF(tracks.album_artist, ''), tracks.artist), '')),
                       listen.local_hour,
                       listen.local_dow,
                       listen.counted,
                       COUNT(*),
                       SUM(listen.heard_seconds),
                       MAX(listen.at_utc_ticks)
                FROM listen
                JOIN tracks ON tracks.relative_path = listen.relative_path
                {Since(cmd, sinceUtcTicks)}
                GROUP BY tracks.folder, listen.local_hour, listen.local_dow, listen.counted
                """;

            using var reader = cmd.ExecuteReader();
            var rows = new List<ListenedRecord>();
            while (reader.Read())
                rows.Add(new ListenedRecord(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    (int)reader.GetInt64(3),
                    (int)reader.GetInt64(4),
                    reader.GetInt64(5) == 1,
                    (int)reader.GetInt64(6),
                    reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                    reader.GetInt64(8)));
            return rows;
        }
    }

    /// <summary>
    /// Every track credit's listening, split only by whether it counted.
    ///
    /// <para>This uses the track's own artist first, and the album artist as the fallback. This is the
    /// reverse of the order used in the album read above, and that is intended. A listen is of a song.
    /// On a compilation or a record with guest artists, the album artist is the label's answer. The
    /// track credit is who actually played the song. A track with no credit of its own uses the album
    /// artist. This is the normal case for an album by one artist.</para>
    ///
    /// <para>Names are merged after the query, not in it. SQLite treats two spellings as two GROUP BY
    /// keys. Which spelling the library uses is decided by <see cref="ArtistCanonicalizer"/>, and SQL
    /// cannot answer that.</para>
    /// </summary>
    private IReadOnlyList<ListenedCredit> ReadListenedCredits(long? sinceUtcTicks)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COALESCE(NULLIF(tracks.artist, ''), NULLIF(tracks.album_artist, '')) AS credit,
                       listen.counted,
                       COUNT(*),
                       SUM(listen.heard_seconds),
                       MAX(listen.at_utc_ticks)
                FROM listen
                JOIN tracks ON tracks.relative_path = listen.relative_path
                {Since(cmd, sinceUtcTicks)}
                GROUP BY credit, listen.counted
                HAVING credit IS NOT NULL
                """;

            using var reader = cmd.ExecuteReader();
            var rows = new List<ListenedCredit>();
            while (reader.Read())
                rows.Add(new ListenedCredit(
                    reader.GetString(0),
                    reader.GetInt64(1) == 1,
                    (int)reader.GetInt64(2),
                    reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                    reader.GetInt64(4)));
            return rows;
        }
    }

    /// <summary>
    /// The window as a WHERE clause with its parameter already bound, or nothing at all.
    ///
    /// <para>For <see cref="ListeningWindow.Everything"/> there is no clause at all, not a comparison
    /// against a floor that no row can be under. The whole log is scanned either way. A clause that is
    /// always true is one a reader may take for a real filter.</para>
    /// </summary>
    private static string Since(SqliteCommand cmd, long? sinceUtcTicks)
    {
        if (sinceUtcTicks is not { } since)
            return "";

        cmd.Parameters.AddWithValue("$since", since);
        return "WHERE listen.at_utc_ticks >= $since";
    }
}

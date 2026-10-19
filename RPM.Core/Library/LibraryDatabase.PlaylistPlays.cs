namespace RPM.Core.Library;

/// <summary>Which kind of list a play belongs to. There are two kinds, because they are keyed by
/// different things and one cannot be turned into the other. A kept list is a row in this mirror. A
/// generated list is a query that the library writes.</summary>
public enum PlaylistPlayKind
{
    /// <summary>A playlist the listener made, keyed by its <c>pl_node</c> id.</summary>
    Kept,

    /// <summary>A list the library generates from play history, keyed by its
    /// <see cref="SmartPlaylistId"/> as <see cref="LibraryDatabase.PlaylistKey"/> writes it.</summary>
    Generated,
}

/// <param name="Key">The node id as text, or a generated list's id. See
/// <see cref="LibraryDatabase.PlaylistKey"/>.</param>
public readonly record struct PlaylistPlayRow(
    PlaylistPlayKind Kind,
    string Key,
    int Plays,
    long LastPlayedUtcTicks);

/// <summary>
/// How often each playlist has been played.
///
/// <para><b>Why it exists.</b> A track has a <c>plays</c> count, and a record is counted through its
/// tracks. But <c>pl_node</c> has no counter, and a generated list has no row at all. Without this
/// table, the mirror could not answer "which playlists are played most", and the home screen's quick
/// selection could not offer them.</para>
///
/// <para><b>Stored in the mirror, not beside it.</b> The station log is stored beside the mirror,
/// for the opposite reason (see <see cref="Radio.RadioListens"/>). A station is not on any shelf, and
/// its listening history outlives whichever library is open. A playlist belongs to this library. Its
/// node ids mean nothing anywhere else. When the app points at another collection, it should show
/// that collection's lists and that collection's counts.</para>
///
/// <para><b>One table for both kinds, not a column on <c>pl_node</c>.</b> A column could only count
/// kept lists, because generated lists have no row to put it on. A row keyed by (kind, key) counts
/// both kinds the same way. A kept list that is deleted simply stops being joined.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// How a generated list is spelled in the play log.
    /// </summary>
    /// <remarks>
    /// The format is written out here, not taken from <c>ToString()</c>. <c>ToString()</c> is generated
    /// by the compiler and changes if the record's members are reordered. The key is stored on disk,
    /// so a changed format would silently orphan every count. There are three parts because the id has
    /// three fields. A decade of 0 and a null genre are part of the key, just as the kind is.
    /// </remarks>
    public static string PlaylistKey(SmartPlaylistId id) =>
        $"{(int)id.Kind}|{id.Genre ?? ""}|{id.Decade}";

    /// <summary>Writes one play of a list. Called when a list starts playing, not when it is opened.
    /// Looking at a playlist is not playing it.</summary>
    /// <param name="atUtcTicks">The time of the play, in UTC ticks. Passed in rather than read here, so
    /// a caller that settles several plays can write one shared time.</param>
    public void NotePlaylistPlayed(PlaylistPlayKind kind, string key, long atUtcTicks)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO playlist_play (kind, key, plays, last_played_utc_ticks)
                VALUES ($kind, $key, 1, $at)
                ON CONFLICT(kind, key) DO UPDATE SET
                    plays = plays + 1,
                    last_played_utc_ticks = $at
                """;
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$at", atUtcTicks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The lists played most, most first. Ties go to the most recent play, so two lists with
    /// one play each are offered newest first, not in whatever order the table holds them.</summary>
    public IReadOnlyList<PlaylistPlayRow> QueryMostPlayedPlaylists(int count)
    {
        if (count <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT kind, key, plays, last_played_utc_ticks
                FROM playlist_play
                WHERE plays > 0
                ORDER BY plays DESC, last_played_utc_ticks DESC
                LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$n", count);

            using var reader = cmd.ExecuteReader();
            var rows = new List<PlaylistPlayRow>();
            while (reader.Read())
                rows.Add(new PlaylistPlayRow(
                    (PlaylistPlayKind)reader.GetInt64(0),
                    reader.GetString(1),
                    (int)reader.GetInt64(2),
                    reader.GetInt64(3)));

            return rows;
        }
    }

    /// <summary>The tracks played most, most first. This is the songs part of the home screen's quick
    /// selection.</summary>
    /// <remarks>
    /// Uses <c>idx_tracks_plays</c>, which already exists for the sortable column. Filtered by the
    /// reach term, like every other read of this table. So a borrowed shelf does not offer a song from
    /// a computer that is switched off.
    /// </remarks>
    public IReadOnlyList<LibraryTrack> QueryMostPlayedTracks(int count)
    {
        if (count <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT * FROM tracks WHERE plays > 0"
                + (ReachTerm("cloud_state") is { Length: > 0 } reach ? $" AND {reach}" : "")
                + " ORDER BY plays DESC, last_played_utc_ticks DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", count);

            using var reader = cmd.ExecuteReader();
            var tracks = new List<LibraryTrack>();
            while (reader.Read())
                tracks.Add(ReadTrack(reader));

            return tracks;
        }
    }
}

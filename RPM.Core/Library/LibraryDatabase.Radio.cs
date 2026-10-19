namespace RPM.Core.Library;

/// <summary>What a song radio reads from the mirror. See <see cref="SongRadio"/>.</summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Every song a radio can pick from or start from, with its genre, year, mood readings and mood lists.
    ///
    /// <para>Read once when a radio starts, off the drawing thread. The track read holds the read gate for one scan of
    /// <c>tracks</c>; the genres and the catalogue facts are two more reads, the same pair the mood cut takes. The mood
    /// lists come from <see cref="SessionMoods"/>, so a radio agrees with the mood playlists shown this session, and
    /// the first radio of a session builds that cut if nothing else has yet.</para>
    /// </summary>
    public RadioCrate ReadRadioCrate()
    {
        var rows = QueryRadioRows();
        var genreByFolder = MoodBands.GenreByFolder(QueryAlbumGenreCounts(), CatalogueFactsByFolder());
        return RadioCrate.Build(rows, genreByFolder, SessionMoods.MoodsByPath());
    }

    /// <summary>
    /// One row per track. Nothing is filtered out, because the song a radio starts from may be one it would never
    /// pick (an interlude, or a track that cannot be played from here); <see cref="RadioRow.Offered"/> says which
    /// can be picked.
    /// </summary>
    private IReadOnlyList<RadioRow> QueryRadioRows()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // The tempo is the analyser's where it is confident enough to beat-match on, else the file's tag (which
            // the analyser also writes when it is). Joined on the analysis table's primary key, so one seek a row.
            cmd.CommandText =
                $"""
                SELECT tracks.relative_path, tracks.folder, COALESCE(tracks.album_artist, ''),
                       COALESCE(tracks.artist, ''), COALESCE(tracks.title, ''), COALESCE(tracks.year, 0),
                       tracks.mood_brightness, tracks.mood_onsets, tracks.mood_dynamics,
                       CASE WHEN tracks.duration_seconds >= {Moods.LeastSeconds}{Reachable} THEN 1 ELSE 0 END,
                       CASE WHEN a.bpm > 0 AND a.bpm_confidence >= $minBpmConfidence THEN a.bpm
                            WHEN tracks.tag_bpm > 0 THEN tracks.tag_bpm
                       END
                FROM tracks
                LEFT JOIN track_analysis a ON a.relative_path = tracks.relative_path
                """;
            cmd.Parameters.AddWithValue("$minBpmConfidence", Analysis.TrackAnalysis.MinBeatMatchConfidence);

            using var reader = cmd.ExecuteReader();
            var rows = new List<RadioRow>();
            while (reader.Read())
                rows.Add(new RadioRow(
                    reader.GetString(0), reader.GetString(1),
                    RecommendRefusals.CreditOf(reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4),
                    (int)reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.GetInt64(9) == 1,
                    reader.IsDBNull(10) ? null : reader.GetDouble(10)));
            return rows;
        }
    }
}

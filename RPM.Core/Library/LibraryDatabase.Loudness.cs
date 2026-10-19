using RPM.Core.Analysis;
using RPM.Core.Cloud;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>A track nobody has measured yet: where it is, and what to stamp the reading against.</summary>
/// <param name="NeedsLevel">Whether this track still needs a level. This is almost always true on a new
/// library, and almost always false on a well-kept one, where ReplayGain tags came with the files. The
/// flag must be checked. A track decoded only for its mood must not have a level it already has
/// overwritten. A decode that fails would take a good reading down with it.</param>
/// <param name="NeedsMood">Whether it still needs describing. A level can be borrowed from the
/// recording pool without opening the file, but a mood cannot. So a row that needs only a level can skip
/// the decode. A row that needs a mood cannot.</param>
public sealed record LoudnessWork(
    string RelativePath,
    long SizeBytes,
    long ModifiedUtcTicks,
    bool NeedsLevel,
    bool NeedsMood);

/// <summary>
/// One track as the band cut reads it: what the meter said, and which record it said it about.
/// </summary>
/// <param name="Folder">The album folder the track sits in. This is the join to what the record is filed
/// under. It is carried on the row rather than looked up by the caller, because a mood is cut within its
/// genre (see <see cref="MoodBands"/>). A reading with no way back to its record cannot be cut.</param>
/// <param name="IsExplicit">Whether the file is marked explicit. It is carried on the reading because one
/// list refuses explicit tracks outright (see <see cref="MoodShelf"/>). There is no default. A track
/// whose advisory nobody has set is not explicit. That must be a decision the reader makes, not a field
/// it forgot.</param>
/// <param name="LyricValence">How positive or negative the song's words are, or null where nothing was
/// believed. See <see cref="Lyrics.LyricValence"/>. Null is the normal answer for most of a collection.
/// It is never read as zero. It means the song is placed by its measurements alone.</param>
/// <param name="Artist">Who this song is by, as <see cref="RecommendRefusals.CreditOf"/> composes it: the
/// record's credit, or the track's own credit where the record is a compilation. On a compilation the
/// album credit is not an act. Empty where nothing names anybody. It is carried because a generated list
/// caps how much of one act it will hold. The cap must be applied where the list is built, not looked up
/// per row afterwards.</param>
/// <param name="Loudness">How loud the file is mastered, in LUFS, or null where nobody has measured it yet.
/// This is not a rank and must never be used as one. See <see cref="MoodBands.LoudAt"/>, where this is
/// explained. Null is the normal answer on a shelf the loudness pass has not reached. A track with null
/// is left exactly where the other three measurements put it.</param>
public sealed record MeasuredMood(
    string RelativePath,
    string Folder,
    double Brightness,
    double Onsets,
    double Dynamics,
    bool IsExplicit,
    string Artist,
    int Year,
    double? LyricValence,
    double? Loudness);

/// <summary>
/// Storage for how loud each track is.
///
/// <para>The columns live on <c>tracks</c> rather than in a table of their own. That is the whole
/// design. A loudness is a fact about the file, in the same way its sample rate is. It is read or
/// measured once, it travels with the row into the cloud catalogue, and it is lost when the bytes
/// change. Everything that already carries a track's stream facts (the mirror, the catalogue push, the
/// phone) carries this too, with no changes needed.</para>
///
/// <para>NULL means nobody has measured it. That is what <see cref="QueryTracksNeedingLoudness"/>
/// looks for. Every row in an existing library reads back as NULL. So after an upgrade, the library is
/// levelled over the next few minutes, and the upgrade itself does not wait for it.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Records what a track measured.
    ///
    /// <para>Stamped against the file it was taken from. A row whose size or modified time has changed
    /// since is not written, because the reading then describes bytes that are no longer there. The
    /// guard is in the WHERE clause, not in the caller. The caller spent a whole decode getting here,
    /// and the file may have been replaced while it did.</para>
    /// </summary>
    /// <param name="reading">What the meter found. An unknown reading is stored as a real answer. See
    /// <see cref="SaveUnmeasurable"/> for why it has to be.</param>
    public void SaveTrackLoudness(string relativePath, long sizeBytes, long modifiedUtcTicks,
        LoudnessReading reading)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (!reading.Known)
        {
            SaveUnmeasurable(relativePath, sizeBytes, modifiedUtcTicks);
            return;
        }

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET loudness_lufs = $lufs, true_peak = $peak
                WHERE relative_path = $path
                  AND size_bytes = $size AND modified_utc_ticks = $mtime
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$mtime", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$lufs", reading.Lufs);
            cmd.Parameters.AddWithValue("$peak", reading.TruePeak > 0 ? reading.TruePeak : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records how positive or negative a song's words are.
    ///
    /// <para>Written where a lyric sheet is already in hand, instead of by a pass of its own. This is the
    /// reason <c>MoodMeter</c> runs on the loudness decode. The expensive part is the fetch, and something
    /// else has already paid for it.</para>
    ///
    /// <para><b>Not stamped against the file's bytes</b>, unlike every other reading here. This is not a
    /// fact about the bytes. It comes from a lyric sheet fetched by artist, title and length. Re-encoding a
    /// file does not change the answer, so re-encoding must not throw it away. What would change it is the
    /// tags being corrected, because then the sheet is for a different track.
    /// </para>
    /// </summary>
    /// <param name="reading">What the words said. An unbelieved reading writes null. That is what every
    /// unread song already holds, so nothing has to tell the two apart. See the v64 migration.
    /// </param>
    public void SaveLyricValence(string relativePath, Lyrics.LyricReading reading)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET lyric_valence = $valence WHERE relative_path = $path";
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue(
                "$valence", reading.Known ? reading.Valence : (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Writes down that a track cannot be measured, so that it is not decoded again on every pass.
    ///
    /// <para>Recorded as a loudness of negative infinity. That is the meter's own answer for silence and
    /// for anything it could not read. It has to be a stored value, not NULL. NULL means "not tried yet". A
    /// corrupt file left at NULL would be picked up, decoded in full, fail, and be picked up again for the
    /// life of the library. The row is tried again once the file is replaced, because the file's stamp
    /// then no longer matches.</para>
    ///
    /// <para>Playback reads it back as "no gain". That is the right thing to do with a track nobody can
    /// measure. It plays at the level it was recorded at.</para>
    ///
    /// <para><b>The arousal numbers are also written</b>, as zero across all three. Being unmeasurable is a
    /// property of the FILE, not of one measurement. The decode that failed is the decode both answers come
    /// from. If they were left null, the pass would still claim the row on every launch for the mood it has
    /// not got. That is the same loop this method exists to close, one column along.</para>
    /// </summary>
    private void SaveUnmeasurable(string relativePath, long sizeBytes, long modifiedUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks
                SET loudness_lufs = $lufs, true_peak = NULL,
                    mood_brightness = 0, mood_onsets = 0, mood_dynamics = 0
                WHERE relative_path = $path
                  AND size_bytes = $size AND modified_utc_ticks = $mtime
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$mtime", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$lufs", double.NegativeInfinity);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records what a track sounded like on the arousal axis.
    ///
    /// <para>Stamped against the file the same way as the level, and for the same reason. It came from the
    /// same decode, and by the time it is written the file may have been replaced.</para>
    ///
    /// <para>An unknown reading is written as zero rather than left null. That is how the pass knows to
    /// stop asking. Null means "not measured". Zero across all three means "measured, and there was nothing
    /// to describe". For example, a forty-second interlude, a locked groove, or a silent track. If these were
    /// left null, each one would be decoded again on every pass, forever.</para>
    /// </summary>
    public void SaveTrackMood(string relativePath, long sizeBytes, long modifiedUtcTicks,
        Analysis.MoodReading mood)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks
                SET mood_brightness = $bright, mood_onsets = $onsets, mood_dynamics = $dynamics
                WHERE relative_path = $path
                  AND size_bytes = $size AND modified_utc_ticks = $mtime
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$mtime", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$bright", mood.Known ? mood.Brightness : 0d);
            cmd.Parameters.AddWithValue("$onsets", mood.Known ? mood.Onsets : 0d);
            cmd.Parameters.AddWithValue("$dynamics", mood.Known ? mood.Dynamics : 0d);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every track that has been described, for working out where the bands fall.
    ///
    /// <para>The whole shelf is read at once, not one page of it. The question the caller has is about the
    /// DISTRIBUTION: which fifth of this collection is the quiet end? A fifth of a page is not a fifth of
    /// anything. Each row is three doubles and two short strings. So a hundred thousand tracks is a few
    /// megabytes and one read.</para>
    ///
    /// <para>The folder comes with each row, because the cut is made within a genre, and a genre is a fact
    /// about the RECORD (see <see cref="MoodBands.GenreByFolder"/>). Asking for it here adds one column to a
    /// read that happens anyway. Asking for it afterwards would need a second pass over the same rows.</para>
    ///
    /// <para>Rows with zero across the board are left out here, not by the reader. They are tracks that were
    /// measured and had nothing to describe. Putting them in the distribution would pull the quiet end down
    /// towards silence.</para>
    ///
    /// <para>Tracks too short to be songs are left out here too, on purpose. The claim has the same guard,
    /// but that only stops such tracks being measured from now on. This guard stops the ones measured before
    /// the rule existed from counting. So a shelf that has already been through the pass needs no migration
    /// and no re-decode. Both read the same number (see <see cref="Moods.LeastSeconds"/>).</para>
    /// </summary>
    public IReadOnlyList<MeasuredMood> QueryMeasuredMoods()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT relative_path, folder, mood_brightness, mood_onsets, mood_dynamics,
                       COALESCE(advisory, 0),
                       -- Both, and the credit is composed in code by RecommendRefusals.CreditOf.
                       -- It used to be COALESCE(album_artist, artist) here, which reads "Various
                       -- Artists" as one act and capped forty-five compilations at ten songs between
                       -- them. The rule now has a compilation in it, and a rule with a condition in
                       -- it belongs where it can be read and tested rather than in a SELECT list.
                       COALESCE(album_artist, ''), COALESCE(artist, ''),
                       -- Nought where nothing said, which is what the column already stores and what
                       -- MoodList.InOneEra reads as "this song cannot be placed in a decade".
                       -- A verdict is NOT read here. It used to be, as one flag per track, and a
                       -- verdict is about a list: see LibraryDatabase.LikedByKind, which is read once
                       -- beside this and applied per kind where each list is built.
                       year,
                       -- Null where nobody read the words, or where the gates refused what they read.
                       -- The cut treats both the same way, which is to leave the song where its
                       -- measurements put it.
                       lyric_valence,
                       -- How loud the master is, which is the one thing the Danceable and Focus cuts
                       -- could not see: a half-time dance record reads as SPARSE on onsets and flat on
                       -- dynamics, and was landing in the focus list. See MoodBands.LoudAt. Null on a
                       -- shelf the loudness pass has not reached, and read as "leave it where the other
                       -- three put it" rather than as quiet.
                       loudness_lufs
                FROM tracks
                WHERE mood_brightness IS NOT NULL
                  AND NOT (mood_brightness = 0 AND mood_onsets = 0 AND mood_dynamics = 0)
                  AND duration_seconds >= {Moods.LeastSeconds}
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<MeasuredMood>();
            while (reader.Read())
                rows.Add(new MeasuredMood(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetInt64(5) == (long)Models.ContentAdvisory.Explicit,
                    RecommendRefusals.CreditOf(reader.GetString(6), reader.GetString(7)),
                    (int)reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    // Infinity is what a silent file measures, and it is not a loudness: read as
                    // nothing, so a data track or a gap is left where its other numbers put it rather
                    // than being called the quietest thing on the shelf.
                    reader.IsDBNull(10) || double.IsInfinity(reader.GetDouble(10))
                        ? null
                        : reader.GetDouble(10)));
            return rows;
        }
    }

    /// <summary>
    /// The next tracks with no reading at all, oldest additions first.
    ///
    /// <para>Only tracks whose bytes are on this disk. A cloud-only row has nothing here to decode. It is
    /// measured by whoever holds it, either the machine that uploaded it or the backfill in the cloud. It
    /// arrives with the number already on it.</para>
    ///
    /// <para><b>Or a mood.</b> The pass hands out a track that needs either, because one decode answers
    /// both (see <c>LoudnessScanner.Scan</c>). A track that needed only the mood would otherwise never be
    /// reached. Most of a well-kept library arrives with a ReplayGain tag, so it is never handed out at all.
    /// What comes back is stored by whichever half asked for it.</para>
    ///
    /// <para><b>Except a track too short to be a song</b> (see <see cref="Moods.LeastSeconds"/>). Such a
    /// track is never asked for a mood, so it is never decoded for one. A short track that also needs a
    /// level is still handed out. It will be played, so it should play at the same level as everything
    /// around it. But it comes back saying it needs no mood, and no mood is stored for it.</para>
    ///
    /// <para>A row lent by another of the user's computers is excluded for the same reason, and it needs its
    /// own note. That machine holds the file locally, so its row arrives here marked
    /// <see cref="CloudState.Local"/>, and the cloud test above lets it through. There is no file under this
    /// library's root to open, so it can never be measured. It would be handed out on every pass, fail, and
    /// be handed out again for as long as the pairing lasts. Its owner measures it, and the reading arrives
    /// with the next catalogue sync.</para>
    /// </summary>
    public IReadOnlyList<LoudnessWork> QueryTracksNeedingLoudness(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT relative_path, size_bytes, modified_utc_ticks,
                       loudness_lufs IS NULL,
                       mood_brightness IS NULL AND duration_seconds >= {Moods.LeastSeconds}
                FROM tracks
                WHERE (loudness_lufs IS NULL
                       OR (mood_brightness IS NULL AND duration_seconds >= {Moods.LeastSeconds}))
                  AND cloud_state <> {(int)CloudState.CloudOnly}
                  AND peer_adopted = 0
                ORDER BY added_utc_ticks
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var work = new List<LoudnessWork>();
            while (reader.Read())
                work.Add(new LoudnessWork(
                    reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                    reader.GetInt64(3) != 0, reader.GetInt64(4) != 0));
            return work;
        }
    }

    /// <summary>How many tracks are still waiting for each half of the pass. A settings page shows this, so
    /// the listener can see the pass working. It counts what <see cref="QueryTracksNeedingLoudness"/> would
    /// hand out, and it must keep doing so. A count that includes rows the pass will never take never
    /// reaches zero. A log line uses this to say what the sweep is doing. A library with ReplayGain tags
    /// shows zero waiting for the level and every track waiting for the mood, until the mood pass
    /// has run.</summary>
    public (int Level, int Mood) CountTracksNeedingMeasurement()
    {
        // This uses the reading gate, because counting only reads. Taking the writing gate would make the
        // count wait in front of every write the app wants to make. That happens at startup, when the pass
        // asks, which is when the most is going on. Measured on a library of 19,747 tracks, the mood half
        // alone held the gate for about 800 ms once warm.
        using (_readGate.Take())
        {
            var where = $"cloud_state <> {(int)CloudState.CloudOnly} AND peer_adopted = 0";
            return (
                (int)(long)(ReadScalar($"SELECT COUNT(*) FROM tracks WHERE loudness_lufs IS NULL AND {where}") ?? 0L),
                (int)(long)(ReadScalar(
                    $"SELECT COUNT(*) FROM tracks WHERE mood_brightness IS NULL " +
                    $"AND duration_seconds >= {Moods.LeastSeconds} AND {where}") ?? 0L));
        }
    }

    public int CountTracksNeedingLoudness()
    {
        using (_readGate.Take())
            return (int)(long)(ReadScalar(
                $"SELECT COUNT(*) FROM tracks WHERE (loudness_lufs IS NULL " +
                $"     OR (mood_brightness IS NULL AND duration_seconds >= {Moods.LeastSeconds})) " +
                $"AND cloud_state <> {(int)CloudState.CloudOnly} AND peer_adopted = 0") ?? 0L);
    }

    /// <summary>One value off the reading connection. <c>Scalar</c> uses the writing connection. A caller
    /// holding <c>_readGate</c> must not use it, because it would read through a connection it does not
    /// hold the gate for.</summary>
    private object? ReadScalar(string sql)
    {
        using var cmd = _read.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>
    /// Every measured track on one album, for working out the album's own loudness.
    ///
    /// <para>Length comes with it, because album loudness is weighted by length. A forty-second interlude
    /// must not count as much as a ten-minute side. See <see cref="ReplayGain.AlbumLufs"/>.</para>
    /// </summary>
    public IReadOnlyList<(double Lufs, double Seconds)> QueryAlbumLoudness(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT loudness_lufs, duration_seconds FROM tracks
                WHERE folder = $folder AND loudness_lufs IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$folder", folder);

            using var reader = cmd.ExecuteReader();
            var readings = new List<(double, double)>();
            while (reader.Read())
                readings.Add((reader.GetDouble(0), reader.GetDouble(1)));
            return readings;
        }
    }

    /// <summary>
    /// The loudest true peak on an album, or zero when none of its tracks carry one.
    ///
    /// <para>An album gain is clamped against the whole record's loudest moment rather than each
    /// track's own. Clamping per track would pull one song down relative to its neighbours, which is
    /// the exact thing album mode exists to prevent.</para>
    /// </summary>
    public double QueryAlbumTruePeak(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        using (_gate.Take())
        {
            var peak = Scalar(
                "SELECT MAX(true_peak) FROM tracks WHERE folder = $folder AND true_peak IS NOT NULL",
                ("$folder", folder));
            return peak is double value && double.IsFinite(value) ? value : 0;
        }
    }

    /// <summary>Forgets every reading, so a library can be measured again from scratch. This is for the
    /// case where the meter itself has changed, not the files.</summary>
    public void ClearTrackLoudness()
    {
        using (_gate.Take())
            Execute("UPDATE tracks SET loudness_lufs = NULL, true_peak = NULL");
    }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);

        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }
}

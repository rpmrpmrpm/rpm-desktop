using System.Globalization;
using RPM.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>An analysed track whose measured tempo and key have not been written to its file yet:
/// where it is, and what its row is currently stamped against.</summary>
public sealed record AnalysisTagWork(
    string RelativePath,
    long SizeBytes,
    long ModifiedUtcTicks);

/// <summary>
/// Storage for what the DJ's analyser found in a track: tempo, beat grid, key, energy and cue
/// points. Analysing a track needs a full decode and a few thousand FFTs, so it is done once and
/// kept. The first auto-mix of a playlist analyses its tracks, and every later one starts at once.
///
/// <para>Rows are keyed by the track's path relative to the library root, and stamped with the
/// file's size and modification time. A file that has been re-tagged, replaced or re-encoded no
/// longer matches its row and is analysed again. The stamp is what makes the cache safe and not
/// only fast.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>Stores (or replaces) one track's analysis.</summary>
    /// <param name="sampleHash">What the decoded audio hashes to (see <see cref="AudioKey"/>), when the
    /// caller decoded it and so knows. Null leaves the column as it was and does not clear it: a
    /// caller that did not decode has learnt nothing about the audio and must not be able to say so.</param>
    public void SaveTrackAnalysis(
        string relativePath, long sizeBytes, long modifiedUtcTicks, TrackAnalysis analysis, string? sampleHash = null)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(analysis);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO track_analysis (
                    relative_path, size_bytes, modified_utc_ticks, analyzer_version, duration_seconds,
                    bpm, bpm_confidence, beat_times, first_downbeat, downbeat_confidence, beats_per_bar,
                    camelot, key_confidence, intensity, energy_curve, intro_end_seconds, outro_start_seconds,
                    analyzed_utc_ticks, sample_hash,
                    audio_start_seconds, audio_end_seconds, beat_first_seconds, beat_last_seconds,
                    bass_curve, mid_curve, top_curve, vocal_curve,
                    phrase_bars, phrase_first_bar, phrase_confidence, tempo_stability,
                    drop_seconds, intro_level, outro_level)
                VALUES (
                    $path, $size, $modified, $version, $duration,
                    $bpm, $bpmConfidence, $beats, $downbeat, $downbeatConfidence, $perBar,
                    $camelot, $keyConfidence, $intensity, $energy, $intro, $outro,
                    $at, $sampleHash,
                    $audioStart, $audioEnd, $beatFirst, $beatLast,
                    $bass, $mid, $top, $vocal,
                    $phraseBars, $phraseFirstBar, $phraseConfidence, $stability,
                    $drops, $introLevel, $outroLevel)
                ON CONFLICT(relative_path) DO UPDATE SET
                    size_bytes = excluded.size_bytes,
                    modified_utc_ticks = excluded.modified_utc_ticks,
                    analyzer_version = excluded.analyzer_version,
                    duration_seconds = excluded.duration_seconds,
                    bpm = excluded.bpm,
                    bpm_confidence = excluded.bpm_confidence,
                    beat_times = excluded.beat_times,
                    first_downbeat = excluded.first_downbeat,
                    downbeat_confidence = excluded.downbeat_confidence,
                    beats_per_bar = excluded.beats_per_bar,
                    camelot = excluded.camelot,
                    key_confidence = excluded.key_confidence,
                    intensity = excluded.intensity,
                    energy_curve = excluded.energy_curve,
                    intro_end_seconds = excluded.intro_end_seconds,
                    outro_start_seconds = excluded.outro_start_seconds,
                    analyzed_utc_ticks = excluded.analyzed_utc_ticks,
                    -- Kept when the caller had nothing to say. COALESCE the other way round would let
                    -- a save that never decoded anything wipe the one thing on the row that travels.
                    sample_hash = COALESCE(excluded.sample_hash, track_analysis.sample_hash),
                    audio_start_seconds = excluded.audio_start_seconds,
                    audio_end_seconds = excluded.audio_end_seconds,
                    beat_first_seconds = excluded.beat_first_seconds,
                    beat_last_seconds = excluded.beat_last_seconds,
                    bass_curve = excluded.bass_curve,
                    mid_curve = excluded.mid_curve,
                    top_curve = excluded.top_curve,
                    vocal_curve = excluded.vocal_curve,
                    phrase_bars = excluded.phrase_bars,
                    phrase_first_bar = excluded.phrase_first_bar,
                    phrase_confidence = excluded.phrase_confidence,
                    tempo_stability = excluded.tempo_stability,
                    drop_seconds = excluded.drop_seconds,
                    intro_level = excluded.intro_level,
                    outro_level = excluded.outro_level,
                    -- A fresh reading has not been written into the file, whatever the old one had.
                    -- The row is only replaced when the file changed or the analyser did, and either
                    -- way the numbers in the tags are now somebody else's answer.
                    tags_written_utc_ticks = 0
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$modified", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$version", analysis.Version);
            cmd.Parameters.AddWithValue("$duration", analysis.DurationSeconds);
            cmd.Parameters.AddWithValue("$bpm", analysis.Bpm);
            cmd.Parameters.AddWithValue("$bpmConfidence", analysis.BpmConfidence);
            cmd.Parameters.AddWithValue("$beats", PackDoubles(analysis.BeatTimes));
            cmd.Parameters.AddWithValue("$downbeat", analysis.FirstDownbeat);
            cmd.Parameters.AddWithValue("$downbeatConfidence", analysis.DownbeatConfidence);
            cmd.Parameters.AddWithValue("$perBar", analysis.BeatsPerBar);
            cmd.Parameters.AddWithValue("$camelot", (object?)NullIfEmpty(analysis.Key.Camelot) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$keyConfidence", analysis.KeyConfidence);
            cmd.Parameters.AddWithValue("$intensity", analysis.Intensity);
            cmd.Parameters.AddWithValue("$energy", PackFloats(analysis.EnergyCurve));
            cmd.Parameters.AddWithValue("$intro", analysis.IntroEndSeconds);
            cmd.Parameters.AddWithValue("$outro", analysis.OutroStartSeconds);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$sampleHash", (object?)NullIfEmpty(sampleHash) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$audioStart", analysis.AudioStartSeconds);
            cmd.Parameters.AddWithValue("$audioEnd", analysis.AudioEndSeconds);
            cmd.Parameters.AddWithValue("$beatFirst", analysis.BeatFirstSeconds);
            cmd.Parameters.AddWithValue("$beatLast", analysis.BeatLastSeconds);
            cmd.Parameters.AddWithValue("$bass", PackFloats(analysis.BassCurve));
            cmd.Parameters.AddWithValue("$mid", PackFloats(analysis.MidCurve));
            cmd.Parameters.AddWithValue("$top", PackFloats(analysis.TopCurve));
            cmd.Parameters.AddWithValue("$vocal", PackFloats(analysis.VocalCurve));
            cmd.Parameters.AddWithValue("$phraseBars", analysis.Phrases.Bars);
            cmd.Parameters.AddWithValue("$phraseFirstBar", analysis.Phrases.FirstBar);
            cmd.Parameters.AddWithValue("$phraseConfidence", analysis.Phrases.Confidence);
            cmd.Parameters.AddWithValue("$stability", analysis.TempoStability);
            cmd.Parameters.AddWithValue("$drops", PackDoubles(analysis.DropSeconds));
            cmd.Parameters.AddWithValue("$introLevel", analysis.IntroLevel);
            cmd.Parameters.AddWithValue("$outroLevel", analysis.OutroLevel);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The stored analysis for a track, or null when there is none, when the file has changed since
    /// it was made, or when it was made by an older analyser than the one running now.
    /// </summary>
    /// <param name="absolutePath">Written into the returned analysis as its
    /// <see cref="TrackAnalysis.FilePath"/>. The row itself only knows the relative path.</param>
    public TrackAnalysis? GetTrackAnalysis(string relativePath, long sizeBytes, long modifiedUtcTicks, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT size_bytes, modified_utc_ticks, analyzer_version, duration_seconds, bpm, bpm_confidence,
                       beat_times, first_downbeat, beats_per_bar, camelot, key_confidence, intensity,
                       energy_curve, intro_end_seconds, outro_start_seconds, downbeat_confidence,
                       audio_start_seconds, audio_end_seconds, beat_first_seconds, beat_last_seconds,
                       bass_curve, mid_curve, top_curve, vocal_curve,
                       phrase_bars, phrase_first_bar, phrase_confidence, tempo_stability,
                       drop_seconds, intro_level, outro_level,
                       -- The listener's own answer about how this record is counted, applied on the
                       -- way out rather than stored into the reading. See Counted.
                       COALESCE((SELECT bpm_octave FROM tracks t
                                 WHERE t.relative_path = track_analysis.relative_path), 1)
                FROM track_analysis WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;

            // The file changed, or the analyser did: either way the row is not about this file any
            // more. Size AND timestamp are both compared, because a re-tag can leave one of them unchanged.
            if (reader.GetInt64(0) != sizeBytes || reader.GetInt64(1) != modifiedUtcTicks)
                return null;
            // Compared on the generation and not the raw stamp: a row records whether a model read it
            // as well as which analyser did, and an old generation is stale either way.
            if (!TrackAnalysis.IsCurrent(reader.GetInt32(2)))
                return null;

            return Whole(Counted(ReadAnalysis(reader, absolutePath), reader.GetDouble(31)));
        }
    }

    /// <summary>
    /// The best stored analysis of <em>this audio</em>, wherever it was made and whatever file it was
    /// made from. Null when nothing on this machine has heard it, or when everything that has is out
    /// of date.
    ///
    /// <para>This lookup survives what the path-and-mtime lookup cannot: a file re-tagged by
    /// something other than this app, a library copied to a second disk, or the same rip held twice
    /// under two names. The caller must have decoded the track to be able to ask (see
    /// <see cref="AudioKey"/>). So what it saves is not the decode but the analyser, which is the
    /// expensive half by a factor of about ten once the models are used.</para>
    ///
    /// <para>It returns the best row, not the first: several rows can have one hash, and a modelled
    /// reading beats a DSP one of the same generation. Ordering on the raw stamp does this on its own,
    /// because the modelled marker is added to the stamp.</para>
    /// </summary>
    /// <param name="absolutePath">Written into the returned analysis as its
    /// <see cref="TrackAnalysis.FilePath"/>. The row it came from describes a different file.</param>
    public TrackAnalysis? GetTrackAnalysisBySampleHash(string sampleHash, string absolutePath)
    {
        if (string.IsNullOrEmpty(sampleHash))
            return null;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT size_bytes, modified_utc_ticks, analyzer_version, duration_seconds, bpm, bpm_confidence,
                       beat_times, first_downbeat, beats_per_bar, camelot, key_confidence, intensity,
                       energy_curve, intro_end_seconds, outro_start_seconds, downbeat_confidence,
                       audio_start_seconds, audio_end_seconds, beat_first_seconds, beat_last_seconds,
                       bass_curve, mid_curve, top_curve, vocal_curve,
                       phrase_bars, phrase_first_bar, phrase_confidence, tempo_stability,
                       drop_seconds, intro_level, outro_level,
                       -- The listener's own answer about how this record is counted, applied on the
                       -- way out rather than stored into the reading. See Counted.
                       COALESCE((SELECT bpm_octave FROM tracks t
                                 WHERE t.relative_path = track_analysis.relative_path), 1)
                FROM track_analysis
                WHERE sample_hash = $hash
                  AND analyzer_version % {TrackAnalysis.ModelledOffset} >= {TrackAnalysis.AnalyzerVersion}
                ORDER BY analyzer_version DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$hash", sampleHash);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Whole(Counted(ReadAnalysis(reader, absolutePath), reader.GetDouble(31))) : null;
        }
    }

    /// <summary>
    /// Every name this machine has for one track: the id in its tags, the hash of its tag-stripped
    /// bytes, and the hash of what it decodes to.
    ///
    /// <para>The three live in three tables and are joined here on the path. The path is the local key
    /// that the shared pool tries to avoid, but only a machine that holds the file can make this join.
    /// Once it has made it, the result can be shared once and nobody has to work it out again.</para>
    ///
    /// <para>Nulls are normal. A track that has never been uploaded has no canonical hash. A track
    /// that has never been analysed has no sample hash. A track that the store has not matched a
    /// fingerprint for has no song id. What comes back is what is known.</para>
    ///
    /// <para>The song id is different, and important: the other three name <em>this copy</em> of the
    /// record, and two independent rips of one CD share none of them. The song id names the recording,
    /// so it is the only key that can join them.</para>
    /// </summary>
    public (string? RpmId, string? CanonicalSha, string? SampleHash, long SongId) RecordingKeysFor(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.rpm_id, t.content_hash, a.sample_hash, COALESCE(f.song_id, 0)
                FROM tracks t
                LEFT JOIN track_analysis a ON a.relative_path = t.relative_path
                LEFT JOIN track_fingerprint f ON f.relative_path = t.relative_path
                WHERE t.relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (null, null, null, 0);

            return (
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt64(3));
        }
    }

    /// <summary>
    /// A page of tracks this machine has more than one name for, oldest path first.
    ///
    /// <para>Two names is the minimum. An uploaded track has the hash of its tag-stripped bytes and
    /// the id in its tags without anything ever decoding it. That is most of a library and costs
    /// nothing to say, so the pass that publishes these is a read and not a sweep. Tracks that have
    /// also been analysed bring the third name too.</para>
    /// </summary>
    /// <param name="after">The last path of the previous page, or null to start. It pages on the path
    /// and not on an offset, so that a sync running at the same time cannot make the pass skip a
    /// row.</param>
    public IReadOnlyList<(string RelativePath, string? RpmId, string? CanonicalSha, string? SampleHash, long SongId)>
        TracksWithSeveralNames(string? after, int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.relative_path, t.rpm_id, t.content_hash, a.sample_hash, COALESCE(f.song_id, 0)
                FROM tracks t
                LEFT JOIN track_analysis a ON a.relative_path = t.relative_path
                LEFT JOIN track_fingerprint f ON f.relative_path = t.relative_path
                WHERE t.relative_path > $after
                  AND (CASE WHEN COALESCE(t.rpm_id, '') <> '' THEN 1 ELSE 0 END
                     + CASE WHEN COALESCE(t.content_hash, '') <> '' THEN 1 ELSE 0 END
                     + CASE WHEN COALESCE(a.sample_hash, '') <> '' THEN 1 ELSE 0 END
                     + CASE WHEN COALESCE(f.song_id, 0) > 0 THEN 1 ELSE 0 END) >= 2
                ORDER BY t.relative_path
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$after", after ?? "");
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var rows = new List<(string, string?, string?, string?, long)>();
            while (reader.Read())
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4)));
            return rows;
        }
    }

    /// <summary>
    /// A stored reading, or null when the row is stamped current but is incomplete.
    ///
    /// <para>The stamp cannot be trusted on its own, because not every row was written by the
    /// analyser. A reading adopted from the pool is stamped with the analyser running HERE, whatever
    /// the machine that published it actually measured (this is what makes the facts' own generations
    /// the thing that travels). So a row can be current and still lack something that this generation
    /// would have measured. Left alone it stays that way for the life of the library, because being
    /// current is what stops anything looking at it again.</para>
    ///
    /// <para>A grid with no bands under it is such a row. Returning null re-reads it once, which costs
    /// a decode and repairs it. A reading with no grid at all is not judged: that is a record with no
    /// tempo, which is a finding, and decoding it again on every set to learn the same thing is the
    /// waste this cache exists to avoid.</para>
    /// </summary>
    private static TrackAnalysis? Whole(TrackAnalysis analysis) =>
        analysis.BeatTimes.Count > 0 && !analysis.HasBands ? null : analysis;

    /// <summary>
    /// The reading as the person who owns the record counts it.
    ///
    /// <para>Which octave a tempo is reported at is a matter of counting and not of speed, and
    /// nothing in the audio settles it: see <see cref="Analysis.TempoOctave"/>. So the app measures
    /// one and offers the other. Where the user accepts the offer, the factor is stored on the
    /// track's own row and applied here, on the way out.</para>
    ///
    /// <para>It is applied on the way out and not stored in the analysis row, which is the whole
    /// design. The row is what the analyser measured, and it is thrown away every time the analyser
    /// changes. The factor is what a person said, and it must survive that, a re-tag, and a re-read
    /// from the shared pool. It also stops one listener's opinion reaching everybody else's library:
    /// the corrected reading comes back marked <see cref="TrackAnalysis.IsEdited"/>, which
    /// <see cref="Cloud.AnalysisFacts"/> already refuses to publish.</para>
    /// </summary>
    private static TrackAnalysis Counted(TrackAnalysis analysis, double octave) =>
        octave is > 0 and not 1 && analysis.Bpm > 0
            ? Analysis.GridEdit.Octave(analysis, octave)
            : analysis;

    /// <summary>
    /// Records that this track is counted at <paramref name="octave"/> times the tempo that was
    /// measured: 2, 0.5, or 1 to go back to what the analyser said.
    ///
    /// <para>It is kept on the track and not on its analysis. A tempo is measured again whenever the
    /// file or the analyser changes, and an answer a person gave should not have to be given again
    /// after either.</para>
    /// </summary>
    public void SetBpmOctave(string relativePath, double octave)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (octave <= 0)
            return;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET bpm_octave = $octave WHERE relative_path = $path";
            cmd.Parameters.AddWithValue("$octave", octave);
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How this track is counted against how it was measured. It is 1 where nobody has said
    /// otherwise, which is nearly every row.</summary>
    public double BpmOctave(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT bpm_octave FROM tracks WHERE relative_path = $path";
            cmd.Parameters.AddWithValue("$path", relativePath);
            return Convert.ToDouble(cmd.ExecuteScalar() ?? 1.0, CultureInfo.InvariantCulture) is var o && o > 0
                ? o
                : 1;
        }
    }

    /// <summary>
    /// The musical key of whichever of these tracks the analyser has reached.
    ///
    /// <para>It is read per list and not carried on the session's mood cut, and the difference
    /// matters. The cut is taken once at launch and held, on purpose, so that every list drawn in one
    /// session agrees with every other. <c>track_analysis</c> is different: it fills in during the
    /// whole session, behind whatever is played or queued. A key stored in the cut would be as old as
    /// the launch, while one read here is as current as the analyser has managed so far.</para>
    ///
    /// <para><b>Only keys that are confident enough to write into a file.</b> The limit is
    /// <see cref="AnalysisTags.MinKeyConfidence"/>, the bar the app already applies before it writes a
    /// key into someone's tags. A reading good enough to put in a file is good enough to order a
    /// playlist by, and one that is not good enough should do neither.</para>
    ///
    /// <para>A track with no row, no key, or a key under the limit is simply absent. The ordering
    /// reads that as "not known to fit and not known to clash" and not as a match: see
    /// <c>MoodFlow</c>, where that distinction is the whole of one constant.</para>
    /// </summary>
    /// <param name="paths">The tracks to ask about, relative. Usually the fifty that a list holds.</param>
    public IReadOnlyDictionary<string, MusicalKey> MusicalKeysOf(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var keys = new Dictionary<string, MusicalKey>(StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
            return keys;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // Driven FROM json_each, and this is not a style choice. Written the other way round
            // (FROM track_analysis JOIN json_each(...)), SQLite scans the whole analysis table and
            // parses the path list again for every row: about 10.8 ms a call against 0.030 ms, on a
            // 20,000-track library. The playlist pane makes one of these calls per offered list. This
            // way the fifty paths drive the query, and each is one seek on the table's primary key.
            cmd.CommandText =
                """
                SELECT asked.value, a.camelot
                FROM json_each($paths) asked
                JOIN track_analysis a ON a.relative_path = asked.value
                WHERE a.camelot IS NOT NULL AND a.key_confidence >= $minKeyConfidence
                """;
            cmd.Parameters.AddWithValue("$paths", JsonList(paths));
            cmd.Parameters.AddWithValue("$minKeyConfidence", AnalysisTags.MinKeyConfidence);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = MusicalKey.FromCamelot(reader.GetString(1));
                if (key.IsKnown)
                    keys[reader.GetString(0)] = key;
            }
        }

        return keys;
    }

    /// <summary>One row, as the analysis it describes. The column order is the one that both queries
    /// above select in. They select in it so that this is the only place it is written down.</summary>
    private static TrackAnalysis ReadAnalysis(SqliteDataReader reader, string absolutePath) => new()
    {
        FilePath = absolutePath,
        DurationSeconds = reader.GetDouble(3),
        Bpm = reader.GetDouble(4),
        BpmConfidence = reader.GetDouble(5),
        BeatTimes = UnpackDoubles(reader.IsDBNull(6) ? null : reader.GetString(6)),
        FirstDownbeat = reader.GetInt32(7),
        BeatsPerBar = reader.GetInt32(8),
        Key = MusicalKey.FromCamelot(reader.IsDBNull(9) ? null : reader.GetString(9)),
        KeyConfidence = reader.GetDouble(10),
        Intensity = reader.GetDouble(11),
        EnergyCurve = UnpackFloats(reader.IsDBNull(12) ? null : reader.GetString(12)),
        IntroEndSeconds = reader.GetDouble(13),
        OutroStartSeconds = reader.GetDouble(14),
        DownbeatConfidence = reader.GetDouble(15),
        AudioStartSeconds = reader.GetDouble(16),
        AudioEndSeconds = reader.GetDouble(17),
        BeatFirstSeconds = reader.GetDouble(18),
        BeatLastSeconds = reader.GetDouble(19),
        BassCurve = UnpackFloats(reader.IsDBNull(20) ? null : reader.GetString(20)),
        MidCurve = UnpackFloats(reader.IsDBNull(21) ? null : reader.GetString(21)),
        TopCurve = UnpackFloats(reader.IsDBNull(22) ? null : reader.GetString(22)),
        VocalCurve = UnpackFloats(reader.IsDBNull(23) ? null : reader.GetString(23)),
        Phrases = new PhraseGrid(reader.GetInt32(24), reader.GetInt32(25), reader.GetDouble(26)),
        TempoStability = reader.GetDouble(27),
        DropSeconds = UnpackDoubles(reader.IsDBNull(28) ? null : reader.GetString(28)),
        IntroLevel = reader.GetDouble(29),
        OutroLevel = reader.GetDouble(30),
        Version = reader.GetInt32(2),
    };

    /// <summary>
    /// The analysed tracks whose measurements have not been put into their files' tags yet, oldest
    /// analysis first.
    ///
    /// <para>Only rows that the analyser is sure of are offered: a tempo it could beat-match to, or a
    /// key it is confident about (see <see cref="Analysis.AnalysisTags"/>, whose thresholds these
    /// are). Any other row would cost a file open just to find that there is nothing to write.</para>
    ///
    /// <para>The beat count that the <see cref="TrackAnalysis.CanBeatMatch"/> rule also asks for is
    /// not checked here, because the grid is stored as text and counting it in SQL would mean reading
    /// every one. The caller re-reads the row properly before writing anything, and the full rule is
    /// applied there. This query only avoids offering rows that are clearly hopeless.</para>
    /// </summary>
    /// <param name="overwrite">True to offer every analysed track again, including the ones already
    /// written. This is what the overwrite option of the backfill asks for.</param>
    public IReadOnlyList<AnalysisTagWork> TracksNeedingAnalysisTags(int limit, bool overwrite = false)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT relative_path, size_bytes, modified_utc_ticks
                FROM track_analysis
                WHERE analyzer_version % {TrackAnalysis.ModelledOffset} >= {TrackAnalysis.AnalyzerVersion}
                  AND ($overwrite = 1 OR tags_written_utc_ticks = 0)
                  AND ((bpm > 0 AND bpm_confidence >= $minBpmConfidence)
                       OR (camelot IS NOT NULL AND key_confidence >= $minKeyConfidence))
                ORDER BY analyzed_utc_ticks
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$overwrite", overwrite ? 1 : 0);
            cmd.Parameters.AddWithValue("$minBpmConfidence", TrackAnalysis.MinBeatMatchConfidence);
            cmd.Parameters.AddWithValue("$minKeyConfidence", AnalysisTags.MinKeyConfidence);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var work = new List<AnalysisTagWork>();
            while (reader.Read())
                work.Add(new AnalysisTagWork(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            return work;
        }
    }

    /// <summary>How many analysed tracks are still waiting for their measurements to reach their
    /// files. It is the count behind the settings page's readout, and it uses the same rule as
    /// <see cref="TracksNeedingAnalysisTags"/>.</summary>
    public int CountTracksNeedingAnalysisTags(bool overwrite = false)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COUNT(*) FROM track_analysis
                WHERE analyzer_version % {TrackAnalysis.ModelledOffset} >= {TrackAnalysis.AnalyzerVersion}
                  AND ($overwrite = 1 OR tags_written_utc_ticks = 0)
                  AND ((bpm > 0 AND bpm_confidence >= $minBpmConfidence)
                       OR (camelot IS NOT NULL AND key_confidence >= $minKeyConfidence))
                """;
            cmd.Parameters.AddWithValue("$overwrite", overwrite ? 1 : 0);
            cmd.Parameters.AddWithValue("$minBpmConfidence", TrackAnalysis.MinBeatMatchConfidence);
            cmd.Parameters.AddWithValue("$minKeyConfidence", AnalysisTags.MinKeyConfidence);
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Records that this track's measurements are now in its file, and re-stamps the row with what the
    /// file became.
    ///
    /// <para>The re-stamp is essential. Writing a tag rewrites the file, so its size and mtime
    /// change, and this row is keyed on exactly those. Without the re-stamp, the analysis that took a
    /// full decode to produce would be thrown away as stale by the very write that used it, and the
    /// track would be analysed again on the next set.</para>
    ///
    /// <para>The row is marked as written even when nothing was written. A track whose file already
    /// had both values, or that the analyser had nothing confident to say about, is settled, and
    /// asking again would mean opening it on every pass for ever. In that case pass the file's
    /// unchanged size and mtime.</para>
    /// </summary>
    public void NoteAnalysisTagsWritten(string relativePath, long sizeBytes, long modifiedUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE track_analysis
                SET size_bytes = $size, modified_utc_ticks = $modified, tags_written_utc_ticks = $at
                WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            cmd.Parameters.AddWithValue("$modified", modifiedUtcTicks);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops every stored analysis. Use it when the analyser has changed and the whole
    /// library should be re-read, and not aged out one file at a time.</summary>
    public void ClearTrackAnalyses()
    {
        using (_gate.Take())
            Execute("DELETE FROM track_analysis");
    }

    /// <summary>How many tracks have a current analysis, for a progress readout. The modulo removes
    /// the modelled marker from the stamp: see <see cref="TrackAnalysis.GenerationOf"/>.</summary>
    public int CountTrackAnalyses()
    {
        using (_gate.Take())
            return Convert.ToInt32(Scalar(
                $"SELECT COUNT(*) FROM track_analysis WHERE analyzer_version % {TrackAnalysis.ModelledOffset} >= {TrackAnalysis.AnalyzerVersion}"),
                CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The tracks whose analysis is current but was made without a model. These are the ones to redo
    /// when a model becomes available on a machine that had none when the library was first read.
    /// </summary>
    public IReadOnlyList<string> TracksAnalyzedWithoutModel()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT relative_path FROM track_analysis
                WHERE analyzer_version < {TrackAnalysis.ModelledOffset}
                  AND analyzer_version >= {TrackAnalysis.AnalyzerVersion}
                """;
            using var reader = cmd.ExecuteReader();
            var paths = new List<string>();
            while (reader.Read())
                paths.Add(reader.GetString(0));
            return paths;
        }
    }

    // The two arrays are stored as comma-separated text and not as blobs: a beat grid is a few
    // thousand numbers, SQLite compresses text rows well enough, and a readable column helps a lot
    // when a grid looks wrong.
    private static string PackDoubles(IReadOnlyList<double> values) =>
        string.Join(',', values.Select(v => v.ToString("F4", CultureInfo.InvariantCulture)));

    private static string PackFloats(IReadOnlyList<float> values) =>
        string.Join(',', values.Select(v => v.ToString("F5", CultureInfo.InvariantCulture)));

    private static double[] UnpackDoubles(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
            return [];
        var parts = packed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            values[i] = double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return values;
    }

    private static float[] UnpackFloats(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
            return [];
        var parts = packed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            values[i] = float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return values;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

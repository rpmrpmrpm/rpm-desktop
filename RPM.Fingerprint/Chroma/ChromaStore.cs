using Microsoft.Data.Sqlite;

namespace RPM.Fingerprint.Chroma;

/// <summary>
/// SQLite store for chroma fingerprints. Storage layout is intentionally compact:
///
///   songs:    one row per song, with the FULL fingerprint as a delta-compressed BLOB.
///   fp_index: inverted index for candidate retrieval. It holds only anchors, a few dozen frames per
///             song, so candidates are found without scanning every song.
///   fp_meta:  which key scheme wrote fp_index, so an index written under another one is rewritten
///             on open rather than read and silently matched against nothing.
///
/// <para>It is also an <see cref="IChromaFingerprints"/>: a matcher over this store reads the index
/// and then asks it for the hundred and fifty candidates a lookup actually opens, rather than
/// holding every fingerprint. A SQLite file is already a store that can answer that.</para>
/// </summary>
public sealed class ChromaStore : IChromaFingerprints, IDisposable
{
    private readonly SqliteConnection _conn;

    /// <summary>How many distinctive sub-fingerprints per song to put in the inverted index.</summary>
    public int IndexAnchorsPerSong { get; }

    /// <summary>
    /// How an anchor becomes index keys. Written into the database beside the index, and an index
    /// written under a different scheme is dropped and rebuilt on open rather than read: keys from
    /// two schemes do not collide, so reading one with the other finds no candidates, which reads
    /// exactly like a store that has never heard the recording.
    /// </summary>
    public ChromaKeyScheme Scheme { get; }

    /// <param name="scheme">Defaults to <see cref="ChromaKeySchemes.Default"/>. Taken here rather
    /// than as an init property because opening the store may rewrite the index under it, and an
    /// object initializer runs after the constructor. Set that way, it would rebuild under the
    /// scheme it was replacing.</param>
    public ChromaStore(string dbPath, ChromaKeyScheme? scheme = null, int indexAnchorsPerSong = 60)
    {
        Scheme = scheme ?? ChromaKeySchemes.Default;
        IndexAnchorsPerSong = indexAnchorsPerSong;

        // SQLite does not create parent folders, so create them here, before the connection is opened.
        var parent = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Init();
        RebuildIndexIfSchemeChanged();
    }

    private void Init()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS songs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                title         TEXT    NOT NULL,
                source_path   TEXT,
                duration_sec  REAL,
                fp_length     INTEGER NOT NULL,
                fingerprint   BLOB    NOT NULL,
                added_at      TEXT    NOT NULL
            );
            -- Stage-1 index: each row stores one key derived from one anchor frame → song.
            -- A query derives keys the same way from its own frames and looks them up; matches are
            -- candidates that get the detailed Hamming check downstream. What a key is made of is
            -- ChromaKeyScheme's business, and which scheme wrote these rows is in fp_meta.
            CREATE TABLE IF NOT EXISTS fp_index (
                key        INTEGER NOT NULL,
                song_id    INTEGER NOT NULL,
                position   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_fp_index_lookup ON fp_index(key);
            CREATE TABLE IF NOT EXISTS fp_meta (
                name  TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops and rewrites the whole index when it was written under a different key scheme.
    ///
    /// <para>Rebuilding is cheap because the fingerprints are stored in full: the index is derived
    /// data and can always be made again. Doing it on open is what makes a scheme change a code
    /// change rather than a migration.</para>
    /// </summary>
    private void RebuildIndexIfSchemeChanged()
    {
        string? written;
        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT value FROM fp_meta WHERE name = 'key_scheme';";
            written = read.ExecuteScalar() as string;
        }
        if (written == Scheme.Id.ToString()) return;

        var songs = LoadAllFingerprintsIntoMemory();
        using var tx = _conn.BeginTransaction();
        using (var clear = _conn.CreateCommand())
        {
            clear.CommandText = "DELETE FROM fp_index;";
            clear.ExecuteNonQuery();
        }
        foreach (var (songId, fp) in songs)
            WriteIndexRows(songId, fp);
        using (var stamp = _conn.CreateCommand())
        {
            stamp.CommandText = "INSERT OR REPLACE INTO fp_meta (name, value) VALUES ('key_scheme', $id);";
            stamp.Parameters.AddWithValue("$id", Scheme.Id.ToString());
            stamp.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Files one song's anchors into <c>fp_index</c>. Caller owns the transaction.</summary>
    private void WriteIndexRows(long songId, ChromaFingerprint fp)
    {
        if (fp.Length == 0) return;

        using var icmd = _conn.CreateCommand();
        icmd.CommandText = "INSERT INTO fp_index (key, song_id, position) VALUES ($key, $sid, $pos);";
        var pKey = icmd.Parameters.Add("$key", SqliteType.Integer);
        var pSid = icmd.Parameters.Add("$sid", SqliteType.Integer);
        var pPos = icmd.Parameters.Add("$pos", SqliteType.Integer);
        pSid.Value = songId;

        Span<uint> keys = stackalloc uint[Scheme.KeysPerAnchor];
        foreach (int pos in ChromaKeyScheme.AnchorPositions(fp.SubFingerprints, IndexAnchorsPerSong))
        {
            int count = Scheme.Extract(fp.SubFingerprints, pos, keys);
            for (int k = 0; k < count; k++)
            {
                pKey.Value = (long)keys[k];
                pPos.Value = pos;
                icmd.ExecuteNonQuery();
            }
        }
    }

    public long AddSong(string title, string? sourcePath, double durationSec, ChromaFingerprint fp)
    {
        byte[] blob = fp.Serialize();

        using var tx = _conn.BeginTransaction();

        long songId;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
                INSERT INTO songs (title, source_path, duration_sec, fp_length, fingerprint, added_at)
                VALUES ($title, $path, $dur, $len, $blob, $now);
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$path", (object?)sourcePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dur", durationSec);
            cmd.Parameters.AddWithValue("$len", fp.Length);
            cmd.Parameters.AddWithValue("$blob", blob);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            songId = (long)cmd.ExecuteScalar()!;
        }

        WriteIndexRows(songId, fp);
        tx.Commit();
        return songId;
    }

    public ChromaFingerprint LoadFingerprint(long songId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT fingerprint FROM songs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", songId);
        var blob = (byte[]?)cmd.ExecuteScalar();
        return blob is null ? new ChromaFingerprint(Array.Empty<uint>()) : ChromaFingerprint.Deserialize(blob);
    }

    /// <summary>
    /// Loads every song's fingerprint into memory, decompressed. Not on any lookup path. A match
    /// opens the candidates through <see cref="GetAsync"/> and nothing else. This is here for the
    /// one job that genuinely needs all of them at once: rewriting the index after a key scheme
    /// change, which happens on open and only when the scheme id beside it has moved.
    /// </summary>
    public Dictionary<long, ChromaFingerprint> LoadAllFingerprintsIntoMemory()
    {
        var dict = new Dictionary<long, ChromaFingerprint>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, fingerprint FROM songs;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            long id = r.GetInt64(0);
            byte[] blob = (byte[])r.GetValue(1);
            dict[id] = ChromaFingerprint.Deserialize(blob);
        }
        return dict;
    }

    /// <summary>
    /// Reads the persisted index into a dictionary. This is not what a matcher uses.
    /// <see cref="OpenMatcher"/> streams the same rows into flat arrays at eight bytes an entry, where
    /// this shape costs about forty. It is kept because it states plainly what is on disk, which is
    /// what a test wants to assert about.
    /// </summary>
    public Dictionary<uint, List<(long SongId, int Position)>> LoadIndexIntoMemory()
    {
        var dict = new Dictionary<uint, List<(long, int)>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, song_id, position FROM fp_index;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            uint key = (uint)r.GetInt64(0);
            long sid = r.GetInt64(1);
            int pos = r.GetInt32(2);
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<(long, int)>();
                dict[key] = list;
            }
            list.Add((sid, pos));
        }
        return dict;
    }

    /// <summary>
    /// A matcher over what this store holds: the index as it was written, and this store as the
    /// place stage 2 opens fingerprints. The fingerprints are not read into memory. A SQLite file
    /// is already a store that can be asked for a hundred and fifty rows.
    /// </summary>
    public ChromaMatcher OpenMatcher(bool debugTiming = false)
    {
        var builder = new ChromaMatcher.Builder(Scheme, IndexAnchorsPerSong) { DebugTiming = debugTiming };
        var ordinals = new Dictionary<long, int>();
        foreach (var song in ListSongs())
            ordinals[song.Id] = builder.AddSongWithoutIndexing(song.Id, song.Title);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, song_id, position FROM fp_index;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!ordinals.TryGetValue(r.GetInt64(1), out int ordinal)) continue;
            builder.AddEntry((uint)r.GetInt64(0), ordinal, r.GetInt32(2));
        }
        return builder.Build(this);
    }

    /// <summary>The fingerprints of these recordings, read on demand. See
    /// <see cref="IChromaFingerprints"/> for why they are asked for rather than held.</summary>
    public ValueTask<IReadOnlyDictionary<long, ChromaFingerprint>> GetAsync(
        IReadOnlyList<long> songIds, CancellationToken ct = default)
    {
        var found = new Dictionary<long, ChromaFingerprint>(songIds.Count);
        if (songIds.Count == 0)
            return ValueTask.FromResult<IReadOnlyDictionary<long, ChromaFingerprint>>(found);

        foreach (long id in songIds)
        {
            if (_justStored.TryGetValue(id, out var remembered)) { found[id] = remembered; continue; }
            var fp = LoadFingerprint(id);
            if (fp.Length > 0) found[id] = fp;
        }
        return ValueTask.FromResult<IReadOnlyDictionary<long, ChromaFingerprint>>(found);
    }

    /// <inheritdoc/>
    public void Remember(long songId, ChromaFingerprint fingerprint) => _justStored[songId] = fingerprint;

    /// <summary>Fingerprints filed into a live matcher over this store but not written here. See
    /// <see cref="IChromaFingerprints.Remember"/>.</summary>
    private readonly Dictionary<long, ChromaFingerprint> _justStored = new();

    public string? GetSongTitle(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT title FROM songs WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public List<(long Id, string Title, string? SourcePath, double DurationSec, int FpLength)> ListSongs()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, source_path, COALESCE(duration_sec,0), fp_length FROM songs ORDER BY id;";
        var results = new List<(long, string, string?, double, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDouble(3), r.GetInt32(4)));
        return results;
    }

    /// <summary>Sum of stored fingerprint blob bytes. Useful for the bytes-per-song metric.</summary>
    public long TotalFingerprintBytes()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(LENGTH(fingerprint)), 0) FROM songs;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public int SongCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM songs;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose() => _conn.Dispose();
}

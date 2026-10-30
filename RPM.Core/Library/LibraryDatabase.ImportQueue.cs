using System.Text;
using RPM.Core.Models;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// The DB-backed import queue. It lives in a SQLite <c>TEMP</c> table on this instance's own
/// connection, which gives two benefits:
///   • <b>per-instance isolation</b>: a temp table belongs to the connection, so two app instances
///     that open the SAME library folder each get their own private queue and cannot overwrite one
///     another;
///   • <b>short lifetime</b>: it is dropped when the connection closes, so there is nothing to
///     persist or sweep across restarts.
/// It has the same virtualisation shape as the library grid (<c>folder</c> + <c>file_name</c>, so the
/// skeleton and paged reads work the same way) and an FTS5 companion for word-START search. That is
/// why the queue can hold a million rows and still search instantly. All access goes through the
/// shared <c>_gate</c>, like the rest of the class.
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>A row still needs recognition when its WANTED side lacks artist/title, or its wanted
    /// metadata source is only a filename guess (<see cref="MetadataSource.FilenameGuess"/> = 2) or unknown
    /// (<see cref="MetadataSource.Unknown"/> = 4). Mirrors <c>TrackRow.NeedsRecognition</c>.
    ///
    /// <para>This is what the queue NAGS about, and only that. It is deliberately NOT the import gate
    /// (see <see cref="UnnamedSql"/>), and it is not the "tags missing" chip either. That chip asks
    /// the user's own question (<see cref="QueueTags"/>), while this asks whether anybody has vouched
    /// for what the file says at all. A file named by its own filename is complete but not vouched
    /// for.</para></summary>
    private const string NeedsRecognitionSql =
        "(w_artist IS NULL OR trim(w_artist) = '' OR w_title IS NULL OR trim(w_title) = '' " +
        "OR w_source IN (2, 4))";

    /// <summary>
    /// A row with nothing to file it under: no wanted artist, or no wanted title. The naming template
    /// builds a path from those, so a row without them has no destination and cannot be imported at
    /// all. It is the one metadata reason that a ticked row stays behind.
    ///
    /// <para>A row whose names came from the file name and not from a listing is NOT in here, and
    /// <see cref="NeedsRecognitionSql"/> must not be used for this. Otherwise a track that the chosen
    /// release did not cover (a bonus cut, a hidden track, or the one edit that the pressing left off)
    /// would silently drop out of the importable set while its tick stayed on, and the user would file
    /// eleven of twelve files without noticing. The tick decides what gets imported. A release that
    /// the user picked names what it can and says how many, and the rest goes in unnamed and is
    /// not left behind.</para>
    /// </summary>
    private const string UnnamedSql =
        "(w_artist IS NULL OR trim(w_artist) = '' OR w_title IS NULL OR trim(w_title) = '')";

    /// <summary>
    /// A row whose FILE cannot be filed: a cloud placeholder whose bytes are not on this disk
    /// (<see cref="Models.FileAvailability.Dataless"/> = 1), or one that neither the tag parser nor
    /// ffmpeg could find audio in (<see cref="Models.FileAvailability.Unreadable"/> = 2).
    ///
    /// <para>Both are kept OUT of the importable set, for the same reason seen from opposite ends.
    /// Filing a placeholder would either move a hollow stub onto the shelf or silently pull a whole
    /// collection down the wire, and nobody asked for either. Filing an unreadable file would put a
    /// track in the library that this app has already found it cannot play, where someone would find
    /// it months later by pressing play.</para>
    /// </summary>
    private const string UnfilableSql = "(availability != 0)";

    /// <summary>
    /// A row of the <c>tracks</c> table that this computer holds a FILE for. It reads over the mirror,
    /// not the queue.
    ///
    /// <para>The duplicate pass depends on this distinction. The mirror lists three kinds of track:
    /// the ones whose files are on this disk, the ones in the account's cloud catalogue that this
    /// install has never had (<see cref="Cloud.CloudState.CloudOnly"/>), and the ones borrowed over
    /// the network from another machine. Only the first kind can make a queued file a copy of
    /// something.</para>
    /// </summary>
    private static readonly string HasLocalFileSql =
        $"(cloud_state <> {(int)Cloud.CloudState.CloudOnly} AND peer_adopted = 0)";

    /// <summary>
    /// A row living in the folder where a split kept its original album file. It is visible in the
    /// queue (that is why it has a readable name) but never <i>imported</i>. It is a whole album in
    /// one file, usually fully tagged, so without this rule "import everything" would file a
    /// forty-minute "track" into the library beside the tracks that were cut out of it. Moving the file
    /// out of the folder is how someone says they meant it.
    /// </summary>
    private static readonly string PreservedOriginalSql =
        $"(folder = '{Splitting.AlbumFileSplitter.OriginalsFolderName}' " +
        $"OR folder LIKE '%/{Splitting.AlbumFileSplitter.OriginalsFolderName}' " +
        // The legacy name is migrated on scan; a folder that refused to rename must still not import.
        // Its underscore is a LIKE wildcard, hence the escape.
        @"OR folder = '_originals' OR folder LIKE '%/\_originals' ESCAPE '\')";

    /// <summary>
    /// What a tick may land on. A tick is someone saying "file this", and the album that a split
    /// preserved can never be filed (<see cref="PreservedOriginalSql"/>). So it is not part of the
    /// ticked set at all: not through Select all, not through its record's box, not through its own.
    ///
    /// <para>The gate is applied on every WRITE and not in a sweep afterwards, because a tick that
    /// exists for even one turn is a tick that some action can act on. If the row were only kept out
    /// of what Import FILED, a split album would import cleanly and leave one row behind, ticked,
    /// with the title of whichever track the rip had stamped on the whole-album file. Nothing on
    /// screen would say why it had stayed.</para>
    ///
    /// <para>Only a tick is gated. Unticking reaches every row, always: a gate that also refused to
    /// let go could strand what it had let through.</para>
    /// </summary>
    private static readonly string TickableSql = $"NOT {PreservedOriginalSql}";

    private bool _queueReady;

    /// <summary>
    /// The editable side of a row, as column names: every field that a person, a recognition or a
    /// lookup can put on a queued track. The INSERT, the write-back and the binder are all generated
    /// from this one list, so a field cannot be added to the row and then quietly forgotten by the
    /// store. Works, movements, soloists, sort names, barcodes, MusicBrainz ids and the chosen cover
    /// all depend on this.
    /// </summary>
    private static readonly string[] WantedColumns =
    [
        "w_title", "w_artist", "w_album_artist", "w_album", "w_track", "w_track_count", "w_disc",
        "w_disc_count", "w_disc_subtitle", "w_year", "w_genre", "w_subgenre", "w_composers", "w_conductor",
        "w_comment", "w_lyrics", "w_grouping", "w_bpm", "w_copyright", "w_publisher", "w_remixed_by",
        "w_isrc", "w_source", "w_score",
        "w_release_country", "w_musical_key", "w_advisory",
        "w_title_sort", "w_artist_sort", "w_album_artist_sort", "w_composer_sort",
        "w_work", "w_movement_name", "w_movement", "w_movement_count",
        "w_lyricist", "w_arranger", "w_ensemble", "w_soloists",
        "w_barcode", "w_catalog_number", "w_media", "w_release_type", "w_original_year",
        "w_mb_recording", "w_mb_release", "w_mb_release_group", "w_mb_artist", "w_mb_release_artist",
        "w_tags_edited", "w_art_hash", "w_art_mime",
    ];

    /// <summary>What the scan read from the file. It is immutable for the row's life, and the grid
    /// compares the wanted side against it.</summary>
    private static readonly string[] CurrentColumns =
    [
        "c_extension", "c_duration", "c_channels", "c_sample_rate", "c_bits", "c_bitrate", "c_codec",
        "c_loudness", "c_true_peak",
        "c_title", "c_artist", "c_album_artist", "c_album", "c_track", "c_track_count", "c_disc",
        "c_disc_count", "c_disc_subtitle", "c_year", "c_genre", "c_composers", "c_conductor",
        "c_comment", "c_lyrics", "c_grouping", "c_bpm", "c_copyright", "c_publisher", "c_remixed_by",
        "c_isrc", "c_release_country", "c_rpm_id", "c_has_art", "c_source", "c_score",
        "c_musical_key", "c_advisory",
        "c_title_sort", "c_artist_sort", "c_album_artist_sort", "c_composer_sort",
        "c_work", "c_movement_name", "c_movement", "c_movement_count",
        "c_lyricist", "c_arranger", "c_ensemble", "c_soloists",
        "c_barcode", "c_catalog_number", "c_media", "c_release_type", "c_original_year",
        "c_mb_recording", "c_mb_release", "c_mb_release_group", "c_mb_artist", "c_mb_release_artist",
    ];

    private static readonly string[] RowColumns =
    [
        "path", "source", "folder", "file_name", "is_checked", "split_hint", "split_cue",
        "split_duration", "size_bytes", "availability", "g_artist", "g_album",
        .. CurrentColumns, .. WantedColumns,
    ];

    private static readonly string QueueInsertSql =
        $"INSERT OR IGNORE INTO queue ({string.Join(", ", RowColumns)}) " +
        $"VALUES ({string.Join(", ", RowColumns.Select(c => "$" + c))}) RETURNING id";

    private static readonly string QueueUpdateWantedSql =
        $"UPDATE queue SET is_checked = CASE WHEN {TickableSql} THEN $is_checked ELSE 0 END, " +
        "g_artist = $g_artist, g_album = $g_album, " +
        string.Join(", ", WantedColumns.Select(c => $"{c} = ${c}")) +
        " WHERE id = $id";

    /// <summary>Creates the temp queue table, its FTS5 companion and its indexes, once per connection. It is idempotent.</summary>
    private void EnsureQueueTableLocked()
    {
        if (_queueReady)
            return;

        Execute(
            """
            CREATE TEMP TABLE IF NOT EXISTS queue (
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                source TEXT NOT NULL,
                folder TEXT NOT NULL COLLATE NOCASE,
                file_name TEXT NOT NULL COLLATE NOCASE,
                is_checked INTEGER NOT NULL DEFAULT 0,
                split_hint INTEGER NOT NULL DEFAULT 0,
                split_cue TEXT,
                split_duration REAL NOT NULL DEFAULT 0,
                size_bytes INTEGER NOT NULL DEFAULT 0,
                dup_state INTEGER NOT NULL DEFAULT 0,
                availability INTEGER NOT NULL DEFAULT 0,

                -- Which album intent this row belongs to (0 = none). Stamped on every row of a group
                -- when somebody says something about the RECORD, so the answer follows the rows
                -- through a rename, a merge or a split rather than being keyed by words that change.
                album_intent_id INTEGER NOT NULL DEFAULT 0,

                -- Where this row sits inside its record, when somebody has said. Zero means nobody
                -- has, and the record reads in the order its tags imply — which is every record until
                -- the first row in it is dragged. Keys are sparse (1024 apart) so an ordinary drop
                -- writes ONE row rather than renumbering the record around it.
                --
                -- Not the track number, and deliberately not: arranging a record and numbering it are
                -- two different acts here. A drag says what comes after what; the Track field changes
                -- when somebody asks for it by name.
                sort_order INTEGER NOT NULL DEFAULT 0,

                -- The tree's two group keys, STORED rather than computed per query. They are
                -- derivable from w_album_artist/w_artist/w_album, and deriving them was costing a
                -- full scan of this table every time a group row asked whether it was ticked —
                -- COALESCE(NULLIF(TRIM(...))) is not something an index can be built on. Written on
                -- insert and on every wanted-side update, by GroupKeysOf, which is the only thing
                -- allowed to decide what they are.
                g_artist TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
                g_album TEXT NOT NULL DEFAULT '' COLLATE NOCASE,

                c_extension TEXT NOT NULL, c_duration REAL NOT NULL DEFAULT 0,
                c_channels INTEGER, c_sample_rate INTEGER, c_bits INTEGER, c_bitrate INTEGER,
                c_codec TEXT, c_loudness REAL, c_true_peak REAL,
                c_title TEXT, c_artist TEXT, c_album_artist TEXT, c_album TEXT,
                c_track INTEGER NOT NULL DEFAULT 0, c_track_count INTEGER NOT NULL DEFAULT 0,
                c_disc INTEGER NOT NULL DEFAULT 0, c_disc_count INTEGER NOT NULL DEFAULT 0,
                c_disc_subtitle TEXT, c_year INTEGER NOT NULL DEFAULT 0, c_genre TEXT,
                c_composers TEXT, c_conductor TEXT, c_comment TEXT, c_lyrics TEXT, c_grouping TEXT,
                c_bpm INTEGER NOT NULL DEFAULT 0, c_copyright TEXT, c_publisher TEXT, c_remixed_by TEXT,
                c_isrc TEXT, c_release_country TEXT, c_rpm_id TEXT, c_has_art INTEGER NOT NULL DEFAULT 0,
                c_source INTEGER NOT NULL DEFAULT 0, c_score REAL NOT NULL DEFAULT 0,
                c_musical_key TEXT, c_advisory INTEGER NOT NULL DEFAULT 0,
                c_title_sort TEXT, c_artist_sort TEXT, c_album_artist_sort TEXT, c_composer_sort TEXT,
                c_work TEXT, c_movement_name TEXT,
                c_movement INTEGER NOT NULL DEFAULT 0, c_movement_count INTEGER NOT NULL DEFAULT 0,
                c_lyricist TEXT, c_arranger TEXT, c_ensemble TEXT, c_soloists TEXT,
                c_barcode TEXT, c_catalog_number TEXT, c_media TEXT, c_release_type TEXT,
                c_original_year INTEGER NOT NULL DEFAULT 0,
                c_mb_recording TEXT, c_mb_release TEXT, c_mb_release_group TEXT,
                c_mb_artist TEXT, c_mb_release_artist TEXT,

                w_title TEXT, w_artist TEXT, w_album_artist TEXT, w_album TEXT,
                w_track INTEGER NOT NULL DEFAULT 0, w_track_count INTEGER NOT NULL DEFAULT 0,
                w_disc INTEGER NOT NULL DEFAULT 0, w_disc_count INTEGER NOT NULL DEFAULT 0,
                w_disc_subtitle TEXT, w_year INTEGER NOT NULL DEFAULT 0, w_genre TEXT, w_subgenre TEXT,
                w_composers TEXT, w_conductor TEXT, w_comment TEXT, w_lyrics TEXT, w_grouping TEXT,
                w_bpm INTEGER NOT NULL DEFAULT 0, w_copyright TEXT, w_publisher TEXT, w_remixed_by TEXT,
                w_isrc TEXT, w_source INTEGER NOT NULL DEFAULT 0, w_score REAL NOT NULL DEFAULT 0,

                -- The rest of what a person or a lookup can put on a row. Everything here used to be
                -- carried by the row in memory and thrown away the moment it was written back, so a
                -- work, a soloist, a barcode or a chosen cover survived exactly until the next
                -- rebuild — which an edit triggers itself. See the note on QueueUpdateWanted.
                w_release_country TEXT, w_musical_key TEXT, w_advisory INTEGER NOT NULL DEFAULT 0,
                w_title_sort TEXT, w_artist_sort TEXT, w_album_artist_sort TEXT, w_composer_sort TEXT,
                w_work TEXT, w_movement_name TEXT,
                w_movement INTEGER NOT NULL DEFAULT 0, w_movement_count INTEGER NOT NULL DEFAULT 0,
                w_lyricist TEXT, w_arranger TEXT, w_ensemble TEXT, w_soloists TEXT,
                w_barcode TEXT, w_catalog_number TEXT, w_media TEXT, w_release_type TEXT,
                w_original_year INTEGER NOT NULL DEFAULT 0,
                w_mb_recording TEXT, w_mb_release TEXT, w_mb_release_group TEXT,
                w_mb_artist TEXT, w_mb_release_artist TEXT,
                w_tags_edited INTEGER NOT NULL DEFAULT 0,

                -- The cover somebody chose, by content hash into queue_art (see PendingArtSql).
                w_art_hash TEXT, w_art_mime TEXT
            )
            """);

        // What was said about the RECORD rather than about any of its files. See QueueAlbumIntent:
        // both answers carry whether they were SET, because null means something in each of them.
        Execute(
            """
            CREATE TEMP TABLE IF NOT EXISTS queue_album (
                id INTEGER PRIMARY KEY,
                classical_set INTEGER NOT NULL DEFAULT 0, classical INTEGER,
                normalize_set INTEGER NOT NULL DEFAULT 0, normalize INTEGER,
                disc_hash TEXT,
                -- The pressing this record was settled against, so the tracklist can be taken from it
                -- later without asking again.
                release_provider TEXT, release_id TEXT,
                -- The series the record is one volume of. Set or not, like the two answers above.
                series_set INTEGER NOT NULL DEFAULT 0, series TEXT, series_volume TEXT
            )
            """);

        // Chosen covers are stored ONCE per image and not once per row. A mass edit puts the same
        // picture on every track of a record, and a temp table with two thousand copies of a
        // three-megabyte scan is not a queue, it is a disk-filling accident.
        Execute(
            """
            CREATE TEMP TABLE IF NOT EXISTS queue_art (
                hash TEXT PRIMARY KEY,
                bytes BLOB NOT NULL
            )
            """);
        Execute("CREATE INDEX IF NOT EXISTS temp.idx_queue_folder_name ON queue(folder, file_name)");
        Execute("CREATE INDEX IF NOT EXISTS temp.idx_queue_checked ON queue(is_checked)");
        // The tree reads by group constantly: the skeleton, every page, every tick state. With the
        // keys stored this is a seek. Without them, each of those would scan the whole queue.
        Execute("CREATE INDEX IF NOT EXISTS temp.idx_queue_group ON queue(g_artist, g_album)");
        // FTS5, in the temp schema so it stays per-instance and short-lived. The rowid ties back to
        // queue.id; the single `text` column holds the effective searchable text and is searched
        // with prefix (word-start) queries.
        Execute("CREATE VIRTUAL TABLE IF NOT EXISTS temp.queue_fts USING fts5(text, tokenize = 'unicode61')");

        _queueReady = true;
    }

    /// <summary>Public entry so callers can create the queue up front (before the first insert).</summary>
    /// <summary>
    /// How many times what a queued row SAYS has been rewritten, for the life of this connection.
    ///
    /// <para>A page of rows is read on a worker thread and handed to the grid a moment later, and in
    /// that moment someone can renumber a record. The read is not wrong, it is OLD, and the values it
    /// carries would be written over the edit that has just been saved, on screen only. Nothing would
    /// correct them until the page happened to be read again. So a reader stamps this value on the way
    /// in and looks at it again on the way out: if it has changed, read the page again.</para>
    ///
    /// <para>It is bumped for a rewrite, a tick, an arrangement and a removal, which are the things
    /// that change what a row in hand says. It is NOT bumped for an insert: a scan adds thousands of
    /// rows and changes nothing about any row already read, and a counter that moved for those would
    /// make every page during a scan be read four times.</para>
    /// </summary>
    public long QueueRevision => Interlocked.Read(ref _queueRevision);

    private long _queueRevision;

    /// <summary>Says a queued row has been rewritten. Call it inside the write's own lock, so that a
    /// reader that stamped before the write can never see the new value with the old stamp.</summary>
    private void BumpQueueRevision() => Interlocked.Increment(ref _queueRevision);

    /// <summary>The queue as it is being looked at right now. Every narrowable read and every tick
    /// goes through this, so there is no way to write one that quietly acts on the whole queue.
    /// See <see cref="QueueView"/>.</summary>
    public QueueView Queue(QueueFilter? filter) => new(this, filter);

    /// <summary>The queue with nothing hidden. This is a stated answer, for the callers that mean it:
    /// a clean-up sweep, a test, or the first row for a debug dialog.</summary>
    public QueueView WholeQueue => new(this, QueueFilter.None);

    public void EnsureQueueTable()
    {
        using (_gate.Take())
            EnsureQueueTableLocked();
    }

    // ── writes ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Inserts a batch, de-duplicating by path (a file already queued is skipped, not
    /// duplicated). Returns how many rows were actually added.</summary>
    public int QueueInsertMany(IEnumerable<QueueTrack> rows)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            var added = 0;
            using var tx = _connection.BeginTransaction();
            foreach (var row in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = QueueInsertSql;

                BindQueueRow(cmd, row);
                // RETURNING gives the new id on a real insert, and nothing when the path was already queued
                // (INSERT OR IGNORE). So this both de-duplicates and gives the row's id for the FTS entry.
                var idObj = cmd.ExecuteScalar();
                if (idObj is not null and not DBNull)
                {
                    added++;
                    UpsertFtsLocked(Convert.ToInt64(idObj), BuildFtsText(row));
                }
            }
            tx.Commit();
            return added;
        }
    }

    /// <summary>
    /// Writes the WANTED side (and the tick) of an existing row back to the store, and refreshes its
    /// search text. The immutable current/identity columns are left untouched. It is keyed by id, so a
    /// caller with only a materialised row (its <c>QueueId</c> and wanted side) can write back without
    /// carrying the whole stored row.
    /// </summary>
    /// <returns>
    /// Whether the row's IDENTITY moved: the group keys it is filed under, or the four fields by which
    /// two files are called the same recording (artist, album, disc, title).
    ///
    /// <para>The caller may leave the tree standing when this is false. A year or a genre written
    /// across forty rows changes what those rows SAY, but nothing about where they sit or which files
    /// they are a copy of. Rebuilding the skeleton for that would throw away the reader's place, the
    /// folders they opened and the row they were looking at. Anything that does move a row says so
    /// here.</para>
    /// </returns>
    public bool QueueUpdateWanted(long id, bool isChecked, TrackInfo wanted)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();

            // Read BEFORE the write, in one trip: the identity the row had, plus the current-side
            // searchable values that the update does not touch and the search text is rebuilt from.
            string fileName = "", cArtist = "", cTitle = "", cAlbum = "";
            (string Artist, string Album, long Disc, string Title) was = ("", "", 0, "");
            using (var q = _connection.CreateCommand())
            {
                q.CommandText =
                    "SELECT file_name, c_artist, c_title, c_album, g_artist, g_album, w_disc, w_title " +
                    "FROM queue WHERE id = $id";
                q.Parameters.AddWithValue("$id", id);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    fileName = r.GetString(0);
                    cArtist = r.IsDBNull(1) ? "" : r.GetString(1);
                    cTitle = r.IsDBNull(2) ? "" : r.GetString(2);
                    cAlbum = r.IsDBNull(3) ? "" : r.GetString(3);
                    was = (r.IsDBNull(4) ? "" : r.GetString(4),
                           r.IsDBNull(5) ? "" : r.GetString(5),
                           r.IsDBNull(6) ? 0 : r.GetInt64(6),
                           r.IsDBNull(7) ? "" : r.GetString(7));
                }
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = QueueUpdateWantedSql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$is_checked", isChecked ? 1 : 0);
            BindWantedLocked(cmd, wanted);
            var keys = GroupKeysOf(wanted);
            cmd.Parameters.AddWithValue("$g_artist", keys.Artist);
            cmd.Parameters.AddWithValue("$g_album", keys.Album);
            cmd.ExecuteNonQuery();

            UpsertFtsLocked(id, FtsText(fileName, wanted.Artist, wanted.Title, wanted.Album, cArtist, cTitle, cAlbum));
            BumpQueueRevision();

            return !Same(was.Artist, keys.Artist)
                || !Same(was.Album, keys.Album)
                || was.Disc != wanted.DiscNumber
                || !Same(was.Title, wanted.Title ?? "");
        }
    }

    /// <summary>
    /// How a record's files read, and the only answer to "what comes after what".
    ///
    /// <para><c>sort_order</c> comes first, because a drag is someone SAYING what the order is, and it
    /// beats whatever the tags claim. Then come the tags themselves, and the file name last as the
    /// tie-break that never ties.</para>
    ///
    /// <para>A page of the grid is ordered by this, and so is every snapshot that an action is handed.
    /// There is one order, so "number these 1 to n from the top" means what it says. Numbering in a
    /// different order from the one on screen would throw away an arrangement that someone dragged
    /// into place.</para>
    /// </summary>
    private const string WithinRecordOrder =
        "sort_order, w_disc, CASE WHEN w_track = 0 THEN 1 ELSE 0 END, w_track, " +
        "w_title COLLATE NOCASE, file_name";

    /// <summary>The whole tree in reading order: artist, then record, then the record's own order.</summary>
    private const string TreeOrder =
        "g_artist COLLATE NOCASE, g_album COLLATE NOCASE, " + WithinRecordOrder;

    /// <summary>The comparison the group columns are collated by: case-insensitive, and null is "".</summary>
    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The ticked rows, which are the set every action works on, in the order the tree reads them.
    ///
    /// <para>They are narrowed by <paramref name="filter"/> when one is given. This is a second check
    /// on top of the ticks already being confined to what is on screen: an action must never reach a
    /// row that nobody can see. "Number these 1 to n from the top" over a set with invisible rows in
    /// it gives numbers that skip, with no way to tell from the screen why.</para>
    /// </summary>
    private IReadOnlyList<QueueTrack> QueueCheckedRowsIn(QueueFilter? filter)
        => QueueQuery("WHERE is_checked = 1", filter);

    /// <summary>Ticked rows that can be filed: what Import puts into the library. A tick is the user
    /// saying the file belongs in the collection, so only three things hold one back, and each makes
    /// filing impossible: no name to file it under (<see cref="UnnamedSql"/>), a whole album that a
    /// split preserved (<see cref="PreservedOriginalSql"/>), and a file whose bytes are not here or
    /// hold no audio (<see cref="UnfilableSql"/>).</summary>
    /// <remarks>The result is narrowed like everything else, as a second check. A tick is only ever
    /// supposed to be on a row that the narrowing is showing (see <see cref="QueueUncheckHidden"/>),
    /// so this can only change the answer when something has gone wrong elsewhere. That is exactly
    /// when filing rows that nobody could see would be the worst thing to do.</remarks>
    private IReadOnlyList<QueueTrack> QueueImportableRowsIn(QueueFilter? filter)
        => QueueQuery(
            $"WHERE is_checked = 1 AND NOT {UnnamedSql} AND NOT {PreservedOriginalSql} AND NOT {UnfilableSql}",
            filter);

    private IReadOnlyList<QueueTrack> QueueQuery(string whereClause, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            // The narrowing is AND-ed onto the caller's own clause, and its parameters go on the
            // command that will run it. With no filter, nothing is added.
            var narrow = QueueFilterPredicate(cmd, filter);
            var where = narrow.Length == 0 ? whereClause : $"{whereClause} AND {narrow}";
            cmd.CommandText = $"SELECT * FROM queue {where} ORDER BY {TreeOrder}";
            using var reader = cmd.ExecuteReader();
            var rows = new List<QueueTrack>();
            while (reader.Read())
                rows.Add(ReadQueueTrack(reader));
            return rows;
        }
    }

    /// <summary>A single queue row by id, or null. The split flow needs the album row's source and
    /// folder to file the tracks it produced.</summary>
    public QueueTrack? QueueRowById(long id)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM queue WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadQueueTrack(reader) : null;
        }
    }

    /// <summary>The distinct source folders behind the queued rows. They are used for the header label
    /// and for the empty-folder sweep after a move.</summary>
    public IReadOnlyList<string> QueueDistinctSources()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT source FROM queue ORDER BY source";
            using var reader = cmd.ExecuteReader();
            var sources = new List<string>();
            while (reader.Read())
                sources.Add(reader.GetString(0));
            return sources;
        }
    }

    /// <summary>
    /// What the store says the tick is, for the rows named by <paramref name="ids"/>.
    ///
    /// <para>It takes named rows, so it is not on <see cref="QueueView"/>. The narrowing exists so that
    /// no read can quietly act on the whole queue, and this one says out loud which rows it is about.
    /// A row that the filter is hiding still answers here, which is the point: the caller is holding
    /// it.</para>
    ///
    /// <para>Use it to put the rows in hand back in step with the store after a write that changed
    /// many of them at once. The alternative is to work out from the tree which rows a group tick must
    /// have covered. The tree is rebuilt from a GROUP BY on every scan batch, so that inference is
    /// only true between two rebuilds. This method is true whenever it is asked. It also gives the
    /// RIGHT answer for the rows that a group tick deliberately skips: ticking a group leaves the
    /// album file it was cut out of untouched (see <see cref="TickableSql"/>), which an inference from
    /// the tree cannot know.</para>
    /// </summary>
    public Dictionary<long, bool> QueueCheckedByIds(IReadOnlyList<long> ids)
    {
        var state = new Dictionary<long, bool>(ids.Count);
        if (ids.Count == 0)
            return state;

        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();

            // Bound parameters and not an interpolated list. An id is a number here, but building a
            // query by concatenation is a bad habit, and the next caller may pass a string.
            var names = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                names[i] = $"$i{i}";
                cmd.Parameters.AddWithValue(names[i], ids[i]);
            }

            cmd.CommandText = $"SELECT id, is_checked FROM queue WHERE id IN ({string.Join(", ", names)})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                state[reader.GetInt64(0)] = reader.GetInt32(1) != 0;
            return state;
        }
    }

    /// <summary>
    /// Ticks or unticks every row in a tree group. A group's tick stands for every file under it (the
    /// tree column's leaves are paged, so this cannot walk them in memory). A null
    /// <paramref name="album"/> means the whole artist, albums and all.
    ///
    /// <para>It stays within the narrowing, like every other way of ticking things here. A record's
    /// box says what is UNDER it, and what is under it is what the chips and the search have left
    /// there. A box that also ticked the rows a chip was hiding would build a selection that nobody
    /// could see, and hand it to the next action.</para>
    /// </summary>
    private void QueueSetCheckedInGroupIn(string artist, string? album, bool isChecked, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var narrow = QueueFilterPredicate(cmd, filter);
            cmd.CommandText = $"UPDATE queue SET is_checked = $c WHERE {GroupWhere(cmd, artist, album)}"
                + (narrow.Length == 0 ? "" : $" AND {narrow}")
                + (isChecked ? $" AND {TickableSql}" : "");
            cmd.Parameters.AddWithValue("$c", isChecked ? 1 : 0);
            cmd.ExecuteNonQuery();
            BumpQueueRevision();
        }
    }

    // ── the order somebody put a record in ─────────────────────────────────────────────────────────

    /// <summary>How far apart two neighbours are set when a record is spread out. The gaps are large
    /// on purpose: the room between neighbours is what lets an ordinary drop write one row and not
    /// all of them.</summary>
    private const long OrderStep = 1024;

    /// <summary>
    /// Puts <paramref name="ids"/> between two rows of the same record, keeping the order they are
    /// given in. Null for either neighbour means the start or the end of the record.
    ///
    /// <para>The record's order is MATERIALISED on first use and not before: a queue that nobody has
    /// dragged in has no order at all and reads exactly as its tags say. When a gap runs out, the
    /// record is spread again and the same question is asked of the new keys. That is the only time
    /// that anything but the moved rows is written.</para>
    /// </summary>
    public void QueueReorder(
        string artist, string album, IReadOnlyList<long> ids, long? afterId, long? beforeId)
    {
        if (ids.Count == 0)
            return;

        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            MaterializeOrderLocked(artist, album);

            var (low, high) = GapLocked(afterId, beforeId, ids.Count);

            // Not enough room between the neighbours for what is being put there: spread the record
            // out again and ask the same question of the keys that result.
            if (high - low <= ids.Count)
            {
                SpreadOrderLocked(artist, album);
                (low, high) = GapLocked(afterId, beforeId, ids.Count);
            }

            var step = (high - low) / (ids.Count + 1);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE queue SET sort_order = $order WHERE id = $id";
            var order = cmd.Parameters.Add("$order", SqliteType.Integer);
            var id = cmd.Parameters.Add("$id", SqliteType.Integer);
            for (var i = 0; i < ids.Count; i++)
            {
                order.Value = low + step * (i + 1);
                id.Value = ids[i];
                cmd.ExecuteNonQuery();
            }
            BumpQueueRevision();
        }
    }

    /// <summary>The room between the two rows a drop landed between. With nothing above, the gap
    /// starts at zero. With nothing below, it runs past the last row by as much as the drop needs.</summary>
    private (long Low, long High) GapLocked(long? afterId, long? beforeId, int count)
    {
        var low = afterId is { } a ? OrderOfLocked(a) : 0;
        var high = beforeId is { } b ? OrderOfLocked(b) : low + OrderStep * (count + 1);
        return (low, high);
    }

    /// <summary>Where a row sits in its record's own order. It is zero when the record has never been
    /// arranged, which makes it read in tag order instead.</summary>
    public long QueueOrderOf(long id)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            return OrderOfLocked(id);
        }
    }

    private long OrderOfLocked(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT sort_order FROM queue WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Gives a record an order of its own, once, from the order it currently reads in. A
    /// record that already has one is left alone: working it out again would throw away the
    /// arrangement that this exists to keep.</summary>
    private void MaterializeOrderLocked(string artist, string album)
    {
        using var probe = _connection.CreateCommand();
        probe.CommandText =
            $"SELECT COUNT(*) FROM queue WHERE {GroupWhere(probe, artist, album)} AND sort_order = 0";
        if (Convert.ToInt32(probe.ExecuteScalar() ?? 0) == 0)
            return;
        SpreadOrderLocked(artist, album);
    }

    /// <summary>Writes 1024, 2048 and so on over the record in the order it currently reads, so that
    /// there is room between every pair of neighbours again.</summary>
    private void SpreadOrderLocked(string artist, string album)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"""
            WITH ordered AS (
                SELECT id, ROW_NUMBER() OVER (
                    ORDER BY sort_order,
                             w_disc, CASE WHEN w_track = 0 THEN 1 ELSE 0 END, w_track,
                             w_title COLLATE NOCASE, file_name) AS rn
                FROM queue WHERE {GroupWhere(cmd, artist, album)}
            )
            UPDATE queue
               SET sort_order = (SELECT rn * {OrderStep} FROM ordered WHERE ordered.id = queue.id)
             WHERE id IN (SELECT id FROM ordered)
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Every row under a tree group (an album, or with a null <paramref name="album"/> a whole artist)
    /// in the order the records play. The group's context menu acts on these. Its leaves are paged, so
    /// the rows that nobody has scrolled to are not in memory to be found, and those are most of what
    /// "recognise this record" is about.
    /// </summary>
    private IReadOnlyList<QueueTrack> QueueRowsInGroupIn(string artist, string? album, QueueFilter? filter)
        => QueueGroupRows(artist, album, extra: null, filter);

    /// <summary>The cloud placeholders under a group: the subset that a download can act on.</summary>
    private IReadOnlyList<QueueTrack> QueueDatalessRowsInGroupIn(string artist, string? album, QueueFilter? filter)
        => QueueGroupRows(artist, album, extra: $"availability = {(int)FileAvailability.Dataless}", filter);

    private IReadOnlyList<QueueTrack> QueueGroupRows(
        string artist, string? album, string? extra, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var narrow = QueueFilterPredicate(cmd, filter);
            cmd.CommandText =
                $"""
                SELECT * FROM queue
                WHERE {GroupWhere(cmd, artist, album)}{(extra is null ? "" : " AND " + extra)}
                      {(narrow.Length == 0 ? "" : " AND " + narrow)}
                ORDER BY {TreeOrder}
                """;
            using var reader = cmd.ExecuteReader();
            var rows = new List<QueueTrack>();
            while (reader.Read())
                rows.Add(ReadQueueTrack(reader));
            return rows;
        }
    }

    /// <summary>How many placeholders sit under a group. The menu needs this before the download it
    /// offers has been asked for.</summary>
    public int QueueDatalessCountInGroup(string artist, string? album)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"SELECT COUNT(*) FROM queue WHERE {GroupWhere(cmd, artist, album)} " +
                $"AND availability = {(int)FileAvailability.Dataless}";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>A group's tick tri-state: (rows ticked, rows total) under it. The virtualised tree
    /// cannot count its paged leaves, so the store does, to give true, false or indeterminate. It
    /// counts within the narrowing: the box has to agree with the rows it is standing over, not with
    /// rows that a chip took away.</summary>
    private (int Checked, int Total) QueueGroupCheckStateIn(string artist, string? album, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var narrow = QueueFilterPredicate(cmd, filter);
            cmd.CommandText =
                $"SELECT COALESCE(SUM(CASE WHEN is_checked = 1 AND {TickableSql} THEN 1 ELSE 0 END), 0), "
                + $"SUM(CASE WHEN {TickableSql} THEN 1 ELSE 0 END) "
                + $"FROM queue WHERE {GroupWhere(cmd, artist, album)}"
                + (narrow.Length == 0 ? "" : $" AND {narrow}");
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }
    }

    /// <summary>Ticks or unticks a row. A row that a tick cannot land on (<see cref="TickableSql"/>) is
    /// left as it is.</summary>
    public void QueueSetChecked(long id, bool isChecked)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE queue SET is_checked = $c WHERE id = $id"
                + (isChecked ? $" AND {TickableSql}" : "");
            cmd.Parameters.AddWithValue("$c", isChecked ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            BumpQueueRevision();
        }
    }

    /// <summary>Removes rows by id (and their FTS entries).</summary>
    public void QueueRemove(IEnumerable<long> ids)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var tx = _connection.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM queue WHERE id = $id; DELETE FROM queue_fts WHERE rowid = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            // What was said about a record is removed with the last of its rows. An intent that no
            // music belongs to any more must not be left for the next import to pick up.
            DropOrphanIntentsLocked();
            tx.Commit();
            BumpQueueRevision();
        }
    }

    /// <summary>
    /// Drops every row that is a lesser copy of something: a track that another queued file holds in
    /// equal or better quality, and a track that the library already holds in equal or better quality.
    /// Returns how many rows were removed.
    ///
    /// <para>What stays is one copy of each track (the best one) plus every upgrade. An upgrade is a
    /// duplicate of a library track and is also the only reason to import it. The marks that the grid
    /// already shows decide this, and nothing is judged again here, so what the user saw is exactly
    /// what is removed.</para>
    ///
    /// <para>It removes rows from the QUEUE only. No file on the disk is touched, and adding the
    /// folder again brings them all back.</para>
    /// </summary>
    public int QueueRemoveDuplicates()
    {
        QueueRefreshDuplicates();   // act on what is true now, not on a stale pass
        return QueueDeleteWhere(
            $"dup_state IN ({(int)QueueDuplicate.InQueue}, {(int)QueueDuplicate.InLibrary})");
    }

    /// <summary>Drops the rows that nothing could read: see <see cref="Models.FileAvailability.Unreadable"/>.
    /// Cloud placeholders are left alone, because those are good files that are only away, and there
    /// is an action for fetching them. Returns how many rows were removed.</summary>
    public int QueueRemoveUnreadable()
        => QueueDeleteWhere($"availability = {(int)Models.FileAvailability.Unreadable}");

    /// <summary>How many rows the two clean-ups above would remove, so that the menu can say so and
    /// grey itself out when there is nothing to do.
    ///
    /// <para>It reads the marks as they stand and does not judge again. This number has to agree with
    /// what the user is looking at, and the marks are refreshed on every rebuild of the tree. A count
    /// that quietly knew better than the grid would offer to remove rows that show no duplicate
    /// mark.</para></summary>
    public int QueueDuplicateCount() => QueueScalar(
        $"SELECT COUNT(*) FROM queue WHERE dup_state IN " +
        $"({(int)QueueDuplicate.InQueue}, {(int)QueueDuplicate.InLibrary})");

    public int QueueUnreadableCount() => QueueScalar(
        $"SELECT COUNT(*) FROM queue WHERE availability = {(int)Models.FileAvailability.Unreadable}");

    /// <summary>Deletes the rows matching a predicate, and their FTS entries with them. A row whose
    /// search text outlives it would be a hit on a track that is no longer in the queue.</summary>
    private int QueueDeleteWhere(string predicate)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var tx = _connection.BeginTransaction();

            using var fts = _connection.CreateCommand();
            fts.CommandText = $"DELETE FROM queue_fts WHERE rowid IN (SELECT id FROM queue WHERE {predicate})";
            fts.ExecuteNonQuery();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM queue WHERE {predicate}";
            var removed = cmd.ExecuteNonQuery();

            DropOrphanIntentsLocked();
            tx.Commit();
            BumpQueueRevision();
            return removed;
        }
    }

    /// <summary>Ticks or unticks every row the filter is SHOWING. "Select all" means the rows in
    /// front of you, as it does in any list. A null filter means the whole queue.</summary>
    private void QueueCheckAllIn(bool isChecked, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var narrowed = QueueFilterPredicate(cmd, filter);
            var where = string.Join(
                " AND ",
                new[] { narrowed, isChecked ? TickableSql : "" }.Where(c => c.Length > 0));
            cmd.CommandText = "UPDATE queue SET is_checked = $c"
                + (where.Length > 0 ? $" WHERE {where}" : "");
            cmd.Parameters.AddWithValue("$c", isChecked ? 1 : 0);
            cmd.ExecuteNonQuery();
            BumpQueueRevision();
        }
    }

    /// <summary>
    /// Unticks every row the filter is not showing. Returns how many were unticked.
    ///
    /// <para>This keeps the ticked set honest: it is always a subset of what is on screen, so no
    /// action and no import can run on a row that someone cannot see. Narrowing the queue therefore
    /// gives up the ticks that went out of sight with it. This is the price of "Select all" and
    /// "Select none" meaning the rows in front of you.</para>
    ///
    /// <para>The ticked-only chip is left OUT of the test on purpose: it says "show me what is
    /// ticked", so unticking by it would empty the very view it is drawing, one row at a time.</para>
    /// </summary>
    private int QueueUncheckHiddenIn(QueueFilter? filter)
    {
        var f = (filter ?? QueueFilter.None) with { TickedOnly = false };
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var where = QueueFilterPredicate(cmd, f);
            if (where.Length == 0)
                return 0;       // nothing is hidden, so nothing to give up

            cmd.CommandText = $"UPDATE queue SET is_checked = 0 WHERE is_checked = 1 AND NOT ({where})";
            var given = cmd.ExecuteNonQuery();
            BumpQueueRevision();
            return given;
        }
    }

    /// <summary>Empties the queue.</summary>
    public void QueueClear()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            Execute("DELETE FROM queue");
            Execute("DELETE FROM queue_fts");
            Execute("DELETE FROM queue_art");   // the covers belonged to those rows and to nothing else
            Execute("DELETE FROM queue_album");
            BumpQueueRevision();
        }
    }

    // ── reads ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tree skeleton: one (artist, album, count) per group the queue holds, optionally
    /// narrowed by a search.
    ///
    /// <para>The queue is grouped in the way the LIBRARY will show these files once they are filed:
    /// by what the tags say, not by the folders they happen to be in. Its whole job is to preview the
    /// shelf, and a source folder is the one thing about a file that importing throws away. Rows
    /// that are missing an artist or an album group under an empty key, and the tree labels those
    /// "Unknown".</para>
    /// </summary>
    private IReadOnlyList<(string Artist, string Album, int TrackCount)> QueueGroupCountsIn(QueueFilter? filter)
    {
        var groups = QueueGroupsIn(filter);
        var counts = new List<(string, string, int)>(groups.Count);
        foreach (var g in groups)
            counts.Add((g.Artist, g.Album, g.TrackCount));
        return counts;
    }

    /// <summary>
    /// The whole skeleton in one read: every group, its track count, and how many of those are
    /// ticked. The tree needs all three together. Asking separately would mean a second pass over the
    /// queue for the counts and then a third, per row, for the ticks.
    /// </summary>
    private IReadOnlyList<QueueGroup> QueueGroupsIn(QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT g_artist, g_album, COUNT(*),
                       COALESCE(SUM(CASE WHEN is_checked = 1 AND {TickableSql} THEN 1 ELSE 0 END), 0),
                       SUM(CASE WHEN {TickableSql} THEN 1 ELSE 0 END),
                       COALESCE(MAX(w_disc), 0), COALESCE(MAX(album_intent_id), 0)
                FROM queue
                {WhereClause(QueueFilterPredicate(cmd, filter))}
                GROUP BY g_artist COLLATE NOCASE, g_album COLLATE NOCASE
                ORDER BY g_artist COLLATE NOCASE, g_album COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<QueueGroup>();
            while (reader.Read())
                results.Add(new QueueGroup(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6)));
            return results;
        }
    }

    /// <summary>
    /// Every group's tick state in ONE read: (artist, album, ticked, total) per album group.
    ///
    /// <para>The obvious shape would be for each group row to ask the store whether it is ticked. That
    /// is one query per visible row, and the tree asks constantly: on realise, on scroll, and again up
    /// the ancestor chain every time a leaf is ticked. At twenty thousand rows that costs about two
    /// milliseconds of SQL per group row, which is a scan of the whole queue to colour one checkbox.
    /// One GROUP BY answers the same question for every group at once, and the skeleton carries the
    /// answers.</para>
    /// </summary>
    private IReadOnlyList<(string Artist, string Album, int Checked, int Total)> QueueGroupCheckStatesIn(
        QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT g_artist, g_album,
                       COALESCE(SUM(CASE WHEN is_checked = 1 AND {TickableSql} THEN 1 ELSE 0 END), 0),
                       SUM(CASE WHEN {TickableSql} THEN 1 ELSE 0 END)
                FROM queue
                {WhereClause(QueueFilterPredicate(cmd, filter))}
                GROUP BY g_artist COLLATE NOCASE, g_album COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<(string, string, int, int)>();
            while (reader.Read())
                results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            return results;
        }
    }

    /// <summary>One window of an album group's rows, in the order the record plays. It is the page
    /// fetch behind the virtualised queue grid. Rows with no track number go to the end of the disc,
    /// by title.</summary>
    private IReadOnlyList<QueueTrack> QueueGroupPageIn(
        string artist, string album, int offset, int limit, QueueFilter? filter)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            var match = QueueFilterPredicate(cmd, filter);
            cmd.CommandText =
                $"""
                SELECT * FROM queue
                WHERE {GroupWhere(cmd, artist, album)}{(match.Length == 0 ? "" : " AND " + match)}
                ORDER BY {WithinRecordOrder}
                LIMIT $limit OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var reader = cmd.ExecuteReader();
            var results = new List<QueueTrack>();
            while (reader.Read())
                results.Add(ReadQueueTrack(reader));
            return results;
        }
    }

    // ── grouping / duplicate identity (SQL fragments, so the queue and the mirror cannot drift) ─────

    /// <summary>The stored group keys. See the column comments: they are written and not computed,
    /// so every read by group is an index seek.</summary>
    private const string GroupArtistSql = "g_artist";
    private const string GroupAlbumSql = "g_album";

    /// <summary>
    /// The artist and album a row files under: album artist first, then artist, then nothing; and the
    /// album as tagged. Empty is a real key (the "Unknown" buckets), and the WORDS for those belong to
    /// the UI. The store only knows they are empty.
    ///
    /// <para>This is the one place that decides this. Insert and update both come through here, so a
    /// row can never be filed under a key that a later read would compute differently.</para>
    /// </summary>
    private static (string Artist, string Album) GroupKeysOf(TrackInfo wanted)
        => QueueGroupKeys(wanted.AlbumArtist, wanted.Artist, wanted.Album);

    /// <summary>The same rule, for a caller that holds the three fields and not a
    /// <see cref="TrackInfo"/>, for example the toolbar asking which groups the ticked rows are in. It
    /// is public so that the question is answered here and not guessed a second time elsewhere.</summary>
    public static (string Artist, string Album) QueueGroupKeys(string? albumArtist, string? artist, string? album)
    {
        var key = (albumArtist ?? "").Trim();
        if (key.Length == 0)
            key = (artist ?? "").Trim();
        return (key, (album ?? "").Trim());
    }

    /// <summary>A WHERE fragment that selects one tree group. A null <paramref name="album"/> means the
    /// whole artist. It is case-insensitive, so one artist spelled two ways is one node and pages as one.</summary>
    private static string GroupWhere(SqliteCommand cmd, string artist, string? album)
    {
        cmd.Parameters.AddWithValue("$g_artist", artist);
        if (album is null)
            return $"{GroupArtistSql} = $g_artist COLLATE NOCASE";
        cmd.Parameters.AddWithValue("$g_album", album);
        return $"{GroupArtistSql} = $g_artist COLLATE NOCASE AND {GroupAlbumSql} = $g_album COLLATE NOCASE";
    }

    /// <summary>
    /// What two files share when they hold the same recording: artist, album, DISC and title, folded to
    /// lower case. It needs a TITLE and at least one of artist and album. A row that nobody has named
    /// yet is not a duplicate of every other unnamed row, but a title plus the record it is on
    /// identifies a track well enough without the artist field being filled in.
    ///
    /// <para>The disc is in the key because a set is one album with the same track titles twice: an
    /// "Intro" on disc 2 is not the "Intro" on disc 1. Without the disc, the duplicate pass would mark
    /// a whole second disc as a lesser copy of the first, and the clean-up would delete it in one
    /// press with no warning. Discs that nobody numbered are 0 on both sides, so an ordinary album is
    /// not affected.</para>
    ///
    /// <para>The key is empty, which matches nothing, when that much is not known.
    /// <see cref="FileKeySql"/> takes over there.</para>
    /// </summary>
    private static string DupKeySql(string artist, string album, string title, string disc) =>
        $"CASE WHEN TRIM(COALESCE({title}, '')) = '' " +
        $"OR (TRIM(COALESCE({artist}, '')) = '' AND TRIM(COALESCE({album}, '')) = '') THEN '' " +
        $"ELSE LOWER(TRIM(COALESCE({artist}, ''))) || CHAR(31) || LOWER(TRIM(COALESCE({album}, ''))) " +
        $"|| CHAR(31) || CAST(COALESCE({disc}, 0) AS TEXT) " +
        $"|| CHAR(31) || LOWER(TRIM(COALESCE({title}, ''))) END";

    /// <summary>
    /// The other way two rows can be the same track: they are the same FILE. Same name, same byte
    /// count, same length.
    ///
    /// <para>This catches the case that the metadata key cannot see at all: a collection imported
    /// into a music app years ago and then imported again from the loose copy it was made from. Both
    /// are untagged, so neither has an identity worth the name, and they sit in the queue as two rows
    /// with the same file name and nothing to say that they are one record played twice.</para>
    ///
    /// <para>All three parts must agree, which is what makes it safe. Two different tracks called
    /// <c>01 Intro.mp3</c> are common, but two that also have exactly the same size in bytes and the
    /// same number of seconds are no coincidence. The key is empty when the size is unknown, because
    /// then only the name is left and a name on its own proves nothing.</para>
    /// </summary>
    private static string FileKeySql(string fileName, string size, string duration) =>
        $"CASE WHEN COALESCE({size}, 0) <= 0 OR TRIM(COALESCE({fileName}, '')) = '' THEN '' " +
        $"ELSE LOWER(TRIM({fileName})) || CHAR(31) || CAST({size} AS TEXT) " +
        $"|| CHAR(31) || CAST(CAST(COALESCE({duration}, 0) AS INTEGER) AS TEXT) END";

    /// <summary>The identity that a row is actually matched on: what it says it is, or if that is not
    /// known, what file it is. It is one column, so a row can never match another through two
    /// different ideas of sameness at once.</summary>
    private static string IdentitySql(string meta, string file)
        => $"CASE WHEN ({meta}) <> '' THEN ({meta}) ELSE ({file}) END";

    /// <summary>
    /// How good an encode is, as one number to sort on: lossless above every lossy file whatever its
    /// bitrate, then resolution within lossless (bits × kHz) and bitrate within lossy. This is the
    /// same judgement that <see cref="Metadata.AudioFormat.Quality"/> puts into words, given as a
    /// number so that SQL can rank two copies of a track without reading either of them.
    /// </summary>
    private static string QualitySql(string ext, string codec, string bits, string rate, string kbps) =>
        $"(CASE WHEN {LosslessSql(ext, codec)} " +
        $"THEN 10000000 + COALESCE({bits}, 0) * (COALESCE({rate}, 0) / 1000) " +
        $"ELSE COALESCE({kbps}, 0) END)";

    /// <summary>Which files keep every sample. The extension answers for all but the MPEG-4 family,
    /// where only the name the decoder gave the stream tells Apple Lossless from AAC.</summary>
    private static string LosslessSql(string ext, string codec) =>
        $"(LOWER(COALESCE({ext}, '')) IN ('.flac', '.wav', '.aif', '.aiff', '.aifc', '.ape', '.wv', " +
        $"'.dsf', '.dff', '.alac') " +
        $"OR (LOWER(COALESCE({ext}, '')) IN ('.m4a', '.m4b', '.mp4') " +
        $"AND LOWER(COALESCE({codec}, '')) LIKE '%alac%'))";

    /// <summary>
    /// Marks every queued row that is a copy of something (of a track the library already holds, or
    /// of another row in this same queue) and which way the quality falls (see
    /// <see cref="QueueDuplicate"/>). It is one pass over the queue and one grouped read of the
    /// mirror. Run it after anything that changes what the rows claim to be (a scan, recognition, a
    /// mass edit).
    /// </summary>
    public void QueueRefreshDuplicates()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();

            var queueKey = IdentitySql(
                DupKeySql("w_artist", "w_album", "w_title", "w_disc"),
                FileKeySql("file_name", "size_bytes", "c_duration"));
            var queueQuality = QualitySql("c_extension", "c_codec", "c_bits", "c_sample_rate", "c_bitrate");
            var libKey = IdentitySql(
                DupKeySql("artist", "album", "title", "disc_number"),
                FileKeySql("file_name", "size_bytes", "duration_seconds"));
            var libQuality = QualitySql("extension", "codec_description", "bits_per_sample",
                                        "sample_rate", "bitrate_kbps");

            using var tx = _connection.BeginTransaction();
            Execute("UPDATE queue SET dup_state = 0");

            // (1) Against the library. A copy already on the shelf makes this one a duplicate, unless
            // this file is the better encode, which makes it the reason to import: an upgrade.
            //
            // The album that a split preserved sits this out, on both counts (see PreservedOriginalSql).
            // It is not a copy of a track. It is the file the tracks were cut out of, and it carries
            // the tags of whichever track the rip stamped on it, so by tags alone a forty-minute record
            // would read as a duplicate of a three-minute song. If it were marked, "Remove duplicates"
            // would sweep away the one file that a bad split has to be redone from.
            Execute(
                $"""
                UPDATE queue SET dup_state =
                    CASE WHEN {queueQuality} > lib.best
                         THEN {(int)QueueDuplicate.Upgrade} ELSE {(int)QueueDuplicate.InLibrary} END
                FROM (SELECT {libKey} AS k, MAX({libQuality}) AS best FROM tracks
                      WHERE {HasLocalFileSql} GROUP BY k) AS lib
                WHERE lib.k <> '' AND lib.k = {queueKey} AND NOT {PreservedOriginalSql}
                """);

            // (2) Against the rest of the queue: the best copy of each track keeps its own state, and
            // every other copy is a duplicate of it. A row that the library already holds is left as it
            // is, because that is the worse news and the one the user has to act on.
            //
            // The preserved album is left out of the ranking entirely, and not only spared its own
            // verdict. It is the biggest file carrying those tags by a factor of ten, so it would win
            // the partition on size and mark the track that came out of it as the lesser copy. One
            // press of "Remove duplicates" would then remove the cut track and leave the uncut record.
            Execute(
                $"""
                UPDATE queue SET dup_state = {(int)QueueDuplicate.InQueue}
                FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY k ORDER BY q DESC, sz DESC, id) AS rn
                    FROM (SELECT id, {queueKey} AS k, {queueQuality} AS q, size_bytes AS sz FROM queue
                          WHERE NOT {PreservedOriginalSql})
                    WHERE k <> ''
                ) AS dup
                WHERE dup.id = queue.id AND dup.rn > 1
                  AND queue.dup_state <> {(int)QueueDuplicate.InLibrary}
                """);

            // (3) Against the cloud records: the collection lists the track but has no file for it
            // on this computer. This is not a copy of anything on this disk (importing it is the one
            // thing that puts a file there), so it is its own state, which neither the duplicates
            // filter nor "Remove duplicates" names.
            //
            // It must not fall into (1). Otherwise an account whose catalogue comes down into a
            // library with no files yet would make that library claim every record in the account, and
            // the first import would call most of the listener's own music duplicates of tracks this
            // machine never held. One press of "Remove duplicates" would leave only what can stream.
            //
            // This is the last of the three, and only over rows still unmarked: a track in the cloud
            // that is also in the queue twice is reported as the in-queue duplicate, because the second
            // local copy is redundant whatever the cloud holds.
            Execute(
                $"""
                UPDATE queue SET dup_state = {(int)QueueDuplicate.InCloud}
                FROM (SELECT {libKey} AS k FROM tracks WHERE NOT {HasLocalFileSql} GROUP BY k) AS cloud
                WHERE cloud.k <> '' AND cloud.k = {queueKey} AND NOT {PreservedOriginalSql}
                  AND queue.dup_state = {(int)QueueDuplicate.None}
                """);

            tx.Commit();
            BumpQueueRevision();
        }
    }

    /// <summary>
    /// Where a row sits inside its own group: its position in exactly the order
    /// <see cref="QueueGroupPage"/> hands rows out, or -1 when the row is gone.
    ///
    /// <para>This makes a moved row findable. Recognition re-files a track under the artist it turned
    /// out to be, which in a twenty-thousand-row tree means the row leaves the screen and, as far as
    /// the user can tell, stops existing. With the group keys and this offset, the grid can say where
    /// it went and take the user there.</para>
    ///
    /// <para>It is a full pass on purpose: it is asked once after a recognition and not per row, and a
    /// stored position would have to be maintained by every edit.</para>
    /// </summary>
    public int QueueRowOffsetInGroup(long id)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT rn FROM (
                    SELECT id, ROW_NUMBER() OVER (
                        PARTITION BY g_artist COLLATE NOCASE, g_album COLLATE NOCASE
                        ORDER BY sort_order,
                         w_disc, CASE WHEN w_track = 0 THEN 1 ELSE 0 END, w_track,
                                 w_title COLLATE NOCASE, file_name) - 1 AS rn
                    FROM queue
                ) WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            var v = cmd.ExecuteScalar();
            return v is null or DBNull ? -1 : Convert.ToInt32(v);
        }
    }

    /// <summary>The group a row files under now: the pair the tree is keyed by.</summary>
    public (string Artist, string Album)? QueueRowGroup(long id)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT g_artist, g_album FROM queue WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
        }
    }

    // ── aggregates (the queue's derived counts, in SQL instead of a LINQ pass over every row) ──────

    public int QueueCount() => QueueScalar("SELECT COUNT(*) FROM queue");

    public int QueueCheckedCount() => QueueScalar("SELECT COUNT(*) FROM queue WHERE is_checked = 1");

    public int QueueNeedsRecognitionCount()
        => QueueScalar($"SELECT COUNT(*) FROM queue WHERE {NeedsRecognitionSql} AND NOT {PreservedOriginalSql} AND NOT {UnfilableSql}");

    public int QueueImportableCount()
        => QueueScalar(
            $"SELECT COUNT(*) FROM queue WHERE is_checked = 1 AND NOT {UnnamedSql} AND NOT {PreservedOriginalSql} AND NOT {UnfilableSql}");

    public int QueueSplitCandidateCount()
        => QueueScalar("SELECT COUNT(*) FROM queue WHERE split_hint != 0");

    /// <summary>
    /// What the TICKED set is made of: the numbers that the toolbar's keys switch on.
    ///
    /// <para>It is separate from <see cref="QueueChipCounts"/> because it asks the opposite question.
    /// The chips report on the whole queue, and a key reports on what it would act on. "Download" over
    /// a selection with nothing in the cloud is a key that can only do nothing, which a button that
    /// looks active must never do.</para>
    /// </summary>
    public (int Ticked, int Dataless) QueueTickedFacts()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COUNT(*),
                       SUM(CASE WHEN availability = {(int)Models.FileAvailability.Dataless}
                                THEN 1 ELSE 0 END)
                FROM queue WHERE is_checked = 1
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (0, 0);
            int At(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            return (At(0), At(1));
        }
    }

    /// <summary>
    /// Every chip's count in one read.
    ///
    /// <para>A chip counts the WHOLE queue, not what is currently shown. A chip that counted only the
    /// rows the other chips had left visible would drop to nothing as soon as it was pressed, which
    /// looks like a broken button and not like a list that has been narrowed.</para>
    /// </summary>
    public QueueChipCounts QueueChipCounts()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT COUNT(*),
                       COALESCE(SUM(is_checked), 0),
                       SUM(CASE WHEN {QueueTags.MissingSql()} AND NOT {PreservedOriginalSql}
                                     AND NOT {UnfilableSql} THEN 1 ELSE 0 END),
                       SUM(CASE WHEN dup_state IN ({(int)QueueDuplicate.InQueue},
                                                   {(int)QueueDuplicate.InLibrary}) THEN 1 ELSE 0 END),
                       SUM(CASE WHEN availability = {(int)Models.FileAvailability.Unreadable}
                                THEN 1 ELSE 0 END),
                       SUM(CASE WHEN split_hint != 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN availability = {(int)Models.FileAvailability.Dataless}
                                THEN 1 ELSE 0 END),
                       SUM(CASE WHEN dup_state = {(int)QueueDuplicate.InCloud} THEN 1 ELSE 0 END),
                       COALESCE(SUM(size_bytes), 0),
                       COALESCE(SUM(c_duration), 0)
                FROM queue
                """;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return default;
            int At(int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);
            return new QueueChipCounts(
                At(0), At(1), At(2), At(3), At(4), At(5), At(6), At(7),
                reader.IsDBNull(8) ? 0 : reader.GetInt64(8),
                reader.IsDBNull(9) ? 0 : reader.GetDouble(9));
        }
    }

    /// <summary>The file kinds actually in the queue, for the bar's kind list. They are in sort order,
    /// so the list does not change order between two scans of the same folder.</summary>
    public IReadOnlyList<string> QueueDistinctExtensions()
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT DISTINCT c_extension FROM queue WHERE c_extension <> '' ORDER BY c_extension";
            using var reader = cmd.ExecuteReader();
            var results = new List<string>();
            while (reader.Read())
                results.Add(reader.GetString(0));
            return results;
        }
    }

    private int QueueScalar(string sql)
    {
        using (_gate.Take())
        {
            EnsureQueueTableLocked();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    // ── search ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The WHERE body for a filtered read of the queue: the FTS word-start match for the search text
    /// AND one clause per chip that is on, or an empty string when nothing is narrowed. Adds whatever
    /// parameters it needs to <paramref name="cmd"/>.
    ///
    /// <para>Every clause here is one that this file already names somewhere else. A chip that had its
    /// own idea of "not identified" would be a second answer to a question already answered, and the
    /// two would drift apart the first time either changed.</para>
    /// </summary>
    private static string QueueFilterPredicate(SqliteCommand cmd, QueueFilter? filter)
    {
        var f = filter ?? QueueFilter.None;
        var parts = new List<string>();

        if (ToPrefixMatch(f.Search) is { } match)
        {
            cmd.Parameters.AddWithValue("$match", match);
            parts.Add("id IN (SELECT rowid FROM queue_fts WHERE queue_fts MATCH $match)");
        }

        if (f.TickedOnly)
            parts.Add("is_checked = 1");
        if (f.NeedsRecognition)
            parts.Add($"({QueueTags.MissingSql()} AND NOT {PreservedOriginalSql} AND NOT {UnfilableSql})");
        // This is the only chip that takes rows away and does not keep them. Its predicate is the one
        // that QueueRemoveDuplicates deletes by, inverted. Whatever it hides, that clean-up would
        // remove, and a chip that promised otherwise would be the more dangerous of the two.
        if (f.HideDuplicates)
            parts.Add($"dup_state NOT IN ({(int)QueueDuplicate.InQueue}, {(int)QueueDuplicate.InLibrary})");
        if (f.Unreadable)
            parts.Add($"availability = {(int)Models.FileAvailability.Unreadable}");
        if (f.NoData)
            parts.Add($"availability = {(int)Models.FileAvailability.Dataless}");
        if (f.SplitCandidates)
            parts.Add("split_hint != 0");
        if (f.Source is { } source)
        {
            cmd.Parameters.AddWithValue("$fsource", (int)source);
            parts.Add("c_source = $fsource");
        }
        if (f.Extension is { Length: > 0 } ext)
        {
            cmd.Parameters.AddWithValue("$fext", ext);
            parts.Add("c_extension = $fext COLLATE NOCASE");
        }

        return string.Join(" AND ", parts);
    }

    /// <summary>A predicate as a WHERE clause, or nothing at all when there is no predicate.</summary>
    private static string WhereClause(string predicate) => predicate.Length == 0 ? "" : "WHERE " + predicate;

    /// <summary>Turns free text into an FTS5 prefix query: each word becomes a <c>word*</c> term (matches
    /// tokens that BEGIN with it), AND-ed together. Non-alphanumeric noise is stripped so the query can
    /// never be malformed. Returns null when nothing usable remains (so there is no filter).</summary>
    internal static string? ToPrefixMatch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return null;

        var terms = new List<string>();
        foreach (var raw in search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw)
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
            if (sb.Length > 0)
                terms.Add(sb + "*");
        }
        return terms.Count == 0 ? null : string.Join(' ', terms);
    }

    private void UpsertFtsLocked(long rowId, string text)
    {
        using var del = _connection.CreateCommand();
        del.CommandText = "DELETE FROM queue_fts WHERE rowid = $id";
        del.Parameters.AddWithValue("$id", rowId);
        del.ExecuteNonQuery();

        using var ins = _connection.CreateCommand();
        ins.CommandText = "INSERT INTO queue_fts (rowid, text) VALUES ($id, $text)";
        ins.Parameters.AddWithValue("$id", rowId);
        ins.Parameters.AddWithValue("$text", text);
        ins.ExecuteNonQuery();
    }

    /// <summary>The searchable text for a row: the file name plus the wanted AND current
    /// artist, title and album, so a search hits whichever the user is looking at.</summary>
    private static string BuildFtsText(QueueTrack row)
        => FtsText(row.FileName, row.Wanted.Artist, row.Wanted.Title, row.Wanted.Album,
                   row.Current.Artist, row.Current.Title, row.Current.Album);

    private static string FtsText(string fileName, params string?[] fields)
        => string.Join(' ', fields.Prepend(fileName).Where(s => !string.IsNullOrWhiteSpace(s)));

    // ── row binding / reading ─────────────────────────────────────────────────────────────────────

    private void BindQueueRow(SqliteCommand cmd, QueueTrack row)
    {
        var c = row.Current;
        cmd.Parameters.AddWithValue("$path", row.Path);
        cmd.Parameters.AddWithValue("$source", row.Source);
        cmd.Parameters.AddWithValue("$folder", row.Folder);
        cmd.Parameters.AddWithValue("$file_name", row.FileName);
        cmd.Parameters.AddWithValue("$is_checked", row.IsChecked ? 1 : 0);
        cmd.Parameters.AddWithValue("$split_hint", row.SplitHint);
        cmd.Parameters.AddWithValue("$split_cue", (object?)row.SplitCue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$split_duration", row.SplitDuration);
        cmd.Parameters.AddWithValue("$size_bytes", row.SizeBytes);
        cmd.Parameters.AddWithValue("$availability", (int)c.Availability);
        var keys = GroupKeysOf(row.Wanted);
        cmd.Parameters.AddWithValue("$g_artist", keys.Artist);
        cmd.Parameters.AddWithValue("$g_album", keys.Album);

        cmd.Parameters.AddWithValue("$c_extension", c.Extension);
        cmd.Parameters.AddWithValue("$c_duration", c.DurationSeconds);
        cmd.Parameters.AddWithValue("$c_channels", (object?)c.Channels ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c_sample_rate", (object?)c.SampleRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c_bits", (object?)c.BitsPerSample ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c_bitrate", (object?)c.BitrateKbps ?? DBNull.Value);
        Text(cmd, "$c_codec", c.CodecDescription);
        cmd.Parameters.AddWithValue("$c_track", c.TrackNumber);
        cmd.Parameters.AddWithValue("$c_track_count", c.TrackCount);
        cmd.Parameters.AddWithValue("$c_disc", c.DiscNumber);
        cmd.Parameters.AddWithValue("$c_disc_count", c.DiscCount);
        cmd.Parameters.AddWithValue("$c_year", c.Year);
        cmd.Parameters.AddWithValue("$c_bpm", c.Bpm);
        cmd.Parameters.AddWithValue("$c_has_art", c.HasArt ? 1 : 0);
        cmd.Parameters.AddWithValue("$c_source", (int)c.Source);
        cmd.Parameters.AddWithValue("$c_score", c.IdentificationScore);
        Text(cmd, "$c_title", c.Title);
        Text(cmd, "$c_artist", c.Artist);
        Text(cmd, "$c_album_artist", c.AlbumArtist);
        Text(cmd, "$c_album", c.Album);
        Text(cmd, "$c_disc_subtitle", c.DiscSubtitle);
        Text(cmd, "$c_genre", c.Genre);
        Text(cmd, "$c_composers", c.Composers);
        Text(cmd, "$c_conductor", c.Conductor);
        Text(cmd, "$c_comment", c.Comment);
        Text(cmd, "$c_lyrics", c.Lyrics);
        Text(cmd, "$c_grouping", c.Grouping);
        Text(cmd, "$c_copyright", c.Copyright);
        Text(cmd, "$c_publisher", c.Publisher);
        Text(cmd, "$c_remixed_by", c.RemixedBy);
        Text(cmd, "$c_isrc", c.Isrc);
        Text(cmd, "$c_release_country", c.ReleaseCountry);
        Text(cmd, "$c_rpm_id", c.RpmId);
        cmd.Parameters.AddWithValue("$c_loudness", (object?)c.LoudnessLufs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c_true_peak", (object?)c.TruePeak ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c_advisory", (int)c.Advisory);
        cmd.Parameters.AddWithValue("$c_movement", c.MovementNumber);
        cmd.Parameters.AddWithValue("$c_movement_count", c.MovementCount);
        cmd.Parameters.AddWithValue("$c_original_year", c.OriginalYear);
        Text(cmd, "$c_musical_key", c.MusicalKey);
        Text(cmd, "$c_title_sort", c.TitleSort);
        Text(cmd, "$c_artist_sort", c.ArtistSort);
        Text(cmd, "$c_album_artist_sort", c.AlbumArtistSort);
        Text(cmd, "$c_composer_sort", c.ComposerSort);
        Text(cmd, "$c_work", c.Work);
        Text(cmd, "$c_movement_name", c.MovementName);
        Text(cmd, "$c_lyricist", c.Lyricist);
        Text(cmd, "$c_arranger", c.Arranger);
        Text(cmd, "$c_ensemble", c.Ensemble);
        Text(cmd, "$c_soloists", c.Soloists);
        Text(cmd, "$c_barcode", c.Barcode);
        Text(cmd, "$c_catalog_number", c.CatalogNumber);
        Text(cmd, "$c_media", c.Media);
        Text(cmd, "$c_release_type", c.ReleaseType is { } cType ? ReleaseTypes.ToStored(cType) : null);
        Text(cmd, "$c_mb_recording", c.MusicBrainzRecordingId);
        Text(cmd, "$c_mb_release", c.MusicBrainzReleaseId);
        Text(cmd, "$c_mb_release_group", c.MusicBrainzReleaseGroupId);
        Text(cmd, "$c_mb_artist", c.MusicBrainzArtistId);
        Text(cmd, "$c_mb_release_artist", c.MusicBrainzReleaseArtistId);

        BindWantedLocked(cmd, row.Wanted);
    }

    /// <summary>
    /// Binds every editable field of a row, the chosen cover included. Called under <c>_gate</c> with
    /// the queue table already created: storing a cover writes a second table, which is why this is not
    /// static like the rest of the binders.
    /// </summary>
    private void BindWantedLocked(SqliteCommand cmd, TrackInfo w)
    {
        Text(cmd, "$w_art_hash", StoreArtLocked(w.PendingArt));
        Text(cmd, "$w_art_mime", w.PendingArtMime);
        cmd.Parameters.AddWithValue("$w_tags_edited", w.TagsEdited ? 1 : 0);
        cmd.Parameters.AddWithValue("$w_advisory", (int)w.Advisory);
        cmd.Parameters.AddWithValue("$w_movement", w.MovementNumber);
        cmd.Parameters.AddWithValue("$w_movement_count", w.MovementCount);
        cmd.Parameters.AddWithValue("$w_original_year", w.OriginalYear);
        Text(cmd, "$w_release_country", w.ReleaseCountry);
        Text(cmd, "$w_musical_key", w.MusicalKey);
        Text(cmd, "$w_title_sort", w.TitleSort);
        Text(cmd, "$w_artist_sort", w.ArtistSort);
        Text(cmd, "$w_album_artist_sort", w.AlbumArtistSort);
        Text(cmd, "$w_composer_sort", w.ComposerSort);
        Text(cmd, "$w_work", w.Work);
        Text(cmd, "$w_movement_name", w.MovementName);
        Text(cmd, "$w_lyricist", w.Lyricist);
        Text(cmd, "$w_arranger", w.Arranger);
        Text(cmd, "$w_ensemble", w.Ensemble);
        Text(cmd, "$w_soloists", w.Soloists);
        Text(cmd, "$w_barcode", w.Barcode);
        Text(cmd, "$w_catalog_number", w.CatalogNumber);
        Text(cmd, "$w_media", w.Media);
        Text(cmd, "$w_release_type", w.ReleaseType is { } wType ? ReleaseTypes.ToStored(wType) : null);
        Text(cmd, "$w_mb_recording", w.MusicBrainzRecordingId);
        Text(cmd, "$w_mb_release", w.MusicBrainzReleaseId);
        Text(cmd, "$w_mb_release_group", w.MusicBrainzReleaseGroupId);
        Text(cmd, "$w_mb_artist", w.MusicBrainzArtistId);
        Text(cmd, "$w_mb_release_artist", w.MusicBrainzReleaseArtistId);

        cmd.Parameters.AddWithValue("$w_track", w.TrackNumber);
        cmd.Parameters.AddWithValue("$w_track_count", w.TrackCount);
        cmd.Parameters.AddWithValue("$w_disc", w.DiscNumber);
        cmd.Parameters.AddWithValue("$w_disc_count", w.DiscCount);
        cmd.Parameters.AddWithValue("$w_year", w.Year);
        cmd.Parameters.AddWithValue("$w_bpm", w.Bpm);
        cmd.Parameters.AddWithValue("$w_source", (int)w.Source);
        cmd.Parameters.AddWithValue("$w_score", w.IdentificationScore);
        Text(cmd, "$w_title", w.Title);
        Text(cmd, "$w_artist", w.Artist);
        Text(cmd, "$w_album_artist", w.AlbumArtist);
        Text(cmd, "$w_album", w.Album);
        Text(cmd, "$w_disc_subtitle", w.DiscSubtitle);
        Text(cmd, "$w_genre", w.Genre);
        Text(cmd, "$w_subgenre", w.Subgenre);
        Text(cmd, "$w_composers", w.Composers);
        Text(cmd, "$w_conductor", w.Conductor);
        Text(cmd, "$w_comment", w.Comment);
        Text(cmd, "$w_lyrics", w.Lyrics);
        Text(cmd, "$w_grouping", w.Grouping);
        Text(cmd, "$w_copyright", w.Copyright);
        Text(cmd, "$w_publisher", w.Publisher);
        Text(cmd, "$w_remixed_by", w.RemixedBy);
        Text(cmd, "$w_isrc", w.Isrc);
    }

    private static void Text(SqliteCommand cmd, string name, string? value)
        => cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    /// <summary>The marker a row has when the edit was "take the cover OFF". This is an empty pending
    /// art, which is a decision and must survive a rebuild exactly as a chosen picture does.</summary>
    private const string ArtRemovedHash = "-";

    /// <summary>
    /// Puts a chosen cover in the art table and returns its key, or null when the row has no pending
    /// art at all.
    ///
    /// <para>It is keyed by content, so the same picture put on all twenty tracks of a record is
    /// stored once. A queue can hold twenty thousand rows and a scan is three megabytes. A copy per
    /// row would be sixty gigabytes of temp file to remember one decision.</para>
    /// </summary>
    private string? StoreArtLocked(byte[]? art)
    {
        if (art is null)
            return null;
        if (art.Length == 0)
            return ArtRemovedHash;

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(art));
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO queue_art (hash, bytes) VALUES ($hash, $bytes)";
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$bytes", art);
        cmd.ExecuteNonQuery();
        return hash;
    }

    /// <summary>The cover behind a row's key: the bytes, an empty array for "remove the cover", or
    /// null when the row never had one.</summary>
    private byte[]? LoadArtLocked(string? hash)
    {
        if (hash is null)
            return null;
        if (hash == ArtRemovedHash)
            return [];

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT bytes FROM queue_art WHERE hash = $hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return cmd.ExecuteScalar() as byte[];
    }

    private QueueTrack ReadQueueTrack(SqliteDataReader r)
    {
        string path = Str(r, "path")!;
        var current = new TrackInfo
        {
            FilePath = path,
            Extension = Str(r, "c_extension")!,
            DurationSeconds = Dbl(r, "c_duration"),
            Channels = NullInt(r, "c_channels"),
            SampleRate = NullInt(r, "c_sample_rate"),
            BitsPerSample = NullInt(r, "c_bits"),
            BitrateKbps = NullInt(r, "c_bitrate"),
            CodecDescription = Str(r, "c_codec"),
            Title = Str(r, "c_title"),
            Artist = Str(r, "c_artist"),
            AlbumArtist = Str(r, "c_album_artist"),
            Album = Str(r, "c_album"),
            TrackNumber = U(r, "c_track"),
            TrackCount = U(r, "c_track_count"),
            DiscNumber = U(r, "c_disc"),
            DiscCount = U(r, "c_disc_count"),
            DiscSubtitle = Str(r, "c_disc_subtitle"),
            Year = U(r, "c_year"),
            Genre = Str(r, "c_genre"),
            Composers = Str(r, "c_composers"),
            Conductor = Str(r, "c_conductor"),
            Comment = Str(r, "c_comment"),
            Lyrics = Str(r, "c_lyrics"),
            Grouping = Str(r, "c_grouping"),
            Bpm = U(r, "c_bpm"),
            Copyright = Str(r, "c_copyright"),
            Publisher = Str(r, "c_publisher"),
            RemixedBy = Str(r, "c_remixed_by"),
            Isrc = Str(r, "c_isrc"),
            ReleaseCountry = Str(r, "c_release_country"),
            RpmId = Str(r, "c_rpm_id"),
            HasArt = Int(r, "c_has_art") != 0,
            Availability = (FileAvailability)Int(r, "availability"),
            Source = (MetadataSource)Int(r, "c_source"),
            IdentificationScore = Dbl(r, "c_score"),
            LoudnessLufs = NullDbl(r, "c_loudness"),
            TruePeak = NullDbl(r, "c_true_peak"),
            MusicalKey = Str(r, "c_musical_key"),
            Advisory = (ContentAdvisory)Int(r, "c_advisory"),
            TitleSort = Str(r, "c_title_sort"),
            ArtistSort = Str(r, "c_artist_sort"),
            AlbumArtistSort = Str(r, "c_album_artist_sort"),
            ComposerSort = Str(r, "c_composer_sort"),
            Work = Str(r, "c_work"),
            MovementName = Str(r, "c_movement_name"),
            MovementNumber = U(r, "c_movement"),
            MovementCount = U(r, "c_movement_count"),
            Lyricist = Str(r, "c_lyricist"),
            Arranger = Str(r, "c_arranger"),
            Ensemble = Str(r, "c_ensemble"),
            Soloists = Str(r, "c_soloists"),
            Barcode = Str(r, "c_barcode"),
            CatalogNumber = Str(r, "c_catalog_number"),
            Media = Str(r, "c_media"),
            ReleaseType = ReleaseTypes.FromStored(Str(r, "c_release_type")),
            OriginalYear = U(r, "c_original_year"),
            MusicBrainzRecordingId = Str(r, "c_mb_recording"),
            MusicBrainzReleaseId = Str(r, "c_mb_release"),
            MusicBrainzReleaseGroupId = Str(r, "c_mb_release_group"),
            MusicBrainzArtistId = Str(r, "c_mb_artist"),
            MusicBrainzReleaseArtistId = Str(r, "c_mb_release_artist"),
        };

        // Wanted keeps current's immutable file facts. Only the editable fields and the source and score differ.
        var wanted = current with
        {
            Title = Str(r, "w_title"),
            Artist = Str(r, "w_artist"),
            AlbumArtist = Str(r, "w_album_artist"),
            Album = Str(r, "w_album"),
            TrackNumber = U(r, "w_track"),
            TrackCount = U(r, "w_track_count"),
            DiscNumber = U(r, "w_disc"),
            DiscCount = U(r, "w_disc_count"),
            DiscSubtitle = Str(r, "w_disc_subtitle"),
            Year = U(r, "w_year"),
            Genre = Str(r, "w_genre"),
            Subgenre = Str(r, "w_subgenre"),
            Composers = Str(r, "w_composers"),
            Conductor = Str(r, "w_conductor"),
            Comment = Str(r, "w_comment"),
            Lyrics = Str(r, "w_lyrics"),
            Grouping = Str(r, "w_grouping"),
            Bpm = U(r, "w_bpm"),
            Copyright = Str(r, "w_copyright"),
            Publisher = Str(r, "w_publisher"),
            RemixedBy = Str(r, "w_remixed_by"),
            Isrc = Str(r, "w_isrc"),
            Source = (MetadataSource)Int(r, "w_source"),
            IdentificationScore = Dbl(r, "w_score"),
            ReleaseCountry = Str(r, "w_release_country"),
            MusicalKey = Str(r, "w_musical_key"),
            Advisory = (ContentAdvisory)Int(r, "w_advisory"),
            TitleSort = Str(r, "w_title_sort"),
            ArtistSort = Str(r, "w_artist_sort"),
            AlbumArtistSort = Str(r, "w_album_artist_sort"),
            ComposerSort = Str(r, "w_composer_sort"),
            Work = Str(r, "w_work"),
            MovementName = Str(r, "w_movement_name"),
            MovementNumber = U(r, "w_movement"),
            MovementCount = U(r, "w_movement_count"),
            Lyricist = Str(r, "w_lyricist"),
            Arranger = Str(r, "w_arranger"),
            Ensemble = Str(r, "w_ensemble"),
            Soloists = Str(r, "w_soloists"),
            Barcode = Str(r, "w_barcode"),
            CatalogNumber = Str(r, "w_catalog_number"),
            Media = Str(r, "w_media"),
            ReleaseType = ReleaseTypes.FromStored(Str(r, "w_release_type")),
            OriginalYear = U(r, "w_original_year"),
            MusicBrainzRecordingId = Str(r, "w_mb_recording"),
            MusicBrainzReleaseId = Str(r, "w_mb_release"),
            MusicBrainzReleaseGroupId = Str(r, "w_mb_release_group"),
            MusicBrainzArtistId = Str(r, "w_mb_artist"),
            MusicBrainzReleaseArtistId = Str(r, "w_mb_release_artist"),
            TagsEdited = Int(r, "w_tags_edited") != 0,
            PendingArt = LoadArtLocked(Str(r, "w_art_hash")),
            PendingArtMime = Str(r, "w_art_mime"),
        };

        return new QueueTrack
        {
            Id = Int64(r, "id"),
            Source = Str(r, "source")!,
            Folder = Str(r, "folder")!,
            FileName = Str(r, "file_name")!,
            IsChecked = Int(r, "is_checked") != 0,
            SplitHint = Int(r, "split_hint"),
            SplitCue = Str(r, "split_cue"),
            SplitDuration = Dbl(r, "split_duration"),
            SizeBytes = Int64(r, "size_bytes"),
            Duplicate = (QueueDuplicate)Int(r, "dup_state"),
            AlbumIntentId = Int64(r, "album_intent_id"),
            Current = current,
            Wanted = wanted,
        };
    }

    private static string? Str(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static double? NullDbl(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }

    private static int? NullInt(SqliteDataReader r, string name)
    {
        var i = r.GetOrdinal(name);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    private static int Int(SqliteDataReader r, string name) => r.GetInt32(r.GetOrdinal(name));
    private static long Int64(SqliteDataReader r, string name) => r.GetInt64(r.GetOrdinal(name));
    private static double Dbl(SqliteDataReader r, string name) => r.GetDouble(r.GetOrdinal(name));
    private static uint U(SqliteDataReader r, string name) => (uint)r.GetInt64(r.GetOrdinal(name));
}

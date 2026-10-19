using Microsoft.Data.Sqlite;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Core.Library;

/// <summary>One record still to be asked about, and what the catalogue has to be asked with.</summary>
/// <param name="Folder">The album, which is what this app means by a record and what a lookup is
/// keyed by.</param>
/// <param name="Artist">Who it is filed under: the album artist, falling back to the track credit. This
/// is the same rule the naming template uses to build the folder.</param>
/// <param name="Album">Its title.</param>
/// <param name="Tracks">How many songs are on it here. This tells a fuzzy release search that the match
/// is the same pressing, not a single or a boxed set.</param>
/// <param name="ReleaseMbid">The MusicBrainz release this album is already linked to, or null when nobody
/// has linked it. There is no default. The alternative is a fuzzy search per album across the whole
/// library, ranked by a Lucene score over the entire index. The id is already in the row that the pass
/// reads. It is stated at every call site, so a new call site has to decide rather than inherit
/// "search for it".</param>
public readonly record struct RecordToAsk(
    string Folder, string Artist, string Album, int Tracks, string? ReleaseMbid);

/// <summary>
/// How much of the world plays each song. The decade lists are ranked by this figure.
///
/// <para><b>Stored, not fetched and thrown away.</b> The album page asks ListenBrainz which song on a
/// record the world plays most, and it keeps only the winner's title. The same three requests return a
/// count for every track on the record. Those counts are the only data in the app that tells a hit from
/// an album track. A list of "the most played songs of the seventies" cannot be built from numbers that
/// were never stored.</para>
///
/// <para><b>Null is not zero, and the column is nullable for that reason alone.</b> Zero means the
/// world was asked and does not play this song. Null means nobody has asked yet. If the two were merged,
/// a library that had never fetched anything would report that nothing in it was ever popular. The
/// sweep would also have no way to tell what it had already done. The same distinction is drawn by
/// <c>PopularTrackAnswer</c> between "no favourite" and "not asked", for the same reason.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Writes what the world plays of one record's songs, matched to the rows by disc and track number.
    ///
    /// <para><b>By track number, because that is what both sides agree on.</b> The catalogue spells a
    /// title its own way. A match on the words drops the tracks it is least sure about, which on a famous
    /// record are the famous ones. See <c>TrackPopularity.PlaysByPlace</c>.</para>
    ///
    /// <para><b>And by disc.</b> A row with no disc number (zero) is treated as disc one. Without the
    /// disc, track 3 on both discs would take the same figure.</para>
    ///
    /// <para><b>Every track on the record is stamped, including the ones the catalogue said nothing
    /// about.</b> Those are written as zero. The record has been asked about. Leaving them null would send
    /// the sweep back to the same album forever. A song the world does not play and a song the catalogue
    /// has no row for give the same answer to the question this column asks, "how popular is it". A
    /// listener cannot see a difference between them.</para>
    /// </summary>
    /// <param name="folder">The record.</param>
    /// <param name="listensByPlace">What the world plays, by disc and position on the record. Places the
    /// catalogue said nothing about are absent.</param>
    /// <returns>How many rows were stamped. That is every track the mirror holds for that folder.</returns>
    public int SaveWorldPlays(string folder, IReadOnlyDictionary<TrackPlace, long> listensByPlace)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(listensByPlace);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            // Zero over the whole record first, so a track the catalogue never mentioned is marked as
            // asked. Then the tracks it did mention are overwritten. There are two statements rather than
            // one per track. A record has about a dozen rows, and a transaction is one disk write either way.
            using (var asked = _connection.CreateCommand())
            {
                asked.CommandText = "UPDATE tracks SET world_plays = 0 WHERE folder = $folder";
                asked.Parameters.AddWithValue("$folder", folder);
                asked.ExecuteNonQuery();
            }

            var stamped = 0;
            using (var write = _connection.CreateCommand())
            {
                write.CommandText =
                    "UPDATE tracks SET world_plays = $plays " +
                    "WHERE folder = $folder AND track_number = $at AND MAX(disc_number, 1) = $disc";
                var plays = write.Parameters.Add("$plays", SqliteType.Integer);
                write.Parameters.AddWithValue("$folder", folder);
                var at = write.Parameters.Add("$at", SqliteType.Integer);
                var disc = write.Parameters.Add("$disc", SqliteType.Integer);

                foreach (var (place, listens) in listensByPlace)
                {
                    plays.Value = listens;
                    at.Value = place.Position;
                    disc.Value = place.Disc;
                    stamped += write.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return stamped;
        }
    }

    /// <summary>One song of a decade's chart: where it is, and which record it is on.</summary>
    public sealed record ChartSong(string RelativePath, string Folder);

    /// <summary>
    /// The songs of one decade the world plays, best first.
    ///
    /// <para><b>Every one of them, not the top fifty.</b> The list stops at fifty. But the turn-taking
    /// that decides WHICH fifty (see <see cref="RecordTurns"/>) must see the whole decade first. If the
    /// list were cut to fifty and then spread out, the answer would be the same album with its songs
    /// re-ordered. That is the problem this avoids.</para>
    ///
    /// <para><c>world_plays &gt; 0</c> rather than IS NOT NULL. Zero is a song the world was asked about
    /// and does not play. Null is a song nobody has asked about yet. Neither is a chart entry. See
    /// <see cref="SaveWorldPlays"/>.</para>
    /// </summary>
    public IReadOnlyList<ChartSong> QueryDecadeChart(int decade)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT relative_path, folder
                FROM tracks
                WHERE year >= $from AND year < $until AND world_plays > 0
                ORDER BY world_plays DESC, relative_path ASC
                """;
            cmd.Parameters.AddWithValue("$from", decade);
            cmd.Parameters.AddWithValue("$until", decade + 10);

            using var reader = cmd.ExecuteReader();
            var songs = new List<ChartSong>();
            while (reader.Read())
                songs.Add(new ChartSong(reader.GetString(0), reader.GetString(1)));
            return songs;
        }
    }

    /// <summary>
    /// The songs of one genre the world plays most, best first.
    ///
    /// <para><b>What the world plays, not what this listener plays.</b> A genre's page opens on a kind of
    /// music, not on a history. The honest answer to "what are the popular tracks here" does not depend
    /// on whether the app has been used yet. These numbers come from <see cref="SaveWorldPlays"/>. The
    /// listener's own plays are on the same page, in the generated lists beside this.</para>
    ///
    /// <para><b>One song per record.</b> The problem that <see cref="QueryDecadeChart"/> solves with
    /// turn-taking is worse here, because the list is eight rows, not fifty. A shelf where one album is
    /// the world's favourite rock would be eight songs from that album, which says nothing about the
    /// genre. The limit is applied in SQL, not after, so the eight rows come back as eight records.</para>
    ///
    /// <para>The folders are passed in rather than looked up, because which of the app's genres a record
    /// belongs to is decided in code. See <see cref="FoldersOnGenre"/>, which callers should pass.</para>
    /// </summary>
    /// <param name="folders">The album folders on the genre. An empty list gives nothing, which is right
    /// for a genre this collection holds none of.</param>
    /// <param name="limit">How many songs to bring back.</param>
    public IReadOnlyList<LibraryTrack> QueryPopularUnder(IReadOnlyList<string> folders, int limit)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (folders.Count == 0 || limit <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();

            // The window function keeps the best row per folder. The outer query then ranks those rows
            // against each other. If it were done the other way round, SQLite would sort the whole genre
            // before dropping anything.
            cmd.CommandText =
                """
                SELECT * FROM (
                    SELECT tracks.*,
                           ROW_NUMBER() OVER (
                               PARTITION BY tracks.folder
                               ORDER BY tracks.world_plays DESC, tracks.relative_path ASC) AS best
                    FROM tracks
                    JOIN json_each($folders) shelf ON shelf.value = tracks.folder
                    WHERE tracks.world_plays > 0
                )
                WHERE best = 1
                ORDER BY world_plays DESC, relative_path ASC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue(
                "$folders", JsonList(folders));
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var songs = new List<LibraryTrack>();
            while (reader.Read())
                songs.Add(ReadTrack(reader));
            return songs;
        }
    }

    /// <summary>
    /// The next records nobody has asked the world about, biggest first.
    ///
    /// <para>Biggest first, because a lookup costs the same three requests whatever is on the record. So
    /// the sweep gets the most rows per request by starting with the fullest albums. A twelve-track album
    /// is more likely to be a record somebody owns, while a one-track folder is usually a stray file.</para>
    ///
    /// <para>A record counts as unasked while ANY of its tracks is null. <see cref="SaveWorldPlays"/>
    /// stamps every row on the record. So a folder that has been asked about has no nulls left, and it
    /// cannot come back. This stops the sweep walking the same album forever.</para>
    ///
    /// <para>Only records with a title and an artist. The catalogue is searched by both. A folder with
    /// neither is a lookup that cannot be made, so it is skipped rather than treated as a failure.</para>
    /// </summary>
    public IReadOnlyList<RecordToAsk> NextRecordsWithoutWorldPlays(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.folder,
                       MIN(NULLIF(COALESCE(NULLIF(t.album_artist, ''), t.artist), '')) AS act,
                       MIN(NULLIF(t.album, '')) AS title,
                       COUNT(*) AS songs,
                       -- The release this album is linked to, and only when it was linked through the
                       -- RPM API: those ids ARE MusicBrainz's. A Discogs link is a real link to the
                       -- wrong catalogue here, and handing it over as an MBID would look up a stranger.
                       MIN(CASE WHEN m.release_provider = $provider THEN NULLIF(m.release_id, '') END) AS mbid
                FROM tracks t
                LEFT JOIN album_meta m ON m.folder = t.folder
                WHERE t.world_plays IS NULL
                GROUP BY t.folder
                HAVING act IS NOT NULL AND title IS NOT NULL
                ORDER BY songs DESC, t.folder
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));
            cmd.Parameters.AddWithValue("$provider", RpmMetadataProvider.ProviderId);

            using var reader = cmd.ExecuteReader();
            var records = new List<RecordToAsk>();
            while (reader.Read())
            {
                // Checked here rather than trusted to the HAVING clause above. Reading a null as a
                // string throws. A row can reach this point with no act or no title, even though the
                // HAVING clause should exclude it. A record with neither cannot be asked about, so it
                // is skipped, and the sweep moves on to the rest of the collection. The folder is
                // NOT NULL in the schema, so only these two are checked.
                if (reader.IsDBNull(1) || reader.IsDBNull(2))
                {
                    continue;
                }

                records.Add(new RecordToAsk(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    (int)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
            return records;
        }
    }

    /// <summary>Marks a record as asked about, without setting any of its songs. This is for records that
    /// no catalogue has heard of. Without it, the sweep would offer the same unfindable album every time
    /// it ran, and it would never reach the rest of the collection.</summary>
    public int NoteWorldPlaysUnknown(string folder) => SaveWorldPlays(folder, new Dictionary<TrackPlace, long>());

    /// <summary>Which matching the stored world plays were written by. The value 2 means matched by disc
    /// and position. Lower values were matched by position alone.</summary>
    public const string WorldPlaysMatchKey = "world_plays_match";

    private const int WorldPlaysMatchedByDisc = 2;

    /// <summary>
    /// Clears the world plays of every record with a second disc. This runs once per library, so the
    /// sweep asks about those records again and matches the counts by disc.
    ///
    /// <para>Records whose figures were matched by position alone have them on the wrong disc. Track 3 of
    /// the second disc overwrote track 3 of the first, and both rows took it. Nothing else is lost by
    /// clearing them. They are a copy of the API's answer, which the server keeps, so asking again costs
    /// a cached request per record.</para>
    /// </summary>
    /// <returns>How many rows were cleared. Zero once the library has been through this.</returns>
    public int AskAgainAboutDiscsMatchedByPosition()
    {
        if (GetMetaVersion(WorldPlaysMatchKey) >= WorldPlaysMatchedByDisc)
            return 0;

        int cleared;
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET world_plays = NULL
                WHERE world_plays IS NOT NULL
                  AND folder IN (SELECT folder FROM tracks GROUP BY folder HAVING MAX(disc_number) > 1)
                """;
            cleared = cmd.ExecuteNonQuery();
        }

        SetMeta(WorldPlaysMatchKey, WorldPlaysMatchedByDisc.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return cleared;
    }

    /// <summary>How far the sweep has got: records asked about, and records still to ask. The settings
    /// line reports this. It is also the one number that says whether the decade lists are worth
    /// drawing yet.</summary>
    public (int Asked, int Waiting) WorldPlaysProgress()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(DISTINCT CASE WHEN world_plays IS NOT NULL THEN folder END),
                       COUNT(DISTINCT CASE WHEN world_plays IS NULL THEN folder END)
                FROM tracks
                """;
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? ((int)reader.GetInt64(0), (int)reader.GetInt64(1))
                : (0, 0);
        }
    }
}

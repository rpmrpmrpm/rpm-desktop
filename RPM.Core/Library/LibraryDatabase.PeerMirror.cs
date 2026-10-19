using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;
using RPM.Core.Models;

namespace RPM.Core.Library;

/// <summary>What one pass over another of the user's computers changed here.</summary>
/// <param name="Adopted">Tracks that computer holds which this library had never heard of.</param>
/// <param name="Updated">Adopted rows it has since corrected.</param>
/// <param name="Forgotten">Rows it no longer holds, or that turned out to be duplicates.</param>
/// <param name="Reachable">Rows this library already had, now known to be on that computer too.
/// These can play over the network instead of from the cloud.</param>
public readonly record struct PeerMirrorResult(int Adopted, int Updated, int Forgotten, int Reachable)
{
    public int Total => Adopted + Updated + Forgotten + Reachable;
}

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Brings the mirror in line with what another of the user's own computers holds.
    ///
    /// <para>
    /// One collection across a person's machines is what signing in already promises. The cloud keeps
    /// that promise for everything that was uploaded. This is the rest of it: the records that were never
    /// uploaded, sitting on a laptop in the next room. They are shown here, and played across the network
    /// while both machines are on it.
    /// </para>
    ///
    /// <para>
    /// The rules for what may be adopted are the cloud's, unchanged, on purpose. The same id, the same
    /// audio, the same library path, or a record already on this shelf under another name means nothing is
    /// adopted. This adds one case that the cloud does not have: a row this library already shows <em>from
    /// the cloud</em> whose file is also on that computer. Nothing is adopted for it. The row is told where
    /// else the bytes are. Playing it reads the network rather than paying for the download.
    /// </para>
    /// </summary>
    /// <param name="deviceId">The computer this listing came from.</param>
    /// <param name="listing">Everything it holds, as <see cref="Sharing.PeerLibraryMirror"/> shapes it. A
    /// partial listing would read as "the rest was deleted". See <paramref name="prune"/>.</param>
    /// <param name="prune">Whether rows missing from the listing should be forgotten. True for a complete
    /// listing.</param>
    /// <param name="cloudOnly">The ids among <paramref name="listing"/> whose bytes are in the store rather
    /// than on this disk. Those rows are marked as cloud rows, which lets one keep playing after everybody
    /// has left the network. Which ids those are depends on who is lending. See
    /// <see cref="Sharing.PeerLibraryMirror.CloudOnlyIds"/> for one of the user's own machines, and
    /// <see cref="Sharing.PeerLibraryMirror.CloudBackedIds"/> for a guest.</param>
    public PeerMirrorResult MirrorPeerLibrary(
        string deviceId, IReadOnlyList<CloudTrack> listing, bool prune = true,
        IReadOnlySet<string>? cloudOnly = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(listing);

        var wanted = CloudLibraryMirror.OneRowPerTrack(listing);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            WriteListing(wanted);

            // One id is one row on this side too. It is settled before anything is read. A mirror
            // written by a build that did not state this rule can hold the pair. The standing row that
            // the pass picks for that id is one of the two. Writing it at the other's path is refused by
            // the path index, on every pass, for as long as both rows are there.
            int forgotten = CollapsePeerDuplicatesLocked(deviceId);

            var mine = KnownToLibrary(deviceId);
            var here = VisibleAlbums(deviceId);
            var adopted = PeerAdoptedByRpmId(deviceId);

            int added = 0, updated = 0;

            // A row this library already has, which that computer also holds, is not a track to adopt.
            // It is a track with a second address. It is stamped before the album loop, so that a record
            // that is half here and half there is left whole, not half adopted.
            int reachable = MarkReachableLocked(deviceId);

            foreach (var album in wanted.GroupBy(CloudLibraryMirror.AlbumKeyOf, StringComparer.Ordinal))
            {
                var rows = album.ToList();

                if (CloudLibraryMirror.IsSameAlbumElsewhere(rows, here.Folders, here.Tagged))
                {
                    forgotten += ForgetPeerLocked(deviceId, [.. rows.Select(r => r.RpmId)]);
                    continue;
                }

                foreach (var track in rows)
                {
                    if (mine.Contains(track.RpmId))
                        continue;

                    var fresh = CloudLibraryMirror.ToTrack(track) with
                    {
                        // Not the cloud's row and not this disk's: that computer's. The two flags are
                        // exclusive by construction. The cloud mirror's prune would read a peer row as a
                        // track that left the account, and delete it.
                        CloudAdopted = false,
                        CloudState = cloudOnly?.Contains(track.RpmId) == true
                            ? CloudState.CloudOnly
                            : CloudState.Local,
                        PeerAdopted = true,
                        PeerDeviceId = deviceId,
                    };

                    if (adopted.TryGetValue(track.RpmId, out var standing))
                    {
                        // The cloud state is compared as well as everything the row shows. A record its
                        // owner has since brought back down onto their disk stops being one this machine
                        // could play from the store. A row that kept claiming otherwise would sign a URL
                        // for bytes that are no longer up there.
                        //
                        // The favourite is set aside first, and that is not a shortcut. A listing never
                        // carries one (PeerLibraryMirror.ToCatalogRow). So a row the listener has
                        // favourited here always differs from its own listing on that field. If it were
                        // left in the comparison, the row would be read as changed on every pass, five
                        // minutes apart, for as long as the favourite stood. The write below does not
                        // touch the column, so the favourite itself was never at risk. What would have
                        // been at risk is the shelf reporting rows as changed on every pass and redrawing.
                        if (CloudLibraryMirror.Same(standing with { IsFavorite = fresh.IsFavorite }, fresh) &&
                            standing.CloudState == fresh.CloudState)
                            continue;

                        Displace(fresh.RelativePath, track.RpmId);

                        UpdatePeerLocked(standing.Id, fresh, deviceId);
                        updated++;
                        continue;
                    }

                    Displace(fresh.RelativePath, track.RpmId);

                    if (InsertPeerLocked(fresh, deviceId))
                        added++;
                }
            }

            forgotten += ForgetPeerAlsoHereLocked(deviceId);

            if (prune)
                forgotten += ForgetPeerMissingLocked(deviceId);

            tx.Commit();

            return new PeerMirrorResult(added, updated, forgotten, reachable);

            // A path is unique in the mirror. The row moving onto a path is not always the row that moves
            // first. For example, an album renumbered on the other computer hands every track the name of
            // the one before it, and the pass meets them in whatever order the listing is grouped in. The
            // row in the way belongs to that computer, so it is removed now, not at the prune below. It is
            // also taken out of the standing set. Then its own turn writes it fresh at the name it has moved
            // to, instead of updating a row that no longer exists.
            void Displace(string path, string keep)
            {
                if (DisplacePeerAtLocked(deviceId, path, keep) is { } moved)
                    adopted.Remove(moved);
            }
        }
    }

    /// <summary>
    /// Drops everything one computer's listing put here, and takes its name off the rows it merely
    /// lent an address to.
    ///
    /// <para>This is what "stop sharing with that computer" does on this side. It also happens when the
    /// user signs out. Music on somebody else's disk is not this library's to show once the arrangement
    /// that made it visible is over.</para>
    /// </summary>
    public int ForgetPeer(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using var drop = _connection.CreateCommand();
            drop.CommandText = "DELETE FROM tracks WHERE peer_adopted = 1 AND peer_device_id = $peer";
            drop.Parameters.AddWithValue("$peer", deviceId);
            int dropped = drop.ExecuteNonQuery();

            using var clear = _connection.CreateCommand();
            clear.CommandText =
                "UPDATE tracks SET peer_device_id = NULL WHERE peer_adopted = 0 AND peer_device_id = $peer";
            clear.Parameters.AddWithValue("$peer", deviceId);
            clear.ExecuteNonQuery();

            tx.Commit();
            return dropped;
        }
    }

    /// <summary>Every computer's music, gone. This happens on sign-out, and when the sharing switch is
    /// turned off.</summary>
    public int ForgetAllPeers()
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE peer_adopted = 1";
            int dropped = cmd.ExecuteNonQuery();

            using var clear = _connection.CreateCommand();
            clear.CommandText = "UPDATE tracks SET peer_device_id = NULL WHERE peer_adopted = 0";
            clear.ExecuteNonQuery();

            tx.Commit();
            return dropped;
        }
    }

    /// <summary>
    /// Everything in this mirror that was <em>not</em> borrowed, gone.
    ///
    /// <para>This is only right for a mirror that holds somebody else's collection and nothing else (see
    /// <c>PeerLibraryStore</c>). Never call it on the user's own mirror. It would delete the library.</para>
    /// </summary>
    /// <returns>How many rows were dropped.</returns>
    public int ForgetLocalRows()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE peer_adopted = 0";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The row's file is on this disk now, so it stops being another computer's music. Sync calls
    /// this when the file turns up here.</summary>
    public void ClearPeerAdopted(long trackId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE tracks SET peer_adopted = 0, peer_device_id = NULL WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// One track by the id stamped in its file, or null.
    ///
    /// <para>The id is how a peer asks for a track. It is the only name for a record that means the same
    /// thing on two computers. A path or a row id means nothing across machines.</para>
    /// </summary>
    public LibraryTrack? FindByRpmId(string rpmId)
    {
        if (string.IsNullOrEmpty(rpmId))
            return null;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT * FROM tracks WHERE rpm_id = $gid LIMIT 1";
            cmd.Parameters.AddWithValue("$gid", rpmId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadTrack(reader) : null;
        }
    }

    /// <summary>How many rows this library is showing on another computer's word. The settings page
    /// states this. It also tells "nothing shared yet" apart from "nothing over there".</summary>
    public int CountPeerAdopted()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE peer_adopted = 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ── the pass itself ──

    /// <summary>
    /// The listed tracks this library already shows, for any reason: its own file, or a row adopted from
    /// the cloud. Both mean a second row would draw the record twice.
    ///
    /// <para>Rows this computer adopted from the same peer are excluded. They are the rows this pass keeps
    /// up to date, not other copies of the track.</para>
    /// </summary>
    private HashSet<string> KnownToLibrary(string deviceId)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);

        Collect("t.rpm_id = c.rpm_id");
        Collect("t.relative_path = c.rel_path");
        Collect("t.content_hash = c.sha AND c.sha IS NOT NULL");

        return known;

        void Collect(string join)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT c.rpm_id FROM temp.cloud_listing c
                JOIN tracks t ON {join}
                WHERE t.peer_adopted = 0 OR t.peer_device_id IS NOT $peer
                """;
            cmd.Parameters.AddWithValue("$peer", deviceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                known.Add(reader.GetString(0));
        }
    }

    /// <summary>
    /// The albums already on this shelf, whatever put them there: this machine's own files, the cloud's
    /// rows, and other computers' rows. The ones this pass is responsible for are left out.
    ///
    /// <para>This is wider than the cloud pass's question, on purpose. "Is this record already drawn?" is
    /// about what the user can see. By the time a second computer's listing arrives, the shelf may already
    /// hold that record from any of the three sources.</para>
    /// </summary>
    private (HashSet<string> Folders, HashSet<string> Tagged) VisibleAlbums(string deviceId)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagged = new HashSet<string>(StringComparer.Ordinal);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT folder, MIN(album), MIN(album_artist), MIN(artist)
            FROM tracks
            WHERE peer_adopted = 0 OR peer_device_id IS NOT $peer
            GROUP BY folder
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            folders.Add(reader.GetString(0));

            var album = reader.IsDBNull(1) ? null : reader.GetString(1);
            var albumArtist = reader.IsDBNull(2) ? null : reader.GetString(2);
            var artist = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (CloudLibraryMirror.TaggedAlbumKey(albumArtist, artist, album) is { } key)
                tagged.Add(key);
        }

        return (folders, tagged);
    }

    /// <summary>
    /// Notes, on rows this library already has, that the same track is also on that computer.
    ///
    /// <para>The case this exists for is the common one between two of a person's machines. A record is
    /// uploaded from the laptop and adopted here from the cloud, and the laptop is on the same network.
    /// Reading it over the network costs nothing and is quicker. The cloud is the fallback for when the
    /// laptop is not there.</para>
    /// </summary>
    private int MarkReachableLocked(string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tracks SET peer_device_id = $peer
            WHERE peer_adopted = 0
              AND (peer_device_id IS NULL OR peer_device_id = $peer)
              AND rpm_id IN (SELECT rpm_id FROM temp.cloud_listing)
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One row per track id among the rows adopted from one computer, keeping the oldest.
    ///
    /// <para>The pair this drops cannot be written any more. A listing is one row per id before it reaches
    /// the database (<see cref="CloudLibraryMirror.OneRowPerTrack"/>). So this is what a mirror filled in
    /// by an earlier build needs, to become updatable again. Rows with no id are left alone, because there
    /// is nothing to call them duplicates of.</para>
    /// </summary>
    private int CollapsePeerDuplicatesLocked(string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE peer_adopted = 1 AND peer_device_id = $peer
              AND rpm_id IS NOT NULL AND rpm_id <> ''
              AND id NOT IN (
                  SELECT MIN(id) FROM tracks
                  WHERE peer_adopted = 1 AND peer_device_id = $peer
                    AND rpm_id IS NOT NULL AND rpm_id <> ''
                  GROUP BY rpm_id)
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The columns the mirror actually looks at, and nothing else.
    ///
    /// <para><b>Every one of these is read by <see cref="CloudLibraryMirror.Same"/>, or by the two
    /// lines after it.</b> Adding a field to that comparison without adding its column here would
    /// leave the standing row holding a null where the listing holds a value. Every pass would then
    /// report a change that is not one, and a shelf would redraw itself every few minutes, forever.
    /// <c>PeerMirrorProjectionTests</c> mirrors a listing with every <see cref="Sharing.PeerTrack"/>
    /// field set, mirrors it again, and requires that nothing changes. A second test fails when a field
    /// is added to <see cref="Sharing.PeerTrack"/> without a value in that listing. The <c>advisory</c>
    /// column must stay in this list. If it is missing, every flagged row from another computer is
    /// rewritten on every pull, and the home screen reloads each time.</para>
    /// </summary>
    private const string Compared =
        "id, rpm_id, relative_path, folder, file_name, extension, content_hash, title, artist, " +
        "album_artist, album, genre, track_number, disc_number, disc_count, work, movement_name, " +
        "movement_no, composer, year, size_bytes, favorite, duration_seconds, cloud_state, advisory";

    /// <summary>
    /// The peer's rows this library already holds, as much of each as the pass compares.
    /// </summary>
    /// <remarks>
    /// <b>Narrow on purpose, and the column positions are taken once.</b> This reads only the columns
    /// the pass compares. Reading with <c>SELECT *</c> through <c>ReadTrack</c> would ask the reader for
    /// a column by NAME fifty-five times per row, and each lookup is a string search. On a borrowed shelf
    /// of about twenty-seven thousand tracks, that is about one and a half million lookups and three
    /// million native column reads. All of it runs inside the writing gate that every other write in the
    /// app waits behind. The drawing thread can then stall for seconds. So this reads twenty-five
    /// columns, and their positions are resolved once before the loop, not per column per row.
    ///
    /// <para>The fields left unset are the ones that nothing in this code reads. This omission is
    /// required. <see cref="Compared"/> explains why, and a test checks it.</para>
    /// </remarks>
    private Dictionary<string, LibraryTrack> PeerAdoptedByRpmId(string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT {Compared} FROM tracks WHERE peer_adopted = 1 AND peer_device_id = $peer";
        cmd.Parameters.AddWithValue("$peer", deviceId);
        using var reader = cmd.ExecuteReader();

        // Once, not per row and not per column. This is what makes the read fast.
        int id = reader.GetOrdinal("id"),
            rpmId = reader.GetOrdinal("rpm_id"),
            path = reader.GetOrdinal("relative_path"),
            folder = reader.GetOrdinal("folder"),
            fileName = reader.GetOrdinal("file_name"),
            extension = reader.GetOrdinal("extension"),
            hash = reader.GetOrdinal("content_hash"),
            title = reader.GetOrdinal("title"),
            artist = reader.GetOrdinal("artist"),
            albumArtist = reader.GetOrdinal("album_artist"),
            album = reader.GetOrdinal("album"),
            genre = reader.GetOrdinal("genre"),
            track = reader.GetOrdinal("track_number"),
            disc = reader.GetOrdinal("disc_number"),
            discs = reader.GetOrdinal("disc_count"),
            work = reader.GetOrdinal("work"),
            movement = reader.GetOrdinal("movement_name"),
            movementNo = reader.GetOrdinal("movement_no"),
            composer = reader.GetOrdinal("composer"),
            year = reader.GetOrdinal("year"),
            size = reader.GetOrdinal("size_bytes"),
            favorite = reader.GetOrdinal("favorite"),
            seconds = reader.GetOrdinal("duration_seconds"),
            cloud = reader.GetOrdinal("cloud_state"),
            advisory = reader.GetOrdinal("advisory");

        var rows = new Dictionary<string, LibraryTrack>(StringComparer.Ordinal);

        while (reader.Read())
        {
            if (reader.IsDBNull(rpmId))
                continue;

            var key = reader.GetString(rpmId);
            if (key.Length == 0)
                continue;

            rows[key] = new LibraryTrack
            {
                Id = reader.GetInt64(id),
                RpmId = key,
                RelativePath = reader.GetString(path),

                // Not compared, but required by the type. A track without them is not a track.
                // Handing one out with empty strings in those places would be a lie that a caller
                // could rely on. Those three extra columns are the cost of avoiding that.
                Folder = reader.GetString(folder),
                FileName = reader.GetString(fileName),
                Extension = reader.GetString(extension),
                ContentHash = Text(reader, hash),
                Title = Text(reader, title),
                Artist = Text(reader, artist),
                AlbumArtist = Text(reader, albumArtist),
                Album = Text(reader, album),
                Genre = Text(reader, genre),
                TrackNumber = (uint)reader.GetInt64(track),
                DiscNumber = (uint)reader.GetInt64(disc),
                DiscCount = (uint)reader.GetInt64(discs),
                Work = Text(reader, work),
                MovementName = Text(reader, movement),
                MovementNumber = reader.IsDBNull(movementNo) ? null : (int)reader.GetInt64(movementNo),
                Composer = Text(reader, composer),
                Year = (uint)reader.GetInt64(year),
                SizeBytes = reader.GetInt64(size),
                IsFavorite = reader.GetInt64(favorite) != 0,
                DurationSeconds = reader.GetDouble(seconds),
                CloudState = (CloudState)reader.GetInt64(cloud),
                Advisory = reader.IsDBNull(advisory) ? null : (ContentAdvisory)reader.GetInt64(advisory),
                PeerAdopted = true,
                PeerDeviceId = deviceId,
            };
        }

        return rows;
    }

    /// <summary>A string column by ordinal, null when it is null. The narrow reader's own helper, so
    /// that no name is looked up inside the loop.</summary>
    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private int ForgetPeerLocked(string deviceId, IReadOnlyList<string> rpmIds)
    {
        if (rpmIds.Count == 0) return 0;

        int dropped = 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "DELETE FROM tracks WHERE peer_adopted = 1 AND peer_device_id = $peer AND rpm_id = $gid";
        cmd.Parameters.AddWithValue("$peer", deviceId);
        var id = cmd.Parameters.Add("$gid", SqliteType.Text);

        foreach (var rpmId in rpmIds)
        {
            id.Value = rpmId;
            dropped += cmd.ExecuteNonQuery();
        }

        return dropped;
    }

    /// <summary>Drops an adopted row whose track this library also holds by itself. These are the same three
    /// tests that refuse an adoption in the first place, applied to the copy that arrived later.</summary>
    private int ForgetPeerAlsoHereLocked(string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE peer_adopted = 1 AND peer_device_id = $peer
              AND EXISTS (
                  SELECT 1 FROM tracks AS mine
                  WHERE mine.peer_adopted = 0
                    AND (mine.rpm_id = tracks.rpm_id
                         OR mine.relative_path = tracks.relative_path
                         OR (tracks.content_hash IS NOT NULL AND mine.content_hash = tracks.content_hash))
              )
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Rows adopted from this computer that its listing no longer mentions. They were deleted
    /// there, or taken out of what it shares.</summary>
    private int ForgetPeerMissingLocked(string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE peer_adopted = 1 AND peer_device_id = $peer
              AND rpm_id NOT IN (SELECT rpm_id FROM temp.cloud_listing)
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears one of this computer's adopted rows out of a path another of its tracks is about to
    /// take, and says which track it was.
    ///
    /// <para>Only rows adopted from the same computer are removed. A path held by anything else (this
    /// machine's own file, the cloud's row, or another computer's) means the track is already on the
    /// shelf, and the pass never reaches a write for it.</para>
    /// </summary>
    /// <returns>The id of the track whose row was dropped, or null if the path was free.</returns>
    private string? DisplacePeerAtLocked(string deviceId, string path, string keepRpmId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE peer_adopted = 1 AND peer_device_id = $peer
              AND relative_path = $path AND rpm_id IS NOT $gid
            RETURNING rpm_id
            """;
        cmd.Parameters.AddWithValue("$peer", deviceId);
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$gid", keepRpmId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
    }

    private bool InsertPeerLocked(LibraryTrack t, string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tracks (relative_path, folder, file_name, extension, size_bytes,
                                modified_utc_ticks, duration_seconds, title, artist,
                                album_artist, album, genre, track_number, disc_number,
                                disc_count, disc_subtitle, work, movement_name, movement_no,
                                composer, year,
                                channels, sample_rate, bits_per_sample, bitrate_kbps,
                                codec_description, loudness_lufs, true_peak,
                                rpm_id, content_hash, cloud_state, cloud_selected, cloud_pinned,
                                canonical_size_bytes, cloud_synced_utc_ticks, cloud_error,
                                added_utc_ticks, cloud_adopted, cloud_updated_at,
                                favorite, favorited_utc_ticks, peer_adopted, peer_device_id,
                                advisory)
            VALUES ($path, $folder, $name, $ext, $size, $mtime, $dur, $title, $artist,
                    $albumartist, $album, $genre, $track, $disc, $disccount, $discsub,
                    $work, $movement, $movementno, $composer, $year,
                    $chan, $rate, $bits, $kbps, $codec, $lufs, $peak,
                    $gid, $hash, $cstate, $csel, $cpin, $csize, $cticks, $cerr,
                    $added, 0, $cloudat, $fav, $favat, 1, $peer,
                    $advisory)
            ON CONFLICT(relative_path) DO NOTHING
            """;
        BindAdopted(cmd, t);
        cmd.Parameters.AddWithValue("$peer", deviceId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>An adopted row, brought up to what that computer now says about it.</summary>
    private void UpdatePeerLocked(long id, LibraryTrack t, string deviceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tracks SET
                relative_path = $path, folder = $folder, file_name = $name, extension = $ext,
                size_bytes = $size, duration_seconds = $dur, title = $title, artist = $artist,
                album_artist = $albumartist, album = $album, genre = $genre,
                track_number = $track, disc_number = $disc, disc_count = $disccount,
                disc_subtitle = $discsub,
                -- Which composition each track belongs to. A borrowed classical box is drawn as
                -- works with their movements, and the row here is the only place that can say so:
                -- the file it describes is on somebody else's disk.
                work = $work, movement_name = $movement,
                movement_no = $movementno, composer = $composer, year = $year,
                channels = $chan, sample_rate = $rate, bits_per_sample = $bits,
                bitrate_kbps = $kbps, codec_description = $codec,
                loudness_lufs = COALESCE($lufs, loudness_lufs),
                true_peak = COALESCE($peak, true_peak),
                rpm_id = $gid, content_hash = $hash, cloud_state = $cstate,
                peer_device_id = $peer,
                -- The explicit flag, only where the listing states one, as the cloud mirror's update
                -- does: a listing from a build that never sent it keeps what the row has.
                advisory = COALESCE($advisory, advisory)
            WHERE id = $id
            """;
        BindAdopted(cmd, t);
        cmd.Parameters.AddWithValue("$peer", deviceId);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}

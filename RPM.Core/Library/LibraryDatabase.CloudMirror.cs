using Microsoft.Data.Sqlite;
using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>What one pass over the user's cloud catalogue changed in the mirror.</summary>
/// <param name="Adopted">Cloud tracks that this library did not know about, now added as rows.</param>
/// <param name="Updated">Adopted rows that the catalogue has since corrected, for example a retitled
/// track, or a record re-filed on the machine that owns the files.</param>
/// <param name="Forgotten">Adopted rows whose track is no longer in the cloud at all.</param>
public readonly record struct CloudMirrorResult(int Adopted, int Updated, int Forgotten)
{
    public int Total => Adopted + Updated + Forgotten;
}

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Brings the mirror in line with the user's cloud catalogue: every track up there that this
    /// machine does not hold becomes a row of its own, and rows adopted earlier that have since left
    /// the cloud are dropped.
    ///
    /// <para>
    /// <b>What counts as "this machine already has it"</b>, in the order in which the questions are
    /// cheap to ask: the same RPM id (the file itself, moved between installs), the same canonical hash
    /// (the same audio, uploaded from here), or the same library path (the same rip, filed by the same
    /// naming template on both machines). If any of the three matches, nothing is adopted. The local
    /// row is the track, and a second row beside it would draw the album twice.
    /// </para>
    ///
    /// <para>
    /// This method never writes local rows. The one statement that could reach one is the insert, and
    /// it refuses the conflict unless the row it lands on is itself adopted.
    /// </para>
    /// </summary>
    /// <param name="cloud">The whole catalogue, as <c>/cloud/list</c> returns it. A partial listing
    /// would read as "the rest was deleted": see <paramref name="prune"/>.</param>
    /// <param name="prune">Whether rows missing from <paramref name="cloud"/> should be forgotten.
    /// True for a complete listing; false when the caller only knows part of the catalogue.</param>
    /// <remarks>
    /// <b>The write gate is taken several times, not once.</b> A single hold around the whole pass
    /// took about 20 s on a 13,000-track catalogue, and for about 18 s of that the interface's own
    /// queue count was waiting behind it. That was the worst single stall in the app, and a user feels
    /// it about half a minute after opening the app. A pass that cannot be interrupted is one that the
    /// window has to wait out, and no admission rule can help: once the gate is held, the interface is
    /// already waiting behind it.
    ///
    /// <para><b>What a writer getting in between two batches cannot do.</b> The three sets read at the
    /// start (known, here, adopted) describe the library as it was when the first hold ended. If
    /// someone else writes between two batches, they would describe a library that no longer exists.
    /// So they are not trusted across a gap. <see cref="WriteTurns"/> is read when the sets are taken
    /// and again inside every batch. If the value has changed, someone else wrote, and all three sets
    /// are read again before any row of that batch is touched. This pass's own holds also step the
    /// counter, which is why the expected value is read again after each hold and not counted up. See
    /// <see cref="Gate.Turns"/>, which exists for exactly this and explains why it is stepped on
    /// release and not on take.</para>
    ///
    /// <para>The check can never say that the collection has not moved when it has, which is the one
    /// answer that would be unsafe. It can say that it HAS moved when the only writer was this pass.
    /// That costs a re-read of three queries, on a path that only happens when something else is
    /// writing anyway.</para>
    ///
    /// <para><b>What it still gives up.</b> Atomicity against a crash. With one transaction, a pass
    /// that died applied nothing. With several, it applies the batches that committed. That is a valid
    /// half-reconciled state and not a wrong one: the pass runs on a timer, can be run again, and the
    /// next pass finishes the job. It is the only property given up here.</para>
    ///
    /// <para>The order is NOT split. The listing is written and the three sets are read in the first
    /// hold, and the prune happens in the last. The prune asks the temp listing table, which is
    /// written whole before any batch runs, so it cannot forget a row because it saw half a
    /// catalogue.</para>
    /// </remarks>
    public CloudMirrorResult MirrorCloudLibrary(IReadOnlyList<CloudTrack> cloud, bool prune = true)
    {
        ArgumentNullException.ThrowIfNull(cloud);

        // One row per library path and one per track id, decided before the database is touched.
        // See CloudLibraryMirror.OneRowPerTrack.
        var wanted = CloudLibraryMirror.OneRowPerTrack(cloud);

        // Grouped before the gate is taken at all. An album is the unit in which a duplicate is
        // decided, so it is also the unit a batch is cut on: a record is never half mirrored.
        var albums = wanted
            .GroupBy(CloudLibraryMirror.AlbumKeyOf, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();

        // What this machine holds at the moment the batch about to run reads it. Declared out here
        // and not inside the hold, because every batch below may replace it.
        Here here;

        // ── the listing, and what this machine already has ───────────────────────────────────────
        //
        // The whole listing goes into the temp table in one hold, because the prune at the end asks
        // that table what the cloud holds. If it were written in pieces, a pass that stopped half way
        // would look to the prune like a catalogue that had lost the other half.
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            WriteListing(wanted);
            tx.Commit();

            here = Read();
        }

        // What the collection had been written to when the sets above were taken. It is read AFTER the
        // hold, because the counter steps on release. Inside the hold it still shows the value from
        // before this pass took the gate, and comparing against that would report our own write as
        // somebody else's.
        var asOf = WriteTurns;

        int added = 0, updated = 0, forgotten = 0;

        // ── the records, a batch at a time ───────────────────────────────────────────────────────
        //
        // The gate is given back at the end of every batch, and that is the moment a waiting window
        // gets in. The loop takes it again on the next turn.
        for (var from = 0; from < albums.Count; from += MirrorBatchAlbums)
        {
            using (_gate.Take())
            {
                // Somebody else wrote while the gate was free. The three sets describe a library that
                // no longer exists, so they are read again before any row of this batch is touched.
                // This is inside the hold, so nothing can change between the check and the re-read.
                if (WriteTurns != asOf)
                    here = Read();

                using var tx = _connection.BeginTransaction();

                var to = Math.Min(from + MirrorBatchAlbums, albums.Count);
                for (var i = from; i < to; i++)
                    MirrorOneAlbum(albums[i], here, ref added, ref updated, ref forgotten);

                tx.Commit();
            }

            asOf = WriteTurns;
        }

        // ── what this machine has taken over, and what has left the cloud ────────────────────────
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            // A track that this machine has since taken over (downloaded, or found on the disk) may
            // still have the row that the catalogue put here. One of the two is a duplicate.
            forgotten += ForgetAdoptedAlsoLocalLocked();

            if (prune) forgotten += ForgetMissingLocked();

            tx.Commit();
        }

        return new CloudMirrorResult(added, updated, forgotten);

        // What this machine holds. It is read with the write gate held, and read again whenever
        // somebody else has written since the last time: see the note above.
        Here Read()
        {
            var albumsHere = LocalAlbums();
            return new Here(KnownLocally(), albumsHere.Folders, albumsHere.Tagged, AdoptedByRpmId());
        }
    }

    /// <summary>
    /// What this machine holds, as the mirror pass needs to ask about it.
    /// </summary>
    /// <remarks>
    /// This is one value and not three locals, because the three are only ever true TOGETHER. They
    /// describe the library at one instant, and the pass gives the write gate back between batches,
    /// so when somebody else writes, all three must be replaced at once. Three separate variables
    /// would make it possible to refresh only two of them.
    /// </remarks>
    /// <param name="Known">Cloud tracks this machine already holds, by id.</param>
    /// <param name="Folders">Album folders on this disk.</param>
    /// <param name="Tagged">Albums this disk holds under their artist and title, whatever they are
    /// filed as.</param>
    /// <param name="Adopted">Rows this library took from the catalogue on an earlier pass, by id.</param>
    private readonly record struct Here(
        HashSet<string> Known,
        HashSet<string> Folders,
        HashSet<string> Tagged,
        Dictionary<string, LibraryTrack> Adopted);

    /// <summary>
    /// How many records one hold of the write gate covers.
    ///
    /// <para>The value is 64. On a 13,000-track catalogue that is about a fifth of a second inside the
    /// gate. It is small enough that a window waiting for the database waits a frame or two and not
    /// twenty seconds. It is large enough that the pass is not mostly the cost of taking a lock: the
    /// transaction per batch is the real minimum cost, and one per album would mean hundreds of
    /// them.</para>
    /// </summary>
    private const int MirrorBatchAlbums = 64;

    /// <summary>
    /// One record's worth of the catalogue, reconciled against what this machine holds.
    /// </summary>
    /// <remarks>
    /// It works album by album, because the thing a duplicate is a duplicate OF is a record and not a
    /// track. A cloud album filed at a path this library does not use is the same record under another
    /// name whenever its artist and title say so. Adopting it would draw the album twice, which is the
    /// one thing this method must never do.
    ///
    /// <para><b>Call it with the write gate held and inside a transaction.</b> This is what the Locked
    /// suffix on everything it calls means. It does not take the gate itself, because taking the gate
    /// once per album is what the batching above exists to avoid.</para>
    /// </remarks>
    private void MirrorOneAlbum(
        List<CloudTrack> rows,
        Here here,
        ref int added,
        ref int updated,
        ref int forgotten)
    {
        if (CloudLibraryMirror.IsSameAlbumElsewhere(rows, here.Folders, here.Tagged))
        {
            // Already on this shelf under a different path. Anything adopted for it in an earlier pass
            // is exactly the duplicate the user would see, so it is removed.
            forgotten += ForgetAdoptedLocked([.. rows.Select(r => r.RpmId)]);
            return;
        }

        foreach (var track in rows)
        {
            if (here.Known.Contains(track.RpmId))
                continue;

            var fresh = CloudLibraryMirror.ToTrack(track);

            if (here.Adopted.TryGetValue(track.RpmId, out var standing))
            {
                if (CloudLibraryMirror.Same(standing, fresh))
                    continue;

                // Edited on this machine and not yet accepted by the store. The catalogue still shows
                // what the track was called before, and writing that over the user's correction a few
                // seconds after they made it would make the edit seem not to save. The edit goes up
                // on the next catalogue pass. Until then this row is the newer of the two.
                if (standing.MetadataEditedUtcTicks > standing.CloudSyncedUtcTicks)
                    continue;

                Displace(fresh.RelativePath, track.RpmId);

                UpdateAdoptedLocked(standing.Id, fresh);
                updated++;
                continue;
            }

            Displace(fresh.RelativePath, track.RpmId);

            if (InsertAdoptedLocked(fresh))
                added++;
        }

        // A path is unique in the mirror, and the row moving onto a path is not always the row that
        // moves first. For example, an album renumbered on another machine gives every track the name
        // of the one before it, and the pass meets them in whatever order the catalogue is grouped in.
        // The row in the way was put here by the catalogue, so it is removed now and not at the prune.
        // It is also taken out of the standing set, so that its own turn writes it fresh at the name
        // it has moved to, and does not update a row that no longer exists.
        void Displace(string path, string keep)
        {
            if (DisplaceAdoptedAtLocked(path, keep) is { } moved)
                here.Adopted.Remove(moved);
        }
    }

    /// <summary>
    /// Drops every row this library adopted from the cloud, leaving its own rows untouched.
    ///
    /// <para>It is used on sign-out, and when a different account is signed in. The catalogue names
    /// every record the user owns. An install that is no longer signed in should not draw a wall of
    /// records it cannot play, nor show the next person who signs in the last person's
    /// collection.</para>
    /// </summary>
    public int ForgetAdopted()
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM tracks WHERE cloud_adopted = 1";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The same, inside a pass and for one track's sake: the adopted row sitting where another
    /// catalogue row is about to be written is dropped, and named so the pass can stop expecting it.
    /// </summary>
    /// <returns>The id of the track whose row was dropped, or null if the path was free.</returns>
    private string? DisplaceAdoptedAtLocked(string relativePath, string keepRpmId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE cloud_adopted = 1 AND relative_path = $path AND rpm_id IS NOT $gid
            RETURNING rpm_id
            """;
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.Parameters.AddWithValue("$gid", keepRpmId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() && !reader.IsDBNull(0) ? reader.GetString(0) : null;
    }

    /// <summary>
    /// Marks the row as this library's own from now on, because its file is on this disk.
    ///
    /// <para>Call it when an adopted track is brought down, and when sync finds that its file has
    /// turned up on the drive. Both mean the same thing: the row no longer lives and dies with the
    /// cloud listing.</para>
    /// </summary>
    public void ClearAdopted(long trackId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET cloud_adopted = 0 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The row and the store's copy of it now say the same thing.
    ///
    /// <para>
    /// Both stamps move together, and that is the point: the local one says "nothing has been edited
    /// here since", and the server's says "this is the version I have accepted". Until the next change
    /// on either side, both questions answer no and both directions of the sync leave the track alone.
    /// See <see cref="Cloud.CloudSyncPlan"/>.
    /// </para>
    /// </summary>
    /// <param name="cloudUpdatedAt">The store's stamp on the row that was just agreed with.</param>
    public void NoteCloudInStep(long trackId, long cloudUpdatedAt, long atUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET
                    cloud_synced_utc_ticks = $at,
                    cloud_updated_at = MAX(cloud_updated_at, $cloud)
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$at", atUtcTicks);
            cmd.Parameters.AddWithValue("$cloud", cloudUpdatedAt);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records the store's stamp on a row that the catalogue push just wrote, and leaves
    /// <c>cloud_synced_utc_ticks</c> where it is.
    ///
    /// <para>It is for a row whose sidecar is still owed (<see cref="Cloud.CloudSyncPlan.OwesSidecar"/>).
    /// The catalogue row now agrees, so the store's stamp is this machine's own write and must not be
    /// read as another machine's change on the next listing. The local stamp stays behind, because it
    /// is the only record that the sidecar has not been sent. The sidecar pass moves it with
    /// <see cref="NoteCloudMetadata"/>.</para>
    /// </summary>
    public void NoteCloudStamp(long trackId, long cloudUpdatedAt)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET cloud_updated_at = MAX(cloud_updated_at, $cloud) WHERE id = $id";
            cmd.Parameters.AddWithValue("$cloud", cloudUpdatedAt);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records that this machine has just changed a row's metadata. This tells an edit made here from
    /// the catalogue's own idea of the track. See <see cref="LibraryTrack.MetadataEditedUtcTicks"/>.
    /// </summary>
    public void NoteMetadataEdited(IEnumerable<string> relativePaths, long atUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE tracks SET metadata_edited_utc_ticks = $at WHERE relative_path = $path";
            cmd.Parameters.AddWithValue("$at", atUtcTicks);
            var path = cmd.Parameters.Add("$path", SqliteType.Text);

            foreach (var relative in relativePaths)
            {
                path.Value = relative;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Sets the advisory flag on tracks that have no file on this machine, as an edit made here.
    ///
    /// <para>Use it only for cloud-only rows. A track with a file keeps the flag in its tags, and the
    /// next scan would put the file's value back over a change made only in the row. For such a track,
    /// write the file instead.</para>
    ///
    /// <para>A row is changed only if its flag is still <paramref name="replacing"/>, which the caller
    /// states: Unknown (which also matches a row never read) to fill a blank, or another flag to
    /// correct it. The check is in the UPDATE itself, so a row edited after the caller decided is left
    /// alone.</para>
    ///
    /// <para>It stamps <c>metadata_edited_utc_ticks</c>, as <see cref="NoteMetadataEdited"/> does for
    /// any edit to a row with no file. The catalogue push then sends the flag, and the sidecar pass
    /// writes it into the sidecar (see <see cref="Cloud.CloudSyncPlan.OwesSidecar"/>).</para>
    /// </summary>
    /// <param name="advisory">The flag to set. It is never Unknown: the catalogue push never sends 0,
    /// so the cloud would keep the old flag and the two would disagree for good.</param>
    /// <returns>How many rows were changed.</returns>
    public int ReplaceCloudOnlyAdvisory(
        IEnumerable<string> rpmIds, Models.ContentAdvisory replacing, Models.ContentAdvisory advisory, long atUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(rpmIds);
        if (advisory == Models.ContentAdvisory.Unknown)
            throw new ArgumentException("Unknown cannot be sent to the cloud, so it is not set here.", nameof(advisory));
        if (advisory == replacing)
            throw new ArgumentException("Replacing a flag with itself changes nothing.", nameof(advisory));

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET
                    advisory = $advisory,
                    metadata_edited_utc_ticks = MAX(metadata_edited_utc_ticks, $at)
                WHERE rpm_id = $id
                  AND cloud_state = $cloudOnly
                  AND COALESCE(advisory, 0) = $replacing
                """;
            cmd.Parameters.AddWithValue("$advisory", (int)advisory);
            cmd.Parameters.AddWithValue("$replacing", (int)replacing);
            cmd.Parameters.AddWithValue("$at", atUtcTicks);
            cmd.Parameters.AddWithValue("$cloudOnly", (int)CloudState.CloudOnly);
            var id = cmd.Parameters.Add("$id", SqliteType.Text);

            var changed = 0;
            foreach (var rpmId in rpmIds)
            {
                id.Value = rpmId;
                changed += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return changed;
        }
    }

    /// <summary>
    /// Gives a row the id that the cloud already knows its audio by.
    ///
    /// <para>Use it only after the canonical hashes have been found to match, which proves the two
    /// are the same recording. See <see cref="Metadata.TagReader.WriteRpmId"/>, which writes the same
    /// id into the file so that the mirror and the file keep agreeing.</para>
    /// </summary>
    public void SetRpmId(long trackId, string rpmId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE tracks SET rpm_id = $gid WHERE id = $id";
            cmd.Parameters.AddWithValue("$gid", rpmId);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>How many rows this library is showing on the cloud's word alone. A settings page
    /// states it, and it tells "nothing adopted yet" from "nothing up there".</summary>
    public int CountAdopted()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM tracks WHERE cloud_adopted = 1";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>
    /// How many adopted rows the catalogue could not say the length of.
    ///
    /// <para>
    /// A row uploaded before the catalogue carried more than a name arrives here with no length, no
    /// sample rate and no track number. The track info pane then shows a file with nothing known about
    /// it, and the tracklist cannot put the record in order. The store can fill these in from the
    /// sidecars it already holds, so this count says whether it is worth asking. The length stands for
    /// all of them: a row that has a length was written by a client that wrote the rest.
    /// </para>
    /// </summary>
    public int CountAdoptedWithoutFacts()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM tracks WHERE cloud_adopted = 1 AND duration_seconds <= 0";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ── the pass itself ──

    /// <summary>The catalogue, in a temp table, so that the three "do we already have this?"
    /// questions are index joins over the mirror and not a query per cloud row.</summary>
    private void WriteListing(IReadOnlyList<CloudTrack> cloud)
    {
        Execute("DROP TABLE IF EXISTS temp.cloud_listing");
        Execute(
            """
            CREATE TEMP TABLE cloud_listing (
                rpm_id TEXT PRIMARY KEY,
                sha TEXT,
                rel_path TEXT COLLATE NOCASE
            )
            """);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "INSERT OR REPLACE INTO temp.cloud_listing (rpm_id, sha, rel_path) VALUES ($id, $sha, $path)";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var sha = cmd.Parameters.Add("$sha", SqliteType.Text);
        var path = cmd.Parameters.Add("$path", SqliteType.Text);

        foreach (var track in cloud)
        {
            id.Value = track.RpmId;
            sha.Value = string.IsNullOrWhiteSpace(track.Sha256) ? DBNull.Value : track.Sha256.Trim();
            path.Value = CloudLibraryMirror.PathFor(track);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The cloud tracks this machine already holds: by id, by canonical hash, or by the path
    /// they are filed at. It uses three index joins and not one OR, which SQLite would answer with a
    /// table scan per row.</summary>
    private HashSet<string> KnownLocally()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);

        Collect(
            """
            SELECT c.rpm_id FROM temp.cloud_listing c
            JOIN tracks t ON t.rpm_id = c.rpm_id
            WHERE t.cloud_adopted = 0
            """);
        Collect(
            """
            SELECT c.rpm_id FROM temp.cloud_listing c
            JOIN tracks t ON t.relative_path = c.rel_path
            WHERE t.cloud_adopted = 0
            """);
        Collect(
            """
            SELECT c.rpm_id FROM temp.cloud_listing c
            JOIN tracks t ON t.content_hash = c.sha
            WHERE t.cloud_adopted = 0 AND c.sha IS NOT NULL
            """);

        return known;

        void Collect(string sql)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                known.Add(reader.GetString(0));
        }
    }

    /// <summary>
    /// The albums this library holds on its own disk, in the two forms by which an album can be
    /// recognised: the folder it is filed in and, for a record that the same user filed somewhere
    /// else on another machine, its album artist and title.
    ///
    /// <para>It is one grouped read over the mirror. The folder space is the collection and not the
    /// track space, so this is cheap where a read per track would not be.</para>
    /// </summary>
    private (HashSet<string> Folders, HashSet<string> Tagged) LocalAlbums()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagged = new HashSet<string>(StringComparer.Ordinal);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT folder, MIN(album), MIN(album_artist), MIN(artist)
            FROM tracks
            WHERE cloud_adopted = 0
            GROUP BY folder
            """;

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

    /// <summary>Drops adopted rows by the id they were adopted under. These are the duplicates of a
    /// record that this library turns out to have already.</summary>
    private int ForgetAdoptedLocked(IReadOnlyList<string> rpmIds)
    {
        if (rpmIds.Count == 0) return 0;

        var dropped = 0;
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tracks WHERE cloud_adopted = 1 AND rpm_id = $gid";
        var id = cmd.Parameters.Add("$gid", SqliteType.Text);

        foreach (var rpmId in rpmIds)
        {
            id.Value = rpmId;
            dropped += cmd.ExecuteNonQuery();
        }

        return dropped;
    }

    /// <summary>
    /// Drops an adopted row whose track this library also holds itself.
    ///
    /// <para>The pass refuses to adopt such a track in the first place. But a row adopted before the
    /// local copy existed (the file downloaded, dropped in by hand, or ripped from the same CD) would
    /// otherwise stay beside the real one for ever. It is matched in the same three ways an adoption
    /// is refused: the id, the audio, the path.</para>
    /// </summary>
    private int ForgetAdoptedAlsoLocalLocked()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE cloud_adopted = 1
              AND EXISTS (
                  SELECT 1 FROM tracks AS mine
                  WHERE mine.cloud_adopted = 0
                    AND (mine.rpm_id = tracks.rpm_id
                         OR mine.relative_path = tracks.relative_path
                         OR (tracks.content_hash IS NOT NULL AND mine.content_hash = tracks.content_hash))
              )
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>What this library has adopted so far, by the id it was adopted under. It is bounded by
    /// the catalogue and not by the collection: a machine that holds all its own files adopts nothing
    /// and reads back an empty set.</summary>
    private Dictionary<string, LibraryTrack> AdoptedByRpmId()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM tracks WHERE cloud_adopted = 1";
        using var reader = cmd.ExecuteReader();

        var rows = new Dictionary<string, LibraryTrack>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var row = ReadTrack(reader);
            if (row.RpmId is { Length: > 0 } id)
                rows[id] = row;
        }
        return rows;
    }

    /// <summary>
    /// Puts an adopted row in, unless a row of this library's own is already at that path.
    ///
    /// <para>The conflict clause is the guard. A local row and a cloud row at one path mean that this
    /// machine holds the file. The match should have caught it, and if it did not, the file on the
    /// disk wins over a listing.</para>
    /// </summary>
    private bool InsertAdoptedLocked(LibraryTrack t)
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
                                favorite, favorited_utc_ticks, advisory)
            VALUES ($path, $folder, $name, $ext, $size, $mtime, $dur, $title, $artist,
                    $albumartist, $album, $genre, $track, $disc, $disccount, $discsub,
                    $work, $movement, $movementno, $composer, $year,
                    $chan, $rate, $bits, $kbps, $codec, $lufs, $peak,
                    $gid, $hash, $cstate, $csel, $cpin, $csize, $cticks, $cerr,
                    $added, 1, $cloudat, $fav, $favat, $advisory)
            ON CONFLICT(relative_path) DO UPDATE SET
                folder = $folder, file_name = $name, extension = $ext, size_bytes = $size,
                duration_seconds = $dur, title = $title, artist = $artist,
                album_artist = $albumartist, album = $album, genre = $genre,
                track_number = $track, disc_number = $disc, disc_count = $disccount,
                -- Which composition each track belongs to, so a classical record adopted from the
                -- catalog is drawn as works with their movements rather than as twenty untitled
                -- tracks. The row has no file here to read them back off.
                work = $work, movement_name = $movement, movement_no = $movementno,
                composer = $composer,
                year = $year, channels = $chan, sample_rate = $rate, bits_per_sample = $bits,
                bitrate_kbps = $kbps, codec_description = $codec,
                -- Only where the catalog has a reading. A row whose loudness the cloud does not know
                -- yet must not have this machine's own measurement wiped by the listing.
                loudness_lufs = COALESCE($lufs, loudness_lufs),
                true_peak = COALESCE($peak, true_peak),
                rpm_id = $gid, content_hash = $hash, cloud_state = $cstate,
                cloud_updated_at = $cloudat,
                -- The listener's mark, as the catalog now states it. Taken on an adopted row without
                -- ceremony: the row has no file and no life of its own here, and the machine that
                -- holds the record is the one that made the mark.
                favorite = $fav, favorited_utc_ticks = $favat,
                -- Only where the catalog states a flag, as with the loudness above.
                advisory = COALESCE($advisory, advisory)
            WHERE tracks.cloud_adopted = 1
            """;
        BindAdopted(cmd, t);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>An adopted row, brought up to date with what the catalogue now says. It works by id, so
    /// a record re-filed on the machine that owns it moves here and does not appear twice.</summary>
    private void UpdateAdoptedLocked(long id, LibraryTrack t)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tracks SET
                relative_path = $path, folder = $folder, file_name = $name, extension = $ext,
                size_bytes = $size, duration_seconds = $dur, title = $title, artist = $artist,
                album_artist = $albumartist, album = $album, genre = $genre,
                track_number = $track, disc_number = $disc, disc_count = $disccount,
                -- Which composition each track belongs to, so a classical record adopted from the
                -- catalog is drawn as works with their movements rather than as twenty untitled
                -- tracks. The row has no file here to read them back off.
                work = $work, movement_name = $movement, movement_no = $movementno,
                composer = $composer,
                year = $year, channels = $chan, sample_rate = $rate, bits_per_sample = $bits,
                bitrate_kbps = $kbps, codec_description = $codec,
                loudness_lufs = COALESCE($lufs, loudness_lufs),
                true_peak = COALESCE($peak, true_peak),
                content_hash = $hash, cloud_state = $cstate, cloud_updated_at = $cloudat,
                favorite = $fav, favorited_utc_ticks = $favat,
                -- Only where the catalog states a flag: a catalog that says nothing keeps the row's.
                advisory = COALESCE($advisory, advisory)
            WHERE id = $id AND cloud_adopted = 1
            """;
        BindAdopted(cmd, t);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void BindAdopted(SqliteCommand cmd, LibraryTrack t)
    {
        BindTrack(cmd, t);
        cmd.Parameters.AddWithValue("$added", t.AddedUtcTicks > 0 ? t.AddedUtcTicks : DateTime.UtcNow.Ticks);
        // The store's own stamp on the row this came from. It tells, next time, whether the catalogue
        // has moved on since this library last agreed with it. See CloudSyncPlan.
        cmd.Parameters.AddWithValue("$cloudat", t.CloudUpdatedAt);
        cmd.Parameters.AddWithValue("$fav", t.IsFavorite ? 1 : 0);
        cmd.Parameters.AddWithValue("$favat", t.FavoritedUtcTicks);
    }

    /// <summary>Rows adopted from a catalogue that no longer names them: the track was deleted from
    /// the cloud, here or on another install. Its bytes are gone from everywhere this machine can
    /// reach, so the row goes with them.</summary>
    private int ForgetMissingLocked()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM tracks
            WHERE cloud_adopted = 1
              AND (rpm_id IS NULL
                   OR rpm_id NOT IN (SELECT rpm_id FROM temp.cloud_listing))
            """;
        return cmd.ExecuteNonQuery();
    }
}

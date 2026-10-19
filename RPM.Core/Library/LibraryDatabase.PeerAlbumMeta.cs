using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>
/// One album's row and the moment somebody last said any of it.
///
/// <para>The time stamp is the whole of the merge rule. Two of a listener's computers can both be
/// edited while they are apart. For example, a record marked classical on the laptop, and the same
/// record rated on the desktop. When they meet again, the later edit is the one that stands. Nothing
/// else in this app compares one machine's clock to another's (see the catalogue's version hash and
/// the favourite's three stamps). This one does, because there is no server in a LAN sync to keep a
/// neutral clock. Two computers signed into one account are set up the same way, so their clocks are
/// assumed to agree. A row whose stamp is 0 (every row written before this column existed) loses to
/// any row that has a stamp. That is the right order. A machine that was upgraded and edited since
/// has newer changes to share.</para>
/// </summary>
public sealed record AlbumMetaEdit(AlbumMeta Meta, long EditedUtcTicks);

/// <summary>
/// Whether an album row arriving from another computer may change the favourite already on it.
///
/// <para>
/// There are two arrivals and two answers, and no default between them. Another of this listener's
/// own computers is one collection in two places. A favourite that did not travel between them would
/// be a favourite the collection does not have. A shelf borrowed from someone else is different. Its
/// rows say what the record is. The favourite on one of those rows is the borrower's own mark, made
/// on this computer about a record on a friend's disk. It is kept in the mirror held for that friend.
/// </para>
///
/// <para>
/// The caller decides this, not the merge, because the merge cannot tell the two cases apart. A
/// guest's listing has no favourite at all, which looks the same as a favourite that was removed.
/// Read the wrong way round, every pull would clear the borrower's favourites on the owner's next
/// album edit, and nothing on either machine would show where they went.
/// </para>
/// </summary>
public enum ArrivingAlbumMarks
{
    /// <summary>Another of this listener's computers. The whole row, favourite included.</summary>
    Whole,

    /// <summary>A shelf borrowed from someone else. Everything the row says about the record, but not
    /// the favourite. The favourite already there belongs to the borrower and is kept.</summary>
    NotTheHeart,
}

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Every album this library has anything written about, with its stamp. This is what one of the
    /// listener's own computers is handed so it can merge the two.
    ///
    /// <para>Everything on the row, marks included. This crosses the network only between machines
    /// signed into the same account. They are one person's collection in two places, not two people
    /// sharing. See SharingService, which builds a listing without any of this for a guest.</para>
    /// </summary>
    public IReadOnlyList<AlbumMetaEdit> AlbumMetaEdits()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT folder, is_favorite, rating, review_md, review_updated_utc_ticks, " +
                "release_id, catno, released, classical, normalize, release_provider, barcode, " +
                "rpm_album_id, disc_id, release_group_id, edition, popular_track, " +
                "popular_asked_utc_ticks, styles, edited_utc_ticks FROM album_meta";

            using var reader = cmd.ExecuteReader();
            var rows = new List<AlbumMetaEdit>();
            while (reader.Read())
            {
                var meta = ReadAlbumMeta(reader.GetString(0), reader, folderOrdinalOffset: 1);
                rows.Add(new AlbumMetaEdit(meta, reader.GetInt64(19)));
            }

            return rows;
        }
    }

    /// <summary>
    /// Merges another of the listener's computers' album rows into this library's, and says how many
    /// it took.
    ///
    /// <para>Row by row, and each row whole. An album is one thing a listener has an opinion about.
    /// Merging it field by field would create a record nobody has: the stars from one machine, the
    /// review from the other, and the classical mark from a third. The later edit wins and takes the
    /// whole row with it.</para>
    ///
    /// <para>The time stamp written is the one that arrived, not the local clock. Then both machines
    /// hold the same row with the same stamp, and the next sync in either direction has nothing to do.
    /// Stamping the row with the current time here would make each machine's copy look newer than the
    /// other's, and every sync would write the row again.</para>
    ///
    /// <para>Nothing is ever deleted here. A row this library has and the peer does not means the peer
    /// has never said anything about that record. It does not mean the peer's marks were removed. The
    /// peer's listing contains only the albums it has a row for.</para>
    /// </summary>
    /// <param name="carrying">Whether the arriving row may change the favourite. There is no default.
    /// See <see cref="ArrivingAlbumMarks"/>, which explains the whole decision.</param>
    public int ApplyAlbumMetaEdits(IReadOnlyList<AlbumMetaEdit> edits, ArrivingAlbumMarks carrying)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
            return 0;

        // The favourite's two lines, present or absent together. When absent, the statement does not
        // name is_favorite at all. An INSERT then leaves the column at its default, and an UPDATE
        // skips it, so the row keeps the favourite it already has.
        var heart = carrying is ArrivingAlbumMarks.Whole;

        var taken = 0;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            foreach (var edit in edits)
            {
                if (string.IsNullOrWhiteSpace(edit.Meta.Folder))
                    continue;

                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;

                // The guard is in the statement, not in a read followed by a write. That is one check
                // and one decision, with no gap between asking whose row is newer and writing the answer.
                cmd.CommandText =
                    $"""
                    INSERT INTO album_meta (
                        folder,{(heart ? " is_favorite," : "")} rating, review_md, review_updated_utc_ticks,
                        release_id, catno, released, classical, normalize, release_provider,
                        barcode, rpm_album_id, disc_id, release_group_id, edition, popular_track,
                        popular_asked_utc_ticks, edited_utc_ticks)
                    VALUES (
                        $folder,{(heart ? " $favorite," : "")} $rating, $review, $reviewAt,
                        $releaseId, $catno, $released, $classical, $normalize, $provider,
                        $barcode, $rpmAlbumId, $discId, $groupId, $edition, $popular, $asked, $edited)
                    ON CONFLICT(folder) DO UPDATE SET
                        {(heart ? "is_favorite = $favorite," : "")}
                        rating = $rating,
                        review_md = $review,
                        review_updated_utc_ticks = $reviewAt,
                        release_id = $releaseId,
                        catno = $catno,
                        released = $released,
                        classical = $classical,
                        normalize = $normalize,
                        release_provider = $provider,
                        barcode = $barcode,
                        rpm_album_id = $rpmAlbumId,
                        disc_id = $discId,
                        release_group_id = $groupId,
                        edition = $edition,
                        popular_track = $popular,
                        popular_asked_utc_ticks = $asked,
                        -- Stamped here when the merge changes the classical mark, so this machine
                        -- sends it on to the store. The clock rather than the arriving stamp: the
                        -- arriving stamp is the other machine's, and a mark synced here after it
                        -- would otherwise read as already sent. Only on the listener's own machines,
                        -- like the heart: a borrowed shelf's mirror is never synced to the store.
                        {(heart ? "classical_edited_utc_ticks = CASE WHEN album_meta.classical IS NOT excluded.classical THEN $now ELSE album_meta.classical_edited_utc_ticks END," : "")}
                        -- Last in the list, so the heart's line below can carry its own comma and be
                        -- absent without leaving a dangling one.
                        edited_utc_ticks = $edited
                        -- The mark is now this machine's to send on as well: the store keeps one
                        -- favourite per album per account, and a mark that arrived over the LAN is
                        -- the same mark the cloud is owed. Stamped with the edit's own moment, so a
                        -- machine that has already sent a later one does not send this instead.
                        {(heart ? ", favorite_edited_utc_ticks = MAX(favorite_edited_utc_ticks, $edited)" : "")}
                    WHERE excluded.edited_utc_ticks > album_meta.edited_utc_ticks
                    """;

                var meta = edit.Meta;
                cmd.Parameters.AddWithValue("$folder", meta.Folder);
                if (heart)
                    cmd.Parameters.AddWithValue("$favorite", meta.IsFavorite ? 1 : 0);
                cmd.Parameters.AddWithValue("$rating", Math.Clamp(meta.Rating, 0, 5));
                cmd.Parameters.AddWithValue("$review", (object?)meta.ReviewMarkdown ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$reviewAt", meta.ReviewUpdatedUtcTicks);
                cmd.Parameters.AddWithValue("$releaseId", (object?)meta.ReleaseId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$catno", (object?)meta.Catno ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$released", (object?)meta.Released ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$classical", Tri(meta.Classical));
                cmd.Parameters.AddWithValue("$normalize", Tri(meta.Normalize));
                cmd.Parameters.AddWithValue("$provider", (object?)meta.ReleaseProvider ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$barcode", (object?)meta.Barcode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rpmAlbumId", (object?)meta.RpmAlbumId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$discId", (object?)meta.DiscId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$groupId", (object?)meta.ReleaseGroupId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$edition", (object?)meta.Edition ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$popular", (object?)meta.PopularTrack ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$asked", meta.PopularAskedUtcTicks);
                cmd.Parameters.AddWithValue("$edited", edit.EditedUtcTicks);
                if (heart)
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);

                taken += cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return taken;

        static object Tri(bool? value) => value is { } set ? set ? 1 : 0 : DBNull.Value;
    }
}

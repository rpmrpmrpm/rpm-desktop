using RPM.Core.Cloud;
using RPM.Core.Models;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>What a node in the playlist tree is: a folder that holds other nodes, or a playlist that
/// holds items.</summary>
public enum PlaylistNodeKind { Folder, Playlist }

/// <summary>What a playlist item points at: a single song, or another playlist (nesting).</summary>
public enum PlaylistItemKind { Song, Playlist }

/// <summary>A folder or playlist in the user's virtual tree. The one node with <see cref="IsNowPlaying"/>
/// is the pinned playlist the player always plays; it cannot be renamed or deleted.</summary>
public sealed record PlaylistNode(
    long Id, long? ParentId, PlaylistNodeKind Kind, string Name, int Sort, bool IsNowPlaying,
    PlaylistCover Cover);

/// <summary>One ordered entry in a playlist: a song (kept by <see cref="RpmId"/>, so it survives a
/// file move, with <see cref="RelPath"/> as the display value and fallback) or a reference to another
/// playlist (<see cref="RefPlaylistId"/>).</summary>
public sealed record PlaylistItem(
    long Id, long PlaylistId, int Position, PlaylistItemKind ItemKind,
    string? RpmId, string? RelPath, long? RefPlaylistId);

/// <summary>
/// What an add does with a song the playlist already holds. It is required at every call of
/// <see cref="LibraryDatabase.AddSongsToPlaylist"/>, with no default. A second copy added without asking,
/// and a song silently not added, are both wrong. Only the caller knows which one was asked for. The
/// phone's <c>PlaylistWriter.AlreadyThere</c> makes the same choice.
/// </summary>
public enum AlreadyInPlaylist
{
    /// <summary>Keep the copy that is there and do not add the song again. A song given twice in the
    /// same call goes in once.</summary>
    LeaveIt,

    /// <summary>Add every song given, including ones already in the playlist. For a listener who was
    /// asked and said so, and for writing a list out whole (a new playlist, the cloud's copy of one),
    /// where the songs given are the list.</summary>
    AddAnother,
}

/// <summary>
/// One of the user's playlists, seen from the outside by what it HOLDS rather than by what it is: how
/// many of a given set of songs are in it, and the first of them.
///
/// <para>It is used for the shelf on an artist's page that says which of your playlists this artist is
/// already in. <see cref="Songs"/> is how many of the artist's songs are in that playlist, not how long
/// the playlist is. The two are different facts, and only the first one answers the question.</para>
///
/// <para>Nothing here says which of the artist's songs are in it. The surfaces that show this draw the
/// playlist's own cover from the playlist's own records. So naming one of the artist's songs would be a
/// fact carried for nobody.</para>
/// </summary>
public sealed record PlaylistHolding(long NodeId, string Name, int Songs);

/// <summary>
/// Where one playlist stands with the cloud: which node it is, which stored copy this machine has
/// seen, and what this machine last published for it.
/// </summary>
/// <param name="Stamp">The store's own stamp for the copy this machine last agreed with. Zero means
/// "never recorded". That is treated as no news, not as very old. Deciding on the first run after an
/// upgrade that every playlist has changed elsewhere is the one way this goes wrong at scale.</param>
/// <param name="Published">The ids that copy named, or <see cref="Cloud.PlaylistBase.Unknown"/>. This is
/// the common ancestor of any merge. See <see cref="Cloud.PlaylistMerge"/>.</param>
/// <param name="SentMeta">The name and folder that went with it, as one string to compare against. See
/// <see cref="MetaOf"/>. Null where nothing has been published.</param>
public sealed record PlaylistCloudState(
    long NodeId, long Stamp, Cloud.PlaylistBase Published, string? SentMeta)
{
    /// <summary>
    /// The parts of a published playlist that are not its songs, in one comparable string.
    ///
    /// <para>It is only ever compared, never read back. So there is nothing to parse and no escaping to get
    /// wrong. It exists so that a rename, or a drag to another folder, counts as the playlist having moved.
    /// Without it, a push that skips unchanged track lists would never send either.</para>
    /// </summary>
    public static string MetaOf(string name, IReadOnlyList<string> folder)
        => string.Join('\u001f', [name, .. folder]);
}

/// <summary>Where a playlist stands with the cloud: the id it is stored under (null until it has ever
/// been put there), whether the user wants it there, and when it was last pushed.</summary>
public sealed record PlaylistCloud(string? CloudId, bool Selected, long SyncedUtcTicks)
{
    public static readonly PlaylistCloud None = new(null, false, 0);
}

/// <summary>
/// The playlist tree (schema v8): a user-built hierarchy of folders and playlists, where a playlist's
/// contents are songs and/or references to other playlists. One pinned "Now playing" playlist is the
/// one the player plays. Songs are held by RPM id so they survive file moves; a playlist reference
/// nests, guarded against cycles. Flattening resolves the whole thing to a play order.
/// </summary>
public sealed partial class LibraryDatabase
{
    // ── the tree ──

    /// <summary>
    /// What the playlist tree adds up to: how many playlists it holds (folders are not playlists, and
    /// the pinned Now playing node is not one either, because it is the player's queue), and how long their
    /// songs play for.
    ///
    /// <para>Songs are counted once per ROW, which is what the user put there. The same song in two
    /// playlists is two entries, and it plays twice. Nested playlist references add nothing of their own.
    /// Their songs are already counted under the playlist that holds them. Following the reference as well
    /// would count them twice for one listen.</para>
    ///
    /// <para>Counted over the playlists the shelf is actually SHOWING. On a borrowed shelf whose owner has
    /// left the network, that is fewer than the shelf holds. It is asked of <see cref="GetPlaylistNodes"/>,
    /// rather than by reading <see cref="CloudRowsOnly"/> a second time here. Two reads of a flag that moves
    /// on a background thread can disagree. A subtitle that names a total the panel below does not draw
    /// reads as a bug.</para>
    /// </summary>
    public (int Playlists, double Seconds) QueryPlaylistTotals()
    {
        var showing = GetPlaylistNodes()
            .Where(n => n.Kind == PlaylistNodeKind.Playlist && !n.IsNowPlaying)
            .Select(n => n.Id)
            .ToList();

        using (_readGate.Take())
        {
            // Nothing showing is written as a list that nothing can be in, rather than by leaving the
            // clause out. An empty IN () is not valid SQL everywhere, and "the whole tree" is the one
            // answer this must never fall back to. The ids are row ids that this method just read, so
            // there is nothing here to parameterise.
            var ids = showing.Count > 0 ? string.Join(",", showing) : "-1";

            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                 SELECT
                   (SELECT COUNT(*) FROM pl_node
                     WHERE kind = 'playlist' AND is_now_playing = 0 AND id IN ({ids})),
                   (SELECT COALESCE(SUM(t.duration_seconds), 0)
                      FROM pl_item i
                      JOIN pl_node n ON n.id = i.playlist_id AND n.is_now_playing = 0
                      JOIN tracks t ON t.rpm_id = i.rpm_id
                     WHERE i.item_kind = 'song' AND i.rpm_id IS NOT NULL AND i.playlist_id IN ({ids}))
                 """;

            using var reader = cmd.ExecuteReader();
            // Convert rather than GetDouble. COALESCE(SUM(...), 0) returns the INTEGER zero when there is
            // nothing to add up, and SQLite's reader will not widen that for us.
            return reader.Read()
                ? (Convert.ToInt32(reader.GetValue(0)), Convert.ToDouble(reader.GetValue(1)))
                : (0, 0);
        }
    }

    /// <summary>
    /// Every playlist holding at least one explicit song. This is what puts the E beside its name.
    ///
    /// <para>Any song, not most of them. The rule that <see cref="QueryExplicitAlbums"/> uses for a record
    /// applies here too. A list with one explicit song in it is a list you would want to know that about,
    /// and a majority rule would hide it.</para>
    ///
    /// <para>Only the playlist's own song rows count. A reference to another playlist adds nothing of its
    /// own. This is the same reading that <see cref="QueryPlaylistTotals"/> takes of the same join, for the
    /// same reason: the songs behind a reference are counted under the playlist that holds them. So a list
    /// whose only explicit song sits inside a list it points at shows no E. That is the honest answer to
    /// "what is in this playlist".</para>
    ///
    /// <para>Every playlist is found in ONE read, not one read per row. The tree asks this once per reload
    /// and stamps the answer on the rows it is already building. Asked per playlist, it would take the
    /// reading gate about eighty times for one fact that one query answers.</para>
    /// </summary>
    public HashSet<long> QueryExplicitPlaylists()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                 SELECT DISTINCT i.playlist_id
                   FROM pl_item i
                   JOIN tracks t ON t.rpm_id = i.rpm_id
                  WHERE i.item_kind = 'song'
                    AND i.rpm_id IS NOT NULL
                    AND t.advisory = {(int)ContentAdvisory.Explicit}
                 """;

            using var reader = cmd.ExecuteReader();
            var marked = new HashSet<long>();
            while (reader.Read())
                marked.Add(reader.GetInt64(0));
            return marked;
        }
    }

    /// <summary>
    /// Every node in the tree, in sibling order. The caller assembles the hierarchy from the parent
    /// links.
    ///
    /// <para>Narrowed by <see cref="CloudRowsOnly"/>, like every other way of reading a collection. On a
    /// borrowed shelf whose owner has left the network, a playlist holding one song that lives on their
    /// disk is a list that stops partway through, so it is not offered. See
    /// <see cref="WithinReachLocked"/>, where that is worked out.</para>
    /// </summary>
    public IReadOnlyList<PlaylistNode> GetPlaylistNodes()
    {
        using (_readGate.Take())
        {
            var nodes = new List<PlaylistNode>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, parent_id, kind, name, sort, is_now_playing, cover FROM pl_node " +
                    "ORDER BY sort, name COLLATE NOCASE";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    nodes.Add(ReadNode(reader));
            }

            if (!CloudRowsOnly)
                return nodes;

            return UnderWhatIsShown(nodes, WithinReachLocked(nodes));
        }
    }

    /// <summary>
    /// The nodes being shown, each filed under the nearest node above it that is shown too.
    ///
    /// <para>For a tree of folders this changes nothing: a folder is kept whenever anything under it is
    /// (see <see cref="WithinReachLocked"/>). It is for a list hung under another LIST. On a borrowed shelf,
    /// that is the owner's mood flavours under the mood's own list. A list is kept only when every song on
    /// it can be reached. So the mood can be out of reach while a flavour is not. Left pointing at a parent
    /// that is not drawn, the flavour has nowhere to go, and the pane does not show it. Filed in the place
    /// the mood was in, it is offered like any other list that plays.</para>
    /// </summary>
    internal static IReadOnlyList<PlaylistNode> UnderWhatIsShown(
        IReadOnlyList<PlaylistNode> nodes, IReadOnlySet<long> shown)
    {
        var parentOf = nodes.ToDictionary(n => n.Id, n => n.ParentId);
        var filed = new List<PlaylistNode>(shown.Count);

        foreach (var node in nodes)
        {
            if (!shown.Contains(node.Id))
                continue;

            // Bounded, like every walk up this tree. A listing that arrived with a cycle in it must
            // cost one row its place, not hang the read.
            var parent = node.ParentId;
            for (var hops = 0; parent is { } up && !shown.Contains(up) && hops < 64; hops++)
                parent = parentOf.GetValueOrDefault(up);

            filed.Add(parent == node.ParentId ? node : node with { ParentId = parent });
        }

        return filed;
    }

    /// <summary>
    /// The folders a node sits in, outermost first. This is the chain that the cloud copy of a playlist
    /// carries, so that another machine can put it back where it was made. The node itself is not in
    /// the answer, and a node at the top of the tree has an empty one.
    ///
    /// <para>It is read from the table, not from <see cref="GetPlaylistNodes"/>, which is narrowed by
    /// <see cref="CloudRowsOnly"/>. A chain with a link missing is not a shorter chain. It is a different
    /// place. Cycles cannot happen, because <see cref="MovePlaylistNode"/> refuses to make one. The walk is
    /// bounded anyway. A database that had a cycle would otherwise hang the sync, instead of skipping a
    /// playlist.</para>
    /// </summary>
    public IReadOnlyList<string> PlaylistFolderChain(long nodeId)
    {
        using (_readGate.Take())
        {
            var chain = new List<string>();
            var seen = new HashSet<long> { nodeId };

            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT parent_id, name FROM pl_node WHERE id = $id LIMIT 1";
            var id = cmd.Parameters.AddWithValue("$id", nodeId);

            var at = nodeId;
            while (true)
            {
                id.Value = at;
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) break;

                // The node's own name is never part of its chain, so the name is only taken on the
                // hops above the one this was called for.
                if (at != nodeId)
                    chain.Add(reader.GetString(1));

                if (reader.IsDBNull(0)) break;

                at = reader.GetInt64(0);
                if (!seen.Add(at)) break;
            }

            chain.Reverse();
            return chain;
        }
    }

    /// <summary>The id of the pinned "Now playing" playlist (seeded at migration).</summary>
    public long NowPlayingNodeId()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT id FROM pl_node WHERE is_now_playing = 1 LIMIT 1";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>Creates a folder under <paramref name="parentId"/> (root when null) and returns its id.</summary>
    public long CreatePlaylistFolder(long? parentId, string name) =>
        CreateNode(parentId, PlaylistNodeKind.Folder, name);

    /// <summary>
    /// The folder at the end of a chain of names, outermost first. Any part of the chain that is not here
    /// yet is created. Null for an empty chain, which is the top of the tree and not a folder.
    ///
    /// <para>This is how a playlist taken in from the cloud is put back where it was made. Names are matched
    /// without regard to case, because that is how the tree already sorts and shows siblings. Two folders
    /// whose names differ only in case would read as one folder written twice. Taking a playlist in should
    /// not be what creates the second. An existing PLAYLIST with that name is not a folder, and it is never
    /// matched. Otherwise the chain would end inside a leaf.</para>
    /// </summary>
    public long? EnsurePlaylistFolder(IReadOnlyList<string> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        long? at = null;
        foreach (var name in chain)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;   // a folder with no name is not a place; the chain simply skips over it

            at = FindChildFolder(at, name) ?? CreatePlaylistFolder(at, name);
        }

        return at;
    }

    private long? FindChildFolder(long? parentId, string name)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = parentId is null
                ? "SELECT id FROM pl_node WHERE parent_id IS NULL AND kind = 'folder' " +
                  "AND name = $name COLLATE NOCASE ORDER BY sort LIMIT 1"
                : "SELECT id FROM pl_node WHERE parent_id = $parent AND kind = 'folder' " +
                  "AND name = $name COLLATE NOCASE ORDER BY sort LIMIT 1";
            cmd.Parameters.AddWithValue("$name", name);
            if (parentId is { } id)
                cmd.Parameters.AddWithValue("$parent", id);

            return cmd.ExecuteScalar() is long found ? found : null;
        }
    }

    /// <summary>Creates a playlist under <paramref name="parentId"/> (root when null) and returns its id.</summary>
    public long CreatePlaylist(long? parentId, string name) =>
        CreateNode(parentId, PlaylistNodeKind.Playlist, name);

    private long CreateNode(long? parentId, PlaylistNodeKind kind, string name)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO pl_node (parent_id, kind, name, sort, created_utc_ticks, modified_utc_ticks)
                VALUES ($parent, $kind, $name, $sort, $now, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", KindText(kind));
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$sort", NextNodeSort(parentId));
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>Renames a node. The Now-playing node is left alone, because its name is fixed.</summary>
    public void RenamePlaylistNode(long id, string name)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE pl_node SET name = $name, modified_utc_ticks = $now " +
                "WHERE id = $id AND is_now_playing = 0";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Writes down how a playlist's picture was chosen. The picture itself is a file that the
    /// cover store owns. This is only the note that says which of the four ways made it. The Now-playing
    /// node is left alone. This is the same refusal that <see cref="RenamePlaylistNode"/> and
    /// <see cref="MovePlaylistNode"/> make, because it is the player's queue rather than a playlist
    /// somebody made. The database refuses the write outright, rather than trusting every caller to check
    /// first.
    ///
    /// <para>It bumps <c>modified_utc_ticks</c>, like every other writer of this row. Nothing reads that
    /// column here yet. The desktop's cloud push resends every cloud-selected playlist on a session cadence
    /// (see <c>CloudPassCadence.DueOncePerSession</c>), and it does not compare stamps. It is written
    /// correctly anyway, so that a future incremental push finds a true stamp.</para>
    /// </summary>
    public void SetPlaylistCover(long nodeId, PlaylistCover cover)
    {
        ArgumentNullException.ThrowIfNull(cover);
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE pl_node SET cover = $cover, modified_utc_ticks = $now " +
                "WHERE id = $id AND is_now_playing = 0";
            cmd.Parameters.AddWithValue("$cover", cover.ToWire() is { Length: > 0 } w ? w : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Moves a node to the end of <paramref name="newParentId"/>'s children (root when null). A
    /// folder cannot be moved inside its own subtree, and the Now-playing node stays where it is.</summary>
    public void MovePlaylistNode(long id, long? newParentId)
    {
        using (_gate.Take())
        {
            if (newParentId is { } target && (target == id || DescendantNodeIds(id).Contains(target)))
                return;   // This would move a folder into itself.

            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE pl_node SET parent_id = $parent, sort = $sort, modified_utc_ticks = $now " +
                "WHERE id = $id AND is_now_playing = 0";
            cmd.Parameters.AddWithValue("$parent", (object?)newParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sort", NextNodeSort(newParentId));
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Rewrites the order of one parent's children after a row has been dragged among them (root level
    /// when <paramref name="parentId"/> is null). Ids that are not that parent's children are ignored.
    ///
    /// <para>Every child is renumbered from zero, rather than the moved one being placed between two
    /// existing numbers. The read orders by sort and then by name. Two rows that share a number fall back
    /// to alphabetical order. That would make the list reorder itself whenever somebody renames an item in
    /// it. Whole numbers, rewritten each time, cannot drift into that.</para>
    ///
    /// <para>The Now-playing node keeps its place at the head of the roots, whatever is asked for here. It
    /// is not a playlist somebody made, so it is not theirs to move.</para>
    /// </summary>
    public void ReorderPlaylistNodes(long? parentId, IReadOnlyList<long> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        using (_gate.Take())
        {
            var own = ChildNodeIdsLocked(parentId);
            var pinned = parentId is null ? NowPlayingNodeIdLocked() : null;

            // One in front, so a drag that tried to put something above it puts it second instead.
            var sort = 0;
            if (pinned is { } head && own.Contains(head))
            {
                WriteNodeSort(head, sort++);
                own.Remove(head);
            }

            foreach (var id in orderedIds)
                if (own.Remove(id))
                    WriteNodeSort(id, sort++);

            // Anything the caller did not mention keeps its order, after what it did.
            foreach (var id in own)
                WriteNodeSort(id, sort++);
        }
    }

    private void WriteNodeSort(long id, int sort)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE pl_node SET sort = $sort, modified_utc_ticks = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$sort", sort);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>One parent's children, in the order they are read in. This is a set that remembers its
    /// order, so anything a reorder did not mention keeps the order it had.</summary>
    private List<long> ChildNodeIdsLocked(long? parentId)
    {
        var ids = new List<long>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = parentId is null
            ? "SELECT id FROM pl_node WHERE parent_id IS NULL ORDER BY sort, name COLLATE NOCASE"
            : "SELECT id FROM pl_node WHERE parent_id = $parent ORDER BY sort, name COLLATE NOCASE";
        if (parentId is { } p)
            cmd.Parameters.AddWithValue("$parent", p);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    private long? NowPlayingNodeIdLocked()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM pl_node WHERE is_now_playing = 1 LIMIT 1";

        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>Deletes a node and everything under it: the whole subtree, each node's items, and any
    /// item elsewhere that referenced one of them. The Now-playing node is never deleted.
    ///
    /// <para>Any cloud id under the subtree is recorded as let go, on the way out. A cloud copy that nobody
    /// claims is taken back in (see <see cref="CloudPlaylistIntake"/>). So if the cloud copy were left
    /// behind, the deleted playlist would be met again on the next sync run. This is written here, in the
    /// one door out of the tree, rather than at the menu item that called it.</para>
    /// </summary>
    public void DeletePlaylistNode(long id)
    {
        using (_gate.Take())
        {
            var subtree = DescendantNodeIds(id);
            subtree.Add(id);
            // Keep Now playing even if it were somehow named here.
            subtree.RemoveWhere(IsNowPlayingLocked);
            if (subtree.Count == 0)
                return;

            var ids = string.Join(",", subtree);
            Execute(
                $"INSERT OR REPLACE INTO pl_cloud_gone (cloud_id, gone_utc_ticks) " +
                $"SELECT cloud_id, {DateTime.UtcNow.Ticks} FROM pl_node " +
                $"WHERE id IN ({ids}) AND cloud_id IS NOT NULL");
            Execute($"DELETE FROM pl_item WHERE playlist_id IN ({ids})");
            Execute($"DELETE FROM pl_item WHERE ref_playlist_id IN ({ids})");
            Execute($"DELETE FROM pl_node WHERE id IN ({ids})");
        }
    }

    // ── a playlist's items ──

    /// <summary>A playlist's items, in order.</summary>
    public IReadOnlyList<PlaylistItem> GetPlaylistItems(long playlistId)
    {
        using (_gate.Take())
            return GetPlaylistItemsLocked(playlistId);
    }

    /// <summary>Appends songs to a playlist, keyed by RPM id, with the relative path as the display value
    /// and fallback. A playlist can list a song more than once. Whether this call adds a second copy is
    /// decided by <paramref name="whenAlreadyThere"/>.</summary>
    /// <param name="whenAlreadyThere">What a song the playlist already holds gets. It is checked against the
    /// playlist as it is inside this call, not against an earlier count. A caller that asked the listener
    /// first may be writing a moment after a cloud sync changed the list.</param>
    /// <returns>How many songs were written. Zero when every song was already there and
    /// <see cref="AlreadyInPlaylist.LeaveIt"/> was given. Zero also when there is no such playlist, for
    /// example because it was deleted while the caller was asking. Nothing is written for a node that is
    /// gone.</returns>
    public int AddSongsToPlaylist(long playlistId, IEnumerable<(string? RpmId, string RelPath)> songs,
        AlreadyInPlaylist whenAlreadyThere)
    {
        using (_gate.Take())
        {
            if (!IsPlaylistLocked(playlistId))
                return 0;

            var held = whenAlreadyThere == AlreadyInPlaylist.LeaveIt ? HeldSongsLocked(playlistId) : null;

            int pos = NextItemPosition(playlistId);
            int added = 0;
            foreach (var (rpmId, relPath) in songs)
            {
                if (held is not null)
                {
                    if (held.Holds(rpmId, relPath))
                        continue;
                    held.Add(rpmId, relPath);
                }

                added++;
                using var cmd = _connection.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO pl_item (playlist_id, position, item_kind, rpm_id, rel_path)
                    VALUES ($pl, $pos, 'song', $gid, $rel)
                    """;
                cmd.Parameters.AddWithValue("$pl", playlistId);
                cmd.Parameters.AddWithValue("$pos", pos++);
                cmd.Parameters.AddWithValue("$gid", (object?)rpmId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$rel", relPath);
                cmd.ExecuteNonQuery();
            }
            return added;
        }
    }

    /// <summary>
    /// How many of these songs the playlist already holds, for the question asked before adding them. A
    /// song counts when an entry has the same RPM id or the same relative path. This is the rule that
    /// <see cref="PlaylistsHolding"/> uses. Only songs held directly count, not songs in a nested playlist.
    /// Zero when there is no such playlist.
    /// </summary>
    public int CountAlreadyInPlaylist(long playlistId, IEnumerable<(string? RpmId, string RelPath)> songs)
    {
        using (_gate.Take())
        {
            var held = HeldSongsLocked(playlistId);
            return songs.Count(song => held.Holds(song.RpmId, song.RelPath));
        }
    }

    /// <summary>The songs a playlist lists directly, by both of the keys an entry is kept by.</summary>
    private sealed class HeldSongs
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

        public bool Holds(string? rpmId, string? relPath) =>
            (rpmId is { Length: > 0 } && _ids.Contains(rpmId))
            || (relPath is { Length: > 0 } && _paths.Contains(relPath));

        public void Add(string? rpmId, string? relPath)
        {
            if (rpmId is { Length: > 0 })
                _ids.Add(rpmId);
            if (relPath is { Length: > 0 })
                _paths.Add(relPath);
        }
    }

    private HeldSongs HeldSongsLocked(long playlistId)
    {
        var held = new HeldSongs();
        foreach (var item in GetPlaylistItemsLocked(playlistId))
            if (item.ItemKind == PlaylistItemKind.Song)
                held.Add(item.RpmId, item.RelPath);
        return held;
    }

    /// <summary>Nests one playlist inside another. Does nothing when that would form a cycle, meaning a
    /// playlist that contains itself, directly or through other playlists.</summary>
    public bool AddPlaylistRef(long playlistId, long refPlaylistId)
    {
        using (_gate.Take())
        {
            if (refPlaylistId == playlistId || ReachablePlaylistIds(refPlaylistId).Contains(playlistId))
                return false;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO pl_item (playlist_id, position, item_kind, ref_playlist_id)
                VALUES ($pl, $pos, 'playlist', $ref)
                """;
            cmd.Parameters.AddWithValue("$pl", playlistId);
            cmd.Parameters.AddWithValue("$pos", NextItemPosition(playlistId));
            cmd.Parameters.AddWithValue("$ref", refPlaylistId);
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>Removes one item from a playlist.</summary>
    public void RemovePlaylistItem(long itemId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM pl_item WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Rewrites a playlist's item order to the given item ids (drag-reorder). Ids not in the
    /// playlist are ignored.</summary>
    public void ReorderPlaylistItems(long playlistId, IReadOnlyList<long> orderedItemIds)
    {
        using (_gate.Take())
        {
            var own = GetPlaylistItemsLocked(playlistId).Select(i => i.Id).ToHashSet();
            int pos = 0;
            foreach (var itemId in orderedItemIds)
            {
                if (!own.Contains(itemId))
                    continue;
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE pl_item SET position = $pos WHERE id = $id";
                cmd.Parameters.AddWithValue("$pos", pos++);
                cmd.Parameters.AddWithValue("$id", itemId);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// Which of the user's playlists hold any of these songs, and how many of them each one holds.
    ///
    /// <para><b>Both keys, because a playlist entry is kept by both.</b> A song is stored by RPM id, with
    /// its relative path as the fallback (see <see cref="AddSongsToPlaylist"/>). Either one can be the only
    /// key that still works. For example, an entry added before the file had an id has no id, and a file
    /// that moved has a path that no longer names it. Neither key has a default, and neither can be
    /// dropped. A caller that passed only ids would quietly miss the older half of a long-lived playlist,
    /// and nothing would say so.</para>
    ///
    /// <para><b>The queue is not one of them.</b> The pinned Now playing node is the player's queue, not
    /// something the listener made. A shelf that says "this artist is in your playlists" and lights up
    /// because they are playing right now would be answering a different question.</para>
    ///
    /// <para><b>Held DIRECTLY, not through a nesting.</b> A playlist that only references another playlist,
    /// which holds the song, is not listed here. That is the honest reading of "this song is in that
    /// playlist": the entry is in the other list. It also stops one song being reported against every list
    /// that points at the list that holds it.</para>
    ///
    /// <para>One pass over the playlist entries, rather than a query per song. A listener has tens of
    /// playlists, and an artist can have hundreds of songs. The set is the cheap side of that.</para>
    /// </summary>
    /// <param name="rpmIds">The songs' RPM ids. Empty is allowed and means "match nothing by id".</param>
    /// <param name="relativePaths">The same songs' library-relative paths, forward-slashed, exactly as
    /// the mirror stores them.</param>
    public IReadOnlyList<PlaylistHolding> PlaylistsHolding(
        IReadOnlySet<string> rpmIds, IReadOnlySet<string> relativePaths)
    {
        if (rpmIds.Count == 0 && relativePaths.Count == 0)
            return [];

        // Only lists the shelf is showing. On a borrowed shelf whose owner has left the network, that is
        // fewer than it holds. An artist page that names a playlist the panel does not have is a shelf
        // pointing at nothing. This uses GetPlaylistNodes for the same reason the totals do. It is the
        // one door, so there is one answer.
        var showing = GetPlaylistNodes().Select(n => n.Id).ToHashSet();

        var counts = new Dictionary<long, int>();
        var names = new Dictionary<long, string>();

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT i.playlist_id, n.name, i.rpm_id, i.rel_path
                  FROM pl_item i
                  JOIN pl_node n ON n.id = i.playlist_id
                 WHERE i.item_kind = 'song' AND n.is_now_playing = 0
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rpmId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var relPath = reader.IsDBNull(3) ? null : reader.GetString(3);

                var hit = (rpmId is { Length: > 0 } && rpmIds.Contains(rpmId))
                          || (relPath is { Length: > 0 } && relativePaths.Contains(relPath));
                if (!hit)
                    continue;

                var id = reader.GetInt64(0);
                if (!showing.Contains(id))
                    continue;

                counts[id] = counts.GetValueOrDefault(id) + 1;
                names[id] = reader.GetString(1);
            }
        }

        // Most of the artist's songs first, then by name. A playlist with eight of their songs is a better
        // answer to "where is this artist in my music" than one with a single track. Ties fall back to the
        // name, so the shelf is the same every time it is built.
        return [.. counts
            .Select(entry => new PlaylistHolding(entry.Key, names[entry.Key], entry.Value))
            .OrderByDescending(playlist => playlist.Songs)
            .ThenBy(playlist => playlist.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Resolves a playlist to its play order: every song, in order, with nested playlists expanded
    /// in place. It guards against cycles, and skips songs whose file is no longer in the library.</summary>
    public IReadOnlyList<LibraryTrack> FlattenPlaylistTracks(long playlistId)
    {
        using (_gate.Take())
        {
            var tracks = new List<LibraryTrack>();
            var visited = new HashSet<long>();
            FlattenLocked(playlistId, tracks, visited);
            return tracks;
        }
    }

    private void FlattenLocked(long playlistId, List<LibraryTrack> into, HashSet<long> visited)
    {
        if (!visited.Add(playlistId))
            return;   // Already expanding this playlist, so this is a cycle. Stop.
        foreach (var item in GetPlaylistItemsLocked(playlistId))
        {
            if (item.ItemKind == PlaylistItemKind.Playlist)
            {
                if (item.RefPlaylistId is { } refId)
                    FlattenLocked(refId, into, visited);
            }
            else if (ResolveTrackLocked(item.RpmId, item.RelPath) is { } track)
            {
                // A song on a borrowed shelf whose owner has left the network plays only if the cloud
                // holds it. A song that is only in the cloud plays only while somebody is signed in. The
                // first rule hides the whole playlist (see WithinReachLocked), so it rarely drops anything
                // here. The two must be read from the same row. Otherwise the panel might one day offer a
                // list that plays only half of itself. The second rule does NOT hide the playlist. This is
                // the listener's own list on their own shelf, and a list that plays nine of its ten songs
                // is still theirs.
                if (!Reaches(track.CloudState))
                    continue;

                into.Add(track);
            }
        }
    }

    // ── the cloud's half of a playlist ──

    /// <summary>
    /// Where every node stands with the cloud, in one read.
    ///
    /// <para>The playlist pane asks this for every row it draws. One query per row would take the reading
    /// connection once per row. Measured at start-up, that meant 34 waits and 9.4 seconds of queueing for
    /// seventeen rows. It gives the same answer as <see cref="GetPlaylistCloud"/>. The album wall's rollups
    /// use the same approach, for the same reason. The point is not fewer statements. It is fewer moments at
    /// which somebody else can be made to wait.</para>
    ///
    /// <para>It covers every node, not only the ones in the cloud. A row that is NOT up there has to be told
    /// so. Otherwise a badge left over from before would stay on.</para>
    /// </summary>
    public IReadOnlyDictionary<long, PlaylistCloud> PlaylistCloudByNode()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT id, cloud_id, cloud_selected, cloud_synced_utc_ticks FROM pl_node";
            using var reader = cmd.ExecuteReader();
            var byNode = new Dictionary<long, PlaylistCloud>();
            while (reader.Read())
                byNode[reader.GetInt64(0)] = new PlaylistCloud(
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.GetInt64(3));
            return byNode;
        }
    }

    /// <summary>What the cloud knows about one playlist: the id it is stored under up there (assigned once
    /// and kept for the playlist's whole life), whether the user asked for it to be there, and when it was
    /// last pushed.</summary>
    public PlaylistCloud GetPlaylistCloud(long nodeId)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT cloud_id, cloud_selected, cloud_synced_utc_ticks FROM pl_node WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", nodeId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return PlaylistCloud.None;
            return new PlaylistCloud(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetInt64(2));
        }
    }

    /// <summary>
    /// Records the stamp the store gave this playlist when this machine last wrote it. See the v48 schema
    /// note. It is written straight after a successful push, and read on the next pull to tell our own
    /// copy coming back from a change made somewhere else.
    /// </summary>
    public void SetPlaylistCloudStamp(long nodeId, long updatedAt)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE pl_node SET cloud_updated_at = $stamp WHERE id = $id";
            cmd.Parameters.AddWithValue("$stamp", updatedAt);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every playlist this library holds for the cloud, by the id it is stored under: the node, the
    /// stamp that node last saw, and the ids it last published.
    ///
    /// <para>The three together are what a pull needs. The stamp says whether an inbound copy is news. The
    /// published list is what that news is measured against. See <see cref="Cloud.PlaylistMerge"/>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, PlaylistCloudState> PlaylistCloudStamps()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT cloud_id, id, cloud_updated_at, cloud_sent, cloud_sent_meta " +
                "FROM pl_node WHERE cloud_id IS NOT NULL";
            using var reader = cmd.ExecuteReader();

            var held = new Dictionary<string, PlaylistCloudState>(StringComparer.Ordinal);
            while (reader.Read())
                held[reader.GetString(0)] = new PlaylistCloudState(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? Cloud.PlaylistBase.Unknown : ReadSent(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4));

            return held;
        }
    }

    /// <summary>
    /// Records what this machine has just published for a playlist: the store's stamp for that copy,
    /// and the ids it named.
    ///
    /// <para>It is written in one call, because they are one fact. Recording the stamp without the list
    /// would say "I have seen this copy", and leave the merge nothing to measure the next one against. If
    /// the two drifted apart, a merge would read somebody's addition as somebody else's removal.</para>
    /// </summary>
    public void NotePlaylistPublished(
        long nodeId, long updatedAt, IReadOnlyList<string> trackIds, string meta)
    {
        ArgumentNullException.ThrowIfNull(trackIds);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE pl_node SET
                    cloud_updated_at = $stamp, cloud_sent = $sent, cloud_sent_meta = $meta
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$stamp", updatedAt);
            cmd.Parameters.AddWithValue("$sent", string.Join('\n', trackIds));
            cmd.Parameters.AddWithValue("$meta", meta);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>One line per id. An empty column is a playlist published with nothing in it. That is a real
    /// thing to have published, and it is not the same as never having published.</summary>
    private static Cloud.PlaylistBase ReadSent(string stored)
        => Cloud.PlaylistBase.Published(stored.Length == 0
            ? []
            : stored.Split('\n', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Records that a playlist is in the cloud under <paramref name="cloudId"/>. The id is written once
    /// and never rewritten. It is the name the playlist has up there. Changing it would orphan what is
    /// already stored, instead of updating it.
    /// </summary>
    public void SetPlaylistInCloud(long nodeId, string cloudId, long syncedUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE pl_node SET
                    cloud_id = COALESCE(cloud_id, $cid), cloud_selected = 1,
                    cloud_synced_utc_ticks = $ticks
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$cid", cloudId);
            cmd.Parameters.AddWithValue("$ticks", syncedUtcTicks);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();

            // A playlist put back in the cloud is one that this library has stopped letting go. The note
            // is cleared here, and only here, so "wanted" and "let go" cannot both be true. A playlist
            // taken in from the cloud comes through this call. It must not be refused because of the note
            // this library wrote when an older copy of it was deleted.
            using var forget = _connection.CreateCommand();
            forget.CommandText = "DELETE FROM pl_cloud_gone WHERE cloud_id = $cid";
            forget.Parameters.AddWithValue("$cid", cloudId);
            forget.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks a playlist as one the account lent this machine: it came down from the cloud and was
    /// never made here.
    ///
    /// <para>This has its own call, rather than a flag on <see cref="SetPlaylistInCloud"/>, because the two
    /// answer different questions. Only one of them is about where the playlist came from. A playlist made
    /// here and pushed up also goes through <see cref="SetPlaylistInCloud"/>. If that method took a parameter
    /// with a default, every existing caller would quietly claim that its playlists were borrowed.</para>
    /// </summary>
    public void MarkPlaylistAdopted(long nodeId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE pl_node SET cloud_adopted = 1 WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Drops every playlist the account lent this machine, and the songs listed in them. This is what
    /// signing out does to playlists. <c>ForgetAdopted</c> does the same for tracks.
    /// </summary>
    /// <returns>How many playlists went.</returns>
    /// <remarks>
    /// <para><b>A raw delete, and it must not be the ordinary one.</b> A normal delete writes a note in
    /// <c>pl_cloud_gone</c> that says this library let the playlist go. That note stops the next sync run
    /// from putting the playlist straight back. The note is wrong here. Nobody let anything go. The account
    /// simply left, and signing back in has to bring these playlists down again. A sign-out that wrote the
    /// note would delete a phone-made playlist from every computer the listener owns, permanently. The only
    /// sign of it would be a playlist that never came back.</para>
    ///
    /// <para>Folders are left standing, empty. A folder belongs to this machine even when everything in it
    /// came from elsewhere. An empty folder is one that somebody can put something in.</para>
    /// </remarks>
    public int ForgetAdoptedPlaylists()
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            using var items = _connection.CreateCommand();
            items.CommandText =
                "DELETE FROM pl_item WHERE playlist_id IN " +
                "(SELECT id FROM pl_node WHERE cloud_adopted = 1)";
            items.ExecuteNonQuery();

            using var nodes = _connection.CreateCommand();
            nodes.CommandText = "DELETE FROM pl_node WHERE cloud_adopted = 1";
            var gone = nodes.ExecuteNonQuery();

            tx.Commit();
            return gone;
        }
    }

    /// <summary>Takes a playlist out of the cloud's books. The cloud id stays, so putting the same playlist
    /// back later replaces what was there, rather than leaving a second copy behind. The playlist is also
    /// recorded as let go. The cloud copy may not have gone yet, or at all. A copy still up there would
    /// otherwise be taken back in as a playlist that nobody here claims.</summary>
    public void SetPlaylistOutOfCloud(long nodeId)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO pl_cloud_gone (cloud_id, gone_utc_ticks)
                SELECT cloud_id, $now FROM pl_node WHERE id = $id AND cloud_id IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();

            using var clear = _connection.CreateCommand();
            clear.CommandText =
                "UPDATE pl_node SET cloud_selected = 0, cloud_synced_utc_ticks = 0 WHERE id = $id";
            clear.Parameters.AddWithValue("$id", nodeId);
            clear.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Every cloud id a node in this tree stands for, whether or not the user still wants it synced.
    /// What a sync run checks a cloud listing against to find the playlists this machine has never seen.
    ///
    /// <para>Not narrowed to the synced ones. A playlist whose cloud id is remembered, but whose sync is
    /// off, is still THIS playlist. Taking its cloud copy in as a new one would put a second copy of it in
    /// the tree, beside the original.</para>
    /// </summary>
    public IReadOnlySet<string> PlaylistCloudIds()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT cloud_id FROM pl_node WHERE cloud_id IS NOT NULL";
            using var reader = cmd.ExecuteReader();

            var ids = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        }
    }

    /// <summary>
    /// The cloud playlists this library has deliberately let go of: what a sync run must NOT take back
    /// in, however long it stays up there.
    ///
    /// <para>Stated as a whole set rather than asked one id at a time. The caller reads a whole listing, and
    /// the answer has to be the same for every row in it. A per-row question could get a different answer
    /// halfway down, if the user deleted something else in the meantime.</para>
    /// </summary>
    public IReadOnlySet<string> CloudPlaylistsLetGo()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT cloud_id FROM pl_cloud_gone";
            using var reader = cmd.ExecuteReader();

            var gone = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                if (!reader.IsDBNull(0))
                    gone.Add(reader.GetString(0));
            return gone;
        }
    }

    /// <summary>True while any track a playlist plays is queued or uploading. This is what makes its row's
    /// cloud badge pulse, instead of sitting still. Nesting is followed, because a playlist is only
    /// finished when everything it plays has arrived.</summary>
    public bool PlaylistHasTracksInFlight(long playlistId)
    {
        using (_gate.Take())
        {
            var tracks = new List<LibraryTrack>();
            FlattenLocked(playlistId, tracks, new HashSet<long>());
            return tracks.Any(t => t.CloudState is CloudState.Queued or CloudState.Uploading);
        }
    }

    /// <summary>Every playlist the user has put in the cloud. A sync run re-pushes these once the tracks
    /// beneath them have arrived.</summary>
    public IReadOnlyList<PlaylistNode> CloudSelectedPlaylists()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT id, parent_id, kind, name, sort, is_now_playing, cover FROM pl_node " +
                "WHERE cloud_selected = 1 AND kind = 'playlist' ORDER BY sort, name COLLATE NOCASE";
            using var reader = cmd.ExecuteReader();
            var nodes = new List<PlaylistNode>();
            while (reader.Read())
                nodes.Add(ReadNode(reader));
            return nodes;
        }
    }

    /// <summary>
    /// Track ids that some <em>other</em> synced playlist still wants. Taking
    /// <paramref name="exceptNodeId"/> out of the cloud must leave these alone. Nested playlists are
    /// followed, because a synced playlist wants everything it plays.
    /// </summary>
    public HashSet<long> TrackIdsHeldByOtherCloudPlaylists(long exceptNodeId)
    {
        using (_gate.Take())
        {
            var held = new HashSet<long>();
            foreach (var node in CloudSelectedPlaylistsLocked())
            {
                if (node.Id == exceptNodeId)
                    continue;
                var tracks = new List<LibraryTrack>();
                FlattenLocked(node.Id, tracks, new HashSet<long>());
                foreach (var track in tracks)
                    held.Add(track.Id);
            }
            return held;
        }
    }

    private IReadOnlyList<PlaylistNode> CloudSelectedPlaylistsLocked()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, parent_id, kind, name, sort, is_now_playing, cover FROM pl_node " +
            "WHERE cloud_selected = 1 AND kind = 'playlist'";
        using var reader = cmd.ExecuteReader();
        var nodes = new List<PlaylistNode>();
        while (reader.Read())
            nodes.Add(ReadNode(reader));
        return nodes;
    }

    /// <summary>Points every playlist entry that names a file by path at where that file moved to. Entries
    /// that carry an RPM id find their song without this. Entries written before a track had an id would
    /// otherwise point at nothing.</summary>
    public void RepointPlaylistPath(string fromRelativePath, string toRelativePath)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE pl_item SET rel_path = $to WHERE rel_path = $from";
            cmd.Parameters.AddWithValue("$from", fromRelativePath);
            cmd.Parameters.AddWithValue("$to", toRelativePath);
            cmd.ExecuteNonQuery();
        }
    }

    // ── helpers ──

    private IReadOnlyList<PlaylistItem> GetPlaylistItemsLocked(long playlistId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, playlist_id, position, item_kind, rpm_id, rel_path, ref_playlist_id " +
            "FROM pl_item WHERE playlist_id = $pl ORDER BY position, id";
        cmd.Parameters.AddWithValue("$pl", playlistId);
        using var reader = cmd.ExecuteReader();
        var items = new List<PlaylistItem>();
        while (reader.Read())
            items.Add(new PlaylistItem(
                Id: reader.GetInt64(0),
                PlaylistId: reader.GetInt64(1),
                Position: (int)reader.GetInt64(2),
                ItemKind: reader.GetString(3) == "playlist" ? PlaylistItemKind.Playlist : PlaylistItemKind.Song,
                RpmId: reader.IsDBNull(4) ? null : reader.GetString(4),
                RelPath: reader.IsDBNull(5) ? null : reader.GetString(5),
                RefPlaylistId: reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        return items;
    }

    private LibraryTrack? ResolveTrackLocked(string? rpmId, string? relPath)
    {
        using var cmd = _connection.CreateCommand();
        if (!string.IsNullOrEmpty(rpmId))
        {
            cmd.CommandText = "SELECT * FROM tracks WHERE rpm_id = $gid LIMIT 1";
            cmd.Parameters.AddWithValue("$gid", rpmId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return ReadTrack(r);
        }
        if (!string.IsNullOrEmpty(relPath))
        {
            using var cmd2 = _connection.CreateCommand();
            cmd2.CommandText = "SELECT * FROM tracks WHERE relative_path = $rel LIMIT 1";
            cmd2.Parameters.AddWithValue("$rel", relPath);
            using var r2 = cmd2.ExecuteReader();
            if (r2.Read())
                return ReadTrack(r2);
        }
        return null;
    }

    /// <summary>Every playlist reachable from <paramref name="startId"/> by following nested-playlist items.
    /// A new reference must not touch this set, or it would loop.</summary>
    private HashSet<long> ReachablePlaylistIds(long startId)
    {
        var seen = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(startId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id))
                continue;
            foreach (var item in GetPlaylistItemsLocked(id))
                if (item.ItemKind == PlaylistItemKind.Playlist && item.RefPlaylistId is { } refId)
                    stack.Push(refId);
        }
        return seen;
    }

    private HashSet<long> DescendantNodeIds(long id)
    {
        var all = new List<(long Id, long? Parent)>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, parent_id FROM pl_node";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                all.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1)));
        }
        var result = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            foreach (var (childId, childParent) in all.Where(n => n.Parent == parent))
                if (result.Add(childId))
                    stack.Push(childId);
        }
        return result;
    }

    /// <summary>Whether a playlist (not a folder) with this id exists. pl_item has no foreign key, so this
    /// check stops songs being written under a node that was deleted.</summary>
    private bool IsPlaylistLocked(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pl_node WHERE id = $id AND kind = 'playlist'";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private bool IsNowPlayingLocked(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT is_now_playing FROM pl_node WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        var v = cmd.ExecuteScalar();
        return v is not null && Convert.ToInt64(v) != 0;
    }

    private int NextNodeSort(long? parentId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = parentId is null
            ? "SELECT COALESCE(MAX(sort), 0) + 1 FROM pl_node WHERE parent_id IS NULL"
            : "SELECT COALESCE(MAX(sort), 0) + 1 FROM pl_node WHERE parent_id = $parent";
        if (parentId is not null)
            cmd.Parameters.AddWithValue("$parent", parentId);
        return (int)Convert.ToInt64(cmd.ExecuteScalar());
    }

    private int NextItemPosition(long playlistId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM pl_item WHERE playlist_id = $pl";
        cmd.Parameters.AddWithValue("$pl", playlistId);
        return (int)Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string KindText(PlaylistNodeKind kind) => kind == PlaylistNodeKind.Folder ? "folder" : "playlist";

    private static PlaylistNode ReadNode(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        ParentId: r.IsDBNull(1) ? null : r.GetInt64(1),
        Kind: r.GetString(2) == "folder" ? PlaylistNodeKind.Folder : PlaylistNodeKind.Playlist,
        Name: r.GetString(3),
        Sort: (int)r.GetInt64(4),
        IsNowPlaying: r.GetInt64(5) != 0,
        // Column 6. Null is the normal case, not the odd one. Every playlist that has not been given a
        // picture is stored as nothing at all.
        Cover: PlaylistCover.FromWire(r.IsDBNull(6) ? null : r.GetString(6)));
}

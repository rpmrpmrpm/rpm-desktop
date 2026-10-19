using System.Text;

namespace RPM.Core.Library;

/// <summary>
/// One folder or playlist, with everything it holds, as a tree travels between two libraries.
///
/// <para><see cref="Id"/> and <see cref="ParentId"/> are the ids of whichever library WROTE the row: this
/// one for a tree going out, the owner's for one coming in. They are here only to join the rows back into
/// a tree. A mirror files what it takes under ids of its own, so nothing else uses them.</para>
/// </summary>
/// <param name="Cover">How the row's picture is worked out. It is stated at every construction and never
/// defaulted. On a tree coming in, it is what says a row is one of the owner's generated lists
/// (<see cref="PlaylistCover.Generated"/>). A row that lost it by omission would be drawn as a mosaic of
/// album covers, with nothing to say it went wrong. <see cref="PlaylistCover.Automatic"/> is used for every row
/// the owner made, because their choice of picture is not sent over the wire.</param>
public sealed record PlaylistTreeRow(
    long Id, long? ParentId, PlaylistNodeKind Kind, string Name, int Sort,
    IReadOnlyList<PlaylistTreeEntry> Items, PlaylistCover Cover);

/// <summary>One entry in a travelling playlist: a song by id (with its path as the fallback), or a
/// reference to another playlist in the same tree, by that tree's id.</summary>
public sealed record PlaylistTreeEntry(
    PlaylistItemKind Kind, string? RpmId, string? RelPath, long? RefPlaylistId);

/// <summary>
/// A whole playlist tree, read out of one library and written into another.
///
/// <para>
/// What LAN sharing carries beyond the records themselves. A shelf borrowed from a friend is their
/// collection as they arranged it. Much of how they arranged it is their playlists. A wall of their albums
/// without the playlists is their music without the structure they gave it.
/// </para>
///
/// <para>
/// The tree is replaced whole rather than merged, because the borrower owns none of it. Every row in a
/// peer mirror's tree came from that peer, so there is no local edit for a merge to protect. The pinned
/// Now playing node is the one exception, in both directions. It is the player's queue, which belongs to
/// whoever is sitting at the machine. It is neither sent nor overwritten.
/// </para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// This library's folders and playlists, flat, with what each one holds.
    ///
    /// <para>Without the pinned Now playing node. A queue is what somebody is listening to at this moment,
    /// not something they made. It is no part of what a shelf is.</para>
    ///
    /// <para>Two queries rather than one per playlist. A tree has tens of rows, and the entries number in
    /// the hundreds. This runs on every catalogue a peer asks for.</para>
    /// </summary>
    public IReadOnlyList<PlaylistTreeRow> PlaylistTree()
    {
        using (_readGate.Take())
        {
            var nodes = new List<PlaylistNode>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, parent_id, kind, name, sort, is_now_playing, cover FROM pl_node " +
                    "WHERE is_now_playing = 0 ORDER BY sort, name COLLATE NOCASE";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    nodes.Add(ReadNode(reader));
            }

            var items = new Dictionary<long, List<PlaylistTreeEntry>>();
            using (var cmd = _read.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT playlist_id, item_kind, rpm_id, rel_path, ref_playlist_id FROM pl_item " +
                    "ORDER BY playlist_id, position, id";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var owner = reader.GetInt64(0);
                    if (!items.TryGetValue(owner, out var list))
                        items[owner] = list = [];

                    list.Add(new PlaylistTreeEntry(
                        Kind: reader.GetString(1) == "playlist" ? PlaylistItemKind.Playlist : PlaylistItemKind.Song,
                        RpmId: reader.IsDBNull(2) ? null : reader.GetString(2),
                        RelPath: reader.IsDBNull(3) ? null : reader.GetString(3),
                        RefPlaylistId: reader.IsDBNull(4) ? null : reader.GetInt64(4)));
                }
            }

            return [.. nodes.Select(n => new PlaylistTreeRow(
                n.Id, n.ParentId, n.Kind, n.Name, n.Sort,
                items.TryGetValue(n.Id, out var held) ? held : [], n.Cover))];
        }
    }

    /// <summary>
    /// How many of this shelf's playlists it is holding but not showing.
    ///
    /// <para>
    /// Zero on the listener's own collection, which shows everything it has. On a borrowed shelf whose
    /// owner has left the network, it is most of them. Most playlists hold at least one song that lives
    /// on that owner's disk. See <see cref="WithinReachLocked"/>.
    /// </para>
    ///
    /// <para>
    /// The count is shown to the user on purpose. A pane that quietly goes from a friend's twelve lists to
    /// none looks like the app losing them. Nothing on it suggests otherwise, although the rows are all
    /// still there and still theirs. One number is the difference between a shelf that has narrowed and a
    /// shelf that has broken.
    /// </para>
    /// </summary>
    public int PlaylistsOutOfReach()
    {
        var held = PlaylistTree().Count(row => row.Kind == PlaylistNodeKind.Playlist);
        var showing = GetPlaylistNodes()
            .Count(node => node.Kind == PlaylistNodeKind.Playlist && !node.IsNowPlaying);

        return Math.Max(0, held - showing);
    }

    /// <summary>
    /// Replaces this mirror's playlist tree with somebody else's.
    ///
    /// <para>
    /// Whole, and only when it differs. Whole, because nothing here belongs to the borrower. A peer
    /// mirror's tree came from that peer, so a merge would have nothing to merge against. Only when it
    /// differs, because a shelf's listing changes every time its owner adds a record. Rewriting the rows
    /// at that moment would give every folder in the panel a new id. The panel remembers which folders
    /// are open by id. The user would then see their friend's tree fold itself shut every few minutes,
    /// for reasons nothing on screen could explain.
    /// </para>
    ///
    /// <para>The pinned Now playing node is left exactly as it is, with everything in it. It is the queue
    /// of whoever is sitting at this machine. This tree is a listing of what somebody else made.</para>
    /// </summary>
    /// <returns>How many folders and playlists were written. Zero for a tree that already matched, so a
    /// caller can tell a shelf that moved from one that did not.</returns>
    public int MirrorPlaylistTree(IReadOnlyList<PlaylistTreeRow> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (Shape(PlaylistTree()) == Shape(tree))
            return 0;

        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            // The queue's own rows are the only ones that stay. The filter is "not the pinned node"
            // rather than a list of ids, so a mirror that somehow holds two pinned nodes keeps both.
            // This pass does not remove the second one.
            Run("DELETE FROM pl_item WHERE playlist_id NOT IN (SELECT id FROM pl_node WHERE is_now_playing = 1)");
            Run("DELETE FROM pl_node WHERE is_now_playing = 0");

            // Parents are set in a second pass, because a row can name a parent that has not been
            // inserted yet. The order a tree arrives in is the order its owner's rows came out, and
            // nothing guarantees that it runs top-down.
            var mine = new Dictionary<long, long>();
            foreach (var row in tree)
            {
                using var cmd = _connection.CreateCommand();
                // The cover is written in the same insert as the row it describes. A reader sees the old
                // tree whole or the new one whole. So it can never see one of the owner's generated lists
                // without the fact that it is one. Such a list would be drawn, and stored, as a mosaic.
                // See PlaylistCover.Generated.
                cmd.CommandText =
                    """
                    INSERT INTO pl_node (parent_id, kind, name, sort, is_now_playing, cover,
                                         created_utc_ticks, modified_utc_ticks)
                    VALUES (NULL, $kind, $name, $sort, 0, $cover, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$kind", row.Kind == PlaylistNodeKind.Folder ? "folder" : "playlist");
                cmd.Parameters.AddWithValue("$name", row.Name);
                cmd.Parameters.AddWithValue("$sort", row.Sort);
                cmd.Parameters.AddWithValue(
                    "$cover", row.Cover.ToWire() is { Length: > 0 } cover ? cover : DBNull.Value);
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                mine[row.Id] = Convert.ToInt64(cmd.ExecuteScalar());
            }

            foreach (var row in tree)
            {
                // A parent this tree does not contain leaves the row at the top, rather than pointing
                // it at nothing. A listing that arrived half-written is a shelf with a few playlists in
                // the wrong place. It is not a panel that draws nothing.
                if (row.ParentId is not { } theirs || !mine.TryGetValue(theirs, out var parent))
                    continue;

                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "UPDATE pl_node SET parent_id = $parent WHERE id = $id";
                cmd.Parameters.AddWithValue("$parent", parent);
                cmd.Parameters.AddWithValue("$id", mine[row.Id]);
                cmd.ExecuteNonQuery();
            }

            foreach (var row in tree)
            {
                var position = 0;
                foreach (var entry in row.Items)
                {
                    // A reference to a playlist that did not come with the tree is dropped. It cannot be
                    // followed or drawn. An item that points at nothing is worse than an item that is
                    // not there.
                    long? reference = entry.Kind == PlaylistItemKind.Playlist
                        ? entry.RefPlaylistId is { } target && mine.TryGetValue(target, out var found)
                            ? found
                            : null
                        : null;

                    if (entry.Kind == PlaylistItemKind.Playlist && reference is null)
                        continue;

                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText =
                        """
                        INSERT INTO pl_item (playlist_id, position, item_kind, rpm_id, rel_path, ref_playlist_id)
                        VALUES ($pl, $pos, $kind, $gid, $rel, $ref)
                        """;
                    cmd.Parameters.AddWithValue("$pl", mine[row.Id]);
                    cmd.Parameters.AddWithValue("$pos", position++);
                    cmd.Parameters.AddWithValue("$kind", entry.Kind == PlaylistItemKind.Playlist ? "playlist" : "song");
                    cmd.Parameters.AddWithValue("$gid", (object?)entry.RpmId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$rel", (object?)entry.RelPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$ref", (object?)reference ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return tree.Count;

            void Run(string sql)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// A tree written down without its ids: what it is made of, rather than which rows happen to hold it.
    /// Two trees with the same shape are the same tree in two libraries.
    ///
    /// <para>The ids cannot be in it. They are the one thing that is guaranteed to differ between the side
    /// that sent the tree and the side that stored it. A comparison that included them would say "changed"
    /// every time. That is the rewrite <see cref="MirrorPlaylistTree"/> exists to avoid. A nested reference
    /// is written as the referenced row's PLACE in the tree, for the same reason.</para>
    ///
    /// <para>
    /// A row whose parent is not IN the tree is a root here, because that is what the mirror makes of it.
    /// Both parts of that sentence matter. Getting either one wrong makes the shape unstable. A
    /// listing can arrive holding a node whose parent did not travel. For example, the parent may be filed
    /// under the pinned queue, or under a folder that was since deleted. <see cref="MirrorPlaylistTree"/>
    /// files such a node at the top, rather than pointing it at nothing. If the shape were walked from the
    /// root alone, the arriving tree would not contain that row at all, while the stored tree plainly does.
    /// The two shapes would never match. The whole tree would be written again on every pull, folding every
    /// folder in the panel shut each time.
    /// </para>
    ///
    /// <para>It is also the only reading under which this describes the whole tree. Anything left out of the
    /// shape would be a change that nothing notices.</para>
    /// </summary>
    private static string Shape(IReadOnlyList<PlaylistTreeRow> tree)
    {
        var children = tree.ToLookup(r => r.ParentId);
        var here = tree.Select(r => r.Id).ToHashSet();
        var place = new Dictionary<long, string>();
        Number(null, "");

        var text = new StringBuilder();
        Walk(null, 0);
        return text.ToString();

        // Every row's place is worked out before anything is written, because a reference can point
        // forwards, at a playlist that comes later in the walk.
        void Number(long? parent, string prefix)
        {
            var at = 0;
            foreach (var row in Ordered(parent))
            {
                var here = $"{prefix}{at++}.";
                place[row.Id] = here;
                Number(row.Id, here);
            }
        }

        void Walk(long? parent, int depth)
        {
            foreach (var row in Ordered(parent))
            {
                // The cover is part of the shape. If it were left out, a mirror written before a row
                // carried its generated-list cover would match the arriving tree and never be written
                // again. Those lists would keep the old cover for good.
                text.Append(new string(' ', depth)).Append(row.Kind == PlaylistNodeKind.Folder ? 'f' : 'p')
                    .Append(' ').Append(row.Name)
                    .Append(' ').Append(row.Cover.ToWire()).Append('\n');

                foreach (var entry in row.Items)
                    text.Append(new string(' ', depth + 1))
                        .Append(entry.Kind == PlaylistItemKind.Playlist
                            ? "> " + (entry.RefPlaylistId is { } id && place.TryGetValue(id, out var where) ? where : "?")
                            : "- " + (entry.RpmId ?? "") + "|" + (entry.RelPath ?? ""))
                        .Append('\n');

                Walk(row.Id, depth + 1);
            }
        }

        // The order the panel draws them in, which is the order the tree is read in. So a library that
        // stored the same tree writes the same shape, whatever order the rows arrived in.
        //
        // The top of the tree is every row with nowhere above it. Those are rows that say so, and rows
        // whose parent did not come with the listing. See the note above. The second kind must be
        // included. Without it, a settled tree would look changed on every pull.
        IEnumerable<PlaylistTreeRow> Ordered(long? parent) =>
            (parent is null
                ? tree.Where(r => r.ParentId is not { } up || !here.Contains(up))
                : children[parent])
            .OrderBy(r => r.Sort).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// The playlists a shelf may show right now, and the folders that still hold one.
    ///
    /// <para>
    /// Only asked when <see cref="CloudRowsOnly"/> is set. That is a borrowed shelf whose owner is not on
    /// this network. The rule the sharing was agreed under is that a record on somebody's own disk plays
    /// where that disk can be reached, and nowhere else. A playlist is only as reachable as the songs in
    /// it. A playlist with one track on the owner's laptop stops partway through. That is worse than a
    /// playlist that is not offered.
    /// </para>
    ///
    /// <para>
    /// It is worked out from the track rows every time it is asked, not carried as a flag. It is not a fact
    /// about the playlist. It is a fact about where its songs are, and that changes when the owner uploads
    /// one, offloads one, or comes back on the network. A playlist whose owner has put it in their cloud
    /// always passes this test, because putting a playlist up there also takes every track it plays.
    /// </para>
    ///
    /// <para>The pinned Now playing node is always in the set. It is the queue of whoever is sitting here,
    /// so it has nothing to do with whose shelf is on screen.</para>
    /// </summary>
    private HashSet<long> WithinReachLocked(IReadOnlyList<PlaylistNode> nodes)
    {
        var songs = new Dictionary<long, (int All, int Cloud)>();
        var refs = new Dictionary<long, List<long>>();

        using (var cmd = _read.CreateCommand())
        {
            // LEFT JOIN, so a song whose track row is not in this mirror at all counts against the
            // playlist exactly as an unreachable one does. It is a line that will not play either.
            cmd.CommandText =
                $"""
                 SELECT i.playlist_id, i.item_kind, i.ref_playlist_id,
                        CASE WHEN t.cloud_state = {(int)Cloud.CloudState.CloudOnly} THEN 1 ELSE 0 END
                   FROM pl_item i
                   LEFT JOIN tracks t ON t.rpm_id = i.rpm_id
                 """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var owner = reader.GetInt64(0);
                if (reader.GetString(1) == "playlist")
                {
                    if (!reader.IsDBNull(2))
                    {
                        if (!refs.TryGetValue(owner, out var list))
                            refs[owner] = list = [];
                        list.Add(reader.GetInt64(2));
                    }
                    continue;
                }

                var (all, cloud) = songs.GetValueOrDefault(owner);
                songs[owner] = (all + 1, cloud + (int)reader.GetInt64(3));
            }
        }

        var verdict = new Dictionary<long, bool>();
        var keep = new HashSet<long>(nodes.Where(n => n.IsNowPlaying).Select(n => n.Id));

        foreach (var node in nodes)
            if (node.Kind == PlaylistNodeKind.Playlist && !node.IsNowPlaying && Reaches(node.Id, []))
                keep.Add(node.Id);

        // A folder is kept when something under it is kept. The walk goes bottom-up, so a folder of
        // folders is kept if a playlist several levels down is kept.
        var children = nodes.ToLookup(n => n.ParentId);
        foreach (var root in nodes.Where(n => n.ParentId is null))
            Holds(root);

        return keep;

        bool Reaches(long id, HashSet<long> path)
        {
            if (verdict.TryGetValue(id, out var known))
                return known;

            // A cycle. The tree guards against making one. A listing that arrived with one is read as
            // contributing nothing, rather than as a reason to answer no. The songs on this playlist's
            // own line are what it is, and they have already been counted.
            if (!path.Add(id))
                return true;

            var (all, cloud) = songs.GetValueOrDefault(id);
            var answer = all == cloud &&
                         (!refs.TryGetValue(id, out var nested) || nested.All(r => Reaches(r, path)));

            path.Remove(id);

            // Stored only for a verdict reached outside a cycle. A verdict reached inside a cycle
            // depends on where the walk started. It answers only this walk, so it is not stored.
            if (path.Count == 0)
                verdict[id] = answer;

            return answer;
        }

        bool Holds(PlaylistNode node)
        {
            var any = keep.Contains(node.Id);
            foreach (var child in children[node.Id])
                any |= Holds(child);

            if (any && node.Kind == PlaylistNodeKind.Folder)
                keep.Add(node.Id);

            return any;
        }
    }
}

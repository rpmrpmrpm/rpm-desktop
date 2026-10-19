using RPM.Core.Library;
using RPM.Core.Splitting;
using RPM.Core.Threading;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using RPM.DataGrid.Tree;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// The import queue's datasource: the same virtualised tree that the library grid uses
/// (<see cref="VirtualTreeModel"/>, from RPM.DataGrid). It is fed from the DB-backed queue (the
/// temporary <c>queue</c> table of <see cref="LibraryDatabase"/>), not from an in-memory list.
///
/// <para>The tree is ARTIST, then ALBUM, then tracks. The grouping comes from the tags, not from the
/// folders the files came from. This pane shows what the library is about to look like. The folder a
/// file is in right now is the one thing that importing throws away. Files with no artist or no album
/// go into the "Unknown" groups. Those groups need a person's attention, so each one gets a node of its
/// own.</para>
///
/// <para>The group skeleton (one <see cref="ImportNode"/> per artist and album, from a single GROUP BY)
/// is kept in memory. The track rows are loaded page by page for each album, as the viewport reaches them.
/// So a queue of 20,000 rows or more draws at once, and search is a SQL word-start match, not an in-memory
/// rebuild.</para>
///
/// <para>Editing a materialised row writes straight back to the store. Its <see cref="ImportNode.QueueId"/>
/// links it to its table row. The edit also raises <see cref="Changed"/>, so the model can refresh its SQL
/// counts. Anything that changes what a row CLAIMS to be moves it to a different group. So the callers that
/// edit in bulk rebuild the tree rather than refreshing it.</para>
/// </summary>
[RpmBindable]
public sealed partial class QueueTreeSource : IDisposable
{
    private readonly LibraryService _library;
    private readonly PlaybackService _playback;
    private readonly DispatcherQueue _dispatcher;
    private readonly VirtualTreeModel _model;

    /// <summary>Groups the user collapsed, by group key. This survives rebuilds (a scan, a search
    /// edit).</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The leaf nodes this source has handed out, by store id.
    ///
    /// <para>A page is read again for many reasons: a field edit, a rearrange, another scan batch. Nearly
    /// all of them change what a row SAYS, not which rows there are. If a fresh node were built each time,
    /// the grid would get a different object for the same file. The grid's selection is a set of those
    /// objects. After an edit, every row would stay ticked but no row would show as selected. In a pane
    /// where the ticks ARE the selection, that looks like a broken selection. So a row that comes back is
    /// given back as the node it already was.</para>
    ///
    /// <para>The dictionary is kept small by <see cref="PruneLeaves"/> on every rebuild.</para>
    /// </summary>
    private readonly Dictionary<long, ImportNode> _leaves = new();

    /// <summary>The group skeleton currently in the tree. It is kept so that a change touching every row,
    /// such as unticking the queue, can correct the groups' own counters without reading anything
    /// again.</summary>
    private List<ImportNode> _roots = [];

    private QueueFilter _filter = QueueFilter.None;
    private CancellationTokenSource? _rebuildCts;

    /// <summary>The groups an untagged row is filed under.</summary>
    private readonly (string Artist, string Album) _unknown;

    /// <param name="unknown">The names of the groups an untagged row is filed under. The caller reads them
    /// on the interface thread, because the resource loader belongs to the UI framework, and the skeleton
    /// is built on a worker thread. The language does not change while the app runs, so reading them once
    /// is enough.</param>
    public QueueTreeSource(
        LibraryService library, PlaybackService playback, DispatcherQueue dispatcher,
        (string Artist, string Album) unknown)
    {
        _library = library;
        _playback = playback;
        _dispatcher = dispatcher;
        _unknown = unknown;
        _model = new VirtualTreeModel(
            childrenOf: n => ((ImportNode)n).Children,
            // Only album groups hold tracks. An artist holds albums, and the root holds artists.
            leafCountOf: n => n is ImportNode node ? node.VirtualTrackCount : 0,
            fetchLeaves: FetchRowsAsync,
            placeholderOf: PlaceholderFor,
            placeholderAtOf: PlaceholderAt,
            // The skeleton is rebuilt from a GROUP BY on every edit. Its nodes are new objects each time,
            // and only a KEY survives a rebuild. Without the next two callbacks, the grid could not find
            // the row it was looking at after a rebuild. It would start again at the top, so every metadata
            // edit would send the reader back to the top.
            // The grid asks this about anything it looks up, including containers and automation peers. So
            // it answers null for everything that is not one of our group rows. A group row carries a group
            // key and no track. A placeholder carries neither, and it must not be keyed as the
            // Unknown-artist bucket.
            groupKeyOf: n => n is ImportNode { Row: null, GroupArtist: not null } g
                ? GroupKeyOf(g)
                : null,
            anchorOf: n => n is ImportNode { Row: not null, LeafOffset: var offset,
                                             Parent: { GroupArtist: not null } album }
                ? (GroupKeyOf(album), offset)
                : null);
        _model.LeafPropertyChanged += (s, e) => LeafChanged?.Invoke(s, e);
    }

    /// <summary>
    /// The tree itself, for the grid that reads it directly rather than through an items source.
    ///
    /// <para>TEMPORARY, and only while both grids exist. RpmGrid reads the tree that this source already
    /// built. So turning its switch off goes back to exactly what shipped, and nothing here has two trees
    /// to keep in step. Remove this when the queue no longer uses the older grid path.</para>
    /// </summary>
    public VirtualTreeModel Tree => _model;

    /// <summary>Relayed from the model. A materialised leaf raised a property change, so a cell needs to
    /// re-read (for example the play or stop glyph). The model handles page-level virtualisation. This
    /// event is per row.</summary>
    public event System.ComponentModel.PropertyChangedEventHandler? LeafChanged;

    /// <summary>
    /// Raised after a row edit writes back to the store. It is also raised whenever the rows the tree shows
    /// are replaced (<see cref="Show"/>). The model re-reads the queue's chrome from the store when this
    /// fires: the counts, whether there is anything to import, the status line, and what the tag panel
    /// holds.
    ///
    /// <para>Every change to WHICH rows are queued ends in a replacement: a scan batch, a removal, a clear,
    /// an import, or a narrower filter. Because the event is raised there, the chrome follows the rows,
    /// whoever changed them. A removal must not rebuild the tree silently. Otherwise the header, the status
    /// line, the tag panel and the Import key would keep describing rows that are gone.</para>
    /// </summary>
    public event Action? Changed;

    /// <summary>Track rows currently loaded (the viewport's pages). It is used for cheap whole-grid glyph
    /// refreshes, such as the play or stop state.</summary>
    public IEnumerable<ImportNode> RealizedFileNodes => _model.CachedLeaves().OfType<ImportNode>();

    // ── skeleton ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the group skeleton from the queue and swaps it in. The GROUP BY, and the duplicate
    /// pass it depends on, run off the UI thread. A newer rebuild (the next search keystroke, or the next
    /// scan batch) cancels this one.</summary>
    public async Task RebuildAsync(QueueFilter filter, bool refreshDuplicates = true)
    {
        _rebuildCts?.Cancel();
        var cts = _rebuildCts = new CancellationTokenSource();

        // This must run while the model still holds the pages that PruneLeaves reads.
        PruneLeaves();

        if (!_library.IsOpen)
        {
            _filter = filter;
            _leaves.Clear();
            Show([]);
            return;
        }

        var f = filter;
        var labels = _unknown;
        var built = await Cancellable.RunAsync(
            () =>
            {
                // What a row claims to be decides both the group it lands in and whether anything else
                // already holds it. So the duplicate pass runs first, on the same trip.
                if (refreshDuplicates)
                    _library.Db.QueueRefreshDuplicates();
                return BuildSkeleton(_library.Db.Queue(f).Groups(), labels);
            }, cts.Token);

        if (built is not { Ran: true, Value: { } roots } || cts.IsCancellationRequested)
            return;

        // The narrowing (the filter) and the skeleton are set together, at the end. If the filter were set
        // on the way in, it would describe a tree that is not on screen yet, for as long as the GROUP BY
        // takes. A page fetched by a scroll in that window would be read under the NEW filter, against the
        // OLD tree. The rows would be right and the offsets wrong, in whichever record the reader is
        // looking at. The filter describes what the tree is showing, so it changes when the tree does.
        _filter = filter;
        Show(roots);
    }

    /// <summary>Inserts a scanned batch (de-duplicated by path in the store) and rebuilds the skeleton, so
    /// the new groups and counts show. Returns how many rows were actually added.</summary>
    public async Task<int> InsertAsync(IReadOnlyList<QueueTrack> rows)
    {
        if (!_library.IsOpen || rows.Count == 0)
            return 0;
        var added = await Task.Run(() => _library.Db.QueueInsertMany(rows));
        await RebuildAsync(_filter);
        return added;
    }

    /// <summary>Empties the queue and clears the tree.</summary>
    public async Task ClearAsync()
    {
        if (_library.IsOpen)
            await Task.Run(() => _library.Db.QueueClear());
        _leaves.Clear();
        await RebuildAsync(_filter);
    }

    /// <summary>Removes rows by store id and rebuilds, because the counts and groups may have
    /// changed.</summary>
    public async Task RemoveAsync(IReadOnlyList<long> ids)
    {
        if (!_library.IsOpen || ids.Count == 0)
            return;
        await Task.Run(() => _library.Db.QueueRemove(ids));
        foreach (var id in ids)
            _leaves.Remove(id);
        await RebuildAsync(_filter);
    }

    /// <summary>
    /// Puts <paramref name="roots"/> on display and raises <see cref="Changed"/>. This is the only place
    /// where the tree's rows are replaced. So nothing can change what the queue shows without
    /// <see cref="Changed"/> being raised.
    /// </summary>
    private void Show(List<ImportNode> roots)
    {
        _roots = roots;
        _model.SetRoots(roots);
        Changed?.Invoke();
    }

    // ── aggregates (SQL, not a LINQ pass over materialised rows) ───────────────────────────────────

    public int TotalCount => _library.IsOpen ? _library.Db.QueueCount() : 0;
    public int CheckedCount => _library.IsOpen ? _library.Db.QueueCheckedCount() : 0;
    public int NeedsRecognitionCount => _library.IsOpen ? _library.Db.QueueNeedsRecognitionCount() : 0;
    public int ImportableCount => _library.IsOpen ? _library.Db.QueueImportableCount() : 0;
    public int SplitCandidateCount => _library.IsOpen ? _library.Db.QueueSplitCandidateCount() : 0;

    /// <summary>
    /// A record's rows are in a different order now, and the store already says so.
    ///
    /// <para>No re-read and no reset. The rows are the nodes the grid is already holding. The only change
    /// is which slot each one sits in, and that is all the tree is told. A <see cref="Refresh"/> here would
    /// drop every page in the queue, and put the reader back wherever the list decided to land. The reader
    /// only wants to move one track inside one record.</para>
    ///
    /// <para><paramref name="settled"/> is the record's rows in the order they now read. The first of them
    /// goes <paramref name="firstOffset"/> places into the record.</para>
    /// </summary>
    public void ReorderLeavesInPlace(ImportNode record, int firstOffset, IReadOnlyList<ImportNode> settled)
    {
        for (var i = 0; i < settled.Count; i++)
            settled[i].LeafOffset = firstOffset + i;

        _model.ReorderLeaves(record, firstOffset, settled);
        Changed?.Invoke();
    }

    /// <summary>
    /// The whole queue has just been ticked or unticked in the store, in one statement. This tells the rows
    /// and the groups that are in hand.
    ///
    /// <para>No reset. <see cref="Refresh"/> drops only the PAGES, and the skeleton keeps its own counters.
    /// So a queue cleared that way would leave every record and artist showing the tick it had before. It
    /// would also throw away the reader's place.</para>
    ///
    /// <para>The rows read their own tick, rather than being given <paramref name="on"/>. The statement
    /// that has just run does not necessarily tick everything. See <see cref="RereadChecksInPlace"/>. The
    /// groups' counters are set from <paramref name="on"/>, which is correct for them. The skeleton's
    /// counts come from the same GROUP BY that the store answers with, and a rebuild corrects them.</para>
    /// </summary>
    public void SetAllChecksInPlace(bool on)
    {
        RereadChecksInPlace();

        foreach (var root in _roots)
            root.SetCheckedThroughout(on);

        Changed?.Invoke();
    }

    // ── skeleton build (artist → album, from the store's group counts) ─────────────────────────────

    private List<ImportNode> BuildSkeleton(
        IReadOnlyList<QueueGroup> groups, (string Artist, string Album) unknown)
    {
        var roots = new List<ImportNode>();
        var artists = new Dictionary<string, (ImportNode Node, int Checked, int Total)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var g in groups)
        {
            if (!artists.TryGetValue(g.Artist, out var entry))
            {
                entry = (GroupNode(g.Artist, album: null, trackCount: 0, depth: 0, unknown), 0, 0);
                artists[g.Artist] = entry;
                roots.Add(entry.Node);
            }

            var albumNode = GroupNode(g.Artist, g.Album, g.TrackCount, depth: 1, unknown, g.DiscCount,
                                      g.AlbumIntentId != 0);
            // Counted over what a tick can land on, not over every row. A record that still has the album
            // file it was cut out of has one row that a tick can never reach. Counting that row in the box
            // would only ever show a dash.
            albumNode.SetCheckState(g.Checked, g.Tickable);
            albumNode.Parent = entry.Node;
            entry.Node.Children.Add(albumNode);

            // The artist's tick state is the sum of its albums' states, and the same single read already
            // gives us those numbers. An artist row that asked the store for its own would be one query per
            // row.
            artists[g.Artist] = (entry.Node, entry.Checked + g.Checked, entry.Total + g.Tickable);
        }

        foreach (var (node, chk, total) in artists.Values)
            node.SetCheckState(chk, total);

        return roots;
    }

    private ImportNode GroupNode(
        string artist, string? album, int trackCount, int depth, (string Artist, string Album) unknown,
        int discCount = 0, bool hasAlbumIntent = false)
    {
        // A set says so on its own row: "Album · 2 discs". The tree has no disc level, and the library does
        // not file discs as albums either. So this text, and the "2-01" on each leaf, are how a record in
        // two boxes stays one record on screen and still reads as two.
        var albumName = album is null
            ? (artist.Length > 0 ? artist : unknown.Artist)
            : (album.Length > 0 ? album : unknown.Album);
        if (album is not null && discCount > 1)
            albumName = $"{albumName}  \u00b7  {Loc.Format("Queue_DiscCount", discCount)}";

        var node = new ImportNode
        {
            Name = albumName,
            Depth = depth,
            GroupArtist = artist,
            GroupDiscCount = discCount,
            HasAlbumIntent = hasAlbumIntent,
            GroupAlbum = album,
            VirtualTrackCount = trackCount,
            IsExpanded = !_collapsed.Contains(CollapseKey(artist, album)),
        };

        // A group's tick is written to the STORE in one statement. Its leaves are paged, not in Children,
        // so there is no in-memory list to walk. Then the rows in hand re-read it. This does not call
        // _model.Refresh(). Refresh drops every cached page and raises a reset, which throws the reader
        // back to wherever the list decides to land. Ticking a checkbox must not move the page under
        // someone's hands.
        node.FolderCheckAll = @checked =>
        {
            if (!_library.IsOpen)
                return;
            _library.Db.Queue(_filter).SetCheckedInGroup(artist, album, @checked);
            RereadChecksInPlace();
            Changed?.Invoke();
        };
        node.PropertyChanged += OnGroupPropertyChanged;
        return node;
    }

    /// <summary>
    /// Brings the leaf rows that this source is holding back in step with the store's ticks.
    ///
    /// <para>This is called after a write that changed many rows at once, such as a group's box or the
    /// whole queue's tick. Those writes run as one statement and never touch the rows in memory. So the
    /// rows in memory have to be told. What they are told is read back from the store, not assumed.</para>
    ///
    /// <para>The new ticks are not inferred from the tree. That would be wrong for two reasons. The tree is
    /// rebuilt from a GROUP BY on every scan batch, so which rows sit under which group is only settled
    /// between two rebuilds. A large import leaves no such gap, so a record ticked during a scan could
    /// leave its cached tracks looking unticked. Also, a group tick skips the rows it cannot reach. The
    /// album file that a record was cut out of is never ticked (see TickableSql in the store). Giving every
    /// leaf the group's value would tick rows that the store had just refused to tick.</para>
    ///
    /// <para>Reading the answer back is true whenever it is asked. It is the same answer the store gives
    /// the next page fetch, so the screen cannot disagree with the queue.</para>
    ///
    /// <para>This overload works on <see cref="_leaves"/>, the leaf nodes this source is holding. That is
    /// not every row the reader can see. See <see cref="RereadChecks"/>, which the view calls with the rows
    /// the grid is actually showing.</para>
    /// </summary>
    private void RereadChecksInPlace() => Reread(_leaves.Values);

    /// <summary>
    /// The same as above, for rows that the caller holds and this source may not hold.
    ///
    /// <para>The view calls this with the rows that the GRID is showing. This is not a small refinement.
    /// The model keeps a limited number of leaf PAGES. A queue of a few thousand small records is a few
    /// thousand separate blocks, so a page is often evicted while its containers are still on screen.
    /// Without this method, the block the reader is looking at could be missing from the cache. It would be
    /// fetched and evicted again, over and over. Anything that asks the cache which rows are in front of
    /// the reader gets an answer about some other part of the queue.</para>
    /// </summary>
    public void RereadChecks(IEnumerable<object> rows)
    {
        var nodes = new List<ImportNode>();
        foreach (var row in rows)
        {
            if (row is ImportNode { Row: not null } leaf)
                nodes.Add(leaf);
        }
        Reread(nodes);
    }

    private void Reread(IEnumerable<ImportNode> leaves)
    {
        if (!_library.IsOpen)
            return;

        // Compared by node identity. ImportNode does not override Equals, so a HashSet compares references.
        // The grid's rows and this source's own are usually the same objects. A row named twice would
        // otherwise be read twice.
        var seen = new HashSet<ImportNode>();
        var held = new List<ImportNode>();
        foreach (var leaf in leaves)
        {
            if (leaf.Row is not null && seen.Add(leaf))
                held.Add(leaf);
        }
        if (held.Count == 0)
            return;

        // Read in slices of 400. Every id in the IN list is a bound parameter, and SQLite limits how many
        // there can be.
        const int Slice = 400;

        for (var at = 0; at < held.Count; at += Slice)
        {
            var these = held.GetRange(at, Math.Min(Slice, held.Count - at));
            var ids = these.ConvertAll(l => l.QueueId);
            var state = _library.Db.QueueCheckedByIds(ids);

            foreach (var leaf in these)
            {
                // A row the store no longer has is not answered for, and it is left alone. It is on its way
                // out of the queue, and a rebuild is already coming for it.
                if (!state.TryGetValue(leaf.QueueId, out var on) || leaf.Row!.IsChecked == on)
                    continue;

                leaf.Row.SetCheckedQuietly(on);   // read from the store; no write-back
                leaf.RaiseChecked();
            }
        }
    }

    // A group's identity across rebuilds: the same two keys that the store groups by, with the LEVEL in
    // front. The level is not decoration. An artist node has a null album, and an album node in the Unknown
    // Album bucket has an empty one. Without the level, those two would have the same key, and an artist
    // would answer for its own unknown-album child. The unit separator keeps an artist called "X/Y" from
    // colliding with an album, which a path-shaped key could not.
    private static string ArtistKey(string artist) => "a\u001f" + artist;
    private static string AlbumKey(string artist, string album) => "b\u001f" + artist + "\u001f" + album;

    /// <summary>Only ever called for a real group node, so the artist key is present by
    /// construction.</summary>
    private static string GroupKeyOf(ImportNode group)
        => group.GroupAlbum is { } album
            ? AlbumKey(group.GroupArtist!, album)
            : ArtistKey(group.GroupArtist!);

    /// <summary>Identifies a group across rebuilds. The unit separator keeps an artist called "X/Y"
    /// from colliding with an album, which a path-shaped key could not.</summary>
    private static string CollapseKey(string artist, string? album) => $"{artist}\u001f{album ?? ""}";

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ImportNode.IsExpanded)
            || sender is not ImportNode { GroupArtist: { } artist } node)
            return;
        var key = CollapseKey(artist, node.GroupAlbum);
        if (node.IsExpanded)
            _collapsed.Remove(key);
        else
            _collapsed.Add(key);
    }

    // ── leaf paging ────────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<object>> FetchRowsAsync(object? group, int offset, int limit, CancellationToken ct)
    {
        // Only an album group has tracks of its own; anything else pages nothing.
        if (group is not ImportNode { GroupArtist: { } artist, GroupAlbum: { } album } albumNode)
            return [];

        var f = _filter;
        var tracks = await ReadPageAsync(artist, album, offset, limit, f);

        var rows = new List<object>(tracks.Count);
        foreach (var qt in tracks)
        {
            var at = offset + rows.Count;

            // A row that has been here before comes back as itself. The store is where these values have
            // just come from, so they are loaded QUIETLY. Through the ordinary setters, every fetch would
            // write every row it read straight back to the store. A fetch that started before an edit could
            // also land after it, and put the old value back over the new one.
            if (_leaves.TryGetValue(qt.Id, out var known) && known.Row is { } kept)
            {
                kept.LoadStoredQuietly(qt.Wanted, qt.IsChecked);
                ApplyStored(kept, qt);
                known.Reseat(LeafName(kept, qt, albumNode.GroupDiscCount), albumNode, at);
                rows.Add(known);
                continue;
            }

            var row = MaterializeRow(qt);
            var node = new ImportNode
            {
                Name = LeafName(row, qt, albumNode.GroupDiscCount),
                Depth = albumNode.Depth + 1,
                Row = row,
                Playback = _playback,
                Parent = albumNode,
                QueueId = qt.Id,
                LeafOffset = at,
            };

            // Any wanted edit or tick on a visible row writes straight back to the store. The row's own
            // mark re-reads, because a field that was filled in is a question mark that has just been
            // answered. This is wired here, and only here. A row that is read again already has these
            // handlers, and a second pair would write it back twice for one edit. The node hears the field
            // change itself (ImportNode.OnRowChanged) and re-reads its line. All that is left here is the
            // store write.
            row.WantedChanged += () => WriteBack(row);
            row.CheckedChanged += () => WriteChecked(row);

            _leaves[qt.Id] = node;
            rows.Add(node);
        }
        return rows;
    }

    /// <summary>
    /// Reads a page from the store, and reads it again if it went out of date while it was being read.
    ///
    /// <para>The read runs on a worker thread, and an edit runs on the UI thread. A page whose SELECT ran
    /// before a renumbering, and whose rows arrive after it, carries the OLD numbers. Those numbers would
    /// go straight onto the rows the grid is holding. The edit would be saved but would look undone.
    /// Nothing would correct it, because from the tree's point of view the page has just been read and is
    /// as fresh as it gets.</para>
    ///
    /// <para>So the store's revision is taken on the way in, and checked again on the way out. The read
    /// itself is done under the store's lock, so an unchanged revision means no rewrite could have slipped
    /// past it. It tries at most twice. If a rewrite arrives on every attempt, something is writing all the
    /// time, and a page from a queue that is always moving is what a rebuild is for.</para>
    /// </summary>
    private async Task<IReadOnlyList<QueueTrack>> ReadPageAsync(
        string artist, string album, int offset, int limit, QueueFilter f)
    {
        for (var attempt = 0; ; attempt++)
        {
            var revision = _library.Db.QueueRevision;
            var tracks = await Task.Run(
                () => _library.Db.Queue(f).GroupPage(artist, album, offset, limit));

            if (attempt >= 2 || _library.Db.QueueRevision == revision)
                return tracks;
        }
    }

    /// <summary>
    /// Drops remembered leaves, and keeps only the ones the tree is still holding.
    ///
    /// <para>This runs at the top of a rebuild, while the pages that answer the question are still in
    /// memory. A node is kept only while the grid may still hold it. Rows scrolled past long ago, and rows
    /// that have left the queue, are of no use to anybody. This keeps a queue of 20,000 rows from leaving
    /// 20,000 rows behind. What is kept is limited by what the model's own pages cost.</para>
    /// </summary>
    private void PruneLeaves()
    {
        if (_leaves.Count == 0)
            return;

        var held = new HashSet<long>();
        foreach (var leaf in RealizedFileNodes)
            held.Add(leaf.QueueId);

        foreach (var id in _leaves.Keys.ToList())
        {
            if (!held.Contains(id))
                _leaves.Remove(id);
        }
    }

    /// <summary>What a track is called in the tree. The rule is on the node, because a row that is
    /// renumbered while it is on screen must re-title itself without a fetch.</summary>
    private static string LeafName(TrackRow row, QueueTrack stored, int albumDiscCount)
        => ImportNode.LeafTitle(row, stored.FileName, albumDiscCount);

    /// <summary>Rebuilds a <see cref="TrackRow"/> from a stored queue row, with its id, its size, its split
    /// flag, and what the store worked out about it being a duplicate. It is public so that the model can
    /// build rows for the checked and importable set actions.</summary>
    public static TrackRow MaterializeRow(QueueTrack qt)
    {
        var row = TrackRow.FromStored(qt.Current, qt.Wanted, qt.IsChecked);
        ApplyStored(row, qt);
        return row;
    }

    /// <summary>The stored facts that sit beside the wanted side: the file size, where the file came in
    /// from, whether anything else already holds it, and whether it is a whole record in one file. None of
    /// these are edited on the row, so they are set directly. The duplicate verdict is read again on every
    /// fetch, because the pass that works it out runs on every rebuild.</summary>
    private static void ApplyStored(TrackRow row, QueueTrack qt)
    {
        row.QueueId = qt.Id;
        row.SizeBytes = qt.SizeBytes;
        row.Folder = qt.Folder;
        row.Duplicate = qt.Duplicate;
        row.SplitCandidate = qt.SplitHint != 0
            ? new AlbumFileCandidate(qt.Path, qt.SplitDuration, qt.SplitCue, (AlbumFileHint)qt.SplitHint)
            : null;
    }

    /// <summary>
    /// Writes a row's TICK back to the store, and nothing else.
    ///
    /// <para>This is separate from <see cref="WriteBack"/>, because a tick is not an edit. WriteBack reads
    /// the row's identity back from the store, writes every wanted field, and rebuilds the row's search
    /// text, because that is what a changed artist or title needs. A tick changes one integer. It cannot
    /// move a row to another group, or change what it is a copy of. So none of that work applies to a tick.
    /// A drag across a run of rows ticks one row per step, and each step would otherwise pay for an edit
    /// that never happened.</para>
    /// </summary>
    public void WriteChecked(TrackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!_library.IsOpen || row.QueueId == 0)
            return;

        _library.Db.QueueSetChecked(row.QueueId, row.IsChecked);
        Changed?.Invoke();
    }

    /// <summary>Writes a row's wanted side and tick back to the store, keyed by its QueueId. Then it
    /// signals the model to refresh its counts.</summary>
    /// <returns>Whether the row moved. It is now filed under a different artist or record, or it is a copy
    /// of something else than before. False means the tree it sits in is still right, and the caller can
    /// re-read the pages instead of rebuilding the skeleton.</returns>
    public bool WriteBack(TrackRow row)
    {
        if (!_library.IsOpen || row.QueueId == 0)
            return false;
        var moved = _library.Db.QueueUpdateWanted(row.QueueId, row.IsChecked, row.ToWantedTrackInfo());
        Changed?.Invoke();
        return moved;
    }

    /// <summary>Re-fetches the visible pages without rebuilding the skeleton. Use this for a change that
    /// touched leaf data but did not change what any row claims to be. Any edit to ARTIST, ALBUM or TITLE
    /// moves rows between groups, and it changes who is a duplicate of what. So those edits rebuild instead
    /// (see <see cref="RebuildAsync"/>).</summary>
    public void Refresh()
    {
        _model.Refresh();
        Changed?.Invoke();
    }

    /// <summary>
    /// Writes a batch of edited rows back, and answers the one question the tree cares about afterwards:
    /// did any of them MOVE?
    ///
    /// <para>There is one write per row, and ONE <see cref="Changed"/> at the end. If it were raised per
    /// row, a mass edit of four hundred tracks would re-read the counts four hundred times. Each count is a
    /// scan of the whole queue, and each one would say what the last one said.</para>
    /// </summary>
    public bool WriteBackMany(IReadOnlyList<TrackRow> rows)
    {
        if (!_library.IsOpen)
            return false;

        // Not short-circuited: every row has to be written, whatever the first one answered.
        var moved = false;
        foreach (var row in rows)
        {
            if (row.QueueId == 0)
                continue;
            moved |= _library.Db.QueueUpdateWanted(row.QueueId, row.IsChecked, row.ToWantedTrackInfo());
        }

        Changed?.Invoke();
        return moved;
    }

    /// <summary>
    /// Puts edited values onto the rows the grid is already holding, and touches nothing else.
    ///
    /// <para>The edit ran on rows that were read from the store. The ticked set is fetched, not taken out
    /// of the tree. So the nodes on screen still show what those rows said before. The obvious answer is
    /// <see cref="Refresh"/>, and it is the wrong one. It drops every page in the queue and reads them
    /// again. That loses the reader's place just to change a genre on four rows. Here the values are copied
    /// onto the same objects that the cells are bound to, and each row raises once.</para>
    ///
    /// <para>This is only for edits that did not move anything. A row whose group, disc or title changed
    /// sits somewhere else now, and where a row sits is the skeleton's job. See
    /// <see cref="WriteBackMany"/>.</para>
    /// </summary>
    public void ApplyEditsInPlace(IReadOnlyList<TrackRow> edited)
    {
        var byId = new Dictionary<long, TrackRow>();
        foreach (var row in edited)
        {
            if (row.QueueId != 0)
                byId[row.QueueId] = row;
        }
        if (byId.Count == 0)
            return;

        foreach (var leaf in RealizedFileNodes)
        {
            if (leaf.Row is not { QueueId: > 0 } shown)
                continue;
            if (!byId.TryGetValue(shown.QueueId, out var edit) || ReferenceEquals(edit, shown))
                continue;

            // The row raises its own change events. The node listens for them, re-titles itself, and raises
            // for every cell on the line (see ImportNode.SyncFromRow). Raising only the mark and the tick
            // here would leave a track number written across four hundred rows in the store, but not on the
            // screen.
            shown.LoadStoredQuietly(edit.ToWantedTrackInfo(), edit.IsChecked);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Where a queue row is in the tree right now, expanding whatever hides it, or -1.
    ///
    /// <para>This answers "it recognised my track and now I cannot find it". A recognised row moves to the
    /// artist it turned out to be. In a big tree, that row leaves the viewport without warning. So the grid
    /// works out the row it moved to, and scrolls there, instead of leaving the user to search. This costs
    /// one store read and a key lookup. No page is fetched to find it.</para>
    /// </summary>
    public int LocateRow(long queueId)
    {
        if (!_library.IsOpen || queueId == 0)
            return -1;
        if (_library.Db.QueueRowGroup(queueId) is not { } g)
            return -1;
        var offset = _library.Db.QueueRowOffsetInGroup(queueId);
        if (offset < 0)
            return -1;

        var key = AlbumKey(g.Artist, g.Album);

        // A row inside a collapsed group has no index at all, so its ancestors are opened first. Taking the
        // user to a row means showing it, not telling them it is somewhere under a folded branch.
        if (_model.GroupByKey(key) is not ImportNode album)
        {
            ExpandTowards(g.Artist);
            if (_model.GroupByKey(key) is not ImportNode reopened)
                return -1;
            album = reopened;
        }
        if (!album.IsExpanded)
            album.IsExpanded = true;
        for (var p = album.Parent; p is not null; p = p.Parent)
            if (!p.IsExpanded)
                p.IsExpanded = true;

        return _model.FlatIndexOf(key, offset);
    }

    private void ExpandTowards(string artist)
    {
        if (_model.GroupByKey(ArtistKey(artist)) is ImportNode node && !node.IsExpanded)
            node.IsExpanded = true;
    }

    // A FRESH node for each call, never a shared per-group instance. The model caches one placeholder per
    // row slot, and duplicate item objects would break the ListView's identity handling.
    private object PlaceholderFor(object? group) => new ImportNode
    {
        Name = "…",
        Depth = group is ImportNode g ? g.Depth + 1 : 0,
    };

    private object PlaceholderAt(object? group, int index) => new ImportNode
    {
        Name = $"{index + 1:00} …",
        Depth = group is ImportNode g ? g.Depth + 1 : 0,
    };

    public void Dispose() => _rebuildCts?.Cancel();
}

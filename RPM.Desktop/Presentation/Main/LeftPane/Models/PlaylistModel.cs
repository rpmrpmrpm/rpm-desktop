using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>What a row in the playlist tree is.</summary>
public enum PlaylistRowKind
{
    Folder,
    Playlist,
    NowPlaying,
    Song,
    Ref,

    /// <summary>A playlist that the library writes for itself. See <see cref="SmartPlaylistKind"/>. It is
    /// pinned under Now playing, and nobody can rename, refile or fill it.</summary>
    Smart,

    /// <summary>The one pinned folder that the generated playlists live in. It is the only row in the tree
    /// that is a folder without being a <c>pl_node</c>. It holds every <see cref="Smart"/> row and nothing
    /// else. It is not a <see cref="Folder"/>, and that is why <c>CanTakeChildren</c> is false. That stops
    /// a playlist somebody made from being dropped into it.</summary>
    SmartFolder,
}

/// <summary>
/// The playlist panel's model. It turns the user's folder and playlist tree into one list that knows
/// which folders are expanded, for the list view. It also holds the operations that the panel and the
/// context menus call. A playlist resolves to a play order when it is needed. Nested playlists are
/// expanded, cycles are guarded, and missing files are skipped. That order feeds RPaMp, which is the
/// "Now playing" queue. "Now playing" is pinned at the top, and it mirrors the live player queue.
/// </summary>
[RpmBindable]
public sealed partial class PlaylistModel : INotifyPropertyChanged, IDisposable
{
    private readonly LibraryService _library;
    private readonly RPaMpService _amp;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly List<PlaylistTreeItem> _roots = new();

    public PlaylistModel(LibraryService library, RPaMpService amp, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _library = library;
        _amp = amp;
        _dispatcher = dispatcher;
        _amp.Transport.Playlist.CollectionChanged += OnNowPlayingChanged;
        _library.Changed += OnLibraryChanged;

        // The cloud changes without the library changing. For example, a track leaves the queue, or a
        // removal starts. The badges must follow that in place, not by rebuilding the tree.
        _cloud = App.Services.GetRequiredService<CloudSyncService>();
        _cloud.Changed += OnCloudChanged;

        // The sizes are read on a worker thread and land when they are ready. So this is a background pass
        // like any other, and it asks before it runs. It once held the read gate for about a second during
        // start-up, while the interface's own artist art waited behind it.
        //
        // The pass runs at Wave.First and Phase.Loading, not at Ready. These numbers ARE the screen. A list
        // with no size next to it looks broken. The pass must not take the gate while the window is still
        // building, and the interface reading decides that.
        _sizes = App.Services.GetRequiredService<Services.Shell.Governor>().Pass(
            "playlist sizes",
            Services.Shell.Lane.Library,
            Services.Shell.Wave.First,
            notBefore: Services.Shell.Phase.Loading);

        Reload();
    }

    /// <summary>What the sizes read asks before it takes the gate.</summary>
    private readonly Services.Shell.Sweep _sizes;

    private readonly CloudSyncService _cloud;

    private void OnCloudChanged() => _dispatcher.TryEnqueue(RefreshCloudState);

    /// <summary>Whether a reload is already sitting on the dispatcher, waiting to run.</summary>
    private int _reloadQueued;

    /// <summary>
    /// The library changed. Library changes (import, sync) can arrive off the UI thread. The tree that a
    /// reload rebuilds is bound to the UI, so the reload goes to the dispatcher.
    ///
    /// <para><b>One reload at a time.</b> At start-up the library raises this several times, as the sync,
    /// the mirror and the album index each land. Each one would queue a whole reload: the tree is read
    /// again, and the generated lists are sized again. On a large collection, one reload can take seconds
    /// on the drawing thread. The flag stops that. It is cleared where it is read, so a change that arrives
    /// while a reload is RUNNING queues the next reload instead of being dropped.</para>
    /// </summary>
    private void OnLibraryChanged()
    {
        if (Interlocked.Exchange(ref _reloadQueued, 1) == 1)
            return;

        _dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _reloadQueued, 0);
            Reload();
        });
    }

    /// <summary>The tree flattened to the rows that are visible now, respecting each folder's and
    /// playlist's expansion. This is the list view's source.</summary>
    /// <remarks>It has its own collection type, so that a {Binding} can hand it to the list under Native
    /// AOT. See <see cref="Common.PlaylistTreeList"/>.</remarks>
    public Common.PlaylistTreeList Visible { get; } = new();

    /// <summary>
    /// The top of the tree: Now playing, the generated lists' folder, and the folders and playlists that
    /// nobody has filed inside another one.
    ///
    /// <para>This is not <see cref="Visible"/>, and the difference is the reason it exists. Visible is the
    /// tree flattened by expansion. It holds every row the list view is showing, at every depth. The icon
    /// grid shows one folder at a time, and it asks each row for its own children. So it needs to start
    /// from the level above the first of them. The flattened list cannot name that level.</para>
    /// </summary>
    public IReadOnlyList<PlaylistTreeItem> Roots => _roots;

    public bool IsOpen => _library.IsOpen;

    /// <summary>
    /// Whether the tree on screen belongs to somebody else. A borrowed shelf brings its owner's playlists
    /// with it. While one is open, this panel shows THEIR arrangement of THEIR records. Nothing here may be
    /// renamed, refiled, filled or deleted. The operations all turn back at this check. The panel hides its
    /// toolbar and menus, rather than offering something that would do nothing.
    ///
    /// <para>This is asked of the library every time, and it is not kept in a field. The answer changes
    /// without this model being told. For example, the user closes a shelf and their own tree comes back. A
    /// flag written when the panel was last built would still describe the shelf from before.</para>
    /// </summary>
    public bool IsBorrowed => _library.IsPeerView;

    // ── building the tree ──

    /// <summary>
    /// The row that each node already has, kept across reloads.
    ///
    /// <para>
    /// A row is more than a name. It carries whether the user has that folder open, and where it stands
    /// with the cloud. If every reload built fresh rows, all of that would be lost. A new row starts
    /// expanded, so a rename or an import would quietly reopen every folder that the user had closed.
    /// Reloads are frequent, so the rows must be reused, not rebuilt.
    /// </para>
    /// </summary>
    private readonly Dictionary<long, PlaylistTreeItem> _itemsByNode = new();

    public void Reload()
    {
        _roots.Clear();
        if (!_library.IsOpen)
        {
            _itemsByNode.Clear();
            _rootNodeOrder.Clear();
            _nowPlayingNode = null;
            _smartTotals.Clear();
            _offeredIds = [];
            // Nothing in flight may land on this. A read that was started against the shelf that has just
            // been closed would put its rows back on a tree that should be empty.
            _totalsRead++;
            ReadTotals();
            RebuildVisible();
            return;
        }

        // The shelf, not the library. While a borrowed shelf is open, this panel draws its owner's
        // playlists. Which of them appear at all is the shelf's decision. A list that holds a song on a
        // computer that has left the network is not offered, because it would stop partway through. See
        // LibraryDatabase.CloudRowsOnly.
        var db = _library.Shelf;
        var nodes = db.GetPlaylistNodes();

        // Which of them hold an explicit song, for the whole tree at once. This is read here, not per row.
        // The loop already holds every node, and one query is enough for a fact that every row needs.
        var marked = db.QueryExplicitPlaylists();

        var byId = new Dictionary<long, PlaylistTreeItem>();

        // Folders and playlists first, linked into a tree. A node that is still here keeps its row. A node
        // whose kind has changed gets a new row, because a row's kind is fixed by what it is.
        foreach (var n in nodes)
        {
            var kind = n.IsNowPlaying ? PlaylistRowKind.NowPlaying
                : n.Kind == PlaylistNodeKind.Folder ? PlaylistRowKind.Folder
                : PlaylistRowKind.Playlist;

            var item = _itemsByNode.TryGetValue(n.Id, out var existing) && existing.Kind == kind
                ? existing
                : new PlaylistTreeItem(this, kind, n.Name) { NodeId = n.Id };

            item.Name = n.Name;
            // How this node has its picture chosen. It is stamped on the row while the nodes are in hand.
            // The grid draws a square for each tile, and every square needs this answer. Asked per tile, it
            // would be a full read of the node table each time, for a fact that this loop already holds.
            // On a borrowed shelf, a kept row whose choice has changed is drawn again. Its square is
            // latched on the row for the session. The node behind a kept row can be a different list after
            // the owner's tree is rewritten. This does not apply to the user's own tree. There, the only
            // way a choice changes is the picker, and SetCoverAsync redraws the row itself. A second redraw
            // here would blank the square it has just drawn. The comparison uses what is stored, not the
            // records. The records hold their folders in a fresh list on every read, so they would never
            // compare equal.
            var redraw = IsBorrowed && ReferenceEquals(item, existing)
                         && item.Cover.ToWire() != n.Cover.ToWire();
            item.Cover = n.Cover;
            if (redraw)
                item.RefreshCover();
            // Only a playlist wears it. A folder is not a list of songs. An E on a folder would be a claim
            // about whatever is filed inside it, not about the row itself.
            item.IsExplicit = kind == PlaylistRowKind.Playlist && marked.Contains(n.Id);
            item.Children.Clear();   // every reused row is re-linked below, so clear before linking
            item.Parent = null;      // re-parented below, so a row that has moved does not keep its old parent
            byId[n.Id] = item;
        }
        foreach (var n in nodes)
        {
            var item = byId[n.Id];
            if (n.ParentId is { } p && byId.TryGetValue(p, out var parent))
            {
                parent.Children.Add(item);
                item.Parent = parent;
            }
        }

        // A reused row keeps its bindings, so whether it can expand must be announced again now that it has
        // its new children. An emptied folder must lose its chevron. A folder that has just gained its
        // first playlist must gain a live one.
        foreach (var item in byId.Values)
            item.NotifyChildrenChanged();

        // The owner's app folders on a borrowed shelf, drawn the way this app draws its own. This is asked
        // of the tree as it now stands, on every reload, because it is a fact about what is in the folder.
        foreach (var item in byId.Values)
            if (item.Kind == PlaylistRowKind.Folder)
                item.HoldsGeneratedListsOnly = HoldsGeneratedListsOnly(
                    item, row => row.Kind, row => row.Face is not null, row => row.Children);

        // Rows whose node is gone go with it. So a deleted playlist cannot come back when a later reload
        // finds its id again.
        _itemsByNode.Clear();
        foreach (var (id, item) in byId)
            _itemsByNode[id] = item;

        // Songs are not shown beneath a playlist in the tree. Selecting a playlist shows its tracks in the
        // detail list below the tree (BuildDetail). The tree holds only the folder and playlist hierarchy.

        // The order of the top-level nodes is kept, so the roots can be laid out again when the generated
        // lists' sizes arrive from the worker. This avoids reading the tree a second time.
        _rootNodeOrder.Clear();
        foreach (var n in nodes.Where(n => n.ParentId is null && !n.IsNowPlaying))
            _rootNodeOrder.Add(n.Id);
        _nowPlayingNode = nodes.FirstOrDefault(n => n.IsNowPlaying)?.Id;

        BuildRoots();
        RefreshCloudState();
        // This is done here, not in RebuildVisible. Every path that can change the totals ends in a reload.
        // RebuildVisible also runs when a folder is opened or closed. That changes nothing about what the
        // tree holds, so it should not spend a query saying so.
        ReadTotals();
        RebuildVisible();

        // The generated lists' sizes are the expensive half, and they are not part of the tree. They are
        // started here, not read above. See StartTotalsRead.
        StartTotalsRead();
    }

    /// <summary>
    /// Whether a folder holds at least one list and nothing but the owner's generated lists, at any
    /// depth. See <see cref="PlaylistTreeItem.HoldsGeneratedListsOnly"/>.
    /// </summary>
    /// <remarks>Takes what it reads of a row as functions, so that the rule can be tested. A
    /// <see cref="PlaylistTreeItem"/> builds XAML objects, and it cannot be made in a test host.</remarks>
    /// <param name="drawnAsGenerated">Whether a row is drawn as a generated list. This is true when
    /// <see cref="PlaylistTreeItem.Face"/> is not null.</param>
    internal static bool HoldsGeneratedListsOnly<T>(
        T folder, Func<T, PlaylistRowKind> kindOf, Func<T, bool> drawnAsGenerated,
        Func<T, IReadOnlyList<T>> childrenOf)
    {
        var held = childrenOf(folder);
        return held.Count > 0 && held.All(row => Generated(row, 1));

        // Bounded, for the reason that every walk of this tree is bounded. A listing that arrives with a
        // cycle in it must cost a folder its colour, and it must not hang the reload.
        bool Generated(T row, int depth) =>
            depth < 64
            && kindOf(row) switch
            {
                PlaylistRowKind.Folder => childrenOf(row).Count > 0,
                PlaylistRowKind.Playlist => drawnAsGenerated(row),
                _ => false,
            }
            && childrenOf(row).All(child => Generated(child, depth + 1));
    }

    /// <summary>The ids of the top-level playlist nodes, in the order they are drawn. They are kept because
    /// <see cref="BuildRoots"/> runs again when the totals arrive, and it must not read the tree
    /// again.</summary>
    private readonly List<long> _rootNodeOrder = new();

    /// <summary>The now-playing node, which is pinned above everything else.</summary>
    private long? _nowPlayingNode;

    /// <summary>
    /// The top level of the tree, built from the rows already in hand.
    ///
    /// <para>Now playing is pinned first. Then come the folders that the generated playlists live in. Then
    /// the user's own top-level nodes, in their sort order. The app's own rows sit above what somebody
    /// made, because they are always there, whatever the tree holds.</para>
    ///
    /// <para>This runs twice per reload and reads no database. It runs once with whatever totals are in
    /// hand, and again when the worker's totals are applied. Whether a generated list is worth a row
    /// depends on how many songs it holds. So the first pass draws only the lists that the last read knew
    /// about. On the first reload of a session, that is none. The second pass draws them all.</para>
    /// </summary>
    private void BuildRoots()
    {
        _roots.Clear();

        // A leaf like every other playlist. Its queue is shown in the detail (BuildDetail).
        if (_nowPlayingNode is { } queue && _itemsByNode.TryGetValue(queue, out var nowRow))
            _roots.Add(nowRow);

        // On a borrowed shelf this is one row, not twelve. See SmartPlaylists.OnABorrowedShelf, which says
        // why each of the others is left out. The one that stays is the favourites: the songs the listener
        // hearted while browsing their friend's collection. It is the only one of those lists that is about
        // the reader, not about the owner.
        if (_smartTotals.Any(t => t.Value.Tracks >= SmartPlaylists.LeastToShow(t.Key)))
            _roots.Add(SmartFolderRow());
        if (MoodsFolderRow() is { } moods)
            _roots.Add(moods);
        if (DecadesFolderRow() is { } decades)
            _roots.Add(decades);

        foreach (var id in _rootNodeOrder)
            if (_itemsByNode.TryGetValue(id, out var row))
                _roots.Add(row);
    }

    /// <summary>
    /// The generated playlists' rows, kept across reloads for the same reason as the stored ones. A row
    /// carries whether it is the selected one. A fresh row on every reload would lose that on every import.
    /// </summary>
    private readonly Dictionary<SmartPlaylistId, PlaylistTreeItem> _smartRows = new();

    /// <summary>The folder that the generated playlists live in. It is kept across reloads, like the rows
    /// inside it, and for one more reason. It carries whether the user has it open. A folder that re-opened
    /// itself on every import could never be closed by the user.</summary>
    private PlaylistTreeItem? _smartFolder;

    /// <summary>The decades folder. It is kept across reloads, for the same reason as
    /// <see cref="_smartFolder"/>: it carries whether the user has it open.</summary>
    private PlaylistTreeItem? _decadesFolder;
    private PlaylistTreeItem? _moodsFolder;

    /// <summary>
    /// The decades folder: one list per decade.
    ///
    /// <para>Each decade is one list of the songs that the WORLD plays most out of that decade's records on
    /// this shelf. Cutting a decade by mood would give the same three questions with a date on them, so
    /// each decade is one list only. See <see cref="SmartPlaylistKind.Decade"/>.</para>
    ///
    /// <para>A decade with too little in it is not a row. A collection that stops at the nineties simply
    /// has no 2010s. A folder with no decades in it is not a row either. The result is null rather than an
    /// empty folder, because an empty folder would be a heading with nothing under it.</para>
    ///
    /// <para>It fills in behind the popularity sweep. The rank is <c>tracks.world_plays</c>. An unhurried
    /// pass fetches that one record at a time. So a fresh install has no decades at all, and they grow over
    /// the following days. The mood lists appear in the same way, behind the measuring pass.</para>
    /// </summary>
    private PlaylistTreeItem? DecadesFolderRow()
    {
        _decadesFolder ??= new PlaylistTreeItem(
            this, PlaylistRowKind.SmartFolder, SmartPlaylistFace.DecadesFolderName);

        // The name is set again on every reload. It is a localised label, not stored data, and the
        // interface language can change while a row is still in use.
        _decadesFolder.Name = SmartPlaylistFace.DecadesFolderName;
        _decadesFolder.Parent = null;
        _decadesFolder.Children.Clear();

        foreach (var id in OfferedDecadeIds())
        {
            if (Totals(id).Tracks < SmartPlaylists.LeastToShow(id))
                continue;

            var row = SmartRow(id);
            row.Parent = _decadesFolder;
            _decadesFolder.Children.Add(row);
        }

        _decadesFolder.NotifyChildrenChanged();
        return _decadesFolder.Children.Count == 0 ? null : _decadesFolder;
    }

    /// <summary>
    /// The moods folder: the lists cut from how the music sounds, and Classical beside them.
    ///
    /// <para>This is a folder of its own, as the decades folder is. The pinned folder answered two
    /// different questions in one list. One question is what the music sounds like. The other is what the
    /// listener has done with it. They are not the same kind of thing, and the second is much the longer
    /// list, so the first sat at the bottom of it.</para>
    ///
    /// <para>What goes in is <see cref="SmartPlaylists.Moods"/>. The genre rows nest under their mood, as
    /// they did in the pinned folder. The mood row is still a list of its own as well as a parent. That is
    /// what makes "Chill" both playable and openable.</para>
    ///
    /// <para>The result is null rather than an empty folder, as with the decades. A heading with nothing
    /// under it is a row that answers nothing. A collection that nobody has measured has no moods
    /// yet.</para>
    /// </summary>
    private PlaylistTreeItem? MoodsFolderRow()
    {
        _moodsFolder ??= new PlaylistTreeItem(
            this, PlaylistRowKind.SmartFolder, SmartPlaylistFace.MoodsFolderName);

        // The name is set again on every reload. It is a localised label, not stored data, and the
        // interface language can change while a row is still in use.
        _moodsFolder.Name = SmartPlaylistFace.MoodsFolderName;
        _moodsFolder.Parent = null;
        _moodsFolder.Children.Clear();

        foreach (var kind in Kinds.Where(SmartPlaylists.Moods.Contains))
        {
            var whole = SmartPlaylistId.WholeShelf(kind);
            if (Totals(whole).Tracks < SmartPlaylists.LeastToShow(whole))
                continue;

            var row = SmartRow(whole);
            row.Parent = _moodsFolder;
            _moodsFolder.Children.Add(row);

            if (!SmartPlaylists.BrokenOutByGenre.Contains(kind))
                continue;

            foreach (var id in OfferedSmartIds().Where(i => i.Kind == kind && i.IsWithinGenre))
            {
                if (Totals(id).Tracks < SmartPlaylists.LeastToShow(id))
                    continue;

                var child = SmartRow(id);
                child.Parent = row;
                row.Children.Add(child);
            }
            row.NotifyChildrenChanged();
        }

        _moodsFolder.NotifyChildrenChanged();
        return _moodsFolder.Children.Count == 0 ? null : _moodsFolder;
    }

    /// <summary>
    /// The pinned folder, with one row inside it for each generated playlist.
    ///
    /// <para>Every kind on offer goes in it, because there is nowhere else for one to be listed. So a kind
    /// added to <see cref="SmartPlaylists.All"/> appears here, and it needs nothing else from the pane.
    /// Which kinds are on offer depends on whose collection is being drawn. See <see cref="Kinds"/>.</para>
    /// </summary>
    private PlaylistTreeItem SmartFolderRow()
    {
        _smartFolder ??= new PlaylistTreeItem(this, PlaylistRowKind.SmartFolder, SmartPlaylistFace.FolderName);

        // The name is set again on every reload, for the same reason as the rows inside it. It is a
        // localised label, not stored data, and the interface language can change while a row is still in
        // use.
        _smartFolder.Name = SmartPlaylistFace.FolderName;
        _smartFolder.Parent = null;
        _smartFolder.Children.Clear();

        // Everything that is not in the moods folder. The two lists together cover Kinds. So each kind is
        // in exactly one of the folders, and never in neither. See SmartPlaylists.Moods.
        foreach (var kind in Kinds.Where(k => !SmartPlaylists.Moods.Contains(k)))
        {
            // A list is drawn when it has enough songs in it to be a list. A "Late night" list over a
            // library that nobody has played yet is a row that answers nothing. A folder full of such rows
            // looks like a broken app, not like an app that is waiting for the listener to play something.
            var whole = SmartPlaylistId.WholeShelf(kind);
            if (Totals(whole).Tracks < SmartPlaylists.LeastToShow(whole))
                continue;

            var row = SmartRow(whole);
            row.Parent = _smartFolder;
            _smartFolder.Children.Add(row);

            // A mood that is cut within a genre has one child for each genre worth listing. The parent
            // stays a list of its own. "Calm" is still all genres together, and clicking it plays that. So
            // the row is both openable and expandable. The tree already allows this, because
            // PlaylistTreeItem.CanExpand is Children.Count > 0. It asks nothing about the kind of the row.
            if (!SmartPlaylists.BrokenOutByGenre.Contains(kind))
                continue;

            foreach (var id in OfferedSmartIds().Where(i => i.Kind == kind && i.IsWithinGenre))
            {
                if (Totals(id).Tracks < SmartPlaylists.LeastToShow(id))
                    continue;

                var child = SmartRow(id);
                child.Parent = row;
                row.Children.Add(child);
            }
            row.NotifyChildrenChanged();
        }
        _smartFolder.NotifyChildrenChanged();
        return _smartFolder;
    }

    /// <summary>
    /// The row for one generated list, kept across reloads.
    ///
    /// <para>A genre row has a title of its own, such as "Slow Burn" under "Chill". It is not the genre it
    /// stands for, and it is not a composed title such as "Calm rock". A composed form needs an adjective
    /// order that the five locales do not share. A bare genre would put a shelf label where a playlist name
    /// goes. See <see cref="SmartPlaylistFace.Name"/>.</para>
    /// </summary>
    private PlaylistTreeItem SmartRow(SmartPlaylistId id)
    {
        string name = SmartPlaylistFace.Name(id);

        if (!_smartRows.TryGetValue(id, out var row))
        {
            row = new PlaylistTreeItem(this, PlaylistRowKind.Smart, name)
            {
                SmartKind = id.Kind,
                SmartGenre = id.Genre,
                SmartDecade = id.Decade,
            };
            _smartRows[id] = row;
        }

        // The name is set again on every reload. It is a localised label, not stored data, and the
        // interface language can change under a row that outlives it.
        row.Name = name;

        // The row is marked again on every reload, and for one more reason. A generated list is a QUERY, so
        // what it holds changes under it. A song retagged as explicit puts the E on a row that nobody
        // touched. The answer comes from the totals that this row is already drawn with (see
        // SmartPlaylistTotals, which reads it in the same pass as the count). So it costs nothing, and it
        // cannot disagree with the size beside it.
        row.IsExplicit = Totals(id).Explicit;
        row.Children.Clear();
        return row;   // parented by SmartFolderRow, the only place where one of these is listed
    }

    /// <summary>
    /// The row standing for one node, wherever it sits in the tree and whether or not its folders are open.
    ///
    /// <para>It is taken from the kept rows, not from <see cref="Visible"/>. That is the point. Visible
    /// holds what the tree is SHOWING, so a playlist inside a collapsed folder is not in it. Anything that
    /// asks to open a playlist by its id has no way of knowing whether the user has that folder
    /// open.</para>
    ///
    /// <para>The result is null for a node that the tree has never seen. That is an ordinary answer. A
    /// playlist can be deleted between the moment something learns its id and the moment it asks for the
    /// row.</para>
    /// </summary>
    public PlaylistTreeItem? RowFor(long nodeId)
        => _itemsByNode.TryGetValue(nodeId, out var item) ? item : null;

    /// <summary>
    /// The row standing for one generated list, for a surface outside this pane that wants to open it. The
    /// home screen navigates to it through here.
    ///
    /// <para>The whole <see cref="SmartPlaylistId"/> is taken, not just a kind. A kind alone stopped being
    /// enough when a list could be cut by a genre. The tree offers "most played", and a genre's page offers
    /// "most played rock". These are one list, asked of different rows. A caller that wants the pooled form
    /// must say <see cref="SmartPlaylistId.WholeShelf"/> out loud.</para>
    ///
    /// <para>The row is built if the tree has not been read yet, rather than answered with null. The row is
    /// a label and a kind and nothing else. A caller that asks for a list it can see on another screen
    /// should not be told that the list does not exist, just because this pane has not been opened.</para>
    ///
    /// <para>Whether the list has anything in it is not checked here. The surface that offers it has
    /// already decided that, from <c>LibraryDatabase.SmartPlaylistTotals</c>, which is the same number the
    /// tree uses to draw its rows. This method refuses only a kind that this collection does not offer at
    /// all. That is the case on a borrowed shelf, and null is returned for it. See <see cref="Kinds"/>,
    /// which decides this.</para>
    /// </summary>
    public PlaylistTreeItem? SmartRowFor(SmartPlaylistId id)
        => Kinds.Contains(id.Kind) ? SmartRow(id) : null;
    /// <summary>Raised when a row's cloud state changed, so the panel can start or stop the animation of
    /// its busy badges.</summary>
    public event Action? CloudStateChanged;

    /// <summary>
    /// Re-reads where each playlist stands with the cloud. This runs on every tick of an upload. That is
    /// why it asks the database for the cloud columns, rather than going through <see cref="Reload"/>. A
    /// reload would re-read the whole tree just to learn one flag per row.
    /// </summary>
    public void RefreshCloudState()
    {
        if (!_library.IsOpen)
            return;

        // A borrowed row carries no badge. The cloud this service knows about is the user's own. Whether
        // somebody else has put their playlist into THEIR storage is not something it can answer. Nor could
        // the borrower act on it if it could.
        //
        // Cleared rather than skipped. Rows are kept across reloads by node id, and two trees number their
        // nodes from one counter. So opening a friend's shelf would give their playlist three the row that
        // the listener's playlist three was using, badge and all. Without this, a cloud mark on somebody
        // else's list would never be removed.
        if (IsBorrowed)
        {
            foreach (var item in AllItems(_roots))
                (item.IsInCloud, item.IsCloudLeaving, item.IsCloudUploading) = (false, false, false);

            CloudStateChanged?.Invoke();
            return;
        }

        var db = _library.Db;
        var cloud = App.Services.GetRequiredService<CloudSyncService>();

        // One read for the whole tree, not one per row. See LibraryDatabase.PlaylistCloudByNode for what a
        // query per row would cost.
        var cloudByNode = db.PlaylistCloudByNode();

        foreach (var item in AllItems(_roots))
        {
            if (item.Kind != PlaylistRowKind.Playlist || item.NodeId is not { } id)
                continue;

            var inCloud = cloudByNode.TryGetValue(id, out var mark) && mark.Selected;
            item.IsInCloud = inCloud;
            item.IsCloudLeaving = cloud.IsPlaylistLeaving(id);
            // Flattening a playlist is not free, so it is asked only of one that is actually in the cloud.
            //
            // And not at all while the engine is stopped. Its tracks are still marked in flight in the
            // mirror, but nothing is moving them. A row that shows upload activity for a week over a queue
            // that is not running would be a badge that lies.
            item.IsCloudUploading = inCloud && !cloud.UploadsStopped && db.PlaylistHasTracksInFlight(id);
        }

        CloudStateChanged?.Invoke();
    }

    /// <summary>Every node in the tree, expanded or not. The badge has to keep up with a collapsed playlist
    /// too. Otherwise, opening its folder would show a stale badge.</summary>
    private static IEnumerable<PlaylistTreeItem> AllItems(IEnumerable<PlaylistTreeItem> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in AllItems(root.Children))
                yield return child;
        }
    }

    /// <summary>True while a queue refresh is already on its way to the dispatcher. The hundreds of
    /// notifications that one queue rebuild raises then turn into one refresh.</summary>
    private bool _queueRefreshQueued;

    /// <summary>
    /// The live queue changed, so the Now playing detail re-reads its tracks, if that is the open one.
    ///
    /// <para>This is coalesced. A queue is rebuilt one item at a time. Playing a stored playlist clears the
    /// queue and adds every track back, which is one notification per track. If each notification rebuilt
    /// the open detail, a long playlist would be re-read from the database row by row, on the UI thread.
    /// Starting a long list would freeze the app for as long as that took, once per track, and the sound
    /// would run dry meanwhile.</para>
    ///
    /// <para>The queue only invalidates the queue. A stored playlist's rows carry nothing that comes from
    /// the player, so rebuilding one because the queue moved would be work with no result. The tree does
    /// not depend on the queue either.</para>
    /// </summary>
    private void OnNowPlayingChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_queueRefreshQueued)
            return;

        _queueRefreshQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _queueRefreshQueued = false;
            QueueInvalidated?.Invoke();
        });
    }

    /// <summary>Raised whenever the tree or the queue changes, so an open playlist detail re-reads its
    /// tracks.</summary>
    public event Action? DetailInvalidated;

    /// <summary>Raised when the live queue changed and nothing else did. Only the Now playing detail has
    /// anything to re-read.</summary>
    public event Action? QueueInvalidated;

    /// <summary>Raised when a song's heart changed and nothing else did. Only a detail that shows one of
    /// the generated lists made of hearts has anything to re-read. See
    /// <see cref="MarksChanged"/>.</summary>
    public event Action? MarksInvalidated;

    internal void RebuildVisible()
    {
        Visible.Clear();
        foreach (var root in _roots)
            AddVisible(root, 0);
        OnChanged(nameof(HasAny));
        DetailInvalidated?.Invoke();
    }

    private void AddVisible(PlaylistTreeItem item, int depth)
    {
        item.Depth = depth;
        Visible.Add(item);
        if (item.IsExpanded)
            foreach (var child in item.Children)
                AddVisible(child, depth + 1);
    }

    public bool HasAny => _roots.Count > 0;

    /// <summary>What the tree adds up to: how many playlists it holds and how long they run. It is the line
    /// under the pane's heading. It is read on reload, rather than kept as a running total. Every path that
    /// changes it (a new playlist, songs dropped in, a delete) already ends in a reload.</summary>
    public string Subheading { get; private set; } = Presentation.Common.PaneStats.Playlists(0, 0);

    /// <summary>What each generated list holds, as of the last reload. It is kept, rather than asked twice.
    /// The answer decides whether the row exists, what the pane's subtitle says, and whether the row shows
    /// an E. Two reads a moment apart could disagree about a list that somebody is in the middle of
    /// filling.</summary>
    private readonly Dictionary<SmartPlaylistId, (int Tracks, double Seconds, bool Explicit)>
        _smartTotals = new();

    /// <summary>
    /// The generated lists closest to being worth drawing, and what each is waiting for. The list is empty
    /// when every list this collection can offer is already in the tree. It is also empty on a borrowed
    /// shelf.
    /// </summary>
    /// <remarks>
    /// At most <see cref="LockedLists.AtOnce"/>. The pane draws them on one card. A short tree then shows
    /// whether that is all the app can do, or all that this collection can answer. See
    /// <see cref="LockedLists"/> for why the list is not every possible list.
    /// </remarks>
    public IReadOnlyList<LockedList> Locked { get; private set; } = [];

    /// <summary>How far the measuring pass has got, as of the same read as the sizes. The card's measuring
    /// line quotes it. It also decides whether a mood list is short of music or short of time.</summary>
    public MeasuringProgress Measuring { get; private set; }

    /// <summary>The collection that the generated lists' sizes were last read from. It is null until the
    /// first read lands. See <see cref="GeneratedListsRead"/>.</summary>
    private LibraryDatabase? _totalsFrom;

    /// <summary>
    /// Whether the generated lists' sizes were read from the collection that is open now. It is false until
    /// the first read for it lands. On a large collection that takes seconds (see
    /// <see cref="StartTotalsRead"/>). Until then the tree is missing those lists, and the pane shows its
    /// loader. A later re-read of the same collection keeps the old sizes on screen, and does not make this
    /// false.
    /// </summary>
    public bool GeneratedListsRead => !_library.IsOpen || ReferenceEquals(_totalsFrom, _library.Shelf);

    private (int Tracks, double Seconds, bool Explicit) Totals(SmartPlaylistId id) =>
        _smartTotals.TryGetValue(id, out var totals) ? totals : (0, 0d, false);

    /// <summary>
    /// Which read of the generated lists' sizes the pane is showing.
    ///
    /// <para>It is raised on the UI thread whenever a read starts, and checked on the UI thread when a read
    /// lands. So a read that a later read has overtaken is dropped, and it is not applied on top of the
    /// newer one. A number that is compared where it was raised is a fact whenever it is asked. A flag that
    /// says "a read is in flight" would be true only for a window of time, and whoever set it would have to
    /// clear it.</para>
    /// </summary>
    private int _totalsRead;

    /// <summary>
    /// Reads every generated list's size on a worker, and applies the answer on the UI thread.
    ///
    /// <para><b>Not on the drawing thread.</b> The sizes take one query per offered list (about eighty
    /// on a 19,747-track collection), and the mood lists compose their pool in memory before the query
    /// runs. The set takes about 5 s with nothing else happening, and about 20 s during start-up, when
    /// every other reader in the app competes for the same connection. If it were read inline from
    /// <see cref="Reload"/>, which runs from MainPage's Loaded event, the app would stay on a black
    /// screen for that long.</para>
    ///
    /// <para>Everything the worker needs is taken here, on the UI thread, and handed over. A worker may not
    /// read <c>_library</c>, resolve a service, or ask what a borrowed shelf is. What comes back is a
    /// value, applied in <see cref="ApplyTotals"/>.</para>
    ///
    /// <para>There is one clock reading for all of them, so the windowed lists ("on repeat", "first
    /// listens") all answer about the same moment. Two rows that disagree about where the last month starts
    /// would be a difference that nobody can see and nobody can explain.</para>
    /// </summary>
    private void StartTotalsRead()
    {
        if (!_library.IsOpen)
            return;

        // Read here, synchronously, because none of it may be asked from the worker.
        var db = _library.Shelf;
        var kinds = (IReadOnlyList<SmartPlaylistKind>)[.. Kinds];
        var borrowed = IsBorrowed;
        var withDecades = !borrowed;
        var read = ++_totalsRead;

        Services.Shell.Background.Run(async () =>
        {
            using var slot = await _sizes.Next(CancellationToken.None);
            if (!slot.Go)
                return;

            // Asked again after the wait. A read that queued behind a busy window for two seconds may have
            // been overtaken by a newer one, and the newer one's answer is the right one. Without this
            // check, the wait would bring back the staleness that _totalsRead exists to stop.
            if (read != _totalsRead)
                return;

            var now = DateTime.UtcNow.Ticks;

            // Every list on offer is read, the genre rows included. A child row needs its own size to know
            // whether it is worth drawing. The parent's total says nothing about it. This is asked here,
            // not on the UI thread, because the first ask of a session builds the mood cut. That is three
            // whole-table reads and a fold.
            var offered = db.OfferedGeneratedLists(kinds, withDecades);

            var totals = new Dictionary<SmartPlaylistId, (int Tracks, double Seconds, bool Explicit)>();
            foreach (var id in offered)
                totals[id] = db.SmartPlaylistTotals(id, now);

            // How far the measuring pass has got, for the card that lists the lists that are not there yet.
            // It is read here, beside the sizes, because it answers the same question at the same moment.
            // It comes from the database, not from the session's mood cut. The mood cut holds the number it
            // was built with until the app is restarted. It counts only the tracks on this disk, because
            // nothing will ever measure a record that is only in the cloud.
            //
            // Not asked on a borrowed shelf, which has no card to show it on.
            var measured = borrowed ? default : db.MeasuredSoFar(TrackScope.OnThisComputer);

            _dispatcher.TryEnqueue(() => ApplyTotals(read, db, offered, totals, measured));
        });
    }

    /// <summary>
    /// The sizes a worker read, put on the tree. UI thread.
    /// </summary>
    /// <param name="read">Which read this was. A read that is not the newest is dropped, because the tree
    /// has been rebuilt since it started, and its sizes describe a collection that has since moved.</param>
    /// <param name="from">The collection the sizes were read from.</param>
    private void ApplyTotals(
        int read,
        LibraryDatabase from,
        IReadOnlyList<SmartPlaylistId> offered,
        Dictionary<SmartPlaylistId, (int Tracks, double Seconds, bool Explicit)> totals,
        MeasuringProgress measured)
    {
        if (read != _totalsRead || !_library.IsOpen)
            return;

        _offeredIds = offered;
        _totalsFrom = from;
        _smartTotals.Clear();
        foreach (var (id, value) in totals)
            _smartTotals[id] = value;

        Measuring = measured;

        // Worked out here, not when the card asks. That way the lists it names and the sizes the tree drew
        // come out of one read. A borrowed shelf has none. Its lists are cut from somebody else's
        // collection, and telling the guest to import records would send the question to the wrong person.
        Locked = IsBorrowed
            ? []
            : LockedLists.Choose(
                totals.ToDictionary(pair => pair.Key, pair => pair.Value.Tracks), measured);

        // The generated lists are rows in the tree, so their arrival is a change to it. Nothing is re-read
        // here. BuildRoots works from the rows already in hand.
        BuildRoots();
        ReadTotals();
        RebuildVisible();
    }

    /// <summary>Re-reads some of the lists and leaves the rest as they were. Use it for a change that can
    /// only have moved a few of the lists. There is one clock reading for the batch, as above.</summary>
    private void ReadSmartTotalsFor(IEnumerable<SmartPlaylistId> ids)
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var id in ids)
            _smartTotals[id] = _library.Shelf.SmartPlaylistTotals(id, now);
    }

    /// <summary>Which generated lists have enough in them to be drawn, as of the totals in hand.</summary>
    private List<SmartPlaylistId> ShownSmartKinds() =>
        [.. OfferedSmartIds().Where(id => Totals(id).Tracks >= SmartPlaylists.LeastToShow(id))];

    /// <summary>
    /// Every generated list this collection could offer: one per kind, plus one per genre under the moods
    /// that are cut within a genre.
    ///
    /// <para>Which genres those are is decided by the shelf, not by the pane. See
    /// <see cref="MoodList.GenresWorthListing"/>. A genre appears here as it fills up, and it drops off
    /// again if it empties. So the tree follows the collection, not a list that somebody wrote down.</para>
    ///
    /// <para><see cref="SmartPlaylistKind.Sleep"/> is deliberately not broken out by genre. It is an
    /// absolute cut over everything. So "sleep jazz" would be the jazz that happens to be in it, not an
    /// answer to anything. <see cref="SmartPlaylists.BrokenOutByGenre"/> is where that is decided.</para>
    ///
    /// <para><b>The answer the last worker brought back, not a question asked here.</b> The rule is
    /// <c>LibraryDatabase.OfferedGeneratedLists</c>. The push that sends this cut to the phone asks the
    /// same rule. If a phone offered a different set of lists from the computer, the rule would have to be
    /// written down twice. The first ask of a session builds the mood cut, which is three whole-table reads
    /// and a fold. Every caller of this method is on the drawing thread, so the ask is made on a worker
    /// instead. <see cref="StartTotalsRead"/> asks it beside the sizes it is needed for, and hands both
    /// over together. The list is empty until the first read lands. An empty list draws no generated rows,
    /// which is the honest answer for a pane that has not read them yet.</para>
    /// </summary>
    private IReadOnlyList<SmartPlaylistId> OfferedSmartIds() => _offeredIds;

    /// <inheritdoc cref="OfferedSmartIds"/>
    private IReadOnlyList<SmartPlaylistId> _offeredIds = [];

    /// <summary>
    /// The lists that the decades folder can offer: one for each decade that the shelf is broken out by.
    ///
    /// <para>They are offered, not drawn. Which of them is worth a row is the same question that every
    /// generated list answers: is there enough in it to be a list? That is asked once, in
    /// <see cref="DecadesFolderRow"/>, from the totals that this list supplies.</para>
    ///
    /// <para>Nothing is offered on a borrowed shelf. <see cref="Kinds"/> is one row there, and none of the
    /// four moods is on it. A decade of somebody else's collection is a question about them.</para>
    /// </summary>
    private IEnumerable<SmartPlaylistId> OfferedDecadeIds()
    {
        // Nothing on a borrowed shelf. A decade of somebody else's collection is a question about them. The
        // borrowed shelf offers one list only. See SmartPlaylists.OnABorrowedShelf.
        if (IsBorrowed)
            yield break;

        foreach (var decade in SmartPlaylists.Decades)
            yield return SmartPlaylistId.In(decade);
    }

    /// <summary>
    /// A song's heart changed, and nothing about the tree changed.
    ///
    /// <para>A reload rebuilds the tree and ends in <see cref="DetailInvalidated"/>. So a heart must not be
    /// handled by a reload. Otherwise hearting one song would re-read and repaint whatever playlist was
    /// open, even a list with no hearts in it, when nothing about that list had changed.</para>
    ///
    /// <para>A mark can only move a small, known set of things. These are the three generated lists made of
    /// hearts (<see cref="LibraryDatabase.SmartPlaylistNeedsMarks"/>), the length that the pane's subtitle
    /// gives for the generated lists, and, only when one of those lists crosses the size it needs to be
    /// drawn at all, which rows the tree holds. Everything else is told through
    /// <see cref="MarksInvalidated"/>. The detail acts on that only if it is showing one of those three
    /// lists. This is the same shape as <see cref="QueueInvalidated"/>, for the same reason.</para>
    /// </summary>
    public void MarksChanged()
    {
        if (!_library.IsOpen)
            return;

        var drawnBefore = ShownSmartKinds();
        ReadSmartTotalsFor(OfferedSmartIds().Where(id => LibraryDatabase.SmartPlaylistNeedsMarks(id.Kind)));

        // A list that has just reached the size worth drawing, or has dropped below it, is a row arriving
        // or leaving. That IS a change to the tree, so the tree is rebuilt for it. This is rare, and it is
        // the one case where a repaint is the honest answer.
        if (!ShownSmartKinds().SequenceEqual(drawnBefore))
        {
            Reload();
            return;
        }

        // The subtitle counts the generated lists' lengths, so it moves with every mark.
        ReadTotals();
        MarksInvalidated?.Invoke();
    }

    /// <summary>Which generated lists this pane offers. That depends on whose collection the pane is
    /// drawing. It is asked in one place, so that the rows, their sizes and the subtitle cannot
    /// disagree.</summary>
    private IReadOnlyList<SmartPlaylistKind> Kinds =>
        IsBorrowed ? SmartPlaylists.OnABorrowedShelf : SmartPlaylists.All;

    private void ReadTotals()
    {
        var (playlists, seconds) = _library.IsOpen ? _library.Shelf.QueryPlaylistTotals() : (0, 0d);

        // The generated lists count too. They are rows in this tree, and they play. A subtitle that left
        // them out would say "2 playlists" above three of them. Only the lists that are actually drawn
        // count. A list the pane is not offering is not a playlist the pane has.
        foreach (var (id, totals) in _smartTotals)
        {
            if (totals.Tracks < SmartPlaylists.LeastToShow(id))
                continue;
            playlists++;
            seconds += totals.Seconds;
        }

        // Nothing is counted until the generated lists have been read. "Nothing here yet" over a tree whose
        // lists are still arriving would be a claim about a collection that nobody has read yet. The pane's
        // loader is shown instead (PlaylistView.ReflectReading).
        var stats = GeneratedListsRead ? Presentation.Common.PaneStats.Playlists(playlists, seconds) : "";

        // Also how many of them the shelf is holding back. On a borrowed shelf whose owner has gone home,
        // that is usually most of them. This is said, not left to be noticed. A pane that went from a
        // friend's twelve lists to none would look as if the app had lost them. But every row is still
        // there. They hold songs that live on that computer, and they come back with it.
        if (IsBorrowed && _library.Shelf.PlaylistsOutOfReach() is > 0 and var held)
            stats += "  ·  " + Presentation.Common.PaneStats.PlaylistsOffline(held);

        // Whose tree this is is said above it. The pane has swapped its whole contents for somebody else's.
        // Nothing else in it would say so: the heading still reads Playlists, and the rows look exactly
        // like the user's own.
        Subheading = IsBorrowed && Services.Sharing.PeerNames.Lender() is { Length: > 0 } lender
            ? Strings.Loc.Format("Playlists_SharedBy", lender) + (stats.Length > 0 ? "  ·  " + stats : "")
            : stats;

        OnChanged(nameof(Subheading));
        OnChanged(nameof(IsBorrowed));
    }

}

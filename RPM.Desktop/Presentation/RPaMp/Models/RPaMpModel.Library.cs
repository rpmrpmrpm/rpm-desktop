using System.Collections.Generic;
using System.IO;
using System.Linq;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.DataGrid.Model;

namespace RPM.Desktop.Presentation.RPaMp.Models;

/// <summary>
/// The library pane: the tree of lists down the left, the two name panes across the top, and the
/// songs they leave underneath.
///
/// <para>This is Winamp's media library, answered by this collection. The whole collection is read once, when the
/// pane is first opened. After that it is narrowed in memory. The panes narrow again on every click, and a database
/// query on each click would repeat the same work. What a narrowing means is defined by <see cref="AmpLibrary"/>.
/// What is currently picked is kept here. Drawing it is the view's job.</para>
///
/// <para><b>It reads the shelf, not this machine's own mirror.</b> So the pane shows whichever collection is on
/// screen, including a borrowed one. The switcher in its title bar is the same switch that the deck's share button
/// uses. Nothing here writes anything, because a borrowed shelf is read-only.</para>
///
/// <para>Three grids read the three <see cref="ListGridSource"/>s below. A pick is never kept in a grid's own
/// selection: a refresh replaces the rows, and a replaced list clears the selection with it. The grids draw the pick
/// by asking this model at the moment they draw a row.</para>
/// </summary>
public partial class RPaMpModel
{
    /// <summary>Every song in the collection, read once. It is empty until the pane is first opened.</summary>
    private IReadOnlyList<LibraryTrack> _libAll = [];

    private bool _libLoaded;

    /// <summary>The collection the rows were read from: a device id, or null for the user's own collection.
    /// <see cref="SyncLibraryToShelf"/> compares against this.</summary>
    private string? _libLoadedFrom;

    private IReadOnlyList<AmpNode> _libTree = [];

    /// <summary>The songs in the open playlist, read when it was picked. Null when the open list is not a playlist, or
    /// when the playlist could not be read.</summary>
    private IReadOnlySet<string>? _libPlaylistPaths;

    /// <summary>The rows the three grids draw.</summary>
    public ListGridSource LibNavRows { get; } = new();

    public ListGridSource LibArtistRows { get; } = new();

    public ListGridSource LibAlbumRows { get; } = new();

    public ListGridSource LibTrackRows { get; } = new();

    /// <summary>The songs currently listed, in the order they are listed. What Play and Add act on.</summary>
    private IReadOnlyList<LibraryRow> _libTracks = [];

    /// <summary>Which list of the collection is open. Null until the pane is first opened.</summary>
    public AmpNode? LibPick { get; private set; }

    /// <summary>The artist picked in the artist pane, or null for all of them.</summary>
    public string? LibArtist { get; private set; }

    /// <summary>The album picked in the album pane, or null for all of them.</summary>
    public string? LibAlbum { get; private set; }

    /// <summary>The column the list is sorted by, or null for the open list's own order.</summary>
    public AmpSort? LibSort { get; private set; }

    private string _libSearch = string.Empty;

    /// <summary>What is typed in the search box.</summary>
    public string LibSearch
    {
        get => _libSearch;
        set
        {
            var text = value ?? string.Empty;
            if (_libSearch == text)
                return;

            _libSearch = text;
            OnChanged(nameof(LibSearch));

            // A search narrows the panes as well as the list. So a name that survives the search still has songs
            // behind it. A pick that does not survive is dropped, rather than left standing over an empty list.
            LibArtist = null;
            LibAlbum = null;
            RefreshLibrary();
        }
    }

    private string _libStatus = string.Empty;

    /// <summary>The line at the foot of the pane: how many songs, how long, how big.</summary>
    public string LibStatus
    {
        get => _libStatus;
        private set
        {
            if (_libStatus == value)
                return;

            _libStatus = value;
            OnChanged(nameof(LibStatus));
        }
    }

    /// <summary>Raised when the rows have been replaced, so the grids can draw again.</summary>
    public event Action? LibraryRefreshed;

    /// <summary>
    /// Reads the collection, once, the first time the pane is opened.
    /// </summary>
    /// <remarks>
    /// The read is deferred until the pane is first used. The player is built at startup, whether or not anybody opens
    /// the library. Reading every row of a large collection is not something to do for a pane that nobody has asked for.
    /// </remarks>
    public void LoadLibrary()
    {
        if (_libLoaded)
            return;
        _libLoaded = true;
        _libLoadedFrom = _library.ViewingPeer;

        var playlists = new List<(string Name, long Id)>();

        // If there is no collection, or it does not answer, the pane is left empty. The player is not affected, and the
        // tree has nothing under its rows. A refusal also puts the problem screen up over the app (see
        // LibraryService.Read).
        _libAll = [];
        if (_library.IsOpen
            && _library.Read(_library.Shelf, "the player's library pane",
                       mirror => (Tracks: mirror.Query(null), Nodes: mirror.GetPlaylistNodes()))
                   .Answers(out var read, out _))
        {
            _libAll = read.Tracks;

            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var node in read.Nodes
                         .Where(n => n.Kind == PlaylistNodeKind.Playlist)
                         .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                // Names are not unique in the tree. The first of a repeated name wins, which is what the pane shows anyway.
                if (seen.Add(node.Name))
                    playlists.Add((node.Name, node.Id));
            }
        }

        _libTree = AmpNav.Tree(playlists);
        LibPick = AmpNav.First(_libTree);
        LibNavRows.Replace(AmpNav.Rows(_libTree));
        RefreshLibrary();
    }

    /// <summary>
    /// Reads the collection again, from the beginning: a different one is on screen.
    /// </summary>
    /// <remarks>
    /// Everything picked is dropped. A playlist, an artist and an album are names in the collection that was open.
    /// Carrying them across to another collection would narrow it by a name that collection may not hold. The result
    /// would be an empty list, with no sign of why.
    /// </remarks>
    public void ReloadLibrary()
    {
        _libLoaded = false;
        _libAll = [];
        _libTree = [];
        _libPlaylistPaths = null;
        LibPick = null;
        LibArtist = null;
        LibAlbum = null;
        LibSort = null;

        LoadLibrary();
    }

    /// <summary>
    /// Reads the collection again if the one on screen is no longer the one these rows came from.
    /// </summary>
    /// <remarks>
    /// The switch can be made from two places: this pane, and the deck's share button. The check is made each time the
    /// pane is shown, rather than pushed from either place. So it holds however the collection was changed. Nothing
    /// happens when the two agree, which is nearly always.
    /// </remarks>
    public void SyncLibraryToShelf()
    {
        if (!_libLoaded || _libLoadedFrom == _library.ViewingPeer)
            return;

        ReloadLibrary();
    }

    /// <summary>Opens a list from the tree. A heading opens and closes instead.</summary>
    public void PickNode(AmpNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node.IsGroup)
        {
            node.IsExpanded = !node.IsExpanded;
            LibNavRows.Replace(AmpNav.Rows(_libTree));
            LibraryRefreshed?.Invoke();
            return;
        }

        if (ReferenceEquals(LibPick, node))
            return;

        LibPick = node;

        // A different list is a different set of songs, so the artist narrowing is cleared. An artist kept from the
        // previous list would show a list with most of its songs missing, and no sign of why.
        LibArtist = null;
        LibAlbum = null;
        LibSort = null;
        _libPlaylistPaths = node.Scope == AmpScope.Playlist ? PlaylistPaths(node.PlaylistId) : null;

        RefreshLibrary();
    }

    /// <summary>Narrows to one artist, or to all of them.</summary>
    public void PickArtist(AmpBrowseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (LibArtist == row.Name)
            return;

        LibArtist = row.Name;
        LibAlbum = null;
        RefreshLibrary();
    }

    /// <summary>Narrows to one album, or to all of them.</summary>
    public void PickAlbum(AmpBrowseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (LibAlbum == row.Name)
            return;

        LibAlbum = row.Name;
        RefreshLibrary();
    }

    /// <summary>
    /// Sorts the list by a column. Each column has three states, as in any table with a sort: up, down, then the open
    /// list's own order again.
    /// </summary>
    public void SortLibraryBy(string column)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);

        LibSort = LibSort switch
        {
            { Column: var had, Descending: false } when had == column => new AmpSort(column, true),
            { Column: var had } when had == column => null,
            _ => new AmpSort(column, false),
        };

        RefreshLibrary();
    }

    /// <summary>Sorts by a stated column and direction. If the column is null, the open list's own order is restored.
    /// The header menu uses this, because its three states are separate items, not three clicks.</summary>
    public void SortLibraryTo(string? column, bool descending)
    {
        LibSort = column is null ? null : new AmpSort(column, descending);
        RefreshLibrary();
    }

    /// <summary>Whether a listed song is the one playing, asked of the row as it is drawn.</summary>
    public bool IsLibraryRowPlaying(LibraryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (Deck is not { } deck || _library.ShelfRoot is not { } root)
            return false;

        return string.Equals(deck.FilePath, FullPath(root, row.Path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plays a row from the pane, and makes the rest of what is listed the queue after it. Picking a song out of an
    /// album means that album, as double-clicking one in the grid does.
    /// </summary>
    public void PlayFromLibrary(LibraryRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (_library.ShelfRoot is not { } root)
            return;

        // The row is looked up in the list that will become the queue, not in the rows on screen. A row whose file the
        // collection no longer holds is not in the queue at all. An index taken from the rows would start the wrong song.
        var wanted = FullPath(root, row.Path);
        var tracks = Listed();
        var start = tracks.FindIndex(t => string.Equals(t.FilePath, wanted, StringComparison.OrdinalIgnoreCase));

        if (start >= 0)
            PlayAlbumFrom(tracks, start);
    }

    /// <summary>
    /// Winamp's two buttons at the foot of the pane. They act on the songs at these positions in the list. Play makes
    /// them the queue and starts it. Add puts them at the end of the queue, and leaves what is playing alone.
    /// </summary>
    /// <param name="rows">Which rows to play, in the order they are to be played. The pane decides which rows those are:
    /// the songs picked in the list, or every song when none is picked. That is decided by the pane, not here, because
    /// the selection is kept in the grid.</param>
    /// <param name="enqueue">Add rather than play.</param>
    public void PlayRows(IReadOnlyList<int> rows, bool enqueue)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (_library.ShelfRoot is not { } root)
            return;

        var byPath = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _libAll)
            byPath[track.RelativePath] = track;

        var tracks = new List<TrackInfo>(rows.Count);
        foreach (var row in rows)
        {
            if (row < 0 || row >= _libTracks.Count)
                continue;

            if (byPath.TryGetValue(_libTracks[row].Path, out var track))
                tracks.Add(track.ToTrackInfo(root));
        }

        if (tracks.Count == 0)
            return;

        if (enqueue)
            AddTracks(tracks, startPlaying: false);
        else
            PlayAlbumFrom(tracks, 0);
    }

    /// <summary>How many songs are listed. What the pane counts off when nothing is picked.</summary>
    public int LibTrackCount => _libTracks.Count;

    /// <summary>Everything listed, as the queue needs it. Empty while the collection has no root, because
    /// there is no file to play without one.</summary>
    private List<TrackInfo> Listed()
    {
        if (_library.ShelfRoot is not { } root)
            return [];

        var byPath = new Dictionary<string, LibraryTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in _libAll)
            byPath[track.RelativePath] = track;

        var tracks = new List<TrackInfo>(_libTracks.Count);
        foreach (var row in _libTracks)
            if (byPath.TryGetValue(row.Path, out var track))
                tracks.Add(track.ToTrackInfo(root));

        return tracks;
    }

    /// <summary>Re-reads the panes and the list out of what is currently picked.</summary>
    private void RefreshLibrary()
    {
        var query = new AmpQuery(
            LibPick?.Scope ?? AmpScope.Audio,
            _libPlaylistPaths,
            _libSearch,
            LibArtist,
            LibAlbum,
            LibSort);

        var result = AmpLibrary.Run(_libAll, query);

        _libTracks = result.Tracks;
        LibArtistRows.Replace([.. result.Artists]);
        LibAlbumRows.Replace([.. result.Albums]);
        LibTrackRows.Replace([.. result.Tracks]);
        LibStatus = AmpLibrary.Status(result.Tracks.Count, result.Seconds, result.Bytes, result.Took).Text;

        LibraryRefreshed?.Invoke();
    }

    /// <summary>The songs a playlist holds, by relative path.</summary>
    /// <remarks>
    /// A playlist holds paths, and the collection is keyed by them. So a song in a playlist that is no longer in the
    /// collection does not appear. Null is returned where the playlist cannot be read. That lists nothing, rather than
    /// everything.
    /// </remarks>
    private IReadOnlySet<string>? PlaylistPaths(long playlistId)
    {
        if (!_library.IsOpen)
            return null;

        // Unanswered lists nothing. A refusal has put the problem screen up over the app.
        return _library.Read(_library.Shelf, "a playlist's songs for the player", mirror => mirror.GetPlaylistItems(playlistId))
            .Match<IReadOnlySet<string>?>(
                items => items
                    .Select(i => i.RelPath)
                    .OfType<string>()
                    .Where(p => p.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                _ => null);
    }

    private static string FullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}

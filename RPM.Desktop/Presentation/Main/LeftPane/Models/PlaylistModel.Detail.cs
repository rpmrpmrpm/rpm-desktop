using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// The track list under the tree: one playlist opened out, in the style of iTunes, with its own
/// ordering and its own drag-and-drop.
/// </summary>
/// <summary>
/// Whether a read of a list asks for a new list, or reads the list as it is.
///
/// <para><b>This is a type and not a boolean with a default, because a wrong answer causes no error.</b>
/// A generated mood list shows fifty songs from a larger pool. It moves its window when this value
/// says so, so that the next reading is a different fifty. Only one caller wants that. Every other
/// read (opening the list, playing it, redrawing it after a heart changed) must return what is already
/// there. Otherwise the listener sees a list of songs that they did not ask to change. The compiler
/// lists every call site, and each one must choose.</para>
/// </summary>
public enum ListReading
{
    /// <summary>The user pressed "generate a new list". The window moves on, so this is a different
    /// set of songs. Only one caller uses this value.</summary>
    NewList,

    /// <summary>The list as it is: opening it, or redrawing it after something about it changed
    /// (a heart changed, an artist was refused). The window does not move, so only the effect of the
    /// change shows.</summary>
    AsItStands,
}

public sealed partial class PlaylistModel
{
    // ── playlist detail (the track list under the tree, in the style of iTunes) ──

    /// <summary>Builds the detail for a selected playlist or folder: its flattened tracks as display
    /// rows (art for each folder, iTunes columns) and a header line. It returns null for rows that are
    /// not track containers.</summary>
    /// <param name="reading">Whether the button asks for a new list, or the read wants the list as it
    /// is. There is no default: a generated list moves its window on <see cref="ListReading.NewList"/>,
    /// so a wrong value would give the listener a different fifty songs. See
    /// <see cref="ListReading"/>.</param>
    public PlaylistDetail? BuildDetail(PlaylistTreeItem item, ListReading reading)
    {
        // The root of the shelf is used because the path of a borrowed row is relative to the mirror
        // kept for that computer. ResolveTracks makes the same join.
        if (!_library.IsOpen || _library.ShelfRoot is null)
            return null;

        // Now playing is the live queue, not a stored playlist. Its rows come from RPaMp.
        if (item.Kind == PlaylistRowKind.NowPlaying)
        {
            var qrows = new List<PlaylistTrackRow>();
            double qtotal = 0;
            foreach (var t in _amp.Transport.Playlist)
            {
                qrows.Add(MakeQueueRow(t, qrows.Count));
                qtotal += t.DurationSeconds;
            }
            return new PlaylistDetail(
                item.DisplayName, Numbered(qrows), DetailSubtitle(qrows.Count, qtotal), IsQueue: true);
        }

        // A generated list is its query, run now. Nothing about it is stored, so its rows have no
        // pl_item id. They cannot be removed one by one or dragged into another order, and the list
        // shows this by having no ids (see PlaylistDetail.PlaylistId). A song is removed from the
        // favourites where it was added: with the heart on its own line.
        if (item.Kind == PlaylistRowKind.Smart)
        {
            var generated = _library.Shelf
                .QuerySmartPlaylist(item.SmartId, DateTime.UtcNow.Ticks, advance: reading == ListReading.NewList)
                .Select(t => t.ToTrackInfo(_library.ShelfRoot!))
                .ToList();
            var generatedRows = generated.Select(t => MakeDetailRow(t, itemId: null)).ToList();
            return new PlaylistDetail(
                item.DisplayName, Numbered(generatedRows),
                DetailSubtitle(generatedRows.Count, generated.Sum(t => t.DurationSeconds)),
                SmartId: item.SmartId);
        }

        if (item.Kind is not (PlaylistRowKind.Playlist or PlaylistRowKind.Ref or PlaylistRowKind.Folder))
            return null;

        var db = _library.Shelf;
        var tracks = ResolveTracks(item);

        // Removal ids exist only for a single playlist of plain songs (no nested refs). Then the
        // flattened order matches the direct item order one to one. Folders and nested playlists get
        // no ids, so the remove command is hidden.
        long? playlistId = item.Kind switch
        {
            PlaylistRowKind.Playlist => item.NodeId,
            PlaylistRowKind.Ref => item.RefPlaylistId,
            _ => null,
        };
        IReadOnlyList<long?> ids;
        if (playlistId is { } pid)
        {
            // This reads the items themselves, not a resolved view of them: it needs only the id of
            // each row and the kind of row. Resolving every item against the tracks table to build a
            // label that nothing here reads would be one query for each song, on the UI thread, every
            // time the list is rebuilt. That would freeze the app on a long playlist.
            var items = db.GetPlaylistItems(pid);
            var songs = items.Where(v => v.ItemKind == PlaylistItemKind.Song).ToList();
            ids = !items.Any(v => v.ItemKind == PlaylistItemKind.Playlist) && songs.Count == tracks.Count
                ? songs.Select(v => (long?)v.Id).ToList()
                : new long?[tracks.Count];
        }
        else
        {
            ids = new long?[tracks.Count];
        }

        var rows = new List<PlaylistTrackRow>(tracks.Count);
        double total = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            rows.Add(MakeDetailRow(tracks[i], ids[i]));
            total += tracks[i].DurationSeconds;
        }
        // The list can be reordered only when every row has its own pl_item id. That shows that the
        // rows and the stored items are the same list in the same order, which a rewrite of the order
        // must be able to assume.
        // A borrowed list is never reordered. Its rows have ids like any other, so it would look
        // reorderable and let the user drag a row. The write is refused, and the row slides back to
        // where it started.
        var ordered = !IsBorrowed && playlistId is { } id && rows.Count > 0 && rows.All(r => r.ItemId is not null)
            ? id
            : (long?)null;

        return new PlaylistDetail(item.DisplayName, Numbered(rows), DetailSubtitle(rows.Count, total),
                                  PlaylistId: ordered);
    }

    /// <summary>
    /// The same list in the tree as it is now, or null if it no longer exists.
    ///
    /// <para><b>Why the id of a tree row is not an identity on a borrowed shelf.</b>
    /// <see cref="LibraryDatabase.MirrorPlaylistTree"/> writes the listing of a peer by deleting every
    /// <c>pl_node</c> and inserting all of them again, so every folder and playlist gets a new rowid.
    /// An open list that still has its old id asks for a playlist that does not exist, gets no tracks
    /// and is drawn as an empty list. Without this method, the list would show and then empty itself a
    /// moment later.</para>
    ///
    /// <para><b>Match by path first and by id second, in this order.</b> After a rewrite the old ids
    /// are not only out of date, they are reused: the same rowid space is handed out again, so the old
    /// id of a list can now belong to another list. Matching on it would show the wrong songs under the
    /// right name. The path (the names from the root down, and the kind) is kept by the rewrite,
    /// because it is a copy of the same listing. The id is used only where the path cannot answer: a
    /// list that was renamed while it is open still has its own id.</para>
    ///
    /// <para>A generated list and the queue are not stored rows and cannot become out of date in this
    /// way. Each is its own query, asked again every time.</para>
    /// </summary>
    public PlaylistTreeItem? Relocate(PlaylistTreeItem open) => Relocate(_roots, open);

    public static PlaylistTreeItem? Relocate(IReadOnlyList<PlaylistTreeItem> tree, PlaylistTreeItem open)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(open);

        if (open.Kind is PlaylistRowKind.Smart or PlaylistRowKind.NowPlaying)
            return open;

        var rows = new List<PlaylistTreeItem>();

        void Walk(IEnumerable<PlaylistTreeItem> at)
        {
            foreach (var row in at)
            {
                rows.Add(row);
                Walk(row.Children);
            }
        }

        Walk(tree);

        var at = Match(
            rows.Select(r => (r.Kind, (IReadOnlyList<string>)PathOf(r), r.NodeId)).ToList(),
            open.Kind, PathOf(open), open.NodeId);

        return at >= 0 ? rows[at] : null;
    }

    /// <summary>
    /// The rule itself: which of these rows is the one that was opened, or -1 if none is.
    ///
    /// <para>It is a separate function over names and ids and not over tree rows. A tree row has a XAML
    /// transform and cannot be built outside the thread that draws, so a rule over tree rows could not
    /// be tested.</para>
    /// </summary>
    public static int Match(
        IReadOnlyList<(PlaylistRowKind Kind, IReadOnlyList<string> Path, long? NodeId)> rows,
        PlaylistRowKind kind,
        IReadOnlyList<string> path,
        long? nodeId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(path);

        var byId = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind != kind)
                continue;

            // A path match always wins, and the path is checked first for every row.
            if (rows[i].Path.SequenceEqual(path, StringComparer.Ordinal))
                return i;

            if (byId < 0 && nodeId is { } want && rows[i].NodeId == want)
                byId = i;
        }

        return byId;
    }

    /// <summary>The names of a row from the root down. These names are kept when the mirror is
    /// rewritten.</summary>
    private static List<string> PathOf(PlaylistTreeItem item)
    {
        var names = new List<string>();
        for (var at = item; at is not null; at = at.Parent)
            names.Add(at.Name);
        names.Reverse();
        return names;
    }

    /// <summary>
    /// The same detail, built off the UI thread. The "generate a new list" button calls this method.
    ///
    /// <para>It works for generated lists only. This is a real restriction: the Now playing branch of
    /// <see cref="BuildDetail"/> reads the live queue, which belongs to the player, and reading it from
    /// the thread pool is not supported.</para>
    ///
    /// <para>Nothing in the returned <see cref="PlaylistDetail"/> is on screen yet. The rows are plain
    /// objects built on the thread pool and handed over whole. The caller must put them into the list
    /// on the dispatcher.</para>
    ///
    /// <para>An exception gives null and is not rethrown, for the reason given in
    /// <see cref="AskCoverAsync"/>: the library can close between the click and the run, which disposes
    /// the connection. The caller reads null as "nothing came back" and keeps the list that is on
    /// screen.</para>
    /// </summary>
    public Task<PlaylistDetail?> BuildSmartDetailAsync(PlaylistTreeItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != PlaylistRowKind.Smart)
            return Task.FromResult<PlaylistDetail?>(null);

        return Task.Run<PlaylistDetail?>(() =>
        {
            try
            {
                return BuildDetail(item, ListReading.NewList);
            }
            catch (Exception ex)
            {
                // The failure is logged, because the caller cannot tell it from a library that closed:
                // both give null, and null leaves the list on screen as it was. A press that has
                // already moved the mood window and then fails here looks like a button that did
                // nothing.
                App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Generate")
                   .LogWarning(ex, "[generate] {List}: the new list could not be built", item.DisplayName);
                return null;
            }
        });
    }

    /// <summary>
    /// Whether this generated list can give another list. This decides whether the header offers a way
    /// to ask for one. See <c>LibraryDatabase.SmartPlaylistCanRegenerate</c>, where both parts of the
    /// question are described.
    /// </summary>
    public bool CanRegenerate(SmartPlaylistId id) =>
        _library.IsOpen && _library.Shelf.SmartPlaylistCanRegenerate(id);

    // ── "don't recommend this act / this record" ──

    /// <summary>
    /// What one row of a generated list can be refused as, and whether it was already refused.
    ///
    /// <para>Both subjects are worked out here and not in the menu, because both are facts about the
    /// mirror and not about the row on screen. The folder is the name of the record in this library.
    /// The credit is composed in the one way that every reader of it uses
    /// (<see cref="RecommendRefusals.CreditOf"/>). An empty subject cannot be refused in this list: a
    /// file outside the library has no folder, and a song without tags names no act.</para>
    /// </summary>
    /// <param name="Artist">The credit, or "" if the song names no act.</param>
    /// <param name="Album">The folder of the record, or "" if the song is not under this library.</param>
    public sealed record RefusalSubjects(
        string Artist, string Album, bool ArtistRefused, bool AlbumRefused)
    {
        public static readonly RefusalSubjects None = new("", "", false, false);
    }

    /// <summary>Which subjects of a row this list was already told to stop offering. It is read when
    /// the menu opens, so the menu entry shows what pressing it will do now, not what was true when
    /// the list was drawn.</summary>
    public RefusalSubjects RefusalsFor(SmartPlaylistId id, TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!_library.IsOpen)
            return RefusalSubjects.None;

        var artist = RecommendRefusals.CreditOf(track.AlbumArtist, track.Artist);
        var album = ShelfFolderOf(track) ?? "";
        var said = _library.Shelf.RecommendRefusalsSaid();

        return new RefusalSubjects(
            artist, album,
            said.Refused(RecommendScope.Artist, artist, id.Kind),
            said.Refused(RecommendScope.Album, album, id.Kind));
    }

    /// <summary>
    /// Stores the refusal, or removes it.
    ///
    /// <para>The refusal applies to the kind of list and not to the one list that the listener was
    /// looking at. A verdict for one song works in the same way (see
    /// <c>LibraryDatabase.SetRecommendRefusal</c>). "Not this act on the dancefloor" covers Dancefloor
    /// and every genre and decade row made from it. Chill, Focus and the others still offer the act.</para>
    /// </summary>
    public void SetRefusal(SmartPlaylistId id, RecommendScope scope, string subject, bool refused)
    {
        if (!_library.IsOpen || string.IsNullOrWhiteSpace(subject))
            return;

        _library.Shelf.SetRecommendRefusal(scope, subject, id.Kind, refused, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// The folder of a track, relative to the library, or null if the track is not under the shelf that
    /// is being read.
    ///
    /// <para>This uses the root of the shelf and not the root of this library. A generated list on a
    /// borrowed collection is drawn from the mirror of that computer. A refusal stored with a path
    /// relative to the wrong root would name a record that does not exist. <see cref="BuildDetail"/>
    /// makes the same join.</para>
    /// </summary>
    private string? ShelfFolderOf(TrackInfo track)
    {
        if (_library.ShelfRoot is not { } root)
            return null;
        if (System.IO.Path.GetDirectoryName(track.FilePath) is not { Length: > 0 } dir)
            return null;

        var relative = System.IO.Path.GetRelativePath(root, dir)
            .Replace(System.IO.Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    /// <summary>Gives each row its position, counting from one. This is done here and not in the row
    /// builders, because the position belongs to the list and not to a song. The same track in two
    /// playlists has two different positions, and neither number belongs to the file.</summary>
    private static List<PlaylistTrackRow> Numbered(List<PlaylistTrackRow> rows)
    {
        for (var at = 0; at < rows.Count; at++)
            rows[at].Position = at + 1;
        return rows;
    }

    private static PlaylistTrackRow MakeDetailRow(TrackInfo t, long? itemId)
    {
        var dir = System.IO.Path.GetDirectoryName(t.FilePath);
        var art = dir is not null ? FolderArtWriter.FindFolderImage(dir) : null;
        return new PlaylistTrackRow
        {
            Info = t,
            Title = string.IsNullOrWhiteSpace(t.Title) ? System.IO.Path.GetFileNameWithoutExtension(t.FilePath) : t.Title!,
            Artist = t.Artist ?? t.AlbumArtist ?? "",
            Album = t.Album ?? "",
            Year = t.Year > 0 ? t.Year.ToString() : "",
            Genre = t.Genre ?? "",
            Duration = FormatSeconds(t.DurationSeconds),
            ArtUri = art,
            ItemId = itemId,
        };
    }

    private static PlaylistTrackRow MakeQueueRow(RPaMp.Models.PlaylistTrack t, int queueIndex)
    {
        var dir = System.IO.Path.GetDirectoryName(t.FilePath);
        var art = dir is not null ? FolderArtWriter.FindFolderImage(dir) : null;
        // The queue item does not keep its full TrackInfo. This builds just enough to play the row.
        var info = new TrackInfo
        {
            FilePath = t.FilePath,
            Extension = System.IO.Path.GetExtension(t.FilePath),
            DurationSeconds = t.DurationSeconds,
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
        };
        return new PlaylistTrackRow
        {
            Info = info,
            Title = t.Title,
            Artist = t.Artist ?? "",
            Album = t.Album ?? "",
            Year = "",
            Genre = "",
            Duration = FormatSeconds(t.DurationSeconds),
            ArtUri = art,
            ItemId = null,
            QueueIndex = queueIndex,
        };
    }

    private static string DetailSubtitle(int count, double seconds) =>
        $"{Strings.Loc.Format("Playlists_SongCount", count)}  ·  {FormatLength(seconds)}";

    private static string FormatSeconds(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string FormatLength(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? Strings.Loc.Format("Playlists_LenHours", (int)t.TotalHours, t.Minutes)
            : Strings.Loc.Format("Playlists_LenMinutes", (int)t.TotalMinutes);
    }

    /// <summary>Plays the whole detail list through RPaMp, shuffled if asked.</summary>
    /// <param name="generated">Which of the library's own lists this is, or null for a stored playlist,
    /// a folder or the queue. It has no default: it decides whether the transport shows its verdict
    /// keys, and a caller that forgot it would never show them.</param>
    public void PlayRows(IReadOnlyList<PlaylistTrackRow> rows, bool shuffle, SmartPlaylistId? generated)
    {
        if (rows.Count == 0)
            return;
        var tracks = rows.Select(r => r.Info).ToList();
        if (shuffle)
            tracks = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        _amp.AddTracks(tracks, play: true);
        NoteGenerated(generated);
    }

    /// <summary>Tells the deck that the queue it was just given is one of the library's own lists, if
    /// it is. This is called after the fill on purpose: the fill clears the source of the queue, and both
    /// calls reach the player through the same dispatcher queue in the order they were made.</summary>
    private void NoteGenerated(SmartPlaylistId? generated)
    {
        if (generated is { } id)
            _amp.NoteGenerated(id);
    }

    /// <summary>Gives the whole detail list to the DJ, which orders it and plays it as one continuous
    /// beat-matched mix. The DJ decides the order, not the list. That is the purpose of the button.</summary>
    public void AutoMixRows(IReadOnlyList<PlaylistTrackRow> rows, SmartPlaylistId? generated)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return;
        _amp.StartDjSet([.. rows.Select(r => r.Info)]);
        NoteGenerated(generated);
    }

    /// <summary>
    /// Double-clicking a track in an open list: play from that track, and let the list continue after
    /// it.
    ///
    /// <para>The kind of list decides what "after it" means, and neither case is the album. A queued
    /// track plays where it stands. The queue is a running order that someone built, and jumping inside
    /// it must not rewrite it. The track of a stored playlist makes the queue from that track down to
    /// the end of that playlist, which is what picking a track from a list asks for.</para>
    /// </summary>
    public void PlayRowInList(
        IReadOnlyList<PlaylistTrackRow> rows, PlaylistTrackRow row, SmartPlaylistId? generated)
    {
        if (row.QueueIndex is { } queued)
        {
            _amp.PlayQueueIndex(queued);
            return;
        }

        var at = rows.ToList().IndexOf(row);
        if (at < 0)
        {
            // The row is not in the list that was passed in. Use its record instead. That is an album
            // and not a generated list, however the row was reached.
            _amp.PlayAlbumFrom(row.Info);
            return;
        }
        _amp.PlayTracksFrom([.. rows.Skip(at).Select(r => r.Info)]);
        NoteGenerated(generated);
    }

    /// <summary>
    /// "Start DJ here" on a row of the open list: the DJ takes that track and all tracks under it in
    /// the list, and plays them as one mix. The set replaces the queue in both cases, so a queue row
    /// and a row of a stored playlist do the same thing here. This is what "here" means in a list.
    /// </summary>
    public void StartDjFromRow(
        IReadOnlyList<PlaylistTrackRow> rows, PlaylistTrackRow row, SmartPlaylistId? generated)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(row);
        _amp.StartDjSet(AlbumTracklist.From([.. rows.Select(r => r.Info)], row.Info));
        NoteGenerated(generated);
    }

    /// <summary>"Start radio" on a row: that song plays and the queue fills with similar songs. The list
    /// that contains the row has no part in this, so the verdict keys of the queue are not shown (there
    /// is no <c>NoteGenerated</c> call).</summary>
    public void StartRadioFromRow(PlaylistTrackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _amp.StartRadio(row.Info);
    }

    /// <summary>"Play now" on a row of the queue: moves it to the front and starts it. This is the
    /// explicit action. Double-clicking plays the row where it stands.</summary>
    public void PlayQueueRowNow(PlaylistTrackRow row)
    {
        if (row.QueueIndex is { } at)
            _amp.MoveToTopAndPlay(at);
    }

    /// <summary>Adds tracks to the end of the live queue. This is the "Now playing" entry of "Add to
    /// playlist". Playback is not interrupted.</summary>
    public void AddToQueue(IEnumerable<TrackInfo> tracks)
    {
        var list = tracks.ToList();
        if (list.Count > 0)
            _amp.AddTracks(list, play: false);
    }

    /// <summary>Removes rows from the list they came from: from the queue by position, from a stored
    /// playlist by pl_item id. One call serves both, so the Delete key of the list view and its
    /// context menu do not need to know which kind of list is open.</summary>
    public void RemoveRows(IEnumerable<PlaylistTrackRow> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
            return;

        var queueIndexes = list.Where(r => r.QueueIndex is not null).Select(r => r.QueueIndex!.Value).ToList();
        if (queueIndexes.Count > 0)
            _amp.RemoveFromQueue(queueIndexes);

        // Rows are removed only from a list that this library owns. A borrowed list is the owner's
        // arrangement of their records. A row removed from the mirror here would stay removed until
        // the other computer sends its listing again, and then come back.
        var itemIds = list.Where(r => r.ItemId is not null).Select(r => r.ItemId!.Value).ToList();
        if (itemIds.Count > 0 && Mine is { } db)
        {
            foreach (var id in itemIds)
                db.RemovePlaylistItem(id);
            Reload();
        }
    }

    private IEnumerable<long> DescendantPlaylists(long folderId)
    {
        // Walks the built tree under this folder and returns the playlists in the order shown.
        var start = Find(_roots, folderId);
        if (start is null)
            yield break;
        foreach (var pl in PlaylistsUnder(start))
            yield return pl;

        static IEnumerable<long> PlaylistsUnder(PlaylistTreeItem node)
        {
            foreach (var child in node.Children)
            {
                if (child.Kind == PlaylistRowKind.Playlist && child.NodeId is { } id)
                    yield return id;
                if (child.Kind is PlaylistRowKind.Folder or PlaylistRowKind.Playlist)
                    foreach (var deep in PlaylistsUnder(child))
                        yield return deep;
            }
        }
    }

    private static PlaylistTreeItem? Find(IEnumerable<PlaylistTreeItem> items, long nodeId)
    {
        foreach (var item in items)
        {
            if (item.NodeId == nodeId)
                return item;
            if (Find(item.Children, nodeId) is { } hit)
                return hit;
        }
        return null;
    }

    /// <summary>
    /// Reads the master that was chosen for a folder, off the UI thread, and sets it on
    /// <paramref name="item"/> if there is one. It is called at most once for each item.
    /// <see cref="PlaylistTreeItem.EnsureCoverLoaded"/> is the gate that stops a second request for the
    /// same folder.
    ///
    /// <para>The read uses the tree that the row belongs to. The folders of a borrowed shelf can also
    /// have a chosen picture, and it is stored under the segment of the peer, not under that of this
    /// machine. <see cref="PlaylistCovers.MadeAsync"/> makes the same split. This method writes no
    /// master. Only the menu of the user writes a master, and only for the tree that is open for
    /// writing (<see cref="Mine"/>).</para>
    /// </summary>
    internal void LoadFolderCoverAsync(PlaylistTreeItem item, long nodeId)
        => _ = LoadFolderCoverCoreAsync(item, nodeId);

    private async Task LoadFolderCoverCoreAsync(PlaylistTreeItem item, long nodeId)
    {
        if (!_library.IsOpen)
        {
            // The check did not happen. This must not latch as a real "no master" answer does. See the
            // note on ResetCoverRequest.
            item.ResetCoverRequest();
            return;
        }

        var shelf = IsBorrowed ? PlaylistShelf.Borrowed : PlaylistShelf.Own;

        byte[]? bytes;
        try
        {
            // The master here, or on a borrowed shelf the picture that its owner chose, from their
            // computer.
            bytes = await PlaylistCovers.ChosenAsync(nodeId, item.Cover, shelf, CancellationToken.None);
        }
        catch
        {
            bytes = null;   // a master that cannot be read means the folder keeps its plain glyph
        }

        if (bytes is not { Length: > 0 })
            return;   // checked and nothing found: a real answer, which stays latched until RefreshCover

        // TinyEdge and not RowEdge: this is a 12-point icon, not a 56-point list row. RowEdge is 256 px
        // and would decode a quarter of a megabyte for a picture drawn about twelve points wide.
        // TinyEdge is the smallest size that the app keeps. See the note on ArtScaler.
        ImageSource? shown;
        try { shown = await RemoteImage.DecodeAsync(bytes, ArtScaler.TinyEdge); }
        catch { shown = null; }

        // The image is set on the item, whatever row shows it now. See the note on EnsureCoverLoaded.
        // A container that was reused for a different folder no longer reads this property, and the
        // container that does read it is already bound to it.
        item.SetCoverImage(shown);
    }

    /// <summary>
    /// Draws the square that the icon grid shows for one row, off the UI thread, and sets it on the
    /// item. It is called at most once for each row in a session.
    /// <see cref="PlaylistTreeItem.EnsureTileLoaded"/> is the gate.
    ///
    /// <para>Each kind of row has its own answer, and the switch below is the whole rule:</para>
    /// <list type="bullet">
    ///   <item>a <b>playlist</b> is drawn as every surface that shows a playlist draws it: the quilt of
    ///   its own records, or the picture that someone chose instead
    ///   (<see cref="PlaylistCovers.MadeAsync"/>). The tile, the shelf of the artist page and the head
    ///   of the track list therefore show one picture;</item>
    ///   <item>a <b>generated list</b> is its name on its own palette, and nothing is read from the
    ///   disk for it;</item>
    ///   <item>a <b>folder</b> is a place and not a list. It has a picture only if someone chose one.
    ///   Without one it keeps the folder glyph, as a file browser does;</item>
    ///   <item>every other row (Now playing, the folder of the generated lists) has no picture. These
    ///   are the rows of the app itself, and their glyph is their face.</item>
    /// </list>
    ///
    /// <para>The picture is set on the item and never on a row, for the reason given in
    /// <see cref="LoadFolderCoverAsync"/>: a draw takes so long that the container that asked for it
    /// often shows a different playlist when the picture is ready.</para>
    /// </summary>
    internal void LoadTileCoverAsync(PlaylistTreeItem item, int decodeEdge)
        => _ = LoadTileCoverCoreAsync(item, decodeEdge);

    private async Task LoadTileCoverCoreAsync(PlaylistTreeItem item, int decodeEdge)
    {
        if (!_library.IsOpen)
        {
            // The check did not happen. As in ResetCoverRequest, this means "not asked", not an answer.
            item.ResetTileRequest();
            return;
        }

        var borrowed = IsBorrowed;
        var shelf = borrowed ? PlaylistShelf.Borrowed : PlaylistShelf.Own;

        byte[]? bytes;
        try
        {
            bytes = item.Kind switch
            {
                PlaylistRowKind.Folder when item.NodeId is { } folder
                    => await PlaylistCovers.ChosenAsync(folder, item.Cover, shelf, CancellationToken.None),

                // The colour of the picture shows which list this is before the words are read. See
                // SmartPlaylistFace.Cover for the colour of each mood.
                PlaylistRowKind.Smart
                    => await GeneratedCover.SquareAsync(item.SmartId, CancellationToken.None),

                PlaylistRowKind.Playlist when item.NodeId is { } list
                    => (await PlaylistCovers.MadeAsync(
                            list, item.Name, item.Cover, shelf, CancellationToken.None)).Cover,

                // A nested reference is the list that it points to, so both have one picture.
                PlaylistRowKind.Ref when item.RefPlaylistId is { } target
                    => (await PlaylistCovers.MadeAsync(
                            target, item.Name, item.Cover, shelf, CancellationToken.None)).Cover,

                _ => null,
            };
        }
        catch
        {
            bytes = null;   // a picture that cannot be drawn means the tile keeps its glyph
        }

        if (bytes is not { Length: > 0 })
            return;   // checked and nothing found: a real answer, latched until RefreshCover

        ImageSource? shown;
        try { shown = await RemoteImage.DecodeAsync(bytes, decodeEdge); }
        catch { shown = null; }

        item.SetTileImage(shown);
    }

    /// <summary>The playlists that a song can be added to, with their depth, which is used to indent the
    /// menu.</summary>
    public IReadOnlyList<(long Id, string Name, int Depth)> PlaylistPickerEntries()
    {
        var list = new List<(long, string, int)>();
        void Walk(PlaylistTreeItem node, int depth)
        {
            if (node.Kind == PlaylistRowKind.Playlist && node.NodeId is { } id)
                list.Add((id, node.Name, depth));
            if (node.Kind is PlaylistRowKind.Folder or PlaylistRowKind.Playlist)
                foreach (var child in node.Children)
                    Walk(child, depth + 1);
        }
        foreach (var root in _roots.Where(r => r.Kind != PlaylistRowKind.NowPlaying))
            Walk(root, 0);
        return list;
    }

    public void Dispose()
    {
        _amp.Transport.Playlist.CollectionChanged -= OnNowPlayingChanged;
        _library.Changed -= OnLibraryChanged;
        _cloud.Changed -= OnCloudChanged;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row in the playlist tree: a folder, a playlist, the pinned Now playing playlist, a song
/// or a reference to another playlist. The tree is flattened for display, so the row has its own depth
/// and expansion state.</summary>
[RpmBindable]
public sealed partial class PlaylistTreeItem : INotifyPropertyChanged
{
    private readonly PlaylistModel _model;
    private string _name;
    private int _depth;
    private bool _isExpanded = true;

    public PlaylistTreeItem(PlaylistModel model, PlaylistRowKind kind, string name)
    {
        _model = model;
        Kind = kind;
        _name = name;
    }

    public PlaylistRowKind Kind { get; }

    /// <summary>Which generated playlist this row is, on the rows that are one. It is null on
    /// everything that the user made. Those rows are keyed by <see cref="NodeId"/>. A generated list has
    /// no stored node: it is a query (see <see cref="SmartPlaylists"/>).</summary>
    public SmartPlaylistKind? SmartKind { get; init; }

    /// <summary>The genre that this generated list is limited to, or null if it covers the whole shelf.
    /// It is set only on the genre rows under a mood. See
    /// <see cref="SmartPlaylists.BrokenOutByGenre"/>.</summary>
    public string? SmartGenre { get; init; }

    /// <summary>Which generated list this row stands for, with both parts together. Every reader of a
    /// smart row should use this: a kind alone cannot tell "calm" from "calm rock".</summary>
    /// <summary>The decade that this generated list is limited to, or zero. It is never set together
    /// with <see cref="SmartGenre"/>. See <see cref="SmartPlaylistId"/> for the reason why a list is cut
    /// by one or the other and never by both.</summary>
    public int SmartDecade { get; init; }

    public SmartPlaylistId SmartId =>
        new(SmartKind ?? SmartPlaylistKind.FavoriteTracks, SmartGenre, SmartDecade);

    private PlaylistCover _cover = new PlaylistCover.Automatic();

    /// <summary>How the picture of this node is chosen, as of the last reload. See
    /// <see cref="PlaylistModel.Reload"/>, which sets it while it has the nodes. It is Automatic on the
    /// rows that have no stored node (Now playing, the generated lists and their folder), which is the
    /// answer those rows would give anyway.</summary>
    public PlaylistCover Cover
    {
        get => _cover;
        internal set
        {
            var face = Face;
            _cover = value;
            if (Face == face)
                return;

            // The mark is drawn from the face, so a row that became one of the generated lists of the
            // owner (or stopped being one) must raise the change. Rows stay after a reload and keep
            // their bindings.
            OnChanged(nameof(Face));
            OnChanged(nameof(Glyph));
            OnChanged(nameof(TileGlyphSize));
            OnChanged(nameof(GlyphBrush));
        }
    }

    private bool _holdsGeneratedListsOnly;

    /// <summary>
    /// A folder on a borrowed shelf that holds only the generated lists of the owner. It is one of the
    /// own folders of the app of the owner (the moods, the decades, the folder of the generated lists),
    /// and it is drawn as this app draws its own folders. <see cref="PlaylistModel.Reload"/> sets it from
    /// the current tree.
    ///
    /// <para>It is derived and exact. The borrowed shelf puts the generated lists of the owner only in
    /// folders that it made for them (<c>BorrowedMoodLists</c>), and the other folders of the owner
    /// cannot hold a generated list. A folder that holds nothing is not one.</para>
    /// </summary>
    public bool HoldsGeneratedListsOnly
    {
        get => _holdsGeneratedListsOnly;
        internal set
        {
            if (_holdsGeneratedListsOnly == value) return;
            _holdsGeneratedListsOnly = value;
            OnChanged();
            OnChanged(nameof(Glyph));
            OnChanged(nameof(TileGlyphSize));
            OnChanged(nameof(GlyphBrush));
        }
    }

    /// <summary>
    /// One of the own folders of the app: the folders that hold the generated lists on the shelf of the
    /// user (<see cref="PlaylistRowKind.SmartFolder"/>), and the same folders on a borrowed shelf
    /// (<see cref="HoldsGeneratedListsOnly"/>). They hold lists and not songs, so a click on one expands
    /// it, and it offers no menu, on either shelf.
    ///
    /// <para>This is the one question that the list view asks, so that the two shelves behave in the
    /// same way. A borrowed copy must not be treated as a folder of songs, because a click would then
    /// open every song in it.</para>
    /// </summary>
    public bool IsAppFolder => AppFolder(Kind, HoldsGeneratedListsOnly);

    /// <summary>The rule behind <see cref="IsAppFolder"/>, separate so that it can be tested. A row
    /// builds XAML objects and cannot be made in a test host.</summary>
    internal static bool AppFolder(PlaylistRowKind kind, bool holdsGeneratedListsOnly) =>
        kind == PlaylistRowKind.SmartFolder || (kind == PlaylistRowKind.Folder && holdsGeneratedListsOnly);

    /// <summary>
    /// The generated list that this row is drawn as, or null if it is drawn as itself.
    ///
    /// <para>A generated row is drawn as the list that it is. A stored playlist on a borrowed shelf can
    /// also be drawn as one: the generated lists of the owner arrive as fixed lists of records and are
    /// kept as playlists (see <see cref="PlaylistCover.Generated"/>). They have the same picture, mark
    /// and colour as the same list on the shelf of the owner. What the row holds is still read as
    /// <see cref="Kind"/> says: a borrowed copy is a stored list and is opened and played as one.</para>
    /// </summary>
    public SmartPlaylistId? Face => FaceOf(Kind, SmartId, _cover);

    /// <summary>The rule behind <see cref="Face"/>, separate so that it can be tested. A row builds XAML
    /// objects and cannot be made in a test host.</summary>
    internal static SmartPlaylistId? FaceOf(PlaylistRowKind kind, SmartPlaylistId smart, PlaylistCover cover)
        => kind switch
        {
            PlaylistRowKind.Smart => smart,
            PlaylistRowKind.Playlist when cover is PlaylistCover.Generated lent => lent.Id,
            _ => null,
        };

    public long? NodeId { get; init; }          // folder / playlist node id
    public long? ItemId { get; init; }          // pl_item id (song / ref rows)
    public long? OwnerPlaylistId { get; init; }
    public long? RefPlaylistId { get; init; }

    public ObservableCollection<PlaylistTreeItem> Children { get; } = new();

    /// <summary>The folder that this row is in, or null at the root. A drag must know which list a row
    /// belongs to before it can put another row beside it. The tree that the panel shows is flattened,
    /// and a flattened tree keeps the depth but not the parent.</summary>
    public PlaylistTreeItem? Parent { get; internal set; }

    /// <summary>The offset of this row from the line where the tree put it: zero, except while the row
    /// is being dragged. It is on the row and not on its container, because the list reuses containers
    /// and an offset left on one container would move another playlist.</summary>
    public Microsoft.UI.Xaml.Media.TranslateTransform Drop { get; } = new();

    private bool _isDropInto;

    /// <summary>Whether a row that is being dragged would go inside this one if it were dropped now.
    /// The folder is highlighted, and no line is drawn between two rows.</summary>
    public bool IsDropInto
    {
        get => _isDropInto;
        set
        {
            if (_isDropInto == value) return;
            _isDropInto = value;
            OnChanged();
            OnChanged(nameof(DropIntoVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility DropIntoVisibility =>
        _isDropInto ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Whether this row can be picked up and moved somewhere else. Folders and playlists can.
    /// Now playing cannot, because it is not a list that someone made and it is not theirs to
    /// move.</summary>
    public bool CanCarry => NodeId is not null && Kind is PlaylistRowKind.Folder or PlaylistRowKind.Playlist;

    /// <summary>Whether other rows can be dropped inside this one.</summary>
    public bool CanTakeChildren => Kind == PlaylistRowKind.Folder;

    /// <summary>Whether <paramref name="other"/> is this row or is below it. A folder must not be moved
    /// into itself or below itself: a subtree moved into its own branch would be cut off from the root,
    /// and its playlists could not be reached again.</summary>
    public bool Holds(PlaylistTreeItem other)
    {
        for (var at = other; at is not null; at = at.Parent)
            if (ReferenceEquals(at, this))
                return true;

        return false;
    }

    private bool _isInCloud;
    private bool _isCloudUploading;
    private bool _isCloudLeaving;

    /// <summary>The user has put this playlist in the cloud. The row shows a small cloud, and the menu
    /// offers to take it out of the cloud, not to put it in.</summary>
    public bool IsInCloud
    {
        get => _isInCloud;
        set { if (_isInCloud == value) return; _isInCloud = value; RaiseCloudChanged(); }
    }

    /// <summary>The tracks of this playlist are queued or uploading now.</summary>
    public bool IsCloudUploading
    {
        get => _isCloudUploading;
        set { if (_isCloudUploading == value) return; _isCloudUploading = value; RaiseCloudChanged(); }
    }

    /// <summary>The playlist is being taken out of the cloud.</summary>
    public bool IsCloudLeaving
    {
        get => _isCloudLeaving;
        set { if (_isCloudLeaving == value) return; _isCloudLeaving = value; RaiseCloudChanged(); }
    }

    private void RaiseCloudChanged()
    {
        OnChanged(nameof(IsInCloud));
        OnChanged(nameof(IsCloudUploading));
        OnChanged(nameof(IsCloudLeaving));
        OnChanged(nameof(IsCloudBusy));
        OnChanged(nameof(CloudGlyph));
        OnChanged(nameof(CanAddToCloud));
        OnChanged(nameof(CanRemoveFromCloud));
        OnChanged(nameof(CloudBadgeVisibility));
        if (!IsCloudBusy)
            CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;
    }

    /// <summary>Only a playlist can be in the cloud, and only a playlist that is not there already can
    /// be put in it. A folder is a way to arrange playlists, not something to play.</summary>
    public bool CanAddToCloud => Kind == PlaylistRowKind.Playlist && !IsInCloud;

    public bool CanRemoveFromCloud => Kind == PlaylistRowKind.Playlist && IsInCloud;

    /// <summary>Work in either direction. The badge pulses while this is true.</summary>
    public bool IsCloudBusy => IsCloudUploading || IsCloudLeaving;

    /// <summary>A plain cloud at rest, a cloud with an arrow while tracks upload, and a cloud with a
    /// line through it while the playlist is taken out.</summary>
    public string CloudGlyph =>
        IsCloudLeaving ? "\uf381"          // cloud-slash
        : IsCloudUploading ? "\uf0ee"      // cloud-arrow-up
        : "\uf0c2";                        // cloud

    /// <summary>The badge is shown while the playlist is in the cloud, and while it is being taken out
    /// of the cloud. The row still shows the state during the removal.</summary>
    public Microsoft.UI.Xaml.Visibility CloudBadgeVisibility =>
        IsInCloud || IsCloudLeaving
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    private double _cloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;

    /// <summary>The opacity of the badge. The tick of the tree sets it on every frame while the playlist
    /// is busy.</summary>
    public double CloudPulse
    {
        get => _cloudPulse;
        set { if (_cloudPulse == value) return; _cloudPulse = value; OnChanged(); }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnChanged();
            // The row binds DisplayName, not Name. Rows stay after a reload, so a rename must raise
            // the change for the binding itself.
            OnChanged(nameof(DisplayName));
        }
    }

    /// <summary>The Now playing playlist shows a fixed, localised label and not its stored name.</summary>
    public string DisplayName => Kind == PlaylistRowKind.NowPlaying ? RPM.Desktop.Strings.Loc.Get("Playlists_NowPlaying") : _name;

    private bool _isExplicit;

    /// <summary>
    /// This playlist holds at least one explicit song. The mark is an E after its name. A record shows
    /// the same mark on the album wall, and an opened playlist shows it over its own track list (see
    /// <c>PlaylistView.ShowDetail</c>).
    ///
    /// <para><b>Two kinds of row set it, in different ways.</b> A playlist that someone made is set on
    /// every reload from <c>LibraryDatabase.QueryExplicitPlaylists</c>, which answers for the whole
    /// tree in one read of what is stored in each playlist. A generated list has nothing stored in it
    /// because it is a query, so its answer comes from the same pass that counts it
    /// (<c>LibraryDatabase.SmartPlaylistTotals</c>) and follows the collection. If a song is retagged
    /// as explicit, the E appears on a row that nobody touched.</para>
    ///
    /// <para>It is false on a folder of any kind. A folder is not a list of songs, so the mark would
    /// describe whatever is inside the folder and not the row.</para>
    /// </summary>
    public bool IsExplicit
    {
        get => _isExplicit;
        internal set { if (_isExplicit == value) return; _isExplicit = value; OnChanged(); }
    }

    public int Depth
    {
        get => _depth;
        set { if (_depth == value) return; _depth = value; OnChanged(); OnChanged(nameof(Indent)); }
    }

    /// <summary>The left inset of the row, a fixed step for each level.</summary>
    public Microsoft.UI.Xaml.Thickness Indent => new(_depth * 16, 0, 0, 0);

    public bool CanExpand => Children.Count > 0;

    /// <summary>The children of the row were linked again by a reload. The model calls this, because a
    /// row that stays after a reload keeps its bindings, and nothing else tells them that the chevron
    /// changed.</summary>
    internal void NotifyChildrenChanged()
    {
        OnChanged(nameof(CanExpand));
        OnChanged(nameof(ChevronGlyph));
        OnChanged(nameof(ChevronName));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnChanged();
            OnChanged(nameof(ChevronGlyph));
            OnChanged(nameof(ChevronName));
            _model.RebuildVisible();
        }
    }

    public void ToggleExpand() { if (CanExpand) IsExpanded = !IsExpanded; }

    public string ChevronGlyph => !CanExpand ? "" : _isExpanded ? "\uf078" : "\uf054"; // chevron-down / -right

    /// <summary>The accessible name of the chevron: what a press does, as the glyph shows it.</summary>
    public string ChevronName =>
        !CanExpand ? "" : RPM.Desktop.Strings.Loc.Get(_isExpanded ? "Playlists_Collapse" : "Playlists_Expand");

    public string Glyph => Kind switch
    {
        PlaylistRowKind.NowPlaying => "\uf04b",   // play
        PlaylistRowKind.Folder when HoldsGeneratedListsOnly => SmartPlaylistFace.FolderGlyph,
        PlaylistRowKind.Folder => "\uf07b",       // folder
        PlaylistRowKind.Playlist when Face is { } lent => SmartPlaylistFace.Glyph(lent.Kind),
        PlaylistRowKind.Playlist => "\uf8c9",     // list-music
        PlaylistRowKind.Ref => "\uf0c1",          // link (a nested playlist reference)
        PlaylistRowKind.Smart => SmartPlaylistFace.Glyph(SmartKind ?? SmartPlaylistKind.FavoriteTracks),
        PlaylistRowKind.SmartFolder => SmartPlaylistFace.FolderGlyph,
        _ => "\uf001",                            // music note (song)
    };

    /// <summary>The size at which the grid draws the glyph of this row. A folder, a play triangle and a
    /// list of notes then look the same size on the tiles, which one font size does not give. See
    /// <see cref="RightPane.PlaylistIcons.TileGlyphSize"/>. The list beside the grid is not affected:
    /// its glyphs are in a column at one small size, where the difference is not visible.</summary>
    public double TileGlyphSize => RightPane.PlaylistIcons.TileGlyphSize(Glyph);

    /// <summary>
    /// The live tile measurement that the grid uses for its size. It is the same type, with the same
    /// arithmetic, as the cards of the album wall (<see cref="Library.Models.AlbumTileMetrics"/>), so a
    /// playlist and a record are the same square in both grids.
    ///
    /// <para>It is on the row and not reached from the view, for the same reason as
    /// <c>AlbumCard.Metrics</c>: the tile template uses compiled bindings, and a compiled binding reads
    /// the row. All tiles on the grid share one instance, so the view sets the size once and every tile
    /// follows.</para>
    ///
    /// <para>It is set when the tiles of the grid are built (<c>PlaylistView.RebuildTiles</c>) and not
    /// after that, so it can be a plain property. The object does not change, only its numbers change,
    /// and the object raises those changes itself. A row that was never a tile keeps
    /// <see cref="RightPane.PlaylistIcons.Unmeasured"/>, which is the opening size of the grid.</para>
    /// </summary>
    public Library.Models.AlbumTileMetrics Metrics { get; internal set; } =
        RightPane.PlaylistIcons.Unmeasured;

    // The folder glyph is azure, as in the library grid. Playlists and items are white. The folder of
    // the generated lists is cyan, the colour of the brand, and not azure, because it is a row of the
    // app and not one of the rows of the user.
    public Brush GlyphBrush => new SolidColorBrush(Kind switch
    {
        PlaylistRowKind.Folder when HoldsGeneratedListsOnly => Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF),
        PlaylistRowKind.Folder => Color.FromArgb(0xFF, 0x03, 0x71, 0xD0),
        PlaylistRowKind.SmartFolder => Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF),
        // Only two marks have a colour: the red heart of the favourites, which is the mark that the
        // songs themselves have, and the fire. The other rows look like ordinary rows. The value comes
        // from SmartPlaylistFace, which chooses the mark. The home screen draws the same marks, and
        // one list must not have two colours on two screens.
        PlaylistRowKind.Smart => SmartPlaylistFace.Ink(SmartKind ?? SmartPlaylistKind.FavoriteTracks),
        PlaylistRowKind.Playlist when Face is { } lent => SmartPlaylistFace.Ink(lent.Kind),
        _ => Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
    });

    public bool IsFolder => Kind == PlaylistRowKind.Folder;
    public bool IsPlaylist => Kind == PlaylistRowKind.Playlist;
    public bool IsNowPlaying => Kind == PlaylistRowKind.NowPlaying;
    public bool IsNode => NodeId is not null;
    public bool IsItem => ItemId is not null;

    // ── the picture of a folder, loaded when it is needed ──

    private bool _coverRequested;
    private ImageSource? _coverImage;

    /// <summary>The master that was chosen for a folder, after it has been read. It is null on every
    /// other kind of row, and null on a folder until <see cref="EnsureCoverLoaded"/> has found
    /// one.</summary>
    public ImageSource? CoverImage
    {
        get => _coverImage;
        private set
        {
            if (Equals(_coverImage, value)) return;
            _coverImage = value;
            OnChanged();
            OnChanged(nameof(CoverVisibility));
            OnChanged(nameof(GlyphVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility CoverVisibility =>
        _coverImage is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The plain folder glyph is shown until a picture is loaded, and hidden after that. They
    /// are never shown together, or a folder with a picture would show the folder icon under it.</summary>
    public Microsoft.UI.Xaml.Visibility GlyphVisibility =>
        _coverImage is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Starts loading the master of this folder, once. Call it from the <c>DataContextChanged</c> of the
    /// row and not at construction. The tree is flattened and virtualised, so a container is reused for
    /// a different item at every scroll, and <c>DataContextChanged</c> is the event that fires when this
    /// happens. A plain <c>Loaded</c> event does not reliably fire again for a pooled container that
    /// only gets new content and is not added to the tree again.
    ///
    /// <para><b>Keyed by the item, not the row.</b> <see cref="_coverRequested"/> is on this object, and
    /// the result is set on <see cref="CoverImage"/> of this object, never on a row. When the disk read
    /// ends, the row may already be reused for a different folder. The binding follows the row from one
    /// item to the next, and this method follows only the item.</para>
    /// </summary>
    internal void EnsureCoverLoaded()
    {
        if (Kind != PlaylistRowKind.Folder || _coverRequested || NodeId is not { } id)
            return;

        _coverRequested = true;
        _model.LoadFolderCoverAsync(this, id);
    }

    /// <summary>
    /// Lets a later <see cref="EnsureCoverLoaded"/> try again after a load that did not reach an answer
    /// because the library was not open when it ran. That is "not loaded", which is different from
    /// "loaded and there is nothing". The second is a real, stable answer and stays latched until
    /// <see cref="RefreshCover"/> changes it. The first gave no answer, and if it stayed latched, this
    /// folder would not be asked again for the rest of the session.
    /// </summary>
    internal void ResetCoverRequest() => _coverRequested = false;

    /// <summary>
    /// The picture of this row changed. See <see cref="PlaylistModel.SetCoverAsync"/>, the only caller.
    /// It clears the picture that is shown at once, so that the old picture (or the plain glyph, for a
    /// folder that just got a picture) does not stay until a scroll reuses the container of this item.
    /// Then it asks for the picture that exists now.
    ///
    /// <para><see cref="PlaylistModel"/> keeps the <see cref="PlaylistTreeItem"/> objects across
    /// <see cref="PlaylistModel.Reload"/>: a node that is still there keeps its row. Without this
    /// method, choosing a picture for a folder would leave the row on its plain glyph, and setting the
    /// picture back to Automatic would leave the old picture visible, until a new item was built with
    /// <c>_coverRequested</c> at its default value. This method tells the existing item that its answer
    /// is no longer current.</para>
    /// </summary>
    internal void RefreshCover()
    {
        CoverImage = null;
        _coverRequested = false;
        EnsureCoverLoaded();

        // Then the square of the grid, which is the same choice drawn at another size. It is asked
        // for again only if this row was ever a tile. The grid may be the view on screen, and a square
        // that stayed blank until its container was reused would be the same stale picture that this
        // method exists to stop.
        TileImage = null;
        _tileRequested = false;
        if (_tileEdge > 0)
            EnsureTileLoaded(_tileEdge);
    }

    /// <summary>Called by <see cref="PlaylistModel.LoadFolderCoverAsync"/> when its read and decode have
    /// finished. The read runs off the UI thread, and this call brings the result back to it.</summary>
    internal void SetCoverImage(ImageSource? image) => CoverImage = image;

    // ── the square that the icon grid draws for this row ──
    //
    // This is a second picture, separate from the picture of the row. The picture of the tree is a
    // twelve-point icon cut at TinyEdge, and only a folder has one. The grid draws every kind at
    // ninety-six points: the quilt of the records of a playlist, or the name of a generated list on
    // its palette. So it needs a different size of a different picture, requested separately and
    // latched separately. It is kept on the item, like the picture of the tree, because rows stay
    // after a reload and a container is reused for a different playlist at every scroll.

    private bool _tileRequested;
    private int _tileEdge;
    private ImageSource? _tileImage;

    /// <summary>The picture that the grid shows for this row, after it has been drawn. It is null until
    /// then, and null permanently for a row that has none (Now playing, or a folder with no chosen
    /// picture), so that the glyph stays visible.</summary>
    public ImageSource? TileImage
    {
        get => _tileImage;
        private set
        {
            if (Equals(_tileImage, value)) return;
            _tileImage = value;
            OnChanged();
            OnChanged(nameof(TileImageVisibility));
            OnChanged(nameof(TileGlyphVisibility));
        }
    }

    public Microsoft.UI.Xaml.Visibility TileImageVisibility =>
        _tileImage is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The plain glyph is shown until a picture is loaded, and hidden after that. They are
    /// never shown together, or a tile with a cover would show its glyph under it.</summary>
    public Microsoft.UI.Xaml.Visibility TileGlyphVisibility =>
        _tileImage is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// Starts the tile picture of this row, once. It is called when the repeater creates a tile for this
    /// row (<c>PlaylistView.Tile_ElementPrepared</c>). The grid is virtualised, and this is the one
    /// callback that fires whether the element is new, reused for another row, or given back to this
    /// row.
    /// </summary>
    /// <param name="decodeEdge">The size in pixels at which the square is decoded, worked out by the
    /// surface from its own display scale. The caller must give it and it has no default: the model has
    /// no window to ask, and a guess would give a blurred tile on every high-density display.</param>
    internal void EnsureTileLoaded(int decodeEdge)
    {
        if (_tileRequested)
            return;

        _tileRequested = true;
        _tileEdge = decodeEdge;
        _model.LoadTileCoverAsync(this, decodeEdge);
    }

    /// <summary>Lets a later request try again after a draw that did not reach an answer because the
    /// library was not open when it ran. The distinction is the same as in
    /// <see cref="ResetCoverRequest"/>.</summary>
    internal void ResetTileRequest() => _tileRequested = false;

    /// <summary>Called by <see cref="PlaylistModel.LoadTileCoverAsync"/> when its draw and decode have
    /// finished. The work runs off the UI thread, and this call brings the result back to it.</summary>
    internal void SetTileImage(ImageSource? image) => TileImage = image;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A selected playlist's contents for the detail list: its name, its track rows, and the
/// "N songs · length" header line. <paramref name="IsQueue"/> marks the live Now playing queue, which
/// offers actions that a stored playlist does not.</summary>
/// <param name="PlaylistId">The stored playlist whose contents these rows are, when they are the own
/// songs of one playlist and nothing else. Only in this case can the list be reordered by hand,
/// because only then do the rows map one to one onto pl_item rows. It is null for the queue, for the
/// tracks of a folder and for anything flattened through a nested reference.</param>
/// <param name="SmartId">Which generated list this is, if it is one. It lets the surface at the head
/// of the list draw the right picture. A generated cover is chosen from what the list is (see
/// <c>SmartPlaylistFace.Cover</c>), and a name is not enough to show which mood it stands for,
/// because "Rock" is a row under three different moods.</param>
public sealed record PlaylistDetail(
    string Name, List<PlaylistTrackRow> Tracks, string Subtitle, bool IsQueue = false,
    long? PlaylistId = null, SmartPlaylistId? SmartId = null);

/// <summary>One track row in the playlist detail list, in the style of iTunes: the display fields, the
/// art of its folder, and how to remove it from the list it came from: a pl_item id for a stored
/// playlist, a position for the live queue. Both are null on a row that cannot be removed on its own
/// (the tracks of a folder, or a playlist flattened through a nested reference).</summary>
[RpmBindable]
public sealed partial class PlaylistTrackRow : INotifyPropertyChanged, RPM.Desktop.Presentation.Shell.ICarriedRow
{
    // These members are not 'required'. The Uno [Bindable] generator constructs this type without
    // arguments for its metadata, which required members do not allow. All fields are set through the
    // object initialiser when the row is created.
    public TrackInfo Info { get; internal set; } = null!;
    public string Title { get; internal set; } = "";
    public string Artist { get; internal set; } = "";
    public string Album { get; internal set; } = "";
    public string Year { get; internal set; } = "";
    public string Genre { get; internal set; } = "";
    public string Duration { get; internal set; } = "";
    public string? ArtUri { get; internal set; }
    public long? ItemId { get; internal set; }

    /// <summary>The position of this row in the live player queue, for the Now playing list only. It is
    /// null for the rows of a stored playlist, which use <see cref="ItemId"/> instead.</summary>
    public int? QueueIndex { get; internal set; }

    public string FilePath => Info.FilePath;
    public bool CanRemove => ItemId is not null || QueueIndex is not null;

    /// <summary>
    /// The position of this song in the list where it is shown, counting from one. It is the order of
    /// the list and never the order of the album. A playlist is an arrangement that someone made, and a
    /// column of album track numbers beside it would count something that nobody chose.
    ///
    /// <para><see cref="Show"/> does not copy this value on purpose. That is what makes a drag look
    /// right: a row that takes the face of another song keeps its own number, so the column still reads
    /// one to N down the list while the songs move between the slots.</para>
    /// </summary>
    public int Position { get; internal set; }

    /// <summary>
    /// The song of this row is the one in the player. The three bars are shown where the number
    /// is.
    /// </summary>
    /// <remarks>
    /// <b>The row asks the player, and the player does not tell the row.</b> The property reads the
    /// player every time, so a row that is created halfway through a track is correct on the first
    /// frame, and nothing has to tell it. <see cref="RaisePlaying"/> is only a request to ask again. It
    /// carries no answer, so a request that arrives late or twice cannot leave the mark on the wrong
    /// row.
    ///
    /// <para><b>This is why the mark survives a drag.</b> A row takes the face of another song in place
    /// (<see cref="Show"/>) while the slots stay where they are. A mark stored on the row would stay in
    /// the slot while the song moved out of it. The value is derived from <see cref="FilePath"/>, so it
    /// moves with the face. <c>Show</c> raises the change together with all the other properties.</para>
    ///
    /// <para>The comparison ignores case. <c>PlaybackService.IsPlaying</c> does not, but every other
    /// list in the app matches a playing file in this way.</para>
    /// </remarks>
    public bool IsPlaying
        => Player?.PlayingPath is { Length: > 0 } path
           && FilePath is { Length: > 0 } mine
           && string.Equals(path, mine, StringComparison.OrdinalIgnoreCase);

    /// <summary>The number, which is hidden while the bars are shown. They use the same 26 points, and
    /// a number behind an equaliser would give the position of the song twice.</summary>
    public Microsoft.UI.Xaml.Visibility NumberShown
        => IsPlaying ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>Asks the bindings to read again because the song in the player changed. It carries no
    /// answer. See <see cref="IsPlaying"/>. Call it on the UI thread only, like every other change
    /// notification here.</summary>
    public void RaisePlaying()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NumberShown)));
    }

    /// <summary>The player, or null if there is none to ask: in the design-time tree, and on the thread
    /// pool thread where the rows of a generated list are built before anything is drawn. It returns
    /// null and does not throw, for the reason given in <see cref="Drop"/> about this type being built
    /// off the dispatcher.</summary>
    private static Services.Audio.PlaybackService? Player
    {
        get
        {
            try
            {
                return App.Services.GetRequiredService<Services.Audio.PlaybackService>();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Whether the file says that this song has explicit lyrics. The mark is an E beside the
    /// name. The value is read from the track and is not a separate field, because it is a fact about
    /// the file and the row already holds the file. <see cref="Show"/> raises the change together with
    /// the other properties, because a row that is dragged to a new place takes the whole face of
    /// another song and the mark must move with it.</summary>
    public bool IsExplicit => Info.IsExplicit;

    private Microsoft.UI.Xaml.Media.TranslateTransform? _drop;

    /// <inheritdoc/>
    /// <remarks>
    /// <para><b>It is created on the first read and not in the constructor, because a row is not always
    /// built on the UI thread.</b> A <c>TranslateTransform</c> is a WinUI object and can be created only
    /// on a thread that has a dispatcher. If it were created in an initialiser, building the row on any
    /// other thread would throw <c>COMException 0x8001010E</c> (wrong thread) before any field is set.
    /// <see cref="BuildSmartDetailAsync"/> builds its rows on the thread pool. A throw there would be
    /// answered with null, and the list on screen would stay as it was, although the mood window had
    /// already moved.</para>
    ///
    /// <para>It is read only from the UI thread, which makes this safe: the container template of the
    /// row binds it (<c>PlaylistView.xaml</c>), and the drag writes to it from pointer input. Both run
    /// on the dispatcher, so the transform is created there. <see cref="ArtImage"/> has the same shape
    /// for the same reason.</para>
    /// </remarks>
    public Microsoft.UI.Xaml.Media.TranslateTransform Drop => _drop ??= new();

    /// <summary>
    /// Takes the face of another row, in place. This is how a row is dragged to a new place in the list.
    ///
    /// <para>The rows keep their positions and their containers. Only the content of each row changes.
    /// See <see cref="RPM.Desktop.Presentation.Shell.CarriedReorder{T}"/> for why moving the row objects
    /// instead would make the list blink and make the dragged row disappear at the edges.</para>
    ///
    /// <para>The art is copied with the face and is not fetched again. These are the same tracks, their
    /// pictures are already decoded, and they are only on other lines now.</para>
    /// </summary>
    public void Show(PlaylistTrackRow other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (ReferenceEquals(Info, other.Info))
            return;

        Info = other.Info;
        Title = other.Title;
        Artist = other.Artist;
        Album = other.Album;
        Year = other.Year;
        Genre = other.Genre;
        Duration = other.Duration;
        ArtUri = other.ArtUri;
        ItemId = other.ItemId;
        QueueIndex = other.QueueIndex;
        _art = other._art;
        _hasArt = other._hasArt;

        foreach (var name in new[]
                 {
                     nameof(Info), nameof(Title), nameof(Artist), nameof(Album), nameof(Year),
                     nameof(Genre), nameof(Duration), nameof(ArtUri), nameof(ItemId),
                     nameof(QueueIndex), nameof(FilePath), nameof(CanRemove), nameof(ArtImage),
                     nameof(HasArt), nameof(NoArt), nameof(IsExplicit),
                     // These are derived from FilePath, which has just changed in this slot. Without
                     // them, the bars stay in the slot that the playing song was dragged out of.
                     nameof(IsPlaying), nameof(NumberShown),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private BitmapImage? _art;
    private bool _hasArt;

    /// <summary>
    /// Where the picture of this row is. It is not always in the folder that its path names.
    ///
    /// <para>A record played from the shelf of another person has no file on this disk. The path is a
    /// line in the mirror of that computer, and the folder it names is empty. A row that only looked
    /// there would draw an empty square under a header that shows the same sleeve. These rows ask the
    /// computer that holds the record first, then the cloud (see <see cref="RemoteSleeve"/>).</para>
    ///
    /// <para>A record that is on this disk and has no cover image beside it asks nobody. Its files are
    /// the whole answer. A lookup for each row of a local album with no artwork would make hundreds of
    /// network requests to draw the same empty square.</para>
    /// </summary>
    public static RowSleeve SleeveFor(string? artUri, bool fileOnDisk) =>
        artUri is not null ? RowSleeve.FolderImage
        : fileOnDisk ? RowSleeve.None
        : RowSleeve.Elsewhere;

    /// <summary>The art of the row as a small thumbnail, filled in asynchronously. The bitmap is given
    /// to the binding empty, and its pixels arrive when the worker has them. This avoids decoding the
    /// whole cover.jpg of the folder for each row (a full-size scan on the decoder of the UI
    /// thread).</summary>
    public BitmapImage? ArtImage
    {
        get
        {
            if (_art is not null)
                return _art;
            var image = new BitmapImage();
            _art = image;
            _ = FillArtAsync(image);
            return image;
        }
    }

    private async Task FillArtAsync(BitmapImage image)
    {
        try
        {
            if (await SleeveBytesAsync(ArtScaler.RowEdge) is not { Length: > 0 } thumb)
                return;

            var stream = Presentation.Common.BitmapBytes.Stream(thumb);
            await image.SetSourceAsync(stream);

            // The picture of a borrowed record arrives after the row was drawn, so the square that
            // shows the placeholder glyph must be told that there is a picture to show now.
            _hasArt = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasArt)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NoArt)));
        }
        catch { /* art is decoration, so the row keeps its placeholder */ }
    }

    /// <summary>
    /// The sleeve of this row at the size that the caller draws, or null if nothing can answer.
    /// </summary>
    /// <param name="maxEdge">The longest side wanted. A cover that is already on this disk is read from
    /// <see cref="ThumbCache"/> by path, so a list of forty rows from one album never opens the
    /// scan.</param>
    internal async Task<byte[]?> SleeveBytesAsync(int maxEdge)
    {
        var path = FilePath;
        var uri = ArtUri;

        switch (await Task.Run(() => SleeveFor(uri, File.Exists(path))))
        {
            case RowSleeve.FolderImage:
                return await Task.Run(() =>
                {
                    try { return ThumbCache.GetFile(uri!, maxEdge); }
                    catch { return null; }
                });

            case RowSleeve.Elsewhere:
                var row = await Task.Run(() => TrackHome.RowFor(path));
                if (row is null)
                    return null;
                var bytes = await RemoteSleeve.ForAsync(row, Artist, Album);
                return bytes is { Length: > 0 }
                    ? await Task.Run(() => ThumbCache.Get(bytes, maxEdge))
                    : null;

            default:
                return null;
        }
    }

    public Microsoft.UI.Xaml.Visibility HasArt =>
        ArtUri is null && !_hasArt
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility NoArt =>
        ArtUri is null && !_hasArt
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
}

/// <summary>Which of the three places the picture of a list row comes from. See
/// <see cref="PlaylistTrackRow.SleeveFor"/>.</summary>
public enum RowSleeve
{
    /// <summary>There is nothing to draw and nothing to ask.</summary>
    None,

    /// <summary>The cover image beside the files, on this disk.</summary>
    FolderImage,

    /// <summary>The computer that holds the record, then the cloud.</summary>
    Elsewhere,
}

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
/// Everything that CHANGES a playlist: make one, rename it, move it, drop tracks into it, take them
/// out again.
/// </summary>
public sealed partial class PlaylistModel
{
    /// <summary>
    /// The tree that a change may be written to. It is null while the tree on screen belongs to somebody
    /// else.
    ///
    /// <para>
    /// Every operation in this file goes through this one door. A borrowed shelf brings its owner's
    /// playlists with it (<see cref="IsBorrowed"/>). The panel draws them exactly as it draws the user's
    /// own, with the same rows, drag and menus. A rename or a delete must never reach a mirror kept for
    /// somebody else's computer. It would stand there until that computer next spoke, and then be
    /// overwritten by the truth.
    /// </para>
    ///
    /// <para>This is a value that goes missing, not a check that each operation has to remember to make. A
    /// check can be forgotten. The compiler does not object to a missing <c>if</c>, but it does object to a
    /// null that it was handed. The panel also hides its toolbar and menus. That is a courtesy. This null
    /// is the guard.</para>
    /// </summary>
    private LibraryDatabase? Mine => _library.IsOpen && !IsBorrowed ? _library.Db : null;

    // ── operations ──

    public void NewFolder(long? parentId, string name)
    {
        if (Mine is not { } db || string.IsNullOrWhiteSpace(name)) return;
        db.CreatePlaylistFolder(parentId, name.Trim());
        Reload();
    }

    /// <summary>The library a mood playlist is made in, for the window's reads; null on a borrowed shelf or with no
    /// library open, where nothing can be made.</summary>
    public LibraryDatabase? MoodLibrary => Mine;

    /// <summary>The recipe a playlist the listener made in the mood window was filled from; null for any other row.</summary>
    public MoodRecipe? MoodRecipeOf(PlaylistTreeItem item) =>
        item.IsPlaylist && item.NodeId is { } id && Mine is { } db ? db.MoodRecipeOf(id) : null;

    /// <summary>The listener's own picture under a mood playlist's frame, unframed, where that is its cover; null
    /// otherwise. Read off the UI thread, for the mood window to show and frame again.</summary>
    public async Task<byte[]?> MoodPictureOfAsync(PlaylistTreeItem item)
    {
        if (Mine is null || item.NodeId is not { } id) return null;
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);
        return await Task.Run(() => PlaylistCoverStore.ReadUnder(id, segment));
    }

    /// <summary>Makes a mood playlist at the root, filled from <paramref name="recipe"/>, wearing
    /// <paramref name="cover"/>. Off the UI thread.</summary>
    /// <param name="under">The listener's own picture, unframed, when that is what is under the frame; else null.</param>
    public async Task CreateMoodPlaylistAsync(string name, MoodRecipe recipe, PlaylistArt.Drawn cover, byte[]? under)
    {
        if (Mine is not { } db || string.IsNullOrWhiteSpace(name)) return;
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);
        await Task.Run(() =>
        {
            var id = db.CreateMoodPlaylist(name.Trim(), recipe);
            WriteMoodCover(db, id, segment, cover, under);
        });
        Reload();
        PlaylistCovers.Forget();
    }

    /// <summary>Renames a mood playlist, fills it again from <paramref name="recipe"/> in place of its songs, and puts
    /// <paramref name="cover"/> on it.</summary>
    public async Task RefillMoodPlaylistAsync(
        PlaylistTreeItem item, string name, MoodRecipe recipe, PlaylistArt.Drawn cover, byte[]? under)
    {
        if (Mine is not { } db || item.NodeId is not { } id || string.IsNullOrWhiteSpace(name)) return;
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);
        await Task.Run(() =>
        {
            db.RefillMoodPlaylist(id, name.Trim(), recipe);
            WriteMoodCover(db, id, segment, cover, under);
        });
        Reload();
        item.RefreshCover();
        PlaylistCovers.Forget();
        DetailInvalidated?.Invoke();
    }

    /// <summary>
    /// A mood playlist's cover, written the way the cover window's choice is (<see cref="SetCoverAsync"/>): the
    /// provenance on the node and the master in the store. The provenance is <see cref="PlaylistCover.Picture"/>:
    /// the master is the framed picture as drawn, nothing is to be composed again from records by the cover sweep,
    /// and what was chosen lives in the recipe (<see cref="MoodCover"/>), which only the mood window reads.
    /// </summary>
    private static void WriteMoodCover(LibraryDatabase db, long id, string? segment, PlaylistArt.Drawn cover, byte[]? under)
    {
        if (cover.Cover is not { Length: > 0 } framed)
            return;   // nothing could be drawn: the list keeps the automatic cover it was made with
        db.SetPlaylistCover(id, new PlaylistCover.Picture());
        PlaylistCoverStore.Write(id, segment, framed, cover.Wash);
        if (under is { Length: > 0 })
            PlaylistCoverStore.WriteUnder(id, segment, under);
    }

    public void NewPlaylist(long? parentId, string name)
    {
        if (Mine is not { } db || string.IsNullOrWhiteSpace(name)) return;
        db.CreatePlaylist(parentId, name.Trim());
        Reload();
    }

    /// <summary>
    /// Writes a playlist's new track order after a row has been dragged to a new place.
    ///
    /// <para>There is no <see cref="Reload"/>. The list on screen is already in this order, because that is
    /// where the order came from. Rebuilding it would throw away every row, only to draw the same rows
    /// again, and each piece of folder art would go out and come back in through its fetch. The tree shows
    /// a playlist's NAME, and reordering the songs inside it does not change the name.</para>
    /// </summary>
    public void ReorderDetail(long playlistId, IReadOnlyList<long> orderedItemIds)
    {
        if (Mine is not { } db || orderedItemIds.Count == 0) return;
        db.ReorderPlaylistItems(playlistId, orderedItemIds);
    }

    /// <summary>
    /// Files <paramref name="carried"/> where a drag has put it: beside <paramref name="target"/> under the
    /// same parent, or inside it when the target is a folder that was dropped into.
    ///
    /// <para>Both writes happen, in this order, because they answer different questions.
    /// <c>MovePlaylistNode</c> says which folder the row is in, and puts the row at the end of that folder.
    /// Then the reorder says where among its new neighbours the row goes. Reordering first would number a
    /// list that the row is not yet in.</para>
    /// </summary>
    /// <returns>Whether anything was written. It is false for a drop that the tree cannot make, so the
    /// panel can put the row back instead of reloading a tree that has not changed.</returns>
    public bool DropNode(PlaylistTreeItem carried, PlaylistTreeItem target, TreeDropWhere where)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(target);

        if (Mine is not { } db || carried.NodeId is not { } id || !carried.CanCarry)
            return false;

        // A folder cannot be filed inside itself, or inside anything it holds. That would make a branch
        // with no way back to the root, and every playlist on it would vanish from the pane.
        if (carried.Holds(target))
            return false;

        var parent = where == TreeDropWhere.Into ? target : target.Parent;
        if (parent is not null && carried.Holds(parent))
            return false;

        var siblings = Siblings(parent);

        // Dropped into a folder that it is already the last child of, or beside itself, nothing was asked
        // for. This is checked here rather than left to the writes. A reload rebuilds the tree, which loses
        // the scroll position, for a gesture that changed nothing.
        if (ReferenceEquals(carried, target))
            return false;

        var slot = where == TreeDropWhere.Into
            ? siblings.Count(s => !ReferenceEquals(s, carried))
            : TreeDrop.Slot(siblings.IndexOf(target), where, siblings.IndexOf(carried));

        var order = siblings.Where(s => !ReferenceEquals(s, carried)).ToList();
        order.Insert(Math.Clamp(slot, 0, order.Count), carried);

        var parentId = parent?.NodeId;
        if (!ReferenceEquals(carried.Parent, parent))
            db.MovePlaylistNode(id, parentId);

        db.ReorderPlaylistNodes(parentId, order.Select(s => s.NodeId).OfType<long>().ToList());

        // A folder is opened when something has just been put in it. Otherwise the row would vanish into a
        // shut folder, and that looks like a drop that went wrong.
        if (where == TreeDropWhere.Into)
            target.IsExpanded = true;

        Reload();

        return true;
    }

    /// <summary>The rows under a parent, in the order the tree holds them. These are the root rows when the
    /// parent is null.</summary>
    private List<PlaylistTreeItem> Siblings(PlaylistTreeItem? parent)
        => parent is null ? _roots.ToList() : parent.Children.ToList();

    /// <summary>
    /// Puts a playlist in the cloud: every track it plays is uploaded, and the order is stored beside them.
    /// This is fire-and-forget from the menu, so a failure is caught in <see cref="RunCloudAsync"/> rather
    /// than lost. The tree re-reads itself once the cloud service has written the selection.
    /// </summary>
    public void AddToCloud(PlaylistTreeItem item)
    {
        if (Mine is null || item.NodeId is not { } id) return;
        _ = RunCloudAsync(cloud => cloud.AddPlaylistToCloudAsync(id), "add", item.Name);
    }

    /// <summary>Takes a playlist out of the cloud, and with it the tracks that were only in the cloud for
    /// its sake.</summary>
    public void RemoveFromCloud(PlaylistTreeItem item)
    {
        if (Mine is null || item.NodeId is not { } id) return;
        _ = RunCloudAsync(cloud => cloud.RemovePlaylistFromCloudAsync(id), "remove", item.Name);
    }

    private async Task RunCloudAsync(Func<CloudSyncService, Task> work, string what, string name)
    {
        try
        {
            await work(App.Services.GetRequiredService<CloudSyncService>());
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<PlaylistModel>>()
                .LogError(ex, "[Playlists] Could not {What} the playlist {Name} in the cloud", what, name);
        }
    }

    public void Rename(PlaylistTreeItem item, string name)
    {
        if (Mine is not { } db || item.NodeId is not { } id || string.IsNullOrWhiteSpace(name)) return;
        db.RenamePlaylistNode(id, name.Trim());
        Reload();
    }

    /// <summary>
    /// Gathers what the cover picker needs about one row, before it opens: the choice as stored, and, only
    /// for a <see cref="PlaylistCover.Picture"/> choice, the master's own bytes.
    ///
    /// <para>This is read here, not by the dialog. The dialog reads other images itself, such as record
    /// sleeves and a freshly picked file, both for its live previews. But the stored custom picture can
    /// only be read by the caller, so it is fetched here. It is fetched for a Picture choice only. That is
    /// the one kind whose picture IS the master, with nothing to recompose it from. A Records or Figure
    /// choice rebuilds its own preview from what the dialog already gathers. Reading a master that such a
    /// kind does not need would be a disk read for nothing.</para>
    ///
    /// <para>Both the node lookup and the master read run off the UI thread. A menu click on the dispatcher
    /// would otherwise run a SQLite query and, for a Picture choice, a real file read.</para>
    ///
    /// <para>This is guarded the same way that <see cref="Services.Library.PlaylistCoverSweepService.Run"/>
    /// guards its own captured database. The click and this background read are two different moments. The
    /// library can close, disposing <c>db</c>, in the gap between them. A disposed connection throws rather
    /// than answering. This runs from an <c>async void</c> menu handler, so an unhandled throw here would
    /// crash the app, not return an error. The exception is caught and read as "nothing to ask about any
    /// more". That is the same answer that a deleted node already gives. The caller does not need to tell
    /// the two apart.</para>
    /// </summary>
    public Task<(PlaylistCover Current, byte[]? CurrentPicture)?> AskCoverAsync(PlaylistTreeItem item)
    {
        if (Mine is not { } db || item.NodeId is not { } id)
            return Task.FromResult<(PlaylistCover, byte[]?)?>(null);

        // "own" only, for the master. Mine has already refused a borrowed shelf, so the master this row
        // could show lives under this library's own segment, and nowhere else.
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);

        return Task.Run<(PlaylistCover, byte[]?)?>(() =>
        {
            // An unanswered read opens nothing, because ChangeCoverAsync treats null as "open nothing". The
            // captured mirror may have been closed between the click and this read. Or it refused, and the
            // problem screen is now up over the app (see LibraryService.Read).
            if (!_library.Read(db, "a playlist's cover choice", mirror => mirror.GetPlaylistNodes())
                        .Answers(out var nodes, out _)
                || nodes.FirstOrDefault(n => n.Id == id) is not { } node)
                return null;

            var picture = node.Cover is PlaylistCover.Picture
                ? PlaylistCoverStore.Read(id, segment).Cover
                : null;

            return (node.Cover, picture);
        });
    }

    /// <summary>
    /// Sets a playlist's picture: the note in the tree, and the master on the disk. The automatic cover
    /// writes no master, and it removes any existing one. That is what makes it a choice rather than an
    /// absence.
    ///
    /// <para>Nothing after the write runs unless the write actually happened. The returned <c>wrote</c>
    /// flag is what guards this. This runs from an <c>async void</c> menu handler, so a database that the
    /// library disposed while closing must not surface as a crash. But swallowing the error must not leave
    /// the row, the shared cache and the other listeners acting as if the save had landed. In that exact
    /// race the user sees nothing: no error and no toast. The row keeps the picture it already had. This is
    /// the same silence that every other write in this method gives when the library is closed.</para>
    /// </summary>
    public async Task SetCoverAsync(PlaylistTreeItem item, PlaylistCover cover, PlaylistArt.Drawn rendered)
    {
        if (Mine is not { } db || item.NodeId is not { } id)
            return;

        // "own tree only", the same rule that the doc of SegmentOf states. Mine above has already refused
        // to get here while a borrowed shelf is open, so this machine only writes a master for its own
        // tree. The segment is captured here, synchronously, before the background hop below.
        // PlaylistCoverSweepService uses the same capture-before-Task.Run pattern, for the same reason.
        // _library could move on to a different tree before a queued background write runs. Reading
        // _library.Root late would then target whichever tree is open by then, not the one this click was
        // about.
        var segment = PlaylistCoverStore.SegmentOf(PlaylistShelf.Own, _library.Root, null);

        // Off the UI thread: a SQLite write plus up to two JPEGs, both written through PlaylistCoverStore's
        // own write-then-rename steps. Running this inline on the dispatcher, right after Save, would add
        // to the cost of the dialog's own composite. It is a bigger disk hit than the master read that
        // AskCoverAsync already moved off the UI thread.
        var wrote = await Task.Run(() =>
        {
            try
            {
                db.SetPlaylistCover(id, cover);

                // One reading of an empty Rendered, not two. Whether Chosen is Automatic (which renders
                // nothing on purpose) or a Records or Figure composite that could not be made, the master
                // is removed either way. Drawn.None means nothing to write, and nothing else. A second
                // branch that treated the automatic case differently would give the same value a second
                // meaning.
                if (rendered.Cover is not { Length: > 0 })
                    PlaylistCoverStore.Forget(id, segment);
                else
                    PlaylistCoverStore.Write(id, segment, rendered.Cover, rendered.Wash);

                return true;
            }
            catch
            {
                // The captured database was disposed. The library closed between Save and this write
                // running on the pool. This is the same race that PlaylistCoverSweepService.Run and
                // DropStaleRecordCovers both guard against. Nothing was written, so nothing after this
                // point may run either. See the note on "wrote" above this method.
                return false;
            }
        });

        if (!wrote)
            return;

        // The row's own picture is stale as soon as the master changes. PlaylistModel reuses
        // PlaylistTreeItem objects across Reload ("a node that is still here keeps its row"). Without this
        // call, the row would keep its old picture, either the plain glyph or the picture that was just
        // replaced, until something else recycled its container. See the note on RefreshCover.
        item.RefreshCover();

        // Everything drawn so far is dropped. Working out which surfaces could show this picture costs more
        // than redrawing the few that are on screen.
        PlaylistCovers.Forget();
        DetailInvalidated?.Invoke();
    }

    public void Delete(PlaylistTreeItem item)
    {
        if (Mine is not { } db) return;
        if (item.NodeId is { } id)
            db.DeletePlaylistNode(id);
        else if (item.ItemId is { } itemId)
            db.RemovePlaylistItem(itemId);
        Reload();
    }

    /// <summary>Appends tracks to a playlist (the "Add to playlist" target). Each track is keyed by its rpm
    /// id, with its library-relative path as the fallback. It takes the tracks as they come off the grid
    /// rows.</summary>
    /// <param name="whenAlreadyThere">What a track that the playlist already holds gets. See
    /// <see cref="AlreadyIn(long, IEnumerable{TrackInfo})"/> for the count that the question is asked
    /// with.</param>
    public void AddTracksToPlaylist(long playlistId, IEnumerable<TrackInfo> tracks, AlreadyInPlaylist whenAlreadyThere)
    {
        if (Mine is not { } db || _library.Root is not { } root) return;
        db.AddSongsToPlaylist(playlistId,
            tracks.Select(t => ((string?)t.RpmId, RelativeTo(root, t.FilePath))), whenAlreadyThere);
        Reload();
    }

    /// <summary>How many of these grid tracks the playlist already holds. It is 0 on a borrowed shelf, or
    /// when no library is open. <see cref="AddTracksToPlaylist"/> writes nothing in those cases
    /// either.</summary>
    public int AlreadyIn(long playlistId, IEnumerable<TrackInfo> tracks) =>
        Mine is { } db && _library.Root is { } root
            ? db.CountAlreadyInPlaylist(playlistId,
                tracks.Select(t => ((string?)t.RpmId, RelativeTo(root, t.FilePath))))
            : 0;

    /// <summary>How many of these library rows the playlist already holds. It is 0 on a borrowed shelf,
    /// where <see cref="AddLibraryTracks"/> writes nothing either.</summary>
    public int AlreadyIn(long playlistId, IEnumerable<LibraryTrack> tracks) =>
        Mine is { } db
            ? db.CountAlreadyInPlaylist(playlistId, tracks.Select(t => ((string?)t.RpmId, t.RelativePath)))
            : 0;

    /// <summary>
    /// Writes what is on screen into a playlist of the listener's own. This is how a generated list is
    /// kept.
    ///
    /// <para>A generated list is only its query. It holds no rows, it is read again every time it is
    /// opened, and the mood lists deliberately give a different fifty each time. So a good one is lost as
    /// soon as it is closed, unless the songs are written down. This method writes them. It writes the
    /// songs as they are shown. They are taken from the rows in hand, not found by asking the question
    /// again, because asking again would save a list that nobody had seen.</para>
    ///
    /// <para>The playlist is created at the root, not beside anything. No folder is selected while a
    /// tracklist is open. The pinned folder that the generated lists live in has no stored node to file the
    /// new playlist under.</para>
    /// </summary>
    /// <returns>The new playlist's node id, or null where nothing was written: a borrowed shelf, a
    /// closed library, an empty list, or a name that is only spaces.</returns>
    public long? SaveAsPlaylist(string name, IEnumerable<TrackInfo> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (Mine is not { } db || _library.Root is not { } root || string.IsNullOrWhiteSpace(name))
            return null;

        var songs = tracks.ToList();
        if (songs.Count == 0)
            return null;

        var id = db.CreatePlaylist(null, name.Trim());
        // AddAnother is used because the playlist was just created and holds nothing. What is written is
        // the list as shown.
        db.AddSongsToPlaylist(id, songs.Select(t => ((string?)t.RpmId, RelativeTo(root, t.FilePath))),
            AlreadyInPlaylist.AddAnother);

        // One reload for both writes. Reloading after the create as well would rebuild the whole tree just
        // to draw an empty playlist, which is about to have fifty songs in it.
        Reload();
        return id;
    }

    private static string RelativeTo(string root, string fullPath) =>
        System.IO.Path.GetRelativePath(root, fullPath).Replace(System.IO.Path.DirectorySeparatorChar, '/');

    /// <summary>Appends library rows to a playlist, keyed by rpm id with the stored relative path as the
    /// fallback. This is the "add this album" path, where the rows already carry both.</summary>
    /// <param name="whenAlreadyThere">What a track the playlist already holds gets. See
    /// <see cref="AlreadyIn(long, IEnumerable{LibraryTrack})"/> for the count the question is asked
    /// with.</param>
    public void AddLibraryTracks(long playlistId, IEnumerable<LibraryTrack> tracks, AlreadyInPlaylist whenAlreadyThere)
    {
        if (Mine is not { } db) return;
        db.AddSongsToPlaylist(playlistId, tracks.Select(t => ((string?)t.RpmId, t.RelativePath)), whenAlreadyThere);
        Reload();
    }

    public void Play(PlaylistTreeItem item) => Send(item, play: true);
    public void AddToNowPlaying(PlaylistTreeItem item) => Send(item, play: false);

    private void Send(PlaylistTreeItem item, bool play)
    {
        var tracks = ResolveTracks(item);
        if (tracks.Count == 0)
            return;

        _amp.AddTracks(tracks, play);

        // Only a row that started playing claims the queue. "Add to now playing" leaves whatever is
        // playing alone, so it has no business saying what the queue is from.
        if (play && item.Kind == PlaylistRowKind.Smart)
            _amp.NoteGenerated(item.SmartId);

        // Only a row that started playing counts as a play. It is written here, because this is the one
        // door that every way of starting a list goes through. The tree's own play, its menu and the detail
        // view's button all reach Send. See LibraryDatabase.PlaylistPlays for why the count exists at all.
        // Nothing recorded plays before, so "the lists you play most" had no answer.
        if (play)
            NoteListPlayed(item);
    }

    /// <summary>
    /// Counts one play of a list, kept or generated.
    ///
    /// <para>This is done quietly, after the tracks have gone to the player. A count is housekeeping, and a
    /// list must never fail to play because the mirror could not be written. A row that is neither a
    /// playlist nor a generated list (a folder, a song, or the now playing row) is not a list, and it is
    /// not counted.</para>
    /// </summary>
    private void NoteListPlayed(PlaylistTreeItem item)
    {
        if (!_library.IsOpen)
            return;

        var (kind, key) = item.Kind switch
        {
            PlaylistRowKind.Smart
                => (PlaylistPlayKind.Generated, LibraryDatabase.PlaylistKey(item.SmartId)),
            PlaylistRowKind.Playlist when item.NodeId is { } node
                => (PlaylistPlayKind.Kept,
                    node.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => (PlaylistPlayKind.Kept, ""),
        };

        if (key.Length == 0)
            return;

        try
        {
            _library.Shelf.NotePlaylistPlayed(kind, key, DateTime.UtcNow.Ticks);
        }
        catch
        {
            // A count that cannot be written is not a reason for a list not to play.
        }
    }

    /// <summary>The tracks a row stands for. A playlist or a folder is flattened to its play order. A song
    /// row is the one song it is.</summary>
    public IReadOnlyList<TrackInfo> ResolveTracks(PlaylistTreeItem item)
    {
        // The shelf's root, not the library's. A borrowed row's path is relative to the mirror kept for
        // that computer. Resolved against this collection, it would name a file of the user's own that sits
        // at the same place.
        if (!_library.IsOpen || _library.ShelfRoot is not { } root)
            return Array.Empty<TrackInfo>();
        var db = _library.Shelf;

        // A generated list is its query, and it is the same query that the detail view uses. So playing the
        // row and reading it can never show different songs. The query is asked of the shelf, because on a
        // borrowed shelf the only list offered is the reader's own hearts among THAT collection's songs.
        //
        // The list plays as it STANDS. Playing does not advance the list's window, so the songs that play
        // are the songs that were on screen. Only the button changes a list.
        if (item.Kind == PlaylistRowKind.Smart)
            return db.QuerySmartPlaylist(item.SmartId, DateTime.UtcNow.Ticks, advance: false)
                     .Select(t => t.ToTrackInfo(root))
                     .ToList();

        // A playlist, or a reference to a nested playlist, is flattened through the store. A folder gathers
        // every playlist beneath it, in order.
        var playlistId = item.Kind switch
        {
            PlaylistRowKind.Playlist => item.NodeId,
            PlaylistRowKind.Ref => item.RefPlaylistId,
            _ => null,
        };
        if (playlistId is { } id)
            return db.FlattenPlaylistTracks(id).Select(t => t.ToTrackInfo(root)).ToList();

        if (item.Kind == PlaylistRowKind.Folder && item.NodeId is { } folderId)
        {
            var tracks = new List<TrackInfo>();
            foreach (var pl in DescendantPlaylists(folderId))
                tracks.AddRange(db.FlattenPlaylistTracks(pl).Select(t => t.ToTrackInfo(root)));
            return tracks;
        }

        return Array.Empty<TrackInfo>();
    }
}

using RPM.Core.Cloud;
using RPM.Core.Files;
using RPM.Core.Library;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// Deleting music from the library: one song, a selection of them, or a whole record. The user also
/// chooses which of its copies is deleted (the local file, the cloud copy, or both).
///
/// <para>Every surface uses this one path with a <see cref="DeleteReach"/> in hand, and there is no
/// overload without one. That is the point of the type. A delete that removed the local files but kept
/// the cloud copy, and also dropped the row, would let the record come back on the next library scan.
/// The user would see that as the app losing music.</para>
///
/// <para>The rule for what survives is <see cref="DeletePlan"/>'s, not this file's. A row leaves the
/// library when the last copy of it goes, and never otherwise.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Fire-and-forget wrapper: a click hands the work off and nothing awaits it, so anything
    /// that escapes is logged rather than lost.</summary>
    private void DeleteTracks(DeleteReach reach, IReadOnlyList<string> filePaths) =>
        _ = RunDeleteTracksAsync(reach, filePaths);

    private async Task RunDeleteTracksAsync(DeleteReach reach, IReadOnlyList<string> filePaths)
    {
        try
        {
            await DeleteTracksAsync(reach, filePaths);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainPage>>()
                .LogError(ex, "[Delete] {Reach} failed on {Count} track(s)", reach, filePaths.Count);
        }
    }

    /// <summary>Songs, by the paths the surfaces hand around.</summary>
    private async Task DeleteTracksAsync(DeleteReach reach, IReadOnlyList<string> filePaths)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (filePaths.Count == 0 || !library.IsOpen || library.Root is not { } root)
            return;
        var db = library.Db;

        // Paths in, mirror rows out. A file the mirror does not hold (previewed from outside the
        // library, or already gone) is dropped here rather than half-deleted below.
        var tracks = new List<LibraryTrack>();
        foreach (var path in filePaths)
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
                continue;
            if (db.FindByRelativePath(relative) is { } row)
                tracks.Add(row);
        }
        if (tracks.Count == 0)
            return;

        await DeleteRowsAsync(
            reach, tracks,
            tracks.Count == 1 ? DeleteSubject.Track : DeleteSubject.Tracks,
            TrackDisplayName(tracks[0]));
    }

    /// <summary>A whole record, down the same path its songs go down. The folder itself is dealt with
    /// at the end, once it is known whether anything is left in it.</summary>
    private async Task DeleteAlbumAsync(string folder, DeleteReach reach)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return;
        var db = library.Db;
        var tracks = db.FolderTracks(folder);
        if (tracks.Count == 0)
            return;

        await DeleteRowsAsync(reach, tracks, DeleteSubject.Album, AlbumDisplayName(db, folder));
    }

    /// <summary>
    /// The one delete. Asks, then does the three things in the order that cannot lose music: the cloud
    /// copy first (it is the one that can fail on a network), then the bookkeeping for every file that
    /// is about to be trashed, then the trash itself.
    /// </summary>
    /// <param name="name">What to call it in the dialog. Ignored for a selection, which is counted.</param>
    private async Task DeleteRowsAsync(
        DeleteReach reach, IReadOnlyList<LibraryTrack> tracks, DeleteSubject subject, string name)
    {
        var library = App.Services.GetRequiredService<LibraryService>();

        // Never on a borrowed shelf: those files are on another computer, and the rows belong to that
        // computer too. The menus already leave the entry out there. This check is a second guard.
        if (library.IsPeerView || !library.IsOpen || library.Root is not { } root || XamlRoot is null)
            return;
        var db = library.Db;

        // What this reach does, row by row. A row that the reach does nothing to is left out of the set.
        // It must not be counted in the dialog, and it must not change the wording.
        var plan = tracks
            .Select(t => (Track: t, Outcome: DeletePlan.For(reach, t.CloudState)))
            .Where(p => p.Outcome != DeleteOutcome.Nothing)
            .ToList();
        if (plan.Count == 0)
            return;

        var wording = DeletePrompt.For(reach, subject, plan.Select(p => p.Track.CloudState));
        var action = Loc.Get(wording.ActionKey);

        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            action,
            // One thing is named; a selection is counted. Naming the first of nine would be the dialog
            // saying it will delete one thing and then deleting nine.
            subject == DeleteSubject.Tracks
                ? Loc.Format(wording.BodyKey, plan.Count)
                : Loc.Format(wording.BodyKey, name),
            // One button, with the same words as the menu entry. A second button for "delete everywhere"
            // would let the user choose the reach halfway through the action, not before it.
            primaryText: action,
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Close);
        if (answer != ContentDialogResult.Primary)
            return;

        // ── the cloud copy ────────────────────────────────────────────────────────────────────────
        // First, because it is the only step that talks to a network, and so the only one that can fail
        // for reasons this code cannot see. A row whose cloud copy would not delete is dropped from the
        // rest of the work below. If its file were trashed anyway, the object would stay in the cloud and
        // come back as a cloud album on the next library scan. This order prevents that.
        if (reach is DeleteReach.Cloud or DeleteReach.Everywhere)
        {
            var fromCloud = plan
                .Select(p => p.Track)
                .Where(t => CloudRemovalPlan.IsInTheCloudsHands(t.CloudState))
                .ToList();
            if (fromCloud.Count > 0)
            {
                await App.Services.GetRequiredService<CloudSyncService>().DeleteFromCloudAsync(fromCloud);

                // Re-read rather than assume: DeleteFromCloudAsync clears a row's cloud state only on
                // the ones that actually went, and reports the rest on the cloud status line.
                var stillUp = db.QueryTracksByIds(fromCloud.Select(t => t.Id).ToList())
                    .Where(t => CloudRemovalPlan.IsInTheCloudsHands(t.CloudState))
                    .Select(t => t.Id)
                    .ToHashSet();
                if (stillUp.Count > 0)
                    plan = plan.Where(p => !stillUp.Contains(p.Track.Id)).ToList();
            }
        }

        // ── the files on this disk ────────────────────────────────────────────────────────────────
        // Mark first, then delete, and check that the mark took. A row that keeps its cloud copy is set to
        // CloudOnly BEFORE its file moves. If the mark does not take (the row is not really in the cloud,
        // whatever the menu showed), the file stays where it is. Otherwise the row would point at a file
        // that no longer exists.
        var trashing = new List<LibraryTrack>();
        foreach (var (track, outcome) in plan)
        {
            if (outcome == DeleteOutcome.TrashKeepingCloud)
            {
                // A mark that does not take means the row is not really in the cloud. The file stays where
                // it is. Demoting a row while its music is still the only copy would lose the album.
                if (db.MarkOffloaded(track.Id, track.RpmId))
                    trashing.Add(track);
                else
                    App.Services.GetRequiredService<ILogger<MainPage>>().LogWarning(
                        "[Delete] {Path} was not marked offloaded; its file was left alone",
                        track.RelativePath);
            }
            else if (outcome == DeleteOutcome.RemoveEntirely && DeletePlan.HasLocalFile(track.CloudState))
            {
                trashing.Add(track);
            }
        }

        // Which records go to the trash whole. The rule is DeletePlan.GoesAsOneRecord. This code gives that
        // rule its input: every row the MIRROR holds under that folder, not only the rows in hand. A
        // selection of nine songs out of twelve leaves three behind. Trashing the folder would take those
        // three with it.
        var byId = plan.ToDictionary(p => p.Track.Id, p => p.Outcome);
        var wholeRecords = plan
            .Select(p => p.Track.Folder)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => DeletePlan.GoesAsOneRecord(
                db.FolderTracks(f).Select(t =>
                    byId.TryGetValue(t.Id, out var outcome) ? outcome : DeleteOutcome.Nothing)))
            .ToList();

        // One move at a time, off the UI thread. A file that refuses to move keeps its row. A mirror that
        // has lost track of a file still on the disk is worse than a delete that did not happen. The next
        // scan would add the file back as a new track with none of its history.
        var moved = new HashSet<long>();
        var left = new List<LibraryTrack>();
        var handledWithRecord = new HashSet<long>();
        if (trashing.Count > 0 || wholeRecords.Count > 0)
            await Task.Run(() =>
            {
                foreach (var folder in wholeRecords)
                {
                    var full = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                    var songs = trashing
                        .Where(t => folder.Equals(t.Folder, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // A trash that reports false having moved the folder anyway is a move.
                    if (!Directory.Exists(full) || OsTrash.Trash(full) || !Directory.Exists(full))
                    {
                        foreach (var track in songs)
                            moved.Add(track.Id);
                    }
                    else
                    {
                        left.AddRange(songs);
                    }

                    foreach (var track in songs)
                        handledWithRecord.Add(track.Id);
                }

                foreach (var track in trashing)
                {
                    // Already handled as part of its record: it moved with the folder, or stayed with it. A
                    // folder that refused to move is not taken apart file by file. Doing that would leave a
                    // half-emptied record with no rows.
                    if (handledWithRecord.Contains(track.Id))
                        continue;

                    var full = Path.Combine(
                        root, track.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    // A Trash that reports false having moved the file anyway is a move.
                    if (!File.Exists(full) || OsTrash.Trash(full) || !File.Exists(full))
                        moved.Add(track.Id);
                    else
                        left.Add(track);
                }
            });

        // The other half of marking first. A row that was set to CloudOnly and then would not move is set
        // back to Synced. If it were left as it is, the mirror would say the file is not on this disk,
        // while the file is still there. The record would show the cloud mark over music it still stores.
        // Only demoted rows can be here with a cloud copy. A row that was removed entirely was Local,
        // queued or in flight. None of those was marked before its file moved.
        foreach (var track in left.Where(t => DeletePlan.HasCloudCopy(t.CloudState)))
            db.SetCloudState(track.Id, CloudState.Synced);

        // ── the rows ──────────────────────────────────────────────────────────────────────────────
        // The only outcome that removes a row from the library. A row is removed only when its file
        // actually went.
        var gone = plan
            .Where(p => p.Outcome == DeleteOutcome.RemoveEntirely
                        && (!DeletePlan.HasLocalFile(p.Track.CloudState) || moved.Contains(p.Track.Id)))
            .Select(p => p.Track)
            .ToList();
        if (gone.Count > 0)
            db.RemoveMany(gone.Select(t => t.RelativePath).ToList());

        // A record whose last song has just gone is no longer a record. Its album facts row is deleted.
        // The rest of the folder, such as a cover, a log or sleeve notes, goes to the trash too, because
        // the listener did not ask to keep a folder of leftovers. A record that only lost its local files
        // still has its rows, so its folder is not touched here.
        var folders = plan
            .Select(p => p.Track.Folder)
            .Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var emptied = new List<string>();
        foreach (var folder in folders)
        {
            if (db.FolderTracks(folder).Count > 0)
                continue;
            db.DeleteAlbumMeta(folder);
            emptied.Add(folder);
            var fullFolder = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(fullFolder))
                await Task.Run(() => OsTrash.Trash(fullFolder));
        }

        library.NotifyChanged();

        // Every cloud mark on screen is read again before anything else is shown. A record that has just
        // given up its local files is now in the cloud only. The cloud mark that shows this must appear
        // on the click, not at the next restart.
        RefreshCloudMarks();

        // A card on the wall is built once per folder and reused by each rebuild that still lists it (see
        // AlbumWallView.RefreshAlbum). So a record that lost a track, or lost its files but kept its rows,
        // would keep showing the old tracklist. Records that went entirely are removed from the rebuild, so
        // nothing needs to be said about them.
        foreach (var folder in folders.Where(f => !emptied.Contains(f, StringComparer.OrdinalIgnoreCase)))
        {
            AlbumWall.RefreshAlbum(folder);
            AlbumList.RefreshAlbum(folder);
        }

        // The album page, if it shows a record that was changed. If that record is now empty, there is no
        // page left to show, so the view goes back to the one it was opened over.
        if (AlbumDetail.Visibility == Visibility.Visible
            && AlbumDetail.Folder is { Length: > 0 } shown
            && folders.Contains(shown, StringComparer.OrdinalIgnoreCase))
        {
            if (emptied.Contains(shown, StringComparer.OrdinalIgnoreCase))
                ShowAlbumDetail(false);
            else
                // The same record, one song shorter. It is loaded again rather than opened fresh,
                // because its album details have not changed.
                AlbumDetail.Load(shown, Library.AlbumOpen.Again);
        }

        if (left.Count > 0)
            await PromptDialog.ShowAsync(
                XamlRoot, action,
                left.Count == 1 && trashing.Count == 1
                    ? Loc.Get("TrackDelete_Failed")
                    : Loc.Format("TrackDelete_FailedSome", left.Count),
                closeText: Loc.Get("Common_OK"));
    }

    /// <summary>What to call the song in the dialog: what it says it is, or its file name when it has
    /// never been named. Never an empty pair of quotes.</summary>
    private static string TrackDisplayName(LibraryTrack track) =>
        string.IsNullOrWhiteSpace(track.Title) ? track.FileName : track.Title!;

    /// <summary>What to call the record: what its tracks say the album is, or the folder it sits
    /// in.</summary>
    private static string AlbumDisplayName(LibraryDatabase db, string folder)
    {
        var first = db.FolderTracks(folder).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first?.Album))
            return first!.Album!;
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : folder;
    }
}

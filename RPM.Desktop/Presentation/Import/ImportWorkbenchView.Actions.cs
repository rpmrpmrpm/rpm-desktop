using System.IO;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// What the workbench does: the right-click menu, the toolbar over the tree, and the actions behind both.
///
/// <para>The queue has no selection of its own. Its <b>ticks</b> are the selection. Only when nothing is
/// ticked does an action use the row that was right-clicked or focused. That rule is in
/// <see cref="QueueActionRows"/>, and every handler here uses it.</para>
/// </summary>
public sealed partial class ImportWorkbenchView
{
    // ── queue tree context menu: recognize / split album / edit / treatments / remove ──

    private TrackRow? _contextQueueRow;

    /// <summary>
    /// The GROUP that was right-clicked, a record or an artist, if there was one. Every action on this menu
    /// except the split acts on the whole group, over every row under it. The queue tree is how a collection
    /// is viewed, and "recognize this record" is the usual question. Ticking forty tracks first would be a
    /// poor answer.
    /// </summary>
    private ImportNode? _contextQueueGroup;

    /// <summary>Fills the row menu for whichever row it was opened on.</summary>
    private void PrepareQueueContextMenu(ImportNode? node)
    {
        _contextQueueRow = node?.Row;
        _contextQueueGroup = node is { Row: null, GroupArtist: { } } ? node : null;
        bool isFile = _contextQueueRow is not null;
        bool isGroup = _contextQueueGroup is not null;

        // How much this menu covers, and how much of it is not on the disk. A group is COUNTED, not fetched.
        // That takes two indexed reads, while loading every row under an artist to open a menu would be slow.
        // A file's answer is the ticked set. It is read once and used twice.
        int rowCount, dataless;
        if (_contextQueueGroup is { GroupArtist: { } groupArtist } group)
        {
            rowCount = Model?.QueueGroupCount(groupArtist, group.GroupAlbum) ?? 0;
            dataless = Model?.QueueDatalessCount(groupArtist, group.GroupAlbum) ?? 0;
        }
        else
        {
            var rows = QueueActionRows();
            rowCount = rows.Count;
            dataless = rows.Count(r => r.Availability is RPM.Core.Models.FileAvailability.Dataless);
        }

        // The tick of a group, from the menu. Only a group has this. A file's box is its own tick.
        var groupOnly = isGroup ? Visibility.Visible : Visibility.Collapsed;
        CtxGroupSelectAll.Visibility = groupOnly;
        CtxGroupSelectNone.Visibility = groupOnly;
        CtxGroupTickSeparator.Visibility = groupOnly;

        // Everything below acts on the same set, so it is offered on the same terms: there must be something
        // to act on. Recognize is enabled whether or not a backend is set up. The dialog says when there is
        // nothing to ask. A greyed-out entry would raise a question the user cannot get answered.
        bool any = rowCount > 0;
        CtxRecognize.IsEnabled = any;
        // Naming from a chosen release works on the current selection: a record, a disc's worth of ticks, or
        // one row that was named wrongly.
        CtxNameTracks.IsEnabled = any;
        CtxTagsFromFilename.IsEnabled = any;
        CtxTagsFromTags.IsEnabled = any;
        CtxNumberTracks.IsEnabled = any;

        // Splitting works on ONE file: the album-in-one-file that was pointed at. A record cannot be split, so
        // on a group the entry is hidden rather than greyed out. A disabled item invites the question of what
        // would enable it.
        CtxSplitAlbum.Visibility = isGroup ? Visibility.Collapsed : Visibility.Visible;
        CtxSplitAlbum.IsEnabled = isFile;
        // Files that look like albums in one file get the split item in bold. Any file can still be split by hand.
        CtxSplitAlbum.FontWeight = _contextQueueRow?.SplitCandidate is not null
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        // Only offered where a file is not on the disk. Anywhere else it would be a menu entry that does
        // nothing, which looks broken.
        CtxDownload.IsEnabled = dataless > 0;
        // The number of files, when there is more than one. A download of a record takes a long time, so its
        // size is shown in the offer, not afterwards in the status line.
        CtxDownload.Text = dataless > 1
            ? Loc.Format("Queue_CtxDownloadMany", dataless)
            : Loc.Get("Queue_CtxDownload");

        // On a group the edit is for the record or the artist, and the menu says so. The window that opens is
        // the many-row window, with the album fields filled in from what the rows have in common.
        CtxEdit.IsEnabled = any;
        // On one row it is that track's own editor, not an edit of "the selection". That is the window that opens.
        CtxEdit.Text = _contextQueueGroup is { } editGroup
            ? Loc.Get(editGroup.GroupAlbum is null ? "Queue_CtxEditArtist" : "Queue_CtxEditAlbum")
            : Loc.Get(rowCount == 1 ? "Queue_CtxEditTrack" : "MassEdit_MenuEdit");

        // A repair for a whole record at once, so it is offered only for a group. One file has nothing to
        // compare itself with, and its own folder is already shown in the Folder column.
        CtxNumberDiscs.Visibility = groupOnly;
        CtxNumberDiscs.IsEnabled = any;

        CtxQueueAddNowPlaying.IsEnabled = any;
        CtxQueuePlayNow.IsEnabled = any;
        CtxRemove.IsEnabled = any;
        CtxTrash.IsEnabled = any;
    }

    /// <summary>
    /// Disc numbering, asked for from the toolbar rather than from a record's own menu.
    ///
    /// <para>The menu entry has a record under the pointer. The toolbar works on ticks, which can span two
    /// records or part of one. So the groups are worked out from the ticked rows, using the store's own
    /// grouping rule. Each group is numbered from its folders. Numbering always reads the whole record,
    /// because the set of folders is what shows which disc is which. Part of a record cannot answer that.</para>
    /// </summary>
    private void NumberDiscsFromToolbar()
    {
        if (Model is not { } m || QueueActionRows() is not { Count: > 0 } rows)
            return;

        var groups = rows
            .Select(r => RPM.Core.Library.LibraryDatabase.QueueGroupKeys(r.AlbumArtist, r.Artist, r.Album))
            .Distinct()
            .ToList();

        var numbered = groups.Sum(g => m.NumberDiscsFromFolders(g.Artist, g.Album));
        m.SetStatus(numbered > 0
            ? Loc.Format("Queue_DiscsNumbered", numbered)
            : Loc.Get("Queue_DiscsNoneFound"));
    }

    /// <summary>Splitting, asked for from the toolbar. A split works on ONE file. Here the file is the single row the
    /// queue is about: the focused row, or the one ticked row if that is all there is. Several rows are not
    /// split. The user is told so, rather than the first row being cut.</summary>
    private void SplitAlbumFromToolbar()
    {
        if (Model is not { } m)
            return;

        var rows = QueueActionRows();
        if (rows.Count > 1)
        {
            m.SetStatus(Loc.Get("Queue_SplitOneFileOnly"));
            return;
        }

        _contextQueueRow = rows.Count == 1 ? rows[0] : _contextQueueRow;
        if (_contextQueueRow is null)
            return;
        CtxSplitAlbum_Click(this, new RoutedEventArgs());
    }

    /// <summary>Gives a record's files the disc numbers their folders imply. Says what it did either
    /// way: "nothing happened" and "nothing here says which disc it is" are different answers, and only
    /// one of them is the user's to act on.</summary>
    private void CtxNumberDiscs_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || _contextQueueGroup is not { GroupArtist: { } artist } group)
            return;
        var numbered = m.NumberDiscsFromFolders(artist, group.GroupAlbum);
        m.SetStatus(numbered > 0
            ? Loc.Format("Queue_DiscsNumbered", numbered)
            : Loc.Get("Queue_DiscsNoneFound"));
    }

    /// <summary>Ticks every row under the right-clicked group. The group's own tick is set by the store in one
    /// statement, and the loaded rows update in place.</summary>
    private void CtxGroupSelectAll_Click(object sender, RoutedEventArgs e) => TickGroup(true);

    private void CtxGroupSelectNone_Click(object sender, RoutedEventArgs e) => TickGroup(false);

    /// <summary>The tick of a record or an artist, from the menu. The highlight follows the ticks, so it is
    /// asked to update for the rows that changed.</summary>
    private void TickGroup(bool on)
    {
        if (_contextQueueGroup is not { } group)
            return;
        group.IsChecked = on;
        RefreshQueueGrid();
    }

    /// <summary>
    /// What a queue context action works on: the ticked rows. The queue has no other selection, because the
    /// ticks are the selection. Only when nothing is ticked does it fall back to the row that was
    /// right-clicked, so a single-row action does not need a tick first.
    ///
    /// <para>A right-clicked GROUP is the exception, and it takes priority over the ticks. It stands for every
    /// row under it. Most of those rows are paged out and were never loaded, so they cannot be ticked. Pointing
    /// at a record is how the whole record is chosen.</para>
    ///
    /// <para>Rows are matched by queue id, not by object. The ticked set is loaded fresh from the store, while
    /// the grid holds its own row objects. Comparing object references would say that the right-clicked row is
    /// never in the ticked set. Every context action would then act only on the row under the pointer, even
    /// when several rows are ticked.</para>
    /// </summary>
    private List<TrackRow> QueueActionRows()
    {
        if (Model is not { } m)
            return new List<TrackRow>();
        if (_contextQueueGroup is { GroupArtist: { } artist } group)
            return m.QueueRowsInGroup(artist, group.GroupAlbum).ToList();
        var ticked = m.SelectedQueueRowsSnapshot().ToList();
        if (ticked.Count > 0)
            return ticked;
        return _contextQueueRow is { } cr ? [cr] : [];
    }

    /// <summary>Fetches the ticked cloud placeholders, or every one under the record or artist that was
    /// right-clicked. This is the only place where the app downloads a file on purpose. Everywhere else, a
    /// file that is not on the disk cannot be opened (see <see cref="RPM.Core.Files.CloudFile"/>). This is
    /// where the user asks for one.
    ///
    /// <para>A group asks the store for its placeholders directly, not through <see cref="QueueActionRows"/>.
    /// This action needs only a SUBSET of the rows under the node. Filtering in the store is faster than
    /// loading an artist's whole discography and throwing most of it away.</para></summary>
    private async void CtxDownload_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m)
            return;
        if (_contextQueueGroup is { GroupArtist: { } artist } group)
            await m.DownloadCloudGroupAsync(artist, group.GroupAlbum);
        else if (QueueActionRows() is { Count: > 0 } rows)
            await m.DownloadCloudRowsAsync(rows);
    }

    /// <summary>
    /// Recognition, as a dialog the user can see and answer. The dialog shows which files were acted on,
    /// reports what each one became, and can use the advanced lookup layer. It handles all three, for exactly
    /// the rows this menu acts on.
    /// </summary>
    private async void CtxRecognize_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        if (QueueActionRows() is not { Count: > 0 } rows || !await EnsureSignedInAsync())
            return;

        await Dialogs.RecognizeDialog.ShowAsync(
            XamlRoot, m,
            Dialogs.SupportStanding.FromAccount(App.Services.GetRequiredService<SupporterService>()),
            rows,
            // Correcting a result is the queue's ordinary edit, so it gets the queue's ordinary
            // lookup: no disc art, because a queue row has no album folder to put a disc.png beside.
            // No track step: recognition already writes what it finds, row by row, as it finds it.
            (editing, wantArt) => LookUpReleaseForRowsAsync(
                editing, wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: false));
    }

    /// <summary>
    /// Names the selected rows from a release the user picks. The rows are only proposals until the queue is
    /// imported, so a wrong pressing costs one more lookup, not a retag of thirty files. The count shows
    /// whether the pressing was right. "Named 18 of 20" tells the user to try another pressing.
    /// </summary>
    private async void CtxNameTracks_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || QueueActionRows() is not { Count: > 0 } rows)
            return;

        // Already settled in the album editor? Then that pressing is used, and the window opens on the tracks,
        // not on a search.
        var settled = m.QueueAlbumIntentForRows(rows) is { ReleaseId: { Length: > 0 } id } intent
            ? new RPM.Core.Metadata.Providers.ReleaseRef(intent.ReleaseProvider ?? "", id)
            : null;

        if (await NameTracksFromReleaseAsync(
                rows, settled, known: null, QueueTrackStep(rows, ticked: true)) is not { } result)
            return;

        m.SetStatus(result.Named == 0
            ? Loc.Get("Queue_NamedNone")
            : Loc.Format("Queue_NamedTracks", result.Named, result.Total));
    }

    private async void CtxSplitAlbum_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || _contextQueueRow is not { } row || XamlRoot is null)
            return;
        var result = await Dialogs.SplitAlbumDialog.ShowAsync(XamlRoot, row, m.CreateSplitAnalyzer(), m.Playback, m.Catalog);
        if (result is not null)
            m.ReplaceWithSplit(row, result);
    }

    private async void CtxEdit_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        var rows = QueueActionRows();   // The ticked rows, or the right-clicked row.
        if (rows.Count == 0)
            return;

        // A right-clicked RECORD opens the record editor. It is the same window the library uses, because a
        // queued album is still an album. It is also the only place to give the album-level answers before the
        // files are filed: whether the album is classical, whether it is levelled, and what the printed disc
        // label looks like.
        if (_contextQueueGroup is { GroupArtist: { } albumArtist, GroupAlbum: not null } albumGroup)
        {
            await EditQueueAlbumAsync(m, albumArtist, albumGroup.GroupAlbum!, rows);
            return;
        }

        // One row opens the full per-file editor. Several rows open the mass-edit window.
        if (rows.Count == 1)
        {
            // Same lookup the many-row window gets, minus the disc label: a queue row has no album
            // folder to put a disc.png beside until it is imported.
            await Dialogs.TrackDetailsDialog.ShowAsync(
                XamlRoot, rows[0],
                // No track step: one file is what this window edits, field by field.
                wantArt => LookUpReleaseForRowsAsync(
                    rows, wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: false));
            // With artist + title filled in, the row counts as manually recognized and becomes
            // importable.
            rows[0].MarkManual();
            m.PersistQueueRows(rows);
            return;
        }

        // Queue rows also get the release lookup. This is the last point before the files are filed, so a
        // record that came in with a bad name should be named here. The cover it finds goes with the rows into
        // the import.
        if (!await MassEdit.EditQueueRowsAsync(XamlRoot, rows,
                // No track step: this window writes its ticked fields to these same rows on OK, and a
                // fill going in behind it would be written twice.
                wantArt => LookUpReleaseForRowsAsync(
                    rows, wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: false)))
            return;
        // The ticked rows come out of the store, so they are not the tree's own objects and nothing
        // is listening to them: the edit has to be written back by hand.
        m.PersistQueueRows(rows);
    }

    /// <summary>
    /// The record editor for a queued album. Its fields are written to the rows. The two album-level answers,
    /// classical and levelled, and the disc's printed label are held against the record until the import
    /// gives it a folder.
    /// </summary>
    private async Task EditQueueAlbumAsync(MainModel m, string artist, string album, List<TrackRow> rows)
    {
        // The folder the record is sitting in NOW, so the editor can show the cover and disc label
        // already lying beside the files. The deepest folder they share: a two-disc set in CD1/CD2
        // answers with the album folder above them, which is where a rip leaves its scans.
        var folder = CommonFolderOf(rows);
        var opening = m.QueueAlbumIntentForRows(rows);

        // This is the one editor that can carry the track step. Nothing here touches a file. The rows are copies
        // from the store. The fill and this window both write to them, and both are saved through
        // PersistQueueRows below. No file is moved, so nobody is left holding a path that has changed.
        var intent = await AlbumEdit.EditQueueAlbumAsync(
            XamlRoot!, folder, rows,
            wantArt => LookUpReleaseForRowsAsync(
                rows, wantArt, QueueTrackStep(rows, ticked: false), wantDiscArt: true),
            normalize: opening?.NormalizeSet == true ? opening.Normalize : null,
            classical: opening?.ClassicalSet == true ? opening.Classical : null,
            // The label chosen earlier. Nothing on disk holds it for a record that is not filed yet. An editor
            // that is not given it shows an empty well, even when the record has a label.
            discArtHeld: opening?.DiscArt);
        if (intent is null)
            return;

        // The rows first: they are copies out of the store, so nothing is listening to them.
        m.PersistQueueRows(rows);
        if (!intent.IsEmpty)
            m.SetQueueAlbumIntent(rows, intent);
    }

    /// <summary>The deepest folder that a set of rows share. If they share no folder, it is the first row's own
    /// folder. A record spread over two disc folders gives the album folder above them.</summary>
    private static string CommonFolderOf(IReadOnlyList<TrackRow> rows)
    {
        var first = Path.GetDirectoryName(rows[0].FilePath) ?? "";
        var common = first;
        foreach (var row in rows.Skip(1))
        {
            var dir = Path.GetDirectoryName(row.FilePath) ?? "";
            while (common.Length > 0
                   && !dir.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(dir, common, StringComparison.OrdinalIgnoreCase))
            {
                common = Path.GetDirectoryName(common) ?? "";
            }
        }
        return common.Length > 0 ? common : first;
    }

    private void CtxRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m && QueueActionRows() is { Count: > 0 } rows)
            m.RemoveFromQueue(rows);
    }

    /// <summary>
    /// Moves the selected files to the OS trash, so they can be recovered, and removes their rows.
    ///
    /// <para>This is the other half of splitting an album. When a cut comes out wrong, the tracks it made are
    /// not wanted, and the original is still in <c>Originals/</c>. Without this, the only way to clear them
    /// was a file manager.</para>
    /// </summary>
    private async void CtxTrash_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        var rows = QueueActionRows();
        if (rows.Count == 0)
            return;

        var confirm = await PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("QueueTrash_Title"),
            rows.Count == 1
                ? Loc.Format("QueueTrash_BodyOne", Path.GetFileName(rows[0].FilePath))
                : Loc.Format("QueueTrash_Body", rows.Count),
            primaryText: Loc.Get("QueueTrash_Confirm"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Close);
        if (confirm != ContentDialogResult.Primary)
            return;

        // A file that is playing is held open. Stop it before it is moved.
        App.Services.GetRequiredService<PlaybackService>().Stop();

        var paths = rows.Select(r => r.FilePath).ToList();
        var refused = await Task.Run(() => TrashFiles(paths));

        // Only rows whose file actually went are dropped: a queue that forgets a file still on disk
        // is a file nobody can get back to.
        var gone = rows.Where(r => !refused.Contains(r.FilePath, StringComparer.OrdinalIgnoreCase)).ToList();
        if (gone.Count > 0)
            m.RemoveFromQueue(gone);

        if (refused.Count > 0)
        {
            await PromptDialog.ShowAsync(
                XamlRoot, Loc.Get("QueueTrash_Title"), Loc.Format("QueueTrash_Failed", refused.Count),
                closeText: Loc.Get("Common_OK"));
        }
    }

    /// <summary>Moves each path to the trash, and the cue sheet written beside it during a split. Returns the
    /// paths that are still on disk, because the OS may refuse. The caller must not treat those as removed.</summary>
    private static List<string> TrashFiles(IReadOnlyList<string> paths)
    {
        var refused = new List<string>();
        foreach (var path in paths)
        {
            if (!RPM.Core.Files.OsTrash.Trash(path) && File.Exists(path))
            {
                refused.Add(path);
                continue;
            }
            var cue = Path.ChangeExtension(path, ".cue");
            if (File.Exists(cue))
                RPM.Core.Files.OsTrash.Trash(cue);
        }
        return refused;
    }

    /// <summary>Keeps the best copy of each track and removes the others. This includes any track the library
    /// already holds in equal or better quality. Only queue rows are removed. The files stay where they are.</summary>
    private async void QueueRemoveDuplicates_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
            await m.RemoveQueueDuplicatesAsync();
    }

    private async void QueueRemoveUnreadable_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } m)
            await m.RemoveQueueUnreadableAsync();
    }

    private void QueueAddToNowPlaying_Click(object sender, RoutedEventArgs e) => SendQueueToPlayer(play: false);
    private void QueuePlayNow_Click(object sender, RoutedEventArgs e) => SendQueueToPlayer(play: true);

    private void SendQueueToPlayer(bool play)
    {
        var rows = QueueActionRows();
        if (rows.Count == 0)
            return;
        var tracks = rows.Select(r => r.ToWantedTrackInfo()).ToList();
        App.Services.GetRequiredService<RPaMpService>().AddTracks(tracks, play);
    }

    // ── the toolbar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The toolbar and the right-click menu are two ways to reach the same handlers.
    ///
    /// <para>The toolbar has no row under the pointer. So it clears the context group and uses the focused row
    /// as the fallback. This is the same rule that <see cref="QueueActionRows"/> already applies: the ticks, and
    /// only when nothing is ticked, the row under the pointer. It is stated here in the toolbar's own terms.</para>
    /// </summary>
    private void QueueToolbar_Invoked(Presentation.Import.QueueAction action)
    {
        _contextQueueGroup = null;
        _contextQueueRow = QueueFocusedNode()?.Row;

        var nothing = new RoutedEventArgs();
        switch (action)
        {
            case Presentation.Import.QueueAction.Recognize: CtxRecognize_Click(this, nothing); break;
            case Presentation.Import.QueueAction.NumberTracks: NumberTracks_Click(this, nothing); break;
            case Presentation.Import.QueueAction.TagsFromFilename: TagsFromFilename_Click(this, nothing); break;
            case Presentation.Import.QueueAction.TagsFromTags: TagsFromTags_Click(this, nothing); break;
            case Presentation.Import.QueueAction.Edit: CtxEdit_Click(this, nothing); break;
            case Presentation.Import.QueueAction.Remove: CtxRemove_Click(this, nothing); break;
            case Presentation.Import.QueueAction.NameFromRelease: CtxNameTracks_Click(this, nothing); break;
            case Presentation.Import.QueueAction.Download: CtxDownload_Click(this, nothing); break;
            case Presentation.Import.QueueAction.SplitAlbum: SplitAlbumFromToolbar(); break;
            case Presentation.Import.QueueAction.NumberDiscs: NumberDiscsFromToolbar(); break;
            case Presentation.Import.QueueAction.AddNowPlaying: QueueAddToNowPlaying_Click(this, nothing); break;
            case Presentation.Import.QueueAction.PlayNow: QueuePlayNow_Click(this, nothing); break;
            case Presentation.Import.QueueAction.Trash: CtxTrash_Click(this, nothing); break;
            case Presentation.Import.QueueAction.ShowInFinder: CtxShowInFinder_Click(this, nothing); break;
            case Presentation.Import.QueueAction.RemoveDuplicates: _ = RemoveDuplicatesAsync(); break;
            case Presentation.Import.QueueAction.RemoveUnreadable: _ = RemoveUnreadableAsync(); break;
        }
    }



    /// <summary>Keeps the best copy of each track and removes the others. This includes any track the library
    /// already holds in equal or better quality. Only queue rows are removed. The files stay where they are.</summary>
    private async Task RemoveDuplicatesAsync()
    {
        if (Model is { } m)
            await m.RemoveQueueDuplicatesAsync();
    }

    private async Task RemoveUnreadableAsync()
    {
        if (Model is { } m)
            await m.RemoveQueueUnreadableAsync();
    }

    /// <summary>Opens the file manager on the first selected row, with that file picked out. Only one row is
    /// used, not forty, because a window for each file would not help the user.</summary>
    private void CtxShowInFinder_Click(object sender, RoutedEventArgs e)
    {
        if (QueueActionRows() is not [{ FilePath: { Length: > 0 } path }, ..])
            return;
        if (!RPM.Core.Files.OsReveal.Reveal(path))
            Model?.SetStatus(Loc.Get("Queue_ShowInFolderFailed"));
    }

    /// <summary>How many rows the toolbar is about right now, using the same rule its buttons act on. It also
    /// decides which menu entries have something to do.
    ///
    /// <para>This is the ticked count and nothing else. The ticks are the selection. A focused row is not
    /// counted. A selected group row is not counted either, because a group row ticks nothing. Enabling the
    /// keys for a group row would offer actions that find no rows to act on.</para></summary>
    private void RefreshQueueToolbar()
    {
        if (Model is not { } m)
            return;
        var ticked = m.QueueTickedFacts;
        QueueToolbar.SetSubject(ticked.Ticked, ticked.Dataless);
        QueueToolbar.SetCleanUp(m.QueueDuplicateCount > 0, m.QueueUnreadableCount > 0);
    }

    /// <summary>Writes the order into the Track field, per disc, 1..n.
    ///
    /// <para>It reads the rows the way the grid shows them, which is what "based on order" means. This is why a
    /// row that a filter is hiding is not numbered. Numbering rows nobody can see would silently lose a track
    /// from a record.</para></summary>
    private void NumberTracks_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || QueueActionRows() is not { Count: > 0 } rows)
            return;

        var changed = Presentation.Import.QueueNumbering.Renumber(rows);
        m.PersistQueueRows(rows);
        m.SetStatus(changed == 0
            ? Loc.Get("Queue_NumberedNone")
            : Loc.Format("Queue_Numbered", changed));
    }

    /// <summary>Reads the tags back from the file names. Everything a wrong pattern would do is shown on screen
    /// before anything is written. That is why this is a window and not a button.</summary>
    /// <summary>Builds one field from the others, for every ticked row. The window is where the pattern is
    /// written and where its effect is shown. Nothing is written until the user confirms.</summary>
    private async void TagsFromTags_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        if (QueueActionRows() is not { Count: > 0 } rows)
            return;

        var written = await Dialogs.TagsFromTagsDialog.ShowAsync(XamlRoot, rows);
        if (written == 0)
            return;

        m.PersistQueueRows(rows);
        m.SetStatus(Loc.Format("TagsFromTags_Applied", written));
    }

    private async void TagsFromFilename_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        if (QueueActionRows() is not { Count: > 0 } rows)
            return;

        var written = await Dialogs.FilenameTagsDialog.ShowAsync(XamlRoot, rows);
        if (written == 0)
            return;

        m.PersistQueueRows(rows);
        m.SetStatus(Loc.Format("FilenameTags_Applied", written));
    }
}

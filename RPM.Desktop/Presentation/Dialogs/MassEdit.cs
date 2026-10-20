using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Cloud;
using RPM.Core.Editing;
using RPM.Core.Files;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>Edits many tracks at once. This is one window with a checkbox for each field. Each control
/// starts with the value from the selection: the one value when the tracks agree, or a list to pick
/// from or type into when they differ. Only the ticked fields are written.
///
/// <para>It is a separate window, not a step in another flow. Three screens use it: the import queue,
/// the album wall and the library table. Each one works the same way. It passes in the rows and gets
/// back what changed. Where the rows come from is up to the caller. What happens to the tracks is
/// handled here, and the two do not need to know about each other. Callers never build the dialog or
/// apply a result by hand.</para>
///
/// <para>The two paths differ because the tracks do. Queue rows are files that have not been filed yet,
/// so the edit is set on the pending row, and the file moves when the import runs. Library rows are
/// files that are already filed, so the edit is written into their tags. Each file is then filed again
/// by the naming template when a field that sets its path has changed.</para>
/// </summary>
internal static class MassEdit
{
    /// <summary>The fields this window offers: every field that can be true of more than one file at
    /// once.
    ///
    /// <para>Title, track number and lyrics are never offered. Each one is true of ONE file. Setting
    /// one across a selection would give every track the same name. Every other field is offered,
    /// including the record's own fields.</para>
    ///
    /// <para>The album fields are offered even when the selection spans several folders. Setting
    /// "Album" across unrelated records renames all of them at once. That rule belongs to the album
    /// editor, not to this window. A record whose tracks are spread over four folders is exactly what a
    /// mass edit is for, so hiding "Album" there would hide the repair. The risk is shown in the
    /// caution band at the head of the window, which says how many records the tick will change.</para>
    /// </summary>
    internal static IReadOnlyList<TagField> FieldsFor() =>
        [.. TagFields.TrackOnly().Where(f => !IsPerFile(f.Kind)), .. TagFields.AlbumAndDisc()];

    /// <summary>Track fields that this window does not offer, because one value across a selection is
    /// never the right answer. Title and track number name a single file. So do the title's sort name,
    /// the movement this file is, the musical key, and the id of the recording it holds. Setting any of
    /// them across a dozen tracks gives twelve files one identity.
    ///
    /// <para>The work is not in this list. Selecting the four movements of a symphony and naming the
    /// work once is what this window is for. It is the fastest way to file a classical record
    /// correctly.</para>
    /// </summary>
    internal static bool IsPerFile(TagFieldKind kind) => kind
        is TagFieldKind.Title or TagFieldKind.TrackNo or TagFieldKind.Lyrics
        or TagFieldKind.TitleSort or TagFieldKind.Movement or TagFieldKind.MovementName
        or TagFieldKind.MusicalKey or TagFieldKind.RecordingId;

    private static ILogger Log =>
        App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.MassEdit");

    // ── the two ways in ───────────────────────────────────────────────────────

    /// <summary>Edits rows that are waiting to be imported. The values are set on the rows, and the
    /// files move when the queue is imported. Returns true when the window was used, and false when it
    /// was closed with nothing ticked. The caller must save the rows itself, because a ticked queue row
    /// is a copy taken from the store, not the tree's own object.
    /// </summary>
    /// <param name="lookup">Where the window's "look up release" button goes. Null hides the button.
    /// There is no disc art here. Disc art is written as the album folder's disc.png, and a queue row
    /// has no album folder until it is imported. The cover travels with the rows themselves, and it
    /// survives the move.</param>
    public static async Task<bool> EditQueueRowsAsync(
        XamlRoot root, IReadOnlyList<TrackRow> rows, Func<ArtAsk, Task<MassEditLookup?>>? lookup = null)
    {
        if (rows.Count == 0)
            return false;
        // Everything is offered, as on the library side, for the same reason. This is one window, and a
        // field must be in the same place whichever way the user came in. Queue rows are not filed yet,
        // so "one album" means what the rows say they are, not where they sit. Rows that disagree on an
        // album name still get the album fields, and the caution says they disagree.
        var result = await MassEditDialog.ShowAsync(
            root, rows.Count, BuildFields(rows), lookup,
            spansAlbums: !AgreeOnAlbum(rows));
        if (result is null)
            return false;

        foreach (var row in rows)
        {
            foreach (var (kind, value) in result.Fields)
                TagFields.Get(kind).Set(row, value);
            if (result.ArtBytes is { Length: > 0 })
                row.SetPendingArt(result.ArtBytes, result.ArtMime);
            else if (result.RemoveArt)
                row.SetPendingArt([], null);   // An empty array removes the cover when the file is written
        }
        return true;
    }

    /// <summary>Whether these rows are all one record. A blank album counts as agreement. A set of
    /// freshly ripped files with no album name yet is the case where the album fields are most
    /// wanted.</summary>
    internal static bool AgreeOnAlbum(IReadOnlyList<TrackRow> rows) =>
        rows.Select(r => (r.Album is { Length: > 0 } a ? a : r.CurrentAlbum).Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() <= 1;

    /// <summary>Edits rows whose files are already in the library. Returns every folder the edit
    /// touched: the ones it left and the ones it landed in. It returns an empty list when the window
    /// was closed with nothing ticked.</summary>
    /// <param name="lookup">Where the window's "look up release" button goes, when the caller has an
    /// album to look up. Null hides the button.</param>
    public static async Task<IReadOnlyList<string>> EditLibraryRowsAsync(
        XamlRoot root, IReadOnlyList<TrackRow> rows, Func<ArtAsk, Task<MassEditLookup?>>? lookup = null)
    {
        if (rows.Count == 0)
            return [];

        // Everything is offered, whatever the selection is: album fields, the cover, and the disc's
        // label. The folder boundary is not a limit here.
        //
        // Setting "Album" across unrelated records renames all of them at once. The album editor is the
        // window that keeps to one record. This window is for sweeping up tracks that are scattered, so
        // it offers the album fields for any selection.
        //
        // The caution band at the head of the window is raised on the condition that decides whether
        // the fields apply to several records. See MassEditDialog.UpdateCaution.
        bool spansAlbums = AlbumFolderOf(rows) is null;
        var result = await MassEditDialog.ShowAsync(
            root, rows.Count, BuildFields(rows), lookup,
            discArt: true, cover: true, spansAlbums: spansAlbums);
        return result is null ? [] : await WriteToLibraryAsync(rows, result);
    }

    /// <summary>The one library folder that every row sits in, or null when the rows are spread over
    /// more than one. A cover, a disc.png and an album name all belong to a folder. If there is no
    /// single folder, there is nowhere to put them.</summary>
    internal static string? AlbumFolderOf(IReadOnlyList<TrackRow> rows)
    {
        string? folder = null;
        foreach (var row in rows)
        {
            if (Path.GetDirectoryName(row.FilePath) is not { Length: > 0 } dir)
                return null;
            if (folder is null)
                folder = dir;
            else if (!string.Equals(folder, dir, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return folder;
    }

    private static IReadOnlyList<MassEditFieldInput> BuildFields(IReadOnlyList<TrackRow> rows) =>
        [.. FieldsFor()
            .Select(f => new MassEditFieldInput(f, FieldValueAggregate.Summarize(rows.Select(f.Get))))];

    // ── library: write into the files' tags and re-file each by the naming template ──

    /// <summary>Carries what these rows now say down onto the files they stand for.
    ///
    /// <para>The methods above fill a <see cref="TrackRow"/> in and stop. That is all the import queue
    /// needs, and it is why the methods can be shared. This method is the step the queue does not have.
    /// It writes the tags, re-files the file under the naming template, and moves the library's mirror
    /// row with it. <c>TagsEdited</c> marks the row as holding a hand-made answer, not whatever the
    /// file last said. Without it, the write does nothing.</para>
    ///
    /// <para>The caller is the last writer on these rows. Re-filing moves files, and a row keeps the
    /// path it had when it was made. So a second window that holds the same rows and writes afterwards
    /// finds nothing at that path.</para>
    /// </summary>
    /// <returns>Every folder the write touched, for the wall to re-read.</returns>
    public static async Task<IReadOnlyList<string>> WriteHandMadeRowsAsync(IReadOnlyList<TrackRow> rows)
    {
        if (rows.Count == 0)
            return [];

        var wanted = rows.ToDictionary(r => r, r => r.ToWantedTrackInfo() with { TagsEdited = true });
        return await WriteToLibraryAsync(
            rows,
            new MassEditResult(new Dictionary<TagFieldKind, string>(), null, null),
            row => wanted[row]);
    }

    /// <summary>Writes the edit into the files, and re-points their library rows. Returns every folder
    /// the edit touched, both the ones it left and the ones it moved into. The album wall uses this
    /// list to refresh cards that it holds by folder. Otherwise those cards would keep showing old data
    /// from memory.</summary>
    /// <param name="infoFor">How to turn a row into the tags to write. Null uses the mass edit's own
    /// method. That method reads the file fresh, so untouched fields are kept, and then the ticked
    /// fields are laid on top. The single-track editor passes its own method, because that window edits
    /// every field of one row, not a few fields of many. In that case, what the editor holds is the
    /// answer.</param>
    public static async Task<IReadOnlyList<string>> WriteToLibraryAsync(
        IReadOnlyList<TrackRow> rows, MassEditResult result, Func<TrackRow, TrackInfo>? infoFor = null)
    {
        var log = Log;
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root)
        {
            log.LogWarning("[mass-edit] no open library — {Rows} rows left untouched", rows.Count);
            return [];
        }
        var db = library.Db;
        var template = App.Services.GetRequiredService<SettingsService>().DefaultTemplate;
        bool artChanged = result.ArtBytes is { Length: > 0 };
        bool discArtChanged = result.DiscArtBytes is { Length: > 0 };

        // The record being edited is often the one that is playing. For example, the user hears a wrong
        // spelling and fixes it. The player has the file open, so it is released first. Remember where
        // the playback position, and resume the track afterwards, wherever it was moved to.
        var hold = await ReleasePlayerAsync(rows, log);

        // Read each file's own tags fresh, so untouched fields are kept. Then the edits are laid on
        // top.
        //
        // A track that exists only in the cloud has no file to read or write. The mirror row is the
        // record, so the edit is made on that row. The sync run's catalogue pass then sends it to the
        // store, and into its sidecar. A download writes the sidecar back. Without this branch, the
        // whole edit would be dropped as unreadable, and the dialog would appear to save nothing.
        var plan = new List<(string OldRelative, TrackInfo Info, LibraryTrack? Cloud, TrackRow Row)>();
        var unreadable = new List<string>();
        foreach (var row in rows)
        {
            var relative = Relative(root, row.FilePath);
            var cloud = File.Exists(row.FilePath) ? null : db.FindByRelativePath(relative);
            if (cloud is { IsOffloaded: false })
                cloud = null;   // A file that is simply missing is not a cloud track

            TrackInfo info;
            if (infoFor is not null)
            {
                info = infoFor(row);
            }
            else if (cloud is not null)
            {
                info = OverlayEdits(cloud.ToTrackInfo(root), result);
            }
            else
            {
                try { info = TagReader.Read(row.FilePath); }
                catch (Exception ex) { unreadable.Add($"{row.FilePath}: {ex.Message}"); continue; }
                info = OverlayEdits(info, result);
            }
            plan.Add((relative, info, cloud, row));
        }
        foreach (var bad in unreadable)
            log.LogWarning("[mass-edit] tags unreadable, row skipped — {Detail}", bad);
        if (plan.Count == 0)
        {
            log.LogWarning("[mass-edit] nothing to write: all {Rows} rows were unreadable", rows.Count);
            return [];
        }

        log.LogInformation("[mass-edit] {Files} files, fields=[{Fields}], art={Art}, discArt={DiscArt}, template={Template}",
            plan.Count, string.Join(",", result.Fields.Keys), artChanged, discArtChanged, template);

        // Files that the applier refuses (a locked file, or a name clash at the destination) are
        // collected on the worker and reported afterwards. Without this, a mass edit that wrote nothing
        // would fail silently, and that looks the same as the window not saving at all.
        var refused = new List<string>();
        // Maps the old path to the row as it now is. Rows are re-pointed, not deleted and added again.
        // Some data exists only in the mirror, such as when the track joined the library and how many
        // times it has been played. A delete and an insert would lose all of that each time a spelling
        // is corrected. See LibraryDatabase.MoveMany.
        var moves = new List<(string From, LibraryTrack Track)>(plan.Count);
        // Album folders that take the edit but have no file moved into them, because their audio is in
        // the cloud. The applied results below cannot name these folders, since nothing was applied to
        // them. The folder sidecars are what every local reader uses to show a record. So the folders
        // are collected here.
        var cloudFolders = new List<string>();
        // Where each caller's row ended up, so the row can be told. A re-file moves the file away from
        // the path the row was made with. A row that still names the old path is skipped as unreadable
        // by every later write, and it is skipped silently. That is how a second window over the same
        // rows could save nothing. See TrackRow.FiledAt.
        var landed = new List<(TrackRow Row, string Path)>(plan.Count);

        await Task.Run(() =>
        {
            // RenamePlanner keeps the input order. So suggestion i, and its apply result, line up with
            // plan[i]. That pairing is how a moved file's old library row is found again.
            var suggestions = RenamePlanner.Plan(
                plan.Select(p => p.Info), template, root, namingView: MergedDiscNaming(library, db, root, template));
            var applied = new List<ApplyResult>(suggestions.Count);
            bool moved = false;

            for (int i = 0; i < suggestions.Count; i++)
            {
                var s = suggestions[i];

                // Nothing to move: the bytes are in the cloud. The row takes the edit and the path that
                // the template gives it. So the record is filed in the library the same way as a local
                // one. The store learns the new location on the next catalogue pass. That keeps a later
                // download in the right place.
                if (plan[i].Cloud is { } cloudRow)
                {
                    var refiled = cloudRow with
                    {
                        RelativePath = s.ProposedRelativePath,
                        Folder = FolderOf(s.ProposedRelativePath),
                        FileName = NameOf(s.ProposedRelativePath),
                        Title = s.Track.Title,
                        Artist = s.Track.Artist,
                        AlbumArtist = s.Track.AlbumArtist,
                        Album = s.Track.Album,
                        Genre = s.Track.Genre,
                        TrackNumber = s.Track.TrackNumber,
                        DiscNumber = s.Track.DiscNumber,
                        DiscCount = s.Track.DiscCount,
                        DiscSubtitle = s.Track.DiscSubtitle,
                        Year = s.Track.Year,
                        // Every other field that the edit windows offer and that the row keeps. A field
                        // left out here is written back as the row's old value by MoveMany. The edit
                        // would then look saved, but it would not be. These are the explicit mark, the
                        // subgenre, the release type and the classical block. They are mapped the same way as
                        // LibraryDatabase.FromTrackInfo maps them for a local file.
                        Advisory = s.Track.Advisory,
                        Subgenre = s.Track.Subgenre,
                        ReleaseType = s.Track.ReleaseType,
                        Composer = s.Track.Composers,
                        Work = s.Track.Work,
                        MovementName = s.Track.MovementName,
                        MovementNumber = (int)s.Track.MovementNumber,
                    };
                    moves.Add((plan[i].OldRelative, refiled));
                    landed.Add((plan[i].Row, Path.Combine(
                        root, refiled.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
                    cloudFolders.Add(Path.Combine(
                        root, refiled.Folder.Replace('/', Path.DirectorySeparatorChar)));
                    continue;
                }

                var res = OrganizeApplier.Apply(s, root, OrganizeMode.Move);
                applied.Add(res);
                // A file that could not be moved (locked, or a name clash) keeps its old tags and its
                // old library row. Only the rows that changed on disk are re-pointed.
                if (!res.Success)
                {
                    refused.Add($"{plan[i].OldRelative} → {s.ProposedRelativePath}: {res.Error}");
                    continue;
                }
                if (!s.IsAlreadyInPlace)
                    moved = true;

                LibraryTrack row;
                try { row = LibraryDatabase.FromTrackInfo(TagReader.Read(res.DestinationPath), root); }
                catch { row = LibraryDatabase.FromTrackInfo(s.Track with { FilePath = res.DestinationPath }, root); }
                moves.Add((plan[i].OldRelative, row));
                landed.Add((plan[i].Row, res.DestinationPath));
            }

            // Re-point each row at what actually landed on disk, keeping the row itself.
            db.MoveMany(moves);

            // Move the RECORD along with its tracks. An edit that changes an album's name or artist re-
            // files the album under the naming template. The mirror also holds data about the album:
            // the rating, the review, the favourite mark, the pressing it is linked to, its RPM id, its
            // catalogue number, and whether it is classical or levelled. That data must move with the
            // album. Otherwise it is left on a folder with nothing in it. The bulk re-file already does
            // this, so a one-album edit uses the same routine.
            var folderMoves = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (from, track) in moves)
            {
                var was = FolderOf(from);
                if (!folderMoves.TryGetValue(was, out var destinations))
                    folderMoves[was] = destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                destinations.Add(track.Folder);
            }
            LibraryRefiler.FollowFolders(db, root, folderMoves);

            // A row whose audio is in the cloud has no file whose modified time shows the edit. So the
            // row records the edit itself. Otherwise the next catalogue read would write the old values
            // back over the correction before it was sent. See MetadataEditedUtcTicks.
            var edited = plan.Where(p => p.Cloud is not null).Select(p => p.OldRelative).ToList();
            if (edited.Count > 0)
                db.NoteMetadataEdited(
                    moves.Where(m => edited.Contains(m.From, StringComparer.OrdinalIgnoreCase))
                         .Select(m => m.Track.RelativePath),
                    DateTime.UtcNow.Ticks);

            if (moved)
                OrganizeApplier.CleanupEmptyFolders(root);

            // New art was embedded into every file. Overwrite each touched folder's cover, so the wall
            // and the art display pick it up. WriteCover replaces an existing cover. WriteCovers would
            // skip a folder that already has one. The folder gets the bigger picture when there is one.
            // A catalogue's scan is kept whole on disk, while the copy inside each file is the scaled
            // one. The disc's own label is a folder sidecar (disc.png), and never a tag. It is written
            // the same way and to the same folders. It replaces an existing one, because here the user
            // explicitly asks for this picture on this record. A record that is only in the cloud is in
            // that list too. It moved no file, but it is still a record in the library. The cover and
            // the disc label given to the editor are the pictures this machine shows it with, until its
            // audio comes back down.
            if (artChanged || discArtChanged || result.RemoveDiscArt || result.RemoveArt)
                foreach (var folder in applied.Where(r => r.Success)
                             .Select(r => Path.GetDirectoryName(r.DestinationPath))
                             .Concat(cloudFolders)
                             .Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    // An offloaded album has no folder on this machine. The writers below refuse a
                    // folder that does not exist. So the folder is created here, but only when there is
                    // a picture to put in it. It is also the folder that a later download lands in.
                    if ((artChanged || discArtChanged) && !Directory.Exists(folder))
                    {
                        try { Directory.CreateDirectory(folder!); }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "[mass-edit] could not make {Folder} for its pictures", folder);
                            continue;
                        }
                    }

                    if (artChanged)
                        FolderArtWriter.WriteCover(folder!, result.FolderArtBytes ?? result.ArtBytes!);
                    else if (result.RemoveArt)
                        FolderArtWriter.DeleteCover(folder!);
                    if (discArtChanged)
                        FolderArtWriter.ReplaceDisc(folder!, result.DiscArtBytes!);
                    else if (result.RemoveDiscArt)
                        FolderArtWriter.DeleteDisc(folder!);
                }
        });

        foreach (var no in refused)
            log.LogWarning("[mass-edit] file refused, left as it was — {Detail}", no);
        log.LogInformation("[mass-edit] done: {Written} of {Files} written, {Refused} refused",
            plan.Count - refused.Count, plan.Count, refused.Count);

        // Read one row straight back from the mirror. This checks the result in the database itself,
        // and not in the code that meant to write it. It also shows that the row survived the edit,
        // rather than being replaced by a new row with no history.
        if (moves.FirstOrDefault().Track is { } sample)
        {
            var back = db.FolderTracks(sample.Folder)
                .FirstOrDefault(t => string.Equals(t.RelativePath, sample.RelativePath, StringComparison.Ordinal));
            log.LogInformation(
                "[mass-edit] mirror row {Path}: {State} albumArtist={AlbumArtist} album={Album} plays={Plays} added={Added:u}",
                sample.RelativePath, back is null ? "MISSING" : "present",
                back?.AlbumArtist, back?.Album, back?.Plays,
                back is { AddedUtcTicks: > 0 } a ? new DateTime(a.AddedUtcTicks, DateTimeKind.Utc) : default);
        }

        // The rows the caller holds now point at where their files are. This runs here and not in the
        // worker, because these objects are bound to a grid. It runs after the write, so a refused file
        // keeps the path it still has.
        foreach (var (row, path) in landed)
            row.FiledAt(path);

        library.NotifyChanged();
        ResumePlayer(hold, root, moves, log);

        // Send the record's pictures to every other place where this library is read. A removal is sent
        // too, or it does not hold. A machine that still has the sleeve would read the missing picture
        // as a gap, and send the sleeve back. The send is not awaited. The caller is about to redraw
        // one album card, and should not wait for an upload. If a send fails, the sync run's art pass
        // picks it up later. That pass compares what is stored with what is on this machine.
        if (artChanged || discArtChanged || result.RemoveArt || result.RemoveDiscArt)
            _ = PublishArtAsync(
                moves,
                artChanged ? result.FolderArtBytes ?? result.ArtBytes : null,
                discArtChanged ? result.DiscArtBytes : null,
                result.RemoveArt, result.RemoveDiscArt,
                log);

        // Every folder the edit touched, old and new. A move leaves the album's card at a folder that
        // the wall is about to stop listing. It also puts the tracks under a folder that the wall has
        // never drawn. An in-place edit leaves the card where it was, with the cover it cached the
        // first time it was shown. So the caller refreshes both ends.
        return [.. moves.Select(m => FolderOf(m.From)).Concat(moves.Select(m => m.Track.Folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        static string FolderOf(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            return slash < 0 ? "" : relativePath[..slash];
        }
    }

    /// <summary>Sends what this edit did to the record's pictures to the cloud, one album at a time:
    /// the cover under the album's key, the disc label under its own key, and a removal as a removal.
    ///
    /// <para>This runs for every album the edit touched, not only the ones whose bytes are in the
    /// cloud. A record on this disk is often on another machine too, and the sleeve just chosen is the
    /// record's sleeve there as well. The store is the only thing the two machines share.</para>
    ///
    /// <para>It is best effort. Nothing here can fail in a way that reaches the person who pressed
    /// Save. The picture is already written to the place where this machine reads it.</para>
    /// </summary>
    private static async Task PublishArtAsync(
        IReadOnlyList<(string From, LibraryTrack Track)> moves,
        byte[]? cover, byte[]? disc, bool removedCover, bool removedDisc, ILogger log)
    {
        try
        {
            var art = App.Services.GetRequiredService<Services.Cloud.CloudArtService>();

            foreach (var album in moves.Select(m => m.Track)
                         .GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase))
            {
                var first = album.First();

                await art.PublishAsync(
                    first.AlbumArtist, first.Artist, first.Album,
                    cover is { Length: > 0 }
                        ? new AlbumCover(cover, AlbumArt.SniffMime(cover), AlbumCover.SourceFolder)
                        : null,
                    disc is { Length: > 0 }
                        ? new AlbumCover(disc, AlbumArt.SniffMime(disc), AlbumCover.SourceFolder)
                        : null);

                // Remove only what was not replaced. A new picture has already taken the old one's
                // place. Deleting after storing it would leave the record with no picture.
                await art.RemoveAsync(
                    first.AlbumArtist, first.Artist, first.Album,
                    removedCover && cover is not { Length: > 0 },
                    removedDisc && disc is not { Length: > 0 });
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "[mass-edit] the artwork could not be sent to the cloud");
        }
    }

    /// <summary>How a file on disc two of a merged album is named, or null when the template already
    /// says which disc a file is on, so nothing needs working around.
    ///
    /// <para>Merged filing puts every disc of a record in one folder. A template that never mentions
    /// the disc then names disc 2, track 1 as "01 - ..." beside disc 1's "01 - ...". So the folder
    /// holds two track ones, sorts as 01, 01, 02, 02, and collides wherever two discs share a title.
    /// Import numbers the names by the merged album's running count, 1 to n across the whole set. The
    /// tags keep the standard per-disc numbers. See <see
    /// cref="MultiDiscImport.NamedInMergedAlbum"/>.</para>
    ///
    /// <para>Every edit that files a library track again must do the same. A mass edit, an album edit,
    /// or a metadata lookup over a double album renders the template from the per-disc tags. Without
    /// this, it renames disc two back to 01 to n, and undoes what import got right.</para>
    ///
    /// <para>The whole record is counted, not only the rows being edited. The discs already in the
    /// library are what disc two must continue from. So an edit that touches one disc must not renumber
    /// it as if the other discs did not exist.</para>
    /// </summary>
    private static Func<TrackInfo, TrackInfo>? MergedDiscNaming(
        LibraryService library, LibraryDatabase db, string root, NamingTemplate template)
    {
        if (template.MentionsDisc)
            return null;

        var albums = new Dictionary<(string Album, string Artist), List<TrackInfo>>();

        return track =>
        {
            if (track.DiscNumber <= 1 || string.IsNullOrWhiteSpace(track.Album))
                return track;

            var key = (track.Album!, track.AlbumArtist ?? track.Artist ?? "");
            if (!albums.TryGetValue(key, out var siblings))
            {
                siblings = [];

                // If the library does not answer, the record is treated as one being filed for the
                // first time. A refusal shows the problem screen over the app (see
                // LibraryService.Read).
                var shelvedRows = library.Read(db, "the discs already filed for a re-file", mirror =>
                        mirror.FindAlbumFolder(key.Item1, key.Item2) is { } folder
                            ? mirror.QueryUnderFolder(folder)
                            : Array.Empty<LibraryTrack>())
                    .Match(rows => rows, _ => Array.Empty<LibraryTrack>());

                foreach (var shelved in shelvedRows)
                {
                    // Only what the count needs: which disc a file is on, and how far up it sits. A
                    // library row has no track count. DiscNumbering then uses the highest track number
                    // filed under the disc, which is correct.
                    siblings.Add(new TrackInfo
                    {
                        FilePath = Path.Combine(root, shelved.RelativePath),
                        Extension = shelved.Extension,
                        DiscNumber = shelved.DiscNumber,
                        DiscCount = shelved.DiscCount,
                        TrackNumber = shelved.TrackNumber,
                    });
                }

                albums[key] = siblings;
            }

            return MultiDiscImport.NamedInMergedAlbum(track, siblings, template);
        };
    }

    // ── the file the listener is on ───────────────────────────────────────────

    /// <summary>Where the playback was when the edit took the file away. It is made only for a track
    /// that was actually sounding. See <see cref="ReleasePlayerAsync"/>.</summary>
    private sealed record PlayerHold(string Path, double Position, double Duration);

    /// <summary>Stops the player on any file this edit is about to write to, and waits for the file
    /// handle to close.
    ///
    /// <para>Stopping is not the same as letting go. The decoder unwinds on the audio thread, so the
    /// file stays held for a moment after the transport reports that it is not playing. Writing in that
    /// moment turns "fix the spelling of the song I am listening to" into "file in use". The wait is
    /// short and bounded. A file still held after it is reported as refused, like any other, and is not
    /// written to anyway.</para>
    /// </summary>
    private static async Task<PlayerHold?> ReleasePlayerAsync(IReadOnlyList<TrackRow> rows, ILogger log)
    {
        var playback = App.Services.GetService<PlaybackService>();
        if (playback is null)
            return null;

        // Only the files the player has open: the track that is playing, and the one queued behind it
        // for a gapless hand-over. See PlaybackService.HoldsOpen.
        var held = rows.Select(r => r.FilePath).Where(playback.HoldsOpen).ToList();
        if (held.Count == 0)
            return null;

        // Only a track that is SOUNDING is worth putting back. Two kinds are not:
        //
        // A queued-next track, which the queue simply picks again. A PAUSED track. IsPlaying means
        // "this is the player's current track", not "sound is coming out". So a paused track also
        // answers yes. It would be recorded as a hold and started again when the write finished, so
        // editing a selection that contains the paused track would start the music.
        //
        // So a paused track is stopped like any other, and it stays stopped. The check is made here,
        // before the write, because the stop below makes it impossible to answer afterwards.
        var playing = held.FirstOrDefault(playback.IsPlaying);
        var hold = playing is null || playback.IsPaused
            ? null
            : new PlayerHold(playing, playback.PositionSeconds, playback.DurationSeconds);

        playback.Stop();
        var stillHeld = await FileHandles.WaitUntilWritableAsync(held);
        foreach (var path in stillHeld)
            log.LogWarning("[mass-edit] {Path} is still open after the player let go — it may refuse", path);
        return hold;
    }

    /// <summary>Puts the playback back on the record that was playing, wherever the edit moved it. A
    /// record that is filed again under a corrected album artist has a new path. Playback was stopped
    /// because the listener was in the middle of it.
    ///
    /// <para>Only called with a hold, and a hold is only made for a track that was actually sounding.
    /// See <see cref="ReleasePlayerAsync"/>. Nothing here can start music that was not already
    /// playing.</para>
    /// </summary>
    private static void ResumePlayer(
        PlayerHold? hold, string root, IReadOnlyList<(string From, LibraryTrack Track)> moves, ILogger log)
    {
        if (hold is null || App.Services.GetService<PlaybackService>() is not { } playback)
            return;

        var was = Relative(root, hold.Path);
        var landed = moves.FirstOrDefault(m => string.Equals(m.From, was, StringComparison.OrdinalIgnoreCase));
        var path = landed.Track is { } track ? Path.Combine(root, track.RelativePath) : hold.Path;
        if (!File.Exists(path))
        {
            log.LogWarning("[mass-edit] the playing track is not where the edit left it — {Path}", path);
            return;
        }
        playback.Play(path, hold.Duration, hold.Position);
    }

    /// <summary>Lays the chosen edits over a file's own tags. Each field knows how it is applied (see
    /// <see cref="TagFields"/>). So there is no second table here that could fall out of step with the
    /// first.</summary>
    private static TrackInfo OverlayEdits(TrackInfo info, MassEditResult result)
    {
        foreach (var (kind, value) in result.Fields)
            info = TagFields.Get(kind).Apply(info, value);

        info = info with { TagsEdited = true };
        if (result.ArtBytes is { Length: > 0 })
            info = info with { PendingArt = result.ArtBytes, PendingArtMime = result.ArtMime };
        // An empty pending picture tells the tag writer to clear the embedded cover (see
        // TagReader.Write). The folder's own copy is removed separately, below.
        else if (result.RemoveArt)
            info = info with { PendingArt = [], PendingArtMime = null };
        return info;
    }

    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The folder part of a library-relative path. This is for a row that is filed again with
    /// no file to move. <c>FromTrackInfo</c> would read these values from the disk, and there is no
    /// disk file here.</summary>
    private static string FolderOf(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? "" : relativePath[..cut];
    }

    private static string NameOf(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? relativePath : relativePath[(cut + 1)..];
    }
}

using RPM.Core.Diagnostics;
using Microsoft.UI.Xaml;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Splitting;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The library table's own version of the three actions the import queue has: recognise a
/// track, name a selection from a release, and cut an album file into its tracks.
///
/// <para>The work is the same. It uses the same identification, the same catalogue and the same ffmpeg,
/// and that code is not written twice. Only the place where the answer is put is different. A queue row
/// is a copy from the import store, with no file of its own yet. So its answer is written back to that
/// store, and the file moves later, at import. A library row is a file that is already filed. So its
/// answer must go into the tags. The file is re-filed if the naming template now puts it somewhere else,
/// and the mirror row is pointed at the new place. <see cref="MassEdit.WriteToLibraryAsync"/> does this,
/// and every action here finishes through it.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Writes what the rows now say onto the files they stand for.
    ///
    /// <para>The dialogs fill a <see cref="TrackRow"/> in and stop. The import queue needs no more than
    /// that, which is why the dialogs can be shared. This is the step the queue does not have. It writes
    /// the tags, re-files the file under the naming template, and moves the mirror row with it.
    /// <c>TagsEdited</c> marks the row as carrying a hand-made answer, not whatever the file last said.
    /// Without it the write does nothing.</para>
    /// </summary>
    /// <returns>Every folder the write touched, for the wall to re-read.</returns>
    private static Task<IReadOnlyList<string>> WriteLibraryRowsAsync(IReadOnlyList<TrackRow> rows)
        => MassEdit.WriteHandMadeRowsAsync(rows);

    /// <summary>What a filled row means on this side of the app: tags are written into the file, the
    /// file is re-filed under the naming template, its mirror row moves with it, and the album cards
    /// are told.
    ///
    /// <para>It is safe to hand this to a window that also writes these rows, such as the album editor.
    /// The write points each row at the place its file landed, so a second write still finds every
    /// row.</para>
    /// </summary>
    private Dialogs.Models.TrackMetadataStep LibraryTrackStep(IReadOnlyList<TrackRow> rows, bool ticked)
        => Dialogs.Models.TrackMetadataStep.For(
            rows, ticked, commit: async () => RefreshAlbums(await WriteLibraryRowsAsync(rows)));

    /// <summary>Runs a menu action and makes its failure visible.
    ///
    /// <para>Every handler on this menu is an <c>async void</c>, because that is what a Click handler is.
    /// An exception thrown inside one cannot be caught by the caller. It is raised on the synchronisation
    /// context, and the window that should have opened does not open. From the user's side it is a menu
    /// entry that does nothing: no window and no error. That is the hardest kind of failure to report and
    /// to find.</para>
    ///
    /// <para>So every handler goes through here. A failure is written to the log with the whole
    /// exception, and a short message with enough detail to repeat is shown in the status bar.</para>
    /// </summary>
    private async Task GuardedAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILoggerFactory>()
               .CreateLogger("RPM.LibraryActions")
               .LogWarning(ex, "[library] {What} did not finish", what);
            Model?.SetStatus(Loc.Format("Library_ActionFailed", Faults.Note("library", what, ex).Say()));
        }
    }

    /// <summary>Tells both album views to re-read the records that a write changed. The wall keeps its
    /// cards by folder, so a card only notices a change when its folder is named here.</summary>
    private void RefreshAlbums(IEnumerable<string> folders)
    {
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AlbumWall.RefreshAlbum(folder);
            AlbumList.RefreshAlbum(folder);
        }
    }

    // ── track metadata from a release ─────────────────────────────────────────

    /// <summary>What the library already knows about the record these rows belong to: which pressing
    /// it was settled against, and the codes printed on its sleeve. Null when the rows are not all one
    /// record. The entry that uses this is then not on the menu.
    /// </summary>
    private static AlbumMeta? AlbumMetaFor(IReadOnlyList<TrackRow> rows)
    {
        var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
        // AlbumFolderOf already returns the mirror's own form: relative to the library, with forward
        // slashes. That is how album_meta is keyed. Making it relative again would ask about a folder
        // called "..".
        if (!library.IsOpen || AlbumFolderOf(rows) is not { } folder)
            return null;

        // A failed read means nothing is known. A refusal has put the problem screen up over the app.
        return library.Read(library.Db, "a record's stored meta", db => db.GetAlbumMeta(folder))
            .Match<AlbumMeta?>(meta => meta, _ => null);
    }

    // ── name tracks from a release ────────────────────────────────────────────

    /// <summary>Names the selected library tracks from a release that the user picks. This is the
    /// queue's "Name tracks from release", for files that are already filed.
    ///
    /// <para>There is one difference, and it matters. In the queue a wrong pressing costs another
    /// lookup, because the rows are only proposals until the import runs. Here the write lands on the
    /// files at once. The count shows what happened: "updated 18 of 20" tells somebody they picked the
    /// wrong pressing. The album fields are left alone in both cases. A lookup that is meant to fix
    /// twelve titles must not rename the record.</para>
    ///
    /// <para>It works on one record only. That is why the entry is not on the menu for a selection that
    /// spans folders (see LibraryTable_RowContextFlyoutOpening). A listing is matched by disc and track
    /// number, and those numbers identify a track within one record only. Track 3 of one album and
    /// track 3 of another are not two candidates for one row of a listing. They are two records. This
    /// is the opposite of the mass edit's rule, on purpose: the mass edit sets one value across
    /// whatever is selected, while this reads one record.</para>
    ///
    /// <para>If the album already knows which pressing it is, that pressing is used. This holds whichever
    /// route set it: the album editor, the album page's release picker, or an identified rip. The link is
    /// kept in album_meta. Asking the user to find it again would ignore what the app already
    /// knows.</para>
    /// </summary>
    private async void CtxLibNameTracks_Click(object sender, RoutedEventArgs e) =>
        await GuardedAsync("track metadata from release", async () =>
        {
            if (Model is not { } m || XamlRoot is null)
                return;
            if (LibraryActionRows() is not { Count: > 0 } rows)
                return;

            var known = AlbumMetaFor(rows);
            // Passed to the step, not run after it. The step is the only part that knows whether the user
            // went through with the page.
            var step = LibraryTrackStep(rows, ticked: true);

            if (await NameTracksFromReleaseAsync(rows, SettledPressing(known), known, step) is not { } result)
                return;   // closed without applying; nothing to say about that

            m.SetStatus(result.Named == 0
                ? Loc.Get("Queue_NamedNone")
                : Loc.Format("Queue_NamedTracks", result.Named, result.Total));
        });

    /// <summary>
    /// The pressing this record is already linked to, as the catalogues name it.
    ///
    /// <para>The link is what makes "Track metadata from release" a single step rather than a search. The
    /// album was settled against a pressing earlier: in the album editor, on the album page, or by an
    /// identified rip. That pressing is used. A record with no link, or with an id but no name for the
    /// catalogue it came from, has nothing to skip the search with.</para>
    /// </summary>
    private static ReleaseRef? SettledPressing(AlbumMeta? meta) =>
        meta is { ReleaseId: { Length: > 0 } id, ReleaseProvider: { Length: > 0 } provider }
            ? new ReleaseRef(provider, id)
            : null;

    // ── recognize ─────────────────────────────────────────────────────────────

    /// <summary>Recognition over library files. It uses the same dialog and the same run as the queue.
    /// The difference is the sink it is given: it writes to the shelf, not to the import store. See
    /// <see cref="RecognitionSink"/>.
    ///
    /// <para>Each row is written as soon as its answer arrives, rather than all at the end. A run over
    /// two hundred files takes minutes. It can be cancelled, or the network can fail halfway. A single
    /// write at the end would lose every name already found.</para>
    /// </summary>
    private async void CtxLibRecognize_Click(object sender, RoutedEventArgs e) =>
        await GuardedAsync("recognize", async () =>
    {
        if (Model is not { } m || XamlRoot is null)
            return;
        if (LibraryActionRows() is not { Count: > 0 } rows || !await EnsureSignedInAsync())
            return;

        var touched = new List<string>();

        await RecognizeDialog.ShowAsync(
            XamlRoot, m,
            Dialogs.SupportStanding.FromAccount(App.Services.GetRequiredService<Services.Account.SupporterService>()),
            rows,
            // No track step: recognition already writes what it finds, row by row, through its own sink.
            (editing, wantArt) => LookUpReleaseForRowsAsync(
                editing, wantArt, Dialogs.Models.TrackMetadataStep.None),
            new RecognitionSink(
                async written => touched.AddRange(await WriteLibraryRowsAsync(written)),
                // Nothing to reveal: the library table is not grouped by what a track says it is, so a
                // named row stays where the reader last saw it. The queue has that problem, and its
                // sink is where it is handled.
                (_, _) => Task.CompletedTask));

        RefreshAlbums(touched);
    });

    // ── split an album file ───────────────────────────────────────────────────

    /// <summary>Whether this library file is one whole record in a single file, so the cut should be
    /// offered.
    ///
    /// <para>The answer is checked on the file each time, not stored. The queue keeps a
    /// <c>SplitCandidate</c> that its scan probe filled in. A library row never went through that
    /// probe. It was read from the mirror. <see cref="AlbumFileDetector"/> uses the same rule as that
    /// probe. It is cheap enough to check on a right-click: it reads the length and looks for a cue
    /// sheet beside the file.</para>
    /// </summary>
    private static bool LooksLikeAlbumFile(TrackRow row)
    {
        try { return AlbumFileDetector.Detect(row.Current) is not null; }
        catch { return false; }   // an unreadable file is not a record to cut up
    }

    /// <summary>Cuts a filed album, which is one file, into its tracks. This uses the same window the
    /// queue opens, on a file that is already in the library.
    ///
    /// <para>The cut itself needs nothing new. <see cref="RPM.Core.Splitting.AlbumFileSplitter"/>
    /// writes the tracks into the source file's own folder, and it moves the album into
    /// <c>Originals/</c> next to them. For a library file, that is where both belong. What is new is
    /// the mirror. It still has one row for a file that is now somewhere else, and no rows for the new
    /// tracks. Reconciling a folder with what is really in it is the synchroniser's job, so this calls
    /// it rather than repeating that logic here. A second copy would be a thinner version that could
    /// get things wrong on its own.</para>
    ///
    /// <para>The preserved album keeps a row of its own, because <c>Originals/</c> is scanned on
    /// purpose (see AudioFileScanner). A split that came out wrong is fixed by deleting the tracks and
    /// cutting the original again. That only works if the app can see the original.</para>
    /// </summary>
    private async void CtxLibSplitAlbum_Click(object sender, RoutedEventArgs e) =>
        await GuardedAsync("split album file", async () =>
    {
        if (Model is not { } m || XamlRoot is null || _contextLibraryRow is not { } row)
            return;

        var folder = Path.GetDirectoryName(row.FilePath);

        var split = await SplitAlbumDialog.ShowAsync(
            XamlRoot, row, m.CreateSplitAnalyzer(), m.Playback, m.Catalog);
        if (split is null)
            return;

        // The files on disk changed under the mirror: one album became several tracks plus a preserved
        // original. This sync pass picks that up.
        await App.Services.GetRequiredService<Services.Library.LibraryService>().SyncAsync();

        if (folder is { Length: > 0 })
            RefreshAlbums([folder]);
    });
}

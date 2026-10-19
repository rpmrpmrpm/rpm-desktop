using RPM.Core.Diagnostics;
using System.IO;
using Windows.ApplicationModel.DataTransfer;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// How music gets into the queue: the Add-music popup, the disc and vinyl windows it opens, a scan,
/// and a folder dragged onto the window from Finder or Explorer.
///
/// <para>What happens to the queue afterwards is not here. That work is done by
/// <see cref="Presentation.Import.ImportWorkbenchView"/>. This file has the entry points only. Each one
/// opens a window of its own, and only one such window can be open at a time.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Opens the Add-music popup. Whatever ends up in the queue is a folder: one the user
    /// picked, or the staging folder a rip wrote to. So every source is handled the same way.</summary>
    private async void AddMusic_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { CanAddMusic: true } m || XamlRoot is null)
            return;

        // Rips are staged inside the library's own .rpm folder. It is on the same drive as the library,
        // so moving a ripped disc into the library is a move, not a copy across drives.
        var settings = App.Services.GetRequiredService<SettingsService>();
        if (settings.LibraryPath is not { } libraryPath)
            return;

        var staging = System.IO.Path.Combine(libraryPath, ".rpm");
        System.IO.Directory.CreateDirectory(staging);

        // A rip that is already running uses the drive. So Add music goes straight back to the disc
        // window it came from. Otherwise it would offer sources that cannot be read.
        var rips = App.Services.GetRequiredService<RipJobService>();
        if (rips.State is RipJobState.Running or RipJobState.Failed)
        {
            await ImportDiscAsync(m, rips.Source == RPM.Core.Ripping.RipSource.Dvd
                ? Dialogs.DiscKind.Dvd
                : Dialogs.DiscKind.Cd, staging, settings);
            return;
        }

        var outcome = await Dialogs.AddMusicDialog.ShowAsync(
            XamlRoot,
            App.Services.GetRequiredService<FolderPickerService>(),
            App.Services.GetRequiredService<RipperService>(),
            staging);

        // Actions that need another window come back as an action value. Only one ContentDialog can be
        // open at a time, so the popup closes first and the next dialog is opened here.
        var source = outcome.Action switch
        {
            Dialogs.AddMusicAction.Import => outcome.Folder,
            Dialogs.AddMusicAction.RipCd => await ImportDiscAsync(m, Dialogs.DiscKind.Cd, staging, settings),
            Dialogs.AddMusicAction.RipDvd => await ImportDiscAsync(m, Dialogs.DiscKind.Dvd, staging, settings),
            Dialogs.AddMusicAction.RecordVinyl => await RecordAndSplitVinylAsync(m, staging),
            Dialogs.AddMusicAction.SplitVinylSession => await SplitVinylSessionAsync(m, outcome.Folder!),
            _ => null,
        };

        if (source is not null)
            await ScanSourcesAsync([source]);
    }

    /// <summary>Scans folders into the queue and says what came of it. Every route in (the folder
    /// somebody picked, a finished rip, a drop from the file manager) goes through here. So "that
    /// folder held no music" is reported the same way whichever route it came by.
    ///
    /// <para>The scan itself cannot report this. It hands the queue what it found, and finding
    /// nothing looks the same as a scan that failed. So the count is returned instead, and a zero
    /// shows the pane's warning band. Without this, the import view would show its add-music
    /// invitation, which is what it shows when nothing has happened at all.</para>
    /// </summary>
    private async Task ScanSourcesAsync(IReadOnlyList<string> folders)
    {
        if (Model is not { CanAddMusic: true } m || folders.Count == 0)
            return;

        // The pane switches to Import before the first scan starts, not as a result of it. The queue's
        // own change (see PaneConductor.SyncQueue) also opens Import, but only once the scan is
        // running. The pane should show what is happening from the moment the scan starts.
        ClearPaneBand();
        Panes.OpenTab(LeftTab.Import);

        var found = 0;
        foreach (var folder in folders)
            found += await m.AddSourceAsync(folder);

        if (found == 0)
        {
            ShowPaneWarning(folders.Count == 1
                ? Loc.Format("Queue_ScanNoMusic", Path.GetFileName(folders[0].TrimEnd(Path.DirectorySeparatorChar)))
                : Loc.Get("Queue_ScanNoMusicMany"));
            return;
        }

        // The scan added rows, so there is a queue to work through. The workbench takes the window and
        // the library folds back to its rail. This happens only when the scan found something. An empty
        // scan has no room to give, so the library stays as it was.
        Panes.FoldLibraryForImport();
    }

    // ── music dragged onto the window ──

    /// <summary>
    /// A folder dragged in from Finder or Explorer, over anywhere on the page. Accepted as a copy
    /// while the queue can take one: the drop goes in through the same door "Add music" uses, so
    /// nothing here decides what happens to the files.
    ///
    /// <para>What was dropped cannot be read yet (on Wasm the package is sealed until the drop, and
    /// this handler runs on every pointer move besides), so the offer is made on there being storage
    /// items at all. A drop of something that turns out to hold no folder says so instead.</para>
    /// </summary>
    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (Model is not { CanAddMusic: true } || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is { } ui)
        {
            ui.Caption = Loc.Get("Queue_DropCaption");
            ui.IsCaptionVisible = true;
            ui.IsGlyphVisible = true;
        }
        e.Handled = true;
    }

    /// <summary>The drop itself. The import view opens on the dropped items, and each folder is
    /// scanned into the queue, one after another. A scan refuses to start while another one is running,
    /// so the scans cannot be started together.
    ///
    /// <para>The deferral keeps the drop open while the package is read. Reading the package is
    /// asynchronous. Without the deferral, the source app would be told the drop is over before the paths
    /// are known.</para>
    /// </summary>
    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (Model is not { CanAddMusic: true } || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        e.Handled = true;
        var deferral = e.GetDeferral();
        IReadOnlyList<string> paths;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            paths = items.Select(i => i.Path).ToList();
        }
        catch (Exception ex)
        {
            ShowPaneWarning(Faults.Note("import", "reading dropped files", ex).Say());
            return;
        }
        finally
        {
            deferral.Complete();
        }

        await ImportDroppedAsync(paths);
    }

    /// <summary>A drop from Finder on macOS. AppKit gives the paths directly (see
    /// <see cref="RPM.Desktop.Services.Platform.MacFolderDrop"/>), because Uno's macOS head raises no
    /// XAML drop event. So the window's drop target is set up here, while the page is loaded.
    /// </summary>
    private void OnNativeFolderDrop(IReadOnlyList<string> paths)
        => DispatcherQueue.TryEnqueue(async () => await ImportDroppedAsync(paths));

    /// <summary>What a drop means, on any platform. The import view opens on the dropped items, and
    /// each folder is scanned into the queue, one after another. A scan refuses to start while another
    /// one is running, so the scans cannot start together.
    /// </summary>
    private async Task ImportDroppedAsync(IReadOnlyList<string> paths)
    {
        if (Model is not { CanAddMusic: true })
            return;

        var folders = DroppedMusic.FoldersIn(paths);

        // Loose files, a folder that was removed between the drag and the drop, or an image: none of
        // these can be scanned. A drop that does nothing at all looks broken. So the pane opens on
        // Import and shows a warning there. The next drop can be made from that view.
        if (folders.Count == 0)
        {
            Panes.OpenTab(LeftTab.Import);
            ShowPaneWarning(Loc.Get("Queue_DropNeedsFolder"));
            return;
        }

        await ScanSourcesAsync(folders);
    }

    /// <summary>Opens the disc window. Nothing is returned from it. A finished rip gives its own
    /// staging folder to the queue (see the RipJobService.Finished wiring in MainPage). A rip that is
    /// still running carries on after the window is closed.</summary>
    private async Task<string?> ImportDiscAsync(MainModel m, Dialogs.DiscKind kind, string staging,
        SettingsService settings)
    {
        await Dialogs.RipDiscDialog.ShowAsync(
            XamlRoot!, kind,
            App.Services.GetRequiredService<FolderPickerService>(),
            App.Services.GetRequiredService<RipperService>(),
            m.Catalog,
            App.Services.GetRequiredService<RipJobService>(),
            staging,
            settings.DefaultTemplate);
        return null;
    }

    /// <summary>Records a vinyl session, then splits it. The recorder's output is a session folder with
    /// one file per side. Closing the dialog without recording anything is a normal cancel.</summary>
    private async Task<string?> RecordAndSplitVinylAsync(MainModel m, string staging)
    {
        var session = await Dialogs.RecordVinylDialog.ShowAsync(XamlRoot!, staging);
        return session is null ? null : await SplitVinylSessionAsync(m, session);
    }

    /// <summary>Finishes a vinyl session. It joins the sides into one album. If the join already
    /// happened in an earlier run, it is not done again. This lets an interrupted recording be resumed.
    /// Then the album goes to the normal split dialog. Splitting cuts the album in place, so the
    /// returned folder holds the new tracks. The source album stays in <c>Originals/</c>, which the
    /// queue shows as its own folder.
    /// </summary>
    private async Task<string?> SplitVinylSessionAsync(MainModel m, string sessionDir)
    {
        RPM.Core.Recording.VinylAlbum album;
        try
        {
            album = await Task.Run(() => RPM.Core.Recording.VinylImporter.PrepareSessionAsync(sessionDir));
        }
        catch (Exception ex)
        {
            m.SetStatus(Loc.Format("Vinyl_OpenFailed", Faults.Note("import", "preparing a vinyl recording", ex).Say()));
            return null;
        }

        var info = new RPM.Core.Models.TrackInfo
        {
            FilePath = album.FilePath,
            Extension = album.Extension,
            DurationSeconds = album.TotalSeconds,
        };
        // The subject stays the raw album. Everything on screen is measured from it. The restored
        // render is used only for the cut. If the render were used here, the waveform and the
        // boundaries would describe a file that does not exist until the cut is confirmed.
        var subject = new Dialogs.SplitSubject(
            album.FilePath, info,
            Candidate: new RPM.Core.Splitting.AlbumFileCandidate(
                album.FilePath, album.TotalSeconds, null, RPM.Core.Splitting.AlbumFileHint.LongDuration),
            Vinyl: new Dialogs.VinylContext(album.SideJoinSeconds));

        // The cut tracks are written to the library's default folders, so the session appears in the
        // queue already arranged the way it will be filed.
        var layout = App.Services.GetRequiredService<SettingsService>().DefaultTemplate;
        var result = await Dialogs.SplitAlbumDialog.ShowAsync(
            XamlRoot!, subject, m.CreateSplitAnalyzer(vinyl: true), m.Playback, m.Catalog, layout);
        return result is null ? null : sessionDir;   // cut tracks live in the session folder
    }
}

using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The album wall's right-click menu, page side: the live per-album state the wall builds the menu
/// from, and the work each item does. The wall stays library-agnostic, everything that touches the
/// database, the cloud, the player or navigation lives here.
/// </summary>
public sealed partial class MainPage
{
    /// <summary>The facts the menu is drawn from. They are read fresh every time the menu opens: which
    /// cloud items to offer, whether the album is a favourite, and whether it has a Discogs identity to
    /// open.</summary>
    private static AlbumMenuState BuildAlbumMenuState(LibraryService library, SettingsService settings, string folder)
    {
        if (!library.IsOpen)
            return new AlbumMenuState(false, false, false, false, false);

        // A record on somebody else's shelf. The state is read from THEIR mirror, because that mirror has
        // the album. The record is marked as borrowed, which removes every entry that would change it.
        //
        // The heart is the exception. It is not a change to their record. It is the opinion of the listener
        // at this computer, written on the row in the mirror for that computer, and never sent back. The
        // owner's own favourite mark is not in that row, so the two cannot be mixed up. A guest's listing
        // carries no favourite mark in either direction.
        if (library.IsPeerView)
        {
            var borrowed = library.Shelf.GetAlbumMeta(folder);
            return new AlbumMenuState(
                CloudSelectedMode: false,
                CanAddToCloud: false,
                IsFavorite: borrowed.IsFavorite,
                HasDiscogsId: !string.IsNullOrEmpty(borrowed.DiscogsReleaseId),
                // Their files are on their computer. There is nothing here to open, and the borrowed menu
                // leaves the entry out instead of greying it out.
                HasLocalFiles: false,
                Borrowed: true);
        }

        var db = library.Db;
        var roll = db.GetAlbumCloudRollup(folder);
        var meta = db.GetAlbumMeta(folder);
        var tracks = db.FolderTracks(folder);
        return new AlbumMenuState(
            CloudSelectedMode: settings.CloudMode == CloudSyncMode.Selected,
            CanAddToCloud: roll.CanAdd,
            IsFavorite: meta.IsFavorite,
            HasDiscogsId: !string.IsNullOrEmpty(meta.DiscogsReleaseId),
            // Some of the record is on this disk, so it has a folder to open. This reads the same rows as
            // the Delete offer. A record whose every track was offloaded still has its old folder path.
            // Opening that path would show an empty window, so the tracks' offload state is checked instead.
            HasLocalFiles: tracks.Any(t => !t.IsOffloaded),
            // Which of the three Delete entries are live. This is read from the record's own rows: where its
            // copies are, and whether this machine is set to put a deleted cloud copy straight back.
            Delete: DeleteOffer.For(
                tracks.Select(t => t.CloudState), settings.CloudMode == CloudSyncMode.All),
            // Any track with no local copy is enough to offer bringing the record down. A partly restored
            // album is the case where this offer is most useful.
            IsCloudOnly: tracks.Any(t => t.IsOffloaded),
            // Read here rather than stored, because the menu is built on every open. A store that filled up,
            // a record removed from the cloud, or a supporter plan bought a moment ago all show in the next
            // right-click. No notification is needed.
            CanUpload: App.Services.GetRequiredService<CloudSyncService>().CanUpload);
    }

    /// <summary>
    /// The menu is fire-and-forget. A click hands the work off and nothing awaits it. An exception that
    /// escaped would look like a menu item that does nothing, so anything that escapes is logged.
    /// </summary>
    private async Task RunAlbumMenuAsync(AlbumMenuCommand command, string folder)
    {
        try
        {
            await OnAlbumMenuAsync(command, folder);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainPage>>()
                .LogError(ex, "[AlbumMenu] {Command} failed on {Folder}", command, folder);
        }
    }

    private async Task OnAlbumMenuAsync(AlbumMenuCommand command, string folder)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return;
        var db = library.Db;
        var amp = App.Services.GetRequiredService<RPaMpService>();

        switch (command)
        {
            case AlbumMenuCommand.Play:
                if (await LoadAlbumTracksAsync(folder, CancellationToken.None) is { Count: > 0 } play)
                    amp.AddTracks(play, play: true);
                break;

            case AlbumMenuCommand.Shuffle:
                if (await LoadAlbumTracksAsync(folder, CancellationToken.None) is { Count: > 0 } shuffle)
                    amp.AddTracks([.. shuffle.OrderBy(_ => Random.Shared.Next())], play: true);
                break;

            case AlbumMenuCommand.PlayNext:
                if (await LoadAlbumTracksAsync(folder, CancellationToken.None) is { Count: > 0 } next)
                    amp.InsertNext(next);
                break;

            // The record becomes the set: the queue is replaced by it and the DJ mixes it. It opens on the
            // album's first track, because the planner takes the first record offered while nothing is
            // playing. The DJ chooses the order of the rest.
            case AlbumMenuCommand.StartDj:
                if (await LoadAlbumTracksAsync(folder, CancellationToken.None) is { Count: > 0 } mix)
                    amp.StartDjSet(mix);
                break;

            case AlbumMenuCommand.AddToPlaylist:
                // For now the one playlist is RPaMp's own queue. This adds to the queue without autoplay.
                // The user playlist subsystem will turn this into a real submenu later.
                if (await LoadAlbumTracksAsync(folder, CancellationToken.None) is { Count: > 0 } queue)
                    amp.AddTracks(queue, play: false);
                break;

            case AlbumMenuCommand.AddToCloud:
            {
                var cloud = App.Services.GetRequiredService<CloudSyncService>();
                await cloud.AddAlbumToCloudAsync(db.FolderTrackIds(folder));
                RefreshCloudMarks();
                break;
            }

            case AlbumMenuCommand.KeepOnComputer:
            {
                // Every track of the record that is not on this disk, handled as one piece of work. The
                // progress chip counts the whole record, not one count per track, and several files come
                // down at once. A file that fails to download does not stop the rest.
                var cloud = App.Services.GetRequiredService<CloudSyncService>();
                await cloud.RestoreAsync([.. db.FolderTracks(folder).Where(t => t.IsOffloaded)]);
                RefreshCloudMarks();
                break;
            }

            case AlbumMenuCommand.ToggleFavorite:
            {
                // Through the service, not the `db` handle above. The service writes to the shelf on
                // screen, which is a borrowed mirror while one is open. It also tells the wall, the album
                // page and the full-screen player about the mark at the same time.
                var favorite = !library.IsAlbumFavorite(folder, AlbumShelf.OnScreen);
                library.SetAlbumFavorite(folder, favorite, AlbumShelf.OnScreen);
                ApplyAlbumFavorite(folder, favorite);
                break;
            }

            case AlbumMenuCommand.WriteReview:
                await ShowAlbumReviewDialogAsync(folder);
                break;

            case AlbumMenuCommand.AlbumPage:
                NavigateToAlbumPage(folder, WhereTheReaderIs);
                break;

            // The record as the disk has it. The folder key is relative to the collection on screen, so the
            // root it is joined to is the shelf root, not the user's own library root. A record opened from
            // another of this machine's libraries is found where that library keeps it.
            case AlbumMenuCommand.ShowInFolder:
            {
                if (library.ShelfRoot is not { } root)
                    break;
                var path = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                // Checked on the disk, not in the rows. A folder moved or removed outside the library would
                // otherwise open Explorer on nothing.
                if (!Directory.Exists(path) || !RPM.Core.Files.OsReveal.OpenFolder(path))
                    App.Services.GetRequiredService<ILogger<MainPage>>()
                        .LogWarning("[AlbumMenu] Could not open {Path} in the file manager", path);
                break;
            }

            case AlbumMenuCommand.ShowInDiscogs:
            {
                var id = db.GetAlbumMeta(folder).DiscogsReleaseId;
                if (!string.IsNullOrEmpty(id))
                    await Windows.System.Launcher.LaunchUriAsync(
                        new Uri($"https://www.discogs.com/release/{id}"));
                break;
            }

            case AlbumMenuCommand.EditAlbum:
                await EditAlbumAsync(folder);
                break;

            // The three entries in the Delete submenu. The command decides which copy goes, and passes that
            // to the one delete path as a DeleteReach. There is no overload without one.
            case AlbumMenuCommand.DeleteLocal:
                await DeleteAlbumAsync(folder, DeleteReach.Local);
                break;

            case AlbumMenuCommand.DeleteCloud:
                await DeleteAlbumAsync(folder, DeleteReach.Cloud);
                break;

            case AlbumMenuCommand.DeleteEverywhere:
                await DeleteAlbumAsync(folder, DeleteReach.Everywhere);
                break;

            case AlbumMenuCommand.ShareAlbum:
                break;   // Reserved for a future social feature. The menu item is shown disabled.
        }
    }
}

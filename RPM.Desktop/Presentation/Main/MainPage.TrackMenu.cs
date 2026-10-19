using RPM.Core.Cloud;
using RPM.Core.Editing;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The right-click menu for one song, wherever songs are listed outside the library table.
/// That is the album page, the expanded records in the album list, and an artist's page on the wall.
///
/// <para>This menu is kept apart from the album menu on purpose. They act on different things: one file
/// and one record. If an album action could be used on a right-clicked song, a listener could change
/// twelve files while looking at one. If a song action could be used on a record, it would offer
/// something the record cannot do. The one crossing is "Show the album", which only navigates and
/// changes nothing.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Runs like the album menu's: a click starts the work and nothing waits for it. Anything
    /// that escapes is logged rather than lost.</summary>
    private async Task RunTrackMenuAsync(TrackMenuCommand command, TrackInfo track)
    {
        try
        {
            await OnTrackMenuAsync(command, track);
        }
        catch (Exception ex)
        {
            App.Services.GetRequiredService<ILogger<MainPage>>()
                .LogError(ex, "[TrackMenu] {Command} failed on {Path}", command, track.FilePath);
        }
    }

    private async Task OnTrackMenuAsync(TrackMenuCommand command, TrackInfo track)
    {
        var amp = App.Services.GetRequiredService<RPaMpService>();

        switch (command)
        {
            case TrackMenuCommand.Play:
                amp.AddTracks([track], play: true);
                break;

            case TrackMenuCommand.PlayNext:
                amp.InsertNext([track]);
                break;

            case TrackMenuCommand.AddToNowPlaying:
                amp.AddTracks([track], play: false);
                break;

            case TrackMenuCommand.StartDjHere:
                amp.StartDjHere(track);
                break;

            case TrackMenuCommand.StartRadio:
                amp.StartRadio(track);
                break;

            case TrackMenuCommand.EditTrack:
                await EditOneTrackAsync(track);
                break;

            case TrackMenuCommand.ShowAlbum:
                if (LibraryFolderOf(track) is { } folder)
                    NavigateToAlbumPage(folder, WhereTheReaderIs);
                break;

            case TrackMenuCommand.AddToCloud:
                if (LibraryRowOf(track) is { } adding)
                {
                    await App.Services.GetRequiredService<CloudSyncService>()
                        .AddTrackToCloudAsync(adding.Id);
                    RefreshCloudMarks();
                }
                break;

            case TrackMenuCommand.ToggleFavorite:
                SetTrackFavorite(track, !IsTrackFavorite(track));
                break;

            // The three that the Delete submenu offers, for one song. They are the same three words and
            // the same three actions, and they use the same path as the record menu and the library
            // table.
            case TrackMenuCommand.DeleteLocal:
                await DeleteTracksAsync(DeleteReach.Local, [track.FilePath]);
                break;

            case TrackMenuCommand.DeleteCloud:
                await DeleteTracksAsync(DeleteReach.Cloud, [track.FilePath]);
                break;

            case TrackMenuCommand.DeleteEverywhere:
                await DeleteTracksAsync(DeleteReach.Everywhere, [track.FilePath]);
                break;

            case TrackMenuCommand.KeepOnComputer:
                // The song's file is not on this computer. It is downloaded here, and progress is shown
                // on the deck's chip, like other cloud work. This is the album menu's entry for one
                // track, not a record.
                if (LibraryRowOf(track) is { } coming)
                {
                    await App.Services.GetRequiredService<CloudSyncService>().RestoreAsync(coming);
                    RefreshCloudMarks();
                }
                break;
        }
    }

    /// <summary>The cloud facts for one song's menu, read fresh from the mirror each time the menu
    /// opens.
    ///
    /// <para>They are read from the mirror, not from the <see cref="TrackInfo"/> in hand. That line was
    /// built when its record was drawn. A song that has finished uploading since then would otherwise
    /// still be offered a trip to the cloud.</para>
    /// </summary>
    private static TrackMenuState BuildTrackMenuState(TrackInfo track)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();

        // Another person's collection is on screen. Almost nothing here writes to it, and the menu
        // shows that by offering nothing. Their files are on their disk, and the rows an edit or a
        // delete would write to are not in this library at all.
        //
        // The heart is the one thing that still belongs to the listener, so the row is read for it
        // rather than returning an empty state. InLibrary is true because a row exists. It is in the
        // mirror held for that computer, which is where the mark is written.
        if (App.Services.GetRequiredService<LibraryService>().IsPeerView)
            return TrackMenuState.None with
            {
                Borrowed = true,
                InLibrary = LibraryRowOf(track) is not null,
                IsFavorite = IsTrackFavorite(track),
            };

        if (LibraryRowOf(track) is not { } row)
            return TrackMenuState.None;

        // The same calculation the album rollup uses. A song counts as in the set when it is chosen, or
        // when it is already in the cloud. That decides whether the song is offered a trip to the cloud
        // at all.
        var inSet = row.CloudSelected
            || row.CloudState is RPM.Core.Cloud.CloudState.Synced or RPM.Core.Cloud.CloudState.CloudOnly;
        return new TrackMenuState(
            CloudSelectedMode: settings.CloudMode == CloudSyncMode.Selected,
            CanAddToCloud: !inSet,
            // Which of the three Delete entries are live, off this one row.
            Delete: DeleteOffer.For([row.CloudState], settings.CloudMode == CloudSyncMode.All),
            CanUpload: App.Services.GetRequiredService<CloudSyncService>().CanUpload,
            // Read from the mirror, for the same reason as the cloud state. The line was built when its
            // record was drawn, and the heart may have been clicked in another view of the same song
            // since then.
            IsFavorite: row.IsFavorite,
            InLibrary: true,
            // Whether the bytes are on this disk. A song the listener deleted to make room, or one
            // another of their machines uploaded, is offered back rather than shown as a dead line.
            IsCloudOnly: row.IsOffloaded);
    }

    /// <summary>Whether the song is a favourite right now, straight off the mirror.</summary>
    private static bool IsTrackFavorite(TrackInfo track) => LibraryRowOf(track)?.IsFavorite ?? false;

    /// <summary>
    /// Writes one song's favourite mark.
    ///
    /// <para>The write is done by the library service (see
    /// <see cref="LibraryService.SetTrackFavorite"/>). That service also tells everything that shows
    /// the song to update, including surfaces this page cannot see, such as the key under the words in
    /// the full-screen player. The result comes back to <see cref="ApplyTrackFavoriteMark"/>, whichever
    /// surface the press came from.</para>
    /// </summary>
    /// <returns>False when the file is not under the library root. Nothing was written, and the mark must
    /// not be filled in over it.</returns>
    private static bool SetTrackFavorite(TrackInfo track, bool favorite) =>
        App.Services.GetRequiredService<LibraryService>().SetTrackFavorite(track.FilePath, favorite);

    /// <summary>Puts a mark that has just been written on every view of this page that shows that song:
    /// the wall's open drawer, the album list's records, the album page, and an artist's page.</summary>
    private void ApplyTrackFavoriteMark(string filePath, bool favorite)
    {
        AlbumWall.ApplyTrackFavorite(filePath, favorite);
        AlbumList.ApplyTrackFavorite(filePath, favorite);
        AlbumDetail.ApplyTrackFavorite(filePath, favorite);
        // ...and an artist's page. The same song is on their listing, and it may also be on a line
        // inside a record opened under them. A heart filled in one view and empty in another would be
        // two answers about one song.
        ArtistDetail.ApplyTrackFavorite(filePath, favorite);
        // ...and the record opened on a home screen, whose lines are the same songs again. Every home
        // this page has wired is covered: its own, and Pegasus's.
        foreach (var home in _homes)
            home.ApplyTrackFavorite(filePath, favorite);
        // The generated lists made of hearts depend on this mark, so the pane re-reads them. The
        // re-read is narrow. A full reload here would rebuild the tree and repaint whatever playlist is
        // open. For a list with no hearts on it, that would be a pane flashing for nothing. See
        // PlaylistModel.MarksChanged.
        _playlistModel?.MarksChanged();
    }

    /// <summary>The mirror row for a listed song, or null when the file is not under the library root. A
    /// file previewed from somewhere else has no row to act on.</summary>
    private static LibraryTrack? LibraryRowOf(TrackInfo track)
    {
        var library = App.Services.GetRequiredService<LibraryService>();

        // The shelf is this library's own, unless a borrowed one is open. A borrowed song has a row, in
        // the mirror held for that computer, and that is the row the heart is written on. Looked up
        // against this collection's root, no row would be found, and the menu would offer nothing.
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return null;

        var relative = Path.GetRelativePath(root, track.FilePath).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? null
            : library.Shelf.FindByRelativePath(relative);
    }

    /// <summary>The per-file editor for a track picked from a list rather than from the table. It uses
    /// the same window and the same write path: tags go into the file, the file is re-filed by the
    /// naming template, and the mirror row is re-pointed. So where the user right-clicked makes no
    /// difference to the result.</summary>
    private async Task EditOneTrackAsync(TrackInfo track)
    {
        if (XamlRoot is null)
            return;

        var row = new TrackRow(track);
        var folder = LibraryFolderOf(track);
        if (await Dialogs.TrackDetailsDialog.ShowAsync(
                XamlRoot, row,
                // No track step: one file is what this window edits, field by field.
                wantArt => LookUpReleaseForRowsAsync([row], wantArt, Dialogs.Models.TrackMetadataStep.None),
                albumFolder: Path.GetDirectoryName(track.FilePath),
                pickAlbum: wantArt => PickLibraryAlbumAsync(SeedFor(row), wantArt)) is not { } picked)
            return;

        var info = row.ToWantedTrackInfo() with { TagsEdited = true };
        var edit = new Dialogs.MassEditResult(
            new Dictionary<Dialogs.TagFieldKind, string>(), info.PendingArt, info.PendingArtMime,
            picked.FolderArtBytes);

        var touched = (await MassEdit.WriteToLibraryAsync([row], edit, _ => info)).ToList();
        touched.AddRange(await ApplyToAlbumAsync(row, info, picked.ApplyToAlbum));
        if (folder is { Length: > 0 })
            touched.Add(folder);

        foreach (var f in touched.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AlbumWall.RefreshAlbum(f);
            AlbumList.RefreshAlbum(f);
        }
    }

    /// <summary>Writes the fields that the listener switched the album marker on for to the record's
    /// OTHER tracks.
    ///
    /// <para>The file they were looking at already has those values, because it is the one they typed
    /// into. The marker says that the rest of the record is wrong about these fields. This is the
    /// repair for an album that split in two. Open the track that is right, switch the marker on, and
    /// save.</para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> ApplyToAlbumAsync(
        TrackRow edited, TrackInfo info, IReadOnlyList<Dialogs.TagFieldKind>? kinds)
    {
        if (kinds is not { Count: > 0 })
            return [];

        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root)
            return [];
        // The file may have just been re-filed under a corrected album artist. So the folder to spread
        // across is where the file is now, not where it was when the window opened.
        if (LibraryFolderOf(info with { FilePath = edited.FilePath }) is not { } folder)
            return [];

        var siblings = library.Db.FolderTracks(folder)
            .Select(t => t.ToTrackInfo(root))
            .Where(t => !string.Equals(t.FilePath, edited.FilePath, StringComparison.OrdinalIgnoreCase))
            .Select(t => new TrackRow(t))
            .ToList();
        if (siblings.Count == 0)
            return [];

        var fields = kinds.ToDictionary(k => k, k => Dialogs.TagFields.Get(k).Get(edited).Trim());
        return await MassEdit.WriteToLibraryAsync(siblings, new Dialogs.MassEditResult(fields, null, null));
    }

    /// <summary>The library-relative folder a track is in, or null when it is not under the library root.
    /// That happens for a cloud-only row, or for a file previewed from somewhere else.</summary>
    internal static string? LibraryFolderOf(TrackInfo track)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root)
            return null;
        if (Path.GetDirectoryName(track.FilePath) is not { Length: > 0 } dir)
            return null;

        var relative = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
    }

    /// <summary>Adds one song to a saved playlist. The album menu's version takes a whole record. This
    /// is the same action on the one track the user right-clicked. It asks first when the playlist
    /// already holds the song.</summary>
    private async Task AddTrackToPlaylistAsync(TrackInfo track, long playlistId)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root || _playlistModel is null)
            return;

        var relative = Path.GetRelativePath(root, track.FilePath).Replace(Path.DirectorySeparatorChar, '/');
        if (library.Db.FindByRelativePath(relative) is not { } row)
            return;

        var answer = await Dialogs.AlreadyInPlaylistPrompt.AskAsync(XamlRoot, PlaylistNameOf(playlistId),
            _playlistModel.AlreadyIn(playlistId, [row]), 1);
        if (answer is not { } whenAlreadyThere)
            return;

        _playlistModel.AddLibraryTracks(playlistId, [row], whenAlreadyThere);
    }

    /// <summary>Makes a playlist and puts this one song in it. This is the song's version of the
    /// record's "New playlist from this album".
    ///
    /// <para>The row is found BEFORE the name is asked for. A song with no row in the library cannot go
    /// in a playlist. Asking somebody to name a list that is then never made is a worse result than the
    /// menu entry doing nothing.</para>
    /// </summary>
    private async Task NewPlaylistFromTrackAsync(TrackInfo track)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root || _playlistModel is null || XamlRoot is null)
            return;

        var relative = Path.GetRelativePath(root, track.FilePath).Replace(Path.DirectorySeparatorChar, '/');
        if (library.Db.FindByRelativePath(relative) is not { } row)
            return;

        if (await PromptPlaylistNameAsync() is not { } name)
            return;

        var id = library.Db.CreatePlaylist(null, name);
        _playlistModel.AddLibraryTracks(id, [row], AlreadyInPlaylist.AddAnother);   // a new, empty playlist
    }
}

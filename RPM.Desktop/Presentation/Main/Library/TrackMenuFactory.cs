using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>What a right-click on one track can ask for.</summary>
public enum TrackMenuCommand
{
    Play,
    PlayNext,
    AddToNowPlaying,
    StartDjHere,

    /// <summary>Play the song, then keep the queue filled with songs like the one playing. See
    /// <c>RPaMpModel.Radio</c>.</summary>
    StartRadio,

    EditTrack,
    ShowAlbum,
    AddToCloud,

    /// <summary>Download a song whose file is not on this computer. The album menu does the same for a
    /// whole record. Use this to get back a file that was deleted to free space.</summary>
    KeepOnComputer,

    /// <summary>Mark the song a favourite, or remove the mark. This is the same action as the heart on
    /// its row. The album menu offers the same action for a whole record.</summary>
    ToggleFavorite,

    /// <summary>Move this one song's file to the OS trash. The cloud copy is kept, so a song that has one
    /// keeps its row and turns cyan.</summary>
    DeleteLocal,

    /// <summary>Delete this one song's cloud copy. The file here is left alone.</summary>
    DeleteCloud,

    /// <summary>Both, and the row with them.</summary>
    DeleteEverywhere,
}

/// <summary>
/// The cloud facts one song's menu is drawn from. They are read fresh every time the menu opens. This
/// is the track menu's version of what <see cref="AlbumMenuState"/> is for a record.
/// </summary>
/// <param name="CloudSelectedMode">Whether this machine is in "selected items" mode. In "all" mode the
/// song is already being uploaded. In "none" mode nothing is. So neither upload entry is offered. The
/// album menu follows the same rule.</param>
/// <param name="CanUpload">Whether the cloud store has room for this song. False greys out "add to
/// cloud". The Delete submenu is still shown, because deleting a cloud copy is how room is made.</param>
/// <param name="Delete">Which of the three Delete entries are enabled and which are greyed out. All
/// three are always shown. See <see cref="DeleteOffer"/>.</param>
/// <param name="IsFavorite">Whether the song is already a favourite. This sets the wording of the
/// entry: "add to" or "remove from". The album menu uses the same wording.</param>
/// <param name="InLibrary">Whether the song has a row in the library. A file previewed from outside
/// the library cannot be favourited, because there is no row to store the mark in.</param>
/// <param name="IsCloudOnly">The song's audio is not on this disk. This is read outside the mode
/// check, as the album menu does. Downloading a song does not depend on what this machine sends up.</param>
public sealed record TrackMenuState(
    bool CloudSelectedMode,
    bool CanAddToCloud,
    DeleteOffer Delete = default,
    bool CanUpload = true,
    bool IsFavorite = false,
    bool InLibrary = true,
    bool IsCloudOnly = false,

    /// <summary>This song is on somebody else's computer. Anything that writes to it, or to a row this
    /// library does not have, is left out.</summary>
    bool Borrowed = false)
{
    /// <summary>The state for a song outside the library. A file previewed from somewhere else has
    /// nothing to offer the cloud.</summary>
    public static readonly TrackMenuState None = new(false, false, InLibrary: false);
}

/// <summary>What a view passes to a track's right-click menu. The views do not depend on the library.
/// The page fills these in.</summary>
/// <param name="NewPlaylistFromTrack">Creates a new playlist and puts this one song in it. Every call
/// must pass it, and it is never defaulted. A view that lists tracks must decide whether its song can
/// start a playlist. The record's menu always offers this. A song that could only be added to an
/// existing playlist would be the odd one out.</param>
public sealed record TrackMenuHooks(
    Action<TrackMenuCommand, TrackInfo>? Invoked,
    Func<IReadOnlyList<(long Id, string Name, int Depth)>>? PlaylistEntries,
    Action<TrackInfo, long>? AddTrackToPlaylist,
    Action<TrackInfo>? NewPlaylistFromTrack,
    Func<TrackInfo, TrackMenuState>? StateProvider = null);

/// <summary>
/// One track's right-click menu. Every view that lists tracks outside the library table uses it: the
/// album page, the expanded records in the album list, and the opened band on the wall.
///
/// <para>Track entries only. A record's actions belong to the record's own menu (see
/// <see cref="AlbumMenuFactory"/>). Putting "Delete album" or "Edit album" on a right-clicked song
/// would let a user change twelve files while looking at one. The one shared entry is "Show the
/// album", which only navigates.</para>
/// </summary>
public static class TrackMenuFactory
{
    /// <param name="outline">The regular face, for the one outline glyph: an unfavourited heart. The
    /// album menu draws its own in the same way. A song and a record must show the same heart before
    /// it is filled in.</param>
    public static MenuFlyout Build(
        TrackInfo track, TrackMenuHooks hooks, FontFamily solid, FontFamily? outline = null)
    {
        var state = hooks.StateProvider?.Invoke(track) ?? TrackMenuState.None;
        var menu = new MenuFlyout();

        void Invoke(TrackMenuCommand cmd) => hooks.Invoked?.Invoke(cmd, track);

        menu.Items.Add(Item("AlbumMenu_Play", "\uf04b", () => Invoke(TrackMenuCommand.Play)));
        menu.Items.Add(Item("Review_CtxPlayNow", "\uf051", () => Invoke(TrackMenuCommand.PlayNext)));
        menu.Items.Add(Item("Review_CtxAddNowPlaying", "\uf8c9", () => Invoke(TrackMenuCommand.AddToNowPlaying)));

        // The fourth way to start this record: the DJ takes it and the rest of the album. It opens on
        // this track and mixes the rest in its own order. It sits with the other three because it is the
        // same decision (what plays now). It uses the same headphones icon as the playlist's auto-mix button.
        menu.Items.Add(Item("Dj_StartHere", "\uf025", () => Invoke(TrackMenuCommand.StartDjHere)));

        // A radio picks from the collection that holds the song, so a file from outside the library has nothing to
        // offer it.
        if (state.InLibrary)
            menu.Items.Add(Item("Track_StartRadio", "\uf8d7", () => Invoke(TrackMenuCommand.StartRadio)));

        // Add to playlist: every saved playlist, on this one track.
        // A playlist is made of rows in THIS library. A borrowed song has no rows, so the entry is left
        // out. Otherwise it would make a playlist that points at nothing.
        var entries = state.Borrowed
            ? Array.Empty<(long, string, int)>()
            : hooks.PlaylistEntries?.Invoke() ?? Array.Empty<(long, string, int)>();

        // The submenu is shown when there is a playlist to add to, or a way to make one. A listener with
        // no playlists still sees "New playlist" here, so a right-clicked song can always start the first
        // playlist.
        if (!state.Borrowed && (entries.Count > 0 || hooks.NewPlaylistFromTrack is not null))
        {
            var playlist = new MenuFlyoutSubItem
            {
                Text = Loc.Get("Playlists_AddToPlaylist"), Icon = Icon("\uf8c9"),
                FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
            };
            foreach (var (id, name, depth) in entries)
            {
                var playlistId = id;
                var entry = new MenuFlyoutItem
                {
                    Text = new string(' ', depth * 2) + name,
                    FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
                };
                entry.Click += (_, _) => hooks.AddTrackToPlaylist?.Invoke(track, playlistId);
                playlist.Items.Add(entry);
            }

            // Last, and set apart from the saved playlists by a rule. The entries above are places to put
            // the song. This entry makes a new place. A right-clicked record has the same entry at the foot
            // of its own playlist submenu.
            if (hooks.NewPlaylistFromTrack is { } create)
            {
                if (entries.Count > 0)
                    playlist.Items.Add(new MenuFlyoutSeparator());

                var made = new MenuFlyoutItem
                {
                    Text = Loc.Get("Playlists_NewPlaylistFrom"), Icon = Icon("\uf067"),
                    FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
                };
                made.Click += (_, _) => create(track);
                playlist.Items.Add(made);
            }

            menu.Items.Add(playlist);
        }

        // The favourite, on the one song. The wording is "add to favourites" or "remove from favourites",
        // not a tick. It says what the click will do. The record's menu and the heart on the row use the
        // same wording. Not offered for a file the library does not hold, because there is no row to store
        // the mark in.
        //
        // Offered on a borrowed song, unlike the other entries here. The mark is not a change to the song.
        // It is the opinion of the listener at THIS computer. It is written on the row in the mirror for
        // that computer and is never sent back to the owner.
        if (state.InLibrary)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var favorite = new MenuFlyoutItem
            {
                Text = Loc.Get(state.IsFavorite ? "Track_UnsetFavorite" : "Track_SetFavorite"),
                Icon = new FontIcon
                {
                    Glyph = "\uf004",   // heart
                    Style = (Style)Application.Current.Resources["FaMenuIcon"],
                    FontFamily = state.IsFavorite ? solid : outline ?? solid,
                },
                FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
            };
            favorite.Click += (_, _) => Invoke(TrackMenuCommand.ToggleFavorite);
            menu.Items.Add(favorite);
        }

        // Cloud, on the one song: the same pictures and words as the record's menu, for the same actions
        // on one file. Uploading a song only applies in "selected items" mode. Downloading one is offered
        // in every mode, because a song that is not on this disk needs to come down. Removing a song from
        // the cloud is not here. It is one of the three entries in the Delete submenu below.
        var cloud = state.Borrowed
            ? MenuCloudEntries.None
            : MenuCloudEntries.For(
                state.CloudSelectedMode, state.CanAddToCloud, state.IsCloudOnly, state.CanUpload);

        if (cloud.Any)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            // A song with no file here. The one useful action is to download it. The words and the
            // picture are the same as on the record's menu, for the one file.
            if (cloud.KeepOnComputer)
                menu.Items.Add(Item("AlbumMenu_KeepOnComputer", "\uf0ed",
                    () => Invoke(TrackMenuCommand.KeepOnComputer)));
            if (cloud.AddToCloud)
                menu.Items.Add(Item("AlbumMenu_AddToCloud", "\uf0ee",
                    () => Invoke(TrackMenuCommand.AddToCloud), enabled: cloud.AddToCloudEnabled));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        // Editing a song rewrites its file. A borrowed file is on another computer, so it cannot be edited
        // here.
        if (!state.Borrowed)
            menu.Items.Add(Item("Review_CtxEditTrack", "\uf044", () => Invoke(TrackMenuCommand.EditTrack)));

        menu.Items.Add(Item("AlbumMenu_AlbumPage", "\uf51f", () => Invoke(TrackMenuCommand.ShowAlbum)));

        // Last, alone below a rule, where the record's own menu keeps its Delete entry. This is the one
        // entry here that removes something. It is not offered on a borrowed shelf (the file is on
        // another computer). It is also not offered for a file the library does not hold, because there
        // is no row to remove and nothing to restore afterwards.
        if (state.InLibrary && !state.Borrowed)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(DeleteMenu.Build(
                state.Delete, Loc.Get("Delete_Menu"), solid,
                () => Invoke(TrackMenuCommand.DeleteLocal),
                () => Invoke(TrackMenuCommand.DeleteCloud),
                () => Invoke(TrackMenuCommand.DeleteEverywhere)));
        }

        return menu;

        MenuFlyoutItem Item(string locKey, string glyph, Action onClick, bool enabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(locKey), Icon = Icon(glyph), IsEnabled = enabled,
                FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
            };
            item.Click += (_, _) => onClick();
            return item;
        }

        // FaMenuIcon sets the glyph in a 16 by 16 box, so the template's Viewbox has nothing to stretch.
        // See the style in Generic.xaml.
        FontIcon Icon(string glyph) => new()
        {
            Glyph = glyph,
            Style = (Style)Application.Current.Resources["FaMenuIcon"],
            FontFamily = solid,
        };
    }
}

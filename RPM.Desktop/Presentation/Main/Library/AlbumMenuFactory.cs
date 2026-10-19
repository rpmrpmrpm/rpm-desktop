using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// What a view has to hand over for an album's right-click menu to work: the album's state at that
/// moment, what to do when an entry is chosen, and the playlists to offer. The views do not know
/// about the library. The page fills all of this in.
/// </summary>
public sealed record AlbumMenuHooks(
    Func<string, AlbumMenuState>? StateProvider,
    Action<AlbumMenuCommand, string>? Invoked,
    Func<IReadOnlyList<(long Id, string Name, int Depth)>>? PlaylistEntries,
    Action<string, long>? AddFolderToPlaylist,
    Action<string>? NewPlaylistFromFolder);

/// <summary>
/// Where the menu was opened. The entries cannot work this out for themselves.
///
/// <para>Every caller must state the place. There is no default, because a default would give the
/// collection's answer. A new caller that builds a menu for a record's own page would then get the
/// wrong menu without noticing. The compiler makes each caller choose.</para>
/// </summary>
public enum AlbumMenuPlace
{
    /// <summary>A record shown among others: the wall, the album list, an artist's shelves. The whole
    /// menu is shown, because every entry on it leads somewhere.</summary>
    Collection,

    /// <summary>The record's own page. Two entries are left out here: the link to the album page, which
    /// would open the page the reader is already on, and the DJ entry.</summary>
    OwnPage,
}

/// <summary>
/// One album's right-click menu, built fresh on each open. The cloud, favourite and Discogs entries
/// therefore show the album's state at that moment.
///
/// <para>Used by every view that shows albums (the wall and the album list). It is the same menu on
/// the same record, so a listener finds the same menu in each view.</para>
/// </summary>
public static class AlbumMenuFactory
{
    /// <param name="solid">The Font Awesome solid face used for the menu icons.</param>
    /// <param name="outline">The regular face, used for the one outline icon (an unfavourited heart).</param>
    /// <param name="place">Where the menu is opened. See <see cref="AlbumMenuPlace"/>. The menu is the
    /// same for both places, except that the two entries which would link to the page they were opened
    /// from are left out.</param>
    public static MenuFlyout Build(
        string folder, AlbumMenuHooks hooks, FontFamily solid, FontFamily outline, AlbumMenuPlace place)
    {
        var state = hooks.StateProvider?.Invoke(folder)
                    ?? new AlbumMenuState(false, false, false, false, false);
        var menu = new MenuFlyout();

        void Invoke(AlbumMenuCommand cmd) => hooks.Invoked?.Invoke(cmd, folder);

        // MenuCloudEntries decides which of the three cloud entries the record gets. The song menu uses
        // the same rule, so the two cannot drift apart.
        // Nothing that changes a record is offered on a borrowed record: not the cloud (the owner's
        // storage), not the tags (the owner's files), not the favourite or review (they have nowhere to
        // be saved), and not delete (it is not this app's to offer on another person's music). What is
        // left is what a borrowed record can do: play, queue and view it.
        var cloud = state.Borrowed
            ? MenuCloudEntries.None
            : MenuCloudEntries.For(
                state.CloudSelectedMode, state.CanAddToCloud, state.IsCloudOnly, state.CanUpload);

        // A record with no files on this computer. Bringing it down is the most useful entry, so it
        // comes first.
        if (cloud.KeepOnComputer)
            menu.Items.Add(Item("AlbumMenu_KeepOnComputer", "\uf0ed",
                () => Invoke(AlbumMenuCommand.KeepOnComputer)));

        // Shown but disabled while the store is full, because the record cannot be uploaded then. A
        // disabled entry tells the user about the limit. Removing the entry would hide the feature from
        // the user who most needs to know there is a limit.
        if (cloud.AddToCloud)
            menu.Items.Add(Item("AlbumMenu_AddToCloud", "\uf0ee",
                () => Invoke(AlbumMenuCommand.AddToCloud), enabled: cloud.AddToCloudEnabled));

        // Add to playlist: the Now playing queue first, then every saved playlist, then an entry to make
        // a new playlist from this album.
        var playlist = new MenuFlyoutSubItem
        {
            Text = Loc.Get("AlbumMenu_AddToPlaylist"), Icon = Icon("\uf8c9"),
            FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
        };
        playlist.Items.Add(Item("AlbumMenu_PlaylistNowPlaying", "\uf04b", () => Invoke(AlbumMenuCommand.AddToPlaylist)));

        var entries = hooks.PlaylistEntries?.Invoke() ?? Array.Empty<(long, string, int)>();
        if (entries.Count > 0)
        {
            playlist.Items.Add(new MenuFlyoutSeparator());
            foreach (var (id, name, depth) in entries)
            {
                var playlistId = id;
                var entry = new MenuFlyoutItem
                {
                    Text = new string(' ', depth * 2) + name,
                    FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
                };
                entry.Click += (_, _) => hooks.AddFolderToPlaylist?.Invoke(folder, playlistId);
                playlist.Items.Add(entry);
            }
        }

        playlist.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem
        {
            Text = Loc.Get("Playlists_NewPlaylistFrom"), Icon = Icon("\uf067"),
            FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
        };
        create.Click += (_, _) => hooks.NewPlaylistFromFolder?.Invoke(folder);
        playlist.Items.Add(create);

        menu.Items.Add(playlist);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("AlbumMenu_Play", "\uf04b", () => Invoke(AlbumMenuCommand.Play)));
        menu.Items.Add(Item("AlbumMenu_Shuffle", "\uf074", () => Invoke(AlbumMenuCommand.Shuffle)));
        menu.Items.Add(Item("AlbumMenu_PlayNext", "\uf051", () => Invoke(AlbumMenuCommand.PlayNext)));

        // The fourth way to play this record, alongside the other three. The DJ takes the whole album,
        // starts on its first track and mixes the rest in its own order. The icon matches the auto-mix
        // button on a playlist, and a right-clicked song has the same entry.
        if (place is AlbumMenuPlace.Collection)
            menu.Items.Add(Item("Dj_Start", "\uf025", () => Invoke(AlbumMenuCommand.StartDj)));

        // The separator divides the ways of playing the record from the things you can do to it. Both
        // groups are still present, so the separator stays.
        menu.Items.Add(new MenuFlyoutSeparator());

        if (place is AlbumMenuPlace.Collection)
            menu.Items.Add(Item("AlbumMenu_AlbumPage", "\uf51f", () => Invoke(AlbumMenuCommand.AlbumPage)));

        if (state.Borrowed)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("AlbumMenu_ShowInDiscogs", "\uf35d",
                () => Invoke(AlbumMenuCommand.ShowInDiscogs), enabled: state.HasDiscogsId));
            return menu;
        }

        // This entry sits next to the album page entry, because both open the record. It uses a pencil
        // icon, not the pen-square icon that every other Edit in the app uses. The review entry below
        // already uses the pen-square, and two identical icons in one menu look like a bug.
        menu.Items.Add(Item("AlbumMenu_EditAlbum", "\uf304", () => Invoke(AlbumMenuCommand.EditAlbum)));

        // A regular item, not a toggle. A checkable item makes the menu reserve a gap on the left of
        // every row for a check mark. The heart shows the state instead: filled when favourited, outline
        // when not.
        var fav = new MenuFlyoutItem
        {
            Text = Loc.Get("AlbumMenu_Favorite"),
            Icon = Icon("\uf004", useOutline: !state.IsFavorite),
            FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
        };
        fav.Click += (_, _) => Invoke(AlbumMenuCommand.ToggleFavorite);
        menu.Items.Add(fav);

        menu.Items.Add(Item("AlbumMenu_WriteReview", "\uf044", () => Invoke(AlbumMenuCommand.WriteReview)));

        menu.Items.Add(new MenuFlyoutSeparator());

        // Shows where the record is on disk. Enabled only when some of its files are on this computer.
        // A record that exists only in the cloud has a folder name but no folder. This entry sits above
        // Show in Discogs, because both answer a question: this one about the files, Discogs about the
        // pressing.
        menu.Items.Add(Item("AlbumMenu_ShowInFolder", "\uf07c",
            () => Invoke(AlbumMenuCommand.ShowInFolder), enabled: state.HasLocalFiles));

        // Show in Discogs is enabled only when the album has a Discogs id. Share is not built yet, so it
        // is disabled.
        menu.Items.Add(Item("AlbumMenu_ShowInDiscogs", "\uf35d",
            () => Invoke(AlbumMenuCommand.ShowInDiscogs), enabled: state.HasDiscogsId));
        menu.Items.Add(Item("AlbumMenu_ShareAlbum", "\uf1e0",
            () => Invoke(AlbumMenuCommand.ShareAlbum), enabled: false));

        menu.Items.Add(new MenuFlyoutSeparator());

        // Three entries, always shown. Which copy is deleted is chosen before the click, not inside a
        // dialog. See DeleteMenu.
        menu.Items.Add(DeleteMenu.Build(
            state.Delete, Loc.Get("Delete_Menu"), solid,
            () => Invoke(AlbumMenuCommand.DeleteLocal),
            () => Invoke(AlbumMenuCommand.DeleteCloud),
            () => Invoke(AlbumMenuCommand.DeleteEverywhere)));

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

        // FaMenuIcon sets the glyph to 16x16, so the template's Viewbox has nothing to stretch. The style
        // is in Generic.xaml. The face is passed in, not looked up here, because the outline heart is in
        // the regular font file, which is a control's own resource.
        FontIcon Icon(string glyph, bool useOutline = false) => new()
        {
            Glyph = glyph,
            Style = (Style)Application.Current.Resources["FaMenuIcon"],
            FontFamily = useOutline ? outline : solid,
        };
    }
}

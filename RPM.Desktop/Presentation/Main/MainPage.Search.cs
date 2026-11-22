using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Diagnostics;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;
using RPM.Desktop.Presentation.Main.Search;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Presentation.Main;

public sealed partial class MainPage
{
    /// <summary>Cmd or Ctrl+F, or the deck's search key, on the library window.
    ///
    /// <para>With the keyboard in the import workbench or the radio, that pane's own field takes it, because
    /// that is what the person is working in (<see cref="SearchTarget"/>). Everywhere else the library search
    /// opens over the whole window, in <paramref name="overlay"/>, whichever view or page is up.</para>
    ///
    /// <para>A request for a side pane's field that is still on its way is held by <see cref="FieldFocus"/>.
    /// It stays valid only while that field is still the one the rule picks.</para>
    /// </summary>
    internal void FocusSearch(Search.SearchOverlay overlay)
    {
        if (OnScreen() is { } shown)
        {
            Said.Line("search", $"shortcut: the side pane's {shown} field, the keyboard is in the side pane");
            FieldFocus.Give(FieldFor(shown), () => OnScreen() == shown);
            return;
        }

        if (Model is not { } model)
        {
            Said.Line("search", "shortcut: no library open, nothing to search");
            return;
        }

        // The playlists view is made first if nothing has asked for it yet: its lists are searched too, and
        // the lists below are read from it.
        EnsurePlaylistPanel();
        if (!ReferenceEquals(_overlay, overlay))
        {
            _overlay = overlay;
            WireChoices(overlay.Answers);
        }

        overlay.Open(SearchSources(model));
    }

    /// <summary>The search bar over the window, once its answers are wired to this page.</summary>
    private Search.SearchOverlay? _overlay;

    private SearchField? OnScreen() => SearchTarget.OnScreen(Panes.OnScreen, keyboardInSide: KeyboardIn(LeftViews));

    private TextBox FieldFor(SearchField field) => field switch
    {
        SearchField.Import => ImportWorkbench.SearchField,
        SearchField.Radio => RadioBody.SearchField,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    /// <summary>Whether the focused element is <paramref name="scope"/> or inside it.</summary>
    private bool KeyboardIn(DependencyObject scope)
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return false;

        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, scope))
                return true;

        return false;
    }

    // ── what the search looks through ──

    /// <summary>
    /// What the library search looks through, as the page holds it now.
    /// </summary>
    /// <remarks>
    /// The records, artists, genres and playlists are the lists the views already hold, handed over as
    /// they stand: the search reads them on a worker and they are replaced, never changed, when the
    /// library moves. The songs are asked of the library itself, capped, because no view holds every
    /// song.
    /// </remarks>
    private LibrarySearchSources SearchSources(MainModel model)
    {
        var index = model.AlbumIndex;
        var records = new List<SearchRecord>(index.Albums.Count);
        foreach (var folder in index.Albums)
            if (index.AlbumFacts.TryGetValue(folder, out var facts))
                records.Add(new SearchRecord(folder, facts.Album, facts.Artist,
                                             index.TrackCounts.GetValueOrDefault(folder)));

        var library = App.Services.GetRequiredService<LibraryService>();

        return new LibrarySearchSources(
            records,
            model.ArtistShelf.Acts,
            _wall ?? [],
            PlaylistsToSearch(),
            (asked, most) => SongsToSearch(library, asked, most));
    }

    /// <summary>Every list the playlists view offers by name: the ones the listener made and the
    /// generated ones, each with the folder it is filed in as its second line.</summary>
    private List<SearchList> PlaylistsToSearch()
    {
        var found = new List<SearchList>();
        if (_playlistModel is null)
            return found;

        void Walk(IEnumerable<PlaylistTreeItem> rows)
        {
            foreach (var row in rows)
            {
                if (row.Kind is PlaylistRowKind.Playlist or PlaylistRowKind.Smart)
                    found.Add(new SearchList(row, row.DisplayName, row.Parent?.DisplayName ?? ""));
                Walk(row.Children);
            }
        }

        Walk(_playlistModel.Roots);
        return found;
    }

    /// <summary>The songs the words name, from the shelf on screen (a borrowed one included), with the
    /// folder of each one's record for its picture. Runs on the search's worker.</summary>
    private static IReadOnlyList<SearchSong> SongsToSearch(LibraryService library, string asked, int most)
    {
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return [];

        return library.Read(library.Shelf, "the library search's songs", db => db.SearchSongs(asked, most))
            .Match<IReadOnlyList<SearchSong>>(
                rows => rows.Select(row => new SearchSong(row.ToTrackInfo(root), FolderOf(row.RelativePath))).ToList(),
                _ => []);
    }

    private static string FolderOf(string relativePath)
    {
        var cut = relativePath.LastIndexOf('/');
        return cut < 0 ? "" : relativePath[..cut];
    }

    /// <summary>What a chosen answer does: each opens where the same thing opens from its own view, and a
    /// song plays, as a song pressed anywhere else in the app does.</summary>
    private void WireChoices(LibrarySearchView search)
    {
        search.CoverLoader = LoadAlbumCoverAsync;

        // The bar can be opened on home, which WhereTheReaderIs does not know. Home is asked first, as for
        // the header's cover, so the page's back button returns there.
        search.RecordChosen += folder =>
            NavigateToAlbumPage(folder, Panes.AtHome ? AlbumFrom.HomeScreen : WhereTheReaderIs);

        // NavigateToArtistPage moves the rail itself.
        search.ArtistChosen += artist => NavigateToArtistPage(artist, ArtistFrom.Collection);

        // The page is built before the move is asked for, as NavigateToAlbumPage explains.
        search.GenreChosen += genre =>
        {
            NavigateToGenrePage(genre);
            Panes.OpenRightTab(RightTab.Genres);
        };

        // Home is asked here too, so backing out of a list opened over home goes back to home.
        search.PlaylistChosen += list =>
        {
            EnsurePlaylistPanel();
            var fromHome = Panes.AtHome;
            var opened = list.Kind == PlaylistRowKind.Smart
                ? PlaylistPanel.OpenSmart(list.SmartId, fromHome)
                : list.NodeId is { } node && PlaylistPanel.OpenPlaylist(node, fromHome);
            if (opened)
                Panes.OpenRightTab(RightTab.Playlists);
        };

        search.SongChosen += track =>
            App.Services.GetRequiredService<RPaMpService>().AddTracks([track], play: true);
    }
}

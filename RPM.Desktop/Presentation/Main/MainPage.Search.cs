using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Presentation.Main.RightPane;
using RPM.Desktop.Presentation.Main.Search;
using RPM.Desktop.Services.Library;

namespace RPM.Desktop.Presentation.Main;

public sealed partial class MainPage
{
    /// <summary>Cmd or Ctrl+F on the library window.
    ///
    /// <para>The fields that search one thing keep the keyboard where they are on screen: an artist's
    /// tracks, the import queue, the stations. Everything else is the library search: the box in the heading
    /// of the view on screen slides out and takes the keyboard. Where no view with one is up (home, a
    /// record's page), the Albums view is asked for and its box takes the keyboard once it is shown.</para>
    ///
    /// <para>A request for a field that is still on its way is held by <see cref="FieldFocus"/>. It stays
    /// valid only while that field is still the one on screen.</para>
    /// </summary>
    internal void FocusSearch()
    {
        if (OnScreen() is { } shown && FieldFor(shown) is { } field)
        {
            FieldFocus.Give(field, () => OnScreen() == shown);
            return;
        }

        WireLibrarySearch();
        if (SearchBoxOnScreen() is { } box)
        {
            box.Unfold();
            return;
        }

        AskForTab(RightTab.Albums);
        SearchSlot.Unfold();
        FieldFocus.Give(SearchSlot.Box, () => Panes.OnScreen.RightShowing == RightTab.Albums);
    }

    /// <summary>The library search's box in the heading on screen, or null where the view up has none
    /// (home, or a record's, an artist's or a genre's page, which cover their view's heading).</summary>
    private Search.LibrarySearchBox? SearchBoxOnScreen()
    {
        if (Home.Visibility == Visibility.Visible)
            return null;

        return Panes.OnScreen.RightShowing switch
        {
            RightTab.Albums when AlbumDetail.Visibility != Visibility.Visible => SearchSlot,
            RightTab.Artists when ArtistDetail.Visibility != Visibility.Visible => ArtistSearchSlot,
            RightTab.Genres when GenreDetail.Visibility != Visibility.Visible => GenreSearchSlot,
            RightTab.Playlists when _playlistModel is not null => PlaylistPanel.SearchBox,
            _ => null,
        };
    }

    private SearchField? OnScreen() => SearchTarget.OnScreen(
        Panes.OnScreen,
        Panes.Shown,
        albumPageUp: AlbumDetail.Visibility == Visibility.Visible,
        artistPageUp: ArtistDetail.Visibility == Visibility.Visible,
        keyboardInSide: KeyboardIn(LeftViews));

    /// <summary>The field that answers for <paramref name="field"/>, or null for the two views whose search
    /// is the library search: Albums and Artists.</summary>
    private TextBox? FieldFor(SearchField field) => field switch
    {
        SearchField.Albums or SearchField.Artists => null,
        SearchField.ArtistTracks => ArtistDetail.TrackSearch,
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

    // ── the library search ──

    private bool _librarySearchWired;

    /// <summary>The heading's box the search is open under, which folds when the search goes away.</summary>
    private Search.LibrarySearchBox? _searchingFrom;

    /// <summary>Hooks a heading's box to the one search. The playlists view's box is hooked when that view
    /// is made (EnsurePlaylistPanel).</summary>
    private void HookSearchBox(Search.LibrarySearchBox box)
    {
        box.Entered += OpenSearchUnder;
        box.Typed += (from, text) =>
        {
            // Emptied on the way out (Fold): nothing to search, and nothing to open.
            if (!LibrarySearch.IsOpen && text.Length == 0)
                return;
            if (!LibrarySearch.IsOpen || !ReferenceEquals(_searchingFrom, from))
                OpenSearchUnder(from);
            LibrarySearch.Ask(text);
        };
        box.Dismissed += _ =>
        {
            LibrarySearch.Close();
            box.Fold();
        };
        box.IntoAnswers += _ => LibrarySearch.FocusFirstAnswer();

        // The keyboard went elsewhere with nothing typed and nothing on the panel to press: the box folds.
        // Not while the panel shows the recent searches, because a press on one of them takes the keyboard
        // on its way to being a click, and folding here would take the row away under the pointer.
        box.Left += from => DispatcherQueue.TryEnqueue(() =>
        {
            if (from.Text.Length > 0 || LibrarySearch.ShowsAnything || from.Box.FocusState != FocusState.Unfocused)
                return;
            LibrarySearch.Close();
            from.Fold();
        });
    }

    /// <summary>
    /// Puts the library search up under <paramref name="box"/>, looking through the collection as it is
    /// now.
    /// </summary>
    /// <remarks>
    /// The records, artists, genres and playlists are the lists the views already hold, handed over as
    /// they stand: the search reads them on a worker and they are replaced, never changed, when the
    /// library moves. The songs are asked of the library itself, capped, because no view holds every
    /// song. The panel reaches from the box to the right edge of the heading's last control.
    /// </remarks>
    private void OpenSearchUnder(Search.LibrarySearchBox box)
    {
        if (Model is not { } model)
            return;

        if (_searchingFrom is { } other && !ReferenceEquals(other, box))
            other.Fold();
        _searchingFrom = box;

        var index = model.AlbumIndex;
        var records = new List<SearchRecord>(index.Albums.Count);
        foreach (var folder in index.Albums)
            if (index.AlbumFacts.TryGetValue(folder, out var facts))
                records.Add(new SearchRecord(folder, facts.Album, facts.Artist,
                                             index.TrackCounts.GetValueOrDefault(folder)));

        var library = App.Services.GetRequiredService<LibraryService>();

        FrameworkElement rightEdge =
            ReferenceEquals(box, SearchSlot) ? LibraryChrome
            : _playlistModel is not null && ReferenceEquals(box, PlaylistPanel.SearchBox) ? PlaylistPanel.SearchRow
            : ReferenceEquals(box, ArtistSearchSlot) ? ArtistChrome
            : ReferenceEquals(box, GenreSearchSlot) ? GenreChrome
            : box;

        LibrarySearch.Open(
            new LibrarySearchSources(
                records,
                model.ArtistShelf.Acts,
                _wall ?? [],
                PlaylistsToSearch(),
                (asked, most) => SongsToSearch(library, asked, most)),
            box,
            rightEdge);
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

    /// <summary>What a chosen answer does, set once: each opens where the same thing opens from its own
    /// view, and a song plays, as a song pressed anywhere else in the app does.</summary>
    private void WireLibrarySearch()
    {
        if (_librarySearchWired)
            return;
        _librarySearchWired = true;

        // The playlists view is made here if nothing has asked for it yet: its lists are searched too, and
        // its heading's box is hooked when it is made.
        EnsurePlaylistPanel();
        HookSearchBox(SearchSlot);
        HookSearchBox(ArtistSearchSlot);
        HookSearchBox(GenreSearchSlot);

        LibrarySearch.CoverLoader = LoadAlbumCoverAsync;

        LibrarySearch.Closed += () =>
        {
            _searchingFrom?.Fold();
            _searchingFrom = null;
        };

        LibrarySearch.RecentPicked += asked =>
        {
            if (_searchingFrom is not { } box)
                return;
            box.Text = asked;
            box.Unfold();
        };

        LibrarySearch.RecordChosen += folder => NavigateToAlbumPage(folder, WhereTheReaderIs);

        // NavigateToArtistPage moves the rail itself.
        LibrarySearch.ArtistChosen += artist => NavigateToArtistPage(artist, ArtistFrom.Collection);

        // The page is built before the move is asked for, as NavigateToAlbumPage explains.
        LibrarySearch.GenreChosen += genre =>
        {
            NavigateToGenrePage(genre);
            Panes.OpenRightTab(RightTab.Genres);
        };

        LibrarySearch.PlaylistChosen += list =>
        {
            EnsurePlaylistPanel();
            var opened = list.Kind == PlaylistRowKind.Smart
                ? PlaylistPanel.OpenSmart(list.SmartId, fromHome: false)
                : list.NodeId is { } node && PlaylistPanel.OpenPlaylist(node, fromHome: false);
            if (opened)
                Panes.OpenRightTab(RightTab.Playlists);
        };

        LibrarySearch.SongChosen += track =>
            App.Services.GetRequiredService<RPaMpService>().AddTracks([track], play: true);
    }
}

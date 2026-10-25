using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Artists and genres in Pegasus: the parts that the trail's five screens share. The screens are the
/// artists list (PegasusChrome.Artists), the genres list (PegasusChrome.Genres), one artist's page
/// (PegasusChrome.ArtistPage), one genre's page (PegasusChrome.GenrePage), and the playlists screen
/// (PegasusChrome.PlaylistsScreen), which only ever stands at the bottom of a trail of its own.
///
/// <list type="bullet">
/// <item><b>Four collection screens.</b> Each is a <see cref="CollectionScreen"/> in the collection layer,
/// as home, the wall and radio are, so a record's page opens over any of them and Back from the record
/// returns to it (see CrossToRecord and HideRecord).</item>
/// <item><b>Back follows a trail.</b> The side
/// menu's Artists or Genres starts the trail with that list. Opening a page adds a stop; Back removes one
/// and shows the stop below it, and Back on the list goes to home. A genre's page can open an artist's page
/// and another genre's page, so the trail can be several stops deep. See <see cref="TrailStop"/>.</item>
/// <item><b>The collection on screen.</b> Read off <c>library.Shelf</c>, as Pegasus's wall and record
/// page read it, so every record on these screens opens on the record page. The read is the main window's
/// own (<see cref="LibraryAlbumIndex.ReadArtistShelf"/>), done again on every visit from the side menu, so
/// a collection changed since the last visit is current.</item>
/// </list>
///
/// <para>A shelf read that lands after a newer read has started is dropped, so an older read never
/// overwrites a newer one.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>
    /// One screen on the trail Back walks. Built only through the four factories, so a page stop always
    /// carries the artist or genre it shows.
    /// </summary>
    private sealed class TrailStop
    {
        private TrailStop(CollectionScreen screen, ArtistWallItem? act, GenreWallItem? genre)
        {
            Screen = screen;
            Act = act;
            Genre = genre;
        }

        public static TrailStop Artists() => new(CollectionScreen.Artists, null, null);

        public static TrailStop Genres() => new(CollectionScreen.Genres, null, null);

        public static TrailStop Playlists() => new(CollectionScreen.Playlists, null, null);

        public static TrailStop Search() => new(CollectionScreen.Search, null, null);

        public static TrailStop OfArtist(ArtistWallItem act) => new(CollectionScreen.Artist, act, null);

        public static TrailStop OfGenre(GenreWallItem genre) => new(CollectionScreen.Genre, null, genre);

        public CollectionScreen Screen { get; }

        /// <summary>The artist an artist page stop shows. Null on every other stop.</summary>
        public ArtistWallItem? Act { get; }

        /// <summary>The genre a genre page stop shows. Null on every other stop.</summary>
        public GenreWallItem? Genre { get; }

        /// <summary>What had the focus on this screen when the next stop was opened over it. Back puts the
        /// focus there again. Null where nothing on it had the focus (the pointer was in use).</summary>
        public UIElement? Opener { get; set; }
    }

    /// <summary>The trail, bottom first. Empty while none of the four screens is up.</summary>
    private readonly List<TrailStop> _trail = [];

    /// <summary>The artists and genres side of the collection on screen, as last read. Null until the
    /// first read lands.</summary>
    private LibraryAlbumIndex.ArtistShelf? _shelf;

    /// <summary>The genres read off <see cref="_shelf"/>.</summary>
    private IReadOnlyList<GenreWallItem> _genreWall = [];

    /// <summary>The number of the read that filled <see cref="_shelf"/>. A read that finishes after a newer
    /// one has started drops what it found.</summary>
    private int _shelfRead;

    private Fader _artistsFade = null!;
    private Fader _genresFade = null!;
    private Fader _artistPageFade = null!;
    private Fader _genrePageFade = null!;

    /// <summary>Builds the parts that stand for the life of the chrome. From the constructor.</summary>
    private void BuildArtistsGenres()
    {
        _artistsFade = new Fader(ArtistsScreen);
        _genresFade = new Fader(GenresScreen);
        _artistPageFade = new Fader(ArtistPageScreen);
        _genrePageFade = new Fader(GenrePageScreen);
        BuildArtistLadder();
        BuildPlaylistsScreen();
        BuildSearchScreen();
    }

    /// <summary>The trail's screens, in the order they stand in the collection layer.</summary>
    private static readonly CollectionScreen[] ArtistsGenresScreens =
    [
        CollectionScreen.Artists, CollectionScreen.Genres, CollectionScreen.Artist, CollectionScreen.Genre,
        CollectionScreen.Playlists, CollectionScreen.Search,
    ];

    /// <summary>Whether <paramref name="screen"/> is one of the trail's screens.</summary>
    private static bool IsArtistsGenres(CollectionScreen screen) => screen is
        CollectionScreen.Artists or CollectionScreen.Genres or CollectionScreen.Artist or CollectionScreen.Genre
        or CollectionScreen.Playlists or CollectionScreen.Search;

    /// <summary>Whether one of the four is the screen in front, with nothing over it. Back's step along the
    /// trail asks this (see PegasusPlayer.TakeBack).</summary>
    public bool ArtistsGenresInFront =>
        LibraryUp && !RecordUp && !LibraryMenuOpen && IsArtistsGenres(_collection);

    /// <summary>The element that holds one of the four screens.</summary>
    private Grid ScreenOf(CollectionScreen screen) => screen switch
    {
        CollectionScreen.Artists => ArtistsScreen,
        CollectionScreen.Genres => GenresScreen,
        CollectionScreen.Artist => ArtistPageScreen,
        CollectionScreen.Playlists => PlaylistsScreen,
        CollectionScreen.Search => SearchScreen,
        _ => GenrePageScreen,
    };

    private Fader FadeOf(CollectionScreen screen) => screen switch
    {
        CollectionScreen.Artists => _artistsFade,
        CollectionScreen.Genres => _genresFade,
        CollectionScreen.Artist => _artistPageFade,
        CollectionScreen.Playlists => _playlistsFade,
        CollectionScreen.Search => _searchFade,
        _ => _genrePageFade,
    };

    // The side menu's two entries.

    /// <summary>The side menu's Artists: the trail starts again with the artists list, and the collection
    /// is read again.</summary>
    private void OpenArtistsList()
    {
        if (!LibraryUp || RecordUp)
            return;
        _trail.Clear();
        _trail.Add(TrailStop.Artists());
        ArtistsScroll.ChangeView(null, 0, null, disableAnimation: true);   // a visit from the menu starts at the top
        ShowTrailTop(focus: null);
        ReadShelfAsync();
    }

    /// <summary>The side menu's Genres: the trail starts again with the genres list, and the collection is
    /// read again.</summary>
    private void OpenGenresList()
    {
        if (!LibraryUp || RecordUp)
            return;
        _trail.Clear();
        _trail.Add(TrailStop.Genres());
        GenresScroll.ChangeView(null, 0, null, disableAnimation: true);
        ShowTrailTop(focus: null);
        ReadShelfAsync();
    }

    // Pages.

    /// <summary>Opens an artist's page over the screen in front, which becomes the stop Back returns
    /// to.</summary>
    /// <param name="opener">The card pressed, where the focus goes back to on Back.</param>
    private void OpenArtistPage(ArtistWallItem act, UIElement? opener)
    {
        if (!LibraryUp || RecordUp || _shelf is not { } shelf)
            return;
        if (_trail.Count > 0)
            _trail[^1].Opener = opener;
        _trail.Add(TrailStop.OfArtist(act));
        BuildArtistPage(act, shelf);
        ShowTrailTop(focus: null);
    }

    /// <summary>Opens a genre's page over the screen in front, which becomes the stop Back returns
    /// to.</summary>
    private void OpenGenrePage(GenreWallItem genre, UIElement? opener)
    {
        if (!LibraryUp || RecordUp || _shelf is not { } shelf)
            return;
        if (_trail.Count > 0)
            _trail[^1].Opener = opener;
        _trail.Add(TrailStop.OfGenre(genre));
        BuildGenrePage(genre, shelf);
        ShowTrailTop(focus: null);
    }

    /// <summary>
    /// Back on one of the four: the stop below on the trail, or home from the bottom of it. A page that is
    /// not the one built for its stop, such as a genre page that was opened from another genre page and
    /// then left, is built again from the stop.
    /// </summary>
    public void StepBackFromArtistsGenres()
    {
        // The search screen takes Back itself first: from its results to its field, then the field emptied.
        if (_collection == CollectionScreen.Search && SearchTakesBack())
            return;

        // The artists list's ladder takes Back first too: to the first act of the letter in hand.
        if (LadderTakesBack())
            return;

        if (_trail.Count <= 1)
        {
            _trail.Clear();
            CrossToHome();
            return;
        }

        _trail.RemoveAt(_trail.Count - 1);
        var back = _trail[^1];
        if (_shelf is { } shelf)
        {
            if (back.Act is { } act && !ReferenceEquals(_artistPageFor, act))
                BuildArtistPage(act, shelf);
            if (back.Genre is { } genre && !ReferenceEquals(_genrePageFor, genre))
                BuildGenrePage(genre, shelf);
        }
        ShowTrailTop(back.Opener);
    }

    /// <summary>
    /// Crosses to the screen at the top of the trail, from whichever collection screen is up. The cross
    /// takes ScreenCrossMs, the same time as the other cross animations. The screen being left fades out.
    /// Every screen still on the trail stays in the tree under the one in front (see
    /// <see cref="ApplyArtistsGenresShown"/>), so the card that opened a page is still there to take the
    /// focus back.
    /// </summary>
    /// <param name="focus">Where the focus goes with the remote in use, or null for the screen's own first
    /// stop.</param>
    private void ShowTrailTop(UIElement? focus)
    {
        if (!LibraryUp || RecordUp || _trail.Count == 0)
            return;

        var to = _trail[^1].Screen;
        var from = _collection;
        _collection = to;

        LayOutLibraryTop();
        PaintLibraryBack();
        SizeArtistsGenres(QueueScale);

        ScreenOf(to).Visibility = Visibility.Visible;
        FadeOf(to).To(1, ScreenCrossMs, null);
        if (from != to)
            LeaveScreen(from);
        ApplyArtistsGenresShown();

        if (to == CollectionScreen.Artists)
            EnsureArtistsFilled();
        else if (to == CollectionScreen.Genres)
            EnsureGenresBuilt();

        // A page lights the room in its own colours; a list gives the room back to whatever is playing.
        if (PageRoomInFront is { } colours)
            RoomPalette?.Invoke(colours);
        else if (from is CollectionScreen.Artist or CollectionScreen.Genre)
            RoomPalette?.Invoke(null);

        FocusScreenLater(focus);
    }

    /// <summary>
    /// The four screens' Visibility, hit testing and the rooms on the genre cards. These follow from three
    /// facts: whether the collection layer is in the tree, which screen is in front, and which screens are
    /// on the trail. This runs wherever any of them changes: through <see cref="SetLibraryLayerShown"/>,
    /// the crosses, and <see cref="LeaveScreen"/>.
    /// </summary>
    /// <remarks>
    /// A screen on the trail under the one in front stays Visible at opacity 0 rather than collapsed. So
    /// the artists list keeps its scroll and its realized cards while an artist's page is over it, and the
    /// card that opened the page can take the focus back. The screen takes no pointer input, and the
    /// remote's search leaves it out (see FocusScreen).
    /// </remarks>
    private void ApplyArtistsGenresShown()
    {
        var layer = LibraryLayer.Visibility == Visibility.Visible;
        foreach (var screen in ArtistsGenresScreens)
        {
            var front = _collection == screen;
            var kept = front || _trail.Any(stop => stop.Screen == screen);
            var grid = ScreenOf(screen);
            grid.Visibility = layer && kept ? Visibility.Visible : Visibility.Collapsed;
            grid.IsHitTestVisible = front;
        }

        var genresUp = layer && _collection == CollectionScreen.Genres;
        foreach (var card in _genreCards)
            card.Room.Shown = genresUp;
        ApplyGenrePageRooms(layer && _collection == CollectionScreen.Genre);
    }

    /// <summary>Fades out one of the four being crossed away from. It is collapsed when the fade ends,
    /// unless it was crossed back to or is still on the trail (see <see cref="ApplyArtistsGenresShown"/>).
    /// Called from <see cref="LeaveScreen"/>.</summary>
    private void LeaveArtistsGenresScreen(CollectionScreen from)
    {
        FadeOf(from).To(0, ScreenCrossMs, ApplyArtistsGenresShown);
        ScreenOf(from).IsHitTestVisible = false;

        // Leaving the four for home, the wall or radio ends the trail, and a page's colours go with it.
        if (!IsArtistsGenres(_collection))
        {
            _trail.Clear();
            if (from is CollectionScreen.Artist or CollectionScreen.Genre)
                RoomPalette?.Invoke(null);
        }
    }

    /// <summary>The collection coming up on home, the wall or radio: the four start faded out and the trail
    /// is empty. From <see cref="ShowLibrary"/>.</summary>
    private void ResetArtistsGenres()
    {
        _trail.Clear();
        _artistsFade.Set(0);
        _genresFade.Set(0);
        _artistPageFade.Set(0);
        _genrePageFade.Set(0);
        _playlistsFade.Set(0);
        _searchFade.Set(0);
        ApplyArtistsGenresShown();
    }

    /// <summary>The record is shown over the collection. The two pages drop what they built and stop their
    /// picture reads. The lists keep their cards, as the wall does.</summary>
    private void DropArtistsGenresPages()
    {
        DropArtistPage();
        DropGenrePage();
    }

    // Reading.

    /// <summary>
    /// Reads the artists and genres of the collection on screen on a worker. What is on screen stays
    /// until the answer lands; a screen built from an answer that says the same thing keeps its cards (see
    /// FillArtists and BuildGenres).
    /// </summary>
    private async void ReadShelfAsync()
    {
        var read = ++_shelfRead;
        var library = Library;
        if (!library.IsOpen)
        {
            ShelfLanded(read, LibraryAlbumIndex.ArtistShelf.Empty);
            return;
        }

        ShowArtistsGenresSpinner(_shelf is null);
        var db = library.Shelf;
        var asked = await Task.Run(() => library.Read(db, "Pegasus's artists and genres", LibraryAlbumIndex.ReadArtistShelf));
        if (read != _shelfRead)
            return;
        ShowArtistsGenresSpinner(false);

        // If the read does not answer, the screens keep what they were built from. A refusal has put the
        // problem screen up over the app. A closed library means the collection changed under the read, and
        // the next visit reads it.
        if (!asked.Answers(out var shelf, out _))
        {
            RPM.Core.Diagnostics.Scope.Said("pegasus", "artists and genres: the collection did not answer");
            return;
        }

        ShelfLanded(read, shelf);
    }

    private void ShelfLanded(int read, LibraryAlbumIndex.ArtistShelf shelf)
    {
        if (read != _shelfRead)
            return;

        _shelf = shelf;
        _genreWall = GenreWall.Build(shelf.Acts, shelf.AlbumGenres);
        RPM.Core.Diagnostics.Scope.Said(
            "pegasus", $"artists and genres read: {shelf.Acts.Count} artists, {_genreWall.Count} genres");

        if (_collection == CollectionScreen.Artists)
            FillArtists();
        else if (_collection == CollectionScreen.Genres)
            BuildGenres();
        else if (_collection == CollectionScreen.Search)
            SearchShelfLanded();
    }

    private void ShowArtistsGenresSpinner(bool spinning)
    {
        ArtistsSpinner.Spinning = spinning && _collection == CollectionScreen.Artists;
        GenresSpinner.Spinning = spinning && _collection == CollectionScreen.Genres;
    }

    // The room.

    /// <summary>The colours of the page in front, or null on a list, or on a page whose picture has not
    /// been read yet.</summary>
    private RoomColours? PageRoomInFront => _collection switch
    {
        CollectionScreen.Artist => _artistPageRoom,
        CollectionScreen.Genre => _genrePageRoom,
        _ => null,
    };

    /// <summary>A page's colours landed: the room takes them if that page is still the one in front.
    /// Nothing happens while a record's page is over it. HideRecord gives the colours back.</summary>
    private void PageRoomLanded(CollectionScreen page)
    {
        if (LibraryUp && !RecordUp && _collection == page && PageRoomInFront is { } colours)
            RoomPalette?.Invoke(colours);
    }

    // Sizes.

    /// <summary>All four at this scale. From SizeLibrary and from every cross to one of them.</summary>
    private void SizeArtistsGenres(double scale)
    {
        SizeArtists(scale);
        SizeGenres(scale);
        SizeArtistPage(scale);
        SizeGenrePage(scale);
        SizePlaylists(scale);
        SizeSearchScreen(scale);
    }

    /// <summary>A page's scroller moved: the mini player goes on a scroll down, by the rule home's and
    /// radio's keep (see Collection_Scrolled).</summary>
    private void PageScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
            return;
        var offset = scroll.VerticalOffset;
        var moved = offset - _pageScrollAt;
        _pageScrollAt = offset;
        if (IsArtistsGenres(_collection) && Math.Abs(moved) > 0.5)
            Collection_Scrolled(moved);
    }

    private double _pageScrollAt;

    /// <summary>The scroller of the one of the four in front, for the mini player's top-of-screen rule
    /// (CollectionAtTop).</summary>
    private ScrollViewer ArtistsGenresScroll => _collection switch
    {
        CollectionScreen.Artists => ArtistsScroll,
        CollectionScreen.Genres => GenresScroll,
        CollectionScreen.Artist => ArtistPageScroll,
        CollectionScreen.Playlists => PlaylistsScroll,
        CollectionScreen.Search => SearchResultsScroll,
        _ => GenrePageScroll,
    };

    // The remote.

    /// <summary>The parts of the collection layer the remote's search leaves out while one of the four is
    /// in front: the other three, which may still be in the tree under it (see
    /// <see cref="ApplyArtistsGenresShown"/>).</summary>
    private IEnumerable<UIElement> ArtistsGenresNotDrawn()
    {
        foreach (var screen in ArtistsGenresScreens)
            if (screen != _collection)
                yield return ScreenOf(screen);
    }

    /// <summary>Where the focus starts on the one of the four in front.</summary>
    private Control? ArtistsGenresFocusTarget() => _collection switch
    {
        // The search field is the target only when the shelf is read and no artists are shown. Until the
        // first read is in, focus waits for the first card, so it does not land in a field that then takes
        // the arrow keys.
        CollectionScreen.Artists => FocusManager.FindFirstFocusableElement(ArtistsWall) as Control
                                    ?? (_shelf is not null && _artistsShown.Count == 0 ? ArtistsSearch : null),
        CollectionScreen.Genres => _genreCards.Count > 0 ? _genreCards[0].Root : null,
        CollectionScreen.Artist => _artistPlayKey,
        CollectionScreen.Playlists => PlaylistsFocusTarget(),
        CollectionScreen.Search => SearchFocusTarget(),
        _ => _genrePlayKey ?? FocusManager.FindFirstFocusableElement(GenrePageContent) as Control,
    };

    /// <summary>
    /// Up and Down on a page of shelves of different lengths. When the search by layout finds nothing in
    /// the column (Down from the eighth card of a long shelf over a short one, or Up from a card under the
    /// photograph to the keys beside it), the nearest row in that direction is taken, at the card nearest
    /// across. The playlists screen keeps the same rule: its first shelf can end in a short row.
    /// </summary>
    private Control? AcrossShelves(FrameworkElement from, FocusNavigationDirection direction, Control? next, RemoteScreen screen)
    {
        if (_collection == CollectionScreen.Search)
            return AcrossSearch(from, direction, next);   // the search screen's own rule (see PegasusChrome.SearchScreen)
        if (next is not null || _collection is not (CollectionScreen.Artist or CollectionScreen.Genre or CollectionScreen.Playlists))
            return next;
        return RemoteSearch.NearestRow(from, direction, ArtistsGenresScroll, screen);
    }

    // The tick.

    /// <summary>One frame: pictures waiting for a card's row to be realized, and what is arriving late on
    /// an artist's page (see PegasusChrome.ArtistPage). From OnRendering.</summary>
    private void TickArtistsGenres()
    {
        if (LibraryUp && _collection == CollectionScreen.Artists)
        {
            PumpArtistFaces();
            TickArtistLadder();
        }
        TickArrivals();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Library;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The genres list in Pegasus: every genre the collection holds a record of, four to a row at a scale of
/// 1, each a card in the genre's own colour with its wordmark across it (see <see cref="GenreCard"/>). OK
/// on one opens the genre's page (PegasusChrome.GenrePage).
///
/// <para><b>From the desktop genres wall, for a remote.</b> The same genres in the same order
/// (<see cref="GenreWall.Build"/>: the shelves in the order the app lists them, "Other" last), the same
/// colours and the same lettering. The desktop card's photograph of an act is left out: a card here is
/// drawn the way radio's row cards are, a still room with a mark on it, which needs nothing from the
/// network and costs nothing to scroll. A genre is a few dozen cards at most, so they are built whole,
/// with no virtualizing panel.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How many genres stand in a row: four at every scale, as radio's row of wide cards
    /// has.</summary>
    private const int GenreColumns = 4;

    private static int GenreColumnsAt(double scale) => GenreColumns;

    /// <summary>The cards on the list, in order.</summary>
    private readonly List<GenreCard> _genreCards = [];

    /// <summary>The genres the cards were made from. Compared with <see cref="_genreWall"/> by reference
    /// on every visit.</summary>
    private IReadOnlyList<GenreWallItem>? _genresFrom;

    private int _genreColumnsCut;

    /// <summary>The list as it should stand on arriving at it: built again if the genres are newer than
    /// the cards.</summary>
    private void EnsureGenresBuilt()
    {
        if (_shelf is null)
            ShowGenresEmpty(null);
        else if (!ReferenceEquals(_genresFrom, _genreWall))
            BuildGenres();
    }

    /// <summary>
    /// Makes the cards from the genres, keeping the ones already made where every genre says the same as
    /// before (<see cref="GenreWallItem.SameAs"/>: the same records and the same acts), so a read that
    /// changed nothing does not redraw forty rooms under the remote.
    /// </summary>
    private void BuildGenres()
    {
        var wall = _genreWall;
        var same = _genreCards.Count == wall.Count
                   && _genreCards.Zip(wall).All(pair => pair.First.Genre.SameAs(pair.Second));
        _genresFrom = wall;
        if (same && _genreCards.Count > 0)
            return;

        foreach (var card in _genreCards)
            card.Room.Shown = false;
        _genreCards.Clear();

        var plain = (Style)Resources["PegasusPlainButton"];
        foreach (var genre in wall)
        {
            var albums = genre.Folders.Count;
            var line = albums == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", albums);
            _genreCards.Add(new GenreCard(genre, line, plain, card => OpenGenrePage(card.Genre, card.Root)));
        }

        CutGenres(GenreColumnsAt(QueueScale));
        SizeGenres(QueueScale);
        ApplyArtistsGenresShown();
        ShowGenresEmpty(wall.Count == 0 ? "Genres_Empty" : null);
    }

    /// <summary>Stands the cards in rows of <paramref name="columns"/>. The cards are the same objects;
    /// only the rows they stand in are made again.</summary>
    private void CutGenres(int columns)
    {
        _genreColumnsCut = columns;
        foreach (var row in GenresContent.Children.OfType<StackPanel>())
            row.Children.Clear();
        GenresContent.Children.Clear();

        StackPanel? open = null;
        for (var i = 0; i < _genreCards.Count; i++)
        {
            if (i % columns == 0)
            {
                open = new StackPanel { Orientation = Orientation.Horizontal };
                GenresContent.Children.Add(open);
            }
            open!.Children.Add(_genreCards[i].Root);
        }
    }

    private void ShowGenresEmpty(string? key)
    {
        GenresEmpty.Text = key is null ? "" : Loc.Get(key);
        GenresEmpty.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The cards' width and writing at this scale, and the rows cut again where the scale asks
    /// for another number to a row.</summary>
    private void SizeGenres(double scale)
    {
        GenresEmpty.FontSize = 14 * scale;

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        if (width <= 1)
            return;

        var columns = GenreColumnsAt(scale);
        if (columns != _genreColumnsCut && _genreCards.Count > 0)
            CutGenres(columns);

        var gap = 22 * scale;
        var strip = Math.Max(320, width - (LibraryEdgeInset * 2));
        var card = (strip - (gap * (columns - 1))) / columns;
        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;

        // Each card carries its own room on every side (see CardRoomAround), so the rows are inset and
        // spaced by that much less and the cards stand where the gap and the inset say.
        var room = CardRoomAround.Points * scale;
        GenresContent.Padding = new Thickness(LibraryEdgeInset - room, HomeTopPad - room, LibraryEdgeInset - room, 34 * scale);
        GenresContent.Spacing = gap - (2 * room);
        foreach (var row in GenresContent.Children.OfType<StackPanel>())
            row.Spacing = gap - (2 * room);
        foreach (var each in _genreCards)
            each.Size(card, scale, raster);
    }
}

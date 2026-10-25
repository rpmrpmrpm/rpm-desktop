using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Windows.UI;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The playlists screen in Pegasus, opened by the side menu's Playlists: the lists the collection generates
/// under "Made for you", then the listener's own lists under "Playlists", each a card with its cover, its
/// name, and under the name the folder it sits in (or how many songs it holds). OK on one opens the list's
/// page on the record screen (<see cref="ShowListRecordAsync"/>), and Back comes back here.
///
/// <para><b>The Android TV app's screen, in the drawer's order.</b> The lists are the drawer's Playlists tab
/// (<see cref="ReadPlaylists"/>): the generated lists that hold enough to be lists
/// (<see cref="SmartPlaylists.LeastToShow"/>), in the order the library window pins them, then the lists
/// somebody made, by name. The pinned Now playing node is left out, as there: it is the queue. A made list
/// with no songs is left out, as on the TV: it would open onto nothing. The cards are laid out like the album
/// wall, as many to a row (<see cref="LibraryColumnsAt"/>), with each heading across the whole width.</para>
///
/// <para><b>One of the trail's screens.</b> It stands in the collection layer beside the artists and genres
/// lists and is crossed to and from in the same way (see PegasusChrome.ArtistsGenres): the side menu starts
/// the trail with it, and Back on it goes to home.</para>
///
/// <para>Read from the collection on screen (<c>library.Shelf</c>), as the drawer's tab and the wall read
/// it, on every visit from the side menu, so a list made since the last visit is there. A read that lands
/// after a newer one has started is dropped.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>One list on the screen, as read: what opens it, where its cover comes from, and the line
    /// under its name.</summary>
    private sealed record PlaylistCardFacts(BrowseEntry Entry, ListCoverDoor Door, string Line, bool IsExplicit);

    /// <summary>The two shelves, as last read: the generated lists, then the listener's own.</summary>
    private sealed record PlaylistsRead(IReadOnlyList<PlaylistCardFacts> Generated, IReadOnlyList<PlaylistCardFacts> Own);

    private Fader _playlistsFade = null!;

    /// <summary>The number of the read that filled the screen. A read that finishes after a newer one has
    /// started drops what it found.</summary>
    private int _playlistsRead;

    /// <summary>Whether a read has landed since the chrome was built, so the screen knows the difference
    /// between "no lists" and "not read yet".</summary>
    private bool _playlistsKnown;

    /// <summary>What the cards on screen were built from, as a key: the lists' names, lines and marks in
    /// order. A read that says the same keeps the cards, so a read that lands while the remote is on the
    /// screen does not take its place away.</summary>
    private string? _playlistsBuiltFrom;

    /// <summary>The two shelves' cards, each with its heading, in the order they stand.</summary>
    private readonly List<(TextBlock Heading, List<(ShelfCard Card, ListCoverDoor Door)> Cards)> _playlistShelves = [];

    private readonly PictureQueue _playlistPictures = new(3);

    /// <summary>The number of cards to a row the shelves were last cut at.</summary>
    private int _playlistColumnsCut;

    /// <summary>Builds the parts that stand for the life of the chrome. From BuildArtistsGenres.</summary>
    private void BuildPlaylistsScreen() => _playlistsFade = new Fader(PlaylistsScreen);

    /// <summary>The side menu's Playlists: the menu closes and the collection crosses to the playlists
    /// screen.</summary>
    private void SideMenuPlaylists_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryMenu(false);
        OpenPlaylistsList();
    }

    /// <summary>The trail starts again with the playlists screen, and the lists are read again.</summary>
    private void OpenPlaylistsList()
    {
        if (!LibraryUp || RecordUp)
            return;
        _trail.Clear();
        _trail.Add(TrailStop.Playlists());
        PlaylistsScroll.ChangeView(null, 0, null, disableAnimation: true);   // a visit from the menu starts at the top
        ShowTrailTop(focus: null);
        ReadPlaylistsScreenAsync();
    }

    // ── reading ──

    /// <summary>
    /// Reads the lists on a worker. What is on screen stays until the answer lands; an answer that says the
    /// same keeps its cards (see <see cref="FillPlaylists"/>).
    /// </summary>
    private async void ReadPlaylistsScreenAsync()
    {
        var read = ++_playlistsRead;
        var library = Library;
        if (!library.IsOpen)
        {
            PlaylistsLanded(read, new PlaylistsRead([], []));
            return;
        }

        PlaylistsSpinner.Spinning = !_playlistsKnown;
        var db = library.Shelf;
        var whose = library.IsPeerView ? PlaylistShelf.Borrowed : PlaylistShelf.Own;
        var asked = await Task.Run(() => library.Read(db, "Pegasus's playlists", mirror => ReadPlaylistCards(mirror, whose)));
        if (read != _playlistsRead)
            return;
        PlaylistsSpinner.Spinning = false;

        // If the read does not answer, the screen keeps what it was built from. A refusal has put the
        // problem screen up over the app. A closed library means the collection changed under the read, and
        // the next visit reads it.
        if (!asked.Answers(out var lists, out _))
        {
            RPM.Core.Diagnostics.Scope.Said("pegasus", "playlists: the collection did not answer");
            if (!_playlistsKnown)
                ShowPlaylistsEmpty("Viz_BrowseNoPlaylists");
            return;
        }

        PlaylistsLanded(read, lists);
    }

    /// <summary>
    /// The two shelves, off the drawer's own reading of the lists (<see cref="ReadPlaylists"/>), with what a
    /// card needs that a row of the drawer does not: how many songs each holds and whether any is explicit.
    /// A generated list's totals are one query; a made list is flattened, because a list can hold other
    /// lists.
    /// </summary>
    private static PlaylistsRead ReadPlaylistCards(LibraryDatabase db, PlaylistShelf whose)
    {
        var now = DateTime.UtcNow.Ticks;
        var generated = new List<PlaylistCardFacts>();
        var own = new List<PlaylistCardFacts>();

        foreach (var entry in ReadPlaylists(db, whose).Entries)
        {
            switch (entry.Cover)
            {
                case ListCoverDoor.Generated g:
                {
                    var (tracks, _, isExplicit) = db.SmartPlaylistTotals(g.Id, now);
                    generated.Add(new PlaylistCardFacts(entry, g, SongsLine(tracks), isExplicit));
                    break;
                }
                case ListCoverDoor.Kept k:
                {
                    var tracks = entry.Tracks();
                    if (tracks.Count == 0)
                        continue;   // a list with no songs opens onto nothing
                    var isExplicit = tracks.Any(t => t.Advisory == ContentAdvisory.Explicit);
                    own.Add(new PlaylistCardFacts(entry, k, entry.Under ?? SongsLine(tracks.Count), isExplicit));
                    break;
                }
            }
        }

        return new PlaylistsRead(generated, own);

        static string SongsLine(int songs) =>
            songs == 1 ? Loc.Get("Stats_SongsOne") : Loc.Format("Stats_Songs", songs);
    }

    private void PlaylistsLanded(int read, PlaylistsRead lists)
    {
        if (read != _playlistsRead)
            return;
        _playlistsKnown = true;
        FillPlaylists(lists);
    }

    // ── building ──

    /// <summary>Builds the two shelves from what was read, unless the cards on screen were built from the
    /// same lists.</summary>
    private void FillPlaylists(PlaylistsRead lists)
    {
        var key = string.Join("\u0001", lists.Generated.Concat(lists.Own)
            .Select(l => $"{l.Entry.Name}\u0002{l.Line}\u0002{l.IsExplicit}\u0002{l.Door}"));
        if (key == _playlistsBuiltFrom && _playlistShelves.Count > 0)
            return;

        var hadFocus = FocusWithin(PlaylistsScreen);
        _playlistsBuiltFrom = key;
        _playlistPictures.Clear();
        _playlistShelves.Clear();

        var plain = (Style)Resources["PegasusPlainButton"];
        AddShelf("Home_MadeForYou", lists.Generated);
        AddShelf("Playlists_Title", lists.Own);

        _playlistColumnsCut = 0;   // cut afresh at this screen's count
        SizePlaylists(QueueScale);
        QueuePlaylistCovers();
        ShowPlaylistsEmpty(_playlistShelves.Count == 0 ? "Viz_BrowseNoPlaylists" : null);

        // The cards the focus stood on are gone. With the remote in use it goes to the first of the new ones.
        if (hadFocus && _collection == CollectionScreen.Playlists)
            FocusScreenLater(null);

        void AddShelf(string heading, IReadOnlyList<PlaylistCardFacts> shelf)
        {
            if (shelf.Count == 0)
                return;

            var title = new TextBlock
            {
                Text = Loc.Get(heading),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var cards = new List<(ShelfCard Card, ListCoverDoor Door)>(shelf.Count);
            foreach (var list in shelf)
            {
                var entry = list.Entry;
                var glyph = list.Door is ListCoverDoor.Generated g ? SmartPlaylistFace.Glyph(g.Id.Kind) : "";   // list-music
                var card = new ShelfCard(
                    entry.Name, list.Line, CardShape.Square, list.IsExplicit,
                    stand: glyph, standFont: (FontFamily)Resources["PegasusFa"], plain,
                    press: () => ShowListRecordAsync(entry));
                cards.Add((card, list.Door));
            }
            _playlistShelves.Add((title, cards));
        }
    }

    /// <summary>
    /// Stands the headings and the cards in rows of <paramref name="columns"/>. The cards are the same
    /// objects; only the rows they stand in are made again.
    /// </summary>
    private void CutPlaylists(int columns)
    {
        _playlistColumnsCut = columns;
        foreach (var row in PlaylistsContent.Children.OfType<StackPanel>())
            row.Children.Clear();
        PlaylistsContent.Children.Clear();

        foreach (var (heading, cards) in _playlistShelves)
        {
            PlaylistsContent.Children.Add(heading);
            StackPanel? open = null;
            for (var i = 0; i < cards.Count; i++)
            {
                if (i % columns == 0)
                {
                    open = new StackPanel { Orientation = Orientation.Horizontal };
                    PlaylistsContent.Children.Add(open);
                }
                open!.Children.Add(cards[i].Card.Root);
            }
        }
    }

    /// <summary>Each card's cover, the top row first (see <see cref="PictureQueue"/>): a generated list's
    /// made from the list itself (<see cref="GeneratedCover"/>), a made list's from its records or the picture
    /// chosen for it (<see cref="PlaylistCovers"/>), the doors home's cards and the list's own page draw
    /// through.</summary>
    private void QueuePlaylistCovers()
    {
        var built = _playlistsBuiltFrom;
        var shelfAt = 0;
        foreach (var (_, cards) in _playlistShelves)
        {
            for (var i = 0; i < cards.Count; i++)
            {
                var (card, door) = cards[i];
                _playlistPictures.Add(new CardPlace(shelfAt, i), async () =>
                {
                    if (built != _playlistsBuiltFrom)
                        return;
                    var bytes = door switch
                    {
                        ListCoverDoor.Generated g => await GeneratedCover.SquareAsync(g.Id, CancellationToken.None),
                        ListCoverDoor.Kept k => (await PlaylistCovers.MadeAsync(
                            k.NodeId, k.Name, k.Cover, k.Shelf, CancellationToken.None)).Cover,
                        _ => null,
                    };
                    if (bytes is not { Length: > 0 } || built != _playlistsBuiltFrom)
                        return;
                    var bitmap = await RemoteImage.DecodeArtAsync(bytes, CardDecodeEdgeFor(card.Edge));
                    if (built == _playlistsBuiltFrom)
                        card.Show(bitmap);
                });
            }
            shelfAt++;
        }
    }

    // ── the rest ──

    /// <summary>The line that stands in the screen's place, or nothing.</summary>
    private void ShowPlaylistsEmpty(string? key)
    {
        PlaylistsEmpty.Text = key is null ? "" : Loc.Get(key);
        PlaylistsEmpty.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The headings and the cards at this scale: the cards as many to a row as the album wall's covers and
    /// as wide, the headings at the size and spacing of a page's shelf headings (see ShelfRow.Size). The
    /// rows are cut again where the scale asks for another number to a row.
    /// </summary>
    private void SizePlaylists(double scale)
    {
        PlaylistsEmpty.FontSize = 22 * scale;   // the TV's couch size for a screen's message

        // Cut before the width is known: the count to a row depends on the scale alone, and cards that are
        // in no row would not be on the screen at all until the next size pass.
        var columns = LibraryColumnsAt(scale);
        if (columns != _playlistColumnsCut && _playlistShelves.Count > 0)
            CutPlaylists(columns);

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        if (width <= 1)
            return;

        // Each card carries its own room on every side (see CardRoomAround), so the rows are inset and
        // spaced by that much less and the cards stand where the gap and the inset say.
        var gap = 22 * scale;
        var room = CardRoomAround.Points * scale;
        var strip = Math.Max(320, width - (LibraryEdgeInset * 2) - LibraryScrollbarRoom);
        var card = (strip - (gap * (columns - 1))) / columns;

        PlaylistsContent.Padding = new Thickness(LibraryEdgeInset - room, HomeTopPad - room, LibraryEdgeInset - room, 34 * scale);
        PlaylistsContent.Spacing = gap - (2 * room);
        var first = true;
        foreach (var child in PlaylistsContent.Children)
        {
            switch (child)
            {
                case StackPanel row:
                    row.Spacing = gap - (2 * room);
                    break;
                case TextBlock heading:
                    // The first heading starts at the top of the content; each one after it has a shelf's
                    // gap above it. The rows' own spacing is taken off, so the gaps are the ones stated.
                    heading.FontSize = 22 * scale;
                    heading.Margin = new Thickness(room, first ? room : (30 * scale) - PlaylistsContent.Spacing, room, (12 * scale) - room - PlaylistsContent.Spacing);
                    first = false;
                    break;
            }
        }
        foreach (var (_, cards) in _playlistShelves)
            foreach (var (each, _) in cards)
                each.Size(card, scale);
    }

    /// <summary>Where the focus starts on the playlists screen: its first card.</summary>
    private Control? PlaylistsFocusTarget() =>
        _playlistShelves.Count > 0 && _playlistShelves[0].Cards.Count > 0 ? _playlistShelves[0].Cards[0].Card.Root : null;

    /// <summary>Whether the screen has been read at least once: what the view's opening cover would wait on
    /// if the collection ever opened on it.</summary>
    private bool PlaylistsAreRead => _playlistsKnown;
}

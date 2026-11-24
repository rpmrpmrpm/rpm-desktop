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
/// The playlists screen in Pegasus, opened by the side menu's Playlists. It is browsed by folder, as on the
/// phones: each card is a list or a folder. OK on a list opens the list's page on the record screen (<see
/// cref="ShowListRecordAsync"/>), and Back comes back here. OK on a folder opens it in place, and Back goes up
/// again. The folder shown fades out and the new one fades in.
///
/// <para><b>What stands where.</b> At the top, under "Made for you", the folder of the moods and then the
/// lists about listening (Your mix, On repeat and the others); under "Playlists", the listener's own folders
/// and then their own lists, each by name. The folder of the decades is the first thing in the moods folder,
/// as on the phones. A mood that is cut by genre is a folder of its own there, with the whole mood's list
/// first and its cuts after it; a mood that is not cut is a list. A list with no songs and a folder with
/// none under it are left out: they would open onto nothing. Which generated lists there are is the library
/// window's own rule (<see cref="LibraryDatabase.OfferedGeneratedLists"/>, and
/// <see cref="SmartPlaylists.LeastToShow"/> for which hold enough to be lists).</para>
///
/// <para><b>A folder's card</b> wears the folder frame with its name in it (<see cref="FolderCover"/>), over
/// its own photograph, the picture chosen for it, or a still room, and "Folder" under its name. The picture and
/// the frame come in together.</para>
///
/// <para><b>One of the trail's screens.</b> It stands in the collection layer beside the artists and genres
/// lists and is crossed to and from in the same way (see PegasusChrome.ArtistsGenres): the side menu starts
/// the trail with it at the top of the tree, and Back at the top goes to home.</para>
///
/// <para>Read from the collection on screen (<c>library.Shelf</c>), as the wall reads it, on every visit from
/// the side menu, so a list made since the last visit is there. A read that lands after a newer one has
/// started is dropped.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>One card on the screen: a list, or a folder.</summary>
    private abstract record PlaylistsCard;

    /// <summary>One list, as read: what opens it, where its cover comes from, and the line under its
    /// name.</summary>
    private sealed record ListCard(BrowseEntry Entry, ListCoverDoor Door, string Line, bool IsExplicit)
        : PlaylistsCard;

    /// <summary>One folder, as read: what it holds and what its cover shows.</summary>
    /// <param name="Place">What the folder is known by across reads: "node:" and its id for one somebody made,
    /// "moods", "decades" or "mood:" and the kind for one of the app's own.</param>
    /// <param name="NodeId">The stored folder, for the picture somebody chose for it. Null on the app's own
    /// folders, which are not stored.</param>
    /// <param name="Inside">The shelves the folder opens onto.</param>
    private sealed record FolderCard(
        string Place, string Name, FolderFace Face, long? NodeId, PlaylistCover Cover, PlaylistShelf Shelf,
        IReadOnlyList<PlaylistsShelf> Inside) : PlaylistsCard;

    /// <summary>One shelf of cards under its heading.</summary>
    private sealed record PlaylistsShelf(string Heading, IReadOnlyList<PlaylistsCard> Cards);

    /// <summary>The whole tree, as last read: the shelves at the top, and through the folders everything
    /// under them.</summary>
    private sealed record PlaylistsRead(IReadOnlyList<PlaylistsShelf> Top);

    private Fader _playlistsFade = null!;

    /// <summary>The number of the read that filled the screen. A read that finishes after a newer one has
    /// started drops what it found.</summary>
    private int _playlistsRead;

    /// <summary>Whether a read has landed since the chrome was built, so the screen knows the difference
    /// between "no lists" and "not read yet".</summary>
    private bool _playlistsKnown;

    /// <summary>The tree as last read. The folders are opened from it, with no read.</summary>
    private PlaylistsRead? _playlistsTree;

    /// <summary>The folders the screen stands in, outermost first, by <see cref="FolderCard.Place"/>. Empty
    /// at the top of the tree.</summary>
    private readonly List<string> _playlistsAt = [];

    /// <summary>The number of the last folder change. A fade that lands after a newer change has started
    /// does nothing.</summary>
    private int _playlistsCross;

    /// <summary>How long the folder shown takes to fade out, and the new one to fade in, as on the
    /// phones.</summary>
    private const int FolderFadeOutMs = 140;
    private const int FolderFadeInMs = 220;

    /// <summary>What the cards on screen were built from, as a key: where the screen stands, and the cards'
    /// names, lines and marks in order. A read that says the same keeps the cards, so a read that lands while
    /// the remote is on the screen does not take its place away.</summary>
    private string? _playlistsBuiltFrom;

    /// <summary>The shelves' cards, each with its heading, in the order they stand.</summary>
    private readonly List<(TextBlock Heading, List<(ShelfCard Card, PlaylistsCard Facts)> Cards)> _playlistShelves = [];

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

    /// <summary>The trail starts again with the playlists screen at the top of the tree, and the lists are
    /// read again.</summary>
    private void OpenPlaylistsList()
    {
        if (!LibraryUp || RecordUp)
            return;
        _trail.Clear();
        _trail.Add(TrailStop.Playlists());
        _playlistsCross++;   // a folder fade that is still running lands on nothing
        if (_playlistsAt.Count > 0)
        {
            _playlistsAt.Clear();
            if (_playlistsTree is { } tree)
                FillPlaylists(tree, focusKey: null);
        }
        PlaylistsScroll.ChangeView(null, 0, null, disableAnimation: true);   // a visit from the menu starts at the top
        ShowTrailTop(focus: null);
        ReadPlaylistsScreenAsync();
    }

    // ── folders ──

    /// <summary>OK on a folder's card: the folder opens in place of the one shown.</summary>
    private void OpenPlaylistFolder(string key)
    {
        if (_collection != CollectionScreen.Playlists || _playlistsTree is null)
            return;
        _playlistsAt.Add(key);
        CrossPlaylistsFolder(focusKey: null);
    }

    /// <summary>
    /// Back on the playlists screen inside a folder: the folder that holds it is shown, with the focus on the
    /// card of the folder that was left. False at the top of the tree, where Back goes on to home.
    /// </summary>
    private bool PlaylistsTakesBack()
    {
        if (_collection != CollectionScreen.Playlists || _playlistsAt.Count == 0 || _playlistsTree is null)
            return false;
        var left = _playlistsAt[^1];
        _playlistsAt.RemoveAt(_playlistsAt.Count - 1);
        CrossPlaylistsFolder(focusKey: left);
        return true;
    }

    /// <summary>The folder shown fades out, the screen is built again where it now stands, and the new
    /// folder fades in.</summary>
    /// <param name="focusKey">The folder whose card takes the focus with the remote in use, or null for
    /// the first card.</param>
    private void CrossPlaylistsFolder(string? focusKey)
    {
        var cross = ++_playlistsCross;
        _playlistsFade.To(0, FolderFadeOutMs, () =>
        {
            if (cross != _playlistsCross || _collection != CollectionScreen.Playlists || _playlistsTree is not { } tree)
                return;
            PlaylistsScroll.ChangeView(null, 0, null, disableAnimation: true);
            FillPlaylists(tree, focusKey);
            _playlistsFade.To(1, FolderFadeInMs, null);
        });
    }

    /// <summary>
    /// The shelves where the screen stands, and where that is. A folder that is no longer in the tree (it
    /// was deleted, or emptied) is left, and the screen stands in the nearest folder above it that still is.
    /// </summary>
    private IReadOnlyList<PlaylistsShelf> PlaylistsHere(PlaylistsRead tree)
    {
        var shelves = tree.Top;
        for (var depth = 0; depth < _playlistsAt.Count; depth++)
        {
            var key = _playlistsAt[depth];
            if (shelves.SelectMany(s => s.Cards).OfType<FolderCard>().FirstOrDefault(f => f.Place == key) is not { } folder)
            {
                _playlistsAt.RemoveRange(depth, _playlistsAt.Count - depth);
                break;
            }
            shelves = folder.Inside;
        }
        return shelves;
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
            PlaylistsLanded(read, new PlaylistsRead([]));
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
    /// The tree: the generated lists in the folders the library window files them in, and the listener's own
    /// folders and lists, with what a card needs that a row of the drawer does not (how many songs each list
    /// holds and whether any is explicit). A generated list's totals are one query; a made list is flattened,
    /// because a list can hold other lists.
    /// </summary>
    private static PlaylistsRead ReadPlaylistCards(LibraryDatabase db, PlaylistShelf whose)
    {
        var top = new List<PlaylistsShelf>();

        if (MadeForYou(db, whose) is { Count: > 0 } generated)
            top.Add(new PlaylistsShelf(Loc.Get("Home_MadeForYou"), generated));

        if (OwnPlaces(db, whose) is { Count: > 0 } own)
            top.Add(new PlaylistsShelf(Loc.Get("Playlists_Title"), own));

        return new PlaylistsRead(top);
    }

    /// <summary>
    /// The generated lists, as the top of the tree holds them: the folder of the moods (with the decades
    /// inside it), then the lists about listening. A shelf with no moods has the decades' folder at the top
    /// instead.
    /// </summary>
    private static List<PlaylistsCard> MadeForYou(LibraryDatabase db, PlaylistShelf whose)
    {
        var now = DateTime.UtcNow.Ticks;
        var borrowed = whose == PlaylistShelf.Borrowed;
        var kinds = borrowed ? SmartPlaylists.OnABorrowedShelf : SmartPlaylists.All;

        // The library window's own offer and its own floor, so the two show the same lists.
        var shown = new List<(SmartPlaylistId Id, ListCard Card)>();
        foreach (var id in db.OfferedGeneratedLists(kinds, withDecades: !borrowed))
        {
            var (tracks, _, isExplicit) = db.SmartPlaylistTotals(id, now);
            if (tracks < SmartPlaylists.LeastToShow(id))
                continue;
            shown.Add((id, new ListCard(
                GeneratedListEntry(db, id), new ListCoverDoor.Generated(id), SongsLine(tracks), isExplicit)));
        }

        var decades = shown.Where(s => s.Id.IsWithinDecade).Select(s => (PlaylistsCard)s.Card).ToList();
        var decadesFolder = decades.Count == 0
            ? null
            : AppFolder("decades", SmartPlaylistFace.DecadesFolderName, FolderFace.Decades, decades);

        // A mood's cuts are drawn only under a mood whose whole list is drawn, as in the library window.
        var moodFolders = new List<PlaylistsCard>();
        var moodLists = new List<PlaylistsCard>();
        foreach (var (id, card) in shown.Where(s => SmartPlaylists.Moods.Contains(s.Id.Kind)
                                                    && s.Id.Genre is null && !s.Id.IsWithinDecade))
        {
            var cuts = shown.Where(s => s.Id.Kind == id.Kind && s.Id.IsWithinGenre)
                            .Select(s => s.Card)
                            .OrderBy(c => c.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
            if (cuts.Count == 0)
            {
                moodLists.Add(card);
                continue;
            }

            // The whole mood first, then its cuts by name: the row somebody opening the folder came for.
            moodFolders.Add(AppFolder("mood:" + id.Kind, card.Entry.Name, FolderFace.OfMood(id.Kind),
                                      [card, .. cuts]));
        }

        var made = new List<PlaylistsCard>();
        if (moodFolders.Count > 0 || moodLists.Count > 0)
        {
            List<PlaylistsCard> moods = [];
            if (decadesFolder is not null)
                moods.Add(decadesFolder);
            moods.AddRange(moodFolders);
            moods.AddRange(moodLists);
            made.Add(AppFolder("moods", SmartPlaylistFace.MoodsFolderName, FolderFace.Moods, moods));
        }
        else if (decadesFolder is not null)
        {
            made.Add(decadesFolder);
        }

        // The lists about listening stand loose at the top, as on the phones.
        made.AddRange(shown.Where(s => !SmartPlaylists.Moods.Contains(s.Id.Kind) && !s.Id.IsWithinDecade
                                       && s.Id.Genre is null)
                           .Select(s => s.Card));
        return made;

        static FolderCard AppFolder(string key, string name, FolderFace face, List<PlaylistsCard> inside) =>
            new(key, name, face, null, new PlaylistCover.Automatic(), PlaylistShelf.Own,
                [new PlaylistsShelf(name, inside)]);
    }

    /// <summary>
    /// The listener's own folders and lists at the top of their tree: the folders first, then the lists, each
    /// by name, because the tree's own order means little on a wall of cards. Every folder holds the same,
    /// one level down.
    /// </summary>
    private static List<PlaylistsCard> OwnPlaces(LibraryDatabase db, PlaylistShelf whose)
    {
        var nodes = db.GetPlaylistNodes();
        var byId = nodes.ToDictionary(n => n.Id);
        var children = nodes.Where(n => !n.IsNowPlaying)
                            .ToLookup(n => n.ParentId is { } parent && byId.ContainsKey(parent) ? parent : (long?)null);

        return Place(null, 0);

        List<PlaylistsCard> Place(long? at, int depth)
        {
            var folders = new List<PlaylistsCard>();
            var lists = new List<PlaylistsCard>();

            // Bounded by a fixed depth. A parent link that pointed back up the chain would otherwise loop.
            if (depth >= 32)
                return folders;

            foreach (var node in children[at].OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (node.Kind == PlaylistNodeKind.Folder)
                {
                    var inside = Place(node.Id, depth + 1);
                    if (inside.Count == 0)
                        continue;   // a folder with no songs anywhere under it opens onto nothing
                    folders.Add(new FolderCard(
                        "node:" + node.Id, node.Name, FolderFace.Plain, node.Id, node.Cover, whose,
                        [new PlaylistsShelf(node.Name, inside)]));
                }
                else if (node.Kind == PlaylistNodeKind.Playlist)
                {
                    var entry = StoredListEntry(db, node, byId, whose);
                    var tracks = entry.Tracks();
                    if (tracks.Count == 0)
                        continue;   // a list with no songs opens onto nothing
                    var isExplicit = tracks.Any(t => t.Advisory == ContentAdvisory.Explicit);
                    lists.Add(new ListCard(entry, (ListCoverDoor.Kept)entry.Cover!, SongsLine(tracks.Count), isExplicit));
                }
            }

            folders.AddRange(lists);
            return folders;
        }
    }

    private static string SongsLine(int songs) =>
        songs == 1 ? Loc.Get("Stats_SongsOne") : Loc.Format("Stats_Songs", songs);

    private void PlaylistsLanded(int read, PlaylistsRead lists)
    {
        if (read != _playlistsRead)
            return;
        _playlistsKnown = true;
        _playlistsTree = lists;
        FillPlaylists(lists, focusKey: null);
    }

    // ── building ──

    /// <summary>Builds the shelves where the screen stands, unless the cards on screen were built from the
    /// same lists in the same place.</summary>
    /// <param name="focusKey">The folder whose card takes the focus with the remote in use, or null for the
    /// screen's first card.</param>
    private void FillPlaylists(PlaylistsRead lists, string? focusKey)
    {
        var here = PlaylistsHere(lists);
        var key = string.Join("\u0003", _playlistsAt) + "\u0004" + string.Join("\u0001", here.Select(s =>
            s.Heading + "\u0005" + string.Join("\u0001", s.Cards.Select(Described))));
        if (key == _playlistsBuiltFrom && _playlistShelves.Count > 0)
            return;

        var hadFocus = FocusWithin(PlaylistsScreen);
        _playlistsBuiltFrom = key;
        _playlistPictures.Clear();
        _playlistShelves.Clear();

        var plain = (Style)Resources["PegasusPlainButton"];
        var folderLine = Loc.Get("Playlists_RowFolder");
        Control? focusCard = null;
        foreach (var shelf in here)
        {
            if (shelf.Cards.Count == 0)
                continue;

            var title = new TextBlock
            {
                Text = shelf.Heading,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var cards = new List<(ShelfCard Card, PlaylistsCard Facts)>(shelf.Cards.Count);
            foreach (var facts in shelf.Cards)
            {
                ShelfCard card;
                switch (facts)
                {
                    case FolderCard folder:
                        // No stand-in: the square stays empty until the folder's cover fades in, so its
                        // frame never shows over an empty window.
                        var opens = folder.Place;
                        card = new ShelfCard(
                            folder.Name, folderLine, CardShape.Square, isExplicit: false,
                            stand: "", standFont: null, plain, press: () => OpenPlaylistFolder(opens));
                        if (opens == focusKey)
                            focusCard = card.Root;
                        break;

                    case ListCard list:
                        var entry = list.Entry;
                        var glyph = list.Door is ListCoverDoor.Generated g ? SmartPlaylistFace.Glyph(g.Id.Kind) : "";   // list-music
                        card = new ShelfCard(
                            entry.Name, list.Line, CardShape.Square, list.IsExplicit,
                            stand: glyph, standFont: (FontFamily)Resources["PegasusFa"], plain,
                            press: () => ShowListRecordAsync(entry));
                        break;

                    default:
                        continue;
                }
                cards.Add((card, facts));
            }
            _playlistShelves.Add((title, cards));
        }

        _playlistColumnsCut = 0;   // cut afresh at this screen's count
        SizePlaylists(QueueScale);
        QueuePlaylistCovers();
        ShowPlaylistsEmpty(_playlistShelves.Count == 0 ? "Viz_BrowseNoPlaylists" : null);

        // The cards the focus stood on are gone. With the remote in use it goes to the card of the folder
        // that was left, or to the first of the new ones.
        if ((hadFocus || focusKey is not null) && _collection == CollectionScreen.Playlists)
            FocusScreenLater(focusCard);

        static string Described(PlaylistsCard card) => card switch
        {
            FolderCard f => $"{f.Place}\u0002{f.Name}\u0002{f.Face}\u0002{f.Cover}",
            ListCard l => $"{l.Entry.Name}\u0002{l.Line}\u0002{l.IsExplicit}\u0002{l.Door}",
            _ => "",
        };
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
    /// through, and a folder's in the folder frame (<see cref="FolderCover"/>), which fades in.</summary>
    private void QueuePlaylistCovers()
    {
        var built = _playlistsBuiltFrom;
        var shelfAt = 0;
        foreach (var (_, cards) in _playlistShelves)
        {
            for (var i = 0; i < cards.Count; i++)
            {
                var (card, facts) = cards[i];
                _playlistPictures.Add(new CardPlace(shelfAt, i), async () =>
                {
                    if (built != _playlistsBuiltFrom)
                        return;
                    var bytes = facts switch
                    {
                        FolderCard folder => await FolderCoverAsync(folder),
                        ListCard { Door: ListCoverDoor.Generated g } =>
                            await GeneratedCover.SquareAsync(g.Id, CancellationToken.None),
                        // Nothing is kept for a picture that could not be drawn, so the next build asks again.
                        ListCard { Door: ListCoverDoor.Kept k } => (await PlaylistCovers.MadeAsync(
                            k.NodeId, k.Name, k.Cover, k.Shelf, CancellationToken.None))
                            .Answers(out var drawn, out _) ? drawn.Cover : null,
                        _ => null,
                    };
                    if (bytes is not { Length: > 0 } || built != _playlistsBuiltFrom)
                        return;
                    var bitmap = await RemoteImage.DecodeArtAsync(bytes, CardDecodeEdgeFor(card.Edge));
                    if (built == _playlistsBuiltFrom)
                        card.Show(bitmap, fade: facts is FolderCard);
                });
            }
            shelfAt++;
        }
    }

    /// <summary>A folder's cover: the folder frame with its name, over the picture chosen for it, the app's
    /// own folder's photograph, or a still room (see <see cref="FolderCover"/>).</summary>
    private static async Task<byte[]?> FolderCoverAsync(FolderCard folder)
    {
        var chosen = folder.NodeId is { } id
            ? await PlaylistCovers.ChosenAsync(id, folder.Cover, folder.Shelf, CancellationToken.None)
            : null;
        return await FolderCover.DrawAsync(folder.Face, folder.Name, chosen, CancellationToken.None);
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

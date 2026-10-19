using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Catalog;
using Windows.UI;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One genre's page in Pegasus: a header across the whole width from the top of the screen, the genre's
/// photograph in its colour with its wordmark on it, then how much of it the collection holds, a key to
/// play it and one to shuffle it, what the encyclopedia says about it, and then shelves that scroll
/// sideways: the songs of it the world plays most, its notable artists, its generated playlists, the
/// records added most recently, the genres beside it, and its records decade by decade. OK on a song
/// plays the popular songs from there; on an artist, the artist's page; on a record, the record page; on
/// a playlist, the list's page; on a genre, that genre's page. Back comes back here from each.
///
/// <para><b>From the desktop genre page, for a remote.</b> The same head (<see cref="GenrePhotos"/>, the
/// genre's colour falling over the photograph, its wordmark), the same shelves read by the same rules as
/// MainPage.BuildGenreShelf (the popular songs one per record, the acts in the wall's order, the lists
/// that hold enough to be lists, the decades oldest first and newest record first in each), and the same
/// subgenres (<see cref="RPM.Core.Metadata.Genres.Related"/>, narrowed to genres the collection holds).
/// The desktop's two columns become a head and shelves, as on the artist page. Play all plays the popular
/// songs, or the most recently added records where the world's play counts have not arrived yet; Shuffle
/// plays two hundred songs drawn from across the genre.</para>
///
/// <para><b>The head is the page's header</b>, not a card on it: the whole width, from the top of the
/// screen, its foot fading into the page (see <see cref="PageHeaderArt"/>). The back key sits on it;
/// the mini player sits on it only while something is playing (see PegasusChrome.PlayerAway).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How many cards a shelf holds: the home screen's figure, as on the desktop page.</summary>
    private const int GenreShelfLength = HomeShelf.RowLength;

    /// <summary>How many popular songs, one per record.</summary>
    private const int GenrePopularSongs = 24;

    /// <summary>How many songs Shuffle plays, and from how many records they are drawn.</summary>
    private const int GenreShuffleSongs = 200;
    private const int GenreShuffleRecords = 40;

    private GenreWallItem? _genrePageFor;
    private int _genrePageRead;
    private RoomColours? _genrePageRoom;
    private Button? _genrePlayKey;
    private Button? _genreShuffleKey;

    /// <summary>The popular songs, once read, in the order they are shown. What Play all and a song's card
    /// play.</summary>
    private List<TrackInfo> _genrePopular = [];

    /// <summary>The records added most recently, newest first. What Play all plays where there are no
    /// popular songs.</summary>
    private IReadOnlyList<string> _genreRecent = [];

    private readonly PictureQueue _genrePagePictures = new(3);
    private readonly List<ShelfCard> _genrePageCards = [];
    private readonly List<GenreCard> _genrePageGenres = [];
    private readonly List<ShelfRow> _genrePageRows = [];

    /// <summary>The header's height as a share of the screen's, where that is taller than the photograph's
    /// own 6 to 1 across the width. Without it a narrow window has a strip too short to hold the wordmark
    /// under the back key.</summary>
    private const double GenreHeadLeast = 0.3;

    /// <summary>How much of the header, from its foot, fades into the page.</summary>
    private const double GenreHeadFoot = 0.45;

    // The head's parts, kept so a resize can size them.
    private Grid? _genreHead;
    private Image? _genreHeadPicture;
    private Image? _genreBandName;
    private TextBlock? _genreBandPlainName;

    /// <summary>The genre's photograph, once read; null where it has none.</summary>
    private byte[]? _genrePhotoBytes;

    /// <summary>Whether the photograph has been read, with or without one. The header is drawn only
    /// after: drawn before, a genre with a photograph would show its colour alone first.</summary>
    private bool _genrePhotoRead;

    /// <summary>Where the colour across the header is solid to, as a share of its width: the wordmark's
    /// right edge. Written by the size pass and drawn into the header.</summary>
    private double _genreHeadSolid;

    /// <summary>What the header was last drawn for (its pixels and the colour's edge), so a size pass
    /// that changed neither draws nothing.</summary>
    private (int W, int H, double Solid) _genreHeadDrawnFor;

    /// <summary>The header drawing in flight. Each drawing takes the next number, and one that finishes
    /// to find a newer number drops what it drew.</summary>
    private int _genreHeadDraw;
    private TextBlock? _genreCounts;
    private Grid? _genreKeys;
    private TextBlock? _genreAbout;
    private GenreWordmark? _genreMark;
    private GenreAccent _genreAccent;
    private (int W, int H) _genreNameDrawnAt;

    /// <summary>The slots the shelves read after the build stand in, in their places, so a shelf that
    /// arrives later does not push the ones under it down.</summary>
    private StackPanel? _genrePopularSlot, _genreMoodsSlot, _genreEssentialSlot;

    // ── building it ──

    private void BuildGenrePage(GenreWallItem genre, LibraryAlbumIndex.ArtistShelf shelf)
    {
        DropGenrePage();
        _genrePageFor = genre;
        var read = _genrePageRead;
        var plain = (Style)Resources["PegasusPlainButton"];
        var scale = QueueScale;

        _genreMark = GenreWordmarks.For(genre.Name);
        _genreAccent = _genreMark is { } mark ? GenreAccent.Of(mark) : new GenreAccent(default, GenreFinish.Frost);
        _genrePageRoom = _genreMark is not null
            ? new RoomColours(_genreAccent.Colour, GenreWordmarks.Lit(_genreAccent), GenreWordmarks.Deep(_genreAccent))
            : null;

        // ── the header: the photograph with the genre's colour laid over its left and its foot faded
        // out, drawn as one picture (DrawGenreHead), and the wordmark on the colour at the lower left.
        // The page's room is in the genre's colours behind it, so where the picture fades the page is
        // the same colour. The desktop band's own drifting room is left out: it cannot fade with the
        // picture, and it would end in a line across the page. ──
        var band = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _genreHead = band;
        _genreHeadPicture = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        band.Children.Add(_genreHeadPicture);
        if (_genreMark is not null)
        {
            _genreBandName = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_genreBandName, genre.Name);
            band.Children.Add(_genreBandName);
        }
        else
        {
            _genreBandPlainName = new TextBlock
            {
                Text = genre.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            band.Children.Add(_genreBandPlainName);
        }
        GenrePageContent.Children.Add(band);

        // ── what it is and what can be done with it ──
        var words = new StackPanel();
        _genreCounts = new TextBlock
        {
            Text = Loc.Format("Genres_InLibrary", genre.Folders.Count, genre.Artists.Count),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        words.Children.Add(_genreCounts);

        _genreKeys = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _genreKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _genreKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _genrePlayKey = RecordKey("\uf04b", Loc.Get("Genres_PopularPlayAll"), scale, 46 * scale,
                                  () => PlayGenreAsync(genre), out _);
        _genreShuffleKey = RecordKey("\uf074", null, scale, 46 * scale, () => ShuffleGenreAsync(genre), out _);
        IconName.Set(_genreShuffleKey, Loc.Get("AlbumMenu_Shuffle"));
        Grid.SetColumn(_genreShuffleKey, 1);
        _genreKeys.Children.Add(_genrePlayKey);
        _genreKeys.Children.Add(_genreShuffleKey);
        words.Children.Add(_genreKeys);

        _genreAbout = new TextBlock
        {
            // Left, not stretched: a TextBlock capped short of its column is centred in it otherwise.
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.WordEllipsis,
            Visibility = Visibility.Collapsed,
        };
        words.Children.Add(_genreAbout);
        GenrePageContent.Children.Add(words);

        // ── the shelves, in the desktop page's order ──
        _genrePopularSlot = new StackPanel();
        GenrePageContent.Children.Add(_genrePopularSlot);

        var acts = genre.Artists.Take(GenreShelfLength).ToList();
        if (acts.Count > 0)
        {
            var row = AddGenreRow(GenrePageContent, Loc.Format("Genres_NotableArtists", RPM.Core.Metadata.Genres.Spoken(genre.Name)));
            var column = 0;
            foreach (var act in acts)
            {
                var own = act.Albums.Count + act.Together.Count;
                var compilations = RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(act.Name);
                ShelfCard? card = null;
                card = new ShelfCard(
                    act.Name,
                    own == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", own),
                    CardShape.Round, isExplicit: false,
                    // The compilations shelf is nobody in particular: the people glyph, as on the list.
                    stand: compilations ? "\uf0c0" : ArtistWall.Initial(act.Name).ToString(),
                    standFont: compilations ? (FontFamily)Resources["PegasusFa"] : null, plain,
                    press: () => OpenArtistPage(act, card!.Root));
                row.Cards.Children.Add(card.Root);
                _genrePageCards.Add(card);
                QueueFace(GenrePlace(row.Root, column++), card, act, shelf, read);
            }
        }

        _genreMoodsSlot = new StackPanel();
        GenrePageContent.Children.Add(_genreMoodsSlot);
        _genreEssentialSlot = new StackPanel();
        GenrePageContent.Children.Add(_genreEssentialSlot);

        var mine = new HashSet<string>(genre.Folders, StringComparer.OrdinalIgnoreCase);
        _genreRecent = shelf.AlbumFacts
            .Where(a => mine.Contains(a.Key))
            .OrderByDescending(a => a.Value.AddedAt)
            .ThenBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
            .Take(GenreShelfLength)
            .Select(a => a.Key)
            .ToList();
        AddGenreRecords(Loc.Format("Genres_RecentlyAdded", RPM.Core.Metadata.Genres.Spoken(genre.Name)),
                        _genreRecent, shelf, plain, read);

        AddGenreRelations(genre, plain);

        foreach (var (decade, folders) in Decades(genre, shelf))
            AddGenreRecords(Loc.Format("Genres_Decade", decade), folders, shelf, plain, read);

        SizeGenrePage(scale);
        GenrePageScroll.ChangeView(null, 0, null, disableAnimation: true);
        ApplyGenrePageRooms(LibraryLayer.Visibility == Visibility.Visible && _collection == CollectionScreen.Genre);

        LoadGenrePhotoAsync(genre.Name, read);
        LoadGenreAboutAsync(genre.Name, read);
        ReadGenreShelvesAsync(genre, read);
    }

    private ShelfRow AddGenreRow(Panel into, string heading)
    {
        var row = new ShelfRow(heading);
        _genrePageRows.Add(row);
        into.Children.Add(row.Root);
        return row;
    }

    private void AddGenreRecords(
        string heading, IReadOnlyList<string> folders, LibraryAlbumIndex.ArtistShelf shelf, Style plain, int read)
    {
        if (folders.Count == 0)
            return;
        var row = AddGenreRow(GenrePageContent, heading);
        for (var i = 0; i < folders.Count; i++)
        {
            var card = SleeveCard(folders[i], shelf, plain);
            row.Cards.Children.Add(card.Root);
            _genrePageCards.Add(card);
            QueueSleeve(_genrePagePictures, GenrePlace(row.Root, i), card, folders[i], () => read == _genrePageRead);
        }
    }

    /// <summary>Where a card stands on the genre page: the place among the page's children of the row,
    /// or of the slot a later row stands in, and its place along the row (see <see
    /// cref="PictureQueue"/>).</summary>
    private CardPlace GenrePlace(UIElement rowOrSlot, int column) =>
        new(GenrePageContent.Children.IndexOf(rowOrSlot), column);

    /// <summary>The genres beside this one that the collection holds, most records first, as small genre
    /// cards. Off the genres list as it stands, which is the one place the app decides which records are
    /// on which genre.</summary>
    private void AddGenreRelations(GenreWallItem genre, Style plain)
    {
        var held = _genreWall.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var related = RPM.Core.Metadata.Genres.Related(genre.Name)
            .Where(held.ContainsKey)
            .Select(name => held[name])
            .OrderByDescending(g => g.Folders.Count)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (related.Count == 0)
            return;

        var row = AddGenreRow(GenrePageContent, Loc.Get("Genres_Subgenres"));
        foreach (var other in related)
        {
            var albums = other.Folders.Count;
            var card = new GenreCard(
                other,
                albums == 1 ? Loc.Get("Genres_SubgenreAlbumOne") : Loc.Format("Genres_SubgenreAlbums", albums),
                plain,
                pressed => OpenGenrePage(pressed.Genre, pressed.Root));
            row.Cards.Children.Add(card.Root);
            _genrePageGenres.Add(card);
        }
    }

    /// <summary>The genre's records by decade, oldest decade first, newest record first in each, a shelf's
    /// length at most. A record whose year the collection does not know is on no decade. The desktop
    /// page's rule (MainPage.ErasIn).</summary>
    private static IEnumerable<(int Decade, IReadOnlyList<string> Folders)> Decades(
        GenreWallItem genre, LibraryAlbumIndex.ArtistShelf shelf)
    {
        var byDecade = new Dictionary<int, List<(string Folder, int Year)>>();
        foreach (var folder in genre.Folders)
        {
            if (!shelf.AlbumFacts.TryGetValue(folder, out var made) || made.Year <= 0)
                continue;
            var decade = made.Year / 10 * 10;
            if (!byDecade.TryGetValue(decade, out var records))
                byDecade[decade] = records = [];
            records.Add((folder, made.Year));
        }

        return byDecade
            .OrderBy(era => era.Key)
            .Select(era => (era.Key, (IReadOnlyList<string>)era.Value
                .OrderByDescending(r => r.Year)
                .ThenBy(r => r.Folder, StringComparer.OrdinalIgnoreCase)
                .Take(GenreShelfLength)
                .Select(r => r.Folder)
                .ToList()));
    }

    /// <summary>An act's round photograph for a card, at the card's size.</summary>
    private void QueueFace(CardPlace place, ShelfCard card, ArtistWallItem act, LibraryAlbumIndex.ArtistShelf shelf, int read)
    {
        if (RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(act.Name))
            return;
        var first = act.Own.Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts) ? facts.Album : null)
                           .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        _genrePagePictures.Add(place, async () =>
        {
            if (read != _genrePageRead)
                return;
            var chosen = await ArtistPortraits.SquareAsync(act.Name, first);
            if (read != _genrePageRead || !chosen.Answers(out var url, out _) || url is null)
                return;
            var picture = await ArtistPortraits.PictureAsync(url);
            if (read != _genrePageRead || !picture.Answers(out var portrait, out _) || portrait is null)
                return;
            var bitmap = await RemoteImage.DecodeArtAsync(portrait.Thumb, CardDecodeEdgeFor(card.Edge));
            if (read == _genrePageRead)
                card.Show(bitmap);
        });
    }

    // ── what is read after the build ──

    /// <summary>What the genre page needs from the collection that the shelf does not hold: the songs of
    /// the genre the world plays most, and the sizes of its generated lists.</summary>
    private sealed record GenreShelvesRead(List<LibraryTrack> Popular, List<(SmartPlaylistId Id, int Tracks)> Lists);

    /// <summary>
    /// Reads the popular songs and the generated lists on a worker, and fills their slots when the answer
    /// lands, if the page is still the one built. A list is shown only where it holds enough to be a list
    /// (<see cref="SmartPlaylists.LeastToShow"/>), as on the desktop page.
    /// </summary>
    private async void ReadGenreShelvesAsync(GenreWallItem genre, int read)
    {
        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
            return;

        var db = library.Db;
        var now = DateTime.UtcNow.Ticks;
        var asked = await Task.Run(() => library.Read(db, "a genre page's shelves for Pegasus", mirror =>
        {
            var popular = mirror.QueryPopularUnder(genre.Folders, GenrePopularSongs).ToList();
            var lists = new List<(SmartPlaylistId, int)>();
            foreach (var kind in SmartPlaylists.EssentialForAGenre.Concat(SmartPlaylists.BrokenOutByGenre))
            {
                var id = SmartPlaylistId.Within(kind, genre.Name);
                var tracks = mirror.SmartPlaylistTotals(id, now).Tracks;
                if (tracks >= SmartPlaylists.LeastToShow(id))
                    lists.Add((id, tracks));
            }
            return new GenreShelvesRead(popular, lists);
        }));

        if (read != _genrePageRead || !asked.Answers(out var found, out _))
            return;

        var plain = (Style)Resources["PegasusPlainButton"];
        var scale = QueueScale;
        var card = GenreCardWidth(scale);

        // The popular songs: each a card with its record's sleeve, OK plays the list from there.
        _genrePopular = found.Popular.Select(t => t.ToTrackInfo(root)).ToList();
        if (found.Popular.Count > 0 && _genrePopularSlot is { } popularSlot)
        {
            var row = AddGenreRow(popularSlot, Loc.Get("Genres_PopularTracks"));
            for (var i = 0; i < found.Popular.Count; i++)
            {
                var track = found.Popular[i];
                var at = i;
                var song = new ShelfCard(
                    track.Title ?? track.FileName,
                    track.Artist ?? track.Album,
                    CardShape.Square, track.Advisory == ContentAdvisory.Explicit,
                    stand: "\uf001", standFont: (FontFamily)Resources["PegasusFa"], plain,
                    press: () => PlayFromDrawer(_genrePopular, at, new SleeveInHand.None()));
                row.Cards.Children.Add(song.Root);
                row.Size(scale, LibraryEdgeInset);
                song.Size(card, scale);
                _genrePageCards.Add(song);
                QueueSleeve(_genrePagePictures, GenrePlace(popularSlot, at), song, track.Folder, () => read == _genrePageRead);
            }
        }

        // The generated lists, the mood ones and the essential ones on two shelves, as on the desktop.
        var essentials = new HashSet<SmartPlaylistKind>(SmartPlaylists.EssentialForAGenre);
        FillLists(_genreMoodsSlot, "Genres_MoodPlaylists", found.Lists.Where(l => !essentials.Contains(l.Id.Kind)));
        FillLists(_genreEssentialSlot, "Genres_Essential", found.Lists.Where(l => essentials.Contains(l.Id.Kind)));

        void FillLists(StackPanel? slot, string heading, IEnumerable<(SmartPlaylistId Id, int Tracks)> lists)
        {
            var listed = lists.ToList();
            if (slot is null || listed.Count == 0)
                return;
            var row = AddGenreRow(slot, Loc.Get(heading));
            row.Size(scale, LibraryEdgeInset);
            var column = 0;
            foreach (var (id, tracks) in listed)
            {
                var list = new ShelfCard(
                    SmartPlaylistFace.Name(id),
                    tracks == 1 ? Loc.Get("Stats_SongsOne") : Loc.Format("Stats_Songs", tracks),
                    CardShape.Square, isExplicit: false,
                    stand: SmartPlaylistFace.Glyph(id.Kind), standFont: (FontFamily)Resources["PegasusFa"], plain,
                    press: () =>
                    {
                        if (Library is { IsOpen: true } open)
                            ShowListRecordAsync(GeneratedListEntry(open.Db, id));
                    });
                row.Cards.Children.Add(list.Root);
                list.Size(card, scale);
                _genrePageCards.Add(list);
                _genrePagePictures.Add(GenrePlace(slot, column++), async () =>
                {
                    if (read != _genrePageRead)
                        return;
                    var bytes = await GeneratedCover.SquareAsync(id, CancellationToken.None);
                    if (read != _genrePageRead || bytes is not { Length: > 0 })
                        return;
                    var bitmap = await RemoteImage.DecodeArtAsync(bytes, CardDecodeEdgeFor(list.Edge));
                    if (read == _genrePageRead)
                        list.Show(bitmap);
                });
            }
        }
    }

    /// <summary>The genre's photograph, a file the app ships (no network), and then the header drawn with
    /// it. A genre with no photograph gets the header with its colour alone.</summary>
    private async void LoadGenrePhotoAsync(string genre, int read)
    {
        var bytes = await GenrePhotos.BytesAsync(genre, CancellationToken.None);
        if (read != _genrePageRead)
            return;
        _genrePhotoBytes = bytes is { Length: > 0 } ? bytes : null;
        _genrePhotoRead = true;
        DrawGenreHead();
    }

    /// <summary>
    /// Draws the header's picture for its present size on a worker (<see cref="PageHeaderArt.Draw"/>) and
    /// shows it, if the page and the size are still the ones it was drawn for. Asked by the photograph
    /// landing and by every size pass; a pass that changed nothing draws nothing.
    /// </summary>
    private async void DrawGenreHead()
    {
        if (!_genrePhotoRead || _genreHead is not { Width: > 0, Height: > 0 } head)
            return;

        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var (wide, tall) = PageHeaderArt.PixelsFor(head.Width, head.Height, raster);
        var solid = Math.Round(_genreHeadSolid, 3);
        if (_genreHeadDrawnFor == (wide, tall, solid))
            return;
        _genreHeadDrawnFor = (wide, tall, solid);

        var read = _genrePageRead;
        var draw = ++_genreHeadDraw;
        var photo = _genrePhotoBytes;
        var across = GenreWordmarks.OverTheBandStops(_genreAccent, solid);

        PageHeaderArt.Drawn? drawn;
        try
        {
            drawn = await Task.Run(() => PageHeaderArt.Draw(photo, wide, tall, down: 0.5, GenreHeadFoot, across));
        }
        catch (Exception ex)
        {
            // Not a picture the decoder reads. The wordmark stands on the room.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "a genre page's header", ex);
            return;
        }

        if (read != _genrePageRead || draw != _genreHeadDraw || drawn is null || _genreHeadPicture is null)
            return;
        _genreHeadPicture.Source = PageHeaderArt.Show(drawn);
    }

    /// <summary>The opening of the encyclopedia's article about the genre, four lines at most. In English
    /// only, as on the desktop (see TrackInfoService.GenreArticleAsync). Nothing where there is
    /// none.</summary>
    private async void LoadGenreAboutAsync(string genre, int read)
    {
        RPM.Core.Metadata.Providers.EncyclopediaArticle? article;
        try
        {
            article = await App.Services.GetRequiredService<TrackInfoService>().GenreArticleAsync(genre);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("pegasus", "a genre page's article", ex);
            return;
        }

        if (read != _genrePageRead || article?.Extract is not { Length: > 0 } prose || _genreAbout is null)
            return;
        _genreAbout.Text = prose.Trim();
        _genreAbout.Visibility = Visibility.Visible;
    }

    // ── playing it ──

    /// <summary>The popular songs from the first; where there are none yet, the most recently added
    /// records, each in disc order.</summary>
    private async void PlayGenreAsync(GenreWallItem genre)
    {
        if (_genrePopular.Count > 0)
        {
            PlayFromDrawer(_genrePopular, 0, new SleeveInHand.None());
            return;
        }

        var read = _genrePageRead;
        var folders = _genreRecent.Take(10).ToList();
        var songs = await ReadSongsAsync(folders);
        if (read == _genrePageRead && songs.Count > 0)
            PlayFromDrawer(songs, 0, new SleeveInHand.None());
    }

    /// <summary>
    /// Two hundred songs drawn from across the genre, shuffled: forty of its records picked at random,
    /// their songs together, shuffled, and the first two hundred played. Read when the key is pressed.
    /// </summary>
    private async void ShuffleGenreAsync(GenreWallItem genre)
    {
        var read = _genrePageRead;
        var folders = genre.Folders.OrderBy(_ => Random.Shared.Next()).Take(GenreShuffleRecords).ToList();
        var songs = await ReadSongsAsync(folders);
        if (read != _genrePageRead || songs.Count == 0)
            return;

        for (var i = songs.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (songs[i], songs[j]) = (songs[j], songs[i]);
        }
        PlayFromDrawer(songs.Take(GenreShuffleSongs).ToList(), 0, new SleeveInHand.None());
    }

    /// <summary>The songs of some records of the listener's own collection, each record in disc order,
    /// read on a worker. Empty where the collection did not answer.</summary>
    private static async Task<List<TrackInfo>> ReadSongsAsync(IReadOnlyList<string> folders)
    {
        var library = Library;
        if (!library.IsOpen || library.Root is not { } root || folders.Count == 0)
            return [];

        var db = library.Db;
        var asked = await Task.Run(() => library.Read(db, "a genre's songs for Pegasus", mirror =>
        {
            var songs = new List<TrackInfo>();
            foreach (var folder in folders)
                songs.AddRange(AlbumTracklist.InDiscOrder(mirror.FolderTracks(folder), t => t.DiscNumber, t => t.TrackNumber)
                                             .Select(t => t.ToTrackInfo(root)));
            return songs;
        }));
        return asked.Answers(out var found, out _) ? found : [];
    }

    // ── the rooms ──

    /// <summary>The subgenre cards' rooms drift only while the genre's page is in front. A room told
    /// nothing keeps drawing behind a collapsed screen (see CardRoom.Shown).</summary>
    private void ApplyGenrePageRooms(bool up)
    {
        foreach (var card in _genrePageGenres)
            card.Room.Shown = up;
    }

    // ── letting go ──

    private void DropGenrePage()
    {
        _genrePageRead++;
        _genrePagePictures.Clear();
        ApplyGenrePageRooms(false);
        _genrePageFor = null;
        _genrePageRoom = null;
        _genrePlayKey = null;
        _genreShuffleKey = null;
        _genrePopular = [];
        _genreRecent = [];
        _genreHead = null;
        _genreHeadPicture = null;
        _genrePhotoBytes = null;
        _genrePhotoRead = false;
        _genreHeadSolid = 0;
        _genreHeadDrawnFor = default;
        _genreHeadDraw++;
        _genreBandName = null;
        _genreBandPlainName = null;
        _genreCounts = null;
        _genreKeys = null;
        _genreAbout = null;
        _genreMark = null;
        _genreNameDrawnAt = default;
        _genrePopularSlot = _genreMoodsSlot = _genreEssentialSlot = null;
        _genrePageCards.Clear();
        _genrePageGenres.Clear();
        _genrePageRows.Clear();
        GenrePageContent.Children.Clear();
    }

    // ── sizes ──

    /// <summary>A card's width on the page's shelves: six to the width, as radio's favourites
    /// are.</summary>
    private double GenreCardWidth(double scale)
    {
        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        var strip = Math.Max(320, width - (LibraryEdgeInset * 2));
        return (strip - (22 * scale * 5)) / 6;
    }

    /// <summary>
    /// The page at this scale: the header across the whole width at the photograph's own 6 to 1 (and no
    /// shorter than <see cref="GenreHeadLeast"/> of the screen), the wordmark at two fifths of the
    /// header's height on the colour in its lower left, the words and keys under the header, and the
    /// shelves' cards six to the width.
    /// </summary>
    private void SizeGenrePage(double scale)
    {
        if (_genreHead is null)
            return;

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        var height = LibraryLayer.ActualHeight > 0 ? LibraryLayer.ActualHeight : Root.ActualHeight;
        if (width <= 1)
            return;

        var inset = LibraryEdgeInset;
        // The content's own padding, not the scroller's padding or the content's margin: a scroller
        // clips at its content's bounds (see ShelfRow.Size). Nothing at the top: the header starts at
        // the top of the screen, and reaches out over the side padding to both edges.
        GenrePageContent.Padding = new Thickness(inset, 0, inset, 34 * scale);

        var bandHeight = Math.Max(width / GenrePhotos.Shape, height * GenreHeadLeast);
        _genreHead.Width = width;
        _genreHead.Height = bandHeight;
        _genreHead.Margin = new Thickness(-inset, 0, -inset, 0);

        var foot = 16 * scale;
        double nameEnds;
        if (_genreMark is { } mark && _genreBandName is not null)
        {
            // Held clear of the back key's strip at the top of the screen, however short the header is.
            var boxHeight = Math.Min(bandHeight * 0.4 / mark.Base, bandHeight - HomeTopPad - foot);
            var boxWidth = Math.Min(boxHeight * mark.Aspect, width * 0.6);
            boxHeight = boxWidth / mark.Aspect;
            _genreBandName.Width = boxWidth;
            _genreBandName.Height = boxHeight;
            _genreBandName.Margin = new Thickness(inset, 0, 0, foot);
            nameEnds = inset + boxWidth;

            var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
            var pixels = ((int)Math.Round(boxWidth * raster), (int)Math.Round(boxHeight * raster));
            if (pixels != _genreNameDrawnAt)
            {
                _genreNameDrawnAt = pixels;
                _genreBandName.Source = GenreWordmarks.Lettering(mark, _genreAccent, GenreFlush.Left, pixels.Item1, pixels.Item2);
            }
        }
        else
        {
            nameEnds = inset + (width * 0.4);
            if (_genreBandPlainName is not null)
            {
                _genreBandPlainName.FontSize = bandHeight * 0.3;
                _genreBandPlainName.Margin = new Thickness(inset, 0, 0, foot);
            }
        }
        _genreHeadSolid = nameEnds / width;

        if (_genreCounts is not null)
        {
            _genreCounts.FontSize = 15 * scale;
            _genreCounts.Margin = new Thickness(0, 12 * scale, 0, 0);
        }
        if (_genreKeys is not null)
        {
            _genreKeys.ColumnSpacing = 10 * scale;
            _genreKeys.Margin = new Thickness(0, 16 * scale, 0, 0);
        }
        if (_genrePlayKey is not null)
        {
            _genrePlayKey.Height = 46 * scale;
            _genrePlayKey.Padding = new Thickness(24 * scale, 0, 24 * scale, 0);
        }
        if (_genreShuffleKey is not null)
        {
            _genreShuffleKey.Height = 46 * scale;
            _genreShuffleKey.Width = 56 * scale;
        }
        if (_genreAbout is not null)
        {
            _genreAbout.FontSize = 14 * scale;
            _genreAbout.MaxWidth = 900 * scale;
            _genreAbout.Margin = new Thickness(0, 18 * scale, 0, 0);
        }

        var card = GenreCardWidth(scale);
        var raster2 = XamlRoot?.RasterizationScale is > 0 and var r ? r : 1;
        foreach (var row in _genrePageRows)
            row.Size(scale, LibraryEdgeInset);
        foreach (var each in _genrePageCards)
            each.Size(card, scale);
        foreach (var genreCard in _genrePageGenres)
            genreCard.Size(card * 1.4, scale, raster2);

        DrawGenreHead();
    }
}

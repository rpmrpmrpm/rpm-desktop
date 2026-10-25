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
/// One genre's page in Pegasus, laid out as an artist's page is: a band across the top with the genre's
/// photograph at the right and, left of it, its wordmark, the first whole sentences of what the
/// encyclopedia says about it, how much of it the collection holds, a key to play it and one to shuffle
/// it; then, under the band, shelves that scroll sideways: the songs of it the world plays most, its
/// notable artists, its generated playlists, the records added most
/// recently, the genres beside it, and its records decade by decade. OK on a song plays the popular songs
/// from there; on an artist, the artist's page; on a record, the record page; on a playlist, the list's
/// page; on a genre, that genre's page. Back comes back here from each. A held OK or the Menu key on a song
/// opens the song's menu, as on the artist's page.
///
/// <para><b>From the desktop genre page, for a remote.</b> The same head (<see cref="GenrePhotos"/>, the
/// genre's colour falling over the photograph, its wordmark), the same shelves read by the same rules as
/// MainPage.BuildGenreShelf (the popular songs one per record, the acts in the wall's order, the lists
/// that hold enough to be lists, the decades oldest first and newest record first in each), and the same
/// subgenres (<see cref="RPM.Core.Metadata.Genres.Related"/>, narrowed to genres the collection holds).
/// The desktop's two columns become a band and shelves, as on the artist page. Play all plays the popular
/// songs, or the most recently added records where the world's play counts have not arrived yet; Shuffle
/// plays two hundred songs drawn from across the genre.</para>
///
/// <para><b>The band is the Android TV app's</b> (GenrePage.kt, PageBand; see
/// <see cref="PageHeaderArt"/>): the photograph dissolving toward its left and its foot, the column
/// centred, the band fixed while the rows scroll under it and fade out under its foot, as on the artist's
/// page. The page's room is in the genre's colours. The back key sits on the band; the mini player sits on
/// it only while something is playing (see PegasusChrome.PlayerAway).</para>
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

    // The band's parts, kept so a resize can size them.
    private Image? _genreBandPhoto;
    private StackPanel? _genreColumn;
    private Grid? _genreNameBox;
    private Image? _genreBandName;
    private TextBlock? _genreBandPlainName;
    private TextBlock? _genreCounts;
    private Grid? _genreKeys;
    private TextBlock? _genreAbout;
    private GenreWordmark? _genreMark;
    private GenreAccent _genreAccent;
    private (int W, int H) _genreNameDrawnAt;

    /// <summary>What the article sits in: what a late article grows from nothing (see Arrival).</summary>
    private Grid? _genreAboutHost;

    /// <summary>The article's first paragraph, whole, once it is known, and the width and size it was last
    /// cut at (see <see cref="CutGenreAbout"/>).</summary>
    private string? _genreAboutProse;
    private (double Width, double Size) _genreAboutCutAt;

    /// <summary>The genre's photograph, once read; null where it has none.</summary>
    private byte[]? _genrePhotoBytes;

    /// <summary>The pixels the photograph was last drawn at, so a size pass that changed nothing draws
    /// nothing.</summary>
    private (int W, int H) _genreHeadDrawnFor;

    /// <summary>The photograph drawing in flight. Each drawing takes the next number, and one that finishes
    /// to find a newer number drops what it drew.</summary>
    private int _genreHeadDraw;

    /// <summary>The rows fading under the band. Made on the first build, for the life of the
    /// chrome.</summary>
    private PageRowsFade? _genreRowsFade;

    /// <summary>When the page was built, on the chrome's clock: an article that lands later than
    /// <see cref="LateAfter"/> after it arrives late, and eases in.</summary>
    private double _genrePageBuiltAt;

    /// <summary>The slots the shelves read after the build stand in, in their places, so a shelf that
    /// arrives later does not push the ones under it down.</summary>
    private StackPanel? _genrePopularSlot, _genreMoodsSlot, _genreEssentialSlot;

    // ── building it ──

    private void BuildGenrePage(GenreWallItem genre, LibraryAlbumIndex.ArtistShelf shelf)
    {
        DropGenrePage();
        _genrePageFor = genre;
        _genrePageBuiltAt = _clock.Elapsed.TotalSeconds;
        var read = _genrePageRead;
        var plain = (Style)Resources["PegasusPlainButton"];
        var scale = QueueScale;

        _genreMark = GenreWordmarks.For(genre.Name);
        _genreAccent = _genreMark is { } mark ? GenreAccent.Of(mark) : new GenreAccent(default, GenreFinish.Frost);
        _genrePageRoom = _genreMark is not null
            ? new RoomColours(_genreAccent.Colour, GenreWordmarks.Lit(_genreAccent), GenreWordmarks.Deep(_genreAccent))
            : null;

        // ── the band: the photograph at the right (drawn when it is read, see DrawGenreHead), and in the
        // left column the wordmark, the article, the counts and the keys, centred in the column, as on the
        // artist's page. The page's room is in the genre's colours behind it, so where the photograph
        // dissolves the page is the genre's colour. ──
        var band = GenrePageBand;
        band.Children.Clear();
        _genreBandPhoto = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        band.Children.Add(_genreBandPhoto);

        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _genreColumn = words;
        band.Children.Add(words);

        _genreNameBox = new Grid();
        if (_genreMark is not null)
        {
            _genreBandName = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_genreBandName, genre.Name);
            _genreNameBox.Children.Add(_genreBandName);
        }
        else
        {
            _genreBandPlainName = new TextBlock
            {
                Text = genre.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _genreNameBox.Children.Add(_genreBandPlainName);
        }
        words.Children.Add(_genreNameBox);

        // The article, under the name and over the counts. Collapsed until there is one; its host is what a
        // late article grows (see Arrival).
        _genreAbout = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = ArtistAboutLines,
            TextTrimming = TextTrimming.WordEllipsis,
            Visibility = Visibility.Collapsed,
        };
        _genreAboutHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        _genreAboutHost.Children.Add(_genreAbout);
        words.Children.Add(_genreAboutHost);

        // Dimmer than the article, as on the artist's page.
        _genreCounts = new TextBlock
        {
            Text = Loc.Format("Genres_InLibrary", genre.Folders.Count, genre.Artists.Count),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        words.Children.Add(_genreCounts);

        _genreKeys = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        _genreKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _genreKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _genrePlayKey = RecordKey("\uf04b", Loc.Get("Genres_PopularPlayAll"), scale, PageKeyHeight * scale,
                                  () => PlayGenreAsync(genre), out _);
        _genreShuffleKey = RecordKey("\uf074", null, scale, PageKeyHeight * scale, () => ShuffleGenreAsync(genre), out _);
        IconName.Set(_genreShuffleKey, Loc.Get("AlbumMenu_Shuffle"));
        Grid.SetColumn(_genreShuffleKey, 1);
        _genreKeys.Children.Add(_genrePlayKey);
        _genreKeys.Children.Add(_genreShuffleKey);
        words.Children.Add(_genreKeys);

        _genreRowsFade ??= new PageRowsFade(GenrePageScroll, GenrePageRows, GenrePageRowsFade);

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
                var compilations = RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(act.Name);
                ShelfCard? card = null;
                // The name alone, with no count of records under it, as on the artists list.
                card = new ShelfCard(
                    act.Name,
                    line: null,
                    CardShape.Round, isExplicit: false,
                    // The compilations shelf is nobody in particular: the people glyph, as on the list.
                    stand: compilations ? "\uf0c0" : ArtistWall.Initial(act.Name).ToString(),
                    standFont: compilations ? (FontFamily)Resources["PegasusFa"] : null, plain,
                    press: () => OpenArtistPage(act, card!.Root));
                row.Cards.Children.Add(card.Root);
                _genrePageCards.Add(card);
                QueueActFace(_genrePagePictures, GenrePlace(row.Root, column++), card, act, shelf, () => read == _genrePageRead);
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
            // The genres list's own card: the photograph under the colour, nothing written under it.
            var card = new GenreCard(other, plain, pressed => OpenGenrePage(pressed.Genre, pressed.Root));
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
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return;

        var db = library.Shelf;
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

        // The popular songs: each a card with its record's sleeve, OK plays the list from there. A held OK
        // or the Menu key opens the song's menu, whose Play does the same (see PopularSongMenu).
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
                    press: () => PlayFromDrawer(_genrePopular, at, new SleeveInHand.None()),
                    menu: () => PopularSongMenu(_genrePopular, at));
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
                            ShowListRecordAsync(GeneratedListEntry(open.Shelf, id));
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

    /// <summary>The genre's photograph, a file the app ships (no network), and then the band drawn with it.
    /// A genre with no photograph has the band's column alone, on the genre's room.</summary>
    private async void LoadGenrePhotoAsync(string genre, int read)
    {
        var bytes = await GenrePhotos.BytesAsync(genre, CancellationToken.None);
        if (read != _genrePageRead)
            return;
        _genrePhotoBytes = bytes is { Length: > 0 } ? bytes : null;
        DrawGenreHead();
    }

    /// <summary>
    /// Draws the band's photograph for its present size on a worker (<see cref="PageHeaderArt.Draw"/>) and
    /// shows it, if the page and the size are still the ones it was drawn for. Asked by the photograph
    /// landing and by every size pass; a pass that changed nothing draws nothing.
    ///
    /// <para>The photographs the app ships are cut for the desktop page's band, six to one. The band's box
    /// is about 1.7 to one, so the middle of the photograph is drawn, larger than it was cut.</para>
    /// </summary>
    private async void DrawGenreHead()
    {
        if (_genrePhotoBytes is not { } photo || _genreBandPhoto is not { Width: > 0, Height: > 0 } box)
            return;

        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var (wide, tall) = PageHeaderArt.PixelsFor(box.Width, box.Height, raster);
        if (_genreHeadDrawnFor == (wide, tall))
            return;
        _genreHeadDrawnFor = (wide, tall);

        var read = _genrePageRead;
        var draw = ++_genreHeadDraw;

        PageHeaderArt.Drawn? drawn;
        try
        {
            drawn = await Task.Run(() => PageHeaderArt.Draw(photo, wide, tall, down: 0.5));
        }
        catch (Exception ex)
        {
            // Not a picture the decoder reads. The column stands on the room.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "a genre page's header", ex);
            return;
        }

        if (read != _genrePageRead || draw != _genreHeadDraw || drawn is null || _genreBandPhoto is null)
            return;
        _genreBandPhoto.Source = PageHeaderArt.Show(drawn);
    }

    /// <summary>The opening of the encyclopedia's article about the genre: its first paragraph, cut at the
    /// last whole sentence that fits in four lines, as on the artist's page. In English only, as on the
    /// desktop (see TrackInfoService.GenreArticleAsync). Nothing where there is none. One that arrives after
    /// the page opened eases in (see <see cref="Arrival"/>).</summary>
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

        if (read != _genrePageRead || article?.Extract is not { Length: > 0 } prose
            || _genreAbout is null || _genreAboutHost is null)
            return;

        _genreAboutProse = prose.Split("\n\n").Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
        if (_genreAboutProse is null)
            return;
        _genreAboutCutAt = default;
        CutGenreAbout();
        _genreAbout.Visibility = Visibility.Visible;

        if (_clock.Elapsed.TotalSeconds - _genrePageBuiltAt > LateAfter && _genreColumn is { Width: > 0 } column)
            StartArrival(_genreAboutHost, _genreAbout, column.Width);
    }

    /// <summary>The article cut to the sentences that fit at the column's width (see
    /// <see cref="WholeSentencesAt"/>). Cut again when the width or the text size changes.</summary>
    private void CutGenreAbout()
    {
        if (_genreAbout is null || _genreAboutProse is not { } paragraph || _genreColumn is not { Width: > 0 } column)
            return;
        var at = (column.Width, _genreAbout.FontSize);
        if (at == _genreAboutCutAt)
            return;
        _genreAboutCutAt = at;
        _genreAbout.Text = WholeSentencesAt(_genreAbout, paragraph, column.Width);
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

    /// <summary>The songs of some records of the collection on screen, each record in disc order,
    /// read on a worker. Empty where the collection did not answer.</summary>
    private static async Task<List<TrackInfo>> ReadSongsAsync(IReadOnlyList<string> folders)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root || folders.Count == 0)
            return [];

        var db = library.Shelf;
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
        _genreBandPhoto = null;
        _genreColumn = null;
        _genreNameBox = null;
        _genrePhotoBytes = null;
        _genreHeadDrawnFor = default;
        _genreHeadDraw++;
        _genreBandName = null;
        _genreBandPlainName = null;
        _genreCounts = null;
        _genreKeys = null;
        _genreAbout = null;
        _genreAboutHost = null;
        _genreAboutProse = null;
        _genreAboutCutAt = default;
        _genreMark = null;
        _genreNameDrawnAt = default;
        _genrePopularSlot = _genreMoodsSlot = _genreEssentialSlot = null;
        _genrePageCards.Clear();
        _genrePageGenres.Clear();
        _genrePageRows.Clear();
        GenrePageContent.Children.Clear();
        GenrePageBand.Children.Clear();
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
    /// The page at this scale: the band as on the artist's page (see <see cref="SizePageBand"/>), the
    /// wordmark as large as the name's box takes it, the rest of the column at the TV's couch size, and the
    /// shelves' cards six to the width.
    /// </summary>
    private void SizeGenrePage(double scale)
    {
        if (_genreBandPhoto is null || _genreColumn is null || _genreNameBox is null)
            return;

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        if (width <= 1)
            return;

        var at = SizePageBand(
            scale, width, _genreBandPhoto, _genreColumn, _genreNameBox,
            GenrePageRows, GenrePageRowsFade, GenrePageContent, _genreRowsFade);

        if (_genreMark is { } mark && _genreBandName is not null)
        {
            // The wordmark's full height is its letters over its base: as tall as nine tenths of the box, or
            // as wide (the TV's GenreTitle).
            var boxHeight = Math.Min(at.NameHeight * GenreTitleFill, at.Column * GenreTitleFill / mark.Aspect);
            var boxWidth = boxHeight * mark.Aspect;
            _genreBandName.Width = boxWidth;
            _genreBandName.Height = boxHeight;

            var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
            var pixels = ((int)Math.Round(boxWidth * raster), (int)Math.Round(boxHeight * raster));
            if (pixels != _genreNameDrawnAt && pixels.Item1 > 0 && pixels.Item2 > 0)
            {
                _genreNameDrawnAt = pixels;
                _genreBandName.Source = GenreWordmarks.Lettering(mark, _genreAccent, GenreFlush.Left, pixels.Item1, pixels.Item2);
            }
        }
        if (_genreBandPlainName is not null)
            _genreBandPlainName.FontSize = 44 * scale;   // the name's couch size, as on the artist's page

        if (_genreCounts is not null)
            _genreCounts.FontSize = PageText * scale;
        if (_genreKeys is not null)
        {
            _genreKeys.ColumnSpacing = 10 * scale;
            _genreKeys.Margin = new Thickness(0, PageKeysGap * scale, 0, 0);
        }
        if (_genrePlayKey is not null)
        {
            _genrePlayKey.Height = PageKeyHeight * scale;
            _genrePlayKey.Padding = new Thickness(24 * scale, 0, 24 * scale, 0);
        }
        if (_genreShuffleKey is not null)
        {
            _genreShuffleKey.Height = PageKeyHeight * scale;
            _genreShuffleKey.Width = 56 * scale;
        }
        if (_genreAbout is not null)
        {
            _genreAbout.FontSize = PageText * scale;
            _genreAbout.MaxWidth = at.Column;
            _genreAbout.Margin = new Thickness(0, 0, 0, PageAboutGap * scale);
            CutGenreAbout();
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

    /// <summary>How much of the name's box the wordmark fills, across or down (the TV's TITLE_FILL).</summary>
    private const double GenreTitleFill = 0.9;
}

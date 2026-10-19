using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Presentation.NowPlaying;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Catalog;
using Windows.UI;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One artist's page in Pegasus: the photograph beside the name, how much of their work the collection
/// holds, a key to play all of it and one to shuffle it, what the encyclopedia says about them, and then
/// their records on shelves that scroll sideways. OK on a record opens the record page, and Back comes back
/// here.
///
/// <para><b>From the desktop artist page, for a remote.</b> The same shelves, in the same order and by the
/// same rule (<see cref="ArtistShelves"/>), the same photograph
/// (<see cref="ArtistPortraits.SquareAsync"/>), logo (<see cref="ArtistLogos"/>) and biography
/// (<see cref="TrackInfoService"/>). The desktop page's two columns become a head with shelves under it. A
/// page read from across a room is read top to bottom, and it is moved along with Left and Right, as home
/// and radio are. Left out: the photo pager, the community wall, Discogs' most popular and the members
/// list. Those are for reading and writing at a desk. Also left out is the list of every song, which Play
/// all and Shuffle stand in for. Play all is the phone's (Rpm_Artist_PlayAll): every song on their own
/// records, then the songs that name them on somebody else's.</para>
///
/// <para>Built when it is opened, from the shelf the list was built from, and let go when the collection
/// goes down. Everything fetched for it lands only while <see cref="_artistPageRead"/> still names the
/// build it was fetched for.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The artist the page was built for, or null.</summary>
    private ArtistWallItem? _artistPageFor;

    /// <summary>The build that filled the page. Every build and every drop takes the next number. An answer
    /// that lands and finds another number drops what it found.</summary>
    private int _artistPageRead;

    /// <summary>The colours of the photograph, for the room, once read.</summary>
    private RoomColours? _artistPageRoom;

    /// <summary>Where the focus starts on the page.</summary>
    private Button? _artistPlayKey;

    private ArtistShelves? _artistShelves;

    private readonly PictureQueue _artistPagePictures = new(3);
    private readonly List<ShelfCard> _artistPageCards = [];
    private readonly List<ShelfRow> _artistPageRows = [];

    // The head's parts, kept so a resize can size them without building the page again.
    private Border? _artistPortrait;
    private Image? _artistPortraitArt;
    private TextBlock? _artistPortraitStand;
    private TextBlock? _artistName;
    private Image? _artistLogo;
    private TextBlock? _artistCounts;
    private Grid? _artistKeys;
    private Button? _artistShuffleKey;
    private TextBlock? _artistAbout;
    private Grid? _artistHead;

    /// <summary>The photograph's bytes, for cutting it again at a new size.</summary>
    private byte[]? _artistPortraitBytes;

    // Building the page.

    private void BuildArtistPage(ArtistWallItem act, LibraryAlbumIndex.ArtistShelf shelf)
    {
        DropArtistPage();
        _artistPageFor = act;
        var read = _artistPageRead;
        var plain = (Style)Resources["PegasusPlainButton"];
        var icons = (FontFamily)Resources["PegasusFa"];
        var compilations = ArtistCredit.IsVariousArtists(act.Name);

        var shelves = ArtistShelves.Of(act, shelf.AlbumFacts, shelf.AlbumReleaseTypes);
        _artistShelves = shelves;

        // The head: the photograph, and beside it the name, the counts, the keys and the about text.
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _artistHead = head;

        _artistPortraitStand = new TextBlock
        {
            Text = compilations ? "\uf0c0" : ArtistWall.Initial(act.Name).ToString(),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (compilations)
            _artistPortraitStand.FontFamily = icons;
        _artistPortraitArt = new Image { Stretch = Stretch.UniformToFill };
        var inside = new Grid();
        inside.Children.Add(_artistPortraitStand);
        inside.Children.Add(_artistPortraitArt);
        _artistPortrait = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = inside,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_artistPortrait, 0);
        head.Children.Add(_artistPortrait);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(words, 1);
        head.Children.Add(words);

        _artistName = new TextBlock
        {
            Text = act.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _artistLogo = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_artistLogo, act.Name);
        words.Children.Add(_artistName);
        words.Children.Add(_artistLogo);

        // The desktop page's three counts (ArtistDetailView.Stats): their records, the records they appear
        // on, and the songs that name them; a count of nothing is left out.
        var own = act.Albums.Count + act.Together.Count;
        var counts = new List<string>(3);
        if (own > 0)
            counts.Add(own == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", own));
        if (act.Appearances.Count > 0)
            counts.Add(act.Appearances.Count == 1
                ? Loc.Get("Artists_AppearsOnOne")
                : Loc.Format("Artists_AppearsOnMany", act.Appearances.Count));
        if (act.Tracks > 0)
            counts.Add(act.Tracks == 1 ? Loc.Get("Stats_SongsOne") : Loc.Format("Stats_Songs", act.Tracks));
        _artistCounts = new TextBlock
        {
            Text = string.Join("  ·  ", counts),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        words.Children.Add(_artistCounts);

        // Play all wide, Shuffle beside it: the record page's two keys, at the record page's sizes.
        var scale = QueueScale;
        _artistKeys = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _artistKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _artistKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _artistPlayKey = RecordKey("\uf04b", Loc.Get("Rpm_Artist_PlayAll"), scale, 46 * scale,
                                   () => PlayArtistAsync(act, shuffled: false), out _);
        _artistShuffleKey = RecordKey("\uf074", null, scale, 46 * scale,
                                      () => PlayArtistAsync(act, shuffled: true), out _);
        IconName.Set(_artistShuffleKey, Loc.Get("AlbumMenu_Shuffle"));
        Grid.SetColumn(_artistShuffleKey, 1);
        _artistKeys.Children.Add(_artistPlayKey);
        _artistKeys.Children.Add(_artistShuffleKey);
        words.Children.Add(_artistKeys);

        _artistAbout = new TextBlock
        {
            // Left, not stretched: a TextBlock capped short of its column is centred in it otherwise.
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.WordEllipsis,
            Visibility = Visibility.Collapsed,
        };
        words.Children.Add(_artistAbout);

        ArtistPageContent.Children.Add(head);

        // The shelves.
        AddArtistShelf("Artists_Albums", shelves.Records, shelf, plain, read);
        AddArtistShelf("Artists_Collaborations", shelves.Together, shelf, plain, read);
        AddArtistShelf("Artists_Eps", shelves.Eps, shelf, plain, read);
        AddArtistShelf("Artists_LiveAlbums", shelves.Live, shelf, plain, read);
        AddArtistShelf("Artists_Mixtapes", shelves.Mixtapes, shelf, plain, read);
        AddArtistShelf("Artists_Soundtracks", shelves.Soundtracks, shelf, plain, read);
        AddArtistShelf("Artists_Compilations", shelves.Compilations, shelf, plain, read);
        AddArtistShelf("Artists_Singles", shelves.Singles, shelf, plain, read);
        AddArtistShelf("Artists_AppearsOn", shelves.Appearances, shelf, plain, read);

        SizeArtistPage(scale);
        ArtistPageScroll.ChangeView(null, 0, null, disableAnimation: true);

        // The compilations shelf is not a person: nothing is asked of the catalogues about it (see
        // MainPage.NobodyInParticular).
        if (compilations)
            return;

        var first = act.Own.Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts) ? facts.Album : null)
                           .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        LoadArtistPortraitAsync(act.Name, first, read);
        LoadArtistLogoAsync(act.Name, first, read);
        LoadArtistAboutAsync(act.Name, read);
    }

    /// <summary>One shelf of records, if there are any, with each sleeve's picture queued.</summary>
    private void AddArtistShelf(
        string heading, IReadOnlyList<string> folders, LibraryAlbumIndex.ArtistShelf shelf, Style plain, int read)
    {
        if (folders.Count == 0)
            return;

        var row = new ShelfRow(Loc.Get(heading));
        _artistPageRows.Add(row);
        ArtistPageContent.Children.Add(row.Root);
        var at = ArtistPageContent.Children.IndexOf(row.Root);
        for (var i = 0; i < folders.Count; i++)
        {
            var card = SleeveCard(folders[i], shelf, plain);
            row.Cards.Children.Add(card.Root);
            _artistPageCards.Add(card);
            QueueSleeve(_artistPagePictures, new CardPlace(at, i), card, folders[i], () => read == _artistPageRead);
        }
    }

    /// <summary>
    /// One record as a card: its name without its edition (<see cref="TitleAsides.NameOnly"/>, as the wall
    /// and the desktop shelves print it), the year under it, and its E. OK opens its record page.
    /// </summary>
    private ShelfCard SleeveCard(string folder, LibraryAlbumIndex.ArtistShelf shelf, Style plain)
    {
        var known = shelf.AlbumFacts.TryGetValue(folder, out var facts);
        var named = known && !string.IsNullOrWhiteSpace(facts.Album)
            ? facts.Album
            : folder.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[^1] : folder;
        var year = known && facts.Year > 0 ? facts.Year.ToString(System.Globalization.CultureInfo.CurrentCulture) : null;

        return new ShelfCard(
            TitleAsides.NameOnly(named), year, CardShape.Square, shelf.ExplicitAlbums.Contains(folder),
            stand: "\uf51f", standFont: (FontFamily)Resources["PegasusFa"], plain,
            press: () => ShowRecordAsync(folder));
    }

    /// <summary>Queues a record's sleeve for a card, at the card's size, out of the listener's own
    /// collection.</summary>
    /// <param name="stillWanted">Asked after each await: whether the page the card is
    /// on is still the one built.</param>
    private void QueueSleeve(PictureQueue queue, CardPlace place, ShelfCard card, string folder, Func<bool> stillWanted)
    {
        queue.Add(place, async () =>
        {
            if (!stillWanted())
                return;
            var edge = CardDecodeEdgeFor(card.Edge);
            var bytes = await AlbumCovers.LoadForSquareAsync(folder, CancellationToken.None, edge, ownCollection: true);
            if (bytes is not { Length: > 0 } || !stillWanted())
                return;
            var bitmap = await RemoteImage.DecodeArtAsync(bytes, edge);
            if (stillWanted())
                card.Show(bitmap);
        });
    }

    /// <summary>The pixel edge a card's picture is cut to: its side in points times the display's scale,
    /// kept to the sizes the art cache keeps.</summary>
    private int CardDecodeEdgeFor(double points)
    {
        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        return Math.Clamp((int)Math.Ceiling(Math.Max(points, 120) * raster), ArtScaler.TinyEdge, ArtScaler.TileEdge);
    }

    // What is fetched for the page.

    /// <summary>The photograph, at the head's size, and the room lit in its colours.</summary>
    private async void LoadArtistPortraitAsync(string name, string? firstRecord, int read)
    {
        var chosen = await ArtistPortraits.SquareAsync(name, firstRecord);
        if (read != _artistPageRead || !chosen.Answers(out var url, out _) || url is null)
            return;

        var picture = await ArtistPortraits.PictureAsync(url);
        if (read != _artistPageRead || !picture.Answers(out var portrait, out _) || portrait is null)
            return;

        _artistPortraitBytes = portrait.Full ?? portrait.Thumb;
        await ShowArtistPortraitAsync(read);

        try
        {
            var (_, tint, glow, shade) = await Task.Run(() => VinylPressing.PaletteFromArt(portrait.Thumb));
            if (read != _artistPageRead)
                return;
            _artistPageRoom = new RoomColours(tint, glow, shade);
            PageRoomLanded(CollectionScreen.Artist);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("pegasus", "an artist page's room colours", ex);
        }
    }

    /// <summary>Cuts the photograph to the head's circle at its present size and shows it.</summary>
    private async Task ShowArtistPortraitAsync(int read)
    {
        if (_artistPortraitBytes is not { } bytes || _artistPortrait is null)
            return;

        var edge = (int)Math.Ceiling(_artistPortrait.Width * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1));
        if (edge <= 0)
            return;
        try
        {
            var bitmap = await RemoteImage.DecodeArtAsync(bytes, edge);
            if (read != _artistPageRead || _artistPortraitArt is null || _artistPortraitStand is null)
                return;
            _artistPortraitArt.Source = bitmap;
            _artistPortraitStand.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Not a picture the decoder reads. The letter stays.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "an artist page's photograph", ex);
        }
    }

    /// <summary>The act's logo, where the catalogues have one. It is drawn in the name's place and cut to
    /// the height it is drawn at. A logo from the catalogue is many times larger than that, and the
    /// decoder's own reduction would drop most of its detail, so the logo is cut first.</summary>
    private async void LoadArtistLogoAsync(string name, string? firstRecord, int read)
    {
        var bytes = await ArtistLogos.ForAsync(name, firstRecord);
        if (read != _artistPageRead || bytes is not { Length: > 0 } || _artistLogo is null || _artistName is null)
            return;

        var cap = ArtistLogoHeight * QueueScale;
        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        int tall;
        Microsoft.UI.Xaml.Media.Imaging.BitmapImage mark;
        try
        {
            var fitted = await Task.Run(() => ArtScaler.FitHeight(bytes, (int)Math.Ceiling(cap * raster)));
            (_, tall) = ArtScaler.BoundsOf(fitted);
            mark = await RemoteImage.DecodeAsync(fitted);
        }
        catch (Exception ex)
        {
            // Not a picture the decoder reads. The name stays.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "an artist page's logo", ex);
            return;
        }
        if (read != _artistPageRead || _artistLogo is null || _artistName is null)
            return;

        _artistLogo.Source = mark;
        _artistLogo.MaxHeight = tall > 0 ? Math.Min(cap, tall / raster) : cap;
        _artistLogo.Visibility = Visibility.Visible;
        _artistName.Visibility = Visibility.Collapsed;
    }

    /// <summary>The logo's tallest, before the scale.</summary>
    private const double ArtistLogoHeight = 92;

    /// <summary>The opening of the encyclopedia's article about them, four lines at most. It is shown at
    /// once from the service's cache where the answer is already there, and asked for otherwise. Nothing is
    /// shown where there is no article or no answer.</summary>
    private async void LoadArtistAboutAsync(string name, int read)
    {
        var service = App.Services.GetRequiredService<TrackInfoService>();
        var dossier = service.Cached(name, null);
        if (dossier is null)
        {
            var asked = await service.GetAsync(name, album: null);
            if (read != _artistPageRead || !asked.Answers(out dossier, out _))
                return;
        }

        if (dossier?.ArtistArticle?.Extract is not { Length: > 0 } prose || _artistAbout is null)
            return;
        _artistAbout.Text = prose.Trim();
        _artistAbout.Visibility = Visibility.Visible;
    }

    // Playing the page.

    /// <summary>
    /// Every song of theirs, read off the collection when the key is pressed: their own records whole, in
    /// the order the page shows them, each in disc order, then the songs that name them on somebody else's
    /// records (<see cref="ArtistWall.Names"/>). Played from the first, or shuffled.
    /// </summary>
    private async void PlayArtistAsync(ArtistWallItem act, bool shuffled)
    {
        var read = _artistPageRead;
        var library = Library;
        if (!library.IsOpen || library.Root is not { } root || _artistShelves is not { } shelves)
            return;

        var db = library.Db;
        var asked = await Task.Run(() => library.Read(db, "an artist's songs for Pegasus", mirror =>
        {
            var songs = new List<TrackInfo>();
            foreach (var folder in shelves.OwnInPageOrder)
                songs.AddRange(InDiscOrder(mirror.FolderTracks(folder)).Select(t => t.ToTrackInfo(root)));
            foreach (var folder in shelves.Appearances)
                songs.AddRange(InDiscOrder(mirror.FolderTracks(folder))
                    .Where(t => ArtistWall.Names(t.Artist, act.Name))
                    .Select(t => t.ToTrackInfo(root)));
            return songs;
        }));

        // The page was left while the songs were read, or nothing answered. Nothing is played then.
        if (read != _artistPageRead || !asked.Answers(out var songs, out _) || songs.Count == 0)
            return;

        if (shuffled)
            PlayShuffledList(songs, new SleeveInHand.None());
        else
            PlayFromDrawer(songs, 0, new SleeveInHand.None());

        static List<LibraryTrack> InDiscOrder(IReadOnlyList<LibraryTrack> tracks)
            => AlbumTracklist.InDiscOrder(tracks, t => t.DiscNumber, t => t.TrackNumber);
    }

    // Letting go.

    /// <summary>Takes everything off the page and stops what is being fetched for it.</summary>
    private void DropArtistPage()
    {
        _artistPageRead++;
        _artistPagePictures.Clear();
        _artistPageFor = null;
        _artistPageRoom = null;
        _artistPlayKey = null;
        _artistShuffleKey = null;
        _artistShelves = null;
        _artistPortrait = null;
        _artistPortraitArt = null;
        _artistPortraitStand = null;
        _artistPortraitBytes = null;
        _artistName = null;
        _artistLogo = null;
        _artistCounts = null;
        _artistKeys = null;
        _artistAbout = null;
        _artistHead = null;
        _artistPageCards.Clear();
        _artistPageRows.Clear();
        ArtistPageContent.Children.Clear();
    }

    // Sizes.

    /// <summary>
    /// The page at this scale: the head's photograph a fifth of the width (and no taller than a third of
    /// the screen), the name at 40 before the scale, and the shelves' cards six to the width, as radio's
    /// favourites are.
    /// </summary>
    private void SizeArtistPage(double scale)
    {
        if (_artistHead is null)
            return;

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        var height = LibraryLayer.ActualHeight > 0 ? LibraryLayer.ActualHeight : Root.ActualHeight;
        if (width <= 1)
            return;

        var strip = Math.Max(320, width - (LibraryEdgeInset * 2));
        // The content's own padding, not the scroller's padding or the content's margin. A scroller clips
        // at its content's bounds (see ShelfRow.Size). At the top there is only the back key's line, as
        // home and radio start. No room is kept for the mini player's card. That card is down here while
        // nothing is playing (see PegasusChrome.PlayerAway), and it stands over the page while something is
        // playing, as it stands over home.
        ArtistPageContent.Padding = new Thickness(LibraryEdgeInset, HomeTopPad, LibraryEdgeInset, 34 * scale);

        var edge = Math.Clamp(strip * 0.2, 140, Math.Max(140, height * 0.34));
        var portraitChanged = _artistPortrait is not null && Math.Abs(_artistPortrait.Width - edge) > 0.5;
        if (_artistPortrait is not null)
        {
            _artistPortrait.Width = _artistPortrait.Height = edge;
            _artistPortrait.CornerRadius = new CornerRadius(edge / 2);
        }
        if (_artistPortraitStand is not null)
            _artistPortraitStand.FontSize = edge * 0.36;

        _artistHead.ColumnSpacing = 40 * scale;
        if (_artistName is not null)
            _artistName.FontSize = 40 * scale;
        if (_artistCounts is not null)
        {
            _artistCounts.FontSize = 15 * scale;
            _artistCounts.Margin = new Thickness(0, 8 * scale, 0, 0);
        }
        if (_artistKeys is not null)
        {
            _artistKeys.ColumnSpacing = 10 * scale;
            _artistKeys.Margin = new Thickness(0, 20 * scale, 0, 0);
        }
        if (_artistPlayKey is not null)
        {
            _artistPlayKey.Height = 46 * scale;
            _artistPlayKey.Padding = new Thickness(24 * scale, 0, 24 * scale, 0);
        }
        if (_artistShuffleKey is not null)
        {
            _artistShuffleKey.Height = 46 * scale;
            _artistShuffleKey.Width = 56 * scale;
        }
        if (_artistAbout is not null)
        {
            _artistAbout.FontSize = 14 * scale;
            _artistAbout.MaxWidth = 900 * scale;
            _artistAbout.Margin = new Thickness(0, 18 * scale, 0, 0);
        }

        var gap = 22 * scale;
        var card = (strip - (gap * 5)) / 6;
        foreach (var row in _artistPageRows)
            row.Size(scale, LibraryEdgeInset);
        foreach (var each in _artistPageCards)
            each.Size(card, scale);

        if (portraitChanged)
            _ = ShowArtistPortraitAsync(_artistPageRead);
    }
}

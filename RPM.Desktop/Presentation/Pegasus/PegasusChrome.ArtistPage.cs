using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
/// One artist's page in Pegasus: a band across the top with their photograph at the right and, left of it,
/// their name (or logo), the first whole sentences of what the encyclopedia says about them, how much of
/// their work the collection holds, a key to play all of it and one to shuffle it; then, under the band,
/// their popular songs in columns of four, and their records on shelves that scroll sideways. OK on a song
/// plays the popular songs from it, and a held OK or the Menu key opens the song's menu (see
/// <see cref="PopularSongMenu"/>); OK on a record opens the record page, and Back comes back here.
///
/// <para><b>The band reads as the Android TV app's</b> (PageBand, see <see cref="PageHeaderArt"/>). The
/// photograph fills the right of the screen, dissolving toward its left and its foot, and the page's room
/// is lit in its colours. The band stays where it is: the rows scroll under it and fade out under its foot
/// (see <see cref="PageRowsFade"/>). The name, the article, the counts and the keys are centred in the left
/// column, the article cut at the last whole sentence that fits in four lines (see
/// <see cref="CutArtistAbout"/>). An article or popular songs that arrive after the page has opened fade in
/// while they rise into place, and what is under them glides down to make room (see <see cref="Arrival"/>),
/// rather than appearing at once and pushing the page down.</para>
///
/// <para><b>The article is found through the act's own records</b>, never by the name alone (see
/// <see cref="LoadArtistAboutAsync"/>): a name picks one of every act called that, and Michael Jackson's
/// page showed somebody else's article.</para>
///
/// <para><b>From the desktop artist page, for a remote.</b> The same shelves, in the same order and by the
/// same rule (<see cref="ArtistShelves"/>), the same photographs (<see cref="ArtworkService"/>), logo
/// (<see cref="ArtistLogos"/>) and biography (<see cref="TrackInfoService"/>). The desktop page's two
/// columns become a band with shelves under it. A
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

    // The band's parts, kept so a resize can size them without building the page again.
    private Image? _artistBandPhoto;
    private StackPanel? _artistColumn;
    private Grid? _artistNameBox;
    private TextBlock? _artistName;
    private Image? _artistLogo;
    private TextBlock? _artistCounts;
    private Grid? _artistKeys;
    private Button? _artistShuffleKey;
    private TextBlock? _artistAbout;

    /// <summary>The rows fading under the band. Made on the first build, for the life of the
    /// chrome.</summary>
    private PageRowsFade? _artistRowsFade;

    /// <summary>The pixels the band's photograph was last drawn at, so a size pass that changed nothing
    /// draws nothing.</summary>
    private (int W, int H) _artistBandDrawnFor;

    /// <summary>The photograph drawing in flight. Each drawing takes the next number, and one that finishes
    /// to find a newer number drops what it drew.</summary>
    private int _artistBandDraw;

    /// <summary>What the article sits in. Its height is what a late article grows from nothing, so the
    /// counts and the keys under it glide down rather than jump.</summary>
    private Grid? _artistAboutHost;

    /// <summary>The article's first paragraph, whole, once it is known. What is shown is cut from it at
    /// the width the article is drawn at (see <see cref="CutArtistAbout"/>), so a resize cuts it
    /// again.</summary>
    private string? _artistAboutProse;

    /// <summary>The width and text size the article was last cut at, so a size pass that changed neither
    /// does not measure it again.</summary>
    private (double Width, double Size) _artistAboutCutAt;

    /// <summary>The width the article is drawn at, written by the size pass.</summary>
    private double _artistAboutWidth;

    /// <summary>Where the popular songs stand, between the head and the shelves, so a list that arrives
    /// later does not push the shelves down at once.</summary>
    private StackPanel? _artistPopularSlot;

    /// <summary>The popular songs, once read, in the order they are shown. What Play all tracks and a
    /// song's entry play.</summary>
    private List<TrackInfo> _artistPopular = [];

    /// <summary>Sizes the popular songs' heading, key and entries at a scale. Null until they are
    /// built.</summary>
    private Action<double>? _artistPopularSize;

    /// <summary>When the page was built, on the chrome's clock. Something that lands later than
    /// <see cref="LateAfter"/> after it arrives late, and eases in.</summary>
    private double _artistPageBuiltAt;

    /// <summary>How long after the page is built an answer counts as late. The collection answers inside
    /// the page's own cross; an answer from the network comes well after.</summary>
    private const double LateAfter = 0.25;

    /// <summary>How many popular songs at most: eight columns of four, as Quick selection on home and the
    /// Android TV app have.</summary>
    private const int ArtistPopularMost = 32;

    /// <summary>How many popular songs stand in a column.</summary>
    private const int ArtistPopularDown = 4;

    /// <summary>The photograph's bytes, for drawing it again at a new size.</summary>
    private byte[]? _artistPhotoBytes;

    // Building the page.

    private void BuildArtistPage(ArtistWallItem act, LibraryAlbumIndex.ArtistShelf shelf)
    {
        DropArtistPage();
        _artistPageFor = act;
        _artistPageBuiltAt = _clock.Elapsed.TotalSeconds;
        var read = _artistPageRead;
        var plain = (Style)Resources["PegasusPlainButton"];
        var compilations = ArtistCredit.IsVariousArtists(act.Name);

        var shelves = ArtistShelves.Of(act, shelf.AlbumFacts, shelf.AlbumReleaseTypes);
        _artistShelves = shelves;

        // The band: the photograph at the right (drawn when it lands, see DrawArtistBand), and in the left
        // column the name, the about text, the counts and the keys, all centred in the column, as the
        // Android TV app's band has them. It stands over the rows, which scroll under it.
        var band = ArtistPageBand;
        band.Children.Clear();
        _artistBandPhoto = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        band.Children.Add(_artistBandPhoto);

        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _artistColumn = words;
        band.Children.Add(words);

        // The name's box: the logo fills it where there is one, the name is centred in it until then.
        _artistName = new TextBlock
        {
            Text = act.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _artistLogo = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_artistLogo, act.Name);
        _artistNameBox = new Grid();
        _artistNameBox.Children.Add(_artistName);
        _artistNameBox.Children.Add(_artistLogo);
        words.Children.Add(_artistNameBox);

        // The article, under the name and over the counts. Collapsed until there is one; its host is what a
        // late article grows (see Arrival).
        _artistAbout = new TextBlock
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
        _artistAboutHost = new Grid { VerticalAlignment = VerticalAlignment.Top };
        _artistAboutHost.Children.Add(_artistAbout);
        words.Children.Add(_artistAboutHost);

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
        // Dimmer than the article, as the desktop's subtitle lines and the Android TV app's counts are.
        _artistCounts = new TextBlock
        {
            Text = string.Join("  ·  ", counts),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        words.Children.Add(_artistCounts);

        // Play all wide, Shuffle beside it: the record page's two keys, at the record page's sizes.
        var scale = QueueScale;
        _artistKeys = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
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

        _artistRowsFade ??= new PageRowsFade(ArtistPageScroll, ArtistPageRows, ArtistPageRowsFade);

        // The popular songs' place, filled when they are read (see ReadArtistPopularAsync).
        _artistPopularSlot = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        ArtistPageContent.Children.Add(_artistPopularSlot);

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

        ReadArtistPopularAsync(act, shelves, shelf, read);

        // The compilations shelf is not a person: nothing is asked of the catalogues about it (see
        // MainPage.NobodyInParticular).
        if (compilations)
            return;

        var first = act.Own.Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts) ? facts.Album : null)
                           .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        LoadArtistPhotoAsync(act.Name, first, read);
        LoadArtistLogoAsync(act.Name, first, read);
        LoadArtistAboutAsync(act, shelves, shelf, read);
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

    /// <summary>Queues a record's sleeve for a card, at the card's size, out of the collection on
    /// screen.</summary>
    /// <param name="stillWanted">Asked after each await: whether the page the card is
    /// on is still the one built.</param>
    private void QueueSleeve(PictureQueue queue, CardPlace place, ShelfCard card, string folder, Func<bool> stillWanted)
    {
        queue.Add(place, async () =>
        {
            if (!stillWanted())
                return;
            var edge = CardDecodeEdgeFor(card.Edge);
            var bytes = await AlbumCovers.LoadForSquareAsync(folder, CancellationToken.None, edge);
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

    /// <summary>
    /// The photograph for the band, and the room lit in its colours. The band takes the act's wide
    /// photograph, the first of their backdrops as the artwork service ranks them (an admin's own pictures
    /// first), and their square one where they have no wide one: the Android TV app's choice.
    /// </summary>
    private async void LoadArtistPhotoAsync(string name, string? firstRecord, int read)
    {
        var asked = await App.Services.GetRequiredService<ArtworkService>().GetAsync(name, firstRecord);
        if (read != _artistPageRead || !asked.Answers(out var art, out _))
            return;
        var url = art.Backgrounds.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
                  ?? art.Thumbs.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
        if (url is null)
            return;

        var picture = await ArtistPortraits.PictureAsync(url);
        if (read != _artistPageRead || !picture.Answers(out var portrait, out _) || portrait is null)
            return;

        // The full photograph, for a band most of a screen wide. A portrait from the cache keeps only its
        // cut-down copy, so the full one is read again, from the web cache under the fetch.
        var full = portrait.Full;
        if (full is null && (await RemoteImage.BytesAsync(url)).Answers(out var fetched, out _))
            full = fetched;
        if (read != _artistPageRead)
            return;
        _artistPhotoBytes = full ?? portrait.Thumb;
        DrawArtistBand();

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

    /// <summary>
    /// Draws the band's photograph for its present size on a worker (<see cref="PageHeaderArt.Draw"/>) and
    /// shows it, if the page and the size are still the ones it was drawn for. Asked by the photograph
    /// landing and by every size pass; a pass that changed nothing draws nothing.
    /// </summary>
    private async void DrawArtistBand()
    {
        if (_artistPhotoBytes is not { } photo || _artistBandPhoto is not { Width: > 0, Height: > 0 } box)
            return;

        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var (wide, tall) = PageHeaderArt.PixelsFor(box.Width, box.Height, raster);
        if (_artistBandDrawnFor == (wide, tall))
            return;
        _artistBandDrawnFor = (wide, tall);

        var read = _artistPageRead;
        var draw = ++_artistBandDraw;
        PageHeaderArt.Drawn? drawn;
        try
        {
            drawn = await Task.Run(() => PageHeaderArt.Draw(photo, wide, tall, down: 0.5));
        }
        catch (Exception ex)
        {
            // Not a picture the decoder reads. The band stands on the room.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "an artist page's photograph", ex);
            return;
        }

        if (read != _artistPageRead || draw != _artistBandDraw || drawn is null || _artistBandPhoto is null)
            return;
        _artistBandPhoto.Source = PageHeaderArt.Show(drawn);
    }

    /// <summary>The act's logo, where the catalogues have one. It is drawn in the name's box and cut to
    /// the height it is drawn at. A logo from the catalogue is many times larger than that, and the
    /// decoder's own reduction would drop most of its detail, so the logo is cut first.</summary>
    private async void LoadArtistLogoAsync(string name, string? firstRecord, int read)
    {
        var bytes = await ArtistLogos.ForAsync(name, firstRecord);
        if (read != _artistPageRead || bytes is not { Length: > 0 } || _artistLogo is null || _artistName is null)
            return;

        var cap = _artistNameBox is { Height: > 0 } box ? box.Height : ArtistLogoHeight * QueueScale;
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

    /// <summary>The logo's tallest before the scale, for a logo that lands before the band has a
    /// size.</summary>
    private const double ArtistLogoHeight = 92;

    /// <summary>How many of the act's own records are asked about before the act is taken to be in no
    /// catalogue. A cold question can take the server several seconds, and they are asked one after
    /// another. The Android TV app's figure.</summary>
    private const int ArtistAboutRecordsTried = 3;

    /// <summary>How many lines of the article the head shows at most.</summary>
    private const int ArtistAboutLines = 4;

    /// <summary>
    /// The opening of the encyclopedia's article about them, found through their own records.
    ///
    /// <para><b>Never from the name alone.</b> Asked about a name, the catalogues pick one of every act
    /// called that: Michael Jackson's page showed the British singer of a heavy metal band, and Nirvana's
    /// would show the 1960s British band. A record is matched on the act's name, its title and its number of
    /// songs, and it names its own act by id, which the dossier believes over the name (see
    /// <see cref="PapersAgree"/>). So the act's records are asked about in turn, at most
    /// <see cref="ArtistAboutRecordsTried"/> of them: their ordinary records, then their live records,
    /// soundtracks, compilations and singles, the records with the most songs first on each shelf (an album
    /// is in more catalogues than an EP of the same year). Records made with others are left out, whose
    /// credit can name the other act first. The first answer that matched the record and credits it to the
    /// act is the one used, article or none. An act with no record a catalogue knows has no article here,
    /// which is better than a paragraph about somebody else. This is the question the album page asks about
    /// the same record, so its answer is usually in the service's cache already.</para>
    ///
    /// <para>Shown at once where the answer is already in the cache. An answer that has to be asked for
    /// arrives late, and eases in (see <see cref="ShowArtistAbout"/>).</para>
    /// </summary>
    private async void LoadArtistAboutAsync(
        ArtistWallItem act, ArtistShelves shelves, LibraryAlbumIndex.ArtistShelf shelf, int read)
    {
        var records = new[] { shelves.Records, shelves.Live, shelves.Soundtracks, shelves.Compilations, shelves.Singles }
            .SelectMany(kind => kind.OrderByDescending(f => shelf.TrackCounts.GetValueOrDefault(f)))
            .Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts)
                ? (Credit: string.IsNullOrWhiteSpace(facts.Artist) ? act.Name : facts.Artist,
                   Title: facts.Album,
                   Tracks: shelf.TrackCounts.GetValueOrDefault(f))
                : (Credit: act.Name, Title: "", Tracks: 0))
            .Where(r => !string.IsNullOrWhiteSpace(r.Title))
            .Take(ArtistAboutRecordsTried)
            .ToList();

        var service = App.Services.GetRequiredService<TrackInfoService>();
        var asking = false;
        foreach (var (credit, title, tracks) in records)
        {
            var dossier = service.Cached(credit, title);
            if (dossier is null)
            {
                asking = true;
                var asked = await service.GetAsync(credit, title, tracks);
                if (read != _artistPageRead)
                    return;
                if (!asked.Answers(out dossier, out _))
                    continue;   // not answered is not "nothing known": the next record may answer
            }

            if (!AboutTheAct(dossier, act))
                continue;

            if (dossier.ArtistArticle?.Extract is { Length: > 0 } prose)
                ShowArtistAbout(prose, late: asking && _clock.Elapsed.TotalSeconds - _artistPageBuiltAt > LateAfter);
            return;
        }

        // A dossier is about the act when the record was matched (so the act came from the record, not from
        // the name alone) and the catalogue credits the record to the act.
        static bool AboutTheAct(TrackDossier dossier, ArtistWallItem act) =>
            dossier.Album is { } record && ArtistWall.Names(record.Artist, act.Name);
    }

    /// <summary>The article's first paragraph goes up, cut to the head (see <see cref="CutArtistAbout"/>).
    /// One that <paramref name="late"/> arrives after the page opened fades in while it rises into place,
    /// and the counts and keys under it glide down as it opens (see <see cref="Arrival"/>).</summary>
    private void ShowArtistAbout(string prose, bool late)
    {
        if (_artistAbout is null || _artistAboutHost is null)
            return;

        _artistAboutProse = prose.Split("\n\n").Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
        if (_artistAboutProse is null)
            return;

        _artistAboutCutAt = default;
        CutArtistAbout();
        _artistAbout.Visibility = Visibility.Visible;

        if (late)
            StartArrival(_artistAboutHost, _artistAbout, _artistAboutWidth);
    }

    /// <summary>
    /// The article as the head shows it: the longest run of the paragraph's first sentences that fits in
    /// <see cref="ArtistAboutLines"/> lines at the width it is drawn at, never a cut word; the first sentence
    /// alone where even that does not fit (it is then cut where the lines end). The Android TV app's rule.
    /// Cut again when the width or the text size changes.
    /// </summary>
    private void CutArtistAbout()
    {
        if (_artistAbout is null || _artistAboutProse is not { } paragraph || _artistAboutWidth <= 0)
            return;
        var at = (_artistAboutWidth, _artistAbout.FontSize);
        if (at == _artistAboutCutAt)
            return;
        _artistAboutCutAt = at;

        // A probe of the same face and size, measured at the same width, counts the lines a text takes. One
        // line's height is the height of the paragraph's first letter alone.
        var probe = new TextBlock
        {
            FontSize = _artistAbout.FontSize,
            FontFamily = _artistAbout.FontFamily,
            TextWrapping = TextWrapping.Wrap,
            Text = paragraph[..1],
        };
        var room = new Windows.Foundation.Size(_artistAboutWidth, double.PositiveInfinity);
        probe.Measure(room);
        var line = Math.Max(1, probe.DesiredSize.Height);

        int Lines(string text)
        {
            probe.Text = text;
            probe.Measure(room);
            return (int)Math.Round(probe.DesiredSize.Height / line);
        }

        _artistAbout.Text = WholeSentences(paragraph, ArtistAboutLines, Lines);
    }

    /// <summary>
    /// The longest run of <paramref name="paragraph"/>'s first sentences that <paramref name="lines"/> says
    /// fits in <paramref name="most"/> lines; the first sentence alone when even that does not.
    /// </summary>
    private static string WholeSentences(string paragraph, int most, Func<string, int> lines)
    {
        var sentences = SentenceEnd().Split(paragraph).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (sentences.Count == 0)
            return paragraph;

        var taken = sentences[0];
        foreach (var next in sentences.Skip(1))
        {
            var longer = taken + " " + next;
            if (lines(longer) > most)
                break;
            taken = longer;
        }
        return taken;
    }

    /// <summary>Where a sentence ends: a full stop, an exclamation or a question mark, then a space, then a
    /// capital, a digit or an opening quote or bracket. So "Jr. and" stays one sentence.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("(?<=[.!?])\\s+(?=[\\p{Lu}\\p{N}\"“‘(])")]
    private static partial System.Text.RegularExpressions.Regex SentenceEnd();

    // ── the popular songs ──

    /// <summary>
    /// Their popular songs (<see cref="ArtistPopular"/>): every song of theirs the world has played, read off
    /// the collection (the counts the popularity sweep wrote, see <c>LibraryDatabase.SaveWorldPlays</c>),
    /// most played first, one per title, at most <see cref="ArtistPopularMost"/>. "Theirs" is the page's own
    /// Play all: their own records whole, and the songs that name them on somebody else's. Nothing is shown
    /// where there are too few.
    /// </summary>
    private async void ReadArtistPopularAsync(
        ArtistWallItem act, ArtistShelves shelves, LibraryAlbumIndex.ArtistShelf shelf, int read)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return;

        var own = shelves.OwnInPageOrder.ToList();
        var appearances = shelves.Appearances;
        var db = library.Shelf;
        var asked = await Task.Run(() => library.Read(db, "an artist's popular songs for Pegasus", mirror =>
        {
            var elsewhere = new HashSet<string>(appearances, StringComparer.OrdinalIgnoreCase);
            var played = mirror.QueryWorldPlaysUnder([.. own, .. appearances])
                               .Where(p => !elsewhere.Contains(p.Track.Folder) || ArtistWall.Names(p.Track.Artist, act.Name))
                               .ToList();
            if (played.Count < ArtistPopular.Fewest)
                return new List<LibraryTrack>();

            // Everything of theirs, counted or not: the half rule's whole.
            var songs = own.Sum(f => shelf.TrackCounts.GetValueOrDefault(f));
            foreach (var folder in appearances)
                songs += mirror.FolderTracks(folder).Count(t => ArtistWall.Names(t.Artist, act.Name));

            return ArtistPopular.Ranked(played, p => p.Track.Title, p => p.Plays, songs, ArtistPopularMost)
                                .Select(p => p.Track)
                                .ToList();
        }));

        if (read != _artistPageRead || !asked.Answers(out var popular, out _) || popular.Count == 0
            || _artistPopularSlot is not { } slot)
            return;

        _artistPopular = popular.Select(t => t.ToTrackInfo(root)).ToList();
        var block = BuildArtistPopular(popular, slot, read);
        slot.Children.Add(block);
        _artistPopularSize?.Invoke(QueueScale);

        if (_clock.Elapsed.TotalSeconds - _artistPageBuiltAt > LateAfter)
            StartArrival(slot, block, ArtistStrip());
    }

    /// <summary>
    /// The popular songs as the Android TV app lays them out, like Quick selection on home: the heading
    /// with Play all tracks at its right, then the songs in columns of four, three columns to the width,
    /// scrolling sideways for more. Each song is its record's cover, the title and the record. OK plays the
    /// popular songs from that one, and a held OK or the Menu key opens the song's menu (see
    /// <see cref="PopularSongMenu"/>). The mouse clicks the song's button; the remote stands on the line
    /// around it (see RemoteLine.Around).
    /// </summary>
    private StackPanel BuildArtistPopular(List<LibraryTrack> popular, StackPanel slot, int read)
    {
        var plain = (Style)Resources["PegasusPlainButton"];
        var icons = (FontFamily)Resources["PegasusFa"];
        var ink = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

        var block = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = Loc.Get("Artists_PopularTracks"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ink,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(heading);

        // Play all tracks: a pill of the smoked glass, the play glyph and the words.
        var playGlyph = new TextBlock { Text = "", FontFamily = icons, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        var playWords = new TextBlock
        {
            Text = Loc.Get("Home_PlayAllTracks"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var playRow = new StackPanel { Orientation = Orientation.Horizontal };
        playRow.Children.Add(playGlyph);
        playRow.Children.Add(playWords);
        var playAll = new Button
        {
            Style = plain,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                Child = playRow,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };
        playAll.Click += (_, _) =>
        {
            if (_artistPopular.Count > 0)
                PlayFromDrawer(_artistPopular, 0, new SleeveInHand.None());
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(playAll, playWords.Text);
        Grid.SetColumn(playAll, 1);
        head.Children.Add(playAll);
        block.Children.Add(head);

        // The columns, in a strip that scrolls sideways. It reaches out over the page's side inset, as a
        // shelf row does (see ShelfRow.Size), so the songs line up with the heading and scroll under the edge.
        var columns = new StackPanel { Orientation = Orientation.Horizontal };
        var strip = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BringIntoViewOnFocusChange = false,
            Content = columns,
        };
        block.Children.Add(strip);

        var entries = new List<(Button Key, RemoteLine Line, StackPanel Words, Border Cover, TextBlock Title, TextBlock Record)>();
        var at = ArtistPageContent.Children.IndexOf(slot);
        StackPanel? column = null;
        for (var i = 0; i < popular.Count; i++)
        {
            if (i % ArtistPopularDown == 0)
            {
                column = new StackPanel();
                columns.Children.Add(column);
            }

            var track = popular[i];
            var art = new Image { Stretch = Stretch.UniformToFill };
            var cover = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)),
                Child = art,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var title = new TextBlock
            {
                Text = track.Title ?? track.FileName,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ink,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var record = new TextBlock
            {
                Text = TitleAsides.NameOnly(track.Album ?? ""),
                Foreground = dim,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(title);
            words.Children.Add(record);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(cover);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);

            var from = i;
            var key = new Button
            {
                Style = plain,
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var play = () => PlayFromDrawer(_artistPopular, from, new SleeveInHand.None());
            key.Click += (_, _) => play();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, title.Text);
            var line = RemoteLine.Around(key, play, () => PopularSongMenu(_artistPopular, from));
            column!.Children.Add(line);
            entries.Add((key, line, words, cover, title, record));

            var folder = track.Folder;
            _artistPagePictures.Add(new CardPlace(at, i), async () =>
            {
                if (read != _artistPageRead)
                    return;
                var edge = CardDecodeEdgeFor(64 * QueueScale);
                var bytes = await AlbumCovers.LoadForSquareAsync(folder, CancellationToken.None, edge);
                if (bytes is not { Length: > 0 } || read != _artistPageRead)
                    return;
                var bitmap = await RemoteImage.DecodeArtAsync(bytes, edge);
                if (read == _artistPageRead)
                    art.Source = bitmap;
            });
        }

        // The couch sizes the Android TV app draws them at: the heading at 22, the title at 17 and the
        // record at 15, the cover 64 across with a corner of 6.
        _artistPopularSize = scale =>
        {
            var width = ArtistStrip();
            heading.FontSize = 22 * scale;
            block.Margin = new Thickness(0, 30 * scale, 0, 0);
            head.Margin = new Thickness(0, 0, 0, 12 * scale);
            playGlyph.FontSize = 14 * scale;
            playWords.FontSize = 16 * scale;
            playRow.Spacing = 9 * scale;
            if (playAll.Content is Border pill)
            {
                pill.CornerRadius = new CornerRadius(20 * scale);
                pill.BorderThickness = new Thickness(1);
                pill.Padding = new Thickness(20 * scale, 8 * scale, 20 * scale, 8 * scale);
            }
            playAll.CornerRadius = new CornerRadius(20 * scale);

            var bleed = LibraryEdgeInset;
            strip.Margin = new Thickness(-bleed, 0, -bleed, 0);
            columns.Padding = new Thickness(bleed - (8 * scale), 0, bleed - (8 * scale), 0);
            var panel = Math.Max(160, (width - (20 * scale)) / 3);
            foreach (var each in columns.Children.OfType<StackPanel>())
                each.Width = panel;
            foreach (var (key, line, words, cover, title, record) in entries)
            {
                key.Padding = new Thickness(8 * scale, 6 * scale, 8 * scale, 6 * scale);
                key.CornerRadius = new CornerRadius(8 * scale);
                line.CornerRadius = key.CornerRadius;   // the focus ring follows it
                cover.Width = cover.Height = 64 * scale;
                cover.CornerRadius = new CornerRadius(6 * scale);
                words.Margin = new Thickness(14 * scale, 0, 0, 0);
                words.Spacing = 3 * scale;
                title.FontSize = 17 * scale;
                record.FontSize = 15 * scale;
            }
        };

        return block;
    }

    /// <summary>
    /// A popular song's menu, on the artist's page and the genre's page: the song's own menu, the one a
    /// record's tracks open (see <see cref="TrackMenu"/>), with its Play playing the popular songs from that
    /// song, as OK on it does. The TV's popular songs do the same (ArtistPage.kt, PopularSongs). Null where
    /// the song is gone or there is no menu to build.
    /// </summary>
    private MenuFlyout? PopularSongMenu(List<TrackInfo> songs, int at)
    {
        if (at < 0 || at >= songs.Count || TrackMenu(songs[at]) is not { } menu)
            return null;

        // The factory's Play plays the one song. It is found by its words, which are the same key here,
        // and replaced in its place at the size the menu was given (see ForThisView).
        var words = Loc.Get("AlbumMenu_Play");
        for (var i = 0; i < menu.Items.Count; i++)
        {
            if (menu.Items[i] is not MenuFlyoutItem item || item.Text != words)
                continue;

            var play = MenuEntry(words, () => PlayFromDrawer(songs, at, new SleeveInHand.None()), PlayGlyph);
            play.FontSize = item.FontSize;
            play.MinHeight = item.MinHeight;
            play.Padding = item.Padding;
            menu.Items[i] = play;
            break;
        }

        return menu;
    }

    /// <summary>The width the page's content stands in: the screen less the side insets.</summary>
    private double ArtistStrip()
    {
        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        return Math.Max(320, width - (LibraryEdgeInset * 2));
    }

    // ── what arrives late ──

    /// <summary>
    /// Something that arrives after its page has opened, brought in as the Android TV app brings it in: it
    /// fades in over <see cref="Arrival.Seconds"/> while it rises into place, and the room it takes grows
    /// with it, so what is under it moves down over the same time rather than jumping. The room is the
    /// height of the element that holds it, written each frame and let go (Auto) once it lands.
    /// </summary>
    /// <remarks>
    /// The height is layout, not composition: each frame of the half second lays the page out again. That
    /// is the point of it, since the rows under it have to move, and it is one page for half a second.
    /// </remarks>
    private sealed class Arrival
    {
        /// <summary>How long it takes to come in, the Android TV app's 520 ms.</summary>
        public const double Seconds = 0.52;

        private readonly FrameworkElement _host;
        private readonly FrameworkElement _content;
        private readonly TranslateTransform _rise = new();
        private readonly double _slide;
        private readonly double _height;
        private double _elapsed;

        /// <param name="height">The content's full height, measured before it starts.</param>
        /// <param name="slide">How far below its place it starts.</param>
        public Arrival(FrameworkElement host, FrameworkElement content, double height, double slide)
        {
            _host = host;
            _content = content;
            _height = height;
            _slide = slide;
            content.RenderTransform = _rise;
            Write(0);
        }

        /// <summary>One frame. False once it has landed.</summary>
        public bool Tick(double dt)
        {
            _elapsed += dt;
            var t = Math.Clamp(_elapsed / Seconds, 0, 1);
            Write(1 - ((1 - t) * (1 - t)));   // ease out, quadratic
            if (t < 1)
                return true;
            Land();
            return false;
        }

        /// <summary>Lands at once: the page is going, or another arrival takes the same place.</summary>
        public void Land()
        {
            _host.Height = double.NaN;
            _content.Opacity = 1;
            _rise.Y = 0;
        }

        private void Write(double eased)
        {
            _host.Height = _height * eased;
            _content.Opacity = eased;
            _rise.Y = (1 - eased) * _slide;
        }
    }

    private readonly List<Arrival> _arrivals = [];
    private double _arrivalsTickedAt;

    /// <summary>Starts <paramref name="content"/> coming into <paramref name="host"/>, measured at
    /// <paramref name="width"/>.</summary>
    private void StartArrival(FrameworkElement host, FrameworkElement content, double width)
    {
        if (width <= 0)
            return;
        content.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var height = content.DesiredSize.Height;
        if (height <= 0)
            return;
        if (_arrivals.Count == 0)
            _arrivalsTickedAt = _clock.Elapsed.TotalSeconds;
        _arrivals.Add(new Arrival(host, content, height, 18 * QueueScale));
    }

    /// <summary>One frame of everything arriving. From the frame tick (TickArtistsGenres).</summary>
    private void TickArrivals()
    {
        if (_arrivals.Count == 0)
            return;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(0.1, Math.Max(0, now - _arrivalsTickedAt));
        _arrivalsTickedAt = now;
        _arrivals.RemoveAll(each => !each.Tick(dt));
    }

    /// <summary>Lands everything still arriving, for a page that is being let go.</summary>
    private void LandArrivals()
    {
        foreach (var each in _arrivals)
            each.Land();
        _arrivals.Clear();
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
        if (!library.IsOpen || library.ShelfRoot is not { } root || _artistShelves is not { } shelves)
            return;

        var db = library.Shelf;
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
        _artistBandPhoto = null;
        _artistColumn = null;
        _artistNameBox = null;
        _artistPhotoBytes = null;
        _artistBandDrawnFor = default;
        _artistBandDraw++;
        _artistName = null;
        _artistLogo = null;
        _artistCounts = null;
        _artistKeys = null;
        _artistAbout = null;
        _artistAboutHost = null;
        _artistAboutProse = null;
        _artistAboutCutAt = default;
        _artistPopularSlot = null;
        _artistPopular = [];
        _artistPopularSize = null;
        LandArrivals();
        _artistPageCards.Clear();
        _artistPageRows.Clear();
        ArtistPageContent.Children.Clear();
        ArtistPageBand.Children.Clear();
    }

    // Sizes.

    /// <summary>
    /// The page at this scale: the band as the TV lays it out (see <see cref="SizePageBand"/>), the name
    /// at 44 and the rest of the column at 19 before the scale, the TV's couch sizes, and the shelves'
    /// cards six to the width, as radio's favourites are.
    /// </summary>
    private void SizeArtistPage(double scale)
    {
        if (_artistBandPhoto is null || _artistColumn is null || _artistNameBox is null)
            return;

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        if (width <= 1)
            return;

        var strip = Math.Max(320, width - (LibraryEdgeInset * 2));
        var at = SizePageBand(
            scale, width, _artistBandPhoto, _artistColumn, _artistNameBox,
            ArtistPageRows, ArtistPageRowsFade, ArtistPageContent, _artistRowsFade);

        if (_artistName is not null)
            _artistName.FontSize = 44 * scale;   // the TV's couch sizes here: the name 44, the rest 19 (PAGE_TEXT)
        if (_artistLogo is not null)
            _artistLogo.MaxWidth = at.Column;
        if (_artistCounts is not null)
            _artistCounts.FontSize = 19 * scale;
        if (_artistKeys is not null)
        {
            _artistKeys.ColumnSpacing = 10 * scale;
            _artistKeys.Margin = new Thickness(0, PageKeysGap * scale, 0, 0);
        }
        if (_artistPlayKey is not null)
        {
            _artistPlayKey.Height = PageKeyHeight * scale;
            _artistPlayKey.Padding = new Thickness(24 * scale, 0, 24 * scale, 0);
        }
        if (_artistShuffleKey is not null)
        {
            _artistShuffleKey.Height = PageKeyHeight * scale;
            _artistShuffleKey.Width = 56 * scale;
        }
        // The article as wide as the column, and cut again to the sentences that fit at that width.
        _artistAboutWidth = Math.Max(160, at.Column);
        if (_artistAbout is not null)
        {
            _artistAbout.FontSize = PageText * scale;
            _artistAbout.MaxWidth = _artistAboutWidth;
            _artistAbout.Margin = new Thickness(0, 0, 0, PageAboutGap * scale);
            CutArtistAbout();
        }
        _artistPopularSize?.Invoke(scale);

        var gap = 22 * scale;
        var card = (strip - (gap * 5)) / 6;
        foreach (var row in _artistPageRows)
            row.Size(scale, LibraryEdgeInset);
        foreach (var each in _artistPageCards)
            each.Size(card, scale);

        DrawArtistBand();
    }

    // The band's column, as the TV draws it, before the scale: the article and the counts at 19 (PAGE_TEXT),
    // 10 under the article, and Pegasus's keys 20 under the counts, 46 tall.
    private const double PageText = 19;
    private const double PageAboutGap = 10;
    private const double PageKeysGap = 20;
    private const double PageKeyHeight = 46;

    /// <summary>
    /// The band of an artist's or a genre's page at this scale, as the TV lays it out (see
    /// <see cref="PageHeaderArt.For"/>): the photograph's box at the right, the column at the left with the
    /// name's box at its head, and the rows under the band, starting where the band's foot fades them.
    ///
    /// <para>The rows start under the column at its fullest (the name's box, four lines of article, the
    /// counts and the keys) wherever that is lower than the TV's place, so the keys never have rows passing
    /// under them. It does not move when an article arrives late.</para>
    /// </summary>
    /// <returns>The band's places, for what the page sizes itself.</returns>
    private PageHeaderArt.Places SizePageBand(
        double scale, double width, Image photo, StackPanel column, Grid nameBox,
        FrameworkElement rows, FrameworkElement fadeHost, StackPanel content, PageRowsFade? fade)
    {
        var at = PageHeaderArt.For(width, HomeTopPad);

        photo.Width = at.Photo;
        photo.Height = at.High;
        column.Width = at.Column;
        column.Margin = new Thickness(LibraryEdgeInset, at.NameTop, 0, 0);
        nameBox.Height = at.NameHeight;
        nameBox.Margin = new Thickness(0, 0, 0, at.Gap);

        var line = PageLineHeight(PageText * scale);
        var foot = at.NameTop + at.NameHeight + at.Gap
                   + (ArtistAboutLines * line) + (PageAboutGap * scale)
                   + line
                   + ((PageKeysGap + PageKeyHeight + 12) * scale);
        var top = at.RowsTop(foot);

        // The content's own padding, not the scroller's padding or the content's margin: a scroller clips at
        // its content's bounds (see ShelfRow.Size). At its top, the depth the rows fade over, so nothing is
        // under the band's foot until the page is scrolled. No room is kept for the mini player's card. That
        // card is down here while nothing is playing (see PegasusChrome.PlayerAway), and it stands over the
        // band while something is playing, as it stands over home.
        rows.Margin = new Thickness(0, top, 0, 0);
        fadeHost.Margin = new Thickness(0, top, 0, 0);
        fadeHost.Height = at.Under;
        content.Padding = new Thickness(LibraryEdgeInset, at.Under, LibraryEdgeInset, 34 * scale);
        if (fade is not null)
            fade.Depth = at.Under;
        return at;
    }

    /// <summary>
    /// Up and Down between an artist's or a genre's band and the rows under it. The keys stand in the band,
    /// outside the rows' scroller, so the rows' own rule (<see cref="RemoteSearch.NearestRow"/>) does not
    /// see them:
    /// <list type="bullet">
    /// <item>Up from the top row reaches the band's play key, from any column of the row.</item>
    /// <item>Up from the band stays in the band. What is above the keys in the rows has scrolled away under
    /// the band, and is not on screen.</item>
    /// </list>
    /// Everything else is the search by layout, then the rows' own rule.
    /// </summary>
    /// <param name="next">Where the search by layout would move the focus, or null.</param>
    private Control? AcrossPageBand(FrameworkElement from, FocusNavigationDirection direction, Control? next, RemoteScreen screen)
    {
        var (band, scroll, play) = _collection == CollectionScreen.Artist
            ? (ArtistPageBand, ArtistPageScroll, _artistPlayKey)
            : (GenrePageBand, GenrePageScroll, _genrePlayKey);

        if (Inside(from, band))
        {
            if (direction == FocusNavigationDirection.Up && next is not null && Inside(next, scroll))
                return null;
            return next ?? RemoteSearch.NearestRow(from, direction, scroll, screen);
        }

        if (next is not null)
            return next;
        return RemoteSearch.NearestRow(from, direction, scroll, screen)
               ?? (direction == FocusNavigationDirection.Up ? play : null);

        static bool Inside(DependencyObject element, DependencyObject scope)
        {
            for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, scope))
                    return true;
            return false;
        }
    }

    /// <summary>The height of one line of the band's text at this size, measured once per size.</summary>
    private double PageLineHeight(double size)
    {
        if (Math.Abs(_pageLineFor.Size - size) < 0.01 && _pageLineFor.Height > 0)
            return _pageLineFor.Height;
        // A digit: a line's height is the face's, whatever is on it.
        var probe = new TextBlock { FontSize = size, FontFamily = FontFamily, Text = "0" };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var height = probe.DesiredSize.Height > 0 ? probe.DesiredSize.Height : size * 1.33;
        _pageLineFor = (size, height);
        return height;
    }

    private (double Size, double Height) _pageLineFor;
}

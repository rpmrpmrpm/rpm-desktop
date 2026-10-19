using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Presentation.Main.Genres.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Genres;

/// <summary>
/// One genre, opened from its row on the wall. The page is like the artist page, but it describes a kind of
/// music.
///
/// <para><b>The same page, on purpose.</b> There are two columns. On the left is what this genre IS: an
/// article, and the genres it stands next to. On the right is what the collection holds OF it, as shelves.
/// A genre that is not a kind of music has neither of the left items, so it has one column (see
/// <see cref="AboutExists"/>). Every card uses the same plate, the same four inks and the same headings.
/// The page folds at the same width (<see cref="ArtistPageLayout.Columns"/>). An artist and a genre are two
/// ways into one collection, so a reader who has learned one page has learned the other.</para>
///
/// <para><b>Where it differs, and why.</b> The band runs across the WHOLE page, not just the column. An
/// artist's band is one photograph of one person, and it sits beside the paragraph about them. A genre's
/// band shows five people side by side. In a column four hundred points wide, five faces would each be a
/// thin slice. The picture needs the width.</para>
///
/// <para>The control knows nothing of the library. Everything it shows arrives through the loaders below.
/// Every press goes back out the same way. This page has no idea what an artist's page is, what opening a
/// record means, or where a generated list is drawn.</para>
/// </summary>
public sealed partial class GenreDetailView : UserControl
{
    /// <summary>The page's own scroller, for the shelves. It is not the column's scroller. The shell pins a
    /// view's scroll in step with the art drawer as the drawer slides. The shelves are what a reader is
    /// looking at, so they are pinned.</summary>
    public ScrollViewer ScrollHost => BodyScroll;

    /// <summary>The way out was pressed. Where back goes is the shell's question, not this
    /// page's.</summary>
    public event EventHandler? BackRequested;

    /// <summary>An act on the genre was pressed.</summary>
    public event Action<ArtistWallItem>? ArtistOpened;

    /// <summary>One of the genres beside this one was pressed, in the card under the article.</summary>
    public event Action<string>? RelatedOpened;

    // ── what the page is drawn from ──

    /// <summary>Draws the genre's band at the shape this page needs. The band uses the photograph shipped
    /// for that shelf, cut to this page's own panel (see <see cref="GenrePhotos"/>). It is null where the
    /// shelf has no photograph. The page then opens with the name on its own surface, which is a finished
    /// page.</summary>
    public Func<GenreWallItem, CancellationToken, Task<byte[]?>>? BannerLoader { get; set; }

    /// <summary>Gets everything the six shelves hold, in one pass. See <see cref="GenreShelf"/>, which
    /// explains why it is one call and not six. It is given the two live measurements, because the size a
    /// card is drawn at is this page's decision, and the cards are built in the loader.
    ///
    /// <para><b>It is asynchronous, and the page draws a turning record until it answers.</b> It reads the
    /// mirror. On a shared library, the first read builds the mood cut and the genre fold for that whole
    /// collection from scratch (see <c>MainPage.BuildGenreShelfAsync</c>). If the thread that draws the
    /// page waited for that, the page would freeze partway through the fade that brings it
    /// in.</para></summary>
    public Func<GenreWallItem, Library.Models.AlbumTileMetrics, ArtistTileMetrics,
                CancellationToken, Task<GenreShelf>>? ShelfLoader
    { get; set; }

    /// <summary>Gets what Wikipedia says about this kind of music. It is null for a genre that nothing is
    /// written down for, and also when the encyclopedia does not answer. The card is hidden either
    /// way.</summary>
    public Func<string, CancellationToken, Task<EncyclopediaArticle?>>? ArticleLoader { get; set; }

    /// <summary>Gets the genres this one stands next to that the collection holds something of, with their
    /// counts. It is synchronous. It is a table lookup and a count over a shelf that is already in memory.
    /// A card that arrived a frame late would flicker for no reason.</summary>
    public Func<GenreWallItem, IReadOnlyList<GenreRelation>>? RelationsLoader { get; set; }

    /// <summary>
    /// The colour this genre's CARD on the wall came out in. The head of its page uses the same colour as
    /// the tile the reader pressed to get here.
    ///
    /// <para>The colour is asked for, not worked out again. A card's colour is read from its photograph,
    /// and this page's photograph is a different one (see <c>MainPage.LoadGenreBannerAsync</c>). A page
    /// that picked its own colour could show a different colour for the same shelf. Null, or unset, leaves
    /// the head in the genre's own colour from the table. That is the colour it always uses.</para>
    ///
    /// <para>It is asked once, when the head is dressed. A card whose photograph arrives after the page is
    /// open keeps the colour the page was opened with. The wall behind the page can change, but this does
    /// not, until the genre is opened again.</para>
    /// </summary>
    public Func<string, GenreAccent?>? AccentOnTheWall { get; set; }

    /// <summary>One record's sleeve. It comes from the same loader that the album wall and the artist page
    /// use.</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? CoverLoader { get; set; }

    /// <summary>One act's photograph. It comes from the artists wall's own loader, so a face already on
    /// screen there costs nothing to show here.</summary>
    public Func<ArtistWallItem, CancellationToken, Task<ArtistFace>>? PortraitLoader { get; set; }

    /// <summary>One generated list's square. It comes from the one place where a playlist's cover is
    /// drawn.</summary>
    public Func<SmartPlaylistId, CancellationToken, Task<byte[]?>>? ListCoverLoader { get; set; }

    /// <summary>
    /// Whether this genre has anything to say about ITSELF: an article, or genres it stands next to.
    ///
    /// <para><b>It is synchronous, and it is one question, not two.</b> The left column holds exactly those
    /// two things. A shelf that is not a kind of music has neither. "Other" and "Other / Unknown" are where
    /// records go when nobody could name them, so there is no encyclopedia article for them, and nothing
    /// stands next to them. Without this check, their page would show four hundred and forty points of
    /// empty column beside the shelves, space the shelves could use.</para>
    ///
    /// <para>The shell answers this, not this page, for the same reason every other loader is answered
    /// outside it. This page has no idea what an encyclopedia is, or where the family table is. It is
    /// synchronous because the answer must be known when the page is BUILT. If it were learned from the
    /// article fetch, it would arrive a network round trip later. Taking a column away from under a reader
    /// who is already reading is worse than never drawing one.</para>
    ///
    /// <para>A null loader means every genre keeps its column.</para>
    /// </summary>
    public Func<GenreWallItem, bool>? AboutExists { get; set; }

    // ── what the presses do ──

    /// <summary>Opens a record where it stands.</summary>
    public Action<string>? OpenRecord { get; set; }

    /// <summary>Opens a generated list.</summary>
    public Action<SmartPlaylistId>? OpenList { get; set; }

    /// <summary>Puts one of the popular songs on.</summary>
    public Action<ArtistSong>? PlaySong { get; set; }

    /// <summary>Puts every song on the popular card into the queue, in the order the card ranks them. This
    /// is what the button beside that heading does.</summary>
    public Action<IReadOnlyList<ArtistSong>>? PlaySongs { get; set; }

    /// <summary>The genre being read, or null while nothing is.</summary>
    public GenreWallItem? Genre { get; private set; }

    /// <summary>The one live measurement for the shelves. It is written on every resize, from the shelves'
    /// own width. Every sleeve and every list card reads it through its binding. There is one per page, not
    /// one per process, because it measures THIS page's shelves.</summary>
    private readonly Library.Models.AlbumTileMetrics ShelfMetrics = new();

    /// <summary>The measurement for the acts' boxes. It is a different shape and a different question. It
    /// uses the artists wall's own measurement type, so a box here and a box there are the same
    /// card.</summary>
    private readonly ArtistTileMetrics ActMetrics = new();

    private GenreShelf _shelf = GenreShelf.Empty;
    private CancellationTokenSource? _reading;
    private string? _articleUrl;

    /// <summary>Which sleeves have already been asked for. A record can be on the newest shelf AND in one
    /// of the decades, so two cards want one cover, and only one of them should fetch it. The set is
    /// cleared wherever the cards are replaced. See <see cref="Show"/>, which is the one place that
    /// happens.</summary>
    private readonly HashSet<string> _coversAsked = new(StringComparer.OrdinalIgnoreCase);

    public GenreDetailView()
    {
        InitializeComponent();

        Acts.ElementPrepared += Act_ElementPrepared;
        Added.ElementPrepared += Record_ElementPrepared;
        Essential.ElementPrepared += List_ElementPrepared;
        Moods.ElementPrepared += List_ElementPrepared;

        Unloaded += (_, _) => _reading?.Cancel();

    }

    // ── opening and closing ──

    /// <summary>
    /// Opens one genre: its name, its shelves, and its band and article as they arrive.
    ///
    /// <para>Everything the library can answer at once is answered at once. The two things that come over a
    /// network arrive when they arrive. A page that waited for an encyclopedia before it showed a shelf
    /// would open blank on a train.</para>
    ///
    /// <para><b>Opening the same genre again is not a second opening.</b> The shell calls this on every
    /// library change, so the shelves follow an import. A genre arrives as a new item each time the wall is
    /// rebuilt. Genres are told apart by NAME, which is what a genre IS here (see <see cref="GenreWall"/>).
    /// The item's identity changes for reasons that have nothing to do with the reader. The shelves are
    /// rebuilt either way, because that is the point of the call. The band, the article and the reader's
    /// place in the page are kept.</para>
    /// </summary>
    public void Load(GenreWallItem genre)
    {
        _headOpenedAt = HeadClock.Elapsed.TotalMilliseconds;
        _headLayouts = 0;
        _preparedSleeves = _preparedActs = _preparedLists = 0;

        Step($"LOAD {genre.Name}, page {(Visibility == Visibility.Visible ? "visible" : "hidden")} "
           + $"at opacity {Opacity:0.##}, band {BannerBox.ActualWidth:0}x{BannerBox.ActualHeight:0}");

        var reopening = Genre is { } was
                     && string.Equals(was.Name, genre.Name, StringComparison.OrdinalIgnoreCase);

        Genre = genre;

        // The count under the name moves with the library. The name itself does not.
        Subtitle.Text = Loc.Format("Genres_InLibrary", genre.Folders.Count, genre.Artists.Count);

        FillRelations(genre);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("relations");

        // A new genre has a new article. A reopening of the same genre does not fetch again. It is a
        // library change on the genre already open. Setting the article back to Waiting there would show
        // the column empty, with nothing coming to fill it. This is set before SettleAbout either way,
        // because the column's placement is worked out from it.
        if (!reopening)
            _article = ArticleState.Waiting;

        // Decides whether this genre gets a column at all, before anything is fetched. See AboutExists. The
        // panel may not have been measured yet. If so, SettleAbout does nothing, and the first measurement
        // does it. The two can arrive in either order, and neither depends on the other.
        _hasAbout = AboutExists?.Invoke(genre) ?? true;
        SettleAbout();
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("about");

        if (reopening)
        {
            // The genre was opened again: the same genre, after a library change, not a navigation. The
            // page never left, so the shelves are rebuilt UNDER what is already on them. The reader is
            // somewhere on this page. Emptying the page to a spinner because an import landed would take
            // away the shelf they are reading.
            //
            // Nothing here touches the head. That is the purpose of this branch. ShowWordmark puts the band
            // DOWN and clears the flag that says its picture has settled. Only DrawBandAsync sets that flag
            // again, and the band is not fetched for a genre that never left. So if a reopening dressed the
            // head, it would take the head down with nothing to bring it back. The grace period in
            // WaitForPicture (see below) would then fade it in again by itself. The head would blink out
            // and back in on every library change, several times per import. The name and the colour belong
            // to the genre. The genre has not changed, so nothing here needs redrawing.
            _ = FillShelvesAsync(genre, clear: false, _reading?.Token ?? CancellationToken.None);
            return;   // The picture and the article are already up or on their way.
        }

        ShowWordmark(genre.Name);
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("wordmark");

        _reading?.Cancel();
        _reading = new CancellationTokenSource();

        _ = FillShelvesAsync(genre, clear: true, _reading.Token);

        Banner.Visibility = Visibility.Collapsed;
        Put(Banner, 0);
        BannerArt.ImageSource = null;
        PageGround.Fill = null;
        PageGround.Visibility = Visibility.Collapsed;
        PageWeight.Visibility = Visibility.Collapsed;
        PageWash.Visibility = Visibility.Collapsed;
        PageWashArt.ImageSource = null;
        Lend();   // And the window with it. The genre being left is not the one the app should show.

        AboutBlock.Visibility = Visibility.Collapsed;
        AboutMissing.Visibility = Visibility.Collapsed;

        // The turning record shows only where an article is coming. A genre with no column has no article.
        // Spinning for one would be a worker turning behind a collapsed panel.
        ArticleBusy.Spinning = _hasAbout;

        // The shelves' record starts down on every genre. Its own read raises it after its grace period, if
        // it needs to. This line stops a record left turning by the genre before this one.
        ShelvesBusy.Spinning = false;
        _articleUrl = null;

        AboutScroll.ChangeView(null, 0, null, disableAnimation: true);
        BodyScroll.ChangeView(null, 0, null, disableAnimation: true);

        _ = DrawBandAsync(genre, _reading.Token);
        if (_hasAbout)
            _ = ReadArticleAsync(genre, _reading.Token);
    }

    /// <summary>The page is being left. Everything on its way is let go, and everything it holds is
    /// dropped. A page kept live behind the wall would hold six shelves of decoded artwork for every genre
    /// anybody had opened.</summary>
    public void Close()
    {

        _reading?.Cancel();
        _reading = null;

        Genre = null;
        _articleUrl = null;

        Banner.Visibility = Visibility.Collapsed;
        Put(Banner, 0);
        BannerArt.ImageSource = null;
        PageGround.Fill = null;
        PageGround.Visibility = Visibility.Collapsed;
        PageWeight.Visibility = Visibility.Collapsed;
        PageWash.Visibility = Visibility.Collapsed;
        PageWashArt.ImageSource = null;

        // The window goes back to its own backdrop as the page goes. Closing is not the same as lending
        // nothing. A page that is open but has nothing to lend keeps the backdrop away, so stepping between
        // two pages does not flash the wallpaper. See PageBackdrop.Showing.
        PageBackdrop.Closed(this);

        AboutBlock.Visibility = Visibility.Collapsed;
        AboutMissing.Visibility = Visibility.Collapsed;
        ArticleBusy.Spinning = false;
        ShelvesBusy.Spinning = false;
        SubgenresBlock.Visibility = Visibility.Collapsed;
        SubgenresList.Children.Clear();
        _article = ArticleState.Waiting;
        _hasRelations = false;

        Show(GenreShelf.Empty);
    }

    /// <summary>The way out. What Back means is decided by the shell. A genre's page is only ever opened
    /// from the wall, but this control does not know that.</summary>
    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    // ── the shelves ──

    /// <summary>
    /// The six shelves, fetched and put up when they arrive.
    ///
    /// <para>The headings are written first, and synchronously. They are this genre's name in a sentence,
    /// and they are known the moment the page opens. So a shelf that is still being read shows a heading
    /// with a turning record under it, rather than a blank panel.</para>
    ///
    /// <para><b>Why a late answer is safe.</b> The answer is put up only if the page is still on THIS genre
    /// when it arrives. The check asks the page's current state, rather than remembering the state from
    /// when the request was made. See <see cref="StillOpen"/>. So two opens in quick succession cannot get
    /// mixed up. The first genre's shelves are dropped on arrival, because the page is no longer about that
    /// genre.</para>
    /// </summary>
    /// <param name="clear">Whether to empty the shelves before the new ones are read. True on a navigation,
    /// where what is on them belongs to the genre being left. False on a rebuild after a library change.
    /// Then they belong to the genre the reader is looking at, and they are about to be replaced by almost
    /// the same thing. Emptying them would make the page blink on every import.</param>
    private async Task FillShelvesAsync(GenreWallItem genre, bool clear, CancellationToken ct)
    {
        var spoken = RPM.Core.Metadata.Genres.Spoken(genre.Name);
        AddedHeading.Text = Loc.Format("Genres_RecentlyAdded", spoken);
        ActsHeading.Text = Loc.Format("Genres_NotableArtists", spoken);
        ErasHeading.Text = Loc.Format("Genres_Eras", spoken);

        if (ShelfLoader is not { } read)
        {
            Show(GenreShelf.Empty);
            return;
        }

        if (clear)
            Show(GenreShelf.Empty);

        try
        {
            var reading = read(genre, ShelfMetrics, ActMetrics, ct);

            // The record turns only for a wait that somebody can see. On this machine's own collection the
            // answer is back within a couple of frames. A spinner shown and taken away again in that time
            // is a blink where a shelf was about to be. That is worse than the short gap it covers.
            if (clear && await Task.WhenAny(reading, Task.Delay(SpinnerGraceMs, ct)) != reading
                      && StillOpen(genre))
                ShelvesBusy.Spinning = true;

            var shelf = await reading;
            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;

            RPM.Desktop.Diagnostics.SwitchDiag.Mark("shelves");
            Show(shelf);
            Step($"shelves up: {shelf.Added.Count} added, {shelf.Acts.Count} acts, "
               + $"{shelf.Essential.Count + shelf.Moods.Count} lists, {shelf.Eras.Count} eras");
        }
        catch (OperationCanceledException)
        {
            // The reader has opened another genre. That genre's own read fills the shelves now.
        }
        catch (Exception)
        {
            // A mirror that closes under the page leaves an empty page. It is not a fault. The head, the
            // name and the article belong to the genre, and they stand without any shelf under them.
            if (!ct.IsCancellationRequested && StillOpen(genre))
                Show(GenreShelf.Empty);
        }
        finally
        {
            // This is done unconditionally, and that is the fix. The spinner must stop in every case. If it
            // stopped only while this genre was still the one open, a reader who opened a second genre
            // during a read would leave the first read's spinner turning, and nothing afterwards would stop
            // it. Load sets the ARTICLE's record for each genre and never touches this one. Close only runs
            // when the page is put away. A spinner writes its angle thirty times a second, and every write
            // repaints the whole window (see RecordSpinner). So a stuck spinner makes the page redraw
            // itself for the rest of the session, over a read that nobody is waiting for.
            //
            // It is safe to stop a spinner that another read might want. The record only goes up after that
            // read's own grace period (see SpinnerGraceMs). That period is far longer than the gap between
            // a cancellation and this line. The worst case is a spinner that does not appear. The case it
            // replaces is a spinner that never stops.
            ShelvesBusy.Spinning = false;
        }
    }

    /// <summary>How long the shelves may take to arrive before the page says it is waiting. It is long
    /// enough that a warm collection never shows a spinner. It is short enough that a shared library, where
    /// the first read builds the whole collection's mood cut, does not look like a page that
    /// failed.</summary>
    private const int SpinnerGraceMs = 180;

    /// <summary>
    /// Puts one reading of the shelves on screen, and takes down every row that has nothing in it.
    ///
    /// <para>Rows are taken down rather than left empty. A heading over an empty shelf is a heading over a
    /// hole in the collection. A genre nobody has played has no popular songs and no lists worth
    /// generating. Its page is complete with two shelves on it.</para>
    /// </summary>
    private void Show(GenreShelf shelf)
    {
        _shelf = shelf;

        // The claim belongs to the CARDS, not to the page. It is dropped exactly when they are. Every card
        // here is newly made, since a rebuild after an import replaces all of them, covers included. A
        // claim that outlived its cards would stop every sleeve on the page from fetching its cover,
        // because the card that made the claim is gone.
        _coversAsked.Clear();

        Added.ItemsSource = shelf.Added;
        AddedSection.Visibility = Seen(shelf.Added.Count);

        Acts.ItemsSource = shelf.Acts;
        ActsSection.Visibility = Seen(shelf.Acts.Count);

        Essential.ItemsSource = shelf.Essential;
        EssentialSection.Visibility = Seen(shelf.Essential.Count);

        Moods.ItemsSource = shelf.Moods;
        MoodSection.Visibility = Seen(shelf.Moods.Count);

        FillPopular(shelf.Popular);
        FillEras(shelf.Eras);

        static Visibility Seen(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How many columns the popular card is divided into. Three is what the page's wide half is
    /// worth. One column of song rows would leave two thirds of the card empty. Fewer columns are used when
    /// there are fewer songs than columns. So two songs fill two columns, and no column is left
    /// empty.</summary>
    private const int PopularColumns = 3;

    /// <summary>The square beside each song, in points. It is small enough that a column of eight of them
    /// is about the height a column of eight text rows already is.</summary>
    private const double SongArt = 30;

    /// <summary>
    /// The songs, down the columns.
    ///
    /// <para>Built in code rather than from a template. The artist page builds its popular records this way
    /// for the same reason. The number of songs depends on what the collection returned. Each row has two
    /// lines, a square and a duration. A repeater for two dozen of those is more machinery than the thing
    /// it draws.</para>
    ///
    /// <para>The songs are filled column by column, not across. The ranking then reads down the first panel
    /// and then down the next. Read across, the eye would skip two columns between first and second.</para>
    /// </summary>
    private void FillPopular(IReadOnlyList<ArtistSong> songs)
    {
        PopularPanels.Children.Clear();
        PopularPanels.ColumnDefinitions.Clear();
        PopularSection.Visibility = songs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (songs.Count == 0)
            return;

        var columns = Math.Min(PopularColumns, songs.Count);
        var deep = (songs.Count + columns - 1) / columns;   // how far down each column goes

        for (var column = 0; column < columns; column++)
        {
            var panel = new StackPanel { Spacing = 2 };
            Grid.SetColumn(panel, column);
            PopularPanels.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PopularPanels.Children.Add(panel);
        }

        for (var i = 0; i < songs.Count; i++)
            ((StackPanel)PopularPanels.Children[i / deep]).Children.Add(SongRow(songs[i], i + 1));
    }

    /// <summary>One song: its place in the ranking, the sleeve of the record it came from, its name above
    /// whoever made it, and how long it is. The number is not decoration. A list of songs with no order
    /// shown reads as songs that happen to be next to each other. What this card says is which songs are
    /// played most.</summary>
    private Button SongRow(ArtistSong song, int place)
    {
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rank = new TextBlock
        {
            Text = place.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 11.5,
            Foreground = Ink("InkFaint"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rank, 0);
        row.Children.Add(rank);

        // The sleeve sits on a plate. So a record with no cover is a grey square of the same size, not a
        // gap that shortens the row. The picture is put into the plate when it arrives.
        var picture = new Image { Stretch = Stretch.UniformToFill };
        var art = new Border
        {
            Width = SongArt,
            Height = SongArt,
            CornerRadius = new CornerRadius(3),
            Background = Ink("InkPlate"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = picture,
        };
        Grid.SetColumn(art, 1);
        row.Children.Add(art);

        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = song.Title,
            FontSize = 12.5,
            Foreground = Ink("InkBody"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        words.Children.Add(new TextBlock
        {
            Text = song.Subtitle,
            FontSize = 11,
            Foreground = Ink("InkFaint"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        Grid.SetColumn(words, 2);
        row.Children.Add(words);

        var length = new TextBlock
        {
            Text = song.Duration,
            FontSize = 11.5,
            Foreground = Ink("InkFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(length, 3);
        row.Children.Add(length);

        var press = Pressable(row, song, Song_Click);

        // The row fills the whole column. That puts the duration against the panel's edge, not against the
        // end of the title. A button left alone is only as wide as its content.
        press.HorizontalAlignment = HorizontalAlignment.Stretch;

        _ = DressSongAsync(song, picture);
        return press;
    }

    /// <summary>
    /// The sleeve of the record one popular song came from.
    ///
    /// <para>It is not claimed through <see cref="_coversAsked"/>, which the shelves' cards share. A record
    /// can be on the newest shelf AND have a popular song. A single claim shared between them would leave
    /// whichever asked second with no picture. These are fetched at the row's edge rather than the tile's
    /// edge. So they are a different read of the same folder, and the thumbnail cache answers each one. The
    /// query behind this card returns one song per record, so no two rows here ask for the same
    /// folder.</para>
    ///
    /// <para>The picture is written to the row's own Image. The row is never recycled. It is made for this
    /// song and thrown away with it. What keeps that safe when a read lands after the page has moved on is
    /// the check below. The song has to still be on the card being drawn.</para>
    /// </summary>
    private async Task DressSongAsync(ArtistSong song, Image picture)
    {
        if (CoverLoader is not { } load)
            return;

        var token = _reading?.Token ?? CancellationToken.None;

        try
        {
            var cover = await load(song.Folder, Services.Art.ArtScaler.RowEdge, token);
            if (cover is null or { Length: 0 })
                return;

            var decoded = await _pictures.Next(() => RemoteImage.DecodeAsync(
                cover,
                Services.Art.ArtScaler.DecodeEdgeFor(SongArt, XamlRoot?.RasterizationScale ?? 1)));

            // Is this still the page's card? The shelves are replaced whole on every library change and on
            // every genre opened. A picture that lands after that belongs to a list nobody is reading. The
            // check asks the shelf as it is now, not as it was before the awaits. The shelf is a fact that
            // holds whenever it is asked.
            if (!token.IsCancellationRequested && _shelf.Popular.Contains(song))
                picture.Source = decoded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sleeve is decoration. The row keeps the plain plate it was drawn with.
        }
    }

    /// <summary>
    /// The decades, oldest first, each in its own small shelf under the one heading.
    ///
    /// <para>Built in code, because the number of decades depends on what the collection holds. One genre
    /// might have two, and another six. A shelf per decade would otherwise need a heading, a scroller and a
    /// repeater written out six times in the markup, with five of them hidden.</para>
    /// </summary>
    private void FillEras(IReadOnlyList<GenreEraShelf> eras)
    {
        Eras.Children.Clear();
        ErasSection.Visibility = eras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var shelves = new List<(ItemsRepeater Shelf, GenreEraShelf Era)>(eras.Count);

        foreach (var era in eras)
        {
            var block = new StackPanel { Spacing = 6 };
            block.Children.Add(new TextBlock
            {
                Text = era.Label,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Ink("InkQuiet"),
                Margin = new Thickness(18, 0, 18, 0),
            });

            // Built empty. The records are added afterwards. See FillErasAsync below.
            var shelf = new ItemsRepeater
            {
                ItemTemplate = (DataTemplate)Resources["SleeveTemplate"],
                Layout = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 2 },
            };
            shelf.ElementPrepared += Record_ElementPrepared;
            shelves.Add((shelf, era));

            block.Children.Add(new ScrollViewer
            {
                Content = shelf,
                HorizontalScrollMode = ScrollMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 0, 12, 14),
                BringIntoViewOnFocusChange = false,
            });

            Eras.Children.Add(block);
        }

        _ = FillErasAsync(shelves, _reading?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Hands the decades their records, one decade at a time.
    ///
    /// <para><b>Why not with the rest of the page.</b> The column these stand in is a plain scroller, not a
    /// virtualizing one. So every decade is laid out when the page is built, whether or not anybody can see
    /// it. A collection that goes back six decades would mean six shelves of sleeves measured, arranged and
    /// asked for their pictures in the frame the page opens. That frame is the one the fade in from the
    /// wall runs in, which is where a transition is most noticeable.</para>
    ///
    /// <para>So the records are handed over one at a time, at the lowest priority. Each decade's layout
    /// waits for a turn when nothing else needs the thread. The eras are the LAST thing on the page, and
    /// the reader starts at the top. By the time they scroll down to the decades, the decades are there.
    /// Nothing is dropped, and no shelf is left short. Every one of them is filled, just not all in one
    /// frame.</para>
    ///
    /// <para>It stops when the page is left, like everything else here. The token belongs to this read, so
    /// a genre closed halfway through its decades simply stops handing them over.</para>
    /// </summary>
    private async Task FillErasAsync(
        IReadOnlyList<(ItemsRepeater Shelf, GenreEraShelf Era)> shelves, CancellationToken ct)
    {
        foreach (var (shelf, era) in shelves)
        {
            if (ct.IsCancellationRequested)
                return;

            // Wait for a turn of the queue below layout and input, so a decade never lands in a frame that
            // somebody is waiting on. Where there is no queue, which is a control that is not in a window,
            // the shelves are filled where they stand. Any test of this page would want that anyway.
            if (DispatcherQueue is { } queue)
            {
                var turn = new TaskCompletionSource();
                if (queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                                     () => turn.TrySetResult()))
                    await turn.Task;
            }

            if (ct.IsCancellationRequested)
                return;

            shelf.ItemsSource = era.Records;
        }

        Step($"decades filled: {shelves.Count}");
    }

    /// <summary>The genres beside this one, each with how much of it the collection holds. A row is a door.
    /// Pressing it opens that genre's page. That is what makes the card a way through the collection, not a
    /// list of words.</summary>
    private void FillRelations(GenreWallItem genre)
    {
        SubgenresList.Children.Clear();

        var related = RelationsLoader is { } read ? read(genre) : [];
        _hasRelations = related.Count > 0;
        SubgenresBlock.Visibility = _hasRelations ? Visibility.Visible : Visibility.Collapsed;

        foreach (var relation in related)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = relation.Name,
                FontSize = 12.5,
                Foreground = Ink("InkBody"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var count = new TextBlock
            {
                Text = relation.Count,
                FontSize = 11,
                Foreground = Ink("InkFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(count, 1);
            row.Children.Add(count);

            SubgenresList.Children.Add(Pressable(row, relation.Name, Related_Click));
        }
    }

    /// <summary>One row of a card, made pressable. Two cards build their rows in code, and both need the
    /// same button around them: a plate that lights under the pointer and nothing else. Written twice, they
    /// would be two buttons that could drift apart.</summary>
    private static Button Pressable(UIElement content, object tag, RoutedEventHandler pressed)
    {
        var press = new Button
        {
            Content = content,
            Tag = tag,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5, 6, 5),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };

        press.Resources["ButtonBackgroundPointerOver"] =
            new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        press.Resources["ButtonBackgroundPressed"] =
            new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

        press.Click += pressed;
        return press;
    }

    private SolidColorBrush Ink(string key) => (SolidColorBrush)Resources[key];

    // ── the pictures, fetched as a card comes on screen ──

    // ══ what the pictures may take of the thread that draws ══
    //
    // A genre's page realizes far more cards in its first frames than a wall does in a scroll. Six shelves
    // come up together, and the eras below the fold are laid out with the rest, because the column they are
    // in is a plain scroller rather than a virtualizing one. Every one of them asks for a picture. Every
    // picture ends in a decode that can only run on the thread the page is drawn on.
    //
    // Decodes on the drawing thread can use much of the frame. They also land inside the fade that brings
    // the page in, and a fade that stalls partway looks broken. So the decodes go through one gate, paced
    // against a share of the frame. The fetches all still run at once, off this thread. That lets the page
    // fill from a cold shelf at the speed of the network, not one card at a time. See PacedPictures, and
    // GenreFaceStream, which uses the same approach for the wall this page is opened from.

    /// <summary>Ten milliseconds of every sixteen, not the wall's usual five. A wall fills in behind
    /// someone who is already reading it. This page is what they are waiting for, and the shelves are blank
    /// squares until their pictures land. So the pictures are worth more of the frame here. The album
    /// wall's loader makes the same distinction when it is the screen that is showing. See
    /// FrameBudget.</summary>
    private const double PictureShareMs = 10;

    private readonly Services.Art.PacedPictures _pictures = new(PictureShareMs);

    /// <summary>A sleeve has been realized. Its cover is fetched once, ever. The check runs on every
    /// realize, not from the template's DataContextChanged. A repeater that hands a recycled element back
    /// to the SAME card rebinds nothing, so nobody would ask.</summary>
    private void Record_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if ((args.Element as FrameworkElement)?.DataContext is ArtistRecord record)
        {
            Step($"prepared sleeve #{++_preparedSleeves} {record.Folder}");
            _ = DressRecordAsync(record);
        }
    }

    private async Task DressRecordAsync(ArtistRecord record)
    {
        // Claimed before the await. A record can be on the newest shelf AND in its decade. So two cards ask
        // for one cover, and a fast scroll can ask for the same one twice.
        if (record.HasCover || CoverLoader is not { } load || !_coversAsked.Add(record.Folder))
            return;

        var token = _reading?.Token ?? CancellationToken.None;

        try
        {
            // Every card that stands for this folder is updated, not only the card that asked. The same
            // record can be on two shelves, and only one of them did the asking. The cards are found after
            // the read has answered. The shelves are rebuilt on every library change, and a list taken
            // before the await could name cards that are no longer on the page.
            //
            // The decode is metered against the frame, and the fetch is not. See PacedPictures. If nothing
            // comes back, CoverFill records that. That is the only thing that lets a card draw its made
            // sleeve. See ArtistRecord.FigureSeed.
            await Services.Art.CoverFill.DressAsync(
                () => CardsFor(record.Folder),
                readToken => load(record.Folder, Services.Art.ArtScaler.TileEdge, readToken),
                async cover => await _pictures.Next(() => RemoteImage.DecodeAsync(
                    cover,
                    Services.Art.ArtScaler.DecodeEdgeFor(ShelfMetrics.Art, XamlRoot?.RasterizationScale ?? 1))),
                token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A sleeve is decoration. The card keeps the made figure it opened with.
        }
    }

    /// <summary>Every record card on the page, on both kinds of shelf. It is read again each time, not kept
    /// in a lookup. The shelves are rebuilt on every library change. A lookup would be a second list of
    /// cards, which could go stale against the one being drawn.</summary>
    private IEnumerable<ArtistRecord> Records()
        => _shelf.Added.Concat(_shelf.Eras.SelectMany(era => era.Records));

    /// <summary>The cards that stand for one record, on both kinds of shelf. A cover, and the answer that
    /// there is none, are both written to all of them.</summary>
    private IEnumerable<ArtistRecord> CardsFor(string folder)
        => Records().Where(c => string.Equals(c.Folder, folder, StringComparison.OrdinalIgnoreCase));

    private void Act_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if ((args.Element as FrameworkElement)?.DataContext is ArtistTile tile)
        {
            Step($"prepared act #{++_preparedActs} {tile.Artist.Name}");
            _ = DressActAsync(tile);
        }
    }

    private async Task DressActAsync(ArtistTile tile)
    {
        if (tile.Asked || PortraitLoader is not { } load)
            return;
        tile.Asked = true;

        var token = _reading?.Token ?? CancellationToken.None;

        try
        {
            var face = await load(tile.Artist, token);
            if (token.IsCancellationRequested)
                return;

            tile.Wear(face);

            // A stand-in is used when a photograph could not be fetched. The act is asked again the next
            // time its box is prepared. See ArtistFace.StandIn.
            if (face.StandIn)
                tile.Asked = false;

            // The drained copy is what a WALL of boxes draws. This shelf is a row of them beside each
            // other, and the choice of one palette for all of them is the artists wall's own.
            if ((face.Picture ?? face.Original) is { Length: > 0 } picture)
                tile.Picture = await _pictures.Next(() => RemoteImage.DecodeAsync(
                    picture,
                    Services.Art.ArtScaler.DecodeEdgeFor(
                        ActMetrics.Height, XamlRoot?.RasterizationScale ?? 1)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A box without a photograph keeps its colour and its name. That is what it is for.
        }
    }

    private void List_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if ((args.Element as FrameworkElement)?.DataContext is GenreList card)
        {
            Step($"prepared list #{++_preparedLists} {card.Name}");
            _ = DressListAsync(card);
        }
    }

    private async Task DressListAsync(GenreList card)
    {
        if (card.HasCover || ListCoverLoader is not { } load)
            return;

        var token = _reading?.Token ?? CancellationToken.None;

        try
        {
            await Services.Art.CoverFill.DressAsync(
                card,
                readToken => load(card.Id, readToken),
                async cover => await _pictures.Next(() => RemoteImage.DecodeAsync(
                    cover,
                    Services.Art.ArtScaler.DecodeEdgeFor(ShelfMetrics.Art, XamlRoot?.RasterizationScale ?? 1))),
                token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The card keeps the made figure it opened with.
        }
    }

    // ── the head's picture ──

    /// <summary>
    /// The photograph at the end of the head: the one shipped for this shelf, cut to this panel and read
    /// off the disk. See <see cref="GenrePhotos"/> and MainPage.Genres.
    ///
    /// <para>The head is finished without it. The colour and the name are the genre's own, and they arrive
    /// with the page. So a shelf with no photograph opens on a head that has no picture in it, rather than
    /// on a gap. That path is still walked and still matters. It is what a genre gets when it was added to
    /// the app before anyone chose a picture for it.</para>
    /// </summary>
    private async Task DrawBandAsync(GenreWallItem genre, CancellationToken ct)
    {
        if (BannerLoader is not { } draw)
            return;

        try
        {
            Step("picture asked for");
            if (await draw(genre, ct) is not { Length: > 0 } band)
            {
                Step("picture: there is none");
                // This shelf has no photograph shipped for it. That is an answer. The head is finished
                // without a picture, so it appears now, rather than waiting for a picture that is not
                // coming.
                Settled();
                return;
            }

            // The check is made against WHICH GENRE IS OPEN, by name, not against the token alone. It is
            // made again after each await, because the reader can act between any two of them.
            //
            // The comparison is by name, not by the item's identity. A library change re-opens the page on
            // a newly built item for the same genre (see Load). A picture still on its way for the genre
            // the reader is looking at is the picture they are waiting for. Comparing references would
            // throw it away, and nothing would ever ask again.
            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;

            var picture = await RemoteImage.DecodeAsync(band);
            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;

            // The picture is placed at full strength, not faded in on its own. The head is held down until
            // this moment (see ShowHead). So the colour, the fall and the photograph come up together, in
            // one fade of the whole band. This is how the art display behind the window brings a backdrop
            // in, and it is for the same reason. Two layers that arrive on two clocks would make a head
            // that assembles itself in front of the reader.
            //
            // The order matters as much as the fade. The layer is set to nothing BEFORE it is given the
            // picture, not after. A layer given its picture while it still has opacity shows that picture
            // at its final size, for as long as it takes to reach the line that hides it. Any frame drawn
            // in between shows the picture at full strength, and then starts its dissolve from the
            // beginning.
            Put(Banner, 0);
            BannerArt.ImageSource = picture;
            Banner.Visibility = Visibility.Visible;

            if (_headLate)
            {
                // The head is already up on its colour. The picture crossfades onto it, the way the art
                // display brings a backdrop in over the one before it.
                Step("picture crossfaded onto a head already up");
                Fade(Banner);
            }
            else
            {
                // The usual path. The head is still down, so the picture is simply placed. The whole band
                // (colour, fall and photograph) comes up in one fade below.
                Step("picture placed on a head still down");
                Put(Banner, 1);
            }

            StopWaiting();
            _headSettled = true;
            ShowHead();

            // ── and only now, the page's own background ──
            //
            // The background is set AFTER the head. That order keeps the head arriving in one piece. A page
            // that lays its background over its foreground is seen assembling itself, so the order matters
            // here. The artist page works the other way round, because its wash is computed long before the
            // page asks for it. This page builds its wash from the same bytes as the head, at the moment
            // the genre is opened. A blur and two colour reads of a full-size photograph take much longer
            // than the head's own grace period (see HeadGrace). If the background came first, the head
            // would give up waiting and show flat colour. The picture would then fade in behind it, and the
            // ground would arrive last. The reader would watch a header that paints itself two or three
            // times.
            //
            // The background belongs behind the head in any case. "The head is finished without it" is the
            // rule this page is built on. The ground under six shelves is not what a reader opening a genre
            // is waiting for.
            var (washed, tint) = await Task.Run(
                () => (Services.Art.ArtScaler.Wash(band),
                       Common.CoverColor.Dominant(band) ?? Common.CoverColor.Highlight(band)),
                ct);

            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;

            // The background uses the photograph's own colours, not the genre's. The head above already
            // carries the shelf's colour. A page washed in that colour as well would be one colour from top
            // to bottom.
            //
            // Both layers are BUILT before either is shown. The colour is a brush, and the wash is a
            // decode. They are ready a few hundred milliseconds apart. A page that showed each one as it
            // arrived would paint its background twice while the reader watched.
            var ground = Dress(tint, genre.Name);

            if (washed is { Length: > 0 })
            {
                PageWashArt.ImageSource = await RemoteImage.DecodeAsync(washed);
                if (ct.IsCancellationRequested || !StillOpen(genre))
                    return;
            }

            // Both come up together, in one turn of the dispatcher. That means one change to the page, and
            // one ground handed to the window instead of two a moment apart. Each of those would put the
            // whole window back on live glass while it settles. See FrostPlate.ArtworkSettle.
            PageGround.Visibility = ground is null ? Visibility.Collapsed : Visibility.Visible;
            PageWash.Visibility = PageWashArt.ImageSource is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            Weigh();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The picture is decoration. The page keeps the head it opened with, which is the genre's
            // colour and its name. That is a finished page.
            Settled();
        }

        // Cancellation is the one exit that does NOT settle. The reader has opened another genre, and that
        // genre's own fetch brings its head up.
        void Settled()
        {
            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;
            _headSettled = true;
            ShowHead();
        }
    }

    // ── what the page does between the press and the picture ────────────────────────────────────
    // Every step of opening a genre is stamped with its time. The head is built from five things, which
    // arrive on five different clocks: the page's own fade, the layout that gives the band its width, the
    // ground painted from that width, the photograph from the network or the cache, and the composition
    // sprite that dissolves the foot. "It flickers" is not a report that any of them can answer. The order
    // of events can answer it.
    //
    // The shelves are stamped too, for the same reason one level down. "Opening a genre is slow" is not a
    // useful report either. A line for each card realized, and one when the shelves and the decades go up,
    // shows where the page spends its time. A burst of decodes can then be told apart from the reads that
    // feed it. Read these beside the [switchdiag] line, which is the same opening measured in frames.
    //
    // Off unless RPM_HEAD_DIAG=1.
    private static readonly bool HeadDiag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_HEAD_DIAG") is "1" or "true";

    private static readonly System.Diagnostics.Stopwatch HeadClock = System.Diagnostics.Stopwatch.StartNew();
    private static double _headOpenedAt;
    private int _headLayouts;
    private int _preparedSleeves, _preparedActs, _preparedLists;

    /// <summary>One step, with how long it is since the page was asked to open.</summary>
    /// <remarks>Lines go through <see cref="RPM.Core.Diagnostics.Said"/>, not to the debug channel. That
    /// way they land in the ring and can be read back after a run with <c>scripts/rpmscope log --channel
    /// head</c>. Written straight to Debug, they reached nobody on a run with no debugger attached, which
    /// is how this page is normally run.</remarks>
    private static void Step(string what)
    {
        if (!HeadDiag)
            return;

        RPM.Core.Diagnostics.Said.Line(
            "head", $"+{HeadClock.Elapsed.TotalMilliseconds - _headOpenedAt:0}ms {what}");
    }

    /// <summary>Whether the genre that something was fetched for is the one on screen. The question is
    /// asked of the page's CURRENT state each time, not remembered from when the fetch started. That makes
    /// it safe whatever order the answers come back in.</summary>
    private bool StillOpen(GenreWallItem genre)
        => Genre is { } showing && string.Equals(showing.Name, genre.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The layers this page dissolves, and the one place any of them is written.
    ///
    /// <para>They are driven from the render tick, not from a Storyboard. A DoubleAnimation holds the
    /// property it animated, so a later <c>Opacity = 0</c> is a write that the animation keeps overriding.
    /// The layer would then stay visible when it should go down.</para>
    /// </summary>
    private Dissolve Dissolving(UIElement layer)
    {
        if (!_dissolves.TryGetValue(layer, out var tween))
            _dissolves[layer] = tween = new Dissolve(layer, "genre-head");
        return tween;
    }

    private readonly Dictionary<UIElement, Dissolve> _dissolves = [];

    /// <summary>Brings a layer up from nothing. The band arrives on the network's clock, or, on a second
    /// launch, off the disk a moment after the page opens. Either way, a picture that appears between two
    /// frames over a page that already shows its name looks like a jolt.</summary>
    private void Fade(UIElement layer, double to = 1)
        => Dissolving(layer).To(to, TimeSpan.FromMilliseconds(RevealMs));

    /// <summary>Sets a layer straight to a value, with nothing left in flight. Every place that sets
    /// Opacity goes through this.</summary>
    private void Put(UIElement layer, double to) => Dissolving(layer).Set(to);

    /// <summary>How long the band takes to come up. This is the wall's own number, so a genre opened from
    /// its row and the row it was opened from dissolve at the same speed.</summary>
    private const int RevealMs = 260;

    /// <summary>
    /// The head photograph's colour across the whole panel, or nothing where the picture had no colour to
    /// give. This is the artist page's <c>Dress</c>, using the same call underneath it.
    ///
    /// <para>It is seeded from the GENRE's name. The name is what <see cref="ArtistGround"/> reads to
    /// choose the second accent and the corner the fall comes from. So the same shelf gets the same
    /// arrangement on every machine and after every restart. The page does not rearrange itself each time
    /// it is opened.</para>
    /// </summary>
    /// <returns>The brush it built, or null where the picture had no colour to give. The caller shows it,
    /// so that the colour and the blurred picture over it appear in the same frame.</returns>
    private Brush? Dress(Windows.UI.Color? colour, string genre)
    {
        var ground = colour is { } read ? ArtistGround.From(read, genre) : null;

        PageGround.Fill = ground;
        return ground;
    }

    /// <summary>The black goes up when there is anything under it to take down. It is asked of both layers,
    /// for the reason the artist page's own <c>Weigh</c> gives. The blurred picture is laid on at more than
    /// half strength, because the black is over it. A page that got a picture but no colour would otherwise
    /// show white writing on a lit backdrop.</summary>
    private void Weigh()
    {
        PageWeight.Visibility =
            PageGround.Visibility == Visibility.Visible || PageWash.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;

        Lend();
    }

    /// <summary>
    /// Hands the page's ground to the whole window. While this genre is open and there is no artwork over
    /// the app, the app shows the shelf's colours instead of its own backdrop. This is the artist page's
    /// own <c>Lend</c>, layer for layer.
    ///
    /// <para>It reads the state of the layers themselves, not the answers that set them. That is what makes
    /// it safe to call from one place. The colour and the picture arrive on their own clocks. So what the
    /// page is showing is a question about the panel as it stands now, not about which of the two arrived
    /// last.</para>
    /// </summary>
    private void Lend()
    {
        var lit = PageGround.Visibility == Visibility.Visible;
        var washed = PageWash.Visibility == Visibility.Visible;

        PageBackdrop.Opened(this, lit || washed
            ? new PageBackdrop.Ground(
                lit ? PageGround.Fill : null,
                washed ? PageWashArt.ImageSource : null,
                PageWash.Opacity,
                Veil: 0,   // held back OVER the photograph, as on the artist page, not inside it
                PageWeight.Fill)
            : null);
    }

    // ── the article ──

    private async Task ReadArticleAsync(GenreWallItem genre, CancellationToken ct)
    {
        try
        {
            if (ArticleLoader is not { } read)
                return;

            var article = await read(genre.Name, ct);
            if (ct.IsCancellationRequested || !StillOpen(genre))
                return;

            if (article is null)
            {
                // The column was drawn for this genre because the encyclopedia has an article for it (see
                // AboutExists). So when nothing comes back, the request failed. It is not a genre with
                // nothing written about it. Saying so is better than a column of room with nothing in it.
                // The next opening of the page asks again, and the line tells the reader to do that.
                // TrackInfoService.GenreArticleAsync keeps only answers, so a failure is not remembered.
                Nothing("nothing came back");
                return;
            }

            // Set the card UP first, then fill it. Every line below writes to an element inside this card.
            // A collapsed element is skipped by the layout pass. Words written while it is down are held,
            // and the card then comes back up showing the words it was last measured with, which belong to
            // the previous genre. With the card up first, nothing between here and the end of this block
            // forces a pass. So there is no frame in which the card is up and empty.
            AboutBlock.Visibility = Visibility.Visible;

            // The encyclopedia's name for the genre, which is not always the app's. The two often differ,
            // so both are shown. For example, the band list says "Metal" while this says the article is
            // about heavy metal music.
            AboutName.Text = article.Title;
            AboutName.Visibility = string.IsNullOrWhiteSpace(article.Title)
                ? Visibility.Collapsed
                : Visibility.Visible;

            AboutProse.Text = article.Extract;

            _articleUrl = article.Url;
            AboutSourceButton.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
            AboutSourceButton.Visibility = string.IsNullOrWhiteSpace(article.Url)
                ? Visibility.Collapsed
                : Visibility.Visible;

            _article = ArticleState.Arrived;
            Step($"article placed on {genre.Name}: \"{article.Title}\", "
               + $"{article.Extract?.Length ?? 0} characters");
            RPM.Desktop.Diagnostics.SwitchDiag.Mark("article");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A request that threw gives the reader the same news as one that came back empty. The column
            // is drawn because the encyclopedia names this genre, so an empty result is a failure, not a
            // fact about the music. Without a message, a column with nothing in it looks like a page that
            // is still loading. That is the usual case in a small library, where the related shelves are
            // empty. So the line says what happened.
            if (!ct.IsCancellationRequested && StillOpen(genre))
                Nothing(ex.GetType().Name);
            else
                Step($"article: {ex.GetType().Name}");
        }
        finally
        {
            // Stopped in every case, for the reason the shelves' own record gives. A spinner belongs to the
            // page. One left turning for a genre nobody is reading any more repaints the window thirty
            // times a second. Load sets this one again for the genre that IS open.
            ArticleBusy.Spinning = false;
        }

        // Both ways of learning there is no article do the same two things, so they are one step here. The
        // line is shown in either case. The column may be standing for its related genres, and there the
        // line explains the gap where the card would be. Where the column stands for the article alone,
        // SettleAbout takes it away, and the line goes with it.
        void Nothing(string why)
        {
            _article = ArticleState.Nothing;
            AboutMissing.Visibility = Visibility.Visible;
            SettleAbout();
            Step($"article: {why}" + (AboutWorthStanding ? "" : ", column folded"));
        }
    }

    private async void AboutSource_Click(object sender, RoutedEventArgs e)
    {
        // http or https only: the address came with the article (see WebLink).
        if (RPM.Core.Net.WebLink.Parse(_articleUrl) is not { } link)
            return;

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(link.Uri);
        }
        catch (Exception)
        {
            // No browser, or the shell refused. Nothing on this page depends on it.
        }
    }

    // ── the presses ──

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ArtistRecord record })
            OpenRecord?.Invoke(record.Folder);
    }

    private void Act_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ArtistTile tile })
            ArtistOpened?.Invoke(tile.Artist);
    }

    private void List_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GenreList card })
            OpenList?.Invoke(card.Id);
    }

    private void Song_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ArtistSong song })
            PlaySong?.Invoke(song);
    }

    private void PopularPlayAll_Click(object sender, RoutedEventArgs e)
    {
        if (_shelf.Popular.Count > 0)
            PlaySongs?.Invoke(_shelf.Popular);
    }

    private void Related_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string genre })
            RelatedOpened?.Invoke(genre);
    }

    // ── the layout ──

    /// <summary>
    /// The page's shape at a panel this wide. It uses the artist page's own arithmetic
    /// (<see cref="ArtistPageLayout.Columns"/>), so the two pages fold at the same width and read as one
    /// page. A reader who narrows the panel should not see one page fold while the other does not.
    /// </summary>
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // A panel that is collapsed, or laid out for the first time, reports no width. Folding on that
        // would take the column apart every time the view is hidden.
        if (e.NewSize.Width <= 0)
            return;

        // The width is held, not read back. The other thing that settles the column is a genre being
        // opened, and that can happen before this page has been measured.
        _pageWidth = e.NewSize.Width;
        SettleAbout();
    }

    // ── where the genre's own column goes ─────────────────────────────────────────────────────────
    //
    // Two independent facts decide it. They arrive on two clocks. One is how wide the panel is, which
    // changes as the reader drags. The other is which genre is open, which changes when they press one.
    // Either can arrive first, and either can arrive again without the other changing. So neither is
    // handled as a one-time EVENT. Both are held as state, and the placement is worked out from both
    // whenever either one arrives. That is the reason this is not two handlers that each do half of the
    // job.

    /// <summary>Where the genre's column is standing.</summary>
    private enum AboutPlace
    {
        /// <summary>In its own column beside the shelves, scrolling on its own clock.</summary>
        Beside,

        /// <summary>Above the shelves, inside their scroller. A panel too narrow to hold two columns gets
        /// this.</summary>
        Above,

        /// <summary>Nowhere. A shelf with nothing to say about itself gets this. See
        /// <see cref="AboutExists"/>.</summary>
        Gone,
    }

    private AboutPlace _about = AboutPlace.Beside;

    /// <summary>Whether the genre now open has anything for the column. It is held rather than asked again,
    /// because the panel is resized far more often than a genre is opened.</summary>
    private bool _hasAbout = true;

    /// <summary>How the article's fetch has gone so far.</summary>
    private enum ArticleState
    {
        /// <summary>Asked for, not back yet. The column stands with the record turning in it.</summary>
        Waiting,

        /// <summary>In the column.</summary>
        Arrived,

        /// <summary>Came back empty, or threw. There is no article and there is not going to be
        /// one.</summary>
        Nothing,
    }

    /// <summary>Where the article has got to. It is held rather than read off the card's visibility. A
    /// resize that lands at any moment then settles the column from the same facts the fetch
    /// uses.</summary>
    private ArticleState _article = ArticleState.Waiting;

    /// <summary>Whether the related-genres card has rows in it. This is the other thing the column can
    /// hold, so it decides half of whether the column is worth standing.</summary>
    private bool _hasRelations;

    /// <summary>
    /// Whether the column has anything in it to read.
    ///
    /// <para>The article decides it until the article is known not to be coming. After that, the related
    /// genres decide. Take a genre that the encyclopedia describes, whose article fails to arrive, in a
    /// library with nothing next to it. Its column would be empty. An empty column reads as a page still
    /// loading, which is how it looked while the API was refusing the request.</para>
    /// </summary>
    private bool AboutWorthStanding =>
        ColumnWorthStanding(_hasAbout, _article == ArticleState.Nothing, _hasRelations);

    /// <summary>The rule itself, kept away from the fields and the control. It can then be read and tested
    /// as the sentence it is.</summary>
    /// <param name="described">Whether the encyclopedia has an article named for this genre at all. See
    /// <see cref="AboutExists"/>, which is answered before anything is fetched.</param>
    /// <param name="articleGone">Whether the article is known not to be coming. It is false while the
    /// article is still on its way. That keeps the column up, with the record turning in it.</param>
    /// <param name="hasRelations">Whether the related-genres card has rows.</param>
    public static bool ColumnWorthStanding(bool described, bool articleGone, bool hasRelations)
        => described && (!articleGone || hasRelations);

    /// <summary>The panel's width as last measured, or zero before it ever has been.</summary>
    private double _pageWidth;

    /// <summary>
    /// Puts the column where the genre and the panel together say it goes, and writes its width.
    ///
    /// <para>It is called from both of the things that can change the answer, and it is safe to call from
    /// either at any time. It compares against where the column already is, and does nothing when that is
    /// where it belongs. This matters. The placement MOVES the panel between two parents, and doing that
    /// twice would put the panel in one of them twice.</para>
    /// </summary>
    private void SettleAbout()
    {
        // The page has not been measured yet. The genre can be loaded before this page has ever had a size.
        // Folding on a width of nothing would take the column apart every time the view is hidden. The
        // first real measurement settles it.
        if (_pageWidth <= 0)
            return;

        var (fold, width) = ArtistPageLayout.Columns(_pageWidth);
        var wanted = !AboutWorthStanding ? AboutPlace.Gone
                   : fold ? AboutPlace.Above
                   : AboutPlace.Beside;

        if (wanted != _about)
        {
            _about = wanted;
            PlaceAbout(wanted);
        }

        // Written on every pixel of the drag, and only where it means something. The other two places put
        // the column at nothing. Writing a width over that would bring the column back.
        if (wanted == AboutPlace.Beside)
            AboutColumn.Width = new GridLength(width);
    }

    /// <summary>
    /// Moves the column between its three places. The width it comes back at is not decided here. See
    /// <see cref="SettleAbout"/>, so there is one place that answers that question.
    ///
    /// <para><b>The panel always has a parent when this returns. That is not a tidiness rule.</b> The
    /// things inside it, such as the article card, the turning record and the related genres, are named in
    /// the markup. A named element is reached through the tree it is in. If the panel had no parent, every
    /// one of those names would return null. That is not a layout that looks wrong. It is a
    /// <c>NullReferenceException</c> the next time anything touches one. <see cref="Close"/> touches four
    /// of them, so closing the page would throw.</para>
    ///
    /// <para>So <see cref="AboutPlace.Gone"/> does not take the panel out of the scroller. It leaves the
    /// panel where <see cref="AboutPlace.Beside"/> has it. It takes away the two things that make it
    /// visible: the scroller, and the column's width. Only <see cref="AboutPlace.Above"/> ever moves the
    /// panel, and it hands the panel to its new parent in the same step.</para>
    /// </summary>
    private void PlaceAbout(AboutPlace place)
    {
        Step($"about column → {place} (genre {Genre?.Name ?? "none"}, article {_article}, "
           + $"relations {_hasRelations}, width {_pageWidth:0})");

        switch (place)
        {
            case AboutPlace.Beside:
                Body.Children.Remove(AboutPanel);
                AboutScroll.Content = AboutPanel;
                AboutScroll.Visibility = Visibility.Visible;
                Body.Margin = new Thickness(0, 18, 0, 0);
                Remeasure();
                break;

            case AboutPlace.Above:
                // Out of the scroller and into the shelves, back to back, and before anything else. A child
                // cannot have two parents, so these two steps cannot be one statement. Between them the
                // panel has no parent, which is the state that makes its named children null. The lines
                // that follow can each cause a layout pass, so none of them goes between the two steps.
                AboutScroll.Content = null;
                Body.Children.Insert(0, AboutPanel);

                AboutScroll.Visibility = Visibility.Collapsed;
                AboutColumn.Width = new GridLength(0);
                // No top margin. The column is now the first thing in the scroller, and it has its own
                // margin.
                Body.Margin = new Thickness(0);
                Remeasure();
                break;

            case AboutPlace.Gone:
                // The panel stays in the scroller, and the scroller is not shown. Nothing here loads a
                // picture or fetches data. The article is never requested for a genre that has none. The
                // panel costs only a collapsed element.
                Body.Children.Remove(AboutPanel);
                AboutScroll.Content = AboutPanel;
                AboutScroll.Visibility = Visibility.Collapsed;
                AboutColumn.Width = new GridLength(0);
                // The shelves keep the space that the column's own scroller gave them. So a genre with a
                // column and one without start their first shelf on the same line.
                Body.Margin = new Thickness(0, 18, 0, 0);
                break;
        }

        // When the panel comes back from Gone, the two lines above do nothing. The panel is already the
        // scroller's content, and it is already out of Body. So only a Visibility and a column width
        // change. Neither of those re-measures a subtree that was collapsed at a width of nothing. The
        // scroller would then draw the last measured words, which can belong to the previous genre, even
        // though the card itself holds the right words. Asking for a new pass puts the two back together.
        void Remeasure()
        {
            AboutPanel.InvalidateMeasure();
            AboutScroll.InvalidateMeasure();
        }
    }

    /// <summary>The sleeve every record on every shelf is drawn at, sized from the shelves' own width. It
    /// uses the album wall's rounding, so a record here and the same record on the wall are one
    /// square.</summary>
    private void BodyScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;

        var (stride, art) = ArtistPageLayout.Sleeve(e.NewSize.Width);
        ShelfMetrics.Set(stride, art);

        // The acts' box is sixteen by nine. It has a different shape from a sleeve, so it gets its own
        // number. It is a little wider than a record's card, because a name reads across the MIDDLE of the
        // box. A box the width of a sleeve would put most names on two lines.
        ActMetrics.Set(Math.Round(stride * ActShare));
    }

    /// <summary>How much wider an act's box is than a record's card. A box is 16:9 and carries a name
    /// across its middle. A sleeve is a square with the name under it.</summary>
    private const double ActShare = 1.35;

    /// <summary>
    /// How tall the band stands, worked out from how wide the page is.
    ///
    /// <para>The height is a share of the width, not a fixed height. This is not a matter of taste. The
    /// picture is drawn at one shape, 1600 by 220, and it is shown cropped to FILL. A box of the wrong
    /// shape does not letterbox the picture. It crops it. If the box is too short, the soft edges are the
    /// first thing cut off, and those edges are what the band is drawn for. If it is too tall, the faces
    /// lose their heads. With the same shape at every width, nothing is cropped.</para>
    ///
    /// <para>The height is held between a floor and a ceiling. A pane can be a third of a laptop screen, or
    /// the whole of a 4K one. Below the floor, the name is taller than the band it stands in. Above the
    /// ceiling, the page opens on a picture with its shelves pushed off the bottom.</para>
    /// </summary>
    private void BannerBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 1)
            return;

        Step($"band measured {e.NewSize.Width:0}x{e.NewSize.Height:0} (layout {++_headLayouts})");

        // How far the genre's colour reaches in over the photograph. This is a fixed number of POINTS,
        // never a share of the band. What the colour has to cover is the name, and the name is a fixed
        // width too (see NameEdge). A scrim measured as a share of the band would be solid a foot past the
        // word on a wide monitor. On a narrow one, it would stop halfway through the word. The second case
        // reads a genre's name off a photograph.
        //
        // The scrim is held inside the band, because a scrim wider than the page leaves a page with no
        // picture on it. When the band clips it, the colour has less room to let go in, not less room to be
        // solid. See PaintHead, which gives the fall the name's edge as a share of whatever this came out
        // as.
        BannerScrim.Width = Math.Round(Math.Min(ScrimReach, e.NewSize.Width));

        // The head is painted and brought up here, because this is the first moment its own numbers are
        // known. See ShowHead.
        ShowHead();
    }

    /// <summary>The shelf the head is currently wearing. It is held because the ground has to be rebuilt on
    /// every resize, and the mark is what it is built from.</summary>
    private GenreWordmark? _head;

    /// <summary>The colour the head is drawn in. It is held for the same reason: the ground is rebuilt on
    /// every resize, and this is the other half of what it is built from. See
    /// <see cref="AccentOnTheWall"/>.</summary>
    private GenreAccent _accent;

    /// <summary>
    /// Paints the head at the band's own numbers. The first time those numbers exist, it brings the whole
    /// head up together.
    ///
    /// <para>The band is a collapsed control until the reader opens a genre. A collapsed control has no
    /// width. So everything measured from it is nothing until the first layout pass. If the head were
    /// painted anyway, it would come up as flat colour and then correct itself. The ground's turn would
    /// jump to where the panel actually starts, and the panel would jump from nothing to its width. The
    /// picture, if it was already in hand, would appear in the middle of that. So the head is held down
    /// until the numbers are real. Then the colour, the fall and the panel arrive in the same frame.</para>
    ///
    /// <para>The picture still arrives when it arrives. It comes off the network the first time a genre is
    /// opened, and out of the cache every time after that. The cached case is the same frame.</para>
    /// </summary>
    private void ShowHead()
    {
        if (BannerBox.ActualWidth < 1)
        {
            Step("show head: not measured yet");
            return;      // the layout pass that measures it calls this again
        }

        PaintHead();

        if (!_headSettled)
        {
            Step("show head: painted, still waiting for the picture");
            WaitForPicture();
            return;      // the answer calls this again, or the grace below does
        }

        if (BannerBox.Opacity < 1)
        {
            Step("show head: UP");
            Dissolving(BannerBox).To(1, TimeSpan.FromMilliseconds(RevealMs));
        }
    }

    /// <summary>Whether the head's picture has been settled: it has arrived, or it has been found not to
    /// exist. The head does not come up before then. A colour that appears, followed by a photograph that
    /// appears after it, is two arrivals. The reader sees the second one as the head correcting
    /// itself.</summary>
    private bool _headSettled;

    /// <summary>
    /// How long the head waits for its picture before it comes up without one.
    ///
    /// <para>It should not need to wait. The wall prepares the head's cut for every genre whose card it has
    /// drawn (see MainPage.Genres), so opening a genre usually finds its picture already made. The head
    /// then arrives about 146 ms after the press, before the page's own fade lands at about 211 ms. The
    /// grace period is for a genre whose card the reader never scrolled past. There, the answer needs a
    /// fetch and two model runs. A head that waited for that would make the page open on a hole.</para>
    ///
    /// <para>After the grace period, the head comes up on its colour. The picture is CROSSFADED in when it
    /// lands. That is what the art display behind the window does with a backdrop. It is a different thing
    /// from the head assembling itself. One is a photograph arriving on a finished head. The other is a
    /// head being built in front of the reader.</para>
    /// </summary>
    private static readonly TimeSpan HeadGrace = TimeSpan.FromMilliseconds(180);

    private DispatcherTimer? _headWait;

    /// <summary>Starts the grace, once per opening.</summary>
    private void WaitForPicture()
    {
        if (_headWait is not null)
            return;

        _headWait = new DispatcherTimer { Interval = HeadGrace };
        _headWait.Tick += (_, _) =>
        {
            StopWaiting();

            if (_headSettled || BannerBox.Opacity >= 1)
                return;

            Step("grace over: head up without its picture");
            _headSettled = true;      // So the picture, when it lands, crossfades onto a finished head
            _headLate = true;
            ShowHead();
        };
        _headWait.Start();
    }

    private void StopWaiting()
    {
        _headWait?.Stop();
        _headWait = null;
    }

    /// <summary>Whether the head came up before its picture did. This decides how the picture arrives:
    /// placed on a head that is still down, or faded onto a head that is already up.</summary>
    private bool _headLate;

    /// <summary>
    /// The ground under the head. It deepens across the band and finishes exactly where the scrim over the
    /// photograph lets go.
    ///
    /// <para>The two have to agree, or there is a visible line. The scrim is laid in the DEEPENED colour
    /// (see <see cref="GenreWordmarks.OverTheBand"/>). So the ground must have finished deepening by the
    /// time the scrim is carrying the colour on its own. Anything else steps. A lit ground meeting a deep
    /// scrim is a line drawn down the page.</para>
    ///
    /// <para>This matters with the photograph in place. It matters MORE without one. A shelf with no
    /// picture is nothing but these two layers, and they have to read as one fall from the name to the far
    /// edge.</para>
    /// </summary>
    private void PaintHead()
    {
        if (_head is null)
            return;

        // The width is read from the band itself, not from the layers. So this does not depend on being
        // called after the line that sizes them. Two readings of one fact are two chances to disagree, and
        // the one that disagrees is the one that shows.
        var band = BannerBox.ActualWidth;

        // Nothing is repainted for a pass that did not change that width. Both brushes here are built
        // fresh, because their stops move with the pane. So painting on every layout pass would make two
        // gradients and write two fills per pass. A layout pass is not a rare thing. The page raises one
        // for a scroll, a shelf filling in, and a column settling. The head was being redrawn through all
        // of them, which is what a header repainting itself looks like.
        if (Math.Abs(band - _paintedAt) < 0.5 && _paintedFor == _accent)
            return;

        _paintedAt = band;
        _paintedFor = _accent;

        var scrim = Math.Min(ScrimReach, band);

        // The head's ground is a room, not a ramp across the band. So there is no turn to work out. A
        // room has no near end and far end to line up with the scrim. What the scrim lets through is
        // decided by NamePeak. The wall's cards decide it the same way.
        var shelf = Genre?.Name ?? "";
        BannerGround.Palette = GenreWordmarks.Room(_accent);
        BannerGround.Apart = Apart(shelf);
        BannerGround.Zoom = HeadZoom;
        BannerGround.Shift = GenreTile.Scatter(shelf);
        BannerGround.Visibility = Visibility.Visible;

        // …and the scrim. It is THE SAME ROOM AGAIN, laid over the photograph. There is one room underneath
        // and a copy over it, so the picture's leading edge has nothing happening at it. The palette and
        // the seed are the same as the ground above. That is what makes the copy sit exactly on the
        // original.
        //
        // The point where the scrim stops being solid is the name's edge, as a share of the room the scrim
        // actually got. On an ordinary pane that is half of it. On a pane narrow enough to clip the scrim,
        // it is more. That makes the fall steeper. The name keeps its ground.
        BannerScrim.Palette = GenreWordmarks.Room(_accent);
        BannerScrim.Apart = Apart(shelf);
        BannerScrim.Zoom = HeadZoom;
        BannerScrim.Shift = GenreTile.Scatter(shelf);
        BannerScrim.Fade = new Windows.Foundation.Point(scrim > 1 ? NameEdge / scrim : 1, 1);
        BannerScrim.Visibility = Visibility.Visible;
    }

    /// <summary>The width the head's two layers were last built for, and the colour they were built in.
    /// Both are what those brushes are made of. A pass that moved neither has nothing to repaint. See
    /// <see cref="PaintHead"/>.</summary>
    private double _paintedAt = -1;

    /// <summary>
    /// How close in the head is on the room.
    /// </summary>
    /// <remarks>
    /// Closer than a card, and for a reason that the two shapes give. The room is scaled by HEIGHT. So a
    /// band a hand high and a window wide shows several times as much of the plane across it as a card
    /// does. At the card's own zoom, a head is a wash with figures too small to read as anything.
    ///
    /// <para>Both layers of the head use this zoom, and they must. The scrim is a copy of the ground laid
    /// over the photograph. A copy at a different zoom is a different picture. That brings back the seam at
    /// the picture's leading edge, which <c>CardRoom.Fade</c> exists to remove.</para>
    /// </remarks>
    private const double HeadZoom = 2.6;

    /// <summary>Where in the room's own drift this shelf is caught, worked out from its NAME. It uses the
    /// same fact and the same arithmetic as <c>GenreTile.Apart</c>, so a genre's page and its card on the
    /// wall are caught in the same pose. It is a stable hash, not <c>string.GetHashCode</c>. That one is
    /// seeded per process, and it would move the pose on every run.</summary>
    private static int Apart(string genre)
    {
        var roll = 2166136261u;
        foreach (var ch in genre)
            roll = (roll ^ ch) * 16777619u;

        return (int)(roll % 4096);
    }

    private GenreAccent _paintedFor;

    /// <summary>
    /// How tall the head stands: exactly two tabs of the library rail beside it
    /// (<see cref="RightPane.RightRail.OneTab"/>).
    ///
    /// <para><b>A number, not a share of the width.</b> There are two reasons. The first is what a reader
    /// sees. The rail runs down the same card. A header that ends on one of its lines reads as part of the
    /// same grid. One that ends a few points off reads as nearly aligned, which is worse than either. The
    /// second is that a height worked out from the width would have to be WRITTEN from the band's own size
    /// handler. That write causes the layout pass that calls it again, and it repaints the whole head on
    /// every one of them.</para>
    /// </summary>
    internal const double BandHeight = RightPane.RightRail.OneTab * 2;

    /// <summary>
    /// How far in over the photograph the genre's colour reaches, in points.
    ///
    /// <para>The photograph runs the whole width of the band. It is the head, not a panel at the end of
    /// one. What keeps the head belonging to a shelf is the palette's own ramp, laid over the left of the
    /// band. It is solid where the name stands, and it lets go entirely by the time it is past. So this
    /// number is the colour's reach, not a picture's width.</para>
    ///
    /// <para><b>Points, not a share of anything.</b> It has one job, which is to carry the name. The name
    /// is <see cref="NameEdge"/> points wide on every pane. So this is that, plus the room a fall needs to
    /// disappear in. That is about two hundred and twenty points. It is far enough that there is no edge to
    /// see, and near enough that the photograph starts on the same screen.</para>
    ///
    /// <para>At a width of a thousand points, the narrowest a reader normally opens a genre at, this leaves
    /// a third of the band as photograph. That is the number that stops the reach growing, and a test holds
    /// it.</para>
    /// </summary>
    internal const double ScrimReach = 640;

    /// <summary>
    /// Where the name's block ends, in points from the left of the band: the head's own inset, the way out,
    /// the space after it, and the widest a wordmark is ever drawn here.
    ///
    /// <para>It is derived, not measured, and not typed in as a number. The layout is fixed. The grid has
    /// <c>Margin="28,28,28,0"</c>, a 32-point key, and the same 28 points again after it. So this is that
    /// arithmetic, written once, next to the number the colour is sized from. Measuring it off the live
    /// element would mean reading a layout from inside the pass that produces it.</para>
    /// </summary>
    internal const double NameEdge = 28 + 32 + 28 + NameRoom;

    /// <summary>
    /// The head of the page, dressed as that genre's card: its colour, its two falls, and its name in the
    /// face it is set in.
    ///
    /// <para>Every layer comes from <see cref="GenreWordmarks"/>. That is the same door the wall's card
    /// goes through. So the page a card opens is that card, and the two cannot drift apart in two files.
    /// The name is flushed LEFT, as on a card in the left column, because the picture is at the other
    /// end.</para>
    ///
    /// <para>It is sized here, not in the markup, because a wordmark's shape belongs to the name, not to
    /// the band. "Pop" is nearly square. "Soundtrack" is five times as wide as it is tall. Both have to fit
    /// one band of a fixed height, without either being set at the size of a caption. So each name is given
    /// the largest box of its own proportions that the band allows.</para>
    /// </summary>
    private void ShowWordmark(string genre)
    {
        var mark = GenreWordmarks.For(genre);
        if (mark is null)
        {
            // Only ever a genre added to the list without a run of the generator. The page shows its name
            // in the app's own face, on the app's own plate, rather than showing nothing.
            BannerGround.Visibility = Visibility.Collapsed;
            BannerScrim.Visibility = Visibility.Collapsed;
            BannerDrop.Fill = null;
            TitleMark.Visibility = Visibility.Collapsed;
            TitleMark.Source = null;
            Title.Text = genre;
            Title.Foreground = Paper;
            Title.Visibility = Visibility.Visible;
            Subtitle.Foreground = PaperFaint;
            return;
        }

        _head = mark;

        // The colour the wall drew this shelf in, so the head and the card the reader pressed are one
        // colour. If the wall has no card for this shelf, the head uses the genre's own colour.
        _accent = AccentOnTheWall?.Invoke(genre) ?? GenreAccent.Of(mark);
        _paintedAt = -1;   // A new shelf, so the head is repainted even where the band did not move

        // The head is held down until the band has been measured. Then it comes up in one piece. See
        // ShowHead. A head painted before its own width is known is painted with the wrong numbers, and it
        // corrects itself a frame later. The reader sees the colour arrive first. The picture and the fall
        // then snap into place behind it.
        Put(BannerBox, 0);
        _headSettled = false;
        _headLate = false;
        StopWaiting();
        Step("head put down");
        ShowHead();
        BannerDrop.Fill = GenreWordmarks.Drop(_accent);

        var ink = new SolidColorBrush(GenreWordmarks.InkOn(_accent));
        var width = Math.Min(NameRoom, NameHeight * mark.Aspect);

        // Drawn once, at the size it is shown, not carried as an outline. See GenreWordmarks.Lettering. The
        // band has one name where the wall has many, and the cost per frame is the same. The renderer
        // strokes a shape's own outline and does path arithmetic over it on every frame that shape moves.
        // This page scrolls, so that cost matters here too.
        var box = Math.Round(width);
        var tall = width / mark.Aspect;
        var scale = XamlRoot?.RasterizationScale ?? 1;

        TitleMark.Source = GenreWordmarks.Lettering(
            mark, _accent, GenreFlush.Left,
            (int)Math.Round(box * scale), (int)Math.Round(tall * scale));
        TitleMark.Width = box;
        TitleMark.Height = tall;
        TitleMark.Visibility = Visibility.Visible;

        Title.Text = genre;                       // Still set, for anything that reads the page aloud
        Title.Visibility = Visibility.Collapsed;

        // The count under the name is written in the same ink as the name, but softened. On the pale
        // shelves that is a dark grey on parchment. On the rest it is white at two thirds strength.
        Subtitle.Foreground = new SolidColorBrush(
            Windows.UI.Color.FromArgb(0xB4, ink.Color.R, ink.Color.G, ink.Color.B));
    }

    private static readonly SolidColorBrush Paper =
        new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static readonly SolidColorBrush PaperFaint =
        new(Windows.UI.Color.FromArgb(0xAE, 0xFF, 0xFF, 0xFF));

    /// <summary>How much of the band the name may take, across and down. The band holds the back key at one
    /// end and the picture at the other. These are the room left between them. The card's own shares (see
    /// <see cref="Models.GenreCardMetrics"/>) would make the page's name as small as the name on a wall
    /// card.</summary>
    private const double NameRoom = 330;
    private const double NameHeight = 42;

}

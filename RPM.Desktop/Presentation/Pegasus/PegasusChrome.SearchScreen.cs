using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Radio;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Art;
using Windows.UI;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The search the side menu's Search opens: the Android TV app's search screen (SearchScreen.kt), in the
/// look of radio's search. A shade over the room, a column half the screen wide (never under 560), the
/// field in the light glass with the go key beside it, and under it a card of the same glass listing what
/// matches, under a heading for each kind: Artists, Albums, Tracks, then Radio stations. The field stands in
/// the middle of the screen until there is something to show, then moves to the top.
///
/// <list type="bullet">
/// <item><b>The collection is searched the way the desktop searches it.</b> The mirror's own search
/// (LibraryDatabase.Query: one substring match over file name, artist, title, album and album artist, the
/// same predicate the wall's search narrows by) is asked 150 ms after the typing stops, as on the TV. What
/// it finds is sorted into kinds: an album where its title or its artist holds the words, a track where its
/// title or its artist does. The artists are the artists list's own (the shelf read off the collection, see
/// PegasusChrome.ArtistsGenres), matched as that list's search matches them (ArtistWall.Matches: case,
/// accents and punctuation folded). Each kind is ordered and cut by the TV's rule (<see cref="SearchRank"/>):
/// 10 artists, 20 albums, 30 tracks.</item>
/// <item><b>The stations are radio's search's.</b> A copy of the radio pane's model of its own, as radio's
/// search screen has (PegasusChrome.RadioSearch): the directory is asked 450 ms after the typing stops, one
/// row per station, the hidden ones taken out. The first 20 are listed.</item>
/// <item><b>The two answers arrive in either order.</b> While a new one is on its way, the last one stays up.
/// The card shows the turning record while nothing has arrived, "Nothing matches your search." when the
/// collection and the directory both found nothing, and otherwise the list. The stations' heading is shown
/// while they are asked for, with the record turning under it, or with the failure, so the collection's rows
/// are not held back for the directory.</item>
/// <item><b>OK</b> on an artist opens their page and on an album its record screen; Back comes back here to
/// the row. OK on a track plays it alone, and on a station tunes it; both go to the player, as on the TV. A
/// held OK or the Menu key on a track opens the song's menu (see TrackMenu), and on a station the station's
/// menu (see StationMenu). Rows are <see cref="RemoteLine"/>s, so the host's held-OK and Menu key work on
/// them as on a record's track lines.</item>
/// <item><b>The field.</b> It has the focus when the screen opens, so typing goes straight into it from a
/// keyboard. OK in it, or the go key, goes to the first result (or searches at once when there is none yet).
/// Down from it goes to the first result, and Up from the first result comes back. Back from the results
/// returns to the field, Back in a field with words in it empties it, and Back in an empty field leaves for
/// home (see <see cref="SearchTakesBack"/>).</item>
/// </list>
///
/// <para><b>One of the trail's screens</b> (see PegasusChrome.ArtistsGenres), like the playlists screen: the
/// side menu starts a trail with it, an artist's page opened from it stands on the trail above it, and Back
/// on it goes home. Each visit from the menu starts with an empty field, as the TV's screen does.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    private Fader _searchFade = null!;

    /// <summary>The words searched for, trimmed. Empty while the field is.</summary>
    private string _searchQuery = "";

    /// <summary>The number of the collection search last asked. An answer to an older one is dropped.</summary>
    private int _searchRead;

    /// <summary>What the collection last answered, or null while nothing has (or the field is empty).
    /// Kept up while the answer to newer words is on its way.</summary>
    private SearchFound? _searchFound;

    /// <summary>The answer the collection's rows on screen were built from.</summary>
    private SearchFound? _searchLibraryBuilt;

    /// <summary>What the stations' rows on screen were built from, as a key, so a model change that says
    /// the same does not build them again.</summary>
    private string? _searchStationsBuilt;

    private DispatcherQueueTimer? _searchSettle;

    /// <summary>The search's own copy of the radio pane's model, made the first time the screen is
    /// used.</summary>
    private RadioModel? _searchStations;

    /// <summary>Whether the directory has answered since the screen opened. Before it has, the stations count
    /// as not asked yet, as on the TV, where they are idle until the typing pause has passed.</summary>
    private bool _searchStationsAnswered;

    /// <summary>The rows on screen, for their sizes, and the headings. Each part keeps its own.</summary>
    private readonly List<SearchRow> _searchLibraryRows = [];
    private readonly List<SearchRow> _searchStationRows = [];
    private readonly List<TextBlock> _searchHeadings = [];

    /// <summary>The number the rows on screen were built under. A picture that lands for an older build is
    /// dropped.</summary>
    private int _searchBuilt;

    private readonly PictureQueue _searchPictures = new(3);

    /// <summary>The stations' logos, queued apart from the collection's pictures, so a late answer from
    /// either side does not drop the other's.</summary>
    private readonly PictureQueue _searchLogos = new(3);

    /// <summary>The height and scale SizeSearchScreen last had, so the field can be placed without a
    /// resize.</summary>
    private double _searchHeight, _searchScale = 1;

    /// <summary>The side of a row's picture, the TV's 64.</summary>
    private const double SearchPicture = 64;

    /// <summary>How long after the last keystroke the collection is searched, as on the TV.</summary>
    private static readonly TimeSpan SearchPause = TimeSpan.FromMilliseconds(150);

    /// <summary>An album found: its folder, its title and whose it is.</summary>
    private sealed record AlbumFound(string Folder, string Title, string Artist);

    /// <summary>What one search of the collection found, each kind best first and cut.</summary>
    private sealed record SearchFound(
        IReadOnlyList<ArtistWallItem> Artists, IReadOnlyList<AlbumFound> Albums, IReadOnlyList<LibraryTrack> Tracks, string Root)
    {
        public bool IsEmpty => Artists.Count == 0 && Albums.Count == 0 && Tracks.Count == 0;
    }

    /// <summary>One row's parts, sized together in SizeSearchRows.</summary>
    private sealed record SearchRow(
        RemoteLine Line, Grid Inside, FrameworkElement Picture, StackPanel Words, TextBlock Title, TextBlock? Under, StationWell? Well);

    /// <summary>Builds the parts that stand for the life of the chrome. From BuildArtistsGenres.</summary>
    private void BuildSearchScreen() => _searchFade = new Fader(SearchScreen);

    /// <summary>
    /// The side menu's Search (SideMenuSearch_Click): the trail starts again with the search screen, with
    /// an empty field that takes the keyboard. The artists are read again, as the artists list reads them,
    /// for the artists the search lists and the pages it opens.
    /// </summary>
    private void OpenSearchScreen()
    {
        if (!LibraryUp || RecordUp)
            return;

        ClearSearch();
        _trail.Clear();
        _trail.Add(TrailStop.Search());
        ShowTrailTop(focus: null);
        FocusFieldLater(SearchScreenBox);   // replaces the screen's own request, for the pointer too
        ReadShelfAsync();
    }

    /// <summary>The field emptied and everything found forgotten, without a search.</summary>
    private void ClearSearch()
    {
        _searchStationsAnswered = false;
        if (SearchScreenBox.Text.Length > 0)
            SearchScreenBox.Text = "";   // TextChanged forgets the answers
        else
            ForgetSearch();
    }

    // ── typing ──

    /// <summary>A keystroke: the collection is searched once the typing stops (<see cref="SearchPause"/>),
    /// and the directory is asked by its model's own pause. An emptied field forgets both answers and puts
    /// the field back in the middle.</summary>
    private void SearchScreenBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = (SearchScreenBox.Text ?? "").Trim();
        if (query == _searchQuery)
            return;
        _searchQuery = query;

        if (query.Length == 0)
        {
            ForgetSearch();
            return;
        }

        if (_searchSettle is null)
        {
            _searchSettle = DispatcherQueue.CreateTimer();
            _searchSettle.Interval = SearchPause;
            _searchSettle.IsRepeating = false;
            _searchSettle.Tick += (timer, _) =>
            {
                timer.Stop();
                SearchCollectionNow();
            };
        }
        _searchSettle.Stop();
        _searchSettle.Start();

        AskStations(now: false);
        PaintSearch();
    }

    private void ForgetSearch()
    {
        _searchQuery = "";
        _searchSettle?.Stop();
        _searchRead++;   // an answer on its way lands nowhere
        _searchFound = null;
        PaintSearch();
    }

    /// <summary>OK (Enter) in the field goes to the first result, or searches at once where there is
    /// none.</summary>
    private void SearchScreenBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.NavigationAccept
                          or Windows.System.VirtualKey.GamepadA))
            return;
        e.Handled = true;
        GoToFirstResult();
    }

    private void SearchScreenGo_Click(object sender, RoutedEventArgs e) => GoToFirstResult();

    private void GoToFirstResult()
    {
        if (FirstSearchRow() is { } first && CanTakeFocus(first))
        {
            RemoteSearch.Give(first);
            return;
        }
        if (_searchQuery.Length == 0)
            return;
        _searchSettle?.Stop();
        SearchCollectionNow();
        AskStations(now: true);
    }

    // ── the collection ──

    /// <summary>
    /// Asks the mirror for the words on a worker, then sorts what it found into the three kinds there too.
    /// The artists are read off the shelf as it stands when the search starts. A shelf that lands later
    /// searches again (see <see cref="SearchShelfLanded"/>).
    /// </summary>
    private async void SearchCollectionNow()
    {
        var read = ++_searchRead;
        var query = _searchQuery;
        if (query.Length == 0)
            return;

        var library = Library;
        if (!library.IsOpen || library.Root is not { } root)
        {
            SearchLanded(read, new SearchFound([], [], [], ""));
            return;
        }

        var shelf = _shelf;
        var db = library.Db;
        try
        {
            var asked = await Task.Run(() => library.Read(db, "Pegasus's search",
                mirror => Find(query, mirror.Query(query), shelf, root)));
            if (asked.Answers(out var found, out _))
                SearchLanded(read, found);
            else
                RPM.Core.Diagnostics.Scope.Said("pegasus", "search: the collection did not answer");
        }
        catch (Exception ex)
        {
            // A collection that closed under the read. The last answer stays up.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "the search screen's read", ex);
        }
    }

    /// <summary>
    /// The three kinds out of the tracks the mirror's search found (in the order of their paths): an album
    /// once, where its title or its artist holds the words (the shelf's title and artist where the shelf
    /// knows the record, else the track's tags); a track where its title or its artist holds them. The
    /// artists come from the shelf. Then each is ordered and cut by <see cref="SearchRank"/>.
    /// </summary>
    private static SearchFound Find(string query, IReadOnlyList<LibraryTrack> rows, LibraryAlbumIndex.ArtistShelf? shelf, string root)
    {
        bool Holds(string? text) => text is { Length: > 0 } && text.Contains(query, StringComparison.OrdinalIgnoreCase);

        var artists = shelf is null
            ? new List<ArtistWallItem>()
            : SearchRank.Best(shelf.Acts.Where(a => ArtistWall.Matches(a.Name, query)), a => a.Name, query, SearchRank.ArtistsMost);

        var albums = new List<AlbumFound>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<LibraryTrack>();
        foreach (var row in rows)
        {
            if (Holds(row.Title) || Holds(row.Artist))
                tracks.Add(row);

            if (row.Folder.Length == 0 || seen.Contains(row.Folder))
                continue;
            var known = shelf is not null && shelf.AlbumFacts.TryGetValue(row.Folder, out var facts) ? facts : (RPM.Core.Cloud.AlbumFacts?)null;
            var title = known?.Album ?? row.Album ?? "";
            var artist = known?.Artist ?? (string.IsNullOrWhiteSpace(row.AlbumArtist) ? row.Artist : row.AlbumArtist) ?? "";
            if (!Holds(title) && !Holds(artist))
                continue;
            seen.Add(row.Folder);
            albums.Add(new AlbumFound(row.Folder, title, artist));
        }

        return new SearchFound(
            artists,
            SearchRank.Best(albums, a => a.Title, query, SearchRank.AlbumsMost),
            SearchRank.Best(tracks, t => t.Title, query, SearchRank.TracksMost),
            root);
    }

    private void SearchLanded(int read, SearchFound found)
    {
        if (read != _searchRead || _searchQuery.Length == 0)
            return;
        _searchFound = found;
        PaintSearch();
    }

    /// <summary>The artists and genres were read again. With words in the field, the collection is
    /// searched again, as its artists may have arrived only now. From ShelfLanded.</summary>
    private void SearchShelfLanded()
    {
        if (_searchQuery.Length > 0)
            SearchCollectionNow();
    }

    // ── the stations ──

    /// <summary>The words handed to the stations' model: it asks the directory after its own typing pause,
    /// or at once with <paramref name="now"/>. The same words a second time (typed again after the field
    /// was emptied) are asked again, since the model does not see a change.</summary>
    private void AskStations(bool now)
    {
        var model = EnsureSearchStations();
        if (now || model.SearchText == _searchQuery)
        {
            model.SearchText = _searchQuery;
            model.Search();
            return;
        }
        model.SearchText = _searchQuery;
    }

    private RadioModel EnsureSearchStations()
    {
        if (_searchStations is { } model)
            return model;
        model = new RadioModel(Radio, DispatcherQueue);
        model.PropertyChanged += SearchStations_PropertyChanged;
        _searchStations = model;
        return model;
    }

    private void SearchStations_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RadioModel.IsBusy) or nameof(RadioModel.Error) or nameof(RadioModel.DialStations)))
            return;
        if (e.PropertyName == nameof(RadioModel.IsBusy) && _searchStations is { IsBusy: false })
            _searchStationsAnswered = true;
        PaintSearch();
    }

    /// <summary>What the stations' side stands at: not asked yet, being asked, failed, or found.</summary>
    private enum StationsState
    {
        Idle,
        Searching,
        Failed,
        Found,
    }

    private StationsState SearchStationsState()
    {
        if (_searchQuery.Length == 0 || _searchStations is not { } model)
            return StationsState.Idle;
        if (model.IsBusy)
            return StationsState.Searching;
        if (!_searchStationsAnswered)
            return StationsState.Idle;
        return model.HasError ? StationsState.Failed : StationsState.Found;
    }

    private List<RadioStationItem> SearchStationsFound() =>
        _searchStations is { } model ? [.. model.Stations.Take(SearchRank.StationsMost)] : [];

    // ── what the card shows ──

    /// <summary>
    /// The field's place and the card's contents, from the two answers as they stand: the turning record
    /// while the collection has not answered (or has found nothing while the directory is still to answer),
    /// the no-results line when both found nothing, and otherwise the list. Each part of the list is built
    /// again only when its answer changed.
    /// </summary>
    private void PaintSearch()
    {
        var typed = _searchQuery.Length > 0;
        var hits = typed ? _searchFound : null;
        var state = SearchStationsState();
        List<RadioStationItem> stations = state == StationsState.Found ? SearchStationsFound() : [];
        var showing = typed && (hits is not null || state != StationsState.Idle);
        PlaceSearchField(showing);

        var library = hits ?? new SearchFound([], [], [], "");
        var waiting = library.IsEmpty && (hits is null || state is StationsState.Idle or StationsState.Searching);
        var nothing = !waiting && library.IsEmpty && stations.Count == 0 && state == StationsState.Found;

        SearchResultsSpinner.Spinning = showing && waiting;
        SearchResultsSpinner.Visibility = showing && waiting ? Visibility.Visible : Visibility.Collapsed;
        SearchResultsNote.Visibility = showing && nothing ? Visibility.Visible : Visibility.Collapsed;
        var listed = showing && !waiting && !nothing;
        SearchResultsScroll.Visibility = listed ? Visibility.Visible : Visibility.Collapsed;

        var hadFocus = FocusWithin(SearchResultsList);
        var rebuilt = false;

        var libraryShown = listed ? library : null;
        if (!ReferenceEquals(libraryShown, _searchLibraryBuilt))
        {
            BuildSearchLibrary(libraryShown);
            rebuilt = true;
        }

        var stationsKey = !listed ? "" : state switch
        {
            StationsState.Found => "found:" + string.Join(",", stations.Select(s => s.Uuid)),
            StationsState.Searching => "searching",
            StationsState.Failed => "failed",
            _ => "",
        };
        if (stationsKey != _searchStationsBuilt)
        {
            BuildSearchStations(listed ? state : StationsState.Idle, listed ? stations : []);
            _searchStationsBuilt = stationsKey;
            rebuilt = true;
        }

        if (!rebuilt)
            return;

        SizeSearchRows(_searchScale);
        MarkTopHeading();

        // The row the focus stood on is gone. With the remote in use it goes to the first row there is.
        if (hadFocus && !FocusWithin(SearchResultsList))
            FocusScreenLater(FirstSearchRow() ?? (Control)SearchScreenBox);
    }

    /// <summary>The collection's rows, under their headings: the artists, the albums, the tracks. Null
    /// clears them.</summary>
    private void BuildSearchLibrary(SearchFound? found)
    {
        _searchLibraryBuilt = found;
        _searchBuilt++;
        _searchPictures.Clear();
        _searchLibraryRows.Clear();
        SearchLibraryPart.Children.Clear();
        _searchHeadings.RemoveAll(h => !SearchStationsPart.Children.Contains(h));
        if (found is null)
            return;

        var built = _searchBuilt;
        var at = 0;

        if (found.Artists.Count > 0)
        {
            AddHeading(SearchLibraryPart, "Tv_Search_Artists");
            foreach (var act in found.Artists)
            {
                var compilations = ArtistCredit.IsVariousArtists(act.Name);
                var (picture, image) = RoundPicture(compilations ? "" : ArtistWall.Initial(act.Name).ToString(), compilations);
                var albums = act.Own.Count();
                var under = albums == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", albums);
                AddSearchRow(SearchLibraryPart, _searchLibraryRows, act.Name, under, picture, null,
                             press: self => OpenArtistPage(act, self), menu: null);
                if (!compilations)
                    QueueArtistPicture(act, image, at++, built);
            }
        }

        if (found.Albums.Count > 0)
        {
            AddHeading(SearchLibraryPart, "Tv_Search_Albums");
            foreach (var album in found.Albums)
            {
                var (picture, image) = SquarePicture();
                var folder = album.Folder;
                AddSearchRow(SearchLibraryPart, _searchLibraryRows, album.Title, album.Artist, picture, null,
                             press: _ => ShowRecordAsync(folder), menu: null);
                QueueCover(folder, image, at++, built);
            }
        }

        if (found.Tracks.Count > 0)
        {
            AddHeading(SearchLibraryPart, "Tv_Search_Tracks");
            foreach (var track in found.Tracks)
            {
                var song = track.ToTrackInfo(found.Root);
                var under = string.Join(" · ", new[] { track.Artist, track.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var (picture, image) = SquarePicture();
                AddSearchRow(SearchLibraryPart, _searchLibraryRows, track.Title ?? track.FileName,
                             under.Length > 0 ? under : null, picture, null,
                             press: _ => PlayFromSearch(song), menu: () => TrackMenu(song));
                QueueCover(track.Folder, image, at++, built);
            }
        }
    }

    /// <summary>The stations' heading and what stands under it: the turning record while the directory is
    /// asked, the failure, or the stations found.</summary>
    private void BuildSearchStations(StationsState state, List<RadioStationItem> stations)
    {
        _searchStationRows.Clear();
        _searchLogos.Clear();
        foreach (var child in SearchStationsPart.Children)
            if (child is TextBlock heading)
                _searchHeadings.Remove(heading);
        SearchStationsPart.Children.Clear();

        if (state is StationsState.Idle || (state == StationsState.Found && stations.Count == 0))
            return;

        AddHeading(SearchStationsPart, "Tv_Search_Stations");
        switch (state)
        {
            case StationsState.Searching:
                SearchStationsPart.Children.Add(new RPM.Desktop.Presentation.Controls.RecordSpinner
                {
                    Width = 30,
                    Height = 30,
                    Spinning = true,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsHitTestVisible = false,
                    Tag = "stations-waiting",
                });
                return;

            case StationsState.Failed:
                SearchStationsPart.Children.Add(new TextBlock
                {
                    Text = Loc.Get("Tv_Search_StationsFailed"),
                    Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                    TextWrapping = TextWrapping.Wrap,
                    Tag = "stations-failed",
                });
                return;
        }

        var icons = (FontFamily)Resources["PegasusFa"];
        var at = 0;
        foreach (var item in stations)
        {
            var station = item.Station;
            var well = new StationWell(icons);
            var detail = item.Detail;
            var row = AddSearchRow(SearchStationsPart, _searchStationRows, item.Name, detail.Length > 0 ? detail : null,
                                   well.Root, well,
                                   press: _ => TuneFromSearch(station),
                                   menu: () => StationMenu(station, () => TuneFromSearch(station)));
            _searchLogos.Add(new CardPlace(at++, 0), async () =>
            {
                if (!SearchStationsPart.Children.Contains(row.Line))
                    return;
                var found = await RadioStationItem.LogoForAsync(station, CardDecodeEdgeFor(SearchPicture * _searchScale));
                if (SearchStationsPart.Children.Contains(row.Line))
                    well.Show(found);
            });
        }
    }

    // ── what OK does ──

    /// <summary>A track plays alone, and the collection goes down to the player, as a song played from any
    /// list does (see PlayFromDrawer).</summary>
    private void PlayFromSearch(RPM.Core.Models.TrackInfo song) => PlayFromDrawer([song], 0, new SleeveInHand.None());

    /// <summary>A station is tuned, said so along the foot, and the collection goes down to the player, as
    /// the TV's search opens its player on a station.</summary>
    private void TuneFromSearch(RadioStation station)
    {
        TuneFromRadio(station);
        HideLibrary();
    }

    // ── building a row ──

    /// <summary>A heading over a kind, in the radio screen's heading type. Its space above is set in
    /// SizeSearchRows, less for the heading at the top of the list.</summary>
    private void AddHeading(StackPanel part, string key)
    {
        var heading = new TextBlock
        {
            Text = Loc.Get(key),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        part.Children.Add(heading);
        _searchHeadings.Add(heading);
    }

    /// <summary>The heading at the top of the list has less space above it than those between the kinds.
    /// Read off the order the headings stand in.</summary>
    private TextBlock? _searchTopHeading;

    private void MarkTopHeading()
    {
        _searchTopHeading = SearchLibraryPart.Children.OfType<TextBlock>().FirstOrDefault()
                            ?? SearchStationsPart.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag is null);
        SizeSearchHeadings(_searchScale);
    }

    /// <summary>
    /// One row, as the TV draws them: lit #33FFFFFF while the remote is on it, a square picture, then the
    /// title over a dim line. It is a <see cref="RemoteLine"/>: OK presses it, and with a menu a held OK or the
    /// Menu key opens that. The pointer presses it with a tap and opens the menu with a right-click.
    /// <paramref name="press"/> is handed the row itself, so an artist's page knows the row to come back
    /// to.
    /// </summary>
    private SearchRow AddSearchRow(
        StackPanel part, List<SearchRow> rows, string title, string? under, FrameworkElement picture, StationWell? well,
        Action<RemoteLine> press, Func<MenuFlyout?>? menu)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        TextBlock? underText = under is null ? null : new TextBlock
        {
            Text = under,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(titleText);
        if (underText is not null)
            words.Children.Add(underText);

        var inside = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        picture.VerticalAlignment = VerticalAlignment.Center;
        inside.Children.Add(picture);
        Grid.SetColumn(words, 1);
        inside.Children.Add(words);

        RemoteLine? self = null;
        var line = new RemoteLine(inside, () => press(self!), menu, turn: null);
        self = line;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(line, title);
        var lit = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        var unlit = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        line.GotFocus += (_, _) => inside.Background = lit;
        line.LostFocus += (_, _) => inside.Background = unlit;
        inside.Tapped += (_, _) => line.Press();
        if (menu is not null)
            inside.RightTapped += (_, e) =>
            {
                e.Handled = true;
                menu()?.ShowAt(line);
            };

        part.Children.Add(line);
        var row = new SearchRow(line, inside, picture, words, titleText, underText, well);
        rows.Add(row);
        return row;
    }

    /// <summary>A record's cover in a row, on a dark well with corner 6 until it arrives.</summary>
    private static (Border Box, Image Image) SquarePicture()
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)),
            Child = image,
        };
        return (box, image);
    }

    /// <summary>An act's photograph in a circle. Until it arrives, their first letter is shown; the
    /// compilations shelf shows the people glyph instead, as on the artists list.</summary>
    private (Border Box, Image Image) RoundPicture(string stand, bool glyph)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var letter = new TextBlock
        {
            Text = stand,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "stand-in",
        };
        if (glyph)
            letter.FontFamily = (FontFamily)Resources["PegasusFa"];
        var inside = new Grid();
        inside.Children.Add(letter);
        inside.Children.Add(image);
        var box = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)),
            Child = inside,
        };
        return (box, image);
    }

    /// <summary>A record's cover, read as the artist's page reads its popular songs' covers.</summary>
    private void QueueCover(string folder, Image image, int at, int built)
    {
        _searchPictures.Add(new CardPlace(at, 0), async () =>
        {
            if (built != _searchBuilt)
                return;
            var edge = CardDecodeEdgeFor(SearchPicture * _searchScale);
            var bytes = await AlbumCovers.LoadForSquareAsync(folder, CancellationToken.None, edge, ownCollection: true);
            if (bytes is not { Length: > 0 } || built != _searchBuilt)
                return;
            var bitmap = await RemoteImage.DecodeArtAsync(bytes, edge);
            if (built == _searchBuilt)
                image.Source = bitmap;
        });
    }

    /// <summary>An act's photograph: the one the artists list shows, from its own store of them where it
    /// has fetched it, else chosen and cut as that list does (see LoadArtistFaceAsync).</summary>
    private void QueueArtistPicture(ArtistWallItem act, Image image, int at, int built)
    {
        _searchPictures.Add(new CardPlace(at, 0), async () =>
        {
            if (built != _searchBuilt)
                return;

            if (!_artistFaces.TryGetValue(act.Name, out var bytes))
            {
                var first = _shelf is { } shelf
                    ? act.Own.Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts) ? facts.Album : null)
                             .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                    : null;
                var chosen = await ArtistPortraits.SquareAsync(act.Name, first);
                if (!chosen.Answers(out var url, out _))
                    return;
                if (url is null)
                {
                    KeepArtistFace(act.Name, null);
                    return;
                }
                var picture = await ArtistPortraits.PictureAsync(url);
                if (!picture.Answers(out var portrait, out _))
                    return;
                bytes = portrait?.Thumb;
                KeepArtistFace(act.Name, bytes);
            }

            if (bytes is not { Length: > 0 } || built != _searchBuilt)
                return;
            var bitmap = await RemoteImage.DecodeArtAsync(bytes, CardDecodeEdgeFor(SearchPicture * _searchScale));
            if (built == _searchBuilt)
                image.Source = bitmap;
        });
    }

    // ── the remote ──

    /// <summary>The first row of the list, whatever its kind, or null where there is none.</summary>
    private Control? FirstSearchRow() =>
        SearchResultsScroll.Visibility == Visibility.Visible
            ? (Control?)_searchLibraryRows.FirstOrDefault()?.Line ?? _searchStationRows.FirstOrDefault()?.Line
            : null;

    /// <summary>Where the focus starts on the search screen: the field.</summary>
    private Control SearchFocusTarget() => SearchScreenBox;

    /// <summary>
    /// The search screen's own Up and Down: Down from the field or the go key goes to the first result (the
    /// list is read from its start, not from whichever row is under the field's middle; with no list yet,
    /// the key stays), and Up from the first result goes back to the field.
    /// </summary>
    private Control? AcrossSearch(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        var first = FirstSearchRow();
        if (direction == FocusNavigationDirection.Down
            && (ReferenceEquals(from, SearchScreenBox) || ReferenceEquals(from, SearchScreenGo)))
            return first is not null && CanTakeFocus(first) ? first : null;
        if (direction == FocusNavigationDirection.Up && first is not null && ReferenceEquals(from, first))
            return SearchScreenBox;
        return next;
    }

    /// <summary>
    /// Back on the search screen, before it steps along the trail: from the results the focus goes back to
    /// the field, and a field with words in it is emptied. True where Back was taken here. From an empty
    /// field, false, and Back leaves for home. From StepBackFromArtistsGenres.
    /// </summary>
    private bool SearchTakesBack()
    {
        if (FocusWithin(SearchResultsList))
        {
            FocusFieldLater(SearchScreenBox);
            return true;
        }
        if (SearchScreenBox.Text.Length > 0)
        {
            SearchScreenBox.Text = "";
            return true;
        }
        return false;
    }

    // ── sizes ──

    /// <summary>
    /// Where the field stands: in the middle of the screen while there is nothing to show, and at the top
    /// once there is, with the results card from under it to near the foot. As radio's search places its
    /// own (PlaceRadioSearchField).
    /// </summary>
    private void PlaceSearchField(bool showing)
    {
        var scale = _searchScale;
        var field = 64 * scale;
        var top = showing ? HomeTopPad : Math.Max(HomeTopPad, _searchHeight / 2 - field / 2);
        SearchColumn.Margin = new Thickness(0, top, 0, 34 * scale);
        SearchResultsCard.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The screen at this scale, on the TV's figures: the column half the width and never under 560, the
    /// field 64 high with a corner of 12 and type of 26, the go key a square of the field's height, the card
    /// 12 under the field.
    /// </summary>
    private void SizeSearchScreen(double scale)
    {
        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        var height = LibraryLayer.ActualHeight > 0 ? LibraryLayer.ActualHeight : Root.ActualHeight;
        _searchHeight = height;
        _searchScale = scale;

        var field = 64 * scale;
        var gap = 12 * scale;
        var corner = new CornerRadius(12 * scale);
        SearchColumn.Width = Math.Max(560, width * 0.5);
        SearchScreenField.Height = field;
        SearchScreenGlass.CornerRadius = corner;
        SearchScreenBox.FontSize = 26 * scale;
        SearchScreenBox.Margin = new Thickness(22 * scale, 0, 22 * scale, 0);
        SearchScreenGo.Width = SearchScreenGo.Height = field;
        SearchScreenGo.Margin = new Thickness(gap, 0, 0, 0);
        SearchScreenGo.CornerRadius = corner;
        SearchScreenGoMark.FontSize = 22 * scale;

        SearchResultsCard.Margin = new Thickness(0, gap, 0, 0);
        SearchResultsGlass.CornerRadius = corner;
        SearchResultsScroll.Padding = new Thickness(12 * scale);
        SearchResultsList.Spacing = 0;
        SearchResultsNote.FontSize = 18 * scale;
        SearchResultsNote.Margin = new Thickness(30 * scale, 48 * scale, 30 * scale, 0);
        SearchResultsSpinner.Width = SearchResultsSpinner.Height = 46 * scale;
        SearchResultsSpinner.Margin = new Thickness(0, 48 * scale, 0, 0);

        PaintSearch();
        SizeSearchRows(scale);
    }

    /// <summary>The rows at the TV's couch sizes: the picture 64 with a corner of 6 (a circle for an act),
    /// 16 between it and the words, the title 17 over a line of 15, the row padded 10 across and 8 down with
    /// a corner of 8, and 2 between rows.</summary>
    private void SizeSearchRows(double scale)
    {
        foreach (var part in new[] { SearchLibraryPart, SearchStationsPart })
            part.Spacing = 2 * scale;

        var edge = SearchPicture * scale;
        foreach (var row in _searchLibraryRows.Concat(_searchStationRows))
        {
            row.Line.CornerRadius = new CornerRadius(8 * scale);
            row.Inside.CornerRadius = new CornerRadius(8 * scale);
            row.Inside.Padding = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
            row.Words.Margin = new Thickness(16 * scale, 0, 0, 0);
            row.Words.Spacing = 2 * scale;
            row.Title.FontSize = 17 * scale;
            if (row.Under is { } under)
                under.FontSize = 15 * scale;

            if (row.Well is { } well)
            {
                well.Size(edge);
                continue;
            }

            switch (row.Picture)
            {
                case Border { Child: Grid round } box:   // an act: a circle, the stand-in letter at 22
                    box.Width = box.Height = edge;
                    box.CornerRadius = new CornerRadius(edge / 2);
                    foreach (var letter in round.Children.OfType<TextBlock>())
                        letter.FontSize = (letter.FontFamily == (FontFamily)Resources["PegasusFa"] ? 20 : 22) * scale;
                    break;
                case Border box:
                    box.Width = box.Height = edge;
                    box.CornerRadius = new CornerRadius(6 * scale);
                    break;
            }
        }

        foreach (var child in SearchStationsPart.Children)
        {
            switch (child)
            {
                case RPM.Desktop.Presentation.Controls.RecordSpinner spinner:
                    spinner.Width = spinner.Height = 30 * scale;
                    spinner.Margin = new Thickness(0, 12 * scale, 0, 12 * scale);
                    break;
                case TextBlock { Tag: "stations-failed" } failed:
                    failed.FontSize = 15 * scale;
                    failed.Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
                    break;
            }
        }

        SizeSearchHeadings(scale);
    }

    /// <summary>The headings at the couch heading size, 22, with 22 above them (4 above the top one) and 6
    /// under, 10 in from the rows' edge.</summary>
    private void SizeSearchHeadings(double scale)
    {
        foreach (var heading in _searchHeadings)
        {
            heading.FontSize = 22 * scale;
            var above = ReferenceEquals(heading, _searchTopHeading) ? 4 : 22;
            heading.Margin = new Thickness(10 * scale, above * scale, 10 * scale, 6 * scale);
        }
    }
}

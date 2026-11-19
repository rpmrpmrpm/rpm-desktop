using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane;
using RPM.Desktop.Services.Shell;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Search;

/// <summary>One record the search can find: its folder, its name, who made it, and how many tracks it has.</summary>
public sealed record SearchRecord(string Folder, string Album, string Artist, int Tracks);

/// <summary>One song the search found, with the folder its record is in, for its picture.</summary>
public sealed record SearchSong(TrackInfo Track, string Folder);

/// <summary>One playlist the search can find, as the playlists view names it, with the line under it.</summary>
public sealed record SearchList(PlaylistTreeItem Item, string Name, string Line);

/// <summary>
/// What the library search looks through, as the page holds it when the search is opened.
/// </summary>
/// <param name="Songs">The songs whose title, artist or record holds the words, at most the given number. It
/// runs off the UI thread. See <c>LibraryDatabase.SearchSongs</c>.</param>
public sealed record LibrarySearchSources(
    IReadOnlyList<SearchRecord> Records,
    IReadOnlyList<ArtistWallItem> Artists,
    IReadOnlyList<GenreWallItem> Genres,
    IReadOnlyList<SearchList> Playlists,
    Func<string, int, IReadOnlyList<SearchSong>> Songs);

/// <summary>
/// The library's search: what the words typed in the search bar over the window (<see cref="SearchOverlay"/>)
/// name, in a panel that drops down under the bar.
/// </summary>
/// <remarks>
/// <para><b>Everything the collection holds that the words name, whichever view it lives in, each kind under
/// its own heading</b>: artists, albums, tracks, playlists, genres. That is the phones' search
/// (<c>LibraryPage.results</c> on the iPhone) with the same rules: songs and records match their title,
/// artist, record or record artist, ignoring case; artists, genres and playlists match their name folded
/// (<see cref="ArtistWall.SearchKey"/>), so "beyonce" finds Beyoncé and "acdc" finds AC/DC. A group shows
/// its first <see cref="FirstShown"/> and opens to the rest with View more.</para>
///
/// <para><b>It does not narrow the views behind it.</b> A search answers in its own list, and the views stay
/// whole, as on the phones. A record found is opened, a song is played.</para>
///
/// <para><b>The keyboard stays in the bar's box.</b> The first row is lit each time the panel is drawn; the
/// bar moves the light with <see cref="MoveLight"/> and presses the lit row with <see cref="ChooseLit"/>.</para>
///
/// <para><b>The recent searches</b> are shown while the box is empty. A search is remembered when the box
/// has been left alone for <see cref="SearchHistory.Settled"/>, or when something it found is chosen. See
/// <see cref="SearchHistory"/> for the rules the phones keep too.</para>
/// </remarks>
public sealed partial class LibrarySearchView : UserControl
{
    /// <summary>How many answers a group shows before View more.</summary>
    private const int FirstShown = 5;

    private readonly DispatcherTimer _typing = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _settling = new() { Interval = SearchHistory.Settled };

    private CancellationTokenSource? _search;
    private LibrarySearchSources? _sources;
    private LibrarySearchFound? _found;
    private string _asked = "";
    private string _shownFor = "";
    private readonly HashSet<string> _unfolded = new(StringComparer.Ordinal);

    /// <summary>The search bar this search drops down from. See <see cref="Place"/>.</summary>
    private FrameworkElement? _anchor;

    public LibrarySearchView()
    {
        InitializeComponent();
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Run();
        };
        _settling.Tick += (_, _) =>
        {
            _settling.Stop();
            if (IsOpen)
                SearchHistoryStore.Remember(_asked);
        };

        SizeChanged += (_, _) => Place();
        SearchHistoryStore.Changed += OnHistoryChanged;
        Unloaded += (_, _) => SearchHistoryStore.Changed -= OnHistoryChanged;
    }

    /// <summary>What a record's cover is read from: the page's own door, as every shelf in the app reads
    /// one (<c>AlbumCovers</c>).</summary>
    public Func<string, int, CancellationToken, Task<Asked<byte[]?>>>? CoverLoader { get; set; }

    public event Action<ArtistWallItem>? ArtistChosen;
    public event Action<string>? RecordChosen;
    public event Action<TrackInfo>? SongChosen;
    public event Action<PlaylistTreeItem>? PlaylistChosen;
    public event Action<GenreWallItem>? GenreChosen;

    /// <summary>A recent search was pressed: the page puts it in the box, which searches it.</summary>
    public event Action<string>? RecentPicked;

    /// <summary>The search was put away.</summary>
    public event Action? Closed;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>Whether the panel has anything on it: the recent searches, or an answer.</summary>
    public bool ShowsAnything => IsOpen && Body.Children.Count > 0;

    // ── opening and closing ──

    /// <summary>
    /// Puts the search up under <paramref name="anchor"/>, the search bar, looking through
    /// <paramref name="sources"/>. A search that is already up is given the newest sources.
    /// </summary>
    public void Open(LibrarySearchSources sources, FrameworkElement anchor)
    {
        _sources = sources;

        if (!ReferenceEquals(_anchor, anchor))
        {
            if (_anchor is not null)
                _anchor.SizeChanged -= Anchor_SizeChanged;
            _anchor = anchor;
            _anchor.SizeChanged += Anchor_SizeChanged;
        }

        if (!IsOpen)
        {
            _asked = "";
            _found = null;
            _shownFor = "";
            _unfolded.Clear();

            // Drawn clear from its first frame (see Rise).
            _rise?.Stop();
            Panel.Opacity = 0;
            PanelShift.Y = -6;

            Visibility = Visibility.Visible;
            UpdateLayout();
            Place();
            Show();
            Rise();
        }
        else
        {
            Place();
            if (_asked.Trim().Length > 0)
                Run();
        }
    }

    /// <summary>The words in the box changed.</summary>
    public void Ask(string text)
    {
        _asked = text ?? "";
        _chooseWhenAnswered = false;
        _typing.Stop();
        _settling.Stop();

        if (_asked.Trim().Length == 0)
        {
            _search?.Cancel();
            _found = null;
            _shownFor = "";
            Show();
            return;
        }

        _typing.Start();
        if (_asked.Trim().Length >= 2)
            _settling.Start();
    }

    /// <summary>Lights the row <paramref name="step"/> rows down (up when negative), and keeps it in view. It
    /// stops at the first and the last row.</summary>
    public void MoveLight(int step)
    {
        var rows = Choices();
        if (rows.Count == 0)
            return;

        var at = _lit is null ? -1 : rows.IndexOf(_lit);
        var to = Math.Clamp(at + step, 0, rows.Count - 1);
        Light(rows[to]);

        // The first row shows the heading over it too.
        if (to == 0)
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        else
            Reveal(rows[to]);
    }

    /// <summary>
    /// Does what a press on the lit row does. False where no row is lit.
    /// </summary>
    /// <remarks>
    /// The lit row counts only while the panel answers the words in the box now (<see cref="AnswersTheBox"/>).
    /// Enter pressed right after a letter comes before the typing timer, and the panel still shows the answer
    /// to the words before it, or the recent searches; choosing a row there would open the wrong thing, or
    /// put a recent search over what was typed. So the search runs at once, and the first answer to these
    /// words is chosen when it lands (<see cref="Run"/>). Another letter typed first cancels that.
    /// </remarks>
    public bool ChooseLit()
    {
        if (!AnswersTheBox)
        {
            _chooseWhenAnswered = true;
            _typing.Stop();
            Run();
            return true;
        }

        switch (_lit?.Tag)
        {
            case Answer answer:
                Choose(answer.Thing);
                return true;
            case Recent recent:
                RecentPicked?.Invoke(recent.Asked);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Enter was pressed before the answer to the words in the box had come. See
    /// <see cref="ChooseLit"/>. Cleared by a letter typed and by closing.</summary>
    private bool _chooseWhenAnswered;

    /// <summary>Whether the panel shows what belongs to the words in the box now: the recent searches for an
    /// empty box, or the answer that was found for these words.</summary>
    private bool AnswersTheBox =>
        _asked.Trim().Length == 0
        || (_found is not null && string.Equals(_shownFor, _asked.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The row lit for the keyboard, or null. Every redraw makes new rows and sets this to null
    /// first (see <see cref="Fill"/>), so it never names a row that is not on the panel.</summary>
    private Button? _lit;

    /// <summary>The rows the keyboard can light, top to bottom: the answers, or the recent searches.</summary>
    private List<Button> Choices() =>
        Body.Children.OfType<Button>().Where(row => row.Tag is Answer or Recent).ToList();

    private void Light(Button? row)
    {
        if (_lit is not null)
            _lit.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _lit = row;
        if (row is not null)
            row.Background = Ink(0x1C);
    }

    /// <summary>Scrolls the panel just far enough to show <paramref name="row"/>.</summary>
    private void Reveal(FrameworkElement row)
    {
        if (row.ActualHeight <= 0)
            return;   // not laid out yet

        Windows.Foundation.Rect at;
        try
        {
            at = row.TransformToVisual(Scroller)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, row.ActualWidth, row.ActualHeight));
        }
        catch (ArgumentException)
        {
            return;   // the row left the panel after this frame started
        }

        const double air = 8;
        if (at.Top < 0)
            Scroller.ChangeView(null, Math.Max(0, Scroller.VerticalOffset + at.Top - air), null, disableAnimation: true);
        else if (at.Bottom > Scroller.ViewportHeight)
            Scroller.ChangeView(null, Scroller.VerticalOffset + at.Bottom - Scroller.ViewportHeight + air, null, disableAnimation: true);
    }

    /// <summary>Puts the search away. What was typed goes with it: opening it again starts a new search, as
    /// on the phones.</summary>
    public void Close()
    {
        if (!IsOpen)
            return;

        _typing.Stop();
        _settling.Stop();
        _search?.Cancel();
        _search = null;
        _asked = "";
        _chooseWhenAnswered = false;
        Visibility = Visibility.Collapsed;
        Body.Children.Clear();
        _lit = null;
        _covers.Clear();
        _portraits.Clear();
        if (_anchor is not null)
            _anchor.SizeChanged -= Anchor_SizeChanged;
        _anchor = null;
        Closed?.Invoke();
    }

    /// <summary>The panel comes down a few points and up from clear, in the time a menu takes. A storyboard
    /// sets its From value only when it next ticks, so Open sets the panel clear before it is shown, and the
    /// landed storyboard lets go of the values, so the next Open can set them again.</summary>
    private void Rise()
    {
        var board = new Storyboard();
        _rise = board;

        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, Panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);

        var drop = new DoubleAnimation
        {
            From = -6, To = 0, Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(drop, PanelShift);
        Storyboard.SetTargetProperty(drop, "Y");
        board.Children.Add(drop);

        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_rise, board))
                return;
            board.Stop();
            Panel.Opacity = 1;
            PanelShift.Y = 0;
            _rise = null;
        };

        board.Begin();
    }

    /// <summary>The panel's fade in, while it runs.</summary>
    private Storyboard? _rise;

    /// <summary>The gap between the bar and the panel.</summary>
    private const double UnderBar = 10;

    /// <summary>The bar follows the window's width, and the panel goes with it.</summary>
    private void Anchor_SizeChanged(object sender, SizeChangedEventArgs e) => Place();

    /// <summary>
    /// Under the bar, as wide as the bar, and down to at most 12 points above the window's foot. The catcher
    /// starts at the panel's top; above it the bar's own shade takes the press.
    /// </summary>
    private void Place()
    {
        if (!IsOpen || _anchor is null || ActualWidth <= 0 || _anchor.ActualWidth <= 0)
            return;

        try
        {
            var bar = _anchor.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            var top = bar.Y + _anchor.ActualHeight + UnderBar;

            Panel.Margin = new Thickness(bar.X, top, 0, 0);
            Panel.Width = _anchor.ActualWidth;
            Panel.MaxHeight = Math.Max(160, ActualHeight - top - 12);
            Catcher.Margin = new Thickness(0, top - UnderBar / 2, 0, 0);
        }
        catch (ArgumentException)
        {
            // The bar is not in this tree right now. The next size change places the panel.
        }
    }

    private void Catcher_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    // ── searching ──

    /// <summary>Searches for what is in the box, off the UI thread. A new keystroke stops a search that is
    /// still running, and its answer is thrown away.</summary>
    /// <remarks>Nothing here throws on the UI thread. <see cref="LibrarySearchQuery.Run"/> returns null for a
    /// search that was stopped or that failed, and the token is never given to <c>Task.Run</c>, whose await
    /// would rethrow the cancellation on this thread. A throw on the UI thread stops the interface for
    /// seconds while a debugger is attached, and a search that is still running is stopped by every new
    /// one.</remarks>
    private async void Run()
    {
        var asked = _asked.Trim();
        if (asked.Length == 0 || _sources is not { } sources)
            return;

        _search?.Cancel();
        var mine = _search = new CancellationTokenSource();
        var ct = mine.Token;

        // Null: stopped by a newer search, or failed. A search that failed shows what it found before, and
        // the next keystroke asks again.
        var found = await Task.Run(() => LibrarySearchQuery.Run(sources, asked, ct));
        if (found is null || ct.IsCancellationRequested || !ReferenceEquals(_search, mine) || !IsOpen)
            return;

        _found = found;
        if (!string.Equals(_shownFor, asked, StringComparison.OrdinalIgnoreCase))
            _unfolded.Clear();   // a new search starts every group folded
        _shownFor = asked;
        Show();

        if (_chooseWhenAnswered && AnswersTheBox)
        {
            _chooseWhenAnswered = false;
            ChooseLit();
        }
    }

    // ── drawing ──

    /// <summary>What an answer row stands for, kept on its button.</summary>
    private sealed record Answer(object Thing);

    /// <summary>The earlier search a recent row puts back in the box, kept on its button.</summary>
    private sealed record Recent(string Asked);

    /// <summary>Covers already decoded during this search, by folder: ten songs off one record are one
    /// picture.</summary>
    /// <remarks>
    /// <para>The pictures are loaded once per search and are never stopped when the panel is drawn again.
    /// The next keystroke usually shows the same record or artist again, and it takes the load that is
    /// already running. A stopped load costs more than a finished one: each awaiting step rethrows the
    /// cancellation on the UI thread, and a photograph stopped while it downloads closes its
    /// connection.</para>
    /// <para>A picture that arrives after its row has left the panel is set on a square that is no longer
    /// shown. That does nothing, so there is no check for it.</para>
    /// </remarks>
    private readonly Dictionary<string, Task<ImageSource?>> _covers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Artists' photographs decoded during this search, by name. See <see cref="_covers"/>.</summary>
    private readonly Dictionary<string, Task<ImageSource?>> _portraits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Draws the panel's body: the recent searches while the box is empty, what was found once a
    /// search has answered, or that nothing was.</summary>
    private void Show()
    {
        Fill();

        // New rows: the first one is lit. Where Fill kept the rows that were there, the lit row stays.
        if (_lit is null)
            Light(Choices().FirstOrDefault());

        // Nothing to show (an empty box and no recent searches): no panel, and nothing over the card either.
        var any = Body.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Panel.Visibility = any;
        Catcher.Visibility = any;
    }

    private void Fill()
    {
        // The first answer for these words has not come yet: what is on the panel stays until it does, so
        // the panel does not blink empty between the recent searches and the answer.
        if (_asked.Trim().Length > 0 && _found is null && Body.Children.Count > 0)
            return;

        Body.Children.Clear();
        _lit = null;

        if (_asked.Trim().Length == 0)
        {
            ShowRecent();
            return;
        }

        if (_found is not { } found)
            return;   // the first answer for this search has not come yet

        if (found.Nothing)
        {
            // Only once the search has caught up with the typing: "no results" while the text is still
            // changing would say the library does not hold what the next keystroke finds.
            if (string.Equals(_shownFor, _asked.Trim(), StringComparison.OrdinalIgnoreCase))
                Body.Children.Add(new TextBlock
                {
                    Text = Loc.Get("Library_NoSearchResults"),
                    FontSize = 13,
                    Foreground = Ink(0x88),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 22),
                });
            return;
        }

        Group("artists", Loc.Get("Artists_Title"), found.Artists, artist =>
            Row(artist, artist.Name, Albums(artist.Own.Count()), null, Picture.Round, ArtistPicture(artist)));

        Group("records", Loc.Get("Viz_BrowseAlbums"), found.Records, record =>
            Row(record, record.Album, record.Artist, record.Tracks == 1 ? Loc.Get("Rpm_Album_TracksOne") : Loc.Format("Rpm_Album_Tracks", record.Tracks),
                Picture.Square, Cover(record.Folder)));

        Group("songs", Loc.Get("Album_Tracks"), found.Songs, song =>
            Row(song, SongTitle(song.Track), song.Track.Artist ?? song.Track.AlbumArtist ?? "", null,
                Picture.Square, Cover(song.Folder)));

        if (found.Songs.Count >= LibrarySearchQuery.SongCeiling && _unfolded.Contains("songs"))
            Body.Children.Add(new TextBlock
            {
                Text = Loc.Format("Rpm_Library_SearchCeiling", LibrarySearchQuery.SongCeiling),
                FontSize = 12,
                Foreground = Ink(0x77),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(24, 10, 24, 6),
            });

        Group("playlists", Loc.Get("Playlists_Title"), found.Playlists, list =>
            Row(list, list.Name, list.Line, null, Picture.Square, ListPicture(list.Item)));

        Group("genres", Loc.Get("Genres_Title"), found.Genres, genre =>
            Row(genre, genre.Name, Albums(genre.Folders.Count), null, Picture.Square, Glyph("", 0xCC)));
    }

    private static string SongTitle(TrackInfo track) =>
        track.Title is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(track.FilePath);

    private static string Albums(int count) =>
        count == 1 ? Loc.Get("Stats_AlbumsOne") : Loc.Format("Stats_Albums", count);

    /// <summary>One group: its heading, its first <see cref="FirstShown"/> rows, and the rest while it is
    /// open. Nothing at all for a kind with no answers.</summary>
    private void Group<T>(string id, string title, IReadOnlyList<T> answers, Func<T, UIElement> row)
    {
        if (answers.Count == 0)
            return;

        var open = _unfolded.Contains(id);
        var folds = answers.Count > FirstShown;

        Body.Children.Add(Heading(title, folds ? Loc.Get(open ? "Library_SearchViewLess" : "Library_SearchViewMore") : null, () =>
        {
            if (!_unfolded.Remove(id))
                _unfolded.Add(id);
            var at = Scroller.VerticalOffset;
            Show();
            Scroller.ChangeView(null, at, null, disableAnimation: true);
        }));

        foreach (var answer in open || !folds ? answers : answers.Take(FirstShown))
            Body.Children.Add(row(answer));
    }

    /// <summary>A group's heading, with View more or View less at its right where the group has more than
    /// it shows.</summary>
    private static UIElement Heading(string title, string? toggle, Action onToggle)
    {
        Button? key = null;
        if (toggle is not null)
        {
            key = Link(toggle, 0xCC);
            key.Click += (_, _) => onToggle();
        }

        return HeadingLine(title, key);
    }

    /// <summary>
    /// Every heading on the panel (the recent searches and each kind of answer) in the one heading type the
    /// cards use over their blocks: the track info pane's, the artist's and the genre's pages'
    /// (<c>BlockHeading</c>: 12, semibold, at the quiet ink), with an optional key at its right.
    /// </summary>
    private static Grid HeadingLine(string title, Button? end)
    {
        var line = new Grid { Margin = new Thickness(16, 12, 10, 4) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        line.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Ink(0xAA),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (end is not null)
        {
            Grid.SetColumn(end, 1);
            line.Children.Add(end);
        }

        return line;
    }

    /// <summary>A key that is only words: View more, Clear.</summary>
    private static Button Link(string words, byte alpha)
    {
        var key = new Button
        {
            Content = new TextBlock { Text = words, FontSize = 12, Foreground = Ink(alpha) },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 3, 6, 3),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(4),
        };
        HoverWash(key);
        return key;
    }

    /// <summary>What shape a row's picture is: round for a person, square for everything else, so an
    /// artist reads apart from the records, playlists and genres around them.</summary>
    private enum Picture
    {
        Round,
        Square,
    }

    /// <summary>
    /// One answer row: a 40-point picture, two lines, and whatever goes at its end.
    /// </summary>
    private Button Row(object thing, string title, string line, string? end, Picture shape, UIElement picture)
    {
        var face = new Grid { ColumnSpacing = 12 };
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        face.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var frame = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(shape == Picture.Round ? 20 : 6),
            Background = Ink(0x14),
            Child = picture,
        };
        face.Children.Add(frame);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        words.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = Ink(0xFF),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
        });
        if (line.Length > 0)
            words.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                Foreground = Ink(0x88),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            });
        Grid.SetColumn(words, 1);
        face.Children.Add(words);

        if (end is not null)
        {
            var tail = new TextBlock
            {
                Text = end,
                FontSize = 12,
                Foreground = Ink(0x66),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(tail, 2);
            face.Children.Add(tail);
        }

        var row = new Button
        {
            Tag = new Answer(thing),
            Content = face,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 12, 6),
            Margin = new Thickness(6, 0, 6, 0),
            MinHeight = 52,
        };
        AutomationProperties.SetName(row, line.Length > 0 ? $"{title}, {line}" : title);
        HoverWash(row);
        row.Click += (_, _) => Choose(thing);
        return row;
    }

    /// <summary>The light wash a row or a word key wears under the pointer, instead of the default button's
    /// grey plate.</summary>
    private static void HoverWash(Button key)
    {
        key.Resources["ButtonBackgroundPointerOver"] = Ink(0x12);
        key.Resources["ButtonBackgroundPressed"] = Ink(0x1E);
        key.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        key.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>An answer chosen: what was typed is remembered, the search goes away, and the page opens or
    /// plays what was chosen.</summary>
    private void Choose(object thing)
    {
        SearchHistoryStore.Remember(_asked);
        Close();

        switch (thing)
        {
            case ArtistWallItem artist: ArtistChosen?.Invoke(artist); break;
            case SearchRecord record: RecordChosen?.Invoke(record.Folder); break;
            case SearchSong song: SongChosen?.Invoke(song.Track); break;
            case SearchList list: PlaylistChosen?.Invoke(list.Item); break;
            case GenreWallItem genre: GenreChosen?.Invoke(genre); break;
        }
    }

    // ── the recent searches ──

    private void OnHistoryChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsOpen && _asked.Trim().Length == 0)
            Show();
    });

    /// <summary>The earlier searches, newest first. A press puts one back in the box; the cross beside it
    /// removes it, and Clear removes them all. Nothing is drawn while there are none.</summary>
    private void ShowRecent()
    {
        var history = SearchHistoryStore.Entries;
        if (history.Count == 0)
            return;

        var clear = Link(Loc.Get("Rpm_Search_ClearRecent"), 0xB8);
        clear.Click += (_, _) => SearchHistoryStore.Clear();
        Body.Children.Add(HeadingLine(Loc.Get("Rpm_Search_Recent"), clear));

        foreach (var asked in history)
        {
            var line = new Grid { ColumnSpacing = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = FaFont,
                FontSize = 11,
                Foreground = Ink(0x77),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var words = new TextBlock
            {
                Text = asked,
                FontSize = 14,
                Foreground = Ink(0xFF),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(words, 1);
            line.Children.Add(words);

            var forget = new Button
            {
                Content = new TextBlock
                {
                    Text = "",
                    FontFamily = FaFont,
                    FontSize = 10,
                    Foreground = Ink(0x77),
                },
                Width = 28,
                Height = 28,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(forget, Loc.Get("Rpm_Search_Forget"));
            ToolTipService.SetToolTip(forget, Loc.Get("Rpm_Search_Forget"));
            HoverWash(forget);
            forget.Click += (_, _) => SearchHistoryStore.Forget(asked);
            Grid.SetColumn(forget, 2);
            line.Children.Add(forget);

            var row = new Button
            {
                Tag = new Recent(asked),
                Content = line,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 4, 4, 4),
                Margin = new Thickness(6, 0, 6, 0),
                MinHeight = 40,
            };
            AutomationProperties.SetName(row, asked);
            HoverWash(row);
            row.Click += (_, _) => RecentPicked?.Invoke(asked);
            Body.Children.Add(row);
        }
    }

    // ── the pictures ──

    /// <summary>The app's icon font, the one every page's markup names <c>Fa</c> (each declares its own; it
    /// is not an app-wide resource).</summary>
    private static readonly FontFamily FaFont = new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro");

    private static SolidColorBrush Ink(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    /// <summary>A mark in the picture's place, for what has no picture of its own.</summary>
    private static UIElement Glyph(string glyph, byte alpha) => new TextBlock
    {
        Text = glyph,
        FontFamily = FaFont,
        FontSize = 15,
        Foreground = Ink(alpha),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The edge pictures are decoded at: the 40-point square at a high-density display's scale.</summary>
    private int PictureEdge => (int)Math.Ceiling(40 * Math.Max(1, XamlRoot?.RasterizationScale ?? 1));

    /// <summary>A square that fills itself with <paramref name="picture"/> when it arrives, and keeps the
    /// plate's own colour where none does.</summary>
    private static Border Filled(Task<ImageSource?> picture)
    {
        var square = new Border();
        _ = FillAsync();
        return square;

        async Task FillAsync()
        {
            try
            {
                if (await picture is { } shown)
                    square.Background = new ImageBrush { ImageSource = shown, Stretch = Stretch.UniformToFill };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A row without its picture still names what it is.
            }
        }
    }

    /// <summary>A record's cover, once per folder per search.</summary>
    private UIElement Cover(string folder)
    {
        if (CoverLoader is not { } load || folder.Length == 0)
            return Glyph("", 0x55);

        if (!_covers.TryGetValue(folder, out var task))
            _covers[folder] = task = LoadCoverAsync(load, folder);

        return Filled(task);
    }

    private async Task<ImageSource?> LoadCoverAsync(
        Func<string, int, CancellationToken, Task<Asked<byte[]?>>> load, string folder)
    {
        var edge = PictureEdge;

        // A cover that could not be read and a cover that is not there both leave the square empty. Neither
        // draws a made sleeve, and the next search reads again (see where _covers is cleared).
        return (await load(folder, CoverThumbEdge, CancellationToken.None)).Answers(out var bytes, out _)
               && bytes is { Length: > 0 }
            ? await RemoteImage.DecodeAsync(bytes, edge)
            : null;
    }

    /// <summary>The thumb kept on disk that covers the picture's edge, so a cover is a small read.</summary>
    private const int CoverThumbEdge = 128;

    /// <summary>An artist's photograph, the one every other surface shows for them, once per name per
    /// search.</summary>
    private UIElement ArtistPicture(ArtistWallItem artist)
    {
        if (!_portraits.TryGetValue(artist.Name, out var task))
            _portraits[artist.Name] = task = LoadPortraitAsync(artist.Name);

        return Filled(task);
    }

    /// <summary>The photograph's box, asked with no token: see <see cref="_covers"/>. A photograph that could
    /// not be had and an artist with none both leave the square empty, and the next search asks again.</summary>
    private async Task<ImageSource?> LoadPortraitAsync(string artist)
    {
        var edge = PictureEdge;

        if (!(await ArtistPortraits.ChooseAsync(artist, null)).Answers(out var url, out _) || url is null)
            return null;

        return (await ArtistPortraits.BoxAsync(url)).Answers(out var box, out _) && box is { Thumb.Length: > 0 }
            ? await RemoteImage.DecodeAsync(box.Thumb, edge)
            : null;
    }

    /// <summary>A playlist's picture where the playlists view has drawn one, and its mark in its colour
    /// where it has not.</summary>
    private static UIElement ListPicture(PlaylistTreeItem list)
    {
        if ((list.TileImage ?? list.CoverImage) is { } picture)
            return new Border { Background = new ImageBrush { ImageSource = picture, Stretch = Stretch.UniformToFill } };

        return new TextBlock
        {
            Text = list.Glyph,
            FontFamily = FaFont,
            FontSize = 15,
            Foreground = list.GlyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}

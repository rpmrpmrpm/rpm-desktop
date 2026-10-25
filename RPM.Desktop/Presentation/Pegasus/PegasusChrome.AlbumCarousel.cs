using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Shell;
using SkiaSharp;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The album wall as a carousel, the other way to show it, chosen in the settings (Album view: Grid or
/// Carousel, see <see cref="ApplyAlbumView"/>). The Android TV app's AlbumCarousel.kt is the model, figure for
/// figure.
///
/// <para><b>What it shows.</b> The covers stand along an arc on a black ground under a soft light, on a curved
/// floor: the one in front faces the reader and is the largest, and the rest are turned toward it and tilted
/// along the arc as it falls away to both sides (see <see cref="AlbumCarouselView"/>, which draws them). Under
/// the arc a card names the record in front: its title, its artist, and its year, track count, length and
/// format. It never ends: after the last record comes the first again.</para>
///
/// <para><b>The keys.</b> The remote's one stop is an empty box over the front cover (<see cref="_carouselKey"/>),
/// marked as a zoomed card so the host draws no glow round it: the front cover at full light, among covers at
/// home's card light, is what shows where the remote is.</para>
/// <list type="bullet">
/// <item>Left and Right move it one record, over <see cref="TickSeconds"/>; a press while it moves adds a step to
/// where it is going. A held key turns it at an even <see cref="HoldRate"/> records a second, reached in
/// <see cref="HoldRampSeconds"/>, and letting go glides it to rest on the next record at the speed it had (see
/// <see cref="CarouselTurn"/>). A step per key repeat would start each step from standing, so the arc would fall
/// further and further behind the key, and then race through the gap when the key came up.</item>
/// <item>OK opens the record in front (see PegasusChrome.Record); Back from it comes back to the same record,
/// which is kept for as long as the wall's records are. A held OK, the Menu key or Down opens the record's
/// menu: Play, then the record page's own entries (see <see cref="RecordMenuOf(PegasusAlbumCard)"/>).</item>
/// <item>Up opens the letter wheel where the wall is sorted by album or by artist name, or a wheel of decades
/// where it is sorted by date (see <see cref="AlbumLetters"/>). Left and Right turn it, and OK or Down jumps the
/// covers to the first record of the stop in front, by the shorter way round. Up from the wheel goes on to the
/// sort key, and Back closes it. Where the order has no wheel, Up goes to the sort key.</item>
/// </list>
///
/// <para><b>The pictures.</b> The covers within <see cref="Ahead"/> records of where the carousel is going are
/// read and decoded at the front cover's size, nearest first, so a cover coming round is drawn whole on its
/// first frame; one that falls out of reach is let go. The wall's own tiles are much smaller than the front
/// cover, so their pictures are not used.</para>
///
/// <para>The mouse works too: the wheel turns it a record a notch, a press on the front cover opens it, and a
/// press on another cover turns that cover to the front.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How long one step takes: a slow tick (the TV's TICK_MS).</summary>
    private const double TickSeconds = 0.52;

    /// <summary>How fast a held key turns the carousel, in records a second, and how long it takes to reach
    /// that from standing.</summary>
    private const double HoldRate = 7;
    private const double HoldRampSeconds = 0.35;

    /// <summary>How far past where it stands a let-go turn runs on, in seconds at the speed it had, before the
    /// record it rests on.</summary>
    private const double RunOutSeconds = 0.12;

    /// <summary>How many records to each side of the front one have their covers read ahead: those on show and
    /// eight more.</summary>
    private const int Ahead = AlbumCarouselView.Slots + 8;

    /// <summary>How many covers are read at once.</summary>
    private const int CoversAtOnce = 3;

    /// <summary>How long the front record must stay in front before its tracks are read for the card's line
    /// of facts. A held key passes seven records a second, and none of those is read.</summary>
    private static readonly TimeSpan FactsSettle = TimeSpan.FromMilliseconds(180);

    private AlbumCarouselView? _carouselView;
    private RemoteLine? _carouselKey;
    private FrameworkElement? _carouselKeyBox;
    private RemoteLine? _lettersKey;
    private FrameworkElement? _lettersKeyBox;
    private Border? _carouselCard;
    private StackPanel? _carouselCardLines;
    private TextBlock? _carouselTitle;
    private TextBlock? _carouselArtist;
    private TextBlock? _carouselFacts;

    /// <summary>Whether the settings ask for the carousel (see <see cref="ApplyAlbumView"/>).</summary>
    private bool _carouselOn;

    /// <summary>Whether the carousel is the wall on screen now.</summary>
    private bool CarouselInFront => _carouselOn && WallInFront && _wallCards.Count > 0;

    // ── where it stands ──

    /// <summary>Where the carousel stands, in records, counted without end, and the record it is going to:
    /// the presses so far, which a press during a move adds to.</summary>
    private double _carouselAt;
    private int _carouselGoing;

    /// <summary>The move under way: from where, to where, how far through it (0 to 1, and 1 when there is
    /// none), how long it takes, and whether it slows evenly (a let-go turn) or eases as a step does.</summary>
    private double _turnFrom;
    private double _turnTo;
    private double _turnT = 1;
    private double _turnSeconds = TickSeconds;
    private bool _turnSlowsEvenly;

    /// <summary>The carousel's speed in records a second over the last frame, which a hold starts from.</summary>
    private double _carouselSpeed;

    /// <summary>The way a held key turns it (-1 or 1), and the speed it has reached; 0 while no key is held
    /// down past its first press.</summary>
    private int _holdWay;
    private double _holdSpeed;

    /// <summary>The way of the Left or Right that is down on the carousel, from its first press until it
    /// comes up; 0 while none is. A press of the same way before the key came up is that key's repeat,
    /// whatever the platform says: on the Skia head a held key's repeats do not carry the repeat flag.</summary>
    private int _carouselKeyDown;

    /// <summary>When the last press or repeat of that key came, and how long the key may stay silent before it
    /// is taken as let go. A release that never reaches the view (the window lost the keyboard while the key
    /// was down) must not leave the carousel turning; the keyboard repeats far more often than this.</summary>
    private long _carouselKeyAt;
    private const int KeySilentMs = 600;

    // ── the letter wheel ──

    private IReadOnlyList<WallLetter> _letters = [];

    /// <summary>Whether the letter wheel is open.</summary>
    public bool CarouselLettersOpen { get; private set; }

    private double _lettersAt;
    private int _lettersGoing;
    private double _lettersFrom;
    private double _lettersT = 1;

    // ── the pictures and the facts ──

    /// <summary>The covers decoded at <see cref="_carouselCoverEdge"/>, by folder, and the reads in flight.
    /// A folder whose read found nothing is held as null, so it is not asked again.</summary>
    private readonly Dictionary<string, SKImage?> _carouselCovers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _carouselReads = new(StringComparer.OrdinalIgnoreCase);
    private int _carouselCoverEdge;
    private string? _carouselCoversRoot;

    /// <summary>The front record's tracks once read, for the card's line of facts, and which folder they are
    /// of.</summary>
    private RecordRead? _frontRead;
    private string? _frontReadFolder;
    private int _frontReadAsk;
    private DispatcherQueueTimer? _factsTimer;

    /// <summary>The record the card under the arc names now, so it is written only when that changes.</summary>
    private PegasusAlbumCard? _cardShown;

    // ── the view: grid or carousel ──

    /// <summary>
    /// Puts the wall in the view and the order the settings ask for: the grid or the carousel (Album view),
    /// and the default order, which is also the one the sort key last chose (see
    /// <see cref="SavedLibraryOrdering"/>). Read whenever the wall comes up, and after a change made in the
    /// settings panel while the wall is behind it, so the wall never shows a view the settings no longer say.
    /// </summary>
    private void ApplyAlbumView()
    {
        var carousel = App.Services.GetRequiredService<SettingsService>().PegasusAlbumCarousel;
        if (carousel != _carouselOn)
        {
            _carouselOn = carousel;
            if (!carousel)
                CloseCarouselLetters();
        }

        if (carousel)
            EnsureCarousel();

        // The grid's scroller is out of the tree while the carousel is shown: collapsed, its repeater realizes
        // nothing, and it is seated again when it comes back (see LibraryScroll_SizeChanged).
        LibraryScreen.Visibility = carousel ? Visibility.Collapsed : Visibility.Visible;
        LibraryBottomFade.Visibility = carousel ? Visibility.Collapsed : Visibility.Visible;
        ShowCarouselHost();

        var ordering = SavedLibraryOrdering();
        if (ordering != _libraryOrdering)
        {
            _libraryOrdering = ordering;
            if (_libraryRows.Count > 0)
                RefillLibrary();
        }

        if (carousel)
        {
            SizeCarousel();
            SyncCarouselView();
        }
    }

    /// <summary>Hands the picture what it draws from: the wall's size, where it stands and the letters; then
    /// asks for the covers around the front and writes the card under the arc.</summary>
    private void SyncCarouselView()
    {
        if (_carouselView is null)
            return;

        _carouselView.Count = _wallCards.Count;
        _carouselView.Position = _carouselAt;
        _carouselView.Letters = [.. _letters.Select(l => l.Letter)];
        AskCarouselCovers();
        FrontChanged();
        _carouselView.Redraw();
    }

    /// <summary>The carousel is in the tree while it is the view chosen and the wall has records; otherwise
    /// the wall's line saying it is empty shows in its place.</summary>
    private void ShowCarouselHost() =>
        CarouselHost.Visibility = _carouselOn && _wallCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Builds the carousel into its host, once: the picture, the ground the mouse presses on, the
    /// card under the arc, and the remote's two stops.</summary>
    private void EnsureCarousel()
    {
        if (_carouselView is not null)
            return;

        _carouselView = new AlbumCarouselView { CoverAt = CarouselCoverAt };
        CarouselHost.Children.Add(_carouselView);

        // A canvas takes no pointer on either head (see SkiaSurface), so a transparent ground over it does.
        var ground = new Border { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        ground.Tapped += CarouselGround_Tapped;
        ground.PointerWheelChanged += CarouselGround_PointerWheelChanged;
        CarouselHost.Children.Add(ground);

        _carouselTitle = CardLine(FontWeights.SemiBold, Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        _carouselArtist = CardLine(FontWeights.Normal, Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
        _carouselFacts = CardLine(FontWeights.Normal, Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        _carouselCardLines = new StackPanel { Children = { _carouselTitle, _carouselArtist, _carouselFacts } };

        // The app's pale glass: #1FFFFFFF with a #26FFFFFF edge.
        _carouselCard = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Child = _carouselCardLines,
        };
        CarouselHost.Children.Add(_carouselCard);

        (_carouselKey, _carouselKeyBox) = CarouselStop(
            press: OpenFrontRecord,
            menu: FrontRecordMenu);
        (_lettersKey, _lettersKeyBox) = CarouselStop(
            press: JumpToLetter,
            menu: null);
        _lettersKey.IsTabStop = false;   // a stop only while the wheel is open
        _lettersKey.LostFocus += (_, _) =>
        {
            // The focus going anywhere else (the sort key, a press of the mouse) closes the wheel, as on the TV.
            if (CarouselLettersOpen && !ReferenceEquals(FocusedWithin(CarouselHost), _lettersKey))
                CloseCarouselLetters();
        };
        CarouselHost.Children.Add(_carouselKey);
        CarouselHost.Children.Add(_lettersKey);

        static TextBlock CardLine(Windows.UI.Text.FontWeight weight, Color ink) => new()
        {
            FontWeight = weight,
            Foreground = new SolidColorBrush(ink),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
        };
    }

    /// <summary>
    /// One of the remote's two stops on the carousel: an empty box laid over the front cover or the front
    /// letter tile. Marked as a zoomed card with an empty photograph (see FocusLift), so the host zooms that
    /// empty box and draws no glow: the front cover itself is what shows the focus. Left and Right on it turn
    /// the carousel or the wheel (see <see cref="CarouselTurn"/>), not the line.
    /// </summary>
    private static (RemoteLine Key, FrameworkElement Box) CarouselStop(Action press, Func<MenuFlyout?>? menu)
    {
        var photo = new Border { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        FocusLift.SetPart(photo, LiftPart.Photo);
        var key = new RemoteLine(photo, press, menu, turn: null)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,   // the ground under it takes the mouse
        };
        FocusLift.SetPart(key, LiftPart.Card);
        return (key, key);
    }

    /// <summary>The carousel's figures at the host's size and the collection's text scale: the stops over the
    /// front cover and tile, and the card under the arc (the TV's sizes: a 26 title, a 19 artist and a 17 line,
    /// on a card 520 to 900 of the TV's pixels wide, 110 under the front cover's foot).</summary>
    private void SizeCarousel()
    {
        if (_carouselView is null || _carouselCard is null || _carouselCardLines is null
            || _carouselKeyBox is null || _lettersKeyBox is null)
            return;

        var size = new Size(CarouselHost.ActualWidth, CarouselHost.ActualHeight);
        if (size.Width <= 0 || size.Height <= 0)
            return;

        var scale = QueueScale;
        var px = size.Height / 1080;
        _carouselView.Sizing = scale;

        Place(_carouselKeyBox, AlbumCarouselView.FrontRect(size));
        Place(_lettersKeyBox, AlbumCarouselView.FrontLetterRect(size));

        var front = AlbumCarouselView.FrontRect(size);
        _carouselCard.Margin = new Thickness(0, front.Bottom + (110 * px), 0, 0);
        _carouselCard.MinWidth = 520 * px;
        _carouselCard.MaxWidth = Math.Min(900 * px, size.Width - 32);
        _carouselCard.CornerRadius = new CornerRadius(12 * px);
        _carouselCard.Padding = new Thickness(32 * px, 16 * px, 32 * px, 16 * px);
        _carouselCardLines.Spacing = 4 * px;
        if (_carouselTitle is not null)
            _carouselTitle.FontSize = 26 * scale;
        if (_carouselArtist is not null)
            _carouselArtist.FontSize = 19 * scale;
        if (_carouselFacts is not null)
            _carouselFacts.FontSize = 17 * scale;

        // The covers are wanted at the front cover's size on the display, in steps, so a small drag of the
        // window's edge does not read every cover again.
        var raster = XamlRoot?.RasterizationScale ?? 1;
        var edge = (int)Math.Ceiling(AlbumCarouselView.CoverEdge(size) * raster / EdgeQuantum) * EdgeQuantum;
        if (edge != _carouselCoverEdge)
        {
            _carouselCoverEdge = edge;
            ForgetCarouselCovers();
            AskCarouselCovers();
        }

        _carouselView.Redraw();

        static void Place(FrameworkElement box, Rect at)
        {
            box.Margin = new Thickness(at.X, at.Y, 0, 0);
            box.Width = at.Width;
            box.Height = at.Height;
        }
    }

    private void CarouselHost_SizeChanged(object sender, SizeChangedEventArgs e) => SizeCarousel();

    /// <summary>
    /// The wall was filled again (a new collection, a search, an order, the heart, a read of the collection
    /// after it changed): the record that was in front stays in front where the wall still holds it, as the
    /// TV keeps its front record for as long as the wall is, and otherwise the carousel starts at the wall's
    /// first record. The letters are the new order's.
    /// </summary>
    private void CarouselWallChanged()
    {
        var front = _carouselFrontFolder;
        CloseCarouselLetters();
        StopCarouselTurn();
        var kept = front is null
            ? -1
            : _wallCards.FindIndex(c => string.Equals(c.Folder, front, StringComparison.OrdinalIgnoreCase));
        _carouselAt = _carouselGoing = Math.Max(kept, 0);
        _cardShown = null;
        _frontRead = null;
        _frontReadFolder = null;

        var root = Library.ShelfRoot ?? "";
        if (!string.Equals(root, _carouselCoversRoot, StringComparison.OrdinalIgnoreCase))
        {
            _carouselCoversRoot = root;
            ForgetCarouselCovers();
        }

        _letters = AlbumLetters.For(
            _wallCards, _libraryOrdering, c => new AlbumFacts(c.Name, c.Under ?? "", c.AddedAt, c.Year, c.Plays));

        ShowCarouselHost();
        if (_carouselOn)
            SyncCarouselView();
    }

    // ── a frame ──

    /// <summary>One frame: the move under way, the hold, the letter wheel turning, and the card under the arc
    /// when another record has come to the front. Nothing at all while the carousel stands still.</summary>
    private void TickCarousel(double dt)
    {
        if (_carouselView is null || !_carouselOn)
            return;

        var was = _carouselAt;
        var moved = false;

        if (_holdWay != 0 && Environment.TickCount64 - _carouselKeyAt > KeySilentMs)
            CarouselKeyUp();

        if (_holdWay != 0)
        {
            _holdSpeed = Math.Min(_holdSpeed + (HoldRate / HoldRampSeconds * dt), HoldRate);
            _carouselAt += _holdWay * _holdSpeed * dt;

            // The record coming to the front, so its cover is asked for ahead, as on a press.
            var next = _holdWay > 0 ? (int)Math.Ceiling(_carouselAt) : (int)Math.Floor(_carouselAt);
            if (next != _carouselGoing)
            {
                _carouselGoing = next;
                AskCarouselCovers();
            }
            moved = true;
        }
        else if (_turnT < 1)
        {
            _turnT = Math.Min(1, _turnT + (dt / _turnSeconds));
            var eased = _turnSlowsEvenly ? EvenSlowing(_turnT) : PaneGlide.EaseAt(_turnT);
            _carouselAt = _turnFrom + ((_turnTo - _turnFrom) * eased);
            moved = true;
        }

        _carouselSpeed = dt > 0 ? (_carouselAt - was) / dt : 0;

        if (_lettersT < 1)
        {
            _lettersT = Math.Min(1, _lettersT + (dt / TickSeconds));
            _lettersAt = _lettersFrom + ((_lettersGoing - _lettersFrom) * PaneGlide.EaseAt(_lettersT));
            moved = true;
        }

        if (!moved)
            return;

        _carouselView.Position = _carouselAt;
        _carouselView.LettersAt = _lettersAt;
        _carouselView.Redraw();
        FrontChanged();
    }

    /// <summary>Slows evenly to a stop: the curve's slope starts at 2 and falls in a straight line to 0. A
    /// let-go turn starts at the speed the hold had, so it takes twice the distance over that speed.</summary>
    private static double EvenSlowing(double t) => 1 - ((1 - t) * (1 - t));

    // ── the keys ──

    /// <summary>One step toward <paramref name="by"/> records on: from where it stands, to where the steps so
    /// far were going and one further.</summary>
    private void StepCarousel(int by)
    {
        _holdWay = 0;
        _carouselGoing += by;
        StartTurn(_carouselGoing, TickSeconds, slowsEvenly: false);
        AskCarouselCovers();
    }

    private void StartTurn(double to, double seconds, bool slowsEvenly)
    {
        _turnFrom = _carouselAt;
        _turnTo = to;
        _turnT = 0;
        _turnSeconds = Math.Max(0.001, seconds);
        _turnSlowsEvenly = slowsEvenly;
    }

    private void StopCarouselTurn()
    {
        _holdWay = 0;
        _turnT = 1;
        _carouselKeyDown = 0;
    }

    /// <summary>A held key: one turn at an even speed, from the speed it is already moving at. The key's
    /// repeats only keep it going.</summary>
    private void HoldCarousel(int way)
    {
        if (_holdWay == way)
            return;
        _holdWay = way;
        _holdSpeed = Math.Abs(_carouselSpeed);
        _turnT = 1;
    }

    /// <summary>The held key came up: on to the next record past a short run-out, slowing evenly from the speed
    /// it was turning at.</summary>
    private void ReleaseCarousel()
    {
        if (_holdWay == 0)
            return;
        var way = _holdWay;
        _holdWay = 0;

        var speed = Math.Max(_holdSpeed, 1);
        var from = _carouselAt;
        _carouselGoing = way > 0
            ? (int)Math.Ceiling(from + (speed * RunOutSeconds))
            : (int)Math.Floor(from - (speed * RunOutSeconds));

        // An ease that slows evenly starts at twice its mean speed, so it takes twice the distance over the speed.
        StartTurn(_carouselGoing, 2 * Math.Abs(_carouselGoing - from) / speed, slowsEvenly: true);
        AskCarouselCovers();
    }

    /// <summary>
    /// Left or Right on one of the carousel's stops (see PegasusChrome.Adjust); null for anything else. On the
    /// covers the first press steps one record and the repeats hold the turn (a press of the same way before
    /// its key came up counts as a repeat). On the letter wheel every press and repeat steps one letter, as on
    /// the TV.
    /// </summary>
    private KeyTurn? CarouselTurn(object? focused, int way, bool repeat)
    {
        if (focused is null)
            return null;

        if (ReferenceEquals(focused, _lettersKey))
        {
            if (CarouselLettersOpen)
                TurnLetters(way);
            return KeyTurn.Turned;
        }

        if (!ReferenceEquals(focused, _carouselKey))
            return null;

        var now = Environment.TickCount64;
        var held = _carouselKeyDown == way && now - _carouselKeyAt <= KeySilentMs;
        _carouselKeyAt = now;
        if (repeat || held)
        {
            _carouselKeyDown = way;
            HoldCarousel(way);
        }
        else
        {
            _carouselKeyDown = way;
            StepCarousel(way);
        }
        return KeyTurn.Turned;
    }

    /// <summary>Left or Right came up (see PegasusChrome.AdjustDone): a held turn glides to rest.</summary>
    private void CarouselKeyUp()
    {
        _carouselKeyDown = 0;
        ReleaseCarousel();
    }

    /// <summary>
    /// Up and Down on the carousel, and Down from the tools above it (see PegasusChrome.NextFocus): the rule
    /// in the class summary. <paramref name="next"/> is where the search by layout would go, kept for every
    /// other move.
    /// </summary>
    private Control? AcrossCarousel(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        if (_carouselKey is null || _lettersKey is null)
            return next;

        if (ReferenceEquals(from, _carouselKey))
        {
            switch (direction)
            {
                case FocusNavigationDirection.Up when _letters.Count > 0:
                    OpenCarouselLetters();
                    return _lettersKey;
                case FocusNavigationDirection.Up:
                    return CanTakeFocus(LibrarySortButton) ? LibrarySortButton : next;
                case FocusNavigationDirection.Down:
                    OpenFrontMenuByKey();
                    return _carouselKey;   // the focus stays; the menu takes it as it opens
                default:
                    return next;
            }
        }

        if (ReferenceEquals(from, _lettersKey))
        {
            switch (direction)
            {
                case FocusNavigationDirection.Down:
                    JumpToLetter();
                    return _carouselKey;
                case FocusNavigationDirection.Up:
                    CloseCarouselLetters();
                    return CanTakeFocus(LibrarySortButton) ? LibrarySortButton : next;
                default:
                    return next;
            }
        }

        // Down from the search, the sort key or the heart: the covers, whatever lies under the key pressed.
        if (direction == FocusNavigationDirection.Down && Inside(from, LibraryTools))
            return _carouselKey;

        return next;
    }

    /// <summary>Where the focus belongs on the carousel: its stop over the front cover, once the wall has
    /// records.</summary>
    private Control? CarouselFocusTarget() =>
        _carouselKey is not null && _wallCards.Count > 0 ? _carouselKey : null;

    // ── the record in front ──

    private PegasusAlbumCard? FrontCard() =>
        _wallCards.Count == 0 ? null : _wallCards[AlbumLetters.Wrap((int)Math.Round(_carouselAt), _wallCards.Count)];

    /// <summary>The folder of the record last in front, so a wall filled again keeps it in front (see
    /// <see cref="CarouselWallChanged"/>).</summary>
    private string? _carouselFrontFolder;

    /// <summary>The record that OK opens and the menu is about: the one the carousel is going to, which is the
    /// one in front once it stands.</summary>
    private PegasusAlbumCard? GoingCard() =>
        _wallCards.Count == 0 ? null : _wallCards[AlbumLetters.Wrap(_carouselGoing, _wallCards.Count)];

    /// <summary>OK on the front cover (or a press on it): its page, as a press on a tile of the grid opens
    /// it.</summary>
    private void OpenFrontRecord()
    {
        if (GoingCard() is { } card)
            ShowRecordAsync(card);
    }

    /// <summary>Writes the card under the arc when another record has come to the front, and starts the wait
    /// before its tracks are read.</summary>
    private void FrontChanged()
    {
        if (FrontCard() is not { } card || ReferenceEquals(card, _cardShown))
            return;
        _cardShown = card;
        _carouselFrontFolder = card.Folder;

        if (_carouselTitle is not null)
            _carouselTitle.Text = card.Name;
        if (_carouselArtist is not null)
            _carouselArtist.Text = card.Under ?? "";
        if (_carouselFacts is not null)
            _carouselFacts.Text = string.Equals(_frontReadFolder, card.Folder, StringComparison.OrdinalIgnoreCase)
                                  && _frontRead is { } read
                ? FactsOf(read)
                : "";

        _factsTimer ??= CreateFactsTimer();
        _factsTimer.Stop();
        _factsTimer.Start();
    }

    private DispatcherQueueTimer CreateFactsTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = FactsSettle;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReadFrontAsync();
        };
        return timer;
    }

    /// <summary>Reads the front record's tracks, for the card's line of facts. Dropped if
    /// another record has come to the front meanwhile.</summary>
    private async void ReadFrontAsync()
    {
        if (FrontCard() is not { } card)
            return;
        if (string.Equals(_frontReadFolder, card.Folder, StringComparison.OrdinalIgnoreCase) && _frontRead is not null)
            return;

        var ask = ++_frontReadAsk;
        var read = await ReadRecordAsync(card.Folder);
        if (ask != _frontReadAsk || read is null)
            return;

        _frontRead = read;
        _frontReadFolder = card.Folder;
        if (ReferenceEquals(FrontCard(), card) && _carouselFacts is not null)
            _carouselFacts.Text = FactsOf(read);
    }

    /// <summary>The card's third line: the year most of the tracks give, the count, the length and what the
    /// files are, as the record's page writes them (see PegasusChrome.Record).</summary>
    private static string FactsOf(RecordRead read)
    {
        var rows = read.Rows;
        if (rows.Count == 0)
            return "";

        var parts = new List<string>(6);
        if (RecordYear(rows) is { } year)
            parts.Add(year.ToString(System.Globalization.CultureInfo.CurrentCulture));
        parts.Add(string.Format(Strings.Loc.Get("Rpm_Album_Tracks"), rows.Count));
        parts.Add(Duration(rows.Sum(t => t.DurationSeconds)));
        var first = rows[0];
        parts.AddRange(RPM.Core.Metadata.AudioFormat.Describe(
            first.Extension, first.Channels, first.CodecDescription,
            first.BitsPerSample, first.SampleRate, first.BitrateKbps));
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The front record's menu for a held OK or the Menu key (see RemoteLine): the record's menu away from its
    /// page, the one a held OK opens on a card of the grid (see <see cref="RecordMenuOf(PegasusAlbumCard)"/>).
    /// It opens at once; its songs are read when an entry needs them.
    /// </summary>
    private MenuFlyout? FrontRecordMenu() => GoingCard() is { } card ? FrontMenu(card) : null;

    /// <summary>Down on the covers: the front record's menu, opened under the front cover.</summary>
    private void OpenFrontMenuByKey()
    {
        if (GoingCard() is not { } card || _carouselKey is not { } key)
            return;

        // After the focus has been given back to the stop (see AcrossCarousel), so the menu takes it last.
        var menu = FrontMenu(card);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CarouselInFront && !CarouselLettersOpen)
                menu.ShowAt(key);
        });
    }

    private MenuFlyout FrontMenu(PegasusAlbumCard card)
    {
        var menu = RecordMenuOf(card);
        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom;
        return menu;
    }

    // ── the letter wheel ──

    /// <summary>Up on the covers: the wheel opens on the stop of the record in front.</summary>
    private void OpenCarouselLetters()
    {
        if (_carouselView is null || _lettersKey is null || _letters.Count == 0 || GoingCard() is not { } front)
            return;

        StopCarouselTurn();
        var letter = AlbumLetters.Of(new AlbumFacts(front.Name, front.Under ?? "", front.AddedAt, front.Year, front.Plays),
                                     _libraryOrdering);
        var at = Math.Max(0, _letters.ToList().FindIndex(l => l.Letter == letter));
        _lettersGoing = at;
        _lettersAt = _lettersFrom = at;
        _lettersT = 1;

        CarouselLettersOpen = true;
        _lettersKey.IsTabStop = true;
        if (_carouselCard is not null)
            _carouselCard.Visibility = Visibility.Collapsed;   // under the blur on the TV; gone here

        _carouselView.LettersOpen = true;
        _carouselView.LettersAt = at;
        _carouselView.Redraw();
        RPM.Core.Diagnostics.Scope.Said("window", $"album carousel: letters opened on {letter}");
    }

    /// <summary>Closes the letter wheel, leaving the covers where they are. True if it was open. Back while it
    /// is open comes here (see PegasusPlayer.TakeBack), and the focus goes back to the covers.</summary>
    public bool CloseCarouselLetters()
    {
        if (!CarouselLettersOpen)
            return false;

        CarouselLettersOpen = false;
        if (_lettersKey is not null)
        {
            var hadFocus = ReferenceEquals(FocusedWithin(CarouselHost), _lettersKey);
            _lettersKey.IsTabStop = false;
            if (hadFocus && _carouselKey is not null)
                RemoteSearch.Give(_carouselKey);
        }
        if (_carouselCard is not null)
            _carouselCard.Visibility = Visibility.Visible;
        if (_carouselView is not null)
        {
            _carouselView.LettersOpen = false;
            _carouselView.Redraw();
        }
        return true;
    }

    private void TurnLetters(int by)
    {
        _lettersGoing += by;
        _lettersFrom = _lettersAt;
        _lettersT = 0;
    }

    /// <summary>OK or Down on the wheel: the covers jump, without a slide, to the first record of the stop in
    /// front, by the shorter way round, and the wheel closes.</summary>
    private void JumpToLetter()
    {
        if (!CarouselLettersOpen || _letters.Count == 0 || _wallCards.Count == 0)
            return;

        var first = _letters[AlbumLetters.Wrap(_lettersGoing, _letters.Count)].First;
        StopCarouselTurn();
        _carouselGoing += AlbumLetters.ShorterWay(_carouselGoing, first, _wallCards.Count);
        _carouselAt = _carouselGoing;
        if (_carouselView is not null)
            _carouselView.Position = _carouselAt;
        AskCarouselCovers();
        CloseCarouselLetters();
        FrontChanged();
    }

    // ── the mouse ──

    /// <summary>A press: on the front cover it opens the record, on another cover it turns that one to the
    /// front, and anywhere while the letters are open it closes them.</summary>
    private void CarouselGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (CloseCarouselLetters())
            return;

        var size = new Size(CarouselHost.ActualWidth, CarouselHost.ActualHeight);
        var at = e.GetPosition(CarouselHost);
        if (AlbumCarouselView.FrontRect(size).Contains(at))
        {
            OpenFrontRecord();
            return;
        }

        var slot = AlbumCarouselView.SlotAt(size, at.X);
        if (slot != 0)
            StepCarousel(slot);
    }

    /// <summary>The mouse wheel: a record a notch, the letters while they are open.</summary>
    private void CarouselGround_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        var delta = e.GetCurrentPoint(CarouselHost).Properties.MouseWheelDelta;
        if (delta == 0)
            return;
        var way = delta > 0 ? -1 : 1;
        if (CarouselLettersOpen)
            TurnLetters(way);
        else
            StepCarousel(way);
    }

    // ── the pictures ──

    /// <summary>The decoded cover of the record at an endless place, or null while it is not in.</summary>
    private SKImage? CarouselCoverAt(int slot)
    {
        if (_wallCards.Count == 0)
            return null;
        var card = _wallCards[AlbumLetters.Wrap(slot, _wallCards.Count)];
        return _carouselCovers.GetValueOrDefault(card.Folder);
    }

    /// <summary>
    /// The covers within <see cref="Ahead"/> records of where the carousel is going, nearest first: the ones not
    /// in hand are read, <see cref="CoversAtOnce"/> at a time, and the ones that fell out of reach are let go, a
    /// read still in flight for one cancelled.
    /// </summary>
    private void AskCarouselCovers()
    {
        if (!_carouselOn || _carouselCoverEdge <= 0 || _wallCards.Count == 0)
            return;

        var count = _wallCards.Count;
        var wanted = new List<string>((Ahead * 2) + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var d = 0; d <= Ahead && seen.Count < count; d++)
        {
            foreach (var slot in d == 0 ? new[] { _carouselGoing } : new[] { _carouselGoing + d, _carouselGoing - d })
            {
                var folder = _wallCards[AlbumLetters.Wrap(slot, count)].Folder;
                if (seen.Add(folder))
                    wanted.Add(folder);
            }
        }

        foreach (var folder in _carouselReads.Keys.Where(f => !seen.Contains(f)).ToList())
        {
            _carouselReads[folder].Cancel();
            _carouselReads.Remove(folder);
        }
        foreach (var folder in _carouselCovers.Keys.Where(f => !seen.Contains(f)).ToList())
        {
            _carouselCovers[folder]?.Dispose();
            _carouselCovers.Remove(folder);
        }

        foreach (var folder in wanted)
        {
            if (_carouselReads.Count >= CoversAtOnce)
                break;
            if (_carouselCovers.ContainsKey(folder) || _carouselReads.ContainsKey(folder))
                continue;
            ReadCarouselCoverAsync(folder, _carouselCoverEdge);
        }
    }

    /// <summary>One cover, read at <paramref name="edge"/> pixels and decoded off the drawing thread, cut square
    /// from its middle. Lands only if it is still wanted at that size; then the next one is asked for.</summary>
    private async void ReadCarouselCoverAsync(string folder, int edge)
    {
        var stop = new CancellationTokenSource();
        _carouselReads[folder] = stop;
        SKImage? image = null;
        try
        {
            image = await Task.Run(async () =>
            {
                var bytes = await AlbumCovers.LoadForSquareAsync(folder, stop.Token, edge);
                return bytes is { Length: > 0 } ? DecodeSquare(bytes, edge) : null;
            }, stop.Token);
        }
        catch (OperationCanceledException)
        {
            image?.Dispose();
            return;
        }
        catch
        {
            // Not a picture the decoder knows: the record keeps its plain ground.
        }

        if (stop.IsCancellationRequested || edge != _carouselCoverEdge
            || !_carouselReads.TryGetValue(folder, out var mine) || !ReferenceEquals(mine, stop))
        {
            image?.Dispose();
            return;
        }

        _carouselReads.Remove(folder);
        _carouselCovers[folder] = image;
        _carouselView?.Redraw();
        AskCarouselCovers();
    }

    /// <summary>A picture's square middle at <paramref name="edge"/> pixels at most, ready to draw.</summary>
    private static SKImage? DecodeSquare(byte[] bytes, int edge)
    {
        using var source = SKBitmap.Decode(bytes);
        if (source is null || source.Width <= 0 || source.Height <= 0)
            return null;

        var side = Math.Min(source.Width, source.Height);
        var to = Math.Min(side, edge);
        var info = new SKImageInfo(to, to, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var square = new SKBitmap(info);
        using (var canvas = new SKCanvas(square))
        {
            var from = SKRect.Create((source.Width - side) / 2f, (source.Height - side) / 2f, side, side);
            source.SetImmutable();   // so the picture below shares its pixels rather than copying them
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, from, SKRect.Create(to, to), ArtScaler.SamplingFor(side, to));
        }
        square.SetImmutable();
        return SKImage.FromBitmap(square);
    }

    private void ForgetCarouselCovers()
    {
        foreach (var stop in _carouselReads.Values)
            stop.Cancel();
        _carouselReads.Clear();
        foreach (var image in _carouselCovers.Values)
            image?.Dispose();
        _carouselCovers.Clear();
    }
}

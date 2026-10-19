using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main.Genres;

/// <summary>
/// The collection read by WHAT KIND of music it is: a wall of cards, two to a row. Each card shows a
/// genre's name, set in a typeface chosen for that genre, standing on a photograph of one of its artists.
///
/// <para>The name is the card's subject. It is a DRAWING, not type: thirty-five faces, none of them
/// shipped, all cut to outlines at build time. See <see cref="GenreWordmarks"/>, which is the only way to
/// reach a genre's lettering and its colour.</para>
///
/// <para>The page decides what the wall lays out before this view sees anything (<see cref="GenreWall"/>):
/// which genres exist, in what order, and which acts are on each. This view does the pairing, the layout
/// and the fetching.</para>
///
/// <para>The control knows nothing of the library. The page gives it the genres and a delegate that turns
/// one genre into a photograph. The artists wall is fed the same way. A press on a card is handed back the
/// same way, and the page decides what an opened genre means.</para>
/// </summary>
public sealed partial class GenreWallView : UserControl
{
    /// <summary>The wall's own scroller. The shell pins the wall's scroll in step with the art drawer as
    /// the drawer slides. It can only do that to a scroller it can reach.</summary>
    public ScrollViewer ScrollHost => Scroller;

    /// <summary>A card was pressed. The page decides what a pressed genre means.</summary>
    public event Action<GenreWallItem>? GenreOpened;

    /// <summary>The genres as the page last gave them, in the app's own order. They are kept because the
    /// wall is laid out from them twice: once when they arrive, and again whenever the pane crosses the
    /// width at which two cards no longer fit on a row.</summary>
    private IReadOnlyList<GenreWallItem> _genres = [];

    /// <summary>Whether the genres on the wall were read from the open collection (see
    /// <see cref="SetGenres"/>).</summary>
    private bool _read;

    /// <summary>The rows that are laid out. This is a plain list that is REPLACED as a whole, not an
    /// observable collection changed in place. A rebuild runs on every library change. Clearing and
    /// re-adding the rows would send one collection notification per row, and the repeater handles each one
    /// separately.</summary>
    private IReadOnlyList<GenrePair> _rows = [];

    /// <summary>Cards are kept across rebuilds, so an import does not fetch again a wall of pictures that
    /// are already on screen. Cards are kept by genre AND by side. The two sides are two different drawings
    /// of the same name, and a card cannot be turned round after it is made.</summary>
    private readonly Dictionary<(string Genre, bool FlushRight), GenreTile> _kept =
        new(TileKeys.Instance);

    /// <summary>
    /// The colour one genre's card is drawn in, for a surface that has to match it. The example is the
    /// genre's own page, whose head is the same shelf shown large.
    ///
    /// <para>The colour is asked of the wall, not worked out again. It cannot be derived from the genre
    /// name alone. A card's colour comes from the photograph the card was given (see
    /// <see cref="GenreAccent.From"/>). A second surface would need the same picture and the same reading
    /// to reach the same colour.</para>
    ///
    /// <para>The result is null for a genre the wall has no card for. For a card whose photograph has not
    /// arrived yet, it is the genre's own colour. That is the same answer the page would reach
    /// alone.</para>
    /// </summary>
    public GenreAccent? AccentFor(string genre)
        => _kept.TryGetValue((genre, false), out var card) ? card.Accent : null;

    /// <summary>The wall's own measurement, shared by every card on it. A resize sets it once, and every
    /// bound card follows.</summary>
    private readonly GenreCardMetrics _metrics = new();

    /// <summary>Whether the wall was last paired up. Cards are rebuilt when this changes, and not every
    /// time the width changes. A drag that moves the pane by one pixel changes every card's size through
    /// the bindings. Rebuilding on each of those would remake the wall on every frame of the drag. How many
    /// pairs fit on a row is the layout's business, and it needs no rebuild.</summary>
    private bool _laidOutPaired = true;

    /// <summary>Turns a genre into the photograph its card stands on: one of its artists, in their own
    /// colours, already decoded to bytes. The page decides which act is used, and what happens when none of
    /// them has ever been photographed. See MainPage.Genres.</summary>
    /// <remarks>Setting this wakes the loader. The loader is the other half of
    /// <c>GenreFaceStream.Wall.Ready</c>. A wall laid out before the page provides this has workers that
    /// found nothing to read with, and they stop. Nothing else would start them again until the reader
    /// scrolls.</remarks>
    public Func<GenreWallItem, CancellationToken, Task<byte[]?>>? PortraitLoader
    {
        get => _portraitLoader;
        set
        {
            _portraitLoader = value;
            if (value is not null)
                Stream.Wake();
        }
    }

    private Func<GenreWallItem, CancellationToken, Task<byte[]?>>? _portraitLoader;

    public GenreWallView()
    {
        InitializeComponent();
        Rows.ElementPrepared += Row_ElementPrepared;
        Rows.ElementClearing += Row_ElementClearing;

        // The album wall's own stopwatch is used here too, so the two can be compared. See
        // Library.WallDiag. Switch it on with RPM_WALL_DIAG=1. Realizing a genre card costs more than
        // realizing a sleeve. It reads the genre's name out of the generated outlines (GenreWordmarks), and
        // the heaviest genre has four thousand path commands. Whether that is why a wall stutters is a
        // measured number, not a guess. This is where the number comes from.
        if (Library.WallDiag.On)
            LayoutUpdated += (_, _) => Library.WallDiag.Laid();

        // The wall checks its own width after every layout pass. Being told the width is not reliable
        // enough. A size the view never hears about leaves the cards at the measurement's starting width of
        // six hundred points. In a small window that makes cards too big for the pane, and only a drag of
        // the window corrects them. The check costs two property reads and a comparison when nothing has
        // changed. See Fit, which explains why the width is pulled rather than waiting for an event.
        LayoutUpdated += (_, _) => Fit(told: false);

        // The loader stops when the view unloads. The work it started keeps running. A genre's photograph
        // is worked out once for the session (see GenreFaces), so a wall shown again is answered from the
        // cache rather than asked again.
        Unloaded += (_, _) =>
        {
            _stream?.Stop();
            _onScreen.Clear();
        };

        // The screen the wall is on. Names are drawn at this screen's scale (see GenreTile.Lettering). It
        // is read on load, not in the constructor, because a control has no XamlRoot until it is in a tree.
        // After that the scale is watched. A window moved from a high-density screen to an ordinary one
        // changes this without changing any size. Names drawn for the old scale would be magnified.
        Loaded += (_, _) =>
        {
            if (XamlRoot is not { } root)
                return;

            _metrics.Scale = root.RasterizationScale;

            if (_watchingScale)
                return;
            _watchingScale = true;
            root.Changed += (asked, _) => _metrics.Scale = asked.RasterizationScale;
        };
    }

    /// <summary>Whether the screen is already being watched. This is set once only. A wall is loaded and
    /// unloaded each time the reader leaves the view and comes back, and the root outlives all of
    /// that.</summary>
    private bool _watchingScale;

    // ── what is on the wall ──

    /// <summary>The wall's genres, in the order they are read. That is the app's own genre order, with the
    /// unfiled genres last. This view does not decide the order.</summary>
    /// <param name="read">Whether <paramref name="genres"/> was read from the open collection. It is false
    /// before the first read. The wall then shows nothing, rather than saying the collection has no genres.
    /// The page's loader is shown in the meantime.</param>
    public void SetGenres(IReadOnlyList<GenreWallItem> genres, bool read)
    {
        _genres = genres;
        _read = read;

        // The watchdog samples these while the UI thread cannot answer. They are set here, not looked up,
        // because a diagnostic should not resolve services.
        //
        // Both databases are sampled: the shelf being drawn, and the user's own database. On a shared
        // library the shelf belongs to another person, so the user's own database is sampled as well. Every
        // write in the app goes to that database. The generated lists are cut there (three whole-table
        // reads behind the WRITE gate, read from a property; see LibraryDatabase.SessionMoods). The
        // catalogue served to a peer is built from it. The page also draws marks, favourites and play
        // counts from it while it shows someone else's records.
        Library.WallDiag.Gates ??= () =>
        {
            var library = App.Services.GetRequiredService<Services.Library.LibraryService>();
            if (!library.IsOpen)
                return default;

            var own = library.Db;
            var shelf = library.Shelf;

            return new Library.WallDiag.GateSample(
                own.ReadGate,
                own.WriteGate,
                shelf.ReadGate,
                shelf.WriteGate,
                Same: ReferenceEquals(own, shelf));
        };

        Library.WallDiag.Ui ??= DispatcherQueue;
        Library.WallDiag.Start(() =>
            $"{_rows.Count} rows of {_genres.Count} genres, {(_metrics.Paired ? "paired" : "single")}"
            + $", card {_metrics.CardWidth:0}x{_metrics.CardHeight:0}"
            + $", extent {Rows.ActualHeight:0.#}");
        Pair();
    }

    /// <summary>How many cards are on the wall. The line under the heading counts these.</summary>
    public int Shown => _genres.Count;

    /// <summary>
    /// The genres cut into rows.
    ///
    /// <para>Two to a row, or one where the pane is too narrow for two. Every card is drawn the same way
    /// round: the name at the left and the photograph at the right.</para>
    ///
    /// <para><b>The pair is not a mirror.</b> The right-hand card is not flipped. A mirrored pair read as
    /// one spread, with the two names at the ends and the faces meeting in the middle. But the name then
    /// sat on top of people. The picture now takes nearly the whole card. It has to, so that its leading
    /// edge falls where the colour is still solid, and does not draw a line down every card. A name flushed
    /// right on a card whose picture is under it would be drawn across someone's face. This happened on
    /// several genres.</para>
    ///
    /// <para>So the side is not chosen by the slot. Every card takes the one side that is known to be
    /// clear. The pair is still a unit of LAYOUT, because it keeps two cards from being split across two
    /// lines. It is not a unit of drawing.</para>
    /// </summary>
    private void Pair()
    {
        var paired = _metrics.Paired;
        _laidOutPaired = paired;

        var step = paired ? 2 : 1;
        var rows = new List<GenrePair>((_genres.Count + step - 1) / step);
        for (var at = 0; at < _genres.Count; at += step)
        {
            var left = Card(_genres[at], flushRight: false);
            var right = paired && at + 1 < _genres.Count
                ? Card(_genres[at + 1], flushRight: false)
                : null;
            rows.Add(new GenrePair(left, right));
        }

        _rows = rows;
        Rows.ItemsSource = rows;

        // A re-paired wall holds different rows, so the cards that were on screen are no longer the same.
        // The realize calls that follow say what is on screen now. The loader is woken too, because a
        // rebuild can change what is worth doing.
        _onScreen.Clear();
        Stream.Wake();

        EmptyHint.Visibility = rows.Count == 0 && _read ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One genre's card for one side of the wall, kept from last time where there is one.
    ///
    /// <para>A genre that changes sides gets a new card, because its name is set differently on each side.
    /// It does NOT get a new fetch. The picture is carried over from the side where the genre was last
    /// drawn, along with the fact that it has already been asked for. The picture belongs to the genre.
    /// Only the lettering belongs to the side.</para>
    /// </summary>
    private GenreTile Card(GenreWallItem genre, bool flushRight)
    {
        if (_kept.TryGetValue((genre.Name, flushRight), out var kept))
        {
            // THE PICTURE GOES WHEN THE ACTS DO. A card is kept by genre. Its photograph is the face of the
            // first act in that genre that anybody has a picture of. So when a shelf's acts change, the
            // card may still show someone who is no longer on the shelf, and Asked is true, so the card
            // would never look again. Signing out showed this. The collection went back to what is on this
            // disk, and the Rock card kept the face of an artist who had been removed with the cloud
            // catalogue.
            //
            // A change in the RECORDS is different. An import adds folders to a genre several times a
            // minute, and that says nothing about who the card is of. Fetching again on each import would
            // put every card on the wall back through two lookups and two models for nothing.
            if (!kept.Genre.SameActsAs(genre))
            {
                kept.Picture = null;
                kept.Asked = false;
            }

            kept.Genre = genre;   // The card is kept, but its genre is read again.
            return kept;
        }

        var card = new GenreTile(genre, _metrics, flushRight);
        if (_kept.TryGetValue((genre.Name, !flushRight), out var mirrored))
        {
            card.Picture = mirrored.Picture;
            card.Asked = mirrored.Asked;
        }

        return _kept[(genre.Name, flushRight)] = card;
    }

    /// <summary>Genre names are compared without regard to case, as the rest of the app compares them. The
    /// side is a plain boolean. A custom comparer is needed because the default comparer for a tuple
    /// compares the names in a case-sensitive way.</summary>
    private sealed class TileKeys : IEqualityComparer<(string Genre, bool FlushRight)>
    {
        public static readonly TileKeys Instance = new();

        public bool Equals((string Genre, bool FlushRight) a, (string Genre, bool FlushRight) b)
            => a.FlushRight == b.FlushRight
               && StringComparer.OrdinalIgnoreCase.Equals(a.Genre, b.Genre);

        public int GetHashCode((string Genre, bool FlushRight) key)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Genre), key.FlushRight);
    }

    // ── the pictures ──

    /// <summary>A row has come on screen, or come back. Both of its cards' pictures are asked for, once
    /// ever each. The check runs on every realize, not from the template's DataContextChanged. A repeater
    /// that hands a recycled element back to the SAME row rebinds nothing, so nobody would ask.</summary>
    private void Row_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
        => Library.WallDiag.Time("realize", () => RowPrepared(args));

    private void RowPrepared(ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _rows.Count)
            return;

        Library.WallDiag.Realized(args.Index);

        var row = _rows[args.Index];
        _onScreen.Add(row);

        // A picture starts hidden (see the template), and fades up when it lands. A card that ALREADY has a
        // picture, because it was scrolled off and back, or drawn again after a rebuild, is shown at once.
        // The fade is for a picture arriving. Fading the same card in each time it comes back on screen
        // would look like the wall breathing.
        //
        // The state of the card is checked on every realize, not remembered from the fetch. That is what
        // makes this safe in either order. The picture can land before this element exists, or long after
        // it. Both cases get the same answer.
        if (Dress(args.Element, "LeftDress") is { } left)
            left.Opacity = row.Left.Answered ? 1 : 0;
        if (Dress(args.Element, "RightDress") is { } right)
            right.Opacity = row.Right is { Answered: true } ? 1 : 0;

        // The loader is woken here, and no fetch is started for each card. The stream decides which card
        // comes next, how many run at once, and how much of a frame they may take. See
        // <see cref="GenreFaceStream"/>.
        Stream.Wake();
    }

    /// <summary>A row has gone off screen. Nothing is cancelled. The picture it is waiting for is still
    /// worth having, even if no one can see it this second. Having it ready makes scrolling back to that
    /// row free. This only stops the row being counted as one that somebody is waiting for.</summary>
    private void Row_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is FrameworkElement { DataContext: GenrePair row })
            _onScreen.Remove(row);
    }

    /// <summary>The rows the repeater holds an element for. The set holds rows, not cards, for the same
    /// reason the album wall keeps rows. A re-pair replaces the cards inside a row. A set of cards captured
    /// at realize time would name genres that are no longer on that row.</summary>
    private readonly HashSet<GenrePair> _onScreen = [];

    /// <summary>The cards in the rows that are on screen now. They are read at the time of the call, not
    /// remembered.</summary>
    private IEnumerable<GenreTile> CardsOnScreen()
    {
        foreach (var row in _onScreen)
            foreach (var card in row.Both)
                yield return card;
    }

    private IEnumerable<GenreTile> AllCards()
    {
        foreach (var row in _rows)
            foreach (var card in row.Both)
                yield return card;
    }

    private GenreFaceStream? _stream;

    /// <summary>The loader. It is built the first time it is needed, not in the constructor. It closes over
    /// the delegate that the page fills in later, and over a XamlRoot, which the control does not have
    /// until it is in a window.</summary>
    private GenreFaceStream Stream => _stream ??= new GenreFaceStream(DispatcherQueue, new GenreFaceStream.Wall(
        OnScreen: CardsOnScreen,
        Everything: AllCards,
        Ready: () => _portraitLoader is not null,
        Read: (card, ct) => _portraitLoader is { } load ? load(card.Genre, ct) : Task.FromResult<byte[]?>(null),
        Colour: ColourAsync,
        Show: ShowAsync)
    {
        Spent = Library.WallDiag.Spent,
    });

    /// <summary>
    /// The card's colour, taken from its own photograph: the picture's most vivid tone, snapped to the
    /// palette (see <see cref="GenreAccent.From"/>).
    ///
    /// <para><b>Vivid, not dominant.</b> This differs from an artist's box, and it has to. The biggest area
    /// of a press shot is often the wall behind the person, which is white, black or grey. A box wears its
    /// colour as a veil over a drained photograph, so a colourless answer would be invisible. Here the
    /// colour IS the card. <c>CoverColor.Tint</c> reads both values from one decode and prefers the vivid
    /// one, which is the reading this wall needs.</para>
    ///
    /// <para>It runs on the pool. It is read BEFORE the picture is shown, so the colour and the photograph
    /// come up in the same fade. Nothing is drawn from it here. The card holds the colour, and the layers
    /// are bound to it.</para>
    /// </summary>
    private async Task ColourAsync(GenreTile card, byte[] bytes)
    {
        if (await Task.Run(() => Common.CoverColor.Tint(bytes)) is { } tint)
            card.TakeColourFrom(tint);
        else
            card.KeepItsOwnColour();
    }

    /// <summary>
    /// The bytes go onto the card, and then the card is brought up.
    ///
    /// <para>The image is decoded at the card's own HEIGHT, because height is the side a portrait is
    /// limited by. Press shots arrive several hundred points tall, and a card is a couple of hundred.
    /// Handed over whole, the renderer shrinks them with four samples per pixel, and the result is grain
    /// rather than a photograph.</para>
    ///
    /// <para>This is the one thing the loader does on the drawing thread, and the whole of what its budget
    /// is spent on. See <see cref="GenreFaceStream"/>.</para>
    /// </summary>
    private async Task ShowAsync(GenreTile card, byte[]? bytes)
    {
        // This job is named, so a cancellation that happens with none of our frames on the stack can still
        // be traced to the wall that asked for it. The renderer opens the picture for the element, from a
        // property change, on the pool. None of that stack is ours, and nothing on it can be changed. The
        // parent is carried in an AsyncLocal instead. It flows through the assignment below and through
        // every continuation started under it. See Scope.Job and `rpmscope throws`.
        using var job = RPM.Core.Diagnostics.Scope.Job(nameof(ShowAsync), nameof(GenreWallView));

        // No photograph is coming for this shelf. The card takes its own colour and is shown with it. That
        // is a finished card. See GenreFaceStream.FillAsync.
        if (bytes is null)
        {
            card.KeepItsOwnColour();
            Reveal(card);
            return;
        }

        card.Picture = await RemoteImage.DecodeAsync(
            bytes,
            Services.Art.ArtScaler.DecodeEdgeFor(
                _metrics.CardHeight, XamlRoot?.RasterizationScale ?? 1));

        // The picture is set on the card first, and the card is shown after. The binding is what puts the
        // picture into the element. A fade started on an Image with nothing in it yet would fade in an
        // empty box.
        Reveal(card);
    }

    /// <summary>
    /// Brings one card's picture up, if that card is on screen.
    ///
    /// <para>Nothing happens when it is not on screen. An element that is not realized has no opacity to
    /// animate. It gets its opacity when it is realized, and that is read from the card (see
    /// <see cref="Row_ElementPrepared"/>). This keeps it safe when the repeater recycles a row while a
    /// fetch is still running.</para>
    ///
    /// <para><b>One clock, not a storyboard per card.</b> The album wall's covers come up on the same clock
    /// (see <see cref="Common.SharpFade"/>, which explains why). On Skia every opacity write repaints the
    /// whole window. A storyboard per card would repaint it once per card for each write. A screenful of
    /// photographs landing together would then be a screenful of clocks, all asking for frames on the
    /// thread the wall is scrolled on.</para>
    /// </summary>
    private void Reveal(GenreTile card)
    {
        var index = IndexOf(card, out var flushRight);
        if (index < 0 || Rows.TryGetElement(index) is not { } element
            || Dress(element, flushRight ? "RightDress" : "LeftDress") is not { } dress)
            return;

        Common.SharpFade.Start(dress);
    }

    /// <summary>Which row a card is on, and which side of it. Off the rows as they stand rather than off
    /// the card's own <see cref="GenreTile.FlushRight"/>: a card that has been re-paired since the fetch
    /// went out is on a row that no longer holds it, and this says so.</summary>
    private int IndexOf(GenreTile card, out bool flushRight)
    {
        for (var at = 0; at < _rows.Count; at++)
        {
            if (ReferenceEquals(_rows[at].Left, card))
            {
                flushRight = false;
                return at;
            }
            if (ReferenceEquals(_rows[at].Right, card))
            {
                flushRight = true;
                return at;
            }
        }

        flushRight = false;
        return -1;
    }

    /// <summary>The group that holds one card's colour and its photograph, in a realized row. It is found
    /// by name, not by walking the tree. The template belongs to this view, so the names are the contract
    /// between the two.</summary>
    private static FrameworkElement? Dress(UIElement element, string name)
        => (element as FrameworkElement)?.FindName(name) as FrameworkElement;

    // ── the layout ──

    /// <summary>
    /// The scroller changed size. The wall is drawn again at the new width. See <see cref="Fit"/>.
    /// </summary>
    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e) => Fit(told: true);

    /// <summary>
    /// Draws the wall at the width it actually has. It does nothing when that is already the width it is
    /// drawn at.
    ///
    /// <para><b>Checked after every layout pass, not only when the wall is told.</b> A resize is an event,
    /// and an event happens at one moment. If the wall misses it, the cards keep their old size. The
    /// measurement starts at six hundred points, so a pane could be full of cards too large for it, and
    /// nothing would correct them. The reader would have to drag the window to get the wall back. The width
    /// is a fact, though, and the layout knows it every time it runs. So this method pulls: it asks the
    /// scroller how wide it is. If that is the width the cards are already drawn at, the check costs two
    /// reads and a comparison. This is order-independent, as the house rule asks. A wall shown collapsed,
    /// measured before its pane settled, or told once at a width it was never arranged at, comes out at the
    /// right size on the next pass.</para>
    ///
    /// <para>Three things follow from the width, and only one of them is a rebuild. The card's own
    /// measurements move through the shared measurement's bindings. The layout is told how wide a pair is,
    /// so it can work out how many fit on a row. The cards are remade ONLY if the wall starts or stops
    /// pairing them up, because that is the one change a card cannot absorb. A card is built for the side
    /// it stands on.</para>
    /// </summary>
    /// <param name="told">Whether this call came from the wall being told its size changed, rather than the
    /// wall asking for it. It is used only so that a correction the event missed can be named in the
    /// diagnostic. See Library.WallDiag, and RPM_WALL_DIAG=1.</param>
    private void Fit(bool told)
    {
        var available = Math.Floor(Scroller.ActualWidth - Scroller.Padding.Left - Scroller.Padding.Right);

        // There is no room yet. The view is collapsed, or the pane has not been arranged. There is nothing
        // to draw a card from, and the last real numbers stay in place.
        if (available < 1)
            return;

        _metrics.Scale = XamlRoot?.RasterizationScale ?? _metrics.Scale;

        // The width the cards are already drawn at is checked before anything is written. This runs after
        // every layout pass. A wall that recalculated its numbers each time would raise eight notifications
        // per pass, and every card would drop its lettering (see GenreTile).
        if (_fitted && _metrics.Width == available)
            return;

        if (_fitted && !told)
            Library.WallDiag.Healed($"genres fitted late: {_metrics.Width:0} → {available:0}");

        // A change to a width the wall was already drawn at is treated as a move in progress, until the
        // width has been still for SettleMs. Moving is set BEFORE the width, so the cards see it when the
        // width notifications reach them. See GenreTile, which keeps its lettering bitmap while Moving is
        // true.
        if (_fitted)
        {
            _metrics.Moving = true;
            _settle ??= CreateSettleTimer();
            _settle.Stop();
            _settle.Start();
        }

        _fitted = true;
        _metrics.Set(available);

        // Both are set together, every time. The floor and the item width must be the same number.
        // Otherwise the floor is not a floor. A repeater measured narrower than one whole item divides by
        // zero inside the layout. See the markup, where that trap is described.
        PairLayout.MinItemWidth = _metrics.PairWidth;
        PairLayout.MinItemHeight = _metrics.CardHeight;
        Rows.MinWidth = _metrics.PairWidth;

        if (_metrics.Paired != _laidOutPaired)
            Pair();
    }

    /// <summary>Whether the wall has ever been drawn at a width it was given. Until then the measurement
    /// holds the number it started with. A first pass that happens to match that number still has to set
    /// the layout's floors.</summary>
    private bool _fitted;

    /// <summary>How long the width must stay still before the cards redraw their lettering at that width.
    /// It is the same delay that CardRoom uses, so the background and the name sharpen together.</summary>
    private const int SettleMs = 140;

    /// <summary>Clears <see cref="GenreCardMetrics.Moving"/> once the width has stopped. It is held in a
    /// field, because a DispatcherQueueTimer that nothing references is collected, and then it never ticks.
    /// It is not stopped on Unload. A wall unloaded in the middle of a move would otherwise keep Moving
    /// set, and its names would never redraw.</summary>
    private DispatcherQueueTimer? _settle;

    private DispatcherQueueTimer CreateSettleTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(SettleMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _metrics.Moving = false;
        };
        return timer;
    }

    // ── the press ──

    /// <summary>A card was pressed, so the page opens that genre. The card does not know what that means.
    /// This is what lets the same wall appear in the library panel and answer to whatever the shell decides
    /// a genre's page is.</summary>
    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GenreTile card })
            GenreOpened?.Invoke(card.Genre);
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Viz;
using Windows.Foundation;
using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Genres.Models;

/// <summary>
/// The one live measurement the genres wall sizes to: how wide the wall is, and everything a card is
/// worked out from it. One instance is shared by every card, so a resize sets it once and every bound
/// card follows. The artists wall works the same way, for the same reason.
/// </summary>
[RpmBindable]
public sealed partial class GenreCardMetrics : INotifyPropertyChanged
{
    /// <summary>
    /// The shape a card is drawn in, as a height over a width.
    ///
    /// <para>Landscape, but only just, because of the picture. A card gives most of its width to ONE
    /// artist's photograph, so the panel that holds it is <c>PictureShare / Shape</c>, which at these
    /// numbers is a little taller than it is wide. Press shots are portraits, and a card cut at three by
    /// two would crop the top of every head to fit a wide, short shape. The two numbers go together:
    /// a larger picture share without a taller card makes that panel square.</para>
    /// </summary>
    public const double Shape = 1 / 1.3;

    /// <summary>
    /// How much of a card's width the photograph takes.
    ///
    /// <para>It must be nearly all of the width. The colour over the picture is solid for the first
    /// fifth of the card and only then starts to fade (see <c>GenreWordmarks.Across</c>), so the
    /// picture's own leading edge has to fall INSIDE that solid part, or it shows as a line down every
    /// card. With a smaller share the edge would sit under four fifths of the colour, and a fifth of a
    /// photograph appearing all at once is a visible edge that no fade can hide.</para>
    /// </summary>
    public const double PictureShare = 0.85;

    /// <summary>The shape of the photograph itself: its share of the width over the whole height. It is
    /// a little taller than it is wide, which is why the card is only just landscape. The framing
    /// models are asked to find a window of this shape, not of the card's shape. See
    /// MainPage.Genres.</summary>
    public const double PictureShape = PictureShare / Shape;

    /// <summary>How much of a card's width the name may take. It is generous, because a name like
    /// "Soundtrack" set in condensed capitals is four times as wide as it is tall and would otherwise be
    /// drawn very small.</summary>
    private const double NameRoomShare = 0.72;

    /// <summary>How much of a card's height the name may take. A little over a quarter: enough that the
    /// typeface can be recognised, and not so much that the lettering fills the card and the photograph
    /// becomes a margin.</summary>
    private const double NameHeightShare = 0.28;

    /// <summary>The space between two cards, across and down. It is the artists wall's gap, because
    /// these two walls are shown in the same panel and the user moves between them.</summary>
    public const double Gutter = 14;

    /// <summary>
    /// How wide a card is aimed at, and how narrow one may end up.
    ///
    /// <para>These are the artists wall's own numbers, on purpose: the two walls show the same
    /// collection in two ways, in the same panel, and a genre card one and a half times the size of an
    /// artist box would look like a different app. This is a target, not a size: the row is filled
    /// edge to edge, so the real card width is this value adjusted to a whole number of columns.</para>
    /// </summary>
    private const double TargetWidth = 300;
    private const double NarrowestCard = 190;

    /// <summary>The shortest and tallest a card is ever drawn, whatever the shape asks for.</summary>
    private const double Shortest = 120;
    private const double Tallest = 300;

    private double _width = 600;

    /// <summary>
    /// How many pixels the screen puts in a point.
    ///
    /// <para>It is kept on the wall's own measurement and not read by each card, like every other number
    /// here: it is one fact about where the wall is shown, and a card that asked its own XamlRoot would
    /// give a second answer. It would also get no answer before it is in the tree, which is exactly
    /// when a card needs it. The lettering is drawn at this size (see <c>GenreTile.Lettering</c>), so a
    /// window dragged to a high-density screen redraws its names instead of magnifying them.</para>
    /// </summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (_scale == value || value <= 0)
                return;
            _scale = value;
            OnChanged(nameof(Scale));
        }
    }

    private double _scale = 1;

    /// <summary>
    /// Whether the width is still changing: a pane fold, a pane drag or a window drag in progress.
    /// Set by the wall, which owns the clock that decides the width has stopped (see
    /// <c>GenreWallView.Fit</c>).
    ///
    /// <para>Cards read it to choose between redrawing their lettering at the new size and keeping the
    /// bitmap they have, scaled by the Image, until the width settles. See <see cref="GenreTile"/>.</para>
    /// </summary>
    public bool Moving
    {
        get => _moving;
        set
        {
            if (_moving == value)
                return;
            _moving = value;
            OnChanged();
        }
    }

    private bool _moving;

    public double Width
    {
        get => _width;
        private set
        {
            if (_width == value)
                return;
            _width = value;
            OnChanged();
            OnChanged(nameof(Columns));
            OnChanged(nameof(Paired));
            OnChanged(nameof(CardWidth));
            OnChanged(nameof(CardHeight));
            OnChanged(nameof(PairWidth));
            OnChanged(nameof(NameWidth));
            OnChanged(nameof(NameHeight));
            OnChanged(nameof(PictureWidth));
        }
    }

    /// <summary>
    /// How many cards go across. It is always an EVEN number, or one.
    ///
    /// <para>It is even because the wall's unit is a mirrored PAIR (a name at each outer edge, the two
    /// photographs meeting in the middle, see <see cref="GenrePair"/>), and the wall cannot draw half a
    /// pair.</para>
    ///
    /// <para><b>Use as few pairs as keeps a card from growing too large, and never so many that a card
    /// becomes narrower than a card can be.</b> Rounding to the NEAREST pair looks reasonable but is
    /// wrong: an even count cannot always land near the target, and for a band of pane widths around
    /// 800 points (an ordinary window) the nearest answer is two cards of 450. That is one and a half
    /// times the size of an artist's box in the same panel. Erring towards more pairs gives a card that
    /// is sometimes smaller than an artist's and never much larger. That is the better way round: a
    /// wall of slightly small cards looks like a wall, and a wall of very large ones looks wrong.</para>
    /// </summary>
    public int Columns => Math.Max(1, Pairs * 2);

    /// <summary>How large a card may get before the wall adds another pair to the row. It is a little
    /// over the target, so an ordinary pane is not pushed to the next pair for a few points.</summary>
    private const double WidestCard = TargetWidth * 1.15;

    /// <summary>How many pairs go on a row. It is zero when the pane cannot hold two usable cards. The
    /// wall then draws a single unmirrored column, which is <see cref="Paired"/> false.</summary>
    private int Pairs
    {
        get
        {
            if ((_width - Gutter) / 2 < NarrowestCard)
                return 0;

            var pairs = 1;
            while (Across(pairs) > WidestCard && Across(pairs + 1) >= NarrowestCard)
                pairs++;

            return pairs;
        }
    }

    /// <summary>What a card comes out at for a given number of pairs on the row.</summary>
    private double Across(int pairs) => (_width - Gutter * (2 * pairs - 1)) / (2 * pairs);

    /// <summary>Whether the wall is laying out mirrored pairs at all. False only on a pane too narrow
    /// to hold two cards, where every card is drawn as a left one.</summary>
    public bool Paired => Pairs > 0;

    public double CardWidth => Paired ? Math.Floor(Across(Pairs)) : Math.Floor(_width);

    public double CardHeight => Math.Round(Math.Clamp(CardWidth * Shape, Shortest, Tallest));

    /// <summary>How wide a whole pair is, which is what the wall actually lays out. The row holds
    /// pairs, not cards, so a pair is never split across two lines.</summary>
    public double PairWidth => Paired ? CardWidth * 2 + Gutter : CardWidth;

    public double NameWidth => Math.Round(CardWidth * NameRoomShare);

    /// <summary>
    /// How tall the LETTERING is on every card of this wall, from the top of the letters to the line
    /// they stand on. This is the wall's one decision about the size of a name.
    ///
    /// <para>It is not the height of the drawing, which is different on every card: a typeface with a
    /// shadow under it, a name with a descender and a name set on two lines all put ink below that
    /// line, and how much has nothing to do with how big the letters are. See
    /// <see cref="GenreWordmark.Base"/>, where a drawing says what fraction of itself stands above its
    /// baseline.</para>
    ///
    /// <para>It is the size at which the longest name in the table still fits the room a card gives it,
    /// so every name can be set at this size and none has to be condensed. It is also limited to the
    /// card's own share, for a very wide wall: beyond that the letters would outgrow the band they
    /// stand in.</para>
    /// </summary>
    public double NameHeight => Math.Min(
        NameWidth / GenreWordmarks.WidestName,
        CardHeight * NameHeightShare);

    public double PictureWidth => Math.Round(CardWidth * PictureShare);

    public void Set(double width) => Width = width;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One card on the genres wall: the kind of music, the acts filed under it, and the photograph of one
/// of them the card is drawn on.
///
/// <para><b>One photograph is what the card is for.</b> A single photograph is used instead of a band
/// of five faces, because five press shots across a row a few hundred points wide are five thumbnails,
/// and a wall of them is texture. One face, half a card tall, is a real photograph. The name beside it
/// is set in a typeface chosen for that genre, which is what says what kind of music it is, so the
/// picture does not have to carry that on its own.</para>
///
/// <para><b>The base colour is the genre's, not the photograph's.</b> It is settled in
/// <c>scripts/wordmarks.json</c> beside the typeface it is set in (see <see cref="GenreWordmarks"/>,
/// which is the only way to either). Once the card's photograph has been read, the card takes its
/// colour from that photograph instead (see <see cref="Accent"/>).</para>
/// </summary>
[RpmBindable]
public sealed partial class GenreTile : INotifyPropertyChanged
{
    public GenreTile(GenreWallItem genre, GenreCardMetrics metrics, bool flushRight)
    {
        Genre = genre;
        Metrics = metrics;
        FlushRight = flushRight;

        _mark = GenreWordmarks.For(genre.Name);
        NameAspect = _mark?.Aspect ?? 1;
        NameBase = _mark?.Base ?? 1;

        // The card's shape comes from the wall and the name's shape comes from the typeface. The box
        // the letters are drawn in is where the two meet, so it moves when the wall is resized, and the
        // card has to be told.
        metrics.PropertyChanged += (_, _) =>
        {
            OnChanged(nameof(NameBoxWidth));
            OnChanged(nameof(NameBoxHeight));
            OnChanged(nameof(NameMargin));

            // The lettering changes with them, because it is drawn AT that box and not stretched to it,
            // but ONLY when the box actually moved. One resize raises this notification eight times
            // (see GenreCardMetrics.Width), and a card that dropped its drawing on each one would draw
            // the same name eight times for one drag of a splitter. The drawing is dropped here, not
            // redrawn: the card asks again when it is next drawn, which is once however many
            // notifications arrive.
            if (Box == _drawn)
                return;

            // While the width is still moving, keep the current drawing: the Image is Stretch=Fill and
            // bound to the box, so it scales the old bitmap to the new size. Redrawing on every frame
            // of a pane move costs about 15 to 20 ms per visible name, with about twenty cards, which
            // holds a fold to 4 or 5 frames of 8. The redraw happens once, when the wall clears Moving
            // and this runs again. A card with no drawing yet still draws, so a name is never missing.
            if (Metrics.Moving && _lettering is not null)
                return;

            _lettering = null;
            OnChanged(nameof(Lettering));
        };
    }

    /// <summary>
    /// What the card is about: the genre and the acts on it.
    ///
    /// <para>It can be set again, as a box on the artists wall can: cards are reused by genre across
    /// rebuilds so an import does not fetch every picture on the wall again, and a card that kept the
    /// earlier reading could draw an artist the genre no longer has.</para>
    /// </summary>
    public GenreWallItem Genre { get; set; }

    /// <summary>The wall's own measurement, shared by every card on it. A card that sized itself would
    /// give a second answer about one shape.</summary>
    public GenreCardMetrics Metrics { get; }

    /// <summary>
    /// Which way round the card is drawn: the name at the outer edge and the photograph towards the
    /// middle of the wall, so a row of two looks like one spread with the faces meeting in the gutter.
    ///
    /// <para>It is fixed when the card is made and not worked out from an index. The wall lays out PAIRS
    /// (see <see cref="GenrePair"/>), so a card's side is the side of the pair it was built into, and
    /// no calculation can disagree with the layout about it.</para>
    /// </summary>
    public bool FlushRight { get; }

    /// <summary>The genre as the app spells it. It is not drawn (the wordmark is), but it is what a
    /// screen reader announces and what the tooltip shows.</summary>
    public string Name => Genre.Name;

    /// <summary>The wordmark the card is set in, or null for a genre with no wordmark. The generator
    /// does not allow that, so it only happens while a developer is editing the table.</summary>
    private readonly GenreWordmark? _mark;

    /// <summary>
    /// The name, set in this genre's own typeface and drawn at the size the card shows it. Null for a
    /// genre that is not in the table; the card then draws its colour and no name.
    ///
    /// <para>It is built on first use and not in the constructor, and so is everything else the card
    /// wears. These are XAML objects, and XAML objects need a UI thread to be created on. If they were
    /// built eagerly, a card could only be made on a UI thread, and the wall's layout code could not be
    /// tested. It also means a rebuild that keeps every card and changes nothing costs nothing.</para>
    /// </summary>
    public ImageSource? Lettering => _lettering ??= Draw();

    /// <summary>
    /// The name drawn, timed when RPM_WALL_DIAG is set.
    ///
    /// <para>This is the one expensive thing a genre card does on the thread that draws it, and it is
    /// paid ONCE per size, not once per frame (see <see cref="GenreWordmarks.Lettering"/>, which explains
    /// what an outline costs the renderer). The timing names the genre, because a wall whose card time
    /// is all in two or three names has a data problem, and one where it is spread evenly does not.</para>
    ///
    /// <para>Null before the wall has been measured, because a box of zero by zero is not a drawing. It
    /// is asked for again after the measurement, which notifies and clears it.</para>
    /// </summary>
    private ImageSource? Draw()
    {
        if (_mark is null)
            return null;

        var box = _drawn = Box;

        ImageSource? drawn = null;
        Library.WallDiag.Time(
            $"wordmark:{Genre.Name}",
            () => drawn = GenreWordmarks.Lettering(
                _mark, Accent, FlushRight ? GenreFlush.Right : GenreFlush.Left, box.Width, box.Height));
        return drawn;
    }

    private ImageSource? _lettering;

    /// <summary>The name's box in PIXELS, which is the only thing the drawing depends on: two different
    /// combinations of points and scale that give the same pixels are the same drawing.</summary>
    private (int Width, int Height) Box => (
        (int)Math.Round(NameBoxWidth * Metrics.Scale),
        (int)Math.Round(NameBoxHeight * Metrics.Scale));

    /// <summary>The box that the card's current drawing was made for.</summary>
    private (int Width, int Height) _drawn;

    /// <summary>How wide the drawing is for every one unit of its height. The wall decides how tall a
    /// name is; this decides how wide that makes it.</summary>
    public double NameAspect { get; }

    /// <summary>
    /// How wide the name is, which is its height times its own proportions.
    ///
    /// <para>The HEIGHT comes from the wall and is the same on every card (see
    /// <see cref="GenreCardMetrics.NameHeight"/>); the width comes from the word. Fitting each name
    /// to the largest box that holds it would set "Pop" at the full name height and
    /// "Electronic / Dance" at a third of it. Each card would look right alone, but the wall would not:
    /// size reads as emphasis, as though the collection valued one shelf more than another, when it
    /// only shows that one word is shorter than the other.</para>
    ///
    /// <para>Nothing is squashed: a name is always drawn at its own proportions. Only the choice of
    /// which side the wall decides is different.</para>
    /// </summary>
    public double NameBoxWidth => Math.Round(NameBoxHeight * NameAspect);

    /// <summary>
    /// The drawing's height: the wall's lettering height plus whatever this particular name hangs
    /// below its baseline. NOT rounded: the width is, so the letters start on a whole point and the
    /// outer edge of the name is crisp. Rounding both would distort the drawing's shape, which on a
    /// small card is a percent or two of squash on the typeface.
    /// </summary>
    public double NameBoxHeight => Metrics.NameHeight / NameBase;

    /// <summary>What fraction of this drawing stands above its baseline (see
    /// <see cref="GenreWordmark.Base"/>). It is 1 for a name that is not in the table, which has no
    /// drawing anyway.</summary>
    public double NameBase { get; }

    /// <summary>
    /// Where the drawing is placed: against the outer edge of the card, and far enough down that the
    /// top of the letters is in the same place on every card.
    ///
    /// <para>The drawing is aligned by its top, not centred, and that is what lines the wall up.
    /// Centring puts the middle of each DRAWING on one line, and a drawing is as tall as whatever hangs
    /// under its baseline, so the letters of a name with a shadow under them would sit higher than the
    /// letters of one without. Aligned by the top, with every drawing's top being the top of its own
    /// letters, they line up.</para>
    /// </summary>
    public Thickness NameMargin => new(NameEdge, NameTop, 0, 0);

    /// <summary>How far the name stands off the outer edge of the card.</summary>
    private const double NameEdge = 18;

    /// <summary>How far down the name starts: the lettering is centred in the card, which puts the ink
    /// that hangs below the baseline a little under the middle. Centring the whole DRAWING instead
    /// would move the letters up and down the card by however much each name hangs.</summary>
    private double NameTop => Math.Round((Metrics.CardHeight - Metrics.NameHeight) / 2);

    /// <summary>
    /// The colour this card is drawn in, and its material (see <see cref="GenreAccent"/>). Every layer
    /// below is worked out from it.
    ///
    /// <para>It is the genre's own colour until a photograph has been read, and the photograph's colour
    /// after that. A colour fixed in a table has no relation to the picture on it, so thirty-six of them
    /// would look like colour swatches with faces behind them. Taken from the picture, a card has the
    /// colour of what it shows, as an artist's box already does (see
    /// <c>ArtistWallModels.Wear</c>).</para>
    /// </summary>
    public GenreAccent Accent => _accent ?? (_mark is null ? default : GenreAccent.Of(_mark));

    private GenreAccent? _accent;

    /// <summary>
    /// Whether the card has been ANSWERED: whether the photograph, or the news that there will be none,
    /// has come back.
    ///
    /// <para>Nothing the card wears is drawn until then. A card that showed its colour and changed it
    /// when the picture arrived would make the wall change colour while it loads, which the artists
    /// wall is built to avoid (see <c>ArtistWallModels.Ground</c>: a box waits on its own plate and does
    /// not guess). Until then a card is the frosted plate it is drawn on, with its name; the colour and
    /// the picture arrive together, in one fade.</para>
    /// </summary>
    public bool Answered => _accent is not null;

    /// <summary>
    /// Takes the card's colour from its own photograph: the picture's dominant tone, snapped to the
    /// palette.
    ///
    /// <para>Every layer is dropped, the name included. The brushes are worked out from the colour and
    /// the ink of the name is baked into a bitmap, so a card that changed colour and kept its lettering
    /// could show a name set for the old colour. That is one extra raster per card, once, when its
    /// picture arrives, which is the cost a resize already pays.</para>
    ///
    /// <para>Call it on the UI thread, like every other write here: the layers are XAML objects and the
    /// notifications go to bound elements. Only the reading of the colour, which is a decode, runs off
    /// that thread (see <c>GenreWallView.ShowAsync</c>).</para>
    /// </summary>
    public void TakeColourFrom(Color picture)
    {
        if (_mark is null)
            return;

        // A picture with no colour that the palette knows gives no answer about colour, so the shelf
        // keeps its own. See GenreAccent.From.
        Wear(GenreAccent.From(_mark, picture) ?? GenreAccent.Of(_mark));
    }

    /// <summary>No photograph is coming for this shelf, or none could be read. The card takes the
    /// genre's own colour from the table and is shown with it. That is a finished card, and it is the
    /// normal card for a genre that has no picture.</summary>
    public void KeepItsOwnColour()
    {
        if (_mark is not null)
            Wear(GenreAccent.Of(_mark));
    }

    private void Wear(GenreAccent accent)
    {
        if (_accent == accent)
            return;

        // The name is redrawn only when the INK actually changes, which among the ten accents almost
        // never happens: every one of them is dark enough at the tone the name stands on to take white
        // ink. Dropping the drawing every time would raster every name on the wall a second time for a
        // colour change nobody could see.
        var reink = _accent is { } was && GenreWordmarks.InkOn(was) != GenreWordmarks.InkOn(accent);

        _accent = accent;
        _room = null;
        _drop = _rim = null;

        OnChanged(nameof(Accent));
        OnChanged(nameof(Answered));
        OnChanged(nameof(Room));
        OnChanged(nameof(Drop));
        OnChanged(nameof(Rim));

        if (!reink)
            return;

        _lettering = null;
        OnChanged(nameof(Lettering));
    }

    /// <summary>
    /// The card's colour under everything, as a room: the soft field of lobes that the playlist cards
    /// stand on, lit in this shelf's own tones. It is the whole card when no picture arrives.
    /// </summary>
    /// <remarks>
    /// <b>It is a room, not a flat gradient.</b> See <see cref="GenreWordmarks.Room"/>, which explains
    /// what a flat gradient lacks. The element that draws it is a <c>CardRoom</c>, the same control the
    /// home screen's list cards use. On this wall it is set to <c>Drift="False"</c>: a card here is one
    /// of a dozen on screen and is scrolled past, while a card on the home screen is one of three on a
    /// screen that is not scrolled. It still costs one shader pass when the card is realized and
    /// nothing after that.
    /// </remarks>
    public RoomPalette Room => _room ??= GenreWordmarks.Room(Accent);

    private RoomPalette? _room;

    /// <summary>
    /// Where in the room's own drift this card is frozen.
    /// </summary>
    /// <remarks>
    /// It comes from the genre's NAME, so Rock is in the same pose every time the wall is drawn, and
    /// two shelves of the same colour are not in the same pose. The home screen's cards give
    /// <c>CardRoom.Apart</c> the same kind of value, for the same two reasons.
    ///
    /// <para>It uses a stable hash and not <c>string.GetHashCode</c>, which is seeded per process:
    /// the same shelf would be in a different pose on every run, so a card would change silently
    /// between launches.</para>
    /// </remarks>
    public int Apart
    {
        get
        {
            if (_apart is { } kept)
                return kept;

            var roll = 2166136261u;
            foreach (var ch in Genre.Name)
                roll = (roll ^ ch) * 16777619u;

            return (_apart = (int)(roll % 4096)).Value;
        }
    }

    private int? _apart;

    /// <summary>
    /// Where on the room's own plane this shelf is looking (see <see cref="CardRoom.Shift"/>).
    /// </summary>
    /// <remarks>
    /// <b><see cref="Apart"/> is not enough on its own.</b> That value moves a card along the drift,
    /// and the drift is slow: two shelves far apart in it still show the same figures in nearly the
    /// same places. Moving the WINDOW over the plane is what makes two shelves genuinely different
    /// pictures.
    ///
    /// <para>It comes from the genre's name, using different hash values from <see cref="Apart"/>, so
    /// the two do not move together. If both came from one number, a shelf far along the drift would
    /// also be far across the plane, and the two would give only one kind of variety.</para>
    ///
    /// <para>It is limited by <see cref="Reach"/> either way, and that figure is small for the reason
    /// given there.</para>
    /// </remarks>
    public Point Shift => _shift ??= Scatter(Genre.Name);

    private Point? _shift;

    /// <summary>
    /// How far from the middle of the room a shelf may look, in card heights.
    /// </summary>
    /// <remarks>
    /// <b>The room is a cluster, not an endless field, and this figure follows from that.</b> Its
    /// lobes are placed at <c>cos(a) * 0.70</c> and similar values: bounded, around the origin, out to
    /// something under one. A window moved much past that sees only the base colour. A large reach
    /// (such as 6) would make every genre head a flat block of colour.
    ///
    /// <para><b>The value was checked by rendering.</b> Rendered at the head's shape and zoom and at
    /// the card's, out to the corners of this reach: at half a card's height every render still has
    /// lobes in it and no two are the same picture, and at 6 they are all the flat base colour.
    /// The test <c>The_room_still_has_figures_at_the_edge_of_the_reach</c> checks this.</para>
    /// </remarks>
    private const double Reach = 0.45;

    /// <summary>
    /// A stable place on the plane for a name.
    /// </summary>
    /// <remarks>
    /// It uses its own two hash values, salted differently from <see cref="Apart"/>'s and from each
    /// other, so x and y are not the same number and a shelf does not sit on the plane's diagonal. It
    /// uses a stable hash and not <c>string.GetHashCode</c>, which is seeded per process: a shelf would
    /// look somewhere else on every run, so a card would change silently between launches.
    /// </remarks>
    internal static Point Scatter(string name)
    {
        return new Point(Axis(name, 0x9E3779B9), Axis(name, 0x85EBCA6B));

        static double Axis(string name, uint salt)
        {
            var roll = salt;
            foreach (var ch in name)
                roll = (roll ^ ch) * 16777619u;

            // Zero to one, then spread to either side of the middle of the room.
            return ((roll % 8192) / 8192.0 * 2 - 1) * Reach;
        }
    }



    /// <summary>The same colour falling DOWN the card, at the weight this shelf's finish needs. It is
    /// the artists wall's own veil: heavy for a frosted shelf and a third of that for a glass one. See
    /// <see cref="GenreWordmarks.Drop"/>.</summary>
    public Brush Drop => _drop ??= _mark is null ? Blank : GenreWordmarks.Drop(Accent);

    private Brush? _drop;


    /// <summary>The edge the card is drawn with, which is most of what makes a pane look like a pane.</summary>
    public Brush Rim => _rim ??= _mark is null ? Blank : GenreWordmarks.Rim(Accent);

    private Brush? _rim;

    private ImageSource? _picture;

    /// <summary>The photograph of one of the genre's acts. It is null while the picture is being
    /// fetched, and permanently null when no catalogue has a picture of any of them. A card without one
    /// is still a card: it keeps its colour and its name.</summary>
    public ImageSource? Picture
    {
        get => _picture;
        set
        {
            if (ReferenceEquals(_picture, value))
                return;
            _picture = value;
            OnChanged();
            OnChanged(nameof(HasPicture));
        }
    }

    public bool HasPicture => _picture is not null;

    /// <summary>True once the card has asked for its picture, so scrolling it back on screen does not
    /// ask again. This includes the case where the answer was "nobody in this genre has been
    /// photographed", which is an answer and not a reason to go back to the network.</summary>
    public bool Asked { get; set; }

    /// <summary>An empty layer, for a card whose genre has no wordmark. It is created on first use like
    /// the rest, for the same reason.</summary>
    private static Brush Blank => _blank ??= new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private static Brush? _blank;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Two genres, side by side, which is what the wall is a list of.
///
/// <para><b>A pair, not a grid, because of the mirroring.</b> Each card is drawn with its name at the
/// OUTER edge of the wall and its photograph towards the middle, so which side a card is on changes
/// the drawing: the wordmark is set flush right on one side and flush left on the other. In a grid, a
/// card would have to work its side out from its own index, and that calculation can disagree with the
/// layout as soon as either changes. With pairs, a card's side is the slot it was built into, so the
/// two cannot differ.</para>
///
/// <para>Pairs also avoid a trap in <c>UniformGridLayout</c>, which divides by a column count that it
/// works out from a width that can be measured as zero in the middle of a resize.</para>
/// </summary>
/// <param name="Left">The card on the left. It is always there: a pair with nothing in it is not made.</param>
/// <param name="Right">The card on the right, or null: the last row of an odd wall, and every row of a
/// pane too narrow to hold two.</param>
/// <remarks>
/// <b>This type must be bindable.</b> It is the type the repeater gives to the template, so every
/// <c>Left.*</c> and <c>Right.*</c> on a card is resolved against it. Without the attribute Uno cannot
/// generate metadata for it and falls back to reflection for all of them. That fallback is reported at
/// startup, and it costs time for every binding evaluation on every card of the wall. The card and the
/// measurement both have the attribute, so the pair must have it too.
/// </remarks>
[RpmBindable]
public sealed partial record GenrePair(GenreTile Left, GenreTile? Right)
{
    /// <summary>Whether the row has a second card at all. It is a property and not a null test in the
    /// template, because a binding through null silently draws nothing, and a collapsed slot looks the
    /// same on screen as a card that failed to bind.</summary>
    public bool HasRight => Right is not null;

    /// <summary>Both cards of the pair, for code that has to act on each of them.</summary>
    /// <remarks>Not named Cards: that name is bound to an ItemsSource elsewhere, and RPM007 checks every
    /// bindable property with a bound name. This one is read from code only.</remarks>
    public IEnumerable<GenreTile> Both
    {
        get
        {
            yield return Left;
            if (Right is not null)
                yield return Right;
        }
    }
}

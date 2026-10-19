using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Art;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Artists.Models;

/// <summary>
/// The one live measurement the artists wall sizes to: a box's width, and the height that makes it
/// sixteen by nine. One instance is shared by every box. A resize sets it once, and every bound box
/// follows. Boxes are not pushed one by one. The album wall's tiles work the same way.
/// </summary>
[RpmBindable]
public sealed partial class ArtistTileMetrics : INotifyPropertyChanged
{
    /// <summary>The shape every box is drawn in, as a height over a width. It is sixteen by nine,
    /// because the photographs arrive in that shape. fanart.tv's backdrops are 1920 by 1080, so a wall
    /// of them crops almost nothing away.
    ///
    /// <para>The value comes from <see cref="ArtistPageLayout.TileShare"/>, the wall's own constant. It
    /// sits beside the band's constant in that class. The number is not written here, because a shape in
    /// a view cannot be tested.</para>
    ///
    /// <para>A box does not draw the same picture as the band it opens. The box always uses a backdrop
    /// (<c>ArtistPortraits.ChooseAsync</c>), which has this shape and loses nothing to it. The band opens
    /// on fanart.tv's square of the act, when there is one (<c>ArtistPortraits.SquareAsync</c>). That
    /// square is trimmed a quarter at the top and a quarter at the bottom, to four by three. The band
    /// then pages through the backdrops.</para></summary>
    public const double Shape = ArtistPageLayout.TileShare;

    private double _width = 260;

    public double Width
    {
        get => _width;
        private set
        {
            if (_width == value)
                return;
            _width = value;
            OnChanged();
            OnChanged(nameof(Height));
            OnChanged(nameof(NameRoom));
        }
    }

    public double Height => Math.Round(_width * Shape);

    /// <summary>How wide a name may run before it wraps. This is the box less a margin on each side. A
    /// long name then wraps onto two centred lines, rather than running off both edges.</summary>
    public double NameRoom => Math.Max(0, _width - 36);

    /// <summary>
    /// The edge, in physical pixels, that a box's photograph is decoded at.
    ///
    /// <para><b>The WIDTH is used. It is the box's long side.</b>
    /// <see cref="RemoteImage.DecodeAsync(byte[], int, string?, string?)"/> fits the picture's SHORT
    /// side to the value it is given. The box is filled with <c>UniformToFill</c>, which crops. So the
    /// picture must cover 16 units across as well as 9 down. If the height were passed, the short side
    /// would be 9 units, and a landscape photograph would fall short across the box. If the width is
    /// passed, the short side is 16 units, and both directions are covered whichever way round the
    /// photograph is. A landscape picture is decoded a little larger than it needs to be. The other
    /// choice gives a wall that is sometimes blurred, for reasons that cannot be seen in the code.</para>
    ///
    /// <para><b>Why this is a method.</b> A decode that is given no size decodes the whole picture. A
    /// fanart.tv backdrop is 1920 by 1080. So every box would hold about 8 MB for a picture drawn three
    /// hundred points across. This has two costs. The large object heap does not shrink back. And each
    /// decode takes longer, which leaves more time for a box that has scrolled away to have its picture
    /// cancelled while it is still decoding. Under a debugger, each of those cancellations can stall the
    /// whole process.</para>
    /// </summary>
    public int DecodeEdge(double rasterScale) => ArtScaler.DecodeEdgeFor(Width, rasterScale);

    public void Set(double width) => Width = width;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// What one artist's box is drawn from. It is always one kind of face: a photograph, a made figure,
/// a shelf mosaic, a record's colour, or nothing.
///
/// <para>The face is made through one of the named factory methods. It is not assembled field by field.
/// That is the reason it is a type. A photograph's colour and a record's colour answer different
/// questions. One washes a picture. The other IS the picture. If a caller could hand over both, every
/// surface would have to decide which one wins. With this type there is nothing to decide.</para>
/// </summary>
public sealed record ArtistFace
{
    private ArtistFace(
        byte[]? picture, byte[]? original, byte[]? banner, byte[]? wash, Color? tint, Color? sleeve)
        => (Picture, Original, Banner, Wash, Tint, Sleeve)
         = (picture, original, banner, wash, tint, sleeve);

    /// <summary>Their photograph, drained to grey, where a catalogue has one. The wall draws this one.
    /// Press shots taken in many lighting setups only look like one wall once the colour is taken out
    /// of them. The app's own colour is then laid back over them (see <see cref="Tint"/>).</summary>
    public byte[]? Picture { get; }

    /// <summary>The same photograph as it was taken, in its own colours. A PAGE draws this one. A page
    /// shows one artist, so it does not need the wall's treatment. It puts a colour cast over the one
    /// picture on the screen.</summary>
    public byte[]? Original { get; }

    /// <summary>
    /// What the PAGE's band draws, when that is a different picture from the box's. It is null when the
    /// band shows the same picture as the box.
    ///
    /// <para>Only a made figure or a shelf mosaic fills this. A photograph is one picture, and both
    /// surfaces show it. A figure is drawn to a shape. A box is about an inch wide and sixteen by nine,
    /// while the band is four hundred points tall. One picture for both would make the band a thumbnail.
    /// Opening a box would also show the same arrangement twice, at two sizes. That looks like the page
    /// failed to load a picture of its own.</para>
    /// </summary>
    public byte[]? Banner { get; }

    /// <summary>The same photograph, blurred so that it does not read as a picture. A surface uses it to
    /// stand on, not to show. The artist's page uses it as its background. It is null where the picture
    /// would not blur. Pale pictures are kept like any others. How pale is too pale depends on what is
    /// put on top, and that is for the surface using it to decide.</summary>
    public byte[]? Wash { get; }

    /// <summary>The colour to wash that photograph in: its own dominant tone, for the surface to snap.</summary>
    public Color? Tint { get; }

    /// <summary>The colour of their first record. It is used for the box when there is no photograph.
    /// It is set only when there is no photograph, because it only means something then.</summary>
    public Color? Sleeve { get; }

    /// <summary>A picture of the artist. The grey copy is what the wall draws. The picture as taken is
    /// what a page draws. The blurred copy sits behind both. The last argument is the colour to wash the
    /// photograph in. None of the four has a default, and every one must be given. A caller that left one
    /// out would build a face with nothing for its surface to draw. The compiler would never ask.</summary>
    public static ArtistFace Photograph(byte[] grey, byte[]? original, byte[]? wash, Color? tint)
        => new(grey, original, banner: null, wash, tint, null);

    /// <summary>
    /// Nobody has photographed the artist. So a picture is made for them, from the colours of one of their
    /// records. See <see cref="SoftFigure"/>.
    ///
    /// <para>The figure is passed in as a picture, not as a colour. That is the point of it. Every surface
    /// already knows how to draw a picture of an artist. So an artist nobody has photographed is not a
    /// separate case that each surface needs a second path for. Two pictures are made, because the box and
    /// the band are different shapes.</para>
    /// </summary>
    /// <param name="tone">The record's own colour, snapped. The surfaces lay it over the top, so a made
    /// picture is dressed exactly as a photograph is.</param>
    public static ArtistFace Figure(byte[] box, byte[] banner, Color tone)
        // The banner is also the page's background. It is already blurred, so it does not read as a
        // picture. That is what a wash is for.
        => new(null, box, banner, banner, tone, null);

    /// <summary>
    /// A SHELF rather than a person: the compilations, where every record filed under "Various Artists"
    /// ends up. Its band is a mosaic of the sleeves on it (see <c>ShelfMosaic</c>). A shelf has no face,
    /// and the records on it are the only true picture of it.
    ///
    /// <para>This is a page face, not a wall face. The missing box is the whole difference. A box on the
    /// wall is about an inch wide, with a name across it. Twelve sleeves at that size are only a grey
    /// texture. So the wall keeps drawing the made figure. Two surfaces showing different pictures is
    /// what <see cref="Figure"/> already does, for the same reason.</para>
    /// </summary>
    /// <param name="banner">The mosaic, for the band at the top of the page.</param>
    /// <param name="wash">The same mosaic, blurred so that it does not read as a picture. The page stands
    /// on it. Null where it would not blur, as with a photograph.</param>
    /// <param name="tone">The colour the page is dressed in: the first sleeve's own dominant colour. The
    /// made figure takes its colour from the same sleeve, so opening the box does not change colour.</param>
    public static ArtistFace Shelf(byte[] banner, byte[]? wash, Color? tone)
        => new(null, null, banner, wash, tone, null);

    /// <summary>No picture of them anywhere, and none could be drawn either, because Skia would not paint
    /// one. The surfaces fall back to a flat fill of this colour.</summary>
    public static ArtistFace Record(Color sleeve) => new(null, null, null, null, null, sleeve);

    /// <summary>Nobody has photographed them, and their records have no covers either. The box keeps the
    /// wall's own dark colour.</summary>
    public static readonly ArtistFace Unknown = new(null, null, null, null, null, null);

    /// <summary>
    /// True when this face stands in for a photograph that could not be fetched. It is not the answer
    /// about the artist. A surface that remembers it has asked (the wall's and the genre page's boxes)
    /// must ask again the next time the box is shown.
    ///
    /// <para>A failed fetch must not be kept as the final answer. Otherwise the artist keeps the made
    /// figure, and looks unphotographed, for the rest of the session.</para>
    /// </summary>
    public bool StandIn { get; private init; }

    /// <summary>The same face, marked as a stand-in for a photograph that could not be fetched.</summary>
    public ArtistFace InsteadOfUnfetchedPhoto() => this with { StandIn = true };
}

/// <summary>
/// One box on the artists wall. It holds who the artist is, the records that put them there, and the
/// photograph the box is drawn from once it has been fetched.
///
/// <para>The picture is drained to grey, and the artist's own colour is laid over it (see
/// <see cref="Wash"/>). That is what makes a wall of thirty press shots, each in a different lighting
/// setup, read as one set of boxes. Both come from the shared portrait cache. So a box shows the same
/// photograph in the same colour as the track pane shows for the same person.</para>
/// </summary>
[RpmBindable]
public sealed partial class ArtistTile : INotifyPropertyChanged
{
    public ArtistTile(ArtistWallItem artist, ArtistTileMetrics metrics)
    {
        Artist = artist;
        Metrics = metrics;
    }

    /// <summary>
    /// Who the box is about, and what they are on.
    ///
    /// <para>This is set again each time, not fixed once. A box outlives the shelf it came from. Boxes
    /// are reused by name across rebuilds, so a search keystroke does not fetch every photograph again.
    /// Without the reset, a box that still held the reading from before an import would open an artist's
    /// page without the record that was just added to them.</para>
    /// </summary>
    public ArtistWallItem Artist { get; set; }

    public ArtistTileMetrics Metrics { get; }

    public string Name => Artist.Name;

    /// <summary>The letter the index rail files this box under.</summary>
    public char Initial => Artist.Initial;

    private ImageSource? _picture;

    /// <summary>The photograph in its own colours. It is null while the picture is still being fetched.
    /// It stays null for good when no catalogue has a picture of the artist. A box without one is still a
    /// box. It keeps its colour and its name, which is what it is for.</summary>
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
            OnChanged(nameof(Ink));
        }
    }

    public bool HasPicture => _picture is not null;

    private Brush _wash = Clear;

    /// <summary>
    /// The artist's colour over their photograph: the app's own accent that is nearest the picture's
    /// dominant tone. The dominant tone is the colour of the biggest thing in the picture, such as a
    /// jacket, a backdrop, or the light it was shot under.
    ///
    /// <para>The colour is snapped to the palette (see <see cref="RpmPalette"/>), not used as it was
    /// read. Each photograph has a different dominant tone. Two thousand of them side by side would be
    /// two thousand colours and no palette. That reads as noise, even when each box on its own looks
    /// fine. Snapped, the wall uses the app's ten colours in many arrangements.</para>
    ///
    /// <para>The colour is laid on as a fall, not as a flat pane over a grey copy of the picture. See
    /// <see cref="ArtistVeil"/> for the reasons. In short, a flat pane made every face on the wall the same
    /// single tone. The faces are the part of the wall worth looking at.</para>
    ///
    /// <para>Nothing is set until a photograph arrives. A box with no photograph never gets a wash. The
    /// layer under it (see <see cref="Ground"/>) is what the box shows in that case. A colour laid over
    /// that layer would be the app's dark spread over the artist's own colour. A box that guessed a colour
    /// and then corrected it would change colour as it scrolled.</para>
    /// </summary>
    public Brush Wash
    {
        get => _wash;
        private set
        {
            _wash = value;
            OnChanged();
            OnChanged(nameof(Ink));
        }
    }

    private Brush _ground = Clear;

    /// <summary>
    /// What the box shows under everything else. Normally this is nothing, which leaves the box's own
    /// frosted plate. That plate is the same veil that a visualizer's card or a review in the groove sits on.
    ///
    /// <para>For an artist nobody has photographed, which in a real collection is a great many of them,
    /// the ground is the colour of their first record. The colour is snapped into the palette and laid
    /// across the box (see <see cref="ArtistGround"/>). This turns a wall of empty boxes into a wall of
    /// artists. The ground is drawn UNDER the photograph layer, not in its place. So there is one code
    /// path, whether or not a picture ever arrives.</para>
    ///
    /// <para>The plate is frosted rather than dark while the answer is still on its way. A box waits for a
    /// portrait fetch. For many artists the fetch never returns a picture, so the wait is permanent. A wall
    /// of black rectangles would look like a wall that failed to load. A wall of the app's own plate looks
    /// like cards that have not been given a picture yet, which is what they are.</para>
    /// </summary>
    public Brush Ground
    {
        get => _ground;
        private set
        {
            _ground = value;
            OnChanged();
            OnChanged(nameof(Ink));
        }
    }

    /// <summary>
    /// What the box wears once the answer about this artist has arrived. This is the only way to set
    /// either layer. So the two layers cannot disagree about which kind of box this is.
    ///
    /// <para>A photograph gets a FALL of the accent. The brush carries its own transparency, so the layer
    /// is at full strength, and the brush decides how much colour appears at each height. The falls are
    /// kept per accent, so a wall of two thousand boxes holds ten brushes, not two thousand. A record
    /// paints the ground instead, and leaves the fall clear. There is no photograph under the fall to
    /// colour.</para>
    /// </summary>
    public void Wear(ArtistFace face)
    {
        if (face.Sleeve is { } sleeve)
        {
            Ground = ArtistGround.From(sleeve, Name) ?? Clear;
            return;
        }

        Wash = face.Tint is { } colour
            ? ArtistVeil.Over(RpmPalette.Nearest(colour)) ?? Clear
            : Clear;
    }

    /// <summary>
    /// The flat dark under the name. It is drawn only where there is something under the name to stand off.
    ///
    /// <para>This is not a fourth layer set alongside the others. It follows from them. A photograph, a
    /// wash or a record's colour turns it on. A box that still shows only its plate does not get it. A
    /// dark ink over the plate would not look like a shadow behind the text. It would look like one grey
    /// box among the frosted ones.</para>
    /// </summary>
    public Brush Ink =>
        HasPicture || !ReferenceEquals(_ground, Clear) || !ReferenceEquals(_wash, Clear)
            ? Shade
            : Clear;

    /// <summary>How dark that ink is. There is one ink for all ten accents. Choosing black or white for
    /// each box would read correctly on each one, but the wall would look like two walls.</summary>
    private static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(0x2E, 0x00, 0x00, 0x00));

    /// <summary>A layer that is not there. Every layer starts this way. An unanswered box is just the
    /// frosted plate it is drawn on.</summary>
    private static readonly Brush Clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>True once the box has asked for its photograph. Scrolling the box back on screen then
    /// does not ask again. This includes artists whose answer was that there is no picture of them. That
    /// is an answer, not a reason to go back to the network.</summary>
    public bool Asked { get; set; }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One letter on the index rail: whether the wall has an artist under it, and whether it is the
/// letter being read right now.</summary>
[RpmBindable]
public sealed partial class IndexLetter : INotifyPropertyChanged
{
    public IndexLetter(char letter) => Letter = letter.ToString();

    public string Letter { get; }

    private bool _has;

    /// <summary>The wall has an artist under this letter. A letter with nobody under it is drawn faint, and
    /// does nothing when pressed. An index that jumps to a gap would be wrong.</summary>
    public bool Has
    {
        get => _has;
        set
        {
            if (_has == value)
                return;
            _has = value;
            OnChanged();
            OnChanged(nameof(Ink));
        }
    }

    private bool _current;

    /// <summary>The letter the wall is standing on. It follows both the scroll and the press, so the rail
    /// shows where you are, whether you got there by dragging the wall or by pressing a letter.</summary>
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            OnChanged();
            OnChanged(nameof(Ink));
            OnChanged(nameof(Weight));
        }
    }

    public Brush Ink => _current ? CurrentInk : _has ? LiveInk : DeadInk;

    public Windows.UI.Text.FontWeight Weight
        => _current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    private static readonly Brush CurrentInk = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly Brush LiveInk = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DeadInk = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One of an artist's own records on their page: its sleeve, its name, and what it is.</summary>
[RpmBindable]
public sealed partial class ArtistRecord : INotifyPropertyChanged, Services.Art.ISleeveCard
{
    public ArtistRecord(
        string folder, string title, string subtitle, bool isExplicit,
        Library.Models.AlbumTileMetrics metrics)
    {
        Folder = folder;
        Title = title;
        Subtitle = subtitle;
        IsExplicit = isExplicit;
        Metrics = metrics;
    }

    /// <summary>How wide the sleeve is drawn. This is the shelf's answer, not this record's. One live
    /// measurement is shared by every sleeve on both shelves and by the drawer under them. So a resize is
    /// written once, and every bound sleeve follows. It is the album wall's measurement, so a record on an
    /// artist's shelf is the same square as that record on the wall.
    ///
    /// <para>It is handed in, not given a default. A sleeve sized from nothing would be drawn at whatever
    /// size the class starts with, which would be a fixed square.</para></summary>
    public Library.Models.AlbumTileMetrics Metrics { get; }

    /// <summary>The library-relative folder. It is the record's identity, and opening the record needs it.</summary>
    public string Folder { get; }

    private bool _coverMissing;

    /// <summary>Set by the loader when it has looked everywhere a cover could be (the folder, the tags, the
    /// computer that holds the record, and the cloud) and found nothing. It is a property, not a field,
    /// because the card draws from it through <see cref="FigureSeed"/>.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            OnChanged(nameof(FigureSeed));
        }
    }

    /// <summary>
    /// What the made sleeve is drawn from. It is null while there is nothing to draw. This is the album
    /// wall's own rule, repeated here because these shelves draw the same square. See
    /// <see cref="Library.Models.AlbumCard.FigureSeed"/>.
    ///
    /// <para>The folder is given ONLY after the read has come back empty. It is never given just because no
    /// picture has arrived yet. The card is shown long before its cover arrives. If the seed meant "no cover
    /// on screen", every record on the page would paint a blurred figure, which is then covered a moment
    /// later. Each of those is a paint and a blur for a picture nobody sees.</para>
    /// </summary>
    public string? FigureSeed => _coverMissing ? Folder : null;

    public string Title { get; }

    /// <summary>Its year and how many songs it holds, where the mirror knows them.</summary>
    public string Subtitle { get; }

    /// <summary>Whether anything on it is marked explicit. The wall and the list use the same mark for the
    /// same record, from the same reading of the collection. A record's explicit mark does not depend on
    /// which view shows it.</summary>
    public bool IsExplicit { get; }

    private ImageSource? _cover;

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            OnChanged();
            OnChanged(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    private bool _isExpanded;

    /// <summary>Whether this record's drawer is the one that is open. The ring around the sleeve shows
    /// this. The album wall uses the same mark on its own cards (<c>AlbumCard.IsExpanded</c>). A press on a
    /// sleeve here opens a tracklist under the shelf, rather than going somewhere else. Without the ring,
    /// nothing on the sleeve itself shows which drawer belongs to it.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnChanged(); }
    }

    private Brush? _ring;

    /// <summary>The ring around the open sleeve, in the drawer's colour. While the sleeve is still being
    /// read, the ring uses the drawer's default colour. After that, it uses the record's own colour, so the
    /// ring and the drawer read as one. This is the same brush, with the same default, that the album wall
    /// uses for its ring. See <see cref="Library.Models.AlbumCard.Ring"/>.</summary>
    /// <remarks>The brush is made on first read, not in a field initializer. A brush takes the thread's
    /// DispatcherQueue when it is built. An initializer would make a record impossible to build anywhere
    /// except on the UI thread. The rest of this record, such as the seed a made sleeve is drawn from, is
    /// string arithmetic and needs no dispatcher.</remarks>
    public Brush Ring
    {
        get => _ring ??= new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x22, 0x2C));
        set { _ring = value; OnChanged(); }
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One of the listener's own playlists, shown on the artist page because this artist is in it.
///
/// <para><b>A card, not a row.</b> It is drawn as the same square that every record on this page is. It
/// sits on the shelf under the singles, among six shelves of sleeves. A list of bare names there would look
/// like a different app's panel placed in the page.</para>
///
/// <para><b>The picture.</b> It is the playlist's own cover, the same one that the head of its tracklist
/// shows, from the same source (see PlaylistCovers). A playlist shown as two different pictures in two views
/// would look like two different playlists. If none of its records has artwork, the made figure is drawn
/// from the playlist's NAME (see SoftFigure), not from any one record. So two playlists that share a song
/// are still two pictures.</para>
/// </summary>
[RpmBindable]
public sealed partial class ArtistPlaylist : INotifyPropertyChanged, Services.Art.ISleeveCard
{
    public ArtistPlaylist(
        long nodeId, string name, string subtitle, Library.Models.AlbumTileMetrics metrics)
    {
        NodeId = nodeId;
        Name = name;
        Subtitle = subtitle;
        Metrics = metrics;
    }

    /// <summary>The playlist's node in the tree, which is what opening it needs.</summary>
    public long NodeId { get; }

    public string Name { get; }

    /// <summary>How many of THIS artist's songs are in it. This is not the length of the playlist. The shelf
    /// answers "how much of this artist is in here". The other number would answer a question nobody on this
    /// page asked.</summary>
    public string Subtitle { get; }

    private bool _coverMissing;

    /// <summary>Set by the loader when none of the records on this list has artwork, so there is no cover to
    /// draw. It is set once the read has answered. The card draws from it. See <see cref="FigureSeed"/>.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            OnChanged(nameof(FigureSeed));
        }
    }

    /// <summary>What the made figure is drawn from, when no record on the list has artwork. The picture belongs
    /// to the playlist, not to whichever record it borrowed.
    ///
    /// <para>It is null until the read has come back empty, for the same reason as
    /// <see cref="ArtistRecord.FigureSeed"/>. A figure drawn before the cover has been looked for is a picture
    /// painted only to be covered up.</para></summary>
    public string? FigureSeed => _coverMissing ? Name : null;

    /// <summary>The shelf's one live measurement, shared with every sleeve on the page. See
    /// <see cref="ArtistRecord.Metrics"/>, which is the same object.</summary>
    public Library.Models.AlbumTileMetrics Metrics { get; }

    private ImageSource? _cover;

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            OnChanged();
            OnChanged(nameof(HasCover));
        }
    }

    public bool HasCover => _cover is not null;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One song by this artist on somebody else's record. This is what a featured credit comes to, once you
/// look for it. The record is shown as the subtitle, not as a heading. An artist can have one song on each
/// of twenty compilations. A page of twenty headings, with one line under each, would not be a list of songs.
/// </summary>
[RpmBindable]
public sealed partial class ArtistSong : INotifyPropertyChanged
{
    public ArtistSong(
        RPM.Core.Models.TrackInfo track, string folder, string subtitle, string duration)
    {
        Track = track;
        Folder = folder;
        Title = string.IsNullOrWhiteSpace(track.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(track.FilePath)
            : track.Title!;
        Subtitle = subtitle;
        Duration = duration;

        // The mirror already knows whether this song is a favourite. The heart is drawn when the line is
        // built, not filled in later by a second read of the library.
        _isFavorite = track.IsFavorite;
    }

    public RPM.Core.Models.TrackInfo Track { get; }

    public string FilePath => Track.FilePath;

    /// <summary>The library folder of the record this song came from. It uses forward slashes, as the mirror
    /// names it. The folder is passed in, not worked out from <see cref="FilePath"/>. The caller already has
    /// it, and working it out here would make this type know where the library root is. A cover is fetched by
    /// this folder, so a list that shows sleeves beside its songs must carry it.</summary>
    public string Folder { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Duration { get; }

    private bool _isPlaying;

    /// <summary>True when this is the song the deck is playing. It shows the three-bar equaliser in front of
    /// the name. The same mark appears on a line inside an opened record, because it is the same song. The
    /// listener should not have to work out that the two lists are about one collection.</summary>
    public bool IsPlaying { get => _isPlaying; set { if (_isPlaying == value) return; _isPlaying = value; OnChanged(); } }

    private bool _isFavorite;

    /// <summary>True when the listener has made this song a favourite. It shows the filled heart at the end of
    /// the line. It is set from the mirror when the line is built. It is set again by the click that toggles
    /// it, so the heart changes at once, without a second read of the artist.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnChanged();
            OnChanged(nameof(IsNotFavorite));
            OnChanged(nameof(ShowHeart));
        }
    }

    /// <summary>The outline heart's own condition. A binding cannot negate a value, so this is a separate property.</summary>
    public bool IsNotFavorite => !_isFavorite;

    private bool _isHovered;

    /// <summary>True while the pointer is over this line. This is what makes the empty heart appear on a song
    /// that nobody has marked. Without it, there would be a column of grey hearts down four hundred songs,
    /// which nobody asked for. With no heart at all, the mark could only be reached through the menu.</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered == value) return; _isHovered = value; OnChanged(); OnChanged(nameof(ShowHeart)); }
    }

    /// <summary>
    /// Whether the heart is drawn at all. It is always drawn on a favourite, and on hover for the rest.
    ///
    /// <para>This includes songs from somebody else's record. Their favourite mark is kept in the mirror for
    /// that computer, and the mark belongs to the listener. The song's right-click menu offers the same
    /// entry, so the two must agree.</para>
    /// </summary>
    public bool ShowHeart => _isFavorite || _isHovered;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

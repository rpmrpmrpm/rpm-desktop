using RPM.Core.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;

using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Radio;
using RPM.Core.Viz;

using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Library.Models;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Strings;

using RemoteLine = RPM.Desktop.Presentation.Pegasus.RemoteLine;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// One row of the home view: its heading and the records in it.
///
/// <para>The rows differ only in heading and contents, so they share one type instead of four
/// hand-written rows in the markup. A row with no records is not drawn: a heading over an empty
/// strip says nothing, and on a new library most rows are empty.</para>
/// </summary>
[RpmBindable]
public sealed partial class HomeRowModel : INotifyPropertyChanged
{
    public HomeRow Kind { get; init; }

    public string Heading { get; init; } = "";

    /// <summary>The row's records.</summary>
    /// <remarks>Its own collection type so a {Binding} can hand it to the row's repeater under Native
    /// AOT; see <see cref="Common.SimilarRowList"/>.</remarks>
    public Common.SimilarRowList Cards { get; } = [];

    /// <summary>The size of the cards in this row, shared by every card in it. It follows the size of
    /// the album wall's tiles, which the listener sets with a slider, so a cover here and a cover on
    /// the wall look like the same size. Whole cards must fill the row, so the final size is rounded
    /// around this value.</summary>
    public SimilarTileMetrics Metrics { get; init; } = new();

    public Visibility Shown => Cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Settled() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shown)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One of the lists the library writes for itself, as the home screen offers it: the mark it has in
/// the playlist tree, its name, and how many songs are in it right now.
///
/// <para>Nothing about it is stored or cached. A generated list is its query, run when somebody asks
/// (see <see cref="SmartPlaylists"/>). This card holds the answer from when the screen was opened,
/// which is the count under the name.</para>
/// </summary>
[RpmBindable]
public sealed partial class HomeListCard : INotifyPropertyChanged, Services.Art.ISleeveCard
{
    /// <summary>Which list this card opens, and in which of the two ways. It is one value, not a kind
    /// beside a node id, so a card cannot be both or neither.</summary>
    public HomeListDoor Door { get; init; }

    public string Name { get; init; } = "";

    public string Glyph { get; init; } = "";

    /// <summary>How many songs, in the same words the playlist pane uses.</summary>
    public string Count { get; init; } = "";

    /// <summary>
    /// The line under the name: what the list is, where it can say so, and how long it is where it
    /// cannot.
    /// </summary>
    /// <remarks>
    /// A mood has a sentence that describes the part of the collection it was drawn from. The playlist
    /// page prints the same sentence under the same list's title; it is worded in one place so the two
    /// cannot disagree (<see cref="MoodBlurbText"/>). Every other kind says what it is in its own name,
    /// so the count is shown instead.
    ///
    /// <para>This is one property, not two with a choice at each surface, because the banner prints
    /// this line too: the card decides once what it has to say.</para>
    /// </remarks>
    public string Caption { get; init; } = "";

    /// <summary>This list holds at least one explicit song: the E after its name, the same mark the
    /// playlists tree puts on the same list. The two kinds of list get the answer differently. A kept
    /// playlist uses <c>LibraryDatabase.QueryExplicitPlaylists</c>. A generated one uses the same pass
    /// that counts it (<c>SmartPlaylistTotals</c>), as the tree does.</summary>
    public bool IsExplicit { get; init; }

    /// <summary>How a KEPT list's picture was chosen, taken from its node in the tree. It is carried on
    /// the card because the node is already in hand where the card is built. Looking it up later
    /// would be a second walk of the tree on the UI thread. It is null on a generated list, which has
    /// no node and no records to draw from.</summary>
    public PlaylistCover? ChosenCover { get; init; }

    /// <summary>The mark's colour. The favourites' red heart and the most-played fire keep their own
    /// colours, the same two the tree colours. The other marks are plain. It is read from the same
    /// switch as the tree, so one list is not two colours on two screens.</summary>
    public Windows.UI.Color InkColor { get; init; } = Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

    private Microsoft.UI.Xaml.Media.Brush? _ink;

    /// <summary>The colour as the Foreground the mark binds to. It is made on first read, not beside
    /// <see cref="InkColor"/>: a brush is a DependencyObject, and making one off the UI thread throws
    /// RPC_E_WRONG_THREAD on WinUI, while these cards are built on the worker that reads the shelf. A
    /// binding is evaluated on the UI thread, so the first read is on the right thread and is the only
    /// reader.</summary>
    public Microsoft.UI.Xaml.Media.Brush Ink =>
        _ink ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(InkColor);

    /// <summary>The square this card is drawn at. It is shared with every other card in the row and
    /// set from the row's width. The rows of records below use the same measurement, so a list and a
    /// record have one size on this screen. It is a plain object, not a DependencyObject, so the
    /// cards can be built on the worker that reads the shelf.</summary>
    public SimilarTileMetrics Metrics { get; init; } = new();

    private ImageSource? _cover;

    /// <summary>The list's own picture, once it has arrived: for a generated list, what
    /// <see cref="GeneratedCover"/> chose; for a kept one, what <see cref="PlaylistCovers"/> draws
    /// from its records. <see cref="Services.Art.CoverFill"/> assigns it on the UI thread, which keeps
    /// an ImageSource off the worker that built the card.</summary>
    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (ReferenceEquals(_cover, value))
                return;
            _cover = value;
            Say(nameof(Cover));
            Say(nameof(HasCover));
            Say(nameof(MarkShown));
            Say(nameof(BadgeShown));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>The fill's verdict: the picture was looked for and not found. <see cref="MarkShown"/>
    /// reads it. The mark stands in for a picture that is never coming, and it must not stand in for
    /// one that has not arrived yet.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            Say(nameof(MarkShown));
        }
    }

    private bool _coverMissing;

    /// <summary>The mark drawn in the middle of the square. It is shown until a picture arrives, and
    /// stays where none does. Once a picture is up, the mark moves to the corner badge, so a list with
    /// a photograph is not mistaken for a record.
    ///
    /// <para>It is never shown on a framed card. The frame carries the list's name and the app's own
    /// name, so a glyph over it would repeat what the square already says, and it would sit in the
    /// middle of the window that the room moves behind.</para>
    ///
    /// <para>It also depends on <see cref="CoverMissing"/>, for the reason given on
    /// <c>HomeQuickCard.MarkShown</c>: a card that has not been told there is no picture shows its
    /// own colour, not a mark that stands in for a picture still on its way.</para></summary>
    public Visibility MarkShown =>
        CoverMissing && _cover is null && !HasRoom ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether this list's cover carries the app's own drawing: the border, the name down
    /// the band and the app's name in the corner. Every generated list has it where the asset exists.
    /// A kept list never has it. It is read at the card, so a missing drawing leaves an ordinary
    /// card.</summary>
    public bool Framed { get; init; }

    /// <summary>Whether the small mark is drawn in the corner of the picture. It says that a square
    /// with a photograph is a list, not a record.
    ///
    /// <para>It is not shown on a framed cover. The frame already names the list down its band and
    /// carries the app's name in that same corner, so the badge would sit on the logo and repeat
    /// what the square already says.</para></summary>
    public Visibility BadgeShown =>
        _cover is not null && !Framed ? Visibility.Visible : Visibility.Collapsed;

    // Framed cards. At present there is one framed list: the room moving behind a window cut in a
    // drawing. Other surfaces compose this into a picture (CoverOverlay); this card draws it live.
    // The room must stop drawing when the screen is not visible (see RoomShown).

    /// <summary>Whether this card draws its own room instead of showing a picture of one. This one
    /// fact has three effects: the mark is hidden, no ordinary cover is fetched, and the room and the
    /// frame are drawn. It is decided from <c>CoverOverlay.Has</c> where the card is built, so a
    /// frame whose file is missing gives an ordinary card, not an empty square.</summary>
    public bool HasRoom => Room is not null;

    /// <summary>Whether the room is drawn at all. The frame over it has its own visibility, because it
    /// is composed on a worker and arrives later.</summary>
    public Visibility RoomDrawn => HasRoom ? Visibility.Visible : Visibility.Collapsed;

    private ImageSource? _overlay;

    /// <summary>
    /// The frame, once it has been composed and decoded: the drawing with the list's name set in its
    /// band and the window still a hole. <see cref="HomeView.FillFrameAsync"/> assigns it on the UI
    /// thread, which keeps an ImageSource off the worker that built the card.
    /// </summary>
    /// <remarks>
    /// It is composed, not read straight from <c>Assets</c>, because the name in the band is in the
    /// current locale. See <c>CoverOverlay.Letter</c>.
    /// </remarks>
    public ImageSource? Overlay
    {
        get => _overlay;
        set
        {
            if (ReferenceEquals(_overlay, value))
                return;
            _overlay = value;
            Say(nameof(Overlay));
            Say(nameof(FrameShown));
        }
    }

    /// <summary>Whether the frame is drawn. It is shown only once it is in hand. The room under it
    /// uses the list's own colours and is a finished square on its own, while half a frame is a
    /// window with no edges.</summary>
    public Visibility FrameShown =>
        HasRoom && _overlay is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The colours the room behind the frame is painted in, and where in its movement it
    /// starts. They are the list's own, the same answer <c>CoverOverlay</c> uses for the still cover,
    /// so the card and the cover it stands for are one picture. It is null on a card with no frame,
    /// which removes the room instead of painting one behind a square that has no frame.</summary>
    /// <remarks>Internal, unlike everything else on this card, because <see cref="CoverRoom"/> is
    /// internal and a public property of an internal type does not compile. Nothing binds to it; the
    /// two properties below are what the element takes.</remarks>
    internal CoverRoom.ListRoom? Room { get; init; }

    /// <summary>The colours alone, which is what the element takes. There are two bindings, not one,
    /// because the element is a <c>DependencyObject</c>, and a nullable struct passed through one
    /// property would have to be unpacked there instead of here.</summary>
    public RoomPalette? RoomColours => Room?.Colours;

    /// <summary>Where in the movement this list starts, beyond what its colours say. Two lists can
    /// have one colourway; see <see cref="CoverRoom.ListRoom"/>.</summary>
    public int RoomApart => Room?.Apart ?? 0;

    private bool _roomShown;

    /// <summary>
    /// Whether the screen this card is on is being looked at. False stops the room's animation clock.
    /// </summary>
    /// <remarks>
    /// <see cref="HomeView.ShowRooms"/> pushes it to every list card. The element does not read it
    /// from the tree, because an element is told nothing when an ANCESTOR is collapsed, and home is
    /// hidden by collapsing the view. The banner's cards answer the same question in the same way and
    /// for the same reason; see <c>HomeFeatureCard.RoomShown</c>. Without it, a hidden screen keeps
    /// drawing thirty frames a second for the life of the window.
    /// </remarks>
    public bool RoomShown
    {
        get => _roomShown;
        set
        {
            if (_roomShown == value)
                return;
            _roomShown = value;
            Say(nameof(RoomShown));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Say(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// What one card in the home screen's top row opens: a list the library writes itself, or one
/// somebody made and kept.
///
/// <para>It is a type, not a kind and a node id side by side, for the same reason as MainPage's
/// AlbumFrom: two fields that must never both be set are a state nobody checks. Both doors lead to
/// the same tracklist in the playlists pane. Which one a card has depends on whose shelf is on
/// screen.</para>
/// </summary>
public readonly record struct HomeListDoor(SmartPlaylistId? Generated, long? Kept)
{
    /// <summary>A list the library writes from the track rows: the favourites, the most played, and
    /// the moods cut to one kind of music. It holds the ID, not the kind, because a kind alone does
    /// not name a list once lists can be cut by genre, and the banner shows exactly those cuts.</summary>
    public static HomeListDoor Made(SmartPlaylistId id) => new(id, null);

    /// <summary>A playlist that was made and kept, by its node in the tree. On a borrowed shelf these
    /// are the lender's own playlists, mirrored with the rest of their collection.</summary>
    public static HomeListDoor Stored(long nodeId) => new(null, nodeId);
}


/// <summary>
/// One entry in the quick selection under the banner: a song, a record, a playlist or a station.
/// </summary>
/// <remarks>
/// <b>One type for four kinds, because on screen they are one thing:</b> a small square, a name and
/// a line under it, in a list of four. Four types would need four templates with four copies of the
/// same layout. The only differences are where the square's picture comes from and what pressing it
/// does, and both depend on <see cref="Kind"/>.
///
/// <para>It implements <see cref="Services.Art.ISleeveCard"/> like the rows' cards, so every picture
/// on this screen goes through the same door and the same paced decode gate.</para>
/// </remarks>
[RpmBindable]
public sealed partial class HomeQuickCard : INotifyPropertyChanged, Services.Art.ISleeveCard
{
    public QuickPickKind Kind { get; init; }

    /// <summary>What names this entry: a track's path, a record's folder, a list's key, a station's
    /// uuid. It is not shown. It is what the press acts on.</summary>
    public string Key { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>The line under the name: the act on a song, the kind and the act on a record, the kind
    /// and the caption on a list, what the station is. It is empty where there is nothing to say, and
    /// the line is then not drawn.</summary>
    public string Subtitle { get; init; } = "";

    public Visibility SubtitleShown =>
        Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The record this entry opens or plays from: a folder for a song and for a record,
    /// empty for the other two. The square's picture is read from it.</summary>
    public string Folder { get; init; } = "";

    /// <summary>The list this entry opens, on the playlist entries. Null on the others.</summary>
    public HomeListDoor? Door { get; init; }

    /// <summary>The station this entry tunes, on the station entries. Null on the others.</summary>
    public RadioStation? Station { get; init; }

    /// <summary>The song this entry plays, on the song entries. Null on the others. "Play all tracks"
    /// collects these, so the whole TrackInfo is carried and not just a path: the queue is filled with
    /// these, and nothing here should have to read the mirror again.</summary>
    public RPM.Core.Models.TrackInfo? Track { get; init; }

    /// <summary>The mark drawn in the square until a picture arrives, and permanently where none
    /// does. There is one per kind, so an entry shows what it IS before it shows which one it is.</summary>
    public string Glyph { get; init; } = "";

    public Windows.UI.Color InkColor { get; init; } = Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

    private Brush? _ink;

    /// <summary>Made on first read, for the same reason as the other cards' brushes: these cards are
    /// built on the worker that reads the shelf, and a brush can only be made on the UI thread.</summary>
    public Brush Ink => _ink ??= new SolidColorBrush(InkColor);

    /// <summary>The square the selection is drawn at. Every entry in it shares one, as a row of
    /// records does. It is a plain object, not a DependencyObject, so the cards can be built on the
    /// worker.</summary>
    public SimilarTileMetrics Metrics { get; init; } = new();

    private ImageSource? _cover;
    private bool _coverMissing;

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            Say(nameof(Cover));
            Say(nameof(HasCover));
            Say(nameof(MarkShown));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>
    /// What a station's mark stands on: the generated figure that the stations pane draws behind the
    /// same mark, or null for everything else.
    ///
    /// <para>It is null for a logo that is already a solid picture, because that has its own
    /// background. It is also null for the three kinds that are not a station: they have a cover and
    /// need nothing under it.</para>
    /// </summary>
    public ImageSource? CoverGround { get; private set; }

    /// <summary>How the square's picture is fitted. A cover fills it. So does a station's logo that
    /// is a square picture with its own background. A mark with nothing behind it, or a wide logo that
    /// would be cropped if it filled the square, is fitted whole. See RadioStationItem.LogoStretch,
    /// which is the rule this reads.</summary>
    public Stretch CoverStretch { get; private set; } = Stretch.UniformToFill;

    /// <summary>The space around a picture inside the square. It is none for a picture that fills the
    /// square.</summary>
    public Thickness CoverPad { get; private set; }

    /// <summary>
    /// Shows a station's mark the way the stations pane shows it: the picture, the generated ground
    /// under it, and the fitting rules that go with them.
    ///
    /// <para>It is one call, not four setters, because the four values are one answer. Set
    /// separately, a card could show a new station's mark on the last station's ground for a
    /// frame.</para>
    /// </summary>
    public void WearMark(RadioStationItem.FoundLogo found)
    {
        CoverGround = found.Ground;
        CoverStretch = found.Fills ? Stretch.UniformToFill : Stretch.Uniform;

        // Held off the edges where it does not fill, about a twelfth of the square. The stations pane
        // and the now playing square are both inset by the same proportion.
        CoverPad = new Thickness(found.Fills ? 0 : MarkInset);

        Say(nameof(CoverGround));
        Say(nameof(CoverStretch));
        Say(nameof(CoverPad));

        // Last, so the ground is already bound when the mark fades in over it.
        Cover = found.Picture;
    }

    /// <summary>How far a mark is held off the square's edges, at the square's own size.</summary>
    private const double MarkInset = 4;

    /// <summary>
    /// The kind's mark, shown where the picture has been looked for and not found.
    /// </summary>
    /// <remarks>
    /// <b>It depends on <see cref="CoverMissing"/>, not on "there is no picture yet".</b> A test of
    /// <c>_cover is null</c> is also true for every card at the start of its life. A screen that is
    /// being re-read would fill with radio marks and disc glyphs while the pictures are still on their
    /// way, and they would disappear one by one as each picture arrived.
    ///
    /// <para>This is the same rule as for the generated cover and for the album wall: until a card
    /// has been TOLD there is no picture, nothing stands in for one. A card that has not been told
    /// shows its own colour, which is a finished square.</para>
    /// </remarks>
    public Visibility MarkShown =>
        _coverMissing && _cover is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the picture has been looked for and not found. The generated cover is drawn
    /// from this and not from HasCover, for the reason CoverFill exists: until a card has been TOLD
    /// there is no picture, a generated one would be drawn over a picture still on its way.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            Say(nameof(FigureSeed));
            Say(nameof(MarkShown));
        }
    }

    /// <summary>What the generated cover is drawn from where a record has no artwork anywhere: its
    /// folder, so the same record gets the same generated picture here and in every row on this
    /// screen. It is null on the kinds that are not a record, which show their mark instead.</summary>
    public string? FigureSeed =>
        _coverMissing && Folder is { Length: > 0 } folder ? folder : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Say(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One card in the banner at the top of the home screen: a record or a generated list, as a wide
/// plate.
///
/// <para>It is its own type, not the row's card, because a banner shows more than a row's square: a
/// kicker line saying which question put it there, a large title, the artist under it, and a blurred
/// copy of the cover behind all three. A record and a list share this type because they are one plate
/// on screen and step through one strip. The only difference is whether the square on the left is a
/// cover or a mark.</para>
/// </summary>
[RpmBindable]
public sealed partial class HomeFeatureCard : INotifyPropertyChanged, Services.Art.ISleeveCard
{
    /// <summary>Which of the screen's questions this card answers. It is kept for the tests and for
    /// the kicker, which is the row heading for the same question.</summary>
    public HomeFeatureKind Kind { get; init; }

    /// <summary>The line over the title, saying why this card is on screen.</summary>
    public string Kicker { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>The artist on a record, the number of songs on a list.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>The artist's name, used to ask the catalogues. It is kept apart from
    /// <see cref="Subtitle"/>, which is display text and is the song count on a list. A lookup made
    /// from whatever is printed under the title would ask about "24 songs".</summary>
    public string Artist { get; init; } = "";

    /// <summary>The record this card opens, or empty where it opens a list.</summary>
    public string Folder { get; init; } = "";

    /// <summary>The record's year, or zero where it is not known. The line that puts the record
    /// forward uses it (see <see cref="RPM.Core.Metadata.AlbumHype"/>). Zero is not a date and is
    /// never printed.</summary>
    public int Year { get; init; }

    private string? _hype;

    /// <summary>
    /// One short line that puts this record forward, taken from what the encyclopedia says about it.
    /// </summary>
    /// <remarks>
    /// Null is the usual answer: most records have no article, and an article that says nothing
    /// remarkable gives no line. Every word of the line is something the article said. See
    /// <see cref="RPM.Core.Metadata.AlbumHype"/>, where the reason this is not a template is written
    /// out.
    ///
    /// <para>It takes the slot of the artist's lettering, so the three are one choice: the line if
    /// there is one, else the lettering, else the artist's name.</para>
    /// </remarks>
    public string? Hype
    {
        get => _hype;
        set
        {
            _hype = value;
            Say(nameof(Hype));
            Say(nameof(HypeShown));
            Say(nameof(LogoShown));
            Say(nameof(SubtitleShown));
        }
    }

    /// <summary>The list this card opens, or null where it opens a record. Exactly one of this,
    /// <see cref="Folder"/> and <see cref="Station"/> is ever set. <see cref="HomeListDoor"/> follows
    /// the same rule.</summary>
    public HomeListDoor? Door { get; init; }

    /// <summary>
    /// The radio station this card opens, or null where it opens a record or a list.
    /// </summary>
    /// <remarks>
    /// It is the whole station, not its uuid, because pressing the card TUNES it and the directory is
    /// not asked again to do that. The log stores what was playing, so a station that the directory
    /// has since removed still plays from here. A preset is stored the same way.
    ///
    /// <para>This card does not draw what the other two draw. There is no cover, no artist and no text
    /// at the foot: a station is a mark, so the mark is the card and it stands in the middle. Behind
    /// it is the room (see <see cref="Room"/>), which a station has for the same reason a generated
    /// list does.</para>
    /// </remarks>
    public RadioStation? Station { get; init; }

    private string? _blurb;

    /// <summary>
    /// What the station IS, in one line: what its own front page says about itself, or what Wikipedia
    /// says about a station of that name.
    /// </summary>
    /// <remarks>
    /// Null is the usual answer: many front pages declare no description and have no article. The
    /// card is complete without it, and the line is not drawn. See
    /// <see cref="RadioStationItem.BlurbAsync"/>, and <see cref="StationBlurb"/> for why those two
    /// sources are used in that order.
    ///
    /// <para>It is its own line, above <see cref="Playing"/>, and does not share a slot with it. The
    /// card's third line is what the station is playing, so both can be shown at once, and they say
    /// different things: what the station IS, and what is on it right now.</para>
    /// </remarks>
    public string? Blurb
    {
        get => _blurb;
        set
        {
            _blurb = value;
            Say(nameof(Blurb));
            Say(nameof(BlurbShown));
        }
    }

    /// <inheritdoc cref="GroundShown"/>
    public Visibility BlurbShown =>
        _blurb is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    private string? _playing;

    /// <summary>
    /// What the station is announcing while it is the one TUNED, as the station sends it. Null the
    /// rest of the time, when <see cref="OnAir"/> answers the same question instead.
    /// </summary>
    /// <remarks>
    /// <c>HomeView.ShowWhatIsOn</c> writes it from one subscription for the view. The card never asks
    /// for it. The announcement changes every few minutes for as long as the station is on, and a card
    /// with its own subscription would have to be unsubscribed when the strip is rebuilt. The view
    /// outlives every strip and walks the cards it holds.
    /// </remarks>
    public string? Playing
    {
        get => _playing;
        set
        {
            if (_playing == value)
                return;

            _playing = value;
            Say(nameof(Playing));
            Say(nameof(NowPlaying));
            Say(nameof(NowPlayingShown));
        }
    }

    private string? _onAir;

    /// <summary>
    /// The same question for a station that is NOT tuned: what it is playing, read from the stream.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the line worth having.</b> The line exists to say whether the station is
    /// worth pressing, so it must be visible before the card is pressed, to invite the listener to
    /// tune in.
    ///
    /// <para>It costs more than <see cref="Playing"/>: the only way to know what is on a station is to
    /// connect to it. See <see cref="IcyPeek"/> for what one peek costs and
    /// <see cref="RadioStationItem.OnAirAsync"/> for the clock that limits how often it is asked.</para>
    ///
    /// <para><see cref="Playing"/> wins where both are set, which happens when a station is tuned
    /// while its own card is on screen: the live value cannot be stale and the peeked one can.</para>
    /// </remarks>
    public string? OnAir
    {
        get => _onAir;
        set
        {
            if (_onAir == value)
                return;

            _onAir = value;
            Say(nameof(OnAir));
            Say(nameof(NowPlaying));
            Say(nameof(NowPlayingShown));
        }
    }

    /// <summary>The card's third line, worded: "Now playing: Artist, Track". It is one property with
    /// one order over the two candidates, the same shape <see cref="HypeShown"/> keeps for a record's
    /// three lines. As two bindings they could both be shown at once, which gives a card with two
    /// lines where the markup has room for one.</summary>
    public string NowPlaying =>
        (_playing ?? _onAir) is { Length: > 0 } announced
            ? Loc.Format("Radio_NowPlayingLine", announced)
            : "";

    /// <inheritdoc cref="GroundShown"/>
    public Visibility NowPlayingShown =>
        NowPlaying.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The mark a list shows, in the tree's own colour. Nothing on a record's card.</summary>
    public string Glyph { get; init; } = "";

    public Windows.UI.Color InkColor { get; init; } = Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

    private Brush? _ink;

    /// <summary>The colour as the Foreground the mark binds to. It is made on first read, for the
    /// reason <see cref="HomeListCard.Ink"/> is: the banner's cards are built on the worker too, and a
    /// brush can only be made on the UI thread.</summary>
    public Brush Ink => _ink ??= new SolidColorBrush(InkColor);

    private Brush? _wash;

    /// <summary>
    /// What a generated list's plate stands on: its own colour, across the whole plate.
    /// </summary>
    /// <remarks>
    /// A record's plate stands on its blurred cover and then on a photograph of the artist. A list has
    /// neither, so without this it would stand on nothing.
    ///
    /// <para>Both ends of it are OPAQUE. The plate itself is see-through, so a ground that fades out
    /// (for example the list's colour at 40% falling to 10%) shows the card BEHIND it at the far
    /// edge, neighbour's text included. A ground has to cover. The colour is mixed into a dark base
    /// here, not laid over whatever is behind, so the far end is still a colour and not a hole.</para>
    ///
    /// <para>It is made on first read for the reason <see cref="Ink"/> is: a brush is a
    /// DependencyObject and these cards are built on the worker that reads the shelf.</para>
    /// </remarks>
    public Brush Wash => _wash ??= new Microsoft.UI.Xaml.Media.LinearGradientBrush
    {
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(1, 1),
        GradientStops =
        {
            new Microsoft.UI.Xaml.Media.GradientStop { Offset = 0, Color = Tinted(0.42) },
            new Microsoft.UI.Xaml.Media.GradientStop { Offset = 1, Color = Tinted(0.16) },
        },
    };

    /// <summary>The list's colour mixed into the plate's dark colour, with <paramref name="share"/> of
    /// itself. It is opaque: see <see cref="Wash"/>.</summary>
    private Windows.UI.Color Tinted(double share)
    {
        static byte Mix(byte ink, byte ground, double share) =>
            (byte)Math.Clamp(Math.Round((ink * share) + (ground * (1 - share))), 0, 255);

        return Windows.UI.Color.FromArgb(
            0xFF,
            Mix(InkColor.R, 0x19, share),
            Mix(InkColor.G, 0x1B, share),
            Mix(InkColor.B, 0x20, share));
    }

    private RoomPalette? _room;

    /// <summary>
    /// The colours this card's room is painted in, or null where it has no room.
    /// </summary>
    /// <remarks>
    /// This is for lists and stations, for the reason <see cref="Wash"/> exists: a record's plate
    /// stands on its blurred cover and then on a photograph of the artist, and neither of these has
    /// either. The room is the wash with the flat colour taken out.
    ///
    /// <para><b>The list's own colours, once its cover has been composed.</b> They are read from the
    /// wash that comes back with the cover: the picture behind the app's drawing, blurred. So the room
    /// is painted in the colours of what the square shows, not of the frame around it. For the five
    /// lists whose cover IS a room (<see cref="CoverOverlay.Roomed"/>) that wash is the still room, so
    /// the plate moves what the square holds. Until the wash arrives, and permanently where nothing is
    /// composed, the mark's own colour in the tree stands in. One colour is all there is then, so
    /// <see cref="RoomPalette.From"/> is given it as the tint, with nothing for the accent, and it
    /// turns a hue for the second family.</para>
    ///
    /// <para>So this answers twice for most lists, and the room is recoloured while it is being
    /// watched. <see cref="CardRoom"/> keeps the movement going across the change.</para>
    ///
    /// <para>A list has a room even though it also has a cover, because they have different jobs: the
    /// room is what the plate stands on, and the cover is what the square holds. The room is drawn
    /// OVER the ground, so a list cannot show both, and the ground is not used; see
    /// <see cref="HomeView.LoadListFaceAsync"/>.</para>
    /// </remarks>
    public RoomPalette? Room =>
        Door is null && Station is null ? null : _room ?? CardRoomArt.Palette(InkColor);

    /// <summary>The colours read from the list's own cover, or from the station's own mark. It is
    /// called once, from the worker that fetched it, and only where a picture came back.</summary>
    public void ShowRoom(RoomPalette painted)
    {
        _room = painted;
        Say(nameof(Room));
    }

    private bool _roomLive;

    /// <summary>Whether this is the card in front, which decides whether its room moves on the WinUI
    /// head. <c>HomeView.ShowFeature</c> writes it. That is the one place that decides where a card
    /// is, in the same turn as the three hosts are handed their cards.</summary>
    public bool RoomLive
    {
        get => _roomLive;
        set
        {
            if (_roomLive == value)
                return;

            _roomLive = value;
            Say(nameof(RoomLive));
        }
    }

    private bool _roomShown;

    /// <summary>Whether the screen this card is on is being looked at. This decides whether its room
    /// moves at all, and whether its spinners turn (<see cref="PlateWaiting"/>,
    /// <see cref="MarkWaiting"/>). <c>HomeView.ShowRooms</c> writes it for EVERY card in the strip,
    /// not only for the three that are drawn. See <see cref="CardRoom.Shown"/> for why it is not
    /// <see cref="RoomLive"/>.</summary>
    public bool RoomShown
    {
        get => _roomShown;
        set
        {
            if (_roomShown == value)
                return;

            _roomShown = value;
            Say(nameof(RoomShown));
            Say(nameof(PlateWaiting));
            Say(nameof(MarkWaiting));
        }
    }

    /// <summary>Which of the two squares the plate draws. It is two properties, not one property and
    /// a converter, because the answer is decided when the card is built and never changes.</summary>
    public Visibility SleeveShown =>
        Door is null && Station is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility MarkShown => Door is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Whether the plate stands on the card's own colour. This is true for every card that
    /// has no picture of a record behind it: a generated list and a station. It is its own property,
    /// not <see cref="MarkShown"/>, because that one also puts the list's MARK in the square at the
    /// foot, and a station has no square to put anything in.</summary>
    public Visibility WashShown =>
        Door is null && Station is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Whether the plate draws the cover, the title and the artist along its foot. It is off
    /// on a station's card, which is a mark in the middle of the plate and nothing else.</summary>
    public Visibility FootShown =>
        Station is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether it draws the station's face instead. Never both.</summary>
    public Visibility RadioShown =>
        Station is null ? Visibility.Collapsed : Visibility.Visible;

    private ImageSource? _stationLogo;

    /// <summary>The station's own mark, drawn large in the middle of the plate. It is null until the
    /// search through everything the station publishes has found one, and permanently where it finds
    /// none (see <c>HomeView.LoadStationFaceAsync</c>). The record spinner stands in while the lookup
    /// runs (<see cref="MarkWaiting"/>), and the radio mark stands in where none is found, as in the
    /// stations pane.</summary>
    public ImageSource? StationLogo
    {
        get => _stationLogo;
        set
        {
            _stationLogo = value;
            Say(nameof(StationLogo));
            Say(nameof(StationLogoShown));
            Say(nameof(StationMarkShown));
            Say(nameof(MarkWaiting));
        }
    }

    private bool _stationMarkFetching;

    /// <summary>The lookup for this station's mark is running. It is set at the start of
    /// <c>HomeView.LoadStationFaceAsync</c> and cleared in its <c>finally</c>. A lookup that failed is
    /// asked again the next time the card comes into a host, which sets this again.</summary>
    public bool StationMarkFetching
    {
        get => _stationMarkFetching;
        set
        {
            if (_stationMarkFetching == value)
                return;
            _stationMarkFetching = value;
            Say(nameof(MarkWaiting));
        }
    }

    /// <summary>Whether a station's card shows the record spinner where its mark goes: the mark has not
    /// arrived and the lookup is still running. This is only while the screen is being looked at, for
    /// the reason written on <see cref="PlateWaiting"/>.</summary>
    public bool MarkWaiting =>
        Station is not null && _stationLogo is null && _stationMarkFetching && _roomShown;

    /// <inheritdoc cref="GroundShown"/>
    public Visibility StationLogoShown =>
        Station is not null && _stationLogo is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The big radio mark on a station's plate, where the station has no mark of its own to draw.
    /// </summary>
    /// <remarks>
    /// <b>It waits to be told.</b> A test of only whether the logo has arrived is false for every
    /// station at the moment its card is built. The plate would open on a radio glyph and swap it for
    /// the station's own mark a moment later, and a re-read of the screen would do it again. It
    /// follows the same rule as <c>HomeQuickCard.MarkShown</c>: nothing stands in for a picture until
    /// the fetch has answered.
    /// </remarks>
    public Visibility StationMarkShown =>
        Station is not null && _stationLogo is null && _stationMarkMissing
            ? Visibility.Visible
            : Visibility.Collapsed;

    private bool _stationMarkMissing;

    /// <summary>The station has been asked for a mark and has none. The one fetch sets it, so the
    /// glyph above is shown only where nothing is coming to replace it.</summary>
    public bool StationMarkMissing
    {
        get => _stationMarkMissing;
        set
        {
            if (_stationMarkMissing == value)
                return;
            _stationMarkMissing = value;
            Say(nameof(StationMarkShown));
        }
    }

    /// <summary>The list's own picture in its square, where one was found for it
    /// (<see cref="Services.Art.GeneratedCoverService"/>). It is its own property, not HasCover,
    /// which a record's square also reads: the two squares are never both shown and must not share a
    /// flag.</summary>
    public Visibility MarkPictureShown =>
        Door is not null && _cover is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The mark in the square, shown only where the list's cover has been asked for and there is
    /// none.
    /// </summary>
    /// <remarks>
    /// <b>It depends on <see cref="MarkMissing"/>, not on "there is no picture yet".</b> The second
    /// test is true for every list card at the moment it is built. The square would open on a glyph
    /// and swap it for the cover a second later, and a re-read of the screen would do it again. The
    /// same rule is kept by <see cref="StationMarkShown"/> and <c>HomeQuickCard.MarkShown</c>, for the
    /// same reason: nothing stands in for a picture until the fetch has answered.
    /// </remarks>
    public Visibility MarkGlyphShown =>
        Door is not null && _cover is null && _markMissing
            ? Visibility.Visible
            : Visibility.Collapsed;

    private bool _markMissing;

    /// <summary>The list has been asked for a cover and has none. The one fetch sets it, so the glyph
    /// above is shown only where nothing is coming to replace it.</summary>
    public bool MarkMissing
    {
        get => _markMissing;
        set
        {
            if (_markMissing == value)
                return;

            _markMissing = value;
            Say(nameof(MarkGlyphShown));
        }
    }

    private ImageSource? _cover;
    private ImageSource? _ground;
    private ImageSource? _portrait;
    private ImageSource? _logo;
    private bool _coverMissing;

    /// <summary>Whether the artist has already been asked about. It is asked once per card and kept on
    /// the card, so stepping back to it is free and a reader who steps to and fro does not ask the
    /// catalogues twice.</summary>
    public bool FaceAsked { get; set; }

    /// <summary>
    /// The lookup that is fetching this card's photograph, or a completed task where none is running.
    /// </summary>
    /// <remarks>
    /// It is kept so the startup can WAIT for it. The reveal holds the splash until the first screen's
    /// pictures are in (<c>HomeView.Coats</c>, <see cref="Services.Shell.FirstScreen.Dressed"/>), and
    /// the artist's photograph is the largest picture on that screen. A lookup started
    /// fire-and-forget cannot be counted by the gate, and its photograph could land after the splash
    /// began to lift and appear over the reveal.
    ///
    /// <para>It is on the CARD and not in a field on the view, for the reason everything else about a
    /// card is: three hosts draw five cards, and a field would be right for only one turn of the
    /// dispatcher. Whoever wants the front card's lookup reads it from the card that is in the plate,
    /// which is a fact whenever it is asked.</para>
    /// </remarks>
    public Task Face { get; set; } = Task.CompletedTask;

    /// <summary>
    /// How wide the plate was when this card's photograph was cut, or NaN where the card has no cut
    /// photograph: a list, a station, an artist with no picture.
    /// </summary>
    /// <remarks>
    /// <b>The cut is made FOR a width and does not survive a different one.</b> The framing puts the
    /// subject clear of the cover and the text, the picture may be mirrored to get it there, and the
    /// left edge is feathered over a distance measured in the plate's own points. All three are baked
    /// into the pixels by <see cref="HomeFeatureArt.FrameAsync"/>. If the picture is drawn into a
    /// narrower plate, the brush crops that bitmap, which removes the feather, leaves a hard edge
    /// where the picture starts, and moves the subject.
    ///
    /// <para><b>Nothing is cut again when the width changes.</b> The picture stays static: re-framing
    /// the same photograph while the reader watches is worse than a small mismatch. So this is only
    /// the record of which width the pixels belong to. It is kept because the cut is written to the
    /// ring, and a cut without its width says nothing.</para>
    /// </remarks>
    public double CutFor { get; set; } = double.NaN;

    /// <summary>Whether a cut is running for this card right now, so that a second settled width does
    /// not start another beside it. The cut is started on the UI thread and this is only read
    /// there.</summary>
    public bool Cutting { get; set; }

    public ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            Say(nameof(Cover));
            Say(nameof(HasCover));
            Say(nameof(MarkPictureShown));
            Say(nameof(MarkGlyphShown));
        }
    }

    public bool HasCover => _cover is not null;

    /// <summary>
    /// The ground on this card is the artist's photograph blurred, not the record's cover.
    /// </summary>
    /// <remarks>
    /// <b>Two answers race for one property.</b> The cover's wash is fetched with the cover and lands
    /// in about a second. The photograph needs a network fetch, two model runs and a wait for a calm
    /// frame, so it lands seconds later, and it is the better picture. Almost always that is also the
    /// order they arrive in, and "almost always" is not an ordering.
    ///
    /// <para>So the better answer latches. Once this is true the cover's wash is refused, and a cover
    /// that arrives late cannot put the record's colours back over the artist's face. It is only set
    /// on the UI thread, where both writers land.</para>
    /// </remarks>
    public bool GroundIsPortrait { get; set; }

    /// <summary>The cover blurred, drawn across the whole plate behind the text. It is its own picture
    /// and not the cover scaled up: the cover is square and the plate is three times as wide as it is
    /// tall, so a cover fitted to the plate would have most of it cut off. It is blurred on a worker,
    /// not on the surface; see <see cref="Services.Art.ArtScaler.Wash"/>.</summary>
    public ImageSource? Ground
    {
        get => _ground;
        set
        {
            _ground = value;
            Say(nameof(Ground));
            Say(nameof(GroundShown));
            Say(nameof(PlateWaiting));
        }
    }

    private bool _groundFetching;

    /// <summary>The cover's fetch for this card is running. It is set at the start of
    /// <c>HomeView.FillFeatureAsync</c> and cleared in its <c>finally</c>, so it cannot outlive the
    /// fetch whatever the outcome.</summary>
    public bool GroundFetching
    {
        get => _groundFetching;
        set
        {
            if (_groundFetching == value)
                return;
            _groundFetching = value;
            Say(nameof(PlateWaiting));
        }
    }

    /// <summary>
    /// Whether a record's card shows the record spinner in the middle of its plate: its ground has not
    /// arrived and the fetch for it is still running.
    /// </summary>
    /// <remarks>
    /// Until the ground arrives, a record's plate is bare glass. When the fetch ends without a
    /// picture, the spinner stops: a missing cover has the generated picture (<see cref="FigureShown"/>),
    /// and a failed fetch leaves the glass. It depends on a running fetch and not on "no ground yet",
    /// so a fetch that never starts (no <c>CoverLoader</c>) cannot leave the spinner turning. It runs
    /// only while the screen is being looked at (<see cref="RoomShown"/>), because
    /// <see cref="RecordSpinner"/> keeps its timer running behind a collapsed ancestor.
    /// </remarks>
    public bool PlateWaiting =>
        Door is null && Station is null && _ground is null && _groundFetching && _roomShown;

    /// <summary>
    /// Whether there is a ground to draw at all. Every picture on this card has a flag like this
    /// beside it, for the reason below.
    ///
    /// <para><b>Three hosts draw five cards.</b> The plate and the two neighbours keep their template
    /// and are handed another card on every step, so what is on screen is the same
    /// <see cref="Microsoft.UI.Xaml.Media.ImageBrush"/> with a new binding. Bound to null, it does not
    /// clear: the last card's picture stays painted, and a card with no photograph of its own shows
    /// the one belonging to the card that stood there before it. That is how a Various Artists
    /// compilation, which is never asked about because it is not a person, could show a photograph
    /// of another artist.</para>
    ///
    /// <para>So the layer is taken DOWN and not emptied. The artist page's shelves follow the same
    /// rule for the same reason, and so does the cover in this template.</para>
    /// </summary>
    public Visibility GroundShown => _ground is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The photograph of the artist, drawn over the ground and under the text. It is null
    /// where no catalogue holds one, where the artist is Various Artists, or where nothing has asked
    /// yet. The plate then stands on the blurred cover, which is a finished plate.
    ///
    /// <para>Only <see cref="ShowPortrait"/> sets it, and it brings the picture up with a fade. The
    /// setter is private so there is no second way in that skips the fade.</para></summary>
    public ImageSource? Portrait
    {
        get => _portrait;
        private set
        {
            _portrait = value;
            Say(nameof(Portrait));
            Say(nameof(PortraitShown));
        }
    }

    /// <inheritdoc cref="GroundShown"/>
    public Visibility PortraitShown => _portrait is null ? Visibility.Collapsed : Visibility.Visible;

    private double _portraitOn;

    private ChromeFade? _portraitFade;

    /// <summary>
    /// How far the photograph has come up, 0 to 1. The plate's picture layer binds its opacity to this.
    ///
    /// <para><b>It is on the card, not on the element.</b> Three hosts draw five cards, so an opacity
    /// written to a host is written to whichever card is standing there when it lands, and a card
    /// carried to a neighbour in the middle of a fade would take the host's value instead of its own.
    /// Bound to the card, a card that is stepped away from goes on coming up where it stands, and one
    /// stepped back to is as far up as it had got. The pictures follow the same rule; see
    /// <see cref="GroundShown"/>.</para>
    /// </summary>
    public double PortraitOn
    {
        get => _portraitOn;
        private set
        {
            if (_portraitOn == value)
                return;
            _portraitOn = value;
            Say(nameof(PortraitOn));
        }
    }

    /// <summary>
    /// Puts <paramref name="photo"/> on the plate and brings it up over <see cref="HomeBanner.PhotoMs"/>
    /// milliseconds.
    /// </summary>
    /// <remarks>
    /// The photograph is fetched, framed by two models and decoded, so it lands seconds after the card
    /// was drawn. If it appeared in one frame, the plate would change in one step under a reader who is
    /// looking straight at it. With the fade, the blurred cover turns into the photograph.
    ///
    /// <para>UI thread only: the fade runs on the composition tick. Every caller is a continuation in
    /// <see cref="HomeView.LoadFaceAsync"/>, which resumes on the dispatcher.</para>
    /// </remarks>
    public void ShowPortrait(ImageSource photo)
    {
        PortraitIn(photo);

        // A fresh fade, not the last one told to go to 1 again: ChromeFade does nothing when it is
        // already headed where it is sent, and a card whose fade has finished is headed for 1.
        _portraitFade?.Stop();
        _portraitFade = new ChromeFade(on => PortraitOn = on, 0);
        _portraitFade.To(1, HomeBanner.PhotoMs);
    }

    /// <summary>
    /// Puts a photograph on the plate at full strength, with no fade.
    /// </summary>
    /// <remarks>
    /// There are two callers, and in both there is nothing to introduce. A RE-CUT is the same
    /// photograph of the same artist cut for the plate's new width, so the reader sees the picture they
    /// were already looking at, fitted to the plate it is now in (see <see cref="CutFor"/>). A picture
    /// that lands BEFORE THE APP IS ON SCREEN has no reader to ease it in for. The reveal is what shows
    /// it, and a 420 ms fade started under the splash would finish over the top of the reveal, and the
    /// artist photograph would be seen loading in. See <c>HomeView.LoadFaceAsync</c>.
    /// </remarks>
    public void SwapPortrait(ImageSource photo)
    {
        _portraitFade?.Stop();
        Portrait = photo;
        PortraitOn = 1;
    }

    /// <summary>The state half of <see cref="ShowPortrait"/>: the picture goes in and is drawn at zero
    /// strength. Only the tests call it on its own, because they run without a composition tick for the
    /// fade. Every other caller wants <see cref="ShowPortrait"/>, which takes this step and then brings
    /// the picture up.</summary>
    internal void PortraitIn(ImageSource photo)
    {
        // Down before the picture goes in, so the first frame that has a photograph to draw draws
        // none of it. The other order shows one frame at full strength, which is the jump this exists
        // to remove.
        PortraitOn = 0;
        Portrait = photo;
    }

    /// <summary>The artist's own lettering, which stands where their name would. The name in their tags
    /// is whatever the person who ripped the disc typed; the logo is the name as the artist set it.
    /// Exactly one of the two is ever drawn.</summary>
    public ImageSource? Logo
    {
        get => _logo;
        set
        {
            _logo = value;
            Say(nameof(Logo));
            Say(nameof(LogoShown));
            Say(nameof(SubtitleShown));
        }
    }

    /// <summary>The line if there is one, then the artist's lettering, then their name. There is one
    /// slot and three candidates, so the three visibilities are read from one order and each does not
    /// decide for itself. Written separately they could all be shown at once, which gives a card with
    /// three lines where the markup has room for one.</summary>
    public Visibility HypeShown =>
        _hype is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public Visibility LogoShown =>
        _hype is { Length: > 0 } || _logo is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SubtitleShown =>
        _hype is { Length: > 0 } || _logo is not null ? Visibility.Collapsed : Visibility.Visible;

    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            Say(nameof(FigureSeed));
            Say(nameof(FigureShown));
        }
    }

    /// <summary>What the generated cover is drawn from where this record has no artwork anywhere. It
    /// is the folder, so the banner and the row below it draw the same record in the same way.</summary>
    public string? FigureSeed => _coverMissing ? Folder : null;

    /// <summary>Whether the generated picture is drawn across the whole plate. This is only once the
    /// cover has been looked for and not found. A record with no artwork anywhere still has a ground
    /// of its own, the same generated picture its square shows, and does not stand on flat colour. It
    /// is its own property for the reason <see cref="GroundShown"/> is one: three hosts, five cards,
    /// and a brush bound to null keeps what it had.</summary>
    public Visibility FigureShown => _coverMissing ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Say(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Home: the collection seen as what has been LISTENED to, not as what is owned.
///
/// <para>Everywhere else in this app the library is a shelf: sorted, searched, filed. This is the
/// one surface that asks a different question: what have you been playing, and what on this shelf
/// goes with it. There are four rows, and each one is derived from facts the library already keeps
/// (see <see cref="HomeShelf"/>, which holds all of the arithmetic and none of the drawing).</para>
///
/// <para><b>Nothing here is a preference and nothing is stored.</b> The rows are read from the shelf
/// each time the view is opened. A record played five minutes ago can therefore be at the front of
/// the first row without anything having been told about it.</para>
///
/// <para><b>Read on a worker, drawn on the UI thread.</b> The shelf read is a group-by over every
/// track row, and the suggestions are a ranking over every album. Both are cheap on a small
/// collection and neither is cheap on a large one. So the whole answer is worked out off the UI
/// thread and handed over once. See <see cref="LoadAsync"/>, and the token that drops an answer the
/// reader has already walked away from.</para>
/// </summary>
public sealed partial class HomeView : UserControl
{
    private readonly ObservableCollection<HomeRowModel> _rows = [];

    private bool _covered;

    /// <summary>
    /// Whether something opaque covers this screen although it is visible: the main window's home while
    /// Pegasus is up (set by <c>ShellPage.Cover</c>). Its rooms are then not shown and leave the beat,
    /// as a collapsed screen's do. Pegasus's own home is never covered and keeps moving.
    /// </summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            if (_covered == value)
                return;
            _covered = value;
            ShowRooms();
        }
    }

    /// <summary>Cancels the read of the shelf when the view is closed, so an answer about a collection
    /// nobody is looking at cannot land on one that has been opened since.</summary>
    private CancellationTokenSource? _loading;

    public HomeView()
    {
        InitializeComponent();
        RowList.ItemsSource = _rows;

        // The quick selection's four panels, bound once. There are four collections, not one, because
        // the layout is four LISTS filled downwards (see QuickPicks.InPanels).
        QuickPanel0.ItemsSource = _panels[0];
        QuickPanel1.ItemsSource = _panels[1];
        QuickPanel2.ItemsSource = _panels[2];
        QuickPanel3.ItemsSource = _panels[3];

        // The banner's height, from the one place it is stated. The neighbours are drawn shorter than
        // it by a fixed drop, so a height also written in the markup would be a second answer.
        FeatureBand.Height = HomeBanner.Height;

        // The remote's stop on the card in front, which DrivenByRemote turns on.
        _plateKey = BuildPlateKey();

        // The named elements' text sizes, from HomeTextSizes for the same reason.
        ApplyTextSizes();

        // The scroller's bar is part of its template, which exists only once the scroller has been laid
        // out, so the space above the first row is applied to it again then (see ContentTop).
        HomeScroller.Loaded += (_, _) => ApplyContentTop();
        HomeScroller.SizeChanged += (_, _) => ApplyContentTop();

        // The generated lists' row has the same hems as the rows of records. It is wired here and not
        // in the markup because this row is written out and not templated. It is asked again for
        // everything that can change either answer: the scrolling itself, the pane being resized, and
        // the cards arriving, which the scroller's own ViewChanged does not report.
        ListScroller.ViewChanged += (_, _) => ShowListHems();
        // The lists' row keeps its card size and slides, as the rows of records below it do (see
        // Row_SizeChanged, which explains why). What is asked again on a width change is the hems.
        ListScroller.SizeChanged += (_, _) => Fitting(PartLists, ListScroller, ShowListHems);
        ListRow.SizeChanged += (_, _) => Fitting(PartListHems, ListRow, ShowListHems);
        Loaded += (_, _) => ShowListHems();

        // A notch of the wheel over this row scrolls the SCREEN. See HoldTheWheel.
        HoldTheWheel(ListRow);

        // The quick selection is hemmed and stepped in the same way and for the same reason: the pane
        // is often too narrow for all four panels. Its width fitting is wired to the SCROLLER and not
        // to the panels, because the fitting writes the panels' own width, and reading that width
        // back would be a loop.
        QuickScroller.ViewChanged += (_, _) => ShowQuickHems();
        QuickScroller.SizeChanged += (_, _) => Fitting(PartQuick, QuickScroller, FitQuick);
        QuickPanels.SizeChanged += (_, _) => Fitting(PartQuickHems, QuickPanels, ShowQuickHems);
        Loaded += (_, _) => FitQuick();
        HoldTheWheel(QuickPanels);

        // The collection this screen is about can be swapped underneath it: a shelf borrowed from
        // another computer takes the place of the user's own, and every other view in the app follows.
        // Home is read on the way in and is not bound to a model, so it is re-read here.
        //
        // Only while it is the view that is up. Home stays in place, collapsed, the whole time the
        // reader is elsewhere, and a group-by over every track row for a screen nobody is looking at
        // is wasted work: the next ShowHome does it anyway (see MainPage.ShowHome).
        Loaded += (_, _) =>
        {
            Library.Changed -= OnShelfChanged;
            Library.Changed += OnShelfChanged;
        };

        // What the tuned station is announcing, put on whichever card is that station. There is ONE
        // subscription for the view, not one per card: the announcement changes every few minutes for
        // as long as a station is on, and the strip is rebuilt on every reading of the shelf, so a
        // card with its own subscription would be a card somebody has to remember to unsubscribe. The
        // view outlives every strip it builds.
        //
        // It is wired to Loaded and not to the constructor so it is dropped again on Unloaded, in the
        // same way as the shelf's own subscription above.
        Loaded += (_, _) =>
        {
            if (Tuner is not { } radio)
                return;

            radio.Changed -= OnTunedChanged;
            radio.Changed += OnTunedChanged;
            ShowWhatIsOn();
        };

        Unloaded += (_, _) =>
        {
            Library.Changed -= OnShelfChanged;
            if (Tuner is { } radio)
                radio.Changed -= OnTunedChanged;
            _loading?.Cancel();
            _dwell.Stop();

            // ...and the record opened in one of the rows: its read, its ground cross-fade and the
            // pulse on its cloud marks. See HomeView.Records.
            RecordsUnloaded();
        };

        // The banner steps itself.
        //
        // The timer is stopped whenever the screen is not the one that is up. Home stays in place and
        // COLLAPSED the whole time the reader is elsewhere (see MainPage.ShowHome), so a timer tied to
        // Loaded alone would step a banner nobody can see, every seven seconds, for as long as the app
        // is open, and the reader would come back to a card they never chose.
        _dwell.Interval = TimeSpan.FromMilliseconds(HomeBanner.DwellMs);
        _dwell.Tick += (_, _) =>
        {
            // Not while the keys are on the strip: the reader is on the card, as with the pointer over
            // it, and OK would open whatever card had just replaced it. It is asked on the tick and not
            // kept from focus events, so a focus that leaves without saying so cannot park the banner.
            if (!KeysOnStrip())
                Step(1);
        };

        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Rewind());

        // A reader with the pointer on the strip is reading it, or is about to press it. It is wired on
        // the strip and not on the plate, so the two neighbours and the two keys are covered as well.
        // The pointer entering a child raises this on the strip too.
        FeatureStrip.PointerEntered += (_, _) => _dwell.Stop();
        FeatureStrip.PointerExited += (_, _) => Rewind();
    }

    /// <summary>What steps the banner on its own. There is one for the view, armed and disarmed by
    /// <see cref="Rewind"/>.</summary>
    private readonly DispatcherTimer _dwell = new();

    /// <summary>
    /// Starts the banner's countdown again, or stops it where there is nothing to step.
    /// </summary>
    /// <remarks>
    /// It is called from every place that changes whether a step would make sense: a reading of the
    /// shelf, a step (the reader's own included, so a press is never followed by a step a moment
    /// later), the screen being shown or hidden, and the pointer leaving the strip.
    ///
    /// <para>It calls Stop before Start, because starting a running timer does not restart it, and
    /// the card would then be held for whatever was left of the last card's seven seconds.</para>
    /// </remarks>
    private void Rewind()
    {
        _dwell.Stop();

        if (Visibility == Visibility.Visible && _features.Count > 1)
            _dwell.Start();

        // The room answers to the same two facts as the countdown, so it is settled from the same
        // place: a screen that is not being looked at has no card in front of anybody.
        ShowRooms();
    }

    /// <summary>
    /// Which cards' rooms move (all of them, while this screen is being looked at) and which one of
    /// them is the card in front.
    /// </summary>
    /// <remarks>
    /// Every card in the strip is told, not only the three that are drawn. A card steps out of the
    /// plate into a neighbour and then out of the strip altogether, and a card that stopped being
    /// drawn without being told would still answer "I am the one in front" the next time it was drawn.
    ///
    /// <para>The answer is derived here and not pushed from whichever handler noticed. It is asked
    /// again on every step and every change of visibility, so it cannot be left over from a turn that
    /// has passed. The element does the rest: <see cref="CardRoom"/> settles its own membership of
    /// the clock from this, its size and whether it is still in the tree.</para>
    /// </remarks>
    private void ShowRooms()
    {
        var live = Visibility == Visibility.Visible && !_covered;

        for (var i = 0; i < _features.Count; i++)
        {
            // Two facts and not one. Whether the room draws at all belongs to the screen, and every
            // card gets the same answer. Which card is in front only splits the timing line. Writing
            // only the second would leave the rooms of a collapsed screen on the clock for the life of
            // the window.
            _features[i].RoomShown = live;
            _features[i].RoomLive = live && i == _feature;
        }

        // The lists' row has rooms too, on the cards drawn inside a frame. They are told here and are
        // not left to read the tree, for the reason the strip's cards are: a collapsed ancestor tells
        // its children nothing, and a room nobody can see goes on drawing for the life of the window.
        // Every card is told, not only the framed ones: the property on a card with no room changes
        // nothing, and a loop that picked cards would be a second place to keep the rule.
        foreach (var card in _listCards)
            card.RoomShown = live;
    }

    /// <summary>Raised on whichever thread swapped the shelf, so it comes back through the queue like
    /// every other report this page acts on.</summary>
    private void OnShelfChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        // Said either way. A screen nobody is looking at is not read again here (it is read when
        // somebody asks for it), but it has to remember that it would now answer differently.
        WentStale();

        if (Visibility == Visibility.Visible)
            Load();
    });

    /// <summary>Where a record's cover comes from: the host's own door, which is
    /// <see cref="Services.Art.AlbumCovers"/>. It tries the folder's picture, the first track's
    /// embedded art, the computer on this network that holds the record, and then the cloud. It is
    /// handed in and not read here, in the same way as for the artist page and the genre page, so
    /// every shelf in the app reads a cover in the same way.</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? CoverLoader { get; set; }

    /// <summary>Ten milliseconds of every sixteen: the genre page's share, not the wall's five. This
    /// screen IS what the reader is waiting for, and its rows are blank squares until the pictures
    /// arrive, so the pictures are worth more of the frame here. See PacedPictures and
    /// FrameBudget.</summary>
    private const double PictureShareMs = 10;

    /// <summary>
    /// The gate every one of this screen's decodes goes through.
    /// </summary>
    /// <remarks>
    /// <para>The FETCHES are not gated: they all run at once, off this thread, which fills the rows at
    /// the speed of the network instead of one card at a time.</para>
    ///
    /// <para><b>The share is ten while the splash is up as well.</b> Raising it to fourteen while
    /// nothing of this screen is visible does not help, even though protecting a screen nobody can see
    /// seems pointless. It is nearly three times slower (about 11 s to dress, against about 4 s),
    /// because the frame the decodes take is the frame the LAYOUT of the rows needs, and a picture
    /// cannot be shown on a card that has not been arranged. See PacedPictures.</para>
    /// </remarks>
    private readonly Services.Art.PacedPictures _pictures = new(PictureShareMs);

    /// <summary>The square the generated lists' row draws at, shared by every card in it in the same
    /// way as a row of records. It is replaced on each read of the shelf and not reset, so cards from
    /// the last reading cannot be re-sized by the width of this one. They are dropped with it.</summary>
    private SimilarTileMetrics _listTiles = new();

    /// <summary>Raised with a library-relative folder when the key on a record's cover is pressed.
    /// It has the same shape as the album wall's own: this view opens nothing, it says which record
    /// was chosen.
    ///
    /// <para>The card itself does not raise it. Pressing a card opens the record's tracklist under its
    /// row (see HomeView.Records), as the album wall and an artist's page do. The record's own page is
    /// the key drawn on the cover under the pointer.</para></summary>
    public event EventHandler<string>? AlbumRequested;

    /// <summary>Raised when one of the generated lists is pressed. It has the same shape: this view
    /// says which list, and the page that owns the panes opens it where playlists live.</summary>
    public event EventHandler<SmartPlaylistId>? PlaylistRequested;

    /// <summary>Raised by the locked panel's one button. It has the same shape as the rest: this view
    /// asks, and the page that owns the import plumbing opens the add-music window.</summary>
    public event EventHandler? AddMusicRequested;

    private void LockedAdd_Click(object sender, RoutedEventArgs e)
        => AddMusicRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Puts the locked panel up or takes it down, and fills it with the gate's own numbers.
    /// </summary>
    /// <remarks>
    /// The rows are hidden and not left behind the panel: they hold the same few records under five
    /// headings, and that is exactly what the gate exists to not show. The ring is told whether it is
    /// on screen, because it animates on a timer and every frame of it repaints the window on the
    /// Skia head.
    /// </remarks>
    private void ShowGate(bool shut, FeatureGate gate)
    {
        LockedState.Visibility = shut ? Visibility.Visible : Visibility.Collapsed;
        HomeScroller.Visibility = shut ? Visibility.Collapsed : Visibility.Visible;
        LockedRing.Live = shut;

        if (!shut)
            return;

        LockedHeading.Text = gate.ToGo == 1
            ? Loc.Get("HomeLock_ToGoOne")
            : Loc.Format("HomeLock_ToGo", gate.ToGo);

        LockedProgress.Text = Loc.Format("HomeLock_Progress", gate.Have, gate.Want);
        LockedRing.Fraction = gate.Fraction;
    }

    /// <summary>Raised when a kept playlist is pressed, by its node in the tree. These are two events
    /// and not one that carries a door, because the host opens the two through different calls, and a
    /// single event would only be unpacked into these two on the other side.</summary>
    public event EventHandler<long>? StoredPlaylistRequested;

    /// <summary>Raised with the station when the radio card is pressed. It has the same shape as the
    /// three above (this view says what was chosen). It is the only one of the four whose answer
    /// starts audio: pressing the card tunes the station.</summary>
    public event EventHandler<RadioStation>? StationRequested;

    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();

    /// <summary>The tuner, or null where there is none to ask (the design-time tree). It is null and
    /// does not throw, because it is read from <c>Loaded</c> and a designer with no services would
    /// take the whole view down with it.</summary>
    private static Services.Audio.RadioService? Tuner
    {
        get
        {
            try
            {
                return App.Services.GetRequiredService<Services.Audio.RadioService>();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The tuner said something: another station, another song, or the stream dropped. This
    /// may be on a background thread, which is why this hops to the dispatcher and reads nothing
    /// here.</summary>
    private void OnTunedChanged() => DispatcherQueue.TryEnqueue(ShowWhatIsOn);

    /// <summary>What a station announces, or null where it is not worth printing: nothing announced,
    /// or the marker the station itself puts out during the ad breaks. The stations pane makes the same
    /// judgement about the same string. Printing the marker as a song would make the line look
    /// broken.</summary>
    /// <param name="station">The station's printed name, taken off the front of the announcement
    /// where it is there, because it is already across the card in bold. See
    /// IcyTitle.WithoutStation.</param>
    internal static string? Printable(string? announced, string? station)
    {
        if (announced is not { Length: > 0 } || RPM.Core.Radio.RadioAds.IsAdBreak(announced))
            return null;

        return RPM.Core.Radio.IcyTitle.WithoutStation(announced, station) is { Length: > 0 } line
            ? line
            : null;
    }

    /// <summary>
    /// What a station announces, with its printed name taken off the front, and also the directory's
    /// name where the admin gave the station a different title.
    ///
    /// <para>The stream announces itself under its own name ("NPO Radio 2 - Artist - Title"), not
    /// under a title set in the admin. With only the printed name taken off, the whole announcement
    /// would be shown.</para>
    /// </summary>
    internal static string? Printable(string? announced, RPM.Core.Radio.RadioStation station)
    {
        var line = Printable(announced, station.Display);
        return line is not null && RPM.Core.Radio.StationEdits.Of(station)?.Name is not null
            ? Printable(line, RPM.Core.Radio.RadioNames.Clean(station.Name))
            : line;
    }

    /// <summary>
    /// Asks the station in front what it is playing, and puts the answer on its card.
    /// </summary>
    /// <remarks>
    /// <b>Only the card in front, and that is the whole cost control.</b> A peek is a connection to
    /// somebody's server (see <see cref="IcyPeek"/>), so it is made for the one station a reader is
    /// looking at, not for the three that are drawn or the seven on the strip. The two neighbours show
    /// no line until they are stepped to, which is when the line starts to be useful.
    ///
    /// <para>It is not used for a station that is TUNED: the tuner already has the title, live and
    /// free, and a peek would be a second connection to a station this app is listening to.</para>
    ///
    /// <para>It is called on every step and not once per card, because the answer goes stale. What
    /// stops that from being a connection every few seconds is the clock on
    /// <see cref="RadioStationItem.OnAirAsync"/>, which answers from memory for a minute and a
    /// half.</para>
    /// </remarks>
    private async void AskWhatIsOn(HomeFeatureCard card, CancellationToken ct)
    {
        if (card.Station is not { } station)
            return;

        if (Tuner is { Current: { } tuned }
            && string.Equals(station.Uuid, tuned.Uuid, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var announced = Printable(await RadioStationItem.OnAirAsync(station, ct), station);

            // Read again on the way back and not trusted from the check above: the station may have
            // been tuned while the peek was in flight, and the live title must not be replaced by one
            // that was already seconds old when it was asked for.
            if (ct.IsCancellationRequested
                || (Tuner is { Current: { } now }
                    && string.Equals(station.Uuid, now.Uuid, StringComparison.OrdinalIgnoreCase)))
                return;

            card.OnAir = announced;

            Faces.LogInformation(
                announced is { Length: > 0 }
                    ? "[home] {Title}: on air, \"{Announced}\""
                    : "[home] {Title}: the station announces nothing. No line.",
                card.Title, announced ?? "");
        }
        catch (OperationCanceledException)
        {
            // The reader stepped on, or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A station having a bad day is not an answer about what is on it.
        }
    }

    /// <summary>
    /// Puts what the tuned station is announcing onto the card that IS that station, and takes it off
    /// every other card.
    /// </summary>
    /// <remarks>
    /// It walks the strip and does not remember which card is the radio one. The strip is rebuilt on
    /// every reading of the shelf and there can be more than one station on it, so a remembered card
    /// can be stale. The walk is over at most <see cref="HomeFeatures.Count"/> cards and runs when a
    /// song changes, which is minutes apart.
    ///
    /// <para>Every card is written on every pass, including the ones being cleared. Otherwise a
    /// station that was stepped away from in the middle of a song would keep the last thing it was
    /// announcing for ever. The card drops a write of the value it already has, so a pass that changes
    /// nothing says nothing.</para>
    ///
    /// <para>An announcement that is the station's own ad marker is not a song, and is not printed as
    /// one: the stations pane makes the same judgement about the same string. The line then falls back
    /// to what the station IS, which is a finished line and not a gap.</para>
    /// </remarks>
    private void ShowWhatIsOn()
    {
        var tuned = Tuner?.Current;
        var announced = Tuner?.NowPlayingTitle;

        foreach (var card in _features)
        {
            if (card.Station is not { } station)
                continue;

            var isTuned = tuned is not null
                          && string.Equals(station.Uuid, tuned.Uuid, StringComparison.OrdinalIgnoreCase);

            card.Playing = isTuned ? Printable(announced, station) : null;

            // The peeked title is dropped as soon as the station becomes the tuned one: the live title
            // is the same fact and is current, and keeping both would leave a stale one behind when
            // the live one goes quiet between songs.
            if (isTuned)
                card.OnAir = null;
        }
    }

    private void List_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomeListCard card)
            return;

        if (card.Door.Generated is { } id)
            PlaylistRequested?.Invoke(this, id);
        else if (card.Door.Kept is { } nodeId)
            StoredPlaylistRequested?.Invoke(this, nodeId);
    }

    /// <summary>
    /// The cover size the album wall is set to, which is what this screen's rows aim at too.
    /// </summary>
    /// <remarks>
    /// It is a slider on the wall, kept in the settings, so it is a number the listener chose and not
    /// one this screen decided. It is read when a row is built. If the slider moves while the home
    /// screen is open, the rows are re-sized on the next read. Nothing here watches for it, because
    /// the wall and this screen are never in the pane at the same time.
    ///
    /// <para>It falls back to the suggestions row's own size where there is no settings service to
    /// ask, which is the design-time tree.</para>
    /// </remarks>
    private static double WallTileSize
    {
        get
        {
            try
            {
                return App.Services.GetRequiredService<Services.Shell.SettingsService>().AlbumTileSize;
            }
            catch
            {
                return SimilarTileMetrics.Target;
            }
        }
    }

    /// <summary>
    /// A row that changed width. Its CARDS do not change with it.
    /// </summary>
    /// <remarks>
    /// <b>The cover is a fixed size and the row slides.</b> If the row divided its width into whole
    /// cards, every width change would re-size every card in it. On a pane move that is a re-measure
    /// of the row on every frame, and the records on screen would grow and shrink while the pane
    /// moves.
    ///
    /// <para>So the cover keeps the size the row was built with (the wall's own tile size, which is
    /// the listener's slider), and a narrower row simply shows fewer covers, with the hems saying
    /// there is more on either side. This is the same as what the row does when it runs out of
    /// width, and it does it at every width.</para>
    ///
    /// <para>What remains here is the hems. They depend on how much scroll is past each end, so they
    /// do have to be asked again.</para>
    /// </remarks>
    private void Row_SizeChanged(object sender, SizeChangedEventArgs e)
        => Fitting(PartRows, sender, () =>
        {
            ShowRowHems(sender);

            // A narrower row holds the same cards at the same stride but shows fewer of them, so the
            // open record's card can leave the screen without the row having been scrolled at all.
            PointAtOpenCard();
        });

    /// <summary>
    /// Whether the rows on screen are still what the shelf would give.
    /// </summary>
    /// <remarks>
    /// <b>True until the first read, and true again only when something has actually changed.</b> The
    /// question is not who is asking (which door home was opened by) but whether the answer would be
    /// different. If it depended on the door, the same screen would rebuild itself or not depending
    /// on how the reader arrived, and neither answer is right for all doors: the reader would lose
    /// their place when returning to home.
    ///
    /// <para>Two things can make the answer different while nobody is looking: the shelf changing
    /// under it (<see cref="OnShelfChanged"/>), and a play landing in the log, which every row here
    /// is a question about (<c>PlayCounter.Noted</c>). If neither happens, the screen the reader left
    /// is still the screen the shelf would draw, down to the scroll position they left it at.</para>
    /// </remarks>
    public bool Stale { get; private set; } = true;

    /// <summary>Says that what is on screen is no longer what the shelf would give, without reading it
    /// again. The read happens when somebody asks for home, and a screen nobody is looking at does not
    /// need to be right yet.</summary>
    public void WentStale() => Stale = true;

    /// <summary>Whether a reading is in flight, and whether anything asked for another while it was.</summary>
    private bool _reading;

    private bool _readAgain;

    /// <summary>
    /// Reads the shelf and fills the rows. The host calls it every time the view is opened. The result
    /// is not kept between openings: what has been played changes while the view is away, and a home
    /// screen showing old listening is the one thing it must never do. One reading runs at a time.
    /// </summary>
    /// <remarks>
    /// <b>Two readings must not run at once.</b> Cancelling the token does not stop a walk already on
    /// the worker, because the block does not check it between its parts. A second ask would start a
    /// second walk of the whole shelf beside the first, and the two would compete for the reading
    /// gate: about 4.2 s for two readings that finish 85 ms apart, against about 1.3 s for one reading
    /// on its own.
    ///
    /// <para>So a reading that arrives while one is running is remembered and not started, and one
    /// more pass runs when the first is done. That is at most two walks instead of N, and never two at
    /// the same time. Whether the second pass is needed at all is <see cref="Stale"/>'s question, and
    /// the caller asks it.</para>
    /// </remarks>
    public async void Load()
    {
        if (_reading)
        {
            _readAgain = true;
            return;
        }

        // This is logged and not fixed, because the alternative is silence. Home reads its cards and
        // then asks CoverLoader for each cover. If it is asked before MainPage has handed one over,
        // every card keeps the generated cover it was built with, and nothing reports it. One line in
        // the ring turns that into something that can be found.
        //
        // A throw would be wrong. A home screen with plain covers is still a home screen, and refusing
        // to draw one is worse than drawing it badly, and this runs during the opening.
        // Read with: rpmscope log --channel boot
        if (CoverLoader is null)
            RPM.Core.Diagnostics.Scope.Said(
                "boot", "home asked to read with no CoverLoader: every sleeve will be a made one");

        _reading = true;

        // The first reading is dressed behind the splash and has nothing to hide. Every later one is a
        // rebuild in front of somebody, so the screen goes down and the loader comes up. See
        // ReadingState in the markup.
        var rebuild = _screenShown;
        if (rebuild)
            HoldScreen(true);

        try
        {
            do
            {
                _readAgain = false;
                _loading?.Cancel();
                _loading = new CancellationTokenSource();

                // Whoever asked for the next reading is answered by this pass, which starts after they
                // asked (see NextReadingDone).
                _readsArmed.AddRange(_readsAsked);
                _readsAsked.Clear();

                // The fold is counted down per reading, not once per window. The groups land again for
                // every read, and a counter left at zero would report the screen dressed before its
                // new pictures had been asked for.
                Interlocked.Exchange(ref _foldLeft, AboveTheFold.Length);
                _dressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                await LoadAsync(_loading.Token);

                if (rebuild && !_readAgain)
                    await DressedOrGiveUpAsync(_loading.Token);
            }
            while (_readAgain);
        }
        finally
        {
            _reading = false;
            if (rebuild)
                HoldScreen(false);

            foreach (var done in _readsArmed)
                done.TrySetResult();
            _readsArmed.Clear();
        }
    }

    /// <summary>Asked for by <see cref="NextReadingDone"/> and not yet covered by a pass.</summary>
    private readonly List<TaskCompletionSource> _readsAsked = [];

    /// <summary>Covered by the reading now running, and answered when it ends.</summary>
    private readonly List<TaskCompletionSource> _readsArmed = [];

    /// <summary>
    /// A task that completes when a reading that STARTS after this call has finished and the screen is
    /// shown again: dressed, or given up on after <see cref="DressWait"/>. A reading already under way
    /// does not count, because it may be reading what was on the shelf before the caller changed it.
    /// Pegasus waits on this after switching collections (see PegasusChrome.PickCollection).
    ///
    /// <para>It never completes if no reading starts: a screen that is not shown reads nothing until it
    /// is shown (see OnShelfChanged). A caller that must not wait for ever puts a limit on it. UI
    /// thread only, as <see cref="Load"/> is.</para>
    /// </summary>
    public Task NextReadingDone()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _readsAsked.Add(done);
        return done.Task;
    }

    /// <summary>How long a rebuild waits for its pictures before it shows the screen anyway. A picture
    /// that is not coming (a cover that returns 404, a network that has gone away) must not hold the
    /// screen the reader is standing in front of. The time is long enough for a warm read, which is
    /// the case this is for: about 1.3 to 2.6 s from the shelf read to the fold being dressed.</summary>
    private static readonly TimeSpan DressWait = TimeSpan.FromSeconds(6);

    /// <summary>Raised when the groups above the fold have all arrived for THIS reading. It is replaced
    /// on each read in <see cref="Load"/>, so a source left completed by the last reading cannot wave
    /// this one through.</summary>
    private TaskCompletionSource? _dressed;

    /// <summary>Waits for the fold to be dressed, for <see cref="DressWait"/>, or for the read to be
    /// overtaken, whichever comes first. It never throws: the caller's next move is to show the
    /// screen either way.</summary>
    private async Task DressedOrGiveUpAsync(CancellationToken ct)
    {
        if (_dressed is not { } dressed)
            return;

        // Nothing to dress. A reading that bailed (the gate shut, an empty collection, a shelf that
        // answered nothing) never asks for a picture, so its fold never lands, and the wait below
        // would spend its whole limit on a screen that is already finished.
        if (_rows.Count == 0 && _listCards.Count == 0 && _features.Count == 0)
            return;

        try
        {
            await Task.WhenAny(dressed.Task, Task.Delay(DressWait, ct));
        }
        catch (OperationCanceledException)
        {
            // Overtaken by another reading, which will hold the screen itself.
        }
    }

    /// <summary>
    /// The screen down and the loader up, or the other way round.
    /// </summary>
    /// <remarks>
    /// It changes the content's opacity and not its visibility. A collapsed panel is not laid out, so
    /// taking it down and putting it back would measure the whole screen twice for nothing, and the
    /// rows would lose the place the reader had scrolled to.
    /// </remarks>
    private void HoldScreen(bool held)
    {
        ReadingState.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        ReadingSpinner.Spinning = held;
        HomeContent.Opacity = held ? 0 : 1;
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is null)
        {
            // There is no shelf to read, so there is nothing to draw, but the screen is still as
            // finished as it is going to get, and leaving it held back would hold the splash with it.
            // See ShowScreen.
            ShowScreen();
            return;
        }

        // Cleared where the read BEGINS and not where it lands, so a change that arrives while this
        // one is in flight is not thrown away when it finishes. The worst cost is one read too many.
        // Clearing it at the end would cost a read that never happens.
        Stale = false;

        var db = library.Shelf;

        // Everything the four rows are decided from, in one read off the worker: the group-by that the
        // wall already makes, plus the traits the ranking asks about. Both are walks of the whole
        // collection and neither belongs on the thread that is drawing.
        // One clock reading for every generated list, so the windowed ones ("on repeat", "first
        // listens") all answer about the same moment. Two cards that disagree about where the last
        // month starts would be a difference nobody can see and nobody can explain. The playlist pane
        // reads its own totals by the same rule.
        var now = DateTime.UtcNow.Ticks;

        // The clock is read ONCE here and carried, so the mood the banner holds up and the day it
        // counts for cannot come from two different moments.
        var local = DateTime.Now;

        // The square this reading's list cards are drawn at. It is built here and not on the worker,
        // so the settings service is read on the UI thread, and it is handed to the readers. A card
        // holds the measurement it was built with, and the row's width sets it as soon as the row is
        // laid out.
        var tiles = _listTiles = new SimilarTileMetrics(WallTileSize, _textSizes);

        // What each part of the reading costs, so a report that the home screen is slow to appear can
        // be answered with a number. Read with: rpmscope log --channel home
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double shelfMs = 0, suggestedMs = 0, tasteMs = 0;

        // Through LibraryService.Read: a refusal puts the problem screen up over the app instead of
        // leaving this screen held back behind it. The body reads `db`, which is the mirror handed in.
        var (read, gathered) = await Cancellable.RunAsync(() => library.Read(db, "home's shelf", _ =>
        {
            // One reading of the listening log for the whole screen. Two things ask for it: the mood
            // the banner holds up, and the row drawn from what this listener plays. It is a walk of
            // every logged play, so asking twice is the same walk twice for one answer. Nothing is read
            // on a borrowed shelf: the log there is the borrower's few plays of somebody else's
            // records, which is a question about nobody.
            var part = System.Diagnostics.Stopwatch.StartNew();
            double Took() { var ms = part.Elapsed.TotalMilliseconds; part.Restart(); return ms; }

            // The SESSION's profile, not a fresh one. There are two reasons, and either decides it.
            //
            // A fresh profile is four whole-collection reads: about 1.5 s of a 3.2 s first reading, and
            // about 8 s on a re-read while the cloud mirror was writing. The database already holds this
            // exact answer for the life of the process (LibraryDatabase.SessionProfile), so a fresh
            // profile would walk the whole log again for a description that is already built.
            //
            // And it is the answer the rest of the app uses. "Your mix" and every offered list that
            // depends on a habit are composed against SessionProfile. If home read its own, the
            // banner's mood and the taste row could describe a listener the lists disagree with. The
            // hour is still read fresh (DayParts.Of below): what is held fixed is the description, not
            // which part of the day it is, which is the distinction SessionProfile states.
            var profile = library.IsPeerView
                ? ListeningProfile.Nothing
                : db.SessionProfile;
            var profileMs = Took();

            // What every mood card on this screen is cut by, read once. The genres are what this
            // listener plays in this part of the day. The offered set is the whole-shelf moods and the
            // genre cuts under them, with no decades, because a decade is not a mood. Nothing is read
            // on a borrowed shelf: it offers no mood at all, so the query would answer nothing at the
            // cost of asking.
            var cuts = library.IsPeerView
                ? MoodCuts.None
                : new MoodCuts(
                    profile.GenresAt(DayParts.Of(local.Hour)),
                    new HashSet<SmartPlaylistId>(
                        db.OfferedGeneratedLists(SmartPlaylists.Moods, withDecades: false)));
            var cutsMs = Took();

            var facts = db.QueryAlbumFacts();
            var factsMs = Took();

            var readTraits = ReadTraits(db);
            var traitsMs = Took();

            // Nothing in this pass is a mood, so nothing in it reads the cuts or could collide with
            // the banner's pick. Both are stated anyway, for the reason written on Cut.
            var lists = ReadLists(db, library.IsPeerView, now, tiles, moodCut: false, cuts, taken: null);
            var listsMs = Took();

            var keptLists = ReadKeptLists(db, library.IsPeerView, tiles);
            var keptMs = Took();

            var bannerList = ReadBannerList(db, library.IsPeerView, now, local, cuts);
            var bannerMs = Took();

            // Which records hold an explicit song, for the whole shelf in one read. It is read here and
            // not per card, for the reason the wall reads it here too: a fact wanted by every card is
            // one query, not four hundred takes of the reading gate.
            var explicitAlbums = new HashSet<string>(db.QueryExplicitAlbums(), StringComparer.OrdinalIgnoreCase);
            var explicitMs = Took();

            if (RPM.Core.Diagnostics.Scope.On)
                RPM.Core.Diagnostics.Scope.Said(
                    "home",
                    $"shelf parts: profile {profileMs:F0}, cuts {cutsMs:F0}, facts {factsMs:F0}, "
                    + $"traits {traitsMs:F0}, lists {listsMs:F0}, kept {keptMs:F0}, "
                    + $"banner {bannerMs:F0}, explicit {explicitMs:F0}");

            return (facts, readTraits, lists, keptLists, bannerList, profile, explicitAlbums, cuts);
        }), ct);

        shelfMs = clock.Elapsed.TotalMilliseconds;

        // Not run, called off, or unanswered: nothing to draw, but the screen must not be left held
        // back. Unanswered means a refusal with the problem screen up over the app, or a mirror
        // closed for another whose Changed reads home again.
        if (!read || ct.IsCancellationRequested || gathered is null || !gathered.Answers(out var shelf, out _))
        {
            ShowScreen();
            return;
        }

        var (facts, traits, lists, kept, banner, profile, marked, cuts) = shelf;

        var listened = HomeShelf.Listened(facts, HomeShelf.RowLength);
        var played = HomeShelf.Played(facts, HomeShelf.RowLength);
        var added = HomeShelf.Added(facts, HomeShelf.RowLength);

        // What the suggestions are read from, and what they must not repeat: the two rows are on the
        // same screen, one under the other. Which row that is, is HomeShelf's answer: a borrowed shelf
        // has no listening on it at all, and the newest records are what it has instead. See
        // HomeShelf.Basis.
        var basis = HomeShelf.Basis(listened, added);
        var byFolder = traits.ToDictionary(t => t.Folder, StringComparer.OrdinalIgnoreCase);
        var seeds = basis.Take(HomeShelf.TasteSeeds)
                         .Where(byFolder.ContainsKey)
                         .Select(f => byFolder[f])
                         .ToList();

        var atSuggested = clock.Elapsed.TotalMilliseconds;
        if (await Cancellable.RunAsync(
                () => HomeShelf.Suggested(seeds, traits, basis, HomeShelf.RowLength, App.Launch), ct)
            is not { Ran: true, Value: { } suggested } || ct.IsCancellationRequested)
        {
            ShowScreen();
            return;
        }

        // What the draw actually produced. The row is meant to differ between launches and stay the
        // same within one. Neither half of that can be seen from one screen: two launches side by
        // side are the only way to tell a row that varies from a row that only looks unfamiliar.
        RPM.Core.Diagnostics.Scope.Said(
            "home",
            $"suggestions: draw {App.Launch}, {suggested.Count} of them, front = "
            + string.Join(" | ", suggested.Take(3)));

        // What this listener is INTO, which is a different question from what they played last week
        // (see HomeShelf.FromTaste). It is read from the whole log and not from the last few records,
        // so it stays steady while the row above it follows the week. Nothing already on screen is
        // repeated: the two recommendation rows are one under the other and would otherwise agree
        // about half a dozen records.
        var heard = facts.Where(f => f.Plays > 0).Select(f => f.Folder).ToList();
        var above = new HashSet<string>(
            listened.Concat(suggested), StringComparer.OrdinalIgnoreCase);

        suggestedMs = clock.Elapsed.TotalMilliseconds - atSuggested;

        var atTaste = clock.Elapsed.TotalMilliseconds;
        if (await Cancellable.RunAsync(
                () => HomeShelf.FromTaste(
                    profile.Artists, profile.Genres, traits, heard, above,
                    HomeShelf.RowLength, App.Launch), ct)
            is not { Ran: true, Value: { } taste } || ct.IsCancellationRequested)
        {
            ShowScreen();
            return;
        }

        RPM.Core.Diagnostics.Scope.Said(
            "home",
            $"taste: {profile.Artists.Count} acts, {profile.Genres.Count} genres, {taste.Count} records, front = "
            + string.Join(" | ", taste.Take(3)));

        var names = facts.ToDictionary(f => f.Folder, StringComparer.OrdinalIgnoreCase);

        // The open record goes with the rows it was opened in: every row container is replaced below,
        // and the drawer holding it is one of them. See HomeView.Records.
        ShutEvery();

        _rows.Clear();
        AddRow(HomeRow.Listened, "Home_Listened", listened, names, marked);
        AddRow(HomeRow.Suggested, "Home_Suggested", suggested, names, marked);
        AddRow(HomeRow.Taste, "Home_Taste", taste, names, marked);
        AddRow(HomeRow.Played, "Home_Played", played, names, marked);
        AddRow(HomeRow.Added, "Home_Added", added, names, marked);

        // The reader's own marks first, then the lists on the shelf itself. On the user's own
        // collection there are none of the second kind: their playlists are one press away on the
        // rail, and home's row is what the library WROTE. A borrowed shelf is the other case. Almost
        // nothing there can be written (see SmartPlaylists.OnABorrowedShelf), and what it has instead
        // is the lender's own lists, mirrored with the records.
        // The collection is observable because the mood lists join this row after the screen is up.
        // It is replaced as a whole on every reading and not added to: what is in it is a reading of
        // the shelf, not a collection somebody is editing.
        _listCards = new ObservableCollection<HomeListCard>(kept.Count > 0 ? [.. lists, .. kept] : lists);
        var offered = _listCards;
        ListRow.ItemsSource = offered;
        ListsBlock.Visibility = offered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Nothing sizes these cards to the row: they are the wall's own tile size and stay it, and a
        // row too narrow for all of them slides. See Row_SizeChanged.

        // ...and the row is named for what is actually in it. Somebody else's playlists are not made
        // for anybody here.
        ListsHeading.Text = Loc.Get(kept.Count > 0 ? "Playlists_Title" : "Home_MadeForYou");

        // The banner is drawn from the same five answers as everything else on this screen, and it is
        // built after the lists because one of its cards is one of them. Its heading is read from the
        // row and is not worked out again, so the two cannot disagree about whose lists these are.
        // What each record says its year is, for the line that puts one forward. It comes from the
        // traits the ranking already read and not from a query of its own: they carry the year that
        // AlbumSort settled on, which is the year every other surface prints.
        var years = traits.ToDictionary(t => t.Folder, t => t.Year, StringComparer.OrdinalIgnoreCase);

        var stationsHeard = MostListenedStations();

        ShowFeatures(BuildFeatures(
            added, suggested, played, listened, banner ?? offered.FirstOrDefault(), ListsHeading.Text,
            names, years, stationsHeard));

        // The quick selection, from the same reading as everything else on this screen. It runs on a
        // worker because two of its four answers are queries. The cards it builds are plain objects
        // whose one brush is made on first read, which is what lets them be built off the thread.
        var peer = library.IsPeerView;
        var shelfRoot = library.ShelfRoot ?? "";
        var quick = await Cancellable.RunAsync(
            () => library.Read(db, "home's quick picks",
                mirror => ReadQuickPicks(mirror, peer, shelfRoot, stationsHeard, names, offered, tiles)),
            ct);

        // Unanswered leaves the quick picks off. A refusal has put the problem screen up over the app.
        if (quick is { Ran: true, Value: { } asked } && asked.Answers(out var picks, out _) && !ct.IsCancellationRequested)
            ShowQuickPicks(picks);

        // The gate.
        //
        // It is asked here and not before the reading, and from facts.Count and not a count query,
        // because this reading already walked every album to build the rows. A second question to the
        // shelf about how many rows it just returned would be a second walk.
        //
        // A BORROWED SHELF IS NOT GATED. The number is about whether this screen's headings can be
        // true of a collection, and a lender's shelf is already a collection: the borrower's own
        // album count says nothing about it.
        var gate = HomeGate.For(facts.Count);
        bool shut = !gate.Open && !library.IsPeerView;

        ShowGate(shut, gate);

        var anything = offered.Count > 0 || _rows.Any(r => r.Cards.Count > 0);
        EmptyState.Visibility = anything || shut ? Visibility.Collapsed : Visibility.Visible;
        HomeSubtitle.Text = anything && !shut
            ? Loc.Format("Home_Count", facts.Count)
            : "";

        // The pictures, in the order they are looked at.
        //
        // THE BANNER FIRST. The plate is the largest thing on the screen and the one a reader looks
        // straight at, so it is asked for first. The fetches all run at once, so their order does not
        // matter. The DECODES go through this screen's one paced gate, and whatever is fetched first
        // gets the slot. If the banner were started after fifty row covers and twelve list squares,
        // the biggest picture on the screen would queue behind sixty small ones (about 6.9 s until
        // every picture was in).
        //
        // They are counted per group, because "the pictures are in" is three different questions, and
        // only two of them are about what is on screen when the splash lifts. See Coats, and the note
        // on FirstScreen.Dressed.
        var bannerCoats = new Coats("banner", Dressed);
        var listCoats = new Coats("lists", Dressed);
        var rowCoats = new Coats("rows", Dressed);

        // The banner's pictures are two per card, not one: the cover and the blur behind it.
        foreach (var card in _features)
            bannerCoats.Add(FillFeatureAsync(card, ct));

        // AND THE ARTISTS' PHOTOGRAPHS, which are the largest pictures on this screen. They must be
        // counted. If the gate opens without them, the front card's photograph can land just after the
        // reveal begins, and the picture comes up over the app appearing.
        //
        // THE THREE THAT ARE DRAWN, which are the same three ShowFeature asks about. A card in no
        // host is not on screen, and its photograph is asked for when it comes into one. They are read
        // from the hosts and not from _feature, the rule the rest of this strip keeps.
        //
        // The cost is small: about 160 ms at most (the neighbours landed 33 ms and 162 ms behind the
        // front card, and the group closed about 2.3 s after the screen had its shape). The bad case
        // is bounded by the gate's own patience, which is four seconds from the screen having shape.
        // That is a LIMIT and not a cost: see ShellPage, where it is set.
        var drawnFaces = new List<Task>();
        foreach (var host in new[] { PlateHost, PeekLeftHost, PeekRightHost })
            if (host.Content is HomeFeatureCard drawn)
            {
                bannerCoats.Add(drawn.Face);
                drawnFaces.Add(drawn.Face);
            }

        // The quick selection's squares come after the banner and before the rows. It is the second
        // thing down the screen and the first that is a LIST of things and not one offer, so a reader
        // who is looking for something to play is looking at it while the rows below are still
        // filling. It is counted with the lists, which are its neighbours and arrive on the same kind
        // of clock.
        foreach (var card in _quick)
            listCoats.Add(FillQuickCoverAsync(card, ct));

        // The lists' squares: one card held up by a slow read, not the whole screen.
        foreach (var card in offered)
            listCoats.Add(FillListCoverAsync(card, ct));

        // The covers come last, and per card: a slow disk or a slow network holds up one square and
        // not the screen. These are below the fold on every window this app opens at, and there are
        // about fifty of them. They are counted to be REPORTED, never to be waited for.
        foreach (var row in _rows)
            foreach (var card in row.Cards)
                rowCoats.Add(FillCoverAsync(card, ct));

        bannerCoats.Close();
        listCoats.Close();
        rowCoats.Close();

        // And the photographs for the cards that are NOT drawn yet, so that a step never shows one
        // arriving. The banner steps itself every seven seconds. If the fourth and fifth cards were
        // asked for only when they came to the front, their photographs would land on a card the
        // reader is already looking at, which is the same fault as at startup, one dwell later.
        //
        // This is AFTER the three that are drawn, and that is the whole of the ordering here. All five
        // decode through this screen's one paced gate, and the ranking is a model run over the widest
        // few, so asking for all of them at once would put the two nobody can see in the queue ahead
        // of the three the reveal is waiting for. See ReadyOffScreenFacesAsync.
        _ = ReadyOffScreenFacesAsync([.. _features], drawnFaces, ct);

        // And the line that puts each record forward. All six are asked at once, not one as each card
        // comes to the front, so the line is there the moment a card is, and does not appear under
        // the reader a moment after they look at it. The cost is bounded: see LoadHypeAsync, which
        // asks a service that answers from memory and then from disk.
        foreach (var card in _features)
            _ = LoadHypeAsync(card, ct);

        // The hems, once the rows are up. It is enqueued and not called: the cards have not been laid
        // out yet in this turn, so there is no scrollable width to read. What lands reads the
        // scrollers' own numbers, so an arrival on a screen that has since been reloaded still tells
        // the truth about what is on it.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowAllHems);

        tasteMs = clock.Elapsed.TotalMilliseconds - atTaste;

        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said(
                "home",
                $"read {clock.Elapsed.TotalMilliseconds:F0}ms: shelf {shelfMs:F0}, "
                + $"suggested {suggestedMs:F0}, taste {tasteMs:F0}, rest "
                + $"{clock.Elapsed.TotalMilliseconds - shelfMs - suggestedMs - tasteMs:F0}");

        // The five lists that need the session's mood cut come after the screen and not before it.
        // See FillMoodListsAsync, which says what that is worth.
        _ = FillMoodListsAsync(
            db, library.IsPeerView, now, tiles, cuts, banner?.Door.Generated, ct);

        // ...and the screen itself, which has been held back until now. Everything with a SHAPE is on
        // it by this line: the rows, the banner, the headings and what each of them says. Only the
        // pictures are still coming, and those fade in one at a time as they arrive. See ShowScreen.
        ShowScreen();
    }

    /// <summary>
    /// The playlists kept on the shelf itself, as cards.
    ///
    /// <para>Only on a borrowed shelf, and that is the whole rule. The user's own playlists have a
    /// rail tab of their own, and home's top row is what the library WROTE about their listening. A
    /// shelf lent from another computer can write almost none of that, because the listing carries
    /// records and no listening, but it does carry the lender's own tree, mirrored with their records.
    /// Without this the row would be one card of favourites over a collection of thousands.</para>
    ///
    /// <para>Their generated lists can be among these: on a borrowed shelf they are kept as playlists
    /// in the same table (see BorrowedMoodLists). Such a card shows the generated list's own mark, and
    /// its picture comes through the same door as every kept list's, which draws it as that generated
    /// list (see PlaylistCover.Generated).</para>
    ///
    /// <para>Empty lists are left out, and so is the pinned Now playing: one opens nothing, and the
    /// other is this machine's queue and has nothing to do with whose shelf is up. Lists are counted
    /// by the items stored on them and not by what the items resolve to. A nested list is not counted,
    /// which is the same answer the pane's own row gives.</para>
    /// </summary>
    private static IReadOnlyList<HomeListCard> ReadKeptLists(
        LibraryDatabase db, bool borrowed, SimilarTileMetrics tiles)
    {
        if (!borrowed)
            return [];

        // Which of the lender's lists hold an explicit song, for the whole tree at once. This is the
        // read the playlists pane makes for its own rows.
        var marked = db.QueryExplicitPlaylists();

        var cards = new List<HomeListCard>();
        foreach (var node in db.GetPlaylistNodes())
        {
            if (node.Kind != PlaylistNodeKind.Playlist || node.IsNowPlaying)
                continue;

            var songs = db.GetPlaylistItems(node.Id).Count(i => i.ItemKind == PlaylistItemKind.Song);
            if (songs == 0)
                continue;

            cards.Add(new HomeListCard
            {
                Door = HomeListDoor.Stored(node.Id),
                Name = node.Name,
                // The mark a kept playlist has in the tree, or the generated list's own where this is
                // one of the owner's generated lists. It is the same choice PlaylistTreeItem.Glyph makes.
                Glyph = node.Cover is PlaylistCover.Generated lent
                    ? SmartPlaylistFace.Glyph(lent.Id.Kind)
                    : "\uf8c9",   // list-music
                Count = Loc.Format("Home_Songs", songs),

                // A playlist somebody made and named says what it is in that name. Nothing here writes
                // a sentence about it, so the line stays the count.
                Caption = Loc.Format("Home_Songs", songs),
                Metrics = tiles,
                ChosenCover = node.Cover,
                IsExplicit = marked.Contains(node.Id),
            });

            if (cards.Count == HomeShelf.RowLength)
                break;
        }

        return cards;
    }

    /// <summary>
    /// The one list the banner holds up: the mood the hour asks for, cut to the music this listener
    /// plays at that hour.
    /// </summary>
    /// <remarks>
    /// Two questions are folded together, and each is answered where it is known.
    /// <see cref="MoodClock"/> says which mood suits the hour, which is a guess about people in
    /// general. The listening profile says which genres this listener plays in that part of the day,
    /// which is a fact about them. So a listener who plays jazz late is offered Jazz Club at midnight,
    /// where somebody who plays techno is offered Slow Machine. <see cref="MoodPick"/> is the
    /// arithmetic and has tests; this is the reading.
    ///
    /// <para>One day in seven it holds up another cut of the same mood instead (see
    /// <see cref="MoodPick.OtherDayIn"/>). A banner that gives the same answer every day stops being
    /// read.</para>
    ///
    /// <para>It is null on a borrowed shelf, which offers none of the moods: somebody else's
    /// collection cut by how THIS listener spends their evenings is a question about nobody. The
    /// banner then has no list card and fills from the other four questions. It is null too where the
    /// shelf offers no mood at all, which is a collection whose music has not been measured yet.</para>
    /// </remarks>
    private static HomeListCard? ReadBannerList(
        LibraryDatabase db, bool borrowed, long now, DateTime local, MoodCuts cuts)
    {
        if (borrowed)
            return null;

        // What they play at this time of day, and what the shelf offers to match it with. Both are
        // handed in and not read here, so the screen reads the log once and asks what is offered once,
        // and so the row of lists below cuts its mood cards by the same two answers. See LoadAsync.
        var (genres, offered) = cuts;

        if (MoodPick.For(MoodClock.Order(local.Hour), genres, offered, local.DayOfYear) is not { } id)
        {
            RPM.Core.Diagnostics.Scope.Said(
                "home", $"banner: no mood list at {local.Hour}h, {offered.Count} offered");
            return null;
        }

        // What it chose and what it chose FROM. The judgement cannot be seen from the card it
        // produces: a mood with one cut and a mood with six look the same on screen, and so does the
        // one day in seven that steps off the usual answer.
        var mood = MoodClock.Best(local.Hour);
        var group = MoodPick.Group(mood, genres, offered);
        RPM.Core.Diagnostics.Scope.Said(
            "home",
            $"banner at {local.Hour}h: {SmartPlaylistFace.Name(id)} ({id.Kind}"
            + $"{(id.Genre is { Length: > 0 } g ? ", " + g : ", whole shelf")}), "
            + $"{mood} group of {group.Count}, day {local.DayOfYear}"
            + $"{(MoodPick.Which(group.Count, local.DayOfYear) == 0 ? "" : " (the one in seven)")}");

        // "tags" and not "genres": the local above is what this LISTENER plays at this hour, and these
        // are the tags of the songs in the list the clock chose. They are two different questions.
        var (tracks, _, tags) = db.SmartPlaylistCard(id, now);
        if (tracks < SmartPlaylists.LeastToShow(id))
            return null;

        return new HomeListCard
        {
            Door = HomeListDoor.Made(id),
            Name = SmartPlaylistFace.Name(id),
            Glyph = SmartPlaylistFace.Glyph(id.Kind),
            Count = Loc.Format("Home_Songs", tracks),
            Caption = MoodBlurbText.For(id, tags) ?? Loc.Format("Home_Songs", tracks),
            InkColor = SmartPlaylistFace.Ink(id.Kind),
        };
    }

    /// <summary>
    /// What a mood card on this screen is cut by: what this listener plays at this hour, and which cuts
    /// the shelf actually offers.
    /// </summary>
    /// <remarks>
    /// It is one value and not two arguments, so a caller cannot take the genres without the cuts that
    /// are available to match them against: a genre this listener plays that the shelf has no list
    /// for is not an answer. It is read once per reading of the screen and handed to both surfaces
    /// that pick one, which stops the banner and the row from disagreeing about what somebody plays
    /// at midnight.
    /// </remarks>
    /// <param name="Genres">What they play in this part of the day, most played first
    /// (<see cref="ListeningProfile.GenresAt"/>). Empty is normal: a fresh collection has no listening
    /// behind it, and every mood card is then the whole shelf's cut.</param>
    /// <param name="Offered">The generated mood lists this shelf has enough music for.</param>
    private readonly record struct MoodCuts(
        IReadOnlyList<GenreShare> Genres,
        IReadOnlyCollection<SmartPlaylistId> Offered)
    {
        /// <summary>Nothing to cut by, which is what a borrowed shelf gets: it offers no mood at all,
        /// and the listening log there is the borrower's few plays of somebody else's records.</summary>
        public static MoodCuts None { get; } = new([], new HashSet<SmartPlaylistId>());
    }

    /// <summary>
    /// Which list a card for this kind actually opens: a cut INSIDE the mood where there is one this
    /// listener plays, and the whole shelf's mood where there is not.
    /// </summary>
    /// <remarks>
    /// <b>A mood card opens a playlist inside the mood list, chosen from the listener's habits, and
    /// not the top-level list.</b> "Chill" is every quiet record on the shelf and "Jazz Club" is the
    /// quiet jazz, and somebody who plays jazz is better served by the smaller list. This is the
    /// argument <see cref="MoodPick.Group"/> is written around for the banner. This is the same
    /// ranking read at its top instead of stepped through, so the banner and the row cannot disagree
    /// about what somebody plays.
    ///
    /// <para><b>Only the four that CAN be cut.</b> <c>SmartPlaylists.BrokenOutByGenre</c> is Calm,
    /// Energetic, Danceable and Focus. Sleep is an absolute cut over the whole collection, so it has
    /// no child list to show and keeps its top-level card. Everything else on this row is about
    /// listening and not about sound, and has no genre reading at all.</para>
    ///
    /// <para><b>And never the one the banner is holding up.</b> The banner takes the same group's top
    /// on six days in seven, so without this the plate and a square on the row below it would be one
    /// list twice. <c>OnTheHomeScreen</c> already drops two kinds for the same objection. A group
    /// with nowhere to step keeps its only member: one list on two surfaces is better than a card
    /// that is not there.</para>
    /// </remarks>
    /// <param name="taken">The list the banner is holding up, or null where it holds up no list.
    /// It is stated at both call sites and never defaulted: the pass that composes the moods is a
    /// different pass from the one that composes everything else, and a default would silently let
    /// the one that matters skip the check.</param>
    private static SmartPlaylistId Cut(
        SmartPlaylistKind kind, MoodCuts cuts, SmartPlaylistId? taken)
    {
        var whole = SmartPlaylistId.WholeShelf(kind);

        if (!SmartPlaylists.BrokenOutByGenre.Contains(kind))
            return whole;

        var group = MoodPick.Group(kind, cuts.Genres, cuts.Offered);
        if (group.Count == 0)
            return whole;

        if (group[0] != taken)
            return group[0];

        return group.Count > 1 ? group[1] : group[0];
    }

    /// <summary>
    /// The generated lists worth offering, with what each of them holds right now.
    ///
    /// <para>Which kinds are offered is the answer of <see cref="SmartPlaylists.OnTheHomeScreen"/>, not
    /// this screen's: the decision about whether a list is music or housekeeping belongs beside the
    /// rules themselves. A borrowed shelf narrows it further, by the same list the playlist pane
    /// narrows by. The listening lists there would be the borrower's few plays of somebody else's
    /// records, which reads as a friend with almost no music.</para>
    ///
    /// <para>A list too short to be worth a card is left out entirely, on the same minimum the pane
    /// uses (<see cref="SmartPlaylists.LeastToShow"/>). An empty card invites the reader to press
    /// something that opens nothing.</para>
    /// </summary>
    private static IReadOnlyList<HomeListCard> ReadLists(
        LibraryDatabase db, bool borrowed, long now, SimilarTileMetrics tiles, bool moodCut,
        MoodCuts cuts, SmartPlaylistId? taken)
    {
        // Which half of the row this pass is for. The lists that need the session's mood cut are
        // composed after the screen is up, because building that cut is most of what the reading
        // costs. See SmartPlaylists.NeedTheMoodCut, and FillMoodListsAsync.
        var offered = (borrowed
                ? SmartPlaylists.OnTheHomeScreen.Where(SmartPlaylists.OnABorrowedShelf.Contains)
                : SmartPlaylists.OnTheHomeScreen)
            .Where(k => SmartPlaylists.NeedTheMoodCut.Contains(k) == moodCut);

        var cards = new List<HomeListCard>();
        var each = RPM.Core.Diagnostics.Scope.On ? new List<string>() : null;
        var perList = System.Diagnostics.Stopwatch.StartNew();

        foreach (var kind in offered)
        {
            perList.Restart();
            var id = Cut(kind, cuts, taken);

            // Three answers out of ONE pass: how long the list is, whether it holds an explicit song,
            // and what it is made of. A generated list has nothing filed in it to ask about, so all
            // three come from composing it once. See LibraryDatabase.SmartPlaylistCard, where the cost
            // of composing a second time is written out.
            var (tracks, marked, genres) = db.SmartPlaylistCard(id, now);
            each?.Add($"{SmartPlaylistFace.Name(id)} {perList.Elapsed.TotalMilliseconds:F0}");

            if (tracks < SmartPlaylists.LeastToShow(id))
                continue;

            // Every generated list is framed, but only five of them stand on the room. A card with a
            // room draws it live behind the window and fetches no cover at all. The rest fetch the
            // cover as usual, which arrives with the frame already composed into it (see
            // GeneratedCover). CoverOverlay.Has answers for the file, so a head built without the asset
            // gets ordinary cards either way.
            var framed = CoverOverlay.Has();

            cards.Add(new HomeListCard
            {
                Door = HomeListDoor.Made(id),
                Name = SmartPlaylistFace.Name(id),
                Glyph = SmartPlaylistFace.Glyph(kind),
                Count = Loc.Format("Home_Songs", tracks),
                Caption = MoodBlurbText.For(id, genres) ?? Loc.Format("Home_Songs", tracks),
                InkColor = SmartPlaylistFace.Ink(kind),
                Metrics = tiles,
                IsExplicit = marked,
                Framed = framed,
                Room = framed && CoverOverlay.Roomed(kind) ? CoverRoom.Of(id) : null,
            });
        }


        if (each is not null)
            RPM.Core.Diagnostics.Scope.Said("home", "each list: " + string.Join(", ", each));

        return cards;
    }

    /// <summary>Every record on the shelf as the ranking asks about it. These are the same three
    /// reads the album page's suggestions row makes, so a record is described in the same way on both
    /// surfaces. Its genres go through <see cref="Genres.Match"/> and its year through
    /// <see cref="AlbumSort.YearFrom"/>, or the two rows would disagree about what a record IS.</summary>
    private static IReadOnlyList<AlbumTraits> ReadTraits(LibraryDatabase db)
    {
        var years = new Dictionary<string, List<(int Year, int Count)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, year, count) in db.QueryAlbumYearCounts())
        {
            if (!years.TryGetValue(folder, out var list))
                years[folder] = list = [];
            list.Add((year, count));
        }

        var genres = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, genre, _) in db.QueryAlbumGenreCounts())
        {
            if (RPM.Core.Metadata.Genres.Match(genre) is not { } canonical)
                continue;

            if (!genres.TryGetValue(folder, out var list))
                genres[folder] = list = [];
            if (!list.Contains(canonical))
                list.Add(canonical);
        }

        var shelf = new List<AlbumTraits>();
        foreach (var row in db.QueryAlbumFacts())
        {
            var parts = row.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            shelf.Add(new AlbumTraits(
                row.Folder,
                row.Album ?? (parts.Length > 0 ? parts[^1] : ""),
                row.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                genres.TryGetValue(row.Folder, out var filed) ? filed : [],
                years.TryGetValue(row.Folder, out var counted) ? AlbumSort.YearFrom(counted) : 0));
        }

        return shelf;
    }

    /// <param name="marked">The folders holding an explicit song. It is stated at the call and not
    /// looked up here, because it is one read for the whole screen. See LoadAsync.</param>
    private void AddRow(
        HomeRow kind,
        string headingKey,
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, LibraryDatabase.AlbumFactsRow> names,
        IReadOnlySet<string> marked)
    {
        var row = new HomeRowModel
        {
            Kind = kind,
            Heading = Loc.Get(headingKey),
            Metrics = new SimilarTileMetrics(WallTileSize, _textSizes),
        };

        foreach (var folder in folders)
        {
            if (!names.TryGetValue(folder, out var fact))
                continue;

            var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            row.Cards.Add(new SimilarRow(
                new SimilarAlbum(
                    fact.Album ?? (parts.Length > 0 ? parts[^1] : folder),
                    fact.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                    folder,
                    SimilarBecause.JustOnTheShelf),
                row.Metrics)
            {
                // Stamped after the ranking's own answer, which says nothing about tags. It is the same
                // assignment the wall makes on its cards, from the same query.
                IsExplicit = marked.Contains(folder),
            });
        }

        _rows.Add(row);
        row.Settled();
    }

    /// <summary>
    /// One card's cover, through the door every wall in this app reads covers by.
    ///
    /// <para>That door is <see cref="CoverLoader"/>: the picture under the folder, the art embedded in
    /// the first track, the computer on this network that holds the record, then the cloud. If this
    /// screen read only the folder on this disk, a borrowed shelf would draw a generated cover for
    /// every card, and so would every record of one's own that had been offloaded, on the one screen
    /// whose whole subject is what has been listened to.</para>
    ///
    /// <para>The cover is asked for at <see cref="ArtScaler.TileEdge"/>, which is the thumb kept on
    /// disk, and DECODED at the bucket that covers the card at this display's scale (see
    /// ArtScaler.DecodeEdgeFor). The card is measured inside the decode and not at the ask: the gate
    /// runs the decode when it reaches it, and by then the row has been laid out and knows how wide
    /// its covers are.</para>
    /// </summary>
    private async Task FillCoverAsync(SimilarRow card, CancellationToken ct)
    {
        if (CoverLoader is not { } load || card.Folder is not { Length: > 0 } folder)
            return;

        try
        {
            // Through the one door, so the card is told there is no picture as well as told there is
            // one. Until it has been told, its generated cover is not drawn at all. See CoverFill.
            await Services.Art.CoverFill.DressAsync(
                card,
                token => load(folder, ArtScaler.TileEdge, token),
                async bytes => await _pictures.Next(() => RemoteImage.DecodeAsync(
                    bytes,
                    ArtScaler.DecodeEdgeFor(card.Metrics.Edge, XamlRoot?.RasterizationScale ?? 1))),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A card without a cover still names the record, which is most of what it is for.
        }
    }

    /// <summary>
    /// One list card's picture, through the same door every other shelf on this screen reads one by.
    ///
    /// <para>What a list shows is not read from a folder, so the fetch is <see cref="ListCoverAsync"/>
    /// and not <see cref="CoverLoader"/>. Everything after that follows the rows' own arrangement:
    /// the decode is metered through this screen's one gate, at the bucket that covers the card at
    /// this display's scale, and the card is measured inside the decode and not at the ask, because
    /// by then the row has been laid out and knows how wide its squares are.</para>
    /// </summary>
    private async Task FillListCoverAsync(HomeListCard card, CancellationToken ct)
    {
        // A framed card is already a finished square: the room draws behind the frame on this screen
        // and is not composed into a picture first. Fetching the composed cover as well would lay a
        // still room over a moving one, which is the same picture twice with the wrong one on top.
        // What this card wants instead is the frame by itself.
        if (card.HasRoom)
        {
            await FillFrameAsync(card, ct);
            return;
        }

        try
        {
            await Services.Art.CoverFill.DressAsync(
                card,
                token => ListCoverAsync(card, token),
                async bytes => await _pictures.Next(() => RemoteImage.DecodeAsync(
                    bytes,
                    ArtScaler.DecodeEdgeFor(card.Metrics.Edge, XamlRoot?.RasterizationScale ?? 1))),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The card keeps its own colour with its mark on it, which is a finished square and not a
            // gap.
        }
    }

    /// <summary>
    /// One framed card's frame: the drawing with this locale's name set in its band, composed on a
    /// worker and decoded through this screen's one gate.
    /// </summary>
    /// <remarks>
    /// The window in it is transparent and the room moves behind it. This is why the frame is a PNG
    /// that the card wears over a <c>CardRoom</c>, and not a picture of the two together. The composed
    /// picture is what every OTHER surface shows for the same list; see <see cref="GeneratedCover"/>.
    ///
    /// <para>The name is read here, on the UI thread, because it is a resource lookup. The drawing is
    /// made on a worker because it is a surface, a face and an encode.</para>
    /// </remarks>
    private async Task FillFrameAsync(HomeListCard card, CancellationToken ct)
    {
        try
        {
            if (card.Door.Generated is not { } id)
                return;

            var name = SmartPlaylistFace.Name(id);

            if (card.Room is not { } room)
                return;

            if (await Cancellable.RunAsync(() => CoverOverlay.Frame(name, room), ct)
                    is not { Ran: true, Value: { Length: > 0 } frame }
                || ct.IsCancellationRequested)
                return;

            var picture = await _pictures.Next(() => RemoteImage.DecodeAsync(frame));

            if (!ct.IsCancellationRequested)
                card.Overlay = picture;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The room stands on its own: it is the list's own colours across the whole square, which
            // is a finished picture and not a gap.
        }
    }

    /// <summary>
    /// The bytes one list is drawn from, out of the one place that list's cover is ever drawn.
    /// </summary>
    /// <remarks>
    /// A GENERATED list goes through <see cref="GeneratedCover"/>, which is where that order lives:
    /// the app's own frame, then a fetched photograph, then the figure drawn from the list's own two
    /// numbers. The playlist page and the playlists tree read the same door, so the card here and the
    /// head of the list it opens are one picture and not two.
    ///
    /// <para>A KEPT list is drawn from its records, through <see cref="PlaylistCovers"/>. This is the
    /// shared door, so this card and that list on an artist's page are the same square. It is always
    /// a borrowed shelf: kept lists are only offered on one (see <see cref="ReadKeptLists"/>).</para>
    /// </remarks>
    private static Task<byte[]?> ListCoverAsync(HomeListCard card, CancellationToken ct)
        => ListCoverAsync(card.Door, card.Name, ct, card.ChosenCover);

    /// <summary>The same, for a caller that has the door and the name but no card: the quick
    /// selection's entries, which are one type across four kinds. It is split and not copied. If a
    /// list's picture were found one way here and another way in the row above, there would be two
    /// pictures of one list.</summary>
    private static async Task<byte[]?> ListCoverAsync(
        HomeListDoor door, string name, CancellationToken ct, PlaylistCover? chosen = null)
    {
        if (door.Generated is { } id)
            return await GeneratedCover.SquareAsync(id, ct);

        if (door.Kept is { } node)
            return (await PlaylistCovers.MadeAsync(
                node, name, chosen ?? new PlaylistCover.Automatic(),
                PlaylistShelf.Borrowed, ct)).Cover;

        return null;
    }

    // The rows' two hems.

    /// <summary>Each row's hems, kept against the scroller they hem. They are built once per scroller:
    /// the rows live in a template and are rebuilt on every reading of the shelf, and a hem is a copy
    /// of the scroller's own visual, not of what is in it.</summary>
    private readonly Dictionary<ScrollViewer, ScrollHem> _hems = new();

    /// <summary>Set once this machine's composition stack has refused, so the other rows do not each
    /// try it again. Their ends stay hard, as they would be if hems did not exist.</summary>
    private bool _noHems;

    private void Row_Loaded(object sender, RoutedEventArgs e)
    {
        ShowRowHems(sender);

        if (sender is ScrollViewer row)
            HoldTheWheel(row.Content as FrameworkElement);
    }

    /// <summary>The row of generated lists, held so the five mood lists can join it once they are
    /// composed.</summary>
    private ObservableCollection<HomeListCard> _listCards = [];

    /// <summary>
    /// The lists that need the session's mood cut, composed after the screen is up.
    /// </summary>
    /// <remarks>
    /// <b>Building that cut is most of what this screen costs.</b> It is three whole-table reads and a
    /// sort, done once per session by whichever list asks first. It took about 1.95 s of a 3.15 s
    /// reading, against 4 to 16 ms for every mood list after it. The screen is held back until the
    /// reading is done, so composing these first would mean two seconds of nothing before anything
    /// appeared.
    ///
    /// <para>They join the row afterwards instead. The row grows to the RIGHT, so nothing already on
    /// screen moves when they arrive. The reader sees five more cards at the end of a row they are
    /// already looking at, and their squares fade in like every other picture here.</para>
    ///
    /// <para>Warming the cut in parallel does not work: it takes the same reading gate as the twelve
    /// lists composed before it, so it serialises against them and the screen waits exactly as
    /// long.</para>
    /// </remarks>
    /// <param name="cuts">What each mood card is cut by (see <see cref="MoodCuts"/>). It is read with
    /// the rest of the screen and carried here, so these five and the banner's own card are cut by
    /// one answer about what this listener plays.</param>
    /// <param name="taken">The list the banner is holding up, so no card here is the same list a
    /// second time. Null where the banner holds up no list.</param>
    private async Task FillMoodListsAsync(
        LibraryDatabase db, bool borrowed, long now, SimilarTileMetrics tiles,
        MoodCuts cuts, SmartPlaylistId? taken, CancellationToken ct)
    {
        var row = _listCards;

        if (await Cancellable.RunAsync(
                () => ReadLists(db, borrowed, now, tiles, moodCut: true, cuts, taken), ct)
                is not { Ran: true, Value: { } moods }
            || ct.IsCancellationRequested
            || !ReferenceEquals(row, _listCards))   // a newer reading owns the row now
            return;

        foreach (var card in moods)
        {
            row.Add(card);
            _ = FillListCoverAsync(card, ct);
        }

        // A shelf whose only lists are these: the row was empty when the screen came up and has
        // something in it now.
        if (row.Count > 0)
            ListsBlock.Visibility = Visibility.Visible;
    }

    // The screen itself.

    /// <summary>Whether the screen has been shown once. It is held back only on the FIRST reading. A
    /// re-read happens under a reader who is already looking at it, and taking the screen away to
    /// bring it back would cause the flash this exists to stop.</summary>
    private bool _screenShown;

    /// <summary>
    /// The screen, brought up once its rows have something in them.
    /// </summary>
    /// <remarks>
    /// <b>The rows must not appear in one frame.</b> Fading the covers fixes the pictures arriving,
    /// but the whole reading lands at once (rows, headings, the banner and the counts), and that needs
    /// a fade too.
    ///
    /// <para>So the content starts at zero opacity in the markup and comes up when the reading is
    /// done. It is one fade for the whole screen and not a fade per thing on it, because it all
    /// arrives on the same turn: several fades of the same length starting together are one fade with
    /// more writes in it. The clock is the album wall's, shared with the covers for the reason
    /// written there.</para>
    ///
    /// <para><b>It is called on every way out of the reading, not only the end of it.</b> A reading
    /// that bails (the shelf not answering, a newer reading cancelling this one) must still leave the
    /// screen visible. Otherwise a first run with nothing to show would hold back the very line that
    /// says there is nothing to show.</para>
    ///
    /// <para><b>It is also what the splash waits for.</b> This screen is what the app opens on, so
    /// this line is the startup's "there is something to look at" (see
    /// <see cref="Services.Shell.FirstScreen"/>). Without it the reveal gate would open on a quiet
    /// drawing thread, which during this reading is a thread with nothing to do because the reading
    /// is on a worker: the splash would come off over a wall that fills in a second later. That is
    /// why every way out of the reading reaches here and not just the end of it. A bail that said
    /// nothing would hold the splash until the gate's six-second limit.</para>
    /// </remarks>
    private void ShowScreen()
    {
        // Said before the early return, not after it: the screen is up from the FIRST reading, and a
        // second reading that arrives while the splash is somehow still on screen is still a screen
        // that is up. FirstScreen is a latch, so saying it twice is saying it once.
        App.Services.GetRequiredService<Services.Shell.FirstScreen>().Up();

        if (_screenShown)
            return;

        _screenShown = true;
        Common.SharpFade.Start(HomeContent);
        SayIfReady();
    }

    /// <summary>
    /// Whether the screen shows its own heading: "home" and the count under it. The main window's
    /// library card does, as every view on that card does. Pegasus does not: there home starts
    /// straight under the mini player (see PegasusChrome.EnsureHome).
    /// </summary>
    public bool HeadingShown
    {
        get => HeadingBox.Visibility == Visibility.Visible;
        set => HeadingBox.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Raised when the screen's vertical position changes, by any means: the wheel, the bar, the
    /// keyboard, or the screen being put back. It carries how far it moved, positive down. Pegasus
    /// sends its mini player away on a move down and ends a reveal of it on any move (see
    /// PegasusChrome.PlayerAway).
    /// </summary>
    public event Action<double>? Scrolled;

    /// <summary>The offset <see cref="Scrolled"/> was last raised at.</summary>
    private double _scrolledAt;

    /// <summary>Whether the screen is scrolled to its top, read from the scroller when asked. Pegasus
    /// brings its mini player back when the screen gets back here (see PegasusChrome.PlayerAway).</summary>
    public bool IsAtTop => HomeScroller.VerticalOffset < 0.5;

    /// <summary>
    /// Where a remote's focus starts on this screen: the first card of the quick selection, which is
    /// the first thing on the screen that plays something, and otherwise the first thing that can take
    /// the focus at all. Pegasus asks for it when home comes up from the keyboard (see
    /// PegasusChrome.DefaultFocusTarget).
    /// </summary>
    public Control? DefaultFocusTarget() =>
        Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(QuickPanels) as Control
        ?? Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(HomeContent) as Control;

    /// <summary>
    /// Space above the first row, inside the scroller, so the rows start below whatever stands over
    /// the top of this screen and scroll up under it. The scroller's bar starts there too, level with
    /// the first row and not at the top of the screen. It is 0 in the main window, where nothing
    /// stands over it. Pegasus sets it to clear the keys in its top corners (see
    /// PegasusChrome.HomeTopPad).
    /// </summary>
    public double ContentTop
    {
        get => _contentTop;
        set
        {
            _contentTop = value;
            ApplyContentTop();
        }
    }

    private double _contentTop;

    /// <summary>
    /// Writes <see cref="ContentTop"/> onto the content and onto the scroller's bar. It is called when
    /// the value is set and when the scroller loads or changes size, because the bar is a template
    /// part and is not there before the scroller has been laid out. Whichever comes last writes both.
    /// </summary>
    private void ApplyContentTop()
    {
        var m = HomeContent.Margin;
        if (Math.Abs(m.Top - _contentTop) > 0.5)
            HomeContent.Margin = new Thickness(m.Left, _contentTop, m.Right, m.Bottom);

        ScrollerBar.StartAt(HomeScroller, _contentTop);
    }

    /// <summary>The text sizes this screen is drawn at. Handed to every card the view builds; see
    /// <see cref="HomeTextSizes"/>.</summary>
    private readonly HomeTextSizes _textSizes = new();

    /// <summary>
    /// What every text size on this screen is multiplied by. It is 1 in the main window. Pegasus sets
    /// it to the figure its mini player is sized by, and sets it again when its window is resized (see
    /// PegasusChrome.SizeLibrary).
    /// </summary>
    public double TextScale
    {
        get => _textSizes.Scale;
        set
        {
            _textSizes.Scale = value;
            ApplyTextSizes();
        }
    }

    /// <summary>
    /// The sizes on the elements written out in the markup and not templated. The templates bind to
    /// <see cref="_textSizes"/> and follow it on their own; these are named, so they are written here.
    /// It is called from the constructor as well, so the markup's own values are never what is drawn.
    /// </summary>
    private void ApplyTextSizes()
    {
        QuickHeading.FontSize = _textSizes.Heading;
        ListsHeading.FontSize = _textSizes.Heading;
        QuickPlayAllMark.FontSize = _textSizes.PlayAllMark;
        QuickPlayAllText.FontSize = _textSizes.PlayAll;
        ListKeyLeft.Margin = _textSizes.RowKeyLeft;
        ListKeyRight.Margin = _textSizes.RowKeyRight;

        // The arrow keys outside the rows' template. The rows' own bind the same three figures.
        foreach (var key in new[] { ListKeyLeft, ListKeyRight, QuickKeyLeft, QuickKeyRight })
            SizeKey(key, _textSizes.KeyWidth, _textSizes.KeyHeight);

        // The banner, and the empty and locked panels, as whole blocks.
        FeatureScale.Factor = _textSizes.Scale;
        PanelScale.Factor = _textSizes.Scale;

        // The panels' least width grows with the text in them. Nothing else re-fits them on a change
        // of scale: the scroller's width, which is what does it otherwise, has not changed.
        SizeQuickPanels(QuickScroller.ActualWidth);
    }

    /// <summary>One arrow key: its box, and the arrow it holds, which is its whole content.</summary>
    private void SizeKey(Button key, double width, double height)
    {
        key.Width = width;
        key.Height = height;
        if (key.Content is FontIcon arrow)
            arrow.FontSize = _textSizes.KeyMark;
    }

    /// <summary>
    /// Whether the screen is up with the pictures above the fold in, or with none to wait for. These
    /// are the same two facts the main window's splash comes off on
    /// (<see cref="Services.Shell.FirstScreen"/>). Pegasus holds its opening cover on this (see
    /// PegasusChrome.Home), so the listener meets the screen finished and not filling in.
    /// </summary>
    /// <remarks>
    /// A host reads this first and listens to <see cref="Ready"/> only while it is false, so a screen
    /// that was ready before the host asked is not waited for. A reading that bails asks for no
    /// pictures and its fold never lands, which is what the second half is for.
    /// </remarks>
    public bool IsReady =>
        _screenShown
        && (Volatile.Read(ref _foldLeft) == 0
            || (_rows.Count == 0 && _listCards.Count == 0 && _features.Count == 0));

    /// <summary>Raised on the UI thread when <see cref="IsReady"/> becomes true. A later reading can
    /// raise it again; a host that is no longer waiting ignores it.</summary>
    public event Action? Ready;

    /// <summary>Asked from both ends, the screen coming up and the fold landing, because either can be
    /// last. It hops to the UI thread because the fold lands on whichever thread its last picture
    /// did, and the counts it reads belong to the UI thread.</summary>
    private void SayIfReady() => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsReady)
            Ready?.Invoke();
    });

    // The pictures, counted per group.

    /// <summary>
    /// One group of this screen's pictures, and whether they have all arrived.
    /// </summary>
    /// <remarks>
    /// <para><b>The count is per group and not one count for the screen</b>, because the groups are
    /// not the same question. The banner and the lists row are what is on screen when the splash
    /// lifts. The four rows of records are fifty squares below the fold that are meant to arrive one
    /// at a time. One count over all of them answers "is every picture in", which is a question
    /// nobody is waiting for (it can take about 6.9 s after the rows have their shape).</para>
    ///
    /// <para><b>The group holds a count of its own until <see cref="Close"/></b>, so the first
    /// picture to land cannot take the count to zero before the last has been started. Every picture
    /// gives its count back however it ends, thrown or cancelled included. A fill that keeps its
    /// count is a group that is never in, and a reveal that waits out its limit on every
    /// launch.</para>
    /// </remarks>
    private sealed class Coats(string name, Action<string> filled)
    {
        private int _outstanding = 1;   // the group's own, given back by Close
        private int _said;

        public void Add(Task picture)
        {
            Interlocked.Increment(ref _outstanding);
            _ = Landed(picture);

            async Task Landed(Task one)
            {
                try
                {
                    await one;
                }
                catch
                {
                    // Every fill already handles its own failure and leaves the card showing something.
                    // This is here for the count alone.
                }
                finally
                {
                    Drop();
                }
            }
        }

        /// <summary>Gives back the count the group held while it was being filled.</summary>
        public void Close() => Drop();

        private void Drop()
        {
            if (Interlocked.Decrement(ref _outstanding) != 0)
                return;

            if (Interlocked.Exchange(ref _said, 1) == 0)
                filled(name);
        }
    }

    /// <summary>Which groups have to be in before the screen counts as dressed: what is above the fold
    /// on any window this app opens at. The rows are reported and not waited for.</summary>
    private static readonly string[] AboveTheFold = ["banner", "lists"];

    /// <summary>Groups still to land before <see cref="Services.Shell.FirstScreen.Dressed"/> is said.
    /// It is counted down and not collected, because the only question asked of it is whether it is
    /// zero.</summary>
    private int _foldLeft = AboveTheFold.Length;

    /// <summary>
    /// One group of pictures is in.
    /// </summary>
    /// <remarks>
    /// Every group is reported on the home channel, because what each costs decides where the fold is
    /// drawn. Only the groups above the fold count the screen down towards dressed.
    /// </remarks>
    private void Dressed(string group)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said("home", $"pictures in: {group}");

        if (!AboveTheFold.Contains(group))
            return;

        if (Interlocked.Decrement(ref _foldLeft) != 0)
            return;

        App.Services.GetRequiredService<Services.Shell.FirstScreen>().Dressed();

        // ...and a rebuild waiting to be shown again. It uses TrySet because the wait may have given
        // up first, and because a group can land twice if a reading was overtaken.
        _dressed?.TrySetResult();

        // ...and a host waiting for the screen to be ready (see IsReady).
        SayIfReady();
    }

    // The covers coming in.

    /// <summary>
    /// One cover on this screen, faded up when its picture lands and not appearing with it.
    /// </summary>
    /// <remarks>
    /// <b>Every picture on this screen arrives after the card it belongs to.</b> The rows are drawn
    /// from a reading of the shelf, and the covers are fetched and decoded per record afterwards. A
    /// screen that assigns them straight to an <c>Image</c> shows a grid of empty squares filling in
    /// one by one, each in a single frame.
    ///
    /// <para>The clock is the album wall's own, <see cref="Common.SharpFade"/>. It is shared for the
    /// reason everything on a timer in this app is shared: the cost of a fade is not its arithmetic
    /// but the repaint it asks for, and one clock that writes every live fade in a single pass
    /// repaints the window once a beat whether one cover is arriving or forty. A whole screen of
    /// records coming in at once is exactly the case it was written for.</para>
    ///
    /// <para><b>The opacity starts at zero in the markup</b>, so the first thing drawn is the
    /// placeholder under it and not the picture at full strength. The watch fades UP only. A card
    /// whose picture never arrives keeps the placeholder, which is what it would have had
    /// anyway.</para>
    ///
    /// <para>It is wired on <c>Loaded</c> and not on the source changing, because <c>Watch</c> reads
    /// the layer once itself: a picture that landed before this element existed is faded from here
    /// and not missed. These rows are laid out by a StackPanel and are not recycled, so one call per
    /// element is the whole of it.</para>
    /// </remarks>
    private void Sleeve_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image sleeve)
            Common.SharpFade.Watch(sleeve);
    }

    // Hover, while the screen is moving.

    /// <summary>How long after the screen last moved before the rows answer the pointer again. It is
    /// short enough that a reader who stops to look at a card has it lit by the time they have looked
    /// at it, and long enough to cover the gap between two notches of a slow spin.</summary>
    private static readonly TimeSpan HoverBack = TimeSpan.FromMilliseconds(160);

    private DispatcherTimer? _hoverBack;

    /// <summary>
    /// Takes the rows' hover away for as long as the screen is moving, and gives it back when it
    /// stops.
    /// </summary>
    /// <remarks>
    /// <b>A scroll under the pointer is a hover event per card per frame.</b> Every card is a Button
    /// with a PointerOver background and three tooltips on it, so dragging the whole screen past a
    /// stationary cursor enters and leaves that state on card after card. Each one is a visual-state
    /// transition and a repaint, on the thread that is also drawing the scroll. On the WinUI head, one
    /// notch with the pointer over a cover overran 125 ms with the thread running and nothing of ours
    /// inside it. The same notch in the gap between two covers costs nothing.
    ///
    /// <para>So nothing under the scroller answers the pointer while it moves. What the reader loses
    /// is the lit card under the cursor for the length of the scroll, which they cannot look at while
    /// it is moving anyway.</para>
    ///
    /// <para>It is hung off <c>ViewChanged</c> and not off the wheel, so it covers every way the
    /// screen moves: the wheel, the bar, the keyboard, and the screen being put back where it was.
    /// It also has to be: with the content not answering the pointer, the next notch never reaches the
    /// row's own handler, so the wheel could not re-arm this even if it wanted to.</para>
    /// </remarks>
    private void HomeScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Only a real move: ViewChanged also arrives for a view that ended where it was.
        var moved = HomeScroller.VerticalOffset - _scrolledAt;
        if (Math.Abs(moved) > 0.5)
        {
            _scrolledAt = HomeScroller.VerticalOffset;
            Scrolled?.Invoke(moved);
        }

        HomeContent.IsHitTestVisible = false;

        _hoverBack ??= new DispatcherTimer { Interval = HoverBack };
        _hoverBack.Stop();
        _hoverBack.Tick -= HoverBack_Tick;
        _hoverBack.Tick += HoverBack_Tick;
        _hoverBack.Start();
    }

    private void HoverBack_Tick(object? sender, object e)
    {
        _hoverBack?.Stop();
        HomeContent.IsHitTestVisible = true;
    }

    // The wheel over a row.

    /// <summary>The rows whose wheel has been taken over. A row is loaded more than once (the
    /// templated ones are rebuilt on every reading of the shelf and reloaded on every scroll back into
    /// view), and a second handler on one element would move the screen twice per notch.</summary>
    private readonly HashSet<FrameworkElement> _wheelHeld = [];

    /// <summary>
    /// Takes the wheel away from one row, so a notch over it moves the screen instead of the row.
    /// </summary>
    /// <remarks>
    /// A row scrolls sideways and the screen scrolls down, and a scroller that can only go sideways
    /// turns a vertical notch into a sideways one. Without this, reading DOWN the home screen with
    /// the pointer over a row of records would drag that row along. The rows are stepped by their two
    /// keys and by nothing else.
    ///
    /// <para>It is wired on the row's CONTENT and not on its scroller, which is what makes it early
    /// enough. The notch bubbles from the card the pointer is over, through the content, through the
    /// scroll presenter, to the scroller. Every one of those scrollers is subscribed without
    /// <c>handledEventsToo</c>, so a notch marked handled at the content reaches none of them. A
    /// handler on the scroller itself would run after its own class handler on the WinUI head, and
    /// the row would have moved before this could say anything about it.</para>
    /// </remarks>
    private void HoldTheWheel(FrameworkElement? row)
    {
        if (row is null || !_wheelHeld.Add(row))
            return;

        row.PointerWheelChanged += Row_PointerWheel;
    }

    /// <summary>
    /// One notch over a row: the row holds still and the screen scrolls by it.
    /// </summary>
    /// <remarks>
    /// The screen is moved BY HAND because nothing else will. The notch has to be marked handled to
    /// keep it off the row, and a handled notch never reaches the screen's own scroller either. So
    /// this is the whole of what happens to it.
    ///
    /// <para>It is handled whatever comes of it, the ends included. A screen already at its foot that
    /// let the notch through would hand it to the row, which is the fault this exists to stop, and it
    /// would only show up at the bottom of a long collection.</para>
    /// </remarks>
    private void Row_PointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.Handled)
            return;

        var delta = e.GetCurrentPoint(HomeScroller).Properties.MouseWheelDelta;
        e.Handled = true;

        var room = HomeScroller.ScrollableHeight;
        if (delta == 0 || room <= 0.5)
            return;

        // Capped at a screenful per notch. This wheel reports 120, 360 and 1080, so a fast spin
        // arrives as one large delta and not as several, and a screen that crosses its whole length on
        // one of them is not scrolling.
        var viewport = HomeScroller.ViewportHeight;
        var step = Math.Clamp(WheelStep(delta), -viewport, viewport);

        HomeScroller.ChangeView(
            null, Math.Clamp(HomeScroller.VerticalOffset - step, 0, room), null, disableAnimation: true);
    }

    /// <summary>
    /// How far one delta asks the screen to travel, in layout points.
    /// </summary>
    /// <remarks>
    /// A delta does not mean the same thing on the two platforms, and nothing in the framework
    /// normalises it. Windows sends multiples of 120, one detent of a wheel each. The macOS host
    /// passes <c>NSEvent.scrollingDeltaY</c> through untouched, which is already the distance to
    /// travel. Reading one as the other is silent and comes out about fifty times wrong. The same
    /// distinction is written out at length in <c>AlbumListView.WheelUnit</c>.
    ///
    /// <para>The step is three lines of sixteen points to a detent, which is what a wheel notch moves
    /// a page by anywhere else on this machine. A screen is being read here and not a list of songs,
    /// so the line is a page's and not the tracklist's own.</para>
    /// </remarks>
    private static double WheelStep(int delta) =>
        OperatingSystem.IsMacOS() ? delta : delta / 120.0 * LinesPerNotch * PageLine;

    private const double LinesPerNotch = 3;

    private const double PageLine = 16;

    /// <summary>The row moved: its two hems, and the open drawer's triangle, which points at a card
    /// that has just moved under it. Both are read from the scroller as it stands and not from the
    /// move.</summary>
    private void Row_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        ShowRowHems(sender);
        PointAtOpenCard();
    }

    /// <summary>The cards arrived, which decides whether the row overflows at all. The scroller's own
    /// size did not change and its ViewChanged says nothing about it, so the row is asked again from
    /// the one thing that did change.
    ///
    /// <para>The scroller is found by walking up, and not by asking for the Parent: what holds a
    /// ScrollViewer's content is the presenter inside its template, not the scroller itself.</para></summary>
    private void RowCards_SizeChanged(object sender, SizeChangedEventArgs e) => Fitting(PartRowHems, sender, () =>
    {
        if (Scroller(sender as DependencyObject) is { } scroller)
            ShowRowHems(scroller);

        // The cards were re-measured, so the stride the triangle is placed by has changed under it.
        // The size slider does this, and it does it while a record is open.
        PointAtOpenCard();
    });

    /// <summary>The scroller an element is inside, or null where it is in none.</summary>
    private static ScrollViewer? Scroller(DependencyObject? from)
    {
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer scroller)
                return scroller;

        return null;
    }

    /// <summary>One row's two hems and its two keys, from one reading of the scroller. They say the
    /// same thing (there are records past this end), and reading it twice is how the two come to
    /// disagree.</summary>
    private void ShowRowHems(object sender)
    {
        if (sender is not ScrollViewer scroller)
            return;

        var (head, foot) = ScrollHem.Depths(
            scroller.HorizontalOffset, scroller.ScrollableWidth, RowHemDepth);
        HemsFor(scroller)?.Show(head, foot);

        if (scroller.Parent is Grid cut && cut.Parent is Grid band)
            ShowKeys(Hem(band, "left"), Hem(band, "right"), head, foot);
    }

    /// <summary>A key is up while the end beside it has records past it. It is told in the same terms
    /// as the hem, so a row can never show a key that scrolls nowhere.</summary>
    private static void ShowKeys(UIElement? left, UIElement? right, double head, double foot)
    {
        if (left is not null)
            left.Visibility = head > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (right is not null)
            right.Visibility = foot > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>How deep a row's two hems are at full stretch: twice what a list's are. A cover is 160
    /// points wide where a list line is 20 high, so the soft end that reads well down a tracklist
    /// reads as a smudge on the edge of a record. See <see cref="ScrollHem.Depth"/>.</summary>
    internal const double RowHemDepth = 48;

    /// <summary>The widest cover a row draws, which is what a key's step leaves on screen. It follows
    /// the same measurement the cards are sized by; see SimilarTileMetrics.</summary>
    internal const double WidestSleeve = 160;

    /// <summary>The least a key may move a row. A pane narrower than one cover would otherwise take a
    /// step of nothing, and the key would be a control that does not work.</summary>
    internal const double LeastStep = 80;

    /// <summary>
    /// How far one press of a key moves a row: a viewport, less one cover.
    ///
    /// <para>It is less one cover so that the record that was at the edge stays on screen, which keeps
    /// the reader's place across the move. A step of a whole viewport puts every record they were
    /// looking at behind them at once.</para>
    /// </summary>
    internal static double KeyStep(double viewport) => Math.Max(viewport - WidestSleeve, LeastStep);

    private static void Nudge(ScrollViewer? row, int direction)
    {
        if (row is null || row.ActualWidth <= 0)
            return;

        row.ChangeView(row.HorizontalOffset + (direction * KeyStep(row.ActualWidth)), null, null);
    }

    /// <summary>The scroller a row's key belongs to. The key is a sibling of the cut the scroller is
    /// in and not a child of it, so it is found through the row and not by walking up.</summary>
    private static ScrollViewer? RowFor(object sender)
        => (sender as FrameworkElement)?.Parent is Grid band && Hem(band, "cut") is Grid cut
            ? cut.Children.OfType<ScrollViewer>().FirstOrDefault()
            : null;

    private void RowLeft_Click(object sender, RoutedEventArgs e) => Nudge(RowFor(sender), -1);

    private void RowRight_Click(object sender, RoutedEventArgs e) => Nudge(RowFor(sender), 1);

    private void ListLeft_Click(object sender, RoutedEventArgs e) => Nudge(ListScroller, -1);

    private void ListRight_Click(object sender, RoutedEventArgs e) => Nudge(ListScroller, 1);

    /// <summary>Every row on the screen, plus the generated lists' row. A fresh reading of the shelf
    /// is followed by this, since none of the rows have been laid out at the moment they are
    /// filled.</summary>
    private void ShowAllHems()
    {
        // A reading of the shelf rebuilds every row, and a rebuilt row is a new scroller. The ones that
        // went with the old rows are dropped here and not kept for the life of the window.
        foreach (var dead in _hems.Keys.Where(s => !s.IsLoaded).ToList())
            _hems.Remove(dead);

        // ...and the rows whose wheel was taken, by the same rule and for the same reason: a reading
        // replaces every templated row, and the set would otherwise hold every row this window has
        // ever drawn. The lists' row is written out and not templated and stays loaded, so it stays.
        _wheelHeld.RemoveWhere(row => !row.IsLoaded);

        foreach (var hem in _hems.Values)
            hem.Show();

        ShowListHems();
        ShowQuickHems();
    }

    private ScrollHem? _listHem;

    private void ShowListHems()
    {
        if (_listHem is null && !_noHems)
        {
            _listHem = ScrollHem.Attach(
                ListScroller, ListCut, ListHemHead, ListHemFoot, Orientation.Horizontal, RowHemDepth);
            _noHems = _listHem is null;
        }

        var (head, foot) = ScrollHem.Depths(
            ListScroller.HorizontalOffset, ListScroller.ScrollableWidth, RowHemDepth);
        _listHem?.Show(head, foot);
        ShowKeys(ListKeyLeft, ListKeyRight, head, foot);
    }

    /// <summary>One row's hems, built on the first ask. The three elements they are made of live in a
    /// DataTemplate, so the only way to find them from here is through the row they are in.</summary>
    private ScrollHem? HemsFor(ScrollViewer row)
    {
        if (_hems.TryGetValue(row, out var made))
            return made;
        if (_noHems)
            return null;

        if (row.Parent is not Grid cut || cut.Parent is not Grid band)
            return null;
        if (Hem(band, "head") is not { } head || Hem(band, "foot") is not { } foot)
            return null;

        if (ScrollHem.Attach(row, cut, head, foot, Orientation.Horizontal, RowHemDepth) is not { } hems)
        {
            _noHems = true;
            return null;
        }

        _hems[row] = hems;
        return hems;
    }

    /// <summary>One of the two hems of a row, which are tagged and not named: they live in a
    /// DataTemplate, so the only way to find them from here is through the row they are in. The album
    /// list finds its tracklist's hems in the same way.</summary>
    private static FrameworkElement? Hem(Grid band, string tag)
    {
        foreach (var child in band.Children)
        {
            if (child is FrameworkElement hem && hem.Tag as string == tag)
                return hem;
        }

        return null;
    }

    // The quick selection.

    /// <summary>What the four panels hold, in the order they are laid out. It is rebuilt on every
    /// reading of the shelf, like everything else on this screen.</summary>
    private readonly List<HomeQuickCard> _quick = [];

    /// <summary>The four panels' own sources, one per column. There are four collections and not one
    /// with a wrapping panel, because the layout is four LISTS: the four most played go down the first
    /// column, and a wrap panel would put them across the top. See QuickPicks.InPanels.</summary>
    private readonly ObservableCollection<HomeQuickCard>[] _panels =
        [.. Enumerable.Range(0, QuickPicks.Panels).Select(_ => new ObservableCollection<HomeQuickCard>())];

    /// <summary>
    /// Reads what the listener plays most, of all four kinds, and builds the cards for it.
    /// </summary>
    /// <remarks>
    /// Which entries are chosen and in which order is the answer of <see cref="QuickPicks.Choose"/>,
    /// not of this view. The reason the four kinds are dealt round-robin and not ranked against each
    /// other is written there. What is done here is the looking up.
    ///
    /// <para>It runs on a worker, because two of the four answers are queries. The stations are
    /// handed in: their log is not in the mirror at all (see <see cref="RadioListens"/>) and is read
    /// on the UI thread with the banner's, which is where the radio service can be asked for.</para>
    ///
    /// <para>Nothing is read on a borrowed shelf's own terms: the plays there are the borrower's few
    /// plays of somebody else's records, which is a question about nobody. The listening profile
    /// keeps the same rule.</para>
    /// </remarks>
    private static List<HomeQuickCard> ReadQuickPicks(
        LibraryDatabase db,
        bool peer,
        string root,
        IReadOnlyList<RadioListen> stations,
        IReadOnlyDictionary<string, LibraryDatabase.AlbumFactsRow> names,
        IReadOnlyList<HomeListCard> lists,
        SimilarTileMetrics tiles)
    {
        if (peer)
            return [];

        // Each kind is asked for the whole selection's worth, not a quarter of it. A collection with
        // no stations and no lists fills all sixteen from songs and records, and a source asked for
        // four could not.
        var songs = db.QueryMostPlayedTracks(QuickPicks.Count);
        var played = db.QueryMostPlayedPlaylists(QuickPicks.Count);

        // The records come from the same reading of the shelf the rows are drawn from, so the quick
        // selection and the Most played row cannot disagree about which record that is.
        var albums = HomeShelf.Played([.. names.Values], QuickPicks.Count);

        var byKey = new Dictionary<string, HomeListCard>(StringComparer.Ordinal);
        foreach (var list in lists)
            if (KeyOf(list.Door) is { Length: > 0 } key)
                byKey[key] = list;

        // A played list this screen has no card for is stepped past and not drawn bare. The card is
        // where the name, the colour and the mark come from, and a list that has been deleted since it
        // was played has none of them.
        var playlists = played.Select(r => r.Key).Where(byKey.ContainsKey).ToList();

        // A radio on the song played most, and on the next one by somebody else. See QuickPicks.RadioSeeds.
        var radios = QuickPicks.RadioSeeds(
            [.. songs.Select(t => (t.RelativePath, RecommendRefusals.CreditOf(t.AlbumArtist, t.Artist)))]);

        var picks = QuickPicks.Choose(
            [.. songs.Select(t => t.RelativePath)],
            albums,
            playlists,
            [.. stations.Select(l => l.Station.Uuid)],
            QuickPicks.Count,
            radios);

        var byPath = songs.ToDictionary(t => t.RelativePath, t => t, StringComparer.OrdinalIgnoreCase);
        var byUuid = stations.ToDictionary(l => l.Station.Uuid, l => l.Station, StringComparer.OrdinalIgnoreCase);

        var cards = new List<HomeQuickCard>(picks.Count);
        foreach (var pick in picks)
        {
            switch (pick.Kind)
            {
                case QuickPickKind.Song when byPath.TryGetValue(pick.Key, out var song):
                    cards.Add(new HomeQuickCard
                    {
                        Kind = pick.Kind,
                        Key = pick.Key,
                        Title = song.Title ?? song.FileName,
                        Subtitle = song.Artist ?? song.AlbumArtist ?? "",
                        Folder = song.Folder,
                        Track = song.ToTrackInfo(root),
                        Glyph = "\uf001",          // a note
                        Metrics = tiles,
                    });
                    break;

                case QuickPickKind.Radio when byPath.TryGetValue(pick.Key, out var seed):
                    // Named for the song it starts from, with the kind first on the line under it, so it reads as
                    // a radio and not as the song's own entry further down.
                    cards.Add(new HomeQuickCard
                    {
                        Kind = pick.Kind,
                        Key = pick.Key,
                        Title = seed.Title ?? seed.FileName,
                        Subtitle = KindLine("Home_QuickRadio", seed.Artist ?? seed.AlbumArtist),
                        Folder = seed.Folder,
                        Track = seed.ToTrackInfo(root),
                        Glyph = "",          // the radio, as on Start radio in the song menus
                        Metrics = tiles,
                    });
                    break;

                case QuickPickKind.Album when names.TryGetValue(pick.Key, out var fact):
                    var parts = pick.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    cards.Add(new HomeQuickCard
                    {
                        Kind = pick.Kind,
                        Key = pick.Key,
                        Title = fact.Album ?? (parts.Length > 0 ? parts[^1] : pick.Key),
                        Subtitle = KindLine("Home_QuickAlbum",
                                            fact.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : "")),
                        Folder = pick.Key,
                        Glyph = "\uf51f",          // a record
                        Metrics = tiles,
                    });
                    break;

                case QuickPickKind.Playlist when byKey.TryGetValue(pick.Key, out var list):
                    cards.Add(new HomeQuickCard
                    {
                        Kind = pick.Kind,
                        Key = pick.Key,
                        Title = list.Name,
                        Subtitle = KindLine("Home_QuickPlaylist", list.Caption),
                        Door = list.Door,
                        Glyph = list.Glyph,
                        InkColor = list.InkColor,
                        Metrics = tiles,
                    });
                    break;

                case QuickPickKind.Station when byUuid.TryGetValue(pick.Key, out var station):
                    cards.Add(new HomeQuickCard
                    {
                        Kind = pick.Kind,
                        Key = pick.Key,
                        Title = station.Display,
                        Subtitle = Loc.Get("Home_QuickStation"),
                        Station = station,
                        Glyph = "\uf8d7",          // the radio
                        InkColor = SoftFigure.RingColours(station.Display) is [var first, ..]
                            ? first
                            : Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
                        Metrics = tiles,
                    });
                    break;
            }
        }

        return cards;
    }

    /// <summary>The line under an album's or a playlist's name in the quick selection: the kind, then
    /// the artist or the list's caption ("Album • Sting", "Playlist • 24 songs"). So a record and a
    /// list are told apart from a song, which keeps the artist alone. The kind alone where nothing
    /// follows it. The phone writes the same line.</summary>
    private static string KindLine(string kindKey, string? rest) =>
        string.IsNullOrWhiteSpace(rest)
            ? Loc.Get(kindKey)
            : Loc.Format("Home_QuickKindJoin", Loc.Get(kindKey), rest);

    /// <summary>How a list card is keyed in the play log. This is the one place the two spellings
    /// meet, so a generated list counted under one name cannot be looked up under another.</summary>
    private static string KeyOf(HomeListDoor door) =>
        door.Generated is { } id ? LibraryDatabase.PlaylistKey(id)
        : door.Kept is { } node ? node.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "";

    /// <summary>Puts the selection up, cut into its panels. A panel with nothing in it is not drawn at
    /// all, and a selection with nothing in it takes the whole section down: a heading over an empty
    /// block is a heading over a question the collection cannot answer yet.</summary>
    private void ShowQuickPicks(IReadOnlyList<HomeQuickCard> cards)
    {
        _quick.Clear();
        _quick.AddRange(cards);

        var panels = QuickPicks.InPanels(
            [.. cards.Select(c => new QuickPick(c.Kind, c.Key))], QuickPicks.Tall);

        var at = 0;
        for (var i = 0; i < _panels.Length; i++)
        {
            _panels[i].Clear();

            var take = i < panels.Count ? panels[i].Count : 0;
            for (var j = 0; j < take; j++)
                _panels[i].Add(_quick[at++]);
        }

        QuickBlock.Visibility = _quick.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Back to the first panel. A reading replaces all sixteen entries, so an offset kept from the
        // one before is a scroll position into a selection that no longer exists.
        QuickScroller.ChangeView(0, null, null, disableAnimation: true);

        // How many panels have anything in them decides their width, and it has just changed. This is
        // free where the block was already up at this width: every write here writes what is already
        // there, and the framework drops a write that changes nothing.
        FitQuick();

        // The button says what it does, and it does nothing where the selection holds no songs. That
        // is a collection whose records have been played but whose individual songs have not.
        QuickPlayAll.Visibility =
            SongsIn(_quick).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The songs in the selection, in the order shown. A radio entry carries its song too, but it is an
    /// offer to start a radio and not a song in the selection: the same song has its own entry when it is there.</summary>
    private static List<RPM.Core.Models.TrackInfo> SongsIn(IEnumerable<HomeQuickCard> cards) =>
        [.. cards.Where(c => c.Kind == QuickPickKind.Song).Select(c => c.Track).OfType<RPM.Core.Models.TrackInfo>()];

    /// <summary>One entry pressed: what it opens depends on what it is, and a song is the only one
    /// that PLAYS and does not open. It uses the same four doors as the rest of this screen, so an
    /// album reached from here arrives where an album reached from a row does.</summary>
    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HomeQuickCard card)
            return;

        switch (card.Kind)
        {
            case QuickPickKind.Song when card.Track is { } track:
                // Into the now playing queue and started, which is what pressing a song anywhere else
                // in this app does. It is not a bare Play: a track played around the queue leaves the
                // queue holding something that is not playing.
                Amp?.AddTracks([track], play: true);
                break;

            case QuickPickKind.Radio when card.Track is { } seed:
                // The same as Start radio in a song's menu: the song plays now, and the queue fills with songs
                // like whichever one is playing.
                Amp?.StartRadio(seed);
                break;

            case QuickPickKind.Album when card.Folder is { Length: > 0 } folder:
                AlbumRequested?.Invoke(this, folder);
                break;

            case QuickPickKind.Playlist when card.Door is { } door:
                if (door.Generated is { } id)
                    PlaylistRequested?.Invoke(this, id);
                else if (door.Kept is { } node)
                    StoredPlaylistRequested?.Invoke(this, node);
                break;

            case QuickPickKind.Station when card.Station is { } station:
                StationRequested?.Invoke(this, station);
                break;
        }
    }

    /// <summary>
    /// "Play all tracks": the songs in the selection, in the order they are shown, into the now
    /// playing queue.
    /// </summary>
    /// <remarks>
    /// <b>Only the songs.</b> The records, the lists and the stations are stepped past. That is also
    /// the only reading that can be acted on without inventing an order: a record is a dozen songs
    /// and a station is not a set of songs at all, so "everything here" would be a queue whose length
    /// depended on what happened to be in the selection.
    ///
    /// <para>The whole TrackInfo is already on each card, so this reads nothing and asks nothing: the
    /// queue is filled from what is on screen.</para>
    /// </remarks>
    private void QuickPlayAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = SongsIn(_quick);
        if (tracks.Count == 0)
            return;

        Amp?.AddTracks(tracks, play: true);
    }

    /// <summary>The now playing queue, or null where there is none to ask (the design-time tree).</summary>
    private static Services.Shell.RPaMpService? Amp
    {
        get
        {
            try
            {
                return App.Services.GetRequiredService<Services.Shell.RPaMpService>();
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>One entry's square, through the door its kind reads pictures by. Everything after the
    /// bytes follows the rows' own arrangement: CoverFill, so the card is told there is NO picture as
    /// well as told there is one, and the decode through this screen's one paced gate.</summary>
    private async Task FillQuickCoverAsync(HomeQuickCard card, CancellationToken ct)
    {
        try
        {
            // A station is dressed, not just decoded. Its entry shows the same three things as the
            // stations pane's row: the mark, the generated ground under it and the fitting rule. They
            // arrive as one answer, so it does not go through the cover path at all. See
            // FillQuickStationAsync.
            if (card is { Kind: QuickPickKind.Station, Station: { } station })
            {
                await FillQuickStationAsync(card, station, ct);
                return;
            }

            await Services.Art.CoverFill.DressAsync(
                card,
                token => QuickCoverAsync(card, token),
                async bytes => await _pictures.Next(() => RemoteImage.DecodeSmoothAsync(
                    bytes, QuickDecodeEdge(bytes), CutFor(card))),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An entry without a picture still names what it is, which is most of what it is for.
        }
    }

    /// <summary>
    /// One station entry's square, dressed exactly as the stations pane dresses its row.
    ///
    /// <para><b>It is the same answer, not the same rules applied twice.</b>
    /// <c>RadioStationItem.LogoForAsync</c> is the row's own dressing, cached per station: the winning
    /// mark decoded, the generated figure drawn under it where it has nothing of its own, and the
    /// shape read from the bytes. Asking for it here costs nothing where the pane has already asked,
    /// and it makes one station one badge on both screens, which is the whole point.</para>
    ///
    /// <para><b>It does not go through this screen's paced gate,</b> which every other picture here
    /// goes through. The gate is a queue: one entry holds the next until it is done. The cover path
    /// fetches OUTSIDE the gate and gates only the decode, and there is no such seam here, because
    /// the walk and the decode are one call, cached together. If it were put in the queue, one
    /// station's walk through half a dozen dead URLs would hold every cover on the screen behind it.
    /// What is skipped is a small decode that is usually already done, because the stations pane
    /// asked for it first.</para>
    ///
    /// <para>No picture gives the same verdict as the cover path: the entry is TOLD there is none, and
    /// falls back to the radio glyph in its own colour. A walk that merely failed is not told apart
    /// from a station that has nothing. The walk itself does not keep a failure, so the next read
    /// asks again.</para>
    /// </summary>
    private async Task FillQuickStationAsync(
        HomeQuickCard card, RadioStation station, CancellationToken ct)
    {
        var found = await RadioStationItem.LogoForAsync(station);
        if (ct.IsCancellationRequested)
            return;

        if (found.Picture is null)
        {
            if (Services.Art.CoverTier.Latches(card.HasCover))
                card.CoverMissing = true;
            return;
        }

        card.WearMark(found);
    }

    /// <summary>
    /// The edge one entry's square is cut to, in physical pixels: the size it is DRAWN at, not a
    /// bucket.
    ///
    /// <para>The ladder in <see cref="ArtScaler.DecodeEdgeFor"/> starts at 128 and this square is 48.
    /// Using the ladder would leave the renderer shrinking a 128 px picture into a 48 px box on every
    /// frame, a plain bilinear read at nearly three to one, which shows as grain. The square moves
    /// only with the text scale (see <see cref="HomeTextSizes.QuickSquare"/>), which Pegasus changes
    /// when its window is resized, so cutting to the exact size costs no re-decode and is the
    /// smallest the picture can be held at. A square already cut keeps its size until the next
    /// reading.</para>
    ///
    /// <para>The edge is widened where a picture is not square, because the square crops with
    /// UniformToFill and what fills it is the SHORT side. See
    /// <see cref="ArtScaler.LongEdgeForShortSide"/>.</para>
    /// </summary>
    private int QuickDecodeEdge(byte[] bytes)
    {
        var square = (int)Math.Ceiling(_textSizes.QuickSquare * (XamlRoot?.RasterizationScale ?? 1));
        var (w, h) = ArtScaler.BoundsOf(bytes);
        return ArtScaler.LongEdgeForShortSide(w, h, square);
    }

    /// <summary>Which rule the cut is sampled by, which the reduction factor cannot say: a generated
    /// list shows a picture this app drew, and a song and a record show photographs. It is never asked
    /// about a station, which does not come through here at all. See <see cref="RemoteImage.Cut"/>,
    /// where both answers are written out.</summary>
    private static RemoteImage.Cut CutFor(HomeQuickCard card)
        => card is { Kind: QuickPickKind.Playlist, Door: { Generated: not null } }
            ? RemoteImage.Cut.Drawing
            : RemoteImage.Cut.Photograph;

    /// <summary>Where one entry's picture comes from, by kind: a record's folder for a song and a
    /// record, the list's own picture for a list. Each is the door that kind is read by everywhere
    /// else in the app. A station is not here; it is dressed and not decoded, by
    /// <see cref="FillQuickStationAsync"/>.</summary>
    private Task<byte[]?> QuickCoverAsync(HomeQuickCard card, CancellationToken ct) => card.Kind switch
    {
        QuickPickKind.Song or QuickPickKind.Album or QuickPickKind.Radio =>
            CoverLoader is { } load && card.Folder is { Length: > 0 } folder
                ? load(folder, ArtScaler.TileEdge, ct)
                : Task.FromResult<byte[]?>(null),

        QuickPickKind.Playlist when card.Door is { } door => ListCoverAsync(door, card.Title, ct),

        // No station here. A station's square is not a picture this screen decodes. It is the row's
        // whole dressing: see FillQuickStationAsync, which never reaches this.
        _ => Task.FromResult<byte[]?>(null),
    };

    /// <summary>
    /// How wide one panel is drawn, which is also how many of them are on screen at once.
    /// </summary>
    /// <remarks>
    /// An entry is a square and two lines of text, so a panel narrower than <see cref="PanelLeast"/>
    /// is a column of trimmed names. The pane is divided by as many panels as it has room for at that
    /// width, and the rest stand past the right end, reached by the two keys. A narrow pane shows
    /// fewer panels, not fewer entries.
    ///
    /// <para>Widths are in POINTS and not stars, because the panels are inside a scroller and a
    /// scroller measures its content with no width limit. A star column given no limit takes its own
    /// content's width, which would size each panel from the longest name in it.</para>
    ///
    /// <para>The entries themselves are not dealt again: panel 0 holds the same four whatever is shown
    /// beside it. Dealing again on every resize would rearrange the whole block under a reader who is
    /// dragging a window edge.</para>
    /// </remarks>
    /// <param name="viewport">The scroller's own width, not the panels'. The panels are as wide as
    /// this says to make them, so reading the width back from them would be reading this answer.</param>
    private void SizeQuickPanels(double viewport)
    {
        if (viewport <= 0)
            return;

        var filled = _panels.Count(p => p.Count > 0);
        if (filled == 0)
            return;

        var gap = QuickPanels.Spacing;

        // How many fit at PanelLeast each, with the space between them counted. Never more than there
        // are: a wide pane divides its width between the panels that exist, not between four slots.
        var least = PanelLeast * _textSizes.Scale;
        var room = Math.Clamp((int)((viewport + gap) / (least + gap)), 1, filled);

        // Floored, so what is shown can never add up to a fraction of a point MORE than the viewport.
        // That fraction is a scroll of nothing, and both hems and both keys would report it as panels
        // past the end.
        var panel = Math.Floor((viewport - (gap * (room - 1))) / room);

        for (var i = 0; i < _panels.Length; i++)
        {
            var wanted = _panels[i].Count > 0;
            PanelAt(i).Width = panel;
            PanelAt(i).Visibility = wanted ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ItemsControl PanelAt(int at) => at switch
    {
        0 => QuickPanel0,
        1 => QuickPanel1,
        2 => QuickPanel2,
        _ => QuickPanel3,
    };

    /// <summary>The panels fitted to the pane again and the hems re-read, in that order. The hems are
    /// a statement about how much is past each end, and the widths written above decide it.</summary>
    private void FitQuick()
    {
        SizeQuickPanels(QuickScroller.ActualWidth);
        ShowQuickHems();
    }

    /// <summary>The selection's two hems and its two keys, from one reading of the scroller, in the
    /// same way as the rows of records below. They are built on the first ask, like the lists'.</summary>
    private void ShowQuickHems()
    {
        if (_quickHem is null && !_noHems)
        {
            _quickHem = ScrollHem.Attach(
                QuickScroller, QuickCut, QuickHemHead, QuickHemFoot, Orientation.Horizontal, RowHemDepth);
            _noHems = _quickHem is null;
        }

        var (head, foot) = ScrollHem.Depths(
            QuickScroller.HorizontalOffset, QuickScroller.ScrollableWidth, RowHemDepth);
        _quickHem?.Show(head, foot);
        ShowKeys(QuickKeyLeft, QuickKeyRight, head, foot);
    }

    private ScrollHem? _quickHem;

    /// <summary>
    /// One press of a key: the selection moves by exactly one panel.
    /// </summary>
    /// <remarks>
    /// The step is a whole panel and not the rows' step of a viewport less one cover. The panels are
    /// columns on a fixed stride, so a step of one keeps every panel on the pane's own left edge
    /// however many presses in the reader is. A step of anything else leaves a column of names cut
    /// down its middle at the edge of the pane.
    ///
    /// <para>The stride is MEASURED at the press and is not written down when the panels are fitted.
    /// A stride kept in a field is true until the next resize, and no ordering guarantees that a
    /// fitting has run before a press. The panel's own width is true whenever it is asked.</para>
    /// </remarks>
    private void NudgeQuick(int direction)
    {
        if (QuickPanel0.ActualWidth <= 0)
            return;

        var stride = QuickPanel0.ActualWidth + QuickPanels.Spacing;
        QuickScroller.ChangeView(
            QuickScroller.HorizontalOffset + (direction * stride), null, null);
    }

    private void QuickLeft_Click(object sender, RoutedEventArgs e) => NudgeQuick(-1);

    private void QuickRight_Click(object sender, RoutedEventArgs e) => NudgeQuick(1);

    /// <summary>The narrowest a panel is worth drawing at a text scale of 1. Below it the names are
    /// trimmed to nothing, and a column of them says less than three columns would. It is multiplied
    /// by the text scale where it is used, because larger names need a wider panel to say as
    /// much.</summary>
    private const double PanelLeast = 210;

    // The featured banner.

    /// <summary>
    /// Pixels per point of the banner as it is drawn: the display's scale times the ScaleBox's
    /// (<c>FeatureScale</c>), which draws the whole banner larger in Pegasus. The banner's cover and
    /// the two marks are decoded at this, so a picture is not drawn larger than it was cut for.
    /// </summary>
    /// <remarks>It is read when the picture is decoded. A picture already decoded keeps its size until
    /// the card is dressed again, so resizing Pegasus's window leaves those a little soft until
    /// then.</remarks>
    private double BannerPixels => (XamlRoot?.RasterizationScale ?? 1) * _textSizes.Scale;

    /// <summary>The cards the banner steps through, and which of them is in front. It is rebuilt on
    /// every reading of the shelf, so a record that arrived since is in the strip and one that was
    /// deleted is not.</summary>
    private readonly List<HomeFeatureCard> _features = [];

    private int _feature;

    /// <summary>Puts a new strip up, from the front. It does not keep the card that was up before the
    /// reload: the strip is a different strip, and "the third card" is not a thing a reader was
    /// looking at.</summary>
    private void ShowFeatures(IReadOnlyList<HomeFeatureCard> cards)
    {
        _features.Clear();
        _features.AddRange(cards);

        FeatureScale.Visibility = _features.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Both keys, together: a strip with one card in it has nothing to step to in either direction.
        var steppable = _features.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        FeatureBack.Visibility = steppable;
        FeatureOn.Visibility = steppable;

        BuildDots();
        ShowFeature(0);
        SizeFeatures(FeatureBand.ActualWidth);

        // What the tuner is announcing, put onto the new strip. It is asked here as well as from the
        // tuner's own event, because a strip built while a station is already on has had no event to
        // hear: the song playing when the screen opened was announced before there was a card to put
        // it on.
        ShowWhatIsOn();

        // A strip with one card in it has nothing to step to, and a reading can turn a strip of five
        // into one of one. It is asked here and not assumed, so the timer's state follows the strip's.
        Rewind();
    }

    /// <summary>The card in front, and the two behind it. It is asked for by index and wrapped both
    /// ways: the strip is a ring, so the key at the end of it steps to the beginning and does not
    /// stop.</summary>
    private void ShowFeature(int index)
    {
        if (_features.Count == 0)
        {
            PlateHost.Content = null;
            PeekLeftHost.Content = null;
            PeekRightHost.Content = null;
            return;
        }

        var count = _features.Count;
        _feature = ((index % count) + count) % count;

        PlateHost.Content = _features[_feature];
        PeekLeftHost.Content = count > 1 ? _features[((_feature - 1) % count + count) % count] : null;
        PeekRightHost.Content = count > 1 ? _features[(_feature + 1) % count] : null;

        ShowRooms();
        ShowDots();

        // The artist's picture, for the three cards that are DRAWN (the one in front and the two
        // behind it) and for no others. Each writes to its own card, so a reader who has stepped on by
        // the time one lands finds it waiting on the card they stepped away from, and not a photograph
        // appearing over the record they are looking at now. The front one is asked first, so it is
        // first through the decode gate.
        _features[_feature].Face = LoadFaceAsync(_features[_feature], Alive);

        // ...and what the station in front is playing. It is asked on every step and not once per
        // card, because it is the one thing on this plate that goes stale. Only the card in FRONT: see
        // AskWhatIsOn, where the cost of asking is written down.
        AskWhatIsOn(_features[_feature], Alive);

        if (PeekRightHost.Content is HomeFeatureCard after)
            after.Face = LoadFaceAsync(after, Alive);

        if (PeekLeftHost.Content is HomeFeatureCard before)
            before.Face = LoadFaceAsync(before, Alive);
    }

    /// <summary>One dot per card, each a way straight to it. They are written here and not in the
    /// markup, because how many there are depends on the collection.</summary>
    private void BuildDots()
    {
        FeatureDots.Children.Clear();
        if (_features.Count < 2)
            return;

        for (var i = 0; i < _features.Count; i++)
        {
            var dot = new Button
            {
                Width = 16,
                Height = 16,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = i,
                Content = new Border
                {
                    Width = 7,
                    Height = 7,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
                },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                dot, Loc.Format("Home_FeatureDot", i + 1, _features.Count));

            dot.Click += Dot_Click;
            FeatureDots.Children.Add(dot);
        }

        ApplyBannerStops();   // driven by a remote, the dots are the pointer's alone
    }

    /// <summary>Which dot is lit. It uses opacity and not two brushes, so the dots are one colour and
    /// the difference between them is only how strongly it is drawn.</summary>
    private void ShowDots()
    {
        for (var i = 0; i < FeatureDots.Children.Count; i++)
            if (FeatureDots.Children[i] is Button { Content: Border mark })
                mark.Opacity = i == _feature ? 1 : 0.32;
    }

    private void Dot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int index)
            Step(index - _feature);
    }

    private void FeaturePrevious_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void FeatureNext_Click(object sender, RoutedEventArgs e) => Step(1);

    /// <summary>A press on a neighbour brings it to the front and does not open it. It is half
    /// covered by the plate, and opening a record from a card the reader can only half see would be
    /// the app acting on something it did not show them.</summary>
    private void PeekLeft_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Step(-1);

    private void PeekRight_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => Step(1);

    /// <summary>The board carrying the strip right now, and which step it belongs to. Both are kept,
    /// because a reader can press the key again before the last step has landed. The old board is
    /// stopped so the two are not animating one property at once, and its Completed is told it is no
    /// longer the step in hand so it cannot put the strip back down in the middle of the new one. One
    /// property is animated and its resting value is its base value, which is what makes stopping it
    /// safe under Uno.</summary>
    private Storyboard? _stepping;

    private int _step;

    /// <summary>
    /// Moves the banner by <paramref name="by"/> cards and carries the strip in behind it.
    ///
    /// <para>The cards are swapped first and the track is then drawn in from the side the step came
    /// from. It is one transform on the group and not three animations: the plates are pictures, and
    /// moving the thing they stand on costs the compositor an offset, where moving each of them costs
    /// a layout.</para>
    /// </summary>
    private void Step(int by)
    {
        if (by == 0 || _features.Count < 2)
            return;

        // The card that is arriving gets a whole dwell of its own, however it was asked for. It is
        // done here and not at the two keys, so a press, a press on a neighbour, a dot and the timer
        // itself all leave the strip in the same state.
        Rewind();

        ShowFeature(_feature + by);

        // The whole strip is carried, which makes a step read as the cards moving ALONG and not as one
        // card being replaced where it stands.
        var carry = HomeBanner.Slide(FeatureBand.ActualWidth);
        var from = by > 0 ? carry : -carry;

        var slide = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(HomeBanner.SlideMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(slide, FeatureSlide);
        Storyboard.SetTargetProperty(slide, "X");

        // THE CARD IN FRONT DOES NOT FADE AT ALL. It is swapped at full opacity, and the strip's
        // slide is what carries the step.
        //
        // Any fade between two cards reads as the card going transparent, because it draws two records
        // at once. If the arriving card fades up over the old one, the two are blended. If the old
        // card holds full opacity, the pair is opaque and the reader sees both. If the old card fades
        // with the new one, two cards that are both part-way through a fade cover less than one card
        // between them, and the plate shows through. Carrying the old card off the plate hides the
        // blend but draws its own edge across the arriving card, as a line down the middle of every
        // step. Sliding the two cards past each other takes the strip apart, because the three cards
        // are drawn in three hosts of different widths with their own clips.
        //
        // So one card is drawn on the plate at every frame, at full strength, which is the only
        // arrangement in which nothing can be seen through anything. What the reader follows is the
        // strip moving.

        // The neighbours do not cross-dissolve either, for the same reason as the plate: an opacity
        // animation on a card is the card going see-through, and what is behind this strip is the
        // window's backdrop. They are opaque cards at full strength with nothing laid over them,
        // swapped in the same turn as the card in front, and the strip's slide is the whole of the
        // step.
        var board = new Storyboard();
        board.Children.Add(slide);

        var mine = ++_step;
        board.Completed += (_, _) =>
        {
            if (_step != mine)
                return;

            // Written first, stopped after: stopping a board puts its properties back to the values
            // they held before it ran, so the resting values have to be the local ones by then.
            FeatureSlide.X = 0;
            board.Stop();

            // Nothing to let go of here: the cards were swapped when the step started, and every one
            // of them draws itself the same way in any host, whichever of the three hosts is holding
            // it.
        };

        // Stopped BEFORE the resting values are written, because a landed board holds the properties
        // it animated and outranks a later write.
        _stepping?.Stop();
        _stepping = board;
        FeatureSlide.X = from;
        Services.Shell.Storyboards.Play(board);
    }

    // There is no opacity animation anywhere on this strip: a card that is part way through a fade
    // is a card you can see through, and what is behind the strip is the window's backdrop. See Step.

    /// <summary>A press on the plate opens what is on it (see <see cref="OpenFeature"/>).</summary>
    private void Feature_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        // A press on one of the keys is a step and nothing else. A Button does not stop a Tapped from
        // reaching the panel underneath it here, so the plate asks where the press STARTED and does
        // not trust the key to have eaten it. This also covers whatever else is put on the plate
        // later.
        if (PressedAKey(e.OriginalSource as DependencyObject))
            return;

        OpenFeature();
    }

    /// <summary>Opens what is on the plate: its station, its list or its record. It is read from the
    /// host and not from the field, so it opens the card that is actually drawn. A press on the plate
    /// and OK on the remote's key over it (<see cref="_plateKey"/>) both come here.</summary>
    private void OpenFeature()
    {
        if (PlateHost.Content is not HomeFeatureCard card)
            return;

        if (card.Station is { } station)
        {
            StationRequested?.Invoke(this, station);
        }
        else if (card.Door is { } door)
        {
            if (door.Generated is { } id)
                PlaylistRequested?.Invoke(this, id);
            else if (door.Kept is { } nodeId)
                StoredPlaylistRequested?.Invoke(this, nodeId);
        }
        else if (card.Folder is { Length: > 0 } folder)
        {
            AlbumRequested?.Invoke(this, folder);
        }
    }

    /// <summary>
    /// What a TV remote stands on for the banner: one key laid over the card in front. OK on it opens
    /// the card, as a press on the plate does, and Left and Right step the cards, as the arrow keys
    /// do. The plate is a Grid and takes no focus, so without this key the arrows would go from one of
    /// the banner's arrow keys to the other and nothing would reach the card between them.
    ///
    /// <para>It is laid over the plate and not around it, and it takes no pointer input, so a press
    /// with the mouse still lands on the plate (<see cref="Feature_Tapped"/>). It is outside the
    /// track, so it stays where the card in front stands while a step carries the cards under it. Its
    /// width is the plate's, written in <see cref="SizeFeatures"/>.</para>
    ///
    /// <para>It is a stop only where <see cref="DrivenByRemote"/> is set, which is Pegasus: it
    /// handles no keys itself, and Pegasus's host presses it on OK (PegasusPlayer.OkDown) and turns it
    /// on Left and Right (PegasusChrome.Adjust). In the main window it would be a stop that Enter does
    /// nothing on.</para>
    /// </summary>
    private readonly RemoteLine _plateKey;

    /// <summary>Builds <see cref="_plateKey"/> and lays it over the plate. It is called once, from the
    /// constructor.</summary>
    private RemoteLine BuildPlateKey()
    {
        var key = new RemoteLine(new Grid(), OpenFeature, menu: null, turn: StepByKey)
        {
            IsHitTestVisible = false,
            IsTabStop = false,   // until DrivenByRemote is set
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 0,
            CornerRadius = new CornerRadius(12),   // the plate's, so the remote's glow follows its corners
        };

        // Above the track, so it is not carried by a step; the two arrow keys stay above it.
        FeatureBand.Children.Insert(FeatureBand.Children.IndexOf(FeatureTrack) + 1, key);
        return key;
    }

    /// <summary>Left or Right on <see cref="_plateKey"/>: one card along, as the arrow keys step it.
    /// False for a strip with nothing to step to, so the remote bounces.</summary>
    private bool StepByKey(int way)
    {
        if (_features.Count < 2)
            return false;
        Step(way);
        return true;
    }

    /// <summary>
    /// Which of the banner's parts the keys stop on. When driven by a remote, only the card in front
    /// (<see cref="_plateKey"/>) is a stop. The arrow keys and the dots stay for the pointer, so Left
    /// and Right on the card step it and do not leave it for an arrow key. Otherwise the keys stop on
    /// the arrow keys and the dots, as Tab has always found them, and not on the card. It is called
    /// when <see cref="DrivenByRemote"/> is set and when the dots are built.
    /// </summary>
    private void ApplyBannerStops()
    {
        _plateKey.IsTabStop = _drivenByRemote;
        FeatureBack.IsTabStop = !_drivenByRemote;
        FeatureOn.IsTabStop = !_drivenByRemote;
        foreach (var dot in FeatureDots.Children)
            if (dot is Control key)
                key.IsTabStop = !_drivenByRemote;
    }

    /// <summary>
    /// An Up or Down that would pass over the banner lands on its card instead. Pegasus asks with
    /// where its search by layout would move the focus (<paramref name="next"/>). The card is centred
    /// and narrower than the screen, so from the quick selection's outer columns, or from the corner
    /// key above it, nothing in the banner stands in the same column, and the search would go past
    /// it: Up to the corner key, Down to the first quick card. Any other move is returned as it was.
    ///
    /// <para>Above and below are read from each element's middle against the banner's edges, not from
    /// its edges. The corner key overlaps the banner's top by a few points, and read by its edge it
    /// would not be above the banner, so Up from the first quick card would still go to it.</para>
    /// </summary>
    public Control? OverBanner(FrameworkElement from, Microsoft.UI.Xaml.Input.FocusNavigationDirection direction, Control? next)
    {
        if (!_plateKey.IsTabStop || _plateKey.ActualWidth <= 0 || FeatureScale.Visibility != Visibility.Visible
            || ReferenceEquals(next, _plateKey) || BoundsHere(FeatureBand) is not { } band
            || BoundsHere(from) is not { } at)
            return next;

        var beyond = next is not null ? BoundsHere(next) : null;
        var passes = direction switch
        {
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Up =>
                Middle(at) > band.Bottom && (beyond is null || Middle(beyond.Value) < band.Top),
            Microsoft.UI.Xaml.Input.FocusNavigationDirection.Down =>
                Middle(at) < band.Top && (beyond is null || Middle(beyond.Value) > band.Bottom),
            _ => false,
        };
        return passes ? _plateKey : next;

        static double Middle(Windows.Foundation.Rect r) => (r.Top + r.Bottom) / 2;
    }

    /// <summary>An element's bounds in this view's points, or null when it is out of the tree.</summary>
    private Windows.Foundation.Rect? BoundsHere(FrameworkElement element)
    {
        try
        {
            return element.TransformToVisual(this)
                          .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether the focus is on the banner (its card, its arrow keys or its dots) and was put
    /// there by keys: a remote's arrow, or Tab in the main window. A key pressed with the mouse keeps
    /// the focus after the press, and that is not somebody reading the banner. The pointer's own hover
    /// holds the countdown while it is over the strip. It is read from the focus manager each time it
    /// is asked.</summary>
    private bool KeysOnStrip()
    {
        if (XamlRoot is null
            || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)
                is not Control { FocusState: not FocusState.Pointer and not FocusState.Unfocused } focused)
            return false;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, FeatureStrip))
                return true;

        return false;
    }

    /// <summary>Whether a press began on a control standing on the plate. The walk goes up as far as
    /// the plate itself and no further: a button anywhere else on this screen is not this plate's
    /// business.</summary>
    private bool PressedAKey(DependencyObject? from)
    {
        for (var node = from; node is not null && node != FeaturePlate; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase)
                return true;

        return false;
    }

    private void FeatureBand_SizeChanged(object sender, SizeChangedEventArgs e)
        => Fitting(PartBanner, FeatureBand, () => SizeFeatures(e.NewSize.Width));

    /// <summary>The three plates, from the width the strip was given. See <see cref="HomeBanner"/>,
    /// which holds the arithmetic and the two cases that are invisible until a reader hits them.</summary>
    private void SizeFeatures(double strip)
    {
        var plate = HomeBanner.PlateWidth(strip);
        var peek = HomeBanner.PeekWidth(strip);
        var shown = peek > 0 && _features.Count > 1;

        FeaturePlate.Width = plate;
        _plateKey.Width = plate;

        // The plate cuts what is inside it, which lets the outgoing card leave: without a clip it
        // would slide out across the neighbour beside it and not disappear at the plate's own edge.
        // It is sized here because the plate's width is decided here.
        FeaturePlate.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, plate, HomeBanner.Height),
        };

        // Each neighbour: the window it is cut to, and the card standing in it, which is drawn at the
        // PLATE's width and shown through that cut. Nothing on this strip dissolves, so there is no
        // second host behind each one.
        // How far in from the strip's outer edge each one sits. It is zero until the plate has hit its
        // cap and the strip goes on growing, and from there the room beside the plate is wider than
        // the card that stands in it. It goes on the OUTER side of each neighbour, because the inner
        // one is against the plate. See HomeBanner.PeekInset.
        var inset = HomeBanner.PeekInset(strip);

        foreach (var (cut, host, outward) in new[]
                 {
                     (PeekLeft, PeekLeftHost, new Thickness(inset, 0, 0, 0)),
                     (PeekRight, PeekRightHost, new Thickness(0, 0, inset, 0)),
                 })
        {
            cut.Width = peek;
            cut.Height = HomeBanner.PeekHeight;
            cut.Margin = outward;
            host.Width = plate;
            host.Height = HomeBanner.PeekHeight;
            cut.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;

            // A clip and not a corner radius: a Border does not cut what stands inside it, so without
            // this the whole neighbouring plate is drawn straight across the one in front.
            cut.Clip = shown
                ? new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, peek, HomeBanner.PeekHeight) }
                : null;
        }

    }

    /// <summary>
    /// The cards the banner offers, from the same five answers the rows are drawn from.
    ///
    /// <para>Which cards, and in which order, is the answer of <see cref="HomeFeatures.Strip"/>, not
    /// of this view: it is arithmetic over five lists, and a test can hold it still. What is done
    /// here is the looking up: a folder to the record's name and artist, a list's name to the card
    /// the row of lists already built.</para>
    /// </summary>
    private static IReadOnlyList<HomeFeatureCard> BuildFeatures(
        IReadOnlyList<string> added,
        IReadOnlyList<string> suggested,
        IReadOnlyList<string> played,
        IReadOnlyList<string> listened,
        HomeListCard? list,
        string listsHeading,
        IReadOnlyDictionary<string, LibraryDatabase.AlbumFactsRow> names,
        IReadOnlyDictionary<string, int> years,
        IReadOnlyList<RadioListen> stations)
    {
        // ONE list, and it is the one ReadBannerList chose: the mood the hour asks for, cut to the
        // music this listener plays at that hour. The row below offers its own lists in its own order.
        // The banner asks a different question and gets its own answer.
        var lists = list is { } only ? new List<HomeListCard> { only } : [];

        // The record that was on last. It does NOT go through Pictured. That call reorders a
        // question's answers by which artists photograph well and drops the artists known to have no
        // photograph at all. This is right for a question with twenty answers and wrong for one with
        // a single answer: a "carry on with this" card that names a record other than the one that
        // was actually on would be lying to get a better picture. It stands on the blurred cover like
        // any other card whose artist has none. A Various Artists record gets no Continue card at all:
        // Strip drops it, and nothing is put in its place.
        var continued = listened.Count > 0 ? new[] { listened[0] } : [];

        // The most-played card is drawn from the front of that row and is not its first entry. On a
        // collection somebody has had for years, the top of a play count does not move, so the banner
        // would hold up one record for ever. The ROW keeps its own order, which is what makes its
        // heading true; only what the banner reaches for is drawn. See HomeShelf.Varied.
        //
        // Varied comes BEFORE Pictured, so the photograph ordering is applied to the draw, and the
        // draw is not applied to a list that Pictured has already settled.
        var drawn = HomeShelf.Varied(played, App.Launch, "banner-played", HomeShelf.BannerReach);

        // The stations by uuid, which is what the strip deals in, and the whole rows kept beside it so
        // the card can be built from one without asking the log again.
        var heard = stations.ToDictionary(l => l.Station.Uuid, l => l, StringComparer.OrdinalIgnoreCase);

        var strip = HomeFeatures.Strip(
            continued, Pictured(added, names), [.. lists.Select(l => l.Name)],
            Pictured(suggested, names), Pictured(drawn, names), Pictured(listened, names),
            [.. stations.Select(l => l.Station.Uuid)],
            key => names.TryGetValue(key, out var fact)
                   && RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(fact.AlbumArtist),
            HomeFeatures.Count);

        var cards = new List<HomeFeatureCard>(strip.Count);
        foreach (var (kind, key) in strip)
        {
            if (kind == HomeFeatureKind.MadeForYou)
            {
                if (lists.FirstOrDefault(l => l.Name == key) is not { } offered)
                    continue;

                cards.Add(new HomeFeatureCard
                {
                    Kind = kind,
                    // The row's own heading, so the banner calls the lists what the row below calls
                    // them: made for the listener on their own shelf, somebody's playlists on a
                    // borrowed one.
                    Kicker = listsHeading,
                    Title = offered.Name,
                    Subtitle = offered.Caption,
                    Door = offered.Door,
                    Glyph = offered.Glyph,
                    InkColor = offered.InkColor,
                });

                continue;
            }

            if (kind == HomeFeatureKind.Radio)
            {
                if (!heard.TryGetValue(key, out var listen))
                    continue;

                var station = listen.Station;

                cards.Add(new HomeFeatureCard
                {
                    Kind = kind,

                    // No kicker. Every other card has one because it has to say which of the screen's
                    // questions put it there: a cover alone does not say whether it is the newest record
                    // or the most played. A station's mark in the middle of a card says what it is
                    // without a word over it, and a kicker such as "Your radio" over a logo would be the
                    // card labelling itself.
                    Title = station.Display,
                    Station = station,

                    // The room's colours until the station's own mark arrives and paints it properly
                    // (LoadStationFaceAsync). They come from the palette's hue ring by NAME, in the same
                    // way as a record with no cover: a station has no colour of its own to borrow either,
                    // and the ring keeps the answer inside this app's palette. The same station is the
                    // same colour on every machine, so the card does not change colour between launches
                    // for the seconds before its logo lands.
                    InkColor = SoftFigure.RingColours(station.Display) is [var first, ..]
                        ? first
                        : Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF),
                });

                continue;
            }

            if (!names.TryGetValue(key, out var fact))
                continue;

            var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
            cards.Add(new HomeFeatureCard
            {
                Kind = kind,
                Kicker = Loc.Get(KickerKey(kind)),
                Title = fact.Album ?? (parts.Length > 0 ? parts[^1] : key),
                Subtitle = fact.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                Artist = fact.AlbumArtist ?? (parts.Length >= 2 ? parts[^2] : ""),
                Folder = key,
                Year = years.TryGetValue(key, out var year) ? year : 0,
            });
        }

        return cards;
    }

    /// <summary>
    /// The stations heard most, most first.
    /// </summary>
    /// <remarks>
    /// It is not read from the shelf, and that is the whole of what makes this card different from
    /// every other one on this screen: a station is not in the library, so the mirror has never heard
    /// of it, and the log it IS in outlives whichever library is open. See
    /// <see cref="RadioListens"/>, and <see cref="Services.Audio.RadioCounter"/>, which is the only
    /// thing that writes to it.
    ///
    /// <para>It is read on the UI thread and not with the shelf, which is a walk of the collection on
    /// a worker. This is a sort of at most <see cref="RadioListens.Kept"/> rows already in memory, and
    /// putting it on the worker would mean holding the radio service across the hand-back for
    /// nothing.</para>
    ///
    /// <para>It is empty where the radio has never been played long enough to count, and empty in the
    /// design-time tree, where there is no service to ask. The strip then fills from the questions the
    /// shelf can answer.</para>
    /// </remarks>
    private static IReadOnlyList<RadioListen> MostListenedStations()
    {
        try
        {
            return App.Services.GetRequiredService<Services.Audio.RadioService>()
                      .Listens.Top(HomeFeatures.Count);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The same records, artists with a photograph first, and artists known to have none left out.
    /// </summary>
    /// <remarks>
    /// The banner IS a picture: a card whose artist has no photograph stands on the blurred cover, and
    /// next to four cards that have one it looks like the one that failed. So the order each question
    /// answers in is kept, and within it the records whose artist is already known to photograph well
    /// come first.
    ///
    /// <para>What is known is what the last session learned: <see cref="Services.Art.BannerOrders"/>
    /// keeps per artist the picture that arrived and the candidates that were ranked. An artist
    /// nobody has asked about yet is neither first nor dropped: it goes behind the known ones, because
    /// it may well have a photograph and a first evening has nothing cached at all. Only an artist
    /// that WAS asked and came back with nothing is left out, and Various Artists, which is not a
    /// person and is never asked.</para>
    ///
    /// <para>The result is never reduced to nothing: if that would leave a question with no answer,
    /// the question is answered unfiltered instead. A banner with four cards in it is worse than a
    /// banner whose fifth card stands on a cover. The unfiltered answer can hold Various Artists
    /// records again; <see cref="HomeFeatures.Strip"/> drops those itself.</para>
    /// </remarks>
    private static IReadOnlyList<string> Pictured(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, LibraryDatabase.AlbumFactsRow> names)
    {
        var known = new List<string>();
        var unasked = new List<string>();

        foreach (var key in keys)
        {
            var artist = names.TryGetValue(key, out var fact) ? fact.AlbumArtist : null;
            if (artist is not { Length: > 0 }
                || RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(artist))
                continue;

            switch (Services.Art.BannerOrders.For(artist))
            {
                case { Chosen.Length: > 0 }:          // a photograph that has arrived before
                case { Urls.Count: > 0 }:             // candidates found, not yet fetched
                    known.Add(key);
                    break;
                case null:                            // never asked
                    unasked.Add(key);
                    break;
                default:                              // asked, and there is nothing for this act
                    break;
            }
        }

        return known.Count + unasked.Count == 0 ? keys : [.. known, .. unasked];
    }

    /// <summary>The kicker over a record's title: the heading of the row that same record stands in,
    /// so the screen names its questions once.</summary>
    private static string KickerKey(HomeFeatureKind kind) => kind switch
    {
        HomeFeatureKind.Continue => "Home_Continue",
        HomeFeatureKind.Added => "Home_Added",
        HomeFeatureKind.Suggested => "Home_Suggested",
        HomeFeatureKind.Played => "Home_Played",
        // No Radio here on purpose: a station's card has no kicker at all, so it never asks. See
        // BuildFeatures, where it is built without one.
        _ => "Home_Listened",
    };

    /// <summary>What went on which plate, and why, one line per card asked. The judgements the banner
    /// makes about a photograph cannot be seen from the screen they produced: a plate with the wrong
    /// artist's picture and a plate with none look the same as a plate nobody asked about.
    ///
    /// <para>It is off unless <c>RPM_FACES_DIAG=1</c> is in the environment, which is the same switch
    /// the playlist headers' framing writes under, and the same subject.</para></summary>
    private static ILogger Faces =>
        FaceDiag ? App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Faces")
                 : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private static readonly bool FaceDiag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_FACES_DIAG") is "1" or "true";

    /// <summary>How often the drawing thread is asked whether it is free, and how long the wait may go
    /// on. The limit is a promise of the same shape as the Governor's: a machine that is never quiet
    /// would otherwise leave the banner standing on blurred covers for the whole session.</summary>
    private const int CalmBeatMs = 48;

    private const int CalmCeilingMs = 4000;

    /// <summary>
    /// Waits until the drawing thread is free, or until the limit.
    ///
    /// <para>Free is <see cref="Services.Shell.UiPulse.Free"/>: not held, and calm for six turns
    /// running. It is measured by a thread that is not the one being measured, which is why it can be
    /// asked from here at all. See the note on that class about a busy thread not being able to
    /// report how busy it is.</para>
    /// </summary>
    private static async Task CalmAsync(CancellationToken ct)
    {
        var pulse = App.Services.GetRequiredService<Services.Shell.UiPulse>();

        for (var waited = 0; waited < CalmCeilingMs && !pulse.Free && !ct.IsCancellationRequested;
             waited += CalmBeatMs)
        {
            try
            {
                await Task.Delay(CalmBeatMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The token the current reading of the shelf is under, for the work that is started later
    /// than the reading: the artist's picture is asked for when a card comes to the front, which can
    /// be minutes after the strip was built.</summary>
    private CancellationToken Alive => _loading?.Token ?? CancellationToken.None;

    /// <summary>Whether the app is on screen: the splash has come off and there is somebody to draw
    /// for. It is a latch, read and not subscribed to, so it holds whatever order things arrive in.
    /// See <see cref="Services.Shell.Phase.Ready"/>, which the reveal gate's callback reaches by every
    /// route out of it.</summary>
    private static bool Revealed =>
        App.Services.GetRequiredService<Services.Shell.Governor>().Reached >= Services.Shell.Phase.Ready;

    /// <summary>
    /// The photographs for the cards the strip is holding but not drawing.
    /// </summary>
    /// <remarks>
    /// <b>The strip is the one as it stood when this was called.</b> It is passed in and not read from
    /// <c>_features</c>, which a later reading clears and refills. Enumerating the live list across an
    /// await would be one reading walking into another reading's list. A card that has since been
    /// replaced is written to and nothing draws it, which is the rule every fill on this screen
    /// keeps.
    ///
    /// <para><b>It runs after the drawn three have landed.</b> Every photograph decodes through this
    /// screen's one paced gate, and choosing one is a ranking that costs a download per candidate plus
    /// a model run. So two cards nobody can see, started at the same moment, would queue in front of
    /// the three the reveal is waiting for. Waiting costs nothing that is seen: the banner holds a
    /// card for seven seconds (<see cref="HomeBanner.DwellMs"/>) before it steps itself, and these
    /// have that whole dwell to arrive in.</para>
    ///
    /// <para>The three drawn cards are asked again and answer at once: <c>FaceAsked</c> is claimed on
    /// the card before the first await, so this costs a comparison each and not a second lookup.
    /// Asking for the whole strip, and not working out which two are left, is the same choice
    /// <c>LoadHypeAsync</c> makes, and for the same reason: one door, and no second answer to keep
    /// right.</para>
    /// </remarks>
    private async Task ReadyOffScreenFacesAsync(
        IReadOnlyList<HomeFeatureCard> strip, IReadOnlyList<Task> drawn, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(drawn);
        }
        catch
        {
            // Every lookup already handles its own failure and leaves the card showing something. This
            // await is for the ORDER, not for the answers.
        }

        if (ct.IsCancellationRequested)
            return;

        foreach (var card in strip)
            _ = LoadFaceAsync(card, ct);
    }

    /// <summary>The height a card's logo is drawn at, in points, and the width it may run to. Both are
    /// the limits the markup puts on the Image (see HomeView.xaml, the row under the card's title).
    /// They are held here as well because the mark is CUT to them, and a cut that does not agree with
    /// what the surface draws is a mark resized twice, once well and once at draw time.</summary>
    private const double LogoLine = 26;

    /// <summary>See <see cref="LogoLine"/>.</summary>
    private const double LogoRun = 260;

    /// <summary>
    /// The artist's photograph and their own lettering, for the card that has just come to the front.
    ///
    /// <para><b>The front card and no others.</b> Both are catalogue lookups, and five of them on every
    /// opening of this screen would be a burst of traffic for four cards nobody has looked at. The
    /// lookup is asked when a card is actually in front and kept on the card, so stepping back to it is
    /// free. The cost is a plate that stands on the blurred cover for the moment the lookup takes, and
    /// then changes under the reader. The cover plate is a finished plate, and the photograph is the
    /// better one.</para>
    ///
    /// <para><b>Nothing is asked about Various Artists.</b> A compilations shelf is not a person, and
    /// the catalogues answer about somebody else entirely. The artists wall keeps the same rule.</para>
    ///
    /// <para>Both caches are the app's own and are shared with the artists wall, the track pane and
    /// the now playing panel (see <see cref="ArtistPortraits"/> and <see cref="ArtistLogos"/>). An
    /// artist whose picture has already been looked at anywhere in the app costs this screen nothing.
    /// Whether anything leaves the machine is the gate's business and not this screen's: see
    /// <c>OnlineGateHandler</c>, which every art source's client is built on.</para>
    /// </summary>
    private async Task LoadFaceAsync(HomeFeatureCard card, CancellationToken ct)
    {
        // Asked once per card and never again. Cutting the picture a second time whenever the plate's
        // aspect moves would re-frame the subject: the same photograph, a different part of it,
        // arriving a quarter of a second after a drag stops. The picture must be static.
        //
        // So the cut is made once, at whatever the plate was when the card was first drawn, and the
        // brush holds it rigid against the card's right edge from then on (see the markup). A plate
        // that grows shows more of the picture and one that narrows shows less, which is the box
        // moving over a still photograph and not the photograph moving inside the box.
        if (card.FaceAsked)
            return;

        if (card.Cutting)
            return;

        // A generated list is not an artist and has no record: what it shows is its own picture,
        // which is a different question asked of a different place.
        if (card.Door is { Generated: { } generated })
        {
            card.FaceAsked = true;
            await LoadListFaceAsync(card, generated, ct);
            return;
        }

        // And a station is neither. What it shows is its own mark and one line saying what it is, both
        // from the station's own site and not from any catalogue of music.
        if (card.Station is { } station)
        {
            card.FaceAsked = true;
            await LoadStationFaceAsync(card, station, ct);
            return;
        }

        if (card.Artist is not { Length: > 0 } artist)
            return;

        if (RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(artist))
        {
            card.FaceAsked = true;
            Faces.LogInformation(
                "[home] {Title}: credited to {Artist}, which is not a person. No photograph asked for.",
                card.Title, artist);
            return;
        }

        card.FaceAsked = true;
        card.Cutting = true;

        try
        {
            // The record, because the artwork service answers better on an artist with one of their
            // albums beside them than on a bare name. It is the record on the card, which is one of
            // theirs.
            var album = card.Title is { Length: > 0 } named ? named : null;

            // The photographs this artist has, in the order they suit THIS PLATE. That is not the order
            // the app settles on for the artist at large: a strip four times as wide as it is tall, with
            // a cover and a title down its left, wants the widest picture whose subject clears them. See
            // ArtistPortraits.BestForAsync, which asks BackdropRanker the same question the now playing
            // header asks about its own strip.
            //
            // The list is walked and not just taken from the top: the best-shaped picture is not always
            // one that arrives. Some of what the sources hand back lives on a host this process may not
            // fetch from, and the first that lands is a photograph of the right artist either way.
            // The order is kept on disk between launches (BannerOrders): ranking costs a download per
            // candidate and a model run over the widest few, and this strip is the first thing on the
            // screen. What is stored is this surface's answer and not the player's. See the note there
            // on why it is not written into the artist_art table.
            var kept = Services.Art.BannerOrders.For(artist);

            IReadOnlyList<string> order;
            if (kept is { Chosen: { Length: > 0 } chosen })
            {
                order = [chosen];   // one that is known to arrive beats any ranking
            }
            else if (kept is { Urls.Count: > 0 })
            {
                order = kept.Urls;
            }
            else
            {
                // The lookup did not answer: nothing is written to BannerOrders, where an empty order
                // would stand as "this artist has no photographs" on every later launch, and the claim
                // is given back so the card asks again the next time it comes into a host.
                if (!(await ArtistPortraits.BestForAsync(
                            artist, album, HomeBanner.Scene(FeaturePlate.ActualWidth), HomeBanner.Considered, ct))
                        .Answers(out var framing, out var unordered))
                {
                    if (!ct.IsCancellationRequested)
                        card.FaceAsked = false;
                    Faces.LogInformation(
                        "[home] {Title} by {Artist}: photographs could not be looked up ({Why}). Asked again later.",
                        card.Title, artist, unordered);
                    return;
                }

                order = framing.Urls;
                Services.Art.BannerOrders.Keep(artist, order, framing.Ranked);
            }

            // Whether any photograph tried could not be fetched. A card that ends with none on it
            // because of that has not learnt that the artist has no photograph; see after the loop.
            var unfetched = false;

            foreach (var url in order.Take(HomeBanner.Tried))
            {
                if (ct.IsCancellationRequested)
                    return;

                var fetched = await ArtistPortraits.PictureAsync(url, ct);
                if (ct.IsCancellationRequested)
                    continue;

                if (!fetched.Answers(out var found, out _))
                {
                    unfetched = true;
                    continue;
                }

                if (found is not { Thumb.Length: > 0 } picture)
                    continue;

                // The picture as fetched where this fetch made it, the cut-down copy where the answer
                // came out of the cache. The plate draws a window out of this across most of 760 points,
                // so a copy already reduced to a tile's worth of pixels would be drawn bigger than it is
                // and look soft. See ArtistPortraits.Portrait.Full.
                var source = picture.Full is { Length: > 0 } whole ? whole : picture.Thumb;

                if (await CutToThePlateAsync(source, ct) is not { Bytes.Length: > 0 } cut)
                {
                    // No width to frame against yet: this card's fetch beat the first layout. Asked
                    // once is the rule, so the claim has to be given back, or the card keeps the
                    // record's colour for the rest of the session. It is asked again the next time it
                    // comes into a host, by which point the plate has a width. A cancellation is not
                    // that: the screen is going away and there is nobody to ask for.
                    if (!ct.IsCancellationRequested)
                        card.FaceAsked = false;
                    return;
                }

                if (ct.IsCancellationRequested)
                    return;

                // ...and the card's ground is this same photograph blurred, across the whole plate. It
                // is made from the picture AS FETCHED and not from the cut: the cut carries a feathered
                // left edge baked in for the plate it was framed against, and blurring that gives a
                // ground that fades out on one side for no reason anybody could see.
                //
                // The flag is set with the picture and not after it, so the cover's wash landing in the
                // same moment is refused and not drawn for a frame.
                if (await Cancellable.RunAsync(() => ArtScaler.Wash(source), ct)
                        is { Ran: true, Value: { Length: > 0 } washed }
                    && !ct.IsCancellationRequested)
                {
                    card.GroundIsPortrait = true;
                    card.Ground = await _pictures.Next(
                        () => RemoteImage.DecodeAsync(washed, ArtScaler.WashEdge));
                }

                if (ct.IsCancellationRequested)
                    return;

                // The photograph as it was taken, not the drained copy the wall draws: there is no wall
                // here for it to match, and what goes over it is a scrim and not a colour cast.
                var drawn = await _pictures.Next(
                    () => RemoteImage.DecodeAsync(cut.Bytes, ArtScaler.TileEdge));

                // A first picture comes up out of the cover's blur. Two cases do not, and both are the
                // same case: there is no reader for the fade to be for.
                //
                // THE FIRST IS A PICTURE THAT LANDS BEFORE THE APP IS ON SCREEN. The splash is still up,
                // home's content is held at zero opacity, and the fade would finish over the top of the
                // reveal, so the photograph would seem to load in as the app arrives. The governor is
                // asked and the state is not tracked here, because it is a LATCH that holds whatever
                // order things arrive in: `Phase.Ready` is reached in the gate's own callback, by every
                // route out of it (dressed, patience or backstop), so a launch that reveals early
                // without its pictures still fades the ones that land afterwards.
                if (!Revealed)
                    card.SwapPortrait(drawn);
                else
                    card.ShowPortrait(drawn);

                // This goes on the ring and not through Faces, which is off unless RPM_FACES_DIAG is
                // set: it is one line per card, and which width a picture was framed for is not
                // otherwise visible from outside the app.
                RPM.Core.Diagnostics.Scope.Said(
                    "home", $"cut {card.Title} for a plate of {cut.Plate:F0}");

                card.CutFor = cut.Plate;

                Services.Art.BannerOrders.Chose(artist, url);
                break;
            }

            // No photograph on the plate, and at least one could not be fetched: the claim is given
            // back, so the card is asked again the next time it comes into a host and does not keep the
            // record's colour for the rest of the session.
            if (card.Portrait is null && unfetched && !ct.IsCancellationRequested)
                card.FaceAsked = false;

            Faces.LogInformation(
                "[home] {Title} by {Artist}: photograph={Photo}", card.Title, artist,
                card.Portrait is null ? (unfetched ? "none, not fetched" : "none") : "on the plate");

            if (ct.IsCancellationRequested)
                return;

            if (await ArtistLogos.ForAsync(artist, album, ct) is { Length: > 0 } mark
                && !ct.IsCancellationRequested)
            {
                // Cut to the height it is drawn at, by the app's own resampler, in the same way as the
                // artist page cuts the same mark (see ArtistDetailView.LogoAsync and ArtScaler.FitHeight).
                // The mark arrives cut to its lettering (ArtScaler.Trim) and is still a couple of
                // hundred pixels tall for a line of 26 points. Handing it over whole would leave the
                // reduction to the renderer at draw time, which is a plain bilinear read. On lettering
                // that gives a coarse, noisy result, which the cut exists to prevent.
                //
                // There are two limits and the lower one wins: the line's height, and the height at
                // which a long name still fits across the card. A mark is drawn to its own proportions,
                // so a name set as one long word costs its height several times over in width and
                // reaches the width limit first.
                var (wide, high) = ArtScaler.BoundsOf(mark);
                var cap = wide > 0 && high > 0
                    ? Math.Min(LogoLine, LogoRun * (double)high / wide)
                    : LogoLine;

                var wanted = (int)Math.Ceiling(cap * BannerPixels);
                if (await Cancellable.RunAsync(() => ArtScaler.FitHeight(mark, wanted), ct)
                        is { Ran: true, Value: { } fitted }
                    && !ct.IsCancellationRequested)
                {
                    card.Logo = await _pictures.Next(() => RemoteImage.DecodeAsync(fitted));
                }
            }

            Faces.LogInformation(
                "[home] {Title} by {Artist}: lettering={Logo}", card.Title, artist,
                card.Logo is null ? "none, the name stands" : "in place of the name");
        }
        catch (OperationCanceledException)
        {
            // The shelf was re-read or the view closed. Nothing to say and nothing to draw.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A source having a bad day is not an answer about this artist. The plate keeps the cover
            // it already stands on and the name it is already printing.
        }
        finally
        {
            card.Cutting = false;
        }
    }

    /// <summary>
    /// The line that puts one record forward, taken from what the encyclopedia says about it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here writes a claim.</b> The reading is <see cref="RPM.Core.Metadata.AlbumHype"/>,
    /// which emits a phrase only where the article's own text carries the fact, and answers null for a
    /// record whose article says nothing remarkable. A template would be easy, but it would have this
    /// app calling every record with an article critically acclaimed.
    ///
    /// <b>The lookup is the one the track pane already makes</b>, so a record whose papers have been
    /// fetched anywhere in the app costs this screen nothing: TrackInfoService answers from memory,
    /// then from disk, and only then from the network. Nothing on this screen waits for it. The card
    /// is drawn with the artist's name and the line replaces it when it lands.
    ///
    /// <para>It is not asked about a list, which has no album to have an article. It is not asked
    /// about Various Artists either, for the reason the photograph is not: a compilations shelf is
    /// not a record anybody wrote about.</para>
    /// </remarks>
    private async Task LoadHypeAsync(HomeFeatureCard card, CancellationToken ct)
    {
        if (card.Door is not null || card.Folder is not { Length: > 0 }
            || card.Artist is not { Length: > 0 } artist
            || card.Title is not { Length: > 0 } album
            || RPM.Core.Metadata.Providers.ArtistCredit.IsVariousArtists(artist))
            return;

        try
        {
            var asked = await App.Services.GetRequiredService<Services.Catalog.TrackInfoService>()
                                .GetAsync(artist, album, ct: ct);

            // Not answered: no line, and the card is asked about again the next time it is built.
            if (ct.IsCancellationRequested || !asked.Answers(out var papers, out _)
                || papers.AlbumArticle is not { Extract.Length: > 0 } article)
                return;

            if (RPM.Core.Metadata.AlbumHype.For(article.Extract, card.Year) is { Length: > 0 } line)
                card.Hype = line;
        }
        catch (OperationCanceledException)
        {
            // The shelf was re-read or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A catalogue having a bad day is not a reason to have no card. The artist's name stands.
        }
    }

    /// <summary>
    /// A station's mark, and the room it stands in.
    /// </summary>
    /// <remarks>
    /// The walk is <see cref="RadioStationItem.LogoBytesAsync"/>, which is the one the stations pane
    /// and the now playing square already take: everything the directory filed for this station, then
    /// what its own front page declares, stopping at the first that is big enough to be a mark. It is
    /// shared and not written again, because a station whose logo this card found and that pane did
    /// not would be two answers about one station.
    ///
    /// <para>The room is painted from the mark's own colours in the same way as a list's is from its
    /// picture, and before the mark goes up, for the same reason: a card recoloured before its
    /// picture lands reads as the card arriving, and the other way round reads as it changing its
    /// mind.</para>
    ///
    /// <para>Nothing arriving is a finished card: the radio mark stands in the middle of the room the
    /// station's name already chose. That is the usual case for a station the directory filed with a
    /// dead favicon and no homepage.</para>
    /// </remarks>
    private async Task LoadStationFaceAsync(HomeFeatureCard card, RadioStation station, CancellationToken ct)
    {
        // The line comes first, on its own errand and not after the mark. The two come from the same
        // page and the same cache, so asking for the line first costs nothing and means a card whose
        // logo walk is still going through half a dozen dead URLs is not silent meanwhile.
        _ = LoadStationLineAsync(card, station, ct);

        // The spinner turns where the mark goes while this runs (HomeFeatureCard.MarkWaiting). It is
        // set in the same turn as ShowFeature hands the card to its host, so the cell is never drawn
        // empty first, and it is cleared in the finally.
        card.StationMarkFetching = true;
        try
        {
            var answer = await RadioStationItem.LogoBytesAsync(station, ct);

            // The walk met a failure and found nothing. That is not "this station has no mark": the
            // glyph is not put up and the claim is given back, so the card asks again the next time it
            // comes into a host.
            if (answer is Asked<byte[]?>.Unanswered { Why: var why })
            {
                if (!ct.IsCancellationRequested)
                    card.FaceAsked = false;

                Faces.LogInformation(
                    "[home] {Title}: the station's mark could not be fetched ({Why}). Asked again later.",
                    card.Title, why);
                return;
            }

            if (answer is not Asked<byte[]?>.Answered { Value: { Length: > 0 } mark }
                || ct.IsCancellationRequested)
            {
                // Said so the plate may draw the radio glyph: nothing is coming for it now. It is not
                // said on cancellation: the screen is being re-read or closed, and a card that marked
                // itself empty on the way out would open on a glyph the next time it is drawn.
                if (!ct.IsCancellationRequested)
                    card.StationMarkMissing = true;

                Faces.LogInformation(
                    "[home] {Title}: no mark for this station. Its own colour stands.", card.Title);
                return;
            }

            if (await Cancellable.RunAsync(() => CoverRoom.Of(mark), ct)
                    is { Ran: true, Value: { } painted }
                && !ct.IsCancellationRequested)
                card.ShowRoom(painted);

            // Cut to the size the plate draws it at, by the app's own resampler, in the same way as the
            // artist's lettering is cut a few dozen lines up (see LoadFaceAsync and ArtScaler.FitMark).
            // A station's mark arrives 512 pixels square for a plate that draws it 160 points tall, and
            // a reduction that size handed to the bitmap decoder is sixteen source pixels read per
            // output pixel with the rest dropped. On lettering, which is what a station's mark is, that
            // gives a coarse, noisy result down every stroke.
            //
            // There are two limits and the lower one wins, for the reason the artist's mark has two: a
            // wide lockup reaches the width limit long before the height one.
            var (wide, high) = ArtScaler.BoundsOf(mark);
            var cap = wide > 0 && high > 0
                ? Math.Min(StationMarkLine, StationMarkRun * (double)high / wide)
                : StationMarkLine;

            var wanted = (int)Math.Ceiling(cap * BannerPixels);

            // Through the same paced gate every other picture on this screen goes through, so the mark
            // does not decode in front of the row covers that are queued behind it.
            card.StationLogo = await _pictures.Next(
                () => RemoteImage.DecodeMarkAsync(mark, wanted, ArtScaler.MarkSide.Height));

            Faces.LogInformation("[home] {Title}: the station's own mark, on the plate.", card.Title);
        }
        catch (OperationCanceledException)
        {
            // The shelf was re-read or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A station's site having a bad day is not an answer about the station. Its colour stands.
        }
        finally
        {
            card.StationMarkFetching = false;
        }
    }

    /// <summary>
    /// The one line under a station's name: what the station says it is, or what Wikipedia says about
    /// a station of that name.
    /// </summary>
    /// <remarks>
    /// This is its own errand and not a step of <see cref="LoadStationFaceAsync"/>, because the two
    /// answer on very different clocks: the line is one cached read of a page, and the mark is a walk
    /// through everything the station published, several of which are usually dead. It is written to
    /// the CARD, like every other picture and line on this plate, so a reader who has stepped on finds
    /// it waiting on the card they stepped away from.
    ///
    /// <para>Nothing is drawn where nothing is found, and that is most stations: the line is not a
    /// placeholder and the card is finished without it.</para>
    /// </remarks>
    private static async Task LoadStationLineAsync(
        HomeFeatureCard card, RadioStation station, CancellationToken ct)
    {
        try
        {
            if (await RadioStationItem.BlurbAsync(station, ct) is not { Length: > 0 } says)
            {
                Faces.LogInformation(
                    "[home] {Title}: nothing says what this station is. No line under the name.",
                    card.Title);
                return;
            }

            if (ct.IsCancellationRequested)
                return;

            card.Blurb = says;
            Faces.LogInformation("[home] {Title}: under the name, \"{Line}\"", card.Title, says);
        }
        catch (OperationCanceledException)
        {
            // The shelf was re-read or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A site or an encyclopedia having a bad day is not an answer about the station.
        }
    }

    /// <summary>The height a station's mark is drawn at on the plate, in points, and the width it may
    /// run to. Both are the limits the markup puts on the Image (see HomeView.xaml, the radio card).
    /// They are held here as well because the mark is CUT to them, and a cut that does not agree with
    /// what the surface draws is a mark resized twice, once well and once at draw time. The artist's
    /// lettering has the same pair; see <see cref="LogoLine"/>.</summary>
    private const double StationMarkLine = 160;

    /// <summary>See <see cref="StationMarkLine"/>.</summary>
    private const double StationMarkRun = 320;

    /// <summary>
    /// What a generated list's card shows: the cover that list shows everywhere else, in the square,
    /// and the list's room moving across the plate behind it.
    /// </summary>
    /// <remarks>
    /// <b>The composed cover, not the bare picture.</b> Asking
    /// <see cref="Services.Art.GeneratedCoverService"/> directly would give the photograph that goes
    /// BEHIND the app's drawing, and nothing at all for the five lists that stand on the room
    /// (<see cref="CoverOverlay.Roomed"/>), so "Your mix" and its four would have a clock in the
    /// square where their cover should be. <see cref="GeneratedCover"/> is the one place the cover is
    /// chosen and framed, so the banner shows what the playlists tree and the list's own page show.
    ///
    /// <para><b>The plate keeps its room, and that is why no ground is set here.</b> The room is drawn
    /// OVER the ground (see the markup), so a ground on a list's plate would be a picture decoded for
    /// nothing. The wash is still read, for its COLOURS, which is what the room is repainted in.</para>
    ///
    /// <para>Those colours come from the wash and not from the cover, because the cover carries the
    /// app's drawing: a border inked in one palette colour and the list's name in another, neither of
    /// them a colour of the picture. The wash is the picture behind that drawing, blurred, and for the
    /// five roomed lists it IS the still room, so the plate moves what the square is holding.</para>
    /// </remarks>
    private async Task LoadListFaceAsync(HomeFeatureCard card, SmartPlaylistId id, CancellationToken ct)
    {
        try
        {
            var drawn = await GeneratedCover.PageAsync(id, ct);

            if (drawn.Cover is not { Length: > 0 } cover || ct.IsCancellationRequested)
            {
                // Said so the square may draw the list's mark: nothing is coming for it now. It is not
                // said on cancellation: the screen is being re-read or closed, and a card that marked
                // itself empty on the way out would open on a glyph the next time it is drawn.
                if (!ct.IsCancellationRequested)
                    card.MarkMissing = true;

                Faces.LogInformation("[home] {Title}: no picture for this list. Its own colour stands.", card.Title);
                return;
            }

            // The room's colours first, because the room is the whole plate and the square is an inch
            // of it: a card recoloured before its picture lands reads as the card arriving, and the
            // other way round reads as it changing its mind. It runs on a worker, because the reading
            // decodes and resizes the picture.
            if (drawn.Wash is { Length: > 0 } wash
                && await Cancellable.RunAsync(() => CoverRoom.Of(wash), ct)
                    is { Ran: true, Value: { } painted }
                && !ct.IsCancellationRequested)
                card.ShowRoom(painted);

            card.Cover = await _pictures.Next(() => RemoteImage.DecodeAsync(cover, ArtScaler.TileEdge));

            Faces.LogInformation("[home] {Title}: the list's own cover, on the plate.", card.Title);
        }
        catch (OperationCanceledException)
        {
            // The shelf was re-read or the view closed.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A source having a bad day is not an answer about this list. Its colour stands.
        }
    }

    /// <summary>A cut picture and the plate width its framing, its mirroring and its feathered edge were
    /// all worked out for. The width is carried out with the bytes because it is read BEFORE two model
    /// runs and a wait for a calm frame, so by the time the cut lands the plate may be somewhere else,
    /// and what the card has to remember is the width the picture actually fits.</summary>
    private readonly record struct PlateCut(byte[] Bytes, double Plate);

    /// <summary>
    /// One photograph, cut to the plate around whoever is in it.
    ///
    /// <para><b>Why not just fit it.</b> The plate is four times as wide as it is tall and a press shot
    /// is not, so something has to go. Fitted to the middle, what goes is the top of the head on a
    /// standing portrait, and the face itself on anyone not standing in the centre of their own
    /// photograph. The half of the plate a face most needs to avoid is the left, which is where the
    /// cover and the text are.</para>
    ///
    /// <para><b>So the models say where the people are.</b> They are the same two the now playing band
    /// and the playlist headers use (the face detector, then U2-Netp for the subject), and the crop is
    /// asked with the PLATE's own obstruction geometry, so the subject lands clear of the cover and
    /// the text and not behind them. See <see cref="HomeFeatureArt"/>, and
    /// <see cref="HomeBanner.Scene"/> for what this plate covers.</para>
    ///
    /// <para><b>What it costs.</b> One pass, on a worker, once per card: both models run at 320 x 320
    /// over a copy of a thumbnail, and the answer is cut out of the picture and kept on the card. A
    /// photograph nobody is in, or a machine with no models installed, falls back to the same cut with
    /// no window named: the picture, squared to the plate. The card is drawn either way.</para>
    /// </summary>
    private async Task<PlateCut?> CutToThePlateAsync(byte[] photo, CancellationToken ct)
    {
        // Not while the screen is moving. The framing runs two models and a Skia pass over a
        // photograph, and it is started by the very press that also steps the banner, so it would land
        // on the frames of the slide, which is where a hitch is most visible. Nothing here is urgent:
        // a plate stands on the record's own colour until its photograph is ready.
        await CalmAsync(ct);

        // The width is read AFTER the wait, and that is load-bearing because a picture is cut once and
        // never again. If it were read before, a card whose fetch beat the first layout would be
        // framed against a plate of zero. That gives the obstruction geometry nothing to work with and
        // puts the subject wherever the fallback lands, and with no refit the card would keep it all
        // session.
        //
        // The band's own arithmetic is used where the plate has still not been given a width, because
        // the plate is told its width by SizeFeatures from exactly that number.
        var plate = FeaturePlate.ActualWidth > 0
            ? FeaturePlate.ActualWidth
            : HomeBanner.PlateWidth(FeatureBand.ActualWidth);

        // Neither has laid out yet. Nothing sensible can be framed against no width, and a picture cut
        // for one is worse than no picture: the card keeps the record's own colour and is asked again
        // when it next comes into a host.
        if (plate <= 0)
            return null;

        var aspect = HomeBanner.Aspect(plate);

        // Off the thread from the first line: the framing decodes the picture before it asks either
        // model anything, and that decode is on whichever thread calls it. A cut the models made on an
        // earlier launch is read back and not made again (see PlateCutCache). Only a settled cut is
        // kept, so a fallback is never what a later launch reads.
        var cut = await Task.Run(async () =>
        {
            var key = Services.Art.PlateCutCache.KeyFor(photo, plate);
            if (Services.Art.PlateCutCache.Find(key) is { } kept)
                return kept;

            var framed = await HomeFeatureArt.FrameAsync(photo, aspect, plate, Faces, ct);
            if (framed.Settled && !ct.IsCancellationRequested)
                Services.Art.PlateCutCache.Keep(key, framed.Bytes);
            return framed.Bytes;
        }, ct);

        return ct.IsCancellationRequested ? null : new PlateCut(cut, plate);
    }

    /// <summary>
    /// One featured card's two pictures: the cover on the left, and the same cover blurred behind the
    /// text.
    ///
    /// <para>There is one fetch for both. The wash is the cover's own bytes blurred on a worker
    /// (<see cref="ArtScaler.Wash"/>), so asking the loader a second time would be the same read from
    /// the same disk, or the same round trip to the computer that holds the record.</para>
    ///
    /// <para>The cover goes through <see cref="Services.Art.CoverFill"/> like every other shelf's,
    /// which is what says there is no artwork as well as what hands one over. The ground does not go
    /// through it and must not: a plate with no ground is a plate in the app's glass, which is a
    /// finished plate, and a verdict written from here would be a second opinion about a read this
    /// did not make.</para>
    /// </summary>
    private async Task FillFeatureAsync(HomeFeatureCard card, CancellationToken ct)
    {
        if (CoverLoader is not { } load || card.Folder is not { Length: > 0 } folder)
            return;

        // The plate's spinner turns while this runs (HomeFeatureCard.PlateWaiting). It is cleared in
        // the finally, so no outcome, cancellation included, leaves it turning.
        card.GroundFetching = true;
        try
        {
            byte[]? sleeve = null;

            await Services.Art.CoverFill.DressAsync(
                card,
                token => load(folder, ArtScaler.TileEdge, token),
                async bytes =>
                {
                    sleeve = bytes;
                    return await _pictures.Next(() => RemoteImage.DecodeAsync(
                        bytes,
                        ArtScaler.DecodeEdgeFor(HomeBanner.Sleeve, BannerPixels)));
                },
                ct);

            if (sleeve is not { Length: > 0 } bytes || ct.IsCancellationRequested)
                return;

            if (await Cancellable.RunAsync(() => ArtScaler.Wash(bytes), ct)
                is not { Ran: true, Value: { Length: > 0 } washed } || ct.IsCancellationRequested)
                return;

            // Written to the CARD, which is why the reader stepping the banner while this is in the air
            // is safe: the card is in the strip whether or not it is the one in front, and a card that
            // is not drawn right now is drawn with its ground the next time it is stepped to.
            //
            // ...unless the artist's photograph has already landed. This is what a card shows for the
            // second or two before that arrives, not what it settles on. See
            // HomeFeatureCard.GroundIsPortrait.
            var drawn = await _pictures.Next(() => RemoteImage.DecodeAsync(washed, ArtScaler.WashEdge));
            if (!card.GroundIsPortrait)
                card.Ground = drawn;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A card with no picture still names the record, which is most of what the banner says.
        }
        finally
        {
            card.GroundFetching = false;
        }
    }
}

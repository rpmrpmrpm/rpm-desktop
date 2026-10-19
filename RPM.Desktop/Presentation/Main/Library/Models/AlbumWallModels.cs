using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// The one live measurement the whole wall sizes to: the card <see cref="Stride"/> (its full width) and
/// the <see cref="Art"/> square inside it. One instance is shared by every card and the open band. When
/// the user drags the size slider, or the panel resizes, the control sets it once. Every bound card then
/// follows, with no push per card. The values are computed in <see cref="RPM.Core.Library.AlbumGridLayout"/>.
/// This class only carries the result to the bindings.
/// </summary>
[RpmBindable]
public sealed partial class AlbumTileMetrics : INotifyPropertyChanged
{
    private double _stride = 166;
    /// <summary>The card's full width, art plus its chrome, and the horizontal stride between
    /// cards.</summary>
    public double Stride { get => _stride; private set { if (_stride == value) return; _stride = value; OnChanged(); } }

    private double _art = 150;
    /// <summary>The edge of the square cover inside the card.</summary>
    public double Art
    {
        get => _art;
        private set
        {
            if (_art == value) return;
            _art = value;
            OnChanged();
            OnChanged(nameof(TitleRoom));
        }
    }

    /// <summary>
    /// How wide a title may be before it has to trim.
    ///
    /// <para>The title and the explicit badge sit in one centred group, so the title cannot take the whole
    /// card. It must leave room for the badge and the gap in front of it. Otherwise a long name would push
    /// the E off the edge of the card. The limit is set here, not in the markup, because it changes with
    /// the size slider.</para>
    /// </summary>
    public double TitleRoom => Math.Max(0, Art - BadgeRoom);

    /// <summary>The badge and the gap in front of it: 12 and 4.</summary>
    private const double BadgeRoom = 16;

    /// <summary>Set both together; each raises change only when it actually moved, so a resize that
    /// keeps the same size is free.</summary>
    public void Set(double stride, double art) { Stride = stride; Art = art; }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// What a card calls a record, and who it says made it.
///
/// <para>Kept apart from the card, because a card cannot be made without a dispatcher (it owns brushes).
/// This is the part with a decision in it. The library is laid out Artist/Album/track, so the folder
/// answers both questions without any tag. That is what fills a wall of two thousand covers before any
/// tag has been read. Tags win where there are any.</para>
/// </summary>
internal static class AlbumCardName
{
    /// <param name="folder">The album's folder, relative to the library root, forward slashes.</param>
    /// <param name="libraryName">What to call the root itself. It holds loose tracks under no artist
    /// folder. A card titled "" is a tile nobody can read.</param>
    public static (string Title, string Artist) For(
        string folder, string libraryName, string? album, string? albumArtist)
    {
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return (
            album ?? (parts.Length == 0 ? libraryName : parts[^1]),
            albumArtist ?? (parts.Length >= 2 ? parts[^2] : ""));
    }
}

/// <summary>
/// One cover in the album grid. The title and artist come from the folder's shape. The library is laid
/// out Artist/Album/track, so the last path segment is the album and the one above it is the artist.
/// This costs nothing to know. The cover is fetched lazily, the first time the card is realized. The band
/// refines the names from the tracks' real tags once it opens.
/// </summary>
[RpmBindable]
public sealed partial class AlbumCard : INotifyPropertyChanged, RPM.Desktop.Presentation.Common.ICloudBadged,
                                RPM.Desktop.Services.Art.ICoverCard
{
    public AlbumCard(string folder, string libraryName, AlbumTileMetrics metrics)
    {
        Folder = folder;
        _libraryName = libraryName;
        Metrics = metrics;
        Name(null, null);
    }

    private readonly string _libraryName;

    public string Folder { get; }

    /// <summary>
    /// What the card calls the record: the album's own tags, with the folder path as the fallback for an
    /// album whose tags say nothing.
    ///
    /// <para>The path is a poor name, and it cannot be made a good one. It must work on every filesystem
    /// the library might live on, so the naming template sanitises it. A band with a slash or a colon in
    /// its name is filed under a spelling that is not its name. The path is also frozen. An album retagged
    /// in place keeps its folder, so a card built from the path would show the old name forever. So the
    /// card is renamed from the mirror on every rebuild. A card is REUSED across rebuilds (see
    /// AlbumWallView.SetAlbums). This is why these two cannot be get-only.</para>
    /// </summary>
    public void Name(string? album, string? albumArtist)
    {
        var (title, artist) = AlbumCardName.For(Folder, _libraryName, album, albumArtist);

        if (Title != title)
        {
            Title = title;
            OnChanged(nameof(Title));
        }
        if (Artist != artist)
        {
            Artist = artist;
            OnChanged(nameof(Artist));
        }
    }

    public string Title { get; private set; } = "";

    /// <summary>The record's name without its edition. This is what the TILE prints.
    ///
    /// <para>A tile is a sleeve with two short lines under it. "(Deluxe Edition)" is the least useful part
    /// for a reader. On a wall of records it repeats the same three words over and over. The drawer the
    /// tile opens still shows the edition, faded and in its place (see AlbumBandView). So it is moved, not
    /// hidden.</para>
    /// </summary>
    public string TitleName => RPM.Core.Library.TitleAsides.NameOnly(Title);

    public string Artist { get; private set; } = "";

    private bool _isExplicit;

    /// <summary>At least one track on the record has explicit lyrics. This puts the E beside its title.
    ///
    /// <para>Any one track counts, not most of them. A record with one explicit song is one the listener
    /// would want to know about. The year and the genre use a majority rule instead, which is what makes a
    /// compilation land somewhere definite. This is set from the shelf's own index (one indexed query for
    /// the whole wall). It is not read from the album's files, because a card is realized while it scrolls
    /// past.</para>
    /// </summary>
    public bool IsExplicit
    {
        get => _isExplicit;
        set { if (_isExplicit == value) return; _isExplicit = value; OnChanged(); }
    }

    private bool _isFavorite;

    /// <summary>
    /// The record is one of the listener's favourites. It has a heart sticker on the corner of its sleeve.
    ///
    /// <para>This is set from the mirror on every rebuild, like the E. A card is REUSED across rebuilds, so
    /// a record favourited or unfavourited since the wall was built would otherwise keep its old mark.</para>
    /// </summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnChanged(); }
    }

    /// <summary>The wall's shared card/art size, the card binds its width and its cover square
    /// here.</summary>
    public AlbumTileMetrics Metrics { get; }

    private ImageSource? _cover;
    public ImageSource? Cover
    {
        get => _cover;
        set { _cover = value; OnChanged(); OnChanged(nameof(HasCover)); }
    }

    /// <summary>Whether this record has a picture at all, from EITHER of the two layers. The second layer
    /// is needed. A record first met at a large tile size gets its sharp picture before the whole-collection
    /// pass has read it. Until that pass runs, the sharp layer is the only picture the record has. If only
    /// the resident picture were counted, the tile would draw the made sleeve over a real cover.</summary>
    public bool HasCover => _cover is not null || _sharp is not null;

    private ImageSource? _sharp;

    /// <summary>
    /// The full-size picture, drawn on its own layer OVER <see cref="Cover"/> and faded in.
    ///
    /// <para>It is kept separate from the resident one, not in its place. Replacing it would be visible. A
    /// tile that swaps a small picture for a large one in a single assignment jumps. A wall of tiles that
    /// jump as the listener scrolls looks unstable. Two layers make the same change look smooth. The small
    /// picture stays where it is, and the sharp one fades in on top of it.</para>
    ///
    /// <para>This layer also costs memory, so a recycled tile gives it back first. Set it to null and the
    /// card keeps its small resident picture, which is still a cover.</para>
    /// </summary>
    public ImageSource? Sharp
    {
        get => _sharp;
        set { _sharp = value; OnChanged(); OnChanged(nameof(HasCover)); }
    }

    /// <summary>The encoded bytes behind <see cref="Tiny"/>. They are kept so the resident pack can be
    /// written from what the wall has already read, without reading the whole collection a second time.
    /// Each is a few kilobytes. This is the compressed form, not the decoded one. See ThumbPack.</summary>
    byte[]? RPM.Desktop.Services.Art.ICoverCard.ResidentBytes { get => ResidentBytes; set => ResidentBytes = value; }

    internal byte[]? ResidentBytes;

    /// <summary>There is no art at all for this record: not in the folder, not in its tags, and not in the
    /// cloud. This is set once, so the loader stops offering it as work. A wall of records with no cover
    /// must not cost a disk read per record on every pass for the rest of the session.</summary>
    bool RPM.Desktop.Services.Art.ICoverCard.CoverMissing { get => CoverMissing; set => CoverMissing = value; }

    private bool _coverMissing;

    /// <summary>The loader's verdict. It is a property, not a field, because the tile draws from it.
    /// See <see cref="FigureSeed"/>.</summary>
    internal bool CoverMissing
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

    /// <summary>What the made sleeve is drawn from, and null while there is nothing to draw.
    ///
    /// <para>The record's own folder. It is given ONLY after the loader has read everywhere a cover could
    /// be and found nothing (<see cref="CoverMissing"/>). It is never given just because no picture has
    /// landed yet. This difference matters. The tile is realized long before its cover is read. If a seed
    /// meant "no cover on screen", every record on the wall would paint a figure that its own artwork then
    /// covers.</para>
    ///
    /// <para>Null clears the brush. So a record given artwork later drops its figure, rather than keeping
    /// it underneath. See <see cref="RPM.Desktop.Converters.SleeveFigureConverter"/>.</para>
    /// </summary>
    public string? FigureSeed => _coverMissing ? Folder : null;

    /// <summary>The edge a read came back empty at. The loader does not ask again at that edge, but it does
    /// ask again at any other edge. See <see cref="CoverTier.WorthAsking"/>. This separates "this record has
    /// no art" (<see cref="CoverMissing"/>, set once and for good) from "nobody could hand it over just now".
    /// The second case is a file still being written, or a computer that did not answer.</summary>
    int RPM.Desktop.Services.Art.ICoverCard.MissedEdge { get => MissedEdge; set => MissedEdge = value; }

    internal int MissedEdge;

    /// <summary>Whether the mirror has been asked for this record's cloud mark. Same purpose: the loader
    /// walks what is on screen looking for work, and an answered card has to fall out of that.</summary>
    internal bool BadgeRead;

    /// <summary>The pixel edge this card's picture was actually DECODED at. It is not the edge the picture
    /// is drawn at, and it is not the edge of the source file. A change of tile size uses it to find the
    /// cards whose pictures now have the wrong density.</summary>
    int RPM.Desktop.Services.Art.ICoverCard.CoverEdge { get => CoverEdge; set => CoverEdge = value; }

    internal int CoverEdge;

    /// <summary>
    /// The record's cover at the resident tier's edge (<see cref="ArtScaler.ResidentEdgeFor"/>), held for
    /// as long as the wall is, this is the one picture a card never gives back.
    ///
    /// <para>This is what lets the wall appear at once. Every record in the collection has one within a
    /// second of the shelf being fed. So a tile realized during a fast scroll has a picture to paint on
    /// the frame it appears, not after a disk read. A recycled tile falls back to it, not to nothing. Each
    /// is about sixty-five kilobytes of pixels on an ordinary display, and about 147 on a doubled one. One
    /// full-size decode costs about a megabyte and a half. So the wall keeps these within a byte budget,
    /// not a count of records.</para>
    /// </summary>
    ImageSource? RPM.Desktop.Services.Art.ICoverCard.Tiny { get => Tiny; set => Tiny = value; }

    internal ImageSource? Tiny;

    // There is no cancellation token here, on purpose. The loader pulls work instead of having it pushed.
    // It asks what is worth doing now, and does one of those things. A card scrolled away simply stops
    // being the answer to that question. So nothing is in flight that another part must cancel.
    // See the loader in AlbumWallView.

    private bool _isExpanded;
    /// <summary>The open card wears a highlight ring; the band it owns lives on the row.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; OnChanged(); }
    }

    private AlbumCloudBadge _cloudBadge;
    /// <summary>How much of this album is in the cloud. It shows as a small cloud under the sleeve in the
    /// record's drawer. The tile itself carries only its cover and name. It is loaded when the card is
    /// realized, and refreshed when the cloud state changes.</summary>
    public AlbumCloudBadge CloudBadge
    {
        get => _cloudBadge;
        set
        {
            if (_cloudBadge == value) return;
            _cloudBadge = value;
            OnChanged();
            OnChanged(nameof(ShowCloudBadge));
            OnChanged(nameof(IsCloudBusy));
            if (!IsCloudBusy)
                CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;   // Ends any pulse in progress, at full opacity
        }
    }

    /// <summary>The badge shows at all only when some of the album is in the cloud.</summary>
    public bool ShowCloudBadge => _cloudBadge != AlbumCloudBadge.None;

    /// <summary>Either direction of work, going up or going out, is what pulses. The state itself is drawn
    /// by <see cref="RPM.Desktop.Presentation.Controls.CloudBadge"/> from <see cref="CloudBadge"/>. The card
    /// only has to say whether the mark is moving.</summary>
    public bool IsCloudBusy =>
        _cloudBadge is AlbumCloudBadge.Working or AlbumCloudBadge.Leaving;

    private double _cloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;

    /// <summary>The badge's opacity. The wall's tick changes it on each frame while the album is busy.
    /// Otherwise it stays at rest.</summary>
    public double CloudPulse
    {
        get => _cloudPulse;
        set { if (_cloudPulse == value) return; _cloudPulse = value; OnChanged(); }
    }

    private Brush? _ring;
    /// <summary>The open card's ring, in the same colour as its drawer's background. The tint is muted
    /// while the drawer is still opening. Then the sleeve's own background is used, once the drawer has
    /// read it (see LoadBandAsync), so the ring and the drawer look like one. Where the cover has no vivid
    /// colour, or no colour at all, the drawer's default background is used. So a card with no cover never
    /// shows a stray green ring.</summary>
    /// <remarks>Made on first read rather than in a field initializer: a Brush asks for the thread's
    /// DispatcherQueue when it is constructed, and nothing else about a card needs one.</remarks>
    public Brush Ring
    {
        get => _ring ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x22, 0x2C));
        set { _ring = value; OnChanged(); }
    }

    /// <summary>The cover's highlight tint (raw, undimmed). It is cached when the cover is decoded. An
    /// opening drawer can then paint its final background and ring at once, instead of flashing the
    /// default colour and repainting.</summary>
    public Windows.UI.Color? Tint { get; set; }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Whether the drawer that is open survives the album feed that has just landed.
///
/// <para>The wall is fed again for many reasons: a keystroke in the search box, a favourite, a record
/// going up to the cloud, or a shelf borrowed over the LAN and given back. Most of these return the same
/// records, and the drawer should stay open. Two cases must close it. The first is a record that is no
/// longer on the wall: narrowed away, offloaded or deleted. The second is a different collection
/// altogether. Then every card is a new instance, and the open band belongs to a wall that is gone.</para>
/// </summary>
public static class AlbumDrawerSurvival
{
    /// <param name="openFolder">The album folder the drawer is showing, or null when none is open.</param>
    /// <param name="sameLibrary">Whether the feed is the same collection the wall is already showing.</param>
    /// <param name="stillOnWall">Whether a folder is among the records the feed handed over.</param>
    public static bool Survives(string? openFolder, bool sameLibrary, Func<string, bool> stillOnWall)
        => openFolder is { } folder && sameLibrary && stillOnWall(folder);
}

/// <summary>
/// A row of the grid: a fixed run of cards plus, when one of them is open, the flip-down band beneath
/// it. The band is part of the row. This lets the whole grid virtualise as a plain vertical list. The
/// band moves with its cards and pushes the rows below it down, so no custom layout is needed.
/// </summary>
[RpmBindable]
public sealed partial class AlbumRow : INotifyPropertyChanged
{
    /// <summary>The row's cards. There is one collection for the row's whole life. It is changed in place
    /// on a reflow (a column count change on resize), not reassigned. Reassigning the source resets the
    /// inner ItemsControl, which causes a blank frame and a flicker. Adding, removing or replacing items on
    /// the same collection updates only the containers that moved, with no blank.</summary>
    /// <remarks>Its own collection type so a {Binding} can hand it to the row's repeater under Native
    /// AOT; see <see cref="Common.AlbumCardList"/>.</remarks>
    public Common.AlbumCardList Cards { get; } = new();

    private AlbumBand? _band;
    public AlbumBand? Band
    {
        get => _band;
        set { _band = value; OnChanged(); OnChanged(nameof(HasBand)); }
    }

    public bool HasBand => _band is not null;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The detail that flips down under a row: the album's tint, its big cover, and its tracks in two
/// columns, with the pointer triangle sitting over the card it belongs to. It starts as a loading shell.
/// The tint, names and tracks arrive from a background fetch once the band opens.
/// </summary>
[RpmBindable]
public sealed partial class AlbumBand : INotifyPropertyChanged
{
    private const double TriangleHalf = BandPointer.TriangleHalf;

    public AlbumBand(AlbumCard card, int columnIndex)
    {
        Card = card;
        ColumnIndex = columnIndex;
        _title = card.Title;
        _subtitle = card.Artist;
        _isExplicit = card.IsExplicit;
        _isFavorite = card.IsFavorite;
    }

    public AlbumCard Card { get; private set; }

    /// <summary>Which card in the row the band belongs to. The triangle points up at it.</summary>
    public int ColumnIndex { get; private set; }

    // ── visual state (recycle-proof) ──
    // The drawer's open or closed VISUAL state lives on the model, not on the element. So a recycled or
    // re-seated element always applies the right state when it is realized (BandRoot_Loaded,
    // DataContextChanged). This stops a resize from leaving the drawer's row reserved but blank.
    // Owned by BandConductor.

    /// <summary>One-shot. The next time the drawer element is realized, it plays the unfold, and then
    /// this is cleared. A fresh open sets it. A reflow re-seat leaves it false, so the element snaps
    /// straight to open.</summary>
    public bool AnimateOpen { get; set; } = true;

    /// <summary>True while the drawer is folding shut. The close animation owns the element, so the
    /// realize-time visual sync leaves it alone.</summary>
    public bool IsClosing { get; set; }

    /// <summary>Moves a fully loaded band into its new row and column after a reflow (the column count
    /// changed). Its cover, tint and tracks are kept. Nothing is reloaded, so there is no flash of the
    /// default background and no spinner.</summary>
    public void Reseat(int columnIndex)
    {
        ColumnIndex = columnIndex;
        OnChanged(nameof(PointerMargin));
    }

    /// <summary>Points the same open drawer at a neighbour album (a switch within the same row). The
    /// pointer slides to the new card, and the title is taken up front. The caller sets the cover, tint and
    /// tracks again. The drawer element and its reserved space are kept, so the drawer does not collapse
    /// and expand.</summary>
    public void Retarget(AlbumCard card, int columnIndex)
    {
        Card = card;
        // The drawer reads the record's cloud marks straight from the card (Card.CloudBadge). So a retarget
        // must say that the card itself changed. Otherwise the marks under the sleeve stay those of the
        // last album.
        OnChanged(nameof(Card));
        ColumnIndex = columnIndex;
        Title = card.Title;
        Subtitle = card.Artist;
        IsExplicit = card.IsExplicit;
        IsFavorite = card.IsFavorite;
        OnChanged(nameof(PointerMargin));
    }

    /// <summary>The left margin that puts the band's pointer triangle over the card it belongs to:
    /// <c>columnIndex * stride + stride/2 - triangleHalf</c>. It reads the wall's live stride, so a card
    /// resize moves the pointer with the cards. <see cref="RefreshPointer"/> raises it again after a size
    /// change that does not rebuild the band.</summary>
    public Thickness PointerMargin =>
        new(Math.Max(0, ColumnIndex * Card.Metrics.Stride + Card.Metrics.Stride / 2 - TriangleHalf), 0, 0, 0);

    /// <summary>Nudge the pointer to follow a live card-size change (the band survives the resize).</summary>
    public void RefreshPointer() => OnChanged(nameof(PointerMargin));

    private string _title;
    public string Title { get => _title; set { _title = value; OnChanged(); } }

    private string _subtitle;
    public string Subtitle { get => _subtitle; set { _subtitle = value; OnChanged(); } }

    private bool _isExplicit;

    /// <summary>The open record has an explicit track on it. This shows the E beside the big heading. It is
    /// carried here, not read through <see cref="Card"/>. A retarget swaps the card without notifying, so a
    /// binding through the card would keep the last record's badge.</summary>
    public bool IsExplicit { get => _isExplicit; set { if (_isExplicit == value) return; _isExplicit = value; OnChanged(); } }

    private bool _isFavorite;

    /// <summary>The open record is one of the listener's favourites. It shows the sticker on the big sleeve
    /// in the drawer. It is carried here for the same reason as the E. A retarget swaps the card without
    /// notifying, so a binding through the card would keep the last record's mark.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnChanged(); }
    }

    private ImageSource? _cover;
    public ImageSource? Cover { get => _cover; set { _cover = value; OnChanged(); OnChanged(nameof(HasCover)); } }
    public bool HasCover => _cover is not null;

    /// <summary>
    /// Whether the heart under the sleeve is drawn at all. It is always drawn.
    ///
    /// <para>A favourite on a borrowed record is a row in the mirror held for that computer. It is not a
    /// row in this library. The mark on that row is the listener's own: what they think of a record they
    /// borrowed. It is kept at this computer and never sent back. The record's own menu offers the same
    /// entry on the same terms, and this must agree with that menu.</para>
    ///
    /// <para>Kept as a property, not folded into the markup, because the binding names it. Changing this
    /// answer means changing the menu too.</para>
    /// </summary>
    public bool CanMark => true;

    // ── the ground: the record's own sleeve, the way the album page wears it ──
    //
    // There are three layers, not one brush: the colour under everything, the sleeve blurred over it, and
    // a black veil inside the blur that takes back the exposure. SleeveGround reads these. It is the same
    // reader the album page uses, so a record's drawer and its page stand on the same ground.
    //
    // The sleeve is read AFTER the drawer has opened. So the drawer first opens on the app's own card
    // plate (the template's brush, not set by this model), and then the record's ground crosses over it.
    // GroundOn is that crossing. It lives on the model, not in the elements, because three separate
    // elements carry the ground (the colour, the wash and the pointer triangle). They must move as ONE.
    // This also keeps the fade safe when the element is recycled. A drawer re-seated in the middle of a
    // crossing binds the value the crossing has reached, and does not snap to either end.

    private Brush? _ground;
    /// <summary>The colour the sleeve makes. It is drawn over the drawer's plate and faded in with
    /// <see cref="GroundOn"/>. It is null until the sleeve has been read.</summary>
    public Brush? Ground { get => _ground; set { _ground = value; OnChanged(); } }

    private double _groundOn;
    /// <summary>How far the sleeve's ground has crossed over the colour the drawer opened with, from 0 to 1.
    /// Every layer that belongs to the sleeve follows this one number.</summary>
    public double GroundOn { get => _groundOn; set { if (_groundOn == value) return; _groundOn = value; OnChanged(); } }

    private BitmapImage? _wash;
    /// <summary>The sleeve, blurred until it is no longer a picture. It is drawn over <see
    /// cref="Background"/> at <see cref="SleeveGround.WashStrength"/>. It is null on a record with no
    /// cover. It is cleared when the drawer is pointed at another record. The drawer is re-seated and
    /// reused, so a wash left behind would show the last album's sleeve under this album's
    /// tracks.</summary>
    public BitmapImage? Wash { get => _wash; set { _wash = value; OnChanged(); OnChanged(nameof(WashStrength)); } }

    /// <summary>How much of the wash is let through. It is zero when there is no sleeve. It is derived,
    /// not set, so the value lives in one place for the drawer and the album page.</summary>
    public double WashStrength => _wash is null ? 0 : SleeveGround.WashStrength;

    private double _veil;
    /// <summary>How much flat black is drawn inside the wash, from 0 to 1. It takes back the sleeve's own
    /// exposure, so white text can sit on it. See <see cref="CoverColor.Veil"/>.</summary>
    public double Veil { get => _veil; set { _veil = value; OnChanged(); } }

    private Brush? _pointerBrush;
    /// <summary>
    /// The pointer triangle's fill: the one colour that its part of the drawer reaches, faded to nothing at
    /// the point. A triangle a shade off the edge it meets looks like a shadow under it. The shape has no
    /// wash to carry, so it takes the drawer's three layers flattened (<see cref="SleeveGround.Flat"/>).
    /// See <see cref="BandPointer.Ramp"/>.
    ///
    /// <para>It is REPLACED a frame at a time while the sleeve's ground crosses in. It is not a second
    /// triangle fading over the first. Two ramped triangles stacked are not the same as one ramp. They are
    /// one alpha over another, and the drawer's mark would come out darker and a different hue from the
    /// drawer it points at. There is one triangle, and its colour is what changes.</para>
    /// </summary>
    /// <remarks>Made on first read, for the same reason as <see cref="AlbumCard.Ring"/>.</remarks>
    public Brush PointerBrush
    {
        get => _pointerBrush ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x22, 0x2C));
        set { _pointerBrush = value; OnChanged(); }
    }


    // Their own list type, not IReadOnlyList. Both columns are bound to an ItemsSource. Under Native AOT
    // a bound list must be a type that CsWinRT has generated WinRT interfaces for. See
    // Common.AlbumTrackItemList (rule RPM007).
    private Common.AlbumTrackItemList _left = [];
    public Common.AlbumTrackItemList Left { get => _left; private set { _left = value; OnChanged(); } }

    private Common.AlbumTrackItemList _right = [];
    public Common.AlbumTrackItemList Right { get => _right; private set { _right = value; OnChanged(); } }

    /// <summary>Every line of the tracklist in reading order: the tracks, and on a multi-disc album the
    /// disc dividers. The columns are cut from this list, so a resize re-cuts them without a
    /// reload.</summary>
    private IReadOnlyList<AlbumTrackItem> _lines = [];

    /// <summary>The same lines, for code that re-reads them in place. The cloud marks are given to the list
    /// the band already has, not to a rebuilt one. So a re-badge never re-lays out the drawer.</summary>
    public IReadOnlyList<AlbumTrackItem> Lines => _lines;

    private bool _twoColumns = true;
    /// <summary>Two columns when the drawer is wide enough for them, otherwise one. Two columns would clip
    /// the titles long before the drawer runs out of room. So a narrow drawer stacks the whole list (disc 1
    /// over disc 2). The control sets this from its measured width.</summary>
    public bool TwoColumns
    {
        get => _twoColumns;
        set { if (_twoColumns == value) return; _twoColumns = value; OnChanged(); LayOutColumns(); }
    }

    /// <summary>Hand the band its tracklist; it cuts the columns to the width it is currently at.</summary>
    public void SetLines(IReadOnlyList<AlbumTrackItem> lines)
    {
        _lines = lines;
        LayOutColumns();
    }

    private void LayOutColumns()
    {
        if (!_twoColumns)
        {
            Left = [.. _lines];
            Right = [];
            return;
        }
        // Copied, not cast. The split builds Lists today, but its declared type is IReadOnlyList. The copy
        // means this binding does not depend on that detail. It is a few dozen lines, once per re-cut.
        var (left, right) = AlbumGridLayout.SplitTwoColumns(_lines, i => i.IsDiscHeader);
        Left = [.. left];
        Right = [.. right];
    }

    private bool _loading = true;
    public bool Loading { get => _loading; set { _loading = value; OnChanged(); OnChanged(nameof(Ready)); } }
    public bool Ready => !_loading;

    /// <summary>Every track in reading order. Play-all, shuffle and queue act on this list. Disc dividers
    /// are lines, not tracks, so they are left out.</summary>
    public IReadOnlyList<AlbumTrackItem> AllTracks => [.. _left.Where(i => i.IsTrack), .. _right.Where(i => i.IsTrack)];

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>The live per-album facts the wall needs to build a card's right-click menu correctly: which
/// cloud items to offer, whether the album is already a favourite, and whether it has a Discogs identity to
/// open. The wall stays library-agnostic. The page fills this in from the database.</summary>
/// <param name="HasLocalFiles">At least one of the record's tracks has its bytes on this disk, so there is
/// a folder to open. False for a record that is only in the cloud, because nothing has been written to the
/// path it would name.</param>
/// <param name="IsCloudOnly">The record's bytes are not on this disk. It was offloaded from here, or it
/// belongs to another of the user's installs and has only ever been in the cloud. It plays either way. This
/// is what offers to bring it home.</param>
/// <param name="CanUpload">Whether the cloud has room for what "add to cloud" would send. False greys out
/// that one entry (see <see cref="MenuCloudEntries"/>). It is read when the menu opens, so the menu follows
/// a store that fills up, a record that is removed, or a supporter plan that is bought, without any
/// refresh.</param>
/// <param name="Delete">Which of the three Delete entries are enabled and which are greyed out. All three
/// are always shown (see <see cref="RPM.Core.Library.DeleteOffer"/>). So the submenu shows, by what it
/// greys out, where this record's copies are.</param>
/// <param name="Borrowed">This record belongs to somebody else's collection, on their computer. Everything
/// the menu offers that would change it, move it or throw it away is removed, not greyed out. An app should
/// not offer to delete another person's record.</param>
public sealed record AlbumMenuState(
    bool CloudSelectedMode,
    bool CanAddToCloud,
    bool IsFavorite,
    bool HasDiscogsId,
    bool HasLocalFiles,
    RPM.Core.Library.DeleteOffer Delete = default,
    bool IsCloudOnly = false,
    bool CanUpload = true,
    bool Borrowed = false);

/// <summary>An action chosen from an album card's right-click menu. The wall raises it with the album's
/// folder. The page does the work.</summary>
public enum AlbumMenuCommand
{
    AddToCloud,

    /// <summary>Bring a record that is only in the cloud down onto this disk.</summary>
    KeepOnComputer,

    AddToPlaylist,
    Play,
    Shuffle,
    PlayNext,

    /// <summary>Hand the whole record to the DJ: the queue becomes the album, and the DJ mixes it.</summary>
    StartDj,
    AlbumPage,
    ToggleFavorite,
    WriteReview,
    ShowInDiscogs,

    /// <summary>Open the record's own folder in the file manager: Explorer on Windows, Finder on a
    /// Mac, whatever is registered on Linux.</summary>
    ShowInFolder,
    ShareAlbum,
    EditAlbum,

    /// <summary>Move the record's files on this disk to the trash. The cloud copy is left alone, so a
    /// record that has one stays in the library and turns cyan.</summary>
    DeleteLocal,

    /// <summary>Delete the record's cloud copy. The files here are left alone, so a record that has
    /// them stays in the library.</summary>
    DeleteCloud,

    /// <summary>Both, and the record with them. The only one of the three that always takes the record
    /// out of the library.</summary>
    DeleteEverywhere,
}

/// <summary>One line of a tracklist: either a track, its number, title and length, over the TrackInfo
/// the player and the RPaMp queue act on, or the divider that opens a disc in a multi-disc album
/// (<see cref="IsDiscHeader"/>). The playing line bolds itself; the control drives it from the shared
/// PlaybackService.</summary>
[RpmBindable]
public sealed partial class AlbumTrackItem : INotifyPropertyChanged, RPM.Desktop.Presentation.Common.ICloudBadged
{
    /// <param name="displayTitle">What to print instead of the file's title: the movement alone, under the
    /// work that names the rest of it. The file keeps the whole title either way.</param>
    /// <param name="subtitle">The line under the title: who wrote it, for a piece that stands as a work in
    /// its own right.</param>
    public AlbumTrackItem(TrackInfo info, string? displayTitle = null, string? subtitle = null)
    {
        Subtitle = subtitle ?? "";
        Info = info;
        _isFavorite = info.IsFavorite;
        Number = info.TrackNumber > 0 ? info.TrackNumber.ToString() : "";
        Title = !string.IsNullOrWhiteSpace(displayTitle) ? displayTitle
            : string.IsNullOrWhiteSpace(info.Title) ? Path.GetFileNameWithoutExtension(info.FilePath) : info.Title!;
        Duration = FormatDuration(info.DurationSeconds);
        IsExplicit = info.IsExplicit;
    }

    private AlbumTrackItem(string header)
    {
        IsDiscHeader = true;
        Title = header;
        Number = "";
        Duration = "";
    }

    /// <summary>The line that opens a work: its name, who wrote it, and how long the whole piece
    /// runs.</summary>
    private AlbumTrackItem(string work, string? composer, double seconds)
    {
        IsWorkHeader = true;
        Title = work;
        Subtitle = composer ?? "";
        Number = "";
        Duration = FormatDuration(seconds);
    }

    /// <summary>
    /// An album's lines in reading order: disc by disc, track by track, with a "CD n" divider in front
    /// of each disc when the album has more than one. Single-disc albums, nearly all of them, get the
    /// plain track list with no divider.
    /// </summary>
    /// <param name="classical">Whether the album is classical, as its owner set it. A record is classical
    /// only because somebody said so, never because its tags look the part. A classical record is drawn
    /// the way it is published: a list of works, each with its movements under it.</param>
    /// <param name="uploadsStopped">Whether the cloud engine is sending nothing, for example because the
    /// store is full. It is passed to <see cref="ApplyCloud"/>. So a record drawn while the queue is stopped
    /// is drawn at rest, and does not start a pulse that the next cloud change would have to stop.</param>
    /// <param name="shelf">Whose collection this record is on, which decides what its marks mean.</param>
    public static List<AlbumTrackItem> Build(
        IReadOnlyList<TrackInfo> tracks, bool classical = false, bool uploadsStopped = false,
        RPM.Core.Cloud.ShelfReading shelf = RPM.Core.Cloud.ShelfReading.Own)
    {
        var discs = AlbumTracklist.ByDisc(tracks);
        var second = SecondLine(tracks, classical);

        List<AlbumTrackItem> items;
        if (discs.Count <= 1)
        {
            var only = discs.Count == 1 ? discs[0].Tracks : [.. tracks];
            items = classical ? Lines(only, second) : [.. only.Select(t => new AlbumTrackItem(t, null, second(t)))];
        }
        else
        {
            items = new List<AlbumTrackItem>(tracks.Count + discs.Count);
            foreach (var disc in discs)
            {
                items.Add(new AlbumTrackItem(HeaderText(disc)));
                items.AddRange(classical
                    ? Lines(disc.Tracks, second)
                    : disc.Tracks.Select(t => new AlbumTrackItem(t, null, second(t))));
            }
        }

        // The cloud marks come from the same tracks the lines were made of. So a record that is partly in
        // the cloud shows that as soon as it is drawn, not only at the next cloud change.
        ApplyCloud(items, tracks, uploadsStopped, shelf);
        return items;
    }

    /// <summary>
    /// One disc's lines: works with their movements under them, and the pieces that are a work all by
    /// themselves.
    ///
    /// <para>A Fantasy on its own is not a lesser piece than the sonata above it. It is another piece on
    /// the record, and the only difference is that it takes one track instead of three. So it is drawn at
    /// the same level and in the same weight as a work heading, with its composer under it. It stays a
    /// playable line, because it IS a track. Only movements are indented, because only a movement is part
    /// of something else.</para>
    /// </summary>
    private static List<AlbumTrackItem> Lines(IReadOnlyList<TrackInfo> tracks, Func<TrackInfo, string?> second)
    {
        var lines = new List<AlbumTrackItem>(tracks.Count + 4);
        foreach (var (work, group) in AlbumTracklist.ByWork(tracks))
        {
            // A one-track work needs no heading of its own. The track's line already gives the whole
            // name, and a heading over a single line would only repeat it.
            if (work is not null && group.Count > 1)
                lines.Add(new AlbumTrackItem(
                    work,
                    // The composer of the piece, where the tracks agree on one. They may disagree, as on a
                    // pasticcio or a badly tagged box set. Printing the first track's composer for all of
                    // them would invent a composer that nobody gave.
                    group.Select(second)
                         .Distinct(StringComparer.CurrentCultureIgnoreCase)
                         .ToList() is [{ Length: > 0 } only] ? only : null,
                    group.Sum(t => t.DurationSeconds)));

            bool movements = work is not null && group.Count > 1;
            lines.AddRange(group.Select(t => new AlbumTrackItem(
                t,
                movements ? MovementOf(t, work) : null,
                // A piece standing on its own carries its own composer, exactly as a work heading
                // carries the run's.
                movements ? null : second(t))
            {
                InWork = movements,
                IsPiece = !movements,
            }));
        }
        return lines;
    }

    /// <summary>
    /// What the small line under a track's title says on this record. On most records it says nothing.
    ///
    /// <para>On a classical record it gives the composer. The performer is on the sleeve, and the composer
    /// is what the piece IS. That is why this line exists at all. On everything else the composer is not
    /// what anyone reads a tracklist for. A pop album with a COMPOSER tag would print a songwriter under
    /// every title, which answers a question nobody asked.</para>
    ///
    /// <para>The line is still useful on a various-artists record (a compilation or a soundtrack). Every
    /// track there is by somebody else, and the record's own heading says "Various Artists" and nothing
    /// more. So the track's own artist takes the composer's place, which is the one thing the heading
    /// cannot say. A record by one artist leaves the line empty, because the artist was already said once
    /// above, for all of them.</para>
    /// </summary>
    private static Func<TrackInfo, string?> SecondLine(IReadOnlyList<TrackInfo> tracks, bool classical)
    {
        if (classical)
            return t => t.Composers;

        return AlbumArtistFill.IsVariousArtists(tracks) ? t => t.Artist : _ => null;
    }

    /// <summary>
    /// What a movement's own line says, under a work that has already said the rest.
    ///
    /// <para>A file's title carries the whole name, such as "Sonata In C Major, KV. 330: Allegretto". Every
    /// program that does not know about works reads that field. Under the heading the sonata's name would
    /// appear three times. So the movement is shown alone: the movement tag where the file has one, and
    /// otherwise what is left of the title once the work in front of it is removed.</para>
    /// </summary>
    private static string? MovementOf(TrackInfo track, string? work)
    {
        if (work is null)
            return null;

        if (!string.IsNullOrWhiteSpace(track.MovementName))
            return track.MovementName;

        var title = track.Title;
        return title is not null && title.Length > work.Length &&
               title.StartsWith(work, StringComparison.CurrentCultureIgnoreCase)
            ? title[work.Length..].TrimStart(' ', ':', '-', '\u2013', '\u2014')
            : null;
    }

    /// <summary>The divider's caption: the disc's own title after its number when the release names
    /// it ("CD 2: B-Sides"), the number alone otherwise.</summary>
    private static string HeaderText(AlbumDiscSection disc)
    {
        var label = Strings.Loc.Format("Album_DiscHeader", disc.DiscNumber);
        return string.IsNullOrWhiteSpace(disc.Subtitle) ? label : $"{label}: {disc.Subtitle}";
    }

    /// <summary>Null on a disc divider, because a divider stands for no file.</summary>
    public TrackInfo? Info { get; }

    /// <summary>True when this line is a disc divider rather than a track.</summary>
    public bool IsDiscHeader { get; }

    /// <summary>True when this line opens a work. It is the parent line of a run of movements.</summary>
    public bool IsWorkHeader { get; }

    public bool IsTrack => !IsDiscHeader && !IsWorkHeader;

    /// <summary>How tall this line is, in track lines. A track is one line. A disc divider and a work
    /// header are two lines each. This is the height the album list's template gives them. So the room a
    /// record needs can be counted before its tracks have been read (see
    /// AlbumListEntry.ReservedTrackHeight).</summary>
    public int LineUnits => IsTrack ? 1 : 2;

    /// <summary>What the line says under its name. See <see cref="SecondLine"/>. On a classical record it is
    /// the composer. On a various-artists record it is the track's own artist. Nowhere else is it used. A
    /// movement never has one, because the work above it has already said it.</summary>
    public string Subtitle { get; } = "";

    public bool HasSubtitle => Subtitle.Length > 0;

    /// <summary>Whether this track is a movement of the work above it. The indent shows which level of the
    /// list a line is on. So a piece that stands on its own, after a sonata, does not look like one more
    /// of that sonata's movements.</summary>
    public bool InWork { get; init; }

    public Microsoft.UI.Xaml.Thickness Indent => new(InWork ? 18 : 0, 0, 0, 0);

    /// <summary>
    /// A track that is a whole piece: it belongs to no work, or it is the only track of one.
    ///
    /// <para>Drawn at the level of a work heading and in its weight, because that is what it is. The
    /// Fantasy between two sonatas is another piece on the record, not a footnote to the one above it.
    /// It stays a track line, though. Unlike a heading, there is something here to play.</para>
    /// </summary>
    public bool IsPiece { get; init; }

    /// <summary>Semibold on a piece, so the top level of the list reads as one level, whether the line is
    /// a heading or a playable piece.</summary>
    public Windows.UI.Text.FontWeight TitleWeight => IsPiece
        ? Microsoft.UI.Text.FontWeights.SemiBold
        : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>A piece is set at the heading's size, and a movement at the track size. A work is a work
    /// whether it has three movements or one. The two must not look like different kinds of line.</summary>
    public double TitleSize => IsPiece ? 12.5 : 12;

    /// <summary>
    /// Where the track number is drawn.
    ///
    /// <para>A movement's number is in the list's number column, right-aligned, so the digits line up as
    /// they climb past nine. A piece's number is not in that column. It is shown in front of the title,
    /// and the title takes the whole row from the left margin. So the digit starts on the same vertical as
    /// the work above it, with the same gap after it that a movement has. In the column, it would sit
    /// wherever the column's spare space put it.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Visibility NumberInColumn => IsPiece
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The same number, in front of the name, a piece only.</summary>
    public Microsoft.UI.Xaml.Visibility NumberInline => IsPiece
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>Which column the name starts in: the number's own on a piece, the one after it on a
    /// movement.</summary>
    public int TitleColumn => IsPiece ? 0 : 1;

    public int TitleSpan => IsPiece ? 2 : 1;

    /// <summary>
    /// Where the star is drawn. It goes where the NUMBER goes: it is a mark on the line's number, not a
    /// column of its own. A column for it at the head of every row would leave twenty-three points of
    /// empty gutter down the left of the list. The heart on the right needs only eight points. So the star
    /// shares the number's cell. It sits there with the digits right-aligned, so the numbers still line up,
    /// and the row starts where the heart's side ends.
    ///
    /// <para>A PIECE has no number in that cell. Its number is in front of its name, and its name starts in
    /// the cell. So its star is shown there too. This is the same question as
    /// <see cref="NumberInColumn"/>, with the same two answers. It is asked separately, because a star and a
    /// number are not always on the same line.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Visibility StarInColumn => IsMostPopular && !IsPiece
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The same star, in front of the name, a piece only.</summary>
    public Microsoft.UI.Xaml.Visibility StarInline => IsMostPopular && IsPiece
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string FilePath => Info?.FilePath ?? "";
    public string Number { get; }
    public string Title { get; }
    public string Duration { get; }

    /// <summary>Explicit lyrics: the E before the title. False on a disc divider, which stands for no file
    /// and so has nothing to be explicit about.</summary>
    public bool IsExplicit { get; }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnChanged();
            // The bars and the cloud mark share one slot in front of the name. See ShowCloudBadge.
            OnChanged(nameof(ShowCloudBadge));
        }
    }

    // ── the heart at the end of the line ──
    //
    // A favourite is the one mark on a track line that the listener sets. So it is also the one that must
    // be clickable. It sits after the length, on the far side of the row from the marks the app writes
    // (the equaliser and the cloud mark). It is drawn only on a track line. A disc divider and a work
    // heading stand for no file, so there is nothing to favourite.

    private bool _isFavorite;

    /// <summary>The listener has made this song a favourite (the filled heart). It is set from the mirror
    /// when the line is built, and again by the click that toggles it. So the heart answers at once,
    /// without reading the record again.</summary>
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

    /// <summary>The outline heart's condition. A binding cannot negate a value, so this is a
    /// property.</summary>
    public bool IsNotFavorite => !_isFavorite;

    private bool _isMostPopular;

    /// <summary>
    /// The song on this record that is played most often. It gets the star at the head of the line.
    ///
    /// <para>Nobody at this desk set it. That is what makes it different from the other marks on a line.
    /// The heart is the listener's, and the equaliser is the player's. This is a report about everybody
    /// else. It arrives a few seconds after the record is drawn, from two external sources. So it can be
    /// set later, and it is not fixed when the line is built (see AlbumDetailView.FillPopularAsync).</para>
    ///
    /// <para>The album page sets it and draws it. The wall's tracklist and the artist page build the same
    /// lines, and leave it alone. A drawer that opens under a tile is a quick look at a record. A mark
    /// nobody asked for does not belong in a quick look.</para>
    /// </summary>
    public bool IsMostPopular
    {
        get => _isMostPopular;
        set
        {
            if (_isMostPopular == value) return;
            _isMostPopular = value;
            OnChanged();

            // Both places are refreshed, because which place applies is a fact about the LINE. This is the
            // only thing that changes about the star. A line told that it is the most played, after the page
            // was drawn, must redraw the star that applies to it.
            OnChanged(nameof(StarInColumn));
            OnChanged(nameof(StarInline));
        }
    }

    private bool _isHovered;

    /// <summary>The pointer is over this line. This makes the empty heart appear on a song nobody has
    /// marked. A column of twenty grey hearts down every record would be twenty prompts nobody asked for.
    /// But with no heart at all, the mark could only be reached through the menu.</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered == value) return; _isHovered = value; OnChanged(); OnChanged(nameof(ShowHeart)); }
    }

    /// <summary>
    /// Whether the heart is drawn at all: always on a favourite, and on hover for the rest.
    ///
    /// <para>Somebody else's song is not an exception. The mirror held for that computer has a row for the
    /// song, and the mark on it belongs to the listener. So the click is accepted. The right-click menu
    /// offers the same entry, and the two must agree.</para>
    /// </summary>
    public bool ShowHeart => IsTrack && (_isFavorite || _isHovered);

    // ── the cloud mark in front of the name ──

    private AlbumCloudBadge _cloudBadge;

    /// <summary>What the cloud holds for THIS track, drawn in front of its name with the same symbols the
    /// album tile uses. It is set by <see cref="ApplyCloud"/>, not by the constructor. It is the one thing
    /// on a line that changes after the line is built. The record it belongs to decides whether it is shown
    /// at all.
    /// </summary>
    public AlbumCloudBadge CloudBadge
    {
        get => _cloudBadge;
        set
        {
            if (_cloudBadge == value) return;
            _cloudBadge = value;
            OnChanged();
            OnChanged(nameof(ShowCloudBadge));
            OnChanged(nameof(IsCloudBusy));
            if (!IsCloudBusy)
                CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;   // Never left part-way through a pulse
        }
    }

    /// <summary>
    /// Whether the mark is drawn at all.
    ///
    /// <para>Not while the track is playing. The three-bar equaliser takes the same slot in front of the
    /// name. The two side by side would push the title along and show two things at once. Playing wins,
    /// because where the music is now is the more urgent fact. The cloud mark returns as soon as the line
    /// stops playing.</para>
    /// </summary>
    public bool ShowCloudBadge => _cloudBadge != AlbumCloudBadge.None && !_isPlaying;

    /// <summary>On its way up or on its way out. These are the states that pulse.</summary>
    public bool IsCloudBusy =>
        _cloudBadge is AlbumCloudBadge.Working or AlbumCloudBadge.Leaving;

    private double _cloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;

    /// <summary>The mark's opacity. The shared badge pulse writes it while the track is busy. It uses the
    /// same clock as the wall's tiles and the playlist rows. So a track going up and its own album's tile
    /// are never out of step.</summary>
    public double CloudPulse
    {
        get => _cloudPulse;
        set { if (_cloudPulse == value) return; _cloudPulse = value; OnChanged(); }
    }

    /// <summary>
    /// Gives a record's lines their cloud marks, from the tracks they were built from.
    ///
    /// <para>All of them or none. A record whose tracks all agree shows nothing, because its tile already
    /// says the one thing there is to say (see <see cref="RPM.Core.Cloud.TrackCloudBadge.WorthShowing"/>).
    /// Tracks are matched by file path, not by position. The lines include disc dividers and work headings,
    /// and a re-read of the folder can return one track fewer than the list on screen.</para>
    /// </summary>
    /// <param name="uploadsStopped">Whether the engine is sending nothing at all, for example because the
    /// store is full. A queued line then reads as chosen and waiting, not as on its way. That is the true
    /// state. It also stops a mark that should rest from pulsing for days over nothing.</param>
    /// <param name="shelf">Whose collection these lines are on. A borrowed one is read the other way round:
    /// the mark shows where the song is coming from, not whose disk it is on. Nothing about the queue is
    /// read at all, because nobody can queue somebody else's song from here.</param>
    public static void ApplyCloud(
        IReadOnlyList<AlbumTrackItem> lines, IReadOnlyList<TrackInfo> tracks, bool uploadsStopped = false,
        RPM.Core.Cloud.ShelfReading shelf = RPM.Core.Cloud.ShelfReading.Own)
    {
        var state = new Dictionary<string, AlbumCloudBadge>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
            state[track.FilePath] = shelf == RPM.Core.Cloud.ShelfReading.Own
                ? RPM.Core.Cloud.TrackCloudBadge.For(
                    track.CloudState, track.CloudSelected, uploadsStopped,
                    track.CloudAttempts, track.CloudPartialError)
                : RPM.Core.Cloud.TrackCloudBadge.ForBorrowed(
                    track.CloudState, shelf == RPM.Core.Cloud.ShelfReading.BorrowedFromOnline);

        var badges = new List<AlbumCloudBadge>(state.Count);
        foreach (var track in tracks)
            badges.Add(state[track.FilePath]);

        var worth = RPM.Core.Cloud.TrackCloudBadge.WorthShowing(badges);
        foreach (var line in lines)
            line.CloudBadge = worth && line.Info is { } info && state.TryGetValue(info.FilePath, out var badge)
                ? badge
                : AlbumCloudBadge.None;
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds))
            return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

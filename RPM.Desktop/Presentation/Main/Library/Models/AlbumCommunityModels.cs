using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Cloud;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// The rows in the community part of the album page: the written reviews, and the votes on them.
///
/// <para>These are view models, not the wire records drawn directly. A vote changes one row in place.
/// Rebinding the whole list to redraw a score would lose the scroll position and reload every avatar on
/// screen, just to move one number by one.</para>
/// </summary>
[RpmBindable]
public sealed partial class ReviewRow : INotifyPropertyChanged
{
    private RpmReview _review;

    /// <param name="pressing">The pressing this reviewer's own copy is, when it is not the record being
    /// viewed. The page loads every pressing of an album together. A review can be about the remaster
    /// while the reader holds the original cut. Naming the pressing makes the disagreement easy to
    /// understand.</param>
    public ReviewRow(RpmReview review, string? signedInUserId, Uri? apiBase, string? pressing = null)
    {
        _review = review;
        IsMine = signedInUserId is { Length: > 0 } && signedInUserId == review.UserId;
        AvatarUrl = BuildAvatarUrl(apiBase, review.UserId, review.HasAvatar);
        Pressing = pressing ?? "";
    }

    /// <summary>The record this review is about, when it is another pressing of the album on screen. It is
    /// empty for a review of the record being viewed, because no name is needed then.</summary>
    public string Pressing { get; }

    public bool HasPressing => Pressing.Length > 0;

    public string UserId => _review.UserId;
    /// <summary>The name to show. An account that was deleted shows "Deleted user". Its rows keep their
    /// text, but not the name.</summary>
    public string AuthorName => _review.AuthorDeleted ? Loc.Get("Community_DeletedUser") : _review.AuthorName;
    public string Body => _review.Body;
    public double Stars => _review.Stars;
    public int Score => _review.Score;
    public int MyVote => _review.MyVote;

    /// <summary>True for the caller's own review. This is the one row that has a delete option instead of a
    /// report option. It is also the one row where the vote arrows are not offered.</summary>
    public bool IsMine { get; }

    public bool CanVote => !IsMine;

    /// <summary>The date the review was written, in the reader's own format. The server sends milliseconds
    /// since the epoch, which is not useful to show anybody.</summary>
    public string When => DateText.Short(_review.Written.LocalDateTime, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>True when the review was changed after it was first posted.</summary>
    public bool WasEdited => _review.UpdatedAt > _review.CreatedAt + 1000;

    public Uri? AvatarUrl { get; }

    public bool HasAvatar => AvatarUrl is not null;

    /// <summary>The letter shown when somebody has no picture: the first letter of their name. This makes a
    /// wall of reviews easy to scan before any of them is read.</summary>
    public string Initial => !_review.AuthorDeleted && AuthorName is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";

    /// <summary>Whether the reader's own vote is up. This lights the up arrow.</summary>
    public bool VotedUp => MyVote > 0;

    public bool VotedDown => MyVote < 0;

    /// <summary>Takes the server's answer to a vote, and updates only what changed.</summary>
    public void Apply(RpmReview updated)
    {
        _review = updated;
        Notify(nameof(Score));
        Notify(nameof(MyVote));
        Notify(nameof(VotedUp));
        Notify(nameof(VotedDown));
    }

    internal static Uri? BuildAvatarUrl(Uri? apiBase, string userId, bool hasAvatar)
        => hasAvatar && apiBase is not null
            ? new Uri(apiBase, RpmCommunityClient.AvatarPath(userId))
            : null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One remark under an album. It is shorter than a review, and it has no rating.</summary>
[RpmBindable]
public sealed partial class CommentRow(RpmComment comment, string? signedInUserId, Uri? apiBase)
{
    public string CommentId => comment.CommentId;
    public string AuthorName => comment.AuthorDeleted ? Loc.Get("Community_DeletedUser") : comment.AuthorName;
    public string Body => comment.Body;

    public bool IsMine { get; } = signedInUserId is { Length: > 0 } && signedInUserId == comment.UserId;

    public string When => DateText.Short(comment.Written.LocalDateTime, System.Globalization.CultureInfo.CurrentCulture);

    public Uri? AvatarUrl { get; } = ReviewRow.BuildAvatarUrl(apiBase, comment.UserId, comment.HasAvatar);

    public bool HasAvatar => AvatarUrl is not null;

    public string Initial => !comment.AuthorDeleted && AuthorName is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";
}

/// <summary>
/// The one live measurement that the similar-albums row uses to size its cards. It is how wide a sleeve
/// is drawn, once the row's width has been divided into whole cards at <see cref="Target"/>.
///
/// <para>One instance is shared by every card. A resize sets it once, and every bound card follows. Cards
/// are not pushed one by one. The artists wall and the album wall use the same arrangement.</para>
///
/// <para>The number of cards is the nearest whole number to the target. The cards then stretch to fill
/// the row. This is the same rounding that the wall and the artist page's shelves use (see
/// ArtistPageLayout.Sleeve). The row is not divided by how many cards it holds. That would make the
/// sleeve depend on the number of suggestions. Nine suggestions would draw nine stamps, however much room
/// there was, and a row that wanted bigger pictures could only get them by fetching fewer records. The
/// width decides the size. The number of records decides how many are in the row. These are two separate
/// questions.</para>
/// </summary>
[RpmBindable]
public sealed partial class SimilarTileMetrics : INotifyPropertyChanged
{
    /// <summary>The padding a card keeps around its picture, on each side. It is set in the markup (see
    /// SimilarTemplate's Button). It is written here because the sleeve is measured with the padding
    /// taken off.
    ///
    /// <para>It is public because the row is placed against it as well as measured by it. The plate behind
    /// a card is eight points wider than its picture at each end. A row whose plates line up with the
    /// columns above it is a row whose sleeves do not. See AlbumDetailView.ApplyStacking, which adds this
    /// back at the left end and takes it off the lane at the right.</para></summary>
    public const double Padding = 8;

    /// <summary>The space between one card and the next. The row's panel adds it, so it is added CARDS-1
    /// times, never after the last card. It is set in markup (see SimilarList's ItemsPanel).</summary>
    private const double CardAir = 6;

    /// <summary>What each card uses besides its sleeve. It is subtracted from the row's share before the
    /// sleeve is measured, so the sleeves are square at a size that actually fits.</summary>
    private const double Chrome = Padding * 2 + CardAir;

    /// <summary>Below this size a sleeve is a stamp, not a picture. A row too narrow to hold its cards at
    /// this size scrolls instead.</summary>
    private const double Floor = 72;

    /// <summary>The sleeve size that the album page's own suggestions row aims for. This is a fact about
    /// another pane's markup: 128 is the square the track info pane draws the playing record's cover at
    /// (TrackInfoView's CoverBox). A suggestion and the record in the pane beside it are the same kind of
    /// thing, a cover you can press to go somewhere. Using the same size keeps both covers the same size on
    /// one screen.
    ///
    /// <para>This is not the size that is drawn. A whole number of cards has to fill the row, so this is
    /// the size the rounding is done around. When one of them changes, change the other: CoverBox and this
    /// value.</para>
    ///
    /// <para>This is a default, not a rule. The home screen's rows aim at the album wall's own tile size
    /// instead. That size is set by a slider the listener drags (<c>SettingsService.AlbumTileSize</c>). A
    /// sleeve on the home screen and a sleeve on the wall are the same kind of thing. If one followed the
    /// listener's size and the other did not, they would look like two different sizes of
    /// it.</para></summary>
    public const double Target = 128;

    private readonly double _target;

    /// <summary>The row that aims at the album page's own size, with its text at the unscaled
    /// sizes.</summary>
    public SimilarTileMetrics() : this(Target, new Home.HomeTextSizes()) { }

    /// <summary>A row that aims at a size the caller chooses. The size has the same floor as a measured
    /// sleeve. Below <see cref="Floor"/>, a cover is a stamp, not a picture, whoever asked for
    /// it.</summary>
    /// <param name="textSizes">The text sizes of the screen the row is drawn on. Home passes its own,
    /// which Pegasus scales.</param>
    public SimilarTileMetrics(double target, Home.HomeTextSizes textSizes)
    {
        _target = Math.Max(Floor, target);
        TextSizes = textSizes;
        _edge = Aimed;

        // The title room depends on the badge's size. The sleeve depends on the target. Both change with
        // the scale.
        textSizes.Follow(this);
    }

    /// <summary>The text sizes the cards on this row are drawn at, and the scale their sleeves are drawn at
    /// with them. Only home's templates read this. The album page's own suggestions row states its sizes in
    /// its markup, and its scale is 1.</summary>
    public Home.HomeTextSizes TextSizes { get; }

    /// <summary>
    /// The size the sleeve aims at: the target, at the screen's scale. The scale is 1 everywhere except
    /// home in Pegasus, where the cards grow by the same factor as the text.
    /// </summary>
    private double Aimed => Math.Max(Floor, _target * TextSizes.Scale);

    /// <summary>The width the row was last divided at (see Set). It is zero for a row that is never
    /// divided. Home's rows keep the size they aim at and scroll, so their sleeve is
    /// <see cref="Aimed"/>.</summary>
    private double _width;

    private double _edge;

    /// <summary>The sleeve's own side. It is square, so this is also its height.</summary>
    public double Edge
    {
        get => _edge;
        private set
        {
            if (Math.Abs(_edge - value) < 0.5)
                return;
            _edge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Edge)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleRoom)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Stride)));
        }
    }

    /// <summary>
    /// How wide a title may be before it has to be trimmed.
    /// </summary>
    /// <remarks>
    /// The title and the explicit badge form one centred group. So the title cannot simply take the whole
    /// card. It must leave the badge its width and the gap before it. Otherwise a long name pushes the E
    /// off the edge. A StackPanel measures its children with unlimited width, so without a limit set here,
    /// nothing would ever be trimmed.
    ///
    /// <para>This is the same number, with the same reasoning, as <c>AlbumTileMetrics.TitleRoom</c>. These
    /// cards write a record in the same way the wall's cards do. The value is set here, not in the markup,
    /// because it changes with the size slider. A card whose title stops trimming at an old width has its
    /// badge hanging over the card beside it.</para>
    ///
    /// <para>The badge's size comes from <see cref="TextSizes"/>: 12 on the album page and in the main
    /// window, and larger on home in Pegasus. A change of scale is raised here through
    /// <see cref="TextScaled"/>.</para>
    /// </remarks>
    public double TitleRoom => Math.Max(0, _edge - TextSizes.Badge - BadgeGap);

    /// <summary>The gap between a title and its badge. It is the StackPanel's Spacing in the
    /// templates.</summary>
    private const double BadgeGap = 4;

    /// <summary>
    /// The scale changed, so the sleeve's aim changed, and so did the badge. Called by
    /// <see cref="Home.HomeTextSizes"/> for every metrics object it was given.
    /// </summary>
    /// <remarks>A row that divides itself is divided again at its last width. A row that does not divide
    /// itself takes the new aim as its sleeve. Either way, Edge updates the title room and the stride. The
    /// title room is also updated here, for the case where the sleeve did not change but the badge
    /// did.</remarks>
    internal void TextScaled()
    {
        if (_width > 0)
            Set(_width);
        else
            Edge = Aimed;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleRoom)));
    }

    /// <summary>The distance from one card's left edge to the next one's: the sleeve, the padding on each
    /// side, and the space after it. The row's drawer uses this to place its pointer triangle. The
    /// markup's three numbers are added up once, here, rather than again wherever a card has to be
    /// found.</summary>
    public double Stride => _edge + Chrome;

    /// <summary>Divides <paramref name="width"/> into whole cards at the sleeve's aim
    /// (<see cref="Target"/>, or the size it was built with, at the screen's scale). Whatever does not
    /// fit scrolls, as the row already did on a narrow page.</summary>
    public void Set(double width)
    {
        if (width <= 0)
            return;

        // One gap is given back before the division, because the row has one fewer gap than it has cards.
        // Without it, the arithmetic counts a space after the last card. The row would then be wider than
        // the box it sits in. It would overflow and draw a scroll bar, however few suggestions there are.
        _width = width;
        var room = width + CardAir;
        var cards = RPM.Core.Library.AlbumGridLayout.ColumnsForTarget(room, Aimed + Chrome);
        Edge = Math.Max(Floor, Math.Round(room / cards - Chrome));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One card in the similar-albums row.
///
/// <para>Its cover comes from one of two places, and the difference matters. An owned record draws the
/// sleeve already on disk. A suggestion draws Discogs' thumbnail over the network. So the image is a
/// settable property, not a URL that the template binds. The local image arrives after a read from disk.
/// The card is on screen before either one arrives.</para>
/// </summary>
[RpmBindable]
public sealed partial class SimilarRow(RPM.Core.Metadata.SimilarAlbum album, SimilarTileMetrics metrics)
    : INotifyPropertyChanged, RPM.Desktop.Services.Art.ISleeveCard
{
    private Microsoft.UI.Xaml.Media.ImageSource? _cover;

    /// <summary>The row's own measurement, shared by every card in it. It is passed in, not fetched from a
    /// static. A card that fetched a static could be drawn in a row it is not part of.</summary>
    public SimilarTileMetrics Metrics { get; } = metrics;

    public string Title => album.Title;
    public string Artist => album.Artist;

    private bool _isExplicit;

    /// <summary>True when this record has at least one explicit song. The E is shown after its name. It
    /// is the same mark the record has on the album wall.</summary>
    /// <remarks>
    /// The row's builder sets this. It is not read from <see cref="RPM.Core.Metadata.SimilarAlbum"/>,
    /// because that value is the ranking's answer about a record and says nothing about its tags. The
    /// card follows the same pattern as <c>AlbumCard.IsExplicit</c>, for the same reason: the fact comes
    /// from one query for the whole shelf (<c>LibraryDatabase.QueryExplicitAlbums</c>). Asking per card
    /// would take the reading gate once for every sleeve.
    ///
    /// <para>The value is false where nothing sets it. That is the album page's own suggestions row. That
    /// row does not gather the set, and it draws no badge.</para>
    /// </remarks>
    public bool IsExplicit
    {
        get => _isExplicit;
        set
        {
            if (_isExplicit == value)
                return;
            _isExplicit = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExplicit)));
        }
    }

    /// <summary>The record this card opens. It is never empty. Every suggestion is a record on this shelf
    /// (see SimilarAlbums), so no card opens a web page instead.</summary>
    public string Folder => album.Folder;

    private bool _coverMissing;

    /// <summary>The fill's verdict: neither the folder on this disk nor the computer that holds the
    /// record has a picture for it. The card draws its made picture from this value. See
    /// <see cref="FigureSeed"/>.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FigureSeed)));
        }
    }

    /// <summary>The folder that the made sleeve is drawn from, when the record's folder holds no picture.
    /// The folder is used, so a record shows the same made picture here as it does on the wall.
    ///
    /// <para>It is null until the sleeve has actually been looked for and not found. This is the same
    /// rule the wall follows. See <see cref="AlbumCard.FigureSeed"/>. The row is on screen before any of
    /// its covers have arrived. A seed handed over early would be a blurred figure drawn on every card,
    /// and it would be covered up a moment later.</para></summary>
    public string? FigureSeed => _coverMissing ? Folder : null;

    /// <summary>Why this record is in the row. It is used for a tooltip and in the tests. The value is
    /// the ranking's own answer, not a guess made back from the card. The page must not be able to say
    /// "same genre" about a card that the ranking put there for its era.</summary>
    public RPM.Core.Metadata.SimilarBecause Why => album.Why;

    public Microsoft.UI.Xaml.Media.ImageSource? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCover)));
        }
    }

    public bool HasCover => _cover is not null;

    private bool _isExpanded;

    /// <summary>True when this record's drawer is the one that is open. The ring around its sleeve shows
    /// this. The wall uses the same mark on its own kind of card (<c>AlbumCard.IsExpanded</c>). A press
    /// on a sleeve opens a tracklist and does not navigate. Without the ring, nothing on the card ties
    /// the drawer below to the record it is about.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    private Microsoft.UI.Xaml.Media.Brush? _ring;

    /// <summary>The ring around the open card, in its drawer's ground colour. While the sleeve is still
    /// being read, the ring uses the drawer's default colour. Once the sleeve is read, it uses the
    /// sleeve's own ground, so the ring and the drawer look like one. This is the same brush and the same
    /// default that the wall uses for its ring. See <c>AlbumCard.Ring</c>.</summary>
    /// <remarks>The brush is made on first read, not in a field initializer. A Brush takes the thread's
    /// DispatcherQueue when it is built. An initializer would make a row buildable only on the UI thread.
    /// The rest of this row, such as the seed a made sleeve is drawn from, is string arithmetic that has
    /// nothing to do with a dispatcher.</remarks>
    public Microsoft.UI.Xaml.Media.Brush Ring
    {
        get => _ring ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x22, 0x2C));
        set
        {
            _ring = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ring)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// What an empty tab of the groove shows: the glyph with a slash through it, the line above the
/// placeholders, the invitation under it, the key that takes that invitation (where the tab has one), and
/// the placeholders themselves.
///
/// <para>This is one record rather than two hand-drawn empty states. The drawing is the same on both
/// tabs: a column of ghost cards in the shape of the missing cards, with a word above them. Only the
/// glyph and the line differ. Two copies of that markup would have to change together whenever the
/// placeholder changes shape.</para>
/// </summary>
/// <param name="Action">The text of the key under the invitation, or null for a tab with no key. Each
/// call site states this explicitly, with no default. The reviews tab has no writing field of its own, so
/// it needs a key. The comments tab has its box under the column, and must not get a second way to write.
/// A new tab must state which of the two it is.</param>
[RpmBindable]
public sealed partial record GrooveEmptyState(string Glyph, string Text, string Invitation, string? Action)
{
    /// <summary>Whether there is a key under the invitation at all.</summary>
    public bool HasAction => !string.IsNullOrEmpty(Action);

    /// <summary>Whether there is a second line at all. A record with no pressing linked is told what to
    /// do in the line above, so it has nothing to invite the reader to do. An empty TextBlock still takes
    /// the stack's spacing, so the line is hidden instead.</summary>
    public Microsoft.UI.Xaml.Visibility InvitationShown => string.IsNullOrWhiteSpace(Invitation)
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The placeholder cards, and how solid each one is. They do not stop at the foot of the
    /// tab. They fade out. A column cut off at whatever line the window ends on looks like an interrupted
    /// list. A column that dissolves looks like a shape, not like data.</summary>
    /// <remarks>This is its own list type, because it is bound to an ItemsSource. Under Native AOT, that
    /// binding needs a type that CsWinRT has generated WinRT interfaces for. See
    /// <see cref="Common.GrooveGhostList"/> (RPM007).</remarks>
    public Common.GrooveGhostList Ghosts { get; } = [.. Column()];

    /// <summary>How many placeholder cards to make. There are just enough to carry the fade to nothing,
    /// and no more. Past <see cref="LastSolid"/> the cards are fading out, and the last one is not drawn
    /// at all. Each card is six borders and four shapes, built once and never changed.</summary>
    private const int GhostCount = 10;

    /// <summary>The last card at full strength. The first three cards say "cards go here". The rest show
    /// the fade as the sentence trailing off.</summary>
    private const int LastSolid = 2;

    /// <summary>The name bars have six different lengths, and every third card is a line shorter. A
    /// column of identical cards looks like a pattern, not like people. The lengths are cycled, not
    /// random. The placeholder is the same each time it is shown, so it stays in the background and does
    /// not look like an animation.</summary>
    private static GrooveGhost[] Column()
    {
        double[] names = [104, 76, 92, 68, 110, 84];

        var ghosts = new GrooveGhost[GhostCount];
        for (var i = 0; i < ghosts.Length; i++)
            ghosts[i] = new GrooveGhost(names[i % names.Length], Full: i % 3 != 2, Fade: Fade(i));

        return ghosts;
    }

    /// <summary>Full strength down to <see cref="LastSolid"/>, then a straight fall to nothing on the
    /// last card. The fall is linear, because readers see a ramp of card opacities as depth. A curve here
    /// would be a curve that nobody can name.</summary>
    private static double Fade(int index)
        => index <= LastSolid
            ? 1
            : 1 - (double)(index - LastSolid) / (GhostCount - 1 - LastSolid);
}

/// <summary>One card that is not there. It records how wide the name over it would have been, whether the
/// text on it would run to a second line, and how much of the card remains when the column reaches
/// it.</summary>
[RpmBindable]
public sealed partial record GrooveGhost(double Name, bool Full, double Fade);

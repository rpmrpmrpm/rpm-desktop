using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.Main.Genres;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The sizes every card on the artists screen binds to, written once for the whole screen (see
/// PegasusChrome.SizeArtists). Shared by every card, as the wall's <see cref="PegasusTileMetrics"/> is,
/// so a resize is one write and one notification per property rather than one per artist.
/// </summary>
[RpmBindable]
public sealed partial class PegasusFaceMetrics : INotifyPropertyChanged
{
    private double _edge = 168;
    private double _scale = 1;

    /// <summary>The photograph's side. The photograph is a circle of this diameter.</summary>
    public double Edge
    {
        get => _edge;
        set
        {
            if (Math.Abs(_edge - value) < 0.5)
                return;
            _edge = value;
            Raise(nameof(Edge), nameof(Round), nameof(InitialSize), nameof(HolderMargin), nameof(PhotoMargin));
        }
    }

    /// <summary>The scale that the text sizes below are multiplied by.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (value <= 0 || Math.Abs(_scale - value) < 0.001)
                return;
            _scale = value;
            Raise(nameof(Title), nameof(TitleBox), nameof(Line), nameof(LineBox));
        }
    }

    /// <summary>The corner that makes the photograph's frame a circle.</summary>
    public CornerRadius Round => new(_edge / 2);

    /// <summary>The letter drawn where there is no photograph: two fifths of the circle.</summary>
    public double InitialSize => _edge * 0.4;

    /// <summary>The margin of the photograph's holder. It gives the focus zoom room (see
    /// <see cref="FocusLift"/>).</summary>
    public Thickness HolderMargin => FocusLift.HolderMargin(_edge);

    /// <summary>The photograph's margin inside its holder.</summary>
    public Thickness PhotoMargin => FocusLift.PhotoMargin(_edge);

    /// <summary>The name, at the size that a radio row card's name uses (14 before the scale).</summary>
    public double Title => 14 * _scale;

    /// <summary>The height of the name's line. It is kept whether or not there is a name, so every row has
    /// the same height (see PegasusCardTemplate).</summary>
    public double TitleBox => 19 * _scale;

    /// <summary>The count under the name.</summary>
    public double Line => 12 * _scale;

    public double LineBox => 16 * _scale;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>How far an artist card's photograph has got. A failed request is not the same as a missing
/// photograph, so it is asked again on the next visit to the screen.</summary>
internal enum FaceState
{
    NotAsked,
    Asking,
    Shown,

    /// <summary>The catalogues answered with no photograph, or the act is the compilations shelf. The
    /// letter stands in for good.</summary>
    None,

    /// <summary>The request got no answer. It is asked again on the next visit.</summary>
    Failed,
}

/// <summary>
/// One act on the artists screen: the name, how many records are theirs, and the photograph once it has
/// been fetched. The photograph is requested while the card's row is realized (see
/// PegasusChrome.PumpArtistFaces).
/// </summary>
[RpmBindable]
public sealed partial class PegasusArtistCard : INotifyPropertyChanged
{
    // Each property is init-only and has a default, not required. Uno's bindable metadata generator creates
    // an instance with no arguments to describe the type. A required member would stop it from doing that.
    public string Name { get; init; } = "";

    /// <summary>For example "12 albums": the records filed under the act, alone or with other artists.</summary>
    public string Line { get; init; } = "";

    /// <summary>The first letter of the name, drawn where there is no photograph. Empty for the
    /// compilations shelf, which wears <see cref="Glyph"/> instead.</summary>
    public string Letter { get; init; } = "";

    /// <summary>The people glyph, for the compilations shelf. Empty for every act with a name.</summary>
    public string Glyph { get; init; } = "";

    public PegasusFaceMetrics Metrics { get; init; } = new();

    private ImageSource? _picture;

    public ImageSource? Picture
    {
        get => _picture;
        set
        {
            _picture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
        }
    }

    /// <summary>The act on the card.</summary>
    internal ArtistWallItem Act { get; init; } = new("", [], [], [], 0);

    /// <summary>The card's place in the list on screen. It puts the focus back on this card after its page is
    /// left.</summary>
    internal int Index { get; set; }

    /// <summary>One of the act's own records, by name. It tells two acts with the same name apart when the
    /// photograph is chosen. Null if none of the act's records has a name.</summary>
    internal string? FirstRecord { get; init; }

    internal FaceState Face { get; set; }

    /// <summary>The photograph's bytes. They are kept for lighting the room when the act's page opens.</summary>
    internal byte[]? PictureBytes { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One row of the artists screen. Rows are used rather than a flow of cards, for the same reason as
/// the wall (see PegasusAlbumRow): the row is what the virtualizing repeater realizes and recycles.</summary>
[RpmBindable]
public sealed partial class PegasusArtistRow
{
    public Common.PegasusArtistCardList Cards { get; init; } = [];
}

/// <summary>The shape of a card's picture: a record's square sleeve or an act's round photograph.</summary>
internal enum CardShape
{
    Square,
    Round,
}

/// <summary>
/// The transparent room that every card has on each side. It is set in points before the scale, using home's
/// card padding. The remote's focus grows a card by a scale on its own element. The Skia head cuts that growth
/// off at the edge of the card's parent panel. Inside this room the picture and the words can grow without
/// reaching that edge. The rows are inset by this much less, and spaced by twice this much less, so the
/// pictures stand where they would stand without it.
/// </summary>
internal static class CardRoomAround
{
    public const double Points = 8;
}

/// <summary>
/// One card on a shelf of an artist's or a genre's page: a picture, a name, and a line under it. It can be a
/// record, an act, a song or a playlist. It is built in code at the collection's scale, like radio's row
/// cards. The page that builds it fetches the picture and hands it over (see <see cref="Show"/>).
/// </summary>
internal sealed class ShelfCard
{
    private readonly Border _frame;
    private readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _stand;
    private readonly TextBlock _title;
    private readonly TextBlock? _line;
    private readonly ExplicitBadge? _badge;
    private readonly StackPanel _stack = new();
    private readonly CardShape _shape;

    /// <summary>The round photograph's holder, which gives the focus zoom room (see <see cref="FocusLift"/>).
    /// It is null on a square card, which keeps the glow.</summary>
    private readonly Grid? _holder;

    /// <param name="stand">What is drawn in the frame until a picture arrives. It stays for good where no
    /// picture is found: an act's first letter, or a glyph.</param>
    /// <param name="standFont">The font that <paramref name="stand"/> is drawn in.</param>
    /// <param name="press">What OK and a click do.</param>
    public ShelfCard(
        string title, string? line, CardShape shape, bool isExplicit, string stand, FontFamily? standFont,
        Style plain, Action press)
    {
        _shape = shape;

        _stand = new TextBlock
        {
            Text = stand,
            Foreground = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        if (standFont is not null)
            _stand.FontFamily = standFont;

        var inside = new Grid();
        inside.Children.Add(_stand);
        inside.Children.Add(_art);
        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Child = inside,
        };

        _title = new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The title and its explicit badge (E) are on one line, with the badge right after the last letter,
        // as in the rest of Pegasus.
        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = shape == CardShape.Round ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        titleRow.Children.Add(_title);
        if (isExplicit)
        {
            _badge = new ExplicitBadge
            {
                IsExplicit = true,
                VerticalAlignment = VerticalAlignment.Center,
                LetterBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x08, 0x08)),
            };
            ToolTipService.SetToolTip(_badge, Strings.Loc.Get("Library_Explicit"));
            titleRow.Children.Add(_badge);
        }

        // A round card is an artist. It is shown the way the tvOS app shows one (see FocusLift): the photograph
        // sits in a holder with room for its zoom, and the lines below it are marked to move down. A square
        // card keeps the glow.
        if (shape == CardShape.Round)
        {
            _holder = new Grid();
            _holder.Children.Add(_frame);
            _stack.Children.Add(_holder);
            FocusLift.SetPart(_frame, LiftPart.Photo);
            FocusLift.SetPart(titleRow, LiftPart.Below);
        }
        else
        {
            _stack.Children.Add(_frame);
        }
        _stack.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(line))
        {
            _line = new TextBlock
            {
                Text = line,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = shape == CardShape.Round ? TextAlignment.Center : TextAlignment.Left,
            };
            _stack.Children.Add(_line);
            if (shape == CardShape.Round)
                FocusLift.SetPart(_line, LiftPart.Below);
        }

        Root = new Button
        {
            Style = plain,
            Content = _stack,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (shape == CardShape.Round)
            FocusLift.SetPart(Root, LiftPart.Card);
        Root.Click += (_, _) => press();
        AutomationProperties.SetName(Root, title);
    }

    public Button Root { get; }

    /// <summary>The picture's side in points, as last sized. The page decodes the picture at this size.</summary>
    public double Edge { get; private set; }

    /// <summary>The card's width and its writing.</summary>
    public void Size(double width, double scale)
    {
        Edge = width;
        _frame.Width = _frame.Height = width;
        _frame.CornerRadius = _shape == CardShape.Round ? new CornerRadius(width / 2) : new CornerRadius(6 * scale);
        _stack.Width = width;
        _stack.Spacing = 7 * scale;
        Root.Padding = new Thickness(CardRoomAround.Points * scale);
        // A round card uses a square corner. The presenter clips to a rounded corner, which would cut off the
        // zoomed photograph. A round card also draws no glow for a corner to shape (see FocusLift).
        Root.CornerRadius = _holder is null ? new CornerRadius(8 * scale) : new CornerRadius(0);
        if (_holder is not null)
        {
            _holder.Margin = FocusLift.HolderMargin(width);
            _frame.Margin = FocusLift.PhotoMargin(width);
        }

        _stand.FontSize = width * 0.36;
        _title.FontSize = 14 * scale;
        if (_line is not null)
            _line.FontSize = 12 * scale;

        var badge = 12 * scale;
        if (_badge is not null)
        {
            _badge.Width = _badge.Height = badge;
            _badge.Margin = new Thickness(5 * scale, 0, 0, 0);
        }

        // A horizontal stack measures its children with no width limit. So the title's width is capped here,
        // or it would never be trimmed.
        _title.MaxWidth = Math.Max(0, width - (_badge is null ? 0 : badge + (5 * scale)));
    }

    /// <summary>Puts the picture in the frame, over the stand-in.</summary>
    public void Show(ImageSource picture)
    {
        _art.Source = picture;
        _stand.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// One genre as Pegasus draws it: the genre's colour as a still room, its wordmark across the room, and its
/// name and size under the card. It is built the same way as radio's row cards (a room with the station's
/// mark on it), in the genre's own colours (<see cref="GenreWordmarks.Room"/>) and lettering
/// (<see cref="GenreWordmarks.Lettering(GenreWordmark, GenreAccent, GenreFlush, int, int)"/>).
/// </summary>
internal sealed class GenreCard
{
    private readonly Grid _art = new();
    private readonly Image _lettering = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>The name in plain text, for a genre that the wordmark table has no drawing for ("Other").</summary>
    private readonly TextBlock? _plainName;

    private readonly TextBlock _name;
    private readonly TextBlock _line;
    private readonly StackPanel _stack = new();
    private readonly GenreWordmark? _mark;
    private readonly GenreAccent _accent;

    /// <summary>The pixel box the lettering was last drawn at. A resize that does not change it does not draw
    /// the lettering again.</summary>
    private (int W, int H) _drawnAt;

    /// <param name="open">What OK and a click do. It is given the card, so the caller knows where the focus
    /// comes back to.</param>
    public GenreCard(GenreWallItem genre, string line, Style plain, Action<GenreCard> open)
    {
        Genre = genre;
        _mark = GenreWordmarks.For(genre.Name);

        // A genre that is not in the table has no colour. In that case the room uses the app's blue.
        _accent = _mark is { } mark ? GenreAccent.Of(mark) : new GenreAccent(default, GenreFinish.Frost);
        Room.Palette = GenreWordmarks.Room(_accent);

        _art.Children.Add(Room);
        if (_mark is not null)
        {
            _art.Children.Add(_lettering);
        }
        else
        {
            _plainName = new TextBlock
            {
                Text = genre.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _art.Children.Add(_plainName);
        }

        _name = new TextBlock
        {
            Text = genre.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _line = new TextBlock
        {
            Text = line,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _stack.Children.Add(_art);
        _stack.Children.Add(_name);
        _stack.Children.Add(_line);

        Root = new Button
        {
            Style = plain,
            Content = _stack,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Root.Click += (_, _) => open(this);
        AutomationProperties.SetName(Root, genre.Name);
    }

    public Button Root { get; }

    public GenreWallItem Genre { get; }

    /// <summary>A still room, drawn once in the genre's colours. Forty drifting rooms on one screen would cost
    /// the drawing thread several milliseconds a frame each (see CardRoom).</summary>
    public CardRoom Room { get; } = new() { Drift = false, Fewest = 32 };

    /// <summary>
    /// The card's width and its writing. The room is 16 by 9. The lettering is sized so that every name's
    /// letters stand the same height on every card. That size is the one at which the widest name in the whole
    /// table fills four fifths of the card (the genres wall's rule, see GenreTile.NameBoxHeight).
    /// </summary>
    /// <param name="rasterScale">Physical pixels per point. The lettering is drawn at this density.</param>
    public void Size(double width, double scale, double rasterScale)
    {
        var height = width * 9 / 16;
        _art.Width = width;
        _art.Height = height;
        _stack.Width = width;
        _stack.Spacing = 7 * scale;

        var corner = new CornerRadius(10 * scale);
        Root.CornerRadius = corner;
        Root.Padding = new Thickness(CardRoomAround.Points * scale);
        Room.CornerRadius = corner;

        _name.FontSize = 14 * scale;
        _line.FontSize = 12 * scale;
        if (_plainName is not null)
        {
            _plainName.FontSize = height * 0.22;
            _plainName.MaxWidth = width * 0.85;
        }

        if (_mark is not { } mark)
            return;

        var letters = width * 0.8 / GenreWordmarks.WidestName;
        var boxHeight = letters / mark.Base;
        var boxWidth = boxHeight * mark.Aspect;
        _lettering.Width = boxWidth;
        _lettering.Height = boxHeight;

        var pixels = ((int)Math.Round(boxWidth * rasterScale), (int)Math.Round(boxHeight * rasterScale));
        if (pixels == _drawnAt)
            return;
        _drawnAt = pixels;
        _lettering.Source = GenreWordmarks.Lettering(mark, _accent, GenreFlush.Left, pixels.Item1, pixels.Item2);
    }
}

/// <summary>
/// One shelf on a page: a heading, and a row of cards under it that scrolls sideways. The remote moves along
/// the row with Left and Right, and RemoteScroll keeps the focused card in view.
/// </summary>
internal sealed class ShelfRow
{
    private readonly TextBlock _heading;
    private readonly ScrollViewer _scroll;

    public ShelfRow(string heading)
    {
        _heading = new TextBlock
        {
            Text = heading,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BringIntoViewOnFocusChange = false,
            Content = Cards,
        };
        Root.Children.Add(_heading);
        Root.Children.Add(_scroll);
    }

    public StackPanel Root { get; } = new();

    /// <summary>What the cards stand in.</summary>
    public StackPanel Cards { get; } = new() { Orientation = Orientation.Horizontal };

    /// <summary>
    /// The heading at radio's heading size and spacing (22 before the scale, 30 above it and 12 below it),
    /// and the gap between cards.
    /// </summary>
    /// <param name="bleed">How far the page is inset from the screen's sides. The row's scroller reaches out by
    /// that much on both sides, and the cards are padded back in by the same amount. So the cards line up with
    /// the heading, scroll under the screen's edge, and the focused card's grow is not cut off at the inset. The
    /// padding is set on the cards' panel, not on the scroller. A scroller clips at its content's bounds, and
    /// the grow is a scale on the card that reaches past them.</param>
    public void Size(double scale, double bleed)
    {
        var room = CardRoomAround.Points * scale;
        _heading.FontSize = 22 * scale;
        _heading.Margin = new Thickness(0, 30 * scale, 0, 12 * scale - room);
        Cards.Spacing = (22 * scale) - (2 * room);

        var lift = 6 * scale;
        _scroll.Margin = new Thickness(-bleed, -lift, -bleed, -lift);
        Cards.Padding = new Thickness(bleed - room, lift, bleed - room, lift);
    }
}

/// <summary>Where a card stands on its page: the index of its row among the page's own children, top first,
/// and its place along the row.</summary>
internal readonly record struct CardPlace(int Row, int Column);

/// <summary>
/// The pictures for one page's cards, fetched a few at a time. They start in the order the cards stand on the
/// page: the top row first, then along each row. This holds whatever order the cards were added in. UI thread
/// only.
///
/// <para><b>By place, not by arrival.</b> A row filled after the page is built (a genre's popular songs and
/// generated lists, read from the collection afterwards) stands above rows queued at build time. If the
/// pictures were started in order of arrival, they would wait behind every record further down. For example,
/// with 281 rock albums on Windows, the popular songs stayed grey for nine seconds after the page opened. The
/// place is required of every caller, so no card can be queued without saying where it is.</para>
///
/// <para><see cref="Clear"/> drops the jobs that have not started. The jobs already running finish. Each one
/// checks after its awaits whether the page it was fetching for is still up, so a picture for a page that was
/// left lands nowhere. A job that throws is recorded in the faults log, and the next job starts.</para>
/// </summary>
internal sealed class PictureQueue(int atOnce)
{
    private readonly PriorityQueue<Func<Task>, (int Row, int Column, long Added)> _waiting = new();
    private int _running;
    private long _added;

    /// <summary>Queues one card's picture. It starts after every card that stands above it, and after the cards
    /// before it in its row. It starts before every card after it. Cards at the same place start in the order
    /// they were added.</summary>
    public void Add(CardPlace place, Func<Task> job)
    {
        _waiting.Enqueue(job, (place.Row, place.Column, _added++));
        Pump();
    }

    public void Clear() => _waiting.Clear();

    private void Pump()
    {
        while (_running < atOnce && _waiting.Count > 0)
            Start(_waiting.Dequeue());
    }

    private async void Start(Func<Task> job)
    {
        _running++;
        try
        {
            await job();
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("pegasus", "a card's picture", ex);
        }
        finally
        {
            _running--;
            Pump();
        }
    }
}

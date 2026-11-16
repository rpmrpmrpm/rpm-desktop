using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Cloud;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The Feed's cards, built in code with the phone apps' sizes: a post (photo, name, text, the song, votes
/// and the reply count) and a review (the author, the record's title, artist, year and stars, the cover
/// at the right with the text run round it, votes and Read full review).
///
/// <para>The sizes inside a card are the phone's points, used as the desktop's pixels. Change them
/// together with <c>ProfileScreen.kt</c> and <c>ReviewCards.kt</c> in rpm-android and
/// <c>ReviewCards.swift</c> in rpm-apple. The cards themselves are as wide as the pane allows, from the
/// heading's left edge to the same margin at the right (see <c>FeedView.xaml</c>).</para>
/// </summary>
internal static class FeedCards
{

    /// <summary>The cover on a review card.</summary>
    public const double ReviewCover = 120;

    /// <summary>The cover on a review read in full.</summary>
    public const double ReaderCover = 140;

    /// <summary>The most lines of a review a card shows. The whole review is one press away.</summary>
    public const int ReviewLines = 10;

    private static readonly FontFamily Fa = new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro");

    private const string Like = "\uF164";
    private const string Dislike = "\uF165";
    private const string Comment = "\uF075";
    private const string Play = "\uF04B";
    private const string Record = "\uF51F";

    /// <summary>The supporter's gold for a name, the phone's <c>Ink.gold</c>.</summary>
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xEB, 0xC6, 0x5A);

    internal static SolidColorBrush White(byte alpha) => new(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    // ── a post ──

    /// <summary>A post as a card. Its votes are drawn by <paramref name="votes"/>, which the caller keeps
    /// so an answer to a vote can redraw the counts in place.</summary>
    /// <param name="onPerson">Opens the author's page from their photo or name; null on that page itself,
    /// where both stay still.</param>
    public static FrameworkElement Post(RpmPost post, VoteKeys votes, Action<string>? onPerson)
    {
        var name = post.AuthorDeleted ? Loc.Get("Community_DeletedUser") : post.AuthorName;
        var open = post.AuthorDeleted || onPerson is null ? null : (Action)(() => onPerson(post.OwnerId));

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(Pressable(Byline(name, post.CreatedAt, post.AuthorSupporter), open, name));
        text.Children.Add(new TextBlock
        {
            Text = post.Body,
            FontSize = 14,
            Foreground = White(0xE6),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        if (post.Song is { } song)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            line.Children.Add(Glyph(Play, 9, White(0xAA)));
            line.Children.Add(new TextBlock
            {
                Text = SongLine(song),
                FontSize = 12,
                Foreground = White(0xAA),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            text.Children.Add(line);
        }

        // The thumbs start where the text starts; the reply count ends at the card's inner edge.
        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(votes);
        var replies = Replies(post.ReplyCount);
        Grid.SetColumn(replies, 1);
        foot.Children.Add(replies);
        text.Children.Add(foot);

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var photo = Pressable(Photo(post.AuthorDeleted ? null : post.OwnerId, post.HasAvatar, 0, name, 40), open, name);
        photo.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(photo);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        return Card(row, new Thickness(14, 12, 14, 2));
    }

    // ── a review ──

    /// <summary>A review as a card. <paramref name="onRead"/> opens the whole review, from Read full review
    /// and from a press anywhere on the card that is not a key.</summary>
    /// <param name="showAuthor">Draw the author's line above the record, as on everyone's feed. On the
    /// author's own page the line is left out and the time stands beside the stars instead.</param>
    /// <param name="onPerson">Opens the author's page from their line; null where it is not drawn.</param>
    public static FrameworkElement Review(
        RpmFeedReview review, VoteKeys votes, Image cover, Action onRead, bool showAuthor, Action<string>? onPerson)
    {
        var stack = new StackPanel();
        if (showAuthor)
        {
            var author = Author(review, onPerson);
            author.Margin = new Thickness(0, 0, 0, 12);
            stack.Children.Add(author);
        }
        stack.Children.Add(new WrappedReview(Head(review, showTime: !showAuthor), Quoted(review.Body), CoverFrame(cover, ReviewCover)));

        var foot = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(votes);
        var read = TextKey(Loc.Get("Rpm_Profile_ReadReview"), 14, onRead);
        Grid.SetColumn(read, 1);
        foot.Children.Add(read);
        stack.Children.Add(foot);

        var card = Card(stack, new Thickness(14, 14, 14, 4));
        card.Tapped += (_, e) =>
        {
            if (!InsideKey(e.OriginalSource as DependencyObject, card))
                onRead();
        };
        return card;
    }

    /// <summary>A review read in full: the author, the cover beside the record's title, artist, year and
    /// stars, every line of the review, the votes, and Open album when the record is in the reader's
    /// collection (<paramref name="onOpenAlbum"/> not null). The key that closes it is in the pane's
    /// heading row.</summary>
    public static IEnumerable<UIElement> ReaderParts(
        RpmFeedReview review, VoteKeys votes, Image cover, Action? onOpenAlbum, bool showAuthor, Action<string>? onPerson)
    {
        if (showAuthor)
            yield return Author(review, onPerson);

        var top = new Grid { ColumnSpacing = 14 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(CoverFrame(cover, ReaderCover));
        var head = Head(review, showTime: true);
        Grid.SetColumn(head, 1);
        top.Children.Add(head);
        yield return top;

        if (!string.IsNullOrWhiteSpace(review.Body))
        {
            yield return new TextBlock
            {
                Text = Quoted(review.Body),
                FontSize = 15,
                Foreground = White(0xEE),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
        }

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.Children.Add(votes);
        if (onOpenAlbum is not null)
        {
            var open = TextKey(Loc.Get("Rpm_Profile_OpenAlbum"), 13, onOpenAlbum);
            Grid.SetColumn(open, 1);
            foot.Children.Add(open);
        }
        yield return foot;
    }

    /// <summary>The record's title, its artist and year, and the stars. On a review read in full, the
    /// time stands beside the stars.</summary>
    private static StackPanel Head(RpmFeedReview review, bool showTime)
    {
        var head = new StackPanel();
        head.Children.Add(new TextBlock
        {
            Text = review.AlbumTitle,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = White(0xFF),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        head.Children.Add(new TextBlock
        {
            Text = review.AlbumYear > 0 ? $"{review.AlbumArtist}, {review.AlbumYear}" : review.AlbumArtist,
            FontSize = 13,
            Foreground = White(0x99),
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (review.Rating > 0 || showTime)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
            if (review.Rating > 0)
                line.Children.Add(new StarRating { Value = review.Stars, GlyphSize = 15, VerticalAlignment = VerticalAlignment.Center });
            if (showTime)
                line.Children.Add(new TextBlock { Text = Written(review.CreatedAt), FontSize = 11, Foreground = White(0x77), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(line);
        }
        return head;
    }

    /// <summary>The author's photo and name over a review, with when it was written. It opens their page.</summary>
    private static FrameworkElement Author(RpmFeedReview review, Action<string>? onPerson)
    {
        var name = review.AuthorDeleted ? Loc.Get("Community_DeletedUser") : review.AuthorName;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(Photo(review.AuthorDeleted ? null : review.UserId, review.HasAvatar, 0, name, 32));
        var byline = Byline(name, review.CreatedAt, review.AuthorSupporter);
        byline.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(byline);
        var open = review.AuthorDeleted || onPerson is null ? null : (Action)(() => onPerson(review.UserId));
        var pressable = Pressable(row, open, name);
        pressable.HorizontalAlignment = HorizontalAlignment.Left;
        return pressable;
    }

    /// <summary>The element as a key that runs <paramref name="onPress"/>, or the element itself when there is
    /// nothing to run. A key, so a press on it does not also open the review card it is on.</summary>
    private static FrameworkElement Pressable(FrameworkElement content, Action? onPress, string name)
    {
        if (onPress is null)
            return content;
        var key = new Button
        {
            Style = Key(),
            Height = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = content,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, name);
        key.Click += (_, _) => onPress();
        return key;
    }

    /// <summary>The cover square: the record glyph, with the picture over it once there is one. The
    /// picture is <paramref name="cover"/>, which the caller fills when the cover is found.</summary>
    private static FrameworkElement CoverFrame(Image cover, double edge)
    {
        var frame = new Grid
        {
            Width = edge,
            Height = edge,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)),
            BorderBrush = White(0x22),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
        };
        frame.Children.Add(Glyph(Record, 28, White(0x3A)));
        cover.Stretch = Stretch.UniformToFill;
        frame.Children.Add(cover);
        return frame;
    }

    // ── shared parts ──

    /// <summary>The card plate: round corners, a light wash, the phone's padding.</summary>
    private static Border Card(UIElement child, Thickness padding) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = White(0x12),
        Padding = padding,
        Child = child,
    };

    /// <summary>A name and when it wrote. A supporter's name is gold.</summary>
    private static StackPanel Byline(string name, long millis, bool gold)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = gold ? new SolidColorBrush(Gold) : White(0xFF),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        line.Children.Add(new TextBlock
        {
            Text = Written(millis),
            FontSize = 11,
            Foreground = White(0x77),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return line;
    }

    /// <summary>A person's photo in a circle, or the first letter of their name while they have none.</summary>
    /// <param name="stamp">The photo's <c>avatarUpdatedAt</c> where it is known, so a changed photo has a new
    /// address; 0 where the answer does not carry it.</param>
    internal static FrameworkElement Photo(string? userId, bool hasAvatar, long stamp, string name, double size)
    {
        var round = new CornerRadius(size / 2);
        var box = new Grid { Width = size, Height = size, CornerRadius = round, Background = White(0x26) };
        box.Children.Add(new TextBlock
        {
            Text = name is { Length: > 0 } ? name[..1].ToUpperInvariant() : "?",
            FontSize = size * 0.42,
            FontWeight = FontWeights.SemiBold,
            Foreground = White(0xDD),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var api = App.Services.GetRequiredService<RpmCommunityClient>().BaseAddress;
        if (userId is { Length: > 0 } && hasAvatar && api is not null)
            box.Children.Add(new AvatarPicture { CornerRadius = round, Source = new Uri(api, RpmCommunityClient.AvatarPath(userId, stamp)) });
        return box;
    }

    /// <summary>The reply mark with how many replies there are, or the word Reply when there are none.
    /// It is a count only: threads open on the phone.</summary>
    private static FrameworkElement Replies(int count)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
        };
        row.Children.Add(Glyph(Comment, 13, White(0x99)));
        row.Children.Add(new TextBlock
        {
            Text = count == 0 ? Loc.Get("Rpm_Profile_Reply") : count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            Foreground = White(0xCC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var label = count switch
        {
            0 => Loc.Get("Rpm_Profile_Reply"),
            1 => Loc.Get("Rpm_Profile_RepliesOne"),
            _ => Loc.Format("Rpm_Profile_Replies", count),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, label);
        return row;
    }

    /// <summary>A key that is only its words, such as Read full review.</summary>
    private static Button TextKey(string text, double size, Action onClick)
    {
        var key = new Button
        {
            Style = Key(),
            Content = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = White(0xFF) },
        };
        key.Click += (_, _) => onClick();
        return key;
    }

    internal static TextBlock Glyph(string glyph, double size, Brush ink) => new()
    {
        Text = glyph,
        FontFamily = Fa,
        FontSize = size,
        Foreground = ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The style of the keys on a card (votes, Read full review, Open album, close). They draw no
    /// plate of their own, as on the phone: the glyph and the text are the key. The plate's brushes for
    /// hover and press are cleared in <c>FeedView.xaml</c>.</summary>
    internal static Style Key() => _key ??= MakeKey();

    private static Style? _key;

    private static Style MakeKey()
    {
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 32d));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        return style;
    }

    /// <summary>Whether a press landed on a key inside the card, which does its own thing, rather than
    /// on the card.</summary>
    private static bool InsideKey(DependencyObject? from, DependencyObject card)
    {
        for (var at = from; at is not null && at != card; at = VisualTreeHelper.GetParent(at))
        {
            if (at is ButtonBase)
                return true;
        }
        return false;
    }

    /// <summary>The review in quotation marks, or nothing for a rating with no text.</summary>
    private static string Quoted(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : Loc.Format("Rpm_Profile_Quoted", body.Trim());

    /// <summary>"Title by Artist", as the phone writes a post's song.</summary>
    private static string SongLine(RpmSong song) =>
        string.IsNullOrEmpty(song.Artist) ? song.Title : Loc.Format("Rpm_Profile_NowPlaying", song.Title, song.Artist);

    /// <summary>When something was written: "just now" in the first minute, then minutes, hours and days
    /// ago, and the date past a month.</summary>
    internal static string Written(long millis)
    {
        if (millis <= 0)
            return "";
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(millis);
        if (age < TimeSpan.FromMinutes(1))
            return Loc.Get("Rpm_Profile_JustNow");
        if (age < TimeSpan.FromHours(1))
            return Loc.Format("Cloud_HistoryMinutes", (int)age.TotalMinutes);
        if (age < TimeSpan.FromDays(1))
            return Loc.Format("Cloud_HistoryHours", (int)age.TotalHours);
        if (age < TimeSpan.FromDays(31))
            return Loc.Format("Cloud_HistoryDays", (int)age.TotalDays);
        return DateText.Short(DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime,
            System.Globalization.CultureInfo.CurrentCulture);
    }
}

/// <summary>
/// Thumbs up with the likes beside it, thumbs down with the dislikes, as on the phone. The reader's own
/// vote is lit, and a press on it takes the vote back. On the reader's own post or review the counts are
/// shown and cannot be pressed: nobody votes on their own.
/// </summary>
internal sealed partial class VoteKeys : StackPanel
{
    private readonly Button _up;
    private readonly Button _down;
    private int _mine;

    /// <param name="onVote">1 up, -1 down, 0 to take the vote back.</param>
    public VoteKeys(bool enabled, Action<int> onVote)
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Left;
        _up = Make(first: true);
        _down = Make(first: false);
        _up.IsEnabled = _down.IsEnabled = enabled;
        _up.Click += (_, _) => onVote(_mine == 1 ? 0 : 1);
        _down.Click += (_, _) => onVote(_mine == -1 ? 0 : -1);
        Children.Add(_up);
        Children.Add(_down);
    }

    /// <summary>Draws the counts and the reader's vote.</summary>
    public void Show(int ups, int downs, int mine)
    {
        _mine = mine;
        Paint(_up, "\uF164", ups, mine == 1, Loc.Format("Rpm_Profile_Likes", ups));
        Paint(_down, "\uF165", downs, mine == -1, Loc.Format("Rpm_Profile_Dislikes", downs));
    }

    private static Button Make(bool first) => new()
    {
        Style = FeedCards.Key(),
        Padding = new Thickness(first ? 0 : 8, 0, 8, 0),
    };

    private static void Paint(Button key, string glyph, int count, bool lit, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(FeedCards.Glyph(glyph, 13, FeedCards.White(lit ? (byte)0xFF : (byte)0x99)));
        row.Children.Add(new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            Foreground = FeedCards.White(lit ? (byte)0xEE : (byte)0x99),
            VerticalAlignment = VerticalAlignment.Center,
        });
        key.Content = row;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(key, label);
    }
}

/// <summary>
/// The head of a review and its text, run round the cover at the top right, as on the phone. The text is
/// measured at the width beside the cover. Every line that starts above the cover's foot stays beside it,
/// and the rest carries on at the full width under it, so the text reads as one block and no line is cut
/// in two. At most <see cref="FeedCards.ReviewLines"/> lines are shown.
///
/// <para>The split is worked out in measure, from the width the panel is given, and kept for that width.
/// A second measure at the same width changes no text, so the layout settles after one extra pass.</para>
/// </summary>
internal sealed partial class WrappedReview : Panel
{
    private const double Gap = 12;
    private const double Space = 8;
    private const double LineHeight = 19;

    private readonly FrameworkElement _head;
    private readonly FrameworkElement _cover;
    private readonly TextBlock _beside;
    private readonly TextBlock _below;
    private readonly string _quoted;

    private double _splitAt = -1;
    private double _belowTop;

    public WrappedReview(FrameworkElement head, string quoted, FrameworkElement cover)
    {
        _head = head;
        _cover = cover;
        _quoted = quoted;
        _beside = Text();
        _below = Text();
        _below.TextTrimming = TextTrimming.CharacterEllipsis;
        Children.Add(_head);
        Children.Add(_beside);
        Children.Add(_below);
        Children.Add(_cover);
    }

    private static TextBlock Text() => new()
    {
        FontSize = 14,
        Foreground = FeedCards.White(0xE6),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = LineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    protected override Size MeasureOverride(Size available)
    {
        // Never infinite inside a card; the fallback is a card in the widest pane.
        var full = double.IsInfinity(available.Width) ? 376 : available.Width;
        var edge = FeedCards.ReviewCover;
        var narrow = Math.Max(1, full - edge - Gap);

        _cover.Measure(new Size(edge, edge));
        _head.Measure(new Size(narrow, double.PositiveInfinity));

        if (full != _splitAt)
        {
            _splitAt = full;
            Split(narrow, edge - _head.DesiredSize.Height - Space);
        }

        _beside.Measure(new Size(narrow, double.PositiveInfinity));
        _below.Measure(new Size(full, double.PositiveInfinity));

        var hasBeside = _beside.Text.Length > 0;
        var left = _head.DesiredSize.Height + (hasBeside ? Space + _beside.DesiredSize.Height : 0);
        // Straight on from the lines beside the cover; under the cover when there were none.
        _belowTop = hasBeside ? left : Math.Max(left, edge + Space);
        var height = Math.Max(left, edge);
        if (_below.Text.Length > 0)
            height = Math.Max(height, _belowTop + _below.DesiredSize.Height);
        return new Size(full, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var edge = FeedCards.ReviewCover;
        var narrow = Math.Max(1, final.Width - edge - Gap);
        _head.Arrange(new Rect(0, 0, narrow, _head.DesiredSize.Height));
        _beside.Arrange(new Rect(0, _head.DesiredSize.Height + Space, narrow, _beside.DesiredSize.Height));
        _cover.Arrange(new Rect(final.Width - edge, 0, edge, edge));
        _below.Arrange(new Rect(0, _belowTop, final.Width, _below.DesiredSize.Height));
        return final;
    }

    /// <summary>Puts the text beside the cover and under it, for a column <paramref name="narrow"/> wide
    /// beside a cover with <paramref name="room"/> of height left under the head.</summary>
    private void Split(double narrow, double room)
    {
        // The lines beside the cover are those that start above its foot.
        var lines = room > 0 ? Math.Min((int)Math.Ceiling(room / LineHeight), FeedCards.ReviewLines) : 0;
        if (_quoted.Length == 0 || lines == 0)
        {
            Place("", _quoted, 0);
            return;
        }

        if (LinesOf(_quoted, narrow) <= lines)
        {
            Place(_quoted, "", 0);
            return;
        }

        // The longest start of the text that fits in those lines, cut where a word starts.
        var cuts = Cuts(_quoted);
        int lo = 0, hi = cuts.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (LinesOf(_quoted[..cuts[mid]].TrimEnd(), narrow) <= lines)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best < 0)
        {
            Place("", _quoted, 0);
            return;
        }

        var beside = _quoted[..cuts[best]].TrimEnd();
        Place(beside, _quoted[cuts[best]..].TrimStart(), LinesOf(beside, narrow));
    }

    private void Place(string beside, string below, int besideLines)
    {
        _beside.Text = beside;
        _below.Text = below;
        _below.MaxLines = Math.Max(1, FeedCards.ReviewLines - besideLines);
    }

    /// <summary>How many lines <paramref name="text"/> takes at <paramref name="width"/>, measured on the
    /// beside block itself so the font and line height are the ones drawn.</summary>
    private int LinesOf(string text, double width)
    {
        _beside.Text = text;
        _beside.Measure(new Size(width, double.PositiveInfinity));
        return (int)Math.Round(_beside.DesiredSize.Height / LineHeight);
    }

    /// <summary>Where the text may be cut: where each word starts. Text written without spaces (Chinese,
    /// Japanese) may be cut at any character.</summary>
    private static List<int> Cuts(string text)
    {
        var cuts = new List<int>();
        for (var i = 1; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i - 1]) && !char.IsWhiteSpace(text[i]))
                cuts.Add(i);
        }
        if (cuts.Count < 4 && text.Length > 40)
        {
            cuts.Clear();
            for (var i = 1; i < text.Length; i++)
            {
                if (!char.IsLowSurrogate(text[i]))
                    cuts.Add(i);
            }
        }
        return cuts;
    }
}

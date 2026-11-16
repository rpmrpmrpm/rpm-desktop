using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The reader's notifications, newest first, as on the phones: who did what, and when. Unread ones are
/// lit. Opening the page marks everything on its first page as read. A friend request carries Accept and
/// Decline; every entry opens what it is about (the post, or the person).
///
/// <para>Out of order: a further page is dropped when a read started since (<see cref="_reads"/>), and two
/// further pages never run together (<see cref="_readingMore"/>).</para>
/// </summary>
internal sealed class FeedNotices
{
    private const double MoreWithin = 400;

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    private readonly StackPanel _column = new() { Spacing = 8, Padding = new Thickness(16, 7, 16, 40) };
    private readonly TextBlock _quiet = new()
    {
        FontSize = 13,
        Foreground = FeedCards.White(0x88),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private string? _next;
    private int _reads;
    private bool _readingMore;

    public FeedNotices()
    {
        _column.Children.Add(new TextBlock
        {
            Text = Loc.Get("Rpm_Notify_Title"),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = FeedCards.White(0xFF),
            Margin = new Thickness(0, 0, 0, 4),
        });
        _column.Children.Add(_quiet);
        Root = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _column,
        };
        Root.ViewChanged += (_, _) =>
        {
            if (_next is not null && Root.ScrollableHeight - Root.VerticalOffset < MoreWithin)
                _ = MoreAsync();
        };
    }

    public ScrollViewer Root { get; }

    /// <summary>A person's page is asked for, by id.</summary>
    public event Action<string>? PersonRequested;

    /// <summary>A post's thread is asked for: its owner and its id.</summary>
    public event Action<string, string>? ThreadRequested;

    /// <summary>Everything was marked as read.</summary>
    public event Action? AllRead;

    public async Task LoadAsync()
    {
        var read = ++_reads;
        RpmNoticePage page;
        try
        {
            page = await Community.GetNoticesAsync(null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            if (read == _reads)
                Quiet("Rpm_Profile_Offline");
            return;
        }
        if (read != _reads)
            return;

        var items = (page.Items ?? []).Where(n => n.Known).ToList();
        Add(items);
        _next = page.Next;
        Quiet(items.Count == 0 ? "Rpm_Notify_Empty" : null);

        if (page.Unread > 0 && (page.Items ?? []).FirstOrDefault() is { } newest)
        {
            try
            {
                await Community.MarkNoticesReadAsync(newest.Id);
                AllRead?.Invoke();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           or RpmCommunityRejectedException or InvalidOperationException)
            {
                // They stay unread, and are marked the next time the page opens.
            }
        }
    }

    private async Task MoreAsync()
    {
        if (_readingMore || _next is not { } before)
            return;
        _readingMore = true;
        var read = _reads;
        try
        {
            var page = await Community.GetNoticesAsync(before);
            if (read != _reads)
                return;
            Add((page.Items ?? []).Where(n => n.Known));
            _next = page.Next;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // The list ends here until the page is opened again.
            _next = null;
        }
        finally
        {
            _readingMore = false;
        }
    }

    private void Quiet(string? key)
    {
        _quiet.Text = key is null ? "" : Loc.Get(key);
        _quiet.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Add(IEnumerable<RpmNotice> notices)
    {
        foreach (var notice in notices)
            _column.Children.Add(Line(notice));
    }

    /// <summary>What the notification says, in the phones' words.</summary>
    private static string Sentence(RpmNotice n) => n.Kind switch
    {
        RpmNotice.ReplyKind => Loc.Format("Rpm_Notify_Reply", n.FromName),
        RpmNotice.VoteKind when n.Count > 1 => Loc.Format("Rpm_Notify_Votes", n.Count),
        RpmNotice.VoteKind => Loc.Format("Rpm_Notify_Vote", n.FromName),
        RpmNotice.FriendRequestKind => Loc.Format("Rpm_Notify_FriendRequest", n.FromName),
        RpmNotice.FriendAcceptedKind => Loc.Format("Rpm_Notify_FriendAccepted", n.FromName),
        RpmNotice.FriendReviewKind => Loc.Format("Rpm_Notify_FriendReview", n.FromName, n.AlbumTitle ?? ""),
        RpmNotice.BadgeKind => Loc.Format("Rpm_Notify_Badge", ProfileBadges.NameOf(n.BadgeId) ?? n.BadgeId ?? ""),
        _ => "",
    };

    /// <summary>What a press on the notification opens.</summary>
    private void Open(RpmNotice n)
    {
        switch (n.Kind)
        {
            case RpmNotice.ReplyKind or RpmNotice.VoteKind when n.PostId is { Length: > 0 } postId:
                ThreadRequested?.Invoke(n.OwnerId is { Length: > 0 } owner ? owner : Auth.UserId ?? "", postId);
                break;
            case RpmNotice.BadgeKind:
                if (Auth.UserId is { Length: > 0 } me)
                    PersonRequested?.Invoke(me);
                break;
            default:
                if (n.FromUserId.Length > 0)
                    PersonRequested?.Invoke(n.FromUserId);
                break;
        }
    }

    private FrameworkElement Line(RpmNotice n)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = Sentence(n),
            FontSize = 14,
            Foreground = FeedCards.White(n.Read ? (byte)0xBB : (byte)0xF2),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock { Text = FeedCards.Written(n.CreatedAt), FontSize = 11, Foreground = FeedCards.White(0x77) });

        if (n.Kind == RpmNotice.FriendRequestKind)
        {
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            var accept = new RpmPrimaryButton { Content = Loc.Get("Rpm_Profile_Accept") };
            var decline = new RpmButton { Content = Loc.Get("Rpm_Profile_Decline") };
            accept.Click += async (_, _) => await AnswerAsync(keys, () => Community.AcceptFriendAsync(n.FromUserId));
            decline.Click += async (_, _) => await AnswerAsync(keys, () => Community.RemoveFriendAsync(n.FromUserId));
            keys.Children.Add(accept);
            keys.Children.Add(decline);
            text.Children.Add(keys);
        }

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var photo = FeedCards.Photo(n.FromUserId, n.FromHasAvatar, 0, n.FromName, 36);
        photo.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(photo);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = FeedCards.White(n.Read ? (byte)0x0C : (byte)0x1C),
            BorderBrush = n.Read ? null : FeedCards.White(0x22),
            BorderThickness = new Thickness(n.Read ? 0 : 1),
            Padding = new Thickness(12, 10, 12, 10),
            Child = row,
        };
        card.Tapped += (_, e) =>
        {
            if (!FeedCards.InsideKey(e.OriginalSource as DependencyObject, card))
                Open(n);
        };
        return card;
    }

    /// <summary>Sends an answer to a friend request and takes the keys away once it is sent.</summary>
    private static async Task AnswerAsync(Panel keys, Func<Task> send)
    {
        keys.IsHitTestVisible = false;
        try
        {
            await send();
            keys.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            keys.IsHitTestVisible = true;
        }
    }
}

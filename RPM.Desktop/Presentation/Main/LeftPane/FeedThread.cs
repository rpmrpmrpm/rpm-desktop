using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// A post with its replies, as the phones open it: the post's card, then every reply in reading order,
/// indented by how deep it answers, and the box to reply to the post at the foot. Reply on a reply opens
/// a box under that reply. A reply can be voted on, deleted by its author or by the page's owner, and
/// reported by anyone else. Six levels are shown; the answers under a reply on the sixth level wait behind
/// a "Show more" key under it, which opens that branch.
///
/// <para>Out of order: replies are read again after a send or a delete, and an answer that arrives after a
/// newer read started is dropped (<see cref="_reads"/>). A vote answer is applied only when it answers the
/// latest vote on that reply (<see cref="_votesSent"/>).</para>
/// </summary>
internal sealed class FeedThread
{
    /// <summary>The deepest a reply is indented. Deeper replies stay at this depth so the text keeps its
    /// width in a narrow pane.</summary>
    private const int DeepestIndent = 6;

    private const double Indent = 16;

    /// <summary>How many levels a thread shows before a reply's deeper answers wait behind "Show more".</summary>
    private const int ShownLevels = 6;

    /// <summary>The replies on the last shown level whose deeper answers were opened.</summary>
    private readonly HashSet<string> _opened = new(StringComparer.Ordinal);

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    private readonly string _ownerId;
    private readonly string _postId;
    private readonly StackPanel _column = new() { Spacing = 12, Padding = new Thickness(16, 7, 16, 40) };
    private readonly Border _postHost = new();
    private readonly StackPanel _lines = new() { Spacing = 10 };
    private readonly TextBlock _quiet = new()
    {
        FontSize = 13,
        Foreground = FeedCards.White(0x88),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
    private readonly Dictionary<string, int> _votesSent = [];
    private readonly Dictionary<string, VoteKeys> _replyVotes = [];

    private RpmPost? _post;
    private List<RpmReply> _replies = [];
    private string? _answering;
    private int _reads;
    private int _postVotes;

    public FeedThread(string ownerId, string postId, RpmPost? post)
    {
        _ownerId = ownerId;
        _postId = postId;
        _post = post;
        _column.Children.Add(_postHost);
        _column.Children.Add(_quiet);
        _column.Children.Add(_lines);
        Root = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _column,
        };
    }

    public ScrollViewer Root { get; }

    /// <summary>A name or photo was pressed, carrying the person's id.</summary>
    public event Action<string>? PersonRequested;

    /// <summary>The post changed here (a vote, a new reply). The lists that show it redraw it.</summary>
    public event Action<RpmPost>? PostChanged;

    public async Task LoadAsync()
    {
        if (_post is null)
            _post = await FindPostAsync();
        DrawPost();
        await ReadRepliesAsync();
    }

    /// <summary>The post, when the thread was opened from a notification that carries only its ids: looked
    /// for on the first page of its owner's feed, which holds their newest posts.</summary>
    private async Task<RpmPost?> FindPostAsync()
    {
        try
        {
            var page = await Community.GetPersonFeedAsync(_ownerId, null, RpmCommunityClient.LongestPersonFeedPage);
            return page?.Known.Select(i => i.Post).FirstOrDefault(p => p?.PostId == _postId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
        {
            return null;
        }
    }

    private void DrawPost()
    {
        if (_post is not { } post)
        {
            _postHost.Child = null;
            return;
        }
        var votes = new VoteKeys(post.OwnerId != Auth.UserId, value => _ = VotePostAsync(value));
        votes.Show(post.Ups, post.Downs, post.MyVote);
        _postHost.Child = FeedCards.Post(post, votes, id => PersonRequested?.Invoke(id), onOpen: null, onMenu: null);
    }

    private async Task VotePostAsync(int value)
    {
        if (_post is not { } post)
            return;
        var sent = ++_postVotes;
        try
        {
            if (await Community.VotePostAsync(post.OwnerId, post.PostId, value) is { } answer && sent == _postVotes)
            {
                _post = answer;
                DrawPost();
                PostChanged?.Invoke(answer);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // Signed out, offline or refused: the counts stay.
        }
    }

    private async Task ReadRepliesAsync()
    {
        var read = ++_reads;
        IReadOnlyList<RpmReply> replies;
        try
        {
            replies = await Community.GetRepliesAsync(_ownerId, _postId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
        {
            if (read == _reads)
                Quiet(Loc.Get("Rpm_Profile_Offline"));
            return;
        }
        if (read != _reads)
            return;
        Quiet(null);
        _replies = [.. replies];
        Draw();
    }

    private void Quiet(string? text)
    {
        _quiet.Text = text ?? "";
        _quiet.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Lays the thread out again: every reply, the box under the reply being answered, and the box
    /// for the post at the foot.</summary>
    private void Draw()
    {
        _lines.Children.Clear();
        _replyVotes.Clear();
        var signedIn = Auth.UserId is { Length: > 0 };
        var lines = new RpmReplyTree(_postId, _replies).Lines;

        // Every reply deeper than the shown levels sits under one reply on the last shown level: its gate.
        // The tree is in reading order, so the gate is the latest reply seen on that level.
        var gates = new string?[lines.Count];
        var hidden = new Dictionary<string, int>(StringComparer.Ordinal);
        string? gate = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var (reply, depth) = lines[i];
            if (depth == ShownLevels - 1)
                gate = reply.ReplyId;
            if (depth < ShownLevels || gate is null)
                continue;
            gates[i] = gate;
            if (!_opened.Contains(gate))
                hidden[gate] = hidden.GetValueOrDefault(gate) + 1;
        }

        FeedComposer? open = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var (reply, depth) = lines[i];
            if (gates[i] is { } closed && !_opened.Contains(closed))
                continue;
            var line = Line(reply, depth, signedIn);
            _lines.Children.Add(line);
            if (signedIn && _answering == reply.ReplyId)
            {
                open = Box(reply.ReplyId);
                open.Margin = new Thickness(Math.Min(depth + 1, DeepestIndent) * Indent, 0, 0, 0);
                _lines.Children.Add(open);
            }
            if (hidden.TryGetValue(reply.ReplyId, out var count))
                _lines.Children.Add(ShowMore(reply.ReplyId, count, depth));
        }
        if (signedIn)
            _lines.Children.Add(Box(null));
        open?.FocusText();
    }

    /// <summary>The key under a reply on the last shown level that opens the answers under it.</summary>
    private Button ShowMore(string replyId, int count, int depth)
    {
        var key = FeedCards.TextKey(
            count == 1 ? Loc.Get("Rpm_Profile_ShowMoreRepliesOne") : Loc.Format("Rpm_Profile_ShowMoreReplies", count),
            12,
            () =>
            {
                _opened.Add(replyId);
                Draw();
            });
        key.Margin = new Thickness((Math.Min(depth + 1, DeepestIndent) * Indent) + 38, -4, 0, 0);
        key.HorizontalAlignment = HorizontalAlignment.Left;
        return key;
    }

    private FeedComposer Box(string? parentId) => new(
        Loc.Get("Rpm_Profile_ReplyPrompt"),
        async (body, _) =>
        {
            try
            {
                var reply = await Community.ReplyAsync(_ownerId, _postId, parentId, body);
                _replies.Add(reply);
                _answering = null;
                if (_post is { } post)
                {
                    _post = post with { ReplyCount = post.ReplyCount + 1 };
                    DrawPost();
                    PostChanged?.Invoke(_post);
                }
                Draw();
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           or RpmCommunityRejectedException or InvalidOperationException)
            {
                return false;
            }
        },
        offerSong: false);

    /// <summary>One reply: photo, name and time, the text (or "Deleted reply"), the votes, Reply, and the
    /// corner menu.</summary>
    private FrameworkElement Line(RpmReply reply, int depth, bool signedIn)
    {
        var me = Auth.UserId;
        var name = reply.AuthorDeleted ? Loc.Get("Community_DeletedUser") : reply.AuthorName;
        Action? open = reply.AuthorDeleted ? null : () => PersonRequested?.Invoke(reply.UserId);

        var text = new StackPanel { Spacing = 4 };
        var by = FeedCards.Pressable(FeedCards.Byline(name, reply.CreatedAt, reply.AuthorSupporter), open, name);
        text.Children.Add(by);
        text.Children.Add(reply.Deleted
            ? new TextBlock
            {
                Text = Loc.Get("Rpm_Profile_ReplyDeleted"),
                FontSize = 13,
                FontStyle = Windows.UI.Text.FontStyle.Italic,
                Foreground = FeedCards.White(0x77),
            }
            : new TextBlock
            {
                Text = reply.Body,
                FontSize = 14,
                Foreground = FeedCards.White(0xE6),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

        if (!reply.Deleted)
        {
            var foot = new StackPanel { Orientation = Orientation.Horizontal };
            var votes = new VoteKeys(signedIn && reply.UserId != me, value => _ = VoteReplyAsync(reply.ReplyId, value));
            votes.Show(reply.Ups, reply.Downs, reply.MyVote);
            _replyVotes[reply.ReplyId] = votes;
            foot.Children.Add(votes);
            if (signedIn)
            {
                var answer = FeedCards.TextKey(Loc.Get("Rpm_Profile_Reply"), 12, () =>
                {
                    _answering = _answering == reply.ReplyId ? null : reply.ReplyId;
                    Draw();
                });
                answer.Padding = new Thickness(4, 0, 4, 0);
                foot.Children.Add(answer);
            }
            text.Children.Add(foot);
        }

        var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(Math.Min(depth, DeepestIndent) * Indent, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var photo = FeedCards.Pressable(
            FeedCards.Photo(reply.AuthorDeleted ? null : reply.UserId, reply.HasAvatar, 0, name, 28), open, name);
        photo.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(photo);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (signedIn && !reply.Deleted)
        {
            var mine = reply.UserId == me || _ownerId == me;
            var more = FeedCards.MenuKey(at => Menu(reply, mine).ShowAt(at));
            more.Margin = new Thickness(0, -4, -8, 0);
            Grid.SetColumn(more, 1);
            row.Children.Add(more);
        }
        return row;
    }

    private MenuFlyout Menu(RpmReply reply, bool mine)
    {
        var menu = new MenuFlyout();
        var item = new MenuFlyoutItem
        {
            Text = Loc.Get(mine ? "Rpm_Profile_DeleteReply" : "Rpm_Profile_ReportReply"),
        };
        item.Click += async (_, _) =>
        {
            if (mine)
                await DeleteAsync(reply);
            else
                await FeedReport.AskAsync(Root.XamlRoot, note => Community.ReportPostAsync(_ownerId, _postId, reply.ReplyId, note));
        };
        menu.Items.Add(item);
        return menu;
    }

    private async Task DeleteAsync(RpmReply reply)
    {
        try
        {
            await Community.DeleteReplyAsync(_ownerId, _postId, reply.ReplyId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            Quiet(Loc.Get("Rpm_Profile_SendFailed"));
            return;
        }
        // Read again: a reply with replies under it stays as a deleted one.
        await ReadRepliesAsync();
    }

    private async Task VoteReplyAsync(string replyId, int value)
    {
        var sent = _votesSent[replyId] = _votesSent.GetValueOrDefault(replyId) + 1;
        try
        {
            if (await Community.VoteReplyAsync(_ownerId, _postId, replyId, value) is not { } answer)
                return;
            if (_votesSent.GetValueOrDefault(replyId) != sent)
                return;
            var at = _replies.FindIndex(r => r.ReplyId == replyId);
            if (at >= 0)
                _replies[at] = answer;
            if (_replyVotes.TryGetValue(replyId, out var keys))
                keys.Show(answer.Ups, answer.Downs, answer.MyVote);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // The counts stay.
        }
    }
}

/// <summary>The report dialog for a post or a reply, as the artist wall asks it.</summary>
internal static class FeedReport
{
    /// <param name="send">Sends the report with the reader's note.</param>
    public static async Task AskAsync(XamlRoot? root, Func<string?, Task> send)
    {
        if (root is null)
            return;
        var note = new TextBox
        {
            PlaceholderText = Loc.Get("Album_ReportPlaceholder"),
            IsSpellCheckEnabled = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 110,
            MaxLength = 2000,
        };
        var answer = await PromptDialog.ShowAsync(
            root,
            Loc.Get("Album_ReportTitle"),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = Loc.Get("Album_ReportCommentPrompt"),
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        Foreground = FeedCards.White(0xCC),
                    },
                    note,
                },
            },
            primaryText: Loc.Get("Album_ReportSubmit"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);
        if (answer != ContentDialogResult.Primary)
            return;
        try
        {
            await send(string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            await PromptDialog.ShowAsync(root, null, Loc.Get("Rpm_Profile_SendFailed"), closeText: Loc.Get("Common_OK"));
        }
    }
}

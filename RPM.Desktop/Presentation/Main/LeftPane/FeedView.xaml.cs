using Microsoft.UI.Xaml.Controls;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The left pane's Feed view: everyone's posts and album reviews, newest first, as on the phone apps' Feed
/// tab, and the pages it opens on top of it: a person's page (from the profile key, or a name or photo), a
/// post with its replies, a review read in full, the notifications, and the edit page. The pages stack;
/// the key at the right of the heading row closes the top one.
///
/// <para>What arrives out of order, beyond what each list and page keeps (<see cref="FeedList"/>,
/// <see cref="FeedThread"/>, <see cref="FeedNotices"/>):</para>
/// <list type="bullet">
/// <item>A person's profile answering after their page was closed: each person page has its own ticket,
/// taken from <see cref="_tickets"/>, and an answer for a page no longer in the stack is dropped.</item>
/// <item>A vote answered while a review is read in full: the list reports it
/// (<see cref="FeedList.Changed"/>) and the reader's keys are redrawn from it.</item>
/// <item>The unread count answering after the notifications page marked everything read: the count is
/// taken only when it is newer than the last mark (<see cref="_unreadAsked"/>).</item>
/// <item>A close pressed twice while the edit page asks whether to apply: <see cref="_closing"/> holds the
/// second press until the first is answered.</item>
/// </list>
/// </summary>
internal sealed partial class FeedView : UserControl
{
    /// <summary>How many entries a page of everyone's feed asks for, as on the phone.</summary>
    private const int PageSize = 20;

    /// <summary>A feed shown again within this time is not read again; the unread count likewise.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    /// <summary>One page over the feed.</summary>
    private sealed class Page
    {
        public required FrameworkElement Root { get; init; }

        /// <summary>The list on the page, to switch its paging on and off with the page.</summary>
        public FeedList? List { get; set; }

        /// <summary>A person's page: whose, what it last read, and the parts redrawn after a change.</summary>
        public PersonParts? Person { get; init; }

        /// <summary>Asked before the page closes; false keeps it open.</summary>
        public Func<Task<bool>>? MayLeave { get; init; }

        /// <summary>A review read in full: the entry and the keys drawn for it, taken off it on close.</summary>
        public FeedEntry? Reading { get; init; }

        public VoteKeys? ReaderVotes { get; init; }
    }

    private sealed class PersonParts
    {
        public required string UserId { get; init; }
        public required StackPanel Column { get; init; }
        public required int Ticket { get; init; }
        public RpmPersonProfile? Profile { get; set; }
        public FrameworkElement? Top { get; set; }
        public FrameworkElement? Box { get; set; }
    }

    private readonly List<Page> _pages = [];
    private readonly FeedList _everyone;

    private int _tickets;
    private bool _closing;
    private DateTimeOffset _unreadAsked;
    private DateTimeOffset _markedRead;
    private int _unread;

    public FeedView()
    {
        InitializeComponent();
        _everyone = new FeedList(Column, Scroller, QuietLine,
            before => Community.GetFeedAsync(before, PageSize), everyone: true, "Rpm_Feed_Empty", postMenus: false);
        Wire(_everyone);
        KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _pages.Count > 0 && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
            {
                e.Handled = true;
                await BackAsync();
            }
        };
    }

    /// <summary>Open album was pressed on a review, carrying the record's folder in the collection on
    /// screen.</summary>
    public event Action<string>? AlbumRequested;

    /// <summary>The view was put up. Reads the feed the first time, and again when it was last read more
    /// than a minute ago; the unread count likewise.</summary>
    public void Shown()
    {
        Show();
        if (DateTimeOffset.UtcNow - _everyone.ReadAt > Fresh)
            _ = _everyone.ReadAsync();
        if (Auth.UserId is { Length: > 0 } && DateTimeOffset.UtcNow - _unreadAsked > Fresh)
            _ = ReadUnreadAsync();
    }

    private async Task ReadUnreadAsync()
    {
        var asked = _unreadAsked = DateTimeOffset.UtcNow;
        try
        {
            var unread = await Community.GetUnreadNoticesAsync();
            if (asked > _markedRead)
                ShowUnread(unread);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // No dot this time; asked again on a later show.
        }
    }

    private void ShowUnread(int unread)
    {
        _unread = unread;
        ProfileDot.Visibility = Vis(unread > 0);
        MenuDot.Visibility = Vis(unread > 0);
        var label = unread > 0 ? Loc.Format("Rpm_Profile_UnreadCount", unread) : Loc.Get("Rpm_Feed_MyProfile");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ProfileKey, label);
    }

    private void Wire(FeedList list)
    {
        list.ReadRequested += OpenReader;
        list.PersonRequested += id => OpenPerson(id);
        list.ThreadRequested += (_, entry) =>
        {
            if (entry.Item.Post is { } post)
                OpenThread(post.OwnerId, post.PostId, post);
        };
        list.PostMenuRequested += ShowPostMenu;
        list.Changed += entry =>
        {
            foreach (var page in _pages)
            {
                if (page.Reading?.Key == entry.Key && page.ReaderVotes is { } keys)
                    FeedList.Paint(keys, entry.Item);
            }
        };
    }

    // ── the page stack ──

    private void Push(Page page)
    {
        _pages.Add(page);
        PageHost.Children.Add(page.Root);
        Show();
    }

    /// <summary>Puts the top page up, and the keys that go with it.</summary>
    private void Show()
    {
        var top = _pages.Count > 0 ? _pages[^1] : null;
        Scroller.Visibility = Vis(top is null);
        PageHost.Visibility = Vis(top is not null);
        foreach (var page in _pages)
        {
            page.Root.Visibility = Vis(page == top);
            if (page.List is { } list)
                list.Active = page == top;
        }
        _everyone.Active = top is null;

        var signedIn = Auth.UserId is { Length: > 0 };
        ProfileKey.Visibility = Vis(top is null && signedIn);
        BackKey.Visibility = Vis(top is not null);
        MenuKey.Visibility = Vis(top?.Person is { Profile: not null } && signedIn);
        MenuDot.Visibility = Vis(_unread > 0 && top?.Person?.UserId == Auth.UserId);
    }

    private static Visibility Vis(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void ProfileKey_Click(object sender, RoutedEventArgs e)
    {
        if (Auth.UserId is { Length: > 0 } me)
            OpenPerson(me);
    }

    private async void BackKey_Click(object sender, RoutedEventArgs e) => await BackAsync();

    /// <summary>Closes the top page, once it agrees to close.</summary>
    private async Task BackAsync()
    {
        if (_closing || _pages.Count == 0)
            return;
        var top = _pages[^1];
        if (top.MayLeave is { } ask)
        {
            _closing = true;
            try
            {
                if (!await ask())
                    return;
            }
            finally
            {
                _closing = false;
            }
            if (_pages.Count == 0 || _pages[^1] != top)
                return;
        }
        Close(top);
        Show();
    }

    private void Close(Page page)
    {
        _pages.Remove(page);
        PageHost.Children.Remove(page.Root);
        if (page.Reading is { } entry && page.ReaderVotes is { } votes)
            entry.Votes.Remove(votes);
    }

    // ── a person's page ──

    private PersonParts? PersonOnTop => _pages.Count > 0 ? _pages[^1].Person : null;

    /// <summary>Opens a person's page over what is shown.</summary>
    private void OpenPerson(string userId)
    {
        if (PersonOnTop?.UserId == userId)
            return;
        var scroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var column = new StackPanel { Spacing = 12, Padding = new Thickness(16, 7, 16, 40) };
        var quiet = new TextBlock
        {
            FontSize = 13,
            Foreground = FeedCards.White(0x88),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        column.Children.Add(quiet);
        scroller.Content = column;

        var own = userId == Auth.UserId;
        var parts = new PersonParts { UserId = userId, Column = column, Ticket = ++_tickets };
        var page = new Page { Root = scroller, Person = parts };
        var list = new FeedList(column, scroller, quiet,
            before => Community.GetPersonFeedAsync(userId, before, RpmCommunityClient.LongestPersonFeedPage),
            everyone: false, own ? "Rpm_Profile_FeedEmptyOwn" : "Rpm_Profile_FeedEmpty",
            postMenus: true);
        Wire(list);
        page.List = list;

        if (own)
        {
            parts.Box = new FeedComposer(Loc.Get("Rpm_Profile_PostPrompt"), async (body, song) =>
            {
                try
                {
                    list.Prepend(await Community.CreatePostAsync(body, song));
                    return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                               or RpmCommunityRejectedException or InvalidOperationException)
                {
                    return false;
                }
            }, offerSong: true);
            column.Children.Insert(0, parts.Box);
        }

        Push(page);
        _ = LoadPersonAsync(page, parts, list);
    }

    private async Task LoadPersonAsync(Page page, PersonParts parts, FeedList list)
    {
        var profile = await ProfileAsync(parts.UserId);
        if (!_pages.Contains(page))
            return;
        parts.Profile = profile;
        DrawTop(parts);
        Show();
        if (profile?.Blocked == true)
            return;
        await list.ReadAsync();
    }

    private static async Task<RpmPersonProfile?> ProfileAsync(string userId)
    {
        try
        {
            return await Community.GetPersonAsync(userId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
        {
            // The page shows the list's own line about the connection.
            return null;
        }
    }

    /// <summary>Draws the top of a person's page again from what was last read.</summary>
    private void DrawTop(PersonParts parts)
    {
        if (parts.Top is { } old)
            parts.Column.Children.Remove(old);
        parts.Top = null;
        if (parts.Profile is not { } person)
            return;
        parts.Top = FeedPerson.Top(person, Below(parts, person));
        parts.Column.Children.Insert(0, parts.Top);
    }

    /// <summary>Under the name on someone else's page: the friend keys, or the blocked line with Unblock.</summary>
    private FrameworkElement? Below(PersonParts parts, RpmPersonProfile person)
    {
        if (Auth.UserId is not { Length: > 0 } me || person.UserId == me)
            return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
        if (person.Blocked)
        {
            var blocked = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            blocked.Children.Add(new TextBlock { Text = Loc.Get("Rpm_Profile_Blocked"), FontSize = 13, Foreground = FeedCards.White(0x88), HorizontalAlignment = HorizontalAlignment.Center });
            blocked.Children.Add(Key<RpmButton>("Rpm_Profile_Unblock", () => ChangeAsync(parts, () => Community.UnblockAsync(person.UserId), readList: true)));
            return blocked;
        }
        switch (person.Friendship)
        {
            case "requested":
                row.Children.Add(Key<RpmButton>("Rpm_Profile_Requested", () => ChangeAsync(parts, () => Community.RemoveFriendAsync(person.UserId))));
                break;
            case "incoming":
                row.Children.Add(Key<RpmPrimaryButton>("Rpm_Profile_Accept", () => ChangeAsync(parts, () => Community.AcceptFriendAsync(person.UserId))));
                row.Children.Add(Key<RpmButton>("Rpm_Profile_Decline", () => ChangeAsync(parts, () => Community.RemoveFriendAsync(person.UserId))));
                break;
            case "friends":
                var friends = new RpmButton { Content = Loc.Get("Rpm_Profile_Friends") };
                friends.Click += (_, _) =>
                {
                    var menu = new MenuFlyout();
                    menu.Items.Add(Item("Rpm_Profile_RemoveFriend", () => ChangeAsync(parts, () => Community.RemoveFriendAsync(person.UserId))));
                    menu.ShowAt(friends);
                };
                row.Children.Add(friends);
                break;
            default:
                row.Children.Add(Key<RpmPrimaryButton>("Rpm_Profile_AddFriend", () => ChangeAsync(parts, () => Community.RequestFriendAsync(person.UserId))));
                break;
        }
        return row;
    }

    private static T Key<T>(string textKey, Func<Task> onClick) where T : Button, new()
    {
        var key = new T { Content = Loc.Get(textKey) };
        key.Click += async (_, _) =>
        {
            key.IsEnabled = false;
            await onClick();
            key.IsEnabled = true;
        };
        return key;
    }

    private static MenuFlyoutItem Item(string textKey, Func<Task> onClick)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(textKey) };
        item.Click += async (_, _) => await onClick();
        return item;
    }

    /// <summary>Sends a change to the friendship or the block, then reads the profile again so the page says
    /// where things stand now.</summary>
    /// <param name="readList">Read the person's posts again too (after unblocking, when they were hidden).</param>
    private async Task ChangeAsync(PersonParts parts, Func<Task> send, bool readList = false)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            await PromptDialog.ShowAsync(XamlRoot, null, Loc.Get("Rpm_Profile_SendFailed"), closeText: Loc.Get("Common_OK"));
            return;
        }
        var page = _pages.FirstOrDefault(p => p.Person == parts);
        if (page is null)
            return;
        parts.Profile = await ProfileAsync(parts.UserId) ?? parts.Profile;
        if (!_pages.Contains(page))
            return;
        DrawTop(parts);
        Show();
        if (readList && parts.Profile?.Blocked != true && page.List is { } list)
            await list.ReadAsync();
    }

    private void MenuKey_Click(object sender, RoutedEventArgs e)
    {
        if (PersonOnTop is not { Profile: { } person } parts)
            return;
        var menu = new MenuFlyout();
        if (person.UserId == Auth.UserId)
        {
            menu.Items.Add(Item("Rpm_Notify_Title", () =>
            {
                OpenNotices();
                return Task.CompletedTask;
            }));
            menu.Items.Add(Item("Rpm_Profile_EditPage", () =>
            {
                OpenEdit(parts, person);
                return Task.CompletedTask;
            }));
        }
        else if (person.Blocked)
        {
            menu.Items.Add(Item("Rpm_Profile_Unblock", () => ChangeAsync(parts, () => Community.UnblockAsync(person.UserId), readList: true)));
        }
        else
        {
            menu.Items.Add(Item("Rpm_Profile_Block", () => BlockAsync(parts, person)));
        }
        menu.ShowAt(MenuKey);
    }

    private async Task BlockAsync(PersonParts parts, RpmPersonProfile person)
    {
        var answer = await PromptDialog.ShowAsync(
            XamlRoot,
            null,
            Loc.Format("Rpm_Profile_BlockConfirm", person.DisplayName),
            primaryText: Loc.Get("Rpm_Profile_Block"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Close);
        if (answer != ContentDialogResult.Primary)
            return;
        await ChangeAsync(parts, () => Community.BlockAsync(person.UserId));
        // A blocked person's posts are left out: the page shows the blocked line alone.
        if (parts.Profile?.Blocked == true && _pages.FirstOrDefault(p => p.Person == parts) is { } page)
        {
            parts.Column.Children.Clear();
            DrawTop(parts);
            page.List = null;
        }
    }

    /// <summary>A post's corner menu on a person's page: Delete post on the reader's own, Report on
    /// someone else's.</summary>
    private void ShowPostMenu(FeedList list, FeedEntry entry, FrameworkElement at)
    {
        if (entry.Item.Post is not { } post)
            return;
        var menu = new MenuFlyout();
        if (post.OwnerId == Auth.UserId)
        {
            menu.Items.Add(Item("Rpm_Profile_DeletePost", async () =>
            {
                try
                {
                    await Community.DeletePostAsync(post.PostId);
                    list.Remove(entry);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                               or RpmCommunityRejectedException or InvalidOperationException)
                {
                    await PromptDialog.ShowAsync(XamlRoot, null, Loc.Get("Rpm_Profile_SendFailed"), closeText: Loc.Get("Common_OK"));
                }
            }));
        }
        else
        {
            menu.Items.Add(Item("Artists_WallReport", () =>
                FeedReport.AskAsync(XamlRoot, note => Community.ReportPostAsync(post.OwnerId, post.PostId, null, note))));
        }
        menu.ShowAt(at);
    }

    // ── a post with its replies ──

    private void OpenThread(string ownerId, string postId, RpmPost? post)
    {
        var thread = new FeedThread(ownerId, postId, post);
        thread.PersonRequested += id => OpenPerson(id);
        thread.PostChanged += changed =>
        {
            _everyone.Update(changed);
            foreach (var page in _pages)
                page.List?.Update(changed);
        };
        Push(new Page { Root = thread.Root });
        _ = thread.LoadAsync();
    }

    // ── notifications ──

    private void OpenNotices()
    {
        var notices = new FeedNotices();
        notices.PersonRequested += id => OpenPerson(id);
        notices.ThreadRequested += (owner, post) => OpenThread(owner, post, null);
        notices.AllRead += () =>
        {
            _markedRead = DateTimeOffset.UtcNow;
            ShowUnread(0);
        };
        Push(new Page { Root = notices.Root });
        _ = notices.LoadAsync();
    }

    // ── the edit page ──

    private void OpenEdit(PersonParts parts, RpmPersonProfile person)
    {
        var edit = new FeedEdit(person);
        var page = new Page { Root = edit.Root, MayLeave = edit.MayLeaveAsync };
        edit.Saved += async () =>
        {
            if (_pages.Contains(page))
            {
                Close(page);
                Show();
            }
            parts.Profile = await ProfileAsync(parts.UserId) ?? parts.Profile;
            DrawTop(parts);
            Show();
        };
        Push(page);
    }

    // ── a review in full ──

    private async void OpenReader(FeedList list, FeedEntry entry)
    {
        if (entry.Item.Review is not { } review)
            return;
        var depth = _pages.Count;
        var folder = await FeedCovers.FolderOfAsync(review);
        // Dropped when a page opened or closed while the folder was read.
        if (_pages.Count != depth)
            return;

        var votes = list.Votes(entry, enabled: review.UserId != Auth.UserId);
        var cover = new Image();
        var column = new StackPanel { Spacing = 14, Padding = new Thickness(16, 7, 16, 24) };
        var page = new Page
        {
            Root = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = column,
            },
            Reading = entry,
            ReaderVotes = votes,
        };
        Action? openAlbum = folder is null ? null : () =>
        {
            if (_pages.Contains(page))
            {
                Close(page);
                Show();
            }
            AlbumRequested?.Invoke(folder);
        };
        var everyone = list == _everyone;
        foreach (var part in FeedCards.ReaderParts(review, votes, cover, openAlbum, showAuthor: everyone,
                     everyone ? id => OpenPerson(id) : null))
            column.Children.Add(part);
        _ = FeedCovers.ShowAsync(cover, review);
        Push(page);
        Focus(FocusState.Programmatic);
    }
}

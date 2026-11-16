using Microsoft.UI.Xaml.Controls;
using RPM.Core.Cloud;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The left pane's Feed view: everyone's posts and album reviews, newest first, as on the phone apps' Feed
/// tab. It opens two kinds of page over the feed, one at a time on top of each other: a person's page (from
/// the profile key, or a name or photo on a card) and a review read in full (with Open album when the record
/// is in the collection on screen). The key at the right of the heading row goes back.
///
/// <para>The lists (<see cref="FeedList"/>) keep their own map of what arrives out of order. Two more
/// arrivals are this view's: a person's profile answering after their page was left, which is dropped by
/// the page's ticket (<see cref="_personTicket"/>); and a vote answered while the review is read in full,
/// which the list reports (<see cref="FeedList.Changed"/>) and the reader's keys are redrawn from.</para>
/// </summary>
internal sealed partial class FeedView : UserControl
{
    /// <summary>How many entries a page of everyone's feed asks for, as on the phone.</summary>
    private const int PageSize = 20;

    /// <summary>A feed shown again within this time is not read again.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();

    /// <summary>What the view shows, the feed at the bottom. The top one is on screen.</summary>
    private enum Screen
    {
        Feed,
        Person,
        Reader,
    }

    private readonly Stack<Screen> _screens = new([Screen.Feed]);
    private readonly FeedList _everyone;

    private FeedList? _person;
    private int _personTicket;

    private FeedEntry? _reading;
    private VoteKeys? _readerVotes;

    public FeedView()
    {
        InitializeComponent();
        _everyone = new FeedList(Column, Scroller, QuietLine,
            before => Community.GetFeedAsync(before, PageSize), everyone: true, "Rpm_Feed_Empty");
        Wire(_everyone);
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _screens.Count > 1)
            {
                Back();
                e.Handled = true;
            }
        };
    }

    /// <summary>Open album was pressed on a review, carrying the record's folder in the collection on
    /// screen.</summary>
    public event Action<string>? AlbumRequested;

    /// <summary>The view was put up. Reads the feed the first time, and again when it was last read more
    /// than a minute ago.</summary>
    public void Shown()
    {
        Show();
        if (DateTimeOffset.UtcNow - _everyone.ReadAt > Fresh)
            _ = _everyone.ReadAsync();
    }

    private void Wire(FeedList list)
    {
        list.ReadRequested += OpenReader;
        list.PersonRequested += OpenPerson;
        list.Changed += entry =>
        {
            if (_reading?.Key == entry.Key && _readerVotes is { } keys)
                FeedList.Paint(keys, entry.Item);
        };
    }

    /// <summary>Puts the top screen up and the keys that go with it.</summary>
    private void Show()
    {
        var top = _screens.Peek();
        Scroller.Visibility = Vis(top == Screen.Feed);
        PersonHost.Visibility = Vis(top == Screen.Person);
        Reader.Visibility = Vis(top == Screen.Reader);
        _everyone.Active = top == Screen.Feed;
        if (_person is not null)
            _person.Active = top == Screen.Person;

        ProfileKey.Visibility = Vis(top == Screen.Feed && Auth.UserId is { Length: > 0 });
        BackKey.Visibility = Vis(top != Screen.Feed);
    }

    private static Visibility Vis(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private void ProfileKey_Click(object sender, RoutedEventArgs e)
    {
        if (Auth.UserId is { Length: > 0 } me)
            OpenPerson(me);
    }

    private void BackKey_Click(object sender, RoutedEventArgs e) => Back();

    private void Back()
    {
        if (_screens.Count <= 1)
            return;
        switch (_screens.Pop())
        {
            case Screen.Reader:
                CloseReader();
                break;
            case Screen.Person:
                _personTicket++;
                _person = null;
                PersonHost.Children.Clear();
                break;
        }
        Show();
    }

    // ── a person's page ──

    /// <summary>Opens a person's page over the feed, in place of any page already open.</summary>
    private async void OpenPerson(string userId)
    {
        _reading = null;   // a review still being opened is not opened over this page
        while (_screens.Count > 1)
            Back();

        var ticket = ++_personTicket;
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
        PersonHost.Children.Add(scroller);

        var list = new FeedList(column, scroller, quiet,
            before => Community.GetPersonFeedAsync(userId, before, RpmCommunityClient.LongestPersonFeedPage),
            everyone: false, "Rpm_Profile_FeedEmpty");
        Wire(list);
        _person = list;
        _screens.Push(Screen.Person);
        Show();

        var profile = ProfileAsync(userId);
        _ = list.ReadAsync();
        if (await profile is { } person && ticket == _personTicket)
            column.Children.Insert(0, FeedPerson.Top(person));
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

    // ── a review in full ──

    private async void OpenReader(FeedList list, FeedEntry entry)
    {
        if (entry.Item.Review is not { } review)
            return;
        _reading = entry;
        var under = _screens.Peek();
        var folder = await FeedCovers.FolderOfAsync(review);
        // Dropped when another review was asked for, or a page opened or closed, while the folder was read.
        if (_reading != entry || _screens.Peek() != under)
            return;

        var votes = _readerVotes = list.Votes(entry, enabled: review.UserId != Auth.UserId);
        var cover = new Image();
        Action? openAlbum = folder is null ? null : () =>
        {
            Back();
            AlbumRequested?.Invoke(folder);
        };
        var everyone = list == _everyone;
        ReaderColumn.Children.Clear();
        foreach (var part in FeedCards.ReaderParts(review, votes, cover, openAlbum, showAuthor: everyone,
                     everyone ? OpenPerson : null))
            ReaderColumn.Children.Add(part);
        _ = FeedCovers.ShowAsync(cover, review);

        Reader.ChangeView(null, 0, null, disableAnimation: true);
        _screens.Push(Screen.Reader);
        Show();
        Focus(FocusState.Programmatic);
    }

    private void CloseReader()
    {
        if (_reading is { } entry && _readerVotes is { } votes)
            entry.Votes.Remove(votes);
        _reading = null;
        _readerVotes = null;
        ReaderColumn.Children.Clear();
    }
}

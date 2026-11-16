using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The left pane's Feed view: everyone's posts and album reviews, newest first, as on the phone apps'
/// Feed tab, with the phone's card sizes. A review opens in full in the pane, with Open album when the
/// record is in the collection on screen.
///
/// <para>What arrives on its own schedule, and what keeps each pair safe. Every continuation runs on the
/// UI thread, so two of these never run at the same time; only their order varies.</para>
/// <list type="bullet">
/// <item>A read and an older read: each read takes a ticket (<see cref="_newest"/>). An answer whose ticket
/// is not the newest is dropped.</item>
/// <item>A further page and a read: the page keeps the ticket it started under and is dropped if a read
/// started since. Two further pages never run together (<see cref="_readingMore"/>).</item>
/// <item>A vote answer and a read: the answer is applied by key to whatever entry holds that key now. A
/// fresh read that brought the same entry back is given the server's newer counts, which is correct.</item>
/// <item>Two votes on one entry: only the answer to the latest vote sent for that key is applied
/// (<see cref="_votesSent"/>), so a slow first answer cannot overwrite the second.</item>
/// <item>A cover found and a card rebuilt: covers are written to the card's own Image, and cards are never
/// reused for another entry, so a late cover lands on a card that may be gone, which is harmless.</item>
/// </list>
/// </summary>
internal sealed partial class FeedView : UserControl
{
    /// <summary>How many entries a page asks for, as on the phone.</summary>
    private const int PageSize = 20;

    /// <summary>A feed shown again within this time is not read again.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    /// <summary>How close to the foot of the list the next page is asked for.</summary>
    private const double MoreWithin = 600;

    /// <summary>How many empty pages in a row are read before stopping. A page is empty when everything
    /// on it was by a person the reader blocked; the server still gives the next page.</summary>
    private const int EmptyPagesInARow = 5;

    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();
    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();
    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();

    /// <summary>Covers found by the cover search, by artist and title, for the records that are not in the
    /// collection. Kept for the life of the app, as the phone's <c>AlbumCovers</c> does.</summary>
    private static readonly ConcurrentDictionary<string, Task<string?>> Found = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Entry> _entries = [];
    private readonly Dictionary<string, int> _votesSent = [];

    private int _newest;
    private bool _readingMore;
    private long? _next;
    private DateTimeOffset _readAt;
    private Entry? _reading;
    private VoteKeys? _readerVotes;

    public FeedView()
    {
        InitializeComponent();
        Column.MaxWidth = FeedCards.PhoneWidth;
        ReaderColumn.MaxWidth = FeedCards.PhoneWidth;
        // A page that does not fill the pane gives no scroll to ask for the next one, so a change in the
        // column's height asks too.
        Column.SizeChanged += (_, _) => MoreIfShort();
        KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _reading is not null)
            {
                CloseReader();
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
        if (DateTimeOffset.UtcNow - _readAt > Fresh)
            _ = ReadAsync();
    }

    // ── reading ──

    private async Task ReadAsync()
    {
        var ticket = ++_newest;
        _readAt = DateTimeOffset.UtcNow;
        RpmFeedPage? page;
        try
        {
            page = await Community.GetFeedAsync(before: null, PageSize);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
        {
            page = null;
        }
        if (ticket != _newest)
            return;

        if (page is null)
        {
            // Read again on the next show, not in a minute: there is nothing on screen to keep.
            _readAt = default;
            if (_entries.Count == 0)
                Quiet("Rpm_Profile_Offline");
            return;
        }

        foreach (var entry in _entries)
            Column.Children.Remove(entry.Card);
        _entries.Clear();
        _votesSent.Clear();
        _next = page.Next;
        Add(page.Known);
        Quiet(_entries.Count == 0 && _next is null ? "Rpm_Feed_Empty" : null);

        if (_entries.Count == 0 && _next is not null)
            await MoreAsync();
    }

    /// <summary>Reads the next page, and the ones after it while they come back empty.</summary>
    private async Task MoreAsync()
    {
        if (_readingMore || _next is null)
            return;
        _readingMore = true;
        try
        {
            var ticket = _newest;
            for (var empty = 0; _next is { } before && empty < EmptyPagesInARow; empty++)
            {
                RpmFeedPage? page;
                try
                {
                    page = await Community.GetFeedAsync(before, PageSize);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
                {
                    return;
                }
                if (ticket != _newest || page is null)
                    return;

                _next = page.Next;
                var known = _entries.Select(e => e.Key).ToHashSet();
                var added = page.Known.Where(i => !known.Contains(KeyOf(i))).ToList();
                Add(added);
                if (added.Count > 0)
                    break;
            }
            if (_entries.Count == 0 && _next is null)
                Quiet("Rpm_Feed_Empty");
        }
        finally
        {
            _readingMore = false;
        }
        MoreIfShort();
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => MoreIfShort();

    /// <summary>Reads the next page when the foot of the list is near, or already on screen.</summary>
    private void MoreIfShort()
    {
        if (_next is not null && _entries.Count > 0 && Scroller.Visibility == Visibility.Visible
            && Scroller.ScrollableHeight - Scroller.VerticalOffset < MoreWithin)
            _ = MoreAsync();
    }

    private void Quiet(string? key)
    {
        QuietLine.Text = key is null ? "" : Loc.Get(key);
        QuietLine.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── the cards ──

    /// <summary>One entry of the feed and what draws it. The item is replaced when a vote is answered, and
    /// every <see cref="VoteKeys"/> drawing it (the card's, and the reader's while it is open) is
    /// redrawn.</summary>
    private sealed class Entry(string key, RpmFeedItem item)
    {
        public string Key { get; } = key;
        public RpmFeedItem Item { get; set; } = item;
        public FrameworkElement Card { get; set; } = null!;
        public List<VoteKeys> Votes { get; } = [];
    }

    /// <summary>A post by its id; a review by its author and album, because two people's reviews of one
    /// record stand side by side.</summary>
    private static string KeyOf(RpmFeedItem item) =>
        item.Post is { } post ? "post:" + post.PostId : $"review:{item.Review!.UserId}:{item.Review.AlbumId}";

    private void Add(IEnumerable<RpmFeedItem> items)
    {
        var me = Auth.UserId;
        foreach (var item in items)
        {
            var entry = new Entry(KeyOf(item), item);
            if (item.Post is { } post)
            {
                var votes = Votes(entry, enabled: post.OwnerId != me);
                entry.Card = FeedCards.Post(post, votes);
            }
            else
            {
                var review = item.Review!;
                var votes = Votes(entry, enabled: review.UserId != me);
                var cover = new Image();
                entry.Card = FeedCards.Review(review, votes, cover, () => OpenReader(entry));
                _ = ShowCoverAsync(cover, review);
            }
            _entries.Add(entry);
            Column.Children.Add(entry.Card);
        }
    }

    private VoteKeys Votes(Entry entry, bool enabled)
    {
        var keys = new VoteKeys(enabled, value => _ = VoteAsync(entry.Key, value));
        Paint(keys, entry.Item);
        entry.Votes.Add(keys);
        return keys;
    }

    private static void Paint(VoteKeys keys, RpmFeedItem item)
    {
        if (item.Post is { } post)
            keys.Show(post.Ups, post.Downs, post.MyVote);
        else if (item.Review is { } review)
            keys.Show(review.Ups, review.Downs, review.MyVote);
    }

    private async Task VoteAsync(string key, int value)
    {
        if (_entries.FirstOrDefault(e => e.Key == key) is not { } entry)
            return;
        var sent = _votesSent[key] = _votesSent.GetValueOrDefault(key) + 1;

        RpmFeedItem? changed = null;
        try
        {
            if (entry.Item.Post is { } post)
            {
                if (await Community.VotePostAsync(post.OwnerId, post.PostId, value) is { } answer)
                    changed = entry.Item with { Post = answer };
            }
            else if (entry.Item.Review is { } review)
            {
                if (await Community.VoteAsync(review.AlbumId, review.UserId, value) is { } answer)
                    changed = entry.Item with { Review = review.With(answer) };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or RpmCommunityRejectedException or InvalidOperationException)
        {
            // Signed out, offline or refused. The counts stay as they were, as on the phone.
            return;
        }

        if (changed is null || _votesSent.GetValueOrDefault(key) != sent)
            return;
        if (_entries.FirstOrDefault(e => e.Key == key) is not { } now)
            return;
        now.Item = changed;
        foreach (var keys in now.Votes)
            Paint(keys, changed);
        if (_reading is { } open && open != now && open.Key == key)
        {
            open.Item = changed;
            foreach (var keys in open.Votes)
                Paint(keys, changed);
        }
    }

    // ── a review in full ──

    private async void OpenReader(Entry entry)
    {
        if (entry.Item.Review is not { } review)
            return;
        CloseReader();
        _reading = entry;
        var folder = await FolderOfAsync(review);
        if (_reading != entry)
            return;

        var votes = _readerVotes = Votes(entry, enabled: review.UserId != Auth.UserId);
        var cover = new Image();

        Action? openAlbum = folder is null ? null : () =>
        {
            CloseReader();
            AlbumRequested?.Invoke(folder);
        };
        foreach (var part in FeedCards.ReaderParts(review, votes, cover, CloseReader, openAlbum))
            ReaderColumn.Children.Add(part);
        _ = ShowCoverAsync(cover, review);

        Reader.ChangeView(null, 0, null, disableAnimation: true);
        Scroller.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        Focus(FocusState.Programmatic);
    }

    private void CloseReader()
    {
        if (_reading is { } entry && _readerVotes is { } votes)
            entry.Votes.Remove(votes);
        _reading = null;
        _readerVotes = null;
        Reader.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        ReaderColumn.Children.Clear();
    }

    // ── covers and the collection ──

    /// <summary>The record's folder in the collection on screen, or null when it is not there. Read off the
    /// UI thread.</summary>
    private static async Task<string?> FolderOfAsync(RpmFeedReview review)
    {
        if (string.IsNullOrWhiteSpace(review.AlbumTitle) || Library.ShelfRoot is null)
            return null;
        try
        {
            return await Task.Run(() =>
            {
                var shelf = Library.Shelf;
                return Library.Read(shelf, "a feed review's record",
                        db => db.FindAlbumFolder(review.AlbumTitle, review.AlbumArtist))
                    .Match<string?>(folder => folder, _ => null);
            });
        }
        catch (InvalidOperationException)
        {
            // The library closed while it was being read.
            return null;
        }
    }

    /// <summary>The record's cover: the reader's own copy's when it is in the collection, else the first
    /// the cover search finds, else the record glyph stays.</summary>
    private static async Task ShowCoverAsync(Image target, RpmFeedReview review)
    {
        if (await FolderOfAsync(review) is { } folder)
        {
            var bytes = await Task.Run(() =>
                FolderArtWriter.FindFolderImage(folder) is { } image ? ThumbCache.GetFile(image, ArtScaler.RowEdge) : null);
            if (bytes is { Length: > 0 })
            {
                try
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(BitmapBytes.Stream(bytes));
                    target.Source = bitmap;
                    return;
                }
                catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
                {
                    // A picture that does not decode. The search below may still find one.
                }
            }
        }

        if (await FoundCoverAsync(review.AlbumArtist, review.AlbumTitle) is { } address
            && Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            target.Source = new BitmapImage(uri) { DecodePixelWidth = ArtScaler.RowEdge };
        }
    }

    private static Task<string?> FoundCoverAsync(string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult<string?>(null);
        var key = $"{artist.Trim()}\u001F{title.Trim()}";
        return Found.GetOrAdd(key, _ => SearchAsync(key, artist, title));

        static async Task<string?> SearchAsync(string key, string artist, string title)
        {
            var search = App.Services.GetRequiredService<ICoverSearchApi>();
            var asked = await search.SearchAsync(new CoverSearchQuery(Album: title, Artist: artist, Limit: 1));
            return asked.Match<string?>(
                found => found.Results.FirstOrDefault() is { } first
                    ? (string.IsNullOrEmpty(first.ThumbUrl) ? first.FullUrl : first.ThumbUrl)
                    : null,
                failure =>
                {
                    // A failed search is asked again next time. Only an answer is kept.
                    Found.TryRemove(key, out var _);
                    return null;
                });
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Cloud;
using RPM.Core.Import;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>One entry of a feed and what draws it. The item is replaced when a vote is answered, and every
/// <see cref="VoteKeys"/> in <see cref="Votes"/> is redrawn.</summary>
internal sealed class FeedEntry(string key, RpmFeedItem item)
{
    public string Key { get; } = key;
    public RpmFeedItem Item { get; set; } = item;
    public FrameworkElement Card { get; set; } = null!;
    public List<VoteKeys> Votes { get; } = [];

    /// <summary>A post by its id; a review by its author and album, because two people's reviews of one
    /// record stand side by side.</summary>
    public static string KeyOf(RpmFeedItem item) =>
        item.Post is { } post ? "post:" + post.PostId : $"review:{item.Review!.UserId}:{item.Review.AlbumId}";
}

/// <summary>
/// A list of feed cards in a column, read a page at a time from <c>read</c>: everyone's feed, or one
/// person's. It draws the cards, follows the pages as the column scrolls, and sends votes.
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
/// <item>A list put away while it reads: a person's page builds a new list and drops the old one with its
/// column, so the old list's answers fill a column nobody shows.</item>
/// </list>
/// </summary>
internal sealed class FeedList
{
    /// <summary>How close to the foot of the list the next page is asked for.</summary>
    private const double MoreWithin = 600;

    /// <summary>How many empty pages in a row are read before stopping. A page is empty when everything
    /// on it was by a person the reader blocked; the server still gives the next page.</summary>
    private const int EmptyPagesInARow = 5;

    private static AuthService Auth => App.Services.GetRequiredService<AuthService>();
    private static RpmCommunityClient Community => App.Services.GetRequiredService<RpmCommunityClient>();

    private readonly Panel _column;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _quiet;
    private readonly Func<long?, Task<RpmFeedPage?>> _read;
    private readonly bool _everyone;
    private readonly string _emptyKey;

    private readonly List<FeedEntry> _entries = [];
    private readonly Dictionary<string, int> _votesSent = [];
    private int _newest;
    private bool _readingMore;
    private long? _next;

    /// <param name="column">The panel the cards are added to, after whatever it already holds.</param>
    /// <param name="quiet">The line that says the list is empty or could not be read.</param>
    /// <param name="read">Reads the page before the given time; null for the first page.</param>
    /// <param name="everyone">Everyone's feed: reviews carry their author's line, and names open the
    /// person's page. On a person's own page every entry is theirs, so neither is drawn.</param>
    /// <param name="emptyKey">The string key of what an empty list says.</param>
    public FeedList(Panel column, ScrollViewer scroller, TextBlock quiet, Func<long?, Task<RpmFeedPage?>> read,
        bool everyone, string emptyKey)
    {
        _column = column;
        _scroller = scroller;
        _quiet = quiet;
        _read = read;
        _everyone = everyone;
        _emptyKey = emptyKey;
        _scroller.ViewChanged += (_, _) => MoreIfShort();
        // A page that does not fill the pane gives no scroll to ask for the next one, so a change in the
        // column's height asks too.
        _column.SizeChanged += (_, _) => MoreIfShort();
    }

    /// <summary>A review's Read full review was pressed.</summary>
    public event Action<FeedList, FeedEntry>? ReadRequested;

    /// <summary>A person's name or photo was pressed, carrying their user id.</summary>
    public event Action<string>? PersonRequested;

    /// <summary>A vote was answered and the entry changed. The view redraws a review read in full.</summary>
    public event Action<FeedEntry>? Changed;

    /// <summary>When the first page was last asked for.</summary>
    public DateTimeOffset ReadAt { get; private set; }

    /// <summary>Whether the list is on screen. A list behind another screen asks for no pages.</summary>
    public bool Active { get; set; }

    public async Task ReadAsync()
    {
        var ticket = ++_newest;
        ReadAt = DateTimeOffset.UtcNow;
        RpmFeedPage? page;
        try
        {
            page = await _read(null);
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
            ReadAt = default;
            if (_entries.Count == 0)
                Quiet("Rpm_Profile_Offline");
            return;
        }

        foreach (var entry in _entries)
            _column.Children.Remove(entry.Card);
        _entries.Clear();
        _votesSent.Clear();
        _next = page.Next;
        Add(page.Known);
        Quiet(_entries.Count == 0 && _next is null ? _emptyKey : null);

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
                    page = await _read(before);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RpmCommunityRejectedException)
                {
                    return;
                }
                if (ticket != _newest || page is null)
                    return;

                _next = page.Next;
                var known = _entries.Select(e => e.Key).ToHashSet();
                var added = page.Known.Where(i => !known.Contains(FeedEntry.KeyOf(i))).ToList();
                Add(added);
                if (added.Count > 0)
                    break;
            }
            if (_entries.Count == 0 && _next is null)
                Quiet(_emptyKey);
        }
        finally
        {
            _readingMore = false;
        }
        MoreIfShort();
    }

    /// <summary>Reads the next page when the foot of the list is near, or already on screen.</summary>
    private void MoreIfShort()
    {
        if (Active && _next is not null && _entries.Count > 0
            && _scroller.ScrollableHeight - _scroller.VerticalOffset < MoreWithin)
            _ = MoreAsync();
    }

    private void Quiet(string? key)
    {
        _quiet.Text = key is null ? "" : Loc.Get(key);
        _quiet.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Add(IEnumerable<RpmFeedItem> items)
    {
        var me = Auth.UserId;
        Action<string>? person = _everyone ? id => PersonRequested?.Invoke(id) : null;
        foreach (var item in items)
        {
            var entry = new FeedEntry(FeedEntry.KeyOf(item), item);
            if (item.Post is { } post)
            {
                entry.Card = FeedCards.Post(post, Votes(entry, enabled: post.OwnerId != me), person);
            }
            else
            {
                var review = item.Review!;
                var cover = new Image();
                entry.Card = FeedCards.Review(review, Votes(entry, enabled: review.UserId != me), cover,
                    () => ReadRequested?.Invoke(this, entry), showAuthor: _everyone, person);
                _ = FeedCovers.ShowAsync(cover, review);
            }
            _entries.Add(entry);
            _column.Children.Add(entry.Card);
        }
    }

    /// <summary>Vote keys for an entry, drawn from its item and redrawn with it.</summary>
    public VoteKeys Votes(FeedEntry entry, bool enabled)
    {
        var keys = new VoteKeys(enabled, value => _ = VoteAsync(entry.Key, value));
        Paint(keys, entry.Item);
        entry.Votes.Add(keys);
        return keys;
    }

    public static void Paint(VoteKeys keys, RpmFeedItem item)
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
        Changed?.Invoke(now);
    }
}

/// <summary>The covers on review cards, and where a reviewed record is in the collection on screen.</summary>
internal static class FeedCovers
{
    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();

    /// <summary>Covers found by the cover search, by artist and title, for the records that are not in the
    /// collection. Kept for the life of the app, as the phone's <c>AlbumCovers</c> does.</summary>
    private static readonly ConcurrentDictionary<string, Task<string?>> Found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The record's folder in the collection on screen, or null when it is not there. Read off the
    /// UI thread.</summary>
    public static async Task<string?> FolderOfAsync(RpmFeedReview review)
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
    public static async Task ShowAsync(Image target, RpmFeedReview review)
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

        if (await FoundAsync(review.AlbumArtist, review.AlbumTitle) is { } address
            && Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            target.Source = new BitmapImage(uri) { DecodePixelWidth = ArtScaler.RowEdge };
        }
    }

    private static Task<string?> FoundAsync(string artist, string title)
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

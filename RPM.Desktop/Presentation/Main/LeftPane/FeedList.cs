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
    private readonly bool _postMenus;

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
    /// <param name="postMenus">Give every post the key in its corner that opens its menu (delete or
    /// report), as a person's page does.</param>
    public FeedList(Panel column, ScrollViewer scroller, TextBlock quiet, Func<long?, Task<RpmFeedPage?>> read,
        bool everyone, string emptyKey, bool postMenus)
    {
        _postMenus = postMenus;
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

    /// <summary>A post was pressed, or its reply key: open it with its replies.</summary>
    public event Action<FeedList, FeedEntry>? ThreadRequested;

    /// <summary>A post's corner key was pressed: the entry, and the key to open the menu at.</summary>
    public event Action<FeedList, FeedEntry, FrameworkElement>? PostMenuRequested;

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
        // The feed is read when its pane is put up, so the answer usually arrives while the pane is still
        // sliding in. Building the cards takes about a tenth of a second, which the move needs for its frames.
        await Common.PaneMotion.SettledAsync();
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
                entry.Card = PostCard(entry, post, me);
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

    private FrameworkElement PostCard(FeedEntry entry, RpmPost post, string? me)
    {
        Action<string>? person = _everyone ? id => PersonRequested?.Invoke(id) : null;
        Action<FrameworkElement>? menu = _postMenus && me is { Length: > 0 }
            ? at => PostMenuRequested?.Invoke(this, entry, at)
            : null;
        entry.Votes.Clear();
        return FeedCards.Post(post, Votes(entry, enabled: post.OwnerId != me), person,
            () => ThreadRequested?.Invoke(this, entry), menu);
    }

    /// <summary>A post changed somewhere else (its thread): the card is drawn again in its place.</summary>
    public void Update(RpmPost post)
    {
        if (_entries.FirstOrDefault(e => e.Item.Post?.PostId == post.PostId) is not { } entry)
            return;
        entry.Item = entry.Item with { Post = post };
        var at = _column.Children.IndexOf(entry.Card);
        entry.Card = PostCard(entry, post, Auth.UserId);
        if (at >= 0)
            _column.Children[at] = entry.Card;
    }

    /// <summary>A post just written, at the top of the list.</summary>
    public void Prepend(RpmPost post)
    {
        var item = new RpmFeedItem("post", post.CreatedAt, post);
        var entry = new FeedEntry(FeedEntry.KeyOf(item), item);
        entry.Card = PostCard(entry, post, Auth.UserId);
        var at = _entries.Count > 0 ? _column.Children.IndexOf(_entries[0].Card) : _column.Children.Count;
        _entries.Insert(0, entry);
        _column.Children.Insert(Math.Max(0, at), entry.Card);
        Quiet(null);
    }

    /// <summary>A post deleted here leaves the list.</summary>
    public void Remove(FeedEntry entry)
    {
        _entries.Remove(entry);
        _column.Children.Remove(entry.Card);
        if (_entries.Count == 0 && _next is null)
            Quiet(_emptyKey);
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
    /// the cover search finds, else the record glyph stays.
    ///
    /// <para>The folder from the collection is relative to the library's root, as every wall holds it, so
    /// the cover is read through <see cref="AlbumCovers"/>, as every wall reads it: the picture beside the
    /// files, the art inside them, or the cloud library's.</para></summary>
    public static async Task ShowAsync(Image target, RpmFeedReview review)
    {
        if (await FolderOfAsync(review) is { } folder)
        {
            byte[]? bytes = null;
            try
            {
                // A cover that could not be read is left to the search below, as a missing one is.
                bytes = (await AlbumCovers.LoadForSquareAsync(folder, CancellationToken.None, ArtScaler.RowEdge))
                    .Answers(out var found, out _) ? found : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A file that went, or a library that closed. The search below may still find one.
            }
            Say($"cover of {review.AlbumTitle}: in the collection at {folder}, {(bytes is { Length: > 0 } ? "drawn from it" : "no picture there")}");
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

        var address = await FoundAsync(review.AlbumArtist, review.AlbumTitle);
        Say($"cover of {review.AlbumTitle}: cover search {(address is null ? "found none" : "found one")}");
        if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            target.Source = new BitmapImage(uri) { DecodePixelWidth = ArtScaler.RowEdge };
    }

    private static void Say(string line)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said("profile", line);
    }

    /// <summary>
    /// The cover search, through the app's disk cache: one answer per record is kept for a month (a "none"
    /// for a week), so a record is searched once and not on every launch. The search is the API's
    /// <c>artwork/search</c>, which counts against a daily allowance per account (it pays for a web search
    /// behind it), the same one the import's cover picker spends.
    ///
    /// <para>Only real answers are kept (see <c>HttpCacheHandler</c>): a refusal or a failure is not stored
    /// as "no cover".</para>
    /// </summary>
    private static async Task<Asked<CoverSearchResponse>> SearchCachedAsync(string artist, string title)
    {
        try
        {
            var token = await App.Services.GetRequiredService<AuthService>().GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return Asked.Failed<CoverSearchResponse>("not signed in");
            var url = $"artwork/search?limit=1&artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(title)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await Searches.Value.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return Asked.Failed<CoverSearchResponse>($"HTTP {(int)response.StatusCode}");
            var found = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync(
                response.Content, CatalogWireJson.Default.CoverSearchResponse);
            return found is null
                ? Asked.Failed<CoverSearchResponse>("the answer had no body")
                : Asked.Answer(found);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Asked.Failed<CoverSearchResponse>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The client <see cref="SearchCachedAsync"/> asks through: the API, behind the disk cache.</summary>
    private static readonly Lazy<HttpClient> Searches = new(() =>
    {
        var config = App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RPM.Desktop.Models.AppConfig>>();
        var api = RPM.Core.Net.RpmApi.At(config.Value.IdentificationApiUrl) ?? RPM.Core.Net.RpmApi.Deployed;
        var policy = new RPM.Core.Net.WebCachePolicy("art", TimeSpan.FromDays(30), TimeSpan.FromDays(7)) { Vary = "feed-cover" };
        return Services.Platform.AppCache.Client(policy, api, TimeSpan.FromSeconds(20));
    });

    /// <summary>Lets one cover search run at a time.</summary>
    private static readonly SemaphoreSlim OneSearch = new(1, 1);

    /// <summary>How long the searches rest after one was refused or failed.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    /// <summary>Until when no search is sent. Read and written only while <see cref="OneSearch"/> is held.</summary>
    private static DateTimeOffset _quietUntil;

    private static Task<string?> FoundAsync(string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult<string?>(null);
        var key = $"{artist.Trim()}\u001F{title.Trim()}";
        return Found.GetOrAdd(key, _ => SearchAsync(key, artist, title));

        static async Task<string?> SearchAsync(string key, string artist, string title)
        {
            // One search at a time, and none for a while after a refusal: a feed page asks for a cover for
            // every review of a record the reader does not own, all at once, and the server answers a burst
            // with 429. A cover not found now is asked for again when the card is drawn again.
            await OneSearch.WaitAsync();
            Asked<CoverSearchResponse> asked;
            try
            {
                if (DateTimeOffset.UtcNow < _quietUntil)
                {
                    Found.TryRemove(key, out var _);
                    return null;
                }
                asked = await SearchCachedAsync(artist, title);
                if (!asked.Answers(out _, out var why))
                {
                    _quietUntil = DateTimeOffset.UtcNow + Quiet;
                    Say($"cover search for {title} did not answer: {why}; no searches for a minute");
                }
            }
            finally
            {
                OneSearch.Release();
            }
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

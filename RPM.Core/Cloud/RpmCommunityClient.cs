using System.Net;
using System.Text.Json.Serialization.Metadata;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>The name somebody asked for is already somebody else's.</summary>
public sealed class RpmNameTakenException(string name)
    : Exception($"The name “{name}” is taken.");

/// <summary>The server refused the write and said why. The message is meant to be shown.</summary>
public sealed class RpmCommunityRejectedException(string message) : Exception(message);

/// <summary>RPM's entry for an album: its id, and what the community has said about it.</summary>
public sealed record RpmAlbum(
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("catno")] string? Catno,
    [property: JsonPropertyName("discogsReleaseId")] string? DiscogsReleaseId,
    [property: JsonPropertyName("reviewCount")] int ReviewCount,
    [property: JsonPropertyName("commentCount")] int CommentCount,
    [property: JsonPropertyName("averageRating")] double AverageRating,
    [property: JsonPropertyName("ratingCount")] int RatingCount,
    [property: JsonPropertyName("musicBrainzReleaseId")] string? MusicBrainzReleaseId = null,
    [property: JsonPropertyName("barcode")] string? Barcode = null,
    [property: JsonPropertyName("groupId")] string? GroupId = null)
{
    /// <summary>Whether RPM knows which pressing this is, not only which album. An entry matched only on
    /// artist, title and year is on the right album, but it says nothing about the exact record. The
    /// album page's link control exists to fix this.</summary>
    public bool IsLinkedToPressing =>
        !string.IsNullOrEmpty(DiscogsReleaseId) ||
        !string.IsNullOrEmpty(MusicBrainzReleaseId) ||
        !string.IsNullOrEmpty(Barcode) ||
        !string.IsNullOrEmpty(Catno);
}

/// <summary>
/// How often RPM's listeners have played one song of a record.
/// </summary>
/// <param name="Title">The song as somebody's tags spell it. This is the first spelling that the server
/// was sent. Songs are matched to the shelf's own lines by name.</param>
/// <param name="Plays">Everybody's plays added up. This is not one listener's count, and it cannot be
/// known per person. The server keeps one counter per song, with no user, no time and no order
/// attached.</param>
public sealed record RpmTrackPlays(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("plays")] long Plays);

/// <summary>One catalogue's name for a pressing, as the album page lists them.</summary>
/// <param name="Namespace">"discogs", "musicbrainz", "barcode", "catno" or "discid".</param>
public sealed record RpmAlbumAlias(
    [property: JsonPropertyName("namespace")] string Namespace,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("createdAt")] long CreatedAt);

/// <param name="Rating">Halves of a star, doubled: 1 to 10. Divide by two to draw it.</param>
/// <param name="MyVote">1, -1 or 0: how the signed-in reader voted. Always 0 when signed out.</param>
/// <param name="AuthorDeleted">The author's account was deleted. The name is empty, and the app draws
/// "Deleted user" instead. This is false when the server is older than account deletion.</param>
public sealed record RpmReview(
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("authorName")] string AuthorName,
    [property: JsonPropertyName("hasAvatar")] bool HasAvatar,
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("updatedAt")] long UpdatedAt,
    [property: JsonPropertyName("ups")] int Ups,
    [property: JsonPropertyName("downs")] int Downs,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("myVote")] int MyVote,
    [property: JsonPropertyName("authorDeleted")] bool AuthorDeleted = false)
{
    /// <summary>The rating as stars, halves included.</summary>
    public double Stars => Rating / 2d;

    public DateTimeOffset Written => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
}

public sealed record RpmReviewPage(
    [property: JsonPropertyName("items")] IReadOnlyList<RpmReview> Items,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("sort")] string Sort)
{
    /// <summary>How many pages the tabs should offer. At least one, so an album with no reviews
    /// still has a page to be on.</summary>
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <param name="AuthorDeleted">As on <see cref="RpmReview"/>.</param>
public sealed record RpmComment(
    [property: JsonPropertyName("albumId")] string AlbumId,
    [property: JsonPropertyName("commentId")] string CommentId,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("authorName")] string AuthorName,
    [property: JsonPropertyName("hasAvatar")] bool HasAvatar,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("authorDeleted")] bool AuthorDeleted = false)
{
    public DateTimeOffset Written => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
}

/// <summary>RPM's entry for an artist: the id of their wall, and the name that RPM uses for them.</summary>
/// <param name="MusicBrainzArtistId">Their MusicBrainz id, once someone has given RPM one. It is null
/// until then, which is the usual state.</param>
public sealed record RpmArtist(
    [property: JsonPropertyName("artistId")] string ArtistId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("musicBrainzArtistId")] string? MusicBrainzArtistId = null);

/// <summary>
/// One line on an artist's wall: something a listener wrote, or something the server wrote when a
/// listener reviewed or rated one of that artist's records.
/// </summary>
/// <param name="Kind">"post" or "review". Read through <see cref="IsPost"/> rather than compared
/// here, so an unknown kind from a newer server reads as neither rather than as a post.</param>
/// <param name="Body">What was written, on a post. Empty on a review line.</param>
/// <param name="Rating">Halves of a star, doubled: 1 to 10. Zero on a post.</param>
/// <param name="HasBody">Whether the review that this line stands for has written text. This is the
/// difference between "rated" and "wrote a review".</param>
/// <param name="AuthorDeleted">As on <see cref="RpmReview"/>.</param>
public sealed record RpmWallEntry(
    [property: JsonPropertyName("artistId")] string ArtistId,
    [property: JsonPropertyName("entryId")] string EntryId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("authorName")] string AuthorName,
    [property: JsonPropertyName("hasAvatar")] bool HasAvatar,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    [property: JsonPropertyName("albumId")] string? AlbumId = null,
    [property: JsonPropertyName("albumTitle")] string? AlbumTitle = null,
    [property: JsonPropertyName("rating")] int Rating = 0,
    [property: JsonPropertyName("hasBody")] bool HasBody = false,
    [property: JsonPropertyName("authorDeleted")] bool AuthorDeleted = false)
{
    public const string PostKind = "post";
    public const string ReviewKind = "review";

    public bool IsPost => string.Equals(Kind, PostKind, StringComparison.OrdinalIgnoreCase);

    public bool IsReview => string.Equals(Kind, ReviewKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The rating as stars, halves included. Zero on a post.</summary>
    public double Stars => Rating / 2d;

    public DateTimeOffset Written => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt);
}

public sealed record RpmProfile(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("hasAvatar")] bool HasAvatar,
    [property: JsonPropertyName("avatarUpdatedAt")] long AvatarUpdatedAt,
    [property: JsonPropertyName("createdAt")] long CreatedAt,
    /// <summary>Whether this person supports rpm. It decides the mark beside their name. The default is
    /// false, so a server that does not send this field reports nobody as a supporter, rather than
    /// failing to parse.</summary>
    [property: JsonPropertyName("supporter")] bool Supporter = false);

/// <summary>How a review list is ordered. The values are the sort tabs on the album page.</summary>
public enum RpmReviewSort
{
    Votes,
    Date,
    Length,
    Positive,
    Negative,
}

/// <summary>The desktop's part of the community: RPM's shared album entries, the reviews that people
/// wrote about them, and the artists those records are filed under, with their walls.
///
/// <para>Reading does not need a token, but writing does. That split is the main design of this client.
/// Every read method works when signed out, so an album page can show its reviews to somebody who has
/// never made an account. Every write throws <see cref="InvalidOperationException"/> when it is called
/// without a token. It does not send an unsigned request and then read a 401 back.</para>
///
/// <para>An album must be resolved before anything else can be asked about it. This is because the id
/// that RPM uses is created on the server, from what the tags say. A client that already holds an id
/// goes straight to the reviews.</para>
/// </summary>
public sealed partial class RpmCommunityClient(HttpClient http, Func<CancellationToken, Task<string?>> accessTokenProvider)
{
    /// <summary>The API's address. A caller uses it to build the full URL of an avatar. An avatar is the
    /// one thing here that an image control fetches for itself, not through this client.</summary>
    public Uri? BaseAddress => http.BaseAddress;

    // ── albums ──

    /// <summary>The album's RPM id. It is created if RPM has not seen this record before. Needs a
    /// token.</summary>
    /// <remarks>Every id passed here is also stored as an alias of the album. So a copy linked to one
    /// catalogue and a copy linked to another end up on the same page. Sending the ids that are known
    /// costs nothing, and it is what joins the two.</remarks>
    public async Task<RpmAlbum> ResolveAlbumAsync(
        string? artist, string title, int year = 0, string? catno = null, string? discogsReleaseId = null,
        int trackCount = 0, string? musicBrainzReleaseId = null, string? musicBrainzReleaseGroupId = null,
        string? barcode = null, string? discId = null, CancellationToken ct = default)
        => await SendAsync<RpmAlbum>(HttpMethod.Post, "albums/resolve",
            JsonContent.Create(
                new AlbumResolveRequest(
                    artist, title, year, catno, discogsReleaseId, trackCount,
                    musicBrainzReleaseId, musicBrainzReleaseGroupId, barcode, discId),
                RpmCommunityJson.Default.AlbumResolveRequest), requireToken: true, ct) ?? throw new InvalidOperationException("Empty response from albums/resolve.");

    /// <summary>
    /// Links this album to a pressing, by whatever id names it, and returns the entry that pressing
    /// belongs to. Needs a token.
    /// </summary>
    /// <remarks>The answer may be an album that the user has never seen. Linking to a pressing that
    /// somebody else already linked joins their page. That is the purpose of linking.</remarks>
    public async Task<RpmAlbum> LinkAlbumAsync(
        string? artist, string title, int year = 0, string? catno = null, string? discogsReleaseId = null,
        string? musicBrainzReleaseId = null, string? musicBrainzReleaseGroupId = null,
        string? barcode = null, string? discId = null, CancellationToken ct = default)
        => await SendAsync<RpmAlbum>(HttpMethod.Post, "albums/link",
            JsonContent.Create(
                new AlbumLinkRequest(
                    artist, title, year, catno, discogsReleaseId,
                    musicBrainzReleaseId, musicBrainzReleaseGroupId, barcode, discId),
                RpmCommunityJson.Default.AlbumLinkRequest), requireToken: true, ct) ?? throw new InvalidOperationException("Empty response from albums/link.");

    /// <summary>Every catalogue id that this album answers to. It needs no token, so the album page can
    /// show them when signed out.</summary>
    public async Task<IReadOnlyList<RpmAlbumAlias>> GetAlbumAliasesAsync(
        string albumId, CancellationToken ct = default)
        => await SendAsync<List<RpmAlbumAlias>>(
            HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}/aliases", null, false, ct) ?? [];

    /// <summary>The same match as <see cref="ResolveAlbumAsync"/>, but without creating an entry. Null when
    /// nobody has written about this record. That is the usual answer, and it is not an error.</summary>
    public async Task<RpmAlbum?> LookupAlbumAsync(
        string? artist, string title, int year = 0, string? catno = null, CancellationToken ct = default)
    {
        var url = $"albums/lookup?title={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(artist)) url += $"&artist={Uri.EscapeDataString(artist)}";
        if (year > 0) url += $"&year={year}";
        if (!string.IsNullOrWhiteSpace(catno)) url += $"&catno={Uri.EscapeDataString(catno)}";

        return await SendAsync<RpmAlbum>(HttpMethod.Get, url, null, requireToken: false, ct);
    }

    public async Task<RpmAlbum?> GetAlbumAsync(string albumId, CancellationToken ct = default)
        => await SendAsync<RpmAlbum>(HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}", null, false, ct);

    // ── what RPM's listeners play ──

    /// <summary>What everybody has played on this record, most played first. It needs no token, like the
    /// reviews. It is a total for the record, not something about a person.</summary>
    public async Task<IReadOnlyList<RpmTrackPlays>> GetAlbumPlaysAsync(
        string albumId, CancellationToken ct = default)
        => await SendAsync<List<RpmTrackPlays>>(
            HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}/plays", null, false, ct) ?? [];

    /// <summary>Adds what this listener has played since the last report: a count per song, and nothing
    /// else.
    /// </summary>
    /// <remarks>A token is needed, because an open counter could be filled in by anybody. The token admits
    /// the request, but it is not written down with the counts. The rows that the server keeps carry no
    /// user.</remarks>
    public async Task ReportAlbumPlaysAsync(
        string albumId, IReadOnlyList<RpmTrackPlays> plays, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plays);
        if (plays.Count == 0)
            return;

        await SendAsync<object>(
            HttpMethod.Post, $"albums/{Uri.EscapeDataString(albumId)}/plays",
            JsonContent.Create(plays, RpmCommunityJson.Default.IReadOnlyListRpmTrackPlays),
            requireToken: true, ct);
    }

    public async Task<IReadOnlyList<RpmAlbum>> SearchAlbumsAsync(
        string query, int limit = 25, CancellationToken ct = default)
        => await SendAsync<List<RpmAlbum>>(
            HttpMethod.Get, $"albums/search?q={Uri.EscapeDataString(query)}&limit={limit}", null, false, ct)
           ?? [];

    // ── reviews ──

    /// <param name="group">Read what people wrote about every pressing of this album, not only about this
    /// one pressing. The album page asks for it. A review belongs to the record that its writer owns, so
    /// reading them one record at a time would split a famous album across thirty nearly empty pages. An
    /// album that RPM has no group for gives the same answer either way.</param>
    public async Task<RpmReviewPage> GetReviewsAsync(
        string albumId, RpmReviewSort sort = RpmReviewSort.Votes, int page = 1, int pageSize = 10,
        bool group = true, CancellationToken ct = default)
    {
        var url = $"albums/{Uri.EscapeDataString(albumId)}/reviews"
                + $"?sort={sort.ToString().ToLowerInvariant()}&page={page}&pageSize={pageSize}"
                + (group ? "&group=true" : "");

        // Signed in, the same call comes back with myVote filled in. So the token is sent when there is
        // one, and the request is made anyway when there is not.
        return await SendAsync<RpmReviewPage>(HttpMethod.Get, url, null, requireToken: false, ct)
               ?? new RpmReviewPage([], page, pageSize, 0, sort.ToString().ToLowerInvariant());
    }

    /// <summary>The caller's own review, or null if they have not written one.</summary>
    public async Task<RpmReview?> GetMyReviewAsync(string albumId, CancellationToken ct = default)
        => await SendAsync<RpmReview>(
            HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}/reviews/me", null, true, ct);

    /// <param name="rating">Halves of a star, doubled: 1 to 10.</param>
    public async Task<RpmReview> WriteReviewAsync(
        string albumId, int rating, string? body, CancellationToken ct = default)
        => await SendAsync<RpmReview>(
            HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/reviews/me",
            JsonContent.Create(new ReviewRequest(rating, body), RpmCommunityJson.Default.ReviewRequest),
            requireToken: true, ct)
           ?? throw new InvalidOperationException("Empty response from reviews/me.");

    public async Task DeleteMyReviewAsync(string albumId, CancellationToken ct = default)
        => await SendAsync<object>(
            HttpMethod.Delete, $"albums/{Uri.EscapeDataString(albumId)}/reviews/me", null, true, ct);

    /// <param name="value">1 to vote it up, -1 down, 0 to withdraw.</param>
    public async Task<RpmReview?> VoteAsync(
        string albumId, string authorId, int value, CancellationToken ct = default)
        => await SendAsync<RpmReview>(
            HttpMethod.Post,
            $"albums/{Uri.EscapeDataString(albumId)}/reviews/{Uri.EscapeDataString(authorId)}/vote",
            JsonContent.Create(new VoteRequest(value), RpmCommunityJson.Default.VoteRequest),
            requireToken: true, ct);

    // ── comments ──

    public async Task<IReadOnlyList<RpmComment>> GetCommentsAsync(
        string albumId, int limit = 50, CancellationToken ct = default)
        => await SendAsync<List<RpmComment>>(
            HttpMethod.Get, $"albums/{Uri.EscapeDataString(albumId)}/comments?limit={limit}", null, false, ct)
           ?? [];

    public async Task<RpmComment> AddCommentAsync(string albumId, string body, CancellationToken ct = default)
        => await SendAsync<RpmComment>(
            HttpMethod.Post, $"albums/{Uri.EscapeDataString(albumId)}/comments",
            JsonContent.Create(new BodyRequest(body), RpmCommunityJson.Default.BodyRequest), true, ct)
           ?? throw new InvalidOperationException("Empty response from comments.");

    public async Task DeleteCommentAsync(string albumId, string commentId, CancellationToken ct = default)
        => await SendAsync<object>(
            HttpMethod.Delete,
            $"albums/{Uri.EscapeDataString(albumId)}/comments/{Uri.EscapeDataString(commentId)}",
            null, true, ct);

    // ── artists and their walls ──

    /// <summary>The artist that RPM already holds under this name, without creating one. Null is the
    /// usual answer and is not an error: an artist that nobody has written about has a wall with nothing
    /// on it.
    /// </summary>
    /// <param name="mbid">Their MusicBrainz id, where the page has it. It matches exactly, so an artist
    /// whose name is spelled unlike anything in RPM is still found by it.</param>
    public async Task<RpmArtist?> LookupArtistAsync(
        string? name, string? mbid = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(mbid))
            return null;

        var url = "artists/lookup?";
        if (!string.IsNullOrWhiteSpace(name)) url += $"name={Uri.EscapeDataString(name)}&";
        if (!string.IsNullOrWhiteSpace(mbid)) url += $"mbid={Uri.EscapeDataString(mbid)}";

        return await SendAsync<RpmArtist>(HttpMethod.Get, url.TrimEnd('&', '?'), null, false, ct);
    }

    /// <summary>The artist's entry, created if RPM has never been asked about them. Needs a token, because
    /// creating an entry is a write. The page calls this before posting, and not before reading.</summary>
    public async Task<RpmArtist> ResolveArtistAsync(
        string name, string? mbid = null, CancellationToken ct = default)
        => await SendAsync<RpmArtist>(HttpMethod.Post, "artists/resolve",
            JsonContent.Create(
                new ArtistResolveRequest(name, mbid), RpmCommunityJson.Default.ArtistResolveRequest),
            requireToken: true, ct) ?? throw new InvalidOperationException("Empty response from artists/resolve.");

    /// <summary>What is on this artist's wall, newest first. It needs no token, like the reviews.</summary>
    public async Task<IReadOnlyList<RpmWallEntry>> GetWallAsync(
        string artistId, int limit = 50, CancellationToken ct = default)
        => await SendAsync<List<RpmWallEntry>>(
            HttpMethod.Get, $"artists/{Uri.EscapeDataString(artistId)}/wall?limit={limit}", null, false, ct)
           ?? [];

    public async Task<RpmWallEntry> PostToWallAsync(
        string artistId, string body, CancellationToken ct = default)
        => await SendAsync<RpmWallEntry>(
            HttpMethod.Post, $"artists/{Uri.EscapeDataString(artistId)}/wall",
            JsonContent.Create(new BodyRequest(body), RpmCommunityJson.Default.BodyRequest), true, ct)
           ?? throw new InvalidOperationException("Empty response from the wall.");

    /// <summary>Removes your own post. A review's line cannot be removed. It goes when the review that it
    /// stands for goes.</summary>
    public async Task DeleteWallEntryAsync(string artistId, string entryId, CancellationToken ct = default)
        => await SendAsync<object>(
            HttpMethod.Delete,
            $"artists/{Uri.EscapeDataString(artistId)}/wall/{Uri.EscapeDataString(entryId)}",
            null, true, ct);

    // ── profile ──

    public async Task<RpmProfile> GetProfileAsync(CancellationToken ct = default)
        => await SendAsync<RpmProfile>(HttpMethod.Get, "profile", null, true, ct)
           ?? throw new InvalidOperationException("Empty response from profile.");

    /// <summary>Somebody else's name and face, by their id. It needs no token, like the reviews that it
    /// is drawn beside.
    ///
    /// <para>Null is an answer, not a failure. An id that never made a profile belongs to a person with
    /// nothing to show. The caller must have some other name to use for them.</para>
    /// </summary>
    public async Task<RpmProfile?> GetPublicProfileAsync(string userId, CancellationToken ct = default)
        => string.IsNullOrEmpty(userId)
            ? null
            : await SendAsync<RpmProfile>(
                HttpMethod.Get, $"profiles/{Uri.EscapeDataString(userId)}", null, false, ct);

    /// <exception cref="RpmNameTakenException">Another person already uses that name.</exception>
    public async Task<RpmProfile> SetDisplayNameAsync(string displayName, CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Put, "profile")
        {
            Content = JsonContent.Create(
                new DisplayNameRequest(displayName), RpmCommunityJson.Default.DisplayNameRequest),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new RpmNameTakenException(displayName);

        return await ReadAsync<RpmProfile>(resp, "profile", ct)
               ?? throw new InvalidOperationException("Empty response from profile.");
    }

    /// <summary>Replaces the caller's avatar. The bytes are sent as they are. The client resizes the
    /// picture for a review row before calling, because the server stores what it is given and never
    /// re-encodes it.
    /// </summary>
    public async Task<RpmProfile> SetAvatarAsync(
        byte[] image, string contentType = "image/jpeg", CancellationToken ct = default)
    {
        var token = await RequireTokenAsync(ct);

        using var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var req = new HttpRequestMessage(HttpMethod.Post, "profile/avatar") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        return await ReadAsync<RpmProfile>(resp, "profile/avatar", ct)
               ?? throw new InvalidOperationException("Empty response from profile/avatar.");
    }

    public async Task<RpmProfile> ClearAvatarAsync(CancellationToken ct = default)
        => await SendAsync<RpmProfile>(HttpMethod.Delete, "profile/avatar", null, true, ct)
           ?? throw new InvalidOperationException("Empty response from profile/avatar.");

    /// <summary>Where somebody's avatar is drawn from, relative to the API base. The stamp is on the
    /// URL, not in a header. A changed picture then has a different address, so nothing has to invalidate
    /// a cache that is already filled.
    /// </summary>
    public static string AvatarPath(string userId, long avatarUpdatedAt = 0)
    {
        var path = $"profiles/{Uri.EscapeDataString(userId)}/avatar";
        return avatarUpdatedAt > 0 ? $"{path}?v={avatarUpdatedAt}" : path;
    }

    // ── reporting ──

    /// <param name="targetType">"album", "review" or "comment".</param>
    /// <param name="targetId">The album id, the review author's id, or the comment id.</param>
    public async Task ReportAsync(
        string targetType, string albumId, string targetId, string? reason = null, string? note = null,
        CancellationToken ct = default)
        => await SendAsync<object>(HttpMethod.Post, "reports",
            JsonContent.Create(
                new ReportRequest(targetType, albumId, targetId, reason, note),
                RpmCommunityJson.Default.ReportRequest), requireToken: true, ct);

    /// <summary>Reports a line on an artist's wall. This is separate from <see cref="ReportAsync"/>,
    /// because it names the artist rather than an album. A report must name one of them, or nobody can
    /// open it.</summary>
    public async Task ReportWallPostAsync(
        string artistId, string entryId, string? reason = null, string? note = null,
        CancellationToken ct = default)
        => await SendAsync<object>(HttpMethod.Post, "reports",
            JsonContent.Create(
                new WallReportRequest("wallpost", artistId, entryId, reason, note),
                RpmCommunityJson.Default.WallReportRequest), requireToken: true, ct);

    // ── plumbing ──

    private async Task<string> RequireTokenAsync(CancellationToken ct)
        => await accessTokenProvider(ct) ?? throw new InvalidOperationException("Not signed in.");

    /// <summary>One call to the community API. The caller builds the body, for the reason given on
    /// <see cref="RpmCommunityJson"/>.</summary>
    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, HttpContent? payload, bool requireToken, CancellationToken ct)
    {
        var token = requireToken
            ? await RequireTokenAsync(ct)
            // A read still sends the token when there is one. It is what fills in how this user voted.
            // But a read never demands one.
            : await SafeTokenAsync(ct);

        using var req = new HttpRequestMessage(method, path);
        if (payload is not null) req.Content = payload;
        if (token is { Length: > 0 }) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        return await ReadAsync<T>(resp, path, ct);
    }

    /// <summary>The token, if one can be had. If a refresh fails on a read path, the reviews are drawn as
    /// signed out. The page does not fail to draw.</summary>
    private async Task<string?> SafeTokenAsync(CancellationToken ct)
    {
        try
        {
            return await accessTokenProvider(ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        // Nothing there is an answer, not a failure: no review written, no album entry, no avatar.
        if (resp.StatusCode == HttpStatusCode.NotFound) return default;
        if (resp.StatusCode == HttpStatusCode.NoContent) return default;

        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                            or HttpStatusCode.RequestEntityTooLarge)
            throw new RpmCommunityRejectedException(await resp.Content.ReadAsStringAsync(ct));

        resp.EnsureSuccessStatusCode();

        if (typeof(T) == typeof(object)) return default;
        if (resp.Content.Headers.ContentLength == 0) return default;

        return await resp.Content.ReadFromJsonAsync(Answer<T>(), ct);
    }
    // ── request shapes ────────────────────────────────────────────────
    //
    // These are named types, not anonymous objects. A published build is Native AOT, and it cannot
    // describe an anonymous type. The field names on the wire come from the camelCase policy on the
    // context below.

    private sealed record AlbumResolveRequest(
        string? Artist,
        string Title,
        int Year,
        string? Catno,
        string? DiscogsReleaseId,
        int TrackCount,
        string? MusicBrainzReleaseId,
        string? MusicBrainzReleaseGroupId,
        string? Barcode,
        string? DiscId);

    private sealed record AlbumLinkRequest(
        string? Artist,
        string Title,
        int Year,
        string? Catno,
        string? DiscogsReleaseId,
        string? MusicBrainzReleaseId,
        string? MusicBrainzReleaseGroupId,
        string? Barcode,
        string? DiscId);

    private sealed record ReviewRequest(int Rating, string? Body);

    private sealed record VoteRequest(int Value);

    /// <summary>A comment, and a wall post: both are one piece of text.</summary>
    private sealed record BodyRequest(string? Body);

    private sealed record ArtistResolveRequest(string Name, string? MusicBrainzArtistId);

    private sealed record DisplayNameRequest(string DisplayName);

    private sealed record ReportRequest(
        string TargetType, string AlbumId, string TargetId, string? Reason, string? Note);

    private sealed record WallReportRequest(
        string TargetType, string ArtistId, string TargetId, string? Reason, string? Note);

    /// <summary>The shape of the answer. It is looked up here, rather than passed at each call.
    /// <see cref="RpmCloudClient"/> has the same method, and it explains what this costs.</summary>
    private static JsonTypeInfo<T> Answer<T>() =>
        RpmCommunityJson.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"{typeof(T).Name} is not listed in {nameof(RpmCommunityJson)}, so it cannot be read.");

    /// <summary>Every shape this client sends or reads, as the source generator describes them.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AlbumResolveRequest))]
    [JsonSerializable(typeof(AlbumLinkRequest))]
    [JsonSerializable(typeof(ReviewRequest))]
    [JsonSerializable(typeof(VoteRequest))]
    [JsonSerializable(typeof(BodyRequest))]
    [JsonSerializable(typeof(ArtistResolveRequest))]
    [JsonSerializable(typeof(DisplayNameRequest))]
    [JsonSerializable(typeof(ReportRequest))]
    [JsonSerializable(typeof(WallReportRequest))]
    [JsonSerializable(typeof(IReadOnlyList<RpmTrackPlays>))]
    [JsonSerializable(typeof(RpmAlbum))]
    [JsonSerializable(typeof(RpmArtist))]
    [JsonSerializable(typeof(RpmComment))]
    [JsonSerializable(typeof(RpmProfile))]
    [JsonSerializable(typeof(RpmReview))]
    [JsonSerializable(typeof(RpmReviewPage))]
    [JsonSerializable(typeof(RpmWallEntry))]
    [JsonSerializable(typeof(List<RpmAlbum>))]
    [JsonSerializable(typeof(List<RpmAlbumAlias>))]
    [JsonSerializable(typeof(List<RpmComment>))]
    [JsonSerializable(typeof(List<RpmTrackPlays>))]
    [JsonSerializable(typeof(List<RpmWallEntry>))]
    private sealed partial class RpmCommunityJson : JsonSerializerContext;

}

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RPM.Core.Net;
using Microsoft.Extensions.Options;

namespace RPM.Desktop.Services.Art;

/// <summary>Cover + artist backdrops for one artist/album, as the art display needs them.</summary>
public sealed record Artwork(
    [property: JsonPropertyName("cover")] string? Cover,
    [property: JsonPropertyName("backgrounds")] IReadOnlyList<string> Backgrounds,
    /// <summary>The album's disc art: a transparent PNG of the record itself. The art display puts it on the
    /// turntable when the album has one. Most albums do not, and the display then falls back to the cover.</summary>
    [property: JsonPropertyName("disc")] string? Disc,
    /// <summary>The artist's logo: their name set the way they set it, on a transparent background. The artist page
    /// puts it where their name would be. It is null for most artists, and the name is written out instead.</summary>
    [property: JsonPropertyName("logo")] string? Logo,
    [property: JsonPropertyName("artistMbid")] string? ArtistMbid)
{
    /// <summary>The artist's square thumbs, best-liked first. These are fanart.tv's <c>artistthumb</c> images, 1000
    /// by 1000. The list is empty where fanart.tv has none, which is most artists.
    ///
    /// <para>This is an init property with an empty default, not a constructor parameter. So an answer from a server
    /// that does not send <c>thumbs</c> reads as an empty list, not as null.</para>
    ///
    /// <para>It is kept apart from <see cref="Backgrounds"/> on purpose. That list is ranked by
    /// <c>BackdropRanker</c>, which scores for a 16:9 composition. The list is also saved as the artist's carousel
    /// order. A square thumb in that list would be scored as a bad backdrop, and then saved as one.</para></summary>
    [JsonPropertyName("thumbs")]
    public IReadOnlyList<string> Thumbs { get; init; } = [];

    public static readonly Artwork None = new(null, [], null, null, null);

    public bool IsEmpty => Cover is null && Backgrounds.Count == 0;
}

/// <summary>
/// Artwork for the library's art display, from the rpm API. The API owns the keys, asks TheAudioDB and fanart.tv,
/// and keeps the shared cache. Two caches sit in front of it. The first is a dictionary, so moving through the grid
/// inside one album costs nothing. The second is the app's own web cache, under it (<see cref="AppCache"/>). That
/// makes the art available at once on the next run, and even with no network at all.
///
/// <see cref="GetAsync"/> returns an <see cref="Asked{T}"/>. An artist the service knows nothing about is answered
/// with <see cref="Artwork.None"/>. A lookup that failed (offline, an HTTP error, or a body that would not read) is
/// reported as not answered. The two must stay different. If a failed lookup were read as "this artist has no
/// photographs", the header would save that over the artist's photo order in the library.
/// </summary>
public sealed partial class ArtworkService
{
    private readonly AuthService _auth;
    private readonly HttpClient? _http;
    /// <summary>What was answered this run, and until when it stands. A whole answer stands for the run. A partial
    /// answer and a failure stand for a short while. Then the art arrives on a later look, without every arrow key
    /// asking the server again. A failure is held AS a failure. A caller asking inside the hold is told the lookup did
    /// not answer, not that there is no art.</summary>
    private readonly Dictionary<string, (Asked<Artwork> Art, DateTime Until)> _memory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a partial answer or a failure is held before the server is asked again. The server finishes
    /// a partial answer in the background, usually within seconds.</summary>
    private static readonly TimeSpan HoldBriefly = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where the artwork answers are filed in the app's web cache: the art category, under a name of their own.
    ///
    /// <para>Answers stored before the server began asking fanart.tv have no backdrops, squares or logos. The art
    /// cache serves an answer for 30 days without asking again. So without a new name, an updated app would keep
    /// showing those old answers for a month. With this name, every artist is asked once more, and the new answer is
    /// cached as before. Change the value again when the answer gains something that a stored answer cannot show.</para>
    /// </summary>
    internal static readonly WebCachePolicy Answers = WebCachePolicy.For("art") with { Vary = "fanart-in-api" };

    public ArtworkService(AuthService auth, IOptions<RPM.Desktop.Models.AppConfig> config)
    {
        _auth = auth;

        if (RPM.Core.Net.RpmApi.At(config.Value.IdentificationApiUrl) is { } api)
            _http = AppCache.Client(Answers, api, TimeSpan.FromSeconds(15));
    }

    public async Task<Asked<Artwork>> GetAsync(string? artist, string? album, string? artistMbid = null,
                                               bool forceRefresh = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer(Artwork.None);

        var key = CacheKey(artist, album);

        // The "look again" button bypasses both caches and asks the server to bypass its own. So a photo added
        // upstream since the art was cached shows up. The fresh result still replaces both caches, so browsing on
        // from here is fast again.
        if (!forceRefresh)
        {
            lock (_memory)
            {
                if (_memory.TryGetValue(key, out var hit) && hit.Until > DateTime.UtcNow)
                    return hit.Art;
            }
        }

        var (fetched, partial) = await FetchAsync(artist, album, artistMbid, forceRefresh, ct);

        // A lookup that failed is not an answer. If it were remembered as one, it would blank the art for as long as
        // the row lives, long after the fault was over. So it is held briefly. A server that is down is not asked
        // again on every arrow key, and it is asked again after the hold. The server answers "not yet" with a 503
        // while its own lookup is still running. That is the second reason the hold is short. The web cache follows
        // the same rule. It stores an answer, including "this artist has none". It never stores a response from a
        // server that was unwell.
        if (fetched is null)
        {
            var failed = Asked.Failed<Artwork>($"artwork lookup for \"{artist}\" did not answer");
            lock (_memory) _memory[key] = (failed, DateTime.UtcNow + HoldBriefly);
            return failed;
        }

        // Part of an answer is shown and held briefly. The web cache does not store it either (see HttpCacheHandler).
        // The next look after the hold gets the rest.
        var answer = Asked.Answer(fetched);
        lock (_memory) _memory[key] = (answer, partial ? DateTime.UtcNow + HoldBriefly : DateTime.MaxValue);
        return answer;
    }

    private async Task<(Artwork? Art, bool Partial)> FetchAsync(
        string artist, string? album, string? artistMbid, bool forceRefresh, CancellationToken ct)
    {
        if (_http is null)
            return (Nothing(artist, "no api url configured"), false);

        try
        {
            var query = $"artwork?artist={Uri.EscapeDataString(artist)}";
            if (!string.IsNullOrWhiteSpace(album))
                query += $"&album={Uri.EscapeDataString(album)}";
            if (!string.IsNullOrWhiteSpace(artistMbid))
                query += $"&artistMbid={Uri.EscapeDataString(artistMbid)}";
            if (forceRefresh)
                query += "&refresh=true";

            using var request = new HttpRequestMessage(HttpMethod.Get, query);

            // The app key on the client admits the request, so art is drawn when signed out too. The bearer is sent
            // when there is a session, but it is not required.
            if (await _auth.GetAccessTokenAsync() is { Length: > 0 } token)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (forceRefresh)
                request.Refresh();   // Past the app's own cache as well as the server's.

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return (Nothing(
                    artist,
                    RPM.Core.Net.OnlineGateHandler.WasRefused(response)
                        ? "the gate refused it, the app believes it is offline"
                        : $"HTTP {(int)response.StatusCode}"), false);

            return (await response.Content.ReadFromJsonAsync(ArtworkServiceJson.Default.Artwork, ct),
                    response.Headers.Contains(RpmMetadataApi.PartialHeader));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Nothing(artist, $"{ex.GetType().Name}: {ex.Message}"), false);   // Optional. Never show this to the user.
        }
    }

    /// <summary>
    /// No artwork. The reason is written to the diagnostics channel.
    /// </summary>
    /// <remarks>
    /// This is the same silence that <c>RpmMetadataApi.Nothing</c> exists for, on the call that carries the portrait,
    /// the backdrops and the artist's logo. No URL, a gate that refused, a 401, a 500, a timeout, and "this artist has
    /// no art" all came back as one null. A page then shows the artist's name in writing. That is also what it
    /// correctly shows for an act nobody has drawn a logo for. So the two look the same on screen.
    ///
    /// <para>The reason is sent to the diagnostics channel, not to <c>Debug.WriteLine</c>. This head is built in
    /// Release, and <c>Debug.WriteLine</c> compiles to nothing there. Read it with
    /// <c>rpmscope log --channel net</c>.</para>
    /// </remarks>
    private static Artwork? Nothing(string artist, string why)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said("net", $"artwork \"{artist}\": {why}");

        return null;
    }

    /// <summary>
    /// Downloads the album's front cover by name, or returns null.
    ///
    /// <para>The catalogue's own art archive is keyed by release id. A great many pressings, most CDs a person owns,
    /// have no scan filed under their release id. This asks the same artwork service that the library's display uses.
    /// That service answers on artist and album. So a rip whose release the archive has never seen still gets a cover.
    /// Best effort, like everything about art.</para>
    /// </summary>
    public async Task<RPM.Core.Art.AlbumArt?> CoverBytesAsync(
        string? artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return null;

        // A lookup that did not answer, and an album with no cover, both return null here. An import then writes
        // the album without a sleeve.
        if ((await GetAsync(artist, album, ct: ct)).Match(found => found, _ => Artwork.None).Cover
                is not { Length: > 0 } url)
            return null;

        try
        {
            using var response = await ImageHttp.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is { Length: > 0 } && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;   // An error page is not a sleeve, and it would be saved as one.

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length > 0
                ? new RPM.Core.Art.AlbumArt(bytes, mime is { Length: > 0 } m ? m : "image/jpeg")
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the album's disc (CD) art for import, or returns null. The rpm API, which owns the TheAudioDB key,
    /// returns the URL. The image bytes are then fetched, so they can be written as the folder's disc.png. Best
    /// effort. No disc art is ordinary, and it is never an error.
    ///
    /// <para>A cdart is meant to be a transparent PNG of the record, and mostly is. Where it is not, the picture is a
    /// photograph, since a JPEG cannot carry transparency. The disc is then cut out of it (see
    /// <see cref="DiscStamp"/>). So every caller gets the same kind of thing back: a round record with a hole in the
    /// middle, not a square with a picture of one on it.</para></summary>
    public async Task<byte[]?> DiscArtBytesAsync(string? artist, string? album, CancellationToken ct = default)
    {
        if (_http is null || string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return null;
        try
        {
            var query = $"artwork/disc?artist={Uri.EscapeDataString(artist)}&album={Uri.EscapeDataString(album)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, query);
            if (await _auth.GetAccessTokenAsync() is { Length: > 0 } token)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var disc = await response.Content.ReadFromJsonAsync(ArtworkServiceJson.Default.DiscResult, ct);
            if (disc?.Disc is not { Length: > 0 } url)
                return null;

            var bytes = await ImageHttp.GetByteArrayAsync(url, ct);
            if (bytes is not { Length: > 0 } || DiscStamp.HasAlpha(bytes))
                return bytes;

            // Flat. Cut the disc out of it. If there is no disc to find, keep the flat picture. The record's own
            // border already clips it round, so a picture is better than none.
            return await Task.Run(() => DiscStamp.Stamp(bytes, vinyl: false)) ?? bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The pictures themselves, as opposed to the URLs of them. These are cached like everything else. A
    /// cover is a few hundred kilobytes that never changes. The same cover is asked for by the wall, the album page
    /// and the importer.</summary>
    private static readonly HttpClient ImageHttp = AppCache.Client("art", RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(15));

    private sealed record DiscResult([property: JsonPropertyName("disc")] string? Disc);

    // ── caching ──

    /// <summary>An artist and album pair. The answer does not depend on the user's fanart.tv key. The server asks
    /// fanart.tv for everyone, and keeps one answer per artist.</summary>
    private static string CacheKey(string artist, string? album)
        => $"{artist.Trim().ToLowerInvariant()}|{(album ?? "").Trim().ToLowerInvariant()}";
    /// <summary>The JSON serialiser for the artwork API answers.
    /// A published build is Native AOT, which serialises from this and never by reflection.</summary>
    [JsonSerializable(typeof(Artwork))]
    [JsonSerializable(typeof(DiscResult))]
    private sealed partial class ArtworkServiceJson : JsonSerializerContext;

}

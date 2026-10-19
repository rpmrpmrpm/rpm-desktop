using System.Text.Json.Serialization.Metadata;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RPM.Core.Art;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// The app's view of the cover search. Every source, including fanart.tv, is asked through the rpm API. The
/// API holds the keys and caches one answer per search for everyone.
/// </summary>
public sealed partial class RpmCoverSearchApi : ICoverSearchApi
{
    private readonly AuthService _auth;
    private readonly HttpClient? _http;

    /// <summary>Downloads use their own client: no bearer token, a browser-style timeout, and a redirect
    /// policy that follows the two redirects of the Cover Art Archive to an Internet Archive storage node.
    /// Built by Wire, so downloads queue at the online gate and count against the connection budget like
    /// the rest.</summary>
    private static readonly HttpClient Downloads =
        RPM.Core.Net.Wire.Outside(RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(30));

    public RpmCoverSearchApi(AuthService auth, IOptions<RPM.Desktop.Models.AppConfig> config)
        : this(auth, RpmApi.At(config.Value.IdentificationApiUrl) is { } api
                         ? Wire.Outside(api, TimeSpan.FromSeconds(30))
                         : null)
    {
    }

    /// <summary>For the tests: the client stated rather than built.</summary>
    internal RpmCoverSearchApi(AuthService auth, HttpClient? http)
    {
        _auth = auth;
        _http = http;
    }

    /// <summary>What can be searched, in the order results are weighted. This is the server's list, which
    /// names only the sources it has a key for.</summary>
    public async Task<Asked<IReadOnlyList<string>>> SourcesAsync(
        CoverKind kind = CoverKind.Cover, CancellationToken ct = default)
    {
        var url = kind == CoverKind.Disc ? "artwork/sources?kind=disc" : "artwork/sources";
        return (await AskAsync(url, CatalogWireJson.Default.ListString, ct))
            .Match(list => list is null
                       ? Asked.Failed<IReadOnlyList<string>>("the answer had no body")
                       : Asked.Answer<IReadOnlyList<string>>(list),
                   Asked.Failed<IReadOnlyList<string>>);
    }

    public async Task<Asked<CoverSearchResponse>> SearchAsync(CoverSearchQuery query, CancellationToken ct = default)
    {
        if (query.IsEmpty)
            return Asked.Answer(new CoverSearchResponse([], [], false, []));

        var url = $"artwork/search?limit={Math.Clamp(query.Limit, 1, 25)}";
        if (!string.IsNullOrWhiteSpace(query.Artist)) url += $"&artist={Uri.EscapeDataString(query.Artist)}";
        if (!string.IsNullOrWhiteSpace(query.Album)) url += $"&album={Uri.EscapeDataString(query.Album)}";
        if (query.Sources is { Count: > 0 })
            url += $"&sources={Uri.EscapeDataString(string.Join(",", query.Sources))}";
        if (query.Kind == CoverKind.Disc)
            url += "&kind=disc";

        // Whole() is used, not a null check on the answer. A property the server did not send arrives as
        // null, whatever its declared type, and both lists are enumerated by the window without any check.
        // The wire data is made usable here, at the seam, because this is the only place that knows it came
        // off the wire.
        return (await AskAsync(url, CatalogWireJson.Default.CoverSearchResponse, ct))
            .Match(wire => wire is null
                       ? Asked.Failed<CoverSearchResponse>("the answer had no body")
                       : Asked.Answer(Whole(wire)),
                   Asked.Failed<CoverSearchResponse>);
    }

    /// <summary>
    /// The picture, fetched straight from where it is hosted.
    ///
    /// <para>Not through the API, on purpose. These are public CDN addresses. Proxying a megabyte of JPEG
    /// through a Lambda to return it unchanged adds a round trip and a cold start, and gains nothing. The
    /// cost of a direct fetch is that a web result, on a server that refuses strangers, comes back as null.
    /// See the interface: null is a documented outcome there, not a failure.</para>
    /// </summary>
    public async Task<byte[]?> DownloadAsync(CoverCandidate candidate, CancellationToken ct = default)
    {
        foreach (var url in new[] { candidate.FullUrl, candidate.ThumbUrl }.Distinct())
        {
            if (string.IsNullOrWhiteSpace(url))
                continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("rpm/1.0 ( https://rpmrpmrpm.com )");

                using var response = await Downloads.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    continue;

                // Anything that is not a picture is a login page, a hotlink block, or an error page written
                // as HTML. Saving it would write that into the album folder as "cover.jpg".
                if (response.Content.Headers.ContentType?.MediaType is not { } mime
                    || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0)
                    return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the thumbnail, then give up. The person choosing a cover cannot do anything about
                // which of the two failed.
            }
        }
        return null;
    }

    /// <summary>
    /// Measures the picture without fetching all of it.
    ///
    /// <para>This is a ranged request for the first 64 KB. That is enough to get past the comment blocks,
    /// colour profiles and embedded thumbnails that a JPEG encoder puts before its frame header, and more
    /// than enough for other formats. Sixty of these across a grid take a couple of megabytes. Sixty full
    /// downloads would be far more.</para>
    ///
    /// <para>A host that ignores the range and sends the whole file is common, and that is fine. The read
    /// stops at the probe length, and the connection is dropped. Treating that as a failure would be wrong,
    /// so the range is a request, not a requirement.</para>
    /// </summary>
    public async Task<CoverMeasurement?> MeasureAsync(
        CoverCandidate candidate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidate.FullUrl))
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.FullUrl);
            request.Headers.UserAgent.ParseAdd("rpm/1.0 ( https://rpmrpmrpm.com )");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, ImageHeader.ProbeBytes - 1);

            // The headers decide whether this is worth reading, so the body is not waited for before then.
            using var response = await Downloads.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            if (response.Content.Headers.ContentType?.MediaType is not { } mime
                || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            // The length of the whole file. Content-Range gives it for a served range. Content-Length only
            // matches when the host ignored the range and sent everything.
            var total = response.Content.Headers.ContentRange?.Length
                     ?? response.Content.Headers.ContentLength
                     ?? 0;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[ImageHeader.ProbeBytes];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                if (read == 0)
                    break;
                filled += read;
            }

            return ImageHeader.Measure(buffer.AsSpan(0, filled)) is not { } size
                ? null
                : new CoverMeasurement(size.Width, size.Height, total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The answer, with its lists present. A reply that is missing a field comes back with an
    /// empty list there, not as a value that throws the first time anything reads it. A call that did not
    /// get a reply never reaches this point.</summary>
    private static CoverSearchResponse Whole(CoverSearchResponse wire)
        => wire with
        {
            Results = wire.Results ?? [],
            Sources = wire.Sources ?? [],
            ReleaseGroupIds = wire.ReleaseGroupIds ?? [],
        };

    /// <summary>
    /// One request. A failure is kept apart from the answer. The failure cases are: no API configured,
    /// nobody signed in (these routes use the user's own session), a status that is not a success, no
    /// network, and a body that cannot be read. Each of these is a failure, not an empty answer. An empty
    /// answer would make the window say "No covers found" for a search that never reached anybody.
    /// </summary>
    /// <param name="shape">What comes back, named at the call: a published build is Native AOT.</param>
    private async Task<Asked<T?>> AskAsync<T>(string relativeUrl, JsonTypeInfo<T> shape, CancellationToken ct)
    {
        if (_http is null)
            return Asked.Failed<T?>("no api url configured");
        try
        {
            var token = await _auth.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return Asked.Failed<T?>("not signed in");

            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Asked.Failed<T?>($"HTTP {(int)response.StatusCode}");
            return Asked.Answer(await response.Content.ReadFromJsonAsync(shape, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Asked.Failed<T?>($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}

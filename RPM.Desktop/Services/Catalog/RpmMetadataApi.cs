using System.Text.Json.Serialization.Metadata;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RPM.Core.Metadata.Providers;
using RPM.Fingerprint.Catalog;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// HTTP client for the rpm metadata API (<c>/metadata/*</c>). The upstream source is decided by the server,
/// so here it is just "rpm metadata". Calls carry the app key. They also carry the rpm bearer token when
/// someone is signed in, the same as <see cref="ArtworkService"/>. Without a configured API, the search,
/// release, disc and cover calls return empty results, so the provider is skipped rather than failing the
/// search. The three papers calls do not do this. They report that they were not answered (see
/// <see cref="AskAsync{T}"/>).
/// </summary>
public sealed partial class RpmMetadataApi : IRpmMetadataApi
{
    private readonly AuthService _auth;
    private readonly HttpClient? _http;

    public RpmMetadataApi(AuthService auth, IOptions<RPM.Desktop.Models.AppConfig> config)
        : this(auth, RpmApi.At(config.Value.IdentificationApiUrl) is { } api
                         ? Wire.Outside(api, TimeSpan.FromSeconds(20))
                         : null)
    {
    }

    /// <summary>For the tests: the client stated rather than built.</summary>
    internal RpmMetadataApi(AuthService auth, HttpClient? http)
    {
        _auth = auth;
        _http = http;
    }

    /// <summary>An API to call. The catalogue reads need no signed-in user. The app sends its own key, and
    /// the bearer token is added only when there is a session, which is not always the case.</summary>
    public bool IsAvailable => _http is not null;

    public async Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(
        string text, string? artist, int limit, ReleaseMedia media,
        string? catalogNumber, string? country, uint? year, CancellationToken ct = default)
    {
        // An empty album is not sent at all. The search may be for an artist's whole output, or for a bare
        // catalogue number. An empty text= parameter is a filter that says nothing.
        var q = $"metadata/search?limit={limit}";
        if (!string.IsNullOrWhiteSpace(text)) q += $"&text={Uri.EscapeDataString(text)}";
        // The medium is sent as its token. Vinyl is also sent under its older name, which an API that
        // predates the media filter still uses to narrow the one search that always had it.
        if (media.Token() is { } token) q += $"&media={token}";
        if (media == ReleaseMedia.Vinyl) q += "&vinyl=true";
        if (!string.IsNullOrWhiteSpace(artist)) q += $"&artist={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(catalogNumber)) q += $"&catno={Uri.EscapeDataString(catalogNumber)}";
        if (!string.IsNullOrWhiteSpace(country)) q += $"&country={Uri.EscapeDataString(country)}";
        if (year is > 0) q += $"&year={year}";

        // Nothing found is 200 and an empty list; a 404 or an empty body is not an answer.
        return Bodied(await AskAsync(q, CatalogWireJson.Default.ListCatalogRelease, NotFound.IsAFailure, ct))
            .Then<IReadOnlyList<CatalogRelease>>(list => list);
    }

    /// <summary>A 404 is the server saying no catalogue knows the release, which is an answer.</summary>
    public async Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        => (await AskAsync($"metadata/release/{Uri.EscapeDataString(releaseId)}",
                           CatalogWireJson.Default.CatalogReleaseDetail, NotFound.IsNothing, ct))
            .Then(Whole);

    public async Task<Asked<CatalogPapers>> GetPapersAsync(
        string artist, string? album, int tracks, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer(CatalogPapers.Empty);

        var q = $"metadata/papers?artist={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(album)) q += $"&album={Uri.EscapeDataString(album)}";
        if (tracks > 0) q += $"&tracks={tracks}";

        // The server answers "the catalogues know nothing" with 200 and empty papers. It never answers with
        // a 404 or an empty body, so either of those is a failure. A partial answer is marked, so the
        // caller shows it and does not keep it.
        var partial = false;
        var asked = await AskAsync(q, CatalogWireJson.Default.CatalogPapers, NotFound.IsAFailure, ct,
                                   response => partial = response.Headers.Contains(PartialHeader));
        return Bodied(asked).Then(papers => partial ? papers with { Partial = true } : papers);
    }

    /// <summary>The header the API sets on an answer that is only part of one. It is written out here
    /// rather than referenced: the API defines it (CatalogCaching.PartialHeader).</summary>
    public const string PartialHeader = "X-RPM-Partial";

    public async Task<Asked<IReadOnlyList<CatalogArtist>>> GetArtistsNamedAsync(
        string name, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Asked.Answer<IReadOnlyList<CatalogArtist>>([]);

        var asked = Bodied(await AskAsync(
            $"metadata/artists?name={Uri.EscapeDataString(name)}&limit={limit}",
            CatalogWireJson.Default.ListCatalogArtist, NotFound.IsAFailure, ct));
        return asked.Then<IReadOnlyList<CatalogArtist>>(list => list);
    }

    /// <summary>A 404 here is the server saying no catalogue knows the id, which is an answer.</summary>
    public async Task<Asked<CatalogArtist?>> GetArtistAsync(string mbid, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(mbid)
            ? Asked.Answer<CatalogArtist?>(null)
            : await AskAsync($"metadata/artist/{Uri.EscapeDataString(mbid)}",
                             CatalogWireJson.Default.CatalogArtist, NotFound.IsNothing, ct);

    /// <summary>The server answers 503 when it could not ask, and 200 with an empty list when it asked and
    /// found nothing. The caller relies on this difference when it writes results down.</summary>
    public async Task<Asked<CatalogPlays>> GetPlaysAsync(
        string artist, string album, int tracks, string? releaseMbid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            return Asked.Answer(CatalogPlays.Empty);

        var q = $"metadata/plays?artist={Uri.EscapeDataString(artist)}"
              + $"&album={Uri.EscapeDataString(album)}";
        if (tracks > 0) q += $"&tracks={tracks}";
        if (!string.IsNullOrWhiteSpace(releaseMbid)) q += $"&release={Uri.EscapeDataString(releaseMbid)}";

        return Bodied(await AskAsync(q, CatalogWireJson.Default.CatalogPlays, NotFound.IsAFailure, ct))
            .Then(Whole);
    }

    /// <summary>A 404 is the server saying no catalogue knows the disc, which is an answer: the rip window
    /// says the disc is unknown. Anything else that is not a success is a failure, and the window says the
    /// lookup failed.</summary>
    public async Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default)
    {
        var q = $"metadata/discid/{Uri.EscapeDataString(discId)}";
        if (!string.IsNullOrWhiteSpace(toc)) q += $"?toc={Uri.EscapeDataString(toc)}";
        return (await AskAsync(q, CatalogWireJson.Default.CatalogDisc, NotFound.IsNothing, ct)).Then(Whole);
    }

    /// <summary>The server's answer about a release's cover: an address, or null where the archives hold
    /// nothing for it (the server sends that as 200 with no address). Not answering is a failure, not "no
    /// cover".</summary>
    public async Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default)
        => Bodied(await AskAsync(
                $"metadata/release/{Uri.EscapeDataString(releaseId)}/cover?size={maxSize}",
                CatalogWireJson.Default.CatalogCover, NotFound.IsAFailure, ct))
            .Then(cover => cover.Url);

    // ── what came off the wire, made usable ───────────────────────────────
    //
    // Three of these shapes declare a list that cannot be null, and their callers read it without checking.
    // A server that leaves the field out sends null anyway. A missing property deserializes to the type's
    // default, however the contract is declared. So the lists are filled in here, at the seam, because this
    // is the only place that knows the value came off the wire.

    private static CatalogReleaseDetail? Whole(CatalogReleaseDetail? wire)
        => wire is null ? null : wire with { Tracks = wire.Tracks ?? [], Media = wire.Media ?? [] };

    private static CatalogDisc? Whole(CatalogDisc? wire)
        => wire is null ? null : wire with { Tracks = wire.Tracks ?? [] };

    private static CatalogPlays Whole(CatalogPlays wire)
        => wire with { Tracks = wire.Tracks ?? [], Plays = wire.Plays ?? [] };

    /// <summary>What a 404 means for one route. It is stated at every call, not given a default. On one
    /// route it means "nobody by that id". On the others it means the route is missing. Reading the second
    /// as the first turns a failure into "nothing".</summary>
    private enum NotFound
    {
        IsNothing,
        IsAFailure,
    }

    /// <summary>
    /// One request. A failure is kept apart from the answer. Every route goes through here.
    ///
    /// <para>No failure is turned into <c>default</c>. Every caller would read such a value as "nothing":
    /// empty papers written to disk, "no releases" in the lookup window, or "unknown disc" in the rip
    /// window. So every answer from this API tells the caller whether it was answered.</para>
    ///
    /// <para>A cancelled call throws, as every awaited call does. The caller that cancelled is the one that
    /// catches it. A time-out also arrives as a cancellation, and it is treated as a failure.</para>
    /// </summary>
    /// <param name="shape">What comes back. It is named at the call, because a published build is Native
    /// AOT, where a type argument alone carries no metadata.</param>
    private Task<Asked<T?>> AskAsync<T>(
        string relativeUrl, JsonTypeInfo<T> shape, NotFound notFound, CancellationToken ct)
        => AskAsync(relativeUrl, shape, notFound, ct, read: null);

    /// <param name="read">Called with the response before its body is read. Only the papers route needs
    /// this, to check the partial-answer header.</param>
    private async Task<Asked<T?>> AskAsync<T>(
        string relativeUrl, JsonTypeInfo<T> shape, NotFound notFound, CancellationToken ct,
        Action<HttpResponseMessage>? read)
    {
        if (_http is null)
            return Failed<T?>(relativeUrl, "no api url configured");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            if (await _auth.GetAccessTokenAsync() is { Length: > 0 } token)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound && notFound == NotFound.IsNothing)
                return Asked.Answer<T?>(default);

            if (!response.IsSuccessStatusCode)
                return Failed<T?>(
                    relativeUrl,
                    OnlineGateHandler.WasRefused(response)
                        ? "the gate refused it, the app believes it is offline"
                        : $"HTTP {(int)response.StatusCode}");

            read?.Invoke(response);
            return Asked.Answer(await response.Content.ReadFromJsonAsync(shape, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed<T?>(relativeUrl, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>An answer with no body is not an answer on a route that always sends one.</summary>
    private static Asked<T> Bodied<T>(Asked<T?> asked) where T : class
        => asked.Match(
            value => value is null ? Asked.Failed<T>("the answer had no body") : Asked.Answer(value),
            Asked.Failed<T>);

    /// <summary>
    /// A failure, and its reason, written to the log ring.
    /// </summary>
    /// <remarks>
    /// It is written to the ring, not with <c>Debug.WriteLine</c>, because this head does not compile that
    /// call. The WinUI head is built and run in Release. Read the ring with <c>rpmscope log --channel
    /// net</c>. Only failures are written. An answer that arrives costs nothing here.
    /// </remarks>
    private static Asked<T> Failed<T>(string relativeUrl, string why)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said("net", $"metadata {Path(relativeUrl)}: {why}");

        return Asked.Failed<T>(why);
    }

    /// <summary>The path without its query, so a log line names the endpoint and not the artist. The name
    /// asked about is already on the papers line.</summary>
    private static string Path(string relativeUrl)
    {
        var mark = relativeUrl.IndexOf('?');
        return mark < 0 ? relativeUrl : relativeUrl[..mark];
    }
}

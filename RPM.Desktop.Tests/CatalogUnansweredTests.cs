using System.Net;
using System.Net.Http;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Catalog;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Tests;

/// <summary>
/// The catalogue calls behind the lookup, rip and cover windows. An answer of "nothing" and no answer at
/// all are two different results.
///
/// <para>A failed request must never look like an empty answer. Otherwise the windows would say "No
/// releases found", "Could not automatically identify the disc", "That release has no track listing" or "No
/// covers found" for requests that never reached the server.</para>
///
/// <para>What the server sends for "nothing" depends on the route. Each test states it: an empty list with
/// 200 for a search, a 404 for a release or disc that is not known, and 200 with no address for a cover. A
/// 502 or 503 means the server could not ask.</para>
/// </summary>
public class CatalogUnansweredTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-catalog-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(answer(request));
    }

    private AuthService SignedOut() => TestSessions.Auth(_dir);

    private RpmMetadataApi Api(HttpStatusCode code, string body = "")
        => new(SignedOut(), Client(code, body));

    private static HttpClient Client(HttpStatusCode code, string body)
        => new(new Stub(_ => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        })) { BaseAddress = new Uri("https://api.example.com/") };

    private static Task<Asked<IReadOnlyList<CatalogRelease>>> Search(RpmMetadataApi api)
        => api.SearchAsync("Rumours", "Fleetwood Mac", 25, ReleaseMedia.Any, null, null, null);

    // ── search ──

    [Fact]
    public async Task A_search_that_found_nothing_is_an_empty_answer()
        => Assert.Empty(Assert.IsType<Asked<IReadOnlyList<CatalogRelease>>.Answered>(
            await Search(Api(HttpStatusCode.OK, "[]"))).Value);

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_search_the_server_could_not_answer_is_unanswered(HttpStatusCode code)
        => Assert.IsType<Asked<IReadOnlyList<CatalogRelease>>.Unanswered>(await Search(Api(code)));

    // ── a release's listing ──

    [Fact]
    public async Task A_release_nobody_knows_is_null_and_one_not_answered_is_unanswered()
    {
        Assert.Null(Assert.IsType<Asked<CatalogReleaseDetail?>.Answered>(
            await Api(HttpStatusCode.NotFound).GetReleaseAsync("r1")).Value);

        Assert.IsType<Asked<CatalogReleaseDetail?>.Unanswered>(
            await Api(HttpStatusCode.BadGateway).GetReleaseAsync("r1"));
    }

    [Fact]
    public async Task The_rpm_provider_passes_a_failed_listing_on_as_unanswered()
    {
        var provider = new RpmMetadataProvider(Api(HttpStatusCode.BadGateway));

        Assert.IsType<Asked<ReleaseDetail?>.Unanswered>(await provider.GetReleaseAsync("r1"));
    }

    // ── a disc ──

    [Fact]
    public async Task A_disc_nobody_knows_is_null_and_one_not_answered_is_unanswered()
    {
        Assert.Null(Assert.IsType<Asked<CatalogDisc?>.Answered>(
            await Api(HttpStatusCode.NotFound).LookupDiscAsync("disc-id", null)).Value);

        Assert.IsType<Asked<CatalogDisc?>.Unanswered>(
            await Api(HttpStatusCode.BadGateway).LookupDiscAsync("disc-id", null));
    }

    // ── a cover ──

    [Fact]
    public async Task A_release_with_no_cover_is_null_and_one_not_answered_is_unanswered()
    {
        Assert.Null(Assert.IsType<Asked<string?>.Answered>(
            await Api(HttpStatusCode.OK, """{"url":null}""").GetCoverUrlAsync("r1", 250)).Value);

        Assert.Equal("https://art/r1.jpg", Assert.IsType<Asked<string?>.Answered>(
            await Api(HttpStatusCode.OK, """{"url":"https://art/r1.jpg"}""").GetCoverUrlAsync("r1", 250)).Value);

        Assert.IsType<Asked<string?>.Unanswered>(
            await Api(HttpStatusCode.BadGateway).GetCoverUrlAsync("r1", 250));
    }

    // ── plays ──

    [Fact]
    public async Task Plays_the_server_could_not_ask_for_are_unanswered()
        => Assert.IsType<Asked<CatalogPlays>.Unanswered>(
            await Api(HttpStatusCode.ServiceUnavailable).GetPlaysAsync("Fleetwood Mac", "Rumours", 11, null));

    // ── the cover search window ──

    /// <summary>These routes use the user's own session. When signed out, nothing is asked, and that is not
    /// the same as "no covers found".</summary>
    [Fact]
    public async Task A_cover_search_nobody_could_be_asked_for_is_unanswered()
    {
        var covers = new RpmCoverSearchApi(SignedOut(), Client(HttpStatusCode.OK, "{}"));

        Assert.IsType<Asked<CoverSearchResponse>.Unanswered>(
            await covers.SearchAsync(new CoverSearchQuery("Rumours", "Fleetwood Mac", 10, null, CoverKind.Cover)));
        Assert.IsType<Asked<IReadOnlyList<string>>.Unanswered>(await covers.SourcesAsync());
    }

    [Fact]
    public async Task A_cover_search_with_no_api_is_unanswered()
    {
        var covers = new RpmCoverSearchApi(SignedOut(), http: null);

        Assert.IsType<Asked<CoverSearchResponse>.Unanswered>(
            await covers.SearchAsync(new CoverSearchQuery("Rumours", "Fleetwood Mac", 10, null, CoverKind.Cover)));
    }
}

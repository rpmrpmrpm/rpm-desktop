using System.Net;
using System.Text;
using System.Text.Json;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Reading an iBroadcast library and its files, against recorded answers of its library,
/// streaming and sign-in servers.</summary>
public class IBroadcastTests
{
    private static readonly IBroadcastAccount Premium = new("7654321", "testlistener", true);
    private static readonly IBroadcastAccount Free = Premium with { Premium = false };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Answer(HttpRequestMessage r) => r.RequestUri!.Host switch
    {
        "library.ibroadcast.com" => FixtureHttp.Sample("ibroadcast", "library.json"),
        "oauth.ibroadcast.com" => FixtureHttp.Sample("ibroadcast", "token-refreshed.json"),
        "streaming.ibroadcast.com" => FixtureHttp.Bytes([1, 2, 3]),
        _ => FixtureHttp.Status(HttpStatusCode.NotFound),
    };

    private static (IBroadcastServer Server, List<TimeSpan> Waits) Server(FixtureHttp http, IBroadcastAccount account)
    {
        var session = new IBroadcastSession(http.Client(), new IBroadcastGrant("access-token-0001", "refresh-token-0001", DateTimeOffset.UtcNow.AddHours(1)), _ => { });
        var waits = new List<TimeSpan>();
        return (new IBroadcastServer(http.Client(), session, account, "Tags")
        {
            Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
        }, waits);
    }

    private static Task<SourceLibrary> ReadAsync(FixtureHttp http, IBroadcastAccount account) =>
        Server(http, account).Server.ReadAsync(new Progress<int>(), CancellationToken.None);

    private static readonly RemoteFile Opening = new("101|/orig/a1b/c2d/101", ".flac", 0);

    private static bool Streaming((HttpRequestMessage Request, string? Body) x) => x.Request.RequestUri!.Host == "streaming.ibroadcast.com";

    private static bool Refresh((HttpRequestMessage Request, string? Body) x) => x.Request.RequestUri!.Host == "oauth.ibroadcast.com";

    [Fact]
    public async Task IBroadcast_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("ibroadcast"), SourceJson.Of(await ReadAsync(new FixtureHttp(Answer), Premium)));

    [Fact]
    public async Task Library_is_asked_of_the_library_server()
    {
        var http = new FixtureHttp(Answer);

        var library = await ReadAsync(http, Premium);

        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal((HttpMethod.Post, "https://library.ibroadcast.com/"), (request.Method, request.RequestUri!.AbsoluteUri));
        Assert.Equal("Bearer access-token-0001", request.Headers.Authorization!.ToString());
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(body!)!;
        Assert.Equal("library", fields["mode"]);
        Assert.Equal(("rpm", "rpm"), (fields["client"], fields["device_name"]));
        Assert.False(string.IsNullOrEmpty(fields["version"]));
        Assert.Equal(request.Headers.UserAgent.ToString(), fields["user_agent"]);
        Assert.Equal((SourceKind.IBroadcast, TagRule.SourceFirst), (library.Kind, library.Tags));
    }

    [Fact]
    public async Task Free_account_reads_128_mp3_with_unknown_size()
    {
        var library = await ReadAsync(new FixtureHttp(Answer), Free);

        Assert.All(library.Tracks, t =>
        {
            var file = Assert.IsType<RemoteFile>(t.File);
            Assert.Contains("|/128/", file.Key);
            Assert.Equal((".mp3", 0L), (file.Extension, file.Size));
            Assert.Equal((long)Math.Round(t.DurationSeconds * 16_000), t.SizeBytes);
        });
        Assert.Equal(3_856_000, library.Tracks[0].SizeBytes);
    }

    [Fact]
    public async Task Open_sends_signature_file_and_user()
    {
        var http = new FixtureHttp(Answer);

        await using var body = await Server(http, Premium).Server.OpenAsync(Opening, CancellationToken.None);

        var request = Assert.Single(http.Requests).Request;
        Assert.Equal((HttpMethod.Get, "https", "streaming.ibroadcast.com", "/orig/a1b/c2d/101"),
            (request.Method, request.RequestUri!.Scheme, request.RequestUri.Host, request.RequestUri.AbsolutePath));
        Assert.Equal(("access-token-0001", "101", "7654321", "rpm"),
            (FixtureHttp.Query(request, "Signature"), FixtureHttp.Query(request, "file_id"), FixtureHttp.Query(request, "user_id"), FixtureHttp.Query(request, "platform")));
        Assert.False(string.IsNullOrEmpty(FixtureHttp.Query(request, "version")));
        var bytes = new MemoryStream();
        await body.CopyToAsync(bytes);
        Assert.Equal([1, 2, 3], bytes.ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Open_refused_renews_once_then_stops(HttpStatusCode refusal)
    {
        var http = new FixtureHttp(r => Streaming((r, null)) ? FixtureHttp.Status(refusal) : Answer(r));

        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => Server(http, Premium).Server.OpenAsync(Opening, CancellationToken.None));

        Assert.Single(http.Requests, Refresh);
        Assert.Equal(["access-token-0001", "access-token-0002"], http.Requests.Where(Streaming).Select(x => FixtureHttp.Query(x.Request, "Signature")));
    }

    [Fact]
    public async Task Open_refused_once_goes_on_with_the_renewed_token()
    {
        var refused = false;
        var http = new FixtureHttp(r =>
        {
            if (Streaming((r, null)) && !refused)
            {
                refused = true;
                return FixtureHttp.Status(HttpStatusCode.Unauthorized);
            }
            return Answer(r);
        });

        await using var body = await Server(http, Premium).Server.OpenAsync(Opening, CancellationToken.None);

        Assert.Equal("access-token-0002", FixtureHttp.Query(http.Requests.Last().Request, "Signature"));
    }

    [Fact]
    public async Task Too_many_requests_waits_then_fails_for_later()
    {
        var http = new FixtureHttp(r =>
        {
            var busy = FixtureHttp.Status(HttpStatusCode.TooManyRequests);
            busy.Headers.TryAddWithoutValidation("Retry-After", "7");
            return busy;
        });
        var (server, waits) = Server(http, Premium);

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => server.OpenAsync(Opening, CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, failed.StatusCode);
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], waits);
    }

    [Fact]
    public async Task Too_many_requests_without_a_wait_waits_half_a_minute_then_goes_on()
    {
        var busy = true;
        var http = new FixtureHttp(r =>
        {
            if (!busy)
                return Answer(r);
            busy = false;
            return FixtureHttp.Status(HttpStatusCode.TooManyRequests);
        });
        var (server, waits) = Server(http, Premium);

        await using var body = await server.OpenAsync(Opening, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(30)], waits);
    }

    /// <summary>The answer the online gate gives when the network is away: no request was sent.</summary>
    private static HttpResponseMessage Offline()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
        response.Headers.TryAddWithoutValidation(RPM.Core.Net.OnlineGateHandler.RefusedHeader, "1");
        return response;
    }

    [Fact]
    public async Task Network_away_is_no_answer_not_a_failure()
    {
        var (server, _) = Server(new FixtureHttp(_ => Offline()), Premium);

        var open = await Assert.ThrowsAsync<HttpRequestException>(() => server.OpenAsync(Opening, CancellationToken.None));
        var read = await Assert.ThrowsAsync<HttpRequestException>(() => server.ReadAsync(new Progress<int>(), CancellationToken.None));

        Assert.Null(open.StatusCode);
        Assert.Null(read.StatusCode);
    }

    [Fact]
    public async Task Busy_answer_with_a_date_waits_until_then()
    {
        var at = DateTimeOffset.UtcNow.AddSeconds(40);
        var busy = true;
        var http = new FixtureHttp(r =>
        {
            if (!busy)
                return Answer(r);
            busy = false;
            var answer = FixtureHttp.Status(HttpStatusCode.TooManyRequests);
            answer.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(at);
            return answer;
        });
        var (server, waits) = Server(http, Premium);

        await using var body = await server.OpenAsync(Opening, CancellationToken.None);

        Assert.InRange(Assert.Single(waits).TotalSeconds, 35, 41);
    }

    [Fact]
    public async Task Unavailable_with_retry_after_waits_like_busy()
    {
        var unavailable = true;
        var http = new FixtureHttp(r =>
        {
            if (!unavailable)
                return Answer(r);
            unavailable = false;
            var answer = FixtureHttp.Status(HttpStatusCode.ServiceUnavailable);
            answer.Headers.TryAddWithoutValidation("Retry-After", "12");
            return answer;
        });
        var (server, waits) = Server(http, Premium);

        await using var body = await server.OpenAsync(Opening, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(12)], waits);
    }

    [Fact]
    public async Task Unauthenticated_answer_renews_then_stops()
    {
        var http = new FixtureHttp(r => r.RequestUri!.Host == "library.ibroadcast.com"
            ? Json("""{"message":"Error, user authentication failed","result":false,"authenticated":false}""")
            : Answer(r));

        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => ReadAsync(http, Premium));

        Assert.Single(http.Requests, Refresh);
        Assert.Equal(2, http.Requests.Count(x => x.Request.RequestUri!.Host == "library.ibroadcast.com"));
    }

    [Fact]
    public async Task Rows_as_objects_read_the_same()
    {
        var http = new FixtureHttp(_ => Json("""
            {"result":true,"library":{
              "tracks":{"5":{"title":"Song","album_id":7,"artist_id":8,"track":2,"year":1990,"genre":"Pop","length":100,
                             "type":"audio/flac","file":"/128/x/5","size":1000,"plays":4,"rating":5,"trashed":false,
                             "uploaded_on":"2020-02-03","uploaded_time":"04:05:06"}},
              "albums":{"7":{"name":"Album","artist_id":8,"disc":1,"year":1990,"trashed":false,"tracks":[5]}},
              "artists":{"8":{"name":"Singer","tracks":[5],"trashed":false}},
              "playlists":{"map":null},"tags":{"map":null}}}
            """));

        var track = Assert.Single((await ReadAsync(http, Premium)).Tracks);

        Assert.Equal(new SourceTrack("5", new RemoteFile("5|/orig/x/5", ".flac", 0), "Song", "Singer", "Album", "Singer", 2, 1, 1990, "Pop",
            100, 1000, 0, true, 4, null, new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero)), track);
    }
}

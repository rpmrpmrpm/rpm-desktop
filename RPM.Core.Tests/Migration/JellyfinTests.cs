using System.Net;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Jellyfin, against recorded answers of its API.</summary>
public class JellyfinTests
{
    private static readonly Uri Server = new("http://192.168.1.20:8096/");
    private static readonly JellyfinSession Session = new(Server, "f1e2d3c4b5a69788", "a1b2c3d4e5f60718293a4b5c6d7e8f90", "jellyfin-token-0001");

    private static HttpResponseMessage Answer(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        return path switch
        {
            "/System/Info/Public" => FixtureHttp.Sample("jellyfin", "public-info.json"),
            "/Users/a1b2c3d4e5f60718293a4b5c6d7e8f90/Items" when FixtureHttp.Query(r, "IncludeItemTypes") == "Playlist"
                => FixtureHttp.Sample("jellyfin", "playlists.json"),
            "/Users/a1b2c3d4e5f60718293a4b5c6d7e8f90/Items" when FixtureHttp.Query(r, "IncludeItemTypes") == "Audio"
                => FixtureHttp.Sample("jellyfin", FixtureHttp.Query(r, "StartIndex") == "2" ? "items-2.json" : "items-0.json"),
            "/Playlists/00000000000000000000000000000pl1/Items" => FixtureHttp.Sample("jellyfin", "playlist-items.json"),
            _ => FixtureHttp.Status(HttpStatusCode.NotFound),
        };
    }

    private static async Task<SourceLibrary> ReadAsync(FixtureHttp http) =>
        await new JellyfinServer(http.Client(), Session).ReadAsync(new Progress<int>(), CancellationToken.None);

    [Fact]
    public async Task Jellyfin_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("jellyfin"), SourceJson.Of(await ReadAsync(new FixtureHttp(Answer))));

    [Fact]
    public async Task Reads_every_page()
    {
        var http = new FixtureHttp(Answer);
        var library = await ReadAsync(http);

        Assert.Equal(3, library.Tracks.Count);
        Assert.Equal(["0", "2"], http.Requests
            .Where(x => FixtureHttp.Query(x.Request, "IncludeItemTypes") == "Audio")
            .Select(x => FixtureHttp.Query(x.Request, "StartIndex")));
    }

    [Fact]
    public async Task Token_is_sent_on_every_request()
    {
        var http = new FixtureHttp(Answer);
        await ReadAsync(http);

        Assert.All(http.Requests, x => Assert.Contains("Token=\"jellyfin-token-0001\"", x.Request.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Download_falls_back_to_static_stream_on_403()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath switch
        {
            "/Items/j1/Download" => FixtureHttp.Status(HttpStatusCode.Forbidden),
            "/Audio/j1/stream" when FixtureHttp.Query(r, "static") == "true" => FixtureHttp.Bytes([1, 2, 3]),
            _ => FixtureHttp.Status(HttpStatusCode.NotFound),
        });

        await using var stream = await new JellyfinServer(http.Client(), Session)
            .OpenAsync(new RemoteFile("j1", ".flac", 3), CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal([1, 2, 3], copy.ToArray());
    }

    [Fact]
    public async Task Unauthorised_throws_sign_in_expired()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Status(HttpStatusCode.Unauthorized));
        var server = new JellyfinServer(http.Client(), Session);

        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => server.ReadAsync(new Progress<int>(), CancellationToken.None));
        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => server.OpenAsync(new RemoteFile("j1", ".flac", 3), CancellationToken.None));
    }

    [Fact]
    public async Task Password_sign_in_returns_the_session()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/Users/AuthenticateByName" && r.Method == HttpMethod.Post
            ? FixtureHttp.Sample("jellyfin", "authenticate.json")
            : FixtureHttp.Status(HttpStatusCode.NotFound));

        var session = await JellyfinSignIn.WithPasswordAsync(http.Client(), new Uri("http://192.168.1.20:8096"), "test", "secret", CancellationToken.None);

        Assert.Equal(Session, session);
        var (request, body) = http.Requests.Single();
        Assert.Contains("Client=\"rpm\"", request.Headers.Authorization!.ToString());
        Assert.Contains("\"Username\":\"test\"", body);
        Assert.Contains("\"Pw\":\"secret\"", body);
    }

    [Fact]
    public async Task Wrong_password_throws_sign_in_failed()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Status(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<MediaServerSignInFailed>(() =>
            JellyfinSignIn.WithPasswordAsync(http.Client(), Server, "test", "wrong", CancellationToken.None));
    }

    [Fact]
    public async Task Quick_connect_returns_null_until_approved()
    {
        var approved = false;
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath switch
        {
            "/QuickConnect/Initiate" => FixtureHttp.Sample("jellyfin", "quickconnect-initiate.json"),
            "/QuickConnect/Connect" => FixtureHttp.Sample("jellyfin", approved ? "quickconnect-approved.json" : "quickconnect-pending.json"),
            "/Users/AuthenticateWithQuickConnect" => FixtureHttp.Sample("jellyfin", "authenticate.json"),
            _ => FixtureHttp.Status(HttpStatusCode.NotFound),
        });

        var (code, secret) = await JellyfinSignIn.StartQuickConnectAsync(http.Client(), Server, CancellationToken.None);
        Assert.Equal("123456", code);
        Assert.Null(await JellyfinSignIn.FinishQuickConnectAsync(http.Client(), Server, secret, CancellationToken.None));

        approved = true;
        Assert.Equal(Session, await JellyfinSignIn.FinishQuickConnectAsync(http.Client(), Server, secret, CancellationToken.None));
    }
}

using System.Net;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Plex, against recorded answers of plex.tv and of a Plex Media Server.</summary>
public class PlexTests
{
    private const string ClientId = "rpm-test-client";
    private static readonly Uri Connection = new("https://192-168-1-10.0123456789abcdef.plex.direct:32400");
    private static readonly PlexServerChoice Choice = new("Home Plex", "machine-1234", "plex-server-token", [Connection]);

    private static HttpResponseMessage ServerAnswer(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/identity" => FixtureHttp.Sample("plex", "identity.json"),
        "/library/sections" => FixtureHttp.Sample("plex", "sections.json"),
        "/library/sections/3/all" => FixtureHttp.Sample("plex",
            FixtureHttp.Query(r, "X-Plex-Container-Start") == "2" ? "tracks-2.json" : "tracks-0.json"),
        "/playlists" => FixtureHttp.Sample("plex", "playlists.json"),
        "/playlists/700/items" => FixtureHttp.Sample("plex", "playlist-700.json"),
        "/playlists/701/items" => FixtureHttp.Sample("plex", "playlist-701.json"),
        _ => FixtureHttp.Status(HttpStatusCode.NotFound),
    };

    private static Task<SourceLibrary> ReadAsync(FixtureHttp http) =>
        new PlexServer(http.Client(), Choice, Connection).ReadAsync(new Progress<int>(), CancellationToken.None);

    [Fact]
    public async Task Plex_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("plex"), SourceJson.Of(await ReadAsync(new FixtureHttp(ServerAnswer))));

    [Fact]
    public async Task Token_is_sent_to_the_server()
    {
        var http = new FixtureHttp(ServerAnswer);
        await ReadAsync(http);
        Assert.All(http.Requests, x => Assert.Equal("plex-server-token", x.Request.Headers.GetValues("X-Plex-Token").Single()));
    }

    [Fact]
    public async Task Smart_playlist_is_flagged()
    {
        var library = await ReadAsync(new FixtureHttp(ServerAnswer));
        Assert.True(library.Playlists.Single(p => p.Name == "Top rated").Smart);
        Assert.False(library.Playlists.Single(p => p.Name == "Road trip").Smart);
    }

    [Fact]
    public async Task Pin_flow_returns_token_once_linked()
    {
        var linked = false;
        var http = new FixtureHttp(r => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
        {
            ("POST", "/api/v2/pins") => FixtureHttp.Sample("plex", "pin-new.json"),
            ("GET", "/api/v2/pins/12345") => FixtureHttp.Sample("plex", linked ? "pin-linked.json" : "pin-new.json"),
            _ => FixtureHttp.Status(HttpStatusCode.NotFound),
        });

        var pin = await PlexSignIn.StartAsync(http.Client(), ClientId, CancellationToken.None);
        Assert.Equal("abcd1234efgh", pin.Code);
        Assert.StartsWith("https://app.plex.tv/auth#?", pin.AuthPage.ToString());
        Assert.Contains("clientID=rpm-test-client", pin.AuthPage.ToString());
        Assert.Contains("code=abcd1234efgh", pin.AuthPage.ToString());
        Assert.Equal(ClientId, http.Requests[0].Request.Headers.GetValues("X-Plex-Client-Identifier").Single());

        Assert.Null(await PlexSignIn.FinishAsync(http.Client(), ClientId, pin, CancellationToken.None));
        linked = true;
        Assert.Equal("plex-account-token", await PlexSignIn.FinishAsync(http.Client(), ClientId, pin, CancellationToken.None));
    }

    [Fact]
    public async Task Servers_list_https_connections_first()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/api/v2/resources"
            ? FixtureHttp.Sample("plex", "resources.json")
            : FixtureHttp.Status(HttpStatusCode.NotFound));

        var servers = await PlexSignIn.ServersAsync(http.Client(), ClientId, "plex-account-token", CancellationToken.None);

        var server = Assert.Single(servers);
        Assert.Equal("Home Plex", server.Name);
        Assert.Equal("machine-1234", server.MachineId);
        Assert.Equal("plex-server-token", server.AccessToken);
        Assert.Equal(
            ["https://192-168-1-10.0123456789abcdef.plex.direct:32400/", "https://1-2-3-4.0123456789abcdef.plex.direct:32400/",
             "https://5-6-7-8.0123456789abcdef.plex.direct:8443/", "http://192.168.1.10:32400/"],
            server.Connections.Select(c => c.ToString()));
    }

    [Fact]
    public async Task Pick_connection_takes_the_first_that_answers()
    {
        var local = new Uri("https://192-168-1-10.0123456789abcdef.plex.direct:32400");
        var remote = new Uri("https://1-2-3-4.0123456789abcdef.plex.direct:32400");
        var http = new FixtureHttp(r => r.RequestUri!.Host.StartsWith("1-2-3-4", StringComparison.Ordinal)
            ? FixtureHttp.Sample("plex", "identity.json")
            : throw new HttpRequestException("unreachable"));

        var picked = await PlexSignIn.PickConnectionAsync(http.Client(), Choice with { Connections = [local, remote] }, CancellationToken.None);

        Assert.Equal(remote, picked);
    }

    [Fact]
    public async Task Download_uses_the_part_key_with_download_flag()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/library/parts/9001/1500000000/file.flac"
            && FixtureHttp.Query(r, "download") == "1" ? FixtureHttp.Bytes([7, 8]) : FixtureHttp.Status(HttpStatusCode.NotFound));

        await using var stream = await new PlexServer(http.Client(), Choice, Connection)
            .OpenAsync(new RemoteFile("/library/parts/9001/1500000000/file.flac", ".flac", 2), CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal([7, 8], copy.ToArray());
    }

    [Fact]
    public async Task Unauthorised_throws_sign_in_expired()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Status(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => ReadAsync(http));
    }
}

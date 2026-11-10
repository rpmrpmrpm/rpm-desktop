using System.Net;
using System.Security.Cryptography;
using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Navidrome (or any Subsonic server), against recorded answers of the Subsonic API.</summary>
public class SubsonicTests
{
    private static readonly SubsonicLogin Login = new(new Uri("http://nas.local:4533"), "test", "secret");

    private static HttpResponseMessage Answer(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/rest/ping" => FixtureHttp.Sample("subsonic", "ping.json"),
        "/rest/search3" => FixtureHttp.Sample("subsonic", FixtureHttp.Query(r, "songOffset") switch
        {
            "0" => "search3-0.json",
            "2" => "search3-2.json",
            _ => "search3-end.json",
        }),
        "/rest/getPlaylists" => FixtureHttp.Sample("subsonic", "getPlaylists.json"),
        "/rest/getPlaylist" when FixtureHttp.Query(r, "id") == "p1" => FixtureHttp.Sample("subsonic", "getPlaylist-p1.json"),
        _ => FixtureHttp.Status(HttpStatusCode.NotFound),
    };

    private static Task<SourceLibrary> ReadAsync(FixtureHttp http) =>
        new SubsonicServer(http.Client(), Login) { PageSize = 2 }.ReadAsync(new Progress<int>(), CancellationToken.None);

    [Fact]
    public async Task Subsonic_sample_matches_expected() =>
        Assert.Equal(SourceJson.Expected("navidrome"), SourceJson.Of(await ReadAsync(new FixtureHttp(Answer))));

    [Fact]
    public async Task Token_is_md5_of_password_and_salt()
    {
        var http = new FixtureHttp(Answer);
        await ReadAsync(http);

        Assert.All(http.Requests, x =>
        {
            var salt = FixtureHttp.Query(x.Request, "s")!;
            Assert.True(salt.Length >= 8);
            Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("secret" + salt))), FixtureHttp.Query(x.Request, "t"));
            Assert.Equal("test", FixtureHttp.Query(x.Request, "u"));
            Assert.Equal("rpm", FixtureHttp.Query(x.Request, "c"));
            Assert.Equal("json", FixtureHttp.Query(x.Request, "f"));
            Assert.DoesNotContain("secret", x.Request.RequestUri!.ToString());
        });
        Assert.Equal(http.Requests.Count, http.Requests.Select(x => FixtureHttp.Query(x.Request, "s")).Distinct().Count());
    }

    [Fact]
    public async Task Pages_until_short_page()
    {
        var http = new FixtureHttp(Answer);
        var library = await ReadAsync(http);

        Assert.Equal(3, library.Tracks.Count);
        Assert.Equal(["0", "2"], http.Requests
            .Where(x => x.Request.RequestUri!.AbsolutePath == "/rest/search3")
            .Select(x => FixtureHttp.Query(x.Request, "songOffset")));
    }

    [Fact]
    public async Task File_type_comes_from_the_path_before_the_suffix()
    {
        const string page = """
            {"subsonic-response":{"status":"ok","searchResult3":{"song":[
              {"id":"n1","title":"A","suffix":"mp4","path":"Band/Record/01 A.m4a","size":10}
            ]}}}
            """;
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/rest/search3"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "application/json") }
            : Answer(r));

        var library = await ReadAsync(http);

        Assert.Equal(".m4a", ((RemoteFile)Assert.Single(library.Tracks).File).Extension);
    }

    [Fact]
    public async Task Rating_is_doubled()
    {
        var library = await ReadAsync(new FixtureHttp(Answer));
        Assert.Equal(8, library.Tracks.Single(t => t.SourceId == "s1").Rating);
        Assert.Equal(2, library.Tracks.Single(t => t.SourceId == "s3").Rating);
    }

    [Fact]
    public async Task Error_40_throws_sign_in_expired()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Sample("subsonic", "error-40.json"));
        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => ReadAsync(http));
    }

    [Fact]
    public async Task Check_reports_the_server_message()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Sample("subsonic", "error-40.json"));
        var refused = await Assert.ThrowsAsync<MediaServerSignInFailed>(() => SubsonicCheck.CheckAsync(http.Client(), Login, CancellationToken.None));
        Assert.Equal("Wrong username or password", refused.ServerMessage);
    }

    [Fact]
    public async Task Check_passes_on_ok() =>
        await SubsonicCheck.CheckAsync(new FixtureHttp(Answer).Client(), Login, CancellationToken.None);

    [Fact]
    public async Task Download_refused_with_error_40_is_an_expired_sign_in()
    {
        // Subsonic servers answer a refused download with status 200 and an error as the body.
        var http = new FixtureHttp(_ => FixtureHttp.Sample("subsonic", "error-40.json"));

        await Assert.ThrowsAsync<MediaServerSignInExpired>(() =>
            new SubsonicServer(http.Client(), Login).OpenAsync(new RemoteFile("s1", ".flac", 3), CancellationToken.None));
    }

    [Fact]
    public async Task Download_refused_with_another_error_is_a_failed_read_not_a_file()
    {
        var http = new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"subsonic-response":{"status":"failed","error":{"code":70,"message":"Song not found"}}}""", Encoding.UTF8, "application/json"),
        });

        var refused = await Assert.ThrowsAsync<IOException>(() =>
            new SubsonicServer(http.Client(), Login).OpenAsync(new RemoteFile("s1", ".flac", 3), CancellationToken.None));
        Assert.Contains("Song not found", refused.Message);
    }

    [Fact]
    public async Task Download_uses_the_download_endpoint()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/rest/download" && FixtureHttp.Query(r, "id") == "s1"
            ? FixtureHttp.Bytes([4, 5, 6]) : FixtureHttp.Status(HttpStatusCode.NotFound));

        await using var stream = await new SubsonicServer(http.Client(), Login).OpenAsync(new RemoteFile("s1", ".flac", 3), CancellationToken.None);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        Assert.Equal([4, 5, 6], copy.ToArray());
    }
}

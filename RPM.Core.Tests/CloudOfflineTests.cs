using System.Net;
using System.Text;
using RPM.Core.Cloud;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// A request that the app's own gate refused is not an answer from the server. The cloud client says so
/// with <see cref="CloudOfflineException"/>, so that the upload queue puts the track back instead of
/// counting a failure, and does not read the gate's 503 as storage refusing the bytes.
/// </summary>
public class CloudOfflineTests
{
    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(answer(request));
        }
    }

    /// <summary>A gate that has measured a network with nothing beyond it, so it refuses every request.</summary>
    private static NetworkReach Shut() => new(
        hasNetwork: () => true,
        ask: _ => Task.FromResult(false),
        pace: StubPace.Steady,
        now: () => DateTimeOffset.UtcNow);

    private static NetworkReach Open() => new(
        hasNetwork: () => true,
        ask: _ => Task.FromResult(true),
        pace: StubPace.Steady,
        now: () => DateTimeOffset.UtcNow);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task An_api_call_the_gate_refused_throws_offline_and_sends_nothing()
    {
        var api = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new RpmCloudClient(
            new HttpClient(new OnlineGateHandler(Shut(), api)) { BaseAddress = new Uri("https://api.test/") },
            new HttpClient(storage),
            _ => Task.FromResult<string?>("token"));

        var refused = await Assert.ThrowsAsync<CloudOfflineException>(() => client.PutPlaylistArtAsync("p1", Png));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(0, api.Sent);
        Assert.Equal(0, storage.Sent);
    }

    /// <summary>The storage upload is refused by the gate after the API answered. That is still "offline",
    /// not storage rejecting the file. A rejection marks the track as one that will not go up.</summary>
    [Fact]
    public async Task A_storage_upload_the_gate_refused_is_offline_and_not_a_rejection()
    {
        var api = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"url":"https://bucket.storage.test/playlist-art/p1"}""", Encoding.UTF8, "application/json"),
        });
        var storage = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new RpmCloudClient(
            new HttpClient(new OnlineGateHandler(Open(), api)) { BaseAddress = new Uri("https://api.test/") },
            new HttpClient(new OnlineGateHandler(Shut(), storage)),
            _ => Task.FromResult<string?>("token"));

        await Assert.ThrowsAsync<CloudOfflineException>(() => client.PutPlaylistArtAsync("p1", Png));

        Assert.Equal(1, api.Sent);
        Assert.Equal(0, storage.Sent);
    }

    /// <summary>A real 503 from the server is the server's answer, not the gate's. It stays an ordinary
    /// HTTP error, so the retry schedule treats it as a failure of the service.</summary>
    [Fact]
    public async Task A_503_from_the_server_is_not_offline()
    {
        var api = new Recording(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new RpmCloudClient(
            new HttpClient(new OnlineGateHandler(Open(), api)) { BaseAddress = new Uri("https://api.test/") },
            new HttpClient(new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            _ => Task.FromResult<string?>("token"));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => client.PutPlaylistArtAsync("p1", Png));

        Assert.IsNotType<CloudOfflineException>(failed);
        Assert.Equal(1, api.Sent);
    }
}

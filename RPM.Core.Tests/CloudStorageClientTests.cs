using System.Net;
using System.Text;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Which client a cloud request goes out on. The API's client keeps idle connections for minutes; the
/// storage service closes them after seconds. So a request to a presigned storage address must go out on
/// the storage client, and an API call on the API's.
/// </summary>
public class CloudStorageClientTests
{
    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add($"{request.Method} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath}");
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task An_upload_asks_the_api_for_the_address_and_sends_the_bytes_on_the_storage_client()
    {
        var api = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"url":"https://bucket.storage.test/playlist-art/p1"}""", Encoding.UTF8, "application/json"),
        });
        var storage = new Recording(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new RpmCloudClient(
            new HttpClient(api) { BaseAddress = new Uri("https://api.test/") },
            new HttpClient(storage),
            _ => Task.FromResult<string?>("token"));

        // A PNG header, so the type sniffs as a picture.
        await client.PutPlaylistArtAsync("p1", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);

        Assert.Equal(["POST api.test/cloud/playlist-art/p1"], api.Seen);
        Assert.Equal(["PUT bucket.storage.test/playlist-art/p1"], storage.Seen);
    }
}

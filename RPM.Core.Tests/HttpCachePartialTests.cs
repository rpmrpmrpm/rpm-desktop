using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The web cache and the RPM API's partial answers.
///
/// <para>The cache keeps an artwork response for thirty days and ignores Cache-Control on purpose. The
/// API marks an incomplete response (a source could not be queried, or did not reply in time) with
/// <c>X-RPM-Partial</c>, and completes it in the background. If the cache stored it, the partial
/// response would be used instead of the complete one for a month.</para>
/// </summary>
public class HttpCachePartialTests
{
    private sealed class Store : IWebCacheStore
    {
        public List<WebCacheEntry> Written { get; } = [];
        public WebCacheEntry? Read(string key) => null;
        public void Write(WebCacheEntry entry) => Written.Add(entry);
        public void Renew(string key, long fetchedUtcTicks, long expiresUtcTicks) { }
        public void Clear() { }
        public long BytesOnDisk() => 0;
    }

    private sealed class Answering(bool partial) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"backgrounds":[]}"""),
                RequestMessage = request,
            };
            if (partial)
                response.Headers.Add("X-RPM-Partial", "1");
            return Task.FromResult(response);
        }
    }

    private static async Task<Store> AskAsync(bool partial)
    {
        var store = new Store();
        using var handler = new HttpCacheHandler(
            store, new WebCachePolicy("test", TimeSpan.FromDays(30), TimeSpan.FromDays(7)), new Answering(partial));
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync("https://api.example/artwork?artist=Fleetwood%20Mac");
        Assert.True(response.IsSuccessStatusCode);
        return store;
    }

    [Fact]
    public async Task A_partial_answer_is_not_filed() => Assert.Empty((await AskAsync(partial: true)).Written);

    [Fact]
    public async Task A_whole_answer_is_filed() => Assert.Single((await AskAsync(partial: false)).Written);
}

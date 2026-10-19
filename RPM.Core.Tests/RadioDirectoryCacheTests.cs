using System.Net;
using System.Text;
using RPM.Core.Net;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The directory client behind the desktop's HTTP cache. A search may be answered from the cache; a
/// play report may not, or the directory never counts the play.
/// </summary>
public sealed class RadioDirectoryCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-radio-cache-tests", Guid.NewGuid().ToString("N"));

    private readonly WebCacheStore _store;

    public RadioDirectoryCacheTests() => _store = new WebCacheStore(_dir);

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Answers every request and records its path.</summary>
    private sealed class Directory_ : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Asked) Asked.Add(path);

            var body = path.StartsWith("/json/url/", StringComparison.Ordinal) ? """{"ok":true}""" : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>Cached for 30 days, the longest any client in the app keeps an answer.</summary>
    private RadioBrowserClient Client(Directory_ directory)
        => new(new HttpClient(new HttpCacheHandler(_store, WebCachePolicy.For("radio"), inner: directory)));

    [Fact]
    public async Task A_play_report_reaches_the_directory_every_time()
    {
        var directory = new Directory_();
        var client = Client(directory);

        await client.ReportPlayedAsync("abc-123");
        await client.ReportPlayedAsync("abc-123");

        Assert.Equal(2, directory.Asked.Count(p => p == "/json/url/abc-123"));
    }

    [Fact]
    public async Task A_search_may_be_answered_from_the_cache()
    {
        var directory = new Directory_();
        var client = Client(directory);

        await client.SearchAsync(new RadioQuery { CountryCode = "NL" });
        await client.SearchAsync(new RadioQuery { CountryCode = "NL" });

        Assert.Single(directory.Asked, p => p == "/json/stations/search");
    }
}

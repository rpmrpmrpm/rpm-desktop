using System.Net;
using System.Text;
using RPM.Core.Analysis;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// A record's sound goes to the pool at its own address, whose daily limit is its own, and a store that
/// has used that limit up says so in a way the app can tell apart from any other failure.
/// </summary>
public class SoundReportTests
{
    private sealed class Store(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    """{"recordingId":"rec-1","taken":["sound"],"agreed":[],"kept":[],"disputed":[]}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private static RpmCloudClient Client(HttpMessageHandler store)
    {
        var http = new HttpClient(store) { BaseAddress = new Uri("https://api.test/") };
        return new(http, http, _ => Task.FromResult<string?>("token"));
    }

    private static RecordingFactBody Sound()
    {
        var v = new float[SoundEmbedding.Dimensions];
        v[0] = 1;
        return SoundFact.From(v)!;
    }

    [Fact]
    public async Task A_sound_goes_to_its_own_address_with_nothing_beside_it()
    {
        var store = new Store(HttpStatusCode.OK);

        var recording = await Client(store).ReportSoundAsync(["canon:a", "samples:b"], Sound());

        Assert.Equal("rec-1", recording);
        var (path, body) = Assert.Single(store.Asked);
        Assert.Equal("/recordings/facts/sound", path);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var fact = Assert.Single(json.RootElement.GetProperty("facts").EnumerateArray());
        Assert.Equal("sound", fact.GetProperty("field").GetString());
    }

    /// <summary>The day's limit used up is a 429, and it reaches the caller as one. RecordingPoolClient
    /// stops reporting sounds for the session on exactly this.</summary>
    [Fact]
    public async Task A_used_up_day_is_told_apart_from_other_failures()
    {
        var refused = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(new Store(HttpStatusCode.TooManyRequests)).ReportSoundAsync(["canon:a"], Sound()));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task No_names_is_no_report()
    {
        var store = new Store(HttpStatusCode.OK);

        Assert.Null(await Client(store).ReportSoundAsync([], Sound()));
        Assert.Empty(store.Asked);
    }
}

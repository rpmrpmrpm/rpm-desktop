using System.Net;
using System.Text.Json;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The listening log on the wire: this machine's listens sent under its device, and every device's listens
/// read back a page at a time.</summary>
public class ListensClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return answer(request);
        }
    }

    private static (RpmCloudClient Client, Handler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        return (new RpmCloudClient(http, http, _ => Task.FromResult<string?>("token")), handler);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static readonly DateTimeOffset Ended = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    [Fact]
    public async Task Listens_are_sent_under_this_machine_in_seconds()
    {
        var (client, handler) = Client(_ => Json("""{"receivedAt":1790000000123}"""));

        var stamp = await client.PutListensAsync("desk-1",
            [new SharedListen("l1", "desk-1", "t1", Ended.UtcTicks, 120.5, 241, true, 23, 5)], CancellationToken.None);

        Assert.Equal(1_790_000_000_123, stamp);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://api.test/cloud/listens", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("desk-1", json.RootElement.GetProperty("deviceId").GetString());
        var listen = json.RootElement.GetProperty("listens")[0];
        Assert.Equal("l1", listen.GetProperty("listenId").GetString());
        Assert.Equal("t1", listen.GetProperty("rpmId").GetString());
        Assert.Equal(1_790_000_000, listen.GetProperty("at").GetInt64());
        Assert.Equal(120.5, listen.GetProperty("heardSeconds").GetDouble());
        Assert.True(listen.GetProperty("counted").GetBoolean());
        Assert.Equal(23, listen.GetProperty("localHour").GetInt32());
        Assert.Equal(5, listen.GetProperty("localDayOfWeek").GetInt32());
    }

    [Fact]
    public async Task Listens_are_read_back_a_page_at_a_time_with_the_device_that_heard_them()
    {
        var (client, handler) = Client(_ => Json("""
            {"listens":[{"listenId":"p1","deviceId":"phone","rpmId":"t1","at":1790000000,"heardSeconds":200,
              "lengthSeconds":240,"counted":true,"localHour":22,"localDayOfWeek":3,"receivedAt":1790000000999}],
             "page":"next-token"}
            """));

        var page = await client.ListListensAsync(since: 77, page: "a b", CancellationToken.None);

        Assert.Equal("https://api.test/cloud/listens?since=77&page=a%20b", handler.Seen[0].Request.RequestUri!.AbsoluteUri);
        var listen = Assert.Single(page.Listens);
        Assert.Equal(new SharedListen("p1", "phone", "t1", Ended.UtcTicks, 200, 240, true, 22, 3), listen);
        Assert.Equal(1_790_000_000_999, page.Newest);
        Assert.Equal("next-token", page.Next);
    }

    [Fact]
    public async Task The_sayings_are_sent_whole_with_the_names_of_kinds_and_verdicts()
    {
        var (client, handler) = Client(_ => Json("{}"));

        await client.PutListSayingsAsync(new ListSayings(
            [new ListVerdict("t1", SmartPlaylistKind.Calm, TrackVerdict.Disliked, Ended.UtcTicks)],
            [new ListRefusal(RecommendScope.Artist, "Nickelback", SmartPlaylistKind.Energetic, Ended.UtcTicks)]), CancellationToken.None);

        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://api.test/cloud/list-sayings", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        var verdict = json.RootElement.GetProperty("verdicts")[0];
        Assert.Equal("t1", verdict.GetProperty("rpmId").GetString());
        Assert.Equal("Calm", verdict.GetProperty("kind").GetString());
        Assert.Equal("Disliked", verdict.GetProperty("verdict").GetString());
        Assert.Equal(1_790_000_000, verdict.GetProperty("at").GetInt64());
        var refusal = json.RootElement.GetProperty("refusals")[0];
        Assert.Equal("Artist", refusal.GetProperty("scope").GetString());
        Assert.Equal("Nickelback", refusal.GetProperty("subject").GetString());
        Assert.Equal("Energetic", refusal.GetProperty("kind").GetString());
    }
}

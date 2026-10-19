using System.Net;
using System.Net.Http;
using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// What <see cref="LrcLibLookup"/> answers and what it throws. LyricsService writes "no sheet" to disk
/// for a fortnight, so only a real "nobody has written this down" may come back as one. A 503
/// (including the app's own offline gate, which answers with a made-up 503 rather than throwing)
/// and a body that will not parse must not come back as no sheet.
/// </summary>
public class LrcLibAnswerTests
{
    private sealed class Answering(HttpStatusCode code, string body = "", bool offline = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(code) { Content = new StringContent(body), RequestMessage = request };
            if (offline)
                response.Headers.TryAddWithoutValidation(RPM.Core.Net.OnlineGateHandler.RefusedHeader, "1");
            return Task.FromResult(response);
        }
    }

    private static LrcLibLookup Live(HttpMessageHandler handler)
        => LrcLibLookup.Live(new HttpClient(handler), "rpm-tests/1.0");

    [Fact]
    public async Task Nobody_has_written_it_down_is_no_sheet()
    {
        var sheet = await Live(new Answering(HttpStatusCode.NotFound)).FindAsync("A", "B", null, 200);

        Assert.False(sheet.IsSynced);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_service_that_did_not_answer_throws(HttpStatusCode code)
        => await Assert.ThrowsAsync<HttpRequestException>(
            () => Live(new Answering(code)).FindAsync("A", "B", null, 200));

    [Fact]
    public async Task The_app_being_offline_throws()
        => await Assert.ThrowsAsync<HttpRequestException>(
            () => Live(new Answering(HttpStatusCode.ServiceUnavailable, offline: true)).FindAsync("A", "B", null, 200));

    [Fact]
    public async Task A_body_that_will_not_parse_throws()
        => await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => new LrcLibLookup((_, _) => Task.FromResult<string?>("<html>busy</html>"))
                .FindAsync("A", "B", null, 200));

    [Fact]
    public async Task The_alternatives_throw_for_a_failure_too()
        => await Assert.ThrowsAsync<HttpRequestException>(
            () => Live(new Answering(HttpStatusCode.BadGateway)).FindAllAsync("A", "B", null, 200));
}

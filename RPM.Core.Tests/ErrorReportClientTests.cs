using System.Net;
using System.Text;
using System.Text.Json;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Sending a failure report. The app keeps its failures locally and shows them; this is what the
/// button next to one does. The two things to check are that the whole message is sent, and that a
/// send that fails reports the failure instead of claiming success.
/// </summary>
public class ErrorReportClientTests
{
    private sealed class FakeApi(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static ErrorReport Report(string detail = "ffmpeg could not canonicalize '07 - Song.m4a': [ipod] refused") =>
        new("cloud", "upload", "Song", "Record", detail, "0.1.0", "Windows", 1234);

    private static (RpmCloudClient Client, FakeApi Api) Api(HttpStatusCode status, string body)
    {
        var api = new FakeApi(status, body);
        var http = new HttpClient(api) { BaseAddress = new Uri("https://api.test/") };
        return (new RpmCloudClient(http, _ => Task.FromResult<string?>("token")), api);
    }

    [Fact]
    public async Task A_report_goes_up_whole()
    {
        var (client, api) = Api(HttpStatusCode.OK, """{"errorId":"err_abc"}""");

        var id = await client.ReportErrorAsync(Report());

        Assert.Equal("err_abc", id);
        Assert.Equal("errors", api.Path);

        var sent = JsonDocument.Parse(api.Body!).RootElement;
        Assert.Equal("cloud", sent.GetProperty("area").GetString());
        Assert.Equal("upload", sent.GetProperty("operation").GetString());
        Assert.Equal("Song", sent.GetProperty("title").GetString());
        Assert.Equal("Record", sent.GetProperty("subtitle").GetString());
        Assert.Equal("0.1.0", sent.GetProperty("appVersion").GetString());
        Assert.Equal(1234, sent.GetProperty("atUtcTicks").GetInt64());

        // The reason is sent unchanged: a report cut down to a headline cannot be acted on.
        Assert.Contains("could not canonicalize", sent.GetProperty("detail").GetString());
        Assert.Contains("[ipod] refused", sent.GetProperty("detail").GetString());
    }

    /// <summary>Offline, signed out, or an API failure: all give the same answer here, which is that
    /// nothing was filed and the button the user pressed should still work.</summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_report_that_does_not_arrive_says_so(HttpStatusCode status)
    {
        var (client, _) = Api(status, """{"error":"no"}""");

        Assert.Null(await client.ReportErrorAsync(Report()));
    }
}

using System.Net;
using System.Text.Json;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>The send: a token when there is one, nothing when there is not, and null on any failure
/// so the report stays pending.</summary>
public class CrashReportClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return answer(request);
        }
    }

    private static readonly CrashReport Report = ReportScope.Full.Crash(
        "crash_0123456789abcdef01234567", "mac", "0.3.1", "macOS 26.5", "arm64", 638_000_000_000_000_000,
        "install-1", "System.NullReferenceException: boom", null, null, "said boot: app ctor");

    private static (RpmCloudClient Client, Handler Handler) Client(string? token, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") };
        return (new RpmCloudClient(http, _ => Task.FromResult(token)), handler);
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("""{"errorId":"crash_0123456789abcdef01234567"}""") };

    [Fact]
    public async Task A_signed_out_user_sends_without_a_token()
    {
        var (client, handler) = Client(null, _ => Ok());

        var id = await client.ReportCrashAsync(Report, CancellationToken.None);

        Assert.Equal("crash_0123456789abcdef01234567", id);
        Assert.Equal("https://api.test/errors/crash", handler.Seen!.RequestUri!.ToString());
        Assert.Null(handler.Seen.Headers.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("install-1", body.RootElement.GetProperty("installId").GetString());
        Assert.Equal("said boot: app ctor", body.RootElement.GetProperty("recent").GetString());
    }

    [Fact]
    public async Task A_signed_in_user_sends_the_token()
    {
        var (client, handler) = Client("token-1", _ => Ok());

        await client.ReportCrashAsync(Report, CancellationToken.None);

        Assert.Equal("Bearer", handler.Seen!.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", handler.Seen.Headers.Authorization.Parameter);
    }

    /// <summary>Offline at launch.</summary>
    [Fact]
    public async Task No_connection_answers_null()
    {
        var (client, _) = Client(null, _ => throw new HttpRequestException("offline"));

        Assert.Null(await client.ReportCrashAsync(Report, CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_answers_null()
    {
        var (client, _) = Client(null, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        Assert.Null(await client.ReportCrashAsync(Report, CancellationToken.None));
    }

    /// <summary>The server takes at most 1.5 MB. JSON escaping makes a report longer than its text (an
    /// .ips is full of quotes), so the body is measured in bytes and the system report is made smaller
    /// until it fits. Otherwise it would be sent and refused at every launch.</summary>
    [Fact]
    public async Task A_report_too_large_for_the_server_is_made_smaller_before_it_is_sent()
    {
        var (client, handler) = Client(null, _ => Ok());
        var large = Report with { SystemReport = new string('"', 1_000_000), SystemReportKind = "wer" };

        Assert.Equal("crash_0123456789abcdef01234567", await client.ReportCrashAsync(large, CancellationToken.None));

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(handler.Body!), 1, RpmCloudClient.MaxCrashRequestBytes);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("System.NullReferenceException: boom", body.RootElement.GetProperty("exception").GetString());
        Assert.True(body.RootElement.GetProperty("systemReport").GetString()!.Length > 100_000);
    }
}

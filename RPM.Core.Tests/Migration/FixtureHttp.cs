using System.Net;
using System.Text;

namespace RPM.Core.Tests;

/// <summary>A media server in a test: answers each request with a sample file, a status, or bytes,
/// and keeps every request so a test can check what was asked.</summary>
internal sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        return answer(request);
    }

    public static HttpResponseMessage Sample(string folder, string file) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(File.ReadAllText(SourceJson.AssetPath(folder, file)), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new StringContent("") };

    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>The query value, decoded, or null.</summary>
    public static string? Query(HttpRequestMessage request, string name) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[name];
}

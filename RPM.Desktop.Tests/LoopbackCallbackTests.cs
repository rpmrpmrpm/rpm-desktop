using System.Net;
using System.Net.Http;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// The listener that receives the end of a social sign-in on Windows and Linux (<see cref="LoopbackCallback"/>).
/// The browser comes back to <c>http://127.0.0.1:{port}/auth?sessionId=…&amp;code=…</c>, and only that
/// request, for this app's own session, ends the wait.
/// </summary>
public class LoopbackCallbackTests
{
    [Fact]
    public void A_request_for_auth_gives_the_session_and_the_code() =>
        Assert.Equal(("abc", "x y&z"), LoopbackCallback.Parse("GET /auth?sessionId=abc&code=x%20y%26z HTTP/1.1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GET /favicon.ico HTTP/1.1")]
    [InlineData("GET /auth HTTP/1.1")]
    [InlineData("GET /auth?sessionId=abc HTTP/1.1")]
    [InlineData("GET /auth?code=xyz HTTP/1.1")]
    [InlineData("POST /auth?sessionId=abc&code=xyz HTTP/1.1")]
    [InlineData("GET /authx?sessionId=abc&code=xyz HTTP/1.1")]
    public void Anything_else_is_not_the_way_back(string? line) =>
        Assert.Null(LoopbackCallback.Parse(line));

    [Fact]
    public void The_page_is_escaped() =>
        Assert.Contains("&lt;b&gt;", LoopbackCallback.Page("<b>", "body"));

    [Fact]
    public async Task The_browser_request_ends_the_wait_and_gets_the_page()
    {
        using var loopback = LoopbackCallback.Open();
        var wait = loopback.WaitForCodeAsync("abc", "<p>done</p>", CancellationToken.None);

        using var http = new HttpClient();
        var favicon = await http.GetAsync($"http://127.0.0.1:{loopback.Port}/favicon.ico");
        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        var other = await http.GetAsync($"http://127.0.0.1:{loopback.Port}/auth?sessionId=other&code=1");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.False(wait.IsCompleted);

        var page = await http.GetStringAsync($"http://127.0.0.1:{loopback.Port}/auth?sessionId=abc&code=xyz");
        Assert.Equal("<p>done</p>", page);
        Assert.Equal("xyz", await wait.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_connection_that_sends_nothing_does_not_block_the_browser()
    {
        using var loopback = LoopbackCallback.Open();
        var wait = loopback.WaitForCodeAsync("abc", "ok", CancellationToken.None);

        using var silent = new System.Net.Sockets.TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, loopback.Port);

        using var http = new HttpClient();
        await http.GetStringAsync($"http://127.0.0.1:{loopback.Port}/auth?sessionId=abc&code=xyz");
        Assert.Equal("xyz", await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Cancelling_ends_the_wait()
    {
        using var loopback = LoopbackCallback.Open();
        using var cts = new CancellationTokenSource();
        var wait = loopback.WaitForCodeAsync("abc", "ok", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public void It_listens_on_loopback_only()
    {
        using var loopback = LoopbackCallback.Open();
        Assert.InRange(loopback.Port, 1024, 65535);
    }
}

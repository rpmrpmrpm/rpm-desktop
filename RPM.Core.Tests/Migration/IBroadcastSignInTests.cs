using System.Net;
using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>The iBroadcast device code sign-in and the token holder, against recorded answers of its
/// sign-in server.</summary>
public class IBroadcastSignInTests
{
    private static readonly DateTimeOffset Noon = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Dictionary<string, string> Form(string? body) =>
        (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    private static IBroadcastCode Code(TimeSpan? interval = null) =>
        new("d-code", "TESTCODE", new Uri("https://oauth.ibroadcast.com/device?user_code=testcode"), interval ?? TimeSpan.FromSeconds(1), Noon.AddMinutes(3));

    private static IBroadcastSession Session(FixtureHttp http, Action<IBroadcastGrant>? renewed = null, DateTimeOffset? expires = null) =>
        new(http.Client(), new IBroadcastGrant("access-token-0001", "refresh-token-0001", expires ?? Noon.AddHours(1)), renewed ?? (_ => { }))
        {
            Now = () => Noon,
        };

    private static bool IsRefresh((HttpRequestMessage Request, string? Body) x) =>
        x.Request.RequestUri!.AbsolutePath == "/token" && Form(x.Body).GetValueOrDefault("grant_type") == "refresh_token";

    [Fact]
    public async Task Start_asks_for_both_scopes_with_the_client_id()
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsolutePath == "/device/code"
            ? FixtureHttp.Sample("ibroadcast", "device-code.json")
            : FixtureHttp.Status(HttpStatusCode.NotFound));

        var before = DateTimeOffset.UtcNow;
        var code = await IBroadcastSignIn.StartAsync(http.Client(), CancellationToken.None);

        var request = Assert.Single(http.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("oauth.ibroadcast.com", request.RequestUri!.Host);
        Assert.Equal(IBroadcastSignIn.ClientId, FixtureHttp.Query(request, "client_id"));
        Assert.Equal("user.library:read user.account:read", FixtureHttp.Query(request, "scope"));
        Assert.Equal("TESTCODE", code.UserCode);
        Assert.Equal(new Uri("https://oauth.ibroadcast.com/device?user_code=testcode"), code.Page);
        Assert.Equal(TimeSpan.FromSeconds(1), code.Interval);
        Assert.InRange(code.ExpiresAt, before.AddSeconds(179), DateTimeOffset.UtcNow.AddSeconds(179));
    }

    [Fact]
    public void Client_id_is_the_registered_one() =>
        Assert.Matches("^[0-9a-f]{32}$", IBroadcastSignIn.ClientId);

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "authorization_pending", 1)]
    [InlineData(HttpStatusCode.TooManyRequests, "slow_down", 6)]
    public async Task Poll_pending_gives_the_code_to_ask_with_next(HttpStatusCode status, string error, int seconds)
    {
        var http = new FixtureHttp(_ => Json(status, $$"""{"error":"{{error}}"}"""));

        var poll = await IBroadcastSignIn.PollAsync(http.Client(), Code(), CancellationToken.None);

        var pending = Assert.IsType<IBroadcastPoll.Pending>(poll);
        Assert.Equal(TimeSpan.FromSeconds(seconds), pending.Code.Interval);
        Assert.Equal("d-code", pending.Code.DeviceCode);
        var form = Form(Assert.Single(http.Requests).Body);
        Assert.Equal(("device_code", "d-code", IBroadcastSignIn.ClientId), (form["grant_type"], form["device_code"], form["client_id"]));
    }

    [Fact]
    public async Task Poll_expired_code_is_expired()
    {
        var http = new FixtureHttp(_ => Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));

        Assert.IsType<IBroadcastPoll.Expired>(await IBroadcastSignIn.PollAsync(http.Client(), Code(), CancellationToken.None));
    }

    [Fact]
    public async Task Poll_approved_gives_the_grant()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Sample("ibroadcast", "token.json"));

        var before = DateTimeOffset.UtcNow;
        var approved = Assert.IsType<IBroadcastPoll.Approved>(await IBroadcastSignIn.PollAsync(http.Client(), Code(), CancellationToken.None));

        Assert.Equal(("access-token-0001", "refresh-token-0001"), (approved.Grant.AccessToken, approved.Grant.RefreshToken));
        Assert.InRange(approved.Grant.ExpiresAt, before.AddSeconds(3599), DateTimeOffset.UtcNow.AddSeconds(3599));
    }

    [Fact]
    public async Task Poll_denied_is_a_refused_sign_in()
    {
        var http = new FixtureHttp(_ => Json(HttpStatusCode.BadRequest, """{"error":"access_denied","error_description":"The user denied the request"}"""));

        var refused = await Assert.ThrowsAsync<MediaServerSignInFailed>(() => IBroadcastSignIn.PollAsync(http.Client(), Code(), CancellationToken.None));
        Assert.Equal("The user denied the request", refused.ServerMessage);
    }

    [Theory]
    [InlineData("status.json", true)]
    [InlineData("status-free.json", false)]
    public async Task Account_reads_id_name_and_premium(string sample, bool premium)
    {
        var http = new FixtureHttp(r => r.RequestUri!.AbsoluteUri == "https://api.ibroadcast.com/status"
            ? FixtureHttp.Sample("ibroadcast", sample)
            : FixtureHttp.Status(HttpStatusCode.NotFound));

        var account = await IBroadcastSignIn.AccountAsync(http.Client(), Session(http), CancellationToken.None);

        Assert.Equal(new IBroadcastAccount("7654321", "testlistener", premium), account);
        Assert.Equal("Bearer access-token-0001", Assert.Single(http.Requests).Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Renew_once_for_two_callers()
    {
        var http = new FixtureHttp(r =>
        {
            Thread.Sleep(100);
            return FixtureHttp.Sample("ibroadcast", "token-refreshed.json");
        });
        var session = Session(http);

        var both = await Task.WhenAll(
            Task.Run(() => session.RenewAsync("access-token-0001", CancellationToken.None)),
            Task.Run(() => session.RenewAsync("access-token-0001", CancellationToken.None)));

        Assert.Equal(["access-token-0002", "access-token-0002"], both);
        var refresh = Assert.Single(http.Requests, IsRefresh);
        Assert.Equal(("refresh-token-0001", IBroadcastSignIn.ClientId), (Form(refresh.Body)["refresh_token"], Form(refresh.Body)["client_id"]));
    }

    [Fact]
    public async Task Renewed_grant_reaches_the_keeper()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Sample("ibroadcast", "token-refreshed.json"));
        IBroadcastGrant? kept = null;
        var session = Session(http, g => kept = g);

        await session.RenewAsync("access-token-0001", CancellationToken.None);

        Assert.Equal(new IBroadcastGrant("access-token-0002", "refresh-token-0002", Noon.AddSeconds(3599)), kept);
        Assert.Equal(kept, session.Grant);
    }

    [Fact]
    public async Task Renewal_without_a_refresh_token_keeps_the_old_one()
    {
        var http = new FixtureHttp(_ => Json(HttpStatusCode.OK, """{"access_token":"access-token-0002","token_type":"Bearer","expires_in":3599}"""));
        var session = Session(http);

        await session.RenewAsync("access-token-0001", CancellationToken.None);

        Assert.Equal("refresh-token-0001", session.Grant.RefreshToken);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("invalid_token")]
    public async Task Refused_refresh_is_sign_in_expired(string error)
    {
        var http = new FixtureHttp(_ => Json(HttpStatusCode.BadRequest, $$"""{"error":"{{error}}"}"""));

        await Assert.ThrowsAsync<MediaServerSignInExpired>(() => Session(http).RenewAsync("access-token-0001", CancellationToken.None));
    }

    [Fact]
    public async Task Failed_refresh_is_not_sign_in_expired()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Status(HttpStatusCode.BadGateway));

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => Session(http).RenewAsync("access-token-0001", CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
    }

    [Fact]
    public async Task Network_away_during_a_renewal_is_no_answer()
    {
        var http = new FixtureHttp(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) };
            response.Headers.TryAddWithoutValidation(RPM.Core.Net.OnlineGateHandler.RefusedHeader, "1");
            return response;
        });

        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => Session(http).RenewAsync("access-token-0001", CancellationToken.None));
        Assert.Null(failed.StatusCode);
    }

    [Fact]
    public async Task Renewal_that_was_sent_finishes_when_the_caller_cancels()
    {
        using var sent = new ManualResetEventSlim();
        var slow = new SlowServer(sent);
        IBroadcastGrant? kept = null;
        var session = new IBroadcastSession(new HttpClient(slow), new IBroadcastGrant("access-token-0001", "refresh-token-0001", Noon.AddHours(1)), g => kept = g)
        {
            Now = () => Noon,
        };
        using var pause = new CancellationTokenSource();

        var renewing = Task.Run(() => session.RenewAsync("access-token-0001", pause.Token));
        sent.Wait(TimeSpan.FromSeconds(5));
        pause.Cancel();
        try { await renewing; } catch (OperationCanceledException) { }

        Assert.Equal("refresh-token-0002", kept?.RefreshToken);
        Assert.Equal("refresh-token-0002", session.Grant.RefreshToken);
    }

    /// <summary>A sign-in server that answers after 200 ms and stops when the request is cancelled, as a
    /// real connection does.</summary>
    private sealed class SlowServer(ManualResetEventSlim sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            sent.Set();
            await Task.Delay(200, ct);
            return FixtureHttp.Sample("ibroadcast", "token-refreshed.json");
        }
    }

    [Fact]
    public async Task Token_renews_a_minute_before_it_expires()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Sample("ibroadcast", "token-refreshed.json"));

        Assert.Equal("access-token-0001", await Session(http, expires: Noon.AddSeconds(61)).TokenAsync(CancellationToken.None));
        Assert.Equal("access-token-0002", await Session(http, expires: Noon.AddSeconds(59)).TokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_sends_the_refresh_token()
    {
        var http = new FixtureHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });

        await IBroadcastSignIn.RevokeAsync(http.Client(), "refresh-token-0002", CancellationToken.None);

        var revoke = Assert.Single(http.Requests);
        Assert.Equal("/revoke", revoke.Request.RequestUri!.AbsolutePath);
        Assert.Equal(("refresh-token-0002", IBroadcastSignIn.ClientId), (Form(revoke.Body)["refresh_token"], Form(revoke.Body)["client_id"]));
    }
}

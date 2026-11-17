using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>An iBroadcast sign-in code. The person opens <paramref name="Page"/> (the code is filled
/// in) and approves rpm there; rpm never sees the iBroadcast password. <paramref name="Interval"/> is
/// how long to wait before asking again.</summary>
public sealed record IBroadcastCode(string DeviceCode, string UserCode, Uri Page, TimeSpan Interval, DateTimeOffset ExpiresAt);

/// <summary>The answer to "was the code approved?".</summary>
public abstract record IBroadcastPoll
{
    private IBroadcastPoll() { }

    /// <summary>Not approved yet. Ask again with <paramref name="Code"/>, after its interval. The
    /// interval is longer when the server asked rpm to slow down.</summary>
    public sealed record Pending(IBroadcastCode Code) : IBroadcastPoll;

    /// <summary>Approved: the tokens.</summary>
    public sealed record Approved(IBroadcastGrant Grant) : IBroadcastPoll;

    /// <summary>The code ran out before it was approved. A new code is needed.</summary>
    public sealed record Expired : IBroadcastPoll;
}

/// <summary>The tokens of a sign-in. Both are secrets. A renewal replaces both: after it, the old
/// access token and the old refresh token are refused.</summary>
public sealed record IBroadcastGrant(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>The signed-in account. A free account gets 128 kbps MP3 copies instead of the original
/// files.</summary>
public sealed record IBroadcastAccount(string UserId, string UserName, bool Premium);

/// <summary>
/// Signing in to iBroadcast with a device code (OAuth 2.0): rpm asks for a code, the person approves it
/// on iBroadcast's site, and rpm then receives the tokens. The flow needs no client secret, so the
/// client id below can be public. rpm asks only to read the library and the account.
/// </summary>
public static class IBroadcastSignIn
{
    /// <summary>rpm's app as registered with iBroadcast. Public: a device code sign-in has no secret.</summary>
    public const string ClientId = "813e6017e13311f1b50eb49691aa2236";

    internal const string Scopes = "user.library:read user.account:read";

    internal static readonly Uri OAuth = new("https://oauth.ibroadcast.com/");

    public static async Task<IBroadcastCode> StartAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(OAuth,
            $"device/code?client_id={ClientId}&scope={Uri.EscapeDataString(Scopes)}"));
        var answer = await JsonAsync(http, request, ct);
        var page = Uri.TryCreate(Str(answer, "verification_uri_complete"), UriKind.Absolute, out var complete) ? complete
            : new Uri("https://oauth.ibroadcast.com/device");
        return new IBroadcastCode(
            Str(answer, "device_code"),
            Str(answer, "user_code"),
            page,
            TimeSpan.FromSeconds(Math.Max(1, Num(answer, "interval"))),
            DateTimeOffset.UtcNow.AddSeconds(Num(answer, "expires_in") is > 0 and var s ? s : 180));
    }

    /// <exception cref="MediaServerSignInFailed">The person declined.</exception>
    public static async Task<IBroadcastPoll> PollAsync(HttpClient http, IBroadcastCode code, CancellationToken ct)
    {
        var (status, answer) = await FormAsync(http, "token",
            [("grant_type", "device_code"), ("device_code", code.DeviceCode), ("client_id", ClientId)], ct);
        if (status == HttpStatusCode.OK)
            return new IBroadcastPoll.Approved(GrantOf(answer, null, DateTimeOffset.UtcNow));
        return Str(answer, "error") switch
        {
            "authorization_pending" => new IBroadcastPoll.Pending(code),
            // The device code standard (RFC 8628) asks for 5 seconds more from then on.
            "slow_down" => new IBroadcastPoll.Pending(code with { Interval = code.Interval + TimeSpan.FromSeconds(5) }),
            "expired_token" => new IBroadcastPoll.Expired(),
            "access_denied" => throw new MediaServerSignInFailed(Str(answer, "error_description") is { Length: > 0 } d ? d : null),
            _ => throw new HttpRequestException($"The iBroadcast sign-in answered {(int)status}.", null, status),
        };
    }

    /// <summary>The account the session belongs to: its id, its name, and whether it is Premium.</summary>
    public static async Task<IBroadcastAccount> AccountAsync(HttpClient http, IBroadcastSession session, CancellationToken ct)
    {
        var user = Child(await IBroadcastApi.PostAsync(http, session, IBroadcastApi.Api("status"), "status", ct), "user");
        var id = Str(user, "id") is { Length: > 0 } s ? s : Num(user, "id").ToString(CultureInfo.InvariantCulture);
        var premium = Child(user, "premium");
        return new IBroadcastAccount(id, Str(user, "username"),
            premium.ValueKind == JsonValueKind.True || Num(user, "premium") == 1);
    }

    /// <summary>Ends the sign-in on iBroadcast's side. The refresh token is refused afterwards.</summary>
    public static async Task RevokeAsync(HttpClient http, string refreshToken, CancellationToken ct)
    {
        await FormAsync(http, "revoke", [("refresh_token", refreshToken), ("client_id", ClientId)], ct);
    }

    /// <summary>Sends a form to the sign-in server and reads its JSON answer, whatever the status. An
    /// answer that is not JSON (the revoke answers "OK") reads as an empty object.</summary>
    internal static async Task<(HttpStatusCode Status, JsonElement Answer)> FormAsync(
        HttpClient http, string path, (string Name, string Value)[] fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(OAuth, path))
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))),
        };
        using var response = await http.SendAsync(request, ct);
        ThrowIfOffline(response);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var document = JsonDocument.Parse(text.Length > 0 ? text : "{}");
            return (response.StatusCode, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return (response.StatusCode, default);
        }
    }

    /// <summary>The tokens in a token answer. A renewal that sends no new refresh token keeps
    /// <paramref name="refreshToken"/>.</summary>
    internal static IBroadcastGrant GrantOf(JsonElement answer, string? refreshToken, DateTimeOffset now) => new(
        Str(answer, "access_token"),
        Str(answer, "refresh_token") is { Length: > 0 } fresh ? fresh : refreshToken ?? "",
        now.AddSeconds(Num(answer, "expires_in") is > 0 and var s ? s : 3600));
}

/// <summary>
/// The tokens of one iBroadcast sign-in while an import uses them. It is the only way to get an access
/// token, so a renewal happens in one place: two requests that find the token expired at the same time
/// renew it once. A renewal replaces the refresh token too, and the old one is refused afterwards, so
/// <c>renewed</c> is told every new grant and must keep it (the desktop writes it to the keychain).
/// </summary>
/// <param name="http">For the sign-in server. Not disposed here.</param>
public sealed class IBroadcastSession(HttpClient http, IBroadcastGrant grant, Action<IBroadcastGrant> renewed)
{
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private IBroadcastGrant _grant = grant;

    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The current tokens.</summary>
    public IBroadcastGrant Grant => Volatile.Read(ref _grant);

    /// <summary>An access token to send. A token with less than a minute left is renewed first.</summary>
    /// <exception cref="MediaServerSignInExpired">iBroadcast refused the renewal.</exception>
    public async Task<string> TokenAsync(CancellationToken ct)
    {
        var current = Grant;
        return current.ExpiresAt - Now() > TimeSpan.FromMinutes(1) ? current.AccessToken : await RenewAsync(current.AccessToken, ct);
    }

    /// <summary>Renews the tokens after iBroadcast refused <paramref name="stale"/>. When another request
    /// renewed them meanwhile, the current token is returned without a second renewal, which would be
    /// refused: the first one made the refresh token it would send invalid.</summary>
    /// <exception cref="MediaServerSignInExpired">iBroadcast refused the renewal.</exception>
    public async Task<string> RenewAsync(string stale, CancellationToken ct)
    {
        await _renewing.WaitAsync(ct);
        try
        {
            var current = Grant;
            if (current.AccessToken != stale)
                return current.AccessToken;

            // Not cancelled by the caller once it is sent: iBroadcast may already have spent the old refresh
            // token, and an answer dropped half-way would lose the new one. The client's timeout bounds it.
            var (status, answer) = await IBroadcastSignIn.FormAsync(http, "token",
                [("grant_type", "refresh_token"), ("refresh_token", current.RefreshToken), ("client_id", IBroadcastSignIn.ClientId)], CancellationToken.None);
            if (status == HttpStatusCode.OK && Str(answer, "access_token").Length > 0)
            {
                var fresh = IBroadcastSignIn.GrantOf(answer, current.RefreshToken, Now());
                Volatile.Write(ref _grant, fresh);
                renewed(fresh);
                return fresh.AccessToken;
            }
            if (Str(answer, "error") is "invalid_grant" or "invalid_token")
                throw new MediaServerSignInExpired();
            throw new HttpRequestException($"The iBroadcast sign-in answered {(int)status}.", null, status);
        }
        finally
        {
            _renewing.Release();
        }
    }
}

/// <summary>
/// The JSON requests to iBroadcast's API and library servers. Every request names the client in its body,
/// and an expired token is answered with HTTP 200 and <c>"authenticated": false</c>, not with 401.
/// </summary>
internal static class IBroadcastApi
{
    /// <summary>The version sent with every request.</summary>
    internal static readonly string Version =
        (Assembly.GetEntryAssembly() ?? typeof(IBroadcastApi).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

    internal static readonly string UserAgent = "rpm/" + Version;

    internal static Uri Api(string mode) => new("https://api.ibroadcast.com/" + mode);

    internal static readonly Uri Library = new("https://library.ibroadcast.com/");

    /// <summary>Sends <paramref name="mode"/> to <paramref name="url"/> and reads the answer. A refused token is
    /// renewed once and the request sent again; a second refusal is <see cref="MediaServerSignInExpired"/>.
    /// An answer with <c>"result": false</c> for another reason is an <see cref="HttpRequestException"/>
    /// with the server's message.</summary>
    internal static async Task<JsonElement> PostAsync(HttpClient http, IBroadcastSession session, Uri url, string mode, CancellationToken ct)
    {
        var token = await session.TokenAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["client"] = "rpm",
                    ["version"] = Version,
                    ["device_name"] = "rpm",
                    ["user_agent"] = UserAgent,
                    ["mode"] = mode,
                }, IBroadcastJson.Default.DictionaryStringString), Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            JsonElement answer;
            try
            {
                answer = await JsonAsync(http, request, ct);
            }
            catch (MediaServerSignInExpired) when (attempt == 0)
            {
                token = await session.RenewAsync(token, ct);
                continue;
            }
            if (Child(answer, "authenticated").ValueKind == JsonValueKind.False)
            {
                if (attempt > 0)
                    throw new MediaServerSignInExpired();
                token = await session.RenewAsync(token, ct);
                continue;
            }
            if (Child(answer, "result").ValueKind == JsonValueKind.False)
                throw new HttpRequestException(Str(answer, "message") is { Length: > 0 } m ? m : $"iBroadcast refused {mode}.");
            return answer;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class IBroadcastJson : System.Text.Json.Serialization.JsonSerializerContext;

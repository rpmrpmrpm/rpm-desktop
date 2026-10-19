using System.Net;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RPM.Fingerprint.Catalog;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Catalog;

/// <summary>Whether this user has a Discogs account connected, and whether that is known yet.</summary>
/// <param name="Configured">False when the deployment has no Discogs credentials at all. This is not the
/// same as "you have not connected yet", because the cause is not the user's.</param>
/// <param name="Known">False when nobody has checked yet. This is not the same as "no". Treating an
/// unchecked state as a refusal would leave a connected account out of every search.</param>
public sealed record DiscogsStatus(bool Connected, bool Configured, string? Username, bool Known = true)
{
    /// <summary>Not checked yet: at startup, or after a check that got no answer.</summary>
    public static readonly DiscogsStatus Unknown = new(false, true, null, Known: false);

    /// <summary>Checked, and the deployment reports that Discogs is not set up at all.</summary>
    public static readonly DiscogsStatus Unavailable = new(false, false, null);
}

/// <summary>Discogs has an account connected but is refusing to answer right now.</summary>
public sealed class DiscogsUnavailableException(string message) : Exception(message);

/// <summary>
/// The desktop side of Discogs. It talks to rpm's own services and never directly to Discogs.
///
/// <para>It talks to <i>two</i> services, and the split matters. <b>Connecting</b> an account goes to the
/// accounts service (<c>connections/discogs</c> on accounts.rpmrpmrpm.com). A connected account belongs to
/// the user rather than to rpm, so it is stored with the rest of the user's account, and any rpm product
/// that can use it can. <b>Searching</b> goes to the rpm API. The API uses the stored token and signs each
/// call with its own copy of the consumer secret.</para>
///
/// <para>Signing does not happen here, because it needs the consumer secret, and a secret in a shipped
/// binary would be public. The user connects their <i>own</i> Discogs account. So their searches count
/// against their own rate limit, not one limit shared by every rpm user.</para>
///
/// <para>Answers are passed through as Discogs' own JSON, not reshaped. The parsing is kept on this side,
/// next to the provider that uses it.</para>
/// </summary>
public sealed partial class DiscogsService
{
    private readonly AuthService _auth;
    private readonly HttpClient? _http;
    private readonly HttpClient _accounts;

    public DiscogsService(AuthService auth, IOptions<RPM.Desktop.Models.AppConfig> config)
    {
        _auth = auth;
        _accounts = Wire.Outside(Destination.Service(AuthService.AccountsBase), TimeSpan.FromSeconds(20));

        if (RpmApi.At(config.Value.IdentificationApiUrl) is { } api)
            _http = Wire.Outside(api, TimeSpan.FromSeconds(20));

        // The status follows whoever is signed in, so it changes when they sign in or out. This includes
        // startup, where the session is restored long before anything searches. The status must be right
        // from the start, or a connected account is skipped by every search.
        _auth.Changed += OnAuthChanged;
        if (_auth.IsLoggedIn)
            Shell.Background.Run(() => RefreshStatusAsync());
    }

    /// <summary>The last known status. The provider reads this to decide whether to bother asking. That
    /// check runs on every keystroke, so it must not go to the network.</summary>
    public DiscogsStatus Status { get; private set; } = DiscogsStatus.Unknown;

    /// <summary>
    /// Whether it is worth asking Discogs at all.
    ///
    /// <para>The session is checked first, and that check is certain. Every call is sent with an rpm bearer
    /// token. A signed-out session cannot be answered by anyone, so asking is a request that could never be
    /// sent. Getting this wrong fills the log with <see cref="DiscogsUnavailableException"/>: the search
    /// asks on every keystroke, the style pass asks every two minutes, and each call throws before it
    /// reaches the network.</para>
    ///
    /// <para>Then the connection is checked, where unknown counts as yes. A refusal costs one call that the
    /// search already handles, and the refusal itself gives the answer. Assuming no would remove the user's
    /// Discogs results for the rest of the session.</para>
    /// </summary>
    public bool MayAnswer => MayAsk(_auth.IsLoggedIn, Status);

    /// <summary>The rule itself, separate from the session, so it can be read and tested on its
    /// own.</summary>
    public static bool MayAsk(bool signedIn, DiscogsStatus status) =>
        signedIn && (status.Connected || !status.Known);

    /// <summary>Raised when the connection is made or broken, so Settings can redraw.</summary>
    public event Action? StatusChanged;

    public async Task<DiscogsStatus> RefreshStatusAsync(CancellationToken ct = default)
    {
        Set(await ReadStatusAsync(ct));
        return Status;
    }

    private void OnAuthChanged()
    {
        if (_auth.IsLoggedIn)
            Shell.Background.Run(() => RefreshStatusAsync());
        else
            Set(DiscogsStatus.Unknown);   // The old status was about the previous user, so it is reset.
    }

    private void Set(DiscogsStatus status)
    {
        if (status == Status)
            return;
        Status = status;
        StatusChanged?.Invoke();
    }

    private async Task<DiscogsStatus> ReadStatusAsync(CancellationToken ct)
    {
        try
        {
            var body = await SendAsync(_accounts, HttpMethod.Get, "connections/discogs", ct, learn: false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return new DiscogsStatus(
                root.TryGetProperty("connected", out var c) && c.GetBoolean(),
                root.TryGetProperty("configured", out var f) && f.GetBoolean(),
                root.TryGetProperty("username", out var u) ? u.GetString() : null);
        }
        catch
        {
            // Signed out, offline, or the Lambda has a temporary fault. We were not told no. We failed to
            // ask. Answering "unavailable" here would make one bad moment at startup last for the whole
            // session.
            return DiscogsStatus.Unknown;
        }
    }

    /// <summary>
    /// Starts connecting, and returns the Discogs page to send the user to.
    /// </summary>
    /// <param name="nativeScheme">Our own URL scheme, when the flow can be handed back into the app. If one
    /// is given, the accounts service sends the finished flow to the app, not to a web page. This closes
    /// the sign-in sheet on macOS, so the app does not have to ask the user whether they are done. Null on
    /// Windows and Linux, which finish on the website.</param>
    public async Task<string> StartConnectAsync(string? nativeScheme = null, CancellationToken ct = default)
    {
        var body = await SendAsync(_accounts, HttpMethod.Post, "connections/discogs/start", ct,
            content: JsonContent.Create(
                new StartConnectRequest(nativeScheme), DiscogsServiceJson.Default.StartConnectRequest));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("authorizeUrl", out var url) && url.GetString() is { Length: > 0 } s
            ? s
            : throw new DiscogsUnavailableException("rpm could not start a Discogs authorisation");
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await SendAsync(_accounts, HttpMethod.Delete, "connections/discogs", ct, learn: false);
        Set(Status with { Connected = false, Username = null, Known = true });
    }

    /// <summary>Discogs' own search JSON, proxied by the rpm API. The API holds the consumer secret the
    /// call must be signed with. Null when Discogs is not in a position to answer. This is not a failure,
    /// and no exception is thrown. See <see cref="MayAnswer"/>.</summary>
    public Task<string?> SearchAsync(
        string q, string? artist, ReleaseMedia media, int limit,
        string? catalogNumber = null, string? country = null, uint? year = null,
        string? barcode = null, CancellationToken ct = default)
    {
        if (!MayAnswer)
            return Task.FromResult<string?>(null);

        // q may be empty: a catalogue number or barcode is a complete search on its own.
        var url = $"discogs/search?limit={limit}";
        if (!string.IsNullOrWhiteSpace(q))
            url += $"&q={Uri.EscapeDataString(q)}";
        if (!string.IsNullOrWhiteSpace(artist))
            url += $"&artist={Uri.EscapeDataString(artist)}";
        // The medium as its token. Vinyl is also sent under its older name, which an API that predates the
        // media filter still uses to narrow the one search that always had it.
        if (media.Token() is { } token)
            url += $"&media={token}";
        if (media == ReleaseMedia.Vinyl)
            url += "&vinylOnly=true";
        if (!string.IsNullOrWhiteSpace(catalogNumber))
            url += $"&catno={Uri.EscapeDataString(catalogNumber)}";
        if (!string.IsNullOrWhiteSpace(barcode))
            url += $"&barcode={Uri.EscapeDataString(barcode)}";
        if (!string.IsNullOrWhiteSpace(country))
            url += $"&country={Uri.EscapeDataString(country)}";
        if (year is > 0)
            url += $"&year={year}";
        return Asked(url, ct);
    }

    /// <summary>One artist's releases, for the "Most popular" card on their page. Read by
    /// <see cref="RPM.Core.Metadata.Providers.DiscogsPopular"/>, which does the ordering that Discogs does
    /// not.</summary>
    /// <param name="page">Which page to fetch, starting from 1. The caller must give it, because there is
    /// no default. One page is a slice of a large artist's catalogue. The counts used for ranking are on
    /// the rows themselves, so a caller that does not walk every page would rank whichever hundred records
    /// the endpoint returned first. See DiscogsPopular.PageCount.</param>
    /// <returns>Null when Discogs is not in a position to answer.</returns>
    public Task<string?> ArtistReleasesAsync(string artistId, int page, CancellationToken ct = default) =>
        Asked($"discogs/artists/{Uri.EscapeDataString(artistId)}/releases?limit=100&page={page}", ct);

    /// <summary>One release's JSON, including the tracklist. Null when Discogs is not in a position to
    /// answer.</summary>
    public Task<string?> ReleaseAsync(string id, CancellationToken ct = default) =>
        Asked($"discogs/releases/{Uri.EscapeDataString(id)}", ct);

    /// <summary>
    /// One read of the rpm API's Discogs proxy. When this session cannot be answered, the call is refused
    /// here, not at the socket.
    ///
    /// <para>The refusal returns null, not an exception. Not being signed in, and not having connected an
    /// account, are normal states that a user can stay in for a whole session. A search that skips a
    /// provider it cannot ask is working correctly, and that should not cost an exception on every
    /// keystroke. Discogs itself refusing still throws, for example a 428 or a rate limit. That is an
    /// answer, and the caller may want to tell the user about it.</para>
    /// </summary>
    private async Task<string?> Asked(string url, CancellationToken ct) =>
        MayAnswer ? await SendAsync(Rpm, HttpMethod.Get, url, ct) : null;

    private HttpClient Rpm =>
        _http ?? throw new DiscogsUnavailableException("No rpm API is configured");

    /// <param name="learn">Whether this call's result says anything about the connection. A search that
    /// works shows there is an account behind it. A 428 shows there is not. This is worth recording,
    /// because the status may never have been checked, or may have been checked before the user finished
    /// connecting in their browser. False for calls that ask about the connection themselves.</param>
    private async Task<string> SendAsync(
        HttpClient client, HttpMethod method, string url, CancellationToken ct,
        bool learn = true, HttpContent? content = null)
    {
        var token = await _auth.GetAccessTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            throw new DiscogsUnavailableException("Sign in to rpm to use Discogs");

        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            if (learn)
                Set(Status with { Connected = true, Configured = true, Known = true });
            return body;
        }

        var notConfigured = NotConfigured(
            response.StatusCode, RPM.Core.Net.OnlineGateHandler.WasRefused(response), body);

        if (learn)
        {
            // These two are answers about the account, not transport failures. They settle the question at
            // once, without a second round trip.
            if (response.StatusCode == (HttpStatusCode)428)
                Set(new DiscogsStatus(Connected: false, Configured: true, Username: null));
            else if (notConfigured)
                Set(DiscogsStatus.Unavailable);
        }

        // The Lambda's refusals, converted into results the UI can act on.
        throw new DiscogsUnavailableException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Sign in to rpm to use Discogs",
            (HttpStatusCode)428 => "Connect a Discogs account in Settings first",
            (HttpStatusCode)429 => "Discogs rate limit reached. Try again shortly",
            _ when notConfigured => "Discogs is not set up on this rpm deployment",
            _ => "Discogs did not answer",
        });
    }

    /// <summary>
    /// Whether a response says Discogs is not set up on this deployment. Only the API's own 503 counts,
    /// with its own error message.
    ///
    /// <para>The app's offline check returns a made-up 503, and a temporary Lambda fault can also
    /// return 503. If any 503 counted, one request made while offline would turn Discogs off for the
    /// session and report that it was not set up. So the API's answer is the one with its message in the
    /// body (DiscogsController, "Discogs is not configured on this deployment").</para>
    /// </summary>
    internal static bool NotConfigured(HttpStatusCode status, bool refusedByGate, string body)
        => status == HttpStatusCode.ServiceUnavailable
           && !refusedByGate
           && body.Contains("not configured", StringComparison.OrdinalIgnoreCase);
    /// <summary>The body the accounts API uses to start an authorisation. This is a named type, not an
    /// anonymous one, because a published build is Native AOT and has no metadata for anonymous
    /// types.</summary>
    /// <param name="NativeScheme">Null on Windows and Linux, which finish on the website. See
    /// <see cref="StartConnectAsync"/>. On those platforms the value is always null.</param>
    private sealed record StartConnectRequest(
        [property: JsonPropertyName("nativeScheme")] string? NativeScheme);

    /// <summary>The one body this sends. A published build is Native AOT, which serializes from this and
    /// never by reflection.</summary>
    [JsonSerializable(typeof(StartConnectRequest))]
    private sealed partial class DiscogsServiceJson : JsonSerializerContext;

}

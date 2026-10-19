using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Art;

/// <summary>What the server said when a key was offered to it.</summary>
public enum FanartKeySave
{
    /// <summary>fanart.tv accepted the key and it is saved to the account.</summary>
    Saved,

    /// <summary>fanart.tv refused the key. Nothing was saved.</summary>
    Rejected,

    /// <summary>The server could not reach fanart.tv to check the key. Nothing was saved.</summary>
    Unchecked,

    /// <summary>The server could not be reached, or the user is signed out. Nothing was saved.</summary>
    Failed,
}

/// <summary>Whether a fanart.tv key is saved to the signed-in account.</summary>
/// <param name="Known">False when the server could not be asked (signed out, offline).</param>
public sealed record FanartKeyStatus(bool Connected, string? Last4, bool Known)
{
    public static readonly FanartKeyStatus Unknown = new(false, null, Known: false);
}

/// <summary>
/// The user's fanart.tv personal key, saved to the user's rpm account on the server (<c>profile/fanart-key</c>).
///
/// <para>The app never calls fanart.tv itself. The rpm API asks fanart.tv with RPM's project key for every user.
/// For requests made on this account, the API adds the user's key as <c>client_key</c>. This works from the
/// desktop and from the phone alike. The key only makes new images arrive sooner, after 2 days instead of 7.</para>
///
/// <para>A key left in the local settings file is uploaded once the user is signed in, and then removed from the
/// file. See <see cref="MoveLocalKeyAsync"/>.</para>
/// </summary>
public sealed partial class FanartService
{
    private readonly AuthService _auth;
    private readonly SettingsService _settings;
    private readonly HttpClient? _http;

    /// <summary>
    /// Every write to the account's key goes through this, one at a time. This covers a save, a clear, and the
    /// move of a local key left in the settings file.
    ///
    /// <para>Without it, the move at launch and a save from the settings page can both be in flight. Whichever PUT
    /// lands last wins, so the old key could overwrite the one the user just typed. With this lock, a user's save
    /// first clears the local key and then waits its turn. The move re-reads the local key after its turn comes.
    /// So the user's key is always the last one written, whichever started first.</para>
    /// </summary>
    private readonly SemaphoreSlim _writes = new(1, 1);

    public FanartService(AuthService auth, SettingsService settings, IOptions<RPM.Desktop.Models.AppConfig> config)
    {
        _auth = auth;
        _settings = settings;
        if (RpmApi.At(config.Value.IdentificationApiUrl) is { } api)
            _http = Wire.Outside(api, TimeSpan.FromSeconds(20));

        _auth.Changed += () => Shell.Background.Run(MoveLocalKeyAsync);
        Shell.Background.Run(MoveLocalKeyAsync);
    }

    public async Task<FanartKeyStatus> StatusAsync(CancellationToken ct = default)
    {
        var said = await SendAsync(HttpMethod.Get, content: null, ct);
        return said is null ? FanartKeyStatus.Unknown : new FanartKeyStatus(said.Connected, said.Last4, Known: true);
    }

    /// <summary>The user's own save. It clears any local key first, so a move that has not started yet finds
    /// nothing to move.</summary>
    public async Task<(FanartKeySave Result, FanartKeyStatus Status)> SaveAsync(string key, CancellationToken ct = default)
    {
        _settings.FanartApiKey = null;
        await _writes.WaitAsync(ct);
        try
        {
            return await PutAsync(key, ct);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<FanartKeyStatus> ClearAsync(CancellationToken ct = default)
    {
        _settings.FanartApiKey = null;
        await _writes.WaitAsync(ct);
        try
        {
            var said = await SendAsync(HttpMethod.Delete, content: null, ct);
            return said is null ? FanartKeyStatus.Unknown : new FanartKeyStatus(said.Connected, said.Last4, Known: true);
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task<(FanartKeySave Result, FanartKeyStatus Status)> PutAsync(string key, CancellationToken ct)
    {
        var said = await SendAsync(HttpMethod.Put,
            JsonContent.Create(new KeyRequest(key.Trim()), FanartServiceJson.Default.KeyRequest), ct);
        if (said is null)
            return (FanartKeySave.Failed, FanartKeyStatus.Unknown);

        var result = said.Check switch
        {
            "valid" => FanartKeySave.Saved,
            "rejected" => FanartKeySave.Rejected,
            _ => FanartKeySave.Unchecked,
        };
        return (result, new FanartKeyStatus(said.Connected, said.Last4, Known: true));
    }

    /// <summary>
    /// Uploads a key left in the settings file, then removes it from the file.
    ///
    /// <para>The key is removed only after an answer about the key (saved or refused). If the user is signed out,
    /// offline, or fanart.tv does not answer, the key stays in the file. The next sign-in or launch tries again.</para>
    ///
    /// <para>Startup and a sign-in can both start this. The local key is read again inside <see cref="_writes"/>,
    /// so the second call finds it gone and does nothing. A save from the settings page can arrive at the same
    /// time. See <see cref="_writes"/>.</para>
    ///
    /// <para>Not verified: a sign-out, or a sign-in as someone else, between reading the bearer and the PUT
    /// landing. The PUT carries the bearer that was read. So the key goes to whoever was signed in at that moment.
    /// That is not always the account that saved it in this settings file.</para>
    /// </summary>
    private async Task MoveLocalKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(_settings.FanartApiKey) || !_auth.IsLoggedIn)
            return;

        await _writes.WaitAsync();
        try
        {
            var local = _settings.FanartApiKey;
            if (string.IsNullOrWhiteSpace(local))
                return;

            var (result, _) = await PutAsync(local, CancellationToken.None);
            if (result is FanartKeySave.Saved or FanartKeySave.Rejected
                && string.Equals(_settings.FanartApiKey, local, StringComparison.Ordinal))
                _settings.FanartApiKey = null;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>One call to <c>profile/fanart-key</c>. Returns null when the call could not be made or did not get
    /// an answer.</summary>
    private async Task<KeyResponse?> SendAsync(HttpMethod method, HttpContent? content, CancellationToken ct)
    {
        if (_http is null)
            return null;
        try
        {
            if (await _auth.GetAccessTokenAsync() is not { Length: > 0 } token)
                return null;

            using var request = new HttpRequestMessage(method, "profile/fanart-key") { Content = content };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync(FanartServiceJson.Default.KeyResponse, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record KeyRequest([property: JsonPropertyName("key")] string Value);

    private sealed record KeyResponse(
        [property: JsonPropertyName("connected")] bool Connected,
        [property: JsonPropertyName("last4")] string? Last4,
        [property: JsonPropertyName("check")] string? Check);

    /// <summary>The JSON serialiser for the key requests and answers.
    /// A published build is Native AOT, which serialises from this and never by reflection.</summary>
    [JsonSerializable(typeof(KeyRequest))]
    [JsonSerializable(typeof(KeyResponse))]
    private sealed partial class FanartServiceJson : JsonSerializerContext;
}

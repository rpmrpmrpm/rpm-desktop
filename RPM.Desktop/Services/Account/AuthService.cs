using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Diagnostics;
using RPM.Core.Net;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Account sign-in for rpm, through its own accounts API (accounts.rpmrpmrpm.com). It holds the session,
/// refreshes the access token when it is near expiry, and keeps the session in the <see cref="SessionStore"/>
/// so the user stays signed in across launches. It gates knowledge base submissions and records which
/// account made each one.
/// </summary>
public sealed partial class AuthService
{
    /// <summary>The address of rpm's accounts service. Connected accounts live there too (see
    /// <see cref="DiscogsService"/>), which is why this is not private.</summary>
    public const string AccountsBase = "https://accounts.rpmrpmrpm.com/";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http = Wire.Outside(Destination.Service(AccountsBase), TimeSpan.FromSeconds(20));
    private readonly SessionStore _store;
    private Session? _session;

    /// <param name="store">Where the session is kept. This is required, so a test has to name a store of its
    /// own. A test that fell back to the real store once signed the developer out of their own copy of the app.</param>
    public AuthService(SessionStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Held while the session is replaced and written, and while a sign-out clears and forgets it.
    ///
    /// <para>A refresh checks <see cref="_era"/> and then writes, and both steps run under this lock. Without
    /// the lock, a sign-out could run in full between the check and the end of the write. The era would change,
    /// the session would be cleared and forgotten, and then the write would put the session back in the store.
    /// With the lock, the sign-out's <see cref="SessionStore.Forget"/> waits for a write that is already under
    /// way, so it runs after that write. A refresh that reaches the check after the era changed drops its
    /// answer. The lock is held only for memory and the store, never across a network call or an await. So a
    /// sign-out on the UI thread waits for at most one store write.</para>
    /// </summary>
    private readonly Lock _persisting = new();

    /// <summary>
    /// True while the <see cref="SigningOut"/> handlers are running. This is the one window in which
    /// <see cref="GetAccessTokenAsync"/> must not refresh.
    ///
    /// <para>It is volatile because the handlers are async. They start on the thread that called
    /// <see cref="Logout"/>, and they may continue on another thread. Both threads must see this value.</para>
    /// </summary>
    private volatile bool _signingOut;

    /// <summary>Whether a sign-out is in progress. Used by the test that holds the log closed.</summary>
    internal bool IsSigningOut => _signingOut;

    /// <summary>Puts a session in place without writing it to the store. Tests only.</summary>
    internal void SeedSession(Session s) => _session = s;

    /// <summary>
    /// Only one refresh runs at a time.
    ///
    /// <para>Without this, several requests that arrive together on an expired session each start their own
    /// refresh. Where the server rotates refresh tokens, only the first refresh spends a token that is still
    /// current. The rest are refused, and a refused refresh signs the user out. The symptom is a user who is
    /// signed out at random, on a machine that made only two requests at once.</para>
    /// </summary>
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>Answers the refresh instead of the network. Null in the app. Tests only.</summary>
    internal Func<string, Task<RefreshResponse?>>? RefreshForTest;

    /// <summary>Answers a revocation instead of the network. It returns true when the server answered, and false
    /// when the revocation must be tried again. Null in the app. Tests only.</summary>
    internal Func<string, Task<bool>>? RevokeForTest;

    /// <summary>
    /// Counts the sign-outs. Every sign-out adds one. A refresh reads the count before it starts, and again
    /// before it writes. The same number means that nobody signed out while the refresh was in flight.
    ///
    /// <para>This is a count, not a wait. The alternative is for <c>Logout</c> to block on the refresh's lock.
    /// <c>Logout</c> is called from the UI thread, so that would freeze the interface for as long as an HTTP
    /// call takes. Asking "is this still the session that I set out to refresh?" gives a correct answer
    /// whenever it is asked, and it does not depend on events arriving in order.</para>
    /// </summary>
    private int _era;

    public sealed record Session(
        string UserId, string Email, string? DisplayName,
        string AccessToken, string RefreshToken, DateTime ExpiresUtc);

    /// <summary>Raised (possibly off the UI thread) whenever the sign-in state changes.</summary>
    public event Action? Changed;

    /// <summary>
    /// Raised just before the session is thrown away, while the access token still works.
    ///
    /// <para>This is for the one kind of cleanup that must happen on the way out, not after it. The example is
    /// telling the account that this computer is no longer one of its devices. By the time
    /// <see cref="Changed"/> reports "signed out", no token is left to make that call with.</para>
    /// </summary>
    public event Func<Task>? SigningOut;

    public bool IsLoggedIn => _session is not null;
    public string? Email => _session?.Email;

    /// <summary>The Cognito subject of the person who is signed in, or null. Server-side rows are keyed by it.
    /// It also tells a page which of the reviews on it were written by the reader.</summary>
    public string? UserId => _session?.UserId;
    public string? DisplayName => _session?.DisplayName;

    /// <summary>Restores the saved session at startup (no network), then tells the listeners. A store that does
    /// not answer shows the person as signed out for this run, and it is asked again on the next launch. The
    /// reason is written to the log under "auth".</summary>
    public void Load()
    {
        var read = _store.Load();
        lock (_persisting)
            _session = read is SessionRead.Found found ? found.Session : null;

        // Which store was read tells whether a harness run could see the sign-in kept on this computer
        // (see SessionHome). No account is named here.
        Said.Line("auth", $"session read from the {_store.StoreName}: {(read is SessionRead.Found ? "signed in" : "signed out")}");
        Changed?.Invoke();

        // A sign-out that the server never heard about. It came from a run that had no network, or that quit first.
        if (_store.PendingRevocations().Count > 0)
            Shell.Background.Run(() => RevokePendingAsync([]));
    }

    /// <summary>Signs in with email and password. The reason for a failure, if there is one, is written to the log
    /// under "auth". The outcome carries only what the dialog says.</summary>
    public async Task<SignInOutcome> LoginAsync(string email, string password)
    {
        try
        {
            var res = await _http.PostAsJsonAsync(
                "auth/login", new LoginRequest(email.Trim(), password), AuthJson.Default.LoginRequest);
            if (!res.IsSuccessStatusCode)
            {
                var said = await TryReadError(res);
                Said.Line("auth", $"sign-in refused: {(int)res.StatusCode} {said}");
                return SignInOutcome.FromLoginStatus(res.StatusCode);
            }

            var body = await res.Content.ReadFromJsonAsync(AuthJson.Default.LoginResponse);
            if (body is null || string.IsNullOrEmpty(body.AccessToken))
            {
                Said.Line("auth", "sign-in answered with no token");
                return new SignInOutcome.Refused(SignInRefusal.Other);
            }

            SetSession(new Session(
                body.UserId, body.Email, body.DisplayName,
                body.AccessToken, body.RefreshToken ?? "",
                DateTime.UtcNow.AddSeconds(body.ExpiresIn > 0 ? body.ExpiresIn : 3600)));
            RevokePendingAfterSignIn();
            return new SignInOutcome.SignedIn();
        }
        catch (Exception ex)
        {
            return new SignInOutcome.Failed(Faults.Note("auth", "sign-in", ex));
        }
    }

    /// <summary>
    /// Social sign-in. It asks the accounts API for a one-time session and a browser URL, opens that URL,
    /// and then polls until the website writes the tokens. Only this app can collect them: it keeps the
    /// poll secret and sends only its hash (see <see cref="PollSecret"/>).
    /// </summary>
    public async Task<SignInOutcome> SignInSocialAsync(string provider, CancellationToken ct)
    {
        try
        {
            // appId is the identifier that the accounts backend registered for this app. It must match the
            // backend's allowlist exactly. A value that is not on the list comes back as a session with no
            // tokens. That looks like a silent sign-in failure, not like an error.
            var secret = PollSecret.Make();
            var start = await _http.PostAsJsonAsync(
                "auth/social-login", new SocialLoginRequest(provider, "RPM", PollSecret.HashOf(secret)),
                AuthJson.Default.SocialLoginRequest, ct);
            if (!start.IsSuccessStatusCode)
            {
                Said.Line("auth", $"social sign-in not started: {(int)start.StatusCode} {await TryReadError(start)}");
                return new SignInOutcome.Refused(SignInRefusal.Other);
            }
            var s = await start.Content.ReadFromJsonAsync(AuthJson.Default.SocialStart, ct);
            if (s is null || string.IsNullOrEmpty(s.LoginUrl))
            {
                Said.Line("auth", "social sign-in answered with no login URL");
                return new SignInOutcome.Refused(SignInRefusal.Other);
            }

            await Windows.System.Launcher.LaunchUriAsync(new Uri(s.LoginUrl));

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                if (!await Cancellable.DelayAsync(2000, ct))
                    return new SignInOutcome.Abandoned();
                using var ask = new HttpRequestMessage(
                    HttpMethod.Get, $"auth/poll-session?sessionId={Uri.EscapeDataString(s.SessionId)}");
                ask.Headers.Add(PollSecret.Header, secret);
                using var answer = await _http.SendAsync(ask, ct);

                // 403: the server holds a different hash for this session. It does not happen to this
                // app's own session, so the sign-in is not this app's to finish.
                if (answer.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    Said.Line("auth", "social sign-in refused: the poll secret did not match");
                    return new SignInOutcome.Refused(SignInRefusal.Other);
                }
                answer.EnsureSuccessStatusCode();
                var poll = await answer.Content.ReadFromJsonAsync(AuthJson.Default.PollResponse, ct);
                if (poll is null || poll.Status != "complete") continue;
                if (string.IsNullOrEmpty(poll.AccessToken))
                {
                    Said.Line("auth", "social sign-in completed with no token");
                    return new SignInOutcome.Refused(SignInRefusal.Other);
                }

                SetSession(new Session(
                    poll.UserId ?? "", poll.Email ?? "", poll.DisplayName,
                    poll.AccessToken, poll.RefreshToken ?? "",
                    DateTime.UtcNow.AddSeconds(poll.ExpiresIn > 0 ? poll.ExpiresIn : 3600)));
                RevokePendingAfterSignIn();
                return new SignInOutcome.SignedIn();
            }
            Said.Line("auth", $"social sign-in with {provider} not finished in the browser within 3 minutes");
            return new SignInOutcome.Refused(SignInRefusal.BrowserTimedOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SignInOutcome.Abandoned();
        }
        catch (Exception ex)
        {
            return new SignInOutcome.Failed(Faults.Note("auth", $"sign-in with {provider}", ex));
        }
    }

    public void Logout()
    {
        // This comes first, so that a refresh already in flight sees the change. It then drops its answer
        // instead of writing a session back over this sign-out.
        Interlocked.Increment(ref _era);

        // The handlers get a short time to finish, and no more. Signing out must not wait on a network that may
        // not be there. Whatever does not finish is cleaned up by the account itself, the next time it is asked.
        if (SigningOut is { } leaving)
        {
            // The handlers use the access token, which is the point of the event. One of them makes an HTTP call.
            // Refreshing is turned off while they run. A refresh that fails signs the user out, and signing out is
            // already what is happening here. See _signingOut.
            _signingOut = true;
            try { Task.WhenAll(leaving.GetInvocationList().Cast<Func<Task>>().Select(f => f())).Wait(TimeSpan.FromSeconds(3)); }
            catch { }
            finally { _signingOut = false; }
        }

        List<string> toRevoke;
        lock (_persisting)
        {
            toRevoke = _session is { RefreshToken.Length: > 0 } ending ? [ending.RefreshToken] : [];
            _session = null;
            toRevoke.AddRange(_store.Forget());

            // The token is written down before it is sent. Deleting the session only removes this computer's copy,
            // not the token. A copy kept elsewhere, such as a backup of the auth.json that earlier builds wrote,
            // would still refresh for up to 30 days. A run that quits, or has no network, before the server answers
            // leaves the token on this list for the next launch or sign-in.
            if (toRevoke.Count > 0)
                _store.AddRevocations(toRevoke);
        }
        Changed?.Invoke();

        // Not awaited. Signing out does not wait for the network (see the comment above SigningOut).
        if (toRevoke.Count > 0)
            Shell.Background.Run(() => RevokePendingAsync(toRevoke));
    }

    /// <summary>After a sign-in, the network is known to work. That is the moment to send what an offline
    /// sign-out left behind.</summary>
    private void RevokePendingAfterSignIn()
    {
        if (_store.PendingRevocations().Count > 0)
            Shell.Background.Run(() => RevokePendingAsync([]));
    }

    /// <summary>
    /// Sends every token from <see cref="SessionStore.PendingRevocations"/>, and the tokens in
    /// <paramref name="alsoThese"/> even when the store could not hold them, to <c>auth/revoke</c>. It then
    /// removes the tokens that were answered from the list.
    ///
    /// <para>Several of these can run at once. A sign-out, a launch and a sign-in can each start one. Revoking a
    /// token twice is harmless. The list is changed only under the store's lock, and the run removes the tokens
    /// it sent. It does not write back the list it read. So a token that was added in the meantime is kept.</para>
    /// </summary>
    internal async Task RevokePendingAsync(IReadOnlyCollection<string> alsoThese)
    {
        var tokens = _store.PendingRevocations().Union(alsoThese).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0)
            return;

        var answered = new List<string>();
        foreach (var token in tokens)
        {
            if (await RevokeOnServerAsync(token).ConfigureAwait(false))
                answered.Add(token);
        }

        if (answered.Count > 0)
            _store.RemoveRevocations(answered);
        Said.Line("auth", $"revoked {answered.Count} of {tokens.Count} signed-out session(s)");
    }

    /// <summary>
    /// One <c>auth/revoke</c> call. It returns true when the server answered and the token needs nothing more.
    /// That is a 2xx, or a 4xx that sending again would not change. It returns false for a 404 (an accounts service
    /// older than <c>auth/revoke</c>), for a 5xx (Cognito not answering, or the network gate's own 503), and when
    /// there is no answer at all. In those cases the token stays on the list.
    /// </summary>
    private async Task<bool> RevokeOnServerAsync(string refreshToken)
    {
        if (RevokeForTest is { } answer)
            return await answer(refreshToken).ConfigureAwait(false);

        try
        {
            using var res = await _http.PostAsJsonAsync(
                "auth/revoke", new RefreshRequest(refreshToken), AuthJson.Default.RefreshRequest)
                .ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (res.IsSuccessStatusCode)
                return true;
            if (status == 404 || status >= 500)
            {
                Said.Line("auth", $"revoke not answered: {status}");
                return false;
            }
            Said.Line("auth", $"revoke refused: {status}");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Said.Line("auth", $"revoke not sent: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Asks the accounts service to delete the signed-in account. This is only the server's part. It is called
    /// through <see cref="AccountDeletion"/>, which then signs out and resets the library, in that order.
    /// </summary>
    /// <param name="typedEmail">What the person typed to confirm. The server compares it with the account's
    /// verified address, and it refuses a mismatch.</param>
    internal async Task<AccountDeletionOutcome> DeleteAccountOnServerAsync(string typedEmail, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync();
        if (token is null) return AccountDeletionOutcome.Failed;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "auth/account")
            {
                Content = JsonContent.Create(
                    new DeleteAccountRequest(typedEmail.Trim()), AuthJson.Default.DeleteAccountRequest),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var res = await _http.SendAsync(request, ct);
            return DeletionAnswer((int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AccountDeletionOutcome.Failed;
        }
    }

    /// <summary>What the accounts service's answer to <c>DELETE auth/account</c> means. 204 means the account is
    /// deleted. 400 means the typed address does not match the account's verified address, or the account has none.
    /// Any other answer means the request did not get through, and nothing was deleted.</summary>
    internal static AccountDeletionOutcome DeletionAnswer(int status) => status switch
    {
        >= 200 and < 300 => AccountDeletionOutcome.Deleted,
        400 => AccountDeletionOutcome.EmailRefused,
        _ => AccountDeletionOutcome.Failed,
    };

    /// <summary>A valid access token for the API. It refreshes the token first if the token is about to expire.
    /// It returns null when nobody is signed in.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        // Read the session once. Everything below decides based on this one value. A refresh on another thread
        // can replace the session at any point.
        var session = _session;
        if (session is null) return null;

        // While signing out, hand over the token that is there and do not refresh. A failed refresh calls Logout,
        // Logout raises SigningOut, and a SigningOut handler is what asked for this token. Refreshing here would
        // start a loop that never ends and finally crashes the app. On a machine that has just woken from sleep,
        // the network gate refuses every request without going to the network. So no await ever yields, and the
        // loop runs on one stack until that stack overflows.
        //
        // This check must stay ABOVE the wait below. The refresh holds _refreshing while it runs, and a failed
        // refresh calls Logout from inside that lock. If a SigningOut handler that asks for a token reached the
        // wait, it would wait for the refresh that is calling it. Returning here prevents that deadlock.
        if (_signingOut) return session.AccessToken;

        if (DateTime.UtcNow < session.ExpiresUtc - RefreshMargin) return session.AccessToken;

        if (string.IsNullOrEmpty(session.RefreshToken)) return session.AccessToken; // best effort

        await _refreshing.WaitAsync().ConfigureAwait(false);
        try
        {
            // Checked again under the lock. The caller who held the lock may have just refreshed. A second refresh
            // would spend a refresh token that the first one already replaced. Where the server rotates refresh
            // tokens, that call fails, and a failed refresh signs the user out. That is a sign-out for no reason,
            // on a machine that only made two requests at once.
            var current = _session;
            if (current is null) return null;
            if (_signingOut) return current.AccessToken;
            if (DateTime.UtcNow < current.ExpiresUtc - RefreshMargin) return current.AccessToken;
            if (string.IsNullOrEmpty(current.RefreshToken)) return current.AccessToken;

            var era = Volatile.Read(ref _era);
            try
            {
                var r = await FetchRefreshAsync(current.RefreshToken).ConfigureAwait(false);
                if (r is not null && !string.IsNullOrEmpty(r.AccessToken))
                {
                    var refreshed = current with
                    {
                        AccessToken = r.AccessToken,
                        ExpiresUtc = DateTime.UtcNow.AddSeconds(r.ExpiresIn > 0 ? r.ExpiresIn : 3600),
                    };

                    // The person may have signed out while the refresh was in flight. Writing the session now would
                    // sign them back in, and it would write to the store after Logout had already forgotten it. The
                    // check and the write are one step under _persisting, so a sign-out cannot land between them.
                    lock (_persisting)
                    {
                        if (Volatile.Read(ref _era) != era) return null;
                        _session = refreshed;
                        _store.Save(refreshed);
                    }
                    Changed?.Invoke();
                    return r.AccessToken;
                }
            }
            catch
            {
                // The problem is the network, not the account, so the session may well still be good. Hand back the
                // token that is there, and leave signing out to an answer that actually says no. Return null if a
                // sign-out happened in the meantime, because then there is no session left to speak for.
                return Volatile.Read(ref _era) == era ? _session?.AccessToken : null;
            }

            // The server refused the refresh token (400, 401 or 403, see IsRefusal). The session is over, so sign out.
            // The UI then asks the person to sign in again.
            Logout();
            return null;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    /// <summary>
    /// The refresh request. It is kept apart so that a test can answer it without a network. That is the only way
    /// to count how many refreshes a burst of callers makes.
    ///
    /// <para>A null result means the server refused the refresh token, and the caller then signs out. Any other
    /// failed status throws. The caller treats that like a network failure and keeps the session. A 502 from a cold
    /// Lambda, or a 503 from Cognito, says nothing about the token. The same goes for the 503 that
    /// <c>OnlineGateHandler</c> makes up without going to the network while it believes the connection is down.
    /// Such a 503 must not sign the user out, because it is not a statement about the token. Otherwise the
    /// first refresh after a laptop wakes from sleep would sign the user out.</para>
    /// </summary>
    private async Task<RefreshResponse?> FetchRefreshAsync(string refreshToken)
    {
        if (RefreshForTest is { } answer) return await answer(refreshToken).ConfigureAwait(false);

        var res = await _http.PostAsJsonAsync(
            "auth/refresh", new RefreshRequest(refreshToken), AuthJson.Default.RefreshRequest)
            .ConfigureAwait(false);

        if (res.IsSuccessStatusCode)
            return await res.Content.ReadFromJsonAsync(AuthJson.Default.RefreshResponse).ConfigureAwait(false);

        if (IsRefusal(res.StatusCode)) return null;

        throw new HttpRequestException(
            $"auth/refresh answered {(int)res.StatusCode}", inner: null, res.StatusCode);
    }

    /// <summary>The statuses that mean the refresh token itself was refused. Only these sign the user out.</summary>
    internal static bool IsRefusal(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden;

    /// <summary>Stores a new sign-in. It replaces any session that was there, and writes the new one to the store.</summary>
    private void SetSession(Session s)
    {
        lock (_persisting)
        {
            _session = s;
            _store.Save(s);
        }
        Changed?.Invoke();
    }

    private static async Task<string?> TryReadError(HttpResponseMessage res)
    {
        try
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private sealed record LoginResponse(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);

    internal sealed record RefreshResponse(
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);

    private sealed record SocialStart(
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("loginUrl")] string LoginUrl);

    private sealed record PollResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("refreshToken")] string? RefreshToken,
        [property: JsonPropertyName("expiresIn")] int ExpiresIn);
    // ── request shapes ────────────────────────────────────────────────
    //
    // These are named types, not anonymous objects. A published build is Native AOT, and it has no metadata
    // for an anonymous type. The field names are the API's own names, written out in full.

    private sealed record LoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("password")] string Password);

    private sealed record SocialLoginRequest(
        [property: JsonPropertyName("provider")] string Provider,
        [property: JsonPropertyName("appId")] string AppId,
        [property: JsonPropertyName("pollHash")] string PollHash);

    private sealed record RefreshRequest(
        [property: JsonPropertyName("refreshToken")] string? RefreshToken);

    private sealed record DeleteAccountRequest(
        [property: JsonPropertyName("confirmEmail")] string ConfirmEmail);

    /// <summary>The shapes of the account API, as the source generator describes them. The shape of the stored
    /// session is in <see cref="SessionStore"/>.</summary>
    [JsonSerializable(typeof(LoginRequest))]
    [JsonSerializable(typeof(LoginResponse))]
    [JsonSerializable(typeof(SocialLoginRequest))]
    [JsonSerializable(typeof(SocialStart))]
    [JsonSerializable(typeof(PollResponse))]
    [JsonSerializable(typeof(RefreshRequest))]
    [JsonSerializable(typeof(RefreshResponse))]
    [JsonSerializable(typeof(DeleteAccountRequest))]
    private sealed partial class AuthJson : JsonSerializerContext;

}

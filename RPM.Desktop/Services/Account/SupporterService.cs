using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using RPM.Core.Net;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Whether this account supports rpm. The advanced recognition layer depends on this answer.
///
/// <para>The app asks before it offers the layer. It does not find out by being refused halfway through
/// a run. The server makes the real decision either way. This class only lets the app know first.</para>
/// </summary>
public sealed partial class SupporterService
{
    private static readonly HttpClient Http = Wire.Outside(Destination.Service(AuthService.AccountsBase), TimeSpan.FromSeconds(100));

    private readonly AuthService _auth;
    private readonly Shell.SettingsService _settings;

    public SupporterService(AuthService auth, Shell.SettingsService settings)
    {
        _auth = auth;
        _settings = settings;

        // Support belongs to an account, so the answer must change when the account changes. Without this,
        // a signed-out machine kept the last supporter's standing. It would keep the gold backdrop, and run
        // the DJ and word alignment, for nobody.
        _auth.Changed += OnAccountChanged;
    }

    /// <summary>
    /// Someone signed in or out, so nothing known about the previous account applies any more.
    ///
    /// <para>Both directions clear the state, including the remembered end date. That date is the one thing
    /// here that outlives a session. It keeps a supporter on a train from being refused what they paid for.
    /// It is stored per machine. If it stayed after a sign-out, the next account would get the previous
    /// account's support until the server was asked. Clearing it loses nothing. Signing in needs the
    /// network, so the standing is read again straight after. The offline fallback is for a session that
    /// stays signed in.</para>
    /// </summary>
    private void OnAccountChanged()
    {
        Current = null;
        _settings.SupporterUntilUnix = 0;
        _settings.SupporterTester = false;
        Changed?.Invoke();

        // Ask about whoever has just arrived. Do it here, not on whichever screen opens first. The standing
        // decides what is shown behind the whole window. A supporter who signs in and sees the default
        // backdrop until they open Cloud settings has had their support applied in the wrong order.
        if (_auth.IsLoggedIn)
            Shell.Background.Run(() => GetAsync());
    }

    /// <param name="Supporter">The supporter features. The server sends true for paying supporters and
    /// for closed beta testers.</param>
    /// <param name="Tester">A closed beta tester who does not pay. False from a server that does not send
    /// the field.</param>
    private sealed record MeBody(
        [property: JsonPropertyName("grSupporter")] bool Supporter,
        [property: JsonPropertyName("grSupporterUntil")] long SupporterUntil,
        [property: JsonPropertyName("grTester")] bool Tester = false);

    /// <summary>
    /// What is known about the account's support. Null means it could not be read at all (signed out, or
    /// offline). The UI shows that as unknown, not as "no". Saying "no" without the user having been given
    /// a real refusal would be a false claim.
    /// </summary>
    /// <param name="IsSupporter">Whether the account has the supporter features. Paying supporters and
    /// closed beta testers have them.</param>
    /// <param name="IsTester">A closed beta tester: the supporter features, 100 GB of cloud storage, no end
    /// date and no supporter's mark. <see cref="SupporterReach"/> and the settings pages read it.</param>
    public sealed record Status(bool IsSupporter, DateTimeOffset? Until, bool IsTester);

    /// <summary>The last answer, or null when there has not been one this session. Anything that wants to
    /// draw the standing without waiting for a network call reads this. The cloud settings page draws from
    /// this, and then refreshes it.</summary>
    public Status? Current { get; private set; }

    /// <summary>
    /// The standing to act on, as opposed to the standing to draw.
    ///
    /// <para>This session's answer, if there is one. Otherwise it is the last answer the server gave on this
    /// machine. That answer is kept in settings and honoured until its date passes. This fallback lets a
    /// supporter run the DJ and word alignment on a train. Those features are limited here, not by a server,
    /// so "could not ask" must not count as "no". See <see cref="SupporterReach"/>.</para>
    ///
    /// <para><b>Signed out is not "could not ask".</b> Signed out means there is nobody to ask about. Support
    /// belongs to an account, and a session with no account has none, whatever the last account bought. The
    /// remembered date is cleared on sign-out too (see <see cref="OnAccountChanged"/>). This is the second half
    /// of the same rule. So no order of events can leave a signed-out window showing gold with the DJ switched
    /// on.</para>
    ///
    /// <para>It is null for an account that nobody has ever been told about, and for no account at all. This
    /// differs from <see cref="Current"/>. <see cref="Current"/> stays null until this session has really heard
    /// back. The settings page draws from <see cref="Current"/>, so it can show "unknown" and not claim a
    /// standing that it is only remembering. The gate uses this property, so it lets a remembered supporter
    /// through.</para>
    /// </summary>
    public Status? Standing =>
        Pretended ?? StandingFrom(
            _auth.IsLoggedIn, Current, _settings.SupporterUntilUnix, _settings.SupporterTester, DateTimeOffset.UtcNow);

#if DEBUG
    /// <summary>
    /// A standing to act on, used instead of the account's own. It lets both sides of each gate be tested
    /// without buying support and cancelling it again. Null uses the real account.
    ///
    /// <para>This works in Debug builds only. It is here, not at each gate, because every locally enforced
    /// feature reads <see cref="Standing"/>. One switch moves all of them, and none of them needs to know it
    /// exists. It cannot change the cloud bitrate. That is decided in the Lambda from the caller's real
    /// token, and nothing on this machine can change that decision.</para>
    /// </summary>
    public bool? Pretend
    {
        get => _settings.SupporterPretend switch { 1 => true, 2 => false, _ => null };
        set
        {
            _settings.SupporterPretend = value switch { true => 1, false => 2, _ => 0 };

            // This raises the same event that a real answer raises. Every screen that follows the standing
            // then follows this change too. There is no second path to keep in step.
            Changed?.Invoke();
        }
    }
#endif

    /// <summary>
    /// The pretended standing, or null when there is none. There is always none outside a debug build. A
    /// pretended supporter gets an end date one year away, so nothing downstream sees the support as about
    /// to run out.
    ///
    /// <para><b>Only over an account.</b> The pretend switch stands in for the server's answer about the
    /// person who is signed in. When nobody is signed in, there is nobody to answer about. So the switch
    /// pretends nothing, and the signed-out path is the real one. Without this check, the switch would
    /// defeat the very gate that a tester signs out to check. The window would stay gold with no account
    /// behind it, a state the app cannot actually reach.</para>
    /// </summary>
    private Status? Pretended =>
#if DEBUG
        _auth.IsLoggedIn && Pretend is { } pretend
            ? new Status(pretend, pretend ? DateTimeOffset.UtcNow.AddYears(1) : null, IsTester: false)
            : null;
#else
        null;
#endif

    /// <summary>
    /// The rule behind <see cref="Standing"/>, with both of its inputs passed in: what this session heard,
    /// and what the last session wrote down. It is kept separate, so the rule can be read and tested without
    /// a settings file, a token or a network. It decides whether a supporter on a train keeps what they paid for.
    /// </summary>
    /// <param name="signedIn">Whether there is an account at all. There is no default. Support belongs to an
    /// account, so every caller must say whether there is one. A caller that forgot would give a signed-out
    /// window the last supporter's gold, DJ and word alignment.</param>
    /// <param name="asked">This session's answer from the server, or null if it has not heard back.</param>
    /// <param name="rememberedUntilUnix">The end date the server last gave on this machine. 0 means both
    /// "not a supporter" and "never told". For this rule, the two are the same.</param>
    /// <param name="rememberedTester">Whether the server last said this account is a closed beta tester.
    /// A tester has no end date, so the mark is remembered on its own and kept until the server says
    /// otherwise or the account changes.</param>
    public static Status? StandingFrom(
        bool signedIn, Status? asked, long rememberedUntilUnix, bool rememberedTester, DateTimeOffset now)
    {
        // Nobody is signed in, so there is nobody to be a supporter. This is not the same as "could not ask".
        // The remembered date below covers that case: a session that has an account but cannot reach the server.
        if (!signedIn)
            return null;

        if (asked is { } fresh)
            return fresh;

        if (rememberedTester)
            return new Status(true, null, IsTester: true);

        if (rememberedUntilUnix <= 0)
            return null;

        // Expired support is kept as an answer, not as no answer. That tells an account that has lapsed apart
        // from one nobody has heard of. So a lapsed supporter is refused straight away, and is not let through
        // until the next successful read.
        var ends = DateTimeOffset.FromUnixTimeSeconds(rememberedUntilUnix);
        return new Status(ends > now, ends, IsTester: false);
    }

    /// <summary>Raised when <see cref="Current"/> changes, so a page that shows it can follow.</summary>
    public event Action? Changed;

    public async Task<Status?> GetAsync(CancellationToken ct = default)
    {
        // Who is being asked about. This is checked again when the answer arrives, because the two moments
        // differ. If the user signs out while the request is in flight, this would write the leaving account's
        // support over the empty state that sign-out just wrote. That would show gold for nobody. It is the same
        // guard, for the same reason, as PeerFaces.FetchSelfAsync.
        var asked = _auth.UserId;

        try
        {
            if (await _auth.GetAccessTokenAsync() is not { Length: > 0 } token)
                return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, "auth/me");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var body = await resp.Content.ReadFromJsonAsync(SupporterServiceJson.Default.MeBody, ct);
            if (body is null) return null;

            if (!string.Equals(_auth.UserId, asked, StringComparison.Ordinal))
                return null;

            return Remember(new Status(
                body.Supporter,
                body.SupporterUntil > 0 ? DateTimeOffset.FromUnixTimeSeconds(body.SupporterUntil) : null,
                body.Supporter && body.Tester));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the answer, and raises <see cref="Changed"/> if the answer is new.
    ///
    /// <para>A failed read is never remembered as "no". <see cref="Current"/> holds the last thing the server
    /// actually said. The UI shows unknown when it is null, not a stale denial.</para>
    /// </summary>
    private Status Remember(Status status)
    {
        var news = Current != status;
        Current = status;

        // Kept for the next run. It is written only from something the server said. A supporter with no end
        // date is not a shape that this account model produces, because 0 is how "not a supporter" is written.
        // So an answer with no end date is remembered as nothing, not as support that never expires.
        _settings.SupporterUntilUnix = status is { IsSupporter: true, IsTester: false, Until: { } ends }
            ? ends.ToUnixTimeSeconds()
            : 0;
        _settings.SupporterTester = status.IsTester;

        if (news) Changed?.Invoke();
        return status;
    }
    /// <summary>What the account API says about this user.
    /// A published build is Native AOT, which serializes from this, and never by reflection.</summary>
    [JsonSerializable(typeof(MeBody))]
    private sealed partial class SupporterServiceJson : JsonSerializerContext;

}

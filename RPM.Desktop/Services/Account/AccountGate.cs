using Microsoft.Extensions.Options;

namespace RPM.Desktop.Services.Account;

/// <summary>What an errand that has to go through the rpm API is allowed to do right now.</summary>
public enum AccountAccess
{
    /// <summary>Signed in, and there is an API to talk to. Go ahead.</summary>
    Open,

    /// <summary>There is an API, but nobody is signed in. Only the user can fix this, so the answer
    /// is the login dialog, not a dead window.</summary>
    SignInRequired,

    /// <summary>This build names no rpm API at all. The user cannot fix this. It is different from
    /// the case above, which signing in fixes.</summary>
    NotConfigured,
}

/// <summary>
/// The single entry point for everything the rpm API does for a signed-in user: the catalogue, the artwork
/// search, identification, and Discogs behind them.
///
/// <para><b>Why this is a type.</b> Each of those services already fails quietly when there is no
/// session. It returns an empty answer or throws, and nothing on screen says why. So a signed-out user
/// sees a lookup window that searches forever and finds nothing, and a cover search with no covers.
/// The log fills with <c>DiscogsUnavailableException</c> from the passes that keep asking. One fact
/// makes all of those wrong. It is known locally, and this gate asks it, so a caller cannot forget to
/// ask.</para>
///
/// <para><b>Asked, never cached.</b> Sign-in changes at any time. A session restored at startup can
/// arrive after the first window is up, and the user can sign out from the menu at any moment. So the
/// answer is computed on every read. <see cref="Changed"/> tells whoever drew a button that it needs
/// drawing again.</para>
/// </summary>
public sealed class AccountGate
{
    private readonly AuthService _auth;

    public AccountGate(AuthService auth, IOptions<Models.AppConfig> config)
    {
        _auth = auth;
        // The same order of sources that the services behind this gate use: the dev override first, the
        // shipped deployment second. Read once. An env var does not change while the process runs, and
        // appsettings is not reloaded.
        HasApi = RPM.Core.Net.RpmApi.IsConfigured(config.Value.IdentificationApiUrl);
        _auth.Changed += () => Changed?.Invoke();
    }

    /// <summary>Whether this run has an rpm API to talk to at all.</summary>
    public bool HasApi { get; }

    /// <summary>Raised (possibly off the UI thread) when the answer may have changed.</summary>
    public event Action? Changed;

    /// <summary>What the API will do for this user right now.</summary>
    public AccountAccess Access => Decide(_auth.IsLoggedIn, HasApi);

    /// <summary>Whether an online request may simply run. This is the short form of <see cref="Access"/>,
    /// for callers that have nothing to say about the other two answers.</summary>
    public bool IsOpen => Access == AccountAccess.Open;

    /// <summary>The rule itself. It is kept apart from the session and the container, so it can be read
    /// and tested as the sentence it is.</summary>
    public static AccountAccess Decide(bool signedIn, bool hasApi) =>
        !hasApi ? AccountAccess.NotConfigured
        : signedIn ? AccountAccess.Open
        : AccountAccess.SignInRequired;
}

using RPM.Core.Identify;
using Microsoft.Extensions.Options;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Chooses the identification backend for this run, in priority order:
///
/// 1. The RPM_API_URL environment variable: the Lambda at that URL. This is a development override,
///    for example a local instance.
/// 2. AppConfig.IdentificationApiUrl (appsettings.json): the deployed rpm Lambda (own fingerprint
///    database, then AcoustID, then AudD using rpm credits). This is the production default. It needs
///    an rpm sign-in; <see cref="SignInRequired"/> tells the UI to ask for one.
///
/// <para>Both use the same entry point. The client never calls AcoustID or MusicBrainz directly. A client
/// that can reach a catalogue directly would eventually do so on a user's machine, outside any rate limit.
/// The development override covers the same need: point it at a local API and the whole chain runs, the
/// same as in production.</para>
/// </summary>
public sealed class IdentificationProvider
{
    private readonly AuthService _auth;
    private readonly bool _usesRpmAuth;

    /// <summary>Raised when availability may have flipped (sign-in / sign-out).</summary>
    public event Action? AvailabilityChanged;

    public IdentificationProvider(AuthService auth, IOptions<RPM.Desktop.Models.AppConfig> config)
    {
        _auth = auth;
        _auth.Changed += () => AvailabilityChanged?.Invoke();

        if (RPM.Core.Net.RpmApi.At(config.Value.IdentificationApiUrl) is { } api)
        {
            Backend = CreateLambdaBackend(api);
            _usesRpmAuth = true;
        }
    }

    private LambdaIdentificationBackend CreateLambdaBackend(RPM.Core.Net.Destination api) =>
        new(RPM.Core.Net.TransientHttp.Client(api, TimeSpan.FromSeconds(100)),
            ct => _auth.GetAccessTokenAsync());

    /// <summary>Null when no identification source is configured.</summary>
    public IIdentificationBackend? Backend { get; }

    public bool IsAvailable => Backend is not null;

    /// <summary>True when the backend is the rpm Lambda but nobody is signed in. Every lookup would fail,
    /// so the UI should ask for a sign-in instead of offering the button.</summary>
    public bool SignInRequired => _usesRpmAuth && !_auth.IsLoggedIn;
}

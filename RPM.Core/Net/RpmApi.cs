namespace RPM.Core.Net;

/// <summary>
/// Where the RPM API is, and the key this app presents to it. Asking for the API and getting the key are
/// one operation: <see cref="At"/> is the only thing that makes a <see cref="Destination"/> carrying the
/// key, so a client cannot end up pointed at the API without one.
/// </summary>
public static class RpmApi
{
    /// <summary>The header the API reads the key from. Matches AppKeyHandler.HeaderName.</summary>
    public const string KeyHeader = "X-RPM-Key";

    /// <summary>
    /// The key compiled into this build. It ships inside the binary and can be read back out of it, so
    /// treat it as a filter on casual traffic rather than as a secret: what bounds what the API costs is
    /// the reserved concurrency and the edge cache in front of it, not this value. The server holds two
    /// keys at once, so replacing this one in a later build does not refuse the builds already installed.
    ///
    /// <para>RPM_APP_KEY overrides it, for pointing a local build at a deployed API. Debug builds only: a
    /// Release build of the app reads no RPM_* switch (see DevSwitch).</para>
    /// </summary>
    public static string Key =>
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_APP_KEY") is { Length: > 0 } supplied ? supplied : Shipped;

    // The key the deployed API accepts. It is compiled in rather than fetched, because it must exist before
    // the first request.
    private const string Shipped = "rpm_live_FEvcaWAmKYVQIOi8EbDUD98kgpkpbT9r";

    /// <summary>The host the deployed API answers on. Named here so <see cref="Owns"/> can recognise it in
    /// an address that came from somewhere else, which is the check that keeps the API from being reached
    /// as an ordinary service.</summary>
    private const string DeployedHost = "api.rpmrpmrpm.com";

    /// <summary>
    /// The API as a destination, or null when this build has no API to talk to.
    ///
    /// <para>The dev override is checked first and the deployed address second. Callers do not need to hold
    /// the address as a string to reach the API.</para>
    /// </summary>
    /// <param name="configured">The address from appsettings, which RPM_API_URL overrides in a Debug build
    /// (see DevSwitch).</param>
    public static Destination? At(string? configured)
        => Address(configured) is { } url ? Destination.Api(url) : null;

    /// <summary>The deployed API, for the clients that cannot work without one, so they have no
    /// "not configured" state. The address is written once here, not as a literal at each call
    /// site.</summary>
    public static Destination Deployed => Destination.Api($"https://{DeployedHost}/");

    /// <summary>
    /// The API's base address as a string, ending in a slash. Use it for callers that need to BUILD a url
    /// rather than aim a client at one.
    ///
    /// <para>It uses the same order as <see cref="At"/>: the dev override, then the deployed address. It
    /// never returns null, because a caller that builds a url cannot do anything useful with null. A build
    /// with no API configured still gets the deployed address.</para>
    /// </summary>
    public static string BaseUrl(string? configured = null)
        => (Address(configured) ?? $"https://{DeployedHost}/").TrimEnd('/') + "/";

    /// <summary>Whether this build has an API at all. The same question <see cref="At"/> answers, for the
    /// callers that only need to know whether to offer something.</summary>
    public static bool IsConfigured(string? configured) => Address(configured) is not null;

    private static string? Address(string? configured)
        => RPM.Core.Diagnostics.DevSwitch.Read("RPM_API_URL") is { Length: > 0 } supplied ? supplied
         : string.IsNullOrWhiteSpace(configured) ? null
         : configured;

    /// <summary>
    /// Whether an address names this app's API. Host only for the deployed one, host and port for a local
    /// override, because a dev API is told apart from anything else on that machine by its port.
    /// </summary>
    /// <remarks>Public because the picture fetcher is outside this assembly and has to ask: it is aimed at
    /// nothing in particular, so it fits the key per request rather than per client. See
    /// <c>RemoteImage.FitKey</c>.</remarks>
    public static bool Owns(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.Equals(DeployedHost, StringComparison.OrdinalIgnoreCase))
            return true;

        return RPM.Core.Diagnostics.DevSwitch.Read("RPM_API_URL") is { Length: > 0 } supplied
            && Uri.TryCreate(supplied, UriKind.Absolute, out var local)
            && uri.Host.Equals(local.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == local.Port;
    }
}

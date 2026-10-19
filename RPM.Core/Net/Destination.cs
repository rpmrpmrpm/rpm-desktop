namespace RPM.Core.Net;

/// <summary>
/// Where a client is pointed, as a typed value rather than a plain string.
///
/// <para>A plain string could hold the address of this app's own API. A client aimed at the API that way
/// sent no app key, and the endpoints that name no user refused it. The compiler could not catch it,
/// because any string is accepted.</para>
///
/// <para>So the address is a value that says which kind it is. The API address can only be made by
/// <see cref="RpmApi.At"/>, which also adds the key. The address and the key cannot come apart.</para>
///
/// <para><see cref="Service"/> throws on the API's own address, because a caller cannot be trusted to check
/// it. The address can be read from configuration, and a string can be passed anywhere. Every client is
/// built at startup, so this fault shows up on the first run of the app.</para>
/// </summary>
public readonly struct Destination
{
    private readonly string? _baseUrl;
    private readonly bool _carriesKey;

    private Destination(string baseUrl, bool carriesKey)
    {
        _baseUrl = baseUrl;
        _carriesKey = carriesKey;
    }

    /// <summary>No base address. Each request gives its own absolute URL. The download clients use this,
    /// because the download address comes back inside the answer that named it.</summary>
    public static Destination Unaddressed => default;

    /// <summary>A service that is not this app's API: the accounts service, a radio directory, a lyrics
    /// host.</summary>
    /// <exception cref="ArgumentException">The address is this app's API. It is reached through
    /// <see cref="RpmApi.At"/>, which is the only thing that fits the app key.</exception>
    public static Destination Service(string baseUrl)
    {
        if (RpmApi.Owns(baseUrl))
            throw new ArgumentException(
                $"{baseUrl} is the rpm API. Build the client from RpmApi.At, which fits the app key. "
                + "A client aimed here without one is refused by every endpoint that names no user.",
                nameof(baseUrl));

        return new Destination(baseUrl, carriesKey: false);
    }

    /// <summary>The API, with the key. Made by <see cref="RpmApi.At"/> and nothing else.</summary>
    internal static Destination Api(string baseUrl) => new(baseUrl, carriesKey: true);

    /// <summary>
    /// Points a client here, and fits what the address requires.
    ///
    /// <para>Public because a cached client is built outside <see cref="Wire"/> and still has to be aimed.
    /// It is safe to expose because an address and the key that belongs with it are one value: there is no
    /// way to aim at the API and leave the key off.</para>
    /// </summary>
    public void Aim(HttpClient client)
    {
        if (_baseUrl is null)
            return;

        client.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");

        if (!_carriesKey)
            return;

        client.DefaultRequestHeaders.Remove(RpmApi.KeyHeader);
        client.DefaultRequestHeaders.Add(RpmApi.KeyHeader, RpmApi.Key);
    }
}

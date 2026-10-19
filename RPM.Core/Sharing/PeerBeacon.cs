namespace RPM.Core.Sharing;

/// <summary>
/// What a machine broadcasts about itself, and how to read somebody else's.
///
/// <para>Kept apart from the code that does the broadcasting. The two ends of RPM find each other with
/// different machinery. A desktop opens a multicast socket. A phone asks the system's own Bonjour
/// responder, which is the only route iOS allows without an entitlement that Apple grants by written
/// application. Both ends must agree on what the record says.</para>
/// </summary>
public static class PeerBeacon
{
    /// <summary>
    /// The TXT record for this machine: the version, who it is, what to call it, who is signed in,
    /// the key its identity rests on, and the account fingerprint when there is one.
    /// </summary>
    /// <param name="name">What the machine is called. Always sent.</param>
    /// <param name="face">The display name of whoever is signed in, or empty. It is sent because a
    /// pairing question is asked before anything can be looked up. The account fingerprint sent beside
    /// it is only for comparing, not for fetching a profile. Without this value, the first thing a
    /// person reads about another computer can only be a machine name. This is not an optional
    /// parameter with a default. A face that is silently forgotten is a display name that silently
    /// never arrives, which is the kind of bug this parameter prevents.</param>
    public static IReadOnlyDictionary<string, string> Announce(
        PeerIdentity identity, string name, string face, string accountFingerprint)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SharingProtocol.Txt.Version] = SharingProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SharingProtocol.Txt.DeviceId] = identity.DeviceId,
            [SharingProtocol.Txt.Name] = name ?? "",
            [SharingProtocol.Txt.PublicKey] = SharingProtocol.ToBase64Url(identity.PublicKey),
        };

        // Left out rather than sent empty, for the same reason in both cases. A missing key says
        // nothing. An empty value invites a reader to match it against another empty value.
        if (!string.IsNullOrWhiteSpace(face))
            txt[SharingProtocol.Txt.Face] = face;

        if (!string.IsNullOrEmpty(accountFingerprint))
            txt[SharingProtocol.Txt.Account] = accountFingerprint;

        return txt;
    }

    /// <summary>
    /// A beacon, read back into the peer it describes. Null for anything that does not add up: a
    /// record with no key, a version this build does not speak, or an id that is not the one the key
    /// implies.
    ///
    /// <para>Nothing here is trusted. The beacon is data sent on a network that anybody can write to.
    /// What it gives is a name and an address to go and ask properly.</para>
    /// </summary>
    public static DiscoveredPeer? Read(
        IReadOnlyDictionary<string, string> txt, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(txt);

        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return null;

        if (Value(txt, SharingProtocol.Txt.PublicKey) is not { } encoded ||
            SharingProtocol.FromBase64Url(encoded) is not { Length: > 0 } key)
            return null;

        if (Value(txt, SharingProtocol.Txt.DeviceId) is not { } id)
            return null;

        int version = int.TryParse(
            Value(txt, SharingProtocol.Txt.Version),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int declared) ? declared : 0;

        var peer = new DiscoveredPeer
        {
            DeviceId = id,
            Name = Value(txt, SharingProtocol.Txt.Name) ?? id,
            Face = Value(txt, SharingProtocol.Txt.Face) ?? "",
            PublicKey = key,
            AccountFingerprint = Value(txt, SharingProtocol.Txt.Account) ?? "",
            Host = host,
            Port = port,
            Version = version,
        };

        return peer.IdMatchesKey ? peer : null;
    }

    private static string? Value(IReadOnlyDictionary<string, string> txt, string key) =>
        txt.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

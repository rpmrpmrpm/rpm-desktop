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
    /// The TXT record for this machine: the version, who it is, what to call it, and the key its
    /// identity rests on.
    ///
    /// <para>Nothing about the account. Anybody on the network reads this record, a café network
    /// included, so it names no person and no account. Which account a peer is in is said only inside
    /// the signed session proof, to a machine whose key it has pinned.</para>
    /// </summary>
    /// <param name="name">What the machine is called. Always sent.</param>
    public static IReadOnlyDictionary<string, string> Announce(PeerIdentity identity, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SharingProtocol.Txt.Version] = SharingProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [SharingProtocol.Txt.DeviceId] = identity.DeviceId,
            [SharingProtocol.Txt.Name] = name ?? "",
            [SharingProtocol.Txt.PublicKey] = SharingProtocol.ToBase64Url(identity.PublicKey),
        };

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
            PublicKey = key,
            Host = host,
            Port = port,
            Version = version,
        };

        return peer.IdMatchesKey ? peer : null;
    }

    private static string? Value(IReadOnlyDictionary<string, string> txt, string key) =>
        txt.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}

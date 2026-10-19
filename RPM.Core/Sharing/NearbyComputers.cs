namespace RPM.Core.Sharing;

/// <summary>What a row under "On this network" offers.</summary>
public enum NearbyKind
{
    /// <summary>Not paired, and it can be asked.</summary>
    CanAsk,

    /// <summary>This computer already shares with five. Nothing can be asked until one is removed.</summary>
    Full,

    /// <summary>That computer runs an older sharing version than this one.</summary>
    NeedsUpdateThere,

    /// <summary>That computer runs a newer sharing version than this one.</summary>
    NeedsUpdateHere,

    /// <summary>There is a pairing with it on the board; the row draws that.</summary>
    Pairing,
}

/// <summary>One row under "On this network".</summary>
public sealed record NearbyComputer(string DeviceId, string Name, string Face, NearbyKind Kind, Pairing? Pairing);

/// <summary>
/// The computers worth a row under "On this network": every unpaired computer the beacon shows, and
/// every computer with a pairing on the board that has not been dismissed.
///
/// <para>This is computed from what is known, and rebuilt on every redraw. Both the chip and the settings
/// page draw it. Two copies kept in step would drift apart. <see cref="PeerAppraisal"/> works the same way.</para>
/// </summary>
public static class NearbyComputers
{
    public static IReadOnlyList<NearbyComputer> Build(
        IReadOnlyList<DiscoveredPeer> seen,
        string myDeviceId,
        string myAccountFingerprint,
        IReadOnlyList<TrustedPeer> trusted,
        IReadOnlyCollection<string> accountDeviceIds,
        IReadOnlyList<Pairing> pairings)
    {
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(accountDeviceIds);
        ArgumentNullException.ThrowIfNull(pairings);

        var rows = new Dictionary<string, NearbyComputer>(StringComparer.OrdinalIgnoreCase);

        // A pairing comes first. While there is one, it is what the row is about, whatever the beacon says.
        foreach (var pairing in pairings)
        {
            if (pairing.Dismissed)
                continue;

            rows[pairing.DeviceId] = new NearbyComputer(
                pairing.DeviceId, pairing.Peer.Name, pairing.Peer.Face, NearbyKind.Pairing, pairing);
        }

        foreach (var peer in seen)
        {
            if (rows.ContainsKey(peer.DeviceId) || !peer.IdMatchesKey ||
                string.Equals(peer.DeviceId, myDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Checked before trust. A paired computer that fell behind is exactly the one that needs telling,
            // because its music has stopped arriving.
            if (peer.Version != SharingProtocol.Version)
            {
                rows[peer.DeviceId] = new NearbyComputer(
                    peer.DeviceId, peer.Name, peer.Face,
                    peer.Version < SharingProtocol.Version ? NearbyKind.NeedsUpdateThere : NearbyKind.NeedsUpdateHere,
                    null);
                continue;
            }

            var verdict = PeerAppraisal.Appraise(peer, myDeviceId, myAccountFingerprint, trusted, accountDeviceIds);
            var kind = verdict switch
            {
                PeerVerdict.Ask => NearbyKind.CanAsk,
                PeerVerdict.Full => NearbyKind.Full,
                _ => (NearbyKind?)null,
            };

            if (kind is { } offered)
                rows[peer.DeviceId] = new NearbyComputer(peer.DeviceId, peer.Name, peer.Face, offered, null);
        }

        return [.. rows.Values
            .OrderBy(r => string.IsNullOrEmpty(r.Face) ? r.Name : r.Face, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.DeviceId, StringComparer.Ordinal)];
    }
}

namespace RPM.Core.Sharing;

/// <summary>One RPM seen on the network, as the beacon describes it. Nothing here is trusted yet. It is
/// what a machine said about itself. The key is checked before any of it is used.</summary>
public sealed record DiscoveredPeer
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required byte[] PublicKey { get; init; }

    public required string Host { get; init; }
    public required int Port { get; init; }

    /// <summary>The sharing protocol it speaks. A version this build does not know is left alone. It
    /// is not guessed at.</summary>
    public int Version { get; init; } = SharingProtocol.Version;

    /// <summary>Whether the id it claims is the one its key implies. A beacon is not authenticated, so
    /// the two can disagree. A peer whose id does not match its key is not offered to anybody.</summary>
    public bool IdMatchesKey =>
        PublicKey is { Length: > 0 }
        && string.Equals(DeviceId, PeerIdentity.IdFor(PublicKey), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Two announcements describe the same machine when they say the same things about it.
    ///
    /// <para>Written out by hand because of one field. The key is a byte array, and the equality that a
    /// record generates compares arrays by reference. Every beacon read from the network builds a new
    /// array, so no two announcements were ever equal. A machine that re-announced itself unchanged
    /// looked like a machine that had just changed, and everything downstream ran again. That caused a
    /// catalogue pull for each announcement, and it dropped a connection from under a pull that was
    /// already running.</para>
    /// </summary>
    public bool Equals(DiscoveredPeer? other) =>
        other is not null
        && string.Equals(DeviceId, other.DeviceId, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Host, other.Host, StringComparison.Ordinal)
        && Port == other.Port
        && Version == other.Version
        && PublicKey.AsSpan().SequenceEqual(other.PublicKey);

    public override int GetHashCode() =>
        HashCode.Combine(DeviceId, Name, Host, Port, Version, PublicKey.Length);
}

/// <summary>What to do about a peer that just turned up.</summary>
public enum PeerVerdict
{
    /// <summary>This machine, hearing its own beacon.</summary>
    Self,

    /// <summary>Already paired. Nothing to ask. Connect to it.</summary>
    Known,

    /// <summary>Another of this user's own installs, already vouched for by the account. It is trusted
    /// without a question, because signing in on both machines was the question.</summary>
    AutoTrust,

    /// <summary>Worth offering a pairing to the user.</summary>
    Ask,

    /// <summary>Not worth asking about: a version this build does not speak, or a beacon that does not
    /// add up.</summary>
    Ignore,

    /// <summary>Would be worth pairing, but this collection already has its five places. Only the user
    /// can decide which one leaves.</summary>
    Full,
}

/// <summary>
/// The rule for what happens when another RPM appears on the network.
///
/// <para>It is a function of what is known, not an object with its own state. The same peer, the same
/// trust list and the same account always give the same verdict. That makes "ask once, and only when
/// asking is worth something" easy to test.</para>
/// </summary>
public static class PeerAppraisal
{
    /// <summary>
    /// What to do about <paramref name="peer"/>.
    /// </summary>
    /// <param name="peer">What the beacon said.</param>
    /// <param name="myDeviceId">This install's device id, so it does not offer to pair with itself.</param>
    /// <param name="trusted">Who this install already shares with.</param>
    /// <param name="accountDeviceIds">The device ids the signed-in account has registered. This list
    /// makes "one of mine" a fact, not a claim. Empty when signed out, or when the registry has never
    /// been read on this machine.</param>
    public static PeerVerdict Appraise(
        DiscoveredPeer peer,
        string myDeviceId,
        IReadOnlyList<TrustedPeer> trusted,
        IReadOnlyCollection<string> accountDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(accountDeviceIds);

        if (peer.Version != SharingProtocol.Version || !peer.IdMatchesKey)
            return PeerVerdict.Ignore;

        if (Same(peer.DeviceId, myDeviceId))
            return PeerVerdict.Self;

        if (PeerTrust.Match(trusted, peer.PublicKey) is not null)
            return PeerVerdict.Known;

        // A device id already in the list under a different key is not that device coming back. It is
        // something claiming to be that device. Leave it alone entirely. Pairing it would overwrite the
        // key that a real peer is trusted by.
        if (PeerTrust.Find(trusted, peer.DeviceId) is not null)
            return PeerVerdict.Ignore;

        bool mine = IsOwnInstall(peer.DeviceId, accountDeviceIds);

        if (!PeerTrust.HasRoom(trusted, peer.DeviceId))
            return PeerVerdict.Full;

        return mine ? PeerVerdict.AutoTrust : PeerVerdict.Ask;
    }

    /// <summary>
    /// Whether a machine is one of this user's own installs: its device id is on the account's list of
    /// registered installs. This one question decides whether its music is merged into this collection or
    /// kept on a shelf of its own.
    ///
    /// <para>The list is the authority. The server writes it for the signed-in user, so a machine that is
    /// not on it is not one of the user's own, however it introduces itself. A device id is derived from the
    /// device's key, and a session needs a signature with that key, so a machine cannot borrow another's
    /// place on the list. Nothing broadcast is consulted: the network says nothing about accounts.</para>
    ///
    /// <para>The list says which installs were registered, not which account each is signed into now. Once a
    /// peer has opened a session it has said that too, inside its signature. See <see cref="StillOwnInstall"/>.</para>
    /// </summary>
    /// <param name="peerDeviceId">The machine in question.</param>
    /// <param name="accountDeviceIds">The installs the signed-in account has registered. Empty when signed
    /// out, or when the registry has never been read on this machine.</param>
    public static bool IsOwnInstall(string peerDeviceId, IReadOnlyCollection<string> accountDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(accountDeviceIds);
        return accountDeviceIds.Any(id => Same(id, peerDeviceId));
    }

    /// <summary>
    /// Whether a paired machine is one of this user's own installs now. Asked on every pass, not answered once
    /// at pairing: a computer that signs into a different account stops being one of the user's own at that
    /// moment, and its records must stop going into the user's collection.
    /// </summary>
    /// <param name="peerDeviceId">The machine in question.</param>
    /// <param name="peerAccount">The account it last proved it is signed into (<see cref="TrustedPeer.AccountUserId"/>),
    /// or empty when it was signed out.</param>
    /// <param name="peerAccountKnown">Whether it has said so since this install started. Until it has, the
    /// answer rests on the device list alone.</param>
    /// <param name="myAccount">This install's user id, or empty when signed out. Empty never matches.</param>
    /// <param name="accountDeviceIds">The installs the signed-in account has registered.</param>
    public static bool StillOwnInstall(
        string peerDeviceId, string peerAccount, bool peerAccountKnown, string myAccount,
        IReadOnlyCollection<string> accountDeviceIds) =>
        !string.IsNullOrEmpty(myAccount)
        && IsOwnInstall(peerDeviceId, accountDeviceIds)
        && (!peerAccountKnown || Same(peerAccount, myAccount));

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

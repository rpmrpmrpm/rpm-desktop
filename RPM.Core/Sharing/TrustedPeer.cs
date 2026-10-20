namespace RPM.Core.Sharing;

/// <summary>What kind of peer another RPM is, which decides where its music lands.</summary>
public enum PeerKind
{
    /// <summary>Another of this user's own installs. Its music is this user's music: one collection, adopted
    /// into the mirror the same way the cloud's music already is.</summary>
    Account,

    /// <summary>Somebody else's RPM. Its music is theirs: a library of its own, switched to, and never mixed
    /// into the shelf.</summary>
    Guest,
}

/// <summary>
/// A peer this install has agreed to share with, and the key that agreement was made against.
///
/// <para>The key is the peer. The name and the address say how it is found and what it is called. They
/// change, and nothing is trusted because of them.</para>
/// </summary>
public sealed record TrustedPeer
{
    /// <summary>Derived from <see cref="PublicKey"/>. See <see cref="PeerIdentity.IdFor"/>.</summary>
    public required string DeviceId { get; init; }

    public required byte[] PublicKey { get; init; }

    /// <summary>What the machine calls itself.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The display name it last broadcast, or empty.
    ///
    /// <para>It is written down rather than looked up each time, because a profile lookup needs the internet
    /// and this does not. Two laptops on a home network with no internet connection is exactly the case that
    /// sharing is for. In that state the choice is between the name its owner picked and a default machine
    /// name. The value is refreshed whenever the two machines are seen again. So it is as new as the last
    /// time they were on a network together. It is behind the live profile, and ahead of nothing.</para>
    /// </summary>
    public string Face { get; init; } = "";

    public PeerKind Kind { get; init; } = PeerKind.Guest;

    /// <summary>
    /// The account that computer is signed into, when it has said so over the pinned link.
    ///
    /// <para>A cloud grant is written against this value. It lets a friend's cloud-stored records keep playing
    /// after everybody has left the network that the sharing was arranged on. It is empty for a peer that is
    /// signed out. That peer's music is on its own disk and stays there.</para>
    /// </summary>
    public string AccountUserId { get; init; } = "";

    /// <summary>When the peer last said which account it is in, inside a signature this machine checked: its
    /// session proof when it borrows from here, or its account answer over the pinned link when this machine
    /// borrows from it. Zero when it has not said so yet. Until then <see cref="AccountUserId"/> says nothing,
    /// and whether the peer is one of this account's own rests on the account's device list alone
    /// (<see cref="PeerAppraisal.StillOwnInstall"/>).</summary>
    public long AccountProvedUtcTicks { get; init; }

    public long PairedUtcTicks { get; init; }

    /// <summary>When this peer was last reachable. The "forget the one you never use" suggestion is made from
    /// this when the list is full.</summary>
    public long LastSeenUtcTicks { get; init; }
}

/// <summary>Why an attempt to trust a peer did not simply happen.</summary>
public enum PeerTrustOutcome
{
    Added,

    /// <summary>Already trusted. The record was refreshed rather than added twice.</summary>
    Updated,

    /// <summary>The list is full. Somebody must be dropped before anybody else is added.</summary>
    Full,
}

/// <summary>
/// The rules about who this collection reaches. They are kept apart from the file they are written to,
/// because the limit is the important part, and a limit can be tested without a disk.
/// </summary>
public static class PeerTrust
{
    /// <summary>
    /// How many other places one collection may reach, with own machines and other people's counted together.
    ///
    /// <para>There is a number rather than no number. "Share with everyone who asks" would turn a music
    /// collection into a distribution service. Five covers a laptop, a desktop, a partner and a couple of
    /// friends. That is the intended use.</para>
    /// </summary>
    public const int Limit = 5;

    /// <summary>Whether there is room for one more peer that is not already known.</summary>
    public static bool HasRoom(IReadOnlyList<TrustedPeer> peers, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return Find(peers, deviceId) is not null || peers.Count < Limit;
    }

    /// <summary>
    /// Puts a peer in the list, or says why it could not go in.
    ///
    /// <para>A peer already in the list is refreshed in place. A machine that was renamed, moved to another
    /// network, or signed into a different account is still the same machine. Pairing it again must not use
    /// up one of the five places.</para>
    /// </summary>
    public static (PeerTrustOutcome Outcome, IReadOnlyList<TrustedPeer> Peers) Add(
        IReadOnlyList<TrustedPeer> peers, TrustedPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(peer);

        var next = peers.ToList();
        int at = next.FindIndex(p => Same(p.DeviceId, peer.DeviceId));

        if (at >= 0)
        {
            // The pairing date is set when this peer is first let in, and it does not change after that.
            // Everything else takes the new values.
            next[at] = peer with { PairedUtcTicks = next[at].PairedUtcTicks };
            return (PeerTrustOutcome.Updated, next);
        }

        if (next.Count >= Limit)
            return (PeerTrustOutcome.Full, peers);

        next.Add(peer);
        return (PeerTrustOutcome.Added, next);
    }

    public static IReadOnlyList<TrustedPeer> Remove(IReadOnlyList<TrustedPeer> peers, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return peers.Where(p => !Same(p.DeviceId, deviceId)).ToList();
    }

    public static TrustedPeer? Find(IReadOnlyList<TrustedPeer> peers, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return string.IsNullOrEmpty(deviceId)
            ? null
            : peers.FirstOrDefault(p => Same(p.DeviceId, deviceId));
    }

    /// <summary>
    /// Whether a peer presenting this key is trusted. The check is against the key, and against the id that
    /// the key implies. So a peer cannot borrow somebody else's id by claiming it.
    /// </summary>
    public static TrustedPeer? Match(IReadOnlyList<TrustedPeer> peers, byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(peers);
        if (publicKey is null || publicKey.Length == 0)
            return null;

        var id = PeerIdentity.IdFor(publicKey);
        var known = Find(peers, id);
        return known is not null && known.PublicKey.AsSpan().SequenceEqual(publicKey) ? known : null;
    }

    /// <summary>
    /// The track id that this account's own cloud may be asked about, or null when the record is somebody
    /// else's.
    ///
    /// <para>
    /// A track id names a row in the storage of the account that uploaded it. For another of this user's own
    /// computers, that is this same account. So the id is as good here as it is there. For a guest it is not.
    /// Their id means nothing in this account's storage. Every request for one returns 404, one per borrowed
    /// album, on every wall that draws their shelf, forever. The album key is what is left to ask under. It
    /// is made from the artist and the album, so it is the same key on both sides.
    /// </para>
    /// </summary>
    public static string? OwnCloudId(
        IReadOnlyList<TrustedPeer> peers, string? rpmId, string? peerDeviceId)
    {
        ArgumentNullException.ThrowIfNull(peers);

        return peerDeviceId is { Length: > 0 } id && Find(peers, id) is { Kind: PeerKind.Guest }
            ? null
            : rpmId;
    }

    /// <summary>The peer to suggest dropping when the list is full: the one nobody has seen for longest.
    /// Failing that, the oldest pairing.</summary>
    public static TrustedPeer? Coldest(IReadOnlyList<TrustedPeer> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        return peers
            .OrderBy(p => p.LastSeenUtcTicks)
            .ThenBy(p => p.PairedUtcTicks)
            .FirstOrDefault();
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

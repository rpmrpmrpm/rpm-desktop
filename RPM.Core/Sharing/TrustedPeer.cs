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

    /// <summary>The two accounts that the pairing was agreed between. Cloud copies are shared only between these two
    /// accounts (<see cref="PeerTrust.GrantFor"/>). Null in a record from a file that does not have it. No cloud
    /// copies are shared through such a pairing until the two computers pair again.</summary>
    public PairedAccounts? PairedAs { get; init; }

    /// <summary>When this peer was last reachable. The "forget the one you never use" suggestion is made from
    /// this when the list is full.</summary>
    public long LastSeenUtcTicks { get; init; }
}

/// <summary>
/// The accounts that were signed in on the two computers when they paired.
///
/// <para>The book of trusted peers belongs to this install, but a cloud share belongs to an account. The people who
/// typed the code agreed to share between these two accounts and no others. Without this record, any account signed
/// in later on either computer would get the pairing too. This machine would share that account's cloud copies with
/// every guest, and a guest that signed into another account would be given this account's copies. Nobody typed a
/// code for either of those.</para>
/// </summary>
public sealed record PairedAccounts
{
    /// <summary>This machine's account when the pairing was made. Empty when this machine was signed out. Such a
    /// pairing shares no cloud copies from this side.</summary>
    public required string Mine { get; init; }

    /// <summary>The first account that the peer proved after the pairing, over a link pinned to its key. Empty when it
    /// proved that it was signed out. Null until it has said anything. It is written once
    /// (<see cref="PeerTrust.Proved"/>), and a later account does not replace it.</summary>
    public string? Theirs { get; init; }
}

/// <summary>
/// Permission to share this account's cloud copies with one other account. It is the only argument
/// <c>RpmCloudClient.GrantShareAsync</c> takes, and only <see cref="PeerTrust.GrantFor"/> makes one. So a share cannot
/// be posted without the check against <see cref="PairedAccounts"/>.
/// </summary>
public sealed class CloudGrant
{
    internal CloudGrant(string granteeId, string granteeName)
    {
        GranteeId = granteeId;
        GranteeName = granteeName;
    }

    /// <summary>The account that may hear this account's cloud copies.</summary>
    public string GranteeId { get; }

    /// <summary>What the peer is called, stored with the share for the settings list.</summary>
    public string GranteeName { get; }
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

    /// <summary>
    /// The record after <paramref name="current"/> has said, over a link pinned to its key, that it is signed into
    /// <paramref name="account"/>. Null when nothing changes.
    ///
    /// <para>The first proof after a pairing is also written into <see cref="TrustedPeer.PairedAs"/>, and only the
    /// first. A record without <see cref="TrustedPeer.PairedAs"/> does not get one here. A proof cannot stand in for a
    /// pairing.</para>
    /// </summary>
    /// <param name="account">The account the peer said it is in. Empty when it said it is signed out.</param>
    /// <param name="nowTicks">The time of the proof, in UTC ticks.</param>
    public static TrustedPeer? Proved(TrustedPeer current, string account, long nowTicks)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(account);

        var pairedAs = current.PairedAs is { Theirs: null } waiting
            ? waiting with { Theirs = account }
            : current.PairedAs;

        var unchanged = current.AccountProvedUtcTicks > 0
            && string.Equals(current.AccountUserId, account, StringComparison.Ordinal)
            && ReferenceEquals(pairedAs, current.PairedAs);

        return unchanged
            ? null
            : current with { AccountUserId = account, AccountProvedUtcTicks = nowTicks, PairedAs = pairedAs };
    }

    /// <summary>
    /// The share that this pairing allows, or null.
    ///
    /// <para>A share is allowed only while this machine is signed into the account it paired under, and the peer is
    /// signed into the account it first proved after the pairing. Both must be accounts, and they must be different
    /// accounts. An account always reaches its own cloud copies, and the server refuses a share to itself.</para>
    /// </summary>
    /// <param name="myAccount">The account signed in on this machine now, or empty.</param>
    /// <param name="theirAccount">The account the peer said it is in, over a link pinned to its key.</param>
    public static CloudGrant? GrantFor(TrustedPeer peer, string myAccount, string theirAccount)
    {
        ArgumentNullException.ThrowIfNull(peer);

        if (peer.PairedAs is not { Mine.Length: > 0, Theirs.Length: > 0 } paired)
            return null;

        if (!SameAccount(paired.Mine, myAccount) || !SameAccount(paired.Theirs, theirAccount))
            return null;

        return SameAccount(myAccount, theirAccount) ? null : new CloudGrant(theirAccount, peer.Name);
    }

    private static bool SameAccount(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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

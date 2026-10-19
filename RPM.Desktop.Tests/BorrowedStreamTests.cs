using RPM.Core.Sharing;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which records this account's cloud may be asked about, and which are somebody else's.
///
/// <para>
/// A track id names a row in the storage of the account that uploaded it. A friend's id means nothing
/// in this account's storage, so the cloud answers 404 for it. The look-ahead asks for eight ids at
/// every track change, and on a borrowed shelf every row is a friend's. So those asks must never be
/// made. The music itself comes from the friend's computer over the network.
/// </para>
///
/// <para>The rule has two parts: which computer holds a record, and whether that makes its id one to ask
/// this account's cloud about.</para>
/// </summary>
public sealed class BorrowedStreamTests
{
    private const string Guest = "THEIR-PC";
    private const string Own = "MY-OTHER-PC";

    private static TrustedPeer Peer(string id, PeerKind kind) =>
        new() { DeviceId = id, PublicKey = [1, 2, 3], Name = id, Kind = kind };

    /// <summary>Nothing in this machine's own mirror, and every peer mirror empty.</summary>
    private static string? Nowhere(string rpmId) => null;

    private static string? Held(string device, string rpmId, string holder, string held) =>
        string.Equals(device, holder, StringComparison.Ordinal) && rpmId == held ? holder : null;

    [Fact]
    public void A_record_nobody_else_holds_is_this_collections_own()
    {
        Assert.Null(SharingService.DeviceHolding(
            "mine", Nowhere, [Peer(Guest, PeerKind.Guest)], (_, _) => null));
    }

    [Fact]
    public void A_guests_record_is_found_in_the_mirror_kept_for_that_computer()
    {
        // Not in the user's own library. A guest's rows are never adopted into it, so the per-peer mirror
        // is the only place this question can be answered.
        var holder = SharingService.DeviceHolding(
            "theirs", Nowhere, [Peer(Guest, PeerKind.Guest)],
            (device, id) => Held(device, id, Guest, "theirs"));

        Assert.Equal(Guest, holder);
    }

    [Fact]
    public void A_record_on_another_of_the_users_own_computers_is_found_in_their_own_mirror()
    {
        // An account peer's rows are adopted into the library, so this record is answered without using
        // a mirror kept for another person.
        var holder = SharingService.DeviceHolding(
            "adopted", id => id == "adopted" ? Own : null, [Peer(Own, PeerKind.Account)],
            (_, _) => throw new InvalidOperationException("the peer mirrors should not be reached"));

        Assert.Equal(Own, holder);
    }

    [Fact]
    public void A_guests_record_is_not_an_id_to_ask_this_accounts_cloud_about()
    {
        List<TrustedPeer> peers = [Peer(Guest, PeerKind.Guest)];

        Assert.Null(PeerTrust.OwnCloudId(peers, "theirs", Guest));
    }

    [Fact]
    public void A_record_on_the_users_own_second_computer_still_streams_from_their_cloud()
    {
        // This must keep working: one collection spans a person's own machines, so an id from another of
        // their machines is valid here too.
        List<TrustedPeer> peers = [Peer(Own, PeerKind.Account)];

        Assert.Equal("adopted", PeerTrust.OwnCloudId(peers, "adopted", Own));
        Assert.Equal("here", PeerTrust.OwnCloudId(peers, "here", null));
    }
}

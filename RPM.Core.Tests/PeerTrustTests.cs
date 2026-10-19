using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// How far one collection reaches, and who it recognises.
///
/// <para>The limit is the part with teeth in it: five places, own machines and friends counted
/// together, and re-pairing a machine already on the list does not quietly spend a sixth. The rest
/// pins that a peer is its key: a device id on its own gives no trust, which stops a beacon
/// (which anybody can write) from being taken as identity.</para>
/// </summary>
public class PeerTrustTests
{
    private static TrustedPeer Peer(string id, byte[]? key = null, PeerKind kind = PeerKind.Guest,
        long lastSeen = 0, long paired = 0) => new()
    {
        DeviceId = id,
        PublicKey = key ?? System.Text.Encoding.UTF8.GetBytes(id),
        Name = id,
        Kind = kind,
        LastSeenUtcTicks = lastSeen,
        PairedUtcTicks = paired,
    };

    private static List<TrustedPeer> Full() =>
        [Peer("a"), Peer("b"), Peer("c"), Peer("d"), Peer("e")];

    [Fact]
    public void Five_places_is_the_limit()
    {
        Assert.Equal(5, PeerTrust.Limit);

        var (outcome, peers) = PeerTrust.Add(Full(), Peer("f"));

        Assert.Equal(PeerTrustOutcome.Full, outcome);
        Assert.Equal(5, peers.Count);
    }

    [Fact]
    public void A_machine_already_on_the_list_does_not_spend_another_place()
    {
        var known = Full();

        var (outcome, peers) = PeerTrust.Add(known, Peer("c") with { Name = "renamed" });

        Assert.Equal(PeerTrustOutcome.Updated, outcome);
        Assert.Equal(5, peers.Count);
        Assert.Equal("renamed", PeerTrust.Find(peers, "c")!.Name);
    }

    [Fact]
    public void The_day_a_peer_was_first_let_in_stays_that_day()
    {
        List<TrustedPeer> known = [Peer("a", paired: 111)];

        var (_, peers) = PeerTrust.Add(known, Peer("a", paired: 999));

        Assert.Equal(111, PeerTrust.Find(peers, "a")!.PairedUtcTicks);
    }

    [Fact]
    public void There_is_room_for_a_peer_that_is_already_known_even_when_the_list_is_full()
    {
        var known = Full();

        Assert.True(PeerTrust.HasRoom(known, "c"));
        Assert.False(PeerTrust.HasRoom(known, "f"));
    }

    [Fact]
    public void Forgetting_one_makes_room()
    {
        var known = PeerTrust.Remove(Full(), "b");

        Assert.Equal(4, known.Count);
        Assert.Null(PeerTrust.Find(known, "b"));

        var (outcome, peers) = PeerTrust.Add(known, Peer("f"));
        Assert.Equal(PeerTrustOutcome.Added, outcome);
        Assert.Equal(5, peers.Count);
    }

    [Fact]
    public void A_peer_is_its_key_not_its_name()
    {
        List<TrustedPeer> known = [Peer("a", key: [1, 2, 3])];

        Assert.NotNull(PeerTrust.Find(known, "a"));
        Assert.Null(PeerTrust.Match(known, [4, 5, 6]));
    }

    [Fact]
    public void A_key_is_matched_under_the_id_that_key_implies()
    {
        using var identity = PeerIdentity.Create();
        List<TrustedPeer> known = [Peer(identity.DeviceId, identity.PublicKey)];

        Assert.NotNull(PeerTrust.Match(known, identity.PublicKey));
    }

    [Fact]
    public void A_key_filed_under_somebody_elses_id_is_not_trusted()
    {
        using var identity = PeerIdentity.Create();

        // The list says "machine a is this key", but the key's own id is not "a". That is what a
        // hand-edited trust file, or a beacon believed too early, would look like.
        List<TrustedPeer> known = [Peer("a", identity.PublicKey)];

        Assert.Null(PeerTrust.Match(known, identity.PublicKey));
    }

    [Fact]
    public void The_one_to_drop_is_the_one_nobody_has_seen()
    {
        List<TrustedPeer> known =
        [
            Peer("a", lastSeen: 500),
            Peer("b", lastSeen: 10),
            Peer("c", lastSeen: 900),
        ];

        Assert.Equal("b", PeerTrust.Coldest(known)!.DeviceId);
    }

    [Fact]
    public void Nothing_to_drop_when_nobody_is_trusted()
    {
        Assert.Null(PeerTrust.Coldest([]));
    }

    [Fact]
    public void A_guests_track_id_is_not_asked_of_this_accounts_cloud()
    {
        // The whole shape of the bug this pins: a borrowed record's id names a row in THEIR storage,
        // so asking for it here is a 404 per album, on every wall that draws their shelf.
        List<TrustedPeer> known = [Peer("theirs", kind: PeerKind.Guest)];

        Assert.Null(PeerTrust.OwnCloudId(known, "their-track", "theirs"));
    }

    [Fact]
    public void A_record_on_the_users_own_computer_is_a_record_their_cloud_knows()
    {
        List<TrustedPeer> known = [Peer("mine", kind: PeerKind.Account)];

        Assert.Equal("my-track", PeerTrust.OwnCloudId(known, "my-track", "mine"));
    }

    [Fact]
    public void A_record_of_this_machines_own_is_asked_for_as_it_always_was()
    {
        Assert.Equal("my-track", PeerTrust.OwnCloudId([], "my-track", null));
        Assert.Equal("my-track", PeerTrust.OwnCloudId([], "my-track", ""));

        // A peer that is no longer in the book: nothing says the record is somebody else's, and the
        // id is the user's own to ask about.
        Assert.Equal("my-track", PeerTrust.OwnCloudId([], "my-track", "forgotten"));
    }
}

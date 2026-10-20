using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What happens when another RPM appears on the network.
///
/// <para>Two rules make the feature. A machine of the user's own, confirmed by the account, is
/// trusted without asking, because signing in on both machines already answered the question.
/// Everybody else can be asked, and can still be asked after a no: whether the offer opens by itself
/// is decided by the screen.</para>
/// </summary>
public class PeerAppraisalTests
{
    private const string MyAccount = "user-mine";

    private static DiscoveredPeer Seen(PeerIdentity identity) => new()
    {
        DeviceId = identity.DeviceId,
        Name = "Anna's iMac",
        PublicKey = identity.PublicKey,
        Host = "192.168.1.7",
        Port = 51733,
    };

    private static TrustedPeer Trusted(PeerIdentity identity) => new()
    {
        DeviceId = identity.DeviceId,
        PublicKey = identity.PublicKey,
        Name = "known",
    };

    [Fact]
    public void This_machine_hearing_itself_is_not_a_peer()
    {
        using var me = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Self,
            PeerAppraisal.Appraise(Seen(me), me.DeviceId, [], []));
    }

    [Fact]
    public void A_stranger_is_asked_about()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Ask,
            PeerAppraisal.Appraise(Seen(them), "me", [], []));
    }

    [Fact]
    public void A_peer_already_paired_with_is_simply_known()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Known,
            PeerAppraisal.Appraise(Seen(them), "me", [Trusted(them)], []));
    }

    [Fact]
    public void Another_of_my_own_computers_is_trusted_without_a_question()
    {
        using var mine = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.AutoTrust,
            PeerAppraisal.Appraise(
                Seen(mine), "me", [], [mine.DeviceId]));
    }

    [Fact]
    public void A_machine_the_account_has_never_registered_is_asked_about()
    {
        using var them = PeerIdentity.Create();

        // The beacon says nothing about accounts. What makes a machine one of ours is the account's own
        // list of devices, and this one is not on it.
        Assert.Equal(PeerVerdict.Ask,
            PeerAppraisal.Appraise(Seen(them), "me", [], ["SOMEBODY-ELSE"]));
    }

    [Fact]
    public void A_beacon_whose_id_is_not_its_key_is_left_alone()
    {
        using var them = PeerIdentity.Create();

        var lying = Seen(them) with { DeviceId = "SOMEBODYELSE" };

        Assert.Equal(PeerVerdict.Ignore,
            PeerAppraisal.Appraise(lying, "me", [], []));
    }

    [Fact]
    public void A_key_pretending_to_be_a_trusted_machine_is_left_alone()
    {
        using var trusted = PeerIdentity.Create();
        using var impostor = PeerIdentity.Create();

        // Same id in the beacon, a different key behind it. Pairing it would overwrite the key a real
        // peer is trusted by.
        var lying = Seen(impostor) with { DeviceId = trusted.DeviceId };

        Assert.Equal(PeerVerdict.Ignore,
            PeerAppraisal.Appraise(lying, "me", [Trusted(trusted)], []));
    }

    [Fact]
    public void A_version_we_do_not_speak_is_left_alone()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Ignore,
            PeerAppraisal.Appraise(
                Seen(them) with { Version = SharingProtocol.Version + 1 }, "me", [], []));
    }

    [Fact]
    public void A_sixth_place_is_the_users_decision_not_a_silent_no()
    {
        using var them = PeerIdentity.Create();

        var full = Enumerable.Range(0, PeerTrust.Limit)
            .Select(i => new TrustedPeer { DeviceId = $"p{i}", PublicKey = [(byte)i], Name = $"p{i}" })
            .ToList();

        Assert.Equal(PeerVerdict.Full,
            PeerAppraisal.Appraise(Seen(them), "me", full, []));
    }

    [Fact]
    public void Even_one_of_my_own_computers_waits_when_there_is_no_room()
    {
        using var mine = PeerIdentity.Create();

        var full = Enumerable.Range(0, PeerTrust.Limit)
            .Select(i => new TrustedPeer { DeviceId = $"p{i}", PublicKey = [(byte)i], Name = $"p{i}" })
            .ToList();

        Assert.Equal(PeerVerdict.Full,
            PeerAppraisal.Appraise(
                Seen(mine), "me", full, [mine.DeviceId]));
    }

    [Fact]
    public void A_machine_is_one_of_the_users_own_only_when_the_account_has_registered_it()
    {
        // The account's own list of installs is the truth. "One of mine" merges somebody's records into
        // this collection instead of putting them on their own shelf.
        Assert.True(PeerAppraisal.IsOwnInstall("LAPTOP", ["LAPTOP", "DESKTOP"]));
        Assert.False(PeerAppraisal.IsOwnInstall("STRANGER", ["LAPTOP"]));

        // Signed out here: no list, so nobody is mine.
        Assert.False(PeerAppraisal.IsOwnInstall("LAPTOP", []));
    }

    [Fact]
    public void A_registered_machine_stops_being_mine_when_it_proves_another_account()
    {
        // Not proved yet (an old pairing, or no session since this install started): the list decides.
        Assert.True(PeerAppraisal.StillOwnInstall("LAPTOP", "", peerAccountKnown: false, MyAccount, ["LAPTOP"]));

        // Proved, in its session signature or its pinned account answer, to be in this account.
        Assert.True(PeerAppraisal.StillOwnInstall("LAPTOP", MyAccount, peerAccountKnown: true, MyAccount, ["LAPTOP"]));

        // Registered once, but now signed into somebody else's account, or signed out.
        Assert.False(PeerAppraisal.StillOwnInstall("LAPTOP", "user-theirs", peerAccountKnown: true, MyAccount, ["LAPTOP"]));
        Assert.False(PeerAppraisal.StillOwnInstall("LAPTOP", "", peerAccountKnown: true, MyAccount, ["LAPTOP"]));

        // Proving the right account does not put a machine on the list.
        Assert.False(PeerAppraisal.StillOwnInstall("STRANGER", MyAccount, peerAccountKnown: true, MyAccount, ["LAPTOP"]));

        // Signed out here. Two signed-out machines are two strangers, not one user.
        Assert.False(PeerAppraisal.StillOwnInstall("LAPTOP", "", peerAccountKnown: true, "", ["LAPTOP"]));
    }

    [Fact]
    public void The_same_machine_announcing_itself_twice_is_not_news()
    {
        using var them = PeerIdentity.Create();

        // Two reads of the same beacon, as they come from the wire: parsed separately, so each has
        // its own copy of the key. Discovery treats an unequal record as a machine that changed, and
        // acts on it with a catalogue pull, releasing the connection of any pull already running.
        // So the two reads must be equal.
        var first = Seen(them) with { PublicKey = them.PublicKey.ToArray() };
        var second = Seen(them) with { PublicKey = them.PublicKey.ToArray() };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void A_machine_that_moved_is_news()
    {
        using var them = PeerIdentity.Create();

        var here = Seen(them);
        Assert.NotEqual(here, here with { Host = "192.168.1.9" });
        Assert.NotEqual(here, here with { Port = 51734 });
        Assert.NotEqual(here, here with { Name = "Anna's MacBook" });
        Assert.NotEqual(here, here with { PublicKey = [1, 2, 3] });
    }
}

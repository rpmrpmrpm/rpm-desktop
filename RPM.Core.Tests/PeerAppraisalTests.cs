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
    private const string MyAccount = "MINE";

    private static DiscoveredPeer Seen(PeerIdentity identity, string account = "") => new()
    {
        DeviceId = identity.DeviceId,
        Name = "Anna's iMac",
        PublicKey = identity.PublicKey,
        AccountFingerprint = account,
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
            PeerAppraisal.Appraise(Seen(me), me.DeviceId, MyAccount, [], []));
    }

    [Fact]
    public void A_stranger_is_asked_about()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Ask,
            PeerAppraisal.Appraise(Seen(them), "me", MyAccount, [], []));
    }

    [Fact]
    public void A_peer_already_paired_with_is_simply_known()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Known,
            PeerAppraisal.Appraise(Seen(them), "me", MyAccount, [Trusted(them)], []));
    }

    [Fact]
    public void Another_of_my_own_computers_is_trusted_without_a_question()
    {
        using var mine = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.AutoTrust,
            PeerAppraisal.Appraise(
                Seen(mine, MyAccount), "me", MyAccount, [], [mine.DeviceId]));
    }

    [Fact]
    public void A_machine_claiming_my_account_that_the_account_has_never_registered_is_asked_about()
    {
        using var them = PeerIdentity.Create();

        // The fingerprint is public: it is in a beacon anybody can write. What makes a machine one of
        // ours is the account's own list of devices, not the claim.
        Assert.Equal(PeerVerdict.Ask,
            PeerAppraisal.Appraise(Seen(them, MyAccount), "me", MyAccount, [], []));
    }

    [Fact]
    public void Signed_out_nobody_is_automatically_mine()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Ask,
            PeerAppraisal.Appraise(
                Seen(them, ""), "me", myAccountFingerprint: "", [], [them.DeviceId]));
    }

    [Fact]
    public void A_beacon_whose_id_is_not_its_key_is_left_alone()
    {
        using var them = PeerIdentity.Create();

        var lying = Seen(them) with { DeviceId = "SOMEBODYELSE" };

        Assert.Equal(PeerVerdict.Ignore,
            PeerAppraisal.Appraise(lying, "me", MyAccount, [], []));
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
            PeerAppraisal.Appraise(lying, "me", MyAccount, [Trusted(trusted)], []));
    }

    [Fact]
    public void A_version_we_do_not_speak_is_left_alone()
    {
        using var them = PeerIdentity.Create();

        Assert.Equal(PeerVerdict.Ignore,
            PeerAppraisal.Appraise(
                Seen(them) with { Version = SharingProtocol.Version + 1 }, "me", MyAccount, [], []));
    }

    [Fact]
    public void A_sixth_place_is_the_users_decision_not_a_silent_no()
    {
        using var them = PeerIdentity.Create();

        var full = Enumerable.Range(0, PeerTrust.Limit)
            .Select(i => new TrustedPeer { DeviceId = $"p{i}", PublicKey = [(byte)i], Name = $"p{i}" })
            .ToList();

        Assert.Equal(PeerVerdict.Full,
            PeerAppraisal.Appraise(Seen(them), "me", MyAccount, full, []));
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
                Seen(mine, MyAccount), "me", MyAccount, full, [mine.DeviceId]));
    }

    [Fact]
    public void A_machine_is_one_of_the_users_own_only_when_the_account_says_so()
    {
        // The fingerprint is what a machine claims. The account's own list of installs is the truth,
        // and the difference matters, because "one of mine" merges somebody's records into this
        // collection instead of putting them on their own shelf.
        Assert.True(PeerAppraisal.IsOwnInstall("LAPTOP", "MINE", "MINE", ["LAPTOP", "DESKTOP"]));

        // Claims the right account, but the account has never registered it.
        Assert.False(PeerAppraisal.IsOwnInstall("STRANGER", "MINE", "MINE", ["LAPTOP"]));

        // Registered once, but now signed into somebody else's account.
        Assert.False(PeerAppraisal.IsOwnInstall("LAPTOP", "THEIRS", "MINE", ["LAPTOP"]));

        // Signed out here. Two signed-out machines are two strangers, not one user.
        Assert.False(PeerAppraisal.IsOwnInstall("LAPTOP", "", "", ["LAPTOP"]));
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
        Assert.NotEqual(here, here with { AccountFingerprint = MyAccount });
        Assert.NotEqual(here, here with { PublicKey = [1, 2, 3] });
    }
}

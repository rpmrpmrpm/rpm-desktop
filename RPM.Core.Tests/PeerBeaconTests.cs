using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The small record a machine broadcasts, and what a reader may take from it.
///
/// <para>The two ends of RPM find each other in different ways (a multicast socket on a desktop, the
/// system's Bonjour responder on a phone), so the record itself is the contract between them. The rest
/// of these tests are about not trusting it: anybody on the network can write to the wire, and a
/// beacon whose id does not match its own key is the easiest lie to tell.</para>
/// </summary>
public class PeerBeaconTests
{
    [Fact]
    public void What_goes_out_comes_back_as_the_same_machine()
    {
        using var identity = PeerIdentity.Create();

        var txt = PeerBeacon.Announce(identity, "Alex's iMac");
        var peer = PeerBeacon.Read(txt, "192.168.1.7", 51733);

        Assert.NotNull(peer);
        Assert.Equal(identity.DeviceId, peer!.DeviceId);
        Assert.Equal("Alex's iMac", peer.Name);
        Assert.Equal(identity.PublicKey, peer.PublicKey);
        Assert.Equal(SharingProtocol.Version, peer.Version);
        Assert.Equal(51733, peer.Port);
    }

    /// <summary>Anybody on the network reads the beacon, a café network included, so it names no person and no
    /// account: the version, the id, the machine's name and the key, and nothing else.</summary>
    [Fact]
    public void The_beacon_names_no_person_and_no_account()
    {
        using var identity = PeerIdentity.Create();

        var txt = PeerBeacon.Announce(identity, "laptop");

        Assert.Equal(
            new[] { SharingProtocol.Txt.DeviceId, SharingProtocol.Txt.PublicKey, SharingProtocol.Txt.Name, SharingProtocol.Txt.Version }
                .OrderBy(k => k, StringComparer.Ordinal),
            txt.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_beacon_with_no_key_is_nobody()
    {
        var txt = new Dictionary<string, string>
        {
            [SharingProtocol.Txt.Version] = "1",
            [SharingProtocol.Txt.DeviceId] = "ABC",
            [SharingProtocol.Txt.Name] = "somebody",
        };

        Assert.Null(PeerBeacon.Read(txt, "10.0.0.4", 51733));
    }

    [Fact]
    public void A_beacon_wearing_somebody_elses_id_is_nobody()
    {
        using var identity = PeerIdentity.Create();

        var txt = new Dictionary<string, string>(PeerBeacon.Announce(identity, "x"))
        {
            [SharingProtocol.Txt.DeviceId] = "SOMEBODYELSE",
        };

        Assert.Null(PeerBeacon.Read(txt, "10.0.0.4", 51733));
    }

    [Fact]
    public void A_machine_with_nowhere_to_be_reached_is_nobody()
    {
        using var identity = PeerIdentity.Create();
        var txt = PeerBeacon.Announce(identity, "x");

        Assert.Null(PeerBeacon.Read(txt, "", 51733));
        Assert.Null(PeerBeacon.Read(txt, "10.0.0.4", 0));
    }

    [Fact]
    public void A_version_from_the_future_is_read_and_left_to_the_appraisal()
    {
        using var identity = PeerIdentity.Create();

        var txt = new Dictionary<string, string>(PeerBeacon.Announce(identity, "x"))
        {
            [SharingProtocol.Txt.Version] = "99",
        };

        // Read, because the record is well formed; ignored afterwards, because this build does not
        // support protocol 99. Keeping those two steps separate lets the version be reported instead
        // of silently dropped.
        var peer = PeerBeacon.Read(txt, "10.0.0.4", 51733);

        Assert.Equal(99, peer!.Version);
        Assert.Equal(PeerVerdict.Ignore, PeerAppraisal.Appraise(peer, "me", [], []));
    }

    [Fact]
    public void A_nameless_machine_is_called_by_its_id()
    {
        using var identity = PeerIdentity.Create();

        var txt = PeerBeacon.Announce(identity, "");

        Assert.Equal(identity.DeviceId, PeerBeacon.Read(txt, "10.0.0.4", 51733)!.Name);
    }

    [Fact]
    public void A_renamed_machine_is_a_different_announcement()
    {
        // DiscoveredPeer defines its own equality, and everything after a beacon is skipped when two
        // announcements are equal. A name left out of that comparison would be a rename that reaches
        // nobody.
        using var identity = PeerIdentity.Create();

        var before = PeerBeacon.Read(PeerBeacon.Announce(identity, "MBP"), "10.0.0.4", 1);
        var after = PeerBeacon.Read(PeerBeacon.Announce(identity, "Studio"), "10.0.0.4", 1);

        Assert.NotEqual(before, after);
    }
}

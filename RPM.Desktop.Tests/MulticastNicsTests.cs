using System.Net;
using System.Net.NetworkInformation;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which network cards an mDNS announcement goes out on.
///
/// <para>The rule matters. Sending to the multicast address on a card with no route fails at once.
/// Announcements are sent on every advertisement, every answer and every sweep. So a card kept by
/// mistake throws several times a minute while the app is open, and a card dropped by mistake means
/// the user cannot share on that network at all.</para>
/// </summary>
public class MulticastNicsTests
{
    private static IPAddress[] V4(string address) => [IPAddress.Parse(address)];

    [Fact]
    public void OrdinaryWiFiIsKept()
    {
        Assert.True(MulticastNics.Carries("en0", NetworkInterfaceType.Wireless80211, V4("192.168.1.24")));
    }

    [Fact]
    public void OrdinaryEthernetIsKept()
    {
        Assert.True(MulticastNics.Carries("eth0", NetworkInterfaceType.Ethernet, V4("10.0.0.5")));
    }

    /// <summary>The case mDNS exists for: no DHCP server, so every machine is on 169.254. Dropping
    /// this card would stop sharing on exactly the network where nothing else could find anyone.</summary>
    [Fact]
    public void SelfAssignedIPv4IsKept()
    {
        Assert.True(MulticastNics.Carries("en0", NetworkInterfaceType.Ethernet, V4("169.254.12.9")));
    }

    /// <summary>AirDrop's own radio link. Up, multicast-capable, and nowhere to send to.</summary>
    [Fact]
    public void AppleLinkOnlyCardsAreDropped()
    {
        foreach (var name in new[] { "awdl0", "llw0", "anpi0", "ap1" })
        {
            Assert.False(
                MulticastNics.Carries(name, NetworkInterfaceType.Ethernet, V4("192.168.1.24")),
                name);
        }
    }

    [Fact]
    public void TunnelsAreDropped()
    {
        Assert.False(MulticastNics.Carries("utun4", NetworkInterfaceType.Tunnel, V4("10.8.0.2")));
        Assert.False(MulticastNics.Carries("ppp0", NetworkInterfaceType.Ppp, V4("10.9.0.2")));
    }

    /// <summary>A tunnel the system reports as plain ethernet is still a tunnel. The name is the
    /// only thing left saying so.</summary>
    [Fact]
    public void TunnelNamedButNotTypedIsDropped()
    {
        Assert.False(MulticastNics.Carries("utun0", NetworkInterfaceType.Ethernet, V4("10.8.0.2")));
    }

    /// <summary>fe80:: is a link-local address that the card gives itself. A card with only that
    /// address has no real network on it.</summary>
    [Fact]
    public void LinkLocalIPv6AloneIsDropped()
    {
        Assert.False(MulticastNics.Carries(
            "en5", NetworkInterfaceType.Ethernet, [IPAddress.Parse("fe80::1c2b:3d4e:5f60:7182")]));
    }

    [Fact]
    public void RoutableIPv6IsKept()
    {
        Assert.True(MulticastNics.Carries(
            "en0", NetworkInterfaceType.Ethernet, [IPAddress.Parse("2001:db8::42")]));
    }

    [Fact]
    public void NoAddressAtAllIsDropped()
    {
        Assert.False(MulticastNics.Carries("en9", NetworkInterfaceType.Ethernet, []));
    }

    private static (string, NetworkInterfaceType, IReadOnlyList<IPAddress>) Card(
        string name, params string[] addresses)
        => (name, NetworkInterfaceType.Ethernet, addresses.Select(IPAddress.Parse).ToList());

    /// <summary>The usual machine has an IPv4 address on this card, so the IPv6 sender is not
    /// opened. It would carry no traffic here.</summary>
    [Fact]
    public void IPv6IsNotOpenedWhenIPv4IsThere()
    {
        Assert.False(MulticastNics.NeedsIPv6([
            Card("en0", "192.168.1.24", "2001:db8::42"),
            Card("utun3", "10.8.0.2"),
        ]));
    }

    /// <summary>An IPv6-only network. Here IPv6 is the only transport there is.</summary>
    [Fact]
    public void IPv6IsOpenedWhenItIsAllThereIs()
    {
        Assert.True(MulticastNics.NeedsIPv6([Card("en0", "2001:db8::42")]));
    }

    /// <summary>The IPv4 address must be on a card that is announced on. A tunnel's address does not
    /// count. Taking it would silence the only transport an IPv6 network has.</summary>
    [Fact]
    public void IPv4OnACardThatIsNotKeptDoesNotCount()
    {
        Assert.True(MulticastNics.NeedsIPv6([
            Card("en0", "2001:db8::42"),
            Card("utun3", "10.8.0.2"),
        ]));
    }

    /// <summary>No card is recognised. Usable gives the service every card rather than none, so this
    /// rules nothing out either.</summary>
    [Fact]
    public void NothingRecognisedRulesNothingOut()
    {
        Assert.True(MulticastNics.NeedsIPv6([Card("utun3", "10.8.0.2")]));
    }
}

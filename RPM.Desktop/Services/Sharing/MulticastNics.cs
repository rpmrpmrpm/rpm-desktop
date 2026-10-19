using System.Net;
using System.Net.NetworkInformation;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// Which network cards an announcement is worth sending on.
///
/// <para>The mDNS library already narrows the list to cards that are up, are not receive-only and can
/// carry multicast. On Windows and Linux that is the whole answer. A Mac keeps several cards that pass
/// all three tests but still cannot carry a packet anywhere. <c>awdl0</c> and <c>llw0</c> are
/// AirDrop's own radio links. <c>anpi0</c> and <c>ap1</c> are internal. A VPN's <c>utun</c> is a
/// point-to-point tunnel with nobody on the other end listening. Sending to 224.0.0.251 on any of them
/// fails at once with <c>No route to host</c>.</para>
///
/// <para>A failed send on one card does not stop the others, which keep working. But the failure is
/// an exception, and the announcement is made on every advertisement, on every answer to another
/// machine's query, and on every sweep. In one session that could mean hundreds of exceptions, which
/// buries the debug output.</para>
///
/// <para>So the cards are chosen, not just accepted. A card is kept when it has an address a packet
/// could be routed from. That is the one fact that separates a real network from an interface that
/// only exists.</para>
/// </summary>
internal static class MulticastNics
{
    /// <summary>Cards that are up and can carry multicast, but still cannot reach a network. They are
    /// named here rather than detected. <c>awdl0</c> is multicast-capable, so its name is the only
    /// sign that it is not a network.</summary>
    private static readonly string[] Private =
        ["awdl", "llw", "anpi", "ap1", "bridge", "utun", "ipsec", "gif", "stf", "XHC", "vmenet"];

    /// <summary>The filter given to the mDNS service. The service asks again whenever the machine's
    /// networks change. So a VPN that starts or a cable that is plugged in is handled this way,
    /// without a restart.</summary>
    public static IEnumerable<NetworkInterface> Usable(IEnumerable<NetworkInterface> all)
    {
        var kept = all.Where(nic => Carries(
            nic.Name,
            nic.NetworkInterfaceType,
            nic.GetIPProperties().UnicastAddresses.Select(a => a.Address)))
            .ToList();

        // Never return an empty list. If no card is recognised, announce on all of them anyway. A
        // failed send only writes a line to the log. Sharing that silently never starts is a worse
        // fault.
        return kept.Count > 0 ? kept : all;
    }

    /// <summary>Whether the multicast service should open an IPv6 sender at all.
    /// <para>False when the cards this machine will announce on have an IPv4 address between them. That
    /// is the usual case. On this app every IPv6 mDNS send fails with <c>HostUnreachable</c>. The
    /// sender is bound to <c>::</c> with no scope, so the machine has no route to <c>ff02::fb</c>. IPv6
    /// does not work sometimes here. It has never carried a packet, and every query logs one
    /// failure.</para>
    /// <para>True when no kept card has an IPv4 address. That is a network where IPv6 is the only
    /// option. A failed send is better than not asking at all.</para>
    /// </summary>
    /// <param name="cards">The cards to consider, each as its name, what the system says it is, and the
    /// addresses on it. These are the same three things that <see cref="Carries"/> is asked, so that
    /// this can be asked about a machine that is not the one running the test.</param>
    public static bool NeedsIPv6(
        IEnumerable<(string Name, NetworkInterfaceType Type, IReadOnlyList<IPAddress> Unicast)> cards)
    {
        var kept = cards.Where(card => Carries(card.Name, card.Type, card.Unicast)).ToList();

        // Nothing kept is the case Usable falls back on, which gives the service every card. So the
        // answer here matches it: no card was recognised, so no transport is ruled out.
        if (kept.Count == 0)
            return true;

        return !kept.Any(card => card.Unicast.Any(
            address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
    }

    /// <summary>The same, asked of this machine.</summary>
    public static bool NeedsIPv6()
    {
        try
        {
            return NeedsIPv6(NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                           && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(nic => (
                    nic.Name,
                    nic.NetworkInterfaceType,
                    (IReadOnlyList<IPAddress>)nic.GetIPProperties().UnicastAddresses
                        .Select(a => a.Address).ToList())));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;   // If the cards cannot be read, the IPv6 sender is opened
        }
    }

    /// <summary>
    /// Whether a card is on a network rather than merely present.
    /// </summary>
    /// <param name="name">The card's system name, e.g. <c>en0</c>.</param>
    /// <param name="type">What the system says it is. A tunnel or a dial-up link is point-to-point,
    /// and multicast on a point-to-point link reaches one machine that is not looking.</param>
    /// <param name="unicast">The addresses assigned to it.</param>
    public static bool Carries(string name, NetworkInterfaceType type, IEnumerable<IPAddress> unicast)
    {
        if (type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return false;

        foreach (var prefix in Private)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // An IPv4 address is enough, including a 169.254 address that no router handed out. A network
        // with no DHCP is the case mDNS exists for. Refusing it here would turn off sharing on exactly
        // the ad-hoc network where sharing is the only thing that works.
        //
        // An IPv6 address must be more than link-local. fe80:: is assigned to every card, even one
        // with no network at all, so it proves nothing.
        foreach (var address in unicast)
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return true;

            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && !address.IsIPv6LinkLocal)
                return true;
        }

        return false;
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RPM.Core.Net;

/// <summary>
/// Whether this machine is on a network at all. The app uses this to pick one of two messages when it
/// cannot reach the internet: "only what is on this disk", or
/// "this disk and the shared libraries on your network".
///
/// <para><see cref="NetworkInterface.GetIsNetworkAvailable"/> does not answer this question on a Mac. It
/// reports yes for a laptop with wifi switched off, because AWDL, the Thunderbolt bridge and a VPN tunnel
/// left behind are all up, and none of them is a loopback. An interface is a real network when it has an
/// address that other machines could send to. That is what this code checks.</para>
/// </summary>
public static class LocalNetwork
{
    /// <summary>Whether any interface on this machine is on a real local network.</summary>
    public static bool IsPresent()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addresses = nic.GetIPProperties().UnicastAddresses.Select(a => a.Address);
                if (CarriesALan(nic.OperationalStatus == OperationalStatus.Up, nic.NetworkInterfaceType, addresses))
                    return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // If the interfaces cannot be read, the machine is not declared offline. The probe still
            // decides, and its answer wins either way.
            return true;
        }

        return false;
    }

    /// <summary>
    /// The rule on its own, so it can be read and tested: an interface that is up, is not a loopback or a
    /// tunnel, and has an address that someone else on the network could send to.
    /// </summary>
    /// <remarks>
    /// The addresses that do not count are the ones a machine gives itself when there is nobody to give it
    /// one: an IPv6 link-local (which is what AWDL and an idle bridge have, and nothing else), and a
    /// 169.254 address, which means the DHCP request went unanswered.
    /// </remarks>
    public static bool CarriesALan(bool isUp, NetworkInterfaceType type, IEnumerable<IPAddress> unicast)
    {
        if (!isUp)
            return false;

        if (type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return false;

        foreach (var address in unicast)
        {
            if (IPAddress.IsLoopback(address))
                continue;

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6LinkLocal)
                    continue;
            }
            else if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var octets = address.GetAddressBytes();
                if (octets[0] == 169 && octets[1] == 254)
                    continue;
            }
            else
            {
                continue;
            }

            return true;
        }

        return false;
    }
}

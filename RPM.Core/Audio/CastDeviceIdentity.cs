using System.Net;

namespace RPM.Core.Audio;

/// <summary>
/// What makes a Cast device the same device.
///
/// <para>This has its own class and tests because a mistake here is hard to see. A speaker
/// <i>group</i> runs on one of its member speakers, at that member's IP address on a different port.
/// So one household can have three entries (a speaker and two groups) that share one address. If the
/// list is deduplicated by address, the three become one and the groups are lost.</para>
///
/// <para>The groups are the entries that must be kept. Google does not allow casting to a speaker that
/// is stereo-paired or in a group: every launch is answered with NOT_ALLOWED. The group is the only
/// thing that can be cast to. A list keyed by address would offer only the destinations that refuse
/// and hide the one that works.</para>
/// </summary>
public static class CastDeviceIdentity
{
    /// <summary>
    /// The stable key for a device: the id it advertises over mDNS or, if there is none,
    /// <c>address:port</c>. That is unique on the network, although it can change after a reboot.
    /// </summary>
    /// <param name="advertisedId">The <c>id</c> field from the device's mDNS record. May be null.</param>
    public static string For(string? advertisedId, IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        return string.IsNullOrWhiteSpace(advertisedId)
            ? $"{address}:{port}"
            : advertisedId.Trim();
    }
}

using System.Net;
using Makaretu.Dns;
using RPM.Core.Sharing;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// Saying that this machine is here, and noticing the ones that say the same.
///
/// <para>DNS-SD is the discovery protocol that printers and speakers use. Finding another RPM is the
/// same act on every platform, but the machinery under it is not. A desktop opens the multicast socket
/// itself. A phone asks the system's own Bonjour responder, because iOS does not allow the other
/// method. Both publish the record described in <see cref="PeerBeacon"/>, and both read the other's.</para>
///
/// <para>Nothing here is trusted. A beacon is gossip anybody on the network can write; what it buys
/// is a name, an address and a key to go and check properly.</para>
/// </summary>
public sealed partial class PeerDiscovery : IDisposable
{
    /// <summary>How often to ask the network again. mDNS answers are cached, and machines announce
    /// themselves when they arrive. This sweep covers the case where nobody was listening at that
    /// moment, such as a laptop that wakes on a network the others are already on.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<PeerDiscovery> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredPeer> _seen = new(StringComparer.OrdinalIgnoreCase);

    private ServiceDiscovery? _dns;
    private MulticastService? _mdns;
    private ServiceProfile? _profile;
    private CancellationTokenSource? _cts;

    public PeerDiscovery(ILogger<PeerDiscovery> log) => _log = log;

    /// <summary>Raised off the UI thread the first time a machine is seen. It is raised again whenever
    /// what the machine says about itself changes, such as a rename, a sign-in or a new address.</summary>
    public event Action<DiscoveredPeer>? Found;

    /// <summary>Raised with a device id when a machine says goodbye. It is not raised for a machine
    /// that simply vanishes, such as a closed laptop that sends nothing. For that case, its music stops
    /// being playable at the first read that fails.</summary>
    public event Action<string>? Lost;

    /// <summary>Every machine seen since this started.</summary>
    public IReadOnlyList<DiscoveredPeer> Seen
    {
        get { lock (_gate) return _seen.Values.ToList(); }
    }

    public bool IsRunning => _dns is not null;

    /// <summary>
    /// Starts announcing this machine and listening for others.
    /// </summary>
    /// <param name="identity">This install's key: what the beacon carries and what a peer's trust is
    /// written against.</param>
    /// <param name="port">Where <see cref="LibraryServer"/> is listening.</param>
    /// <param name="name">What to call this machine on somebody else's screen.</param>
    public void Start(PeerIdentity identity, int port, string name)
    {
        ArgumentNullException.ThrowIfNull(identity);

        lock (_gate)
        {
            if (_dns is not null)
                return;

            try
            {
                // The multicast service is made here, not by ServiceDiscovery's own constructor. That
                // constructor takes no card filter, so it would send on cards that cannot carry a
                // packet. See <see cref="MulticastNics"/>. This class starts and disposes the service.
                // ServiceDiscovery only disposes a service that it made itself.
                _mdns = new MulticastService(MulticastNics.Usable)
                {
                    // The IPv6 sender is opened only where there is no IPv4 to carry the announcement.
                    // Everywhere else it is a send that fails every time. See MulticastNics.NeedsIPv6.
                    // Set it before Start, because the service opens its sockets there.
                    UseIpv6 = MulticastNics.NeedsIPv6(),
                };
                _mdns.Start();

                _dns = new ServiceDiscovery(_mdns);
                _dns.ServiceInstanceDiscovered += OnDiscovered;
                _dns.ServiceInstanceShutdown += OnShutdown;

                Advertise(identity, port, name);

                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => SweepAsync(token));
            }
            catch (Exception ex)
            {
                // No multicast: a network that blocks it, or a machine whose local network permission
                // was refused. Sharing is simply unavailable, and the settings page says so.
                _log.LogWarning(ex, "[Sharing] Could not start on the local network");
                Stop();
            }
        }
    }

    /// <summary>
    /// Says the same thing again with something changed: the machine renamed. The old record is
    /// withdrawn first so that peers do not hold both. The beacon names no account (PeerBeacon), so
    /// signing in or out changes nothing in it.
    /// </summary>
    public void Republish(PeerIdentity identity, int port, string name)
    {
        lock (_gate)
        {
            if (_dns is null)
                return;

            try
            {
                if (_profile is { } old)
                    _dns.Unadvertise(old);

                Advertise(identity, port, name);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Sharing] Could not re-announce this machine");
            }
        }
    }

    /// <summary>Asks the network who is out there now, instead of waiting for the next sweep. The
    /// settings page's "look again" uses this.</summary>
    public void Query()
    {
        try { _dns?.QueryServiceInstances(SharingProtocol.ServiceType); }
        catch (Exception ex) { Said.Line("sharing", $"Query failed: {ex.Message}"); }
    }

    public void Stop()
    {
        ServiceDiscovery? dns;
        MulticastService? mdns;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            dns = _dns; _dns = null;
            mdns = _mdns; _mdns = null;
            cts = _cts; _cts = null;
            _profile = null;
            _seen.Clear();
        }

        try { cts?.Cancel(); } catch { }
        try { dns?.Unadvertise(); } catch { }
        try { dns?.Dispose(); } catch { }
        // Stop this one by hand, after the goodbye. ServiceDiscovery did not make it, so
        // ServiceDiscovery does not stop it.
        try { mdns?.Stop(); } catch { }
        try { mdns?.Dispose(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    private void Advertise(PeerIdentity identity, int port, string name)
    {
        // The instance name is the device id rather than the machine's name: two computers called
        // "MacBook Pro" on one network are two instances, and DNS-SD names have to differ. What the
        // user reads is the name in the TXT record.
        var profile = new ServiceProfile(identity.DeviceId, SharingProtocol.ServiceType, (ushort)port);

        foreach (var (key, value) in PeerBeacon.Announce(identity, name))
            profile.AddProperty(key, value);

        _dns!.Advertise(profile);
        _profile = profile;

        // Run off the calling thread, because the calling thread is often the UI thread.
        //
        // Advertise() only registers the profile. The two calls below put packets on the wire, and
        // the library does that by blocking on a Task. Makaretu's ServiceDiscovery.Announce ends in
        // Task.Wait(). On the UI thread that wait would freeze the window.
        //
        // Nothing reads a result from either call. Answers from the network come back on
        // ServiceInstanceDiscovered. The only thing given up by not waiting is knowing the moment the
        // packet left, and nothing needs that.
        var dns = _dns;
        Shell.Background.Run(() =>
        {
            // Check that this is still the live discovery. Sharing can be switched off or restarted
            // between this being queued and run. Stop() swaps _dns out under the lock and disposes it
            // outside the lock. So this reads the current value instead of assuming the order.
            lock (_gate)
            {
                if (!ReferenceEquals(_dns, dns))
                    return Task.CompletedTask;
            }

            try
            {
                dns.Announce(profile, 1);

                // Query right away as well as announcing. Machines already on the network do not
                // announce again just because this one arrived.
                dns.QueryServiceInstances(SharingProtocol.ServiceType);
            }
            catch (ObjectDisposedException)
            {
                // A beacon for something that has already gone is not a fault worth reporting.
            }
            catch (NullReferenceException)
            {
                // The same case, arriving as a different exception. Announce takes about a second,
                // because it waits between its two sends. A Stop() during that second disposes the
                // discovery mid-send. In net-mdns 0.38.0, ServiceDiscovery.Dispose sets its Mdns
                // property to null and does not mark itself disposed. Every later call then
                // dereferences that null.
            }

            return Task.CompletedTask;
        });
    }

    private async Task SweepAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await Cancellable.TickAsync(timer, cancellation))
        {
            // This runs fire-and-forget, so an exception would go nowhere and end the sweep. One
            // failed query would then leave this machine unable to see any peer for the rest of the
            // session. A network that refused one query is asked again on the next tick.
            try
            {
                Query();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[Sharing] Could not ask the network who is there");
            }
        }
    }

    private void OnDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            if (Read(e) is not { } peer)
                return;

            lock (_gate)
            {
                if (_seen.TryGetValue(peer.DeviceId, out var known) && known == peer)
                    return;

                _seen[peer.DeviceId] = peer;
            }

            Found?.Invoke(peer);
        }
        catch (Exception ex)
        {
            Said.Line("sharing", $"A beacon could not be read: {ex.Message}");
        }
    }

    private void OnShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        var instance = e.ServiceInstanceName.Labels.FirstOrDefault();
        if (string.IsNullOrEmpty(instance))
            return;

        lock (_gate)
        {
            if (!_seen.Remove(instance))
                return;
        }

        Lost?.Invoke(instance);
    }

    /// <summary>
    /// One announcement, turned into the machine it describes. The TXT record says who it is, the
    /// SRV record for its port, and an A record for where to find it.
    /// </summary>
    private static DiscoveredPeer? Read(ServiceInstanceDiscoveryEventArgs e)
    {
        var records = e.Message.Answers.Concat(e.Message.AdditionalRecords).ToList();
        var instance = e.ServiceInstanceName;

        if (records.OfType<SRVRecord>().FirstOrDefault(r => r.Name == instance) is not { } srv)
            return null;

        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records.OfType<TXTRecord>().Where(r => r.Name == instance))
        {
            foreach (var entry in record.Strings)
            {
                int equals = entry.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                    txt[entry[..equals]] = entry[(equals + 1)..];
            }
        }

        // IPv4 only. The address goes into a URL, and a link-local IPv6 address is not usable there
        // without its scope id.
        var address = records.OfType<ARecord>()
            .FirstOrDefault(r => r.Name == srv.Target)?.Address
            ?? (e.RemoteEndPoint?.Address is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } from
                ? from
                : null);

        return address is null ? null : PeerBeacon.Read(txt, address.ToString(), srv.Port);
    }

    public void Dispose() => Stop();
}

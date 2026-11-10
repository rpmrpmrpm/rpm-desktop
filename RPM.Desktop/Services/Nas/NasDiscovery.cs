using Makaretu.Dns;
using RPM.Core.Diagnostics;
using RPM.Core.Sharing;
using RPM.Core.Threading;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// Finds the rpm NAS machines on the local network: each NAS, and each library folder on it
/// (<see cref="SharingProtocol.NasServiceType"/>). It only listens and asks; it announces nothing.
///
/// <para>It has its own multicast service, separate from <see cref="PeerDiscovery"/>, because a NAS must
/// be found during first-run setup, before sharing is switched on. Several sockets on port 5353 are
/// allowed on every system rpm runs on; the system's own responder is one of them.</para>
/// </summary>
public sealed class NasDiscovery : IDisposable
{
    private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(30);

    /// <summary>An announcement not heard again for this long is dropped. Three missed questions.</summary>
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(100);

    private readonly ILogger<NasDiscovery> _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DiscoveredNas Nas, DateTime Seen)> _found = new(StringComparer.OrdinalIgnoreCase);
    private MulticastService? _mdns;
    private ServiceDiscovery? _dns;
    private CancellationTokenSource? _cts;

    public NasDiscovery(ILogger<NasDiscovery> log) => _log = log;

    /// <summary>Raised on a background thread when an announcement appears, changes or goes.</summary>
    public event Action? Changed;

    /// <summary>Everything heard recently.</summary>
    public IReadOnlyList<DiscoveredNas> Found
    {
        get
        {
            lock (_gate)
                return [.. _found.Values.Select(v => v.Nas)];
        }
    }

    /// <summary>The NAS machines as a whole, for the library location lists.</summary>
    public IReadOnlyList<DiscoveredNas> Machines => [.. Found.Where(n => n.Role is NasRole.Nas).OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The NAS announcement that serves the library with <paramref name="libraryId"/>, on this
    /// protocol version. Null when none is on the network.</summary>
    public DiscoveredNas? LibraryServing(string libraryId) =>
        Found.FirstOrDefault(n => n.Role is NasRole.Library && n.SameVersion &&
                                  string.Equals(n.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        lock (_gate)
        {
            if (_dns is not null)
                return;
            try
            {
                _mdns = new MulticastService(MulticastNics.Usable) { UseIpv6 = MulticastNics.NeedsIPv6() };
                _mdns.Start();
                _dns = new ServiceDiscovery(_mdns);
                _dns.ServiceInstanceDiscovered += OnDiscovered;
                _dns.ServiceInstanceShutdown += OnShutdown;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => AskAsync(token));
            }
            catch (Exception ex)
            {
                // No multicast on this network, or the local network permission was refused. No NAS
                // is offered; the local folder still works.
                _log.LogWarning(ex, "[Nas] Could not listen on the local network");
                StopCore();
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopCore();
    }

    private void StopCore()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _dns?.Dispose(); } catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException) { }
        try { _mdns?.Stop(); } catch (ObjectDisposedException) { }
        try { _mdns?.Dispose(); } catch (ObjectDisposedException) { }
        _cts?.Dispose();
        _cts = null;
        _dns = null;
        _mdns = null;
        _found.Clear();
    }

    /// <summary>Asks the network now, for a list that is on screen.</summary>
    public void Ask()
    {
        ServiceDiscovery? dns;
        lock (_gate)
            dns = _dns;
        Shell.Background.Run(() =>
        {
            try { dns?.QueryServiceInstances(SharingProtocol.NasServiceType); }
            catch (Exception ex) when (ex is ObjectDisposedException or NullReferenceException) { }
            return Task.CompletedTask;
        });
    }

    private async Task AskAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(AskEvery);
        Ask();
        while (await Cancellable.TickAsync(timer, cancellation))
        {
            Ask();
            Forget();
        }
    }

    private void Forget()
    {
        var cutoff = DateTime.UtcNow - ForgetAfter;
        bool changed;
        lock (_gate)
        {
            var old = _found.Where(p => p.Value.Seen < cutoff).Select(p => p.Key).ToList();
            foreach (var key in old)
                _found.Remove(key);
            changed = old.Count > 0;
        }

        if (changed)
            Changed?.Invoke();
    }

    private void OnDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            var instance = e.ServiceInstanceName.ToString();
            if (Read(e) is not { } nas)
                return;
            bool changed;
            lock (_gate)
            {
                changed = !_found.TryGetValue(instance, out var known) || !Same(known.Nas, nas);
                _found[instance] = (nas, DateTime.UtcNow);
            }

            if (changed)
                Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Said.Line("nas", $"A NAS announcement could not be read: {ex.Message}");
        }
    }

    private void OnShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        bool removed;
        lock (_gate)
            removed = _found.Remove(e.ServiceInstanceName.ToString());
        if (removed)
            Changed?.Invoke();
    }

    private static bool Same(DiscoveredNas a, DiscoveredNas b) =>
        a.DeviceId == b.DeviceId && a.Host == b.Host && a.Port == b.Port && a.Name == b.Name &&
        a.Version == b.Version && a.LibraryId == b.LibraryId && a.Smb == b.Smb;

    private static DiscoveredNas? Read(ServiceInstanceDiscoveryEventArgs e)
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
                var equals = entry.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                    txt[entry[..equals]] = entry[(equals + 1)..];
            }
        }

        // IPv4 only, as for computers: the address goes into a URL.
        var address = records.OfType<ARecord>().FirstOrDefault(r => r.Name == srv.Target)?.Address
            ?? (e.RemoteEndPoint?.Address is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } from ? from : null);
        return address is null ? null : NasBeacon.Read(txt, address.ToString(), srv.Port);
    }

    public void Dispose() => Stop();
}

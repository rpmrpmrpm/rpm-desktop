using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Nas;

/// <summary>
/// Which NAS machines serve which computer's library, as each computer's own catalogue says
/// (<see cref="PeerCatalog.Hosts"/>). A reader only plays from a NAS that a computer it trusts has named,
/// and stops when that computer's next catalogue no longer names it.
///
/// <para>Kept on disk, so a reader whose owner computer is switched off still knows where the NAS is
/// after a restart.</para>
/// </summary>
public sealed class NasRoutes
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, List<PeerHost>> _byOwner;

    public NasRoutes(string path)
    {
        _path = path;
        _byOwner = Load(path);
    }

    /// <summary>Replaces the hosts for <paramref name="ownerDeviceId"/> with the list from its latest
    /// catalogue. An empty list withdraws every NAS for that computer.</summary>
    public void Learn(string ownerDeviceId, IReadOnlyList<PeerHost> hosts)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerDeviceId);
        ArgumentNullException.ThrowIfNull(hosts);
        lock (_gate)
        {
            var known = _byOwner.TryGetValue(ownerDeviceId, out var list) ? list : [];
            if (known.SequenceEqual(hosts))
                return;
            if (hosts.Count == 0)
                _byOwner.Remove(ownerDeviceId);
            else
                _byOwner[ownerDeviceId] = [.. hosts];
            Save();
        }
    }

    public IReadOnlyList<PeerHost> HostsFor(string ownerDeviceId)
    {
        lock (_gate)
            return _byOwner.TryGetValue(ownerDeviceId, out var list) ? [.. list] : [];
    }

    /// <summary>Forgets a computer's hosts, when that computer is forgotten.</summary>
    public void Forget(string ownerDeviceId) => Learn(ownerDeviceId, []);

    /// <summary>
    /// The NAS to use for <paramref name="ownerDeviceId"/> now: the first host it named whose library
    /// announcement is on the network, on this protocol version, with the key it named. The key is the
    /// one from the host list, so the connection is pinned to what the trusted computer said.
    /// </summary>
    public DiscoveredNas? Reachable(string ownerDeviceId, IReadOnlyList<DiscoveredNas> found)
    {
        ArgumentNullException.ThrowIfNull(found);
        foreach (var host in HostsFor(ownerDeviceId))
        {
            if (SharingProtocol.FromBase64Url(host.PublicKey) is not { Length: > 0 } key)
                continue;
            var seen = found.FirstOrDefault(n =>
                n.Role is NasRole.Library && n.SameVersion &&
                string.Equals(n.DeviceId, host.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                n.PublicKey.AsSpan().SequenceEqual(key) &&
                string.Equals(n.LibraryId, host.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (seen is not null)
                return seen;
        }

        return null;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_byOwner, NasRoutesJson.Default.DictionaryStringListPeerHost));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory for this session; learned again from the next catalogue.
        }
    }

    private static Dictionary<string, List<PeerHost>> Load(string path)
    {
        try
        {
            if (File.Exists(path) &&
                JsonSerializer.Deserialize(File.ReadAllText(path), NasRoutesJson.Default.DictionaryStringListPeerHost) is { } read)
                return new Dictionary<string, List<PeerHost>>(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new Dictionary<string, List<PeerHost>>(StringComparer.OrdinalIgnoreCase);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, List<PeerHost>>))]
internal sealed partial class NasRoutesJson : JsonSerializerContext;

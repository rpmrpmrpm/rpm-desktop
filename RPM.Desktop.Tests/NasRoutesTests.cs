using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

public sealed class NasRoutesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rpm-nas-routes-" + Guid.NewGuid().ToString("N"), "nas-routes.json");
    private readonly PeerIdentity _nas = PeerIdentity.Create();
    private const string Library = "0123456789abcdef0123456789abcdef";

    public void Dispose()
    {
        _nas.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true); } catch (IOException) { }
    }

    private PeerHost Host() => new()
    {
        DeviceId = _nas.DeviceId,
        PublicKey = SharingProtocol.ToBase64Url(_nas.PublicKey),
        Name = "DiskStation",
        LibraryId = Library,
    };

    private DiscoveredNas Seen(byte[]? key = null, int version = SharingProtocol.Version, string library = Library,
        NasRole role = NasRole.Library) => new()
    {
        Role = role,
        DeviceId = _nas.DeviceId,
        Name = "Chivan",
        PublicKey = key ?? _nas.PublicKey,
        Host = "192.168.1.20",
        Port = 4000,
        Version = version,
        LibraryId = library,
    };

    [Fact]
    public void Hosts_from_a_catalogue_are_learned_and_kept_after_a_restart()
    {
        new NasRoutes(_path).Learn("OWNER", [Host()]);

        var again = new NasRoutes(_path);

        Assert.Equal(Host(), Assert.Single(again.HostsFor("OWNER")));
        Assert.Empty(again.HostsFor("SOMEONE ELSE"));
    }

    [Fact]
    public void Empty_hosts_withdraw_the_nas()
    {
        var routes = new NasRoutes(_path);
        routes.Learn("OWNER", [Host()]);

        routes.Learn("OWNER", []);

        Assert.Empty(routes.HostsFor("OWNER"));
        Assert.Null(routes.Reachable("OWNER", [Seen()]));
    }

    [Fact]
    public void Reachable_when_the_named_library_is_on_the_network()
    {
        var routes = new NasRoutes(_path);
        routes.Learn("OWNER", [Host()]);

        Assert.Equal(4000, routes.Reachable("OWNER", [Seen()])!.Port);
        Assert.Null(routes.Reachable("OWNER", []));
    }

    [Fact]
    public void Reachable_needs_the_key_library_role_and_version_the_owner_named()
    {
        var routes = new NasRoutes(_path);
        routes.Learn("OWNER", [Host()]);
        using var other = PeerIdentity.Create();

        Assert.Null(routes.Reachable("OWNER", [Seen(key: other.PublicKey)]));
        Assert.Null(routes.Reachable("OWNER", [Seen(version: SharingProtocol.Version + 1)]));
        Assert.Null(routes.Reachable("OWNER", [Seen(library: "ffffffffffffffffffffffffffffffff")]));
        Assert.Null(routes.Reachable("OWNER", [Seen(role: NasRole.Nas)]));
    }

    [Fact]
    public void Forgetting_a_computer_forgets_its_nas()
    {
        var routes = new NasRoutes(_path);
        routes.Learn("OWNER", [Host()]);

        routes.Forget("OWNER");

        Assert.Empty(new NasRoutes(_path).HostsFor("OWNER"));
    }
}

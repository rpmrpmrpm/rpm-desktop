using System.Text.Json;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>The NAS additions to the sharing protocol: they must leave the wire unchanged for a computer,
/// and must change a catalogue's version when the NAS a library is on changes.</summary>
public class NasWireTests
{
    private static PeerTrack Track(string id) => new() { RpmId = id, RelativePath = id + ".flac" };

    [Fact]
    public void Hello_without_nas_fields_is_unchanged_on_the_wire()
    {
        var hello = new PeerHello { DeviceId = "ID", Name = "Mac", PublicKey = "KEY", TrackCount = 3 };

        var json = JsonSerializer.Serialize(hello, SharingJson.Default.PeerHello);

        Assert.Equal("""{"version":4,"deviceId":"ID","name":"Mac","publicKey":"KEY","trackCount":3}""", json);
    }

    [Fact]
    public void Hello_from_a_nas_reads_its_space_and_libraries()
    {
        const string json = """
            {"version":4,"deviceId":"ID","name":"DiskStation","publicKey":"KEY","trackCount":0,
             "nas":{"smb":"smb://ds/music/rpm","usedBytes":10,"freeBytes":20,
                    "libraries":[{"folder":"Chivan","libraryId":"abc","name":"Chivan","account":"fp","deviceId":"D","publicKey":"K"}]}}
            """;

        var hello = JsonSerializer.Deserialize(json, SharingJson.Default.PeerHello)!;

        Assert.Null(hello.LibraryId);
        Assert.Null(hello.Nas!.LimitBytes);
        Assert.Equal(20, hello.Nas.FreeBytes);
        Assert.Equal("fp", Assert.Single(hello.Nas.Libraries).Account);
    }

    [Fact]
    public void A_host_change_changes_the_catalogue_version()
    {
        PeerTrack[] tracks = [Track("a")];
        var host = new PeerHost { DeviceId = "NAS1", PublicKey = "K", Name = "DiskStation", LibraryId = "L" };

        var without = PeerCatalog.VersionOf(tracks, [], [], [], []);
        var with = PeerCatalog.VersionOf(tracks, [], [], [], [host]);

        Assert.NotEqual(without, with);
        Assert.Equal(with, PeerCatalog.VersionOf(tracks, [], [], [], [host]));
    }

    [Fact]
    public void Beacon_with_mismatched_id_is_dropped()
    {
        using var identity = PeerIdentity.Create();
        var txt = new Dictionary<string, string>
        {
            ["v"] = "4",
            ["id"] = "NOTTHEID",
            ["n"] = "DiskStation",
            ["k"] = SharingProtocol.ToBase64Url(identity.PublicKey),
            ["r"] = "nas",
        };

        Assert.Null(NasBeacon.Read(txt, "nas.local", 1234));

        txt["id"] = identity.DeviceId;
        var nas = NasBeacon.Read(txt, "nas.local", 1234)!;
        Assert.Equal(NasRole.Nas, nas.Role);
        Assert.True(nas.SameVersion);
    }

    [Fact]
    public void Library_beacon_without_a_library_id_is_dropped()
    {
        using var identity = PeerIdentity.Create();
        var txt = new Dictionary<string, string>
        {
            ["v"] = "4",
            ["id"] = identity.DeviceId,
            ["k"] = SharingProtocol.ToBase64Url(identity.PublicKey),
            ["r"] = "library",
        };

        Assert.Null(NasBeacon.Read(txt, "nas.local", 1234));
        txt["l"] = "0123456789abcdef0123456789abcdef";
        Assert.Equal("0123456789abcdef0123456789abcdef", NasBeacon.Read(txt, "nas.local", 1234)!.LibraryId);
    }

    [Fact]
    public void Publish_serialises_kind_as_a_number()
    {
        var catalog = PeerCatalogBuilder.Catalog([], "x", [], [], [], []);
        var publish = new NasPublish
        {
            Owner = catalog,
            Guest = catalog,
            Trust = [new NasTrusted { PublicKey = "K", Kind = PeerKind.Guest }],
        };

        var json = JsonSerializer.Serialize(publish, SharingJson.Default.NasPublish);

        Assert.Contains("\"kind\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"hosts\":[]", json, StringComparison.Ordinal);
    }
}

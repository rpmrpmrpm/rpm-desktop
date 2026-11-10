using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

/// <summary>Runs when <c>RPM_TEST_NAS_DAEMON</c> names a built <c>rpm-collection-daemon</c>, on a machine
/// whose network carries multicast.</summary>
public sealed class NasDaemonLiveFactAttribute : FactAttribute
{
    public static string? Binary =>
        Environment.GetEnvironmentVariable("RPM_TEST_NAS_DAEMON") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

    public NasDaemonLiveFactAttribute()
    {
        if (Binary is null)
            Skip = "Set RPM_TEST_NAS_DAEMON to a built rpm-collection-daemon to run this.";
    }
}

/// <summary>
/// The desktop's multicast DNS library against the daemon's. The two are different implementations,
/// and the desktop needs the address, the port and the TXT record from one answer.
/// </summary>
public sealed class NasDiscoveryLiveTests : IDisposable
{
    private const string LibraryId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rpm-nas-disc-" + Guid.NewGuid().ToString("N"));
    private Process? _daemon;

    public void Dispose()
    {
        try
        {
            if (_daemon is { HasExited: false })
                _daemon.Kill();
            _daemon?.Dispose();
        }
        catch (InvalidOperationException)
        {
        }

        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    [NasDaemonLiveFact]
    public async Task Finds_the_nas_and_its_library_with_their_records()
    {
        var folder = Path.Combine(_temp, "root", "Chivan");
        Directory.CreateDirectory(Path.Combine(folder, ".rpm"));
        File.WriteAllText(Path.Combine(folder, ".rpm", "library.json"), $"{{\"id\":\"{LibraryId}\"}}\n");
        var name = "LiveNAS" + Random.Shared.Next(1000, 9999);
        var psi = new ProcessStartInfo(NasDaemonLiveFactAttribute.Binary!)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.Environment["RPM_ROOT"] = Path.Combine(_temp, "root");
        psi.Environment["RPM_DATA"] = Path.Combine(_temp, "data");
        psi.Environment["RPM_SMB_SHARE"] = "smb://live.local/music/rpm";
        psi.Environment["RPM_NAME"] = name;
        _daemon = Process.Start(psi)!;

        using var discovery = new NasDiscovery(NullLogger<NasDiscovery>.Instance);
        discovery.Start();

        DiscoveredNas? nas = null, library = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && (nas is null || library is null))
        {
            discovery.Ask();
            await Task.Delay(500);
            nas = discovery.Machines.FirstOrDefault(n => n.Name == name);
            library = discovery.LibraryServing(LibraryId);
        }

        Assert.NotNull(nas);
        Assert.Equal("smb://live.local/music/rpm", nas!.Smb);
        Assert.True(nas.SameVersion);
        Assert.NotNull(library);
        Assert.True(library!.Port > 0);

        // The address it gives must reach the daemon, with the key it gives.
        using var identity = PeerIdentity.Create();
        using var client = new PeerClient(identity, library.Host, library.Port, library.PublicKey, () => "");
        var hello = await client.HelloAsync();
        Assert.Equal(LibraryId, hello!.LibraryId);
    }
}

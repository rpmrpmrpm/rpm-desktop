using System.Diagnostics;
using System.Net.Http.Json;
using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

/// <summary>
/// Runs on macOS against a running daemon and the Samba share of the same folder:
/// <c>RPM_TEST_SMB</c> (<c>smb://host:port/share|user|password</c>), <c>RPM_TEST_NAS</c> (the NAS peer, for
/// example <c>https://127.0.0.1:47100</c>) and <c>RPM_TEST_NAS_LIBRARY_PORT</c> (the port the daemon gives the
/// first library, with a fixed <c>RPM_PORT</c>).
/// </summary>
public sealed class NasEndToEndFactAttribute : FactAttribute
{
    public static string? Nas => Environment.GetEnvironmentVariable("RPM_TEST_NAS") is { Length: > 0 } v ? v : null;

    public static int LibraryPort =>
        int.TryParse(Environment.GetEnvironmentVariable("RPM_TEST_NAS_LIBRARY_PORT"), out var port) ? port : 0;

    public NasEndToEndFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || SmbLiveFactAttribute.Parts is null || Nas is null || LibraryPort == 0)
            Skip = "Set RPM_TEST_SMB, RPM_TEST_NAS and RPM_TEST_NAS_LIBRARY_PORT on macOS to run this.";
    }
}

/// <summary>The steps of setting up a library on a NAS, with the real mount, the real claim written over
/// SMB, the real daemon reading it, and the real upload.</summary>
public sealed class NasEndToEndLiveTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rpm-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly PeerIdentity _owner = PeerIdentity.Create();
    private string? _mounted;
    private string? _library;

    public void Dispose()
    {
        try { if (_library is not null) Directory.Delete(_library, recursive: true); } catch (IOException) { }
        if (_mounted is not null)
        {
            using var umount = Process.Start(new ProcessStartInfo("umount", _mounted) { UseShellExecute = false });
            umount?.WaitForExit(10_000);
        }

        _owner.Dispose();
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    /// <summary>The NAS's hello, read without pinning: this test does not know the NAS key beforehand.</summary>
    private static async Task<PeerHello> NasHelloAsync()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler);
        return (await http.GetFromJsonAsync(NasEndToEndFactAttribute.Nas + "/rpm/hello", SharingJson.Default.PeerHello))!;
    }

    [NasEndToEndFact]
    public async Task A_library_claimed_over_smb_is_served_and_takes_the_owners_upload()
    {
        var parts = SmbLiveFactAttribute.Parts!;
        var address = NasSmbAddress.Parse(parts[0])!;

        // 1. Mount the share hidden, as onboarding does.
        var mounted = Assert.IsType<NasMountResult.Mounted>(
            new MacNetFsMount(Path.Combine(_temp, "mounts")).Mount("E2E", address, new NasCredentials(parts[1], parts[2])));
        _mounted = mounted.ShareRoot;

        // 2. The library folder, its id and the claim, written through the mount, as onboarding does.
        var hello = await NasHelloAsync();
        var folder = NasLibraryChoice.NewFolderName("End to end", [.. hello.Nas!.Libraries.Select(l => l.Folder)]);
        _library = address.LibraryPath(mounted.ShareRoot, folder);
        Directory.CreateDirectory(_library);
        Assert.True(NasMountPoint.IsLiveNetworkMount(_library));
        var libraryId = NasLibrary.EnsureId(_library);
        NasClaim.AddKey(_library, new NasClaimKey
        {
            PublicKey = SharingProtocol.ToBase64Url(_owner.PublicKey),
            Name = "End to end",
            Account = NasClaim.AccountFingerprint("user-e2e"),
        });
        File.WriteAllBytes(Path.Combine(_library, "01.flac"), [1, 2, 3, 4]);

        // 3. The daemon finds the library within its rescan and lists it with the claim's name and account.
        NasLibraryEntry? entry = null;
        for (var i = 0; i < 30 && entry is null; i++)
        {
            await Task.Delay(1000);
            entry = (await NasHelloAsync()).Nas!.Libraries.FirstOrDefault(l => l.LibraryId == libraryId);
        }

        Assert.NotNull(entry);
        Assert.Equal("End to end", entry!.Name);
        Assert.Equal(NasClaim.AccountFingerprint("user-e2e"), entry.Account);
        Assert.Equal(entry, NasLibraryChoice.Choose((await NasHelloAsync()).Nas!, NasClaim.AccountFingerprint("user-e2e")));

        // 4. The owner uploads, pinned to the key the NAS lists, and the catalogue and the track are served.
        using var client = new PeerClient(_owner, "127.0.0.1", NasEndToEndFactAttribute.LibraryPort,
            SharingProtocol.FromBase64Url(entry.PublicKey)!, () => "user-e2e");
        Assert.Equal(libraryId, (await client.HelloAsync())!.LibraryId);
        var track = new PeerTrack { RpmId = "RPM-E2E", RelativePath = "01.flac" };
        var catalogue = new PeerCatalog { Version = PeerCatalog.VersionOf([track], [], [], [], []), Tracks = [track] };
        Assert.True(await client.PublishAsync(new NasPublish { Owner = catalogue, Guest = catalogue, Trust = [] }, CancellationToken.None));
        Assert.Equal(catalogue.Version, (await client.CatalogAsync())!.Version);
        Assert.True(await client.HasTrackAsync("RPM-E2E", CancellationToken.None));
        Assert.False(await client.HasTrackAsync("RPM-MISSING", CancellationToken.None));
    }
}

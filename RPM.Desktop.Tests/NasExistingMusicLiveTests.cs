using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using RPM.Core.Library;
using RPM.Core.Nas;
using RPM.Core.Sharing;
using RPM.Desktop.Services.Nas;

namespace RPM.Desktop.Tests;

/// <summary>
/// A NAS folder that already holds music, made a library with its files kept as they are, over the real
/// mount and the real daemon. Runs with the same settings as <see cref="NasEndToEndLiveTests"/>, against a
/// share whose folder holds music and no library. The folder is put back as it was afterwards.
/// </summary>
public sealed class NasExistingMusicLiveTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "rpm-kept-nas-" + Guid.NewGuid().ToString("N"));
    private string? _mounted;

    public void Dispose()
    {
        if (_mounted is not null)
        {
            KeptFiles.Close(_mounted);
            try { Directory.Delete(Path.Combine(_mounted, ".rpm"), recursive: true); } catch (IOException) { }
            using var umount = Process.Start(new ProcessStartInfo("umount", _mounted) { UseShellExecute = false });
            umount?.WaitForExit(10_000);
        }

        try { Directory.Delete(_local, recursive: true); } catch (IOException) { }
    }

    private static async Task<NasHello> NasAsync()
    {
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        using var http = new HttpClient(handler);
        var hello = await http.GetFromJsonAsync(NasEndToEndFactAttribute.Nas + "/rpm/hello", SharingJson.Default.PeerHello);
        return hello!.Nas!;
    }

    private static Dictionary<string, string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .Where(p => !p.StartsWith(".rpm", StringComparison.Ordinal))
            .ToDictionary(
                p => p,
                p => File.Exists(Path.Combine(root, p))
                    ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, p))))
                    : "folder");

    [NasEndToEndFact]
    public async Task Existing_music_kept_as_it_is_is_read_over_smb_and_served_by_the_daemon_unchanged()
    {
        var parts = SmbLiveFactAttribute.Parts!;
        var address = NasSmbAddress.Parse(parts[0])!;
        Assert.True((await NasAsync()).ExistingMusic, "the test share must hold music and no library");

        var mounted = Assert.IsType<NasMountResult.Mounted>(
            new MacNetFsMount().Mount(address, new NasCredentials(parts[1], parts[2])));
        _mounted = mounted.ShareRoot;
        var root = address.LibraryPath(mounted.ShareRoot, "");
        Assert.Equal(LibraryFolderKind.HasMusic, LibraryFolder.Classify(root));
        var before = Snapshot(root);

        // What NasLibraryService.PrepareAsync does for "keep": the marker first, then the id and the claim.
        FileKeeping.Write(root, null, FileHandling.Kept);
        var libraryId = NasLibrary.EnsureId(root);
        using var owner = PeerIdentity.Create();
        NasClaim.AddKey(root, new NasClaimKey
        {
            PublicKey = SharingProtocol.ToBase64Url(owner.PublicKey),
            Name = "Kept music",
            Account = "",
        });

        // What LibraryService.Open does: the guard first, then the database on this computer.
        KeptFiles.Open(root);
        using (var db = new LibraryDatabase(MirrorLocation.For(root, _local)))
        {
            Assert.Equal(FileHandling.Kept, FileKeeping.Read(root, db));
            LibrarySynchronizer.Sync(db, quarantineForeign: true);
            LibrarySynchronizer.Sync(db, quarantineForeign: true);

            var rows = db.Query();
            Assert.Equal(3, rows.Count);
            Assert.Equal(3, rows.Select(r => r.RpmId).Distinct().Count());
            var song = rows.Single(r => r.Title == "Morning Swell");
            Assert.Equal("Boards Ladder", song.Artist);
            Assert.Equal(KeptIds.For(libraryId, song.RelativePath), song.RpmId);
        }

        // Every entry that was there is unchanged. The only thing added at the top is rpm's own .rpm folder,
        // and, on a share that cannot hold extended attributes, the ._.rpm file macOS keeps them in.
        var after = Snapshot(root);
        foreach (var (path, hash) in before)
            Assert.Equal(hash, after[path]);
        Assert.All(after.Keys.Except(before.Keys), added => Assert.Equal("._.rpm", added));

        // The daemon rescans every ten seconds: it then serves the folder itself as the library.
        NasHello nas = await NasAsync();
        for (var i = 0; i < 30 && nas.Libraries.Count == 0; i++)
        {
            await Task.Delay(500);
            nas = await NasAsync();
        }

        Assert.False(nas.ExistingMusic);
        var library = Assert.Single(nas.Libraries);
        Assert.Equal("", library.Folder);
        Assert.Equal(libraryId, library.LibraryId);
        Assert.Equal("Kept music", library.Name);
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;
using RPM.Core.Nas;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Runs when <c>RPM_TEST_NAS_DAEMON</c> names a built <c>rpm-collection-daemon</c> binary, and is skipped
/// otherwise. Build it with <c>cargo build -p rpm-daemon</c> in the rpm-collection-daemon repo.
/// </summary>
public sealed class NasDaemonFactAttribute : FactAttribute
{
    public static string? Binary =>
        Environment.GetEnvironmentVariable("RPM_TEST_NAS_DAEMON") is { Length: > 0 } path && File.Exists(path)
            ? path
            : null;

    public NasDaemonFactAttribute()
    {
        if (Binary is null)
            Skip = "Set RPM_TEST_NAS_DAEMON to a built rpm-collection-daemon to run this.";
    }
}

/// <summary>
/// rpm's own peer client against the NAS daemon, which is a second implementation of the server half of
/// the protocol in another language. Where the two disagree, the daemon is fixed, not this test.
/// </summary>
public sealed partial class NasDaemonCompatibilityTests : IDisposable
{
    private const string LibraryId = "0123456789abcdef0123456789abcdef";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "rpm-nas-compat-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly PeerIdentity _owner = PeerIdentity.Create();
    private readonly PeerIdentity _guest = PeerIdentity.Create();
    private Process? _daemon;

    public NasDaemonCompatibilityTests()
    {
        _folder = Path.Combine(_temp, "root", "Chivan");
        Directory.CreateDirectory(Path.Combine(_folder, ".rpm"));
        File.WriteAllText(Path.Combine(_folder, ".rpm", "library.json"), $"{{\"id\":\"{LibraryId}\"}}\n");
        NasClaim.AddKey(_folder, new NasClaimKey
        {
            PublicKey = SharingProtocol.ToBase64Url(_owner.PublicKey),
            Name = "Chivan",
            Account = NasClaim.AccountFingerprint("user-1"),
        });
    }

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
            // It ended on its own.
        }

        _owner.Dispose();
        _guest.Dispose();
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { }
    }

    [GeneratedRegex(@"on port (\d+)")]
    private static partial Regex PortLine();

    /// <summary>Starts the daemon and returns the NAS's port and the library's port.</summary>
    private async Task<(int Nas, int Library)> StartAsync()
    {
        var psi = new ProcessStartInfo(NasDaemonFactAttribute.Binary!)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.Environment["RPM_ROOT"] = Path.Combine(_temp, "root");
        psi.Environment["RPM_DATA"] = Path.Combine(_temp, "data");
        psi.Environment["RPM_SMB_SHARE"] = "smb://test.local/music/rpm";
        psi.Environment["RPM_NAME"] = "TestNAS";
        psi.Environment["RPM_LOG"] = "info";
        _daemon = Process.Start(psi)!;

        var ports = new List<int>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (ports.Count < 2)
        {
            var line = await _daemon.StandardError.ReadLineAsync(deadline.Token)
                ?? throw new InvalidOperationException("The daemon stopped before it was listening.");
            if (PortLine().Match(line) is { Success: true } match)
                ports.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return (ports[0], ports[1]);
    }

    private byte[] LibraryKey()
    {
        var pkcs8 = File.ReadAllBytes(Path.Combine(_temp, "data", LibraryId, "peer.key"));
        using var identity = PeerIdentity.Import(pkcs8);
        return identity.PublicKey;
    }

    private PeerClient Client(PeerIdentity who, int port) =>
        new(who, "127.0.0.1", port, LibraryKey(), () => "user-1");

    private static PeerCatalog Catalogue(string name, params PeerTrack[] tracks) => new()
    {
        Version = PeerCatalog.VersionOf(tracks, [], [], [], []),
        Name = name,
        Tracks = tracks,
    };

    private NasPublish Upload(PeerCatalog owner, PeerCatalog guest) => new()
    {
        Owner = owner,
        Guest = guest,
        Trust = [new NasTrusted { PublicKey = SharingProtocol.ToBase64Url(_guest.PublicKey), Kind = PeerKind.Guest }],
    };

    [NasDaemonFact]
    public async Task Hello_reports_the_library_id_and_the_nas_lists_it()
    {
        var (nas, library) = await StartAsync();
        using var libraryClient = Client(_owner, library);
        var hello = await libraryClient.HelloAsync();

        Assert.Equal(LibraryId, hello!.LibraryId);
        Assert.Equal("smb://test.local/music/rpm", hello.Nas!.Smb);

        // The NAS's own peer has its own key. Read it from its key file to pin it.
        using var nasIdentity = PeerIdentity.Import(File.ReadAllBytes(Path.Combine(_temp, "data", "nas.key")));
        using var nasClient = new PeerClient(_owner, "127.0.0.1", nas, nasIdentity.PublicKey, () => "");
        var nasHello = await nasClient.HelloAsync();
        var entry = Assert.Single(nasHello!.Nas!.Libraries);
        Assert.Equal("Chivan", entry.Name);
        Assert.Equal(NasClaim.AccountFingerprint("user-1"), entry.Account);
        Assert.Equal(hello.DeviceId, entry.DeviceId);
    }

    [NasDaemonFact]
    public async Task Session_then_publish_then_catalogue_round_trips()
    {
        var (_, library) = await StartAsync();
        var track = new PeerTrack { RpmId = "RPM0001", RelativePath = "Artist/Album/01.flac", Title = "One" };
        var owner = Catalogue("owner copy", track);
        var guest = Catalogue("guest copy", track) with { Version = "guest-version" };

        using var ownerClient = Client(_owner, library);
        Assert.True(await ownerClient.PublishAsync(Upload(owner, guest), CancellationToken.None));

        var asOwner = await ownerClient.CatalogAsync();
        Assert.Equal(owner.Version, asOwner!.Version);
        Assert.Equal("owner copy", asOwner.Name);
        Assert.Equal("One", Assert.Single(asOwner.Tracks).Title);
        Assert.Equal(PeerKind.Account, ownerClient.Kind);

        using var guestClient = Client(_guest, library);
        var asGuest = await guestClient.CatalogAsync();
        Assert.Equal("guest-version", asGuest!.Version);
        Assert.Equal(PeerKind.Guest, guestClient.Kind);
    }

    [NasDaemonFact]
    public async Task Catalogue_with_current_version_is_unchanged()
    {
        var (_, library) = await StartAsync();
        var owner = Catalogue("x");
        using var client = Client(_owner, library);
        await client.PublishAsync(Upload(owner, owner), CancellationToken.None);

        Assert.Null(await client.CatalogAsync(owner.Version));
        Assert.NotNull(await client.CatalogAsync("older"));
    }

    [NasDaemonFact]
    public async Task Guest_cannot_publish()
    {
        var (_, library) = await StartAsync();
        using var ownerClient = Client(_owner, library);
        await ownerClient.PublishAsync(Upload(Catalogue("x"), Catalogue("x")), CancellationToken.None);

        using var guestClient = Client(_guest, library);
        Assert.False(await guestClient.PublishAsync(Upload(Catalogue("y"), Catalogue("y")), CancellationToken.None));
    }

    [NasDaemonFact]
    public async Task Untrusted_key_gets_no_session()
    {
        var (_, library) = await StartAsync();
        using var stranger = PeerIdentity.Create();
        using var client = Client(stranger, library);

        Assert.Null(await client.TokenAsync());
    }

    [NasDaemonFact]
    public async Task Track_streams_whole_and_from_mid_file_through_ffmpeg()
    {
        if (!Probe("ffmpeg") || !Probe("ffprobe"))
            return;
        var album = Path.Combine(_folder, "Artist", "Album");
        Directory.CreateDirectory(album);
        var file = Path.Combine(album, "01.flac");
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=4", file);

        var (_, library) = await StartAsync();
        var track = new PeerTrack { RpmId = "RPM0001", RelativePath = "Artist/Album/01.flac" };
        using var ownerClient = Client(_owner, library);
        await ownerClient.PublishAsync(Upload(Catalogue("x", track), Catalogue("x", track)), CancellationToken.None);

        using var guestClient = Client(_guest, library);
        var url = await guestClient.TrackUrlAsync("RPM0001");
        Assert.NotNull(url);

        var whole = Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", url!);
        Assert.InRange(double.Parse(whole, System.Globalization.CultureInfo.InvariantCulture), 3.5, 4.5);

        var tail = Path.Combine(_temp, "tail.wav");
        Run("ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-ss", "3", "-i", url!, tail);
        var rest = Run("ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", tail);
        Assert.InRange(double.Parse(rest, System.Globalization.CultureInfo.InvariantCulture), 0.5, 1.5);
    }

    [NasDaemonFact]
    public async Task Cover_comes_from_the_folder_image_and_art_from_an_upload()
    {
        var album = Path.Combine(_folder, "Album");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "01.flac"), [1, 2, 3]);
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
        File.WriteAllBytes(Path.Combine(album, "cover.png"), png);

        var (_, library) = await StartAsync();
        var track = new PeerTrack { RpmId = "RPM0001", RelativePath = "Album/01.flac" };
        using var ownerClient = Client(_owner, library);
        await ownerClient.PublishAsync(Upload(Catalogue("x", track), Catalogue("x", track)), CancellationToken.None);
        Assert.True(await ownerClient.PutArtAsync("pl:12", png, CancellationToken.None));

        using var guestClient = Client(_guest, library);
        Assert.Equal(png, await guestClient.ImageAsync(SharingProtocol.CoverRoute, "RPM0001"));
        Assert.Equal(png, await guestClient.ImageAsync(SharingProtocol.ArtRoute, "pl:12"));
        Assert.Null(await guestClient.ImageAsync(SharingProtocol.DiscRoute, "RPM0001"));
    }

    private static bool Probe(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-version");
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        var error = p.StandardError.ReadToEnd().Trim();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"{exe} failed ({p.ExitCode}): {error}");
        return output;
    }
}

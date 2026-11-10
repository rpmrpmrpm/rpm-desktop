using System.Text.Json;
using RPM.Core.Nas;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

public sealed class NasClaimTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpm-nas-claim-" + Guid.NewGuid().ToString("N"));

    public NasClaimTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Second_key_is_added_not_replacing_the_first()
    {
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = "A", Name = "Chivan", Account = "fp" });
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = "B", Name = "Chivan", Account = "fp" });

        var claim = NasClaim.Read(_root)!;

        Assert.Equal(["A", "B"], claim.Keys.Select(k => k.PublicKey));
    }

    [Fact]
    public void Same_key_is_written_once()
    {
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = "A", Name = "Old" });
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = "A", Name = "New" });

        var key = Assert.Single(NasClaim.Read(_root)!.Keys);
        Assert.Equal("New", key.Name);
    }

    [Fact]
    public void File_has_the_shape_the_daemon_reads()
    {
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = "A", Name = "Chivan", Account = "fp" });

        var json = File.ReadAllText(Path.Combine(_root, ".rpm", "daemon-owner.json"));

        Assert.Equal("""{"keys":[{"publicKey":"A","name":"Chivan","account":"fp"}]}""", json);
    }

    [Fact]
    public void Holds_compares_decoded_keys()
    {
        using var identity = PeerIdentity.Create();
        NasClaim.AddKey(_root, new NasClaimKey { PublicKey = SharingProtocol.ToBase64Url(identity.PublicKey) });

        Assert.True(NasClaim.Read(_root)!.Holds(identity.PublicKey));
        using var other = PeerIdentity.Create();
        Assert.False(NasClaim.Read(_root)!.Holds(other.PublicKey));
    }

    [Fact]
    public void Unreadable_file_reads_as_no_claim()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".rpm"));
        File.WriteAllText(NasClaim.PathIn(_root), "not json");

        Assert.Null(NasClaim.Read(_root));
    }

    [Fact]
    public void Fingerprint_matches_the_vector_file()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sharing", "peer-vectors.json");
        var v = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

        Assert.Equal(
            v.GetProperty("accountFingerprint").GetString(),
            NasClaim.AccountFingerprint(v.GetProperty("accountFingerprintUserId").GetString()!));
    }
}

public class NasLibraryChoiceTests
{
    private static NasHello Hello(params (string Folder, string Account)[] libraries) => new()
    {
        Libraries = [.. libraries.Select(l => new NasLibraryEntry { Folder = l.Folder, Account = l.Account, LibraryId = l.Folder })],
    };

    [Fact]
    public void Choose_finds_the_library_with_this_account()
    {
        var hello = Hello(("Gianni", "fp-g"), ("Chivan", "fp-c"));

        Assert.Equal("Chivan", NasLibraryChoice.Choose(hello, "fp-c")!.Folder);
        Assert.Null(NasLibraryChoice.Choose(hello, "fp-other"));
    }

    [Fact]
    public void Choose_returns_null_when_signed_out()
    {
        var hello = Hello(("Unclaimed", ""));

        Assert.Null(NasLibraryChoice.Choose(hello, null));
        Assert.Null(NasLibraryChoice.Choose(hello, ""));
    }

    [Fact]
    public void New_folder_name_avoids_taken_names_case_insensitively()
    {
        Assert.Equal("Chivan", NasLibraryChoice.NewFolderName("Chivan", ["Gianni"]));
        Assert.Equal("Chivan 2", NasLibraryChoice.NewFolderName("Chivan", ["chivan"]));
        Assert.Equal("Chivan 3", NasLibraryChoice.NewFolderName("Chivan", ["Chivan", "CHIVAN 2"]));
    }

    [Fact]
    public void New_folder_name_strips_characters_smb_refuses()
    {
        Assert.Equal("Chivan's Mac", NasLibraryChoice.NewFolderName("Chivan's Mac", []));
        Assert.Equal("AB", NasLibraryChoice.NewFolderName("A/B:", []));
        Assert.Equal("Name", NasLibraryChoice.NewFolderName("Name. ", []));
        Assert.Equal("rpm", NasLibraryChoice.NewFolderName("???", []));
    }
}

public class NasSpaceTests
{
    private static NasHello Hello(long used, long free, long? limit) =>
        new() { UsedBytes = used, FreeBytes = free, LimitBytes = limit };

    [Fact]
    public void Space_full_when_incoming_passes_limit() =>
        Assert.Equal(NasSpaceAnswer.Full, NasSpace.Check(Hello(900, 10_000, 1000), 101));

    [Fact]
    public void Space_full_when_incoming_passes_free_space_without_limit() =>
        Assert.Equal(NasSpaceAnswer.Full, NasSpace.Check(Hello(0, 50, null), 51));

    [Fact]
    public void Space_almost_full_over_90_percent()
    {
        Assert.Equal(NasSpaceAnswer.AlmostFull, NasSpace.Check(Hello(850, 10_000, 1000), 60));
        Assert.Equal(NasSpaceAnswer.Fits, NasSpace.Check(Hello(800, 10_000, 1000), 100));
    }

    [Fact]
    public void Space_fits_without_a_limit_when_the_disk_has_room() =>
        Assert.Equal(NasSpaceAnswer.Fits, NasSpace.Check(Hello(10, 1000, null), 999));
}

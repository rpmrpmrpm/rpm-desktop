using RPM.Core.Files;
using RPM.Core.Scanning;

namespace RPM.Core.Tests;

/// <summary>
/// The placeholder rules. A real dataless file cannot be made in a test (that needs a file provider
/// and a network), so what is tested here is everything around the syscall: the stub naming both
/// ways, the scanner not losing an evicted track, and the one thing that must never regress, which is
/// that an ORDINARY file is never called a placeholder. A false positive there would tell a user
/// their music is in the cloud and refuse to import it.
/// </summary>
public class CloudFileTests : IDisposable
{
    private readonly string _root;

    public CloudFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmcloud-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Touch(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void An_ordinary_file_is_never_a_placeholder()
    {
        Assert.False(CloudFile.IsDataless(Touch("song.mp3", "some bytes")));
    }

    [Fact]
    public void An_empty_file_is_not_a_placeholder()
    {
        // Zero allocated blocks, like a placeholder, and zero logical length, unlike one. Telling
        // these apart is the entire reason the macOS check asks twice.
        Assert.False(CloudFile.IsDataless(Touch("empty.mp3", "")));
    }

    [Fact]
    public void A_file_that_is_not_there_at_all_is_not_a_placeholder()
    {
        Assert.False(CloudFile.IsDataless(Path.Combine(_root, "never-existed.mp3")));
    }

    [Fact]
    public void An_icloud_stub_names_the_track_it_stands_for()
    {
        var stub = Path.Combine(_root, ".Hysteria.mp3.icloud");

        Assert.True(CloudFile.IsICloudStub(stub));
        Assert.Equal(Path.Combine(_root, "Hysteria.mp3"), CloudFile.TrackBehindStub(stub));
        Assert.Equal(stub, CloudFile.StubFor(Path.Combine(_root, "Hysteria.mp3")));
    }

    [Fact]
    public void An_ordinary_dotfile_is_not_a_stub()
    {
        Assert.False(CloudFile.IsICloudStub(Path.Combine(_root, ".DS_Store")));
        Assert.False(CloudFile.IsICloudStub(Path.Combine(_root, "Hysteria.mp3")));
    }

    [Fact]
    public void An_evicted_track_reads_as_a_placeholder_through_its_stub()
    {
        Touch(".Hysteria.mp3.icloud", "stub");
        var track = Path.Combine(_root, "Hysteria.mp3");

        // The track itself is not on the disk: the stub is what says it exists at all.
        Assert.False(File.Exists(track));
        Assert.True(CloudFile.IsDataless(track));
    }

    [Fact]
    public void The_scanner_reports_an_evicted_track_rather_than_losing_it()
    {
        var present = Touch("01 Present.mp3");
        Touch(".02 Evicted.mp3.icloud", "stub");
        Touch(".DS_Store", "junk");          // an ordinary dotfile stays skipped
        Touch(".notes.txt.icloud", "stub");  // an evicted NON-audio file is still not music

        var found = AudioFileScanner.Scan(_root).Files;

        Assert.Equal(
            [present, Path.Combine(_root, "02 Evicted.mp3")],
            found.OrderBy(f => f, StringComparer.Ordinal));
    }
}

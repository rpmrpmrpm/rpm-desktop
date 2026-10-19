using RPM.Core.Scanning;

namespace RPM.Core.Tests;

public class AudioFileScannerTests : IDisposable
{
    private readonly string _root;

    public AudioFileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void Finds_audio_files_recursively_and_sorted()
    {
        Touch("b.mp3");
        Touch("sub/a.flac");
        Touch("sub/deep/c.M4A");

        var files = AudioFileScanner.Scan(_root).Files;

        Assert.Equal(3, files.Count);
        Assert.EndsWith("b.mp3", files[0]);
        Assert.EndsWith(Path.Combine("sub", "a.flac"), files[1]);
        Assert.EndsWith(Path.Combine("sub", "deep", "c.M4A"), files[2]);
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_is_named_rather_than_skipped()
    {
        if (OperatingSystem.IsWindows())
            return;

        Touch("open/a.mp3");
        Touch("locked/b.mp3");
        var locked = Path.Combine(_root, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var scan = AudioFileScanner.Scan(_root);

            Assert.Single(scan.Files);
            Assert.EndsWith("a.mp3", scan.Files[0]);
            Assert.Equal([locked], scan.Unreadable);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_albums_a_split_preserved_are_part_of_the_scan()
    {
        Touch("song.mp3");
        Touch("Originals/big album rip.mp3");
        Touch("sub/_originals/other.flac");   // the folder an older build made

        var files = AudioFileScanner.Scan(_root).Files;

        // Nothing is hidden from the queue: the preserved album is what a bad split is redone from.
        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.EndsWith("big album rip.mp3", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("other.flac", StringComparison.Ordinal));
    }

    [Fact]
    public void Ignores_non_audio_and_hidden()
    {
        Touch("cover.jpg");
        Touch("notes.txt");
        Touch(".hidden.mp3");
        Touch(".git/objects/x.mp3");
        Touch("real.ogg");

        var files = AudioFileScanner.Scan(_root).Files;

        Assert.Single(files);
        Assert.EndsWith("real.ogg", files[0]);
    }
}

using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// The folder a split keeps the album in. It is called "Originals" and it is visible in the import
/// tree, because the album that a bad cut must be redone from must not be visible only in a file
/// manager. Folders named "_originals" (hidden) are renamed when found, so they are not lost.
/// </summary>
public class OriginalsFolderTests : IDisposable
{
    private readonly string _root;

    public OriginalsFolderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
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
    public void The_folder_is_named_for_a_person_to_read()
    {
        Assert.Equal("Originals", AlbumFileSplitter.OriginalsFolderName);
        Assert.True(AlbumFileSplitter.IsOriginalsFolder("Originals"));
        Assert.True(AlbumFileSplitter.IsOriginalsFolder("originals"));
        Assert.True(AlbumFileSplitter.IsOriginalsFolder("_originals"));
        Assert.False(AlbumFileSplitter.IsOriginalsFolder("Original Soundtrack"));
    }

    [Fact]
    public void An_older_builds_folder_is_renamed()
    {
        Touch("album/_originals/side.flac");

        AlbumFileSplitter.MigrateLegacyOriginals(_root);

        Assert.False(Directory.Exists(Path.Combine(_root, "album", "_originals")));
        Assert.True(File.Exists(Path.Combine(_root, "album", "Originals", "side.flac")));
    }

    [Fact]
    public void Both_folders_present_means_the_contents_move_across()
    {
        Touch("album/Originals/kept.flac", "new");
        Touch("album/_originals/old.flac", "old");

        AlbumFileSplitter.MigrateLegacyOriginals(_root);

        var originals = Path.Combine(_root, "album", "Originals");
        Assert.True(File.Exists(Path.Combine(originals, "kept.flac")));
        Assert.True(File.Exists(Path.Combine(originals, "old.flac")));
        Assert.False(Directory.Exists(Path.Combine(_root, "album", "_originals")));
    }

    [Fact]
    public void A_name_collision_keeps_both_files()
    {
        Touch("album/Originals/side.flac", "new");
        Touch("album/_originals/side.flac", "old");

        AlbumFileSplitter.MigrateLegacyOriginals(_root);

        var originals = Path.Combine(_root, "album", "Originals");
        var files = Directory.GetFiles(originals);
        Assert.Equal(2, files.Length);
        Assert.Contains(files, f => File.ReadAllText(f) == "old");
        Assert.Contains(files, f => File.ReadAllText(f) == "new");
    }

    [Fact]
    public void Finding_the_folder_prefers_the_current_name_and_falls_back_to_the_old_one()
    {
        Assert.Null(AlbumFileSplitter.FindOriginalsDir(_root));

        Touch("_originals/a.flac");
        Assert.Equal(Path.Combine(_root, "_originals"), AlbumFileSplitter.FindOriginalsDir(_root));

        Touch("Originals/b.flac");
        Assert.Equal(Path.Combine(_root, "Originals"), AlbumFileSplitter.FindOriginalsDir(_root));
    }

    [Fact]
    public void Migrating_a_tree_with_nothing_to_migrate_is_harmless()
    {
        Touch("album/track.flac");

        AlbumFileSplitter.MigrateLegacyOriginals(_root);
        AlbumFileSplitter.MigrateLegacyOriginals(Path.Combine(_root, "nowhere"));

        Assert.True(File.Exists(Path.Combine(_root, "album", "track.flac")));
    }
}

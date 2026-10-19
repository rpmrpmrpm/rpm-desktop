using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Which folders can become the central music folder. Both places where the user picks one (first-run
/// setup and the music-folder card in settings) use only this check, so these tests cover both.
/// </summary>
public class LibraryFolderTests : IDisposable
{
    private readonly string _tmp;

    public LibraryFolderTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), $"libfolder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void An_empty_folder_is_a_new_collection()
    {
        Assert.Equal(LibraryFolderKind.Empty, LibraryFolder.Classify(Folder("fresh")));
        Assert.True(LibraryFolder.Adoptable(Folder("fresh")));
    }

    [Fact]
    public void Hidden_entries_do_not_make_a_folder_used()
    {
        var path = Folder("dotted");
        File.WriteAllText(Path.Combine(path, ".DS_Store"), "");

        Assert.Equal(LibraryFolderKind.Empty, LibraryFolder.Classify(path));
    }

    [Fact]
    public void A_folder_with_music_already_in_it_is_refused()
    {
        // The important case: adopting this folder would add every audio file in it to the collection
        // in place and write an rpm id into each file's tags.
        var path = Folder("my-music");
        File.WriteAllText(Path.Combine(path, "01 Something.mp3"), "");

        Assert.Equal(LibraryFolderKind.NotEmpty, LibraryFolder.Classify(path));
        Assert.False(LibraryFolder.Adoptable(path));
    }

    [Fact]
    public void A_folder_that_already_holds_a_mirror_is_reopened_however_full_it_is()
    {
        var path = Folder("library");
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(path))) { }
        File.WriteAllText(Path.Combine(path, "01 Something.mp3"), "");

        Assert.Equal(LibraryFolderKind.ExistingLibrary, LibraryFolder.Classify(path));
        Assert.True(LibraryFolder.Adoptable(path));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        Assert.False(LibraryFolder.Adoptable(Path.Combine(_tmp, "never-made")));
    }

    [Fact]
    public void A_folder_inside_the_other_overlaps_either_way_round()
    {
        var outer = Folder("outer");
        var inner = Folder(Path.Combine("outer", "inner"));

        Assert.True(LibraryFolder.Overlap(outer, inner));
        Assert.True(LibraryFolder.Overlap(inner, outer));
        Assert.True(LibraryFolder.Overlap(outer, outer));
    }

    [Fact]
    public void Two_folders_side_by_side_do_not_overlap()
    {
        Assert.False(LibraryFolder.Overlap(Folder("one"), Folder("two")));
    }

    [Fact]
    public void A_name_that_merely_starts_the_same_is_not_inside()
    {
        // "…/music" and "…/music-old" only share a prefix. Compared as strings without the separator,
        // the second would look like a folder inside the first.
        Assert.False(LibraryFolder.Overlap(Folder("music"), Folder("music-old")));
    }
}

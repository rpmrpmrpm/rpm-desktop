using System.IO;
using RPM.Desktop.Presentation.Main;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the app makes of a drop from Finder or Explorer: which of the dropped items are folders it
/// will scan into the import queue.
/// </summary>
public class DroppedMusicTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rpm-drop-").FullName;

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File(string name)
    {
        var path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Folders_dropped_are_the_folders_scanned()
    {
        var one = Folder("Ella");
        var two = Folder("Miles");

        Assert.Equal([one, two], DroppedMusic.FoldersIn([one, two]));
    }

    [Fact]
    public void Loose_files_are_not_a_source()
    {
        Assert.Empty(DroppedMusic.FoldersIn([File("a.mp3"), File("cover.jpg")]));
    }

    [Fact]
    public void Nothing_dropped_is_nothing_to_scan()
    {
        Assert.Empty(DroppedMusic.FoldersIn([null, "", "   ", Path.Combine(_root, "gone")]));
    }

    /// <summary>A record dropped together with the artist folder it is in is read once, because the
    /// artist's scan already covers it.</summary>
    [Fact]
    public void A_folder_inside_another_dropped_folder_is_left_to_it()
    {
        var artist = Folder("Ella");
        var album = Folder("Ella", "Songbook");

        Assert.Equal([artist], DroppedMusic.FoldersIn([album, artist]));
    }

    /// <summary>Containment is by folder, not by prefix: "Live" does not swallow "Live Sessions".</summary>
    [Fact]
    public void A_folder_whose_name_starts_the_same_is_its_own_source()
    {
        var live = Folder("Live");
        var sessions = Folder("Live Sessions");

        Assert.Equal([live, sessions], DroppedMusic.FoldersIn([live, sessions]));
    }

    [Fact]
    public void The_same_folder_dropped_twice_is_scanned_once()
    {
        var folder = Folder("Ella");

        Assert.Equal([folder], DroppedMusic.FoldersIn([folder, folder + Path.DirectorySeparatorChar]));
    }
}

using RPM.Core.Library;

namespace RPM.Core.Tests;

public class AlbumOrderTests
{
    /// <summary>A stand-in for the grid's own folder node: the three things the order rule reads.</summary>
    private sealed record Node(string Folder, int Tracks, params Node[] Kids);

    private static List<string> Build(int rootTracks, params Node[] roots)
        => AlbumOrder.Build(roots, n => n.Folder, n => n.Tracks, n => n.Kids, rootTracks);

    /// <summary>The library's own shape: an artist folder holding album folders. The artist is
    /// only structure: it is not an album, and the albums under it are numbered in tree order.</summary>
    [Fact]
    public void Albums_are_the_folders_that_hold_tracks_in_tree_order()
    {
        var albums = Build(0,
            new Node("Miles Davis", 0,
                new Node("Miles Davis/Kind of Blue", 5),
                new Node("Miles Davis/Milestones", 6)),
            new Node("John Coltrane", 0,
                new Node("John Coltrane/Blue Train", 5)));

        Assert.Equal(
            ["Miles Davis/Kind of Blue", "Miles Davis/Milestones", "John Coltrane/Blue Train"],
            albums);
    }

    /// <summary>A folder holding nothing but other folders is an artist or a box set, not a record.</summary>
    [Fact]
    public void Scaffolding_folder_is_not_an_album()
    {
        var albums = Build(0,
            new Node("Miles Davis", 0,
                new Node("Miles Davis/Kind of Blue", 5)),
            new Node("John Coltrane", 0,
                new Node("John Coltrane/Blue Train", 5)));

        Assert.Equal(["Miles Davis/Kind of Blue", "John Coltrane/Blue Train"], albums);
    }

    /// <summary>A folder can hold tracks AND subfolders (loose songs directly under an artist). It is
    /// an album in its own right, and its row comes before its children's.</summary>
    [Fact]
    public void Folder_with_both_tracks_and_subfolders_is_an_album_before_its_children()
    {
        var albums = Build(0,
            new Node("Miles Davis", 2,
                new Node("Miles Davis/Kind of Blue", 5)));

        Assert.Equal(["Miles Davis", "Miles Davis/Kind of Blue"], albums);
    }

    /// <summary>Loose tracks at the library root are the last rows in the tree (a folder's tracks
    /// follow its subfolders), so the root is the last album.</summary>
    [Fact]
    public void Root_tracks_are_the_last_album()
    {
        var albums = Build(3, new Node("A", 0, new Node("A/One", 1)));

        Assert.Equal(["A/One", ""], albums);
    }

    /// <summary>No loose tracks at the root, no root album. An empty key would be a card on the wall
    /// for a folder with nothing in it.</summary>
    [Fact]
    public void Root_with_no_tracks_is_not_an_album()
    {
        var albums = Build(0, new Node("A", 0, new Node("A/One", 1)));

        Assert.Equal(["A/One"], albums);
    }

    /// <summary>An empty library lays out nothing rather than throwing.</summary>
    [Fact]
    public void Empty_library_has_no_albums()
    {
        Assert.Empty(Build(0));
    }
}

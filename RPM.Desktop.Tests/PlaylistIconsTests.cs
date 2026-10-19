using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.RightPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// The rules the playlist pane's icon view stands on: which rows are a folder to step into, where the path
/// above the tiles comes from, and (the one that is not obvious) where the grid is standing after the tree
/// under it has been rebuilt.
///
/// <para>The rules sit outside the view for this reason. The grid holds a pointer to the folder it is
/// inside. The tree is rebuilt on every import tick and keeps its rows across the rebuild. So a folder that
/// was deleted leaves an object behind. That object still names its old parent and answers every question
/// about itself as if it were still there. Nothing about the row shows it is gone. Only the tree knows,
/// because it no longer links to the row.</para>
///
/// <para>The tests use a stand-in node, not <c>PlaylistTreeItem</c>. <c>PlaylistTreeItem</c> builds XAML
/// objects, which cannot be made in a test host. The rules take the two links they walk as functions for
/// this reason.</para>
/// </summary>
public class PlaylistIconsTests
{
    /// <summary>A row, reduced to the two things the rules walk: who holds it, and what it holds.</summary>
    private sealed class Node
    {
        public Node? Parent;
        public readonly List<Node> Children = new();

        public static Node Under(Node? parent)
        {
            var node = new Node { Parent = parent };
            parent?.Children.Add(node);
            return node;
        }

        /// <summary>Removes this row from the tree, as a delete does. The tree stops listing it, but the
        /// row is not told. It keeps the parent it had.</summary>
        public void Delete() => Parent?.Children.Remove(this);
    }

    private static Node? StillOpen(Node? open, IReadOnlyList<Node> roots)
        => PlaylistIcons.StillOpen(open, roots, n => n.Parent, n => n.Children);

    private static IReadOnlyList<Node> PathTo(Node? open)
        => PlaylistIcons.PathTo(open, n => n.Parent);

    // ── which rows open as a folder ──

    [Theory]
    [InlineData(PlaylistRowKind.Folder)]
    [InlineData(PlaylistRowKind.SmartFolder)]
    public void A_folder_is_stepped_into(PlaylistRowKind kind)
        => Assert.True(PlaylistIcons.OpensAsFolder(kind));

    [Theory]
    [InlineData(PlaylistRowKind.Playlist)]
    [InlineData(PlaylistRowKind.NowPlaying)]
    [InlineData(PlaylistRowKind.Smart)]
    [InlineData(PlaylistRowKind.Ref)]
    [InlineData(PlaylistRowKind.Song)]
    public void Everything_else_drills_into_its_tracks(PlaylistRowKind kind)
        => Assert.False(PlaylistIcons.OpensAsFolder(kind));

    // ── the path above the tiles ──

    [Fact]
    public void The_top_of_the_tree_has_no_path()
        => Assert.Empty(PathTo(null));

    [Fact]
    public void The_path_reads_from_the_top_down_to_the_open_folder()
    {
        var root = Node.Under(null);
        var middle = Node.Under(root);
        var open = Node.Under(middle);

        Assert.Equal(new[] { root, middle, open }, PathTo(open));
    }

    // ── where the grid is standing, once the tree has moved ──

    [Fact]
    public void A_folder_that_is_still_in_the_tree_is_still_the_one_open()
    {
        var root = Node.Under(null);
        var open = Node.Under(root);

        Assert.Same(open, StillOpen(open, new[] { root }));
    }

    [Fact]
    public void The_top_of_the_tree_stays_the_top()
        => Assert.Null(StillOpen(null, new[] { Node.Under(null) }));

    [Fact]
    public void Deleting_the_folder_you_are_in_leaves_you_in_the_one_that_held_it()
    {
        var root = Node.Under(null);
        var open = Node.Under(root);
        open.Delete();

        Assert.Same(root, StillOpen(open, new[] { root }));
    }

    /// <summary>Deleting a folder takes everything under it, including the one you were standing in. The
    /// grid does not fall all the way to the top for that: it stops at the first folder above you that is
    /// still in the tree, which is where the branch was cut.</summary>
    [Fact]
    public void A_branch_going_leaves_the_grid_at_the_cut()
    {
        var root = Node.Under(null);
        var middle = Node.Under(root);
        var open = Node.Under(middle);
        middle.Delete();   // the folder above the open one is what went, and it took the branch with it

        Assert.Same(root, StillOpen(open, new[] { root }));
    }

    /// <summary>And where nothing above you survived either, the top is what is left.</summary>
    [Fact]
    public void A_whole_branch_going_takes_the_grid_back_to_the_top()
    {
        var root = Node.Under(null);
        var open = Node.Under(root);
        open.Delete();
        root.Delete();     // …and the folder it was in was a root, which the tree no longer lists

        Assert.Null(StillOpen(open, Array.Empty<Node>()));
    }

    /// <summary>
    /// The shelf was swapped for somebody else's. Every row on screen belongs to a tree that is no longer
    /// the tree, and none of them knows it: the branch is intact, link for link, and simply hangs off a
    /// root the pane no longer has. Which is why the check ends at the ROOTS rather than at "does this
    /// still have a parent".
    /// </summary>
    [Fact]
    public void A_folder_from_the_tree_before_this_one_is_not_open_at_all()
    {
        var wasRoot = Node.Under(null);
        var open = Node.Under(wasRoot);

        Assert.Null(StillOpen(open, new[] { Node.Under(null) }));
    }

    // ── the size a tile's glyph is drawn at ──

    /// <summary>The area a glyph covers at the size the rule gives it, against the area a glyph that
    /// fills its em covers: 1.0 is the same size as its neighbour, 0.8 is a fifth smaller than
    /// it.</summary>
    private static double Beside(double width, double height)
    {
        var square = PlaylistIcons.SizeForInk(new FaInk.Ink(1, 1));
        var size = PlaylistIcons.SizeForInk(new FaInk.Ink(width, height));
        return width * height * size * size / (square * square);
    }

    /// <summary>The folder icon covers about 1.02 by 0.77 em. At one font size shared by every tile, it would
    /// look a fifth smaller than the list of notes beside it. Sized to its own ink, it lands within a tenth.
    /// The rest is the ceiling below.</summary>
    [Fact]
    public void The_folder_is_no_longer_the_small_icon_on_the_grid()
    {
        Assert.True(1.02 * 0.77 < 0.85, "the folder does not fill its em, which is the whole problem");
        Assert.InRange(Beside(1.02, 0.77), 0.9, 1.05);
    }

    /// <summary>The play triangle, narrow where the folder is short, comes out beside it either
    /// way.</summary>
    [Fact]
    public void A_narrow_glyph_is_drawn_as_large_as_a_square_one()
        => Assert.InRange(Beside(0.90, 1.07), 0.95, 1.05);

    /// <summary>A glyph that already fills its em is left at the plain box: the size the grid drew
    /// everything at before, which is the size this rule is anchored to.</summary>
    [Fact]
    public void A_glyph_that_fills_its_em_keeps_the_plain_size()
        => Assert.Equal(34, PlaylistIcons.SizeForInk(new FaInk.Ink(1, 1)), precision: 1);

    /// <summary>The ceiling. The lightbulb is narrow, so equal area would make it 41 points high among
    /// 34-point neighbours. Its growth therefore stops at its longest side. That costs it some area, and it
    /// should.</summary>
    [Fact]
    public void A_very_narrow_glyph_is_held_under_the_ceiling()
    {
        var ink = new FaInk.Ink(0.71, 1.03);
        var size = PlaylistIcons.SizeForInk(ink);

        Assert.True(ink.Height * size <= 34 * 1.12 + 0.01, $"stood {ink.Height * size:F1} high");
        Assert.True(Beside(0.71, 1.03) < 1, "the ceiling costs it area");
    }

    /// <summary>A glyph nothing could be measured for is drawn at the plain box rather than at a size
    /// worked out from nothing: see FaInk, which answers None when the face will not open.</summary>
    [Fact]
    public void An_unmeasurable_glyph_falls_back_to_the_plain_size()
        => Assert.Equal(34, PlaylistIcons.SizeForInk(FaInk.None), precision: 1);

    [Fact]
    public void Nothing_at_all_is_not_a_glyph_to_measure()
        => Assert.False(FaInk.Of("").Known);
}

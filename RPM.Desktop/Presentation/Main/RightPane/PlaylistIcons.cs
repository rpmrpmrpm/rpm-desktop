using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The rules that the icon view of the playlist tree is built on. There is no picture and no
/// control in them.
///
/// <para>The grid shows ONE folder at a time. A tree draws its shape with indents, but a grid has no
/// indents. So the contents of a folder are reached by opening it. The way back up is along the path above
/// the tiles. That means the grid must hold which folder is open. This is state about a tree that the grid
/// does not own, and such state goes stale easily. The tree is rebuilt on every import tick. The folder on
/// screen can be renamed, moved or deleted by a menu two clicks away. It can be changed through the other
/// view of the same tree. It can also change when a shelf is swapped for somebody else's.</para>
///
/// <para>So "the folder that is open" is not a pointer kept from the click that opened it. It is a claim
/// that is checked again against the tree in hand, every time the tree changes. See <see
/// cref="StillOpen"/>, which is the only way that the grid reaches its folder.</para>
/// </summary>
internal static class PlaylistIcons
{
    /// <summary>The box that a tile's GLYPH is laid out in, in points, before the square it sits in scales
    /// it. See the Viewbox in the tile template. The glyph sizes (<see cref="TileGlyphSize"/>) are worked
    /// out against this value and nothing else. So they are a ratio, and they survive the
    /// scaling.</summary>
    public const double ArtEdge = 96;

    /// <summary>What a row's <c>Metrics</c> reads before the grid has measured itself. There is one shared
    /// instance, not one per row. A row that held its own would never hear the size change. A null would be
    /// a binding that draws nothing. Its defaults are those of the wall's opening card.</summary>
    public static readonly Main.Library.Models.AlbumTileMetrics Unmeasured = new();

    /// <summary>The square that a tile's glyph is drawn to fill. Icons that cover their whole em (the
    /// list-music mark, the link) come out at this size. Every other icon is scaled to MATCH this area, and
    /// not to the font size that they share. See <see cref="TileGlyphSize"/>.</summary>
    private const double GlyphBox = 34;

    /// <summary>The longest that a scaled glyph's own side may be. A narrow, tall mark (the lightbulb, at
    /// 0.71 by 1.03 em) would have to stand 41 points high to cover the same area as a square one. A tower
    /// among its neighbours is as wrong as a runt, seen from the other side. The ceiling stops the growth
    /// at a tenth over the box.</summary>
    private const double GlyphLongest = GlyphBox * 1.12;

    /// <summary>The font size that draws <paramref name="glyph"/> at the same size as the icon beside it.
    ///
    /// <para>A font size names the em, not the mark. So icons at one font size were not one size. The
    /// folder covers three quarters of its em's height, and it read as the small one on the grid. The mark
    /// is measured (<see cref="FaInk"/>). The size is chosen so that every icon COVERS the same area. That
    /// area is the square root of width times height, which is the side of the square with that area. The
    /// result is held under <see cref="GlyphLongest"/>, so a narrow icon does not grow sideways into
    /// nothing.</para>
    ///
    /// <para>A glyph that cannot be measured is drawn at the plain box. An icon at that size is a little
    /// wrong. An icon sized from a failed measurement could be much too large.</para>
    /// </summary>
    public static double TileGlyphSize(string glyph) => SizeForInk(FaInk.Of(glyph));

    /// <summary>The size rule itself, applied to an ink that is already measured. This is the half of <see
    /// cref="TileGlyphSize"/> that has no font in it.</summary>
    public static double SizeForInk(FaInk.Ink ink) =>
        !ink.Known
            ? GlyphBox
            : Math.Min(GlyphBox / Math.Sqrt(ink.Width * ink.Height),
                       GlyphLongest / Math.Max(ink.Width, ink.Height));

    /// <summary>How far up a parent chain is walked before it is treated as broken.
    ///
    /// <para>A tree cannot hold a cycle. Filing a folder inside its own branch is refused while the drag is
    /// still running (see <c>PlaylistTreeItem.Holds</c>). But a row whose node was deleted keeps the parent
    /// it had when it went. The walks below are asked about exactly those rows. A ceiling costs nothing. It
    /// is the difference between a stale pointer and a hang.</para>
    /// </summary>
    private const int Deepest = 64;

    /// <summary>Whether a click on this row opens a folder in the grid, rather than drilling into a track
    /// list.
    ///
    /// <para>The generated lists' folders count here. The reason is not the name. A folder like that holds
    /// lists rather than songs. So there is no tracklist behind it to drill into. The list view answers a
    /// click on it with its chevron, for the same reason.</para>
    /// </summary>
    public static bool OpensAsFolder(PlaylistRowKind kind) =>
        kind is PlaylistRowKind.Folder or PlaylistRowKind.SmartFolder;

    /// <summary>The tiles that one folder's contents come to: its own rows, with a generated list's
    /// children standing BESIDE it rather than behind it.
    ///
    /// <para><b>A grid has nowhere else to put them.</b> The list view nests the genre cuts of a mood under
    /// the mood's own row, and opens them with a chevron. A tile has no chevron. A press on a tile plays
    /// the list. So under the grid's own rules, "Slow Burn" and "Sunrise Set" existed but could not be
    /// reached by any gesture the grid has. Pressing Chill plays Chill. That is the right result for the
    /// press, but it does not open the list of cuts.</para>
    ///
    /// <para><b>Only a LIST's children, and that is the whole of the rule.</b> A list's children are more
    /// lists. They are the same mood cut to one genre, each with a name of its own. On the user's own shelf
    /// they are a <see cref="PlaylistRowKind.Smart"/> row. On a borrowed shelf they are a <see
    /// cref="PlaylistRowKind.Playlist"/>. On a borrowed shelf, the owner's generated lists are kept as
    /// playlists, and a flavour hangs under its mood's list (see <c>BorrowedMoodLists</c>). Songs are never
    /// rows in this tree, so a list has no other kind of child. A folder's children are reached by opening
    /// the folder, which is the gesture the grid does have. See <see cref="OpensAsFolder"/>, which is the
    /// other half of this rule.</para>
    ///
    /// <para>The walk goes to the bottom, not just one level down. A mood's genre rows have no children of
    /// their own today, so in practice this is one level. Written as a walk, it stays correct for a tree
    /// that grows another level. A fixed one level would hide the new level quietly.</para>
    /// </summary>
    /// <remarks>Takes the row's kind and children as functions, as <see cref="StillOpen"/> does. That way
    /// the rule can be tested. A <c>PlaylistTreeItem</c> builds XAML objects, and it cannot be made in a
    /// test host.</remarks>
    public static List<T> TilesFor<T>(
        IReadOnlyList<T> rows, Func<T, PlaylistRowKind> kindOf, Func<T, IReadOnlyList<T>> childrenOf)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var tiles = new List<T>(rows.Count);
        Gather(rows, 0);
        return tiles;

        void Gather(IReadOnlyList<T> level, int depth)
        {
            if (depth >= Deepest)
                return;   // the same ceiling the parent walk uses, for the same reason

            foreach (var row in level)
            {
                tiles.Add(row);
                if (kindOf(row) is PlaylistRowKind.Smart or PlaylistRowKind.Playlist
                    && childrenOf(row) is { Count: > 0 } lists)
                    Gather(lists, depth + 1);
            }
        }
    }

    /// <summary>The path down to the open folder: the top of the tree first, and the open folder last. The
    /// path is empty when nothing is open. That is the grid standing at the top of the tree.
    /// </summary>
    public static IReadOnlyList<T> PathTo<T>(T? open, Func<T, T?> parentOf) where T : class
    {
        var path = new List<T>();
        for (var at = open; at is not null && path.Count < Deepest; at = parentOf(at))
            path.Add(at);

        path.Reverse();
        return path;
    }

    /// <summary>Which folder the grid is actually standing in, now that the tree has been rebuilt. It is
    /// the folder it was showing, if that is still in the tree. Otherwise it is the nearest folder above it
    /// that is. Otherwise it is the top.
    ///
    /// <para>The answer must come from the tree, not from the row. A deleted row does not know that it was
    /// deleted. Rows are kept across a reload and re-linked from the database. A node that is gone is not
    /// re-linked. Its object survives, still holding the parent it had, but its parent does not list it.
    /// So the test is this: a folder is in the tree when every step from it up to a root is a link the tree
    /// still draws, and the root it reaches is one that the tree still has.</para>
    ///
    /// <para>Walking UP instead of giving up is what keeps the grid somewhere. Deleting the folder you are
    /// looking at leaves you in the folder that held it. That is what a file browser does.</para>
    /// </summary>
    public static T? StillOpen<T>(
        T? open, IReadOnlyList<T> roots, Func<T, T?> parentOf, Func<T, IEnumerable<T>> childrenOf)
        where T : class
    {
        var steps = 0;
        for (var at = open; at is not null && steps < Deepest; at = parentOf(at), steps++)
            if (InTree(at, roots, parentOf, childrenOf))
                return at;

        return null;
    }

    private static bool InTree<T>(
        T node, IReadOnlyList<T> roots, Func<T, T?> parentOf, Func<T, IEnumerable<T>> childrenOf)
        where T : class
    {
        var at = node;
        for (var steps = 0; steps < Deepest; steps++)
        {
            if (parentOf(at) is not { } parent)
                return roots.Any(root => ReferenceEquals(root, at));

            if (!childrenOf(parent).Any(child => ReferenceEquals(child, at)))
                return false;

            at = parent;
        }

        return false;
    }
}

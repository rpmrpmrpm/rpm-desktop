using RPM.DataGrid.Model;
using RPM.Desktop.Presentation.RPaMp.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The four grids of the RPaMp library pane, as <see cref="RpmGrid"/> wants them: the tree of lists,
/// the artist and album panes, and the songs they leave.
///
/// <para>The same grid the library table and the import queue are drawn with, so the pane looks like
/// part of the app. The track list's column ids are the names <see cref="AmpLibrary"/> sorts by,
/// which is what a header click hands back.</para>
/// </summary>
internal static class AmpGrids
{
    /// <summary>The tree down the left. One column, no header: it is a list of places, not a table.</summary>
    public static GridColumns Nav() => new(
    [
        GridColumn.Tree("nav", 168, row =>
        {
            var node = (AmpNode)row;
            var marks = node.IsGroup
                ? TreeMarks.Expandable | (node.IsExpanded ? TreeMarks.Expanded : TreeMarks.None)
                : TreeMarks.None;

            return new TreeCell(node.Depth, marks, node.Label.Text, node.Glyph, GlyphWhite);
        }),
    ]);

    /// <summary>The artist pane: every name in the open list, and how many songs it holds.</summary>
    public static GridColumns Artists() => Names(Loc.Get("Review_ColArtist"));

    /// <summary>The album pane, narrowed by the artist beside it.</summary>
    public static GridColumns Albums() => Names(Loc.Get("Review_ColAlbum"));

    /// <summary>
    /// The songs. Every column sorts, and the sort is <see cref="AmpLibrary"/>'s: the rows are all in
    /// memory, so a header click re-orders what is already here.
    /// </summary>
    public static GridColumns Tracks(Func<LibraryRow, bool> isPlaying)
    {
        ArgumentNullException.ThrowIfNull(isPlaying);

        return new GridColumns(
        [
            GridColumn.PlayingBars("playing", 26, row => isPlaying((LibraryRow)row)),

            Text("title", 190, "Review_ColTitle", r => r.Title),
            Text("artist", 140, "Review_ColArtist", r => r.Artist),
            Text("album", 140, "Review_ColAlbum", r => r.Album),
            Text("track", 52, "Review_ColTrack", r => r.TrackText, CellAlign.Right),
            Text("length", 60, "Review_ColLength", r => r.DurationText, CellAlign.Right),
            Text("year", 52, "Review_ColYear", r => r.YearText, CellAlign.Right),
            Text("genre", 110, "Review_ColGenre", r => r.Genre),
            Text("kind", 60, "Review_ColKind", r => r.Kind),
            Text("plays", 56, "Review_ColPlays", r => r.Plays > 0 ? r.Plays.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty, CellAlign.Right),
        ]);
    }

    /// <summary>The white the tree glyphs are drawn in.</summary>
    private const uint GlyphWhite = 0xCCFFFFFF;

    /// <summary>
    /// A name pane: the name, then the count of songs behind it.
    /// </summary>
    /// <remarks>
    /// The count column has no header. Any short label would be no shorter than the number.
    /// </remarks>
    private static GridColumns Names(string header) => new(
    [
        GridColumn.Text("name", 214, row => ((AmpBrowseRow)row).Text, header: header),
        GridColumn.Text(
            "count", 44,
            row => ((AmpBrowseRow)row).Count.ToString(System.Globalization.CultureInfo.CurrentCulture),
            align: CellAlign.Right),
    ]);

    private static GridColumn Text(
        string id, double width, string headerKey, Func<LibraryRow, string?> read,
        CellAlign align = CellAlign.Left) =>
        GridColumn.Text(
            id, width, row => read((LibraryRow)row),
            header: Loc.Get(headerKey), canSort: true, align: align);
}

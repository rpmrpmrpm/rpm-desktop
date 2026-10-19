using Microsoft.UI.Xaml.Media;
using RPM.DataGrid.Model;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The import queue's columns, as <see cref="RpmGrid"/> wants them.
///
/// <para>Twenty-four columns, in order, at the widths the grid uses. Each one reads a property of
/// <see cref="ImportNode"/>. The grid paints the cells itself, so no XAML bindings or templates are
/// needed for each cell.</para>
/// </summary>
internal static class QueueGrid
{
    /// <summary>Font Awesome's pencil, grip-lines-vertical, and the marks the state glyph uses.</summary>
    private const string PencilGlyph = "\uF044";

    private const string GripGlyph = "\uF58E";

    public static GridColumns Columns() => new(
    [
        // The tick. What Recognize and Import act on; a record's tick stands for every file under it.
        GridColumn.Tick("tick", 30, row => TickOf((ImportNode)row)),

        // The per-file editor. A glyph that can be pressed, not a Button in a cell: the cell takes
        // the pointer on press, which would take it away from a button in the middle of a click.
        GridColumn.GlyphButton("details", 26, row =>
            ((ImportNode)row).IsFile
                ? new GlyphCell(PencilGlyph, 0xCCFFFFFF, Loc.Get("Details_Title"))
                : default),

        // The duplicate mark. Its own column and not the tree's glyph, because it answers a different
        // question: the tree glyph says what this FILE is, this says whether anything else already
        // holds the track.
        GridColumn.Glyph("duplicate", 26, row =>
        {
            var node = (ImportNode)row;
            return new GlyphCell(node.DuplicateGlyph, Argb(node.DuplicateBrush), node.DuplicateTip);
        }),

        GridColumn.Tree("name", 260, row => TreeOf((ImportNode)row),
            header: Loc.Get("Review_ColName"), gripGlyph: GripGlyph),

        // Where it came in from, next to the name. A track by Unknown Artist has no other clue on its
        // row, and the folder it came from usually answers the question.
        Text("folder", 180, "Review_ColFolder", n => n.ColPath),

        // The library table's columns, in the same order. This pane previews the library, so a question
        // such as "does every track have a year?" can be answered from one column.
        Text("title", 190, "Review_ColTitle", n => n.ColTitle),
        Text("artist", 150, "Review_ColArtist", n => n.ColArtist),
        Text("album", 150, "Review_ColAlbum", n => n.ColAlbum),
        Text("albumartist", 150, "Review_ColAlbumArtist", n => n.ColAlbumArtist),
        Text("track", 56, "Review_ColTrack", n => n.ColTrack, CellAlign.Right),
        Text("disc", 48, "Review_ColDisc", n => n.ColDisc, CellAlign.Right),
        Text("disctitle", 130, "Review_ColDiscTitle", n => n.ColDiscSubtitle),
        Text("year", 64, "Review_ColYear", n => n.ColYear, CellAlign.Right),
        Text("genre", 120, "Review_ColGenre", n => n.ColGenre),
        Text("subgenre", 120, "Review_ColSubgenre", n => n.ColSubgenre),
        Text("length", 70, "Review_ColLength", n => n.ColLength, CellAlign.Right),
        Text("kind", 70, "Review_ColKind", n => n.ColKind),
        Text("codec", 120, "Review_ColCodec", n => n.ColCodec),
        Text("bitrate", 86, "Review_ColBitrate", n => n.ColBitrate, CellAlign.Right),
        Text("samplerate", 90, "Review_ColSampleRate", n => n.ColSampleRate, CellAlign.Right),
        Text("bitdepth", 76, "Review_ColBitDepth", n => n.ColBitDepth, CellAlign.Right),
        Text("channels", 80, "Review_ColChannels", n => n.ColChannels, CellAlign.Right),
        Text("size", 80, "Review_ColSize", n => n.ColSize, CellAlign.Right),
        Text("file", 200, "Review_ColFile", n => n.ColFile),
    ]);

    /// <summary>
    /// Whether a column can be turned off from the columns menu. The tick, the two mark columns and
    /// the name are not optional: the tree needs them and a queue without them is not usable.
    /// </summary>
    public static bool IsOptional(GridColumn column) =>
        column.Id is not ("tick" or "details" or "duplicate" or "name");

    private static GridColumn Text(
        string id, double width, string headerKey, Func<ImportNode, string?> read,
        CellAlign align = CellAlign.Left) =>
        GridColumn.Text(id, width, row => read((ImportNode)row), header: Loc.Get(headerKey), align: align);

    /// <summary>
    /// A row's tick. A file carries its own on the <c>TrackRow</c>, so it survives the tree being
    /// rebuilt; a folder is on when every row under it is and partly on when only some are.
    /// </summary>
    private static TickState TickOf(ImportNode node) =>
        !node.CanTick ? TickState.None
        : node.IsChecked switch
        {
            true => TickState.On,
            false => TickState.Off,
            null => TickState.Partly,
        };

    /// <summary>
    /// The tree cell: indent, expander, and one of three things in the glyph slot:
    /// the now-playing mark, the drag handle, or the state glyph of the file.
    /// </summary>
    private static TreeCell TreeOf(ImportNode node)
    {
        var marks = TreeMarks.None;

        if (node.HasChildren)
            marks |= TreeMarks.Expandable;
        if (node.IsExpanded)
            marks |= TreeMarks.Expanded;

        // Only a track can be dragged. A folder cannot be, and neither can the track that is playing,
        // because its glyph slot shows the playing mark. Whether the grip is shown is up to the painter,
        // because only the frame knows which row the pointer is on.
        if (node.IsFile && !node.IsPlaying)
            marks |= TreeMarks.Draggable;

        if (node.IsPlaying)
            return new TreeCell(node.Depth, marks | TreeMarks.Playing, node.Name);

        return new TreeCell(node.Depth, marks, node.Name,
            node.CurrentStateGlyph, Argb(node.CurrentStateBrush), node.CurrentStateTip);
    }

    /// <summary>
    /// Converts a XAML brush to the painter's colour format. Returns zero for anything that is not a
    /// solid colour, and the painter reads zero as "use the row's own text colour".
    ///
    /// <para>The nodes only give out solid brushes, so a gradient here would be a bug somewhere else.
    /// Using the row's colour is the safer fallback.</para>
    /// </summary>
    private static uint Argb(Brush? brush) =>
        brush is SolidColorBrush { Color: var c }
            ? ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B
            : 0;
}

namespace RPM.DataGrid.Model;

/// <summary>
/// What a column draws. A closed set, because this grid has one consumer and these are all the cell
/// types RPM's two tables use. A new kind needs a change to this file and to the painter. It cannot
/// be added from markup as a data template.
/// </summary>
public enum CellKind
{
    /// <summary>A string, cut with an ellipsis at the column edge. Most columns use this.</summary>
    Text,

    /// <summary>One glyph from an icon face, with its own colour and tooltip. The queue's duplicate
    /// mark.</summary>
    Glyph,

    /// <summary>A checkbox. Tri-state on a group row, because a record's tick stands for all the
    /// files under it.</summary>
    Tick,

    /// <summary>Indent, expander, state glyph and text. The queue's Name column.</summary>
    Tree,

    /// <summary>A glyph that can be pressed. The queue's details button.</summary>
    GlyphButton,

    /// <summary>Text with the explicit badge behind it. The library's Title column.</summary>
    TitleBadge,

    /// <summary>The three-bar now-playing mark. The library's first column, and the only cell that
    /// animates.</summary>
    PlayingBars,

    /// <summary>
    /// A square picture: album art. The cell carries a KEY, not pixels, and the host answers it
    /// through <c>GridFrame.Art</c>.
    ///
    /// <para>A key because a cell is read once per visible row per frame, but a picture is decoded
    /// once for a whole album. Reading pixels here would decode a cover sixty times a second, once
    /// for every row it appears on. The host answers from a cache that it fills off the dispatcher.
    /// The answer is null until the cache has the picture. While it is null, the empty square (the
    /// "well") is drawn.</para>
    /// </summary>
    Art,
}

/// <summary>Where a cell's content sits in its column.</summary>
public enum CellAlign
{
    Left,
    Right,
    Centre,
}

/// <summary>A tick's three states. A group row can be neither on nor off.</summary>
public enum TickState
{
    /// <summary>No tick is drawn at all. A row that cannot be ticked.</summary>
    None,
    Off,
    On,
    /// <summary>Some of what is under this row is ticked and some is not.</summary>
    Partly,
}

/// <summary>What a tree cell shows besides its text.</summary>
[Flags]
public enum TreeMarks
{
    None = 0,

    /// <summary>It has children, so it gets an expander.</summary>
    Expandable = 1,

    /// <summary>Its children are showing.</summary>
    Expanded = 2,

    /// <summary>
    /// The row can be picked up and moved. The grip is shown in place of the state glyph while the
    /// pointer is on the row. The painter decides this, because the hovered row is stored once, in
    /// the frame. A row does not carry its own hover state.
    /// </summary>
    Draggable = 4,

    /// <summary>This row is the one playing, so the glyph slot holds the now-playing mark instead of
    /// a glyph.</summary>
    Playing = 8,
}

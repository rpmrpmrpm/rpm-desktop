namespace RPM.DataGrid.Model;

/// <summary>A glyph cell's content.</summary>
public readonly record struct GlyphCell(string? Glyph, uint Colour = 0, string? Tip = null);

/// <summary>A tree cell's content.</summary>
public readonly record struct TreeCell(
    int Depth,
    TreeMarks Marks,
    string? Text,
    string? Glyph = null,
    uint GlyphColour = 0,
    string? Tip = null);

/// <summary>A title cell's content.</summary>
public readonly record struct TitleCell(string? Text, bool Badge);

/// <summary>
/// One column: what it is called, how wide it is, and how to get its value out of a row.
///
/// <para>Built through the factory for its kind and no other way. The factory takes a reader whose
/// return type only fits that kind, so a column cannot be declared as text and then hand the
/// painter a tick. A <see cref="CellKind"/> beside a general-purpose delegate could disagree
/// without any warning, and the result would be a blank column instead of an error.</para>
/// </summary>
public sealed class GridColumn
{
    private GridColumn(string id, CellKind kind, double width, Func<object, CellValue> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(read);

        Id = id;
        Kind = kind;
        Width = width;
        Read = read;
    }

    /// <summary>
    /// Its name in the store. The same name is the persistence key and, where the column sorts or
    /// filters, the database column used in the query.
    /// </summary>
    public string Id { get; }

    /// <summary>What the column draws.</summary>
    public CellKind Kind { get; }

    /// <summary>The value of this column for a row.</summary>
    public Func<object, CellValue> Read { get; }

    /// <summary>The header's text. Empty for the mark columns, which have no heading.</summary>
    public string Header { get; init; } = "";

    /// <summary>How wide it is now. Settable: the user drags the edge and the session remembers it.</summary>
    public double Width { get; set; }

    /// <summary>The narrowest width the user can drag it to.</summary>
    public double MinWidth { get; init; } = 24;

    /// <summary>Whether it is drawn at all. The user can hide a column from the header's menu.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Whether the edge can be dragged. Off for the fixed mark columns.</summary>
    public bool CanResize { get; init; } = true;

    /// <summary>Whether clicking the header sorts by it.</summary>
    public bool CanSort { get; init; }

    /// <summary>
    /// Whether the header offers a text search to filter by: starts with, contains, ends with.
    ///
    /// <para>Off where the cell's text is not the value the database holds. The match is a SQL LIKE
    /// against the column named by <see cref="Id"/>, so a column that formats its value cannot be
    /// searched by what is on screen: a length shows "3:45" and stores 225, a date shows a date and
    /// stores ticks. A search for either would run against the stored number and match nothing,
    /// with no error. A column whose text is the stored value can be searched whether or not its
    /// values repeat.</para>
    /// </summary>
    public bool CanFilter { get; init; }

    /// <summary>Where the content sits.</summary>
    public CellAlign Align { get; init; } = CellAlign.Left;

    /// <summary>
    /// The glyph a tree row shows in place of its state glyph while the pointer is on it and it can
    /// be dragged. Empty for a column whose rows cannot be moved.
    /// </summary>
    public string? GripGlyph { get; init; }

    /// <summary>
    /// The properties every kind shares, given once. They are optional because a column with no
    /// header and no sort is normal for the mark columns, and because none of them change what a
    /// cell draws: a missing header is visible on screen, which is an acceptable failure for a label.
    /// </summary>
    private GridColumn With(string header, bool canSort, bool canFilter, CellAlign align, double minWidth) =>
        new(Id, Kind, Width, Read)
        {
            Header = header,
            CanSort = canSort,
            CanFilter = canFilter,
            Align = align,
            MinWidth = minWidth,
            CanResize = CanResize,
        };

    /// <summary>A column of plain text.</summary>
    public static GridColumn Text(
        string id, double width, Func<object, string?> read,
        string header = "", bool canSort = false, bool canFilter = false,
        CellAlign align = CellAlign.Left, double minWidth = 24) =>
        new GridColumn(id, CellKind.Text, width, row => CellValue.OfText(read(row)))
            .With(header, canSort, canFilter, align, minWidth);

    /// <summary>A column of one icon glyph. Never resized: it is sized to its glyph.</summary>
    public static GridColumn Glyph(string id, double width, Func<object, GlyphCell> read) =>
        new(id, CellKind.Glyph, width, row =>
        {
            var g = read(row);
            return CellValue.OfGlyph(g.Glyph, g.Colour, g.Tip);
        })
        { CanResize = false };

    /// <summary>A column of one icon glyph that can be pressed.</summary>
    public static GridColumn GlyphButton(string id, double width, Func<object, GlyphCell> read) =>
        new(id, CellKind.GlyphButton, width, row =>
        {
            var g = read(row);
            return CellValue.OfGlyphButton(g.Glyph, g.Colour, g.Tip);
        })
        { CanResize = false };

    /// <summary>A column of checkboxes.</summary>
    public static GridColumn Tick(string id, double width, Func<object, TickState> read) =>
        new(id, CellKind.Tick, width, row => CellValue.OfTick(read(row)))
        { CanResize = false };

    /// <summary>The tree column: indent, expander, state glyph, text.</summary>
    public static GridColumn Tree(
        string id, double width, Func<object, TreeCell> read,
        string header = "", double minWidth = 60, string? gripGlyph = null)
    {
        var column = new GridColumn(id, CellKind.Tree, width, row =>
        {
            var t = read(row);
            return CellValue.OfTree(t.Depth, t.Marks, t.Text, t.Glyph, t.GlyphColour, t.Tip);
        })
            .With(header, canSort: false, canFilter: false, CellAlign.Left, minWidth);

        return gripGlyph is null ? column : column.WithGrip(gripGlyph);
    }

    private GridColumn WithGrip(string glyph) =>
        new(Id, Kind, Width, Read)
        {
            Header = Header,
            MinWidth = MinWidth,
            CanResize = CanResize,
            CanSort = CanSort,
            CanFilter = CanFilter,
            Align = Align,
            GripGlyph = glyph,
        };

    /// <summary>Text with the explicit badge behind it.</summary>
    public static GridColumn TitleBadge(
        string id, double width, Func<object, TitleCell> read,
        string header = "", bool canSort = false, bool canFilter = false, double minWidth = 60) =>
        new GridColumn(id, CellKind.TitleBadge, width, row =>
        {
            var t = read(row);
            return CellValue.OfTitle(t.Text, t.Badge);
        })
            .With(header, canSort, canFilter, CellAlign.Left, minWidth);

    /// <summary>A column of album art. The reader returns the cache key, not pixels (see
    /// <see cref="CellKind.Art"/>).</summary>
    public static GridColumn Art(string id, double width, Func<object, string?> read) =>
        new(id, CellKind.Art, width, row => CellValue.OfArt(read(row))) { CanResize = false };

    /// <summary>The now-playing mark.</summary>
    public static GridColumn PlayingBars(string id, double width, Func<object, bool> read) =>
        new(id, CellKind.PlayingBars, width, row => CellValue.OfBars(read(row)))
        { CanResize = false };

    public override string ToString() => $"{Id} ({Kind}, {Width:F0}px)";
}

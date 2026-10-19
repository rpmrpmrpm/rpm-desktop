namespace RPM.DataGrid.Model;

/// <summary>
/// What one cell shows, read from a row when the painter needs it.
///
/// <para>One struct rather than a set of nullable delegates per kind: a column has exactly one way
/// to produce a value, and <see cref="Kind"/> decides which fields the painter reads. Values are
/// built through the factory for the kind, so a value cannot be made without saying what it
/// is.</para>
///
/// <para>A struct, with no allocation beyond the strings the row already holds. The painter reads
/// one of these per visible cell per frame.</para>
/// </summary>
public readonly struct CellValue
{
    private CellValue(CellKind kind)
    {
        Kind = kind;
    }

    /// <summary>Which factory made it, and therefore which fields mean anything.</summary>
    public CellKind Kind { get; private init; }

    /// <summary>True when there is nothing to draw at all: a placeholder row, or a cell the row
    /// leaves blank.</summary>
    public bool IsBlank { get; private init; }

    /// <summary>The string, for <see cref="CellKind.Text"/>, <see cref="CellKind.Tree"/> and
    /// <see cref="CellKind.TitleBadge"/>.</summary>
    public string? Text { get; private init; }

    /// <summary>The icon-face glyph, for <see cref="CellKind.Glyph"/>,
    /// <see cref="CellKind.GlyphButton"/> and <see cref="CellKind.Tree"/>.</summary>
    public string? Glyph { get; private init; }

    /// <summary>The glyph's colour as 0xAARRGGBB. Zero means the row's own text colour.</summary>
    public uint GlyphColour { get; private init; }

    /// <summary>The tooltip shown when the pointer rests on this cell. Null for most cells.</summary>
    public string? Tip { get; private init; }

    /// <summary>For <see cref="CellKind.Tick"/>.</summary>
    public TickState Tick { get; private init; }

    /// <summary>For <see cref="CellKind.Tree"/>: how deep the row sits.</summary>
    public int Depth { get; private init; }

    /// <summary>For <see cref="CellKind.Tree"/>.</summary>
    public TreeMarks Marks { get; private init; }

    /// <summary>For <see cref="CellKind.TitleBadge"/>: whether the badge is drawn.</summary>
    public bool Badge { get; private init; }

    /// <summary>For <see cref="CellKind.PlayingBars"/>: whether this row is the one playing.</summary>
    public bool Active { get; private init; }

    /// <summary>For <see cref="CellKind.Art"/>: what to ask the host's picture cache for. Usually
    /// the album's folder, so every track on a record asks for one picture.</summary>
    public string? ArtKey { get; private init; }

    /// <summary>Nothing to draw. What a placeholder row returns for every column.</summary>
    public static CellValue Blank(CellKind kind) => new(kind) { IsBlank = true };

    public static CellValue OfText(string? text) =>
        string.IsNullOrEmpty(text)
            ? Blank(CellKind.Text)
            : new CellValue(CellKind.Text) { Text = text };

    public static CellValue OfGlyph(string? glyph, uint colour = 0, string? tip = null) =>
        string.IsNullOrEmpty(glyph)
            ? Blank(CellKind.Glyph)
            : new CellValue(CellKind.Glyph) { Glyph = glyph, GlyphColour = colour, Tip = tip };

    public static CellValue OfGlyphButton(string? glyph, uint colour = 0, string? tip = null) =>
        string.IsNullOrEmpty(glyph)
            ? Blank(CellKind.GlyphButton)
            : new CellValue(CellKind.GlyphButton) { Glyph = glyph, GlyphColour = colour, Tip = tip };

    public static CellValue OfTick(TickState state) =>
        state == TickState.None
            ? Blank(CellKind.Tick)
            : new CellValue(CellKind.Tick) { Tick = state };

    public static CellValue OfTree(
        int depth, TreeMarks marks, string? text,
        string? glyph = null, uint glyphColour = 0, string? tip = null) =>
        new(CellKind.Tree)
        {
            Depth = Math.Max(0, depth),
            Marks = marks,
            Text = text,
            Glyph = glyph,
            GlyphColour = glyphColour,
            Tip = tip,
        };

    public static CellValue OfTitle(string? text, bool badge) =>
        new(CellKind.TitleBadge) { Text = text, Badge = badge, IsBlank = string.IsNullOrEmpty(text) && !badge };

    public static CellValue OfBars(bool active) =>
        new(CellKind.PlayingBars) { Active = active, IsBlank = !active };

    /// <summary>A picture, named by its key in the host's cache. Never blank while there is a key:
    /// the empty square is drawn whether or not the picture has arrived, so the column does not
    /// flicker as covers decode.</summary>
    public static CellValue OfArt(string? key) =>
        string.IsNullOrEmpty(key)
            ? Blank(CellKind.Art)
            : new CellValue(CellKind.Art) { ArtKey = key };
}

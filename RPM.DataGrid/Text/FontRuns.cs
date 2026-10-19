namespace RPM.DataGrid.Text;

/// <summary>A stretch of a string drawn with one face.</summary>
/// <param name="Start">Index of the first UTF-16 code unit.</param>
/// <param name="Length">How many code units.</param>
/// <param name="Face">Which face draws it, as <see cref="IFaceCoverage"/> numbers them.</param>
public readonly record struct FontRun(int Start, int Length, int Face);

/// <summary>
/// Which face can draw a given character, and what to fall back to when the preferred one cannot.
///
/// <para>An interface so <see cref="FontRuns.Split"/> can be tested without a font on disk. The
/// implementation over SkiaSharp is <c>SkiaFaces</c>.</para>
/// </summary>
public interface IFaceCoverage
{
    /// <summary>The face asked for first. Every run starts here.</summary>
    int Primary { get; }

    /// <summary>Whether that face has a glyph for the code point.</summary>
    bool Covers(int face, int codePoint);

    /// <summary>A face that does have a glyph for it, or -1 when nothing installed does.</summary>
    int Fallback(int codePoint);
}

/// <summary>
/// Splits a string into the fewest stretches that can each be drawn with a single face.
///
/// <para>This is the step that keeps a Japanese, Cyrillic or Greek title from rendering as a row of
/// boxes. Skia draws with the face it is given and does not look for another one, so a cell drawn in
/// the UI face alone shows .notdef for every character that face has no glyph for. The framework's
/// text stack does this internally; a drawn grid has to do it itself.</para>
///
/// <para>Pure arithmetic over <see cref="IFaceCoverage"/>: no Skia, no allocation beyond the list
/// the caller supplies.</para>
/// </summary>
public static class FontRuns
{
    /// <summary>
    /// Fills <paramref name="into"/> with the runs of <paramref name="text"/>, in order. Clears it
    /// first. An empty string produces no runs.
    /// </summary>
    public static void Split(ReadOnlySpan<char> text, IFaceCoverage faces, List<FontRun> into)
    {
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();
        if (text.IsEmpty)
            return;

        var primary = faces.Primary;
        var face = primary;
        var runStart = 0;
        var started = false;

        var i = 0;
        while (i < text.Length)
        {
            var units = CodePointAt(text, i, out var cp);

            // Marks, joiners and variation selectors belong to the character in front of them. Picking
            // a face for them on their own splits an accented letter or an emoji sequence into pieces
            // that shape separately, which is how a flag becomes two letters and an e-acute becomes an
            // e followed by a floating accent.
            if (started && IsAttaching(cp))
            {
                i += units;
                continue;
            }

            var want = FaceFor(faces, primary, face, cp, started);

            if (!started)
            {
                face = want;
                runStart = 0;
                started = true;
            }
            else if (want != face)
            {
                into.Add(new FontRun(runStart, i - runStart, face));
                face = want;
                runStart = i;
            }

            i += units;
        }

        if (started)
            into.Add(new FontRun(runStart, text.Length - runStart, face));
    }

    /// <summary>
    /// The face for one code point, preferring to stay where the run already is.
    ///
    /// <para>The order matters: it can be the difference between two runs and six. A space between a
    /// Latin word and a Japanese one is covered by both faces; asking the primary first would end the
    /// Japanese run at every space and start it again after. So the order is: the current face, then
    /// the primary, then a fallback.</para>
    /// </summary>
    private static int FaceFor(IFaceCoverage faces, int primary, int current, int cp, bool started)
    {
        if (started && faces.Covers(current, cp))
            return current;

        if (faces.Covers(primary, cp))
            return primary;

        var fallback = faces.Fallback(cp);

        // No installed font draws it. Use the primary's .notdef, which keeps the run whole instead of
        // cutting it around a character that will be a box in any face.
        return fallback >= 0 ? fallback : primary;
    }

    /// <summary>
    /// Whether the code point attaches to whatever came before it rather than standing on its own.
    ///
    /// <para>Combining marks, the zero-width joiner and non-joiner, and the two variation-selector
    /// blocks. Enough to keep accented Latin, Indic and Thai clusters and emoji sequences together;
    /// this is a face-splitting question, not a grapheme-breaking one, and the shaper does the rest.</para>
    /// </summary>
    private static bool IsAttaching(int cp)
    {
        if (cp is 0x200C or 0x200D)                        // zero-width non-joiner, joiner
            return true;
        if (cp is >= 0xFE00 and <= 0xFE0F)                 // variation selectors 1-16
            return true;
        if (cp is >= 0xE0100 and <= 0xE01EF)               // variation selectors 17-256
            return true;
        if (cp is >= 0x1F3FB and <= 0x1F3FF)               // emoji skin-tone modifiers
            return true;

        if (cp > 0xFFFF)
        {
            var rune = char.ConvertFromUtf32(cp);
            return char.GetUnicodeCategory(rune, 0) is
                System.Globalization.UnicodeCategory.NonSpacingMark or
                System.Globalization.UnicodeCategory.SpacingCombiningMark or
                System.Globalization.UnicodeCategory.EnclosingMark;
        }

        return char.GetUnicodeCategory((char)cp) is
            System.Globalization.UnicodeCategory.NonSpacingMark or
            System.Globalization.UnicodeCategory.SpacingCombiningMark or
            System.Globalization.UnicodeCategory.EnclosingMark;
    }

    /// <summary>Reads the code point at <paramref name="i"/> and returns how many code units it took.
    /// An unpaired surrogate is read as itself, so a malformed string still advances.</summary>
    internal static int CodePointAt(ReadOnlySpan<char> text, int i, out int codePoint)
    {
        var c = text[i];
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            codePoint = char.ConvertToUtf32(c, text[i + 1]);
            return 2;
        }

        codePoint = c;
        return 1;
    }
}

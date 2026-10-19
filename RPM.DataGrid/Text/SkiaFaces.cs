using SkiaSharp;

namespace RPM.DataGrid.Text;

/// <summary>
/// The installed fonts, as <see cref="FontRuns"/> wants to see them: a primary face, and whatever
/// the system offers for characters that face cannot draw.
///
/// <para>Faces are numbered as they are discovered and the numbers are stable for the life of the
/// instance, so a run can name one with an int and the painter can look the typeface back up.</para>
///
/// <para>Not thread-safe, and not meant to be. One of these belongs to one grid, on the UI thread.</para>
/// </summary>
public sealed class SkiaFaces : IDisposable
{
    /// <summary>How many resolved code points are remembered before the table is dropped and
    /// rebuilt. A viewport holds a few thousand characters at most and each resolution costs a
    /// system font lookup, so this limit is deliberately large.</summary>
    private const int ResolutionLimit = 8192;

    private readonly List<SKTypeface> _faces = [];
    private readonly Dictionary<string, int> _byName = [];

    /// <summary>Code point to face. Holds -1 for a code point nothing installed can draw, so a
    /// second lookup for the same character does not ask the system again.</summary>
    private readonly Dictionary<int, int> _resolved = [];

    private readonly SKFontManager _fonts;

    /// <param name="primary">The face asked for first. Every string starts here.</param>
    /// <param name="fonts">The font manager fallbacks are looked up in. Defaults to the system's.</param>
    public SkiaFaces(SKTypeface primary, SKFontManager? fonts = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        _fonts = fonts ?? SKFontManager.Default;
        Primary = Intern(primary);
    }

    /// <summary>The face every run starts with.</summary>
    public int Primary { get; }

    /// <summary>How many faces have been handed out a number.</summary>
    public int Count => _faces.Count;

    /// <summary>The typeface a run's face number stands for.</summary>
    public SKTypeface Typeface(int face) => _faces[face];

    /// <summary>
    /// Adds a face and returns its number, or returns the number it already has. Used for the fonts
    /// the grid names itself: Font Awesome for the glyph columns, and whatever the theme's family
    /// resolves to.
    /// </summary>
    public int Intern(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);

        var key = Key(typeface);
        if (_byName.TryGetValue(key, out var existing))
            return existing;

        _faces.Add(typeface);
        var id = _faces.Count - 1;
        _byName[key] = id;
        return id;
    }

    /// <summary>Whether that face has a glyph for the code point.</summary>
    public bool Covers(int face, int codePoint) =>
        face >= 0 && face < _faces.Count && _faces[face].ContainsGlyph(codePoint);

    /// <summary>
    /// A face that can draw the code point, or -1 when nothing installed can.
    ///
    /// <para>Answers are remembered, including the negative ones: a character with no font on the
    /// machine otherwise costs a full system-wide search on every frame it is drawn in.</para>
    /// </summary>
    public int Fallback(int codePoint)
    {
        if (_resolved.TryGetValue(codePoint, out var known))
            return known;

        // A table this size means either a very mixed library or a leak. Either way, starting again
        // is cheaper than growing without bound, and the characters on screen are re-resolved on the
        // next frame that draws them.
        if (_resolved.Count >= ResolutionLimit)
            _resolved.Clear();

        var id = -1;

        // Try the faces already known first. A grid showing a Japanese record and a Russian one asks
        // for the same two faces again and again, and this avoids a system lookup each time.
        for (var f = 0; f < _faces.Count; f++)
        {
            if (_faces[f].ContainsGlyph(codePoint))
            {
                id = f;
                break;
            }
        }

        if (id < 0 && _fonts.MatchCharacter(codePoint) is { } found)
            id = Intern(found);

        _resolved[codePoint] = id;
        return id;
    }

    /// <summary>A coverage view over this set, for <see cref="FontRuns.Split"/>.</summary>
    public IFaceCoverage Coverage => new View(this);

    private static string Key(SKTypeface t) =>
        $"{t.FamilyName}|{t.FontWeight}|{t.FontWidth}|{t.FontSlant}";

    public void Dispose()
    {
        foreach (var f in _faces)
            f.Dispose();
        _faces.Clear();
        _byName.Clear();
        _resolved.Clear();
    }

    private sealed class View(SkiaFaces faces) : IFaceCoverage
    {
        public int Primary => faces.Primary;
        public bool Covers(int face, int codePoint) => faces.Covers(face, codePoint);
        public int Fallback(int codePoint) => faces.Fallback(codePoint);
    }
}

using System.Globalization;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace RPM.DataGrid.Text;

/// <summary>
/// One cell's text, shaped and ready to draw. Positions are relative to an origin of (0, baseline),
/// so the same object draws at any row.
/// </summary>
public sealed class ShapedText : IDisposable
{
    internal ShapedText(SKTextBlob? blob, float width, bool truncated)
    {
        Blob = blob;
        Width = width;
        Truncated = truncated;
    }

    internal SKTextBlob? Blob { get; }

    /// <summary>How wide it is once drawn, including the ellipsis if there is one.</summary>
    public float Width { get; }

    /// <summary>Whether it was cut to fit and now ends in an ellipsis.</summary>
    public bool Truncated { get; }

    /// <summary>Nothing to draw: the string was empty, or everything in it was.</summary>
    public bool IsEmpty => Blob is null;

    /// <summary>Draws it with its left edge at <paramref name="x"/> and its baseline at
    /// <paramref name="baseline"/>.</summary>
    public void Draw(SKCanvas canvas, float x, float baseline, SKPaint paint)
    {
        if (Blob is { } blob)
            canvas.DrawText(blob, x, baseline, paint);
    }

    public void Dispose() => Blob?.Dispose();
}

/// <summary>
/// Turns a string into glyphs, once, and remembers the answer.
///
/// <para>Every string is shaped through HarfBuzz, not only the ones that seem to need it. A library
/// holds titles in many scripts, and a per-string test for whether shaping is needed can be wrong;
/// shaping every string cannot. The result is cached, so a row that is on screen for a hundred
/// frames is shaped once.</para>
///
/// <para>Fitting to a column is done by shaping the candidate, not by cutting a shaped result. A
/// shaped run's glyphs are not in the string's order for every script, and its glyph count is not
/// its character count, so cutting one at a pixel position is wrong for exactly those scripts.
/// Re-shaping a shorter string is always right, costs a binary search over graphemes, and only
/// happens for cells that actually overflow.</para>
///
/// <para>One of these belongs to one grid, on the UI thread. It is not thread-safe.</para>
/// </summary>
public sealed class TextShop : IDisposable
{
    /// <param name="Face">The face to shape with, or -1 for the primary and its fallbacks.</param>
    private readonly record struct Key(string Text, int Size, int Width, int Face);

    private readonly SkiaFaces _faces;
    private readonly IFaceCoverage _coverage;
    private readonly int _limit;

    private readonly Dictionary<int, SKShaper> _shapers = [];
    private readonly Dictionary<(int Face, int Size), SKFont> _fonts = [];

    // Two generations rather than a true LRU. When the live table fills, it becomes the old one and
    // a fresh table takes over; a hit in the old table is promoted. Nothing is disposed until a
    // generation is dropped whole, so a blob cannot be freed while a paint still holds it.
    private Dictionary<Key, ShapedText> _live = [];
    private Dictionary<Key, ShapedText> _old = [];

    private readonly List<FontRun> _runs = [];

    public TextShop(SkiaFaces faces, int limit = 4096)
    {
        ArgumentNullException.ThrowIfNull(faces);
        _faces = faces;
        _coverage = faces.Coverage;
        _limit = Math.Max(64, limit);
    }

    /// <summary>How many strings are shaped and held. Diagnostics only.</summary>
    public int Held => _live.Count + _old.Count;

    /// <summary>The primary face's metrics at a size, for placing the baseline in a row.</summary>
    public SKFontMetrics Metrics(float size) => Font(_faces.Primary, size).Metrics;

    /// <summary>
    /// The shaped form of <paramref name="text"/>, cut to <paramref name="maxWidth"/> with an
    /// ellipsis if it does not fit.
    /// </summary>
    /// <param name="maxWidth">Zero or less means no limit.</param>
    public ShapedText Get(string? text, float size, float maxWidth = 0) =>
        Get(text, size, maxWidth, AnyFace);

    /// <summary>
    /// The shaped form of one icon glyph, drawn with the face named and no other.
    ///
    /// <para>An icon glyph is in the private use area, where the system cannot say which face draws
    /// it: the code point means one thing in Font Awesome and something else in another icon font.
    /// So the fallback chain is not used at all. The caller names the face, and a face that does not
    /// have the glyph draws .notdef, which is correct and visible on screen.</para>
    /// </summary>
    public ShapedText GetIcon(string? glyph, int face, float size) =>
        Get(glyph, size, 0, face);

    private ShapedText Get(string? text, float size, float maxWidth, int face)
    {
        if (string.IsNullOrEmpty(text))
            return Empty;

        var key = new Key(text, Quantize(size), maxWidth > 0 ? (int)MathF.Round(maxWidth) : 0, face);

        if (_live.TryGetValue(key, out var hit))
            return hit;

        if (_old.TryGetValue(key, out hit))
        {
            _live[key] = hit;
            _old.Remove(key);
            return hit;
        }

        var made = Fit(text, size, maxWidth, face);
        Hold(key, made);
        return made;
    }

    /// <summary>Shape with the primary face and whatever the system falls back to.</summary>
    private const int AnyFace = -1;

    /// <summary>Drops everything shaped so far. Called when the theme or the scale changes, where
    /// every cached blob was built at the wrong size.</summary>
    public void Clear()
    {
        DropAll(_live);
        DropAll(_old);
        _live = [];
        _old = [];
    }

    private static readonly ShapedText Empty = new(null, 0, false);

    /// <summary>Sizes are quantized so that a scale factor with a long tail does not produce a cache
    /// entry per frame. A twentieth of a pixel is finer than any display can show.</summary>
    private static int Quantize(float size) => (int)MathF.Round(size * 20f);

    private void Hold(Key key, ShapedText made)
    {
        if (_live.Count >= _limit)
        {
            DropAll(_old);
            _old = _live;
            _live = [];
        }

        _live[key] = made;
    }

    private static void DropAll(Dictionary<Key, ShapedText> table)
    {
        foreach (var v in table.Values)
            v.Dispose();
        table.Clear();
    }

    /// <summary>
    /// Shapes the string, and if it is too wide, finds the longest leading run of graphemes that
    /// fits once an ellipsis is added.
    /// </summary>
    private ShapedText Fit(string text, float size, float maxWidth, int face)
    {
        var whole = Build(text, size, face, truncated: false);
        if (maxWidth <= 0 || whole.Width <= maxWidth)
            return whole;

        whole.Dispose();

        var mark = Ellipsis();
        var starts = StringInfo.ParseCombiningCharacters(text);

        // Cut on a grapheme, not on a char. Half a surrogate pair is not a character, and a letter
        // without the mark that belongs to it is a different letter.
        var lo = 0;                 // known to fit: the ellipsis on its own
        var hi = starts.Length - 1; // known not to: the whole string, less nothing

        var best = Build(mark, size, face, truncated: true);

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var candidate = Build(string.Concat(text.AsSpan(0, starts[mid]), mark), size, face, truncated: true);

            if (candidate.Width <= maxWidth)
            {
                best.Dispose();
                best = candidate;
                lo = mid + 1;
            }
            else
            {
                candidate.Dispose();
                hi = mid - 1;
            }
        }

        return best;
    }

    /// <summary>The ellipsis to use: the real character where the face has it, three periods where
    /// it does not.</summary>
    private string Ellipsis() => _faces.Covers(_faces.Primary, 0x2026) ? "\u2026" : "...";

    /// <summary>Splits, shapes and builds one blob with a run per face.</summary>
    private ShapedText Build(string text, float size, int face, bool truncated)
    {
        if (face >= 0)
        {
            // One face, said by the caller: no splitting and no fallback. See GetIcon.
            _runs.Clear();
            _runs.Add(new FontRun(0, text.Length, face));
        }
        else
        {
            FontRuns.Split(text, _coverage, _runs);
        }

        if (_runs.Count == 0)
            return Empty;

        using var builder = new SKTextBlobBuilder();
        var pen = 0f;
        var any = false;

        foreach (var run in _runs)
        {
            var font = Font(run.Face, size);
            var shaper = Shaper(run.Face);
            var slice = text.Substring(run.Start, run.Length);

            var shaped = shaper.Shape(slice, pen, 0, font);
            var count = shaped.Codepoints.Length;
            if (count == 0)
                continue;

            var buffer = builder.AllocatePositionedRun(font, count, null);

            var glyphs = buffer.Glyphs;
            for (var i = 0; i < count; i++)
                glyphs[i] = (ushort)shaped.Codepoints[i];

            shaped.Points.AsSpan().CopyTo(buffer.Positions);

            // Result.Width is the run's OWN advance, not the pen position it ended at: the shaper
            // adds xOffset to every point but returns the width without it. If this assigned instead
            // of adding, a two-script title would report only the width of its last run.
            pen += shaped.Width;
            any = true;
        }

        return any ? new ShapedText(builder.Build(), pen, truncated) : Empty;
    }

    private SKShaper Shaper(int face)
    {
        if (_shapers.TryGetValue(face, out var s))
            return s;

        s = new SKShaper(_faces.Typeface(face));
        _shapers[face] = s;
        return s;
    }

    private SKFont Font(int face, float size)
    {
        var key = (face, Quantize(size));
        if (_fonts.TryGetValue(key, out var f))
            return f;

        f = new SKFont(_faces.Typeface(face), size)
        {
            // Subpixel POSITIONING, which is what keeps a column of text from jittering as the list
            // scrolls. Not subpixel edging: the grid is drawn over the app's glass, and LCD
            // filtering against a translucent background puts colour fringes on every letter.
            Subpixel = true,
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Slight,
        };
        _fonts[key] = f;
        return f;
    }

    public void Dispose()
    {
        Clear();
        foreach (var s in _shapers.Values)
            s.Dispose();
        _shapers.Clear();
        foreach (var f in _fonts.Values)
            f.Dispose();
        _fonts.Clear();
    }
}

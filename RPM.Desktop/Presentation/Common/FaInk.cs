using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// How much of its own square a Font Awesome glyph covers. The value is measured from the face that the
/// app draws the glyph with.
///
/// <para><b>It has to be measured.</b> A font size is not a size on the screen. It names the em, and each
/// glyph fills a different part of it. The folder is a wide, short mark (about 1.02 by 0.77 em). The
/// play triangle is narrow and tall (0.90 by 1.07). So icons that all use the same FontSize look
/// different sizes. The playlist grid's tiles showed exactly that. The numbers are in the font, so they
/// are read from the font. They are not guessed per glyph in a switch, where the next glyph might be
/// missing.</para>
///
/// <para>Each glyph is measured once and then kept. The face is opened when first needed. If the face
/// will not open, the answer is <see cref="None"/>, not a number that would quietly mis-size every
/// icon. See <see cref="PlaylistIcons.TileGlyphSize"/>, which then falls back to the plain em.</para>
/// </summary>
internal static class FaInk
{
    /// <summary>What a glyph covers, as a fraction of the em: the mark's own width and height, with the
    /// empty space around it removed. <see cref="None"/> when it could not be measured.</summary>
    public readonly record struct Ink(double Width, double Height)
    {
        public bool Known => Width > 0 && Height > 0;
    }

    public static readonly Ink None = new(0, 0);

    /// <summary>The size the face is measured at. It is large, so the outward rounding that Skia applies
    /// to the bounds is a tiny error and not a large share of the answer.</summary>
    private const float Probe = 100f;

    private static readonly Lazy<SKTypeface?> Face = new(() =>
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "fa-solid-900.ttf");
            return File.Exists(path) ? SKTypeface.FromFile(path) : null;
        }
        catch
        {
            return null;
        }
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Ink> Measured = new();

    /// <summary>The ink of one glyph. It is measured the first time it is asked for, then
    /// remembered.</summary>
    public static Ink Of(string glyph) =>
        string.IsNullOrEmpty(glyph) ? None : Measured.GetOrAdd(glyph, Measure);

    private static Ink Measure(string glyph)
    {
        try
        {
            if (Face.Value is not { } face)
                return None;

            using var font = new SKFont(face, Probe);
            font.MeasureText(glyph, out var ink);
            return ink.Width <= 0 || ink.Height <= 0
                ? None
                : new Ink(ink.Width / Probe, ink.Height / Probe);
        }
        catch
        {
            return None;
        }
    }
}

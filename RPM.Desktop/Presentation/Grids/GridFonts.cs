using RPM.DataGrid.Text;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The faces the grid draws with, resolved once for the app.
///
/// <para>Two of them. The UI face, which is whatever the rest of the window's text is set in, and
/// Font Awesome, which the glyph columns and the tree's state marks name by code point. Anything
/// else a title happens to be written in is found by <see cref="SkiaFaces"/>'s fallback.</para>
///
/// <para>The icon face is loaded from the file rather than looked up by family name. The app ships
/// it and it is not installed on the machine. A name lookup could miss it, or find another copy of
/// Font Awesome. Its code points are private-use, so the wrong font draws the wrong pictures rather
/// than nothing, and that is hard to notice.</para>
/// </summary>
internal sealed partial class GridFonts : IDisposable
{
    private GridFonts(SkiaFaces faces, int icon)
    {
        Faces = faces;
        Icon = icon;
        Shop = new TextShop(faces);
    }

    /// <summary>The face table, primary first.</summary>
    public SkiaFaces Faces { get; }

    /// <summary>Shapes text and keeps its caches. There is one for the whole app, so the library table and the
    /// queue share the text they have both already shaped.</summary>
    public TextShop Shop { get; }

    /// <summary>Font Awesome's number in <see cref="Faces"/>.</summary>
    public int Icon { get; }

    private static GridFonts? _shared;

    /// <summary>The app's own. Built on first use, on the UI thread.</summary>
    public static GridFonts Shared => _shared ??= Build();

    private static GridFonts Build()
    {
        var faces = new SkiaFaces(UiFace());
        var icon = faces.Intern(IconFace());
        return new GridFonts(faces, icon);
    }

    /// <summary>
    /// The face the rest of the app's text uses. The app sets no font family for body text, so this
    /// is the platform's UI font, which keeps the grid in line with the panes beside it.
    /// </summary>
    internal static SKTypeface UiFace()
    {
        // Named rather than left to SKTypeface.Default, which on some platforms is a serif face
        // nothing else in the app uses.
        foreach (var name in new[] { ".AppleSystemUIFont", "SF Pro Text", "Helvetica Neue", "Segoe UI Variable Text", "Segoe UI" })
        {
            if (SKTypeface.FromFamilyName(name) is { } face && face.FamilyName.Length > 0)
                return face;
        }

        return SKTypeface.Default;
    }

    /// <summary>Font Awesome Solid, from the file the app ships.</summary>
    private static SKTypeface IconFace()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "fa-solid-900.ttf");

        // If the icon font is missing, the glyph columns draw .notdef boxes. That is the chosen failure:
        // the grid still works and the marks are plainly wrong, instead of the app failing to start.
        return (File.Exists(path) ? SKTypeface.FromFile(path) : null) ?? SKTypeface.Default;
    }

    public void Dispose()
    {
        Shop.Dispose();
        Faces.Dispose();
    }
}

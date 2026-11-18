using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The two marks a drawer puts outside itself: the triangle pointing up at the record it belongs to, and
/// the ring around that record's card. This class holds their colours and the walk between two of them.
///
/// <para>The code is here, not on the wall, because two surfaces draw these marks now. The album wall's
/// cards sit on a fixed stride, and the home screen's rows scroll sideways. So WHERE the triangle goes is
/// calculated separately on each surface. What the triangle is made of must be the same on both. If its
/// fill is made twice, the two drift apart, and the first place they drift is the junction where the mark
/// meets the drawer.</para>
/// </summary>
public static class BandPointer
{
    /// <summary>Half the triangle's width. The triangle is drawn from its middle, so this is the amount
    /// taken off a card's centre to place it. It is also the room needed at either end of a row to draw
    /// the triangle whole.</summary>
    public const double TriangleHalf = 10;

    /// <summary>How far below the triangle's point the drawer's top edge is. Each drawer has this as its
    /// top margin, in the same Grid as its triangle: <c>AlbumWallView.xaml</c>, <c>HomeView.xaml</c> and the
    /// shelves in <c>ArtistDetailView.xaml</c>. Change them together.</summary>
    public const double Join = 10;

    /// <summary>The triangle's height when it runs past <see cref="Join"/>, under the drawer.</summary>
    private const double Depth = 12;

    /// <summary>
    /// The fill of the pointer triangle: the drawer's own colour where the triangle meets it, solid from
    /// the base to the point.
    ///
    /// <para>Solid, with no fade at the point. A triangle that faded to nothing at its tip showed a pale,
    /// greyish point over the dark row, which read as a second colour in the mark rather than as the drawer
    /// rising to a point.</para>
    ///
    /// <para>The plate a drawer opens on is a thin white, not an opaque colour, and the triangle takes it
    /// with its own alpha, so the mark is as faint as the drawer it belongs to. Where the triangle stops
    /// depends on that alpha: see <see cref="Corners"/>.</para>
    /// </summary>
    public static SolidColorBrush Fill(Color colour) => new(colour);

    /// <summary>
    /// The triangle's three corners for a fill, in the triangle's own units: the point at the top, the
    /// drawer's edge at <see cref="Join"/>.
    ///
    /// <para>An opaque fill runs 2 units past the edge, and the drawer draws over that strip. Then the
    /// join has no seam when it falls between two device pixels (a scrolled row, a scale that is not a
    /// whole number). Two shapes that meet inside one pixel each cover only part of it, and the row behind
    /// shows through there.</para>
    ///
    /// <para>A translucent fill stops at the edge. Under the drawer the strip would be painted twice, the
    /// triangle's alpha and then the drawer's, and would show as a bright line across the base. Where two
    /// thin shapes meet inside one pixel the seam is too faint to see, so nothing needs to run past.</para>
    ///
    /// <para>Both are the same triangle above the edge: the translucent one is the opaque one cut at
    /// <see cref="Join"/>. A fill that is not known to be opaque stops at the edge.</para>
    /// </summary>
    public static IReadOnlyList<Point> Corners(Color? fill)
    {
        if (fill is { A: 255 })
            return [new(0, Depth), new(TriangleHalf, 0), new(2 * TriangleHalf, Depth)];

        var half = TriangleHalf * Join / Depth;
        return [new(TriangleHalf - half, Join), new(TriangleHalf, 0), new(TriangleHalf + half, Join)];
    }

    /// <summary>The triangle's outline for <paramref name="fill"/> (see <see cref="Corners"/>), as the
    /// polygon takes it.</summary>
    public static PointCollection Outline(Brush? fill)
    {
        var outline = new PointCollection();
        foreach (var corner in Corners(ColourOf(fill)))
            outline.Add(corner);
        return outline;
    }

    /// <summary>
    /// Paints a triangle placed from code: its fill, and the outline that fill needs. The two are set
    /// together here, so a triangle cannot keep the outline of its last fill. The outline is replaced only
    /// when it changes, because the fill is set on every frame of a cross.
    /// </summary>
    public static void Paint(Polygon point, Brush fill)
    {
        point.Fill = fill;
        var corners = Corners(ColourOf(fill));
        if (point.Points is not { } now || !now.SequenceEqual(corners))
            point.Points = Outline(fill);
    }

    /// <summary>The colour a triangle was filled with, which is the drawer's own colour. Null if the brush
    /// was not made by <see cref="Fill"/>, so the caller can use its own fallback.</summary>
    public static Color? ColourOf(Brush? brush) => (brush as SolidColorBrush)?.Color;

    /// <summary>The plate a drawer opens on: the frosted white that every other box of text in this
    /// window stands on. It is read from the app's resources, so no drawer can differ from them.</summary>
    public static Color Plate =>
        (Application.Current.Resources["RpmCardPlateBrush"] as SolidColorBrush)?.Color
        ?? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// One colour moved toward another by <paramref name="t"/> (0 to 1), the way the drawer moves: the
    /// second laid over the first at opacity <paramref name="t"/>.
    ///
    /// <para>The drawer crosses into its sleeve's colours by fading the ground in, as a layer, over the
    /// card plate, which is a thin white and mostly transparent. The triangle and the ring must cross the
    /// same way, so the two colours are mixed with their alpha applied, not channel by channel. A straight
    /// mix of the thin white and an opaque colour is half white half way through: the triangle went pale
    /// while the drawer beside it did not. Between two opaque colours the two mixes are the same.</para>
    /// </summary>
    public static Color Blend(Color from, Color to, double t)
    {
        var fromAlpha = from.A / 255.0;
        var toAlpha = to.A / 255.0;
        var alpha = fromAlpha + ((toAlpha - fromAlpha) * t);
        if (alpha <= 0)
            return Color.FromArgb(0, to.R, to.G, to.B);

        return Color.FromArgb(
            (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255),
            Channel(from.R, to.R), Channel(from.G, to.G), Channel(from.B, to.B));

        byte Channel(byte a, byte b)
        {
            var mixed = (a * fromAlpha) + (((b * toAlpha) - (a * fromAlpha)) * t);
            return (byte)Math.Clamp(Math.Round(mixed / alpha), 0, 255);
        }
    }
}

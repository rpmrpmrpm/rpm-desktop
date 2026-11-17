using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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

    /// <summary>
    /// The fill of the pointer triangle: the drawer's own colour where the triangle meets it, solid from
    /// the base to the point.
    ///
    /// <para>Solid, with no fade at the point. A triangle that faded to nothing at its tip showed a pale,
    /// greyish point over the dark row, which read as a second colour in the mark rather than as the drawer
    /// rising to a point.</para>
    ///
    /// <para>The triangle's base runs two pixels past the drawer's top edge, and the drawer draws over it,
    /// so the join has no sub-pixel gap. See the band templates.</para>
    ///
    /// <para>The plate a drawer opens on is a thin white, not an opaque colour, and the triangle takes it
    /// with its own alpha, so the mark is as faint as the drawer it belongs to.</para>
    /// </summary>
    public static SolidColorBrush Fill(Color colour) => new(colour);

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

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
/// calculated separately on each surface. What the triangle is made of must be the same on both. If a
/// ramp is built twice, the two ramps drift apart, and the first place they drift is the junction where
/// the mark meets the drawer.</para>
/// </summary>
public static class BandPointer
{
    /// <summary>Half the triangle's width. The triangle is drawn from its middle, so this is the amount
    /// taken off a card's centre to place it. It is also the room needed at either end of a row to draw
    /// the triangle whole.</summary>
    public const double TriangleHalf = 10;

    /// <summary>Where the triangle meets the drawer, as a fraction of its own height: 10 of its 12
    /// pixels. The other 2 pixels are the overlap, which the drawer covers. See the band
    /// templates.</summary>
    private const double Join = 10.0 / 12.0;

    /// <summary>
    /// The fill of the pointer triangle: its colour, faded to nothing at the point.
    ///
    /// <para>A flat triangle would be a hard shape on the wall: the drawer's colour on a dark row, with
    /// two sharp diagonals. A ramp makes the same triangle look like the drawer's ground rising to a
    /// point. It has no edge at the top, and it reaches full colour where the drawer's own top edge takes
    /// over.</para>
    ///
    /// <para>Only the point is soft. The triangle is twelve pixels tall. A ramp spread over all of them
    /// would make the triangle disappear, and it must still read as a mark pointing at its record. So it
    /// is at full colour by half its height. The fade is only on the top few pixels, where there is
    /// nothing underneath to meet.</para>
    ///
    /// <para>Full colour is reached at <see cref="Join"/>, not at the bottom. The triangle's base runs
    /// two pixels past the drawer's top edge, and the drawer draws over it. So the junction has no
    /// sub-pixel gap. If the ramp reached full colour at the bottom of the shape, the visible part would
    /// meet the drawer a sixth short of its colour, and the seam would show.</para>
    ///
    /// <para>Both stops use the same colour and differ only in alpha. A fade to transparent black would
    /// pull the point toward grey.</para>
    ///
    /// <para>The ramp is a fraction of the colour's own alpha, not a fixed value. The drawer opens on the
    /// card plate, which is a thin white, not an opaque colour. A fixed alpha would make a solid white
    /// arrow on the wall, where the drawer is barely visible.</para>
    /// </summary>
    public static LinearGradientBrush Ramp(Color colour)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Fade(colour, 0), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Fade(colour, 0xE0 / 255.0), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = colour, Offset = Join });
        return brush;

        static Color Fade(Color c, double of) =>
            Color.FromArgb((byte)Math.Round(c.A * of), c.R, c.G, c.B);
    }

    /// <summary>The colour a ramp was built from: its full stop, which is the drawer's own colour. Null
    /// if the brush was not made by <see cref="Ramp"/>, so the caller can use its own fallback.</summary>
    public static Color? ColourOf(Brush? brush) =>
        brush is LinearGradientBrush { GradientStops.Count: > 0 } ramp ? ramp.GradientStops[^1].Color : null;

    /// <summary>The plate a drawer opens on: the frosted white that every other box of text in this
    /// window stands on. It is read from the app's resources, so no drawer can differ from them.</summary>
    public static Color Plate =>
        (Application.Current.Resources["RpmCardPlateBrush"] as SolidColorBrush)?.Color
        ?? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

    /// <summary>One colour moved toward another by <paramref name="t"/> (0 to 1). Alpha is blended too,
    /// because one end of the drawer's walk is the card plate, which is mostly transparent. Each channel
    /// is blended directly, because there is no hue to travel between.</summary>
    public static Color Blend(Color from, Color to, double t) => Color.FromArgb(
        (byte)Math.Clamp(Math.Round(from.A + ((to.A - from.A) * t)), 0, 255),
        (byte)Math.Clamp(Math.Round(from.R + ((to.R - from.R) * t)), 0, 255),
        (byte)Math.Clamp(Math.Round(from.G + ((to.G - from.G) * t)), 0, 255),
        (byte)Math.Clamp(Math.Round(from.B + ((to.B - from.B) * t)), 0, 255));
}

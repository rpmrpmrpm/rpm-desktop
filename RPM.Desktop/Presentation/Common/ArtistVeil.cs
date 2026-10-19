using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The artist's colour laid over their photograph, as a gradient that fades out. It is not a flat sheet.
///
/// <para>A flat sheet of the accent colour over a grey copy of the picture makes every face come out in one
/// tone. Across a wall of two thousand artists, the faces would not look like the people they show. The
/// palette is meant to hold the wall together, but a flat sheet does that by hiding the photographs, which
/// are the only thing on the wall worth looking at.</para>
///
/// <para>A gradient keeps both. The picture keeps its own colours at the top of the box, where the face
/// usually is. The accent gathers towards the foot, where the name is written, and where a photograph is
/// mostly background. The wall still uses the app's palette. The colour is placed where it hides
/// nothing.</para>
///
/// <para>Brushes are cached, not made again for each box. The wall only uses the palette's ten colours,
/// and a box is a brush that is created and removed as it scrolls past.</para>
/// </summary>
public static class ArtistVeil
{
    /// <summary>
    /// The gradient for a box on the wall. It is strong, because a box is a small card with a name across
    /// its middle. The colour is what makes thousands of them look like one wall.
    /// </summary>
    public static Brush? Over(Color accent) => Kept(accent, Box);

    /// <summary>
    /// The gradient for the artist page's band. It uses the same colour, but much more lightly.
    ///
    /// <para>The box's strength is wrong here, because the band is a different size. A box is small and
    /// mostly name. The band is about 400 points of one photograph, and it is the subject of the screen,
    /// not one of thousands of tiles that have to agree with each other. At the box's strength the picture
    /// would look printed in one ink, which is the same fault as the flat sheet. Here the colour is a cast
    /// over the foot only, and the rest is a photograph.</para>
    ///
    /// <para>The band does not need to carry the name, so it can be this light. The foot of the band fades
    /// into the page, and is not darkened behind the text. See the hero dissolve in
    /// ArtistDetailView.</para>
    /// </summary>
    public static Brush? Page(Color accent) => Kept(accent, PageFall);

    private static Brush? Kept(Color accent, (byte Top, byte Middle, byte Foot) fall)
    {
        var key = (accent, fall);
        lock (Held)
        {
            if (Held.TryGetValue(key, out var kept))
                return kept;

            var brush = Paint(accent, fall);
            Held[key] = brush;
            return brush;
        }
    }

    private static Brush Paint(Color accent, (byte Top, byte Middle, byte Foot) fall)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = At(accent, fall.Top), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = At(accent, fall.Middle), Offset = 0.5 });
        brush.GradientStops.Add(new GradientStop { Color = At(accent, fall.Foot), Offset = 1 });
        return brush;
    }

    private static Color At(Color accent, byte alpha)
        => Color.FromArgb(alpha, accent.R, accent.G, accent.B);

    /// <summary>The alpha of the accent at the top of a box, at its middle (where the name is), and at its
    /// foot. The top is not zero. A photo with no colour there would look different from one with full
    /// colour, so the wall would look like two walls. But the amount is small enough that the picture is
    /// still visible.</summary>
    private static readonly (byte Top, byte Middle, byte Foot) Box = (0x2B, 0x82, 0xCC);

    /// <summary>The same for the page band: a light touch at the top and a cast at the foot. This is about
    /// a third of the box's strength. A photograph shown large still needs that much of the picture left
    /// to look like a photograph.</summary>
    private static readonly (byte Top, byte Middle, byte Foot) PageFall = (0x0D, 0x2B, 0x59);

    private static readonly Dictionary<(Color, (byte, byte, byte)), Brush> Held = [];
}

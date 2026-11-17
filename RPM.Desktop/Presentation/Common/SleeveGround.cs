using Windows.UI;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The ground that a record makes from its own sleeve. It has three parts: the sleeve's mean
/// colour, the sleeve blurred, and how much black must be laid over the blur so that white text can be
/// read on it.
///
/// <para>There are three layers, not one brush, because each layer gives a different part of the
/// record. The colour gives its hue, the wash gives its shape, and the veil gives its exposure. A
/// surface that uses all three looks as if it stands on the sleeve. A surface that uses one flat colour
/// taken from it does not.</para>
///
/// <para>This is shared by the album page and the wall's drawer, so that "the same background" is true
/// by construction. There is one place that defines it. The ground is read from the file bytes on a
/// background thread. It is null where the picture will not decode, and each caller must then show a
/// surface with no ground.</para>
/// </summary>
/// <param name="Mean">The sleeve's average colour, which the ground and the veil are derived
/// from.</param>
/// <param name="Edge">The average colour of the band that the surface's top edge shows. It is not the
/// same as <c>Mean</c>. See <see cref="EdgeFrom"/>.</param>
/// <param name="Ground">The colour under everything, muted out of the mean.</param>
/// <param name="Wash">The sleeve blurred until it is no longer a recognisable picture, or null where
/// the blur failed.</param>
/// <param name="Veil">How much flat black is laid inside the wash, from 0 to 1.</param>
public sealed record SleeveGround(Color Mean, Color Edge, Color Ground, byte[]? Wash, double Veil)
{
    /// <summary>How much of the blurred sleeve is let through over the colour taken from it. Half. The
    /// ground carries the record's hue and the wash carries its shape. A wash much stronger than this
    /// stops being a light in the room and becomes the sleeve printed across the page.</summary>
    public const double WashStrength = 0.5;

    /// <summary>Reads the ground from a sleeve. This call blocks, so run it on a worker thread. It
    /// decodes the picture twice: once to blur it, and once to average the blur.
    ///
    /// <para>The mean is read from the blurred copy, not from the sleeve. It is the same picture,
    /// averaged, and it is already decoded at a small size. That is one small decode instead of a
    /// second full one. The tone it reports is also the tone the reader actually sees.</para>
    /// </summary>
    public static SleeveGround? Read(byte[] sleeve)
    {
        var wash = ArtScaler.Wash(sleeve);
        if (CoverColor.Average(wash ?? sleeve, EdgeFrom, EdgeTo) is not { } read)
            return null;

        return new SleeveGround(read.Whole, read.Band, CoverColor.Ground(read.Whole), wash,
                                CoverColor.Veil(read.Whole))
        {
            Grid = wash is null ? null : WashGrid.Read(wash),
        };
    }

    /// <summary>The blurred sleeve as a small grid of colours, so the colour at one point of it can be
    /// read. Null where there is no wash, or where the wash would not decode. See <see cref="FlatAt"/>.</summary>
    public WashGrid? Grid { get; init; }

    /// <summary>The band of the sleeve that a surface's top edge actually shows, read from the top
    /// down. It is not the whole picture, and it is not the top row. The difference matters. A triangle
    /// drawn in this band looks like part of the drawer. If it is drawn in the wrong band, it looks
    /// like a shape sitting on the drawer.
    ///
    /// <para>The wash is drawn with UniformToFill. A square sleeve in a box three times as wide as it
    /// is tall is scaled until it covers the width. That leaves a third of its height on screen, taken
    /// from the middle. So the pixels along the top edge come from about a third of the way down the
    /// sleeve. They are never from the top row, and only by chance from its average. A drawer's shape
    /// moves this a little. This band is where the edge lands across the shapes that a tracklist
    /// uses.</para>
    /// </summary>
    private const double EdgeFrom = 0.28;
    private const double EdgeTo = 0.44;

    /// <summary>The same ground as ONE colour, for surfaces that cannot hold three layers, such as the
    /// drawer's pointer triangle and the open card's ring.
    ///
    /// <para>This is not a separate idea of the record's colour. It is what the three layers come to at
    /// the surface's top edge. The wash, under its own veil, sits over the ground at <see
    /// cref="WashStrength"/>. The colour of the wash at that point is <see cref="Edge"/>, not the mean.
    /// A triangle painted in the mean would show the record's average tone against the part of the
    /// sleeve that is really on screen. On most sleeves those two colours are clearly different, so the
    /// triangle looks like a separate shape on the drawer.</para>
    /// </summary>
    public Color Flat => Compose(Edge);

    /// <summary>
    /// The colour a box wearing this ground shows at one point: the ground, the wash at that point under
    /// its veil, at <see cref="WashStrength"/>. The box is <paramref name="width"/> by
    /// <paramref name="height"/>, and the wash is drawn over it with UniformToFill, centred, as the
    /// drawer and the album page draw it.
    ///
    /// <para>This is what the pointer triangle wears. <see cref="Flat"/> is one colour for the whole top
    /// edge, averaged across the full width of the sleeve. The triangle meets the edge at one place, and
    /// a sleeve is seldom the same colour from side to side: a dark border and a bright logo in the middle
    /// average to a colour that is darker than the middle and lighter than the border. A triangle painted
    /// in that average shows a hard line where it meets the drawer. Read at the place where the triangle
    /// stands, the two are the same colour, because the wash is blurred far wider than the
    /// triangle.</para>
    ///
    /// <para><see cref="Flat"/> is the answer where the colour cannot be read at a point: no wash, no grid,
    /// or a box that has not been laid out yet.</para>
    /// </summary>
    public Color FlatAt(double x, double y, double width, double height)
    {
        if (Wash is null || Grid is not { } grid || width <= 0 || height <= 0)
            return Flat;
        var (u, v) = grid.Place(x, y, width, height);
        return Compose(grid.At(u, v));
    }

    /// <summary>
    /// The three layers over each other, as the head draws them, with <paramref name="wash"/> as the
    /// wash's colour at the point being read.
    ///
    /// <para>The veil is a child of the wash in every surface that wears a ground, at the wash's own
    /// opacity. The two heads draw that differently, and the colour given here has to be the colour on
    /// screen. WinUI draws the wash and its veil together and lays the pair over the ground at
    /// <see cref="WashStrength"/>, so the veil darkens the wash only. The Skia head draws the wash over the
    /// ground first and then the veil over both, at the wash's strength times its own, so the veil darkens
    /// the ground as well. For example, on the Skia head a ground of (104, 86, 98), a wash of
    /// (205, 200, 194) and a veil of 0.372 draw (126, 117, 119). The first rule gives (116, 106, 110),
    /// the second (126, 116, 119).</para>
    /// </summary>
    private Color Compose(Color wash)
    {
        var lit = Wash is null ? 0 : WashStrength;
        return Color.FromArgb(0xFF, Mix(Ground.R, wash.R), Mix(Ground.G, wash.G), Mix(Ground.B, wash.B));

        byte Mix(byte ground, byte over)
        {
#if RPM_WINUI
            var drawn = (ground * (1 - lit)) + (over * (1 - Veil) * lit);
#else
            var drawn = ((ground * (1 - lit)) + (over * lit)) * (1 - (Veil * lit));
#endif
            return (byte)Math.Clamp(Math.Round(drawn), 0, 255);
        }
    }
}

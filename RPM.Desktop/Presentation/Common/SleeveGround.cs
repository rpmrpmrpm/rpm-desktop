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
                                CoverColor.Veil(read.Whole));
    }

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
    public Color Flat
    {
        get
        {
            var lit = Wash is null ? 0 : WashStrength;
            return Color.FromArgb(0xFF, Mix(Ground.R, Edge.R), Mix(Ground.G, Edge.G), Mix(Ground.B, Edge.B));

            byte Mix(byte ground, byte edge) =>
                (byte)Math.Clamp(Math.Round((ground * (1 - lit)) + (edge * (1 - Veil) * lit)), 0, 255);
        }
    }
}

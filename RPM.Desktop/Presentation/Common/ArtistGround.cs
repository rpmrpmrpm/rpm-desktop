using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The box an artist gets when no photograph of them exists. It uses a colour taken from the first record
/// they have, snapped to the app's palette, and laid across the box as a gradient.
///
/// <para><b>Why use a record's colour and not a colour of their own.</b> Much of a real collection is not
/// on fanart.tv. Examples are local bands, bootlegs, and names that a tagger typed differently. If many
/// boxes were the same flat dark colour, the wall would look like a wall of failures and not a wall of
/// artists. The one thing the app reliably has for these names is their sleeves. A sleeve's colour is not
/// an arbitrary stand-in. It is the colour the artist chose to be seen in, on the record that the library
/// files first under their name.</para>
///
/// <para><b>The colour is also snapped to the palette, for the same reason as the photographs.</b> Taken
/// straight from the sleeve, it would be thousands of colours and no palette (see
/// <see cref="RpmPalette"/>). Snapped, a box with no photograph uses one of the app's ten accents. It then
/// sits beside the washed photographs as the same kind of thing. The wall should read as one wall, not as
/// photographs with holes in them.</para>
///
/// <para><b>Two colours, not one.</b> With only ten accents, a wall of artists without photographs would
/// show the same flat orange on a dozen boxes in one screen. The second colour is the accent next to the
/// first on the palette's hue ring. So the pair is always two neighbouring colours. Which neighbour it is,
/// which way the gradient falls, and where it turns over are all taken from the artist's NAME. That makes
/// the box theirs. It is the same box every time the wall is built, on every machine. Two artists whose
/// records share a colour still get two different boxes.</para>
/// </summary>
public static class ArtistGround
{
    /// <summary>
    /// The box for one artist, based on the colour of their first record. Returns null when the palette
    /// cannot be read at all. The box then keeps its own frosted plate.
    /// </summary>
    /// <param name="sleeve">The dominant colour of their first record's cover.</param>
    /// <param name="artist">Their name. The arrangement of the gradient is taken from the name, not from a
    /// random number, so a box does not change its look when the wall is rebuilt.</param>
    public static Brush? From(Color sleeve, string artist)
    {
        if (Pair(sleeve, artist) is not { } pair)
            return null;

        var (primary, secondary, at) = pair;
        var seed = Seed(artist);
        var fall = Falls[(int)((seed >> 1) % (uint)Falls.Length)];

        // Brushes are kept, not made again for each box. The wall can only use 10 accents, 2 neighbours,
        // 4 falls and 3 turns, which is 240 combinations. A box is a brush that is created and removed as
        // it scrolls.
        var key = (primary, secondary, fall, at);
        lock (Kept)
        {
            if (Kept.TryGetValue(key, out var kept))
                return kept;

            var brush = Paint(primary, secondary, fall, at);
            Kept[key] = brush;
            return brush;
        }
    }

    /// <summary>
    /// The two accents for a colour and a name, and the point where they meet.
    ///
    /// <para>This is the one place where the pairing is decided. So a box and a band cannot get two
    /// different pairs for the same artist or the same shelf. A colour with no hue, such as a black and
    /// white cover, does not snap to a palette colour. It keeps its own tone, and its partner is that tone
    /// at a lower brightness. So a grey sleeve gives a grey box.</para>
    /// </summary>
    private static (Color Primary, Color Secondary, double At)? Pair(Color colour, string name)
    {
        var ring = Ring;
        if (ring.Count == 0)
            return null;   // No palette means there is nothing to be near. The caller keeps its own surface.

        var seed = Seed(name);

        var primary = RpmPalette.Nearest(colour);
        var index = ring.IndexOf(primary);
        var secondary = index < 0
            ? CoverColor.Dim(primary, NeutralPartner)
            : ring[(index + ((seed & 1) == 0 ? 1 : ring.Count - 1)) % ring.Count];

        return (primary, secondary, Turns[(int)((seed >> 4) % (uint)Turns.Length)]);
    }

    private static Brush Paint(Color primary, Color secondary, (Point Start, Point End) fall, double turn)
    {
        var brush = new LinearGradientBrush { StartPoint = fall.Start, EndPoint = fall.End };

        // The colours are dimmed on the way in, and not evenly. A box is drawn under the same black veil
        // that photographed boxes have. A palette accent at full strength under that veil comes out
        // BRIGHTER than a grey press photo washed with the same accent. That is close enough to the white
        // text to clash with it. The far end is darker still. That makes the box look lit from one
        // corner, and not like a rectangle that someone filled in.
        brush.GradientStops.Add(new GradientStop { Color = CoverColor.Dim(primary, NearEnd), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = CoverColor.Dim(Mix(primary, secondary), Middle), Offset = turn });
        brush.GradientStops.Add(new GradientStop { Color = CoverColor.Dim(secondary, FarEnd), Offset = 1 });

        return brush;
    }

    /// <summary>Halfway between two colours, calculated channel by channel. It is not done in a colour
    /// space. This is the middle of a gradient. The renderer interpolates between two stops in exactly this
    /// way, so a different midpoint would show as a visible kink in the gradient.</summary>
    private static Color Mix(Color a, Color b) => Color.FromArgb(
        0xFF,
        (byte)((a.R + b.R) / 2),
        (byte)((a.G + b.G) / 2),
        (byte)((a.B + b.B) / 2));

    /// <summary>How much of the accent is kept at the lit corner, at the turn, and at the far corner. The
    /// near end is kept just under full strength. Gold is the brightest of the ten, and it must still take
    /// white text under the box's black. The far end is dark enough that the gradient reads as a gradient
    /// and not as a plain tint.</summary>
    private const double NearEnd = 0.84;
    private const double Middle = 0.66;
    private const double FarEnd = 0.46;

    /// <summary>The second colour for a sleeve with no hue: the same colour, dimmed further. A colour that
    /// is not on the ring has no neighbour on it.</summary>
    private const double NeutralPartner = 0.45;

    /// <summary>The four directions in which the gradient can run across the box. All four are diagonals.
    /// A box is nearly twice as wide as it is tall, and a straight left-to-right fall would look like a
    /// band, not a corner. The name picks one, so each pair can have four different arrangements.</summary>
    private static readonly (Point Start, Point End)[] Falls =
    [
        (new Point(0, 0), new Point(1, 1)),
        (new Point(1, 0), new Point(0, 1)),
        (new Point(0, 1), new Point(1, 0)),
        (new Point(1, 1), new Point(0, 0)),
    ];

    /// <summary>The points where the two colours meet. They are off centre, because a turn in the middle of
    /// the box would fall exactly where the name is written.</summary>
    private static readonly double[] Turns = [0.38, 0.5, 0.64];

    private static readonly Dictionary<(Color, Color, (Point, Point), double), Brush> Kept = [];

    /// <summary>The palette as a ring. The accents are in the order that the asset file lists them. That
    /// order runs round the hues, so the entry next to one is a colour next to it.
    ///
    /// <para>The value is kept only once it has entries. Do NOT use a <see cref="Lazy{T}"/> here. See
    /// <see cref="RpmPalette"/>, which explains the same trap. This is read from a worker thread that draws
    /// a box. A Lazy would keep whatever the first worker got. If an empty ring were kept, the app would
    /// have no palette until it was restarted.</para></summary>
    private static List<Color> Ring
        => _ring is { Count: > 0 } have
            ? have
            : RpmPalette.Colours is { Count: > 0 } read ? _ring = [.. read] : [];

    private static List<Color>? _ring;

    /// <summary>
    /// A name as a number: FNV-1a over the letters, folded for case.
    ///
    /// <para>This is written out by hand, instead of using <see cref="string.GetHashCode()"/>. That is
    /// seeded per PROCESS. The same artist would get a different box each time the app started. The
    /// arrangement must be the same every time.</para>
    /// </summary>
    private static uint Seed(string name)
    {
        var hash = 2166136261u;
        foreach (var c in name)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619u;
        }
        return hash;
    }
}

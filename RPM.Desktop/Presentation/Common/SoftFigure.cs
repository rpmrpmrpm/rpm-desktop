using SkiaSharp;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>A picture for something nobody has photographed: a soft abstract form, blurred until it
/// has no recognisable shape, in colours that belong to whatever it stands for.
///
/// <para><b>Why a picture and not a gradient.</b> A surface can paint a plain two-stop gradient itself
/// (see
/// <see cref="ArtistGround"/>). On a wall that looks like a rectangle that someone filled in. Most of a
/// real collection is artists that fanart.tv has no picture for, so that rectangle is not the
/// exception. It is most of the wall. This class makes an ordinary picture instead: six wide, soft
/// blobs in the record's own colours, laid over each other and blurred until none of them has an edge.
/// It reads as a picture of nothing in particular, and not as a missing photograph.</para>
///
/// <para><b>Drawn once, off the UI thread, and handed over as bytes.</b> The reason is the same as for
/// <see cref="Services.Art.ArtScaler.Wash"/>. A blur done here is one pass on a background thread, and
/// what comes out is a picture that any surface can draw. A blur done on the surface would run again on
/// every frame the surface is shown, and it needs an effect pipeline that the machine may not
/// have.</para>
///
/// <para><b>It is the same picture every time.</b> Every arrangement is read off the artist's NAME.
/// That covers where each blob sits, how wide it is, which colour it takes, and which way the light
/// falls across it. So a box does not rearrange itself when the wall is rebuilt, and the same artist
/// looks the same on every machine. See
/// <see cref="Seed"/>. It is written out, not taken from <see cref="string.GetHashCode()"/>, for
/// exactly that reason.</para>
/// </summary>
public static class SoftFigure
{
    /// <summary>
    /// The figure for a BOX on the wall, in the shape the boxes are.
    /// </summary>
    /// <param name="colours">Colours read off one of their records, best first. Empty gives null: there
    /// is nothing to make a picture out of.</param>
    public static byte[]? Box(IReadOnlyList<Color> colours, string artist)
        => Paint(colours, artist, 384, 216, variant: 0, blurDivisor: ArtistBlurDivisor);

    /// <summary>The figure for the BANNER at the top of an artist's page. It is a different picture
    /// from the box, and is not the box stretched.
    ///
    /// <para>It has to be different. The banner is four hundred points tall and a box is about an inch.
    /// One picture for both would be either a banner made from a thumbnail, or a box that is a crop of
    /// something wider. Also, opening a box would show the reader the same arrangement twice, at two
    /// sizes. That looks like the page failed to load its own picture. The colours and the name are the
    /// same, and the draw is different. The shared look is the point. The repetition is not.</para>
    /// </summary>
    public static byte[]? Banner(IReadOnlyList<Color> colours, string artist)
        => Paint(colours, artist, 512, 384, variant: 1, blurDivisor: ArtistBlurDivisor);

    /// <summary>The GROUND that a playlist's cover stands on: the same soft form, square, at whatever
    /// size the caller is composing at.
    ///
    /// <para>It is used by <see cref="PlaylistArt"/>, where the sleeves of the songs in a list are laid
    /// over it, inset from the edges. So this is the only figure in the file that is drawn to be
    /// covered up. What shows of it is a border in the playlist's own colours. That is why the colours
    /// come from the caller. They are read off the same sleeves that sit on top.</para>
    /// </summary>
    /// <param name="edge">The side of the square, in pixels. The caller states it, because a cover is
    /// composed at a size that this class should not choose.</param>
    public static byte[]? Ground(IReadOnlyList<Color> colours, string identity, int edge)
        => Paint(colours, identity, edge, edge, variant: 3, blurDivisor: BlurDivisor);

    /// <summary>The figure a GENERATED playlist is drawn as: the palette's own colours, taken from its
    /// name. It is square, and it is softened only half as much as everything else in this file.
    ///
    /// <para><b>Why less blur.</b> Every other figure here is a background. Something stands on it, and
    /// a picture with a clear shape would compete with what stands on it. This figure IS the picture. A
    /// generated list has no sleeves to show, because it is a query and not a set of records. So the
    /// form is all there is to look at. At the usual blur depth it would become one averaged tone. Half
    /// the blur keeps the places where two colours meet, and still leaves no edge anywhere.</para>
    ///
    /// <para>The colours come off the NAME, as they do for a record with no cover (see <see
    /// cref="Sleeve"/>). The reason is the same. A generated list is a rule, not a thing, and it has no
    /// sleeve to borrow a colour from. The same name gives the same picture on every machine.</para>
    /// </summary>
    /// <param name="edge">The square's side in pixels.</param>
    public static byte[]? Metaballs(string identity, int edge)
        => RingColours(identity) is { Count: > 0 } colours
            ? Paint(colours, identity, edge, edge, variant: 4, blurDivisor: HalfBlurDivisor)
            : null;

    /// <summary>Lays the blobs down, then blurs them.
    ///
    /// <para>The canvas is small on purpose, like every other blur in this app. The picture is about to
    /// lose all of its detail, so a larger canvas would only draw pixels to throw away. The size it is
    /// drawn at has no effect on what it is shown at. A blur is the one operation whose result does not
    /// depend on the resolution it was taken at.</para>
    /// </summary>
    /// <param name="blurDivisor">How hard the finished picture is blurred, as a fraction of its long
    /// edge. See
    /// <see cref="BlurDivisor"/>. Every caller states it, and there is no default. A box on the artists
    /// wall and a made sleeve are looked at at different sizes, and they need different softness. A
    /// default would silently give one of them the other's.</param>
    private static byte[]? Paint(
        IReadOnlyList<Color> colours, string artist, int width, int height, int variant, float blurDivisor)
    {
        if (colours.Count == 0)
            return null;

        try
        {
            var ink = Palette(colours, artist);
            var random = new Roll(Seed(artist) ^ (uint)(variant * 0x9E3779B9));

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null)
                return null;

            var canvas = surface.Canvas;

            // The ground is the darkest of the colours, so wherever no blob reaches is still the
            // record's own colour rather than a hole.
            canvas.Clear(Darkest(ink));

            var edge = MathF.Max(width, height);

            // Wide and overlapping. A blob narrower than about half the picture looks like a spot on
            // something. At this size the blobs are the picture. What the eye sees is where two colours
            // meet, not a circle.
            for (var i = 0; i < Blobs; i++)
            {
                var colour = ink[i % ink.Count];
                var centre = new SKPoint(
                    random.Between(-0.15f, 1.15f) * width,
                    random.Between(-0.15f, 1.15f) * height);
                var radius = random.Between(0.45f, 0.95f) * edge;

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(
                        centre, radius,
                        [colour, colour.WithAlpha(0)],
                        [0f, 1f],
                        SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(SKRect.Create(width, height), paint);
            }

            // One shaft of light goes across it. Without it the picture is pleasant but has no shape. A
            // single soft diagonal makes it read as a FORM rather than a stain. It is also the only
            // element that has a direction.
            using (var shaft = new SKPaint
            {
                IsAntialias = true,
                Shader = LightShaft(random, width, height, Lightest(ink)),
            })
                canvas.DrawRect(SKRect.Create(width, height), shaft);

            canvas.Flush();

            // Then the whole picture is blurred. This turns the overlapping circles into one continuous
            // shape with no edges anywhere in it.
            using var drawn = surface.Snapshot();
            using var soft = SKSurface.Create(info);
            if (soft is null)
                return null;

            using (var blur = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(
                    edge / blurDivisor, edge / blurDivisor, SKShaderTileMode.Clamp),
            })
                soft.Canvas.DrawImage(drawn, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blur);

            using var figure = soft.Snapshot();
            using var encoded = figure.Encode(SKEncodedImageFormat.Jpeg, 88);
            return encoded is null || encoded.Size == 0 ? null : encoded.ToArray();
        }
        catch
        {
            return null;   // Skia could not draw; the surface uses its flat colour instead
        }
    }

    /// <summary>The diagonal band of light. It has its own soft gradient, not a blob, because a shaft has a
    /// direction and a blob does not.</summary>
    private static SKShader LightShaft(Roll random, int width, int height, SKColor colour)
    {
        var from = new SKPoint(random.Between(0.1f, 0.9f) * width, 0);
        var to = new SKPoint(random.Between(0.1f, 0.9f) * width, height);

        return SKShader.CreateLinearGradient(
            from, to,
            [colour.WithAlpha(0), colour.WithAlpha(ShaftAlpha), colour.WithAlpha(0)],
            [0f, random.Between(0.3f, 0.7f), 1f],
            SKShaderTileMode.Clamp);
    }

    /// <summary>The colours the figure is drawn in. These are the colours read from the record,
    /// snapped into the app's palette. A neighbour from the palette's own hue ring is added when the
    /// record gives only one colour.
    ///
    /// <para>Snapping is done for the same reason as for every other artist colour in this app. Two
    /// thousand unsnapped covers on a wall are two thousand colours and no palette. The neighbour stops
    /// a single-colour sleeve from giving a picture of one flat colour. The ring is ordered by hue, so
    /// an entry next to another is a close colour, and the pair always looks like a pair.</para>
    /// </summary>
    private static List<SKColor> Palette(IReadOnlyList<Color> colours, string artist)
    {
        var ring = RpmPalette.Colours;
        var seed = Seed(artist);

        var ink = new List<SKColor>(4);
        foreach (var colour in colours.Take(3))
            ink.Add(Ink(CoverColor.Dim(RpmPalette.Nearest(colour), Lit)));

        if (ring.Count > 0)
        {
            var primary = RpmPalette.Nearest(colours[0]);
            var at = ring.ToList().IndexOf(primary);
            if (at >= 0)
                ink.Add(Ink(CoverColor.Dim(
                    ring[(at + ((seed & 1) == 0 ? 1 : ring.Count - 1)) % ring.Count], Lit)));
        }

        return ink;
    }

    private static SKColor Ink(Color colour) => new(colour.R, colour.G, colour.B, 0xFF);

    private static SKColor Darkest(List<SKColor> ink)
        => ink.OrderBy(Brightness).First().WithAlpha(0xFF);

    private static SKColor Lightest(List<SKColor> ink)
        => ink.OrderByDescending(Brightness).First();

    private static double Brightness(SKColor colour)
        => (0.2126 * colour.Red) + (0.7152 * colour.Green) + (0.0722 * colour.Blue);

    /// <summary>How many soft forms are laid over each other. There are enough that the colours meet in
    /// several places, and few enough that they do not average out into one flat tone.</summary>
    private const int Blobs = 6;

    /// <summary>How hard the whole picture is blurred, as a fraction of its long edge. This is a hard
    /// blur. The picture must have no edge anywhere, and a blur that leaves the blobs recognisable as
    /// circles has not done the job.</summary>
    private const float BlurDivisor = 7f;

    /// <summary>The blur for the figures on the artists wall and the artist page. This is thirty per
    /// cent less blur than <see cref="BlurDivisor"/>, which is a larger divisor by the same amount. A
    /// box on the wall is about an inch wide and has a name written across it. At that size the full
    /// blur makes the picture flat, and what is left is one averaged tone. With less blur, the place
    /// where two of the record's colours meet is still visible in the box, and there is still no edge
    /// anywhere in it.</summary>
    private const float ArtistBlurDivisor = BlurDivisor / 0.7f;

    /// <summary>The same idea for a picture that is the whole of what is being looked at, and not a
    /// ground under something else. This is the generated playlist covers. Half the blur, which is
    /// twice the divisor. See
    /// <see cref="Metaballs"/>, where the reason is given.</summary>
    private const float HalfBlurDivisor = BlurDivisor / 0.5f;

    /// <summary>How much of the record's colour survives into the figure. It is held back, because the
    /// surfaces that draw the figure lay their own colour and their own black over the top. A picture
    /// drawn at full strength comes out brighter under those layers than a photograph does.</summary>
    private const double Lit = 0.72;

    /// <summary>How bright the shaft is at its middle. A suggestion, not a beam.</summary>
    private const byte ShaftAlpha = 0x5A;

    /// <summary>A name as a number: FNV-1a over the letters, folded for case.
    ///
    /// <para>It is written out here, not taken from <see cref="string.GetHashCode()"/>. That method is
    /// seeded per PROCESS, so the same artist would get a different picture each time the app started.
    /// The arrangement is drawn from the name so that it is the same picture every time.</para>
    ///
    /// <para>This is internal, not private. The playlist cover picker rolls its twelve generated tiles
    /// from a playlist's name in the same way that this rolls a figure from an artist's name. The
    /// number must be the SAME. A picker with its own roll would offer twelve tiles that never include
    /// the one that
    /// <see cref="PlaylistArt.Figure"/> draws for the list's own name.</para>
    /// </summary>
    internal static uint Seed(string name)
    {
        var hash = 2166136261u;
        foreach (var c in name)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= 16777619u;
        }
        return hash;
    }

    /// <summary>The figure for a RECORD with no cover. It is square, and its colours come from the
    /// app's palette.
    ///
    /// <para><b>The colours cannot come off the sleeve, because there is no sleeve.</b> This is the
    /// main difference from the artist's figure. An artist borrows colours from a record that the
    /// artist made. A record with no cover has nothing to borrow from. So the palette is asked instead.
    /// WHICH entries it gives comes from the record's identity, which is its folder. The folder is the
    /// one name the record has that does not change when it is retagged.</para>
    ///
    /// <para>Three entries are taken, walking the palette's hue ring from the point where the name
    /// lands. Neighbours on that ring always look like a set. So every made sleeve is a different
    /// picture, and the shelf they sit on still uses one palette.</para>
    /// </summary>
    public static byte[]? Sleeve(string identity)
        // Its own variant, so a record and the artist who made it never come out as the same
        // arrangement even where the palette hands them the same colours.
        => RingColours(identity) is { Count: > 0 } colours ? Tile(colours, identity) : null;

    /// <summary>Three neighbours on the palette's hue ring, chosen by where a name lands on it. This is
    /// in its own method because <see cref="Metaballs"/> asks the same question for the same reason.
    /// Something with no colour of its own needs colours from somewhere else. Two copies of this walk
    /// would give two answers for the same name.
    /// <see cref="PlaylistArt.Figure"/> asks it too, so a generated cover and a record with no sleeve
    /// come from one palette. Empty where the palette has not loaded.</summary>
    internal static List<Color> RingColours(string identity)
    {
        var ring = RpmPalette.Colours;
        if (ring.Count == 0)
            return [];

        var at = (int)(Seed(identity) % (uint)ring.Count);
        return
        [
            ring[at],
            ring[(at + 1) % ring.Count],
            ring[(at + ring.Count - 1) % ring.Count],
        ];
    }

    /// <summary>The figure for a SQUARE, in colours the caller already has.
    ///
    /// <para>This is what a station's logo is set on. A mark published as a transparent picture has no
    /// ground of its own. The app's dark colour behind it is a hole, not a background. So the mark gets
    /// the same made picture that an unphotographed artist gets, in the mark's own colours. It then
    /// reads as a badge.</para>
    /// </summary>
    /// <param name="identity">What the arrangement is drawn from. The same subject gives the same
    /// picture every time, on every machine. For a station, this is its name.</param>
    public static byte[]? Tile(IReadOnlyList<Color> colours, string identity)
        => Paint(colours, identity, 320, 320, variant: 2, blurDivisor: BlurDivisor);

    /// <summary>A small deterministic random roll, using xorshift32. It is not for anything that must
    /// be unguessable. It lays out one picture the same way each time, from one number.</summary>
    private struct Roll(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9 : seed;

        public float Between(float low, float high)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return low + ((high - low) * (_state / (float)uint.MaxValue));
        }
    }
}

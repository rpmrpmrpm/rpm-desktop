using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Common;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Foundation;
using Windows.UI;

namespace RPM.Desktop.Presentation.Main.Genres;

/// <summary>Which edge a name's lines are set against, when the name has more than one line. A card sets
/// its name flush to the outer edge of the wall where it stands. The genre's own page sets it flush left,
/// as a card in the left column does. A single-line name is the same drawing either way.</summary>
public enum GenreFlush
{
    Left,
    Right,
}

/// <summary>
/// How a genre's card shows its colour. There are two finishes, and each one is a way of showing glass.
///
/// <para><b>Glass</b> is used for shelves that sound modern: pop, club and machine music. The picture stays
/// sharp, and the colour is a tint over it. A light catches along the top edge, as it does on a pane of
/// glass. <b>Frost</b> is used for older music, and for music made with wood and air. The same colour is
/// laid on more heavily, under a milky haze, so the picture looks as if it is behind something rather than
/// under it.</para>
///
/// <para>Both are painted, not blurred. The app's real <see cref="GlassBrush"/> samples what is behind it
/// and blurs it again on every frame. That suits one pane over a moving scene. It is the wrong choice for a
/// wall of many cards that is scrolled. See the note on render cost in <c>GlassBrush</c>. This wall needs
/// the LOOK of the two materials, and a gradient gives that for the cost of a rectangle.</para>
/// </summary>
public enum GenreFinish
{
    Glass,
    Frost,
}

/// <summary>
/// The colour a genre card is drawn in, and the material it is drawn as.
///
/// <para>This is one type, not two arguments. Every layer on a card needs the same colour: the ground, the
/// fall across it, the fall down it, the finish, the edge, and the ink of the name. If the colour were
/// passed separately, one surface could take four of the six from one colour and two from another. That
/// would compile, and it would look like a renderer bug.</para>
///
/// <para>The finish is always the genre's own. Glass or frost says what kind of music this is (see
/// <see cref="GenreFinish"/>), and no photograph has a say in it. Only the colour has two possible answers.
/// The two factory methods below decide which one is used.</para>
/// </summary>
/// <param name="Colour">The colour every layer is worked out from.</param>
/// <param name="Finish">The material, which is the genre's own either way.</param>
public readonly record struct GenreAccent(Color Colour, GenreFinish Finish)
{
    /// <summary>The genre's own colour, from the generated table. A genre's page uses it, and a card uses
    /// it until a photograph has been read.</summary>
    public static GenreAccent Of(GenreWordmark mark) => new(GenreWordmarks.ColourOf(mark), mark.Finish);

    /// <summary>
    /// The colour taken from the photograph instead: its dominant tone, snapped to the app's palette. The
    /// artists wall does the same with a press shot (see <c>ArtistWallModels.Wear</c>).
    ///
    /// <para>The tone is snapped rather than used as read. A photograph's dominant tone is different for
    /// every picture, so a wall of raw tones would have no palette. Snapped, the wall uses the app's own
    /// ten colours, and each card takes the colour of the picture on it.</para>
    ///
    /// <para>A picture with no colour in it, such as a monochrome press shot, snaps to nothing. It comes
    /// back as the tone it was given, which is what the artists wall does too.</para>
    /// </summary>
    public static GenreAccent? From(GenreWordmark mark, Color picture)
    {
        if (Pinned(mark.Genre) is { } pinned)
            return new(pinned, mark.Finish);

        // The result must land on one of the ten colours. A picture with no colour in it snaps to nothing.
        // Examples are a white wall, a black stage and an overcast sky, which between them cover many press
        // shots. Then RpmPalette.Nearest returns the tone it was given: a white card or a grey one. That is
        // right for an artist's box, where the colour is a veil over a drained photograph. Here the colour
        // IS the card, and a wall of white tiles is what it would look like.
        //
        // So a picture that cannot say what colour it is does not get to dress the card. The shelf keeps
        // its own colour.
        var snapped = RpmPalette.Nearest(picture);
        return RpmPalette.Colours.Contains(snapped)
            ? new GenreAccent(Substitute(snapped), mark.Finish)
            : null;
    }

    /// <summary>
    /// The shelves whose colour is fixed, whatever their photograph shows.
    ///
    /// <para>There are three so far. The photograph kept getting each of these shelves wrong. Country's
    /// acts are shot in browns, and the brown is right there. Latin's came back in the same brown, when the
    /// shelf needs red. Techno's need pink. A fixed colour is the whole answer for a shelf like that. It is
    /// one line, and the card stops arguing with the picture.</para>
    ///
    /// <para>Both are palette colours, so a pinned card is built exactly like a snapped one. Its tones come
    /// from the asset's own gradient for that accent.</para>
    /// </summary>
    private static Color? Pinned(string genre) => genre switch
    {
        "Country" => Brown,
        "Latin" => Red,
        "Techno" => Pink,
        _ => null,
    };

    /// <summary>The palette's <c>RpmRedColor</c>.</summary>
    private static readonly Color Red = Color.FromArgb(0xFF, 0xEE, 0x07, 0x09);

    /// <summary>
    /// The one accent the wall does not draw. Brown is replaced by a deep purple.
    ///
    /// <para>Press shots are lit warm and shot against wood, brick and sepia. A great many of them snap to
    /// Rust. On a card, Rust is the colour that made the wall look flat and dull. Purple is a colour a
    /// photograph rarely produces, and it never lands there by accident. So it reads as a choice, not as
    /// the wall failing to find a colour.</para>
    ///
    /// <para>This applies only here. The purple is not in <c>Assets/RpmPalette.xaml</c>, and it is not
    /// added to it. The palette is the app's ten accents. The artists wall, the drawers and everything else
    /// that snaps to the palette still draw brown pictures brown.</para>
    /// </summary>
    private static Color Substitute(Color snapped) => snapped == Brown ? Purple : snapped;

    /// <summary>The palette's brown: <c>RpmRustColor</c>, which is what a warm-lit photograph snaps
    /// to.</summary>
    private static readonly Color Brown = Color.FromArgb(0xFF, 0xCC, 0x6E, 0x11);

    /// <summary>The colour a card draws in place of brown.</summary>
    private static readonly Color Purple = Color.FromArgb(0xFF, 0x6A, 0x2C, 0xA5);

    /// <summary>The palette's <c>RpmMagentaColor</c>, which is its pink.</summary>
    private static readonly Color Pink = Color.FromArgb(0xFF, 0xAC, 0x52, 0x8D);

    /// <summary>
    /// The three tones this colour is drawn at.
    ///
    /// <para>For any of the ten colours, these are the palette's own gradient (see
    /// <see cref="GenreWordmarks.Ground"/> for why they are read and never computed). The purple is not in
    /// the asset, so the palette's own dimming rule is applied to it: 0.62, 0.51 and 0.40 of the tint. That
    /// is the arithmetic written at the top of <c>RpmPalette.xaml</c>, and it produced every gradient in
    /// that file. The result is null where the palette cannot be read at all. The layers below handle that
    /// case themselves.</para>
    /// </summary>
    public IReadOnlyList<Color>? Tones => Colour == Purple ? PurpleTones : RpmPalette.RampFor(Colour);

    private static readonly IReadOnlyList<Color> PurpleTones =
    [
        GenreWordmarks.Deepen(Purple, 0.62),
        GenreWordmarks.Deepen(Purple, 0.51),
        GenreWordmarks.Deepen(Purple, 0.40),
    ];
}

/// <summary>
/// The genres' lettering: one shelf's name, set in a face chosen for that shelf, as a drawing.
///
/// <para><b>Why a drawing and not a font.</b> Thirty-five faces are used for the names. Anton is used for
/// Rock, a blackletter for Religious, and an eight-by-eight bitmap face for Video Game. An app that shipped
/// all of them would carry several megabytes just to write a few dozen words. It would load them at
/// startup, and it would need a fallback for each one that failed. So the words are set at BUILD time (see
/// <c>scripts/gen_wordmarks.py</c>). The app carries only the outline of that setting. Nothing has to be
/// loaded, and nothing needs a fallback. A name is the same drawing at forty points on the wall and at two
/// hundred on the genre's own page.</para>
///
/// <para>The table it reads is generated, and it is never edited by hand: <c>GenreWordmarks.Data.cs</c>.
/// The colours are in it too. A shelf's face and the fall over its photograph are one decision, and two
/// files would let them drift apart.</para>
/// </summary>
public static partial class GenreWordmarks
{
    /// <summary>The wordmark for a shelf. It returns nothing for a name the table does not know. That only
    /// happens for a genre added to <c>subgenres.json</c> without running the generator, because the
    /// generator refuses to write a table with a shelf missing from it.</summary>
    public static GenreWordmark? For(string genre) => ByGenre.GetValueOrDefault(genre);

    /// <summary>The table, looked up by name. It is built on the first question, not before.
    ///
    /// <para>It is lazy because the table it is built FROM is in the other half of this class. Static
    /// fields are initialised in the order they are written. Across two files of one partial class, the
    /// compiler chooses that order. So an eager field here could read <c>Cut</c> before the generated file
    /// has filled it, and the whole class would fail to initialise. Asked for from inside a method, the
    /// class is already set up and the array is already there.</para></summary>
    private static Dictionary<string, GenreWordmark> ByGenre =>
        _byGenre ??= Cut.ToDictionary(mark => mark.Genre, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, GenreWordmark>? _byGenre;

    /// <summary>Every shelf's lettering. The tests use it to measure what the whole table costs, not what
    /// one name costs.</summary>
    internal static IReadOnlyList<GenreWordmark> Everything => Cut;

    /// <summary>
    /// The name that runs furthest for the size its letters are set at: the greatest width in the table per
    /// unit of <see cref="GenreWordmark.Base"/>.
    ///
    /// <para>This is the wall's one decision about size. Every name is set so that the top of its letters
    /// and the line they stand on are in the same two places on every card. The size used for all of them
    /// is the largest size at which even this name still fits inside a card.</para>
    ///
    /// <para>It is taken from the WHOLE table, not from the genres a collection happens to hold. That keeps
    /// the lettering the same size on every wall. Otherwise a shelf with one more genre than yours would
    /// resize every name on the screen.</para>
    /// </summary>
    public static double WidestName => _widest ??= Cut.Max(mark => mark.Aspect / mark.Base);

    private static double? _widest;

    /// <summary>
    /// One shelf's lettering, drawn once at the size it will be shown at, as a picture.
    ///
    /// <para><b>A picture, not the outline itself. This is the whole reason the wall scrolls.</b> The
    /// renderer charges for a shape's own outline once for every frame that shape moves. For each frame it
    /// strokes the outline to allow for antialiasing, unions the stroke back over it and intersects the
    /// result with the clip. It does all of this in path arithmetic, to work out which part of the screen
    /// the shape has changed. A rectangle is four commands. Punk, for example, has three hundred contours
    /// and four thousand commands. On a shared library the wall can show twenty-seven of those at once.
    /// Redrawing that outline on every frame held the UI thread for about a second per flick of the wheel.
    /// Drawn into a bitmap, a name is a RECTANGLE as far as that arithmetic is concerned, so the per-frame
    /// cost goes away.</para>
    ///
    /// <para>The cost moves to one raster per name per size. It happens when a card is first drawn, and
    /// again if the wall is resized or the window moves to another screen. Each raster takes a few hundred
    /// microseconds. The letters are the same letters. The outlines are drawn at the pixel size they will
    /// be seen at, so no picture is scaled anywhere and nothing is softened.</para>
    /// </summary>
    /// <param name="flush">Which edge the lines are set against: the outer edge of the wall for a card, or
    /// the left for the genre's own page. Every single-line name ignores this, because the two settings are
    /// one drawing.</param>
    /// <param name="width">The width in PHYSICAL pixels. That is the box in points times the screen's
    /// scale. It is not in points: a name rastered at points and shown on a high-density screen would be at
    /// half resolution.</param>
    /// <param name="height">The height in physical pixels. This method does not read the wordmark's
    /// proportions. The caller has already given the box those (see <c>GenreTile.NameBoxWidth</c>). The
    /// drawing is stretched to fill exactly what it is given, the way <c>Stretch="Fill"</c> did.</param>
    public static ImageSource? Lettering(GenreWordmark mark, GenreFlush flush, int width, int height)
        => Lettering(mark, GenreAccent.Of(mark), flush, width, height);

    /// <summary>The same name, set in the ink that the card's own colour asks for. On the wall, that colour
    /// comes from the photograph rather than from the table (see <see cref="GenreAccent"/>). The drawing is
    /// otherwise the same. A card that changes colour must draw its name again, because the ink is baked
    /// into the bitmap.</summary>
    public static ImageSource? Lettering(
        GenreWordmark mark, GenreAccent accent, GenreFlush flush, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;

        using var outline = Outline(mark.On(flush));

        // The FILL bounds, not the control points. The handles of a cubic curve sit outside the ink it
        // draws. A box measured from them would set every name a little small and off centre. The error
        // differs for each name, because it depends on how far that face's handles reach.
        var ink = outline.TightBounds;
        if (ink.Width <= 0 || ink.Height <= 0)
            return null;

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[info.BytesSize];

        using (var surface = SKSurface.Create(info))
        {
            if (surface is null)
                return null;

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.SetMatrix(SKMatrix.CreateScaleTranslation(
                width / ink.Width, height / ink.Height,
                -ink.Left * width / ink.Width, -ink.Top * height / ink.Height));

            Letter(surface.Canvas, outline, ink, InkOn(accent), height);

            // The pixels are read straight out of the surface into our own array. The array is pinned, so
            // it is not copied twice. A raster is about a megabyte on a large card, and this runs on the
            // thread that draws.
            var held = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                if (!surface.ReadPixels(info, held.AddrOfPinnedObject(), info.RowBytes, 0, 0))
                    return null;
            }
            finally
            {
                held.Free();
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
            buffer.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// The letters themselves, with the one thing that stops them looking like a stencil.
    ///
    /// <para><b>Inside the glyph and nowhere else.</b> The drawing is stretched to fill exactly the box the
    /// card gives it. That is what puts every name's letters at the same height on every card. So the ink
    /// bounds ARE the edges of the picture. A cast shadow or a glow under the letters would be drawn
    /// outside those bounds and clipped off. What looks like a soft shadow in a mockup would come out as a
    /// hard line along the bottom of the box. So the depth is placed where there is room for it: in the
    /// ink.</para>
    ///
    /// <para><b>A fall down the letters, and a crown on top of it.</b> The fall is light from above. The
    /// ink is a little lifted where the letters begin, and a little deepened at the line they stand on.
    /// That is what a solid object looks like, and a stencil does not. The crown is the same letters drawn
    /// a little higher, and clipped BACK to themselves. What survives is a thin lit edge along every top
    /// surface. There are two passes. Both are inside the outline, and neither can reach the edge of the
    /// picture.</para>
    ///
    /// <para>This is paid once per name per size, like the raster it is part of (see
    /// <see cref="Lettering"/>).</para>
    /// </summary>
    /// <param name="ink">The outline's own tight bounds. The fall is measured down these. The canvas is
    /// already scaled so that these bounds fill the picture. So a gradient stated in them is a gradient
    /// down the letters, however the box is shaped.</param>
    /// <param name="tall">The picture's height in PIXELS. The crown's lift is a fraction of it. It is
    /// stated in device pixels, not in the outline's own units, because the two axes are scaled by
    /// different amounts. A lift stated in path units would differ between a wide name and a narrow
    /// one.</param>
    private static void Letter(SKCanvas canvas, SKPath outline, SKRect ink, Color colour, int tall)
    {
        var (crown, body, foot) = Shading(colour);

        using var fall = SKShader.CreateLinearGradient(
            new SKPoint(0, ink.Top),
            new SKPoint(0, ink.Bottom),
            [Skia(crown), Skia(body), Skia(foot)],
            [0f, CrownEnds, 1f],
            SKShaderTileMode.Clamp);

        using var brush = new SKPaint { Shader = fall, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(outline, brush);

        // The lit edge. The letters are clipped first. Then the same letters are drawn a little higher, in
        // the crown colour. Everything that lands outside the glyph is thrown away. What is left is a
        // sliver along each top surface.
        //
        // The lift is at least one whole pixel. A fortieth of a name set fourteen pixels tall rounds to
        // nothing, and that is so on every card of a screen that is not doubled. The edge is the half of
        // this that can be seen at small sizes, where a fall of a few levels down a letter a dozen pixels
        // tall is invisible.
        var lift = Math.Max(1f, (float)Math.Round(tall * CrownLift));

        canvas.Save();
        canvas.ClipPath(outline, SKClipOperation.Intersect, antialias: true);
        canvas.Translate(0, -lift / (float)canvas.TotalMatrix.ScaleY);
        using var edge = new SKPaint
        {
            Color = Skia(crown).WithAlpha(CrownAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawPath(outline, edge);
        canvas.Restore();
    }

    /// <summary>
    /// The three tones a name is lettered in: the lit top, the letter's own colour, and the foot.
    ///
    /// <para>Light ink is lifted TOWARDS the ink's own extreme, not towards white. White ink is lifted by
    /// taking the deepening off the foot and leaving the top as it is, because there is nowhere above white
    /// to go. Dark ink on a bright shelf is lifted towards white, because there is room there. This is a
    /// separate function, so the two ends can be checked against numbers. See the tests.</para>
    /// </summary>
    internal static (Color Crown, Color Body, Color Foot) Shading(Color ink)
        => Luminance(ink) > Halfway
            ? (ink, Deepen(ink, BodyOfLight), Deepen(ink, FootOfLight))
            : (Lift(ink, CrownOfDark), ink, Deepen(ink, FootOfDark));

    /// <summary>The same colour with light put INTO it. This is a blend towards white, not a multiply.
    /// <see cref="Deepen"/> does the opposite. A dark ink multiplied upwards would only ever reach
    /// itself.</summary>
    private static Color Lift(Color colour, double by) => Color.FromArgb(
        colour.A,
        (byte)(colour.R + ((255 - colour.R) * by)),
        (byte)(colour.G + ((255 - colour.G) * by)),
        (byte)(colour.B + ((255 - colour.B) * by)));

    private static SKColor Skia(Color colour) => new(colour.R, colour.G, colour.B, colour.A);

    /// <summary>Where the crown ends and the fall to the foot begins, down the letters. It is early,
    /// because the lit part of a solid object is the part that faces the light, not half of it.</summary>
    private const float CrownEnds = 0.34f;

    /// <summary>How light ink is shaded through the body of a letter, and at its foot. The numbers are
    /// small on purpose. This is a shelf's name, not a chrome bevel. Once the foot of a letter has a
    /// different colour from its top, the effect is a gradient that someone applied, not depth.</summary>
    private const double BodyOfLight = 0.94;
    private const double FootOfLight = 0.82;

    /// <summary>The same for dark ink, which has room above it rather than below. Dark ink is lifted more
    /// at the crown and deepened less at the foot. A dark letter on a bright shelf loses its edges sooner
    /// than a light one does.</summary>
    private const double CrownOfDark = 0.22;
    private const double FootOfDark = 0.88;

    /// <summary>Where light ink and dark ink are split. It uses the straightened luminance that the rest of
    /// this file uses (see <see cref="Luminance(Color)"/>), at its midpoint.</summary>
    private const double Halfway = 0.5;

    /// <summary>How far the lit edge is lifted, as a fraction of the picture's height. About a fortieth.
    /// That is a pixel or two on a card. It is an edge, not a second copy of the letters.</summary>
    private const double CrownLift = 0.025;

    /// <summary>How much of the crown colour the lit edge carries. Under half, so it reads as light on the
    /// ink, not as an outline drawn round it.</summary>
    private const byte CrownAlpha = 0x66;

    /// <summary>The generated data as a path Skia can draw. The tests measure the reader
    /// (<see cref="Read"/>). This method hands the four commands that the reader returns to Skia, and does
    /// nothing else.</summary>
    private static SKPath Outline(string data)
    {
        // Letterforms are wound for the non-zero fill rule, and script faces cross their own strokes. Under
        // even-odd, which is Skia's default and also XAML's, every crossing would cut a hole through the
        // letter. So the fill is set to winding.
        var path = new SKPath { FillType = SKPathFillType.Winding };

        foreach (var contour in Read(data))
        {
            path.MoveTo((float)contour.Start.X, (float)contour.Start.Y);

            foreach (var step in contour.Steps)
            {
                if (step.Curve)
                    path.CubicTo(
                        (float)step.One.X, (float)step.One.Y,
                        (float)step.Two.X, (float)step.Two.Y,
                        (float)step.End.X, (float)step.End.Y);
                else
                    path.LineTo((float)step.End.X, (float)step.End.Y);
            }

            if (contour.Closed)
                path.Close();
        }

        return path;
    }

    /// <summary>
    /// The colour to write a shelf's name in. It is worked out from that shelf's own colour.
    ///
    /// <para>The name stands on the fall at full strength (see the wall's template). So this is a
    /// comparison of one colour with another, and it has an arithmetic answer.</para>
    ///
    /// <para><b>White wherever white can be read, not only where white wins.</b> On contrast alone, dark
    /// letters beat white on well over half of this palette: hot pink, orange and green. A wall written
    /// half in dark and half in white would look like two apps. So white is the answer, unless it falls
    /// below what a large piece of lettering needs. That happens on the pale shelves: the lemon of House,
    /// the parchment of Classical, and the arcade lime of Video Game. Dark ink on those is what a record
    /// sleeve in those colours would use anyway.</para>
    /// </summary>
    /// <remarks>The comparison is made against <see cref="Lit"/>, not against the shelf's bare tint. The
    /// letters are written on the lightest tone of the ground, with the fall over it. Both come from the
    /// palette's own ramp. Tested against the bare tint, the answer would be for a colour that is nowhere
    /// on the card. For example, House's bare lime is pale enough for dark letters, but the tone it is
    /// actually drawn at is not.</remarks>
    public static Color InkOn(GenreWordmark mark) => InkOn(GenreAccent.Of(mark));

    /// <summary>The same question, asked of the colour the card is actually drawn in. On the wall that is
    /// not always the genre's own colour (see <see cref="GenreAccent.From"/>). A card that takes its colour
    /// from a photograph can land on Gold where the table says Indigo. A name set in white on that card
    /// would be unreadable for as long as that picture is shown.</summary>
    public static Color InkOn(GenreAccent accent)
        => Contrast(Paper, Lit(accent)) >= Readable ? Paper : Ink;

    /// <summary>The contrast a name needs against its own colour. It is the large-text figure, not the
    /// body-text one. These are large letters, and the smallest one drawn on the wall is about forty
    /// points.</summary>
    private const double Readable = 3.0;

    private static readonly Color Paper = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Ink = Color.FromArgb(0xFF, 0x14, 0x14, 0x18);

    /// <summary>How far apart two colours are to read, the WCAG way.</summary>
    private static double Contrast(Color a, Color b)
    {
        var one = Luminance(a);
        var two = Luminance(b);
        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
    }

    private static double Luminance(Color colour) => Luminance(
        ((uint)colour.R << 16) | ((uint)colour.G << 8) | colour.B);

    /// <summary>Relative luminance of a packed 0xAARRGGBB value, using the sRGB formula. The channels are
    /// straightened first. Without that step, the raw bytes would put House's lemon and Rap's gold on the
    /// same side of any threshold worth having.</summary>
    private static double Luminance(uint colour)
    {
        static double Straighten(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        var r = Straighten(((colour >> 16) & 0xFF) / 255.0);
        var g = Straighten(((colour >> 8) & 0xFF) / 255.0);
        var b = Straighten((colour & 0xFF) / 255.0);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>Converts the table's packed colour to a colour. This is the only place the packed form is
    /// unpacked.</summary>
    public static Color ColourOf(GenreWordmark mark) => Color.FromArgb(
        (byte)(mark.Colour >> 24), (byte)(mark.Colour >> 16), (byte)(mark.Colour >> 8), (byte)mark.Colour);

    // ── what a card wears ──

    /// <summary>
    /// The genre's colour under the photograph. Where no photograph arrives, it is the whole card. It is
    /// the palette's OWN gradient for this shelf's accent, laid across the card with its lightest tone
    /// where the name stands.
    ///
    /// <para>The head of the genre's page uses the same three tones, laid the same way round (see
    /// <see cref="Head"/>). A card and a head are the two places a shelf's colour is seen. A reader moving
    /// between them should see one colour.</para>
    ///
    /// <para><b>Read, never computed.</b> Other approaches invented their tones, and produced colours that
    /// appear nowhere in <c>Assets/RpmPalette.xaml</c>. The palette exists to prevent exactly that. The
    /// asset already ships a gradient beside every tint, and this is that gradient.</para>
    ///
    /// <para>The brush is kept, because a card's brush is put up and taken down as the wall scrolls. The
    /// whole wall can only ever use ten of these.</para>
    /// </summary>
    public static Brush Ground(GenreAccent accent)
        => accent.Tones is { Count: >= 2 } ramp
            ? Ramped(ramp, Lie.Left)
            : Kept(accent.Colour, Flat, Lie.Left);

    /// <summary>
    /// The genre's colour as a ROOM: the soft field of lobes that the playlist cards stand on, lit in this
    /// shelf's own tones.
    /// </summary>
    /// <remarks>
    /// <b>Why a room and not the gradient above.</b> The asset's ramp for a shelf runs from about six
    /// tenths of the tint to four tenths of it. For Vermilion that is #821E08 through #6B1807 to #541305.
    /// The range is about one and a half to one, and all of it is dark. Across a card a hand's breadth
    /// wide, it reads as a flat block. The card needs more variation in colour, like the coloured
    /// background of the playlist cards.
    ///
    /// <para><b>Built by hand, NOT through <see cref="RoomPalette.From"/>.</b> That method is written for
    /// covers, and a cover supplies colours it actually has. Given a shelf's three tones, it would find the
    /// tint and the glow to be the same hue, and it would turn one of them 42 degrees to make a second
    /// family. That produces a colour that appears nowhere in <c>Assets/RpmPalette.xaml</c>, which the
    /// palette exists to prevent. <see cref="Ground"/> explains why the tones are read, not computed. Every
    /// value below is read from the palette.</para>
    ///
    /// <para><b>Four slots, one hue, far enough apart in value to stay four.</b> The ground is the ramp's
    /// foot, so the dark parts of the card are a dark version of the shelf's colour, not black. The glow is
    /// the shelf's bare TINT, which is the light end the ramp never reaches. That gives about two and a
    /// half to one across the card, instead of one and a half. <see cref="RoomPalette.Blues"/> has the same
    /// shape for the setup wizard, and it is the precedent for a room in one hue.</para>
    ///
    /// <para>Kept per accent, like every other brush here. The wall can only ever use ten.</para>
    /// </remarks>
    public static RoomPalette Room(GenreAccent accent)
    {
        lock (Rooms)
        {
            if (Rooms.TryGetValue(accent, out var kept))
                return kept;

            return Rooms[accent] = Build(accent);
        }

        static RoomPalette Build(GenreAccent accent)
        {
            // NO COLOUR AT ALL. This is a shelf the wordmark table has never heard of, such as a genre from
            // a shared library, or one added without running the generator. Deepening this would deepen
            // black and return a black room. A black room is not a missing one. It is a card that draws as
            // a hole. So the app's own blue is used instead. RoomPalette keeps that blue for a screen that
            // has nothing to take a colour from.
            //
            // Nothing should reach this. A card with no mark is never Answered, and the wall does not show
            // a room until it is. The check is here because "never" is a claim about the caller, and this
            // is the answer if the claim stops being true.
            if (accent.Colour.A == 0 || (accent.Colour.R | accent.Colour.G | accent.Colour.B) == 0)
                return RoomPalette.Blues;

            // There is no ramp beside this tint. This happens when an asset was not read, or an entry was
            // re-curated without a gradient. The shelf's own colour is deepened instead. Every other
            // fallback here does the same.
            if (accent.Tones is not { Count: >= 3 } ramp)
            {
                var lit = accent.Colour;
                return new RoomPalette(
                    Vec(Deepen(lit, 0.40)), Vec(Deepen(lit, 0.62)), Vec(lit), Vec(Deepen(lit, 0.51)));
            }

            return new RoomPalette(
                Ground: Vec(ramp[^1]),      // the foot: the dark the lobes sit on
                Tint: Vec(ramp[0]),         // the ramp's own head, the mid of the three
                Glow: Vec(accent.Colour),   // the bare tint, and the light end the ramp has not got
                Shade: Vec(ramp[ramp.Count / 2]));
        }
    }

    private static readonly Dictionary<GenreAccent, RoomPalette> Rooms = [];

    /// <summary>A colour as the shader reads one: straight sRGB, zero to one. <c>CoverRoom</c> is given the
    /// same format.</summary>
    private static Vector3 Vec(Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>The asset's own tones, in order, at full strength. They are held like every other brush
    /// here. This one has its own builder, because the other brushes are ONE colour at varying alpha, and
    /// this one is three colours at full alpha. The difference is kept in the types, not in a
    /// comment.</summary>
    private static Brush Ramped(IReadOnlyList<Color> ramp, Lie lie)
    {
        var key = (ramp[0], ramp[^1], lie);
        lock (Ramps)
        {
            if (Ramps.TryGetValue(key, out var kept))
                return kept;

            var brush = new LinearGradientBrush
            {
                StartPoint = lie == Lie.Right ? new Point(1, 0) : new Point(0, 0),
                EndPoint = lie switch
                {
                    Lie.Right => new Point(0, 0),
                    Lie.Down => new Point(0, 1),
                    _ => new Point(1, 0),
                },
            };

            for (var at = 0; at < ramp.Count; at++)
                brush.GradientStops.Add(new GradientStop
                {
                    Offset = (double)at / (ramp.Count - 1),
                    Color = ramp[at],
                });

            return Ramps[key] = brush;
        }
    }

    private static readonly Dictionary<(Color, Color, Lie), Brush> Ramps = [];

    /// <summary>
    /// The colour running ACROSS the card. Its whole job is the name.
    ///
    /// <para>It is solid where the letters stand, and it is gone before the middle of the picture. So a
    /// wordmark has one colour under it, whatever the photograph is doing. The PICTURE is dressed by the
    /// fall down the card and by the finish over it. Doing all three jobs in this layer would make every
    /// card a block of colour with a faint photograph behind it.</para>
    ///
    /// <para>The first two stops are flat on purpose. A wordmark set large enough to cross the turn would
    /// otherwise be written in two colours.</para>
    /// </summary>
    public static Brush Fall(GenreAccent accent, bool flushRight)
        => Kept(Lit(accent), Across, flushRight ? Lie.Right : Lie.Left);

    /// <summary>
    /// The colour falling DOWN the card, at the weight this shelf's finish asks for.
    ///
    /// <para>Both weights come from <see cref="ArtistVeil"/>, and the finish chooses between them. That
    /// choice is the whole difference between the two materials. A frosted shelf uses the artists wall's
    /// BOX fall, with most of the colour gathered at the foot. It is meant to read as something laid over
    /// the picture. A glass shelf uses the PAGE fall, which is about a third of that. It is a breath of
    /// colour at the head and a cast at the foot, and the photograph stays a photograph.</para>
    /// </summary>
    public static Brush Drop(GenreWordmark mark) => Drop(GenreAccent.Of(mark));

    /// <summary>The same fall in the colour the card is drawn in. See <see cref="GenreAccent"/>.</summary>
    public static Brush Drop(GenreAccent accent)
        => Kept(Deep(accent), accent.Finish == GenreFinish.Glass ? UnderGlass : UnderFrost, Lie.Down);

    /// <summary>
    /// The finish itself: white, and the one layer that is not the genre's colour at all.
    ///
    /// <para>Frost is a haze: an even milkiness over the whole card, heavier at the head, where the light
    /// would sit on it. Glass is a rim: light caught along the top edge, which is gone within the first
    /// third. That is what tells the eye that something is a pane of glass rather than a wash of
    /// colour.</para>
    /// </summary>
    public static Brush Finish(GenreAccent accent)
        => White(accent.Finish == GenreFinish.Glass ? GlassRim : FrostHaze);

    /// <summary>
    /// The same finish in a WARM white, for the head of a genre's page.
    ///
    /// <para>A card is one of many on a wall, so it uses the neutral white. Many warm hazes over ten
    /// colours would give a wall with a colour cast. A page shows one genre, and its head is a heading
    /// rather than a tile. Laid on in cream instead of white, the finish reads as lit rather than printed.
    /// That is the difference between a heading someone wants to read under and a colour bar.</para>
    ///
    /// <para>It is not a colour the shelf wears. The palette is still the only place a shelf's own colour
    /// comes from. This is the light falling on it.</para>
    /// </summary>
    public static Brush Hearth(GenreWordmark mark) => Hearth(GenreAccent.Of(mark));

    /// <summary>The same finish over whatever the head is drawn in. See
    /// <see cref="GenreAccent"/>.</summary>
    public static Brush Hearth(GenreAccent accent)
        => Kept(Cream, accent.Finish == GenreFinish.Glass ? GlassRim : FrostHaze, Lie.Down);

    private static readonly Color Cream = Color.FromArgb(0xFF, 0xFF, 0xF1, 0xE0);

    /// <summary>
    /// The ground under the head of a genre's page: the palette's OWN gradient for this shelf's accent,
    /// laid across the band with its lightest tone at the name.
    ///
    /// <para><b>Read, never computed.</b> <c>Assets/RpmPalette.xaml</c> ships more than ten colours. Beside
    /// each <c>Rpm&lt;Name&gt;Color</c> is an <c>Rpm&lt;Name&gt;Gradient</c>: the drawer's own three tones
    /// over that tint. Those are as much part of the curated set as the tint is. Other approaches invented
    /// tones instead, and produced colours that appear nowhere in the palette, which is what the palette
    /// exists to stop. This reads the three tones the asset already draws, and lays them down in
    /// order.</para>
    ///
    /// <para><b>Turned on its side, lightest at the left.</b> The asset draws them as a vertical fall down
    /// a drawer. A head is a band four hundred points wide and a hand high, with a name at one end and a
    /// photograph at the other. A fall down a band that short cannot be seen. Across it, the name stands on
    /// the light end and the picture comes out of the dark one.</para>
    ///
    /// <para>The ramp finishes at the panel and holds its last tone from there on. That keeps the picture's
    /// leading edge invisible. The fall laid over the photograph starts on the same tone (see
    /// <see cref="Deep"/>). So the two meet at one value, instead of stepping from a dark ground to a lit
    /// band. A step would be a line down the page, exactly where the reader is looking.</para>
    /// </summary>
    /// <param name="turn">Where across the band the picture's panel begins, from 0 to 1.</param>
    public static Brush Head(GenreWordmark mark, double turn) => Head(GenreAccent.Of(mark), turn);

    /// <summary>The same band, in whatever colour the head is drawn in. On a genre opened from the wall,
    /// that is the colour the genre's CARD came out in. See <see cref="GenreAccent"/>.</summary>
    public static Brush Head(GenreAccent accent, double turn)
    {
        if (accent.Tones is not { Count: >= 2 } ramp)
            return Unpaired(accent, turn);

        var ends = Math.Clamp(turn, 0.05, 1);

        // Built fresh each time, not held. The turn moves with the pane. A brush held in a table would be
        // shared, and editing its stops later would change it under anyone else who asked for it.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var at = 0; at < ramp.Count; at++)
            brush.GradientStops.Add(new GradientStop
            {
                Offset = ends * at / (ramp.Count - 1),
                Color = ramp[at],
            });

        // The last tone is held from the panel on. Two layers have to agree on that exactly. See the note
        // above, and Deep, where the fall over the picture reads it.
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = ramp[^1] });
        return brush;
    }

    /// <summary>The band to use when the palette could not be read at all. That is when there is no asset,
    /// or an entry was re-curated without a gradient beside it. The shelf's own colour is shown deepened. A
    /// head with no ground would leave a hole at the top of the page, so there is always one.</summary>
    private static Brush Unpaired(GenreAccent accent, double turn)
    {
        var lit = accent.Colour;
        var deep = Deepen(lit, WithoutAPalette);
        var at = Math.Clamp(turn, 0.05, 1);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = lit });
        brush.GradientStops.Add(new GradientStop { Offset = at, Color = deep });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = deep });
        return brush;
    }

    /// <summary>How far the fallback band goes down, where the asset has no ramp to read. The asset's own
    /// first step is this same figure, so the fallback lands where the ramp would have started.</summary>
    private const double WithoutAPalette = 0.62;

    /// <summary>The tone the head has settled to by the time the photograph begins. It is the last tone of
    /// the palette's own gradient for this shelf. The fall over the photograph starts from it. It is
    /// defined in one place, because the two have to agree on it exactly. See <see cref="Head"/>.</summary>
    public static Color Deep(GenreWordmark mark) => Deep(GenreAccent.Of(mark));

    /// <summary>The same tone in the colour the card is drawn in. See <see cref="GenreAccent"/>.</summary>
    public static Color Deep(GenreAccent accent)
        => accent.Tones is { Count: >= 2 } ramp
            ? ramp[^1]
            : Deepen(accent.Colour, WithoutAPalette);

    /// <summary>
    /// The other end of the same ramp: the lightest tone the palette draws this shelf at. The name stands
    /// on it, and the fall over the picture's near edge is made of it.
    ///
    /// <para>This is NOT the shelf's bare tint. The bare tint is the undimmed colour. The palette hands it
    /// to code that dims it, such as the drawers and the artists wall. Used raw across a card, it is a chip
    /// of full strength on a tile a hand's breadth wide. Every tone on a card is one of the three the
    /// asset already draws for this accent. So a tile and the head of its page look at one colour.</para>
    /// </summary>
    public static Color Lit(GenreWordmark mark) => Lit(GenreAccent.Of(mark));

    /// <summary>The same tone in the colour the card is drawn in. See <see cref="GenreAccent"/>.</summary>
    public static Color Lit(GenreAccent accent)
        => accent.Tones is { Count: >= 2 } ramp
            ? ramp[0]
            : accent.Colour;

    /// <summary>The same colour with the light taken out of it. This is a multiply towards black, not a
    /// blend towards grey. That keeps a deepened vermilion looking like vermilion.</summary>
    internal static Color Deepen(Color colour, double by) => Color.FromArgb(
        colour.A, (byte)(colour.R * by), (byte)(colour.G * by), (byte)(colour.B * by));

    /// <summary>One colour running into another, held like the rest of them. It has its own builder,
    /// because every other brush on a card is ONE colour at varying strengths. This one is two colours at
    /// full strength. The difference is kept in the types, not in a comment.</summary>
    private static Brush Kept(Color from, Color to, Lie lie)
    {
        if (Runs.TryGetValue((from, to, lie), out var held))
            return held;

        var brush = new LinearGradientBrush
        {
            StartPoint = lie == Lie.Right ? new Point(1, 0) : new Point(0, 0),
            EndPoint = lie switch
            {
                Lie.Right => new Point(0, 0),
                Lie.Down => new Point(0, 1),
                _ => new Point(1, 0),
            },
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = from });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = to });

        return Runs[(from, to, lie)] = brush;
    }

    private static readonly Dictionary<(Color From, Color To, Lie Lie), Brush> Runs = [];

    /// <summary>The edge the card is drawn with. Glass takes a brighter one, because the edge is most of
    /// what makes a pane read as a pane. Frost's edge is barely there.</summary>
    public static Brush Rim(GenreAccent accent)
        => White(accent.Finish == GenreFinish.Glass ? [(0, 0x59), (1, 0x2E)] : [(0, 0x24), (1, 0x18)]);

    private static readonly (double At, byte Alpha)[] UnderGlass = [(0, 0x0D), (0.5, 0x2B), (1, 0x59)];
    private static readonly (double At, byte Alpha)[] UnderFrost = [(0, 0x2B), (0.5, 0x6E), (1, 0xAA)];

    /// <summary>A milky sheet, strongest at the head. It is nowhere near opaque. What is wanted is a
    /// picture seen through something. Past about this much, it is a picture that failed to load.</summary>
    private static readonly (double At, byte Alpha)[] FrostHaze =
        [(0, 0x2A), (0.55, 0x1C), (1, 0x12)];

    /// <summary>Light along the top edge, gone by a third of the way down.</summary>
    private static readonly (double At, byte Alpha)[] GlassRim =
        [(0, 0x3E), (0.12, 0x1A), (0.34, 0x00), (1, 0x00)];

    /// <summary>
    /// How much of the colour there is at the name, through the turn, and out over the photograph.
    ///
    /// <para><b>Smoothstepped, and every stop of it is on the curve.</b> A fall with corners in it draws
    /// lines where the slope changes. An eye finds the place where a slope CHANGES, not the place where it
    /// is steepest. The corners of a stepped fall read as two lines down a card that has none. (This is the
    /// same Mach band that the scrim behind the artwork band is built to avoid.)</para>
    ///
    /// <para>So the alpha follows <c>t²(3−2t)</c> from a fifth of the way across to four fifths. That
    /// leaves the name's ground with no slope on it, and arrives at nothing with none either. Where the
    /// fall STARTS is the other half of the job. The photograph begins at a fixed share of the card's
    /// width. So the colour has to still be solid there, or the picture's own leading edge stands as a
    /// line. A fall that eases through that point, however smoothly, leaves that line behind. The colour is
    /// solid past the picture's edge and lets go entirely over the picture, so there is no edge on the card
    /// at all. The last fifth is the photograph, unwashed, which is the point of the card.</para>
    /// </summary>
    private static readonly (double At, byte Alpha)[] Across =
    [
        (0, 0xFF), (0.167, 0xFF), (0.25, 0xFA), (0.333, 0xE1), (0.417, 0xB7), (0.5, 0x86),
        (0.583, 0x53), (0.667, 0x27), (0.75, 0x09), (0.833, 0x00), (1, 0x00),
    ];

    /// <summary>
    /// The colour over the left of a head whose picture runs the whole band.
    ///
    /// <para>A card's picture starts at a fixed share of the card's width. So <see cref="Across"/>, which
    /// is measured across the card, lines up with it at every size. A head has no such place. The
    /// photograph is the whole band. What this must cover instead is the NAME. The name is a fixed number
    /// of points wide (<c>GenreDetailView.NameEdge</c>) whatever the pane is doing.</para>
    ///
    /// <para><b>So it is built on each layout, not kept, and the caller passes where the solid part
    /// ends.</b> This is the difference between this and every other fall here. It is the whole reason the
    /// method takes an argument. The name needs SOLID colour under it at any width, and the fall needs
    /// whatever room is left over. A single curve laid across a scrim that is compressed on a narrow pane
    /// would compress the solid part along with it. The result would make a genre's name hard to read on
    /// top of a photograph.</para>
    ///
    /// <para>It reaches nothing WITHIN the band wherever there is room. That is the point of a head that
    /// bleeds. Past this, the photograph is a photograph. The ground's own ramp is built to finish at the
    /// same place (see <see cref="Head"/>). So a shelf with no photograph wears one continuous fall, rather
    /// than a ramp and then a step.</para>
    /// </summary>
    /// <param name="mark">The shelf. Its DEEPENED colour, not its lit one. See <see cref="Head"/>. The
    /// ground has finished deepening by the time this carries the colour on its own. A fall in the full
    /// colour would step back up to full brightness, and draw the line it is there to prevent.</param>
    /// <param name="solid">How far across the scrim the colour stays opaque, from 0 to 1. This is where the
    /// name ends.</param>
    public static Brush OverTheBand(GenreWordmark mark, double solid)
        => OverTheBand(GenreAccent.Of(mark), solid);

    /// <summary>The same scrim in whatever the head is drawn in. See <see cref="GenreAccent"/>.</summary>
    public static Brush OverTheBand(GenreAccent accent, double solid)
    {
        // Built fresh rather than held, for the reason Head gives. The shape of it moves with the pane. A
        // brush held in a table would be shared, and editing its stops later would change it under anyone
        // else who asked for it.
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var (offset, colour) in OverTheBandStops(accent, solid))
            brush.GradientStops.Add(new GradientStop { Offset = offset, Color = colour });
        return brush;
    }

    /// <summary>The stops of <see cref="OverTheBand"/>, from left to right. A caller that draws the same
    /// scrim into a bitmap uses these instead of a brush. The Pegasus genre page does this (see
    /// PageHeaderArt).</summary>
    public static IReadOnlyList<(double Offset, Color Colour)> OverTheBandStops(GenreAccent accent, double solid)
    {
        var ends = Math.Clamp(solid, 0.05, 0.95);
        var deep = Deep(accent);

        // Everything before the name's edge is at full strength. This is one stop rather than a run of
        // them. The curve below starts at 0xFF, so this stop meets it at the same value, and there is no
        // step.
        var stops = new List<(double, Color)>(LettingGo.Length + 1) { (0, deep) };
        foreach (var (at, alpha) in LettingGo)
            stops.Add((ends + (1 - ends) * at, Color.FromArgb(alpha, deep.R, deep.G, deep.B)));
        return stops;
    }

    /// <summary>
    /// How the colour lets go once it is past the name, over whatever room is left.
    ///
    /// <para><c>t²(3−2t)</c>, the same curve as <see cref="Across"/> and as the fall down the head's own
    /// foot. It is used for the same reason: a corner in a fall is a line drawn down the page. This is the
    /// tail alone, not a whole band, because the caller decides where it starts.</para>
    /// </summary>
    private static readonly (double At, byte Alpha)[] LettingGo =
    [
        (0, 0xFF), (0.125, 0xF3), (0.25, 0xCE), (0.375, 0x9B), (0.5, 0x80), (0.625, 0x50),
        (0.75, 0x1F), (0.875, 0x06), (1, 0x00),
    ];

    private static readonly (double At, byte Alpha)[] Flat = [(0, 0xFF), (1, 0xFF)];

    /// <summary>Which way a layer runs: out from the left edge, out from the right, or down.</summary>
    private enum Lie { Left, Right, Down }

    /// <summary>Kept rather than made for each card. The wall can only ever want ten colours in three
    /// directions and two finishes. A card is a brush that is put up and taken down as it is scrolled
    /// past.</summary>
    private static Brush Kept(GenreWordmark mark, (double At, byte Alpha)[] fall, Lie lie)
        => Kept(ColourOf(mark), fall, lie);

    private static Brush White((double At, byte Alpha)[] fall)
        => Kept(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), fall, Lie.Down);

    private static Brush Kept(Color colour, (double At, byte Alpha)[] fall, Lie lie)
    {
        var key = (colour, fall, lie);
        lock (Held)
        {
            if (Held.TryGetValue(key, out var kept))
                return kept;

            var brush = new LinearGradientBrush
            {
                // Right to left for a card whose name is on the right. So "the first stop is at the name"
                // is true for both sides, and the stops are written once.
                StartPoint = lie == Lie.Right ? new Point(1, 0) : new Point(0, 0),
                EndPoint = lie switch
                {
                    Lie.Right => new Point(0, 0),
                    Lie.Down => new Point(0, 1),
                    _ => new Point(1, 0),
                },
            };
            foreach (var (at, alpha) in fall)
                brush.GradientStops.Add(new GradientStop
                {
                    Offset = at,
                    Color = Color.FromArgb(alpha, colour.R, colour.G, colour.B),
                });

            Held[key] = brush;
            return brush;
        }
    }

    private static readonly Dictionary<(Color, (double, byte)[], Lie), Brush> Held = [];

    // ── reading the drawing ──

    /// <summary>One contour of a letter: where it starts, how it goes round, and whether it closes. Every
    /// contour of a letterform closes. A contour that did not close would mean the generator wrote
    /// something this reader was not written for.</summary>
    internal sealed record Contour(Point Start, IReadOnlyList<Stroke> Steps, bool Closed);

    /// <summary>One step along a contour: a line to a point, or a cubic through two handles to
    /// it.</summary>
    internal readonly record struct Stroke(bool Curve, Point One, Point Two, Point End);

    /// <summary>
    /// The generated path data as plain numbers.
    ///
    /// <para><b>It has its own reader, not the XAML one, because the data is its own dialect.</b> The
    /// generator writes four commands and nothing else: move, line, cubic and close. It writes whole-number
    /// coordinates. Both halves are written to that agreement. This keeps the reader simple, with no
    /// special cases: no relative commands, no shorthand curves, no arcs, and no implicit repeats. It also
    /// does not depend on a runtime type converter behaving the same way on every head the app runs on.
    /// Anything else in the string means the generator has changed and this reader has not. In that case a
    /// card with no name is better than a card with the wrong shape.</para>
    ///
    /// <para>The leading <c>F1</c> is read and discarded, along with the spaces. Non-zero is the only fill
    /// a letterform is ever wound for, so the fill rule is set where the geometry is built, not taken from
    /// the string.</para>
    /// </summary>
    internal static IReadOnlyList<Contour> Read(string data)
    {
        var contours = new List<Contour>();
        var steps = new List<Stroke>();
        var start = default(Point);
        var open = false;
        var at = 0;

        void Close(bool closed)
        {
            if (open)
                contours.Add(new Contour(start, steps.ToArray(), closed));
            steps.Clear();
            open = false;
        }

        while (at < data.Length)
        {
            switch (data[at])
            {
                case 'M':
                    at++;
                    Close(closed: false);
                    start = ReadPoint(data, ref at);
                    open = true;
                    break;

                case 'L':
                    at++;
                    steps.Add(new Stroke(Curve: false, default, default, ReadPoint(data, ref at)));
                    break;

                case 'C':
                    at++;
                    var one = ReadPoint(data, ref at);
                    var two = ReadPoint(data, ref at);
                    steps.Add(new Stroke(Curve: true, one, two, ReadPoint(data, ref at)));
                    break;

                case 'Z':
                    at++;
                    Close(closed: true);
                    break;

                default:
                    // The F1 that the generator writes at the start, and the spaces between the values.
                    at++;
                    break;
            }
        }

        Close(closed: false);
        return contours;
    }

    private static Point ReadPoint(string data, ref int at)
    {
        var x = ReadNumber(data, ref at);
        if (at < data.Length && data[at] == ',')
            at++;
        return new Point(x, ReadNumber(data, ref at));
    }

    private static double ReadNumber(string data, ref int at)
    {
        while (at < data.Length && data[at] == ' ')
            at++;

        var start = at;
        if (at < data.Length && data[at] == '-')
            at++;
        while (at < data.Length && (char.IsAsciiDigit(data[at]) || data[at] == '.'))
            at++;

        return at > start && double.TryParse(
            data.AsSpan(start, at - start), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }
}

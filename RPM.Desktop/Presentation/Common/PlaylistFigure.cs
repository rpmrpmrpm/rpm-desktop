using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The eight pictures a generated playlist cover can be, and the colours each one is drawn
/// in.
///
/// <para><b>All eight are soft.</b> Hard geometry, such as bars, rings, chevrons and a tonearm, looks
/// like a diagram at thumbnail size. It says "made by a program", which a cover should not say. Twenty
/// choices is also more than anybody wants to look through. So the whole set is light instead: a sun, a
/// sphere, a mesh of colour, a sweep, sheets of light on the lean, lights out of focus, a drop hitting
/// water, and a smear of spectrum. Nothing here has an edge, except where something soft ends.</para>
///
/// <para><b>Eight shapes, and the colours move.</b> The family says what KIND of light it is. The
/// <see cref="Colourway"/> says which colours, and it is rolled from the same seed as the arrangement.
/// So the picker shows eight tiles, not forty. Asking again gives the same eight shapes in a new set of
/// colours. That is the choice people actually make about a cover.</para>
///
/// <para><b>This file paints the whole square.</b> These pictures are their own ground. A second soft
/// form under them would only be a colour they have to fight. <see cref="PlaylistArt.Figure"/> clears
/// the canvas, clips to the rounded square, passes the box here, and encodes what comes back.</para>
///
/// <para><b>Nothing here reads the palette.</b> The colours arrive as a list. So this whole file is
/// Skia and arithmetic, and it can be rendered to a contact sheet and looked at outside the app. A
/// family can draw a blank tile without any error. Only looking at a rendered sheet of all eight
/// families shows that. A new family should be checked this way.</para>
/// </summary>
internal static class PlaylistFigure
{
    /// <summary>How many families there are. <see cref="PlaylistArt.Styles"/> is the public name for
    /// this. This number must agree with the switch in <see cref="Paint"/>.
    ///
    /// <para>A style number that comes from the database is wrapped into range rather than refused (see
    /// <see cref="Paint"/>). So a saved style of 13 draws family 5. In practice an old cover is not
    /// redrawn at all. A cover that somebody picked is written to the cover store as bytes at the
    /// moment it is chosen, and that file is read before anything here is asked. So nobody's cover
    /// changes.</para></summary>
    internal const int Families = 8;

    /// <summary>The colours one cover is drawn from: three neighbours on the palette's hue ring,
    /// sorted by how much light each one carries.
    ///
    /// <para><b>It is sorted, and that is the point of the type.</b> Every family is a picture of light
    /// falling on something. Light needs to know which colour is the bright one and which is the
    /// shadow. The ring is ordered by hue, and it says nothing about brightness. Three neighbours taken
    /// from it could come back as gold, lime and vermilion, in that order. A family that used them in
    /// arrival order would light a cover from its darkest colour. So the ring order stops at this
    /// point, and what goes past is <see cref="Hot"/>, <see cref="Mid"/> and
    /// <see cref="Deep"/>.</para>
    ///
    /// <para><b>Neighbours, not any three.</b> The ring is ordered by hue, so entries next to each
    /// other always look like a set. A record with no sleeve is drawn the same way. So a generated
    /// cover and a made sleeve on one shelf come from one palette.</para>
    /// </summary>
    /// <param name="Hot">The most luminous of the three: what the light in the picture is made
    /// of.</param>
    /// <param name="Mid">The middle one, which is most of what is actually seen.</param>
    /// <param name="Deep">The least luminous: the shadow, and what a dark ground is tinted
    /// with.</param>
    internal readonly record struct Colourway(SKColor Hot, SKColor Mid, SKColor Deep)
    {
        /// <summary>Three neighbours off the ring, chosen by the seed, in light order.
        /// </summary>
        /// <param name="palette">The app's own colours, in the ring's own hue order. An empty list
        /// gives null. There is no picture to make without colours, and a cover made of grey would
        /// belong to no palette.</param>
        /// <param name="seed">Which colours, and how far apart. The same seed gives the same colourway
        /// on every machine, and after every restart. That is why a cover can be stored as two
        /// numbers.</param>
        internal static Colourway? From(IReadOnlyList<SKColor> palette, uint seed)
        {
            if (palette.Count == 0)
                return null;

            // How far round the ring the other two colours sit. One step is the closest harmony. Two
            // steps is a visibly wider set. The span is rolled, so that not every generated cover uses
            // the tightest set.
            var span = 1 + (int)((seed >> 11) % 2u);
            var at = (int)(seed % (uint)palette.Count);

            var trio = new List<SKColor>(3)
            {
                palette[at],
                palette[(at + span) % palette.Count],
                palette[(((at - span) % palette.Count) + palette.Count) % palette.Count],
            };

            trio.Sort((a, b) => Light(a).CompareTo(Light(b)));
            return new Colourway(trio[2], trio[1], trio[0]);
        }

        /// <summary>The brightest thing in the picture: the hot colour taken most of the way to white.
        /// What a core, a rim and a catch of light are made of.</summary>
        internal SKColor Glow => Lift(Hot, 0.74f);

        /// <summary>A pale field to stand something on. It is the middle colour as a tint, not as a
        /// full colour. That makes a light cover read as paper, not as a wash.</summary>
        internal SKColor Paper => Lift(Mid, 0.90f);

        /// <summary>And the dark one: the deep colour taken nearly to black, but never TO it. A true
        /// black ground makes every colour on it look separate from the ground. A black that still has
        /// the playlist's hue makes them look lit.</summary>
        internal SKColor Night => Sink(Deep, 0.86f);
    }

    /// <summary>Paints one whole cover into <paramref name="box"/>.
    /// </summary>
    /// <param name="canvas">Already cleared and already clipped to the rounded square. Nothing here
    /// saves or restores it, past the layers it opens and closes itself.</param>
    /// <param name="box">The square, which is the whole cover. These families bleed to the edges,
    /// because a soft picture inset on a border looks like a photograph pasted onto a card.</param>
    /// <param name="palette">The app's colours, in ring order, see <see
    /// cref="Colourway.From"/>.</param>
    /// <param name="style">Which family. A number outside the range is wrapped rather than refused. The
    /// number has been through a database and a picker, and a playlist with no picture at all is a
    /// worse result than the first family.</param>
    /// <param name="seed">The roll that arranges this cover and also picks its colourway. The same seed
    /// gives the same cover every time.</param>
    /// <returns>False where the palette had nothing to give, and the caller draws no cover at
    /// all.</returns>
    internal static bool Paint(
        SKCanvas canvas, SKRect box, IReadOnlyList<SKColor> palette, int style, uint seed)
    {
        if (Colourway.From(palette, seed) is not { } ink)
            return false;

        var roll = new Roll(seed == 0 ? 0x9E3779B9u : seed);

        switch (((style % Families) + Families) % Families)
        {
            case 0: Halo(canvas, box, ink); break;
            case 1: Orb(canvas, box, ink); break;
            case 2: Mesh(canvas, box, ink, ref roll); break;
            case 3: Sweep(canvas, box, ink); break;
            case 4: Panels(canvas, box, ink); break;
            case 5: Bokeh(canvas, box, ink, ref roll); break;
            case 6: Ripple(canvas, box, ink); break;
            default: Prism(canvas, box, ink); break;
        }

        return true;
    }

    // ── the eight ────────────────────────────────────────────────────────────

    /// <summary>A sun: a near-white core that falls off through the whole colourway into a flat tinted
    /// field. It is the one family that is nearly symmetrical, and it has to be. What it shows is a
    /// source of light seen head on. A sun off its own axis looks like a mistake, not a
    /// composition.</summary>
    private static void Halo(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // Always two thirds of the way to white. If this were rolled across a range, the field would
        // have a different strength in every colourway. The field is most of what is on screen at tile
        // size, so the picture would seem to change, not just its colours.
        //
        // A trace of the DEEP colour is mixed in, and that is needed. Without it, two colourways in ten
        // share Hot and Mid, and differ only in their third colour. Those two came out as the same
        // cover, byte for byte. Tests caught this, but looking would not have. A family has to use all
        // three of its colours, or it cannot tell all of its colourways apart.
        var field = Lift(Mix(ink.Mid, ink.Deep, 0.22f), 0.66f);
        Fill(canvas, box, field);

        // Dead centre, and it does not move. A form in the middle can look like a target. But that
        // argument is about a form with an edge, and this one has no edge. A roll off the centre would
        // only put the sun in a different place in each colourway. Eight tiles whose suns do not line
        // up look unsteady, not varied. The colours move. The light does not.
        var centre = new SKPoint(box.MidX, box.MidY);

        // Wide, and every step between two colours a small one. A sun drawn with a tight radius and
        // four strong stops is a target. The eye reads the places where the colour changes fastest as
        // rings, and rings are a shape. Spread over most of the square with eight stops instead, the
        // same colours read as one fall of light, and the field is only what is left in the corners.
        using (var paint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                centre, edge * Spread,
                [
                    Lift(ink.Hot, 0.74f),                 // the core, all but white
                    Lift(ink.Hot, 0.60f),
                    Lift(ink.Hot, 0.30f),
                    ink.Hot,
                    Mix(ink.Hot, ink.Mid, 0.6f),
                    Mix(Mix(ink.Mid, ink.Deep, 0.42f), field, 0.25f),   // furthest from the light
                    Mix(ink.Mid, field, 0.72f),
                    field,
                ],
                [0f, 0.10f, 0.20f, 0.31f, 0.44f, 0.60f, 0.80f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, paint);

        Grain(canvas, box, 0x12, 0.5f, SKBlendMode.SoftLight, 0x4D17u);
    }

    /// <summary>How far out the sun's light reaches, as a fraction of the edge. It goes past the middle
    /// of the square and stops short of the corners. The field has to remain somewhere, or the fall of
    /// light has nothing to fall to.</summary>
    private const float Spread = 0.84f;

    /// <summary>One sphere on an empty field, lit from below. A whole cover made of one form and a lot
    /// of empty field. It works only because the form has real mass in the frame, and the empty part is
    /// a tint, not a colour.</summary>
    private static void Orb(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // Always the night colour, never a random choice between paper and dark. A random choice would
        // give two covers for one number, and that is not allowed. The sphere is lit from inside, and a
        // lit thing needs something dark to be lit against.
        Fill(canvas, box, ink.Night);

        // Centred, like the sun above, and for the same reason. A sphere a few points off the middle
        // does not look like a composition. It looks like a sphere that was not centred. It is one
        // size, as the sun's spread is one number. Nothing here moves between one colourway and the
        // next except the colours.
        var radius = edge * Bead;
        var centre = new SKPoint(box.MidX, box.MidY);

        // Blurred as a LAYER, not as a shape, and with decal tiling rather than clamp. The result must
        // be a sphere with no edge anywhere on it. A clamped blur would drag its colour out to the
        // corners of the square and take the field with it.
        using (var soft = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.05f, edge * 0.05f, SKShaderTileMode.Decal),
        })
        {
            canvas.SaveLayer(soft);

            using (var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(centre.X, centre.Y - radius),
                    new SKPoint(centre.X, centre.Y + radius),
                    // Lit from below, and the top is the MIDDLE colour rather than the shadow: a sphere
                    // whose top is the darkest thing on a pale ground reads as a hole in the paper.
                    [ink.Mid, Mix(ink.Mid, ink.Hot, 0.6f), Lift(ink.Hot, 0.20f), Lift(ink.Hot, 0.58f)],
                    [0f, 0.34f, 0.72f, 1f],
                    SKShaderTileMode.Clamp),
            })
                canvas.DrawCircle(centre, radius, paint);

            canvas.Restore();
        }

        Grain(canvas, box, 0x0E, 0.5f, SKBlendMode.SoftLight, 0x6A38u);
    }

    /// <summary>How big the sphere is, as a fraction of the edge: a third of it. The form has real mass
    /// in the frame, and the field around it is still most of the picture.</summary>
    private const float Bead = 0.34f;

    /// <summary>Colour with no subject: half a dozen wide pools laid over each other and blurred until
    /// none of them is a shape. The most saturated of the eight, and the one that reads at the smallest
    /// size, which is why it is what a name landing nowhere in particular gets.</summary>
    private static void Mesh(SKCanvas canvas, SKRect box, in Colourway ink, ref Roll roll)
    {
        var edge = box.Width;
        var under = Mix(ink.Deep, ink.Mid, 0.35f);
        Fill(canvas, box, under);

        SKColor[] pools =
        [
            Lift(ink.Hot, 0.55f),
            ink.Hot,
            Lift(ink.Mid, 0.25f),
            ink.Mid,
            Mix(ink.Mid, ink.Hot, 0.5f),
            Lift(ink.Deep, 0.15f),
        ];

        using (var soft = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.06f, edge * 0.06f, SKShaderTileMode.Clamp),
        })
        {
            canvas.SaveLayer(soft);
            Fill(canvas, box, under);

            // Wide, and mostly off the frame. A pool that fits inside the square looks like a spot on
            // something. At this size, and this far over the edge, the pools are the picture. What the
            // eye sees is where two of them meet.
            for (var i = 0; i < pools.Length; i++)
            {
                var centre = new SKPoint(
                    box.Left + (roll.Between(-0.25f, 1.25f) * edge),
                    box.Top + (roll.Between(-0.25f, 1.25f) * edge));

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(
                        centre, edge * roll.Between(0.45f, 0.85f),
                        [pools[i], pools[i].WithAlpha(0)],
                        [0f, 1f],
                        SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(box, paint);
            }

            canvas.Restore();
        }

        Grain(canvas, box, 0x14, 0.5f, SKBlendMode.SoftLight, roll.Next());
    }

    /// <summary>A line of light across a fall from pale to dark. It is the one family with a line in
    /// it. The line is the edge of something, not a mark on top of it.
    ///
    /// <para>It reads as an object because of the pairing. There is a wide soft band for the body, and
    /// a thin bright one where the light catches. It reads as a LINE, not a swoop, because it bends
    /// very little. It is almost straight, bowed once and gently, with both control points on the same
    /// side of it. Two curves in opposite directions would make an S, which is a ribbon. One shallow
    /// bow is a straight edge seen from slightly the wrong angle, and that is what this is meant to
    /// show.</para>
    ///
    /// <para><b>The numbers are fixed, not rolled</b>, as for <see cref="Ripple"/> and <see
    /// cref="Aurora"/>. This is one arrangement. A new set of colours is a request for new colours. So
    /// the line stays where it is, and only what it is made of changes.</para>
    /// </summary>
    private static void Sweep(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // Pale at the top right and deep at the bottom left, always. The light in this picture comes
        // from one place. If the corners changed with the colours, the lighting would change, not just
        // the colour.
        using (var ground = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.Right, box.Top), new SKPoint(box.Left, box.Bottom),
                [
                    Lift(ink.Mid, 0.78f),
                    Mix(ink.Mid, ink.Deep, 0.55f),
                    Sink(ink.Deep, 0.62f),
                    Sink(ink.Deep, 0.80f),
                ],
                [0f, 0.34f, 0.72f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, ground);

        // The line runs from off one edge to off another. Its ends are outside the square on purpose. A
        // line that starts and stops inside the frame is a stroke. This is meant to show something
        // passing through. Both control points sit a little ABOVE the straight run between the ends, on
        // the same side as each other. That is the difference between a bow and an S.
        using var path = new SKPath();
        path.MoveTo(box.Left - (edge * 0.2f), box.Top + (edge * 0.22f));
        path.CubicTo(
            box.Left + (edge * 0.34f), box.Top + (edge * 0.40f),
            box.Left + (edge * 0.62f), box.Top + (edge * 0.52f),
            box.Right + (edge * 0.2f), box.Top + (edge * 0.80f));

        using (var wide = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.055f, edge * 0.055f, SKShaderTileMode.Decal),
        })
        {
            canvas.SaveLayer(wide);
            using var band = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = edge * 0.20f,
                StrokeCap = SKStrokeCap.Round,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Bottom),
                    [Alpha(ink.Glow, 0f), Alpha(ink.Glow, 0.72f), Alpha(Lift(ink.Mid, 0.5f), 0.30f)],
                    [0f, 0.45f, 1f],
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(path, band);
            canvas.Restore();
        }

        // The catch of light along the line. It is thin, and blurred only enough to have no edge. This
        // is the one place in the file where something is nearly sharp, and the picture depends on it.
        using (var fine = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.006f, edge * 0.006f, SKShaderTileMode.Decal),
        })
        {
            canvas.SaveLayer(fine);
            using var line = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = edge * 0.012f,
                StrokeCap = SKStrokeCap.Round,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(box.Left, box.Top), new SKPoint(box.Right, box.Bottom),
                    [Alpha(ink.Glow, 0f), Alpha(Lift(ink.Hot, 0.92f), 0.85f), Alpha(ink.Glow, 0f)],
                    [0.1f, 0.5f, 0.95f],
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(path, line);
            canvas.Restore();
        }

        Grain(canvas, box, 0x12, 0.5f, SKBlendMode.SoftLight, 0x3C0Bu);
    }

    /// <summary>Sheets of light leaning across the frame, one behind another, all of them soft.
    ///
    /// <para>The picture is a stack of panes at an angle, lit from somewhere off to the side. A few
    /// catch the light almost white, and a few are in shadow. There is no edge anywhere except where
    /// two of them meet. This is the nearest thing to a photograph in the set, after <see
    /// cref="Ripple"/>. It is the only one made of straight lines, and it gets away with that because
    /// every line is out of focus.</para>
    ///
    /// <para><b>The lean is the whole composition.</b> Upright, these are a fence. Laid over, they are
    /// a floor. At twenty degrees off the vertical they read as sheets standing at an angle. That is
    /// the one reading with any depth in it. So the angle is a constant, not a roll.</para>
    ///
    /// <para><b>The numbers are fixed, not rolled</b>, as for <see cref="Sweep"/>, <see cref="Ripple"/>
    /// and
    /// <see cref="Prism"/>. There are twelve sheets. Each has its own width and its own pair of tones,
    /// and they are laid one after another across the frame. New colours are new colours. The
    /// arrangement does not move.</para>
    /// </summary>
    private static void Panels(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // Palest to deepest. Each sheet picks two of these, one for each side of it, so the light
        // falls ACROSS each sheet. A stack of flat strips would look like a bar chart.
        //
        // The palest tone is a COLOUR, not a white. A tile that is half white, beside seven other
        // covers, does not read as the brightest of the eight. It reads as the one that is wrong.
        SKColor[] tones =
        [
            Lift(ink.Hot, 0.46f),                             // the ones catching the light square on
            Sink(Mix(ink.Mid, ink.Deep, 0.5f), 0.62f),        // the field the rest of them stand in
            Lift(ink.Hot, 0.10f),
            Sink(Mix(ink.Mid, ink.Deep, 0.4f), 0.38f),
            Sink(ink.Deep, 0.70f),
            Sink(ink.Deep, 0.86f),                            // and the ones edge-on in the shadow
        ];

        Fill(canvas, box, tones[1]);

        // The whole stack is blurred at once, not sheet by sheet. The picture must have no sharp edge,
        // and the boundary between two sheets is exactly where the blur is needed. If each sheet were
        // blurred alone, it would be blurred against the ground rather than against its neighbour. The
        // stack would then come apart into separate strips.
        using (var soft = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.018f, edge * 0.018f, SKShaderTileMode.Clamp),
        })
        {
            canvas.SaveLayer(soft);

            canvas.Save();
            canvas.RotateDegrees(Lean, box.MidX, box.MidY);

            // Far enough past the square that the rotation cannot bring an end of one into the frame. A
            // sheet that stops inside the picture would look like a rectangle lying on it.
            var over = edge * 0.9f;
            var top = box.Top - over;
            var foot = box.Bottom + over;

            var x = box.Left - (edge * 0.42f);
            foreach (var sheet in Sheets)
            {
                var width = edge * sheet.Width;

                using (var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(
                        new SKPoint(x, box.MidY), new SKPoint(x + width, box.MidY),
                        [tones[sheet.From], tones[sheet.To]],
                        [0f, 1f],
                        SKShaderTileMode.Clamp),
                })
                    canvas.DrawRect(new SKRect(x, top, x + width, foot), paint);

                x += width;
            }

            canvas.Restore();
            canvas.Restore();
        }

        // One long fall of light over the whole stack, running across the lean rather than with it. It
        // stops twelve sheets of even brightness from reading as a pattern. The stack is lit, and a lit
        // thing is brighter at one end.
        using (var falls = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.Left, box.Bottom), new SKPoint(box.Right, box.Top),
                [Alpha(Sink(ink.Deep, 0.55f), 0.30f), Alpha(ink.Deep, 0f), Alpha(Lift(ink.Hot, 0.85f), 0.22f)],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, falls);

        Grain(canvas, box, 0x14, 0.5f, SKBlendMode.SoftLight, 0x2B71u);
    }

    /// <summary>How far off the vertical the sheets stand, in degrees. Clockwise, so the top of each
    /// one leans right.
    /// </summary>
    private const float Lean = 16f;

    /// <summary>One sheet. It has a width, as a fraction of the edge, and two tones. The light runs
    /// between the two tones across its width. <c>From</c> is the tone at its left edge, and <c>To</c>
    /// is the tone at its right. Both are indexes into the six tones the family is drawn in, palest
    /// first.</summary>
    private readonly record struct Sheet(float Width, int From, int To);

    /// <summary>The stack, laid left to right and starting well off the frame.
    ///
    /// <para>Their widths never repeat, and no two neighbours are the same tone. A stack of equal
    /// sheets looks like a grating. Two pale sheets side by side look like one wide pale sheet with a
    /// crease in it. The widths add up to nearly twice the square, which is what it takes to still
    /// cover the square once the whole stack has been leant over.</para>
    /// </summary>
    private static readonly Sheet[] Sheets =
    [
        new(0.18f, 1, 0),
        new(0.07f, 4, 3),
        new(0.20f, 0, 1),
        new(0.10f, 2, 3),
        new(0.16f, 0, 0),
        new(0.10f, 5, 4),
        new(0.22f, 1, 0),
        new(0.12f, 3, 2),
        new(0.18f, 0, 1),
        new(0.08f, 4, 5),
        new(0.24f, 1, 0),
        new(0.09f, 2, 3),
        new(0.20f, 0, 1),
    ];

    /// <summary>Lights well out of focus, at three depths. Nothing here is in focus. That is the trick.
    /// The eye reads the big faint discs as far away, and the small bright ones as near. A flat field
    /// of identical circles would look like spots on a page.</summary>
    private static void Bokeh(SKCanvas canvas, SKRect box, in Colourway ink, ref Roll roll)
    {
        var edge = box.Width;

        using (var ground = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.MidX, box.Top), new SKPoint(box.MidX, box.Bottom),
                [Mix(ink.Night, ink.Deep, 0.35f), ink.Night],
                [0f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, ground);

        SKColor[] lamps =
        [
            Lift(ink.Hot, 0.62f),
            Lift(ink.Hot, 0.28f),
            ink.Hot,
            Lift(ink.Mid, 0.42f),
            ink.Mid,
            Lift(ink.Hot, 0.85f),
        ];

        // Far, middle and near. Each pass is blurred as a whole, not disc by disc. A dozen separate
        // blurs would be a dozen layers, and the depth belongs to the pass anyway.
        (float Blur, float Low, float High, float Alpha, int Count)[] depths =
        [
            (0.035f, 0.09f, 0.16f, 0.40f, 9),
            (0.014f, 0.05f, 0.10f, 0.62f, 12),
            (0.005f, 0.02f, 0.05f, 0.85f, 14),
        ];

        foreach (var depth in depths)
        {
            using var soft = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(
                    edge * depth.Blur, edge * depth.Blur, SKShaderTileMode.Decal),
            };
            canvas.SaveLayer(soft);

            for (var i = 0; i < depth.Count; i++)
            {
                var radius = edge * roll.Between(depth.Low, depth.High);
                var centre = new SKPoint(
                    box.Left + (edge * roll.Between(-0.05f, 1.05f)),
                    box.Top + (edge * roll.Between(-0.05f, 1.05f)));
                var colour = lamps[(int)roll.Between(0f, lamps.Length)];
                var strength = depth.Alpha * roll.Between(0.55f, 1f);

                // Brighter at the rim than in the middle. An out-of-focus point of light is brighter at
                // its rim. That is the main thing that stops these from looking like coloured dots.
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(
                        centre, radius,
                        [
                            Alpha(colour, strength * 0.80f),
                            Alpha(colour, strength * 0.72f),
                            Alpha(colour, strength),
                            Alpha(colour, 0f),
                        ],
                        [0f, 0.70f, 0.93f, 1f],
                        SKShaderTileMode.Clamp),
                };
                canvas.DrawCircle(centre, radius, paint);
            }

            canvas.Restore();
        }

        Grain(canvas, box, 0x1A, 0.7f, SKBlendMode.SoftLight, roll.Next());
    }

    /// <summary>A drop hitting water: the rings going out from it, the column it throws up, and the
    /// drop itself still in the air over its own reflection.
    ///
    /// <para><b>This is the one family that depicts something, and the one that is not rolled.</b> The
    /// other seven are light with no subject, such as a sun, a sweep or a field of colour. They vary by
    /// being rearranged. This is a composition. People recognise a photograph of a splash, and most of
    /// what they recognise is the arrangement. Move the horizon, flatten the water a different amount,
    /// or put the drop somewhere else, and it stops being that photograph. It becomes a smear with a
    /// ball in it. So every measurement here is a fixed NUMBER, not a range. The family takes no roll
    /// at all, which is why its signature does not carry one. Asking for new colours gives this cover
    /// back as the same picture, in those colours.</para>
    ///
    /// <para><b>Two colours do all the work</b>, as they do in the photograph. Everything above the
    /// water is the sky. Everything the water shows is either the sky reflected off the front of a
    /// crest, or the dark of the depth behind it. The rings alternate between exactly those two. So
    /// this family needs a colourway with a real span in it. See the sky and water below, which take
    /// that span out of LIGHTNESS. <see cref="Colourway"/> gives three neighbours on the hue ring, and
    /// three neighbours are not a contrast.</para>
    /// </summary>
    private static void Ripple(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // The colourway is the only thing that changes. The sky is lifted most of the way up, and the
        // water is sunk most of the way down. Two hues one step apart at the same lightness look like
        // fog. This picture needs a bright half and a dark half, or it has no water in it.
        var sky = Lift(ink.Hot, 0.10f);
        var water = Sink(Mix(ink.Mid, ink.Deep, 0.65f), 0.62f);
        var deep = Sink(ink.Deep, 0.90f);

        var horizon = box.Top + (edge * HorizonAt);
        var centre = new SKPoint(box.MidX, box.Top + (edge * ImpactAt));

        // The whole picture is drawn into one layer, and softened on the way out.
        //
        // This family is the only one built from hard-edged marks: a horizon, a stroked column and two
        // circles. Next to the other seven, which have no edge anywhere, it looked like clip-art beside
        // photographs. A little softness over the whole picture puts it back into the same set. That is
        // enough to take the cut off the horizon and the outline off the drops. It is nowhere near
        // enough to lose the rings, which are gradients and survive anything.
        //
        // Clamped, not decal. Decal would pull transparency in from beyond the edges, and leave the
        // cover with a pale border that it does not have.
        using var whole = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(
                edge * Softness, edge * Softness, SKShaderTileMode.Clamp),
        };
        canvas.SaveLayer(whole);

        // The sky, then the water under it. Both are drawn across the whole square, and the rings go
        // over them. So wherever the rings have died out, there is still a surface.
        Fill(canvas, box, sky);

        using (var surface = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.MidX, horizon), new SKPoint(box.MidX, box.Bottom),
                [Mix(water, sky, 0.45f), water, Mix(water, deep, 0.75f)],
                [0f, 0.38f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(new SKRect(box.Left, horizon, box.Right, box.Bottom), surface);

        Rings(canvas, box, centre, sky, water, deep);

        // The far edge of the disturbance, thrown up against the sky. It is torn, not straight. The
        // water at the rim of a splash forms a crown. A horizon ruled with a straight edge would look
        // like a tabletop. The shape is still a fixed, written-down shape, like everything else here.
        using (var torn = new SKPath())
        {
            torn.MoveTo(box.Left, box.Top);
            torn.LineTo(box.Right, box.Top);
            torn.LineTo(box.Right, horizon);

            for (var i = Crown.Length - 1; i >= 0; i--)
                torn.LineTo(box.Left + (box.Width * i / (Crown.Length - 1f)), horizon - (edge * Crown[i]));

            torn.LineTo(box.Left, box.Top);
            torn.Close();

            using var paint = new SKPaint { IsAntialias = true, Color = sky };
            canvas.DrawPath(torn, paint);
        }

        // Haze where the water meets the sky. The horizon is the one hard edge in the picture and
        // the only place two flat colours meet along a line; a little of the sky spilling down into the
        // water is what keeps that from reading as a join between two sheets of paper.
        using (var haze = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.MidX, horizon - (edge * 0.02f)),
                new SKPoint(box.MidX, horizon + (edge * 0.16f)),
                [Alpha(sky, 0.26f), Alpha(sky, 0.08f), Alpha(sky, 0f)],
                [0f, 0.35f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(new SKRect(box.Left, horizon, box.Right, box.Bottom), haze);

        Column(canvas, centre, edge, sky, water, deep);

        // The whole surface gets one soft sheen across it, low and wide. Without it, the water looks
        // like paper printed with rings.
        using (var sheen = new SKPaint
        {
            BlendMode = SKBlendMode.Screen,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(box.MidX, box.Bottom + (edge * 0.1f)), edge * 0.75f,
                [Alpha(sky, 0.18f), Alpha(sky, 0f)],
                [0f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, sheen);

        canvas.Restore();

        // Drawn outside the layer, so the blur does not reach it. A grain that has been through a blur
        // is not a grain. It becomes a mottle, and the mottle undoes the one job of this pass, which is
        // to keep a wide smooth gradient from coming back out of the JPEG as steps.
        //
        // The seed is fixed, like everything else in this family. The noise is hard to see at the size
        // a cover is viewed. But "the same picture in new colours" is a promise about the bytes as well
        // as the composition, and a rolled seed would quietly break it.
        Grain(canvas, box, 0x14, 0.45f, SKBlendMode.SoftLight, 0x51EDu);
    }

    /// <summary>Where the water meets the sky, and where the drop went in. Both are fractions of the
    /// square's edge. The horizon is high and just below the middle. It must be high enough that the
    /// water is the picture, and the impact low enough that the drop above it has room.</summary>
    private const float HorizonAt = 0.21f;
    private const float ImpactAt = 0.57f;

    /// <summary>How flat the water is seen. The rings are circles on the water, and they look like
    /// ellipses in the picture. This is the whole of the perspective. The camera is nearly down at the
    /// surface, which is where a photograph of a splash is taken from, and the only place a splash
    /// reads as one. The value is well under a half. A ring seen at a slant is different from a ring
    /// seen from above, and a splash seen from above looks like a target.</summary>
    private const float Squash = 0.40f;

    /// <summary>The crown along the horizon, as lifts off it in fractions of the edge. It is fixed, not
    /// rolled. Three teeth in known places make a splash. A new set of teeth each time the colours
    /// change would make a different photograph.</summary>
    private static readonly float[] Crown =
        [0.002f, -0.001f, 0.011f, 0.001f, 0.003f, 0.015f, 0f, 0.002f, 0.009f, -0.002f, 0.001f, 0.012f, 0.002f];

    /// <summary>The rings, as one radial gradient with many stops, not as a stack of drawn ellipses.
    ///
    /// <para>One shader is used instead of thirty strokes, and it is also cheaper. A stroked ring has a
    /// width, and so two edges. Thirty of those at thumbnail size make a moiré pattern. A gradient has
    /// no edges at all. It is a wave, which is what is being drawn.</para>
    ///
    /// <para>There are four stops for each wave, and they are NOT evenly spaced. The crest is a narrow
    /// bright band, and the trough is a wide dark one. If the stops were spread evenly, the water would
    /// look like the end grain of a plank. What makes it read as water is that the light is only on the
    /// front of each wave, which is a small part of it. The contrast fades to nothing by the corners. A
    /// picture whose rings are as strong at the edge as at the middle has no centre, and the water out
    /// there has not been reached yet.</para>
    ///
    /// <para><b>The rings are drawn three times, each softer and further out than the last.</b> Sharp
    /// everywhere, five rings look like a diagram of a wave. A photograph of a wave has a hard centre
    /// and an outside that has gone soft. The far water is further from the lens, and the wave has
    /// travelled and spread. There is nothing out there for the eye to catch on. This is the other
    /// reason for the softening. It points the eye at the middle of the picture, where the drop
    /// is.</para>
    /// </summary>
    private static void Rings(
        SKCanvas canvas, SKRect box, SKPoint centre, SKColor sky, SKColor water, SKColor deep)
    {
        var edge = box.Width;
        var reach = edge * Reach;

        var colours = new List<SKColor>((Crests * 4) + 1);
        var stops = new List<float>((Crests * 4) + 1);

        // The same wave is read a second time, as LIGHT. Every crest becomes a band of sky at full
        // strength, and everything between the crests is transparent. When it is blurred and added back
        // over the picture, it makes a bloom. A bloom is the glow that a bright thing spills into the
        // dark area beside it. This is what separates a photograph of a wave from a diagram of one. A
        // camera produces this glow, and a plain fill does not.
        var glows = new List<SKColor>((Crests * 4) + 1);

        (float At, int Which)[] wave =
        [
            (0.00f, 0),   // the trough, at its darkest
            (0.34f, 1),   // climbing the back of the next one
            (0.60f, 2),   // the crest, holding the sky
            (0.74f, 1),   // and away down its front
        ];

        for (var w = 0; w <= Crests; w++)
        {
            foreach (var (at, which) in wave)
            {
                var t = (w + at) / Crests;
                if (t > 1f)
                    break;

                var fade = Math.Clamp(1f - MathF.Pow(t, 1.4f), 0f, 1f);
                var target = which switch
                {
                    0 => deep,
                    1 => water,
                    _ => Lift(sky, 0.18f),
                };

                colours.Add(Mix(water, target, fade));
                glows.Add(which == 2
                    ? Alpha(Lift(sky, 0.30f), 0.85f * fade * fade)
                    : Alpha(sky, 0f));

                // The stops are spread near the middle and crowded further out. That is what the waves
                // in the photograph do. It makes the middle read as the newest part of the water.
                stops.Add(MathF.Pow(t, 0.82f));
            }
        }

        using var rings = SKShader.CreateRadialGradient(
            new SKPoint(0, 0), reach, [.. colours], [.. stops], SKShaderTileMode.Clamp);

        canvas.Save();
        canvas.Translate(centre.X, centre.Y);
        canvas.Scale(1f, Squash);

        // The box in the squashed space is taller by the same factor as the squash is short. Otherwise
        // the bottom of the picture is outside what this paints, and it stays flat water. Everything
        // below is drawn in this space, so a blur laid on here is flattened the same way as the rings.
        // It has to be. A round blur on an elliptical wave looks like a wave with a halo.
        var tall = box.Height / Squash;
        var frame = new SKRect(box.Left - centre.X, -tall, box.Right - centre.X, tall);

        using (var sharp = new SKPaint { IsAntialias = true, Shader = rings })
            canvas.DrawRect(frame, sharp);

        Soften(canvas, frame, rings, reach, edge * 0.010f, 0.26f, 0.52f);
        Soften(canvas, frame, rings, reach, edge * 0.034f, 0.54f, 0.96f);

        // The bloom goes over the whole of it. It is added, not laid on, so it can only brighten. Light
        // does not take light away. A glow through an ordinary blend would grey the troughs it is
        // supposed to shine into.
        using (var bloom = SKShader.CreateRadialGradient(
                   new SKPoint(0, 0), reach, [.. glows], [.. stops], SKShaderTileMode.Clamp))
        using (var spill = new SKPaint
        {
            IsAntialias = true,
            Shader = bloom,
            BlendMode = SKBlendMode.Plus,
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.022f, edge * 0.022f, SKShaderTileMode.Clamp),
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x72),
        })
            canvas.DrawRect(frame, spill);

        // The middle burns: the newest water, still moving, right where the drop went in. One soft
        // source at the centre stops a set of even rings from reading as a target.
        using (var burn = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(0, 0), reach * 0.42f,
                [Alpha(Lift(sky, 0.4f), 0.20f), Alpha(sky, 0.06f), Alpha(sky, 0f)],
                [0f, 0.45f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(frame, burn);

        canvas.Restore();
    }

    /// <summary>
    /// The rings again, blurred, and kept only from <paramref name="from"/> outwards, so it covers the
    /// sharp copy underneath at the edge of the picture and leaves it alone in the middle.
    /// </summary>
    /// <param name="mask">The radius the fractions are measured against: the same reach the rings
    /// themselves are drawn to, so "half way out" means the same thing to both.</param>
    /// <param name="from">Where this copy starts to show, as a fraction of that reach. Nothing of it
    /// survives inside this.</param>
    /// <param name="to">And where it has covered the sharp copy completely.</param>
    private static void Soften(
        SKCanvas canvas, SKRect frame, SKShader rings, float mask, float sigma, float from, float to)
    {
        using var blur = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp),
        };

        canvas.SaveLayer(blur);

        using (var paint = new SKPaint { IsAntialias = true, Shader = rings })
            canvas.DrawRect(frame, paint);

        // The gradient removes parts of the copy. It is not painted over it. DstIn keeps the blurred
        // copy only where this gradient is opaque, which is the outside. A mask drawn the other way
        // round, as an alpha ramp painted over the top, would be a grey veil over the water and not a
        // hole in the copy.
        using (var keep = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(0, 0), mask,
                [new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 0), SKColors.Black, SKColors.Black],
                [0f, from, to, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(frame, keep);

        canvas.Restore();
    }

    /// <summary>How hard the finished picture is softened, as a fraction of its edge. Small: this is
    /// a photograph being taken slightly out of focus, not a picture being blurred past reading.</summary>
    private const float Softness = 0.006f;

    /// <summary>How many waves there are, from the middle outwards. There are five. They are wide
    /// enough to see what each one is doing. A dozen would close up into a texture at the size a cover
    /// is shown.</summary>
    private const int Crests = 5;

    /// <summary>How far the furthest wave has travelled, as a fraction of the edge: most of the way to
    /// the corner.</summary>
    private const float Reach = 0.85f;

    /// <summary>What is standing up out of the water: the column that the drop threw back up, the drop
    /// itself in the air above it, and both of them again, upside down, in the surface.
    ///
    /// <para>The reflection is drawn first and dimmed. Everything above the water stands ON its
    /// reflection, and has to cover it. The reflection is squashed by the same factor as the rings,
    /// because it is the same surface seen at the same angle. A reflection with any other flattening
    /// looks like a second picture pasted under the first.</para>
    /// </summary>
    private static void Column(
        SKCanvas canvas, SKPoint centre, float edge, SKColor sky, SKColor water, SKColor deep)
    {
        var height = edge * 0.125f;
        var waist = edge * 0.012f;
        var bead = edge * 0.036f;
        var speck = bead * 0.46f;

        var beadAt = new SKPoint(centre.X, centre.Y - height - (edge * 0.07f) - bead);
        var speckAt = new SKPoint(centre.X, beadAt.Y - bead - (edge * 0.07f));

        // Everything under the surface first, at a third of its strength and squashed the way the
        // rings are.
        canvas.Save();
        canvas.Translate(centre.X, centre.Y);
        canvas.Scale(1f, -Squash);
        canvas.Translate(-centre.X, -centre.Y);

        using (var mirrored = new SKPaint
        {
            IsAntialias = true,
            Color = Alpha(Mix(sky, deep, 0.55f), 0.34f),
        })
        {
            canvas.DrawOval(new SKRect(
                beadAt.X - (bead * 0.62f), beadAt.Y - (bead * 0.62f),
                beadAt.X + (bead * 0.62f), beadAt.Y + (bead * 0.62f)), mirrored);
            canvas.DrawOval(new SKRect(
                speckAt.X - (speck * 0.62f), speckAt.Y - (speck * 0.62f),
                speckAt.X + (speck * 0.62f), speckAt.Y + (speck * 0.62f)), mirrored);
        }

        canvas.Restore();

        // The column: a spike coming out of the water, with the sky down its lit side and the depth
        // down the other side. It is two curves, not a triangle, because water thrown up has a waist.
        using (var stem = new SKPath())
        {
            stem.MoveTo(centre.X - waist, centre.Y + (edge * 0.01f));
            stem.CubicTo(
                centre.X - (waist * 0.5f), centre.Y - (height * 0.5f),
                centre.X - (waist * 0.6f), centre.Y - (height * 0.85f),
                centre.X, centre.Y - height);
            stem.CubicTo(
                centre.X + (waist * 0.6f), centre.Y - (height * 0.85f),
                centre.X + (waist * 0.5f), centre.Y - (height * 0.5f),
                centre.X + waist, centre.Y + (edge * 0.01f));
            stem.Close();

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(centre.X - waist, centre.Y), new SKPoint(centre.X + waist, centre.Y),
                    [Lift(sky, 0.35f), Mix(sky, deep, 0.5f), deep],
                    [0f, 0.5f, 1f],
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawPath(stem, paint);

            // The collar of water round its foot. Without it, the column would seem to stand on the
            // surface rather than come out of it.
            using var collar = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(centre.X, centre.Y), waist * 3.2f,
                    [Alpha(Lift(sky, 0.3f), 0.5f), Alpha(sky, 0f)],
                    [0f, 1f],
                    SKShaderTileMode.Clamp),
            };
            canvas.DrawOval(
                new SKRect(centre.X - (waist * 3.2f), centre.Y - (waist * 1.2f),
                           centre.X + (waist * 3.2f), centre.Y + (waist * 1.2f)),
                collar);
        }

        Airborne(canvas, beadAt, bead, sky, water, deep);
        Airborne(canvas, speckAt, speck, sky, water, deep);
    }

    /// <summary>One drop in the air: the sky along its top, the water it came out of along its foot,
    /// and the lamp on its shoulder.</summary>
    private static void Airborne(
        SKCanvas canvas, SKPoint at, float radius, SKColor sky, SKColor water, SKColor deep)
    {
        var bounds = new SKRect(at.X - radius, at.Y - radius, at.X + radius, at.Y + radius);

        // The halo is drawn before the drop. A drop in the air with a hard edge and nothing round it
        // looks like a bead pasted onto the picture. The halo puts it in the same light as everything
        // else.
        using (var halo = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateRadialGradient(
                at, radius * 2.6f,
                [Alpha(Lift(sky, 0.35f), 0.50f), Alpha(sky, 0.14f), Alpha(sky, 0f)],
                [0.25f, 0.55f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawCircle(at.X, at.Y, radius * 2.6f, halo);

        using (var body = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(at.X, at.Y - radius), new SKPoint(at.X, at.Y + radius),
                [Lift(sky, 0.35f), sky, Mix(water, deep, 0.5f), Mix(sky, water, 0.65f)],
                [0f, 0.42f, 0.78f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawOval(bounds, body);

        using (var rim = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = radius * 0.14f,
            Color = Alpha(Lift(sky, 0.55f), 0.55f),
        })
            canvas.DrawOval(bounds, rim);

        using (var spark = new SKPaint
        {
            IsAntialias = true,
            BlendMode = SKBlendMode.Screen,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xC0),
            ImageFilter = SKImageFilter.CreateBlur(radius * 0.12f, radius * 0.12f, SKShaderTileMode.Decal),
        })
            canvas.DrawCircle(at.X - (radius * 0.34f), at.Y - (radius * 0.38f), radius * 0.20f, spark);
    }

    /// <summary>Light smeared the long way down the frame, seen through fluting.
    ///
    /// <para>The vertical smear gives the colours somewhere to have come from. Every band here is a
    /// pool of colour drawn out. So the bands belong to each other, the way the colours of a spectrum
    /// do, and not the way a list of unrelated colours does.</para>
    ///
    /// <para><b>The arrangement is fixed, not rolled</b>, as for <see cref="Sweep"/>, <see
    /// cref="Aurora"/> and
    /// <see cref="Ripple"/>. There are five pools in five places. The fall is always pale on the left
    /// and deep on the right. The fluting comes from a fixed walk, which is the same walk every time.
    /// New colours are new colours. The picture underneath them does not move.</para>
    /// </summary>
    private static void Prism(SKCanvas canvas, SKRect box, in Colourway ink)
    {
        var edge = box.Width;

        // Pale at the left and deep at the right, always. If this were rolled the other way, the same
        // cover would come back mirrored whenever its colours changed. That is not a recolouring. It is
        // the light moving to the other side of the room.
        using (var ground = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(box.Left, box.MidY), new SKPoint(box.Right, box.MidY),
                [Lift(ink.Mid, 0.88f), Lift(ink.Mid, 0.35f), Sink(ink.Deep, 0.55f), Sink(ink.Deep, 0.84f)],
                [0f, 0.28f, 0.66f, 1f],
                SKShaderTileMode.Clamp),
        })
            canvas.DrawRect(box, ground);

        // Each pool is drawn round and then pulled straight. The blur is a sixth of the square down one
        // axis, and almost nothing across it. So a pool of colour comes out as a band that still has
        // the pool's own shading in it.
        using (var smear = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(edge * 0.055f, edge * 0.16f, SKShaderTileMode.Clamp),
        })
        {
            canvas.SaveLayer(smear);

            SKColor[] beams = [ink.Glow, ink.Hot, ink.Mid, Lift(ink.Hot, 0.5f), ink.Deep];
            for (var i = 0; i < beams.Length; i++)
            {
                var pool = Spectrum[i];
                var centre = new SKPoint(
                    box.Left + (edge * pool.At), box.Top + (edge * pool.Down));

                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateRadialGradient(
                        centre, edge * pool.Radius,
                        [Alpha(beams[i], pool.Strength), Alpha(beams[i], 0f)],
                        [0f, 1f],
                        SKShaderTileMode.Clamp),
                };
                canvas.DrawRect(box, paint);
            }

            canvas.Restore();
        }

        // The fluting is a ribbed sheet of glass over the smear, not a set of stripes drawn on it. The
        // strength matters. At a quarter alpha the ribs look like a barcode. At a tenth they read as
        // the surface the light is behind.
        //
        // The ribs are uneven. Evenly spaced ribs at thumbnail size beat against the pixel grid and
        // make a moiré that nobody drew. The unevenness comes from a fixed walk, not from the roll.
        // Stepping by the golden ratio and taking the fraction never repeats and never clumps. A roll
        // cannot promise that. The walk also gives the same fluting every time, which is the point
        // here.
        const int ribs = 36;
        var pitch = edge / ribs;
        for (var i = 0; i < ribs; i++)
        {
            var step = ((i * 0.6180339f) % 1f + 1f) % 1f;
            var width = pitch * (0.32f + (0.34f * step));
            var up = step < 0.5f;

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = up
                    ? new SKColor(0xFF, 0xFF, 0xFF, (byte)(5f + (18f * step)))
                    : new SKColor(0, 0, 0, (byte)(5f + (16f * (1f - step)))),
            };
            canvas.DrawRect(SKRect.Create(box.Left + (i * pitch), box.Top, width, box.Height), paint);
        }

        Grain(canvas, box, 0x12, 0.6f, SKBlendMode.SoftLight, 0x7E42u);
    }

    /// <summary>Where the five pools of light sit before they are pulled down the frame, and how strong
    /// each is: across, down, how wide, and how much colour it carries, all as fractions of the
    /// edge.</summary>
    private readonly record struct Pool(float At, float Down, float Radius, float Strength);

    /// <summary>The arrangement. The pools are spread across the frame and staggered down it. So the
    /// bands that come out of the smear differ in length as well as in colour. Five pools at one height
    /// would smear into five bands that all start and stop together, which looks like a printed
    /// stripe.</summary>
    private static readonly Pool[] Spectrum =
    [
        new(0.09f, 0.30f, 0.24f, 0.62f),
        new(0.27f, 0.64f, 0.18f, 0.54f),
        new(0.45f, 0.34f, 0.27f, 0.70f),
        new(0.67f, 0.72f, 0.20f, 0.46f),
        new(0.88f, 0.44f, 0.25f, 0.58f),
    ];

    // ── the small tools ──────────────────────────────────────────────────────

    private static void Fill(SKCanvas canvas, SKRect box, SKColor colour)
    {
        using var paint = new SKPaint { Color = colour };
        canvas.DrawRect(box, paint);
    }

    /// <summary>Noise over the whole square.
    ///
    /// <para>Every family ends with a pass of this. It is not decoration. The result is encoded as a
    /// JPEG at ninety, and a JPEG of a wide smooth gradient is what that codec handles worst. It comes
    /// back as steps. A few counts of noise under the quantiser is the standard answer. It costs
    /// nothing that anybody can see at the size a cover is viewed.</para>
    ///
    /// <para>The noise is drained to grey first. Perlin noise has three independent channels, which
    /// makes coloured static. Laid over a picture with its own colours, that looks like a fault. What
    /// is wanted here is a surface, not an effect.</para>
    /// </summary>
    /// <param name="frequency">How fine the noise is, in cycles per pixel: a tenth is a coarse mottle
    /// and one is the tooth of paper.</param>
    private static void Grain(
        SKCanvas canvas, SKRect box, byte alpha, float frequency, SKBlendMode blend, uint seed)
    {
        using var noise = SKShader.CreatePerlinNoiseFractalNoise(frequency, frequency, 2, seed % 1024u);

        // The last row is <c>0 0 0 1 0</c>, which passes alpha straight through, and it has to. A
        // matrix that SETS alpha (a last row ending in 0 1, which is the obvious way to write "opaque
        // grey") throws away the strength asked for in the paint. Skia folds the paint's alpha into the
        // shader, and then runs the filter over the result. A filter that overwrites alpha replaces
        // that strength with full opacity, and the grain then reads as a photograph of static.
        using var grey = SKColorFilter.CreateColorMatrix(
        [
            0.33f, 0.33f, 0.33f, 0f, 0f,
            0.33f, 0.33f, 0.33f, 0f, 0f,
            0.33f, 0.33f, 0.33f, 0f, 0f,
            0f,    0f,    0f,    1f, 0f,
        ]);

        using var paint = new SKPaint
        {
            Shader = noise,
            ColorFilter = grey,
            BlendMode = blend,
            Color = new SKColor(0xFF, 0xFF, 0xFF, alpha),
        };
        canvas.DrawRect(box, paint);
    }

    /// <summary>Toward white, by a fraction. This is not a brightness multiply. Multiplying a saturated
    /// colour up pushes one channel to its ceiling, and turns gold into yellow. Mixing toward white
    /// keeps the hue and only takes the strength out of it, which is what a tint is.</summary>
    private static SKColor Lift(SKColor colour, float towards)
        => Mix(colour, new SKColor(0xFF, 0xFF, 0xFF), towards);

    /// <summary>And toward black, the same way.</summary>
    private static SKColor Sink(SKColor colour, float towards)
        => Mix(colour, new SKColor(0, 0, 0), towards);

    private static SKColor Mix(SKColor from, SKColor to, float towards)
    {
        var t = Math.Clamp(towards, 0f, 1f);
        return new SKColor(
            (byte)(from.Red + ((to.Red - from.Red) * t)),
            (byte)(from.Green + ((to.Green - from.Green) * t)),
            (byte)(from.Blue + ((to.Blue - from.Blue) * t)));
    }

    private static SKColor Alpha(SKColor colour, float alpha)
        => colour.WithAlpha((byte)Math.Clamp(alpha * 255f, 0f, 255f));

    /// <summary>How much light a colour carries, which is what <see cref="Colourway"/> sorts on. The
    /// same weighting every other brightness question in this app is asked with.</summary>
    private static double Light(SKColor colour)
        => (0.2126 * colour.Red) + (0.7152 * colour.Green) + (0.0722 * colour.Blue);

    /// <summary>The same xorshift32 that <see cref="SoftFigure"/> uses to lay out its blobs. It is
    /// written again here, because that one is private to its own file, and this needs only the three
    /// lines. It is deterministic, and it is not for anything that needs to be unguessable.</summary>
    private struct Roll(uint seed)
    {
        private uint _state = seed;

        public float Between(float low, float high)
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return low + ((_state % 10000u) / 10000f * (high - low));
        }

        /// <summary>A count of elements, inclusive at both ends. <see cref="Between"/>'s upper bound is
        /// exclusive. It scales a remainder that never reaches 10000/10000. So an <c>(int)</c> cast
        /// applied directly would drop the top value.</summary>
        public int Count(int low, int high) => (int)Between(low, high + 1f);

        /// <summary>A whole number, for something that needs a seed of its own, such as the noise,
        /// which takes one rather than a fraction. It comes from the same stream, so it changes with
        /// everything else the arrangement rolled.</summary>
        public uint Next() => (uint)Between(0f, 1_000_000f);
    }
}

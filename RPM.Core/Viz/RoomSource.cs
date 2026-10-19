using System.Globalization;

namespace RPM.Core.Viz;

/// <summary>
/// The room itself, as one piece of GLSL both heads compile.
///
/// <para>Three families of soft lobes drift on their own slow phases. The whole plane is warped twice
/// before any of them is measured. So what would be fifteen circles reads as smoke, or cloth, or
/// something with no clear name. That is the intent. The music swells them. The low end pushes the big
/// family outward and lets it fall. The middle darts the second family across the room. The air lights
/// the third family behind both. Their colours come from the cover.</para>
///
/// <para><b>Why the text is here.</b> The desktop paints this in a GL fragment program
/// (<c>PegasusStage</c>). The phone paints it on Skia in SkSL (<c>RoomStage</c>). <c>GLCanvasElement</c>
/// has no iOS build, and <c>SKCanvasElement</c> is what iOS has instead. There are two compilers and one
/// picture. It stays one picture only while it is one text. Written once here, the desktop drops it into
/// its own shader whole. The phone runs it through <see cref="SkslSource.Translate"/>, which only changes
/// the spelling.</para>
///
/// <para>What is NOT here is everything that each head does with the room afterwards. That includes the
/// dim that holds one side back under the words, the spectrum over it, the mirror ball, the vignette and
/// the grain. These belong to the composition, not to the room. The two compositions may differ. The
/// desktop writes down the left of a landscape window. The phone writes along the foot of a portrait
/// window.</para>
/// </summary>
public static class RoomSource
{
    /// <summary>How many soft figures drift in the background. Six is where they stop reading as separate
    /// shapes and start reading as one moving body of colour.</summary>
    public const int Lobes = 6;

    /// <summary>How many of the second, smaller family cross over them. Five, because these are meant to
    /// read as separate shapes. With more of them, the two families would merge into one wash.</summary>
    public const int Lobes2 = 5;

    /// <summary>How many of the third, smaller family there are. Four. They are half the size of the
    /// second family, and they answer to the busiest part of the music. With more of them, the top of the
    /// picture is never still.</summary>
    public const int Lobes3 = 4;

    /// <summary>How much quicker the third family moves than the second: thirty per cent. This is applied
    /// to the family's clock, not to each rate in turn. So its drift, its orbit and its wobble keep the
    /// proportions they were tuned in.</summary>
    public const float AirRate = 1.3f;

    /// <summary>How far the third family's falloff is spread. This is its blur, in the terms that
    /// <c>rpmLobe</c> takes. Behind the other two families, the third has nothing to hold an edge against.
    /// A soft light behind the room is what it is there to be.
    ///
    /// <para>The family's colour ramps are written against <c>AIR_PEAK</c>, which is derived from this. So
    /// this figure can be changed on its own, and the family blurs without also dimming.</para></summary>
    public const float AirSoft = 1.08f;

    /// <summary>
    /// The room, as a function of the plane and the music. It declares <c>rpmLobe</c> and <c>rpmRoom</c>,
    /// and nothing else. There are no uniforms and no entry point. So either head may drop it into a
    /// program of its own.
    ///
    /// <para><c>p</c> is the plane with the origin in the middle of the screen. It is scaled by its HEIGHT,
    /// so the figures are the same size and shape whatever the window's aspect. <c>t</c> is seconds.</para>
    /// </summary>
    public static string Glsl { get; } = $$"""
        const int RPM_LOBES = {{Lobes}};
        const int RPM_LOBES2 = {{Lobes2}};
        const int RPM_LOBES3 = {{Lobes3}};
        const float AIR_RATE = {{Num(AirRate)}};
        const float AIR_SOFT = {{Num(AirSoft)}};
        // The height one lobe of that softness reaches on its own - see rpmLobe(), where d is nought. The
        // third family's ramps are all written against this, so its blur can be changed without its
        // brightness following.
        const float AIR_PEAK = 1.0 / AIR_SOFT;

        // A lobe with no edge at all: it never reaches zero, which is exactly what lets six of them melt
        // into one another instead of overlapping like coins. `soft` is how far the falloff is spread -
        // larger takes the peak down and the skirt out, which is a blurrier lobe of the same size rather
        // than a bigger one.
        float rpmLobe(vec2 p, vec2 c, float r, float soft)
        {
            vec2 d = p - c;
            float r2 = r * r;
            return r2 / (dot(d, d) + r2 * soft);
        }

        vec3 rpmRoom(vec2 p, float t, float bass, float mid, float air,
                     vec3 ground, vec3 tint, vec3 glow, vec3 shade)
        {
            // -- the third family, which is drawn first --
            //
            // Smallest, quickest, in the sleeve's third colour alone, and BEHIND the other two - so this is
            // where the room starts, on the ground rather than over the top of it. Everything below is laid
            // over what comes out of here.
            //
            // Behind is the right place for it. What it answers to is the busiest part of the music, and a
            // fast small figure in front of two slow large ones reads as something scattered ON the picture
            // - the eye goes to it and stays. Underneath, the families above break it up and it is glimpsed
            // between them instead, which is what the top end of a mix actually does: it is heard around
            // the music rather than on top of it.
            //
            // Where the second family travels between two of the cover's colours, these are only ever the
            // one, which is what still makes them findable as a third thing down there rather than as more
            // ground.
            //
            // Everything about their motion runs at AIR_RATE, thirty per cent above the second family's -
            // one factor on the clock rather than a rate per term, so drift, orbit and wobble stay in the
            // proportion they were tuned in and the whole family simply moves quicker.
            float t3 = t * AIR_RATE;

            vec2 w3 = p;
            w3 += 0.11 * vec2(sin(p.y * 3.2 + t3 * 0.29), cos(p.x * 2.9 - t3 * 0.25));
            w3 += (0.05 + air * 0.11) * vec2(sin(w3.y * 5.1 - t3 * 0.21), cos(w3.x * 4.6 + t3 * 0.23));

            float field3 = 0.0;
            for (int i = 0; i < RPM_LOBES3; i++)
            {
                float fi = float(i);
                float a = t3 * (0.095 + fi * 0.026) + fi * 2.9;
                vec2 c = vec2(cos(a * 0.94 + fi * 1.9) * 0.70,
                              sin(a * 1.37 + fi * 2.6) * 0.42);
                // Half the size of the second family's, and swelling harder - a hat should be seen to land.
                float r = (0.085 + 0.030 * sin(t3 * 0.41 + fi * 3.1)) * (1.0 + air * 0.85);
                // The softest skirt of the three: a figure that is glimpsed between other things does not
                // want an edge at all - it wants to be a light behind them.
                field3 += rpmLobe(w3, c, r, AIR_SOFT);
            }

            // No wash of its own to average: one colour means there is nothing to mix between, and the
            // weighting the families below do exists only to keep two colours from averaging into one.
            //
            // The two ramps are written as fractions of AIR_PEAK - the height ONE lobe reaches, which is
            // 1/soft and so moves whenever the blur does. Written as bare numbers they were tuned against a
            // peak of 1.39, and the softer skirt drops that to 0.93: the mix would have weakened and the
            // glow all but stopped, so blurring the family would have quietly dimmed it as well.
            vec3 back = mix(ground, shade, smoothstep(0.17 * AIR_PEAK, 1.22 * AIR_PEAK, field3) * 0.70);
            // The glow, and the point of the whole family. The ramp starts BELOW a single lobe's peak - the
            // two families below only bloom where several of them pile up, which for four small fast
            // figures would be almost never.
            back += shade * smoothstep(0.65 * AIR_PEAK, 1.73 * AIR_PEAK, field3) * (0.16 + air * 0.62);

            // -- the figures --
            //
            // Warp the plane twice before measuring anything. The lobes below are round; this is the whole
            // of why the picture is not. The second round is pushed by the bass, so the low end does not
            // merely swell the shapes, it stirs them.
            vec2 w = p;
            w += 0.20 * vec2(sin(p.y * 2.1 + t * 0.19), cos(p.x * 1.8 - t * 0.15));
            w += (0.10 + bass * 0.14) * vec2(sin(w.y * 3.9 - t * 0.11), cos(w.x * 3.3 + t * 0.13));

            float field = 0.0;
            float weight = 0.0;
            vec3 wash = vec3(0.0);
            for (int i = 0; i < RPM_LOBES; i++)
            {
                float fi = float(i);
                // The golden angle spreads their phases so no two of them ever come round together.
                float a = t * (0.047 + fi * 0.0115) + fi * 2.39996;
                vec2 c = vec2(cos(a + fi * 1.3) * (0.46 + 0.20 * sin(t * 0.031 + fi)),
                              sin(a * 0.83 + fi * 2.1) * 0.32);
                float r = (0.20 + 0.055 * sin(t * 0.21 + fi * 1.7)) * (1.0 + bass * 0.62);

                float g = rpmLobe(w, c, r, 0.42);
                field += g;
                // Weighted by the SQUARE of the lobe's strength, not the strength. Averaged linearly, six
                // overlapping lobes hand every pixel very nearly the same mixture and the whole screen
                // comes out one colour; squared, the nearest lobe owns its own neighbourhood and the
                // colours stay separate where they meet.
                float wg = g * g;
                weight += wg;
                wash += mix(tint, glow, fract(fi * 0.41 + 0.15)) * wg;
            }
            wash /= max(weight, 1e-4);

            // Over the third family rather than over the ground: that layer IS the ground now, and where
            // the lobes here are strong they cover it, which is what puts it behind them.
            vec3 col = mix(back, wash, smoothstep(0.30, 1.75, field));
            // Where several lobes lie over one another the colour LIFTS rather than clipping, and the low
            // end lifts it further - which is the bloom that makes the room feel lit from inside.
            //
            // The light in the BODY of the room answers to the bass and to nothing else. A whole room lit
            // by everything at once flickers on the whole mix, which reads as noise rather than as a pulse;
            // and the middle already drives how the second family MOVES, which is motion, and a separate
            // question from light. The third family is the one deliberate exception, and being small and
            // behind both of these is what affords it.
            col += wash * smoothstep(1.60, 3.40, field) * (0.12 + bass * 0.44);

            // -- the second family --
            //
            // Smaller, quicker, and in the counter-colour: figures that cross the room rather than fill it.
            // Their own warp is tighter and their own drive is the MIDDLE of the music, so they dart while
            // the big lobes below swell - two things happening at two speeds, which is the difference
            // between a moving picture and a moving wash.
            //
            // Their warp runs at a LOW frequency: high-frequency warp is detail, and detail is the opposite
            // of what these are meant to be. They are as soft-edged as the lobes below them - only smaller,
            // quicker, and in the other colour.
            vec2 w2 = p;
            w2 += 0.15 * vec2(sin(p.y * 2.6 - t * 0.27), cos(p.x * 2.3 + t * 0.23));
            w2 += (0.07 + mid * 0.13) * vec2(sin(w2.y * 4.4 + t * 0.19), cos(w2.x * 3.9 - t * 0.21));

            float field2 = 0.0;
            float weight2 = 0.0;
            vec3 wash2 = vec3(0.0);
            for (int i = 0; i < RPM_LOBES2; i++)
            {
                float fi = float(i);
                float a = t * (0.083 + fi * 0.021) + fi * 1.7;
                vec2 c = vec2(cos(a * 1.21 + fi * 2.7) * 0.62,
                              sin(a + fi * 0.9) * 0.38);
                float r = (0.145 + 0.045 * sin(t * 0.37 + fi * 2.3)) * (1.0 + mid * 0.55);

                // A wider skirt than the big lobes wear (0.42), which is what spreads these out instead of
                // leaving five bright coins travelling over the wash.
                float g = rpmLobe(w2, c, r, 0.88);
                // ...and a gentler weighting than the square the primaries use: these want to bleed into
                // one another, not hold their own ground.
                float wg = g * sqrt(g);
                field2 += g;
                weight2 += wg;
                wash2 += mix(shade, glow, fract(fi * 0.53 + 0.40)) * wg;
            }
            wash2 /= max(weight2, 1e-4);

            // A long, low ramp: the edge of the second family should never be findable.
            col = mix(col, wash2, smoothstep(0.20, 1.90, field2) * 0.78);
            col += wash2 * smoothstep(1.30, 3.50, field2) * (0.14 + bass * 0.40);

            return col;
        }
        """;

    /// <summary>Formats a float the way GLSL spells it. Interpolating a float straight into the source
    /// would use the current culture. On a machine with a comma as the decimal separator, <c>0.62</c>
    /// would be written <c>0,62</c>, and the shader would not compile. So this always uses the invariant
    /// culture, with a decimal point.</summary>
    private static string Num(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}

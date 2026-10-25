namespace RPM.Core.Viz;

/// <summary>
/// The GLSL of the room's other animations (<see cref="RoomStyle"/>): Yang, Lava and Alchemy.
///
/// <para>Each one is a function <c>vec3 styleRoom(vec2 p)</c>, which a program calls in place of
/// <c>rpmRoom</c> from <see cref="RoomSource"/>. It gives the room's colour at <c>p</c>, before anything
/// the program does to the room afterwards (the dimming, the spectrum, the vignette, the grain). So a
/// program that draws the original can draw these with no other change. <c>p</c> is the same plane as
/// <c>rpmRoom</c>'s: the origin in the middle of the screen, scaled by the height.</para>
///
/// <para><b>What the program around it must declare.</b> These uniforms, by these names:
/// <c>vec2 uResolution</c>, <c>float uBass, uMid, uAir</c> and <c>vec3 uGround, uTint, uGlow, uShade</c>.
/// It must also include <see cref="RoomSource.Glsl"/> first, for <c>rpmLobe</c>. Each style declares its
/// own figures as uniform arrays of vec4 (<see cref="Arrays"/>). <see cref="RoomFigures"/> fills them
/// once per frame, so the shader spends nothing per pixel on numbers that are the same for every
/// pixel.</para>
///
/// <para>The cost is about the original's: one pass, no texture reads, no second buffer. The original
/// spends twelve sines and cosines on its warps and fifteen lobes per pixel. Yang spends no sines and
/// twenty-nine lobes, Lava one sine and ten lobes, and Alchemy fifteen sines, eleven exponentials and
/// one power.</para>
///
/// <para>The Android TV app draws the same text (its RoomShaders.kt). Change both together. ASCII only,
/// comments included: GLSL ES accepts nothing above ASCII.</para>
/// </summary>
public static class RoomStyleSource
{
    /// <summary>The <c>styleRoom</c> function of <paramref name="style"/>, with its own uniforms and
    /// constants. Empty for <see cref="RoomStyle.Original"/>, which is <c>rpmRoom</c>.</summary>
    public static string For(RoomStyle style) => style switch
    {
        RoomStyle.Yang => Yang,
        RoomStyle.Lava => Lava,
        RoomStyle.Alchemy => Alchemy,
        _ => "",
    };

    /// <summary>The names of the three uniform arrays that <paramref name="style"/> declares, in the order
    /// <see cref="RoomFigures"/> fills them, with how many vec4 each holds. Null for
    /// <see cref="RoomStyle.Original"/>, which works its figures out in the shader.</summary>
    public static (string Name, int Count)[]? Arrays(RoomStyle style) => style switch
    {
        RoomStyle.Yang => [("uBig", RoomFigures.YangBig), ("uSmall", RoomFigures.YangSmall), ("uCentre", 1)],
        RoomStyle.Lava => [("uBlobs", RoomFigures.LavaBlobs), ("uPools", 1), ("uBack", 1)],
        RoomStyle.Alchemy =>
            [("uRibbonA", RoomFigures.Ribbons), ("uRibbonB", RoomFigures.Ribbons), ("uRibbonC", RoomFigures.Ribbons)],
        _ => null,
    };

    /// <summary>
    /// Yang (<see cref="RoomFigures.Yang"/>). The middle ball is drawn as the original's third family
    /// (its softness of 1.08 and its ramp), lit by the middle of the music where that family is lit by
    /// the air. Each pair of balls is a family of its own, at the original's thresholds: the big balls in
    /// the tint, lit by the bass, and the small ones in the glow, lit by the air. An echo's weight scales
    /// its share of the field, so the tail fades as it thins. The small balls are drawn first and the big
    /// ones over them, so a small ball passes behind a big one.
    /// </summary>
    public static string Yang { get; } = $$"""
        const int YANG_BIG = {{RoomFigures.YangBig}};
        const int YANG_SMALL = {{RoomFigures.YangSmall}};
        uniform vec4 uBig[YANG_BIG];
        uniform vec4 uSmall[YANG_SMALL];
        uniform vec4 uCentre;

        vec3 styleRoom(vec2 p)
        {
            float c = rpmLobe(p, uCentre.xy, uCentre.z, 1.08);
            vec3 col = mix(uGround, uShade, smoothstep(0.16, 1.13, c) * 0.70);
            col += uShade * smoothstep(0.60, 1.60, c) * (0.16 + uMid * 0.62);

            float big = 0.0;
            float small = 0.0;
            for (int i = 0; i < YANG_BIG; i++)
            {
                big += rpmLobe(p, uBig[i].xy, uBig[i].z, 0.42) * uBig[i].w;
            }
            for (int i = 0; i < YANG_SMALL; i++)
            {
                small += rpmLobe(p, uSmall[i].xy, uSmall[i].z, 0.60) * uSmall[i].w;
            }

            // The small balls first and the big ones over them: where a big ball stands, it hides the
            // small one under it.
            col = mix(col, uGlow, smoothstep(0.25, 1.60, small) * 0.85);
            col += uGlow * smoothstep(1.20, 3.00, small) * (0.14 + uAir * 0.40);
            float cover = smoothstep(0.30, 1.75, big);
            col = mix(col, uTint, cover);
            col += uTint * smoothstep(1.60, 3.40, big) * (0.12 + uBass * 0.44);
            return col;
        }
        """;

    /// <summary>
    /// Lava (<see cref="RoomFigures.Lava"/>). The background ball is drawn as Yang's middle one, lit by
    /// the middle. The pools are lobes in one direction only: a band across the screen with a slow
    /// ripple, summed into the blobs' field, so a blob that reaches one melts into it. The blobs wear the
    /// colours of the original's first family, weighted by the square as that family is. Their edge is
    /// narrower than the original's (0.70 to 1.30 of the field, not 0.30 to 1.75), so they read as wax
    /// and not as haze.
    /// </summary>
    public static string Lava { get; } = $$"""
        const int LAVA_BLOBS = {{RoomFigures.LavaBlobs}};
        uniform vec4 uBlobs[LAVA_BLOBS];
        uniform vec4 uPools;
        uniform vec4 uBack;

        vec3 styleRoom(vec2 p)
        {
            float b = rpmLobe(p, uBack.xy, uBack.z, 1.08);
            vec3 col = mix(uGround, uShade, smoothstep(0.16, 1.13, b) * 0.70);
            col += uShade * smoothstep(0.60, 1.60, b) * (0.16 + uMid * 0.62);

            float wave = sin(p.x * 3.1 + uBack.w);
            float db = p.y - (uPools.x + 0.025 * wave);
            float dt = p.y - (uPools.z - 0.020 * wave);
            float rb = uPools.y * uPools.y;
            float rt = uPools.w * uPools.w;
            float bottom = rb / (db * db + rb * 0.42);
            float top = rt / (dt * dt + rt * 0.42);
            float field = bottom + top;
            float weight = bottom * bottom + top * top;
            vec3 wash = uTint * (bottom * bottom) + uGlow * (top * top);
            for (int i = 0; i < LAVA_BLOBS; i++)
            {
                float g = rpmLobe(p, uBlobs[i].xy, uBlobs[i].z, 0.42);
                field += g;
                float wg = g * g;
                weight += wg;
                wash += mix(uTint, uGlow, uBlobs[i].w) * wg;
            }
            wash /= max(weight, 1e-4);
            col = mix(col, wash, smoothstep(0.70, 1.30, field));
            col += wash * smoothstep(1.60, 3.40, field) * (0.12 + uBass * 0.44);
            col += uGlow * smoothstep(2.40, 4.00, field) * uAir * 0.25;
            return col;
        }
        """;

    /// <summary>
    /// Alchemy (<see cref="RoomFigures.Alchemy"/>): make_rpamp_ground.py, pixel for pixel, in the
    /// script's own frame (u across, v down, both 0 to 1).
    ///
    /// <para>Each ribbon is a curve of two sines and a tilt. Its light falls off on both sides as a
    /// Gaussian whose thickness swells along it, with a filament a fifth as thick along the crest. The
    /// ribbons are summed, lit from the top left, and put through the script's soft knee and exponent.
    /// The script scales by the picture's own floor and 99th percentile. Those are fixed here at the
    /// values its picture has (0.046 and 2.13). The top is eased off above 0.9, so a ribbon swollen by the
    /// bass reaches the crest colour and does not spread it into a sheet.</para>
    ///
    /// <para>The colour ramp is the script's, with its stops made from the palette: the dark ground, then
    /// each ribbon's own colour (weighted by its share of the light) at 0.12, 0.39, 0.66 and 0.95 of
    /// itself, then the glow on the crests. The steps overlap, so the layers of light melt into one
    /// another. The picture is a background meant to be seen at full strength, so it is lifted
    /// <c>LIFT</c> times against the room's dimming to 0.62.</para>
    /// </summary>
    public static string Alchemy { get; } = $$"""
        const int RIBBONS = {{RoomFigures.Ribbons}};
        const float LIFT = 1.5;
        uniform vec4 uRibbonA[RIBBONS];
        uniform vec4 uRibbonB[RIBBONS];
        uniform vec4 uRibbonC[RIBBONS];

        vec3 ribbonColour(float m)
        {
            return mix(mix(uTint, uGlow, clamp(m, 0.0, 1.0)), uShade, clamp(m - 1.0, 0.0, 1.0));
        }

        vec3 styleRoom(vec2 p)
        {
            float u = p.x * uResolution.y / uResolution.x + 0.5;
            float v = 0.5 - p.y;
            float field = 0.0;
            vec3 wash = vec3(0.0);
            for (int i = 0; i < RIBBONS; i++)
            {
                vec4 a = uRibbonA[i];
                vec4 b = uRibbonB[i];
                vec4 c = uRibbonC[i];
                float curve = a.x + a.y * u + a.z * sin(a.w * u + b.x) + 0.35 * a.z * sin(a.w * 2.3 * u + b.y);
                float w = max(b.z * (1.0 + b.w * sin(2.8274 * u + c.x)), 0.04);
                float d = (v - curve) / w;
                float d2 = d * d;
                float g = (exp(-d2) + c.z * exp(-25.0 * d2)) * c.y;
                field += g;
                wash += ribbonColour(c.w) * g;
            }
            wash /= max(field, 1e-4);
            field *= 1.0 - 0.20 * (u * 0.65 + v * 0.35);
            float n = max((field - 0.046) / 2.13, 0.0);
            float lv = pow((1.0 - exp(-1.05 * n)) / 0.661, 2.35);
            if (lv > 0.9) lv = 0.9 + 0.1 * (1.0 - exp(-(lv - 0.9) / 0.1));

            // The script's colour ramp, as overlapping steps: each one starts before the last has ended,
            // so the layers of light melt into one another instead of meeting at a line.
            vec3 col = uGround * 0.75;
            col = mix(col, uGround * 0.9 + wash * 0.12, smoothstep(0.02, 0.55, lv));
            col = mix(col, wash * 0.39, smoothstep(0.30, 0.78, lv));
            col = mix(col, wash * 0.66, smoothstep(0.52, 0.94, lv));
            col = mix(col, wash * 0.95, smoothstep(0.70, 1.02, lv));
            col = mix(col, uGlow * 0.90, smoothstep(0.88, 1.16, lv));
            return col * LIFT;
        }
        """;
}

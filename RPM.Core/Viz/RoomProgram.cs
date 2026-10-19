namespace RPM.Core.Viz;

/// <summary>
/// The phone's whole room, as one SkSL program: the uniforms, the shared figures, and this head's own
/// reading of them.
///
/// <para>The figures are <see cref="RoomSource"/>, which the desktop drops into its GL program whole.
/// Everything around them here is the composition, not the room: where the light is held back, and how
/// the corners fall away. The two heads may differ about that. The desktop writes down the left of a
/// landscape window and dims that side. A phone writes along the foot of a portrait window and dims
/// that.</para>
///
/// <para>The text is kept here, rather than beside the surface that draws it, for one reason. A shader
/// that will not compile is invisible until someone runs the app on a phone. Here it can be checked
/// without one.</para>
/// </summary>
public static class RoomProgram
{
    /// <summary>
    /// The uniforms the drawing side has to fill.
    ///
    /// <list type="bullet">
    /// <item><c>uResolution</c>: the surface, in points.</item>
    /// <item><c>uTime</c>: seconds since the room came up.</item>
    /// <item><c>uBass</c>, <c>uMid</c>, <c>uAir</c>: the three envelopes, from <see
    /// cref="RoomBands"/>.</item>
    /// <item><c>uGround</c>, <c>uTint</c>, <c>uGlow</c>, <c>uShade</c>: the record's own colours, from
    /// <see cref="RoomPalette"/>.</item>
    /// <item><c>uFoot</c>: where the words start, as a fraction of the height from the top. The room is
    /// held back below it. One means nothing is written anywhere, and the whole room is lit.</item>
    /// </list>
    /// </summary>
    public static string Sksl { get; } = $$"""
        uniform float2 uResolution;
        uniform float  uTime;
        uniform float  uBass;
        uniform float  uMid;
        uniform float  uAir;
        uniform float3 uGround;
        uniform float3 uTint;
        uniform float3 uGlow;
        uniform float3 uShade;
        uniform float  uFoot;

        {{SkslSource.Translate(RoomSource.Glsl)}}

        half4 main(float2 coord)
        {
            // Skia counts y down from the top and the room was written counting up from the bottom.
            // Flipped once here rather than in the shared figures, which are neither head's to bend.
            float2 flipped = float2(coord.x, uResolution.y - coord.y);
            float2 uv = flipped / uResolution;
            float2 p = (flipped - 0.5 * uResolution) / uResolution.y;

            float3 col = rpmRoom(p, uTime, uBass, uMid, uAir, uGround, uTint, uGlow, uShade);

            // The figures are the room, not the subject, and everything that matters is written over
            // them in white. So the whole wash is taken down, and taken down further along the FOOT,
            // where the title, the seek bar and the keys are. Dimmed rather than veiled: a grey panel
            // laid over the top would kill the colour, where this simply moves that part of the room
            // away from the light.
            col *= 0.62;
            col *= mix(0.42, 1.0, smoothstep(uFoot - 0.24, uFoot, uv.y));

            // The corners fall away, so the middle of the screen holds the eye.
            float vig = pow(16.0 * uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y), 0.20);
            col *= 0.42 + 0.58 * vig;

            // A grain of dither. Eight bits across a gradient this wide and this soft bands visibly, and
            // the bands crawl as the figures drift, which is far more distracting than the noise.
            float grain = fract(sin(dot(coord, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
            col += grain * (1.6 / 255.0);

            return half4(half3(max(col, float3(0.0))), 1.0);
        }
        """;
}

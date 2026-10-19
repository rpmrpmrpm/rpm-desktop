using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Core.Viz;

/// <summary>
/// Turns shader source written in GLSL into the SkSL Skia's runtime effects are written in.
///
/// <para> The desktop draws its visualisers with real OpenGL through <c>GLCanvasElement</c>. Uno supports
/// that only on WinAppSDK and Skia Desktop. There is no iOS build of it. iOS does have
/// <c>SKCanvasElement</c>, which draws on the same Skia surface that the window is composited with. Skia
/// can run a runtime shader written in SkSL. So a piece of shader source that both heads share is written
/// once in GLSL. Two different compilers then build it. This class is the piece in between.
/// </para>
///
/// <para> SkSL has the same shape as GLSL with different spelling. Only the spelling is handled here:
/// <c>vec2/3/4</c> become <c>float2/3/4</c>, and matrices become <c>floatNxN</c>. The shape is not
/// handled here. SkSL has one entry point, <c>half4 main(float2)</c>, which RETURNS its colour. Its
/// origin is the top left, where GLSL's is the bottom left. Each head writes that entry point itself,
/// around the shared figures, because the composition belongs to the head (see <see
/// cref="RoomProgram"/>).
/// </para>
///
/// <para> This class is kept in Core and is pure, so the rewriting can be tested without a surface. This
/// is the same reason that <see cref="RPM.Core.Audio.ShaderAudioTexture"/> is here.
/// </para>
/// </summary>
public static class SkslSource
{
    /// <summary>GLSL's type names, mapped to SkSL's. The match is on whole words, so an identifier that
    /// only contains one of these names is left alone.</summary>
    private static readonly (string From, string To)[] Types =
    [
        ("vec2", "float2"), ("vec3", "float3"), ("vec4", "float4"),
        ("ivec2", "int2"), ("ivec3", "int3"), ("ivec4", "int4"),
        ("bvec2", "bool2"), ("bvec3", "bool3"), ("bvec4", "bool4"),
        ("mat2", "float2x2"), ("mat3", "float3x3"), ("mat4", "float4x4"),
    ];

    /// <summary>
    /// Turns GLSL spelling into SkSL spelling, and nothing else. No entry point is found, no uniforms are
    /// added, and no coordinate is flipped. This is for shared shader source that is not a whole program.
    /// <see cref="RoomSource"/> is one example. Both heads compile it, and each one wraps it in an entry
    /// point of its own.
    /// </summary>
    public static string Translate(string glsl)
    {
        ArgumentNullException.ThrowIfNull(glsl);

        var source = Ascii(glsl);

        // Replace ivec2 before vec2. Otherwise the longer name is rewritten as "i" plus the replacement
        // for vec2.
        foreach (var (from, to) in Types.OrderByDescending(t => t.From.Length))
            source = Regex.Replace(source, $@"\b{from}\b", to);

        return source;
    }

    /// <summary>
    /// Drops everything above ASCII. A dash or any other non-ASCII character in a comment can make a
    /// shader compiler report a syntax error on a line that is correct. The rule is cheap to keep, so
    /// it stays.
    /// </summary>
    public static string Ascii(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clean = new StringBuilder(source.Length);
        foreach (var c in source)
            clean.Append(c <= 0x7F ? c : ' ');

        return clean.ToString();
    }
}

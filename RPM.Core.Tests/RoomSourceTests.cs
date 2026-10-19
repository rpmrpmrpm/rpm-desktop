using System.Globalization;
using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The room's own figures, which two compilers have to accept: real GLSL on the desktop's OpenGL, and
/// SkSL on the phone's Skia. It is one picture only while it is one text, and these tests cover the
/// two ways that text can quietly stop being valid for one of them.
/// </summary>
public class RoomSourceTests
{
    [Fact]
    public void The_room_declares_a_lobe_and_a_room_and_nothing_else()
    {
        var glsl = RoomSource.Glsl;

        Assert.Contains("float rpmLobe(", glsl, StringComparison.Ordinal);
        Assert.Contains("vec3 rpmRoom(", glsl, StringComparison.Ordinal);

        // No entry point and no uniforms: each platform wraps this in its own, and a `main` in here
        // would collide with the one the phone writes around it.
        Assert.DoesNotContain("void main(", glsl, StringComparison.Ordinal);
        Assert.DoesNotContain("uniform ", glsl, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every type name is rewritten on the way to SkSL. One that is not rewritten gives a shader that
    /// fails to compile on the phone and nowhere else. That shows up as a blank room on a device, far
    /// from where the line was written.
    /// </summary>
    [Fact]
    public void Nothing_gl_shaped_survives_the_translation()
    {
        var sksl = SkslSource.Translate(RoomSource.Glsl);

        foreach (var spelling in new[] { "vec2", "vec3", "vec4", "mat2", "mat3", "mat4" })
            Assert.DoesNotContain(spelling, sksl, StringComparison.Ordinal);

        Assert.Contains("float3 rpmRoom(", sksl, StringComparison.Ordinal);
        Assert.Contains("float2 d = p - c;", sksl, StringComparison.Ordinal);
    }

    /// <summary>
    /// ASCII only, comments included. GLSL ES accepts nothing above ASCII, even inside a comment: one
    /// non-ASCII character such as an em dash in a line of prose fails the whole shader with a syntax
    /// error that points at an empty token.
    /// </summary>
    [Fact]
    public void The_source_is_ascii_throughout()
    {
        foreach (var c in RoomSource.Glsl)
            Assert.True(c <= 0x7F, $"non-ASCII '{c}' (U+{(int)c:X4}) in the room's source");
    }

    /// <summary>
    /// The numbers baked into it are written with a decimal POINT, whatever the machine's culture. On
    /// a Dutch, French or German machine an interpolated float would come out as <c>1,3</c>, and the
    /// shader would fail with "cannot convert from 'const int' to 'const highp float'".
    /// </summary>
    [Fact]
    public void The_numbers_are_spelled_the_way_a_compiler_reads_them()
    {
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");

            // Glsl is built once, so the culture at the moment of the build decides how the numbers
            // are written. What is asserted is the text itself: no comma may ever appear inside a
            // number.
            foreach (var line in RoomSource.Glsl.Split('\n'))
            {
                var at = line.IndexOf("//", StringComparison.Ordinal);
                var code = at >= 0 ? line[..at] : line;

                for (int i = 1; i < code.Length - 1; i++)
                    Assert.False(code[i] == ',' && char.IsDigit(code[i - 1]) && char.IsDigit(code[i + 1]),
                        $"a number written in the current culture: {code.Trim()}");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    /// <summary>
    /// The phone's program: one entry point, the figures inside it, and every uniform the drawing side
    /// writes.
    ///
    /// <para>That it COMPILES cannot be asserted here: Core does not reference SkiaSharp, and pulling
    /// in a native graphics stack to run a unit test is a poor trade. It is checked against the very
    /// SkiaSharp the app ships whenever this text is changed. What is guarded here is the shape, which
    /// is what silently drifts.</para>
    /// </summary>
    [Fact]
    public void The_phones_program_is_a_whole_shader()
    {
        var sksl = RoomProgram.Sksl;

        Assert.Contains("half4 main(float2 coord)", sksl, StringComparison.Ordinal);
        Assert.Contains("float3 rpmRoom(", sksl, StringComparison.Ordinal);

        foreach (var name in new[]
        {
            "uResolution", "uTime", "uBass", "uMid", "uAir", "uGround", "uTint", "uGlow", "uShade", "uFoot",
        })
            Assert.Matches($@"uniform\s+float[234]?\s+{name}\s*;", sksl);

        // The room is called with the phone's own plane, flipped: Skia counts y downwards and the
        // figures count upwards.
        Assert.Contains("uResolution.y - coord.y", sksl, StringComparison.Ordinal);
    }

    /// <summary>The lobe counts the source is built with are the ones its loops actually run.</summary>
    [Fact]
    public void The_lobe_counts_reach_the_source()
    {
        Assert.Contains($"const int RPM_LOBES = {RoomSource.Lobes};", RoomSource.Glsl, StringComparison.Ordinal);
        Assert.Contains($"const int RPM_LOBES2 = {RoomSource.Lobes2};", RoomSource.Glsl, StringComparison.Ordinal);
        Assert.Contains($"const int RPM_LOBES3 = {RoomSource.Lobes3};", RoomSource.Glsl, StringComparison.Ordinal);
    }
}

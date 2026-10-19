using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The GLSL-to-SkSL rewrite. Each of these tests covers a way that shared shader source silently
/// stops being the shader that was written: a type left in GLSL's spelling, a longer type name
/// rewritten as a shorter one, or a character that the compiler reports as a syntax error on a line
/// with nothing wrong with it.
/// </summary>
public class SkslSourceTests
{
    [Theory]
    [InlineData("vec2", "float2")]
    [InlineData("vec3", "float3")]
    [InlineData("vec4", "float4")]
    [InlineData("mat3", "float3x3")]
    public void Translates_glsl_type_names(string glsl, string sksl)
    {
        var source = SkslSource.Translate($"{glsl} v;");

        Assert.Contains($"{sksl} v;", source);
    }

    [Fact]
    public void Leaves_identifiers_that_merely_contain_a_type_name()
    {
        var sksl = SkslSource.Translate("float vec2Scale = 2.0;");

        Assert.Contains("float vec2Scale = 2.0;", sksl);
    }

    [Fact]
    public void Translates_the_longer_vector_types_before_the_shorter_ones()
    {
        // ivec2 rewritten as vec2-first gives "ifloat2", which compiles nowhere.
        var sksl = SkslSource.Translate("ivec2 cell = ivec2(1, 2);");

        Assert.Contains("int2 cell = int2(1, 2);", sksl);
        Assert.DoesNotContain("ifloat2", sksl);
    }

    /// <summary>The shape is the caller's: an entry point, its uniforms and the flip between the two
    /// origins are written by the platform that compiles the source, so nothing here invents
    /// them.</summary>
    [Fact]
    public void Adds_nothing_around_what_it_is_given()
    {
        var sksl = SkslSource.Translate("vec3 rpmRoom(vec2 p) { return vec3(p, 0.0); }");

        Assert.Equal("float3 rpmRoom(float2 p) { return float3(p, 0.0); }", sksl);
    }

    [Fact]
    public void Strips_characters_a_shader_compiler_chokes_on()
    {
        // A non-ASCII character in a comment is reported as a syntax error on that line, with a
        // token that is not in the source.
        var sksl = SkslSource.Translate("""
            // an em-dash — right here
            vec4 c = vec4(1.0);
            """);

        Assert.True(sksl.All(c => c <= 0x7F), "translated source still holds non-ASCII");
        Assert.Contains("float4 c = float4(1.0);", sksl);
    }
}

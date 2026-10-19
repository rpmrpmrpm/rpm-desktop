using System.Globalization;

namespace RPM.Desktop.Presentation.Viz;

/// <summary>
/// Two helpers that keep C#-written GLSL compiling. Both problems show up as a compile error with a
/// misleading message, and neither shows up on the machine where the shader was written.
/// </summary>
internal static class GlslSource
{
    /// <summary>Replaces every non-ASCII character with a space. The length stays the same, so the
    /// compiler's line and column numbers are still right.
    ///
    /// GLSL ES accepts ASCII only, inside comments too (on ANGLE). One em dash in a comment fails the
    /// whole shader with <c>ERROR: 0:82: '' : syntax error</c>, which points at an empty token on a
    /// line that looks correct. Everything written here goes through this before it is compiled.</summary>
    public static string Ascii(string source)
    {
        char[] chars = source.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] > '\x7F')
                chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>A float as GLSL spells it. Interpolating a float straight into the source formats it
    /// with the CURRENT culture. On a Dutch, French or German machine <c>0.62</c> becomes <c>0,62</c>,
    /// and the shader fails with <c>cannot convert from 'const int' to 'const highp float'</c>. This
    /// always uses the invariant culture, so the decimal point is always a dot.</summary>
    public static string Num(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}

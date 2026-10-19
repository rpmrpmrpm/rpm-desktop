using RPM.Core.Import;

namespace RPM.Core.Tests;

public class PathSanitizerTests
{
    [Theory]
    [InlineData("AC/DC", "AC DC")]
    [InlineData("What?!", "What!")]
    [InlineData("a<b>c:d\"e|f*g", "abcdefg")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("normal name", "normal name")]
    public void SanitizeSegment_strips_invalid_chars(string input, string expected)
        => Assert.Equal(expected, PathSanitizer.SanitizeSegment(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    public void SanitizeSegment_falls_back_when_nothing_survives(string? input)
        => Assert.Equal("Unknown", PathSanitizer.SanitizeSegment(input));

    [Fact]
    public void SanitizeSegment_dodges_windows_reserved_names()
        => Assert.Equal("CON_", PathSanitizer.SanitizeSegment("CON"));

    [Fact]
    public void SanitizeSegment_caps_length()
    {
        string longName = new('x', 500);
        Assert.Equal(PathSanitizer.MaxSegmentLength, PathSanitizer.SanitizeSegment(longName).Length);
    }

    [Fact]
    public void SanitizeSegment_strips_control_chars()
        => Assert.Equal("a b", PathSanitizer.SanitizeSegment("a\tb"));
}

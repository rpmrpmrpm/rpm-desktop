using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// Only http and https addresses from outside data are opened.
/// </summary>
public class WebLinkTests
{
    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/Prologue_(album)")]
    [InlineData("http://www.example.com/")]
    [InlineData("HTTPS://BAND.EXAMPLE/")]
    [InlineData("  https://band.example/tour  ")]
    public void A_web_address_is_a_link(string text)
    {
        var link = WebLink.Parse(text);

        Assert.NotNull(link);
        Assert.True(link.Uri.Scheme is "http" or "https");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("www.example.com")]
    [InlineData("/wiki/Prologue")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("spotify:track:123")]
    [InlineData("x-apple.systempreferences:com.apple.preference.security")]
    public void Anything_else_is_not(string? text)
    {
        Assert.Null(WebLink.Parse(text));
    }
}

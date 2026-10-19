using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>The sign-in dialog's "Forgot password?" opens the reset page with whatever address the
/// person had typed, so they do not type it twice.</summary>
public class RpmSiteTests
{
    [Fact]
    public void A_typed_address_is_filled_in()
    {
        Assert.Equal("https://rpmrpmrpm.com/forgot-password?email=someone%40example.com",
            RpmSite.ForgotPasswordFor("someone@example.com"));
    }

    [Fact]
    public void The_address_is_trimmed_and_escaped()
    {
        Assert.Equal("https://rpmrpmrpm.com/forgot-password?email=a%2Bb%26c%40example.com",
            RpmSite.ForgotPasswordFor("  a+b&c@example.com "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    public void Anything_else_opens_the_empty_form(string? typed)
    {
        Assert.Equal(RpmSite.ForgotPassword, RpmSite.ForgotPasswordFor(typed));
    }
}

using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// Where a client may be pointed, and what comes with each answer.
///
/// <para>The rule under test: a client pointed at this app's API carries the app key. The type
/// enforces this, because a rule written only in a comment is easy to miss when a new client is
/// built.</para>
/// </summary>
public class DestinationTests
{
    private static HttpClient Fresh() => new(new StubHandler());

    [Fact]
    public void The_api_carries_the_key()
    {
        using var client = Fresh();
        Assert.NotNull(RpmApi.At("https://api.rpmrpmrpm.com"));
        RpmApi.At("https://api.rpmrpmrpm.com")!.Value.Aim(client);

        Assert.Equal(new Uri("https://api.rpmrpmrpm.com/"), client.BaseAddress);
        Assert.Equal(RpmApi.Key, Assert.Single(client.DefaultRequestHeaders.GetValues(RpmApi.KeyHeader)));
    }

    /// <summary>The purpose of the type. The address can be read from configuration, so a string
    /// holding it can be passed anywhere. What must not happen is that string becoming a client
    /// with no key on it.</summary>
    [Fact]
    public void The_api_cannot_be_reached_as_an_ordinary_service()
    {
        var refused = Assert.Throws<ArgumentException>(
            () => Destination.Service("https://api.rpmrpmrpm.com"));
        Assert.Contains("RpmApi.At", refused.Message);

        // The path does not decide which service it is, so a deeper address is refused too.
        Assert.Throws<ArgumentException>(() => Destination.Service("https://api.rpmrpmrpm.com/artwork"));
    }

    /// <summary>Another host on the same domain is a different service and is not refused: the
    /// accounts service is reached this way, and is not behind the app key.</summary>
    [Fact]
    public void A_neighbouring_service_is_not_the_api()
    {
        using var client = Fresh();
        Destination.Service("https://accounts.rpmrpmrpm.com/").Aim(client);

        Assert.Equal(new Uri("https://accounts.rpmrpmrpm.com/"), client.BaseAddress);
        Assert.False(client.DefaultRequestHeaders.Contains(RpmApi.KeyHeader));
    }

    /// <summary>Unaddressed leaves the client without a base address: every request names its own
    /// absolute URL, and a base address would be read as a prefix for them.</summary>
    [Fact]
    public void Unaddressed_points_nowhere_and_carries_nothing()
    {
        using var client = Fresh();
        Destination.Unaddressed.Aim(client);

        Assert.Null(client.BaseAddress);
        Assert.False(client.DefaultRequestHeaders.Contains(RpmApi.KeyHeader));
    }

    /// <summary>A build with nothing configured has no API, which is a different answer from an API
    /// that cannot be reached: the caller shows "no account service" and does not retry.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_configured_is_no_destination_at_all(string? configured)
    {
        // RPM_API_URL overrides the configured address, so the question is only meaningful when the
        // override is absent. It is not set in a test run, and is not set here either.
        Assert.Null(Environment.GetEnvironmentVariable("RPM_API_URL"));
        Assert.Null(RpmApi.At(configured));
        Assert.False(RpmApi.IsConfigured(configured));
    }

    [Fact]
    public void The_deployed_api_is_a_destination_without_being_configured()
    {
        using var client = Fresh();
        RpmApi.Deployed.Aim(client);

        Assert.Equal(new Uri("https://api.rpmrpmrpm.com/"), client.BaseAddress);
        Assert.Equal(RpmApi.Key, Assert.Single(client.DefaultRequestHeaders.GetValues(RpmApi.KeyHeader)));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new NotSupportedException("Nothing here sends anything.");
    }
}

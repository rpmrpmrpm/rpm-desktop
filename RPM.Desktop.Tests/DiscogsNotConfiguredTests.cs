using System.Net;
using RPM.Desktop.Services.Catalog;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>Only the API's own "not configured" answer turns Discogs off for the session. A 503 from
/// the offline gate, or from a Lambda that is briefly failing, is a failed request and does not count.
/// See <see cref="DiscogsService.NotConfigured"/>.</summary>
public class DiscogsNotConfiguredTests
{
    private const string ApiSaysSo = "{\"error\":\"Discogs is not configured on this deployment\"}";

    [Fact]
    public void The_apis_own_answer_is_not_configured()
        => Assert.True(DiscogsService.NotConfigured(HttpStatusCode.ServiceUnavailable, refusedByGate: false, ApiSaysSo));

    [Fact]
    public void The_offline_gate_is_not_an_answer()
        => Assert.False(DiscogsService.NotConfigured(HttpStatusCode.ServiceUnavailable, refusedByGate: true, ""));

    [Theory]
    [InlineData("")]
    [InlineData("{\"message\":\"Service Unavailable\"}")]
    public void A_bare_503_is_not_an_answer(string body)
        => Assert.False(DiscogsService.NotConfigured(HttpStatusCode.ServiceUnavailable, refusedByGate: false, body));

    [Fact]
    public void Another_status_is_not_an_answer_about_the_deployment()
        => Assert.False(DiscogsService.NotConfigured(HttpStatusCode.BadGateway, refusedByGate: false, ApiSaysSo));
}

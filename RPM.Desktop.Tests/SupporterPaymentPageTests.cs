using System.Net;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// The address the app opens to pay for supporting rpm. The accounts service gives a Stripe page for
/// the signed-in account; anything else falls back to the website's supporter page, which still leads
/// to a way to pay. An answer that is not a secure address is never opened.
/// </summary>
public class SupporterPaymentPageTests
{
    private const string Checkout = "https://checkout.stripe.com/c/pay/cs_live_1";

    [Fact]
    public void An_answer_with_a_secure_address_is_that_address()
    {
        var page = SupporterService.PaymentPage.Read(HttpStatusCode.OK, $$"""{"url":"{{Checkout}}","alreadyPaying":false}""");

        Assert.Equal(new SupporterService.PaymentPage.At(Checkout), page);
    }

    [Fact]
    public void Not_found_means_the_account_has_never_paid_and_is_not_a_failure()
    {
        Assert.IsType<SupporterService.PaymentPage.NoPayments>(
            SupporterService.PaymentPage.Read(HttpStatusCode.NotFound, """{"error":"This account has no payments."}"""));
    }

    [Fact]
    public void A_service_that_fails_is_a_failure()
    {
        Assert.IsType<SupporterService.PaymentPage.Failed>(
            SupporterService.PaymentPage.Read(HttpStatusCode.ServiceUnavailable, """{"error":"Payments are not available."}"""));
    }

    [Theory]
    [InlineData("""{"url":"http://checkout.stripe.com/c/pay/cs_1"}""")]
    [InlineData("""{"url":"file:///etc/passwd"}""")]
    [InlineData("""{"url":42}""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    public void An_answer_without_a_secure_address_is_a_failure(string body)
    {
        Assert.IsType<SupporterService.PaymentPage.Failed>(SupporterService.PaymentPage.Read(HttpStatusCode.OK, body));
    }

    [Fact]
    public void A_checkout_address_is_opened()
    {
        Assert.Equal(Checkout, SupporterLinks.WhereToPay(new SupporterService.PaymentPage.At(Checkout)));
    }

    [Fact]
    public void Without_a_checkout_the_website_supporter_page_is_opened()
    {
        Assert.Equal(SupporterLinks.BecomeSupporterUrl, SupporterLinks.WhereToPay(new SupporterService.PaymentPage.Failed()));
        Assert.Equal(SupporterLinks.BecomeSupporterUrl, SupporterLinks.WhereToPay(new SupporterService.PaymentPage.NoPayments()));
    }
}

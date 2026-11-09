using Microsoft.Extensions.DependencyInjection;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// Where people go to support rpm, and how the desktop app opens that page.
///
/// <para>It is offered from four places in the app: the recognition window, the cloud settings page,
/// the storage-full window and the last step of the setup. All four open it through
/// <see cref="OpenBecomeSupporterAsync"/>. The website's address is <see cref="RPM.Core.Cloud.RpmSite"/>,
/// because the phone app offers the same link and the two must stay the same.</para>
/// </summary>
public static class SupporterLinks
{
    /// <summary>The app's own page.</summary>
    public const string BecomeSupporterUrl = RPM.Core.Cloud.RpmSite.BecomeSupporter;

    /// <summary>
    /// Opens a place to pay in the user's browser. For a signed-in account that is a Stripe Checkout
    /// for that account, so the payment reaches it. Signed out, or when no checkout could be had, it
    /// is the website's supporter page, which leads to the web app and the same checkout. Best effort:
    /// nothing here throws out of a click handler.
    /// </summary>
    public static async Task OpenBecomeSupporterAsync()
    {
        var supporter = App.Services.GetRequiredService<SupporterService>();
        Open(WhereToPay(await supporter.CheckoutAsync()));
    }

    /// <summary>The address to open for an answer from the accounts service.</summary>
    public static string WhereToPay(SupporterService.PaymentPage page) =>
        page is SupporterService.PaymentPage.At at ? at.Url : BecomeSupporterUrl;

    /// <summary>Opens a Stripe page in the user's browser. Best effort, like the supporter page.</summary>
    public static void OpenPage(SupporterService.PaymentPage.At page) => Open(page.Url);

    private static void Open(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Nothing to say and nothing to do. The address is also shown as text on the settings page.
        }
    }
}

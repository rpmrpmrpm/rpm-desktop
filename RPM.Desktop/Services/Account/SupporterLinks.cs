namespace RPM.Desktop.Services.Account;

/// <summary>
/// Where people go to support rpm, and how the desktop app opens that page.
///
/// <para>It is offered from three separate places in the app: the recognition window, the cloud
/// settings page and the storage-full window. If the page moved, it would have to be changed in all
/// three. The address itself is <see cref="RPM.Core.Cloud.RpmSite"/>, because the phone app offers the
/// same link and the two must stay the same. This class only holds the desktop's way of opening it.</para>
/// </summary>
public static class SupporterLinks
{
    /// <summary>The app's own page.</summary>
    public const string BecomeSupporterUrl = RPM.Core.Cloud.RpmSite.BecomeSupporter;

    /// <summary>Opens the page in the user's browser. Best effort: a machine with no browser
    /// association must not throw out of a click handler.</summary>
    public static void OpenBecomeSupporter() => Open(BecomeSupporterUrl);

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

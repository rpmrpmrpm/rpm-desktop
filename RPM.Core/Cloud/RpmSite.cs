namespace RPM.Core.Cloud;

/// <summary>
/// The app's own pages on the web.
///
/// <para>These are defined here, not in either head, because the desktop and the phone both link to
/// the same pages and must not point at different ones. The desktop links them from the recognition
/// window, the cloud settings page and the storage-full window. The phone links them from the profile
/// tab. If a page moved, it would otherwise have to be changed in two projects.</para>
/// </summary>
public static class RpmSite
{
    /// <summary>The site itself.</summary>
    public const string Home = "https://rpmrpmrpm.com";

    /// <summary>Where a listener is sent for help. The shell menu and the About screen both link to it,
    /// so both doors lead to one page.</summary>
    public const string Support = "https://rpmrpmrpm.com/support";

    /// <summary>What the app does with the data it is given. Linked from the same two places.</summary>
    public const string Privacy = "https://rpmrpmrpm.com/privacy";

    /// <summary>The terms of use. They include the licence for the apps. Linked from the same two places
    /// as the privacy page.</summary>
    public const string Terms = "https://rpmrpmrpm.com/terms";

    /// <summary>The LGPL section of the credits. It gives the source of each LGPL component that rpm
    /// ships, the ffmpeg build script, and the written offer. The About screen links it. The LGPL
    /// requires the offer to reach anyone who has the app, not only people who find the site.</summary>
    public const string LgplSource = "https://rpmrpmrpm.com/credits#lgpl";

    /// <summary>Where rpm can be supported. What is sold there, and what the account gets in return, is
    /// decided by the accounts service. This constant is only the link.</summary>
    public const string BecomeSupporter = "https://rpmrpmrpm.com/become-supporter";

    /// <summary>The password reset page for an email account. The page emails a link where the person
    /// chooses a new password (see rpm.Api's PasswordResetController). The desktop sign-in dialog links
    /// to it.</summary>
    public const string ForgotPassword = "https://rpmrpmrpm.com/forgot-password";

    /// <summary><see cref="ForgotPassword"/> with the address filled in, when the person typed one
    /// before asking. Anything that is not an address opens the empty form.</summary>
    public static string ForgotPasswordFor(string? email)
    {
        var typed = email?.Trim();
        return string.IsNullOrEmpty(typed) || !typed.Contains('@')
            ? ForgotPassword
            : $"{ForgotPassword}?email={Uri.EscapeDataString(typed)}";
    }
}

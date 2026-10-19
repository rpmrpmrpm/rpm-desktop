namespace RPM.Core.Net;

/// <summary>
/// A web address that may be handed to the system to open: absolute, and http or https.
///
/// <para>Links that come from outside data (an artist's site from a catalogue, a Wikipedia article's
/// address, a cover's page from the cover search) are checked with <see cref="Parse"/> before they are
/// opened. The system opener runs any scheme it knows, so an address with <c>file:</c>, <c>ms-settings:</c>
/// or another app's own scheme in a catalogue entry would open a file or start that app.</para>
/// </summary>
public sealed class WebLink
{
    public Uri Uri { get; }

    private WebLink(Uri uri) => Uri = uri;

    /// <summary>The address as a link, or null when it is empty, relative, or not http or https.</summary>
    public static WebLink? Parse(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && uri.Host.Length > 0
            ? new WebLink(uri)
            : null;
}

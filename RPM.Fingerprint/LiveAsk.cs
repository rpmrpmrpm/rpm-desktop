using System.Globalization;

namespace RPM.Fingerprint;

/// <summary>
/// A parsed request to the transcoder's own URL.
///
/// <para>
/// This is the function's whole input. The function is behind an open function URL, it receives no
/// account and no session, and it calls no other service: the API decided what may be served when it
/// wrote the ticket, and the ticket is the only authorisation. So every kind of invalid request is
/// handled here, once, and the result is a blob, a rate and a start time in seconds.
/// </para>
///
/// <para>
/// <b>Seconds, not a byte range.</b> A seek arrives as <c>?from=</c> because the encoder can start at
/// any time. A byte offset could be converted to a time only by assuming a constant bit rate, which
/// AAC does not have: the error would be a second or two on a four-minute track, which a listener
/// notices.
/// </para>
/// </summary>
public sealed record LiveAsk(StreamTicket Ticket, double From)
{
    private const string Prefix = "/live/";

    /// <summary>
    /// What this request asks for, or null: a path with no ticket, a ticket that does not verify, or
    /// a ticket that has expired. All of these return null, because they all get the same reply.
    /// </summary>
    /// <param name="rawPath">The request path, as the function URL event gives it.</param>
    /// <param name="from">The <c>from</c> query value, or null. Any value that is not a positive
    /// number of seconds is read as 0 (the start): it comes from the network and becomes an ffmpeg
    /// argument, where a negative value seeks backwards from the end.</param>
    public static LiveAsk? Read(string? rawPath, string? from, string secret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (rawPath is null) return null;

        var at = rawPath.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0) return null;

        // The ticket, and nothing after it. A function URL and CloudFront treat a trailing slash
        // differently. Without the trim, every request through CloudFront would be refused while
        // every direct request worked.
        var token = rawPath[(at + Prefix.Length)..].Trim('/');
        if (token.Length == 0) return null;

        var ticket = StreamTicket.Read(token, secret, now);
        if (ticket is null) return null;

        return new LiveAsk(ticket, Seconds(from));
    }

    private static double Seconds(string? from)
    {
        if (!double.TryParse(from, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return 0;

        // Not a valid position: infinity, NaN, and a negative number that ffmpeg would read as an
        // offset from the end.
        return double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
    }
}

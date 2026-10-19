using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RPM.Fingerprint;

/// <summary>
/// A signed ticket that allows one blob to be transcoded and served at one rate, for the next few
/// minutes.
///
/// <para>
/// A Lambda function URL offers two kinds of authentication and neither fits: SigV4 signing, which a
/// phone has no credentials for, or no authentication at all. So the URL is open, and this ticket
/// protects it. The API creates it after checking who the caller is, what they may be served and
/// whose library the row is in. The transcoder reads it and checks none of those things itself.
/// </para>
///
/// <para>
/// <b>The rate is inside the signature, not next to it.</b> It is the rate the encoder produces and
/// the rate the account was checked for, so if a listener could change it, anyone could get a
/// supporter tier for free. The hash is signed for the same reason: it names the object, and a
/// ticket without it would be valid for any record in the store.
/// </para>
///
/// <para>
/// Defined here, next to <see cref="CloudLayout"/>, because the two functions that must agree on it
/// exactly cannot share a larger assembly: the API must not depend on <c>RPM.Core</c> (it would make
/// cold starts slower), and the transcoder must not depend on the API.
/// </para>
/// </summary>
public sealed record StreamTicket(string Sha256, int Kilobits)
{
    /// <summary>The format version of the ticket. Tickets expire quickly, so a change here needs no
    /// migration (the old ones expire), but a reader that could not tell versions apart would verify
    /// the wrong bytes and refuse everything.</summary>
    private const string Version = "v1";

    /// <summary>
    /// Creates a ticket. Throws for a rate that is not produced: <c>rate/160/...</c> is a path that
    /// nothing writes to or reads from. It is refused here, so the error points to the code that
    /// chose the number.
    /// </summary>
    /// <param name="secret">Shared by the API and the transcoder, and by nothing else.</param>
    public static string Mint(string sha256, int kilobits, DateTimeOffset expires, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // The head clip is not served this way (it is three seconds and is sent as bytes), and the
        // original is not transcoded at all, so neither is valid on a ticket that means "run an
        // encoder for this".
        if (!AudioLadder.IsOnTheLadder(kilobits))
            throw new ArgumentOutOfRangeException(nameof(kilobits), kilobits, "not a rate on the ladder");

        var body = Body(sha256, kilobits, expires.ToUnixTimeSeconds());

        return $"{body}.{Sign(body, secret)}";
    }

    /// <summary>
    /// Reads a ticket, or returns null.
    ///
    /// <para>Every kind of invalid ticket returns null, and the transcoder gives the same reply for
    /// all of them: an expired ticket, a ticket signed with an old secret, a rate changed by hand,
    /// and a string that is not a ticket. Different replies would tell an attacker which part of a
    /// forged ticket was correct.</para>
    /// </summary>
    /// <param name="now">Required, and must be the caller's clock: with a default, tests could not
    /// reach the expiry, so the expiry would never be tested.</param>
    public static StreamTicket? Read(string? token, string secret, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != Version) return null;

        var sha = parts[1];
        if (!IsHash(sha)) return null;

        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kilobits))
            return null;
        if (!AudioLadder.IsOnTheLadder(kilobits)) return null;

        if (!long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var expires))
            return null;

        // Check the signature before the clock, so that a forged ticket is refused as forged, not
        // as expired.
        var body = Body(sha, kilobits, expires);
        if (!Matches(parts[4], Sign(body, secret))) return null;

        if (now.ToUnixTimeSeconds() >= expires) return null;

        return new StreamTicket(sha, kilobits);
    }

    private static string Body(string sha256, int kilobits, long expires) =>
        string.Create(CultureInfo.InvariantCulture, $"{Version}.{sha256}.{kilobits}.{expires}");

    /// <summary>Base64url, so the whole ticket can go in a query string or a path segment without
    /// escaping. An escaped ticket can be unescaped somewhere else by code that does not know it is a
    /// signature.</summary>
    private static string Sign(string body, string secret) =>
        Base64Url.EncodeToString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));

    /// <summary>Fixed-time, because a comparison that returns as soon as two bytes differ lets a
    /// caller measure how much of a guess was correct.</summary>
    private static bool Matches(string given, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private static bool IsHash(string s)
    {
        if (s.Length != 64) return false;

        foreach (var c in s)
            if (!char.IsAsciiHexDigitLower(c)) return false;

        return true;
    }
}

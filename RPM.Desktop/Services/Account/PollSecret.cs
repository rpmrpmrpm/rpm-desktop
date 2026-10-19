using System.Security.Cryptography;
using System.Text;

namespace RPM.Desktop.Services.Account;

/// <summary>
/// The secret that ties a social sign-in to this app. The app sends only the hash when it starts the
/// sign-in, and the secret itself when it polls, so someone who has the login URL (it holds the
/// session id) cannot collect the tokens. The server side is <c>PollSecret</c> in rpm.Api; the iOS
/// and Android apps make the same hash. Change all of them together.
/// </summary>
internal static class PollSecret
{
    /// <summary>The header the secret is sent in when polling.</summary>
    public const string Header = "X-Poll-Secret";

    /// <summary>A new random secret: 32 bytes, base64url without padding.</summary>
    public static string Make() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The hash sent for <paramref name="secret"/>: the base64url SHA-256 of its UTF-8
    /// bytes, without padding.</summary>
    public static string HashOf(string secret) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

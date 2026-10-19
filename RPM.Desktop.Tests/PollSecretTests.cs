using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// The poll secret sent with a social sign-in. The hash must be the one the server computes
/// (rpm.Api <c>PollSecret.HashOf</c>), or the server refuses this app's own poll.
/// </summary>
public class PollSecretTests
{
    [Fact]
    public void The_hash_is_the_unpadded_base64url_sha256_of_the_secret() =>
        // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        Assert.Equal("ungWv48Bz-pBQUDeXa4iI7ADYaOWF3qctBD_YfIAFa0", PollSecret.HashOf("abc"));

    [Fact]
    public void A_secret_is_43_base64url_characters_and_new_each_time()
    {
        var a = PollSecret.Make();
        var b = PollSecret.Make();

        Assert.Equal(43, a.Length);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_hash_has_the_shape_the_server_accepts() =>
        Assert.Matches("^[A-Za-z0-9_-]{43}$", PollSecret.HashOf(PollSecret.Make()));
}

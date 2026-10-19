using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The six digits two people compare before they share anything.
///
/// <para>The number is only useful because both machines compute it and neither chooses it. So these
/// tests check that both sides agree without a shared order (either side may be "A"), and that they
/// disagree as soon as any of the four inputs differs from what the other side used, which is exactly
/// what a machine in the middle swapping a key would cause.</para>
/// </summary>
public class PairingCodeTests
{
    private static readonly byte[] KeyA = [1, 2, 3, 4];
    private static readonly byte[] KeyB = [9, 8, 7, 6];
    private static readonly byte[] NonceA = [10, 11, 12];
    private static readonly byte[] NonceB = [20, 21, 22];

    [Fact]
    public void Both_ends_get_the_same_number_whichever_of_them_asks()
    {
        var mine = PairingCode.For(KeyA, KeyB, NonceA, NonceB);
        var theirs = PairingCode.For(KeyB, KeyA, NonceB, NonceA);

        Assert.Equal(mine, theirs);
    }

    [Fact]
    public void It_is_six_digits_leading_zeros_and_all()
    {
        var code = PairingCode.For(KeyA, KeyB, NonceA, NonceB);

        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
    }

    [Fact]
    public void A_swapped_key_shows_a_different_number_on_the_two_screens()
    {
        byte[] impostor = [5, 5, 5, 5];

        Assert.NotEqual(
            PairingCode.For(KeyA, KeyB, NonceA, NonceB),
            PairingCode.For(KeyA, impostor, NonceA, NonceB));
    }

    [Fact]
    public void A_nonce_paired_with_the_wrong_key_does_not_agree()
    {
        Assert.NotEqual(
            PairingCode.For(KeyA, KeyB, NonceA, NonceB),
            PairingCode.For(KeyA, KeyB, NonceB, NonceA));
    }

    [Fact]
    public void Todays_number_is_not_tomorrows()
    {
        var first = PairingCode.For(KeyA, KeyB, PairingCode.Nonce(), PairingCode.Nonce());
        var second = PairingCode.For(KeyA, KeyB, PairingCode.Nonce(), PairingCode.Nonce());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_nonce_is_long_enough_to_be_used_once()
    {
        Assert.Equal(PairingCode.NonceBytes, PairingCode.Nonce().Length);
        Assert.NotEqual(PairingCode.Nonce(), PairingCode.Nonce());
    }

    [Fact]
    public void Exactly_one_of_two_machines_asking_at_once_carries_on()
    {
        // Both ends run the same comparison over the same two ids, so whatever the answer is, they
        // agree on it. That is the only thing that matters.
        Assert.True(PairingCode.AsksFirst("AAA", "BBB"));
        Assert.False(PairingCode.AsksFirst("BBB", "AAA"));

        Assert.NotEqual(PairingCode.AsksFirst("AAA", "BBB"), PairingCode.AsksFirst("BBB", "AAA"));
    }

    [Fact]
    public void A_machine_never_beats_itself()
    {
        // This does not happen (a machine does not pair with itself), but a rule that said yes to
        // both sides here would have a gap in it.
        Assert.False(PairingCode.AsksFirst("SAME", "SAME"));
    }

    [Fact]
    public void Codes_match_only_when_they_are_the_same_code()
    {
        Assert.True(PairingCode.Same("004213", "004213"));
        Assert.False(PairingCode.Same("004213", "004214"));
        Assert.False(PairingCode.Same("004213", "04213"));
        Assert.False(PairingCode.Same(null, "004213"));
        Assert.False(PairingCode.Same("004213", null));
    }
}

using System.Security.Cryptography;

namespace RPM.Core.Sharing;

/// <summary>
/// The six digits one machine shows and the other has typed into it.
///
/// <para>Computed from both keys and both nonces. The digits are shown on the machine that was asked
/// and typed on the machine that asked. The asked machine reveals its nonce only after the asking
/// machine has committed to what was typed. So the asking machine cannot compute the digits before
/// someone has read them off the other screen. If the nonce were sent up front, any machine on the
/// network could pair without anybody looking.</para>
///
/// <para>Both machines check the digits, one check per round. The order of the exchange is what makes
/// both checks worth something. See <see cref="PairingSecrets"/> for the asked machine's half and
/// <see cref="PeerClient.PairingAsker"/> for the asking machine's.</para>
///
/// <para>The nonces stop a number from one pairing being replayed in a later one. Each side makes its
/// own nonce, and neither side can choose the result alone. The keys are sorted before hashing, so
/// both ends hash the same bytes without having to agree which key is which.</para>
/// </summary>
public static class PairingCode
{
    /// <summary>How many random bytes each side contributes. Sixteen is enough for a value that is
    /// used once and never stored.</summary>
    public const int NonceBytes = 16;

    /// <summary>A nonce for this pairing. One per attempt, never reused.</summary>
    public static byte[] Nonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    /// <summary>
    /// The number both screens must show. Six digits, leading zeros kept. "004213" is a code, not
    /// the number 4213.
    /// </summary>
    /// <param name="publicKeyA">One side's public key.</param>
    /// <param name="publicKeyB">The other side's.</param>
    /// <param name="nonceA">The nonce that belongs with <paramref name="publicKeyA"/>.</param>
    /// <param name="nonceB">The nonce that belongs with <paramref name="publicKeyB"/>.</param>
    public static string For(byte[] publicKeyA, byte[] publicKeyB, byte[] nonceA, byte[] nonceB)
    {
        ArgumentNullException.ThrowIfNull(publicKeyA);
        ArgumentNullException.ThrowIfNull(publicKeyB);
        ArgumentNullException.ThrowIfNull(nonceA);
        ArgumentNullException.ThrowIfNull(nonceB);

        // Sorted by key. Each nonce stays with the key it came with. Sorting the two lists separately
        // would let the two ends pair a key with the wrong nonce and disagree, for a reason that has
        // nothing to do with an attacker.
        var (first, second) = Compare(publicKeyA, publicKeyB) <= 0
            ? ((publicKeyA, nonceA), (publicKeyB, nonceB))
            : ((publicKeyB, nonceB), (publicKeyA, nonceA));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(first.Item1);
        hash.AppendData(first.Item2);
        hash.AppendData(second.Item1);
        hash.AppendData(second.Item2);

        var digest = hash.GetHashAndReset();

        // Twenty bits from the front of the hash, folded into the six-digit range. A person reads six
        // digits off one screen and checks them on another, so the code must be short. Twenty bits
        // is more than a million values, so the fold covers the whole range.
        int value = ((digest[0] << 16) | (digest[1] << 8) | digest[2]) & 0xFFFFF;
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Which of two machines asks, when both asked at once.
    ///
    /// <para>
    /// Two people pressing the button at the same moment is a normal case. It happens when someone
    /// sets up two computers standing side by side. Without a rule, the two requests cross. Each
    /// machine shows a code and waits for the other to type one, and nothing completes. The rule is
    /// arbitrary on purpose: the smaller device id asks. Both ends work this out from ids they already
    /// have. The machine that loses stands down and answers the question already on its screen.
    /// </para>
    /// </summary>
    /// <returns>True when <paramref name="mine"/> is the one that asks.</returns>
    public static bool AsksFirst(string mine, string theirs) =>
        string.CompareOrdinal(mine ?? "", theirs ?? "") < 0;

    /// <summary>
    /// The asked computer's commitment to its nonce for one round. It is sent before anybody has
    /// typed anything.
    ///
    /// <para>It lets the asking computer check the digits itself once the nonce is revealed. The nonce
    /// shown later must be the one committed to now. The asking computer sends its own nonce for the
    /// round only after it has this commitment. So a machine in the middle must fix its nonce before it
    /// learns the asking computer's. It cannot pick a nonce that makes the asking computer's digits
    /// match the digits on the real owner's screen. A wrong nonce matches by chance only one time in
    /// a million.</para>
    /// </summary>
    public static byte[] CommitNonce(byte[] nonce) => Hash("rpm-pair-nonce", nonce);

    /// <summary>
    /// The asking computer's commitment to the code its user typed. It is bound to both keys as the
    /// asking computer sees them.
    ///
    /// <para>Sent in place of the code. The asked computer reveals its nonce only after this arrives.
    /// The asking computer opens the commitment only after it has checked that nonce. So a machine in
    /// the middle never learns the digits in time to use them. It cannot read the digits from this
    /// value. It cannot forward the value either, because the value names the asking computer's key
    /// and the middle machine's key, not the middle machine's key and the owner's key.</para>
    /// </summary>
    /// <param name="askerKey">The asking computer's public key.</param>
    /// <param name="ownerKey">The asked computer's public key, as the asking computer pinned it.</param>
    /// <param name="opening">A fresh random value, revealed afterwards to open the commitment.</param>
    /// <param name="code">What was typed.</param>
    public static byte[] CommitCode(byte[] askerKey, byte[] ownerKey, byte[] opening, string code) =>
        Hash("rpm-pair-code", askerKey, ownerKey, opening, System.Text.Encoding.UTF8.GetBytes(code ?? ""));

    /// <summary>
    /// The asking computer's nonce for one round. It is derived from a secret that is never sent.
    ///
    /// <para>It is derived rather than drawn fresh for each round. Two calls that send it for the same
    /// round (the status call and a reply racing it) then send the same bytes. The asked computer keeps
    /// the first nonce it receives for a round, so it keeps the nonce the asking computer checks
    /// against.</para>
    /// </summary>
    public static byte[] AskerNonce(byte[] seed, int round)
    {
        var number = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, round);
        return Hash("rpm-pair-asker-nonce", seed, number)[..NonceBytes];
    }

    /// <summary>Whether two hashes are the same, compared in constant time. False when either is
    /// missing.</summary>
    public static bool Same(byte[]? a, byte[]? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(a, b);

    /// <summary>SHA-256 over a label and each part, with the length of each part in front. So no two
    /// different lists of parts hash to the same bytes.</summary>
    private static byte[] Hash(string label, params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.ASCII.GetBytes(label));
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, part.Length);
            hash.AppendData(length);
            hash.AppendData(part);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>Whether two codes are the same. The comparison takes the same time whatever the
    /// result, so the timing does not show how many digits matched. A six-digit code checked over a
    /// network must be compared this way.</summary>
    public static bool Same(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
            return false;

        int difference = 0;
        for (int i = 0; i < a.Length; i++)
            difference |= a[i] ^ b[i];

        return difference == 0;
    }

    private static int Compare(byte[] a, byte[] b)
    {
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
        {
            if (a[i] != b[i])
                return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }
}

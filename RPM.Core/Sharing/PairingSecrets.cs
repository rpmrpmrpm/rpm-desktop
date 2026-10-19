namespace RPM.Core.Sharing;

/// <summary>
/// One round of a pairing on the asked computer. It holds the nonce kept until the asking computer
/// has committed to a code, the commitment to that nonce, and, once the asking computer's nonce for
/// the round has arrived, the six digits this side shows.
/// </summary>
/// <param name="Number">Counts from 1. A wrong code starts the next round with a new nonce on both
/// sides and new digits. So a nonce that has been revealed never decides anything again.</param>
public sealed record PairingRound(int Number, byte[] Nonce, byte[] Commitment)
{
    /// <summary>The asking computer's nonce for this round, or null until it arrives.</summary>
    public byte[]? AskerNonce { get; init; }

    /// <summary>The six digits on this screen. Empty until <see cref="AskerNonce"/> has arrived.</summary>
    public string Code { get; init; } = "";
}

/// <summary>
/// What the asked computer holds for one pairing: both keys. From these it makes each round and
/// checks the asking computer's opening.
///
/// <para>Each round runs in this order. Each step is refused if it comes out of order:</para>
/// <list type="number">
/// <item>This side draws a nonce and sends only its commitment (<see cref="Round"/>).</item>
/// <item>The asking computer sends its nonce for the round, and the digits go on this screen
/// (<see cref="Joined"/>). The first nonce received for a round is kept.</item>
/// <item>The asking computer commits to what was typed. Only then is this side's nonce revealed.</item>
/// <item>The asking computer checks the nonce against the commitment from step 1, works out the
/// digits itself, and opens its commitment only when the digits agree (<see cref="Opens"/>).</item>
/// </list>
/// </summary>
/// <param name="askerKey">The asking computer's public key, as the request carried it.</param>
/// <param name="ownerKey">This computer's public key.</param>
public sealed class PairingSecrets(byte[] askerKey, byte[] ownerKey)
{
    /// <summary>A new round: a fresh nonce, and the commitment that goes on the wire in its place. No
    /// digits yet.</summary>
    public PairingRound Round(int number)
    {
        var nonce = PairingCode.Nonce();
        return new PairingRound(number, nonce, PairingCode.CommitNonce(nonce));
    }

    /// <summary>The round with the asking computer's nonce in it, and the digits made from them.</summary>
    public PairingRound Joined(PairingRound round, byte[] askerNonce) => round with
    {
        AskerNonce = askerNonce,
        Code = PairingCode.For(askerKey, ownerKey, askerNonce, round.Nonce),
    };

    /// <summary>Whether <paramref name="opening"/> opens the asking computer's commitment to this
    /// round's digits. An empty opening means the asking computer's own check failed.</summary>
    public bool Opens(PairingRound round, byte[] askerCommitment, byte[] opening) =>
        round.Code.Length > 0 && opening.Length > 0 &&
        PairingCode.Same(askerCommitment, PairingCode.CommitCode(askerKey, ownerKey, opening, round.Code));
}

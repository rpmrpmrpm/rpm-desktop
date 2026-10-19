namespace RPM.Core.Sharing;

public sealed partial class PeerClient
{
    /// <summary>
    /// The asking computer's half of a pairing. It sends this side's nonce for each round. It turns what
    /// the person typed into a commitment, a check and an opening. The code itself never crosses the
    /// network.
    ///
    /// <para>Two rules carry all of this side's protection. Both live here, so that nothing else can send
    /// a nonce, a commitment or an opening. The calls that do send them are private to
    /// <see cref="PeerClient"/>.</para>
    /// <list type="bullet">
    /// <item><b>This side's nonce for a round goes out only after the other computer's commitment for
    /// that round has arrived.</b> A machine in the middle then fixes its nonce before it learns this
    /// side's nonce. It cannot choose a nonce that makes the digits here match the ones on the real
    /// owner's screen, which it could otherwise learn. If this side sent its nonce first, the machine in
    /// the middle could try nonces until one matched.</item>
    /// <item><b>The commitment is pinned at the first status of the round.</b> A later status for the same
    /// round can carry a different commitment. Checking against that one would let the machine in the
    /// middle commit again after it has seen this side's nonce.</item>
    /// </list>
    /// </summary>
    /// <param name="client">The client the pairing was asked through. The commitment to the typed code is
    /// bound to the key that this client is pinned to. So it cannot be forwarded to a different
    /// computer.</param>
    public sealed class PairingAsker(PeerClient client)
    {
        /// <summary>Never sent. Each round's nonce is derived from it.</summary>
        private readonly byte[] _seed = PairingCode.Nonce();

        private readonly object _gate = new();

        /// <summary>The newest round seen, with the commitment it was first seen with. Guarded by
        /// <see cref="_gate"/>.</summary>
        private Round? _round;

        private sealed record Round(int Number, byte[] Promised, byte[] Nonce);

        /// <summary>
        /// Takes every status seen from the asked computer, in any order and from any thread. When a
        /// status shows a round whose digits are not on that screen yet, sends this side's nonce for it.
        /// </summary>
        /// <returns>The answer when a nonce was sent. Null when nothing needed sending, or when the call
        /// failed. A failed send is retried with the next status for the round.</returns>
        public async Task<PairStatus?> JoinAsync(string id, PairStatus status, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(status);

            // Pinned even when the digits are already on screen. That way TypeAsync knows the round,
            // whichever status arrives first.
            if (status.State is not PairStatusKind.ShowingCode ||
                Pin(status) is not { } round || round.Number != status.Round || status.Shown)
                return null;

            return await client.PostNonceAsync(id, round.Number, round.Nonce, cancellation);
        }

        /// <summary>The round, pinned the first time it is seen. An older round is not taken back.</summary>
        private Round? Pin(PairStatus status)
        {
            lock (_gate)
            {
                if (status.Round > (_round?.Number ?? 0) &&
                    SharingProtocol.FromBase64Url(status.Commit) is { Length: > 0 } promised)
                {
                    _round = new Round(status.Round, promised, PairingCode.AskerNonce(_seed, status.Round));
                }

                return _round;
            }
        }

        /// <summary>
        /// The code typed here, in three steps.
        /// <list type="number">
        /// <item>Commit to it, bound to both keys. The other computer reveals its nonce for the round only
        /// after the commitment has arrived.</item>
        /// <item>Check the revealed nonce against the pinned commitment, and work out the digits here.</item>
        /// <item>Open the commitment if the digits agree. Otherwise send an empty opening. The other
        /// computer counts that as a wrong code and answers with a new round. A commitment that could not
        /// be checked is never opened. The opening is what would let a machine in the middle read the
        /// code.</item>
        /// </list>
        /// </summary>
        /// <returns>The last status received. Null when no round has been seen or when a call failed.</returns>
        public async Task<PairStatus?> TypeAsync(string id, string code, CancellationToken cancellation)
        {
            Round? round;
            lock (_gate)
                round = _round;

            if (round is null)
                return null;

            var askerKey = client._identity.PublicKey;
            var ownerKey = client._pinnedKey;
            var opening = PairingCode.Nonce();
            var commitment = PairingCode.CommitCode(askerKey, ownerKey, opening, code ?? "");

            var committed = await client.PostCommitAsync(id, round.Number, commitment, cancellation);

            // The pairing ended or moved on in the meantime, or the digits were not on that screen yet. The
            // status says which, and nothing is opened.
            if (committed is null || committed.Round != round.Number ||
                SharingProtocol.FromBase64Url(committed.Reveal) is not { Length: > 0 } nonce)
                return committed;

            bool agrees = PairingCode.Same(PairingCode.CommitNonce(nonce), round.Promised)
                       && PairingCode.Same(PairingCode.For(askerKey, ownerKey, round.Nonce, nonce), code);

            return await client.PostOpenAsync(id, round.Number, agrees ? opening : [], cancellation);
        }
    }
}

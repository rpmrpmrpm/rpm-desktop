namespace RPM.Core.Sharing;

/// <summary>
/// One request this computer made, followed until it ends.
///
/// <para>Sends the request, then keeps one status call open on the other computer for as long as the
/// pairing is live. It copies every answer onto the board. The open call also tells the other
/// computer that this one is still asking. When the call stops, the other computer's lease runs out
/// and its code comes off the screen.</para>
///
/// <para>Everything it writes goes through the board with its own attempt number. So a run that has
/// been replaced (Ask again) or cancelled changes nothing, however late its replies arrive.</para>
/// </summary>
public sealed class PairingRun : IDisposable
{
    /// <summary>How long to wait before retrying a failed status call.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    private readonly PairingBoard _board;
    private readonly PeerClient _client;
    private readonly Pairing _started;
    private readonly Func<DateTime> _now;
    private readonly Action<Pairing> _accepted;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The wire id, once the offer has arrived. Set once by <see cref="RunAsync"/>. The
    /// methods <see cref="SubmitAsync"/> and <see cref="CancelAsync"/> read it from other threads.</summary>
    private volatile string _id = "";

    /// <summary>Sends this side's nonce for each round and turns a typed code into a commitment, a
    /// check and an opening. Every status seen is passed to it.</summary>
    private readonly PeerClient.PairingAsker _asker;

    /// <param name="board">The board <paramref name="started"/> is on.</param>
    /// <param name="client">A client pinned to the key from the other computer's beacon. This run owns
    /// it from here on.</param>
    /// <param name="started">What <see cref="PairingBoard.Begin"/> returned.</param>
    /// <param name="clock">The current UTC time.</param>
    /// <param name="accepted">Called once, with the pairing, when this side first sees it accepted:
    /// from the code reply or from a status reply, whichever comes first.</param>
    public PairingRun(
        PairingBoard board, PeerClient client, Pairing started, Func<DateTime> clock, Action<Pairing> accepted)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _started = started ?? throw new ArgumentNullException(nameof(started));
        _now = clock ?? throw new ArgumentNullException(nameof(clock));
        _accepted = accepted ?? throw new ArgumentNullException(nameof(accepted));
        _asker = new PeerClient.PairingAsker(client);
    }

    public string DeviceId => _started.DeviceId;
    public long Attempt => _started.Attempt;

    /// <summary>Sends the request and follows the pairing until it ends, is replaced, or this run is
    /// stopped.</summary>
    public async Task RunAsync(string name, string face, string accountFingerprint)
    {
        var token = _stop.Token;

        // Older builds read the nonce sent here. See PairRequest.Nonce.
        var (offer, delivery) = await _client.PairAsync(name, face, accountFingerprint, PairingCode.Nonce(), token);
        if (token.IsCancellationRequested)
            return;

        if (delivery is PairDelivery.Crossed)
        {
            _board.Crossed(DeviceId, Attempt);
            return;
        }

        // The client is pinned to the beacon's key, so an offer that arrived came from that computer.
        // An offer naming another key is refused anyway, because the pairing would be written against
        // that key.
        if (offer is null ||
            SharingProtocol.FromBase64Url(offer.PublicKey) is not { } key ||
            !key.AsSpan().SequenceEqual(_started.Peer.PublicKey))
        {
            _board.Undelivered(DeviceId, Attempt);
            return;
        }

        // An offer that shows a code with no commitment comes from a computer running an older build.
        // That build sends the code in the clear, so this side does not pair with it. The request is
        // withdrawn, so its code comes off that screen.
        if (offer.Status.State is PairStatusKind.ShowingCode && offer.Status.Commit.Length == 0)
        {
            _board.Outdated(DeviceId, Attempt);
            if (offer.Id.Length > 0)
                await _client.PairWithdrawAsync(offer.Id, CancellationToken.None);
            return;
        }

        _id = offer.Id;
        _board.Offered(DeviceId, Attempt, offer.Id, offer.Status);
        await JoinAsync(offer.Status);

        long rev = offer.Status.Rev;
        DateTime? failingSince = null;

        while (!token.IsCancellationRequested && _board.IsCurrent(DeviceId, Attempt))
        {
            var status = await _client.PairStatusAsync(_id, rev, token);
            if (token.IsCancellationRequested)
                return;

            if (status is null)
            {
                var now = _now();
                failingSince ??= now;
                if (now - failingSince.Value >= _board.Times.LostAfter)
                {
                    _board.Lose(DeviceId, Attempt);
                    return;
                }

                try { await Task.Delay(Retry, token); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            failingSince = null;
            rev = status.Rev;
            Mirror(status);
            await JoinAsync(status);
        }
    }

    /// <summary>Sends the code typed here, through <see cref="PeerClient.PairingAsker.TypeAsync"/>.
    /// The code itself never crosses the network.</summary>
    /// <returns>False when the other computer could not be reached. The status call then decides
    /// whether that becomes Lost.</returns>
    public async Task<bool> SubmitAsync(string code)
    {
        if (_id is not { Length: > 0 } id || _stop.IsCancellationRequested)
            return false;

        var status = await _asker.TypeAsync(id, code, _stop.Token);
        if (status is null)
            return false;

        Mirror(status);
        await JoinAsync(status);
        return true;
    }

    /// <summary>
    /// Stops following and tells the other computer. Called after the pairing has been taken off the
    /// board (<see cref="PairingBoard.Withdraw"/>). The other computer is told on a best-effort basis.
    /// If it cannot be reached, its lease ends the pairing there.
    /// </summary>
    public async Task CancelAsync()
    {
        var id = _id;
        _stop.Cancel();

        if (id.Length > 0)
            await _client.PairWithdrawAsync(id, CancellationToken.None);
    }

    /// <summary>Sends this side's nonce when <paramref name="status"/> shows a round still waiting for
    /// it (a new pairing, or a new round after a wrong code), and copies the answer.</summary>
    private async Task JoinAsync(PairStatus status)
    {
        if (await _asker.JoinAsync(_id, status, _stop.Token) is { } joined)
            Mirror(joined);
    }

    private void Mirror(PairStatus status)
    {
        if (_board.Mirror(DeviceId, Attempt, status) is { } accepted)
            _accepted(accepted);
    }

    /// <remarks>The token source is cancelled and not disposed. A run that is still unwinding reads its
    /// token, and a disposed source throws from <c>Token</c>. The run holds no timer, so there is
    /// nothing else to release.</remarks>
    public void Dispose()
    {
        _stop.Cancel();
        _client.Dispose();
    }
}

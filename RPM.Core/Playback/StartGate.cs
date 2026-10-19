namespace RPM.Core.Playback;

/// <summary>
/// Which attempt at starting something owns the player.
///
/// <para>
/// Starting a track takes time. A URL is signed, an asset's tracks are loaded over the network, and a
/// reader is built. A listener can change their mind during that time, so two taps a second apart start two
/// attempts. With no arbiter, both attempts finish. The one that finishes <em>last</em> is what plays, and
/// that is not always the one tapped last. The symptom was the second track's name on the now-playing
/// screen while the first track was still playing.
/// </para>
///
/// <para>
/// So an attempt takes a numbered claim on the player before it waits for anything. The claim is checked
/// again at the one point that matters: just before the source is handed to the engine. A superseded
/// attempt is also <em>cancelled</em>, not only ignored. This stops a download it is waiting on, instead of
/// letting it finish and use up the listener's data.
/// </para>
///
/// <para>
/// The engine has a counter of the same shape, and it is not enough on its own. It orders the transitions
/// it is <em>told about</em>, in the order it is told them. The problem here is that they arrive out of
/// order.
/// </para>
///
/// <para>This type lives here, not in the platform head, so it can be tested without a device, like <see
/// cref="QueueEdit"/> and <see cref="PlaybackFailure"/>.</para>
/// </summary>
public sealed class StartGate
{
    private readonly object _gate = new();

    private int _generation;
    private CancellationTokenSource? _live;

    /// <summary>
    /// Claims the player for a new attempt, and cancels whichever attempt was already on its way.
    /// </summary>
    /// <param name="outer">The caller's own token, if it has one. The attempt ends when either that token
    /// or a later claim says so.</param>
    public Attempt Begin(CancellationToken outer = default)
    {
        lock (_gate)
        {
            CancelLive();

            var cancel = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _live = cancel;

            return new Attempt(this, ++_generation, cancel);
        }
    }

    /// <summary>
    /// Cancels whatever is on its way without claiming the player for anything else. This is used when the
    /// player is stopped, or handed to something that does not come through this gate, such as a radio
    /// station.
    /// </summary>
    public void Retire()
    {
        lock (_gate)
        {
            _generation++;
            CancelLive();
        }
    }

    /// <summary>Whether <paramref name="generation"/> is still the attempt that owns the player.</summary>
    public bool IsLive(int generation)
    {
        lock (_gate) return _generation == generation;
    }

    /// <summary>Cancels the live attempt without disposing it. The owning attempt disposes it, because it
    /// may still be inside a call that uses its token.</summary>
    private void CancelLive()
    {
        try { _live?.Cancel(); }
        catch (ObjectDisposedException) { /* the owner has already disposed it */ }

        _live = null;
    }

    private void Forget(CancellationTokenSource cancel)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_live, cancel)) _live = null;
        }
    }

    /// <summary>
    /// One attempt's claim. Dispose it when the attempt ends, however it ended: cancelled, failed, or
    /// playing.
    /// </summary>
    public sealed class Attempt(StartGate gate, int generation, CancellationTokenSource cancel)
        : IDisposable
    {
        /// <summary>This attempt's number, for a caller that wants to ask again later.</summary>
        public int Generation => generation;

        /// <summary>Cancelled when a later attempt claims the player, or when the caller's own token is
        /// cancelled.</summary>
        public CancellationToken Token => cancel.Token;

        /// <summary>Whether this is still the attempt that owns the player. Read it immediately before
        /// doing anything the user would see or hear.</summary>
        public bool IsLive => gate.IsLive(generation);

        public void Dispose()
        {
            gate.Forget(cancel);
            cancel.Dispose();
        }
    }
}

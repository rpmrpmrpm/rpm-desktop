using RPM.Fingerprint;

namespace RPM.Core.Audio;

/// <summary>What to ask the store for, based on how the last records actually arrived.
///
/// <para>The rate is a setting: one for wifi, one for a mobile data plan. A setting says what somebody
/// wants. It does not say what the connection can carry. At home those are the same, but on a train they
/// are not, and the phone had no way to tell. It kept asking for the original, the ring behind the decoder
/// ran dry every time, and the record stopped every twenty seconds. The listener had to turn the quality
/// down by hand, after the problem had already happened.
/// </para>
///
/// <para>So the evidence is kept. A stream that runs its buffer dry has shown that this connection cannot
/// carry the rate, so the next record is asked for a rung lower. Nothing is turned down under a record that
/// is already playing. A track does not change quality while someone is listening. The rung comes back on
/// its own once <see cref="Patience"/> has passed without trouble. So a tunnel costs one record, not the
/// rest of the afternoon.
/// </para>
///
/// <para>The step down is taken from what was CHOSEN, never from what was last served. The listener's
/// setting stays the ceiling. Lowering it by hand lowers this too, and raising it is still worth doing when
/// the connection is better.
/// </para>
/// </summary>
public sealed class RateWatch
{
    /// <summary>How long a rung stays given up before it is tried again.
    ///
    /// <para>About a record and a half. Long enough that a bad connection does not flap up and down across
    /// a whole journey. Short enough that the listener gets the better quality back before the next
    /// album.</para>
    /// </summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    /// <summary>The furthest down this will go. The whole ladder has six rungs, so this reaches the bottom
    /// from the top and no further. A counter that kept climbing would take an hour to walk back up after a
    /// bad afternoon.</summary>
    private const int Deepest = 5;

    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    private int _down;
    private DateTime _since;

    /// <param name="now">The clock. There is no default. This type's behaviour is a comparison against
    /// time, and a test that cannot move the clock tests nothing.</param>
    public RateWatch(Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
    }

    /// <summary>A record that this connection could not keep up with.
    ///
    /// <para>The buffer shows this, not a guess. The ring behind the decoder had filled and then run dry.
    /// That means the bytes were arriving more slowly than they were being played. Called from the audio
    /// thread, once per stream.</para>
    /// </summary>
    public void Stumbled()
    {
        lock (_gate)
        {
            Recover();

            _down = Math.Min(_down + 1, Deepest);
            _since = _now();
        }
    }

    /// <summary>The rate to ask the store for now.
    /// </summary>
    /// <param name="chosen">What the listener set for this kind of connection. This is the ceiling.</param>
    /// <param name="offer">The rungs this account may have, best first.</param>
    public int Ask(int chosen, AudioLadder.RateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        lock (_gate)
        {
            Recover();

            if (_down == 0 || offer.Rungs.Count == 0) return chosen;

            // Never go past the bottom of what is on offer. A free account has one rung, and stepping down
            // from it lands on the same rung. That is the honest answer: nothing cheaper is available, and
            // the stumble was not about the rate.
            var at = Math.Min(offer.IndexOf(chosen) + _down, offer.Rungs.Count - 1);
            return offer.Rungs[at];
        }
    }

    /// <summary>Forgets everything it has learned.
    ///
    /// <para>Used when moving between wifi and a mobile data plan. The evidence is about one connection.
    /// Carrying it over to the new connection would hold the good connection down for
    /// <see cref="Patience"/> for no visible reason.</para>
    /// </summary>
    public void Forget()
    {
        lock (_gate)
        {
            _down = 0;
            _since = default;
        }
    }

    /// <summary>Gives back one rung for each <see cref="Patience"/> that has passed without trouble. It
    /// climbs one rung at a time. So a long quiet stretch climbs the ladder gradually, and does not jump
    /// back to the top and stumble again.</summary>
    private void Recover()
    {
        while (_down > 0 && _now() - _since >= Patience)
        {
            _down--;
            _since += Patience;
        }
    }
}

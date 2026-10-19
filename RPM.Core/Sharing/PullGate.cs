namespace RPM.Core.Sharing;

/// <summary>
/// One pull per computer at a time, and a repeat for anybody who asked while one was running.
///
/// <para><b>Why this exists.</b> Reading a peer's catalogue is started from three places: the timed
/// round, a beacon from a computer already known, and a beacon from one trusted on sight. None of them
/// knew about the others. A beacon is not a single event. The responder re-announces itself. In the
/// first minutes after launch, when discovery is busiest, the same computer is found over and over. Each
/// one started a whole pass over that machine's listing. A pass holds the library's one writing gate for
/// its whole length.</para>
///
/// <para>The passes were not slow because the work was hard. Several passes ran at once, and each held the
/// door that the next one needed. So a pass could wait a long time behind another pass, and a user's
/// album edit could wait behind them all.</para>
///
/// <para><b>Coalesced rather than queued.</b> A second ask for a computer that is already being read is
/// not worth a second pass. The two would read the same catalogue. So the ask is remembered as one
/// "again", and the running pass repeats once when it is done. Ten beacons during one pull cost one extra
/// pass, not ten. The catalogue is still read after the last of them, not only before the first.</para>
///
/// <para><b>A type rather than a flag at each call site.</b> There are three call sites today. The next
/// one added could be the one that forgets. <see cref="Begin"/> is the only way in and
/// <see cref="Continue"/> is the only way out. They hand the caller a loop that it cannot write wrongly.
/// </para>
/// </summary>
public sealed class PullGate
{
    /// <summary>
    /// The computers being read right now, against whether somebody asked again while they were.
    /// </summary>
    private readonly Dictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _lock = new();

    /// <summary>
    /// Claims the right to read this computer.
    /// </summary>
    /// <returns><c>true</c> when the caller should go ahead. It then owes a <see cref="Continue"/> for every
    /// pass. <c>false</c> when somebody else is already reading it. The ask has been remembered, and that
    /// pass will repeat rather than this one waiting.</returns>
    public bool Begin(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        lock (_lock)
        {
            if (_running.ContainsKey(deviceId))
            {
                _running[deviceId] = true;
                return false;
            }

            _running[deviceId] = false;
            return true;
        }
    }

    /// <summary>
    /// Whether the pass that just finished should run again, and the claim released if not.
    /// </summary>
    /// <remarks>
    /// <b>Both halves under the one lock. This is the whole of the correctness.</b> Asking "did anybody
    /// want another?" and then releasing, as two steps, leaves a gap. An ask that lands in the gap is
    /// answered by neither. The running pass has already decided to stop, and the asker has already been
    /// told that somebody else would do it. The catalogue then stays stale until the next timed round,
    /// which is five minutes. That happens exactly when beacons are busiest.
    /// </remarks>
    /// <returns><c>true</c> to go round again, still holding the claim. <c>false</c> when the claim has
    /// been given up and the caller must stop.</returns>
    public bool Continue(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        lock (_lock)
        {
            if (_running.TryGetValue(deviceId, out var again) && again)
            {
                _running[deviceId] = false;
                return true;
            }

            _running.Remove(deviceId);
            return false;
        }
    }

    /// <summary>How many computers are being read. For a diagnostic line, not for a decision.</summary>
    public int Running
    {
        get
        {
            lock (_lock)
                return _running.Count;
        }
    }
}

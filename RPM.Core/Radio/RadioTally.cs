namespace RPM.Core.Radio;

/// <summary>
/// How much of a station somebody has heard, added up across reconnects.
///
/// <para><b>Why this is not <see cref="Playback.ListenTally"/>.</b> That type counts the same kind of
/// thing for a track, but two of its three rules are wrong for a stream. A stream cannot be seeked, so
/// the only thing that resets the engine's counter is the station dropping and coming back. That is one
/// listen, not two. Reconnecting also starts a new session at offset zero. <c>ListenTally</c> reads
/// that as somebody playing the track again, and ends the listen. On the radio that would end a listen
/// every time the stream reconnected, so a station that drops once a minute would never record a
/// listen. Passing a made-up start offset to that type would hide the problem from the compiler.</para>
///
/// <para>So this type keeps three rules. The station is what a listen belongs to. A change of play
/// session means the counter underneath went back to zero, so the heard time so far is banked. The
/// heard time is never dropped. A listen ends only when the STATION changes.</para>
/// </summary>
public sealed class RadioTally
{
    /// <summary>A listen that has ended, for the caller to record.</summary>
    /// <param name="Uuid">The station it was of.</param>
    /// <param name="HeardSeconds">Audio delivered, across every stretch.</param>
    public readonly record struct Listened(string Uuid, double HeardSeconds);

    private string? _uuid;
    private int _session;
    private double _banked;
    private double _stretch;

    /// <summary>The station being tallied, or null when nothing is.</summary>
    public string? Uuid => _uuid;

    /// <summary>Everything heard of it so far: the banked stretches plus the one running.</summary>
    public double HeardSeconds => _banked + _stretch;

    /// <summary>
    /// Takes a reading and returns the listen it ended, if it ended one.
    /// </summary>
    /// <param name="uuid">The station tuned now, or null for none.</param>
    /// <param name="session">The engine's play session. It changes when the stream is reopened, which
    /// is when the counter below goes back to zero.</param>
    /// <param name="heardSeconds">Audio delivered in the current stretch.</param>
    public Listened? Sample(string? uuid, int session, double heardSeconds)
    {
        Listened? finished = null;

        if (!string.Equals(uuid, _uuid, StringComparison.OrdinalIgnoreCase))
        {
            if (_uuid is { } was)
                finished = new Listened(was, HeardSeconds);

            _uuid = uuid;
            _banked = 0;
            _stretch = 0;
        }
        else if (session != _session)
        {
            // The stream reopened. The same listen carries on with the counter below back at zero.
            _banked += _stretch;
            _stretch = 0;
        }

        _session = session;

        if (uuid is null)
            return finished;

        // Never downwards within a stretch: a reading taken while the engine is between streams reports
        // less than the last one, and taking it would throw away what this listen has already earned.
        if (heardSeconds > _stretch)
            _stretch = heardSeconds;

        return finished;
    }

    /// <summary>Ends the listen in hand and returns it, for a player being shut down with a station
    /// still on it. The tally is left empty, so a listen cannot be recorded twice.</summary>
    public Listened? Close()
    {
        if (_uuid is not { } was)
            return null;

        var finished = new Listened(was, HeardSeconds);
        _uuid = null;
        _banked = 0;
        _stretch = 0;
        return finished;
    }
}

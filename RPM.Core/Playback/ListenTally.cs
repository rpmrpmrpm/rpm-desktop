namespace RPM.Core.Playback;

/// <summary>
/// How much of a track somebody has heard, added up across the seeks.
///
/// <para>
/// The engine's own counter cannot give this total on its own. A seek starts a fresh decoder, because the
/// file is reopened at the new offset, so the counter goes back to zero. A reader that only keeps the
/// highest figure it has seen would count the longest unbroken stretch, not the total listening. Someone
/// who plays two minutes, seeks back to the chorus and plays two more has heard four minutes, but would be
/// credited with two. That is most wrong for a favourite track, which people often seek around in. Those
/// are the records the count is there to find.
/// </para>
///
/// <para>
/// So each stretch is banked when it ends, and the total is what <see cref="Listen"/> judges. The engine's
/// play session separates one stretch from the next. It changes on every Play and every Stop, but not on a
/// gapless change to the next track, so it is the one signal that the counter has been rewound. Watching
/// for the figure to fall would confuse a rewind with the counter starting again at zero for a gapless
/// change. The incoming track would then be given everything the outgoing one had earned.
/// </para>
///
/// <para>
/// A restart is not a seek. A track played again from its beginning is a second listen. The first listen is
/// settled before the tally starts over. Otherwise a track on repeat would build one endless play out of a
/// hundred passes.
/// </para>
/// </summary>
public sealed class ListenTally
{
    /// <summary>A start this close to the beginning means the track was played again, not that it was
    /// sought. Half a second is used because a "play from the top" starts at exactly zero, and only a
    /// deliberate drag lands near it.</summary>
    public const double RestartWithinSeconds = 0.5;

    /// <summary>A listen that has ended, for the caller to judge and record.</summary>
    /// <param name="Path">The track it was of.</param>
    /// <param name="HeardSeconds">Everything heard of it, across every stretch.</param>
    /// <param name="LengthSeconds">How long the track is, as the engine reported it.</param>
    public readonly record struct Listened(string Path, double HeardSeconds, double LengthSeconds);

    private string? _path;
    private int _session;
    private double _banked;
    private double _stretch;
    private double _length;

    /// <summary>The track being tallied, or null when nothing is.</summary>
    public string? Path => _path;

    /// <summary>Everything heard of it so far: the banked stretches plus the one running.</summary>
    public double HeardSeconds => _banked + _stretch;

    /// <summary>How long that track is, or zero when nothing has said.</summary>
    public double LengthSeconds => _length;

    /// <summary>
    /// Takes a reading of the engine and returns the listen it ended, if it ended one.
    /// </summary>
    /// <param name="path">What is playing now, or null for nothing.</param>
    /// <param name="session">The engine's play session. See the class remarks.</param>
    /// <param name="startSeconds">Where the current stretch was started from. Only read when the session
    /// has changed, to tell a replay from a seek.</param>
    /// <param name="heardSeconds">Audio delivered in the current stretch.</param>
    /// <param name="lengthSeconds">The track's length, zero when unknown.</param>
    public Listened? Sample(string? path, int session, double startSeconds, double heardSeconds, double lengthSeconds)
    {
        Listened? finished = null;

        var moved = !string.Equals(path, _path, StringComparison.Ordinal);
        var replayed = !moved && session != _session && startSeconds <= RestartWithinSeconds;

        if (moved || replayed)
        {
            if (_path is { } was) finished = new Listened(was, HeardSeconds, _length);
            _path = path;
            _banked = 0;
            _stretch = 0;
            _length = 0;
        }
        else if (session != _session)
        {
            // A seek: the same listen carries on, with the counter underneath back at zero.
            _banked += _stretch;
            _stretch = 0;
        }

        _session = session;

        if (path is null) return finished;

        // Never move the value down within a stretch. A chained track's counter starts from zero again. A
        // reading taken a moment early, while the audio output still plays the end of the outgoing track,
        // would otherwise throw away what this track has already earned.
        if (heardSeconds > _stretch) _stretch = heardSeconds;
        if (lengthSeconds > 0) _length = lengthSeconds;

        return finished;
    }

    /// <summary>Ends the listen in hand and returns it, for a player that is shut down while a track is
    /// still playing. The tally is left empty, so the same listen cannot be counted twice.</summary>
    public Listened? Close()
    {
        if (_path is not { } was) return null;

        var finished = new Listened(was, HeardSeconds, _length);
        _path = null;
        _banked = 0;
        _stretch = 0;
        _length = 0;
        return finished;
    }
}

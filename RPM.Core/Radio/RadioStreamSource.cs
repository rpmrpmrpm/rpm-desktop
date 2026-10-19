using RPM.Core.Audio;

namespace RPM.Core.Radio;

/// <summary>
/// A radio station, read as PCM.
///
/// <para>This is more than decoding a URL. First, it never ends on its own. A station drops, a mirror
/// changes, a laptop lid closes and the socket dies. None of these means the user stopped listening. A
/// dropped stream reconnects. While it reconnects, the source returns silence. Returning zero would
/// tell the engine that the track has finished, and the whole player would tear down. So the source
/// gives up only when the station has stayed unreachable for <see cref="MaxAttempts"/> tries.</para>
///
/// <para>Second, it reads the metadata. The connection is opened here, not inside ffmpeg. That way the
/// station's interleaved ICY blocks can be taken out and shown as the now-playing title, which is the
/// only readout a radio has. HLS stations are the exception. Their URL is a playlist, not a byte
/// stream, so ffmpeg opens them itself, and they play without a title.</para>
///
/// <para>Reads happen on the prefetch thread, never on the audio pump. <see cref="PrefetchPcmSource"/>
/// sits between them. This is what makes it safe for a read here to block on a socket.</para>
/// </summary>
public sealed class RadioStreamSource : IPcmSource, INowPlayingSource
{
    /// <summary>The number of tries before giving up. With the backoff below, this is a little over a
    /// minute of a station being unreachable. That is long enough to survive a mirror change, and
    /// short enough that a station which has gone away does not leave silence playing
    /// forever.</summary>
    public const int MaxAttempts = 8;

    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15),
    ];

    private readonly RadioStation _station;
    private readonly int _channels;
    private readonly Func<RadioStation, Action<Fielded>, IPcmSource?> _connect;
    private readonly ManualResetEventSlim _stopping = new(false);
    private readonly object _gate = new();

    private IPcmSource? _decoder;
    private int _attempt;
    private long _frames;
    private volatile Heard? _heard;
    private volatile bool _disposed;

    /// <param name="connect">Opens a decoder for the station, or returns null when it cannot be
    /// reached, calling the given sink each time the station announces another track. Injectable so the
    /// reconnect behaviour and the readout can be tested without a radio station.</param>
    public RadioStreamSource(
        RadioStation station,
        int sampleRate = 44100,
        int channels = 2,
        Func<RadioStation, Action<Fielded>, IPcmSource?>? connect = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        _station = station;
        _channels = channels;
        SampleRate = sampleRate;
        _connect = connect ?? ((s, announce) => IcyConnection.Open(s, sampleRate, channels, announce));
    }

    public int SampleRate { get; }

    public RadioStation Station => _station;

    /// <summary>
    /// What is known about how a station writes its announcements. See <see cref="IcyOrders"/>.
    ///
    /// <para>The app that tunes the station sets this, because only the app knows which collection to
    /// ask. <see cref="IcyOrders.Unlearned"/> is used where nothing is known yet. That is the guess,
    /// and it is what this source uses before any collection is consulted.</para>
    /// </summary>
    public IcyOrders Orders { get; init; } = IcyOrders.Unlearned;

    /// <summary>What the player calls this source: the station's printed name, so that the banner and
    /// the row agree. See RadioStation.Display.</summary>
    public string Label => _station.Display;

    /// <summary>The track the station last announced, or null when it has not (or cannot).</summary>
    public string? Title => _heard?.Spoken;

    /// <summary>
    /// What the station last announced: the line it said, and the two halves if it NAMED them, rather
    /// than leaving the order to be worked out. See <see cref="IcyFields"/>.
    /// </summary>
    /// <remarks>
    /// One immutable object sits behind one volatile reference. The reading thread replaces it while
    /// the interface thread reads it. Two separate fields could be caught half-swapped, with one
    /// station's artist against the last station's title, for one frame. That kind of readout cannot be
    /// reproduced afterwards.
    /// </remarks>
    private sealed record Heard(string Spoken, Announced? Named);

    /// <summary>True while the station is unreachable and the source is handing back silence.</summary>
    public bool IsReconnecting { get; private set; }

    /// <summary>Raised when the station announces a different track, and when the connection drops or
    /// comes back. These are all the events the readout has to follow. Raised on the reading
    /// thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// What the player shows, in the same fields a track uses, so that the header needs no special
    /// case. The announced artist and track are shown where the station names both. The station itself
    /// is shown as the album. That keeps the station's name on screen during a song the station has not
    /// named. The logo stands in for the cover.
    ///
    /// <para>Duration is zero, because radio has no end. Position counts the audio actually played, so
    /// the readout ticks up like a very long track instead of sitting at nothing.</para>
    /// </summary>
    public NowPlayingTrack? NowPlayingTrack
    {
        get
        {
            // Read once. The reader publishes a whole announcement or none of one, so one copy is used
            // below.
            var heard = _heard;

            // The pair the station NAMED is used where it named one (see IcyFields). The guess is used
            // only where it did not. Some stations announce the pair the other way round, and some
            // announce their own programme rather than a record. Which way round those mean is learnt
            // from what the station has said and what the collection recognises (see IcyOrders). A
            // station that states which half is which is not one of these. Asking anyway would let a
            // collection that knows an odd artist name turn a stated pair round.
            var announced = heard?.Named ?? Orders.Read(_station.Uuid, heard?.Spoken, _station.Name);

            // A break marker is not a title. The station sends it to say that nothing is playing. The
            // header therefore names the station where a record's name would go, not "Advertisement".
            // The dial shows the break itself (see RadioModel.StatusText).
            var running = RadioAds.IsAdBreak(heard?.Spoken);

            // What the station is naming, or nothing: a break, an ident, or a station that sends no
            // metadata at all. Nothing is the normal state of a dial between records, and it has to
            // look like one. The station name goes where the record's name would be, and the line under
            // it stays EMPTY rather than repeating the name. A repeated name would read as a failed
            // readout, not as a station between songs.
            var named = running ? null : announced.Title;

            return new NowPlayingTrack(
                _station.StreamUrl,
                IsRecord: false,        // an address, not a record: nothing can be looked up by it
                named ?? _station.Display,
                named is null ? null : announced.Artist,
                named is null ? null : _station.Display,
                Interlocked.Read(ref _frames) / (double)SampleRate,
                0,
                _station.Favicon);
        }
    }

    public int Read(float[] buffer, int frames)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_disposed || frames <= 0)
            return 0;

        while (!_disposed)
        {
            var decoder = Current();
            if (decoder is not null)
            {
                int got = decoder.Read(buffer, frames);
                if (got > 0)
                {
                    Interlocked.Add(ref _frames, got);
                    _attempt = 0;                     // it is playing; the next drop starts over
                    return got;
                }
                Drop();                               // ran out: the station went away mid-stream
            }

            if (_attempt >= MaxAttempts)
                return 0;                             // genuinely gone, so let the player stop

            // The first try, when tuning in or retrying straight after a drop, is immediate. Only a try
            // that has already failed waits. The wait happens here, on the prefetch thread, and it can
            // be interrupted. A Stop during a reconnect must take effect at once, not after the next
            // fifteen-second backoff.
            if (_attempt > 0)
            {
                _stopping.Wait(Backoff[Math.Min(_attempt - 1, Backoff.Length - 1)]);
                if (_disposed)
                    return 0;
            }

            _attempt++;
            if (Reconnect())
                continue;

            // Still down. Hand back a block of silence, so the device keeps running and the pane can
            // show the state. Returning zero here would make the engine read this as the end of the
            // stream.
            Array.Clear(buffer, 0, frames * _channels);
            return frames;
        }

        return 0;
    }

    private IPcmSource? Current()
    {
        lock (_gate) return _decoder;
    }

    /// <summary>Retires the dead decoder and says the station is being reconnected.</summary>
    private void Drop()
    {
        IPcmSource? dead;
        lock (_gate) { dead = _decoder; _decoder = null; }
        if (dead is null)
            return;

        try { dead.Dispose(); } catch { }
        IsReconnecting = true;
        _heard = null;                   // whatever it was announcing is no longer true
        Changed?.Invoke();
    }

    private bool Reconnect()
    {
        IPcmSource? opened;
        try { opened = _connect(_station, OnTitle); }
        catch { opened = null; }

        if (opened is null)
            return false;

        bool late;
        lock (_gate)
        {
            late = _disposed;
            if (!late) _decoder = opened;
        }
        if (late)
        {
            try { opened.Dispose(); } catch { }
            return false;
        }

        IsReconnecting = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Receives the tuned station's announcements.
    ///
    /// <para>The fields that an encoder adds are already removed. <see cref="IcyConnection"/> reads
    /// every announcement through <see cref="IcyFields"/> before it is handed here. So everything
    /// downstream (the split, the order, the ad marker, the dial and the header) reads what the station
    /// SAID, and never the encoder fields that carried it.</para>
    ///
    /// <para>A marker that says a break has ENDED is stored as nothing at all. The spots are over, and
    /// no record has been named yet. The header then shows the station, and the readout falls back the
    /// same way it does before the station's first announcement. That is the true state at that
    /// moment.</para>
    /// </summary>
    private void OnTitle(Fielded read)
    {
        _heard = read.Spoken is not { } spoken || RadioAds.IsBreakOver(spoken) || SaysOnlyItself(read)
            ? null
            : new Heard(spoken, read.Named);

        Changed?.Invoke();
    }

    /// <summary>
    /// Whether the fields say that this is the station naming itself and nothing else. For example, the
    /// ident on 102.7 KIIS FM is <c>text="102.7 KIIS FM" song_spot="T"</c>.
    ///
    /// <para>This is stored as nothing, because it adds nothing to a header that already prints the
    /// station's name underneath. The same name on both lines would look like a broken readout. A
    /// station that announces its programme, such as "NPO Radio 2 - RINKELDEKINKEL - BNNVARA", has said
    /// something. That is not an ident, so it is kept.</para>
    /// </summary>
    private bool SaysOnlyItself(Fielded read)
        => read.Named is { IsSong: false, Title: { } said }
           && (IcyAnnouncement.NamesTheStation(said, _station.Name)
               || IcyAnnouncement.NamesTheStation(said, _station.Display));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopping.Set();                 // wake a read parked in the backoff

        IPcmSource? decoder;
        lock (_gate) { decoder = _decoder; _decoder = null; }
        try { decoder?.Dispose(); } catch { }
        _stopping.Dispose();
    }
}

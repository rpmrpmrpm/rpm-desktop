using System.Collections.Concurrent;
using RPM.Core.Audio;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// RPaMp's audio pipeline, owned end to end: ffmpeg decodes the file to float PCM, our
/// <see cref="Equalizer"/> filters it, and an <see cref="IAudioSink"/> plays it.
///
/// Owning the whole pipeline allows pause and resume, live volume and live EQ (nothing is restarted),
/// a play position read from the sink itself, and a spectrum computed from the samples being played.
/// The spectrum is therefore in sync with the sound, and no second decoder is needed. The decoder runs
/// as a child process, so it stops when the app stops.
///
/// <para>The sink can be replaced, which is how casting works. <see cref="OpenAlSink"/> sends the
/// audio to the sound card. <see cref="CastSink"/> sends the same samples, after the EQ, to a Cast
/// speaker. The EQ, the visualiser and the position are all above the sink, so they do not depend on
/// which sink is in use. See <see cref="SetSink"/>.</para>
/// </summary>
public sealed partial class AudioEngine : IDisposable
{
    public const int Rate = 44100;
    public const int Channels = 2;

    private const int FramesPerBuffer = 2205;   // 50 ms

    /// <summary>How far the decoder may work ahead of the pump, on its own thread (see
    /// <see cref="PrefetchPcmSource"/>). This is the margin that absorbs a slow file, a cloud read or
    /// an ffmpeg stall without a break in the sound. It also sets the delay before a live EQ or volume
    /// change is heard, so it is kept to one second.</summary>
    private const int PrefetchFrames = Rate;    // 1 s

    /// <summary>The visualiser reads the ring behind the write head, by however far the sink is
    /// lagging. A sound card lags by milliseconds. A Cast receiver buffers seconds of audio before its
    /// speaker plays it. The ring is sized for the slow case, because a large ring costs
    /// little.</summary>
    private const int SpectrumRingSeconds = 30;

    /// <summary>Guards the bookkeeping. It is deliberately <b>not</b> taken by anything the pump uses
    /// while playing. The pump has a 50 ms deadline and the UI and render threads do not. A lock shared
    /// with them could make the pump wait. The visualiser takes the lock 60 times a second, so the pump
    /// would be blocked often. The pump therefore uses only volatile fields and a single-writer ring,
    /// and it takes no lock at all.</summary>
    private readonly object _gate = new();
    private readonly Equalizer _eq = new(Rate, Channels);

    /// <summary>
    /// The gain for the track being played, as a linear multiplier. A track is played at the level of
    /// the previous track. 1.0 means no change, which is what an unmeasured track gets.
    ///
    /// <para>The writer is whoever starts a track, and the pump reads it. It is volatile and not
    /// locked, because the pump has a 50 ms deadline and must never wait for the UI thread. See
    /// <see cref="_gate"/>.</para>
    /// </summary>
    private volatile float _gainTarget = 1f;

    /// <summary>
    /// The current gain, which moves toward <see cref="_gainTarget"/>. Only the pump writes this.
    ///
    /// <para>The gain moves gradually. A gain that changes in one sample is a step in the waveform, and
    /// a step is heard as a click. This matters most at a gapless change. There, a track at -6 dB is
    /// followed directly by a track at +3 dB, and the device is not told about the change. Moving the
    /// gain over a few milliseconds is not heard. A sudden jump is.</para>
    /// </summary>
    private float _gainNow = 1f;

    /// <summary>The same gain in dB. A replacement sink must be told the gain in dB, and an ffmpeg
    /// filter cannot take the linear value above. Guarded by <see cref="_gate"/>. Only the sink swap
    /// and the setter use it, never the pump.</summary>
    private double _gainDb;

    /// <summary>How far the gain moves toward its target on each frame. A 50 ms block at 44.1 kHz is
    /// 2205 frames. This step size covers the largest gain change in about twenty milliseconds, which
    /// is less than one block. It is too fast to be heard as a fade.</summary>
    private const float GainStepPerFrame = 1f / (Rate / 50);

    private volatile IAudioSink _sink;
    private bool _ownsSink = true;

    /// <summary>Where the samples come from: an ffmpeg decoder for a plain track, or the DJ's two-deck
    /// mix. The pump treats them the same. So a mix goes through the same EQ, visualiser tap, volume
    /// and sink (including a Cast speaker) as a single file.</summary>
    private volatile IPcmSource? _pcm;

    /// <summary>True when <see cref="_pcm"/> is a file that this engine opened, so it can be reopened.
    /// A seek and a sink swap depend on this. False for a source passed in from outside, such as the
    /// DJ's mix. That source has no path to reopen, so it is moved to the new sink instead of
    /// restarted.</summary>
    private bool _sourceIsFile = true;

    private Thread? _pump;
    private volatile bool _running;

    /// <summary>The sink's play position as of the last block the pump handed over. The pump publishes
    /// it, so readers never query the device themselves. The OpenAL context is shared by the whole
    /// process. A FramesPlayed query from the UI or render thread would wait for the pump's buffer
    /// calls, and rendering would stall with each 50 ms audio block. The visualiser only needs an
    /// approximate position, and a block of delay is not visible to it.</summary>
    private long _playHead;

    /// <summary>
    /// Whether <see cref="_playHead"/> belongs to the track that is now loaded.
    ///
    /// <para>The flag is cleared as soon as a track is handed over. It is set again by
    /// <see cref="ResetTap"/>, which runs on the worker after the previous pump has stopped. While a
    /// teardown runs there is a window where the counter still holds the previous track's frames, but
    /// the path and the offset already belong to the new track.</para>
    ///
    /// <para>If the counter is read during that window, it gives the position of the previous track.
    /// The "previous" command uses the position: it restarts the current track when more than a few
    /// seconds have played, and goes back one track otherwise. With the stale value, pressing back
    /// twice in a row would restart the track twice instead of going back. So the flag must be checked
    /// before the counter is used.</para>
    /// </summary>
    private volatile bool _playHeadLive;

    /// <summary>Changed by every Play and Stop. A pump keeps running only while this value still
    /// matches the value it was started with. A pump that outlives its track, because it is still
    /// waiting in Enqueue after the join timeout, cannot be revived by the next Play setting
    /// <see cref="_running"/> back to true.</summary>
    private int _gen;
    private volatile bool _paused;

    /// <summary>Starting and stopping a track blocks. The sink empties the device, the old decoder is
    /// stopped and joined, and ffmpeg is started, which can take seconds on a cold cache. The callers
    /// are UI event handlers. Doing this work on the UI thread froze the window and stalled the
    /// visualiser and the audio. So every heavy change runs here, on one background worker. This also
    /// keeps the changes in order, so a burst of track changes cannot overlap.</summary>
    private readonly BlockingCollection<Action> _commands = new(new ConcurrentQueue<Action>());
    private readonly object _workerGate = new();
    private Thread? _worker;

    /// <summary>What the engine is playing, or null when nothing is playing. Every "is anything
    /// playing?" check reads this. It is volatile so that readers, such as the position readout, the
    /// visualiser and each grid row, never take a lock that the pump might be waiting for.</summary>
    private volatile Playing? _playing;

    /// <summary>
    /// What the engine is playing: the name it is known by, and whether that name is a record. A record
    /// is a track in the collection. It can be opened, looked up, and shown in the track info.
    ///
    /// <para>The two values are stored together, because each one is only correct with the other. A
    /// source that names no track of its own is played under its own label. No library row can match
    /// that label. If the label were kept next to a previous track's "this is a record" flag, the app
    /// would try to open the label as a file. The track info
    /// header would then show a label such as "dj-set:f33270" for the first seconds of a set.</para>
    /// </summary>
    /// <param name="Name">The value that the "is this the row that is playing?" check
    /// compares against.</param>
    /// <param name="IsRecord">Whether <paramref name="Name"/> is a path that the collection
    /// knows.</param>
    /// <param name="Names">The source that names its own tracks, if it has any, such as a DJ set or a
    /// radio station. It is stored here and not read from the pump. The pump runs on a worker that can
    /// lag behind. The state here is set as soon as a play is accepted, but the pump's source changes
    /// only when the worker reaches it. Asking the pump in between would return the previous
    /// record.</param>
    private sealed record Playing(string Name, bool IsRecord, INowPlayingSource? Names)
    {
        /// <summary>A record in the collection, identified by the path the library uses. This applies
        /// whether or not this device has the file. A cloud record still has its library row.</summary>
        public static Playing Track(string path, INowPlayingSource? names = null) => new(path, true, names);

        /// <summary>A stream that plays under its own label. Something is playing, but the label does
        /// not match any record in the library.</summary>
        public static Playing Stream(string label, INowPlayingSource? names) => new(label, false, names);
    }

    /// <summary>What ffmpeg reads: the file at <see cref="Playing.Name"/>, or a URL when the track is
    /// only in the cloud. A seek restarts the decoder, so this value is kept.</summary>
    private string? _source;

    /// <summary>
    /// Where a source passed in from outside can be reopened, if there is such a place. This is the
    /// presigned URL of a cloud track that started from its head clip.
    ///
    /// <para>A source passed in from outside cannot normally be sought. A DJ mix has no file to reopen,
    /// and a decoder cannot start at "half an hour into the set". A streamed track is different. The
    /// clip is only the first few seconds of a recording, and ffmpeg can open that recording at any
    /// offset. So the seek bar keeps working, and a seek opens the full stream.</para>
    /// </summary>
    private string? _seekVia;

    private SinkTrack _track;
    private double _startOffset;
    private double _duration;
    private double? _playLength;
    private int _volume = 100;

    /// <summary>A mono mix of everything sent to the sink, indexed by frames since the track started.
    /// The visualiser reads it at the current play position, so it shows what is heard.
    ///
    /// <para>There is one writer and no lock. Only the pump writes. It updates
    /// <see cref="_specWritten"/> <i>after</i> writing the samples, so a reader that sees a count can
    /// trust the data behind it. Readers check the count again afterwards, in case the writer overwrote
    /// the data they were copying. In practice the writer cannot get that far, because the ring holds
    /// thirty seconds. The ring is not guarded by <see cref="_gate"/>, because that would put the 60 Hz
    /// render thread and the audio thread on the same lock.</para>
    /// </summary>
    private readonly float[] _specRing = new float[Rate * SpectrumRingSeconds];
    private long _specWritten;

    /// <summary>Raised (background thread) when playback starts, pauses, or stops.</summary>
    public event Action? Changed;

    /// <summary>Raised (background thread) only when a track plays out to its end.</summary>
    public event Action? TrackEnded;

    /// <summary>Raised on a background thread when playback is stopped. This happens when the listener
    /// stops it, when another source takes over the player, or at shutdown. It is not raised when a
    /// track plays to its end. That case raises <see cref="TrackEnded"/>. A queue that moves on to the
    /// next track is not a stop.
    ///
    /// <para>The two events are separate because <see cref="Changed"/> cannot tell them apart. It is
    /// raised with nothing playing in both cases. In the short gap between one queued track ending and
    /// the next starting, it reports "nothing is playing" while the queue is still running. Code that
    /// tracks what is loaded must know which of the two events it received.</para>
    /// </summary>
    public event Action? Stopped;

    /// <summary>Raised on a background thread when a track ends and the source queued behind it starts
    /// without the device being touched. This is the gapless changeover. The finished track is already
    /// disposed, and the engine's state already belongs to the new track. A handler only updates its
    /// own bookkeeping. <see cref="TrackEnded"/> is not raised for this track. The queue has already
    /// moved on, and a handler that moved it again would skip a track.</summary>
    public event Action? TrackChained;

    /// <summary>
    /// What follows the current track the instant it plays out, or null. See <see cref="QueueNext"/>.
    /// </summary>
    private ChainLink? _next;

    /// <param name="Playing">The playing state this track will have once it is the one playing. This is
    /// the same value that <see cref="PlaySource"/> sets. It is set here in advance, because the change
    /// happens on the pump, where no caller is available.</param>
    /// <param name="SeekVia">Where this track can be reopened at another offset once it is playing.
    /// This is the presigned URL of a streamed track. It is null for a local file, because the file
    /// path is already in <see cref="SinkTrack.Path"/>.</param>
    /// <param name="Gain">The gain of the next track. It is stored here because the change happens on
    /// the pump, where no caller is available. Without it, the next track would play at the previous
    /// track's level until something else set the gain. In a gapless join, that is the whole
    /// track.</param>
    private sealed record ChainLink(
        IPcmSource Source, SinkTrack Track, IPcmSource Declared, Playing Playing,
        string? SeekVia = null, float Gain = 1f);

    /// <summary>The number of frames the pump has taken from its sources since <see cref="ResetTap"/>.
    /// Only the pump writes it, the same as the spectrum ring.</summary>
    private long _enqueued;

    /// <summary>
    /// The point in the sink's frame count where the current track began.
    ///
    /// <para>This is zero for a track started normally. A chained track starts in the middle of the
    /// stream, because the sink is not told about the change. So its position and heard time are the
    /// sink's count minus this value. The value is set to the number of frames queued at the moment of
    /// the chain. The device is still playing the end of the old track then, so the readings are
    /// clamped at zero and never go negative.</para>
    /// </summary>
    private long _chainBase;

    // The phone build compiles this same file (see RPM.Mobile.csproj), so a DJ set on the phone uses
    // this same pipeline. The output device is not included. OpenAL is the desktop sink. The phone
    // uses its own AudioQueue sink, so on the phone a sink is always passed in and no default is used.
#if !__IOS__
    public AudioEngine() : this(new OpenAlSink()) { }
#endif

    public AudioEngine(IAudioSink sink)
    {
        _sink = sink;
    }

    // ── state ──

    /// <summary>Where the audio is currently going. Swap it with <see cref="SetSink"/>.</summary>
    public IAudioSink Sink => _sink;   // volatile field: the pump reads this, so no lock

    public bool IsAvailable => Sink.IsAvailable;

    /// <summary>Whether the current playback can be sought. A file can be sought. A DJ mix cannot,
    /// because there is no file to reopen at another offset. The player disables the seek bar when this
    /// is false, so that the user cannot drag it to no effect.</summary>
    public bool CanSeekCurrent { get { lock (_gate) return _sourceIsFile || _seekVia is not null; } }

    /// <summary>What is playing, as the rest of the app identifies a track: a file path. A source that
    /// moves between tracks inside one stream, such as a DJ mix, reports the track it is on. So the
    /// artwork, the playing row in the grid and the album badge follow the mix from record to record.
    /// They do not show a single unnamed stream for the whole set.</summary>
    /// <remarks><see cref="_playing"/> is checked first. It says whether anything is playing. Stop
    /// clears it at once, but the source it was reading is released later, on the worker. Asking the
    /// source first would keep naming a record during that gap.</remarks>
    public string? PlayingPath =>
        _playing is not { } playing ? null : playing.Names?.NowPlayingTrack?.Path ?? playing.Name;

    /// <summary>The record behind what is playing, as a path the collection can use. This is null when
    /// there is no record. Examples are a mix before its first record starts, and a radio stream at any
    /// time. Code that opens or shows the playing item must use this value.
    ///
    /// <para><see cref="PlayingPath"/> is only for matching. It returns the stream's own label when
    /// there is no record. That keeps "is anything playing?" true during a changeover, and no grid row
    /// is lit. The label is not the name of a real record, so it must not be shown on screen.</para>
    /// </summary>
    public string? PlayingRecord
    {
        get
        {
            if (_playing is not { } playing) return null;

            // A source that plays several tracks in turn reports the track it is on, and whether that
            // track is a record. The DJ's tracks are records. A radio station's track is its own
            // address. When it is not on a track, such as a set whose first record is still starting,
            // the answer is the name the play was given. For a set, that name is a label and not a
            // record.
            if (playing.Names?.NowPlayingTrack is { } now)
                return now.IsRecord ? now.Path : null;

            return playing.IsRecord ? playing.Name : null;
        }
    }

    /// <summary>
    /// The track a multi-track source is on right now, such as the record in a mix or the song a radio
    /// station is announcing. This is null when the playing item is a single track from start to end.
    /// That kind of track is described by its own tags.
    /// </summary>
    public NowPlayingTrack? NowPlaying => _playing?.Names?.NowPlayingTrack;

    /// <summary>
    /// The source that was passed in, for a caller that needs to know whether its own source is still
    /// the one playing. <see cref="PlayingPath"/> changes with the music, so it cannot answer that.
    ///
    /// <para>This is the source the engine has been <em>told</em> to play. It may not yet be the source
    /// the pump has taken. <see cref="PlaySource"/> passes the work to the audio worker and raises
    /// Changed at once. A handler that asks during that call would still see the previous track, and it
    /// would wrongly conclude that its source was replaced before it started.</para>
    /// </summary>
    public object? PlayingSource { get { lock (_gate) return _declared; } }

    /// <summary>Backing field for <see cref="PlayingSource"/>. It is set as soon as a source is
    /// accepted, and cleared when playback of that source ends, by any route.</summary>
    private IPcmSource? _declared;

    public bool IsPaused => _paused;

    /// <summary>The length of what is playing. During a mix this is the length of the current record,
    /// so the progress bar fills over the track and not over the whole set.</summary>
    public double DurationSeconds
    {
        get
        {
            if (_playing?.Names?.NowPlayingTrack is { DurationSeconds: > 0 } now)
                return now.DurationSeconds;
            lock (_gate) return _duration;
        }
    }

    /// <summary>Play position taken from the sink (frames actually played), not a clock.</summary>
    public double PositionSeconds
    {
        get
        {
            if (_playing is not { } playing) return 0;

            // A mix reports the position within the record that is playing. A frame count since Begin
            // would give the position within the whole set. That value passes every track's length and
            // would pin the bar at the end.
            if (playing.Names?.NowPlayingTrack is { } now)
                return now.DurationSeconds > 0
                    ? Math.Clamp(now.PositionSeconds, 0, now.DurationSeconds)
                    : Math.Max(0, now.PositionSeconds);

            double start, duration;
            lock (_gate) { start = _startOffset; duration = _duration; }
            // This uses the published play head (see _playHead). The position readout is polled from
            // the UI thread several times a second. Asking the device from there would stall it against
            // the pump.
            var seconds = start + (_playHeadLive ? TrackFrames() / (double)Rate : 0);
            return duration <= 0 ? seconds : Math.Min(duration, seconds);
        }
    }

    /// <summary>
    /// How many seconds of this track have actually been delivered. This is what was <em>heard</em>. It
    /// is not the same as the needle position. See <see cref="PositionSeconds"/>.
    ///
    /// <para>
    /// This value is kept after the track ends. The path is cleared as soon as the pump drains. So a
    /// position read in a <c>TrackEnded</c> handler is zero, but this value still holds the full track
    /// length.
    /// </para>
    ///
    /// <para>The value covers only this stretch of listening. A seek starts a new decoder at another
    /// offset, so the value starts again from zero. <see cref="RPM.Core.Playback.ListenTally"/> adds
    /// the stretches together. <see cref="PlaySession"/> tells it a rewind from a gapless
    /// change.</para>
    ///
    /// <para>This value is what is needed to decide whether a track counts as listened to. See
    /// <see cref="RPM.Core.Playback.Listen"/>.</para>
    /// </summary>
    public double HeardSeconds => _playHeadLive ? TrackFrames() / (double)Rate : 0;

    /// <summary>
    /// The run of the pump that the readings belong to. It changes on every Play, on every seek (a Play
    /// at another offset), and on every Stop. It does <em>not</em> change at a gapless changeover.
    ///
    /// <para>So it is the one reliable answer to "has the counter been reset?" The
    /// <see cref="RPM.Core.Playback.ListenTally"/> uses it to add up listens that were interrupted by
    /// seeks. Without it, only the longest unbroken part would count.</para>
    /// </summary>
    public int PlaySession => Volatile.Read(ref _gen);

    /// <summary>Where the current run of the pump started, in seconds from the start of the track. This
    /// is zero for a track played from the beginning, and the seek target after a seek.</summary>
    public double StartOffsetSeconds { get { lock (_gate) return _startOffset; } }

    /// <summary>The sink's frame count for the <em>current</em> track. It counts from the point where a
    /// chained track began, and it is never negative. At the moment of a chain, the device is still
    /// playing the end of the previous track. See <see cref="_chainBase"/>.</summary>
    private long TrackFrames() =>
        Math.Max(0, CurrentFrames() - Volatile.Read(ref _chainBase));

    /// <summary>
    /// The sink's play position. It is read live from the sink when that is safe to do here. Otherwise
    /// it is the last value the pump published.
    ///
    /// <para>A Cast sink needs the live read. The pacer pauses the pump for seconds at a time, and the
    /// drain at the end of a track does not run the pump at all. So a published value stops moving
    /// while the speaker is still playing. The seek bar would then freeze, jump, and stay still for the
    /// last ten seconds of each track. A Cast sink reports its own clock, which is cheap to read. A
    /// local device needs a query that must run on the pump thread, so it stays there.</para>
    /// </summary>
    private long CurrentFrames() =>
        Sink is { ReportsPositionItself: true } remote ? remote.FramesPlayed : Volatile.Read(ref _playHead);

    /// <summary>
    /// How far the source is ahead of what is heard: everything decoded but not yet played.
    ///
    /// <para>There are two buffers. The decode-ahead ring holds up to <see cref="PrefetchFrames"/> that
    /// the pump has not taken yet. The sink holds audio that was handed over but not played. That is a
    /// few milliseconds for a sound card, and seconds for a Cast receiver.</para>
    ///
    /// <para>This matters to anything that draws where the source is. A source's position counts what
    /// it has <em>produced</em>. What is heard is behind that by the buffered amount. A progress bar
    /// that is a second off is rarely noticed. A marker on a waveform that is a second off is in the
    /// wrong place, and a mix cannot be judged from it.</para>
    /// </summary>
    public double OutputLagSeconds
    {
        get
        {
            // Both values count output frames, so their difference is a duration whatever the source is
            // doing. The result is never negative. The two values are published by different threads
            // and may be read a block apart.
            long queued = _pcm is PrefetchPcmSource ahead ? ahead.BufferedFrames : 0;
            long undrained = Math.Max(0, Volatile.Read(ref _enqueued) - CurrentFrames());
            return (queued + undrained) / (double)Rate;
        }
    }

    /// <summary>
    /// How the last track ended: it reached its end, or it failed.
    ///
    /// <para>Read this from a <see cref="TrackEnded"/> handler, because that event is raised in both
    /// cases. A queue that moves on for either case turns a stream that failed into the next track a
    /// few seconds later. Only <see cref="PcmState.Ended"/> means the track finished normally.</para>
    /// </summary>
    public PcmState EndedAs { get; private set; } = PcmState.Ended;

    /// <summary>The reason, when <see cref="EndedAs"/> is <see cref="PcmState.Failed"/>.</summary>
    public string? EndFault { get; private set; }

    public int Volume { get { lock (_gate) return _volume; } }

}

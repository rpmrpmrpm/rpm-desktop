using System.Collections.Concurrent;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>The operations the rest of the app asks the engine to perform: play, stop, pause, seek and
/// change the volume. It also covers everything the engine must get right underneath those operations.
/// </summary>
public sealed partial class AudioEngine
{
    // ── control ──

    /// <summary>
    /// Moves playback to another destination, such as the sound card or a Cast receiver. Playback
    /// continues from the same position. The decoder restarts at the current position, using the same
    /// code as a seek. A switch in the middle of a track causes a short pause, the same as a seek. The
    /// playlist, the EQ and the queue are not affected.
    /// </summary>
    /// <param name="sink">The new destination. The engine takes ownership unless
    /// <paramref name="ownsSink"/> says otherwise.</param>
    /// <param name="ownsSink">False when the caller disposes the sink itself. This is the case for a
    /// Cast session, which can be reused after playback switches away from it.</param>
    public void SetSink(IAudioSink sink, bool ownsSink = true)
    {
        ArgumentNullException.ThrowIfNull(sink);

        Playing? playing;
        string? source;
        string? seekVia;
        double duration, at;
        double? length;
        bool wasPaused;
        IPcmSource? handed = null;
        IPcmSource? declared = null;
        lock (_gate)
        {
            if (ReferenceEquals(_sink, sink)) return;
            playing = _playing; source = _source; duration = _duration; length = _playLength;
            wasPaused = _paused;
            declared = _declared;
            seekVia = _seekVia;

            // A DJ mix cannot be restarted like a file, because it has no path to reopen. Take the
            // source out of the engine's hands before the teardown. The teardown would otherwise
            // dispose it. The source is given to the new sink instead. The mix continues across the
            // switch. The listener hears only a short re-buffering gap.
            if (!_sourceIsFile && _pcm is not null)
            {
                handed = _pcm;
                _pcm = null;
            }
        }
        at = PositionSeconds;

        // Runs inline. The old sink is disposed a few lines below, so the pump must stop using it
        // first.
        StopInternal(raise: false, inline: true);

        IAudioSink? old;
        bool disposeOld;
        lock (_gate)
        {
            old = _sink;
            disposeOld = _ownsSink;
            _sink = sink;
            _ownsSink = ownsSink;
        }

        sink.SetVolume(_volume);
        sink.KeepAhead(_workAhead is { } asked ? asked / 2 : SinkAheadSeconds);

        // The new sink has not yet been told the gain of the current track. This matters for one case:
        // a surround cast started during playback. There, the encoder is the only part of the app that
        // applies the gain itself.
        double gainDb;
        lock (_gate) gainDb = _gainDb;
        try { sink.SetTrackGain(gainDb); } catch { }

        if (disposeOld) old.Dispose();

        if (handed is not null && sink.IsAvailable)
        {
            // The source is declared as what it was before the switch, such as a mix or a radio
            // station. It is not declared as the buffer it is passed in. See the note on the
            // declared parameter of PlaySource. The source is mid-track, so playback continues from
            // the same position and the readout keeps that position.
            //
            // The source's identity and its seek address are passed on as well. A cloud track sent to a
            // speaker is still the same library row, and it can still be sought. A stream keeps its own
            // label. Passing that label as a path would make it look like a library record.
            PlaySource(handed, duration, _track.Title, _track.Artist, declared, startSeconds: at,
                       path: playing is { IsRecord: true } record ? record.Name : null,
                       seekVia: seekVia, album: _track.Album);
            if (wasPaused) Pause();
        }
        else if (handed is not null)
        {
            handed.Dispose();          // nowhere to play it: the set is over
            Changed?.Invoke();
        }
        else if (playing is { IsRecord: true } record && sink.IsAvailable)
        {
            // Switching to a Cast speaker also restarts the decoder. A cloud track has no local file
            // to restart from, so the restart reads from its source URL.
            Play(record.Name, duration, at, length, _track.Title, _track.Artist, _track.Album, source);
            if (wasPaused) Pause();
        }
        else
        {
            // Nothing is playing, or the stream's source is not held by this engine. In that case the
            // pump had not yet taken the source when the speaker changed, and this switch replaces the
            // start that was on its way. Nothing is carried over. The stream's label is not a file
            // that can be opened, so the stream is not restarted from it.
            Changed?.Invoke();
        }
    }

    /// <param name="source">
    /// Where the bytes come from, when that differs from <paramref name="path"/>. This is a presigned
    /// URL for a track whose local file was moved to the cloud. The decoder is ffmpeg, and <c>-i</c>
    /// accepts a URL as well as a file. Ranges work, so a streamed track seeks like a local one.
    /// <paramref name="path"/> stays the track's identity. Code that checks "is this the row that is
    /// playing?" compares against that path.
    /// </param>
    /// <returns>False when the sink is not available and nothing was started. The caller must show
    /// this on screen. Otherwise a refused play looks like the app ignored the click.</returns>
    public bool Play(
        string path,
        double durationSeconds,
        double startSeconds,
        double? playLengthSeconds = null,
        string? title = null,
        string? artist = null,
        string? album = null,
        string? source = null)
    {
        var sink = Sink;
        if (!sink.IsAvailable)
        {
            // A local sink that could not open its device keeps trying to reopen it while idle (see
            // OpenAlSink.ReopenWhileIdle). The player shows the reason on screen.
            Said.Line("audio", $"{sink.GetType().Name} has no device — '{System.IO.Path.GetFileName(path)}' was not started");
            Refuse();
            return false;
        }

        SinkTrack track;
        double start = Math.Max(0, startSeconds);
        lock (_gate)
        {
            _playing = Playing.Track(path);
            _source = source ?? path;
            _seekVia = null;   // a normal decode: _source is where a seek reopens the file
            _duration = durationSeconds;
            _startOffset = start;
            _playHeadLive = false;   // false until the worker resets the play head (see the field)
            _playLength = playLengthSeconds;
            _paused = false;
            _sourceIsFile = true;
            _declared = null;
            _track = track = new SinkTrack(
                path,
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title,
                artist,
                album,
                durationSeconds,
                start);
        }

        // The old playback is retired, and the blocking work runs on the worker thread. The state above
        // is already set, so PlayingPath and the playing-row highlight are correct at once, even
        // though the device and the decoder are still starting.
        int gen = Interlocked.Increment(ref _gen);
        string decodeFrom = source ?? path;
        Post(() => PlayCore(gen, track, decodeFrom, start, playLengthSeconds));
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Plays a source that the engine did not open itself, such as the DJ's two-deck mix. The source is
    /// then handled like a decoded file, with the same pump, EQ, visualiser tap, volume and sink. So a
    /// mix can be sent to a speaker and shows in the spectrum like any other track.
    /// </summary>
    /// <param name="source">Taken over by the engine, and disposed when playback stops.</param>
    /// <param name="durationSeconds">The expected length of the whole set, used for the position
    /// readout. It is an estimate, because the tracks in a mix overlap. Nothing depends on it being
    /// exact.</param>
    /// <param name="declared">
    /// What <see cref="PlayingSource"/> reports, when that is not <paramref name="source"/> itself.
    /// Only <see cref="SetSink"/> passes this. A switch hands over the decode-ahead buffer, not the
    /// source inside it. Code that checks "is this still what is playing?" compares against the source
    /// inside the buffer. Without this value, a radio that was cast would look like it had stopped, and
    /// the tuner would switch the station off.
    /// </param>
    /// <param name="startSeconds">
    /// The position in the track where the source already is. This is zero for a new source. Only
    /// <see cref="SetSink"/> passes a larger value, when a source that is mid-track moves to another
    /// speaker. Playback continues from that position, and the readout shows it.
    /// </param>
    /// <param name="path">
    /// The track this source is, when it is a track. This is the library path of a cloud track started
    /// from its head clip. Code that checks "is this the row that is playing?" compares against this
    /// path. Without it, a streamed track plays with no row lit and no artwork shown.
    /// </param>
    /// <param name="seekVia">
    /// Where the same recording can be opened at another offset. For a streamed track this is the
    /// presigned URL. Passing it keeps the seek bar working. See <see cref="_seekVia"/>.
    /// </param>
    /// <returns>False when the sink is not available. The source is then disposed and nothing is
    /// started.</returns>
    public bool PlaySource(
        IPcmSource source, double durationSeconds, string? title = null, string? artist = null,
        IPcmSource? declared = null, double startSeconds = 0, string? path = null, string? seekVia = null,
        string? album = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sink = Sink;
        if (!sink.IsAvailable)
        {
            Said.Line("audio", $"{sink.GetType().Name} has no device — '{source.Label}' was not started");
            source.Dispose();
            Refuse();
            return false;
        }

        SinkTrack track;
        double start = Math.Max(0, startSeconds);
        lock (_gate)
        {
            // When the caller gives no path, the source's label is used in its place. The label is what
            // "is something playing?" checks against. No library row matches it, so no row is lit as
            // playing. A cloud track gives its own path, because it has a library row. Which of the two
            // applies is stored with the playing state (see Playing), so a label is never shown or
            // opened as if it were a record.
            var names = (declared ?? source) as INowPlayingSource;
            _playing = path is null ? Playing.Stream(source.Label, names) : Playing.Track(path, names);
            _source = seekVia ?? _playing.Name;
            _seekVia = seekVia;
            _duration = durationSeconds;
            _startOffset = start;
            _playHeadLive = false;   // the counter still holds the previous track's value (see the field)
            _playLength = null;
            _paused = false;
            _sourceIsFile = false;
            _declared = declared ?? source;
            _track = track = new SinkTrack(
                _playing.Name, title ?? source.Label, artist, album, durationSeconds, start);
        }

        int gen = Interlocked.Increment(ref _gen);
        Post(() => PlaySourceCore(gen, track, source));
        Changed?.Invoke();
        return true;
    }

    /// <summary>Whether something is already queued to follow the current track. What a caller asks
    /// before opening a successor, so two preparations do not race for one slot.</summary>
    public bool HasQueuedNext { get { lock (_gate) return _next is not null; } }

    /// <summary>
    /// The file already opened for the gapless changeover. It waits until the current track ends.
    ///
    /// <para>This is null when nothing is queued. It is also null when the queued track is a stream,
    /// because a stream is a URL and has no file. Use this together with <see cref="PlayingPath"/> to
    /// find every file the engine has open. <see cref="PlayingPath"/> gives the current track. During a
    /// gapless join there are two open files.</para>
    /// </summary>
    public string? QueuedNextPath { get { lock (_gate) return _next?.Track.Path; } }

    /// <summary>
    /// Sets the track to play as soon as the current one ends. This is the gapless changeover.
    ///
    /// <para>
    /// The sink is not told about the change. When the current source ends, the pump switches to the
    /// new source in the middle of the stream, and the sink keeps playing. There is no stop, no begin,
    /// no buffer reallocation and no silence. This only happens when the track really finished. A
    /// source that failed uses the normal end-of-track path, with its retries.
    /// </para>
    ///
    /// <para>The new source is buffered from the moment it is queued. Queuing early means its first
    /// seconds are already decoded when the change happens. A later call replaces the queued source.
    /// The queued source is also dropped when playback stops or starts something else. A queued track
    /// never outlives the track it was queued behind.</para>
    /// </summary>
    /// <param name="source">Taken over by the engine, whether or not it is played.</param>
    /// <param name="path">The track this source is, as the library identifies it. The playing-row
    /// highlight, the artwork and the queue compare against it after the change.</param>
    /// <param name="seekVia">Where the same recording can be opened at another offset once it is
    /// playing. This is the file, or the presigned URL of a streamed track. Without it, after a gapless
    /// change the scrubber would do nothing.</param>
    /// <param name="gainDb">The gain for this track, in dB. It is stored with the source because the
    /// change happens on the pump, where no caller is available to ask. See
    /// <see cref="SetTrackGain"/>.</param>
    public void QueueNext(
        IPcmSource source, double durationSeconds, string? title = null, string? artist = null,
        string? path = null, string? seekVia = null, string? album = null, double gainDb = 0)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffered = source as PrefetchPcmSource
            ?? new PrefetchPcmSource(source, Channels, PrefetchFrames, FramesPerBuffer);

        var names = source as INowPlayingSource;
        var playing = path is null ? Playing.Stream(source.Label, names) : Playing.Track(path, names);
        var track = new SinkTrack(
            playing.Name, title ?? source.Label, artist, album, durationSeconds, 0);

        var link = new ChainLink(buffered, track, source, playing, seekVia,
            RPM.Core.Analysis.ReplayGain.ToLinear(gainDb));

        ChainLink? drop;
        lock (_gate)
        {
            if (_playing is null)
            {
                drop = link;    // nothing is playing, so there is no track for this one to follow
            }
            else
            {
                drop = _next;
                _next = link;
            }
        }

        drop?.Source.Dispose();
    }

    /// <summary>Drops the source queued to play next. Used when the queue is edited, because the
    /// prepared source may no longer be the one that should follow.</summary>
    public void ClearQueuedNext()
    {
        ChainLink? drop;
        lock (_gate) { drop = _next; _next = null; }
        drop?.Source.Dispose();
    }

    /// <summary>
    /// Moves the pump onto the queued next source, if the source that just ended was a normal end.
    ///
    /// <para>This runs on the pump thread, at the one point where taking the lock is safe. The sink
    /// still holds queued audio, so there is the same time margin as on the drain path. The finished
    /// source is disposed on the worker thread. Disposing it waits for its decode thread to stop, which
    /// may take longer than the pump's 50 ms budget.</para>
    /// </summary>
    private bool TryChain(int gen)
    {
        lock (_gate) if (_next is null) return false;

        // Only a source that ended normally is chained. A failed source keeps the ordinary ending,
        // where EndedAs and the retries are handled. Chaining over it would turn a dead stream into the
        // next track.
        if (_pcm is not { State: PcmState.Ended }) return false;

        ChainLink link;
        IPcmSource? finished;

        lock (_gate)
        {
            if (_next is null || Volatile.Read(ref _gen) != gen) return false;

            link = _next;
            _next = null;

            finished = _pcm;
            _pcm = link.Source;

            _playing = link.Playing;
            _source = link.SeekVia ?? link.Track.Path;
            _seekVia = link.SeekVia;
            _duration = link.Track.DurationSeconds;
            _startOffset = 0;
            _sourceIsFile = false;
            _declared = link.Declared;
            _track = link.Track;
        }

        // The new track's gain is adopted when the pump starts reading it. The gain slides to the new
        // value instead of jumping, so a join between a -6 dB track and a +3 dB track is heard as a
        // smooth level change, not as a click.
        _gainTarget = link.Gain;
        double chainedDb = 20 * Math.Log10(Math.Max(1e-6, link.Gain));
        lock (_gate) _gainDb = chainedDb;
        try { Sink.SetTrackGain(chainedDb); } catch { }

        // From here on, the position readings belong to the new track. The device is still playing the
        // end of the old track, so the readings are clamped at zero and never go negative. See
        // _chainBase.
        Volatile.Write(ref _chainBase, _enqueued);

        // The sink is not restarted, because restarting would cause a gap. A Cast receiver still shows
        // the previous track's title, and it has no other way to learn the new one. Do not call Begin
        // here, because beginning again causes the gap this path avoids.
        try { Sink.TrackChanged(link.Track); } catch { /* a failed title update must not stop playback */ }

        Post(() =>
        {
            try { finished?.Dispose(); } catch { /* already disposed */ }

            if (Volatile.Read(ref _gen) != gen) return;

            Changed?.Invoke();
            TrackChained?.Invoke();
        });

        return true;
    }

    /// <summary>The blocking part of <see cref="PlaySource"/>, run on the worker thread. It retires the
    /// old track, starts the device, and pumps the given source. No decoder is opened.</summary>
    private void PlaySourceCore(int gen, SinkTrack track, IPcmSource source)
    {
        Teardown();
        if (Volatile.Read(ref _gen) != gen)
        {
            source.Dispose();
            return;
        }

        var sink = Sink;
        ResetTap();
        BeginOn(sink, track);

        // The source is placed behind the same decode-ahead buffer that a file uses. A mix is the most
        // demanding source. It runs two ffmpeg pipes at once, and it opens and closes decoders on each
        // speed change. If the pump read from it directly, the device would often run out of audio. A
        // crackle at a changeover is a block that arrives late.
        //
        // A source that is already a buffer is used as it is. A source moved between devices (SetSink)
        // arrives as the buffer it was already behind, and it keeps reading on its own thread. Wrapping
        // it in a second buffer would add a second decode thread and a second ring each time the sound
        // moved to a speaker and back.
        _pcm = source as PrefetchPcmSource
            ?? new PrefetchPcmSource(source, Channels, PrefetchFrames, FramesPerBuffer);

        // Applies the requested depth to the ring that was just built, as well as to the sink.
        WorkAhead(_workAhead);

        if (Volatile.Read(ref _gen) != gen)
            return;

        StartPump(gen);
    }

    /// <summary>The default lead, in seconds, that the decode ring keeps ahead of the speakers. The
    /// ring is sized by <see cref="PrefetchFrames"/>.</summary>
    private const double RingAheadSeconds = 1.0;

    /// <summary>The default lead, in seconds, that the sink keeps ahead of the speakers. See
    /// OpenAlSink.BufferCount.</summary>
    private const double SinkAheadSeconds = 0.5;

    private double? _workAhead;

    /// <summary>
    /// How much audio may be buffered between the decoder and the speakers, in seconds. Null selects
    /// the defaults that suit a music player.
    ///
    /// <para>This value sets the delay between doing something to a source and hearing the result. The
    /// audio is decoded ahead, so that a slow file read, a cloud read or an ffmpeg stall does not
    /// interrupt the sound. It is also queued ahead on the device, so that a scheduling delay does not
    /// interrupt it. Every buffered frame was prepared before the most recent action. A music player
    /// can accept this delay, because a volume change that takes effect a second late is not noticed. A
    /// test bench needs a short delay, because a measurement cannot be judged if the response comes a
    /// second late.</para>
    ///
    /// <para>The value is split evenly between the decode ring and the sink. Both hold the same margin,
    /// and the sound breaks when either one runs out.</para>
    /// </summary>
    public void WorkAhead(double? seconds)
    {
        _workAhead = seconds;

        double ring = seconds is { } r ? r / 2 : RingAheadSeconds;
        double sink = seconds is { } s ? s / 2 : SinkAheadSeconds;

        if (_pcm is PrefetchPcmSource ahead)
            ahead.KeepAheadFrames = (int)(ring * Rate);
        Sink.KeepAhead(sink);
    }

    /// <summary>Resets the play head and the visualiser's ring for a new track. Call this on the worker
    /// thread only, and only after <see cref="Teardown"/> has stopped the previous pump. Each of those
    /// fields then has one writer at a time, so the pump can write them without a lock.</summary>
    private void ResetTap()
    {
        Volatile.Write(ref _playHead, 0);       // Begin resets the device's own count; keep ours in step
        Volatile.Write(ref _specWritten, 0);
        Volatile.Write(ref _chainBase, 0);
        _enqueued = 0;
        _eq.Reset();

        // A new track started from the start is not a gapless change. The device was stopped and
        // started again, so there is no waveform to fade in. The gain is set to its target at once, so
        // the first part of the track is not played at the previous track's level.
        _gainNow = _gainTarget;

        // This is the last step. From here on the counter belongs to this track, and code that reads
        // the position can use it again.
        _playHeadLive = true;
    }

    private void StartPump(int gen)
    {
        _running = true;
        _pump = new Thread(() => Pump(gen)) { IsBackground = true, Name = "RPaMp audio" };
        _pump.Start();
    }

    /// <summary>
    /// Call this when a multi-track source has moved to another track. The position is polled, so the
    /// clock needs no push. The artwork and the playing-row highlight update only when this is raised.
    /// The event is raised on the caller's thread. Each handler switches threads itself if it needs to.
    /// </summary>
    public void NotifyNowPlayingChanged() => Changed?.Invoke();

    /// <summary>Queues a transition to run on the audio worker. The worker starts on first use. It is
    /// the only thread that runs transitions, so they run in order.</summary>
    private void Post(Action command)
    {
        if (_worker is null)
        {
            lock (_workerGate)
            {
                if (_worker is null)
                {
                    _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "RPaMp audio commands" };
                    _worker.Start();
                }
            }
        }
        try { _commands.Add(command); } catch (InvalidOperationException) { /* the engine is disposed */ }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                try { command(); } catch { /* a failed transition must not stop the worker */ }
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Starts the track on the sink, and sets the sink to the same paused or playing state as
    /// the player.
    ///
    /// <para>The pause state must be applied here, and this is the only place it can be applied. A sink
    /// cannot be paused before it has begun. Both sink types ignore a pause while stopped, and
    /// beginning clears the pause flag. A pause can still be in effect when a track starts.
    /// <see cref="SetSink"/> carries the pause across a speaker change, and the start is queued on the
    /// worker. Applying the pause here means it reaches the sink in every thread order, because it is
    /// read from the engine state after the sink exists.</para>
    ///
    /// <para>Without this step, the pause would be lost. The transport would show paused, while the
    /// track kept playing on the speaker. The pump does not handle pausing. A pause is the sink
    /// standing still, and this sink would never have been told to stand still.</para>
    /// </summary>
    private void BeginOn(IAudioSink sink, SinkTrack track)
    {
        sink.Begin(track, Rate, Channels);
        if (_paused) sink.Pause();
    }

    /// <summary>The blocking part of Play, run on the worker. It tears down the old track, starts the
    /// device and the decoder, and starts the pump. Each step stops early if a newer Play or Stop has
    /// replaced this one.</summary>
    private void PlayCore(int gen, SinkTrack track, string decodeFrom, double start, double? playLengthSeconds)
    {
        Teardown();
        if (Volatile.Read(ref _gen) != gen) return;

        var sink = Sink;
        ResetTap();
        BeginOn(sink, track);

        if (!StartDecoder(decodeFrom, start, playLengthSeconds))
        {
            sink.Stop();
            if (Volatile.Read(ref _gen) == gen) _playing = null;
            Changed?.Invoke();
            return;
        }

        if (Volatile.Read(ref _gen) != gen) return;

        StartPump(gen);
    }

    public void Pause()
    {
        if (_paused || PlayingPath is null) return;
        _paused = true;
        Sink.Pause();
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (!_paused || PlayingPath is null) return;
        _paused = false;
        Sink.Resume();
        Changed?.Invoke();
    }

    public void Stop() => StopInternal(raise: true);

    /// <summary>
    /// A play that the sink refuses ends as a stop. The previous playback is retired. On a sink that is
    /// not available, nothing was being heard anyway. <see cref="Stopped"/> and <see cref="Changed"/>
    /// are raised, as they are for any stop.
    ///
    /// <para>This clears the track from the queue's now-playing slot. The queue sets the slot before it
    /// asks for the start, and only a stop clears it. Without this, a refused start would leave the
    /// header, the spinning record and the mini player showing a track that is not playing.</para>
    /// </summary>
    private void Refuse() => StopInternal(raise: true);

    /// <summary>Restarts the decoder at the given second, and discards the audio the sink still
    /// holds.</summary>
    public void SeekTo(double seconds)
    {
        string? path;
        string? source;
        double duration;
        double? length;
        SinkTrack track;
        bool isFile;
        string? seekVia;
        lock (_gate)
        {
            path = _playing?.Name; source = _source; duration = _duration; length = _playLength; track = _track;
            isFile = _sourceIsFile;
            seekVia = _seekVia;
        }
        if (path is null) return;

        // A mix has no file that can be reopened at another offset, so seeking it is not possible. A
        // source with no file and no seek address cannot be seeked either.
        //
        // A cloud track started from its head clip can be seeked. The clip is the start of a recording
        // behind a URL, and the seek opens that URL at the new position. From then on the track plays
        // as a normal decode.
        if (!isFile && seekVia is null) return;

        // A seek opens a new decoder, and it must use the same source as the first one. A cloud track
        // has no local file, so the name it plays under cannot be used. Using that name would open a
        // path that does not exist, and the track would stop.
        Play(path, duration, Math.Clamp(seconds, 0, Math.Max(0, duration - 0.25)), length,
             track.Title, track.Artist, track.Album, seekVia ?? source);
    }

    /// <summary>Takes effect immediately on the sink.</summary>
    public void SetVolume(int volume)
    {
        var v = Math.Clamp(volume, 0, 100);
        lock (_gate) _volume = v;
        Sink.SetVolume(v);
    }

    /// <summary>
    /// The gain for the track now playing, in dB. It sets how far the track is moved to match the level
    /// of the previous track. Zero leaves the track as it was recorded.
    ///
    /// <para>The change takes effect at once, like the volume and the EQ. The gain slides to the new
    /// value over about twenty milliseconds instead of jumping. Turning normalisation on during
    /// playback is heard as a level change, not as a click. The gain is set per track. See
    /// <see cref="QueueNext"/> for how the next track in a gapless join gets its own gain.</para>
    /// </summary>
    public void SetTrackGain(double gainDb)
    {
        lock (_gate) _gainDb = gainDb;
        _gainTarget = RPM.Core.Analysis.ReplayGain.ToLinear(gainDb);

        // The gain is also sent to the sink. One output needs it this way: a surround cast re-encodes
        // the source file and never sees the samples this code changes. Every other sink ignores it.
        try { Sink.SetTrackGain(gainDb); } catch { }
    }

    /// <summary>Takes effect at once. The new coefficients apply from the next block of about 50 ms.
    /// The playback is not restarted.</summary>
    /// <remarks>
    /// The tone shelves are passed as 0 dB rather than left out. Shelves are used for vinyl or cassette
    /// transfers, where they shape a recording before it is written to disk. The player's EQ has no
    /// shelves, so 0 dB is the correct value.
    /// </remarks>
    public void SetEqualizer(double preampDb, IReadOnlyList<double> bandGainsDb)
        => _eq.SetGains(preampDb, bandGainsDb, bassDb: 0, trebleDb: 0);
}

namespace RPM.Core.Audio;

/// <summary>
/// Where the post-EQ samples go. The decoder and the equaliser do not care whether the audio
/// ends up in the sound card or in another room, so the destination is the only thing that
/// varies: the local sound device, or a Cast receiver.
///
/// <para><b>Enqueue is the clock.</b> A sink must not accept samples faster than they are consumed. It
/// blocks instead. That backpressure paces the decoder, so nothing needs a timer and nothing runs ahead of
/// the audio. The local sink blocks on OpenAL's free-buffer queue. The Cast sink blocks all the way down
/// the wire: the receiver stops pulling, the TCP window closes, the encoder stalls, and the ring
/// fills.</para>
/// </summary>
public interface IAudioSink : IDisposable
{
    /// <summary>False when the sink could not be opened at all: no device and no receiver. The
    /// engine refuses to play rather than pretending.</summary>
    bool IsAvailable { get; }

    /// <summary>Frames the sink reports as actually <i>played</i> since <see cref="Begin"/>. Not a
    /// wall clock and not the amount handed over: it is what came out. The play position and the
    /// visualiser both read this, so they agree with each other and with what the listener hears.</summary>
    long FramesPlayed { get; }

    /// <summary>
    /// True when <see cref="FramesPlayed"/> may be read from any thread, at any time.
    ///
    /// <para>False for a local device: the number comes from querying the audio device, so the engine
    /// publishes it from the one thread that is already inside the device. True for a Cast sink, where
    /// the number is the receiver's own reported clock, stored in a field and not read from a
    /// device.</para>
    ///
    /// <para>This needs its own property because of what the pump does while casting. The Cast sink blocks
    /// the pump for seconds at a time, and the pump publishes nothing during that time. During the drain at
    /// the end of a track, while the speaker plays out what it holds, the pump does not run at all. A
    /// position that can only change when the pump runs would freeze, then jump, then stay still at the end
    /// of every track.</para>
    /// </summary>
    bool ReportsPositionItself => false;

    /// <summary>Starts a track. Called once per play or seek, before any <see cref="Enqueue"/>. The
    /// track's metadata is passed here because a Cast receiver shows it on screen. The local sink
    /// uses only the format.</summary>
    void Begin(SinkTrack track, int sampleRate, int channels);

    /// <summary>
    /// The track changed without the playback stopping. This is the gapless changeover, where the
    /// engine replaces the decoder under a sink that is still running (<c>AudioEngine.TryChain</c>).
    /// No <see cref="Begin"/> follows, and none should: calling it again would cause an audible gap.
    ///
    /// <para>A local device does nothing here. A Cast receiver has one thing to do: the title card on
    /// the television still shows the track that started the stream, and it can be updated without
    /// disturbing the playback.</para>
    /// </summary>
    void TrackChanged(SinkTrack track) { }

    /// <summary>
    /// How much audio the sink may hold ahead of the speakers, in seconds. This is a request, not
    /// a promise: a sink follows it when it can and ignores it otherwise.
    ///
    /// <para>The queue delays every live change. A fader move, a stopped deck or a swapped record is
    /// applied to audio the device has not played yet, so the deeper the queue, the longer before the
    /// listener hears the change. A deep queue is right for normal playback and wrong for judging audio
    /// changes, which is the only reason this is adjustable. A receiver on the network cannot follow it and
    /// ignores it.</para>
    /// </summary>
    void KeepAhead(double seconds) { }

    /// <summary>Hands over <paramref name="frames"/> interleaved frames. <b>Blocks</b> until the
    /// sink has room for them. Returns false once the sink has stopped and will take no more.</summary>
    bool Enqueue(ReadOnlySpan<float> interleaved, int frames);

    /// <summary>Blocks until everything already enqueued has played out. This is the end of a track,
    /// not the end of the decode.</summary>
    void Drain(CancellationToken cancellation = default);

    void Pause();

    void Resume();

    /// <summary>Stops now and discards whatever is still queued. Unblocks any thread parked in
    /// <see cref="Enqueue"/> or <see cref="Drain"/>.</summary>
    void Stop();

    /// <summary>
    /// The calling thread is finished with this sink and is about to end. A sink that binds the device
    /// to whichever thread reached it gives that binding back here.
    ///
    /// <para>A receiver on the network does nothing here. The local device has one task: OpenAL's
    /// current context belongs to a thread, not to the process (see <c>OpenAlSink.ClaimContext</c>).
    /// The pump is a new thread for every record played. If a thread ends while it still holds a
    /// context, the library prints <c>Context ... current for thread being destroyed!</c> once per
    /// track and drops its reference from a destructor at an unpredictable time. The code that claimed
    /// the context must release it.</para>
    ///
    /// <para>Call it only for a thread that is leaving. It is not a pause or a stop: the sink keeps
    /// playing what it has, and the next call from this thread claims the context again, as every call
    /// does.</para>
    /// </summary>
    void ReleaseThread() { }

    /// <summary>Output volume, 0..100.</summary>
    void SetVolume(int volume);

    /// <summary>
    /// The levelling gain for the track being played, in dB: how much this track is raised or
    /// lowered so that it has the same level as the previous one.
    ///
    /// <para>Almost every sink does nothing here, on purpose: the gain is already applied to the
    /// samples before they reach the sink, so a sink that applied it again would apply it twice. The
    /// one exception is a surround cast, which re-encodes the source file instead of taking our PCM, so
    /// the samples never pass through the gain. See <c>CastSink</c>.</para>
    /// </summary>
    void SetTrackGain(double gainDb) { }
}

/// <summary>What a sink needs to know about the track it is about to play. A local device needs
/// none of it. A Cast receiver shows every field on the television.</summary>
/// <param name="Path">The source file, for sinks that serve it or name it.</param>
/// <param name="Title">Track title, falling back to the file name.</param>
/// <param name="Artist">May be null. An unknown artist is a normal state.</param>
/// <param name="Album">May be null.</param>
/// <param name="DurationSeconds">Total length of the track, not of this play.</param>
/// <param name="StartSeconds">Where this play begins. A seek starts here.</param>
public readonly record struct SinkTrack(
    string Path,
    string Title,
    string? Artist,
    string? Album,
    double DurationSeconds,
    double StartSeconds);

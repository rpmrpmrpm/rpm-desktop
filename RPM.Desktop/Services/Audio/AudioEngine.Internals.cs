using System.Collections.Concurrent;
using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The spectrum tap the visualizers read, and the engine's own plumbing beneath the verbs.
/// </summary>
public sealed partial class AudioEngine
{
    // ── spectrum tap ──

    /// <summary>Copies the mono samples at the current play position into <paramref name="dest"/>.
    /// These are the exact samples the sink is playing, so the visualiser stays in sync with the sound.
    /// This holds even when the sound comes out of a speaker in another room.</summary>
    public bool CopyPlayFrame(float[] dest)
    {
        ArgumentNullException.ThrowIfNull(dest);

        if (_playing is null) return false;

        // Use the play head that the pump publishes. Do not query the device here (see _playHead).
        long at = Volatile.Read(ref _playHead);
        long written = Volatile.Read(ref _specWritten);

        if (at < 0 || at + dest.Length > written) return false;
        if (at < written - _specRing.Length + dest.Length) return false;            // already overwritten

        for (int i = 0; i < dest.Length; i++)
            dest[i] = _specRing[(int)((at + i) % _specRing.Length)];

        // No lock is held during the copy, so check that the pump did not overwrite the ring while
        // copying. The ring holds about thirty seconds, so this should not happen. The check costs one
        // read.
        return Volatile.Read(ref _specWritten) - _specRing.Length + dest.Length <= at;
    }

    // ── internals ──

    private bool StartDecoder(string path, double startSec, double? lengthSec)
    {
        try
        {
            var source = new FfmpegPcmSource(new FfmpegPcmSource.Options
            {
                Source = path,
                SampleRate = Rate,
                Channels = Channels,
                StartSeconds = startSec,
                LengthSeconds = lengthSec,
                // For a cloud URL or a network share, ffmpeg reopens the input itself when the
                // connection drops. The read-ahead ring hides the short stall from the listener.
                InputArgs = FfmpegPcmSource.ReconnectArgs(path),
            });
            if (source.Ended)
            {
                source.Dispose();
                return false;
            }

            // Reads the decoder output on a thread of its own, about a second ahead of the sound. If
            // decoding ran on the pump thread, any delay in reading the bytes (a slow file, a URL, or
            // ffmpeg not being scheduled) would take audio straight from the device queue. The listener
            // would hear that as a dropout.
            _pcm = new PrefetchPcmSource(source, Channels, PrefetchFrames, FramesPerBuffer);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Ring → EQ → sink, one 50 ms block at a time. This method has no timer. The sink's
    /// <see cref="IAudioSink.Enqueue"/> blocks until it has room, so the loop runs at the speed the
    /// audio is played.
    ///
    /// <para>This thread has a hard 50 ms deadline, and the code is written around it. It takes no lock
    /// (see <see cref="_gate"/>), allocates nothing, does not decode (see
    /// <see cref="PrefetchPcmSource"/>), and raises no events. A handler that blocked here would stop
    /// the music. The thread also asks the OS to schedule it like a UI thread, not like a background
    /// job. On Apple silicon this chooses a performance core over an efficiency core. Without it, the
    /// visualiser's render thread can take priority, and the device queue runs dry.</para>
    /// </summary>
    /// <summary>The longest time the pump may stay away from the sink before a log line is written.
    /// This is the whole gap, not only the read. The sink paces the pump, so the work between two
    /// blocks should take almost no time. The next block is already decoded and waiting. A gap of a
    /// tenth of a second means something is wrong. The limit is well under the half second of audio
    /// that the device holds, so the log line is written before the break, not after it.
    ///
    /// <para>The gap includes all the work between two blocks, not only the read. A gap that is not a
    /// read still gets a log line. See <see cref="FeedGap"/>.</para>
    /// </summary>
    private const double LateBlockMs = 100;

    /// <param name="gen">The generation this pump was started for. <see cref="_gen"/> changes on every
    /// Play or Stop. A pump still waiting in Enqueue when its track is replaced sees a stale generation
    /// when it wakes, and it exits. Checking only <see cref="_running"/> is not enough. The next Play
    /// sets it back to true, which would revive the old pump. Several old pumps would then feed the
    /// same sink, compete for the OpenAL context, and slow the UI.</param>
    private void Pump(int gen)
    {
        AudioThread.EnterPlayback();

        var sink = Sink;
        var pcm = new float[FramesPerBuffer * Channels];   // one block, reused (no per-block garbage)

        bool Live() => _running && Volatile.Read(ref _gen) == gen;

        // Until the first block is handed over, no block can be late. The ring is empty by definition,
        // and the read that fills it is the start of the track, not a break in it.
        bool primed = false;

        // The time the sink counts as "away", measured from this end so it can be split into parts.
        // See FeedGap. The time is recorded when Enqueue returns, which is when the sink records its
        // own time. The time is kept across a gapless change. The turn that swaps sources is part of
        // the same gap as the read after it. Resetting the timers on each turn would hide a long pause.
        long awayFrom = 0, readTicks = 0, chainTicks = 0, headTicks = 0;
        var gcAtFeed = default(TimeSpan);
        int ahead = -1;

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        try
        {
            while (Live())
            {
                // The number of frames in the ring before the block is read. A full ring with a slow
                // read means the pump was not running. An empty ring means the decoder could not keep
                // up.
                ahead = (_pcm as PrefetchPcmSource)?.BufferedFrames ?? -1;

                long tDecode = PipelineProbe.Begin(PipelineProbe.Stage.Decode);
                int frames = FillBuffer(pcm, gen);
                PipelineProbe.Work(PipelineProbe.Stage.Decode, tDecode);
                readTicks += PipelineProbe.Now - tDecode;

                if (frames <= 0)
                {
                    // The decode has ended. If a successor is queued and the track really ended, this
                    // is the gapless changeover. The sources are swapped and the sink keeps playing.
                    //
                    // The time is measured, because this runs on the deadline thread. It is the only
                    // place in the loop that takes the engine's lock.
                    long tChain = Stopwatch.GetTimestamp();
                    bool chained = TryChain(gen);
                    chainTicks += Stopwatch.GetTimestamp() - tChain;

                    if (chained) continue;
                    break;
                }

                _enqueued += frames;

                // This is the pump's half of the sink's underrun log line. It is written before the
                // block is sent, not after. The sink writes its own line inside the call below, and
                // the two lines are meant to be read together.
                if (primed)
                {
                    var gap = new FeedGap(
                        totalMs: Ms(Stopwatch.GetTimestamp() - awayFrom),
                        readMs: Ms(readTicks),
                        changeoverMs: Ms(chainTicks),
                        playheadMs: Ms(headTicks),
                        collectorMs: (GC.GetTotalPauseDuration() - gcAtFeed).TotalMilliseconds,
                        ringMs: ahead < 0 ? null : ahead * 1000.0 / Rate);

                    // The closure is allocated here on purpose. The deadline has already been missed
                    // when this runs. Writing a log line can block on a terminal pipe, so the worker
                    // thread writes it instead.
                    if (gap.IsLate(LateBlockMs))
                        Post(() => Said.Line("audio", $"the pump was {gap.Describe()}"));
                }

                long tSink = PipelineProbe.Begin(PipelineProbe.Stage.Sink);
                bool ok = sink.Enqueue(pcm, frames);
                PipelineProbe.Work(PipelineProbe.Stage.Sink, tSink);
                primed = true;

                // The pump's part of the gap starts here. The sink records its own time a moment
                // earlier, inside the call that has just returned. Its "away" time is this window plus
                // the work it did at both ends. It reports that work as "library". Together, the two
                // parts account for the whole gap.
                awayFrom = Stopwatch.GetTimestamp();
                gcAtFeed = GC.GetTotalPauseDuration();
                readTicks = chainTicks = 0;

                // The play head is published here, from the pump thread that is already using the
                // device. The time is measured too. This call takes the sink's lock and asks the sound
                // library. If the device hangs, the pump is held up in a call that would otherwise not
                // be counted.
                Volatile.Write(ref _playHead, sink.FramesPlayed);
                headTicks = Stopwatch.GetTimestamp() - awayFrom;

                // Timing is reported from here, as well as from the render loop. This thread keeps its
                // 20 Hz rate even when the render thread gets no time. That is when the numbers matter
                // most. The formatting and the write happen on the worker thread. Debug output can
                // block on a pipe, and this thread cannot wait for that.
                if (PipelineProbe.Due) Post(PipelineProbe.Report);

                if (!ok) return;                                 // the sink was stopped
            }

            if (!Live()) return;

            sink.Drain();                                        // play the remaining audio to the end
            if (!Live()) return;

            _running = false;

            // The pump takes this lock once, after the audio has drained. The source is taken and the
            // path is cleared in the same step. This stops a Teardown from running at the same time and
            // disposing the source twice. A queued next track that reaches this point was not chained,
            // because the track failed instead of ending. It is discarded.
            IPcmSource? finished;
            ChainLink? unplayed;
            lock (_gate)
            {
                _playing = null; finished = _pcm; _pcm = null; _declared = null; _seekVia = null;
                unplayed = _next; _next = null;
            }

            // The state is read before the source is released, because only the source knows it. A
            // source reports ended, waiting, or failed (see PcmState). The TrackEnded handler reads
            // this value to tell a track that finished from one that stopped.
            EndedAs = finished?.State ?? PcmState.Ended;
            EndFault = finished?.Fault;

            // Runs on the worker thread. Disposing the source waits for the decode thread and stops
            // ffmpeg. The handlers for these events may start the next track. Neither should run on
            // the thread that is still playing the last of the audio.
            Post(() =>
            {
                try { finished?.Dispose(); } catch { }
                try { unplayed?.Source.Dispose(); } catch { }

                // A Stop or a new Play may have run while this was queued, and it has already notified
                // everyone. Raising "track finished" now would move a playlist on after the user
                // stopped it.
                if (Volatile.Read(ref _gen) != gen) return;

                Changed?.Invoke();
                TrackEnded?.Invoke();
            });
        }
        catch { /* sink or decoder went away */ }
        finally
        {
            // This thread lives for one track and then ends. StartPump creates a new thread for each
            // play. The local sink binds OpenAL's current context to the calling thread. If the thread
            // exits without releasing it, OpenAL prints "Context ... current for thread being
            // destroyed!" for each track. The reference is then dropped from a destructor instead of by
            // this code. Every exit path is covered, including the early returns and a sink that
            // throws.
            try { sink.ReleaseThread(); } catch { /* it was disposed under us */ }
        }
    }

    /// <summary>Takes one block from the decode-ahead ring, applies the EQ, and copies it to the
    /// spectrum tap. Returns the frame count, which is 0 at the end of the stream. It takes no lock
    /// (see <see cref="Pump"/>).</summary>
    private int FillBuffer(float[] pcm, int gen)
    {
        var source = _pcm;
        if (source is null) return 0;

        int frames;
        try { frames = source.Read(pcm, pcm.Length / Channels); }
        catch { return 0; }

        if (Volatile.Read(ref _gen) != gen) return 0;   // a stale track must not write to the spectrum ring
        if (frames <= 0) return 0;

        _eq.Process(pcm, frames);
        ApplyGain(pcm, frames);

        // This is the only writer of the ring and of _specWritten (see the field). The samples are
        // written first. Then the count is updated, which makes them visible to readers.
        long written = _specWritten;
        for (int i = 0; i < frames; i++)
        {
            var mono = (pcm[i * 2] + pcm[i * 2 + 1]) * 0.5f;
            _specRing[(int)((written + i) % _specRing.Length)] = mono;
        }
        Volatile.Write(ref _specWritten, written + frames);

        return frames;
    }

    /// <summary>
    /// Applies the gain. The gain moves toward its target a little at a time, instead of jumping.
    ///
    /// <para>This runs on the pump thread inside its 50 ms deadline. It allocates nothing, takes no
    /// lock, and does one multiply per sample. When the gain is 1.0 and already settled, it does
    /// nothing. That is the usual case: tracks that were not measured, and listeners who turned
    /// normalisation off.</para>
    ///
    /// <para>The output is clamped, like the output of <see cref="Equalizer"/>. A boost on a signal
    /// that already peaks near full scale would otherwise wrap around. The gain is limited to a safe
    /// ceiling before it reaches this method (see <see cref="RPM.Core.Analysis.ReplayGain.Clamp"/>).
    /// This clamp is the backup. It catches a track whose peak was never measured.</para>
    /// </summary>
    private void ApplyGain(float[] pcm, int frames)
    {
        float target = _gainTarget;

        // Nothing to do, and the gain is not moving. This is the usual case.
        if (_gainNow == target && Math.Abs(target - 1f) < 1e-6f)
            return;

        int samples = frames * Channels;

        if (_gainNow == target)
        {
            for (int i = 0; i < samples; i++)
                pcm[i] = Math.Clamp(pcm[i] * target, -1f, 1f);
            return;
        }

        // The gain moves once per frame, not once per sample. Both channels of a frame then get the
        // same value. Different values would shift the stereo image while the gain changes.
        float now = _gainNow;
        for (int f = 0; f < frames; f++)
        {
            if (now < target)
                now = Math.Min(target, now + GainStepPerFrame);
            else if (now > target)
                now = Math.Max(target, now - GainStepPerFrame);

            int at = f * Channels;
            for (int c = 0; c < Channels; c++)
                pcm[at + c] = Math.Clamp(pcm[at + c] * now, -1f, 1f);
        }

        _gainNow = now;
    }

    /// <param name="inline">Runs the teardown on this thread instead of queueing it. Use this only when
    /// the caller must know the pump has stopped before it uses the sink, such as when switching to a
    /// Cast device or shutting down. Other callers queue it, so the UI thread is not held up.</param>
    private void StopInternal(bool raise, bool inline = false)
    {
        // The generation is changed first. From then on the old pump is ignored, whatever it is blocked
        // on. The next Play sets _running back to true, and that cannot revive the old pump. The state
        // is cleared here, not on the worker, so "nothing is playing" is true as soon as Stop returns.
        Interlocked.Increment(ref _gen);
        _running = false;
        lock (_gate)
        {
            _playing = null;
            _paused = false;
            _declared = null;
            _seekVia = null;
        }

        if (inline) Teardown(); else Post(Teardown);

        // Stopped is raised before Changed. The deck must be empty when a listener reads the transport.
        // Otherwise the listener would see the engine with nothing playing while the deck still shows a
        // track.
        if (raise)
        {
            Stopped?.Invoke();
            Changed?.Invoke();
        }
    }

    /// <summary>Releases the device, the decoder and the pump. This blocks, so it runs only on the
    /// audio worker, or on the disposing thread at shutdown. It never runs on the caller's
    /// thread.</summary>
    private void Teardown()
    {
        _running = false;

        // Release both places the pump can be blocked before waiting for it to stop. The sink releases
        // a pump that is waiting for room. Disposing the source ends a blocking read on the decoder's
        // pipe, and it stops the ffmpeg process. These must happen before the join. If they happen
        // after it, a stuck pump can outlive the timeout and keep running.
        Sink.Stop();

        IPcmSource? source;
        ChainLink? next;
        lock (_gate)
        {
            source = _pcm;
            _pcm = null;

            // A queued next track is dropped with the track it was queued behind. A stop, a new play or
            // a sink change makes the queued track wrong too.
            next = _next;
            _next = null;
        }

        try { source?.Dispose(); } catch { }
        try { next?.Source.Dispose(); } catch { }

        var pump = _pump;
        _pump = null;
        if (pump is not null && pump != Thread.CurrentThread)
            pump.Join(1000);
    }

    /// <summary>Set first in <see cref="Dispose"/>, under the worker's lock, so that <see cref="Post"/> never
    /// starts a worker after it.</summary>
    private volatile bool _disposed;

    public void Dispose()
    {
        lock (_workerGate)
            _disposed = true;

        Interlocked.Increment(ref _gen);
        _running = false;
        lock (_gate) { _playing = null; _paused = false; }

        // Shutdown is the one place the teardown runs inline: the worker is being retired with it.
        _commands.CompleteAdding();
        var worker = _worker;
        _worker = null;
        if (worker is not null && worker != Thread.CurrentThread) worker.Join(2000);
        Teardown();
        _commands.Dispose();

        IAudioSink sink;
        bool owns;
        lock (_gate) { sink = _sink; owns = _ownsSink; }
        if (owns) sink.Dispose();
    }
}
